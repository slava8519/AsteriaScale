using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AsteriaScale.RankerProtocol;

namespace AsteriaScale.Services;

public enum GlossBertRankerProvider
{
    Cpu,
    Npu
}

internal static class GlossBertRankerProviderExtensions
{
    // Exhaustive on purpose: a provider without declared traits must fail
    // loudly instead of borrowing the CPU's gloss rendering and cache identity.
    internal static RankerProviderTraits Traits(this GlossBertRankerProvider provider) =>
        provider switch
        {
            GlossBertRankerProvider.Cpu => RankerProviderTraits.Cpu,
            GlossBertRankerProvider.Npu => RankerProviderTraits.Npu,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
}

public enum RankerCpuModelProfile
{
    GlossBert,
    Deberta,
    Sandwich,
    Consec
}

/// <summary>
/// A normal result may deliberately contain no index. That is a calibrated
/// ranker abstention rather than a transport or model failure.
/// </summary>
public sealed record GlossBertDictionarySelection(
    int? Index,
    bool Abstained,
    double? Margin,
    double? Score,
    double? Confidence,
    GlossBertRankerProvider RequestedProvider,
    string ExecutionProvider,
    string Device,
    string ModelVersion,
    string Pipeline = "cross")
{
    public int? SelectedGroupId => Index;
}

public sealed class GlossBertRankerException : InvalidOperationException
{
    public GlossBertRankerException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public GlossBertRankerException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// Owns the isolated GlossBERT worker and serializes JSONL transactions.
/// The worker provider is explicit: an NPU request can never fall back to CPU.
/// </summary>
public sealed class GlossBertDictionarySelectorService : IAsyncDisposable
{
    private readonly record struct RankerTransactionResult(
        RankerResponse Response,
        RankerCpuModelProfile CpuModelProfile);

    public const string ExpectedModelVersionPrefix =
        "GlossBERT:0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a";
    public const string CpuModelCacheId =
        ExpectedModelVersionPrefix + ":fp32-dynamic";
    public const string NpuModelCacheId =
        ExpectedModelVersionPrefix + ":vitis-epcontext-b4-s64";
    public const string DebertaExpectedModelVersionPrefix = "GlossDeBERTa:";
    public const string SandwichExpectedModelVersionPrefix = "SANDWiCH:";
    public const string ConsecExpectedModelVersionPrefix = "ConSeC:";
    // Exact identity of the current v2 pilot. Replace this in lockstep with the
    // worker manifest pins when a final candidate is promoted.
    public const string DebertaCpuModelCacheId =
        "GlossDeBERTa:DeBERTa-WSD:" +
        "cross-encoder/nli-deberta-v3-xsmall@" +
        "a150876415327c80daeff35ca6f68f5ed8cf5c24:" +
        "8c85154563ac9950:m-8c85154563ac:t-8e71d5c91216:" +
        "s96:b16:c2:p1:fp32-dynamic";
    public const long DebertaManifestLength = 527;
    public const string DebertaManifestSha256 =
        "f4028fe04c8a138dfef3d4e453907c6d2ad9e14ed8a44af75d4410be51149b89";
    public const string SandwichCpuModelCacheId =
        "SANDWiCH:naacl-2025:singleton-gloss:" +
        "m-nv-b6a21c42c974:m-v-21b778dc9552:t-2689db9b56b9:" +
        "s512:c2:p1";
    public const string ConsecCpuModelCacheId =
        "ConSeC:WNGT:9602b5fd:" +
        "adapter-consec-wngt-positional-extractor-v1:" +
        "g-0eb0688f3fd2:d-1257dc55ebec:t-df91c443c8ea:" +
        "pretokenizer-v3:context-full-v1:s512";
    public const string CpuEmbeddingBundleCacheTag =
        "bi-c9745ed1-13c0fb6d-6fd5d72f-be50c362";
    public static readonly string CpuExecutionProvider = RankerProviderTraits.Cpu.ExecutionProvider;
    public static readonly string NpuExecutionProvider = RankerProviderTraits.Npu.ExecutionProvider;
    public const string CpuEngineLabel = "NPU OCR  •  CPU-СМЫСЛ";
    public const string NpuEngineLabel = "NPU OCR  •  NPU-СМЫСЛ";

    private const int MaximumCandidates = 128;
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownResponseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ShutdownExitTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _transactionLock = new(1, 1);
    private readonly System.Threading.Timer _idleTimer;
    private readonly string _workerExecutable;
    private readonly string _modelDirectory;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _responseTimeout;
    private readonly Func<GlossBertRankerProvider, ProcessStartInfo>? _workerStartInfoFactory;
    private RankerCpuModelProfile _cpuModelProfile;
    private Process? _worker;
    private StreamWriter? _workerInput;
    private StreamReader? _workerOutput;
    private Task? _errorDrain;
    private GlossBertRankerProvider? _activeProvider;
    private string? _activeModelVersion;
    private volatile bool _keepWarm = true;
    private volatile bool _disposed;

