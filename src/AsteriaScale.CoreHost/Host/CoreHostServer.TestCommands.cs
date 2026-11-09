using System.Diagnostics;
using System.Text;
using AsteriaScale.CoreHost.Posix;
using AsteriaScale.CoreHost.Protocol;

namespace AsteriaScale.CoreHost.Host;

// Test commands, reachable only with --enable-test-commands (hello reports
// testCommands, and the app's supervisor refuses such a helper). They stand
// in for Core where the macOS subset needs a deterministic one: slow,
// cooperative or uncancellable work, a fake hover engine with gates and
// delays, a long-lived child like llama-server, floods of stdout and stderr,
// and a sudden exit. There is no abort mode on purpose: SIGABRT and SIGSEGV
// make ReportCrash write into the owner's real ~/Library/Logs/DiagnosticReports
// whatever HOME the process had.
internal sealed partial class CoreHostServer
{
    private const int MaxTestDelayMs = 600_000;
    private const int FloodFrameBytes = 64 * 1024;
    private const int StderrLineBytes = 1024;

    private readonly List<Process> _testChildren = [];

    private bool AdmitTest(RequestEnvelope envelope)
    {
        switch (envelope.Command)
        {
            case "test.sleep":
                AdmitSleep(envelope);
                return true;
            case "test.fakeCore":
                AdmitControl(envelope, J.TestFakeCorePayload, ConfigureFakeCore);
                return true;
            case "test.stats":
                AdmitControl(envelope, J.EmptyPayload, (request, _) => Respond(request, new TestStatsResult
                {
                    DroppedEvents = _writer.DroppedEvents,
                    FramesWritten = _writer.FramesWritten,
                    Outstanding = Volatile.Read(ref _outstanding),
                    EngineCalls = (Volatile.Read(ref _hoverEngine) as FakeHoverEngine)?.Calls ?? 0,
                }, J.TestStatsResult));
                return true;
            case "test.exit":
                // Exit 70 as an internal error would, at once and without a
                // crash report; the client must see it as EOF.
                AdmitControl(envelope, J.EmptyPayload, (_, _) => PosixProcess.ExitImmediately(ExitCodes.Internal));
                return true;
            case "test.spawnLongChild":
                AdmitControl(envelope, J.EmptyPayload, SpawnLongChild);
                return true;
            case "test.violate":
                AdmitControl(envelope, J.EmptyPayload, (request, _) =>
                {
                    _writer.Raw("this line is not JSON\n"u8.ToArray());
                    Respond(request, new EmptyResult(), J.EmptyResult);
                });
                return true;
            case "test.stderr":
                AdmitBytes(envelope, WriteStderrFlood);
                return true;
            case "test.flood":
                AdmitBytes(envelope, WriteStdoutFlood);
                return true;
            default:
                return false;
        }
    }

    private void AdmitSleep(RequestEnvelope envelope)
    {
        if (!TryPayload(envelope, J.TestSleepPayload, out TestSleepPayload? payload))
        {
            return;
        }

        CancelMode? mode = payload.Mode switch
        {
            null or "immediate" => CancelMode.Immediate,
            "cooperative" => CancelMode.Cooperative,
            "notCancellable" => CancelMode.NotCancellable,
            _ => null,
        };
        if (mode is null || payload.Ms is < 0 or > MaxTestDelayMs || payload.Ticks is < 0 or > 1000)
        {
            _writer.FailUnadmitted(envelope.Id, Errors.BadRequest(
                "test.sleep takes ms 0..600000, ticks 0..1000 and mode immediate, cooperative or notCancellable"));
            return;
        }

        if (!TryReserve(envelope))
        {
            return;
        }

        RequestState request = Register(envelope, Lane.Work, mode.Value);
        StartWork(request, async state =>
        {
            CancellationToken token = payload.IgnoreCancel ? CancellationToken.None : state.Cancellation.Token;
            if (payload.Ticks == 0)
            {
                await Task.Delay(payload.Ms, token).ConfigureAwait(false);
            }
            else
            {
                for (int tick = 1; tick <= payload.Ticks; tick++)
                {
                    await Task.Delay(payload.Ms / payload.Ticks, token).ConfigureAwait(false);
                    _writer.Event(state, "test.tick", new TestTickEvent { N = tick }, J.TestTickEvent);
                }
            }

            Respond(state, new EmptyResult(), J.EmptyResult);
        });
    }

    private void ConfigureFakeCore(RequestState request, TestFakeCorePayload payload)
    {
        if (payload.Phrase is not (null or "none" or "final" or "provisional") ||
            payload.PhraseDelayMs is < 0 or > MaxTestDelayMs ||
            payload.PrimaryDelaysMs?.Values.Any(delay => delay is < 0 or > MaxTestDelayMs) == true)
        {
            Fail(request, Errors.BadRequest(
                "test.fakeCore takes delays 0..600000 ms and phrase none, final or provisional"));
            return;
        }

        Volatile.Write(ref _hoverEngine, new FakeHoverEngine(payload));
        Respond(request, new EmptyResult(), J.EmptyResult);
    }

