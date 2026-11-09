using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

internal enum TranslateGemmaExecutionBackend
{
    Cpu,
    Vulkan
}

internal sealed record SensePromptPlan(
    string Prompt,
    IReadOnlyList<ScaffoldSenseHint> AppliedSenseHints);

/// <summary>
/// Owns an isolated llama.cpp process for the pinned TranslateGemma experiment.
/// Artifact acquisition is deliberately outside this service: a missing or
/// changed binary is an explicit setup error, never a network side effect or a
/// silent model/backend substitution.
/// </summary>
internal sealed class TranslateGemmaTranslationService :
    ISmartPhraseTranslator,
    IHoverPhraseModel,
    IAsyncDisposable
{
    public const string EngineLabel = "TRANSLATEGEMMA 4B";
    public const string ModelIdentity =
        "google/translategemma-4b-it@10042cb0e6e7fdce748996a71dc3dc432a4e0c89" +
        "/mradermacher-static-q4_K_M@35a7486e128b19642cdc72d7b91b21ba388aaf42:" +
        ModelSha256 + "/llama.cpp-b9632-cpu";
    /// <summary>
    /// The runner's own Vulkan numeric path, KHR_coopmat allowed. It shipped
    /// until 8 November 2026, so every stored run before that date carries
    /// this identity; a benchmark reproduces it with --phrase-coopmat on.
    /// </summary>
    internal const string VulkanCoopmatModelIdentity =
        "google/translategemma-4b-it@10042cb0e6e7fdce748996a71dc3dc432a4e0c89" +
        "/mradermacher-static-q4_K_M@35a7486e128b19642cdc72d7b91b21ba388aaf42:" +
        ModelSha256 + "/llama.cpp-b9632-vulkan-full";
    public const string VulkanModelIdentity =
        VulkanCoopmatModelIdentity + NoCoopmatIdentitySuffix;
    /// <summary>
    /// One quantization of the pinned model: file name, exact bytes, SHA-256
    /// and the identity both backends report. Production always uses
    /// <see cref="Default"/>; a benchmark may name a different quantization,
    /// but never a looser check — an alternative pin still has to match its own
    /// size and hash, and it renames the identity so a report can never claim
    /// numbers were produced by the shipped artifact. The Vulkan identity here
    /// is the runner-default numeric path; <see cref="RuntimeIdentity"/> adds
    /// the coopmat choice on top of it.
    /// </summary>
    internal sealed record TranslateGemmaModelPin(
        string FileName,
        long Bytes,
        string Sha256,
        string CpuIdentity,
        string VulkanIdentity)
    {
        public static TranslateGemmaModelPin Default { get; } = new(
            LocalModelFileName,
            ModelSize,
            ModelSha256,
            ModelIdentity,
            VulkanCoopmatModelIdentity);

        public string BlobFileName => "sha256-" + Sha256;

        public string IdentityFor(TranslateGemmaExecutionBackend backend) => backend switch
        {
            TranslateGemmaExecutionBackend.Cpu => CpuIdentity,
            TranslateGemmaExecutionBackend.Vulkan => VulkanIdentity,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
        };
    }

    /// <summary>
    /// Whether ggml-vulkan may use its KHR_coopmat (cooperative-matrix)
    /// shaders. On b9632 with the Radeon 860M those shaders make greedy output
    /// depend on earlier requests to the same server process (audit E17);
    /// without them a completion is a function of its prompt alone, at a
    /// measured latency cost. The choice changes the numeric path, so it is
    /// part of the Vulkan runtime identity and never an invisible tweak.
    /// </summary>
    internal enum VulkanCoopmat
    {
        Enabled,
        Disabled
    }

    /// <summary>
    /// The numeric path production runs. A benchmark may select the other one,
    /// but the identity then names it, so stored runs cannot be mixed.
    /// Disabled since the same-build dev201 A/B of 8 November 2026: no sense
    /// or COMET loss in either lane, output order-free, +6–10 % latency
    /// (RESEARCH.md, "Vulkan без coopmat"). Changing it requires a new A/B.
    /// </summary>
    internal const VulkanCoopmat ShippedVulkanCoopmat = VulkanCoopmat.Disabled;
    internal const string DisableCoopmatEnvironmentVariable = "GGML_VK_DISABLE_COOPMAT";
    internal const string NoCoopmatIdentitySuffix = "-nocoopmat";

    public const string DirectPromptIdentity = "translategemma-direct-en-ru-v1";
    public const string CorrectionPromptIdentity = "translategemma-ape-en-ru-v4";
    public const string SenseConditionedPromptIdentity =
        "translategemma-sense-conditioned-en-ru-v2";
    public const string SenseRepairPromptIdentity = "translategemma-sense-repair-en-ru-v2";
    public const string SenseGatePolicyIdentity =
        "translategemma-sense-gate-policy-v3/" + SenseEvidenceTrustPolicy.Identity;
    public const string PromptIdentity = "translategemma-direct-ape-sense-gate-en-ru-v8";
    public const string RunnerBuild = "b9632";
    public const string RunnerArchiveFileName = "llama-b9632-bin-win-cpu-x64.zip";
    public const string RunnerArchiveSha256 =
        "b835d5c5155dd2a5ed748a0351debf2ede0dc9f808757e0429f8700a11832dcd";
    public const long RunnerArchiveSize = 16_899_258;
    public const string VulkanRunnerArchiveFileName =
        "llama-b9632-bin-win-vulkan-x64.zip";
    public const string VulkanRunnerArchiveSha256 =
        "90b914ff48167370190477ff3f54fd2c4eb034a305b943913eaa310eda78793d";
    public const long VulkanRunnerArchiveSize = 38_545_553;
    public const string ModelSha256 =
        "81200d03e843d2ec1ece6eeafe7d13cb6e5211e1fcd336ade55790b683a08330";
    public const long ModelSize = 2_489_909_760;
    public const int ContextSize = 2_048;

    internal const string LocalModelFileName = "translategemma-4b-it.Q4_K_M.gguf";
    internal const string OllamaBlobFileName = "sha256-" + ModelSha256;
    internal const int MaximumGeneratedTokens = 256;
    internal const int MaximumSourceCharacters = SmartPhraseContextBuilder.MaximumCharacters;
    // The pinned server has a 2,048-token context and reserves 256 tokens for
    // output. A conservative character ceiling keeps JSON-heavy multilingual
    // prompts below that window without relying on an unpinned tokenizer API.
    internal const int MaximumSensePromptCharacters = 4_800;

    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupEvidenceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RuntimeDeleteInitialDelay = TimeSpan.FromMilliseconds(25);
    // App.xaml acquires the single-instance mutex before constructing this
    // service. Exact generated names can therefore be swept immediately; a
    // surviving orphan process will keep its DLLs locked and bounded retry
    // leaves that directory intact.
    private static readonly TimeSpan StaleRuntimeMinimumAge = TimeSpan.Zero;
    internal const int RuntimeDeleteMaximumAttempts = 8;
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly string _modelRoot;
    private readonly string _ollamaModelsRoot;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<TranslateGemmaExecutionBackend> _backendResolver;
    private readonly ProcessLogTail _processLog = new();

    private Process? _server;
    private Task? _stdoutDrain;
    private Task? _stderrDrain;
    private Uri? _endpoint;
    private string? _apiKey;
    private string? _runtimeDirectory;
    private readonly TranslateGemmaModelPin _modelPin;
    private readonly VulkanCoopmat _vulkanCoopmat;
    private readonly bool _cachePrompt;
    private TranslateGemmaExecutionBackend? _activeBackend;
    private string? _activeModelIdentity;
    private VulkanStartupEvidenceTracker? _startupEvidence;
    private bool _disposed;

    public TranslateGemmaTranslationService(
        Func<TranslateGemmaExecutionBackend>? backendResolver = null)
        : this(
            ResolveDefaultModelRoot(),
            ResolveDefaultOllamaModelsRoot(),
            DefaultStartupTimeout,
            DefaultRequestTimeout,
            backendResolver: backendResolver)
    {
    }

    /// <summary>
    /// Benchmark entry point for measuring an alternative quantization or
    /// Vulkan numeric path. The pin is still enforced, and the reported
    /// identity names both choices, so a run cannot be mistaken for the
    /// shipped runtime. <paramref name="cachePrompt"/> = false makes every
    /// completion recompute its whole prompt instead of reusing the server's
    /// KV prefix; it exists to measure order dependence (audit E17), which it
    /// showed the cache does not cause.
    /// </summary>
    internal TranslateGemmaTranslationService(
        TranslateGemmaModelPin modelPin,
        Func<TranslateGemmaExecutionBackend>? backendResolver = null,
        VulkanCoopmat vulkanCoopmat = ShippedVulkanCoopmat,
        bool cachePrompt = true)
        : this(
            ResolveDefaultModelRoot(),
            ResolveDefaultOllamaModelsRoot(),
            DefaultStartupTimeout,
            DefaultRequestTimeout,
            backendResolver: backendResolver,
            modelPin: modelPin,
            vulkanCoopmat: vulkanCoopmat,
            cachePrompt: cachePrompt)
    {
    }

    /// <summary>
    /// The model/runtime identity this instance answers for. Requests must
    /// carry exactly this value, so a pipeline built around a variant cannot
    /// receive a translation produced under another numeric path.
    /// </summary>
    public string ModelIdentityFor(TranslateGemmaExecutionBackend backend) =>
        RuntimeIdentity(_modelPin, backend, _vulkanCoopmat);

    internal VulkanCoopmat VulkanCoopmatMode => _vulkanCoopmat;

    internal bool CachePrompt => _cachePrompt;

    internal TranslateGemmaTranslationService(
        string modelRoot,
        string ollamaModelsRoot,
        TimeSpan startupTimeout,
        TimeSpan requestTimeout,
        HttpMessageHandler? handler = null,
        Func<TranslateGemmaExecutionBackend>? backendResolver = null,
        TranslateGemmaModelPin? modelPin = null,
        VulkanCoopmat vulkanCoopmat = ShippedVulkanCoopmat,
        bool cachePrompt = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ollamaModelsRoot);
        if (startupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        }

        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        if (!Enum.IsDefined(vulkanCoopmat))
        {
            throw new ArgumentOutOfRangeException(nameof(vulkanCoopmat));
        }

        _modelPin = modelPin ?? TranslateGemmaModelPin.Default;
        _vulkanCoopmat = vulkanCoopmat;
        _cachePrompt = cachePrompt;
        _modelRoot = Path.GetFullPath(modelRoot);
        _ollamaModelsRoot = Path.GetFullPath(ollamaModelsRoot);
        _startupTimeout = startupTimeout;
        _requestTimeout = requestTimeout;
        _backendResolver = backendResolver ?? (static () => TranslateGemmaExecutionBackend.Cpu);
        _httpClient = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            })
            : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public bool IsWarm
    {
        get
        {
            Process? server = _server;
            if (_endpoint is null ||
                server is null ||
                _activeBackend is null ||
                _activeModelIdentity is null)
            {
                return false;
            }

            try
            {
                return !server.HasExited;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    internal TranslateGemmaExecutionBackend? ActiveBackend => _activeBackend;

    internal string RequestedModelIdentity =>
        ModelIdentityFor(ResolveRequestedBackend());

    internal static TranslateGemmaExecutionBackend ResolveBackend(string? settingsValue)
    {
        if (string.IsNullOrWhiteSpace(settingsValue) ||
            string.Equals(
                settingsValue,
                SmartPhraseTranslationBackends.Cpu,
                StringComparison.OrdinalIgnoreCase))
        {
            return TranslateGemmaExecutionBackend.Cpu;
        }

        if (string.Equals(
                settingsValue,
                SmartPhraseTranslationBackends.Vulkan,
                StringComparison.OrdinalIgnoreCase))
        {
            return TranslateGemmaExecutionBackend.Vulkan;
        }

        throw new InvalidOperationException(
            $"Неизвестный backend TranslateGemma: {settingsValue}.");
    }

    internal static TranslateGemmaExecutionBackend ResolveBackendIdentity(
        string modelIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        if (string.Equals(modelIdentity, ModelIdentity, StringComparison.Ordinal))
        {
            return TranslateGemmaExecutionBackend.Cpu;
        }

        if (string.Equals(modelIdentity, VulkanModelIdentity, StringComparison.Ordinal))
        {
            return TranslateGemmaExecutionBackend.Vulkan;
        }

        throw new InvalidOperationException(
            $"TranslateGemma не поддерживает model identity {modelIdentity}.");
    }

    /// <summary>
    /// Maps a request identity to the backend of this instance. Unlike
    /// <see cref="ResolveBackendIdentity"/>, it accepts only what this
    /// instance itself reports: a request built for another quantization or
    /// numeric path fails here instead of later as an identity mismatch.
    /// </summary>
    internal TranslateGemmaExecutionBackend ResolveRequestBackend(string modelIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        foreach (TranslateGemmaExecutionBackend backend in Enum.GetValues<TranslateGemmaExecutionBackend>())
        {
            if (string.Equals(modelIdentity, ModelIdentityFor(backend), StringComparison.Ordinal))
            {
                return backend;
            }
        }

        throw new InvalidOperationException(
            $"TranslateGemma не поддерживает model identity {modelIdentity}.");
    }

    internal static string GetModelIdentity(TranslateGemmaExecutionBackend backend) =>
        RuntimeIdentity(TranslateGemmaModelPin.Default, backend, ShippedVulkanCoopmat);

    internal static string RuntimeIdentity(
        TranslateGemmaModelPin modelPin,
        TranslateGemmaExecutionBackend backend,
        VulkanCoopmat vulkanCoopmat)
    {
        ArgumentNullException.ThrowIfNull(modelPin);
        string identity = modelPin.IdentityFor(backend);
        // Only the Vulkan runner reads the variable; the CPU identity must
        // stay comparable across both settings.
        return backend == TranslateGemmaExecutionBackend.Vulkan &&
               vulkanCoopmat == VulkanCoopmat.Disabled
            ? identity + NoCoopmatIdentitySuffix
            : identity;
    }

    internal static string GetEngineLabel(TranslateGemmaExecutionBackend backend) => backend switch
    {
        TranslateGemmaExecutionBackend.Cpu => EngineLabel + "  •  CPU",
        TranslateGemmaExecutionBackend.Vulkan => EngineLabel + "  •  VULKAN",
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
    };

    private TranslateGemmaExecutionBackend ResolveRequestedBackend()
    {
        TranslateGemmaExecutionBackend backend = _backendResolver();
        return Enum.IsDefined(backend)
            ? backend
            : throw new InvalidOperationException(
                $"Неизвестный backend TranslateGemma: {(int)backend}.");
    }

    private bool IsWarmFor(TranslateGemmaExecutionBackend backend) =>
        IsWarm &&
        _activeBackend == backend &&
        string.Equals(
            _activeModelIdentity,
            ModelIdentityFor(backend),
            StringComparison.Ordinal);

    private void EnsureActiveBackend(
        TranslateGemmaExecutionBackend backend,
        string modelIdentity)
    {
        if (_activeBackend != backend ||
            !string.Equals(_activeModelIdentity, modelIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"TranslateGemma runtime не подтверждает backend {backend} и identity {modelIdentity}.");
        }
    }

    private void ResetActiveBackend()
    {
        _activeBackend = null;
        _activeModelIdentity = null;
        _startupEvidence = null;
    }

    Task ISmartPhraseTranslator.PrepareAsync(
        SmartPhraseTranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WarmUpAsync(
            ResolveRequestBackend(request.ModelIdentity),
            cancellationToken);
    }

    /// <summary>
    /// Validates both pinned artifacts, extracts the explicitly selected runner
    /// to an owned directory, starts the server and verifies its backend.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        TranslateGemmaExecutionBackend backend = ResolveRequestedBackend();
        await WarmUpAsync(backend, cancellationToken);
    }

    private async Task WarmUpAsync(
        TranslateGemmaExecutionBackend backend,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (IsWarmFor(backend))
        {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            // Backend replacement owns both locks in the same order as
            // UnloadAsync. A new warm-up must never kill the server beneath an
            // in-flight completion from the previous backend.
            await _requestLock.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                if (IsWarmFor(backend))
                {
                    return;
                }

                await StopAndCleanupOwnedRuntimeAsync();

                using CancellationTokenSource startup =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(_startupTimeout);
                try
                {
                    RuntimeArtifacts artifacts = await PrepareArtifactsAsync(backend, startup.Token);
                    _runtimeDirectory = artifacts.RuntimeDirectory;
                    StartServer(artifacts);
                    await WaitForHealthAsync(startup.Token);
                    await WaitForStartupEvidenceAsync(backend, startup.Token);
                    _activeBackend = backend;
                    _activeModelIdentity = ModelIdentityFor(backend);
                }
                catch (OperationCanceledException cancellationFailure)
                    when (cancellationToken.IsCancellationRequested)
                {
                    IReadOnlyList<Exception> cleanupFailures =
                        await CollectCleanupErrorsAsync(
                            StopOwnedServerAsync,
                            CleanupRuntimeDirectoriesAsync);
                    foreach (Exception cleanupFailure in cleanupFailures)
                    {
                        _processLog.Append(
                            "cleanup",
                            cleanupFailure.GetType().Name + ": " + cleanupFailure.Message);
                    }

                    ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
                    throw;
                }
                catch (OperationCanceledException cancellationFailure)
                {
                    TimeoutException timeoutFailure = new(
                        $"TranslateGemma не запустилась за {_startupTimeout.TotalSeconds:0} с. " +
                        ProcessFailureSuffix(),
                        cancellationFailure);
                    await ThrowStartupFailureAfterCleanupAsync(timeoutFailure);
                    throw;
                }
                catch (Exception startupFailure)
                {
                    await ThrowStartupFailureAfterCleanupAsync(startupFailure);
                    throw;
                }
            }
            finally
            {
                _requestLock.Release();
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Runs the byte-exact official direct prompt for diagnostics and callers
    /// that do not have a typed Smart Dictionary request. Contextual
    /// post-editing is orchestrated only by <see cref="TranslateAsync"/>.
    /// </summary>
    public async Task<string> TranslatePhraseToRussianAsync(
        string phrase,
        CancellationToken cancellationToken = default)
    {
        string source = NormalizeSource(phrase);
        TranslateGemmaExecutionBackend backend = ResolveRequestedBackend();
        string modelIdentity = ModelIdentityFor(backend);
        await WarmUpAsync(backend, cancellationToken);

        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureActiveBackend(backend, modelIdentity);
            Uri endpoint = _endpoint ??
                throw new InvalidOperationException("TranslateGemma не прогрета.");
            string apiKey = _apiKey ??
                throw new InvalidOperationException("TranslateGemma не получила ключ локального API.");
            string requestJson = BuildCompletionRequestJson(source, _cachePrompt);

            using HttpRequestMessage request = new(
                HttpMethod.Post,
                new Uri(endpoint, "completion"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    timeout.Token);
                string responseJson = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"TranslateGemma вернула HTTP {(int)response.StatusCode}: " +
                        LimitDiagnostic(responseJson));
                }

                return ParseCompletionResponse(responseJson);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"TranslateGemma не ответила за {_requestTimeout.TotalSeconds:0} с. " +
                    ProcessFailureSuffix());
            }
            catch (HttpRequestException exception)
            {
                throw new InvalidOperationException(
                    "Локальный сервер TranslateGemma недоступен. " + ProcessFailureSuffix(),
                    exception);
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public Task<SmartPhraseTranslationResult> TranslateAsync(
        SmartPhraseTranslationRequest request,
        CancellationToken cancellationToken) =>
        TranslateAsync(
            request,
            directDraft: null,
            directPreview: null,
            cancellationToken);

    /// <summary>
    /// Sends the byte-exact official direct prompt that
    /// <see cref="TranslateAsync(SmartPhraseTranslationRequest, SmartPhraseDirectDraft?, Action{SmartPhraseTranslationResult}?, CancellationToken)"/>
    /// would send first. Handing the draft back changes when candidate A is
    /// computed, never what it is, so the sense gate sees the same hypothesis.
    /// </summary>
    public async Task<SmartPhraseDirectDraft?> GenerateDirectDraftAsync(
        string modelInputPhrase,
        string modelIdentity,
        CancellationToken cancellationToken)
    {
        // Same identity contract as TranslateAsync: a draft requested for
        // another quantization or Vulkan numeric path fails here, not later.
        TranslateGemmaExecutionBackend backend = ResolveRequestBackend(modelIdentity);
        string pinnedIdentity = ModelIdentityFor(backend);
        string source = NormalizeSource(modelInputPhrase);
        await WarmUpAsync(backend, cancellationToken);
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureActiveBackend(backend, pinnedIdentity);
            Uri endpoint = _endpoint ??
                throw new InvalidOperationException("TranslateGemma is not warmed up.");
            string apiKey = _apiKey ??
                throw new InvalidOperationException("TranslateGemma did not obtain a local API key.");
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            long started = Stopwatch.GetTimestamp();
            string direct = await SendCompletionAsync(
                endpoint,
                apiKey,
                BuildRawPrompt(source),
                timeout.Token);
            return new SmartPhraseDirectDraft(
                source,
                pinnedIdentity,
                direct,
                ElapsedMillisecondsSince(started));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"TranslateGemma did not finish the direct pass within {_requestTimeout.TotalSeconds:0} s. " +
                ProcessFailureSuffix());
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task<SmartPhraseTranslationResult> TranslateAsync(
        SmartPhraseTranslationRequest request,
        SmartPhraseDirectDraft? directDraft,
        Action<SmartPhraseTranslationResult>? directPreview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        TranslateGemmaExecutionBackend backend =
            ResolveRequestBackend(request.ModelIdentity);
        string modelIdentity = ModelIdentityFor(backend);

        if (!string.Equals(request.PromptVersion, PromptIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Запрошен prompt {request.PromptVersion}, но runtime использует {PromptIdentity}.");
        }

        if (SmartPhraseTranslationGuard.ValidateRequest(request) !=
            SmartPhraseRejectionReason.None)
        {
            throw new ArgumentException(
                "TranslateGemma received an invalid Smart Dictionary sentence request.",
                nameof(request));
        }

        string source = NormalizeSource(request.ModelInputPhrase);
        bool reuseDraft = CanReuseDirectDraft(directDraft, source, modelIdentity);
        await WarmUpAsync(backend, cancellationToken);
        long started = Stopwatch.GetTimestamp();
        SmartPhraseRefinement refined;
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            EnsureActiveBackend(backend, modelIdentity);
            Uri endpoint = _endpoint ??
                throw new InvalidOperationException("TranslateGemma is not warmed up.");
            string apiKey = _apiKey ??
                throw new InvalidOperationException("TranslateGemma did not obtain a local API key.");
            // A reused draft spent its own bounded budget; this one covers the
            // remaining passes, so the per-pass worst case is unchanged.
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            Func<string, CancellationToken, Task<string>> completeAsync =
                (prompt, token) => SendCompletionAsync(endpoint, apiKey, prompt, token);

            string direct = reuseDraft
                ? directDraft!.Translation
                : await completeAsync(BuildRawPrompt(source), timeout.Token);
            refined = await RefineDirectAsync(
                request,
                modelIdentity,
                direct,
                completeAsync,
                timeout.Token,
                cancellationToken,
                directPreview);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"TranslateGemma did not finish its bounded translation passes within {_requestTimeout.TotalSeconds:0} s. " +
                ProcessFailureSuffix());
        }
        finally
        {
            _requestLock.Release();
        }

        long elapsed = ElapsedMillisecondsSince(started) +
                       (reuseDraft ? directDraft!.ElapsedMilliseconds : 0);
        return new SmartPhraseTranslationResult(
            refined.Translation,
            modelIdentity,
            elapsed,
            Cacheable: refined.Cacheable,
            GenerationRoute: refined.Route)
        {
            AppliedSenseHints = refined.AppliedSenseHints
        };
    }

    internal static bool CanReuseDirectDraft(
        SmartPhraseDirectDraft? draft,
        string normalizedSource,
        string modelIdentity) =>
        draft is not null &&
        string.Equals(draft.ModelInputPhrase, normalizedSource, StringComparison.Ordinal) &&
        string.Equals(draft.ModelIdentity, modelIdentity, StringComparison.Ordinal);

    /// <summary>
    /// Everything after candidate A: the sense gate when accepted evidence
    /// yields a conditioned prompt, otherwise the optional context post-edit.
    /// Static and completion-injected so the orchestration, including when the
    /// preview fires, is testable without a llama-server process.
    /// </summary>
    internal static async Task<SmartPhraseRefinement> RefineDirectAsync(
        SmartPhraseTranslationRequest request,
        string modelIdentity,
        string direct,
        Func<string, CancellationToken, Task<string>> completeAsync,
        CancellationToken completionToken,
        CancellationToken callerCancellationToken,
        Action<SmartPhraseTranslationResult>? directPreview = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentNullException.ThrowIfNull(completeAsync);
        bool cacheable = true;
        SmartPhraseGenerationRoute generationRoute = SmartPhraseGenerationRoute.Direct;
        IReadOnlyList<ScaffoldSenseHint> appliedSenseHints =
            Array.Empty<ScaffoldSenseHint>();
        bool requiresGenderPostEdit =
            request.CurrentSpeaker?.Gender is SpeakerGender.Masculine or SpeakerGender.Feminine;
        SmartPhraseGuardResult directGuard = SmartPhraseTranslationGuard.Validate(
            request,
            new SmartPhraseTranslationResult(
                direct,
                modelIdentity,
                ElapsedMilliseconds: 0));
        SmartPhraseGuardResult directDisplayGuard =
            SmartPhraseTranslationGuard.ValidateForDisplay(
                request,
                new SmartPhraseTranslationResult(
                    direct,
                    modelIdentity,
                    ElapsedMilliseconds: 0));
        string translation = directGuard.Accepted
            ? directGuard.Translation
            : directDisplayGuard.Accepted
                ? directDisplayGuard.Translation
                : SmartPhraseTranslationGuard.NormalizeOutput(direct);
        string? postEditFailureFallback = SelectPostEditFailureFallback(
            directGuard,
            directDisplayGuard);

        SensePromptPlan? conditionedPlan = request.SenseHints.Count > 0
            ? TryBuildSenseConditionedPromptPlan(request)
            : null;
        SmartPhraseTranslationRequest postEditRequest =
            conditionedPlan is null && request.SenseHints.Count > 0
                ? CreateContextOnlyPostEditRequest(request)
                : request;
        bool postEdit = conditionedPlan is null &&
                        ShouldPostEdit(postEditRequest, directGuard);
        if (conditionedPlan is not null || postEdit)
        {
            // Only a hypothesis that a later pass may still replace is worth
            // a provisional line; a final A would just render twice.
            PublishDirectPreview(directPreview, direct, modelIdentity);
        }

        if (conditionedPlan is not null)
        {
            SensePreservationRunResult gated = await RunSensePreservationGateAsync(
                request,
                modelIdentity,
                direct,
                completeAsync,
                completionToken,
                callerCancellationToken,
                conditionedPlan);
            translation = gated.Selected.Translation;
            generationRoute = gated.Selected.Kind switch
            {
                SensePreservationCandidateKind.Conditioned =>
                    SmartPhraseGenerationRoute.SenseConditioned,
                SensePreservationCandidateKind.Repair =>
                    SmartPhraseGenerationRoute.SenseRepair,
                _ => SmartPhraseGenerationRoute.Direct
            };
            appliedSenseHints = gated.Selected.AppliedSenseHints;
            cacheable = gated.Cacheable &&
                        (!requiresGenderPostEdit ||
                         generationRoute != SmartPhraseGenerationRoute.Direct);
        }
        else if (postEdit)
        {
            try
            {
                callerCancellationToken.ThrowIfCancellationRequested();
                string corrected = await completeAsync(
                    BuildPostEditRawPrompt(postEditRequest, translation),
                    completionToken);
                SmartPhraseGuardResult correctedGuard =
                    SmartPhraseTranslationGuard.Validate(
                        postEditRequest,
                        new SmartPhraseTranslationResult(
                            corrected,
                            modelIdentity,
                            ElapsedMilliseconds: 0));
                SmartPhraseGuardResult correctedDisplayGuard =
                    SmartPhraseTranslationGuard.ValidateForDisplay(
                        postEditRequest,
                        new SmartPhraseTranslationResult(
                            corrected,
                            modelIdentity,
                            ElapsedMilliseconds: 0));
                if (correctedGuard.Accepted)
                {
                    translation = correctedGuard.Translation;
                    generationRoute = SmartPhraseGenerationRoute.ContextPostEdit;
                }
                else if (correctedDisplayGuard.Accepted)
                {
                    bool useCorrected = requiresGenderPostEdit ||
                                        !directDisplayGuard.Accepted ||
                                        IsDisplaySemanticImprovement(
                                            postEditRequest,
                                            directDisplayGuard.Translation,
                                            correctedDisplayGuard.Translation);
                    translation = useCorrected
                        ? correctedDisplayGuard.Translation
                        : directDisplayGuard.Translation;
                    generationRoute = useCorrected
                        ? SmartPhraseGenerationRoute.ContextPostEdit
                        : SmartPhraseGenerationRoute.Direct;
                }
                else if (directDisplayGuard.Accepted)
                {
                    translation = directDisplayGuard.Translation;
                    cacheable = !requiresGenderPostEdit;
                }
                else if (!directGuard.Accepted)
                {
                    // Returning the rejected correction makes the shared
                    // caller guard report unavailable and keeps it out of
                    // the persistent cache.
                    translation = corrected;
                }
            }
            catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch when (postEditFailureFallback is not null)
            {
                // The primary dictionary line is already visible. A valid
                // direct translation remains useful if optional post-edit
                // exhausts the shared budget or the server rejects it.
                translation = postEditFailureFallback!;
                cacheable = !requiresGenderPostEdit;
            }
        }

        return new SmartPhraseRefinement(
            translation,
            generationRoute,
            appliedSenseHints,
            cacheable);
    }

    private static void PublishDirectPreview(
        Action<SmartPhraseTranslationResult>? directPreview,
        string direct,
        string modelIdentity)
    {
        if (directPreview is null)
        {
            return;
        }

        try
        {
            directPreview(new SmartPhraseTranslationResult(
                direct,
                modelIdentity,
                ElapsedMilliseconds: 0,
                Cacheable: false,
                GenerationRoute: SmartPhraseGenerationRoute.Direct));
        }
        catch (Exception)
        {
            // The preview only paints a provisional line. A consumer failure
            // must not cost the refinement that the final line depends on; the
            // consumer logs its own errors.
        }
    }

    private static long ElapsedMillisecondsSince(long started) =>
        (long)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MidpointRounding.AwayFromZero);

    public async Task UnloadAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleLock.WaitAsync();
        try
        {
            await _requestLock.WaitAsync();
            try
            {
                _endpoint = null;
                _apiKey = null;
                ResetActiveBackend();
                await StopAndCleanupOwnedRuntimeAsync();
            }
            finally
            {
                _requestLock.Release();
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await UnloadAsync();
        _disposed = true;
        _httpClient.Dispose();
        _requestLock.Dispose();
        _lifecycleLock.Dispose();
    }

    internal static string BuildRawPrompt(string phrase)
    {
        string source = NormalizeSource(phrase);
        const string instruction =
            "You are a professional English (en) to Russian (ru) translator. " +
            "Your goal is to accurately convey the meaning and nuances of the original English text " +
            "while adhering to Russian grammar, vocabulary, and cultural sensitivities.\n" +
            "Produce only the Russian translation, without any additional explanations or commentary. " +
            "Please translate the following English text into Russian:";

        // The official guide requires two empty lines, hence three newline
        // characters between the instruction line and the source text.
        return "<start_of_turn>user\n" + instruction + "\n\n\n" + source +
               "<end_of_turn>\n<start_of_turn>model\n";
    }

    internal static string BuildPostEditRawPrompt(
        SmartPhraseTranslationRequest request,
        string draft)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (SmartPhraseTranslationGuard.ValidateRequest(request) !=
            SmartPhraseRejectionReason.None)
        {
            throw new ArgumentException("Invalid post-edit request.", nameof(request));
        }

        string current = NormalizeSource(request.ModelInputPhrase);
        string normalizedDraft = SmartPhraseTranslationGuard.NormalizeOutput(draft);
        if (normalizedDraft.Length == 0 ||
            normalizedDraft.Length > SmartPhraseTranslationGuard.MaximumOutputCharacters)
        {
            throw new ArgumentException("Invalid Russian draft for post-editing.", nameof(draft));
        }

        var evidence = new
        {
            properNames = request.ProperNames,
            requiredConcepts = request.SemanticTerms.Select(term => new
            {
                source = term.Source,
                russian = term.Translation
            }),
            selectedSenses = request.SenseHints.Select(hint => new
            {
                source = hint.Source,
                meaning = hint.EnglishDefinition,
                russian = hint.SelectedTranslation
            })
        };
        string contextJson = BuildPreviousContextPromptJson(request.PreviousContext);
        string currentSpeakerJson = BuildCurrentSpeakerPromptJson(request.CurrentSpeaker);
        string currentJson = JsonSerializer.Serialize(current, PromptJsonOptions);
        string draftJson = JsonSerializer.Serialize(normalizedDraft, PromptJsonOptions);
        string evidenceJson = JsonSerializer.Serialize(evidence, PromptJsonOptions);
        const string instruction =
            "You are performing automatic translation post-editing from English to Russian. " +
            "Translate the complete CURRENT text only. PREVIOUS_CONTEXT is context only: never translate, quote, " +
            "summarize, or continue it, and never emit its speakerMetadata. " +
            "CURRENT_SPEAKER is grammatical metadata only: never emit it or turn it into a speaker label. " +
            "When its grammaticalGender is " +
            "masculine or feminine, use it only to inflect words whose subject is the current speaker, including " +
            "first-person or omitted-subject forms. Never apply that gender to an addressee or a third person. " +
            "When grammaticalGender is unknown or ignore, do not infer or force a gender from this metadata. " +
            "Correct the Russian draft when needed. Preserve every clause and semantic role, negation, modality, " +
            "cause, contrast, tone, deliberate irony, and content on both sides of an internal ellipsis. " +
            "An internal ellipsis marks hesitation, not omitted or censored text. " +
            "Preserve a speaker label or outer quotation wrapper only when it occurs literally in CURRENT. " +
            "PROPER_NAMES in SEMANTIC EVIDENCE are advisory identity anchors: preserve the person or entity, " +
            "transliterating naturally into Russian when appropriate, and never introduce a listed name absent from CURRENT. " +
            "Never add a name, speaker label, possessive, modifier, detail, or specificity not licensed by CURRENT. " +
            "Translate quoted wordplay as a whole or copy its complete quoted wording; never truncate it. " +
            "SEMANTIC evidence identifies intended concepts or senses; render it naturally with suitable inflection " +
            "instead of copying wording mechanically. Return only the corrected Russian translation of CURRENT, " +
            "without labels, numbering, explanations, or alternatives.";
        string payload =
            instruction +
            "\nPREVIOUS_CONTEXT (DO NOT TRANSLATE, JSON entries): " + contextJson +
            "\nCURRENT_SPEAKER (DO NOT OUTPUT, JSON metadata): " + currentSpeakerJson +
            "\nCURRENT (TRANSLATE ONLY THIS JSON string): " + currentJson +
            "\nRUSSIAN DRAFT (POST-EDIT THIS): " + draftJson +
            "\nSEMANTIC EVIDENCE (JSON): " + evidenceJson;
        return "<start_of_turn>user\n" + payload +
               "<end_of_turn>\n<start_of_turn>model\n";
    }

    internal static string BuildSenseConditionedRawPrompt(
        SmartPhraseTranslationRequest request)
        => TryBuildSenseConditionedPromptPlan(request)?.Prompt ??
           throw new InvalidOperationException(
               "No unambiguous sense evidence fits the pinned model context.");

    internal static SensePromptPlan? TryBuildSenseConditionedPromptPlan(
        SmartPhraseTranslationRequest request)
    {
        ValidateSensePromptRequest(request);
        IReadOnlyList<ScaffoldSenseHint> applicable =
            NeuralSenseFidelityGuard.GetApplicableHints(
                request.SourcePhrase,
                request.SenseHints);
        return BuildBudgetedSensePromptPlan(
            request,
            applicable,
            (context, names, terms, applied) =>
                BuildSenseConditionedRawPromptCore(
                    request,
                    context,
                    names,
                    terms,
                    applied));
    }

    private static string BuildSenseConditionedRawPromptCore(
        SmartPhraseTranslationRequest request,
        IReadOnlyList<string> previousContext,
        IReadOnlyList<string> properNames,
        IReadOnlyList<ScaffoldTerm> semanticTerms,
        IReadOnlyList<ScaffoldSenseHint> appliedHints)
    {
        string current = NormalizeSource(request.ModelInputPhrase);
        string contextJson = BuildPreviousContextPromptJson(previousContext);
        string currentSpeakerJson = BuildCurrentSpeakerPromptJson(request.CurrentSpeaker);
        string currentJson = JsonSerializer.Serialize(current, PromptJsonOptions);
        string evidenceJson = BuildSenseEvidencePromptJson(
            properNames,
            semanticTerms,
            appliedHints);
        const string instruction =
            "You are producing an independent English-to-Russian translation candidate. " +
            "Translate the complete CURRENT text only. PREVIOUS_CONTEXT is context only: never translate, quote, " +
            "summarize, continue, or output it or its speakerMetadata. CURRENT_SPEAKER is grammatical metadata " +
            "only and must never become a label or a name in the output. When grammaticalGender is masculine or " +
            "feminine, use it only for forms whose subject is the current speaker; never transfer it to an " +
            "addressee or third person. Preserve every clause, semantic role, negation, modality, cause, contrast, " +
            "tone, deliberate irony, quotation, and both sides of an internal ellipsis. Never invent a name, " +
            "possessive, modifier, detail, or specificity. SEMANTIC_EVIDENCE contains required concepts and " +
            "senseConstraints accepted by a separate disambiguator. For each source occurrence, preserve its " +
            "meaning and use selectedRussian only as an inflectable lexical anchor, not wording to copy. Do not " +
            "use rejectedRussian to render that source occurrence. A natural Russian paraphrase of the selected " +
            "meaning is allowed. Return only one complete Russian translation of CURRENT, without labels, " +
            "numbering, explanations, alternatives, or the English source.";
        string payload =
            instruction +
            "\nPREVIOUS_CONTEXT (DO NOT TRANSLATE, JSON entries): " + contextJson +
            "\nCURRENT_SPEAKER (DO NOT OUTPUT, JSON metadata): " + currentSpeakerJson +
            "\nCURRENT (TRANSLATE ONLY THIS JSON string): " + currentJson +
            "\nSEMANTIC_EVIDENCE (JSON): " + evidenceJson;
        return "<start_of_turn>user\n" + payload +
               "<end_of_turn>\n<start_of_turn>model\n";
    }

    internal static string BuildSenseRepairRawPrompt(
        SmartPhraseTranslationRequest request,
        string draft,
        IReadOnlyList<ScaffoldSenseHint> repairHints,
        IReadOnlyList<string>? structuralViolationCodes = null)
        => TryBuildSenseRepairPromptPlan(
               request,
               draft,
               repairHints,
           structuralViolationCodes)?.Prompt ??
           throw new InvalidOperationException(
               "No repair evidence fits the pinned model context.");

    internal static SensePromptPlan? TryBuildSenseRepairPromptPlan(
        SmartPhraseTranslationRequest request,
        string draft,
        IReadOnlyList<ScaffoldSenseHint> repairHints,
        IReadOnlyList<string>? structuralViolationCodes = null)
    {
        ValidateSensePromptRequest(request);
        ArgumentNullException.ThrowIfNull(repairHints);
        string[] structuralCodes = (structuralViolationCodes ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (structuralCodes.Any(code => code is not
                SensePreservationGate.PolarityMismatchCode and not
                SensePreservationGate.NumberNotPreservedCode))
        {
            throw new ArgumentException(
                "Unknown structural violation code.",
                nameof(structuralViolationCodes));
        }

        if (repairHints.Count == 0 && structuralCodes.Length == 0)
        {
            throw new ArgumentException(
                "At least one violated sense or structure fact is required.",
                nameof(repairHints));
        }

        string normalizedDraft = SmartPhraseTranslationGuard.NormalizeOutput(draft);
        if (normalizedDraft.Length == 0 ||
            normalizedDraft.Length > SmartPhraseTranslationGuard.MaximumOutputCharacters)
        {
            throw new ArgumentException("Invalid Russian draft for sense repair.", nameof(draft));
        }

        HashSet<string> focusSources = repairHints
            .Select(hint => hint.Source)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<ScaffoldSenseHint> allApplicable =
            NeuralSenseFidelityGuard.GetApplicableHints(
                request.SourcePhrase,
                request.SenseHints);
        IReadOnlyList<ScaffoldSenseHint> applicable = repairHints.Count > 0
            ? allApplicable
                .Where(hint => focusSources.Contains(hint.Source))
                .ToArray()
            : allApplicable;
        return BuildBudgetedSensePromptPlan(
            request,
            applicable,
            (context, names, terms, applied) =>
                BuildSenseRepairRawPromptCore(
                    request,
                    normalizedDraft,
                    context,
                    names,
                    terms,
                    applied,
                    structuralCodes));
    }

    private static string BuildSenseRepairRawPromptCore(
        SmartPhraseTranslationRequest request,
        string normalizedDraft,
        IReadOnlyList<string> previousContext,
        IReadOnlyList<string> properNames,
        IReadOnlyList<ScaffoldTerm> semanticTerms,
        IReadOnlyList<ScaffoldSenseHint> appliedHints,
        IReadOnlyList<string> structuralViolationCodes)
    {
        string current = NormalizeSource(request.ModelInputPhrase);
        string contextJson = BuildPreviousContextPromptJson(previousContext);
        string currentSpeakerJson = BuildCurrentSpeakerPromptJson(request.CurrentSpeaker);
        string currentJson = JsonSerializer.Serialize(current, PromptJsonOptions);
        string draftJson = JsonSerializer.Serialize(normalizedDraft, PromptJsonOptions);
        string[] focusSources = appliedHints
            .Select(hint => hint.Source)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string evidenceJson = BuildSenseEvidencePromptJson(
            properNames,
            semanticTerms,
            appliedHints);
        object[] violations = focusSources
            .Select(source => (object)new
            {
                source,
                code = "sense_not_preserved"
            })
            .Concat(structuralViolationCodes.Select(code => (object)new { code }))
            .ToArray();
        string violationJson = JsonSerializer.Serialize(violations, PromptJsonOptions);
        const string instruction =
            "You are repairing one English-to-Russian translation after a deterministic sense-preservation check. " +
            "Translate the complete CURRENT text only. The RUSSIAN_DRAFT is not authoritative: repair each listed " +
            "violation while preserving every clause, semantic role, negation, modality, cause, contrast, tone, " +
            "quotation, and both sides of an internal ellipsis. PREVIOUS_CONTEXT and CURRENT_SPEAKER are hidden " +
            "context only and must never be output. For each violated source occurrence, preserve the selected " +
            "English meaning. Treat selectedRussian as an inflectable lexical anchor and do not use rejectedRussian " +
            "to render that occurrence. Do not delete the concept merely to avoid a rejected word, and do not invent " +
            "details. Return only one complete corrected Russian translation of CURRENT, without labels, numbering, " +
            "explanations, or alternatives.";
        string payload =
            instruction +
            "\nPREVIOUS_CONTEXT (DO NOT TRANSLATE, JSON entries): " + contextJson +
            "\nCURRENT_SPEAKER (DO NOT OUTPUT, JSON metadata): " + currentSpeakerJson +
            "\nCURRENT (TRANSLATE ONLY THIS JSON string): " + currentJson +
            "\nRUSSIAN_DRAFT (REPAIR, JSON string): " + draftJson +
            "\nVIOLATIONS (JSON): " + violationJson +
            "\nSEMANTIC_EVIDENCE (JSON): " + evidenceJson;
        return "<start_of_turn>user\n" + payload +
               "<end_of_turn>\n<start_of_turn>model\n";
    }

    internal static async Task<SensePreservationRunResult> RunSensePreservationGateAsync(
        SmartPhraseTranslationRequest request,
        string modelIdentity,
        string direct,
        Func<string, CancellationToken, Task<string>> completeAsync,
        CancellationToken completionToken,
        CancellationToken callerCancellationToken,
        SensePromptPlan? prebuiltConditionedPlan = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentNullException.ThrowIfNull(completeAsync);
        if (request.SenseHints.Count == 0)
        {
            throw new ArgumentException("Sense gate requires accepted sense evidence.", nameof(request));
        }

        List<SensePreservationCandidate> candidates =
        [
            SensePreservationGate.Evaluate(
                request,
                direct,
                modelIdentity,
                SensePreservationCandidateKind.Direct)
        ];
        SensePromptPlan? conditionedPlan = prebuiltConditionedPlan ??
                                           TryBuildSenseConditionedPromptPlan(request);
        if (conditionedPlan is null)
        {
            return new SensePreservationRunResult(
                candidates[0],
                AdditionalGenerationCount: 0,
                Cacheable: IsCandidateCacheable(candidates[0]));
        }

        int additionalGenerations = 0;
        try
        {
            additionalGenerations++;
            string conditioned = await completeAsync(
                conditionedPlan.Prompt,
                completionToken);
            candidates.Add(SensePreservationGate.Evaluate(
                request,
                conditioned,
                modelIdentity,
                SensePreservationCandidateKind.Conditioned,
                conditionedPlan.AppliedSenseHints));
            callerCancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            callerCancellationToken.ThrowIfCancellationRequested();
            if (candidates[0].DisplayAccepted)
            {
                // The primary dictionary line is already visible. A transient
                // conditioned-pass failure may show direct A, but must not make it
                // persistent: the next hover should be allowed to try the gate again.
                return new SensePreservationRunResult(
                    candidates[0],
                    additionalGenerations,
                    Cacheable: false);
            }

            throw;
        }

        SensePreservationCandidate selected =
            SensePreservationGate.SelectBest(request, candidates);
        if (!SensePreservationGate.RequiresTargetedRepair(selected))
        {
            return new SensePreservationRunResult(
                selected,
                additionalGenerations,
                Cacheable: IsCandidateCacheable(selected));
        }

        SensePromptPlan? repairPlan = TryBuildSenseRepairPromptPlan(
            request,
            selected.Translation,
            selected.SenseAssessment.RepairHints,
            selected.StructuralViolationCodes);
        if (repairPlan is null)
        {
            return new SensePreservationRunResult(
                selected,
                additionalGenerations,
                Cacheable: false);
        }

        try
        {
            additionalGenerations++;
            string repaired = await completeAsync(
                repairPlan.Prompt,
                completionToken);
            candidates.Add(SensePreservationGate.Evaluate(
                request,
                repaired,
                modelIdentity,
                SensePreservationCandidateKind.Repair,
                repairPlan.AppliedSenseHints));
            callerCancellationToken.ThrowIfCancellationRequested();
            selected = SensePreservationGate.SelectBest(request, candidates);
            return new SensePreservationRunResult(
                selected,
                additionalGenerations,
                Cacheable: IsCandidateCacheable(selected));
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            callerCancellationToken.ThrowIfCancellationRequested();
            if (selected.DisplayAccepted)
            {
                return new SensePreservationRunResult(
                    selected,
                    additionalGenerations,
                    Cacheable: false);
            }

            throw;
        }
    }

    private static bool IsCandidateCacheable(SensePreservationCandidate candidate) =>
        candidate.StructuralPenaltyCount == 0 &&
        !candidate.SenseAssessment.HasBlockingState;

    private static void ValidateSensePromptRequest(SmartPhraseTranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (SmartPhraseTranslationGuard.ValidateRequest(request) !=
                SmartPhraseRejectionReason.None ||
            request.SenseHints.Count == 0)
        {
            throw new ArgumentException("Invalid sense-conditioned request.", nameof(request));
        }
    }

    internal static SmartPhraseTranslationRequest CreateContextOnlyPostEditRequest(
        SmartPhraseTranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Repeated or internally incompatible source-level WSD evidence has no
        // character span. It must not sneak back into the legacy APE prompt
        // after the gate omits it.
        return request with { SenseHints = Array.Empty<ScaffoldSenseHint>() };
    }

    private static SensePromptPlan? BuildBudgetedSensePromptPlan(
        SmartPhraseTranslationRequest request,
        IReadOnlyList<ScaffoldSenseHint> applicableHints,
        Func<
            IReadOnlyList<string>,
            IReadOnlyList<string>,
            IReadOnlyList<ScaffoldTerm>,
            IReadOnlyList<ScaffoldSenseHint>,
            string> buildPrompt)
    {
        if (applicableHints.Count == 0)
        {
            return null;
        }

        List<string> context = request.PreviousContext.ToList();
        List<string> names = request.ProperNames.ToList();
        List<ScaffoldTerm> mandatoryTerms = request.SemanticTerms
            .Where(ScaffoldTermPolicy.IsMandatory)
            .ToList();
        List<ScaffoldTerm> advisoryTerms = request.SemanticTerms
            .Where(term => !ScaffoldTermPolicy.IsMandatory(term))
            .ToList();
        List<string> sources = applicableHints
            .GroupBy(hint => hint.Source, StringComparer.Ordinal)
            .OrderByDescending(group => group.Any(hint => hint.IsPrimary))
            .Select(group => group.Key)
            .ToList();

        string BuildCurrent()
        {
            HashSet<string> included = new(sources, StringComparer.Ordinal);
            ScaffoldSenseHint[] applied = applicableHints
                .Where(hint => included.Contains(hint.Source))
                .GroupBy(hint => hint.Source, StringComparer.Ordinal)
                .SelectMany(group => group
                    .GroupBy(hint => hint.EnglishDefinition, StringComparer.Ordinal)
                    .Select(definitions => definitions.First())
                    .Take(SemanticSenseEvidenceFactory.MaximumDefinitionsPerSense))
                .ToArray();
            return buildPrompt(
                context,
                names,
                mandatoryTerms.Concat(advisoryTerms).ToArray(),
                applied);
        }

        string prompt = BuildCurrent();
        while (prompt.Length > MaximumSensePromptCharacters && context.Count > 0)
        {
            context.RemoveAt(0);
            prompt = BuildCurrent();
        }

        while (prompt.Length > MaximumSensePromptCharacters && advisoryTerms.Count > 0)
        {
            advisoryTerms.RemoveAt(advisoryTerms.Count - 1);
            prompt = BuildCurrent();
        }

        while (prompt.Length > MaximumSensePromptCharacters && names.Count > 0)
        {
            names.RemoveAt(names.Count - 1);
            prompt = BuildCurrent();
        }

        // Sources are ordered by evidence priority. Trimming from the tail
        // preserves the hover-selected sense whenever at least one group fits.
        while (prompt.Length > MaximumSensePromptCharacters && sources.Count > 1)
        {
            sources.RemoveAt(sources.Count - 1);
            prompt = BuildCurrent();
        }

        if (prompt.Length > MaximumSensePromptCharacters || sources.Count == 0)
        {
            return null;
        }

        HashSet<string> appliedSources = new(sources, StringComparer.Ordinal);
        IReadOnlyList<ScaffoldSenseHint> finalApplied = applicableHints
            .Where(hint => appliedSources.Contains(hint.Source))
            .GroupBy(hint => hint.Source, StringComparer.Ordinal)
            .SelectMany(group => group
                .GroupBy(hint => hint.EnglishDefinition, StringComparer.Ordinal)
                .Select(definitions => definitions.First())
                .Take(SemanticSenseEvidenceFactory.MaximumDefinitionsPerSense))
            .ToArray();
        return new SensePromptPlan(prompt, finalApplied);
    }

    private static string BuildSenseEvidencePromptJson(
        IReadOnlyList<string> properNames,
        IReadOnlyList<ScaffoldTerm> semanticTerms,
        IReadOnlyList<ScaffoldSenseHint> appliedHints)
    {
        object[] senseConstraints = appliedHints
            .GroupBy(hint => hint.Source, StringComparer.Ordinal)
            .Select(group =>
            {
                ScaffoldSenseHint[] grouped = group.ToArray();
                string[] selected = grouped
                    .Select(hint => hint.SelectedTranslation)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (!NeuralSenseFidelityGuard.AreMutuallyCompatible(selected))
                {
                    return null;
                }

                string selectedRussian = selected[0];
                string[] rejected = grouped
                    .SelectMany(hint => hint.RejectedTranslations)
                    .Where(value => !NeuralSenseFidelityGuard.AreEquivalentSurfaces(
                        selectedRussian,
                        value))
                    .Distinct(StringComparer.Ordinal)
                    .Take(4)
                    .ToArray();
                return (object)new
                {
                    source = group.Key,
                    selectedMeanings = grouped
                        .Select(hint => hint.EnglishDefinition)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.Ordinal)
                        .Take(2),
                    selectedRussian,
                    rejectedRussian = rejected
                };
            })
            .Where(value => value is not null)
            .Cast<object>()
            .ToArray();
        var evidence = new
        {
            policy = SenseGatePolicyIdentity,
            properNames,
            requiredConcepts = semanticTerms.Select(term => new
            {
                source = term.Source,
                russian = term.Translation,
                mandatory = ScaffoldTermPolicy.IsMandatory(term)
            }),
            senseConstraints
        };
        return JsonSerializer.Serialize(evidence, PromptJsonOptions);
    }

    internal static string BuildPreviousContextPromptJson(
        IReadOnlyList<string> previousContext)
    {
        ArgumentNullException.ThrowIfNull(previousContext);
        var entries = previousContext.Select((value, order) =>
        {
            (string text, string? speakerMetadata) = SplitPreviousContextEntry(value);
            return new
            {
                order,
                role = "context_only",
                text,
                speakerMetadata
            };
        });
        return JsonSerializer.Serialize(entries, PromptJsonOptions);
    }

    internal static string BuildCurrentSpeakerPromptJson(CurrentSpeakerContext? speaker)
    {
        if (speaker is null)
        {
            return "null";
        }

        string grammaticalGender = speaker.Gender switch
        {
            SpeakerGender.Masculine => "masculine",
            SpeakerGender.Feminine => "feminine",
            SpeakerGender.Ignore => "ignore",
            _ => "unknown"
        };
        return JsonSerializer.Serialize(
            new
            {
                role = "current_speaker",
                grammaticalGender
            },
            PromptJsonOptions);
    }

    private static (string Text, string? SpeakerMetadata) SplitPreviousContextEntry(
        string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return (string.Empty, null);
        }

        if (normalized[0] == '[')
        {
            int closing = normalized.IndexOf(']');
            if (closing is > 1 and <= 49)
            {
                string speaker = normalized[1..closing].Trim();
                if (LooksLikeSpeakerMetadata(speaker))
                {
                    return (normalized[(closing + 1)..].Trim(), speaker);
                }
            }
        }

        int colon = normalized.IndexOf(':');
        if (colon is > 0 and <= 48)
        {
            string speaker = normalized[..colon].Trim();
            if (LooksLikeSpeakerMetadata(speaker))
            {
                return (normalized[(colon + 1)..].Trim(), speaker);
            }
        }

        return LooksLikeSpeakerMetadata(normalized)
            ? (string.Empty, normalized)
            : (normalized, null);
    }

    private static bool LooksLikeSpeakerMetadata(string value)
    {
        if (value.Length is < 1 or > 48 || !char.IsUpper(value[0]))
        {
            return false;
        }

        int words = 0;
        bool insideWord = false;
        foreach (char character in value)
        {
            bool wordCharacter = char.IsLetter(character) ||
                                 char.GetUnicodeCategory(character) ==
                                 System.Globalization.UnicodeCategory.NonSpacingMark ||
                                 character is '-' or '\'' or '\u2019';
            if (wordCharacter)
            {
                if (!insideWord)
                {
                    words++;
                    insideWord = true;
                }

                continue;
            }

            if (!char.IsWhiteSpace(character))
            {
                return false;
            }

            insideWord = false;
        }

        return words is >= 1 and <= 3;
    }

    internal static string BuildCompletionRequestJson(string phrase, bool cachePrompt = true)
        => BuildCompletionRequestJsonFromPrompt(BuildRawPrompt(phrase), cachePrompt);

    internal static string BuildPostEditRequestJson(
        SmartPhraseTranslationRequest request,
        string draft) =>
        BuildCompletionRequestJsonFromPrompt(BuildPostEditRawPrompt(request, draft), cachePrompt: true);

    private static string BuildCompletionRequestJsonFromPrompt(string prompt, bool cachePrompt)
    {
        var payload = new
        {
            prompt,
            n_predict = MaximumGeneratedTokens,
            temperature = 0.0,
            top_k = 1,
            top_p = 1.0,
            min_p = 0.0,
            seed = 0,
            stream = false,
            cache_prompt = cachePrompt,
            stop = new[] { "<end_of_turn>" }
        };
        return JsonSerializer.Serialize(payload);
    }

    internal static bool ShouldPostEdit(
        SmartPhraseTranslationRequest request,
        SmartPhraseGuardResult directGuard)
    {
        if (!directGuard.Accepted ||
            request.PreviousContext.Count > 0 ||
            request.CurrentSpeaker?.Gender is SpeakerGender.Masculine or SpeakerGender.Feminine ||
            request.SemanticTerms.Count > 0 ||
            request.SenseHints.Count > 0)
        {
            return true;
        }

        string[] tokens = EnglishContextLexicon.TokenRegex()
            .Matches(request.ModelInputPhrase)
            .Select(match => EnglishContextLexicon.NormalizeToken(match.Value))
            .Where(token => token.Length > 0)
            .ToArray();
        if (tokens.Length >= 8)
        {
            // Longer sentences are where the 4B direct pass most often drops
            // a clause or swaps semantic roles. One APE pass is cheaper than
            // showing a fluent but incomplete second line.
            return true;
        }

        HashSet<string> structuralSignals = new(StringComparer.Ordinal)
        {
            "not", "no", "never", "cannot", "can't", "must", "should",
            "would", "could", "have", "has", "had"
        };
        if (tokens.Any(structuralSignals.Contains))
        {
            return true;
        }

        HashSet<string> possessiveRoles = tokens
            .Where(token => token is "my" or "your" or "our" or "their" or "his" or "her")
            .ToHashSet(StringComparer.Ordinal);
        return possessiveRoles.Count >= 2;
    }

    private async Task<string> SendCompletionAsync(
        Uri endpoint,
        string apiKey,
        string prompt,
        CancellationToken cancellationToken)
    {
        string requestJson = BuildCompletionRequestJsonFromPrompt(prompt, _cachePrompt);
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri(endpoint, "completion"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"TranslateGemma returned HTTP {(int)response.StatusCode}: " +
                    LimitDiagnostic(responseJson));
            }

            return ParseCompletionResponse(responseJson);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                "The local TranslateGemma server is unavailable. " + ProcessFailureSuffix(),
                exception);
        }
    }

    internal static string ParseCompletionResponse(string responseJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(
                    "stopped_limit",
                    out JsonElement stoppedLimitElement) &&
                stoppedLimitElement.ValueKind == JsonValueKind.True)
            {
                throw new InvalidDataException(
                    "TranslateGemma остановила генерацию по лимиту токенов.");
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("content", out JsonElement contentElement) ||
                contentElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "Ответ TranslateGemma не содержит строковое поле content.");
            }

            string content = contentElement.GetString() ?? string.Empty;
            int endOfTurn = content.IndexOf("<end_of_turn>", StringComparison.Ordinal);
            if (endOfTurn >= 0)
            {
                content = content[..endOfTurn];
            }

            if (content.Contains("<start_of_turn>", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "TranslateGemma вернула служебный маркер вместо чистого перевода.");
            }

            content = content.Trim();
            if (content.Length == 0)
            {
                throw new InvalidDataException("TranslateGemma вернула пустой перевод.");
            }

            return content;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("TranslateGemma вернула повреждённый JSON.", exception);
        }
    }

    internal static bool IsDisplaySemanticImprovement(
        SmartPhraseTranslationRequest request,
        string direct,
        string corrected)
    {
        (int DirectTerms, int DirectSenses) = SemanticViolationCounts(request, direct);
        (int CorrectedTerms, int CorrectedSenses) = SemanticViolationCounts(request, corrected);
        return CorrectedTerms < DirectTerms ||
               CorrectedTerms == DirectTerms && CorrectedSenses < DirectSenses;
    }

    internal static string? SelectPostEditFailureFallback(
        SmartPhraseGuardResult directStrict,
        SmartPhraseGuardResult directDisplay) =>
        directDisplay.Accepted
            ? directDisplay.Translation
            : directStrict.Accepted
                ? directStrict.Translation
                : null;

    private static (int Terms, int Senses) SemanticViolationCounts(
        SmartPhraseTranslationRequest request,
        string translation) =>
        (
            SenseFidelityGuard.FindMissingTerms(translation, request.SemanticTerms).Count,
            NeuralSenseFidelityGuard.FindConflictingHints(
                translation,
                request.SenseHints,
                request.SourcePhrase).Count
        );

    internal static async Task ValidatePinnedArtifactAsync(
        string path,
        long? expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (expectedSha256.Length != 64 ||
            expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 должен содержать 64 шестнадцатеричных символа.",
                nameof(expectedSha256));
        }

        FileInfo file = new(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Не найден закреплённый артефакт TranslateGemma.", path);
        }

        if (expectedSize is not null && file.Length != expectedSize.Value)
        {
            throw new InvalidDataException(
                $"Неверный размер артефакта {file.Name}: {file.Length}, ожидалось {expectedSize.Value}.");
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        string actual = Convert.ToHexStringLower(hash);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Неверный SHA-256 артефакта {file.Name}: {actual}.");
        }
    }

    internal static string FindModelPath(
        string modelRoot,
        string ollamaModelsRoot,
        TranslateGemmaModelPin? modelPin = null)
    {
        TranslateGemmaModelPin pin = modelPin ?? TranslateGemmaModelPin.Default;
        string local = Path.Combine(modelRoot, pin.FileName);
        if (File.Exists(local))
        {
            return local;
        }

        string ollamaBlob = Path.Combine(ollamaModelsRoot, "blobs", pin.BlobFileName);
        if (File.Exists(ollamaBlob))
        {
            return ollamaBlob;
        }

        throw new FileNotFoundException(
            $"Не найдена TranslateGemma 4B ({pin.FileName}). " +
            "Ожидался локальный GGUF либо закреплённый blob Ollama.",
            local + " | " + ollamaBlob);
    }

    internal static ProcessStartInfo CreateServerStartInfo(
        string executable,
        string workingDirectory,
        string modelPath,
        int port,
        string apiKey,
        TranslateGemmaExecutionBackend backend = TranslateGemmaExecutionBackend.Cpu,
        VulkanCoopmat vulkanCoopmat = ShippedVulkanCoopmat)
    {
        if (!Enum.IsDefined(vulkanCoopmat))
        {
            throw new ArgumentOutOfRangeException(nameof(vulkanCoopmat));
        }

        ProcessStartInfo startInfo = new(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(IPAddress.Loopback.ToString());
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--ctx-size");
        startInfo.ArgumentList.Add(ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        switch (backend)
        {
            case TranslateGemmaExecutionBackend.Cpu:
                startInfo.ArgumentList.Add("--n-gpu-layers");
                startInfo.ArgumentList.Add("0");
                break;
            case TranslateGemmaExecutionBackend.Vulkan:
                // Selecting a named non-CPU device makes a missing Vulkan
                // backend a parser error. Disabling auto-fit prevents llama.cpp
                // from silently reducing the requested full offload.
                startInfo.ArgumentList.Add("--device");
                startInfo.ArgumentList.Add("Vulkan0");
                startInfo.ArgumentList.Add("--n-gpu-layers");
                startInfo.ArgumentList.Add("all");
                startInfo.ArgumentList.Add("--fit");
                startInfo.ArgumentList.Add("off");
                startInfo.ArgumentList.Add("-lv");
                startInfo.ArgumentList.Add("4");
                // ggml-vulkan disables KHR_coopmat when the variable exists
                // with any value. It is set or removed explicitly so that an
                // inherited environment can never move the numeric path away
                // from the identity this runtime reports. The startup log
                // cannot confirm the choice: b9632 registers its backends
                // before -lv applies, so the device-capability line is never
                // printed and both paths log identically. This launch
                // environment is therefore the single source of truth.
                if (vulkanCoopmat == VulkanCoopmat.Disabled)
                {
                    startInfo.Environment[DisableCoopmatEnvironmentVariable] = "1";
                }
                else
                {
                    startInfo.Environment.Remove(DisableCoopmatEnvironmentVariable);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
        }

        startInfo.ArgumentList.Add("--parallel");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--api-key");
        startInfo.ArgumentList.Add(apiKey);
        // The official TranslateGemma multimodal Jinja template cannot be
        // auto-parsed by llama-server. This service uses the tested raw
        // /completion prompt, so template parsing must stay disabled.
        startInfo.ArgumentList.Add("--no-jinja");
        startInfo.ArgumentList.Add("--no-webui");
        return startInfo;
    }

    internal static string FindRunnerArchivePath(
        string modelRoot,
        TranslateGemmaExecutionBackend backend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRoot);
        string fileName = backend switch
        {
            TranslateGemmaExecutionBackend.Cpu => RunnerArchiveFileName,
            TranslateGemmaExecutionBackend.Vulkan => VulkanRunnerArchiveFileName,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
        };
        string path = Path.Combine(Path.GetFullPath(modelRoot), fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Не найден закреплённый runner TranslateGemma для {backend}.",
                path);
        }

        return path;
    }

    private async Task<RuntimeArtifacts> PrepareArtifactsAsync(
        TranslateGemmaExecutionBackend backend,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_modelRoot);
        string archivePath = FindRunnerArchivePath(_modelRoot, backend);
        (long archiveSize, string archiveSha256) = backend switch
        {
            TranslateGemmaExecutionBackend.Cpu =>
                (RunnerArchiveSize, RunnerArchiveSha256),
            TranslateGemmaExecutionBackend.Vulkan =>
                (VulkanRunnerArchiveSize, VulkanRunnerArchiveSha256),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
        };
        await ValidatePinnedArtifactAsync(
            archivePath,
            archiveSize,
            archiveSha256,
            cancellationToken);

        string modelPath = FindModelPath(_modelRoot, _ollamaModelsRoot, _modelPin);
        await ValidatePinnedArtifactAsync(
            modelPath,
            _modelPin.Bytes,
            _modelPin.Sha256,
            cancellationToken);

        string runtimeRoot = Path.Combine(_modelRoot, "Runtime");
        Directory.CreateDirectory(runtimeRoot);
        string runtimeDirectory = Path.Combine(
            runtimeRoot,
            RunnerBuild + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDirectory);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, runtimeDirectory, overwriteFiles: false);
            string[] serverExecutables = Directory.GetFiles(
                runtimeDirectory,
                "llama-server.exe",
                SearchOption.AllDirectories);
            if (serverExecutables.Length != 1)
            {
                throw new InvalidDataException(
                    $"Архив {Path.GetFileName(archivePath)} должен содержать ровно один llama-server.exe.");
            }

            return new RuntimeArtifacts(
                serverExecutables[0],
                Path.GetDirectoryName(serverExecutables[0])!,
                modelPath,
                runtimeDirectory,
                backend);
        }
        catch (Exception preparationFailure)
        {
            try
            {
                await DeleteRuntimeDirectoryWithRetryAsync(runtimeDirectory, runtimeRoot);
            }
            catch (Exception cleanupFailure)
            {
                // Keep the exact owned path retryable by UnloadAsync; losing it
                // here caused every failed extraction to become permanent junk.
                _runtimeDirectory = runtimeDirectory;
                throw new AggregateException(
                    "Не удалось подготовить и очистить Runtime TranslateGemma.",
                    preparationFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(preparationFailure).Throw();
            throw;
        }
    }

    private void StartServer(RuntimeArtifacts artifacts)
    {
        int port = ReserveLoopbackPort();
        string apiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Process process = new()
        {
            StartInfo = CreateServerStartInfo(
                artifacts.ServerExecutable,
                artifacts.WorkingDirectory,
                artifacts.ModelPath,
                port,
                apiKey,
                artifacts.Backend,
                _vulkanCoopmat),
            EnableRaisingEvents = true
        };

        _processLog.Clear();
        VulkanStartupEvidenceTracker? startupEvidence =
            artifacts.Backend == TranslateGemmaExecutionBackend.Vulkan
                ? new VulkanStartupEvidenceTracker()
                : null;
        _startupEvidence = startupEvidence;
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Не удалось запустить llama-server для TranslateGemma.");
        }

        _server = process;
        _apiKey = apiKey;
        _endpoint = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        _stdoutDrain = DrainAsync(process.StandardOutput, "stdout", startupEvidence);
        _stderrDrain = DrainAsync(process.StandardError, "stderr", startupEvidence);
    }

    private async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        Uri endpoint = _endpoint ?? throw new InvalidOperationException("Адрес llama-server не задан.");
        string apiKey = _apiKey ?? throw new InvalidOperationException("Ключ llama-server не задан.");
        Uri healthUri = new(endpoint, "health");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process server = _server ??
                throw new InvalidOperationException("Процесс TranslateGemma не запущен.");
            if (server.HasExited)
            {
                throw new InvalidOperationException(
                    $"llama-server завершился с кодом {server.ExitCode}. " + ProcessFailureSuffix());
            }

            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, healthUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The listening socket appears after GGUF validation and load.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    private async Task WaitForStartupEvidenceAsync(
        TranslateGemmaExecutionBackend backend,
        CancellationToken cancellationToken)
    {
        if (backend == TranslateGemmaExecutionBackend.Cpu)
        {
            return;
        }

        VulkanStartupEvidenceTracker evidence = _startupEvidence ??
            throw new InvalidOperationException(
                "Не создан трекер доказательств Vulkan TranslateGemma.");
        long started = Stopwatch.GetTimestamp();
        while (!evidence.IsComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process server = _server ??
                throw new InvalidOperationException("Процесс TranslateGemma не запущен.");
            if (server.HasExited)
            {
                throw new InvalidOperationException(
                    $"llama-server завершился с кодом {server.ExitCode}. " + ProcessFailureSuffix());
            }

            if (Stopwatch.GetElapsedTime(started) >= StartupEvidenceTimeout)
            {
                throw new InvalidOperationException(
                    "Vulkan TranslateGemma достигла health endpoint без обязательного " +
                    $"доказательства backend: {evidence.DescribeMissing()}. " +
                    ProcessFailureSuffix());
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private async Task StopAndCleanupOwnedRuntimeAsync()
    {
        IReadOnlyList<Exception> failures = await CollectCleanupErrorsAsync(
            StopOwnedServerAsync,
            CleanupRuntimeDirectoriesAsync);
        ThrowCleanupFailures(
            failures,
            "Не удалось полностью остановить и очистить Runtime TranslateGemma.");
    }

    private async Task ThrowStartupFailureAfterCleanupAsync(Exception primaryFailure)
    {
        IReadOnlyList<Exception> cleanupFailures = await CollectCleanupErrorsAsync(
            StopOwnedServerAsync,
            CleanupRuntimeDirectoriesAsync);
        Exception failure = CombinePrimaryAndCleanupFailures(
            primaryFailure,
            cleanupFailures);
        ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static async Task<IReadOnlyList<Exception>> CollectCleanupErrorsAsync(
        params Func<Task>[] cleanupSteps)
    {
        ArgumentNullException.ThrowIfNull(cleanupSteps);
        List<Exception> failures = [];
        foreach (Func<Task> cleanupStep in cleanupSteps)
        {
            ArgumentNullException.ThrowIfNull(cleanupStep);
            try
            {
                await cleanupStep();
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        return failures;
    }

    internal static Exception CombinePrimaryAndCleanupFailures(
        Exception primaryFailure,
        IReadOnlyList<Exception> cleanupFailures)
    {
        ArgumentNullException.ThrowIfNull(primaryFailure);
        ArgumentNullException.ThrowIfNull(cleanupFailures);
        if (cleanupFailures.Count == 0)
        {
            return primaryFailure;
        }

        List<Exception> failures = new(cleanupFailures.Count + 1)
        {
            primaryFailure
        };
        failures.AddRange(cleanupFailures);
        return new AggregateException(
            "TranslateGemma failed; cleanup errors follow the primary failure.",
            failures);
    }

    private static void ThrowCleanupFailures(
        IReadOnlyList<Exception> failures,
        string aggregateMessage)
    {
        if (failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
            return;
        }

        throw new AggregateException(aggregateMessage, failures);
    }

    private async Task StopOwnedServerAsync()
    {
        Process? process = _server;
        Task? stdoutDrain = _stdoutDrain;
        Task? stderrDrain = _stderrDrain;
        _endpoint = null;
        _apiKey = null;
        ResetActiveBackend();

        if (process is not null)
        {
            bool exited = false;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                using CancellationTokenSource timeout = new(ProcessExitTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    exited = true;
                }
                catch (OperationCanceledException timeoutFailure)
                    when (timeout.IsCancellationRequested)
                {
                    if (HasExited(process))
                    {
                        exited = true;
                    }
                    else
                    {
                        throw new TimeoutException(
                            $"llama-server TranslateGemma не завершился за {ProcessExitTimeout.TotalSeconds:0} с.",
                            timeoutFailure);
                    }
                }
            }
            catch (Exception) when (HasExited(process))
            {
                exited = true;
            }
            finally
            {
                if (exited)
                {
                    _server = null;
                    _stdoutDrain = null;
                    _stderrDrain = null;
                    process.Dispose();
                }
            }

            if (!exited)
            {
                throw new TimeoutException(
                    $"llama-server TranslateGemma не завершился за {ProcessExitTimeout.TotalSeconds:0} с.");
            }
        }
        else
        {
            _stdoutDrain = null;
            _stderrDrain = null;
        }

        if (stdoutDrain is not null || stderrDrain is not null)
        {
            try
            {
                Task drains = Task.WhenAll(
                    stdoutDrain ?? Task.CompletedTask,
                    stderrDrain ?? Task.CompletedTask);
                await drains.WaitAsync(ProcessExitTimeout);
            }
            catch
            {
                // Closed process pipes are expected during a forced unload.
            }
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private async Task CleanupRuntimeDirectoriesAsync()
    {
        string runtimeRoot = Path.Combine(_modelRoot, "Runtime");
        IReadOnlyList<Exception> failures = await CollectCleanupErrorsAsync(
            DeleteOwnedRuntimeDirectoryAsync,
            () => DeleteStaleRuntimeDirectoriesAsync(
                runtimeRoot,
                activeDirectory: _runtimeDirectory));
        ThrowCleanupFailures(
            failures,
            "Не удалось полностью очистить Runtime TranslateGemma.");
    }

    private async Task DeleteOwnedRuntimeDirectoryAsync()
    {
        string? runtimeDirectory = _runtimeDirectory;
        if (runtimeDirectory is null)
        {
            return;
        }

        string runtimeRoot = Path.Combine(_modelRoot, "Runtime");
        await DeleteRuntimeDirectoryWithRetryAsync(runtimeDirectory, runtimeRoot);
        if (string.Equals(
                _runtimeDirectory,
                runtimeDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            _runtimeDirectory = null;
        }
    }

    internal static async Task<bool> DeleteRuntimeDirectoryWithRetryAsync(
        string directory,
        string runtimeRoot,
        int maximumAttempts = RuntimeDeleteMaximumAttempts,
        TimeSpan? initialRetryDelay = null,
        Action<string>? deleteDirectory = null,
        Func<TimeSpan, Task>? delayAsync = null,
        Action<int, Exception>? retryObserver = null,
        Func<string, string, string>? validateDirectory = null)
    {
        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        TimeSpan firstDelay = initialRetryDelay ?? RuntimeDeleteInitialDelay;
        if (firstDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialRetryDelay));
        }

        Action<string> delete = deleteDirectory ??
            (static path => Directory.Delete(path, recursive: true));
        Func<TimeSpan, Task> wait = delayAsync ?? Task.Delay;
        Func<string, string, string> validate =
            validateDirectory ?? ValidateOwnedRuntimeDirectory;

        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            // Re-check immediately before every destructive attempt. A failed
            // delete must not leave a window in which the owned directory can
            // be replaced by a junction and then trusted on retry.
            string fullDirectory = validate(directory, runtimeRoot);
            if (!Directory.Exists(fullDirectory))
            {
                return false;
            }

            try
            {
                delete(fullDirectory);
                if (!Directory.Exists(fullDirectory))
                {
                    return true;
                }

                throw new IOException(
                    "Каталог Runtime TranslateGemma остался после операции удаления.");
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (!Directory.Exists(fullDirectory))
                {
                    return false;
                }

                if (attempt == maximumAttempts)
                {
                    throw;
                }

                retryObserver?.Invoke(attempt, exception);
                double milliseconds = Math.Min(
                    1_000,
                    firstDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await wait(TimeSpan.FromMilliseconds(milliseconds));
            }
        }

        return false;
    }

    internal static async Task<int> DeleteStaleRuntimeDirectoriesAsync(
        string runtimeRoot,
        string? activeDirectory = null,
        TimeSpan? minimumAge = null,
        int maximumAttempts = 2,
        Action<string>? deleteDirectory = null,
        Func<TimeSpan, Task>? delayAsync = null)
    {
        string fullRoot = Path.GetFullPath(runtimeRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(fullRoot))
        {
            return 0;
        }

        TimeSpan age = minimumAge ?? StaleRuntimeMinimumAge;
        if (age < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumAge));
        }

        string? fullActive = string.IsNullOrWhiteSpace(activeDirectory)
            ? null
            : Path.GetFullPath(activeDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        DateTime cutoff = DateTime.UtcNow - age;
        int deleted = 0;
        foreach (string candidate in Directory.EnumerateDirectories(
                     fullRoot,
                     RunnerBuild + "-*",
                     SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(candidate);
            if (!IsOwnedRuntimeDirectoryName(name) ||
                string.Equals(candidate, fullActive, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                FileAttributes attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    Directory.GetLastWriteTimeUtc(candidate) > cutoff)
                {
                    continue;
                }

                bool removed = await DeleteRuntimeDirectoryWithRetryAsync(
                    candidate,
                    fullRoot,
                    maximumAttempts,
                    RuntimeDeleteInitialDelay,
                    deleteDirectory,
                    delayAsync);
                if (removed)
                {
                    deleted++;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A still-active directory stays discoverable for the next
                // unload; one locked legacy runner must not block the others.
            }
        }

        return deleted;
    }

    private static string ValidateOwnedRuntimeDirectory(
        string directory,
        string runtimeRoot)
    {
        string fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(runtimeRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(fullDirectory)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!string.Equals(parent, fullRoot, StringComparison.OrdinalIgnoreCase) ||
            !IsOwnedRuntimeDirectoryName(Path.GetFileName(fullDirectory)))
        {
            throw new InvalidOperationException(
                "Отказано в удалении каталога вне Runtime TranslateGemma.");
        }

        if (Directory.Exists(fullDirectory) &&
            (File.GetAttributes(fullDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Отказано в удалении ссылки из Runtime TranslateGemma.");
        }

        return fullDirectory;
    }

    private static bool IsOwnedRuntimeDirectoryName(string name)
    {
        string prefix = RunnerBuild + "-";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }

    private async Task DrainAsync(
        StreamReader reader,
        string streamName,
        VulkanStartupEvidenceTracker? startupEvidence)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                _processLog.Append(streamName, line);
                startupEvidence?.Observe(line);
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposing an owned process closes both redirected pipes.
        }
        catch (IOException)
        {
            // A killed process can tear down a pipe between read operations.
        }
    }

    private string ProcessFailureSuffix()
    {
        string tail = _processLog.ToString();
        return tail.Length == 0 ? "Журнал llama-server пуст." : "Журнал: " + tail;
    }

    private static string NormalizeSource(string phrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phrase);
        string normalized = phrase.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Contains("<start_of_turn>", StringComparison.Ordinal) ||
            normalized.Contains("<end_of_turn>", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Исходный текст содержит служебный маркер Gemma.",
                nameof(phrase));
        }

        if (normalized.Length > MaximumSourceCharacters ||
            EnglishContextLexicon.TokenRegex().Matches(normalized).Count >
            SmartPhraseContextBuilder.MaximumWords)
        {
            throw new ArgumentException(
                "TranslateGemma принимает только короткую целевую фразу умного словаря.",
                nameof(phrase));
        }

        return normalized;
    }

    private static int ReserveLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    // An unresolved user root throws here, as an empty LocalAppData always did.
    private static string ResolveDefaultModelRoot() =>
        CoreDataLayout.Current.PhraseModelRoot;

    private static string ResolveDefaultOllamaModelsRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new InvalidOperationException("Windows не сообщил каталог профиля пользователя.");
        }

        return Path.Combine(profile, ".ollama", "models");
    }

    private static string LimitDiagnostic(string value)
    {
        const int limit = 600;
        string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= limit ? normalized : normalized[..limit] + "…";
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record RuntimeArtifacts(
        string ServerExecutable,
        string WorkingDirectory,
        string ModelPath,
        string RuntimeDirectory,
        TranslateGemmaExecutionBackend Backend);

    internal sealed class VulkanStartupEvidenceTracker
    {
        private const int DeviceMask = 1;
        private const int SelectedDeviceMask = 2;
        private const int FullOffloadMask = 4;
        private const int CompleteMask = DeviceMask | SelectedDeviceMask | FullOffloadMask;
        private int _observed;

        public bool IsComplete =>
            (Volatile.Read(ref _observed) & CompleteMask) == CompleteMask;

        public void Observe(string line)
        {
            ArgumentNullException.ThrowIfNull(line);
            int observed = 0;
            if (line.Contains("Vulkan0", StringComparison.Ordinal))
            {
                observed |= DeviceMask;
            }

            if (line.Contains("using device Vulkan0", StringComparison.OrdinalIgnoreCase))
            {
                observed |= SelectedDeviceMask;
            }

            if (line.Contains("offloaded 35/35 layers to GPU", StringComparison.Ordinal))
            {
                observed |= FullOffloadMask;
            }

            if (observed != 0)
            {
                Interlocked.Or(ref _observed, observed);
            }
        }

        public string DescribeMissing()
        {
            int observed = Volatile.Read(ref _observed);
            List<string> missing = [];
            if ((observed & DeviceMask) == 0)
            {
                missing.Add("device Vulkan0");
            }

            if ((observed & SelectedDeviceMask) == 0)
            {
                missing.Add("using device Vulkan0");
            }

            if ((observed & FullOffloadMask) == 0)
            {
                missing.Add("offloaded 35/35 layers to GPU");
            }

            return string.Join(", ", missing);
        }
    }

    private sealed class ProcessLogTail
    {
        private const int MaximumCharacters = 4_096;
        private readonly object _sync = new();
        private readonly StringBuilder _builder = new();

        public void Append(string streamName, string line)
        {
            lock (_sync)
            {
                _builder.Append('[').Append(streamName).Append("] ").AppendLine(line);
                if (_builder.Length > MaximumCharacters)
                {
                    _builder.Remove(0, _builder.Length - MaximumCharacters);
                }
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _builder.Clear();
            }
        }

        public override string ToString()
        {
            lock (_sync)
            {
                return _builder.ToString().Trim();
            }
        }
    }
}