    // Lets the regression test deterministically pause in the only interval where
    // a completed response can race a profile reconfiguration. Production never
    // assigns this hook.
    internal Func<Task>? BeforeSelectionMaterializationForTests { get; set; }

    public GlossBertDictionarySelectorService()
        : this(
            ResolveCompanionBaseDirectory(
                Environment.ProcessPath,
                AppContext.BaseDirectory),
            DefaultIdleTimeout)
    {
    }

    public GlossBertDictionarySelectorService(RankerCpuModelProfile cpuModelProfile)
        : this(
            ResolveCompanionBaseDirectory(
                Environment.ProcessPath,
                AppContext.BaseDirectory),
            DefaultIdleTimeout,
            cpuModelProfile)
    {
    }

    internal static string ResolveCompanionBaseDirectory(
        string? processPath,
        string appContextBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appContextBaseDirectory);

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string? executableDirectory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(executableDirectory))
            {
                return Path.GetFullPath(executableDirectory);
            }
        }

        return Path.GetFullPath(appContextBaseDirectory);
    }

    internal GlossBertDictionarySelectorService(string baseDirectory, TimeSpan idleTimeout)
        : this(
            baseDirectory,
            idleTimeout,
            DefaultResponseTimeout,
            workerStartInfoFactory: null,
            RankerCpuModelProfile.GlossBert)
    {
    }

    internal GlossBertDictionarySelectorService(
        string baseDirectory,
        TimeSpan idleTimeout,
        RankerCpuModelProfile cpuModelProfile)
        : this(
            baseDirectory,
            idleTimeout,
            DefaultResponseTimeout,
            workerStartInfoFactory: null,
            cpuModelProfile)
    {
    }

    internal GlossBertDictionarySelectorService(
        string baseDirectory,
        TimeSpan idleTimeout,
        TimeSpan responseTimeout,
        Func<GlossBertRankerProvider, ProcessStartInfo>? workerStartInfoFactory)
        : this(
            baseDirectory,
            idleTimeout,
            responseTimeout,
            workerStartInfoFactory,
            RankerCpuModelProfile.GlossBert)
    {
    }

    internal GlossBertDictionarySelectorService(
        string baseDirectory,
        TimeSpan idleTimeout,
        TimeSpan responseTimeout,
        Func<GlossBertRankerProvider, ProcessStartInfo>? workerStartInfoFactory,
        RankerCpuModelProfile cpuModelProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        if (idleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        }

        if (responseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        }

        if (!Enum.IsDefined(cpuModelProfile))
        {
            throw new ArgumentOutOfRangeException(nameof(cpuModelProfile));
        }

        string rankerDirectory = Path.Combine(Path.GetFullPath(baseDirectory), "Ranker");
        _workerExecutable = Path.Combine(
            rankerDirectory,
            "AsteriaScale.RankerWorker.exe");
        _modelDirectory = Path.Combine(rankerDirectory, "Models");
        _idleTimeout = idleTimeout;
        _responseTimeout = responseTimeout;
        _workerStartInfoFactory = workerStartInfoFactory;
        _cpuModelProfile = cpuModelProfile;
        _idleTimer = new System.Threading.Timer(
            static state =>
            {
                var service = (GlossBertDictionarySelectorService)state!;
                _ = service.UnloadAfterIdleAsync();
            },
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public bool IsWorkerRunning
    {
        get
        {
            Process? worker = _worker;
            if (worker is null)
            {
                return false;
            }

            try
            {
                return !worker.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public GlossBertRankerProvider? ActiveProvider =>
        IsWorkerRunning ? _activeProvider : null;

    public string? ActiveModelVersion =>
        IsWorkerRunning ? _activeModelVersion : null;

    public RankerCpuModelProfile CpuModelProfile => _cpuModelProfile;

    internal int? WorkerProcessId
    {
        get
        {
            Process? worker = _worker;
            try
            {
                return worker is not null && !worker.HasExited ? worker.Id : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public static string GetEngineLabel(GlossBertRankerProvider provider) =>
        provider switch
        {
            GlossBertRankerProvider.Cpu => CpuEngineLabel,
            GlossBertRankerProvider.Npu => NpuEngineLabel,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

    // A new provider must name its own artifact identity; inheriting the CPU's
    // would let its verdicts collide with CPU verdicts in the decision cache.
    public static string GetModelCacheId(GlossBertRankerProvider provider) =>
        provider switch
        {
            GlossBertRankerProvider.Cpu => CpuModelCacheId,
            GlossBertRankerProvider.Npu => NpuModelCacheId,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

    public static string GetModelCacheId(
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile)
    {
        ValidateProviderModel(provider, cpuModelProfile);
        return cpuModelProfile switch
        {
            RankerCpuModelProfile.Deberta => DebertaCpuModelCacheId,
            RankerCpuModelProfile.Sandwich => SandwichCpuModelCacheId,
            RankerCpuModelProfile.Consec => ConsecCpuModelCacheId,
            _ => GetModelCacheId(provider)
        };
    }

    public static string GetExpectedModelVersionPrefix(
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile)
    {
        ValidateProviderModel(provider, cpuModelProfile);
        return cpuModelProfile switch
        {
            RankerCpuModelProfile.Deberta => DebertaExpectedModelVersionPrefix,
            RankerCpuModelProfile.Sandwich => SandwichExpectedModelVersionPrefix,
            RankerCpuModelProfile.Consec => ConsecExpectedModelVersionPrefix,
            _ => ExpectedModelVersionPrefix
        };
    }

    internal static void AddWorkerArguments(
        ProcessStartInfo startInfo,
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile,
        string modelDirectory)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ValidateProviderModel(provider, cpuModelProfile);
        startInfo.ArgumentList.Add("--provider");
        startInfo.ArgumentList.Add(provider.Traits().WireName);
        startInfo.ArgumentList.Add("--cpu-model");
        startInfo.ArgumentList.Add(cpuModelProfile switch
        {
            RankerCpuModelProfile.Deberta => "deberta",
            RankerCpuModelProfile.Sandwich => "sandwich",
            RankerCpuModelProfile.Consec => "consec",
            _ => "glossbert"
        });
        startInfo.ArgumentList.Add("--model-directory");
        startInfo.ArgumentList.Add(modelDirectory);
    }

    public void ConfigureKeepWarm(bool keepWarm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _keepWarm = keepWarm;
        if (keepWarm)
        {
            DisableIdleTimer();
        }
        else
        {
            ScheduleIdleUnload();
        }
    }

    public async Task ConfigureCpuModelProfileAsync(
        RankerCpuModelProfile cpuModelProfile,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(cpuModelProfile))
        {
            throw new ArgumentOutOfRangeException(nameof(cpuModelProfile));
        }

        await _transactionLock.WaitAsync(cancellationToken);
        try
        {
            if (_cpuModelProfile == cpuModelProfile)
            {
                return;
            }

            await StopWorkerLockedAsync(attemptGracefulShutdown: true);
            _cpuModelProfile = cpuModelProfile;
        }
        finally
        {
            _transactionLock.Release();
        }
    }

    public async Task WarmUpAsync(
        GlossBertRankerProvider provider,
        CancellationToken cancellationToken = default)
    {
        RankerRequest request = NewRequest(RankerProtocolV2.Warm);
        _ = await SendAsync(request, provider, expectedGroupIds: null, cancellationToken);
    }

    public async Task<GlossBertDictionarySelection> SelectAsync(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<string> candidates,
        IReadOnlyList<int> candidateGroupIds,
        string sourceLanguage,
        GlossBertRankerProvider provider,
        CancellationToken cancellationToken = default)
    {
        ValidateSelectionArguments(
            context,
            target,
            characterIndex,
            candidates,
            candidateGroupIds,
            sourceLanguage);

        string[] normalizedCandidates = candidates
            .Select(value => value.Trim())
            .ToArray();
        int[] stableGroupIds = candidateGroupIds.ToArray();
        RankerRequest request = CreateSelectionRequestForTests(
            context,
            target,
            characterIndex,
            normalizedCandidates,
            stableGroupIds,
            sourceLanguage);

        RankerTransactionResult transaction = await SendAsync(
            request,
            provider,
            stableGroupIds,
            cancellationToken);
        if (BeforeSelectionMaterializationForTests is { } beforeMaterialization)
        {
            await beforeMaterialization();
        }

        return CreateSelection(
            transaction.Response,
            provider,
            stableGroupIds,
            transaction.CpuModelProfile);
    }

    public async Task UnloadAsync()
    {
        DisableIdleTimer();
        await _transactionLock.WaitAsync();
        try
        {
            await StopWorkerLockedAsync(attemptGracefulShutdown: true);
        }
        finally
        {
            _transactionLock.Release();
        }
    }

    private async Task<RankerTransactionResult> SendAsync(
        RankerRequest request,
        GlossBertRankerProvider provider,
        IReadOnlyCollection<int>? expectedGroupIds,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ResetIdleTimerForActivity();

        Task<RankerTransactionResult> transaction = ExecuteTransactionAsync(
            request,
            provider,
            expectedGroupIds,
            cancellationToken);
        try
        {
            RankerTransactionResult response =
                await transaction.WaitAsync(cancellationToken);
            ScheduleIdleUnload();
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // If cancellation happens after the JSON line was written, the private
            // transaction remains the sole stdout consumer and drains exactly one
            // response before releasing the semaphore. The UI can return at once.
            _ = ObserveAbandonedTransactionAsync(transaction);
            throw;
        }
    }

    private async Task<RankerTransactionResult> ExecuteTransactionAsync(
        RankerRequest request,
        GlossBertRankerProvider provider,
        IReadOnlyCollection<int>? expectedGroupIds,
        CancellationToken startCancellationToken)
    {
        await _transactionLock.WaitAsync(startCancellationToken);
        try
        {
            RankerCpuModelProfile transactionCpuModelProfile = _cpuModelProfile;
            await EnsureWorkerLockedAsync(
                provider,
                transactionCpuModelProfile,
                startCancellationToken);
            startCancellationToken.ThrowIfCancellationRequested();

            // Deliberately non-cancellable from the write onward. Cancelling a
            // ReadLine would leave this response queued for the next request.
            await WriteRequestLockedAsync(request);
            RankerResponse response = await ReadResponseLockedAsync();
            ValidateResponse(
                response,
                request.RequestId,
                provider,
                expectedGroupIds,
                expectedCommand: request.Command,
                transactionCpuModelProfile);
            _activeModelVersion = response.ModelVersion;
            return new RankerTransactionResult(
                response,
                transactionCpuModelProfile);
        }
        catch (OperationCanceledException) when (startCancellationToken.IsCancellationRequested)
        {
            // No application request has entered the stream in this path.
            throw;
        }
        catch
        {
            await StopWorkerLockedAsync(attemptGracefulShutdown: false);
            throw;
        }
        finally
        {
            _transactionLock.Release();
        }
    }

    private async Task EnsureWorkerLockedAsync(
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateProviderModel(provider, cpuModelProfile);
        if (_worker is { HasExited: false } && _activeProvider == provider)
        {
            return;
        }

        if (_worker is not null)
        {
            await StopWorkerLockedAsync(attemptGracefulShutdown: true);
        }

        ValidateArtifactPaths();
        ProcessStartInfo startInfo = _workerStartInfoFactory?.Invoke(provider) ??
            new ProcessStartInfo(_workerExecutable);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardInputEncoding = new UTF8Encoding(false);
        startInfo.StandardOutputEncoding = new UTF8Encoding(false);
        startInfo.StandardErrorEncoding = new UTF8Encoding(false);
        if (string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
        {
            startInfo.WorkingDirectory = Path.GetDirectoryName(_workerExecutable)!;
        }

        if (_workerStartInfoFactory is null)
        {
            AddWorkerArguments(
                startInfo,
                provider,
                cpuModelProfile,
                _modelDirectory);
        }

        Process worker;
        try
        {
            worker = Process.Start(startInfo)
                     ?? throw new InvalidOperationException(
                         "Process.Start returned no ranker process.");
        }
        catch (Exception ex)
        {
            throw new GlossBertRankerException(
                "worker_start_failed",
                $"Не удалось запустить изолированный GlossBERT worker: {ex.Message}",
                ex);
        }

        _worker = worker;
        _workerInput = worker.StandardInput;
        _workerInput.AutoFlush = true;
        _workerOutput = worker.StandardOutput;
        _errorDrain = DrainErrorsAsync(worker.StandardError);
        _activeProvider = provider;
        _activeModelVersion = null;

        try
        {
            RankerRequest hello = NewRequest(RankerProtocolV2.Hello);
            await WriteRequestLockedAsync(hello);
            RankerResponse response = await ReadResponseLockedAsync();
            ValidateResponse(
                response,
                hello.RequestId,
                provider,
                expectedGroupIds: null,
                expectedCommand: hello.Command,
                cpuModelProfile);
            _activeModelVersion = response.ModelVersion;
        }
        catch
        {
            await StopWorkerLockedAsync(attemptGracefulShutdown: false);
            throw;
        }
    }

    private async Task WriteRequestLockedAsync(RankerRequest request)
    {
        if (_workerInput is null)
        {
            throw new GlossBertRankerException(
                "worker_not_running",
                "GlossBERT worker input is not available.");
        }

        string payload = JsonSerializer.Serialize(request, RankerProtocolV2.JsonOptions);
        await _workerInput.WriteLineAsync(payload);
        await _workerInput.FlushAsync();
    }

    private async Task<RankerResponse> ReadResponseLockedAsync()
    {
        if (_workerOutput is null)
        {
            throw new GlossBertRankerException(
                "worker_not_running",
                "GlossBERT worker output is not available.");
        }

        Task<string?> readTask = _workerOutput
            .ReadLineAsync(CancellationToken.None)
            .AsTask();
        string? line;
        try
        {
            line = await readTask.WaitAsync(_responseTimeout);
        }
        catch (TimeoutException ex)
        {
            _ = ObserveTimedOutReadAsync(readTask);
            throw new GlossBertRankerException(
                "worker_response_timeout",
                "GlossBERT worker не ответил в установленный срок. " +
                "Процесс будет принудительно перезапущен.",
                ex);
        }

        if (line is null)
        {
            int? exitCode = _worker is { HasExited: true } worker
                ? worker.ExitCode
                : null;
            string suffix = exitCode is null ? string.Empty : $" Код выхода: {exitCode}.";
            throw new GlossBertRankerException(
                "worker_eof",
                "GlossBERT worker завершился, не вернув ответ." + suffix);
        }

        try
        {
            return JsonSerializer.Deserialize<RankerResponse>(
                       line.TrimStart('\uFEFF'),
                       RankerProtocolV2.JsonOptions)
                   ?? throw new JsonException("The response is null.");
        }
        catch (JsonException ex)
        {
            throw new GlossBertRankerException(
                "invalid_worker_json",
                "GlossBERT worker вернул некорректную строку JSON.",
                ex);
        }
    }

    private static async Task ObserveTimedOutReadAsync(Task<string?> readTask)
    {
        try
        {
            _ = await readTask;
        }
        catch
        {
            // The timeout owner kills and disposes the worker tree. This observer
            // only prevents the abandoned pipe read from surfacing unobserved.
        }
    }

    private static void ValidateResponse(
        RankerResponse response,
        string expectedRequestId,
        GlossBertRankerProvider requestedProvider,
        IReadOnlyCollection<int>? expectedGroupIds,
        string expectedCommand,
        RankerCpuModelProfile cpuModelProfile)
    {
        ValidateProviderModel(requestedProvider, cpuModelProfile);
        bool requireSelection = string.Equals(
            expectedCommand,
            RankerProtocolV2.Select,
            StringComparison.Ordinal);
        bool requireLoadedIdentity = requireSelection || string.Equals(
            expectedCommand,
            RankerProtocolV2.Warm,
            StringComparison.Ordinal);
        if (!string.Equals(
                response.Version,
                RankerProtocolV2.Version,
                StringComparison.Ordinal))
        {
            throw ProtocolError(
                $"Worker protocol '{response.Version}' does not match " +
                $"'{RankerProtocolV2.Version}'.");
        }

        if (!string.Equals(
                response.RequestId,
                expectedRequestId,
                StringComparison.Ordinal))
        {
            throw ProtocolError("Worker response requestId does not match the request.");
        }

        string expectedExecutionProvider = requestedProvider.Traits().ExecutionProvider;
        if (!string.Equals(
                response.Provider,
                expectedExecutionProvider,
                StringComparison.Ordinal))
        {
            throw ProtocolError(
                $"Requested {expectedExecutionProvider}, but worker reported " +
                $"'{response.Provider ?? "<null>"}'. Provider fallback is forbidden.");
        }

        string expectedDevice = requestedProvider.Traits().Device;
        if (!string.Equals(response.Device, expectedDevice, StringComparison.Ordinal))
        {
            throw ProtocolError(
                $"Requested device {expectedDevice}, but worker reported " +
                $"'{response.Device ?? "<null>"}'.");
        }

        string expectedModelVersionPrefix = GetExpectedModelVersionPrefix(
            requestedProvider,
            cpuModelProfile);
        if (string.IsNullOrWhiteSpace(response.ModelVersion) ||
            !response.ModelVersion.StartsWith(
                expectedModelVersionPrefix,
                StringComparison.Ordinal))
        {
            throw ProtocolError(
                $"Unexpected ranker model '{response.ModelVersion ?? "<null>"}'.");
        }

        if (!response.Ok)
        {
            string errorCode = string.IsNullOrWhiteSpace(response.ErrorCode)
                ? "worker_error"
                : response.ErrorCode;
            string message = string.IsNullOrWhiteSpace(response.Error)
                ? "GlossBERT worker rejected the request."
                : response.Error;
            throw new GlossBertRankerException(errorCode, message);
        }

        if (requireLoadedIdentity &&
            !HasPinnedArtifactIdentity(
                response.ModelVersion,
                requestedProvider,
                cpuModelProfile))
        {
            throw ProtocolError(
                $"Ranker artifact '{response.ModelVersion}' does not match the " +
                $"requested {requestedProvider} cache identity.");
        }

        if (requireLoadedIdentity &&
            !HasPinnedPipelineIdentity(
                response.ModelVersion,
                requestedProvider,
                cpuModelProfile))
        {
            throw ProtocolError(
                $"Ranker artifact '{response.ModelVersion}' does not declare " +
                "the pinned cross/bi-encoder pipeline identity.");
        }

        ValidateFinite(response.Margin, "margin");
        ValidateFinite(response.Score, "score");
        ValidateFinite(response.Confidence, "confidence");
        if (response.Confidence is < 0 or > 1)
        {
            throw ProtocolError("Worker confidence must be in the [0, 1] range.");
        }

        if (!requireSelection)
        {
            if (response.Index is not null || response.Abstained)
            {
                throw ProtocolError(
                    "A non-selection command returned selection fields.");
            }

            return;
        }

        if (expectedGroupIds is null || expectedGroupIds.Count == 0)
        {
            throw ProtocolError("Selection validation has no candidate group ids.");
        }

        if (response.Margin is null ||
            response.Score is null ||
            response.Confidence is null)
        {
            throw ProtocolError(
                "A selection response must include margin, score and confidence diagnostics.");
        }

        if (response.Pipeline is not (
                "cross" or "bi+cross" or "sandwich" or "consec-independent"))
        {
            throw ProtocolError(
                $"A selection response must declare its ranking pipeline; " +
                $"received '{response.Pipeline ?? "<null>"}'.");
        }

        string? requiredDedicatedPipeline = cpuModelProfile switch
        {
            RankerCpuModelProfile.Sandwich => "sandwich",
            RankerCpuModelProfile.Consec => "consec-independent",
            _ => null
        };
        bool dedicatedPipeline = response.Pipeline is "sandwich" or "consec-independent";
        if (requiredDedicatedPipeline is not null
                ? !string.Equals(
                    response.Pipeline,
                    requiredDedicatedPipeline,
                    StringComparison.Ordinal)
                : dedicatedPipeline)
        {
            throw ProtocolError(
                $"Worker pipeline '{response.Pipeline}' does not match CPU model profile " +
                $"'{cpuModelProfile}'.");
        }

        bool allowsBiEncoderPipeline =
            requestedProvider.Traits().BiEncoderPrefilter &&
            response.ModelVersion.EndsWith(
                ":" + CpuEmbeddingBundleCacheTag,
                StringComparison.Ordinal) &&
            expectedGroupIds.Distinct().Count() > 16;
        if (string.Equals(response.Pipeline, "bi+cross", StringComparison.Ordinal) &&
            !allowsBiEncoderPipeline)
        {
            throw ProtocolError(
                $"Worker pipeline '{response.Pipeline}' does not match the pinned " +
                $"{requestedProvider} artifact or candidate inventory.");
        }

        if (response.Abstained)
        {
            if (response.Index is not null)
            {
                throw ProtocolError("An abstained response cannot contain an index.");
            }

            return;
        }

        if (response.Index is not { } selectedGroupId)
        {
            throw ProtocolError(
                "A non-abstained response must contain a selected group id.");
        }

        if (!expectedGroupIds.Contains(selectedGroupId))
        {
            throw ProtocolError(
                $"Worker selected unknown candidate group id {selectedGroupId}.");
        }
    }

    private static GlossBertDictionarySelection CreateSelection(
        RankerResponse response,
        GlossBertRankerProvider requestedProvider,
        IReadOnlyCollection<int> expectedGroupIds,
        RankerCpuModelProfile cpuModelProfile)
    {
        ValidateResponse(
            response,
            response.RequestId,
            requestedProvider,
            expectedGroupIds,
            expectedCommand: RankerProtocolV2.Select,
            cpuModelProfile);
        return new GlossBertDictionarySelection(
            response.Index,
            response.Abstained,
            response.Margin,
            response.Score,
            response.Confidence,
            requestedProvider,
            response.Provider!,
            response.Device!,
            response.ModelVersion!,
            response.Pipeline ?? "cross");
    }

    private static void ValidateSelectionArguments(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<string> candidates,
        IReadOnlyList<int> candidateGroupIds,
        string sourceLanguage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(candidateGroupIds);
        if (characterIndex < 0 || characterIndex > context.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(characterIndex));
        }

        if (candidates.Count == 0 || candidates.Count > MaximumCandidates)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidates),
                $"GlossBERT expects between 1 and {MaximumCandidates} candidates.");
        }

        if (candidates.Count != candidateGroupIds.Count)
        {
            throw new ArgumentException(
                "candidateGroupIds must be parallel to candidates.",
                nameof(candidateGroupIds));
        }

        if (candidates.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "GlossBERT candidates cannot be empty.",
                nameof(candidates));
        }

        if (candidateGroupIds.Any(groupId => groupId < 0))
        {
            throw new ArgumentException(
                "Candidate group ids must be non-negative stable outcome ids.",
                nameof(candidateGroupIds));
        }
    }

    private void ValidateArtifactPaths()
    {
        if (!File.Exists(_workerExecutable))
        {
            throw new GlossBertRankerException(
                "worker_missing",
                $"Не найден GlossBERT worker: '{_workerExecutable}'.");
        }

        if (!Directory.Exists(_modelDirectory))
        {
            throw new GlossBertRankerException(
                "model_directory_missing",
                $"Не найден каталог модели GlossBERT: '{_modelDirectory}'.");
        }
    }

    private async Task StopWorkerLockedAsync(bool attemptGracefulShutdown)
    {
        Process? worker = _worker;
        StreamWriter? input = _workerInput;
        StreamReader? output = _workerOutput;
        Task? errorDrain = _errorDrain;
        GlossBertRankerProvider? provider = _activeProvider;

        _worker = null;
        _workerInput = null;
        _workerOutput = null;
        _errorDrain = null;
        _activeProvider = null;
        _activeModelVersion = null;
        if (worker is null)
        {
            input?.Dispose();
            output?.Dispose();
            return;
        }

        bool graceful = false;
        if (attemptGracefulShutdown &&
            provider is { } requestedProvider &&
            !worker.HasExited &&
            input is not null &&
            output is not null)
        {
            try
            {
                RankerRequest shutdown = NewRequest(RankerProtocolV2.Shutdown);
                string payload = JsonSerializer.Serialize(
                    shutdown,
                    RankerProtocolV2.JsonOptions);
                await input.WriteLineAsync(payload);
                await input.FlushAsync();

                using CancellationTokenSource responseTimeout =
                    new(ShutdownResponseTimeout);
                string? line = await output
                    .ReadLineAsync(CancellationToken.None)
                    .AsTask()
                    .WaitAsync(responseTimeout.Token);
                RankerResponse response = JsonSerializer.Deserialize<RankerResponse>(
                                              line ?? string.Empty,
                                              RankerProtocolV2.JsonOptions)
                                          ?? throw new JsonException(
                                              "Empty shutdown response.");
                ValidateResponse(
                    response,
                    shutdown.RequestId,
                    requestedProvider,
                    expectedGroupIds: null,
                    expectedCommand: shutdown.Command,
                    _cpuModelProfile);

                using CancellationTokenSource exitTimeout =
                    new(ShutdownExitTimeout);
                await worker.WaitForExitAsync(exitTimeout.Token);
                graceful = true;
            }
            catch
            {
                // A malformed or timed-out shutdown is never trusted. The whole
                // process tree is terminated below so model memory is reclaimed.
            }
        }

        if (!graceful && !worker.HasExited)
        {
            try
            {
                worker.Kill(entireProcessTree: true);
                using CancellationTokenSource killTimeout =
                    new(ShutdownExitTimeout);
                await worker.WaitForExitAsync(killTimeout.Token);
            }
            catch
            {
                // Best-effort cleanup during shutdown or transport recovery.
            }
        }

        input?.Dispose();
        output?.Dispose();
        worker.Dispose();
        if (errorDrain is not null)
        {
            try
            {
                await errorDrain.WaitAsync(ShutdownExitTimeout);
            }
            catch
            {
                // Native diagnostics cannot hold the app shutdown path.
            }
        }
    }

    private static async Task DrainErrorsAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is not null)
            {
                // Vitis/ORT diagnostics are intentionally drained from a private
                // pipe. They never share stdout with the JSONL protocol.
            }
        }
        catch (ObjectDisposedException)
        {
            // Process cleanup owns the reader lifetime.
        }
    }

    private async Task ObserveAbandonedTransactionAsync(
        Task<RankerTransactionResult> transaction)
    {
        try
        {
            await transaction;
        }
        catch
        {
            // ExecuteTransactionAsync already kills a failed worker. Observation
            // here only prevents an unobserved background task exception.
        }
        finally
        {
            ScheduleIdleUnload();
        }
    }

    private void ResetIdleTimerForActivity()
    {
        if (!_disposed && !_keepWarm)
        {
            DisableIdleTimer();
        }
    }

    private void ScheduleIdleUnload()
    {
        if (_disposed || _keepWarm)
        {
            return;
        }

        try
        {
            _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Dispose won the race with a completed transaction.
        }
    }

    private void DisableIdleTimer()
    {
        try
        {
            _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Idempotent shutdown.
        }
    }

    private async Task UnloadAfterIdleAsync()
    {
        if (_disposed || _keepWarm)
        {
            return;
        }

        try
        {
            await UnloadAsync();
        }
        catch
        {
            // A future request can start a fresh isolated process.
        }
    }

    private static RankerRequest NewRequest(string command) => new()
    {
        Command = command,
        RequestId = Guid.NewGuid().ToString("N")
    };

    internal static RankerRequest CreateSelectionRequestForTests(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<string> candidates,
        IReadOnlyList<int> candidateGroupIds,
        string sourceLanguage) =>
        NewRequest(RankerProtocolV2.Select) with
        {
            Context = context,
            Target = target.Trim(),
            CharacterIndex = characterIndex,
            SourceLanguage = sourceLanguage.Trim(),
            Candidates = candidates,
            CandidateGroupIds = candidateGroupIds
        };

    private static void ValidateFinite(double? value, string fieldName)
    {
        if (value is { } number && (double.IsNaN(number) || double.IsInfinity(number)))
        {
            throw ProtocolError($"Worker {fieldName} must be finite.");
        }
    }

    private static bool HasPinnedArtifactIdentity(
        string modelVersion,
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile)
    {
        if (cpuModelProfile == RankerCpuModelProfile.GlossBert)
        {
            return modelVersion.StartsWith(
                GetModelCacheId(provider),
                StringComparison.Ordinal);
        }

        if (cpuModelProfile == RankerCpuModelProfile.Sandwich)
        {
            return provider.Traits().AlternativeModels &&
                   modelVersion.StartsWith(
                       SandwichCpuModelCacheId,
                       StringComparison.Ordinal);
        }

        if (cpuModelProfile == RankerCpuModelProfile.Consec)
        {
            return provider.Traits().AlternativeModels &&
                   modelVersion.StartsWith(
                       ConsecCpuModelCacheId,
                       StringComparison.Ordinal);
        }

        return provider.Traits().AlternativeModels &&
               TrySplitDebertaArtifactIdentity(modelVersion, out _);
    }

    private static bool HasPinnedPipelineIdentity(
        string modelVersion,
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile)
    {
        string suffix;
        if (cpuModelProfile == RankerCpuModelProfile.GlossBert)
        {
            string artifactIdentity = GetModelCacheId(provider);
            if (!modelVersion.StartsWith(artifactIdentity, StringComparison.Ordinal))
            {
                return false;
            }

            suffix = modelVersion[artifactIdentity.Length..];
        }
        else if (cpuModelProfile == RankerCpuModelProfile.Sandwich)
        {
            if (!provider.Traits().AlternativeModels ||
                !modelVersion.StartsWith(
                    SandwichCpuModelCacheId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            suffix = modelVersion[SandwichCpuModelCacheId.Length..];
        }
        else if (cpuModelProfile == RankerCpuModelProfile.Consec)
        {
            if (!provider.Traits().AlternativeModels ||
                !modelVersion.StartsWith(
                    ConsecCpuModelCacheId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            suffix = modelVersion[ConsecCpuModelCacheId.Length..];
        }
        else if (!provider.Traits().AlternativeModels ||
                 !TrySplitDebertaArtifactIdentity(modelVersion, out suffix))
        {
            return false;
        }

        if (suffix.StartsWith(":onnx-", StringComparison.Ordinal))
        {
            int tagEnd = suffix.IndexOf(':', 6);
            if (tagEnd < 0 ||
                !long.TryParse(
                    suffix.AsSpan(6, tagEnd - 6),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long version) ||
                version <= 0)
            {
                return false;
            }

            suffix = suffix[tagEnd..];
        }

        if (string.Equals(suffix, ":pipeline-cross", StringComparison.Ordinal))
        {
            return true;
        }

        if (cpuModelProfile == RankerCpuModelProfile.Sandwich &&
            string.Equals(suffix, ":pipeline-sandwich", StringComparison.Ordinal))
        {
            return true;
        }

        if (cpuModelProfile == RankerCpuModelProfile.Consec &&
            string.Equals(
                suffix,
                ":pipeline-consec-independent",
                StringComparison.Ordinal))
        {
            return true;
        }

        return provider.Traits().BiEncoderPrefilter &&
               string.Equals(
                   suffix,
                   ":" + CpuEmbeddingBundleCacheTag,
                   StringComparison.Ordinal);
    }

    private static bool TrySplitDebertaArtifactIdentity(
        string modelVersion,
        out string suffix)
    {
        suffix = string.Empty;
        if (!modelVersion.StartsWith(
                DebertaCpuModelCacheId,
                StringComparison.Ordinal))
        {
            return false;
        }

        suffix = modelVersion[DebertaCpuModelCacheId.Length..];
        return true;
    }

    private static void ValidateProviderModel(
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider));
        }

        if (!Enum.IsDefined(cpuModelProfile))
        {
            throw new ArgumentOutOfRangeException(nameof(cpuModelProfile));
        }

        if (!provider.Traits().AlternativeModels &&
            cpuModelProfile != RankerCpuModelProfile.GlossBert)
        {
            throw new GlossBertRankerException(
                "unsupported_provider_model",
                "The selected semantic ranker is CPU-only; NPU remains pinned to GlossBERT.");
        }
    }

    private static GlossBertRankerException ProtocolError(string message) =>
        new("protocol_violation", message);

    internal static GlossBertDictionarySelection ValidateSelectionResponseForTests(
        RankerResponse response,
        string expectedRequestId,
        GlossBertRankerProvider requestedProvider,
        IReadOnlyCollection<int> expectedGroupIds,
        RankerCpuModelProfile cpuModelProfile = RankerCpuModelProfile.GlossBert)
    {
        ValidateResponse(
            response,
            expectedRequestId,
            requestedProvider,
            expectedGroupIds,
            expectedCommand: RankerProtocolV2.Select,
            cpuModelProfile);
        return CreateSelection(
            response,
            requestedProvider,
            expectedGroupIds,
            cpuModelProfile);
    }

    internal static void ValidateWarmResponseForTests(
        RankerResponse response,
        string expectedRequestId,
        GlossBertRankerProvider requestedProvider,
        RankerCpuModelProfile cpuModelProfile = RankerCpuModelProfile.GlossBert) =>
        ValidateResponse(
            response,
            expectedRequestId,
            requestedProvider,
            expectedGroupIds: null,
            expectedCommand: RankerProtocolV2.Warm,
            cpuModelProfile);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisableIdleTimer();
        await _transactionLock.WaitAsync();
        try
        {
            await StopWorkerLockedAsync(attemptGracefulShutdown: true);
        }
        finally
        {
            _transactionLock.Release();
        }

        _idleTimer.Dispose();
        _transactionLock.Dispose();
    }
}