    // Started the way Core starts llama-server: stdout and stderr redirected,
    // stdin inherited, so it shows what an unredirected child of CoreHost
    // holds (fd 0 must be /dev/null) and that the orderly exit and the
    // parent-gone path both end it.
    private void SpawnLongChild(RequestState request, EmptyPayload _)
    {
        ProcessStartInfo startInfo = new("/bin/sleep")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("30");
        Process child = Process.Start(startInfo) ??
            throw new InvalidOperationException("/bin/sleep did not start");
        lock (_testChildren)
        {
            _testChildren.Add(child);
        }

        Respond(request, new TestChildResult { Pid = child.Id }, J.TestChildResult);
    }

    private void KillTestChildren()
    {
        lock (_testChildren)
        {
            foreach (Process child in _testChildren)
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                child.Dispose();
            }

            _testChildren.Clear();
        }
    }

    private void AdmitBytes(RequestEnvelope envelope, Func<RequestState, int, Task> work)
    {
        if (!TryPayload(envelope, J.TestBytesPayload, out TestBytesPayload? payload))
        {
            return;
        }

        if (payload.Bytes is < 0 or > 64 << 20)
        {
            _writer.FailUnadmitted(envelope.Id, Errors.BadRequest("bytes must be 0..64 MiB"));
            return;
        }

        if (!TryReserve(envelope))
        {
            return;
        }

        RequestState request = Register(envelope, Lane.Work, CancelMode.NotCancellable);
        StartWork(request, state => work(state, payload.Bytes));
    }

    private Task WriteStderrFlood(RequestState request, int bytes)
    {
        byte[] line = Encoding.ASCII.GetBytes(HostLog.Prefix + new string('x', StderrLineBytes - HostLog.Prefix.Length - 1));
        for (int written = 0; written < bytes; written += line.Length + 1)
        {
            HostLog.WriteLine(line);
        }

        Respond(request, new EmptyResult(), J.EmptyResult);
        return Task.CompletedTask;
    }

    private Task WriteStdoutFlood(RequestState request, int bytes)
    {
        string padding = new('x', FloodFrameBytes - 64);
        int tick = 0;
        for (long written = 0; written < bytes && Volatile.Read(ref _state) != Abandoning; written += FloodFrameBytes)
        {
            _writer.Event(request, "test.tick", new TestTickEvent { N = ++tick, Padding = padding }, J.TestTickEvent);
        }

        Respond(request, new EmptyResult(), J.EmptyResult);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A hover engine with scripted delays: a hover on a word listed in
    /// primaryDelaysMs waits that long before its primary card (others do not
    /// wait), then posts no phrase line, a final one, or a provisional one
    /// followed by the final one after phraseDelayMs. Calls counts the hovers
    /// that reached it, so a check can wait until one is inside.
    /// </summary>
    private sealed class FakeHoverEngine(TestFakeCorePayload config) : IHoverEngine
    {
        private static readonly PhraseModelIdentity Model = new()
        {
            Family = "test",
            Variant = "fake",
            Backend = "cpu",
            Identity = "test-fake",
        };

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<string> ResolveAsync(HoverJob job, IHoverEvents events, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            CancellationToken token = config.IgnoreCancel ? CancellationToken.None : cancellationToken;
            if (config.PrimaryDelaysMs?.GetValueOrDefault(job.Source.HoveredWord) is > 0 and int delay)
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }

            if (config.Fail)
            {
                throw new InvalidOperationException("the fake hover engine was told to fail");
            }

            events.Primary(new HoverPrimaryEvent
            {
                Source = job.Source.HoveredWord,
                Translation = "проверка: " + job.Source.HoveredWord,
                Language = "en",
                IsPhrase = false,
                Found = true,
                Provenance = "dictionary",
                EngineLabel = "ПРОВЕРКА",
            });
            if (config.Phrase == "provisional")
            {
                events.Phrase(Phrase(job, "черновик фразы", provisional: true));
            }

            if (config.Phrase is "provisional" or "final")
            {
                if (config.PhraseDelayMs > 0)
                {
                    await Task.Delay(config.PhraseDelayMs, token).ConfigureAwait(false);
                }

                events.Phrase(Phrase(job, "проверочная фраза", provisional: false));
            }

            return HoverOutcomes.Completed;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static HoverPhraseEvent Phrase(HoverJob job, string translation, bool provisional) => new()
        {
            Source = job.Source.Text,
            Translation = translation,
            Status = "translated",
            Provisional = provisional,
            FromCache = false,
            Model = Model,
        };
    }
}
