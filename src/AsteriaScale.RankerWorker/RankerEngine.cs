using System.Security.Cryptography;
using System.Text.Json;
using AsteriaScale.RankerProtocol;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Windows.AI.MachineLearning;

namespace AsteriaScale.RankerWorker;

internal sealed class RankerEngine : IDisposable
{
    private const string DefaultModelVersion =
        "GlossBERT:0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a";
    private const int MaximumCandidates = 128;
    private const int CpuBatchSize = 32;
    private const int CpuMaximumSequenceLength = 128;
    private const int NpuBatchSize = 4;
    private const int NpuSequenceLength = 64;
    private const double AbstentionMargin = 0.25;
    private const string ModelBundleDirectoryName = "GlossBert";
    private const string CpuModelFileName = "glossbert-fp32-dynamic.onnx";
    private const string NpuModelFileName = "glossbert-eager-vitis-context.onnx";
    private const string NpuContextBinaryFileName =
        "glossbert-eager-vitis-context.onnx_VITISAI.bin";
    private const string NpuCompatibilityFileName =
        "glossbert-eager-vitis-context.onnx.compatibility.txt";
    private const long CpuModelLength = 438_167_088;
    private const string CpuModelSha256 =
        "daa4098d59b9077af59e22f846818455ca39593567326338f88e328ebeef18b5";
    private const long TokenizerLength = 465_642;
    private const string TokenizerSha256 =
        "04fd39dff4bfa1b7ae42425c484ef0173f8da60b485f66642d2b44b251c6a014";
    private const long NpuModelLength = 93_776_750;
    private const string NpuModelSha256 =
        "152bd5b4c54d4a339a4dbb642489b950af6c712bc7736f2b7ceb977a05573320";
    private const long NpuContextBinaryLength = 123_964_416;
    private const string NpuContextBinarySha256 =
        "94095699d6b7b59ab6edc92c7471bf507b861bd14b4d57cb3ec75f49f3f5d672";
    private const long NpuCompatibilityLength = 235;
    private const string NpuCompatibilitySha256 =
        "a15f82d6aed5de34eb6bd75b727e4ecbc2476fbf14c91b04e37a7f1c1ff0f777";
    private const long EmbeddingIndexLength = 91_577_685;
    private const string EmbeddingIndexSha256 =
        "13c0fb6db773d85f94e2c930e2c49c8bc022bd2003550413bcf1f963cc55c2d3";
    private const long EmbeddingModelLength = 90_405_214;
    private const string EmbeddingModelSha256 =
        "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452";
    private const long EmbeddingTokenizerLength = 466_247;
    private const string EmbeddingTokenizerSha256 =
        "be50c3628f2bf5bb5e3a7f17b1f74611b2561a3a27eeab05e5aa30f411572037";
    private const string EmbeddingModelId =
        "sentence-transformers/all-MiniLM-L6-v2";
    private const string EmbeddingRevision =
        "c9745ed1d9f207416be6d2e6f8de32d1f16199bf";
    private const string EmbeddingSenseFileSha256 =
        "A15C4037F37137DD54CD5B3E451A6D4E1B3E847A7D94266CD40A7B3F970E85FF";
    private const int EmbeddingDimensions = 384;
    private const int EmbeddingSequenceLength = 64;
    private const int EmbeddingKeyCount = 147_426;
    private const int EmbeddingVectorCount = 225_217;
    private const string EmbeddingBundleCacheTag =
        "bi-c9745ed1-13c0fb6d-6fd5d72f-be50c362";
    private static readonly HashSet<string> AllowedCpuProfileNodes =
    [
        "/model/bert/embeddings/word_embeddings/Gather_kernel_time|Gather",
        "/model/bert/embeddings/token_type_embeddings/Gather_kernel_time|Gather",
        "/model/bert/Cast_kernel_time|Cast",
        "/model/bert/Flatten_kernel_time|Flatten",
        "/model/bert/Gather_1_kernel_time|Gather"
    ];

    // Engage only on truly large inventories and keep half of them: probe
    // measurements showed that k=8 can evict the correct sense of
    // hyper-polysemous words ("set"), inflating the margin past the
    // abstention threshold on a wrong answer, while pruning 13-16 groups by
    // one or two only pays the encoder cost without a latency win.
    private const string EmbeddingBundleDirectoryName = "GlossEmbeddings";

    private readonly WorkerOptions _options;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private InferenceSession? _session;
    private PairTokenizer? _tokenizer;
    private GlossEmbeddingIndex? _embeddingIndex;
    private ContextEncoder? _contextEncoder;
    private bool _embeddingLoadAttempted;
    private RankerException? _embeddingFailure;
    private RankerException? _initializationFailure;
    private bool _warmupCompleted;
    private bool _npuExecutionVerified;
    private string _crossModelVersion;

    public RankerEngine(WorkerOptions options)
    {
        _options = options;
        Provider = options.Provider == RankerProvider.Npu
            ? "VitisAIExecutionProvider"
            : "CPUExecutionProvider";
        Device = options.Provider == RankerProvider.Npu ? "NPU" : "CPU";
        _crossModelVersion = DefaultModelVersion;
        ModelVersion = DefaultModelVersion;
    }

    public string Provider { get; private set; }

    public string Device { get; private set; }

    public string ModelVersion { get; private set; }

    public async Task WarmAsync()
    {
        await EnsureLoadedAsync();
        if (_options.Provider == RankerProvider.Cpu)
        {
            EnsureEmbeddingPipelineLoaded();
        }

        if (_warmupCompleted)
        {
            return;
        }

        _ = Score(
            "The \"warmup\" pass prepares the dictionary ranker.",
            "warmup",
            ["a preparatory operation"]);
        if (_options.Provider == RankerProvider.Npu && !_npuExecutionVerified)
        {
            try
            {
                VerifyNpuExecutionProfile();
                _npuExecutionVerified = true;
            }
            catch (RankerException ex)
            {
                _initializationFailure = ex;
                _session?.Dispose();
                _session = null;
                _tokenizer?.Dispose();
                _tokenizer = null;
                throw;
            }
        }

        _warmupCompleted = true;
        Console.Error.WriteLine($"[ranker] warm on {Provider} / {Device}");
    }

