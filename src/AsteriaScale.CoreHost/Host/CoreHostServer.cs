using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AsteriaScale.CoreHost.Posix;
using AsteriaScale.CoreHost.Protocol;
using AsteriaScale.Services;

namespace AsteriaScale.CoreHost.Host;

/// <summary>
/// The protocol side of CoreHost. Admission runs on the reader thread in
/// arrival order and never blocks: control commands are answered at once
/// (ping through the thread pool, so a starved pool shows up as a missed
/// ping), the hover lane holds one hover and a newer one supersedes it, and
/// everything else runs as a pool task whose ordering is Core's business,
/// except that a settings change takes its place in Core's settings queue at
/// admission, so whatever is admitted after it sees it however late the pool
/// runs either.
/// The Core-backed commands (CoreHostServer.Core.cs) answer not_ready until
/// core.init has built the <see cref="CoreSession"/>.
///
/// Lifecycle. core.shutdown and SIGTERM start the same orderly drain: refuse
/// new requests, answer Immediate ones, signal Cooperative ones, wait for the
/// rest up to the drain bound, dispose the services, answer the shutdown and
/// exit 0 (or core.fault shutdown_timeout and exit 6). stdin EOF without
/// core.shutdown means the parent is gone: everything is cancelled, given
/// 500 ms, and the process exits 4; CoreHostProgram then kills what CoreHost
/// owns.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class CoreHostServer
{
    private const int Serving = 0;
    private const int Draining = 1;
    private const int Abandoning = 2;

    private const int MaxSelectionTextLength = 4000;
    private const int MaxHoveredWordLength = 128;
    private const int MaxNameLength = 256;

    // The parent is gone, so nothing waits for these requests' terminals;
    // the grace only lets Core's own cancellation run before the group is
    // killed, and keeps the group empty within the supervisor's 1 s bound.
    private static readonly TimeSpan ParentGoneGrace = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WriterDrainBound = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ServiceDisposeBound = TimeSpan.FromSeconds(15);

    private static readonly WireJson J = WireJson.Strict;

    private readonly HostOptions _options;
    private readonly string[] _blockedSignals;
    private readonly ConcurrentDictionary<long, RequestState> _open = new();
    private readonly ConcurrentDictionary<long, Task> _running = new();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly object _hoverLock = new();
    private readonly object _statusLock = new();
    private readonly Dictionary<string, string> _lastStatus = new(StringComparer.Ordinal);
    private readonly Stream _input;
    private readonly FrameWriter _writer;
    private long _lastId;
    private bool _helloDone;
    private int _state;
    private int _outstanding;
    private RequestState? _hoverCurrent;
    private Task _hoverTail = Task.CompletedTask;
    private IHoverEngine? _hoverEngine;
    private CoreSession? _core;
    private int _initStarted;

    // The writer exists before the SIGTERM handler is registered, so a
    // SIGTERM that arrives before the first frame still drains cleanly.
    public CoreHostServer(HostOptions options, string[] blockedSignals, Stream input, Stream output)
    {
        _options = options;
        _blockedSignals = blockedSignals;
        _input = input;
        _writer = new FrameWriter(output, Fatal);
    }

    /// <summary>Starts reading requests; completes with the exit code.</summary>
    public Task<int> RunAsync()
    {
        Thread reader = new(() => ReadLoop(_input)) { IsBackground = true, Name = "corehost-reader" };
        reader.Start();
        return _exit.Task;
    }

    /// <summary>SIGTERM: the orderly drain, whether or not stdin is still open.</summary>
    public void RequestShutdown(string reason) => BeginShutdown(null, reason);

    private void ReadLoop(Stream input)
    {
        try
        {
            FrameReader reader = new(input, WireProtocol.MaxInboundFrameBytes);
            while (Volatile.Read(ref _state) != Abandoning)
            {
                FrameReader.Result result = reader.Read(out ReadOnlyMemory<byte> frame);
                if (result == FrameReader.Result.Eof)
                {
                    OnEndOfInput();
                    return;
                }

                if (result == FrameReader.Result.TooLong)
                {
                    Violation(ErrorCodes.FrameTooLong, $"an inbound frame exceeds {WireProtocol.MaxInboundFrameBytes} bytes");
                    return;
                }

                RequestEnvelope? envelope = RequestEnvelope.Parse(
                    frame,
                    _lastId,
                    out JsonDocument? document,
                    out RequestEnvelope.Violation? violation);
                using (document)
                {
                    if (envelope is null)
                    {
                        Violation(violation!.Code, violation.Detail);
                        return;
                    }

                    _lastId = envelope.Id;
                    Admit(envelope);
                }
            }
        }
        catch (Exception exception)
        {
            Fatal(ExitCodes.Internal, "the reader failed: " + exception);
        }
    }

    private void Admit(RequestEnvelope envelope)
    {
        if (!_helloDone && envelope.Command != Commands.Hello)
        {
            _writer.FailUnadmitted(envelope.Id, Errors.HelloRequired());
            return;
        }

        if (Volatile.Read(ref _state) != Serving)
        {
            _writer.FailUnadmitted(envelope.Id, Errors.ShuttingDown());
            return;
        }

        switch (envelope.Command)
        {
            case Commands.Hello:
                AdmitControl(envelope, J.HelloPayload, Hello);
                return;
            case Commands.Ping:
                AdmitPing(envelope);
                return;
            case Commands.Cancel:
                AdmitControl(envelope, J.CancelPayload, Cancel);
                return;
            case Commands.Shutdown:
                AdmitShutdown(envelope);
                return;
            case Commands.HoverResolve:
                AdmitHover(envelope);
                return;
            case Commands.OcrSelect:
                AdmitOcrSelect(envelope);
                return;
        }

        if (AdmitCore(envelope))
        {
            return;
        }

        if (Commands.ReservedStage(envelope.Command) is { } stage)
        {
            _writer.FailUnadmitted(envelope.Id, Errors.NotImplemented(envelope.Command, stage));
            return;
        }

        if (_options.TestCommands && AdmitTest(envelope))
        {
            return;
        }

        _writer.FailUnadmitted(envelope.Id, Errors.UnknownCommand(envelope.Command));
    }

    // ---- control lane -------------------------------------------------

    private void AdmitControl<T>(RequestEnvelope envelope, JsonTypeInfo<T> info, Action<RequestState, T> handler)
    {
        if (!TryPayload(envelope, info, out T? payload))
        {
            return;
        }

        RequestState request = Register(envelope, Lane.Control, CancelMode.NotCancellable);
        try
        {
            handler(request, payload);
        }
        catch (RequestErrorException exception)
        {
            Fail(request, exception.Body);
        }
        catch (Exception exception)
        {
            HostLog.Write($"request {request.Id} ({request.Command}) failed: {exception}");
            Fail(request, Errors.Internal(exception.GetType().Name + ": " + exception.Message));
        }

        if (!request.IsTerminal)
        {
            Fail(request, Errors.Internal(request.Command + " returned without a terminal"));
        }
    }

    private void Hello(RequestState request, HelloPayload payload)
    {
        if (payload.ProtocolMin > WireProtocol.Version || payload.ProtocolMax < WireProtocol.Version)
        {
            Fail(request, Errors.UnsupportedVersion(
                $"the client speaks {payload.ProtocolMin}..{payload.ProtocolMax}, the host {WireProtocol.Version}"));
            return;
        }

        if (_helloDone)
        {
            Fail(request, Errors.BadRequest("core.hello was already answered"));
            return;
        }

        int pgid = PosixProcess.ProcessGroup;
        Assembly host = typeof(CoreHostServer).Assembly;
        Respond(request, new HelloResult
        {
            Protocol = WireProtocol.Version,
            CoreHostVersion = host.GetName().Version?.ToString(3) ?? "",
            CoreVersion = typeof(CoreDataLayout).Assembly.GetName().Version?.ToString(3) ?? "",
            // The informational version carries the source revision, so the
            // app can refuse a helper from another build of the bundle.
            Build = host.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
            Runtime = RuntimeInformation.FrameworkDescription,
            Aot = !RuntimeFeature.IsDynamicCodeSupported,
            Pid = Environment.ProcessId,
            Pgid = pgid,
            ProcessGroupLeader = pgid == Environment.ProcessId,
            Platform = "macos",
            TestCommands = _options.TestCommands,
            BlockedSignals = _blockedSignals,
            Paths = new HostPaths
            {
                DataRoot = _options.DataRoot,
                UserData = _options.UserData,
                LogDir = _options.LogDirectory,
                CacheDir = _options.CacheDirectory,
                RankerWorker = _options.RankerWorker,
                RankerModels = _options.RankerModels,
            },
            // Only what this platform can run (rule 3): no NPU and no phrase
            // model before 20.5; the text is the app's selection or what
            // Apple Vision read in its captures.
            Capabilities = new Capabilities
            {
                HoverModes = ["dictionary", "smart"],
                HoverSources = ["selection", "observations"],
                SenseProviders = ["cpu"],
                SenseModels = ["glossbert", "consec"],
                PhraseBackends = [],
                PhraseVariants = [],
            },
        }, J.HelloResult);
        _helloDone = true;
    }

    private void AdmitPing(RequestEnvelope envelope)
    {
        if (!TryPayload(envelope, J.EmptyPayload, out _))
        {
            return;
        }

        RequestState request = Register(envelope, Lane.Control, CancelMode.NotCancellable);
        // Through the pool on purpose: a starved pool must miss the ping.
        ThreadPool.UnsafeQueueUserWorkItem(_ => Respond(request, new PingResult
        {
            UptimeMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            Outstanding = Volatile.Read(ref _outstanding),
            GcHeapBytes = GC.GetTotalMemory(false),
            WorkingSetBytes = Environment.WorkingSet,
        }, J.PingResult), null);
    }

    private void Cancel(RequestState request, CancelPayload payload)
    {
        if (payload.Target < 1 || payload.Target >= request.Id)
        {
            Fail(request, Errors.BadRequest("request.cancel must target an earlier id"));
            return;
        }

        if (!_open.TryGetValue(payload.Target, out RequestState? target) || target.IsTerminal)
        {
            Respond(request, new CancelResult { Cancelled = false, Reason = "not_in_flight" }, J.CancelResult);
            return;
        }

        switch (target.CancelMode)
        {
            case CancelMode.Immediate:
                // The target's terminal goes first: once the client has this
                // answer, nothing more arrives for the target (G4).
                bool wrote = Fail(target, Errors.Cancelled());
                target.Cancel();
                Respond(request, wrote
                    ? new CancelResult { Cancelled = true }
                    : new CancelResult { Cancelled = false, Reason = "not_in_flight" }, J.CancelResult);
                return;
            case CancelMode.Cooperative:
                target.Cancel();
                Respond(request, new CancelResult { Cancelled = false, Reason = "requested" }, J.CancelResult);
                return;
            default:
                Respond(request, new CancelResult { Cancelled = false, Reason = "not_cancellable" }, J.CancelResult);
                return;
        }
    }

    private void AdmitShutdown(RequestEnvelope envelope)
    {
        if (!TryPayload(envelope, J.EmptyPayload, out _))
        {
            return;
        }

        RequestState request = Register(envelope, Lane.Control, CancelMode.NotCancellable);
        if (!BeginShutdown(request, Commands.Shutdown))
        {
            Fail(request, Errors.ShuttingDown());
        }
    }

    // ---- hover lane ---------------------------------------------------

    private void AdmitHover(RequestEnvelope envelope)
    {
        // Each source kind has its own strict type: the kind is read first,
        // and the selection type and its bytes stay as they were in 20.2.
        HoverJob job;
        if (envelope.Payload.ValueKind == JsonValueKind.Object &&
            envelope.Payload.TryGetProperty("source", out JsonElement source) &&
            source.ValueKind == JsonValueKind.Object &&
            source.TryGetProperty("kind", out JsonElement kind) &&
            kind.ValueKind == JsonValueKind.String &&
            kind.ValueEquals("observations"))
        {
            if (!TryPayload(envelope, J.HoverObservationsPayload, out HoverObservationsPayload? observed))
            {
                return;
            }

            if ((ObservationRules.ValidateHover(observed.Mode, observed.Source) ?? ValidateGame(observed.Game)) is { } invalid)
            {
                _writer.FailUnadmitted(envelope.Id, Errors.BadRequest(invalid));
                return;
            }

            job = new HoverJob(envelope.Id, observed.Mode, Selection: null, observed.Source, observed.Game);
        }
        else
        {
            if (!TryPayload(envelope, J.HoverResolvePayload, out HoverResolvePayload? payload))
            {
                return;
            }

            if (ValidateHover(payload) is { } problem)
            {
                _writer.FailUnadmitted(envelope.Id, Errors.BadRequest(problem));
                return;
            }

            job = new HoverJob(envelope.Id, payload.Mode, payload.Source, Observations: null, payload.Game);
        }

        IHoverEngine? engine = Volatile.Read(ref _hoverEngine);
        if (engine is null)
        {
            _writer.FailUnadmitted(envelope.Id, Errors.NotReady("the hover engine is not ready"));
            return;
        }

        if (!TryReserve(envelope))
        {
            return;
        }

        RequestState request = Register(envelope, Lane.Hover, CancelMode.Immediate);
        lock (_hoverLock)
        {
            RequestState? previous = _hoverCurrent;
            _hoverCurrent = request;
            if (previous is not null)
            {
                // The superseded hover's terminal is queued here, at
                // admission, before anything the new hover can emit (G3);
                // its Core work drains privately.
                Fail(previous, Errors.Superseded(request.Id));
                previous.Cancel();
            }

            // On the pool: the engine may run synchronously up to its first
            // await, and admission must never wait for it.
            Task tail = _hoverTail;
            Task work = Task.Run(() => RunHoverAsync(request, job, tail, engine));
            _hoverTail = work;
            Track(request.Id, work);
        }
    }

    private static string? ValidateHover(HoverResolvePayload payload)
    {
        SelectionSource source = payload.Source;
        if (!string.Equals(source.Kind, "selection", StringComparison.Ordinal))
        {
            return $"source.kind must be selection or observations, got '{source.Kind}'";
        }

        if (payload.Mode is not (null or "dictionary" or "smart"))
        {
            return $"mode must be dictionary or smart, got '{payload.Mode}'";
        }

        if (source.Text.Length is 0 or > MaxSelectionTextLength)
        {
            return $"source.text must hold 1..{MaxSelectionTextLength} characters";
        }

        if (source.HoveredWord.Length is 0 or > MaxHoveredWordLength)
        {
            return $"source.hoveredWord must hold 1..{MaxHoveredWordLength} characters";
        }

        if (source.CharacterIndex < 0 || source.CharacterIndex > source.Text.Length)
        {
            return "source.characterIndex must lie within source.text";
        }

        return source.SpeakerName is { Length: > MaxNameLength }
            ? $"speaker and game names must hold at most {MaxNameLength} characters"
            : ValidateGame(payload.Game);
    }

    // Both source kinds (docs/MACOS.md T32): a game key Core drops, or one
    // its normalization changes again on a second pass, would silently lose
    // the speaker's gender, so it is refused as speaker.set refuses it.
    private static string? ValidateGame(GameIdentity? game)
    {
        if (game is null)
        {
            return null;
        }

        if (game.Key.Length is 0 or > MaxNameLength || game.DisplayName.Length > MaxNameLength)
        {
            return $"speaker and game names must hold at most {MaxNameLength} characters";
        }

        (string key, bool stable) = CoreSession.NormalizeGameKey(game.Key, game.DisplayName);
        return key.Length == 0 || !stable
            ? $"game.key must start with path: or process: and keep its normalized form when normalized again, got '{game.Key}'"
            : null;
    }

    // ocr.select: stateless and without translation, so it needs core.hello
    // but not core.init (the one exception from not_ready); it runs on the
    // work lane and counts toward busy.
    private void AdmitOcrSelect(RequestEnvelope envelope)
    {
        if (!TryPayload(envelope, J.OcrSelectPayload, out OcrSelectPayload? payload))
        {
            return;
        }

        if (ObservationRules.ValidateSelect(payload) is { } problem)
        {
            _writer.FailUnadmitted(envelope.Id, Errors.BadRequest(problem));
            return;
        }

        if (!TryReserve(envelope))
        {
            return;
        }

        RequestState request = Register(envelope, Lane.Work, CancelMode.Immediate);
        StartWork(request, state =>
        {
            Respond(state, ObservationSelection.Select(payload.Mode!, payload.Source), J.OcrSelectResult);
            return Task.CompletedTask;
        });
    }

    private async Task RunHoverAsync(RequestState request, HoverJob job, Task previous, IHoverEngine engine)
    {
        // One hover in the engine at a time, as Core's ModelActivityGate
        // requires: this one starts only after the previous call returned.
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        HoverEventSink sink = new(this, request);
        try
        {
            request.Cancellation.Token.ThrowIfCancellationRequested();
            sink.MarkStarted();
            string outcome = await engine.ResolveAsync(job, sink, request.Cancellation.Token).ConfigureAwait(false);
            if (outcome == HoverOutcomes.Failed)
            {
                // Core has logged it and reported its own text; the app gets
                // the same structured status as for a failing engine.
                EmitStatus("hover", "error", ErrorCodes.HoverFailed, "Не удалось перевести текст под курсором.");
            }

            Respond(request, new HoverResolveResult { Outcome = outcome, Timings = sink.Timings }, J.HoverResolveResult);
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
            Fail(request, Errors.Cancelled());
        }
        catch (RequestErrorException exception)
        {
            // hover_disabled and the like: an answer, not a failed hover.
            Fail(request, exception.Body);
        }
        catch (Exception exception)
        {
            // Danger zone: a failed hover is an outcome, never a dead host.
            HostLog.Write($"hover {request.Id} failed: {exception}");
            EmitStatus("hover", "error", ErrorCodes.HoverFailed, "Не удалось перевести текст под курсором.");
            Respond(request, new HoverResolveResult { Outcome = HoverOutcomes.Failed, Timings = sink.Timings }, J.HoverResolveResult);
        }
        finally
        {
            lock (_hoverLock)
            {
                if (ReferenceEquals(_hoverCurrent, request))
                {
                    _hoverCurrent = null;
                }
            }
        }
    }

    private sealed class HoverEventSink(CoreHostServer server, RequestState request) : IHoverEvents
    {
        private readonly HoverTimings _timings = new();

        public HoverTimings Timings
        {
            get
            {
                lock (_timings)
                {
                    return new HoverTimings
                    {
                        QueuedMs = _timings.QueuedMs,
                        PrimaryMs = _timings.PrimaryMs,
                        PhraseFirstMs = _timings.PhraseFirstMs,
                        PhraseFinalMs = _timings.PhraseFinalMs,
                    };
                }
            }
        }

        public void MarkStarted()
        {
            lock (_timings)
            {
                _timings.QueuedMs = Elapsed();
            }
        }

        public void Loading(HoverLoadingEvent card) =>
            server._writer.Event(request, Events.HoverLoading, card, J.HoverLoadingEvent);

        public void Primary(HoverPrimaryEvent card)
        {
            if (server._writer.Event(request, Events.HoverPrimary, card, J.HoverPrimaryEvent))
            {
                lock (_timings)
                {
                    _timings.PrimaryMs ??= Elapsed();
                }
            }
        }

        public void Phrase(HoverPhraseEvent line)
        {
            if (server._writer.Event(request, Events.HoverPhrase, line, J.HoverPhraseEvent))
            {
                lock (_timings)
                {
                    _timings.PhraseFirstMs ??= Elapsed();
                    if (!line.Provisional)
                    {
                        _timings.PhraseFinalMs = Elapsed();
                    }
                }
            }
        }

        private double Elapsed() => Stopwatch.GetElapsedTime(request.AdmittedTimestamp).TotalMilliseconds;
    }

    // ---- work lane ----------------------------------------------------

    private void StartWork(RequestState request, Func<RequestState, Task> work)
    {
        // Zero unless test.delayNextWork asked for this request to start late.
        TimeSpan holdBack = TakeTestHoldBack();
        Task task = Task.Run(async () =>
        {
            if (holdBack > TimeSpan.Zero)
            {
                // Not cancellable: a held-back request is still answered by
                // its own work, as one the pool ran late would be.
                await Task.Delay(holdBack).ConfigureAwait(false);
            }

            try
            {
                await work(request).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
            {
                Fail(request, Errors.Cancelled());
            }
            catch (RequestErrorException exception)
            {
                Fail(request, exception.Body);
            }
            catch (Exception exception)
            {
                HostLog.Write($"request {request.Id} ({request.Command}) failed: {exception}");
                Fail(request, Errors.Internal(exception.GetType().Name + ": " + exception.Message));
            }

            if (!request.IsTerminal)
            {
                Fail(request, Errors.Internal(request.Command + " returned without a terminal"));
            }
        });
        Track(request.Id, task);
    }

    private void Track(long id, Task task)
    {
        _running[id] = task;
        _ = task.ContinueWith(
            _ => _running.TryRemove(id, out Task? _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // ---- admission helpers ----------------------------------------------

    private bool TryPayload<T>(RequestEnvelope envelope, JsonTypeInfo<T> info, [NotNullWhen(true)] out T? payload)
    {
        try
        {
            payload = envelope.Payload.ValueKind == JsonValueKind.Undefined
                ? JsonSerializer.Deserialize("{}"u8, info)
                : envelope.Payload.Deserialize(info);
            if (payload is null)
            {
                throw new JsonException("the payload is null");
            }

            return true;
        }
        catch (JsonException exception)
        {
            // The serializer's message names .NET types and changes between
            // runtime versions, so it goes to the log; the wire detail stays
            // stable for the golden transcript.
            HostLog.Write($"request {envelope.Id} ({envelope.Command}): bad payload: {exception.Message}");
            _writer.FailUnadmitted(envelope.Id, Errors.BadRequest(
                $"the payload of {envelope.Command} does not match its schema (unknown, missing, null or mistyped member)"));
            payload = default;
            return false;
        }
    }

    // Only the hover and work lanes count: a client at the limit can still
    // ping, cancel and shut down.
    private bool TryReserve(RequestEnvelope envelope)
    {
        int outstanding = Volatile.Read(ref _outstanding);
        if (outstanding >= WireProtocol.MaxOutstandingRequests)
        {
            _writer.FailUnadmitted(envelope.Id, Errors.Busy(outstanding));
            return false;
        }

        return true;
    }

    private RequestState Register(RequestEnvelope envelope, Lane lane, CancelMode mode)
    {
        RequestState request = new(envelope.Id, envelope.Command, lane, mode);
        if (lane != Lane.Control)
        {
            Interlocked.Increment(ref _outstanding);
        }

        _open[envelope.Id] = request;
        return request;
    }

    private bool Respond<T>(RequestState request, T result, JsonTypeInfo<T> info)
    {
        bool wrote;
        try
        {
            wrote = _writer.Respond(request, result, info);
        }
        catch (OutboundFrameTooLargeException exception)
        {
            wrote = _writer.Fail(request, Errors.Internal(exception.Message));
        }

        if (wrote)
        {
            Retire(request);
        }

        return wrote;
    }

    private bool Fail(RequestState request, ErrorBody error)
    {
        bool wrote = _writer.Fail(request, error);
        if (wrote)
        {
            Retire(request);
        }

        return wrote;
    }

    private void Retire(RequestState request)
    {
        if (_open.TryRemove(request.Id, out _) && request.Lane != Lane.Control)
        {
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private void EmitStatus(string area, string? state, string? code, string text)
    {
        string key = state + "\n" + code + "\n" + text;
        lock (_statusLock)
        {
            if (_lastStatus.TryGetValue(area, out string? last) && last == key)
            {
                return;
            }

            _lastStatus[area] = key;
            _writer.Event(null, Events.Status, new StatusEvent { Area = area, State = state, Code = code, Text = text }, J.StatusEvent);
        }
    }

    // ---- lifecycle ----------------------------------------------------

    private bool BeginShutdown(RequestState? request, string reason)
    {
        if (Interlocked.CompareExchange(ref _state, Draining, Serving) != Serving)
        {
            return false;
        }

        _ = Task.Run(() => DrainAsync(request, reason));
        return true;
    }

    private async Task DrainAsync(RequestState? shutdown, string reason)
    {
        try
        {
            HostLog.Write($"orderly shutdown ({reason}): draining");
            foreach (RequestState request in _open.Values)
            {
                if (ReferenceEquals(request, shutdown))
                {
                    continue;
                }

                if (request.CancelMode == CancelMode.Immediate)
                {
                    Fail(request, Errors.ShuttingDown(reason));
                    request.Cancel();
                }
                else if (request.CancelMode == CancelMode.Cooperative)
                {
                    request.Cancel();
                }
            }

            // Every running task counts, including the private drain of an
            // Immediate request that already has its terminal: Core must be
            // idle before its services are disposed.
            if (!await WaitAllAsync(_running.Values.ToArray(), _options.DrainTimeout).ConfigureAwait(false))
            {
                string detail = $"work was still running after the {_options.DrainTimeout.TotalMilliseconds:0} ms drain bound";
                HostLog.Write("orderly shutdown: " + detail);
                if (shutdown is not null)
                {
                    Fail(shutdown, Errors.ShutdownTimeout(detail));
                }

                _writer.Event(null, Events.Fault, new FaultEvent
                {
                    Code = ErrorCodes.ShutdownTimeout,
                    Message = Errors.FaultMessage(ErrorCodes.ShutdownTimeout),
                    Detail = detail,
                }, J.FaultEvent);
                await FinishWriterAsync().ConfigureAwait(false);
                Terminate(ExitCodes.ShutdownTimeout, detail);
                return;
            }

            await DisposeServicesAsync().ConfigureAwait(false);
            if (shutdown is not null)
            {
                Respond(shutdown, new EmptyResult(), J.EmptyResult);
            }

            await FinishWriterAsync().ConfigureAwait(false);
            Terminate(ExitCodes.Clean, "orderly shutdown (" + reason + ")");
        }
        catch (Exception exception)
        {
            Fatal(ExitCodes.Internal, "the orderly shutdown failed: " + exception);
        }
    }

    private void OnEndOfInput()
    {
        if (Interlocked.CompareExchange(ref _state, Abandoning, Serving) != Serving)
        {
            // stdin closed after core.shutdown or during a SIGTERM drain: the
            // drain finishes and its exit code stands.
            HostLog.Write("stdin EOF during the orderly shutdown");
            return;
        }

        HostLog.Write("stdin EOF without core.shutdown: the parent is gone");
        _ = Task.Run(async () =>
        {
            CancelEverything();
            await WaitAllAsync(_running.Values.ToArray(), ParentGoneGrace).ConfigureAwait(false);
            Terminate(ExitCodes.PeerGone, "the parent is gone");
        });
    }

    private void Violation(string code, string detail)
    {
        if (Interlocked.Exchange(ref _state, Abandoning) == Abandoning)
        {
            return;
        }

        HostLog.Write($"protocol violation: {code}: {detail}");
        _writer.Event(null, Events.Fault, new FaultEvent
        {
            Code = code,
            Message = Errors.FaultMessage(code),
            Detail = detail,
        }, J.FaultEvent);
        CancelEverything();
        _ = Task.Run(async () =>
        {
            await FinishWriterAsync().ConfigureAwait(false);
            Terminate(ExitCodes.ProtocolViolation, "protocol violation " + code);
        });
    }

    private void Fatal(int exitCode, string detail)
    {
        Interlocked.Exchange(ref _state, Abandoning);
        CancelEverything();
        Terminate(exitCode, detail);
    }

    private void Terminate(int exitCode, string reason)
    {
        if (_exit.TrySetResult(exitCode))
        {
            HostLog.Write($"exit {exitCode}: {reason}");
        }
    }

    private void CancelEverything()
    {
        foreach (RequestState request in _open.Values)
        {
            request.Cancel();
        }
    }

    private async Task FinishWriterAsync()
    {
        _writer.Complete();
        try
        {
            await _writer.Drained.WaitAsync(WriterDrainBound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            HostLog.Write("the peer did not read the last frames");
        }
    }

    private async Task DisposeServicesAsync()
    {
        try
        {
            Task dispose = DisposeServicesCoreAsync();
            await dispose.WaitAsync(ServiceDisposeBound).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Program kills whatever CoreHost still owns after this anyway.
            HostLog.Write("disposing the services failed: " + exception.Message);
        }
    }

    private async Task DisposeServicesCoreAsync()
    {
        // The Core session may also be the hover engine; it is disposed once,
        // after any other engine (test.fakeCore) that replaced it.
        CoreSession? core = Volatile.Read(ref _core);
        if (Interlocked.Exchange(ref _hoverEngine, null) is { } engine && !ReferenceEquals(engine, core))
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        if (core is not null)
        {
            await core.DisposeAsync().ConfigureAwait(false);
        }

        KillTestChildren();
    }

    // True when every task finished (a failed one counts: its request was
    // answered already) within the bound.
    private static async Task<bool> WaitAllAsync(Task[] tasks, TimeSpan bound)
    {
        Task all = Task.WhenAll(tasks);
        return await Task.WhenAny(all, Task.Delay(bound)).ConfigureAwait(false) == all;
    }

    private static class Commands
    {
        public const string Hello = "core.hello";
        public const string Ping = "core.ping";
        public const string Cancel = "request.cancel";
        public const string Shutdown = "core.shutdown";
        public const string HoverResolve = "hover.resolve";
        public const string OcrSelect = "ocr.select";

        // Named in v1 so a client sees a clear not_implemented instead of
        // unknown_command; each answers with its stage in detail.
        public static string? ReservedStage(string command) => command switch
        {
            "models.install" or "models.verify" or "phrase.variants" => "20.5",
            _ => null,
        };
    }

    private static class Events
    {
        public const string HoverLoading = "hover.loading";
        public const string HoverPrimary = "hover.primary";
        public const string HoverPhrase = "hover.phrase";
        public const string Status = "status";
        public const string Fault = "core.fault";
    }
}