    public async Task<RankerSelection> SelectAsync(RankerRequest request)
    {
        ValidateSelection(request);
        await WarmAsync();

        var markedContext = BuildMarkedContext(
            request.Context!,
            request.Target!,
            request.CharacterIndex);
        var (candidates, candidateGroupIds, pipeline) =
            ApplyBiEncoderPrefilter(request, markedContext);
        var scores = Score(markedContext, request.Target!, candidates);
        var groupedScores = GroupScores(scores, candidateGroupIds);
        var best = groupedScores[0];
        var margin = groupedScores.Count == 1
            ? 1.0
            : best.Score - groupedScores[1].Score;
        // Pair scores are independent and computed in fixed-size chunks, so a
        // large sense inventory cannot change individual scores. The margin
        // threshold is therefore the sole abstention criterion; large-inventory
        // dev-set measurements are recorded in assets/Ranker/README.md.
        var abstained = groupedScores.Count > 1 && margin < AbstentionMargin;
        return new RankerSelection(
            abstained ? null : best.GroupId,
            abstained,
            margin,
            best.Score,
            best.Score,
            pipeline);
    }

    /// <summary>
    /// MiniLM prefilter for large sense inventories (roadmap task #7): rank
    /// GROUPS by their best gloss/context cosine similarity and keep the top 12
    /// groups with all their glosses, so GroupIds and group-margin semantics
    /// survive unchanged. Fail-open everywhere: a missing index, an unknown
    /// key or an unmatched gloss hash keeps the full candidate set.
    /// </summary>
    private (IReadOnlyList<string> Candidates, IReadOnlyList<int> GroupIds, string Pipeline)
        ApplyBiEncoderPrefilter(RankerRequest request, string markedContext)
    {
        var candidates = request.Candidates!;
        var groupIds = request.CandidateGroupIds!;
        var fallback = (candidates, groupIds, "cross");
        try
        {
            int distinctGroupCount = groupIds.Distinct().Count();
            if (!RankerSelectionLogic.ShouldApplyBiEncoder(
                    _options.Provider == RankerProvider.Cpu,
                    distinctGroupCount))
            {
                return fallback;
            }

            EnsureEmbeddingPipelineLoaded();
            if (_embeddingIndex is null || _contextEncoder is null)
            {
                return fallback;
            }

            var key = request.Target!.Trim().ToLowerInvariant();
            if (!_embeddingIndex.TryReadVectors(key, out var vectors))
            {
                return fallback;
            }

            var byHash = vectors.ToLookup(vector => vector.GlossHash);
            var matchedVectors = new GlossVector[candidates.Count][];
            for (var index = 0; index < candidates.Count; index++)
            {
                var hash = GlossEmbeddingIndex.HashNormalizedGloss(candidates[index]);
                var matches = byHash[hash].ToArray();
                if (matches.Length == 0)
                {
                    return fallback;
                }

                matchedVectors[index] = matches;
            }

            var query = _contextEncoder.Encode(markedContext);
            var similarities = matchedVectors
                .Select(matches => (double?)matches.Max(vector =>
                    GlossEmbeddingIndex.Similarity(query, vector)))
                .ToArray();
            HashSet<int>? keptGroups = RankerSelectionLogic.SelectPrefilterGroups(
                groupIds,
                similarities);
            if (keptGroups is null)
            {
                return fallback;
            }

            var filteredCandidates = new List<string>(candidates.Count);
            var filteredGroupIds = new List<int>(groupIds.Count);
            for (var index = 0; index < candidates.Count; index++)
            {
                if (keptGroups.Contains(groupIds[index]))
                {
                    filteredCandidates.Add(candidates[index]);
                    filteredGroupIds.Add(groupIds[index]);
                }
            }

            return (filteredCandidates, filteredGroupIds, "bi+cross");
        }
        catch (Exception ex) when (ex is not RankerException)
        {
            Console.Error.WriteLine($"[ranker] bi-encoder prefilter disabled: {ex.Message}");
            _embeddingIndex?.Dispose();
            _contextEncoder?.Dispose();
            _embeddingIndex = null;
            _contextEncoder = null;
            RefreshModelVersion();
            return fallback;
        }
    }

    private void EnsureEmbeddingPipelineLoaded()
    {
        if (_embeddingFailure is { } failure)
        {
            throw new RankerException(failure.ErrorCode, failure.Message, failure);
        }

        if (_embeddingLoadAttempted)
        {
            return;
        }

        _embeddingLoadAttempted = true;
        var directory = Path.Combine(
            _options.ModelDirectory,
            EmbeddingBundleDirectoryName);
        var indexPath = Path.Combine(directory, "eng-senses.vec");
        var modelPath = Path.Combine(directory, "minilm-l6-v2.onnx");
        var tokenizerPath = Path.Combine(directory, "tokenizer.json");
        string[] paths = [indexPath, modelPath, tokenizerPath];
        int presentCount = paths.Count(File.Exists);
        if (presentCount == 0)
        {
            RefreshModelVersion();
            return;
        }

        if (presentCount != paths.Length)
        {
            _embeddingFailure = new RankerException(
                "embedding_bundle_incomplete",
                "The optional GlossEmbeddings bundle is partially present. " +
                "Remove it or install all three pinned artifacts.");
            throw _embeddingFailure;
        }

        try
        {
            ValidateFileIntegrity(
                indexPath,
                EmbeddingIndexLength,
                EmbeddingIndexSha256,
                "embedding_bundle_integrity_failed",
                "gloss embedding index");
            ValidateFileIntegrity(
                modelPath,
                EmbeddingModelLength,
                EmbeddingModelSha256,
                "embedding_bundle_integrity_failed",
                "MiniLM context encoder");
            ValidateFileIntegrity(
                tokenizerPath,
                EmbeddingTokenizerLength,
                EmbeddingTokenizerSha256,
                "embedding_bundle_integrity_failed",
                "MiniLM tokenizer");
        }
        catch (RankerException ex)
        {
            _embeddingFailure = ex;
            throw;
        }

        GlossEmbeddingIndex? index = null;
        ContextEncoder? encoder = null;
        try
        {
            index = new GlossEmbeddingIndex(indexPath);
            ValidateEmbeddingMetadata(index);
            encoder = new ContextEncoder(
                modelPath,
                tokenizerPath,
                index.Dimensions);
            _embeddingIndex = index;
            index = null;
            _contextEncoder = encoder;
            encoder = null;
            RefreshModelVersion();
            Console.Error.WriteLine(
                $"[ranker] bi-encoder prefilter ready: {_embeddingIndex.KeyCount:N0} keys, " +
                $"{_embeddingIndex.ModelId}@{_embeddingIndex.Revision[..8]}");
        }
        catch (RankerException ex)
        {
            _embeddingFailure = ex;
            throw;
        }
        catch (Exception ex)
        {
            _embeddingFailure = new RankerException(
                "embedding_bundle_invalid",
                $"The pinned GlossEmbeddings bundle could not be loaded: {ex.Message}",
                ex);
            throw _embeddingFailure;
        }
        finally
        {
            index?.Dispose();
            encoder?.Dispose();
        }
    }

    private static void ValidateEmbeddingMetadata(GlossEmbeddingIndex index)
    {
        if (index.Format != 1 ||
            !string.Equals(index.ModelId, EmbeddingModelId, StringComparison.Ordinal) ||
            !string.Equals(index.Revision, EmbeddingRevision, StringComparison.Ordinal) ||
            index.Dimensions != EmbeddingDimensions ||
            !index.Normalized ||
            index.SequenceLength != EmbeddingSequenceLength ||
            !string.Equals(
                index.SenseFileSha256,
                EmbeddingSenseFileSha256,
                StringComparison.Ordinal) ||
            index.DeclaredKeyCount != EmbeddingKeyCount ||
            index.DeclaredVectorCount != EmbeddingVectorCount ||
            index.KeyCount != EmbeddingKeyCount)
        {
            throw new RankerException(
                "embedding_metadata_mismatch",
                "The GlossEmbeddings header does not match the pinned model, " +
                "revision, source dictionary or index dimensions.");
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _tokenizer?.Dispose();
        _embeddingIndex?.Dispose();
        _contextEncoder?.Dispose();
        _initializationLock.Dispose();
    }

    private async Task EnsureLoadedAsync()
    {
        if (_session is not null)
        {
            return;
        }

        ThrowCachedInitializationFailure();
        await _initializationLock.WaitAsync();
        try
        {
            if (_session is not null)
            {
                return;
            }

            ThrowCachedInitializationFailure();
            try
            {
                var tokenizerPath = Path.Combine(
                    GetBundleDirectory(),
                    "tokenizer.json");
                if (!File.Exists(tokenizerPath))
                {
                    throw new RankerException(
                        "tokenizer_missing",
                        $"Expected tokenizer.json in '{_options.ModelDirectory}'.");
                }
                ValidateFileIntegrity(
                    tokenizerPath,
                    TokenizerLength,
                    TokenizerSha256,
                    "tokenizer_integrity_failed",
                    "tokenizer");

                var modelPath = _options.Provider == RankerProvider.Npu
                    ? ValidateNpuArtifacts()
                    : ValidateCpuArtifacts();
                PairTokenizer? tokenizer = null;
                SessionOptions? sessionOptions = null;
                InferenceSession? session = null;
                NpuSessionSetup? npuSetup = null;
                try
                {
                    tokenizer = new PairTokenizer(tokenizerPath);
                    if (_options.Provider == RankerProvider.Npu)
                    {
                        npuSetup = await CreateNpuSessionOptionsAsync(
                            ReadNpuCompatibilitySidecar());
                        sessionOptions = npuSetup.Options;
                    }
                    else
                    {
                        sessionOptions = CreateCpuSessionOptions();
                    }

                    session = new InferenceSession(modelPath, sessionOptions);
                    ValidateModelInputs(session, _options.Provider);
                    if (npuSetup is not null)
                    {
                        ValidateLoadedNpuContext(
                            session,
                            npuSetup.Device,
                            npuSetup.CompatibilityInfo);
                    }

                    UpdateModelVersion(session);
                    _tokenizer = tokenizer;
                    tokenizer = null;
                    _session = session;
                    session = null;
                }
                catch (RankerException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var code = _options.Provider == RankerProvider.Npu
                        ? "npu_context_load_failed"
                        : "model_load_failed";
                    throw new RankerException(code, ex.Message, ex);
                }
                finally
                {
                    session?.Dispose();
                    tokenizer?.Dispose();
                    sessionOptions?.Dispose();
                }
            }
            catch (RankerException ex)
            {
                _initializationFailure = ex;
                throw;
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private string ValidateCpuArtifacts()
    {
        var modelPath = Path.Combine(
            GetBundleDirectory(),
            "Cpu",
            CpuModelFileName);
        if (!File.Exists(modelPath) || new FileInfo(modelPath).Length == 0)
        {
            throw new RankerException(
                "model_missing",
                $"Expected the dynamic FP32 CPU model at '{modelPath}'.");
        }

        ValidateFileIntegrity(
            modelPath,
            CpuModelLength,
            CpuModelSha256,
            "cpu_model_integrity_failed",
            "dynamic FP32 CPU model");
        return modelPath;
    }

    private string ValidateNpuArtifacts()
    {
        var npuDirectory = Path.Combine(GetBundleDirectory(), "Npu");
        var modelPath = Path.Combine(npuDirectory, NpuModelFileName);
        var binaryPath = Path.Combine(npuDirectory, NpuContextBinaryFileName);
        var compatibilityPath = Path.Combine(npuDirectory, NpuCompatibilityFileName);
        var missing = new[] { modelPath, binaryPath, compatibilityPath }
            .Where(path => !File.Exists(path) || new FileInfo(path).Length == 0)
            .Select(Path.GetFileName)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new RankerException(
                "npu_context_missing",
                "The fixed Vitis EPContext package is incomplete. Missing or empty: " +
                string.Join(", ", missing) +
                $". Expected all files in '{npuDirectory}'.");
        }

        ValidateFileIntegrity(
            modelPath,
            NpuModelLength,
            NpuModelSha256,
            "npu_context_integrity_failed",
            "Vitis EPContext ONNX");
        ValidateFileIntegrity(
            binaryPath,
            NpuContextBinaryLength,
            NpuContextBinarySha256,
            "npu_context_integrity_failed",
            "Vitis EPContext binary");
        ValidateFileIntegrity(
            compatibilityPath,
            NpuCompatibilityLength,
            NpuCompatibilitySha256,
            "npu_context_integrity_failed",
            "Vitis compatibility sidecar");
        return modelPath;
    }

    private static void ValidateFileIntegrity(
        string path,
        long expectedLength,
        string expectedSha256,
        string errorCode,
        string description)
    {
        var actualLength = new FileInfo(path).Length;
        if (actualLength != expectedLength)
        {
            throw new RankerException(
                errorCode,
                $"The pinned {description} has length {actualLength}, expected {expectedLength}: '{path}'.");
        }

        string actualSha256;
        try
        {
            using var stream = File.OpenRead(path);
            actualSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            throw new RankerException(
                errorCode,
                $"Could not hash the pinned {description} '{path}': {ex.Message}",
                ex);
        }

        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new RankerException(
                errorCode,
                $"The pinned {description} SHA-256 is {actualSha256}, expected {expectedSha256}: '{path}'.");
        }
    }

    private string ReadNpuCompatibilitySidecar()
    {
        var path = Path.Combine(
            GetBundleDirectory(),
            "Npu",
            NpuCompatibilityFileName);
        try
        {
            var compatibilityInfo = File.ReadAllText(path).Trim();
            if (compatibilityInfo.Length == 0)
            {
                throw new RankerException(
                    "npu_context_invalid",
                    $"The Vitis compatibility sidecar '{path}' is empty.");
            }

            return compatibilityInfo;
        }
        catch (RankerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RankerException(
                "npu_context_invalid",
                $"Could not read the Vitis compatibility sidecar '{path}': {ex.Message}",
                ex);
        }
    }

    private string GetBundleDirectory() =>
        Path.Combine(_options.ModelDirectory, ModelBundleDirectoryName);

    private async Task<NpuSessionSetup> CreateNpuSessionOptionsAsync(
        string compatibilityInfo)
    {
        var catalog = ExecutionProviderCatalog.GetDefault()
            ?? throw new RankerException(
                "npu_catalog_unavailable",
                "Windows ML execution-provider catalog is unavailable.");

        var vitisProvider = catalog.FindAllProviders().FirstOrDefault(
            provider => string.Equals(
                provider.Name,
                "VitisAIExecutionProvider",
                StringComparison.OrdinalIgnoreCase));
        if (vitisProvider is null)
        {
            throw new RankerException(
                "npu_provider_unavailable",
                "Windows ML did not find the certified VitisAIExecutionProvider package.");
        }

        try
        {
            if (vitisProvider.ReadyState != ExecutionProviderReadyState.Ready)
            {
                var readyResult = await vitisProvider.EnsureReadyAsync();
                if (readyResult.Status != ExecutionProviderReadyResultState.Success)
                {
                    throw new RankerException(
                        "npu_provider_registration_failed",
                        $"VitisAI provider readiness failed: {readyResult.Status}; " +
                        $"{readyResult.DiagnosticText}");
                }
            }

            if (!vitisProvider.TryRegister())
            {
                throw new RankerException(
                    "npu_provider_registration_failed",
                    "Windows ML could not register VitisAIExecutionProvider with ONNX Runtime.");
            }

            Console.Error.WriteLine("[ranker] Windows ML registered VitisAIExecutionProvider");
        }
        catch (RankerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RankerException(
                "npu_provider_registration_failed",
                $"Windows ML could not register a certified NPU provider: {ex.Message}",
                ex);
        }

        var environment = OrtEnv.Instance();
        var npuDevice = environment
            .GetEpDevices()
            .FirstOrDefault(device =>
                device.HardwareDevice.Type == OrtHardwareDeviceType.NPU &&
                device.EpName.Contains("VitisAI", StringComparison.OrdinalIgnoreCase));

        if (npuDevice is null)
        {
            var available = environment.GetEpDevices()
                .Select(device => $"{device.EpName}/{device.HardwareDevice.Type}")
                .Distinct(StringComparer.OrdinalIgnoreCase);
            throw new RankerException(
                "npu_provider_unavailable",
                "VitisAI NPU device was not exposed by Windows ML. Available devices: " +
                string.Join(", ", available));
        }

        Provider = npuDevice.EpName;
        var hardware = npuDevice.HardwareDevice;
        Device = "NPU";
        Console.Error.WriteLine(
            $"[ranker] selected NPU hardware: " +
            $"{hardware.Vendor} / device {hardware.DeviceId}");

        OrtCompiledModelCompatibility compatibility;
        try
        {
            compatibility = environment.GetModelCompatibilityForEpDevices(
                [npuDevice],
                compatibilityInfo);
        }
        catch (Exception ex)
        {
            throw new RankerException(
                "npu_context_compatibility_check_failed",
                $"Vitis could not validate the compiled EPContext sidecar: {ex.Message}",
                ex);
        }

        if (compatibility != OrtCompiledModelCompatibility.EP_SUPPORTED_OPTIMAL)
        {
            throw new RankerException(
                "npu_context_incompatible",
                $"The compiled Vitis EPContext is not optimal for this NPU: {compatibility}.");
        }

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
            EnableProfiling = true,
            ProfileOutputPathPrefix = Path.Combine(
                Path.GetTempPath(),
                $"asteria-ranker-npu-proof-{Environment.ProcessId}-{Guid.NewGuid():N}")
        };

        try
        {
            options.AddFreeDimensionOverrideByName("batch_size", NpuBatchSize);
            options.AddFreeDimensionOverrideByName("sequence_length", NpuSequenceLength);
            options.AppendExecutionProvider(
                environment,
                [npuDevice],
                new Dictionary<string, string>());

            // The pinned graph intentionally leaves an allowlisted front-end on
            // CPU: mask/position shape-control plus word/position embedding
            // lookups. The transformer/classifier is the Vitis EPContext node.
            // A one-shot warmup profile verifies that exact assignment before
            // any request can succeed.
            return new NpuSessionSetup(options, npuDevice, compatibilityInfo);
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    private static SessionOptions CreateCpuSessionOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
            IntraOpNumThreads = Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 4)),
            InterOpNumThreads = 1
        };
        options.AppendExecutionProvider_CPU();
        return options;
    }

    private CandidateScore[] Score(
        string markedContext,
        string target,
        IReadOnlyList<string> candidateDefinitions)
    {
        var session = _session
            ?? throw new RankerException("model_not_ready", "The ranker session is not initialized.");
        var tokenizer = _tokenizer
            ?? throw new RankerException("tokenizer_not_ready", "The tokenizer is not initialized.");
        var glossPairs = candidateDefinitions
            .Select(definition => $"{target.Trim()}: {definition.Trim()}")
            .ToArray();

        try
        {
            return _options.Provider == RankerProvider.Npu
                ? ScoreNpu(session, tokenizer, markedContext, glossPairs)
                : ScoreCpu(session, tokenizer, markedContext, glossPairs);
        }
        catch (RankerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RankerException("inference_failed", ex.Message, ex);
        }
    }

    private static CandidateScore[] ScoreCpu(
        InferenceSession session,
        PairTokenizer tokenizer,
        string markedContext,
        IReadOnlyList<string> candidates)
    {
        var scores = new CandidateScore[candidates.Count];
        for (var chunkOffset = 0; chunkOffset < candidates.Count; chunkOffset += CpuBatchSize)
        {
            var realCount = Math.Min(CpuBatchSize, candidates.Count - chunkOffset);
            var chunk = candidates
                .Skip(chunkOffset)
                .Take(realCount)
                .ToArray();
            var encoded = tokenizer.EncodePairs(
                markedContext,
                chunk,
                CpuMaximumSequenceLength,
                padToLongest: true);
            var rawScores = RunBatch(session, encoded);
            for (var lane = 0; lane < realCount; lane++)
            {
                var originalIndex = chunkOffset + lane;
                scores[originalIndex] = new CandidateScore(
                    originalIndex,
                    rawScores[lane]);
            }
        }

        return scores;
    }

    private static CandidateScore[] ScoreNpu(
        InferenceSession session,
        PairTokenizer tokenizer,
        string markedContext,
        IReadOnlyList<string> candidates)
    {
        var scores = new CandidateScore[candidates.Count];
        for (var chunkOffset = 0; chunkOffset < candidates.Count; chunkOffset += NpuBatchSize)
        {
            var realCount = Math.Min(NpuBatchSize, candidates.Count - chunkOffset);
            var paddedCandidates = new string[NpuBatchSize];
            var originalIndexes = new int[NpuBatchSize];
            Array.Fill(originalIndexes, -1);

            for (var lane = 0; lane < realCount; lane++)
            {
                var originalIndex = chunkOffset + lane;
                paddedCandidates[lane] = candidates[originalIndex];
                originalIndexes[lane] = originalIndex;
            }

            // Duplicate the last real pair into unused lanes. This keeps every
            // fixed lane in-distribution while the duplicate logits remain
            // excluded from global selection and confidence.
            for (var lane = realCount; lane < NpuBatchSize; lane++)
            {
                paddedCandidates[lane] = paddedCandidates[realCount - 1];
            }

            var encoded = tokenizer.EncodePairs(
                markedContext,
                paddedCandidates,
                NpuSequenceLength);
            if (encoded.BatchSize != NpuBatchSize ||
                encoded.SequenceLength != NpuSequenceLength)
            {
                throw new RankerException(
                    "invalid_npu_batch",
                    $"The NPU requires [{NpuBatchSize},{NpuSequenceLength}] input tensors.");
            }

            var chunkScores = RunBatch(session, encoded);
            for (var lane = 0; lane < realCount; lane++)
            {
                var originalIndex = originalIndexes[lane];
                scores[originalIndex] = new CandidateScore(
                    originalIndex,
                    chunkScores[lane]);
            }
        }

        return scores;
    }

    private static float[] RunBatch(InferenceSession session, EncodedBatch encoded)
    {
        var dimensions = new[] { encoded.BatchSize, encoded.SequenceLength };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                FindInputName(session, "input_ids"),
                new DenseTensor<long>(encoded.InputIds, dimensions)),
            NamedOnnxValue.CreateFromTensor(
                FindInputName(session, "attention_mask"),
                new DenseTensor<long>(encoded.AttentionMask, dimensions))
        };

        if (TryFindInputName(session, "token_type_ids", out var tokenTypeInputName))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                tokenTypeInputName,
                new DenseTensor<long>(encoded.TokenTypeIds, dimensions)));
        }

        using var outputs = session.Run(inputs);
        var tensor = outputs.First().AsTensor<float>();
        return ExtractCandidateScores(tensor, encoded.BatchSize);
    }

    private static float[] ExtractCandidateScores(Tensor<float> logits, int batchSize)
    {
        var raw = logits.ToArray();
        if (raw.Length != batchSize * 2)
        {
            throw new RankerException(
                "invalid_model_output",
                $"Expected two class logits per candidate, received {raw.Length} values " +
                $"for {batchSize} candidates.");
        }

        var scores = new float[batchSize];
        for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
        {
            var negativeLogit = raw[batchIndex * 2];
            var positiveLogit = raw[(batchIndex * 2) + 1];
            if (!float.IsFinite(negativeLogit) || !float.IsFinite(positiveLogit))
            {
                throw new RankerException("invalid_model_output", "The model returned a non-finite score.");
            }

            // For a two-class classifier, softmax(positive) is exactly the
            // stable sigmoid of the logit difference.
            var difference = (double)positiveLogit - negativeLogit;
            var probability = difference >= 0
                ? 1.0 / (1.0 + Math.Exp(-difference))
                : Math.Exp(difference) / (1.0 + Math.Exp(difference));
            scores[batchIndex] = (float)probability;
        }

        return scores;
    }

    private static string FindInputName(InferenceSession session, string conventionalName)
    {
        if (TryFindInputName(session, conventionalName, out var actualName))
        {
            return actualName;
        }

        throw new RankerException(
            "unsupported_model",
            $"The ONNX model does not expose the required '{conventionalName}' input.");
    }

    private static bool TryFindInputName(
        InferenceSession session,
        string conventionalName,
        out string actualName)
    {
        actualName = session.InputMetadata.Keys.FirstOrDefault(
            name => string.Equals(name, conventionalName, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return actualName.Length > 0;
    }

    private static void ValidateModelInputs(
        InferenceSession session,
        RankerProvider provider)
    {
        if (session.InputMetadata.Count != 3 ||
            !session.InputMetadata.ContainsKey("input_ids") ||
            !session.InputMetadata.ContainsKey("attention_mask") ||
            !session.InputMetadata.ContainsKey("token_type_ids"))
        {
            throw new RankerException(
                "unsupported_model",
                "GlossBERT must expose exactly the Int64 inputs " +
                "'input_ids', 'attention_mask', and 'token_type_ids'.");
        }

        const string inputIdsName = "input_ids";
        const string attentionMaskName = "attention_mask";
        const string tokenTypeIdsName = "token_type_ids";
        ValidateRankTwoInput(session, inputIdsName);
        ValidateRankTwoInput(session, attentionMaskName);
        ValidateRankTwoInput(session, tokenTypeIdsName);
        ValidateInputType(session, inputIdsName);
        ValidateInputType(session, attentionMaskName);
        ValidateInputType(session, tokenTypeIdsName);

        if (provider == RankerProvider.Npu)
        {
            ValidateFixedNpuInput(session, inputIdsName);
            ValidateFixedNpuInput(session, attentionMaskName);
            ValidateFixedNpuInput(session, tokenTypeIdsName);
        }
        else
        {
            ValidateDynamicCpuInput(session, inputIdsName);
            ValidateDynamicCpuInput(session, attentionMaskName);
            ValidateDynamicCpuInput(session, tokenTypeIdsName);
        }

        if (session.OutputMetadata.Count != 1 ||
            !session.OutputMetadata.TryGetValue("logits", out var logitsMetadata) ||
            logitsMetadata.ElementType != typeof(float) ||
            logitsMetadata.Dimensions.Length != 2)
        {
            throw new RankerException(
                "unsupported_model",
                "The ranker model must expose exactly one Float output named 'logits' with rank 2.");
        }

        var outputDimensions = logitsMetadata.Dimensions;
        var outputShapeIsValid = provider == RankerProvider.Npu
            ? outputDimensions[0] == NpuBatchSize && outputDimensions[1] == 2
            : outputDimensions[0] <= 0 && outputDimensions[1] == 2;
        if (!outputShapeIsValid)
        {
            throw new RankerException(
                "unsupported_model",
                $"Output 'logits' must be " +
                (provider == RankerProvider.Npu ? "[4,2]" : "[dynamic,2]") +
                $", received [{string.Join(",", outputDimensions)}].");
        }
    }

    private static void ValidateRankTwoInput(InferenceSession session, string inputName)
    {
        var dimensions = session.InputMetadata[inputName].Dimensions;
        if (dimensions.Length != 2)
        {
            throw new RankerException(
                "unsupported_model",
                $"Input '{inputName}' must have rank 2, received [{string.Join(",", dimensions)}].");
        }
    }

    private static void ValidateInputType(InferenceSession session, string inputName)
    {
        if (session.InputMetadata[inputName].ElementType != typeof(long))
        {
            throw new RankerException(
                "unsupported_model",
                $"Input '{inputName}' must contain Int64 values.");
        }
    }

    private static void ValidateFixedNpuInput(InferenceSession session, string inputName)
    {
        var dimensions = session.InputMetadata[inputName].Dimensions;
        if (dimensions[0] != NpuBatchSize || dimensions[1] != NpuSequenceLength)
        {
            throw new RankerException(
                "npu_context_shape_mismatch",
                $"The compiled EPContext input '{inputName}' must be " +
                $"[{NpuBatchSize},{NpuSequenceLength}], received " +
                $"[{string.Join(",", dimensions)}].");
        }
    }

    private static void ValidateDynamicCpuInput(InferenceSession session, string inputName)
    {
        var dimensions = session.InputMetadata[inputName].Dimensions;
        if (dimensions.Any(dimension => dimension > 0))
        {
            throw new RankerException(
                "cpu_model_not_dynamic",
                $"The FP32 CPU input '{inputName}' must retain dynamic batch and sequence dimensions, received " +
                $"[{string.Join(",", dimensions)}].");
        }
    }

    private static void ValidateLoadedNpuContext(
        InferenceSession session,
        OrtEpDevice npuDevice,
        string expectedCompatibilityInfo)
    {
        var compatibilityKey = $"ep_compatibility_info.{npuDevice.EpName}";
        if (!session.ModelMetadata.CustomMetadataMap.TryGetValue(
                compatibilityKey,
                out var embeddedCompatibilityInfo) ||
            string.IsNullOrWhiteSpace(embeddedCompatibilityInfo))
        {
            throw new RankerException(
                "npu_context_invalid",
                $"The NPU model is not a compiled {npuDevice.EpName} EPContext graph; " +
                $"metadata '{compatibilityKey}' is missing.");
        }

        embeddedCompatibilityInfo = embeddedCompatibilityInfo.Trim();
        if (!string.Equals(
                embeddedCompatibilityInfo,
                expectedCompatibilityInfo,
                StringComparison.Ordinal))
        {
            throw new RankerException(
                "npu_context_sidecar_mismatch",
                "The Vitis EPContext and its compatibility sidecar do not belong to the same package.");
        }

        OrtCompiledModelCompatibility compatibility;
        try
        {
            compatibility = OrtEnv.Instance().GetModelCompatibilityForEpDevices(
                [npuDevice],
                embeddedCompatibilityInfo);
        }
        catch (Exception ex)
        {
            throw new RankerException(
                "npu_context_compatibility_check_failed",
                $"Vitis could not validate the loaded EPContext metadata: {ex.Message}",
                ex);
        }

        if (compatibility != OrtCompiledModelCompatibility.EP_SUPPORTED_OPTIMAL)
        {
            throw new RankerException(
                "npu_context_incompatible",
                $"The loaded Vitis EPContext is not optimal for this NPU: {compatibility}.");
        }

        Console.Error.WriteLine(
            $"[ranker] validated fixed Vitis EPContext " +
            $"[{NpuBatchSize},{NpuSequenceLength}] ({compatibility})");
    }

    private void VerifyNpuExecutionProfile()
    {
        var session = _session
            ?? throw new RankerException(
                "npu_execution_unverified",
                "The NPU session is not initialized for execution verification.");
        string? profilePath = null;
        try
        {
            profilePath = session.EndProfiling();
            if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
            {
                throw new RankerException(
                    "npu_execution_unverified",
                    "ONNX Runtime did not produce the required one-shot NPU assignment profile.");
            }

            var cpuCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var vitisCount = 0;
            using (var stream = File.OpenRead(profilePath))
            using (var document = JsonDocument.Parse(stream))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new RankerException(
                        "npu_execution_unverified",
                        "The NPU assignment profile is not a JSON event array.");
                }

                foreach (var profileEvent in document.RootElement.EnumerateArray())
                {
                    if (!HasStringProperty(profileEvent, "cat", "Node") ||
                        !HasStringProperty(profileEvent, "ph", "X") ||
                        !profileEvent.TryGetProperty("name", out var nameElement) ||
                        nameElement.GetString() is not { } name ||
                        !name.EndsWith("_kernel_time", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!profileEvent.TryGetProperty("args", out var arguments) ||
                        arguments.ValueKind != JsonValueKind.Object ||
                        !arguments.TryGetProperty("provider", out var providerElement) ||
                        providerElement.GetString() is not { Length: > 0 } assignedProvider ||
                        !arguments.TryGetProperty("op_name", out var opNameElement) ||
                        opNameElement.GetString() is not { Length: > 0 } opName)
                    {
                        throw new RankerException(
                            "npu_execution_unverified",
                            $"Profiled kernel '{name}' has no provider/op assignment.");
                    }

                    if (string.Equals(
                            assignedProvider,
                            "CPUExecutionProvider",
                            StringComparison.Ordinal))
                    {
                        var key = $"{name}|{opName}";
                        if (!AllowedCpuProfileNodes.Contains(key) ||
                            !MatchesPinnedCpuProfileNode(key, arguments))
                        {
                            throw new RankerException(
                                "npu_cpu_assignment_rejected",
                                $"The pinned EPContext assigned an unexpected node or tensor shape to CPU: {key}.");
                        }

                        cpuCounts[key] = cpuCounts.GetValueOrDefault(key) + 1;
                        continue;
                    }

                    if (!string.Equals(
                            assignedProvider,
                            Provider,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            name,
                            $"{Provider}_{opName}_0_kernel_time",
                            StringComparison.Ordinal) ||
                        !HasPinnedVitisOpName(opName) ||
                        !HasStringProperty(arguments, "node_index", "6") ||
                        !HasTensorShapes(
                            arguments,
                            "input_type_shape",
                            new TensorShapeExpectation("float", [4, 64, 768]),
                            new TensorShapeExpectation("float", [4, 64, 768]),
                            new TensorShapeExpectation("bool", [4, 1, 1, 64, 1])) ||
                        !HasTensorShapes(
                            arguments,
                            "output_type_shape",
                            new TensorShapeExpectation("float", [4, 2])))
                    {
                        throw new RankerException(
                            "npu_execution_unverified",
                            $"Unexpected profiled execution assignment: {assignedProvider}|{name}|{opName}.");
                    }

                    vitisCount++;
                }
            }

            var invalidCpuCounts = AllowedCpuProfileNodes
                .Where(key => cpuCounts.GetValueOrDefault(key) != 1)
                .Select(key => $"{key}={cpuCounts.GetValueOrDefault(key)}")
                .ToArray();
            if (vitisCount != 1 || invalidCpuCounts.Length > 0)
            {
                throw new RankerException(
                    "npu_execution_unverified",
                    $"Expected one pinned Vitis fused kernel and one of every allowlisted CPU front-end node; " +
                    $"Vitis={vitisCount}; CPU=[{string.Join(", ", invalidCpuCounts)}].");
            }

            Console.Error.WriteLine(
                $"[ranker] NPU fail-closed proof passed: one Vitis fused kernel; " +
                $"{AllowedCpuProfileNodes.Count} allowlisted CPU front-end nodes");
        }
        catch (RankerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RankerException(
                "npu_execution_unverified",
                $"Could not verify the NPU execution assignment: {ex.Message}",
                ex);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                try
                {
                    File.Delete(profilePath);
                }
                catch
                {
                    // A stale diagnostic profile is harmless and must not turn
                    // an already verified NPU assignment into a runtime failure.
                }
            }
        }
    }

    private static bool HasStringProperty(
        JsonElement element,
        string propertyName,
        string expectedValue) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        string.Equals(property.GetString(), expectedValue, StringComparison.Ordinal);

    private static bool HasPinnedVitisOpName(string opName)
    {
        const string prefix = "VitisAIExecutionProvider_";
        const string suffix = "_0";
        if (!opName.StartsWith(prefix, StringComparison.Ordinal) ||
            !opName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var identifier = opName.AsSpan(
            prefix.Length,
            opName.Length - prefix.Length - suffix.Length);
        return identifier.Length > 0 && identifier.ToArray().All(char.IsAsciiDigit);
    }

    private static bool MatchesPinnedCpuProfileNode(
        string key,
        JsonElement arguments) =>
        key switch
        {
            "/model/bert/embeddings/word_embeddings/Gather_kernel_time|Gather" =>
                HasStringProperty(arguments, "node_index", "4") &&
                HasTensorShapes(
                    arguments,
                    "input_type_shape",
                    new TensorShapeExpectation("float", [30_522, 768]),
                    new TensorShapeExpectation("int64", [4, 64])) &&
                HasTensorShapes(
                    arguments,
                    "output_type_shape",
                    new TensorShapeExpectation("float", [4, 64, 768])),

            "/model/bert/embeddings/token_type_embeddings/Gather_kernel_time|Gather" =>
                HasStringProperty(arguments, "node_index", "3") &&
                HasTensorShapes(
                    arguments,
                    "input_type_shape",
                    new TensorShapeExpectation("float", [2, 768]),
                    new TensorShapeExpectation("int64", [4, 64])) &&
                HasTensorShapes(
                    arguments,
                    "output_type_shape",
                    new TensorShapeExpectation("float", [4, 64, 768])),

            "/model/bert/Cast_kernel_time|Cast" =>
                HasStringProperty(arguments, "node_index", "0") &&
                HasTensorShapes(
                    arguments,
                    "input_type_shape",
                    new TensorShapeExpectation("int64", [4, 64])) &&
                HasTensorShapes(
                    arguments,
                    "output_type_shape",
                    new TensorShapeExpectation("bool", [4, 64])),

            "/model/bert/Flatten_kernel_time|Flatten" =>
                HasStringProperty(arguments, "node_index", "1") &&
                HasTensorShapes(
                    arguments,
                    "input_type_shape",
                    new TensorShapeExpectation("bool", [4, 64])) &&
                HasTensorShapes(
                    arguments,
                    "output_type_shape",
                    new TensorShapeExpectation("bool", [256, 1])),

            "/model/bert/Gather_1_kernel_time|Gather" =>
                HasStringProperty(arguments, "node_index", "2") &&
                HasTensorShapes(
                    arguments,
                    "input_type_shape",
                    new TensorShapeExpectation("bool", [256, 1]),
                    new TensorShapeExpectation("int64", [4, 1, 1, 64])) &&
                HasTensorShapes(
                    arguments,
                    "output_type_shape",
                    new TensorShapeExpectation("bool", [4, 1, 1, 64, 1])),

            _ => false
        };

    private static bool HasTensorShapes(
        JsonElement arguments,
        string propertyName,
        params TensorShapeExpectation[] expectedShapes)
    {
        if (!arguments.TryGetProperty(propertyName, out var shapes) ||
            shapes.ValueKind != JsonValueKind.Array ||
            shapes.GetArrayLength() != expectedShapes.Length)
        {
            return false;
        }

        var shapeIndex = 0;
        foreach (var tensor in shapes.EnumerateArray())
        {
            if (tensor.ValueKind != JsonValueKind.Object ||
                !tensor.TryGetProperty(
                    expectedShapes[shapeIndex].ElementType,
                    out var dimensions) ||
                dimensions.ValueKind != JsonValueKind.Array ||
                !dimensions.EnumerateArray()
                    .Select(dimension => dimension.GetInt32())
                    .SequenceEqual(expectedShapes[shapeIndex].Dimensions))
            {
                return false;
            }

            shapeIndex++;
        }

        return true;
    }

    private void UpdateModelVersion(InferenceSession session)
    {
        var metadata = session.ModelMetadata;
        var artifactTag = _options.Provider == RankerProvider.Npu
            ? ":vitis-epcontext-b4-s64"
            : ":fp32-dynamic";
        var onnxVersionTag = metadata.Version > 0 && metadata.Version < long.MaxValue
            ? $":onnx-{metadata.Version}"
            : string.Empty;
        _crossModelVersion = DefaultModelVersion + artifactTag + onnxVersionTag;
        RefreshModelVersion();
    }

    private void RefreshModelVersion()
    {
        ModelVersion = _embeddingIndex is null
            ? _crossModelVersion + ":pipeline-cross"
            : _crossModelVersion + ":" + EmbeddingBundleCacheTag;
    }

    private void ThrowCachedInitializationFailure()
    {
        if (_initializationFailure is { } failure)
        {
            throw new RankerException(failure.ErrorCode, failure.Message, failure);
        }
    }

    private static void ValidateSelection(RankerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Context))
        {
            throw new RankerException("missing_context", "context is required for select.");
        }

        if (string.IsNullOrWhiteSpace(request.Target))
        {
            throw new RankerException("missing_target", "target is required for select.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceLanguage))
        {
            throw new RankerException("missing_source_language", "sourceLanguage is required for select.");
        }

        if (request.Candidates is not { Count: > 0 })
        {
            throw new RankerException("missing_candidates", "At least one candidate is required for select.");
        }

        if (request.Candidates.Count > MaximumCandidates)
        {
            throw new RankerException(
                "too_many_candidates",
                $"At most {MaximumCandidates} candidates can be ranked at once.");
        }

        if (request.Candidates.Any(string.IsNullOrWhiteSpace))
        {
            throw new RankerException("invalid_candidate", "Candidates cannot be empty.");
        }

        if (request.CandidateGroupIds is null ||
            request.CandidateGroupIds.Count != request.Candidates.Count)
        {
            throw new RankerException(
                "invalid_candidate_groups",
                "candidateGroupIds must contain one stable outcome id per candidate gloss.");
        }

        if (request.CandidateGroupIds.Any(groupId => groupId < 0))
        {
            throw new RankerException(
                "invalid_candidate_groups",
                "candidateGroupIds cannot contain negative values.");
        }

        if (!IsEnglish(request.SourceLanguage))
        {
            throw new RankerException(
                "unsupported_source_language",
                "The pinned GlossBERT sense ranker supports English source text only.");
        }
    }

    private static bool IsEnglish(string sourceLanguage) =>
        sourceLanguage.Trim().Equals("en", StringComparison.OrdinalIgnoreCase) ||
        sourceLanguage.Contains("english", StringComparison.OrdinalIgnoreCase) ||
        sourceLanguage.Contains("англ", StringComparison.OrdinalIgnoreCase);

    internal static string BuildMarkedContext(
        string context,
        string target,
        int? characterIndex)
    {
        int anchor = Math.Clamp(characterIndex ?? 0, 0, context.Length);
        int index = RankerSelectionLogic.FindClosestTargetOccurrence(
            context,
            target,
            anchor);
        if (index < 0)
        {
            throw new RankerException(
                "target_not_in_context",
                "The selected target does not occur in the source context.");
        }

        return (
            context[..index] +
            "\"" + context.Substring(index, target.Length) + "\"" +
            context[(index + target.Length)..]).Trim();
    }

    private static IReadOnlyList<GroupScore> GroupScores(
        IReadOnlyList<CandidateScore> scores,
        IReadOnlyList<int> candidateGroupIds)
    {
        var groupOrder = new List<int>();
        var maxima = new Dictionary<int, float>();
        for (var index = 0; index < scores.Count; index++)
        {
            var groupId = candidateGroupIds[index];
            var score = scores[index].Score;
            if (!maxima.TryGetValue(groupId, out var current))
            {
                groupOrder.Add(groupId);
                maxima[groupId] = score;
            }
            else if (score > current)
            {
                maxima[groupId] = score;
            }
        }

        return groupOrder
            .Select((groupId, order) => new GroupScore(groupId, maxima[groupId], order))
            .OrderByDescending(group => group.Score)
            .ThenBy(group => group.Order)
            .ToArray();
    }

}

internal sealed record RankerSelection(
    int? Index,
    bool Abstained,
    double Margin,
    double Score,
    double Confidence,
    string Pipeline = "cross");

internal readonly record struct CandidateScore(int OriginalIndex, float Score);

internal readonly record struct GroupScore(int GroupId, float Score, int Order);

internal sealed record TensorShapeExpectation(
    string ElementType,
    IReadOnlyList<int> Dimensions);

internal sealed record NpuSessionSetup(
    SessionOptions Options,
    OrtEpDevice Device,
    string CompatibilityInfo);
