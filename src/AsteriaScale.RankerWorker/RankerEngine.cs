using System.Text;
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
    // Shared with the host through the protocol assembly: the host folds the
    // same threshold into its decision cache keys, so a recalibration cannot
    // leave stale verdicts readable.
    private const double AbstentionMargin = RankerAbstention.MarginThreshold;
    private const string ModelBundleDirectoryName = "GlossBert";
    private const string DebertaBundleDirectoryName = "GlossDeberta";
    private const string SandwichBundleDirectoryName = "Sandwich";
    private const string DebertaManifestFileName = "model-manifest.json";
    // This is the current v2 pilot bundle pin. The final candidate intentionally
    // changes only these two values and the host's exact cache identity together.
    private const long DebertaManifestLength = 527;
    private const string DebertaManifestSha256 =
        "f4028fe04c8a138dfef3d4e453907c6d2ad9e14ed8a44af75d4410be51149b89";
    private const string SandwichNonVerbModelFileName = "encoder-nv-fp32.onnx";
    private const string SandwichVerbModelFileName = "encoder-v-fp32.onnx";
    private const long SandwichNonVerbModelLength = 568_094_742;
    private const string SandwichNonVerbModelSha256 =
        "b6a21c42c974f145a39a20e5c5b62b740cb0b63ed698499fa4c5e4b397998739";
    private const long SandwichVerbModelLength = 568_094_742;
    private const string SandwichVerbModelSha256 =
        "21b778dc9552f84732d1061ca1ace23f9f6c071e68fe4ab0615109f192e78f6b";
    private const long SandwichTokenizerLength = 8_656_779;
    private const string SandwichTokenizerSha256 =
        "2689db9b56b94ae9fac86daeb5fe7df0be0c5f1a9dce76a0edda7afa93dd1304";
    private const int SandwichMaximumSequenceLength = 512;
    private const int SandwichBatchSize = 8;
    internal const string SandwichModelCacheId =
        "SANDWiCH:naacl-2025:singleton-gloss:" +
        "m-nv-b6a21c42c974:m-v-21b778dc9552:t-2689db9b56b9:s512:c2:p1";
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
    // Format 1 recorded the SHA-256 of the gzip container; the shipped index is
    // format 1, so this stays the historical container digest and no longer
    // matches the sidecar on disk (recompression changes those bytes). A
    // regenerated bundle is format 2, whose digest covers the decompressed
    // contents — build-gloss-embeddings.py prints every constant to update.
    private const int EmbeddingIndexFormat = 1;
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
    private InferenceSession? _sandwichVerbSession;
    private PairTokenizer? _tokenizer;
    private ConsecArtifactBundle? _consecBundle;
    private ConsecTokenizer? _consecTokenizer;
    private ConsecInputBuilder? _consecInputBuilder;
    private GlossEmbeddingIndex? _embeddingIndex;
    private ContextEncoder? _contextEncoder;
    private bool _embeddingLoadAttempted;
    private RankerException? _embeddingFailure;
    private RankerException? _initializationFailure;
    private bool _warmupCompleted;
    private bool _npuExecutionVerified;
    private CpuModelConfiguration? _cpuModelConfiguration;
    private string _crossModelVersion;

    public RankerEngine(WorkerOptions options)
    {
        if (!options.Provider.Traits().AlternativeModels &&
            options.CpuModel != CpuRankerModel.GlossBert)
        {
            throw new RankerException(
                "unsupported_provider_model",
                $"The {options.CpuModel} ranker is CPU-only; " +
                "NPU remains pinned to GlossBERT.");
        }

        _options = options;
        Provider = options.Provider.Traits().ExecutionProvider;
        Device = options.Provider.Traits().Device;
        _crossModelVersion = options.CpuModel switch
        {
            CpuRankerModel.Deberta => "GlossDeBERTa:manifest-pending",
            CpuRankerModel.Sandwich => SandwichModelCacheId,
            CpuRankerModel.Consec => ConsecArtifactBundle.ModelCacheId,
            _ => DefaultModelVersion
        };
        ModelVersion = options.CpuModel switch
        {
            CpuRankerModel.Sandwich => _crossModelVersion + ":pipeline-sandwich",
            CpuRankerModel.Consec =>
                _crossModelVersion + ":pipeline-consec-independent",
            _ => _crossModelVersion
        };
    }

    public string Provider { get; private set; }

    public string Device { get; private set; }

    public string ModelVersion { get; private set; }

    public async Task WarmAsync()
    {
        await EnsureLoadedAsync();
        if (_options.Provider.Traits().BiEncoderPrefilter &&
            _options.CpuModel is not (CpuRankerModel.Sandwich or CpuRankerModel.Consec))
        {
            EnsureEmbeddingPipelineLoaded();
        }

        if (_warmupCompleted)
        {
            return;
        }

        if (_options.CpuModel == CpuRankerModel.Consec)
        {
            ConsecInput input = GetConsecInputBuilder().BuildFromTokens(
                ["The", "warmup", "pass", "prepares", "the", "ranker", "."],
                targetIndex: 1,
                ["a preparatory operation"],
                []);
            _ = RunConsec(input);
        }
        else
        {
            string warmupContext = _options.CpuModel == CpuRankerModel.Sandwich
                ? "The [D] warmup [D] pass prepares the dictionary ranker."
                : "The \"warmup\" pass prepares the dictionary ranker.";
            _ = Score(
                warmupContext,
                "warmup",
                ["a preparatory operation"]);
        }
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

        if (_options.CpuModel == CpuRankerModel.Consec)
        {
            ConsecInput input = GetConsecInputBuilder().BuildFromContext(
                request.Context!,
                request.Target!,
                request.CharacterIndex,
                request.Candidates!);
            float[] tokenLogits = RunConsec(input);
            ConsecSelection result = ConsecScorer.Select(
                tokenLogits,
                input.MarkerPositions,
                request.CandidateGroupIds!);
            return new RankerSelection(
                result.GroupId,
                result.Abstained,
                result.Margin,
                result.Score,
                result.Score,
                "consec-independent");
        }

        bool useSandwich = _options.CpuModel == CpuRankerModel.Sandwich;
        var markedContext = useSandwich
            ? BuildSandwichMarkedContext(
                request.Context!,
                request.Target!,
                request.CharacterIndex)
            : BuildMarkedContext(
                request.Context!,
                request.Target!,
                request.CharacterIndex);
        var (candidates, candidateGroupIds, pipeline) = useSandwich
            ? (request.Candidates!, request.CandidateGroupIds!, "sandwich")
            : ApplyBiEncoderPrefilter(request, markedContext);
        var scores = Score(markedContext, request.Target!, candidates);
        var groupedScores = useSandwich
            ? RankerSelectionLogic.AggregateSandwichGroups(
                scores.Select(score => score.Score).ToArray(),
                candidateGroupIds)
            : GroupScores(scores, candidateGroupIds);
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
                    _options.Provider.Traits().BiEncoderPrefilter,
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

        GlossEmbeddingIndex? index = null;
        ContextEncoder? encoder = null;
        try
        {
            using RankerArtifactReadLease indexLease =
                RankerArtifactIntegrity.AcquireReadLeaseAndValidate(
                    indexPath,
                    EmbeddingIndexLength,
                    EmbeddingIndexSha256,
                    "embedding_bundle_integrity_failed",
                    "gloss embedding index");
            using RankerArtifactReadLease modelLease =
                RankerArtifactIntegrity.AcquireReadLeaseAndValidate(
                    modelPath,
                    EmbeddingModelLength,
                    EmbeddingModelSha256,
                    "embedding_bundle_integrity_failed",
                    "MiniLM context encoder");
            using RankerArtifactReadLease tokenizerLease =
                RankerArtifactIntegrity.AcquireReadLeaseAndValidate(
                    tokenizerPath,
                    EmbeddingTokenizerLength,
                    EmbeddingTokenizerSha256,
                    "embedding_bundle_integrity_failed",
                    "MiniLM tokenizer");

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
        if (index.Format != EmbeddingIndexFormat ||
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
        _sandwichVerbSession?.Dispose();
        _tokenizer?.Dispose();
        _consecTokenizer?.Dispose();
        _consecBundle?.Dispose();
        _embeddingIndex?.Dispose();
        _contextEncoder?.Dispose();
        _initializationLock.Dispose();
    }

    private async Task EnsureLoadedAsync()
    {
        if (IsModelLoaded())
        {
            return;
        }

        ThrowCachedInitializationFailure();
        await _initializationLock.WaitAsync();
        try
        {
            if (IsModelLoaded())
            {
                return;
            }

            ThrowCachedInitializationFailure();
            try
            {
                if (_options.CpuModel == CpuRankerModel.Consec)
                {
                    LoadConsecCpuModel();
                    return;
                }

                var cpuModel = GetCpuModelConfiguration();
                var tokenizerPath = Path.Combine(
                    cpuModel.BundleDirectory,
                    "tokenizer.json");
                if (!File.Exists(tokenizerPath))
                {
                    throw new RankerException(
                        "tokenizer_missing",
                        $"Expected tokenizer.json in '{cpuModel.BundleDirectory}'.");
                }
                using RankerArtifactReadLease tokenizerLease =
                    RankerArtifactIntegrity.AcquireReadLeaseAndValidate(
                        tokenizerPath,
                        cpuModel.TokenizerLength,
                        cpuModel.TokenizerSha256,
                        "tokenizer_integrity_failed",
                        "tokenizer");
                using NpuArtifactLeases? npuArtifacts =
                    _options.Provider == RankerProvider.Npu
                        ? AcquireNpuArtifacts()
                        : null;
                using RankerArtifactReadLease? cpuModelLease =
                    _options.Provider == RankerProvider.Cpu
                        ? AcquireCpuArtifact(cpuModel)
                        : null;
                using RankerArtifactReadLease? sandwichVerbModelLease =
                    _options.Provider == RankerProvider.Cpu &&
                    _options.CpuModel == CpuRankerModel.Sandwich
                        ? AcquireSandwichVerbArtifact(cpuModel.BundleDirectory)
                        : null;
                string modelPath = npuArtifacts?.Model.Path ??
                    cpuModelLease?.Path ??
                    throw new RankerException(
                        "model_missing",
                        "No ranker model artifact was selected.");
                PairTokenizer? tokenizer = null;
                SessionOptions? sessionOptions = null;
                SessionOptions? sandwichVerbSessionOptions = null;
                InferenceSession? session = null;
                InferenceSession? sandwichVerbSession = null;
                NpuSessionSetup? npuSetup = null;
                try
                {
                    tokenizer = new PairTokenizer(tokenizerPath);
                    if (_options.Provider == RankerProvider.Npu)
                    {
                        npuSetup = await CreateNpuSessionOptionsAsync(
                            ReadNpuCompatibilitySidecar(
                                npuArtifacts!.CompatibilitySidecar));
                        sessionOptions = npuSetup.Options;
                    }
                    else
                    {
                        sessionOptions = CreateCpuSessionOptions();
                    }

                    session = new InferenceSession(modelPath, sessionOptions);
                    ValidateModelInputs(
                        session,
                        _options.Provider,
                        _options.CpuModel,
                        cpuModel.OutputClassCount);
                    if (_options.CpuModel == CpuRankerModel.Sandwich)
                    {
                        sandwichVerbSessionOptions = CreateCpuSessionOptions();
                        sandwichVerbSession = new InferenceSession(
                            sandwichVerbModelLease!.Path,
                            sandwichVerbSessionOptions);
                        ValidateModelInputs(
                            sandwichVerbSession,
                            RankerProvider.Cpu,
                            CpuRankerModel.Sandwich,
                            cpuModel.OutputClassCount);
                    }

                    if (npuSetup is not null)
                    {
                        ValidateLoadedNpuContext(
                            session,
                            npuSetup.Device,
                            npuSetup.CompatibilityInfo);
                    }

                    UpdateModelVersion(session, cpuModel);
                    _tokenizer = tokenizer;
                    tokenizer = null;
                    _sandwichVerbSession = sandwichVerbSession;
                    sandwichVerbSession = null;
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
                    sandwichVerbSession?.Dispose();
                    session?.Dispose();
                    tokenizer?.Dispose();
                    sandwichVerbSessionOptions?.Dispose();
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

    private bool IsModelLoaded() =>
        _session is not null &&
        (_options.CpuModel != CpuRankerModel.Sandwich ||
            _sandwichVerbSession is not null) &&
        (_options.CpuModel != CpuRankerModel.Consec ||
            (_consecBundle is not null &&
             _consecTokenizer is not null &&
             _consecInputBuilder is not null));

    private void LoadConsecCpuModel()
    {
        if (_options.Provider != RankerProvider.Cpu)
        {
            throw new RankerException(
                "unsupported_provider_model",
                "The ConSeC ranker is CPU-only.");
        }

        ConsecArtifactBundle? bundle = null;
        ConsecTokenizer? tokenizer = null;
        SessionOptions? sessionOptions = null;
        InferenceSession? session = null;
        try
        {
            bundle = ConsecArtifactBundle.Open(_options.ModelDirectory);
            tokenizer = new ConsecTokenizer(
                bundle.TokenizerPath,
                bundle.ReadTokenizerBytes());
            sessionOptions = CreateCpuSessionOptions();
            session = new InferenceSession(bundle.GraphPath, sessionOptions);
            ValidateConsecModelInputs(session);

            _crossModelVersion = ConsecArtifactBundle.ModelCacheId;
            ModelVersion = _crossModelVersion + ":pipeline-consec-independent";
            _consecInputBuilder = new ConsecInputBuilder(tokenizer);
            _consecTokenizer = tokenizer;
            tokenizer = null;
            _consecBundle = bundle;
            bundle = null;
            _session = session;
            session = null;
        }
        catch (RankerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RankerException(
                "model_load_failed",
                $"Could not load the pinned ConSeC CPU model: {ex.Message}",
                ex);
        }
        finally
        {
            session?.Dispose();
            sessionOptions?.Dispose();
            tokenizer?.Dispose();
            bundle?.Dispose();
        }
    }

    private ConsecInputBuilder GetConsecInputBuilder() =>
        _consecInputBuilder ?? throw new RankerException(
            "model_not_ready",
            "The ConSeC tokenizer and input builder are not initialized.");

    private float[] RunConsec(ConsecInput input)
    {
        InferenceSession session = _session ?? throw new RankerException(
            "model_not_ready",
            "The ConSeC CPU session is not initialized.");
        if (input.SequenceLength is <= 0 or > ConsecInputBuilder.MaximumSequenceLength ||
            input.InputIds.Length != input.SequenceLength ||
            input.AttentionMask.Length != input.SequenceLength ||
            input.TokenTypeIds.Length != input.SequenceLength ||
            input.RelativePositions.Length !=
                checked(input.SequenceLength * input.SequenceLength))
        {
            throw new RankerException(
                "consec_invalid_input",
                "The ConSeC input tensors do not have the pinned batch-1 shape.");
        }

        try
        {
            int sequenceLength = input.SequenceLength;
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(
                    "input_ids",
                    new DenseTensor<long>(
                        input.InputIds,
                        [1, sequenceLength])),
                NamedOnnxValue.CreateFromTensor(
                    "attention_mask",
                    new DenseTensor<long>(
                        input.AttentionMask,
                        [1, sequenceLength])),
                NamedOnnxValue.CreateFromTensor(
                    "token_type_ids",
                    new DenseTensor<long>(
                        input.TokenTypeIds,
                        [1, sequenceLength])),
                NamedOnnxValue.CreateFromTensor(
                    "relative_pos",
                    new DenseTensor<long>(
                        input.RelativePositions,
                        [1, sequenceLength, sequenceLength]))
            };
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
                session.Run(inputs);
            DisposableNamedOnnxValue? output = outputs.FirstOrDefault(item =>
                string.Equals(item.Name, "token_logits", StringComparison.Ordinal));
            if (output is null)
            {
                throw new RankerException(
                    "consec_invalid_output",
                    "The ConSeC model returned no 'token_logits' tensor.");
            }

            Tensor<float> logits = output.AsTensor<float>();
            if (logits.Dimensions.Length != 2 ||
                logits.Dimensions[0] != 1 ||
                logits.Dimensions[1] != sequenceLength)
            {
                throw new RankerException(
                    "consec_invalid_output",
                    $"ConSeC token_logits must be [1,{sequenceLength}], received " +
                    $"[{string.Join(',', logits.Dimensions.ToArray())}].");
            }

            return logits.ToArray();
        }
        catch (RankerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RankerException(
                "inference_failed",
                $"ConSeC CPU inference failed: {ex.Message}",
                ex);
        }
    }

    private static void ValidateConsecModelInputs(InferenceSession session)
    {
        (string Name, int Rank)[] expectedInputs =
        [
            ("input_ids", 2),
            ("attention_mask", 2),
            ("token_type_ids", 2),
            ("relative_pos", 3)
        ];
        if (session.InputMetadata.Count != expectedInputs.Length ||
            !session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedInputs.Select(item => item.Name)))
        {
            throw new RankerException(
                "unsupported_model",
                "The ConSeC model does not expose the exact pinned four-input ABI.");
        }

        foreach ((string name, int rank) in expectedInputs)
        {
            NodeMetadata metadata = session.InputMetadata[name];
            if (metadata.ElementType != typeof(long) ||
                metadata.Dimensions.Length != rank ||
                metadata.Dimensions.Any(dimension => dimension > 0))
            {
                throw new RankerException(
                    "unsupported_model",
                    $"ConSeC input '{name}' must be a dynamic rank-{rank} Int64 tensor; " +
                    $"received [{string.Join(',', metadata.Dimensions)}] " +
                    $"{metadata.ElementType.Name}.");
            }
        }

        if (session.OutputMetadata.Count != 1 ||
            !session.OutputMetadata.TryGetValue(
                "token_logits",
                out NodeMetadata? output) ||
            output.ElementType != typeof(float) ||
            output.Dimensions.Length != 2 ||
            output.Dimensions.Any(dimension => dimension > 0))
        {
            throw new RankerException(
                "unsupported_model",
                "ConSeC must expose one dynamic rank-2 Float output named 'token_logits'.");
        }
    }

    private static RankerArtifactReadLease AcquireCpuArtifact(
        CpuModelConfiguration cpuModel)
    {
        var modelPath = Path.Combine(
            cpuModel.BundleDirectory,
            "Cpu",
            cpuModel.ModelFileName);
        if (!File.Exists(modelPath) || new FileInfo(modelPath).Length == 0)
        {
            throw new RankerException(
                "model_missing",
                $"Expected the dynamic FP32 CPU model at '{modelPath}'.");
        }

        return RankerArtifactIntegrity.AcquireReadLeaseAndValidate(
            modelPath,
            cpuModel.ModelLength,
            cpuModel.ModelSha256,
            "cpu_model_integrity_failed",
            "dynamic FP32 CPU model");
    }

    private static RankerArtifactReadLease AcquireSandwichVerbArtifact(
        string bundleDirectory)
    {
        var modelPath = Path.Combine(
            bundleDirectory,
            "Cpu",
            SandwichVerbModelFileName);
        if (!File.Exists(modelPath) || new FileInfo(modelPath).Length == 0)
        {
            throw new RankerException(
                "model_missing",
                $"Expected the SANDWiCH verb encoder at '{modelPath}'.");
        }

        return RankerArtifactIntegrity.AcquireReadLeaseAndValidate(
            modelPath,
            SandwichVerbModelLength,
            SandwichVerbModelSha256,
            "cpu_model_integrity_failed",
            "SANDWiCH verb FP32 CPU model");
    }

    private NpuArtifactLeases AcquireNpuArtifacts()
    {
        var npuDirectory = Path.Combine(GetGlossBertBundleDirectory(), "Npu");
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

        RankerArtifactReadLease? model = null;
        RankerArtifactReadLease? binary = null;
        RankerArtifactReadLease? compatibility = null;
        try
        {
            model = RankerArtifactIntegrity.AcquireReadLeaseAndValidate(
                modelPath,
                NpuModelLength,
                NpuModelSha256,
                "npu_context_integrity_failed",
                "Vitis EPContext ONNX");
            binary = RankerArtifactIntegrity.AcquireReadLeaseAndValidate(
                binaryPath,
                NpuContextBinaryLength,
                NpuContextBinarySha256,
                "npu_context_integrity_failed",
                "Vitis EPContext binary");
            compatibility = RankerArtifactIntegrity.AcquireReadLeaseAndValidate(
                compatibilityPath,
                NpuCompatibilityLength,
                NpuCompatibilitySha256,
                "npu_context_integrity_failed",
                "Vitis compatibility sidecar");
            NpuArtifactLeases leases = new(model!, binary!, compatibility!);
            model = null;
            binary = null;
            compatibility = null;
            return leases;
        }
        finally
        {
            compatibility?.Dispose();
            binary?.Dispose();
            model?.Dispose();
        }
    }

    private static string ReadNpuCompatibilitySidecar(
        RankerArtifactReadLease compatibilitySidecar)
    {
        string path = compatibilitySidecar.Path;
        try
        {
            var compatibilityInfo = Encoding.UTF8
                .GetString(compatibilitySidecar.ReadAllBytes())
                .Trim();
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

    private string GetGlossBertBundleDirectory() =>
        Path.Combine(_options.ModelDirectory, ModelBundleDirectoryName);

    private CpuModelConfiguration GetCpuModelConfiguration()
    {
        if (_cpuModelConfiguration is not null)
        {
            return _cpuModelConfiguration;
        }

        _cpuModelConfiguration = _options.CpuModel switch
        {
            CpuRankerModel.GlossBert => new CpuModelConfiguration(
                BundleDirectory: GetGlossBertBundleDirectory(),
                ModelFileName: CpuModelFileName,
                ModelLength: CpuModelLength,
                ModelSha256: CpuModelSha256,
                TokenizerLength: TokenizerLength,
                TokenizerSha256: TokenizerSha256,
                MaximumSequenceLength: CpuMaximumSequenceLength,
                BatchSize: CpuBatchSize,
                OutputClassCount: 2,
                PositiveLabelIndex: 1,
                BaseModelVersion: DefaultModelVersion),
            CpuRankerModel.Deberta => LoadDebertaCpuModelConfiguration(
                _options.ModelDirectory),
            CpuRankerModel.Sandwich => new CpuModelConfiguration(
                BundleDirectory: Path.Combine(
                    _options.ModelDirectory,
                    SandwichBundleDirectoryName),
                ModelFileName: SandwichNonVerbModelFileName,
                ModelLength: SandwichNonVerbModelLength,
                ModelSha256: SandwichNonVerbModelSha256,
                TokenizerLength: SandwichTokenizerLength,
                TokenizerSha256: SandwichTokenizerSha256,
                MaximumSequenceLength: SandwichMaximumSequenceLength,
                BatchSize: SandwichBatchSize,
                OutputClassCount: 2,
                PositiveLabelIndex: 1,
                BaseModelVersion: SandwichModelCacheId),
            _ => throw new RankerException(
                "unsupported_cpu_model",
                $"Unsupported CPU model profile '{_options.CpuModel}'.")
        };
        return _cpuModelConfiguration;
    }

    internal static CpuModelConfiguration LoadDebertaCpuModelConfiguration(
        string modelDirectory)
    {
        var bundleDirectory = Path.Combine(
            modelDirectory,
            DebertaBundleDirectoryName);
        var manifestPath = Path.Combine(bundleDirectory, DebertaManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new RankerException(
                "cpu_model_manifest_missing",
                $"Expected the DeBERTa CPU manifest at '{manifestPath}'.");
        }

        // The manifest controls every other path, hash, tensor shape and cache
        // identity. Parse the authenticated byte array itself so a file swap
        // cannot replace it between verification and field deserialization.
        byte[] json = RankerArtifactIntegrity.ReadAllBytesAndValidate(
            manifestPath,
            DebertaManifestLength,
            DebertaManifestSha256,
            "cpu_model_manifest_integrity_failed",
            "DeBERTa CPU manifest");

        DebertaCpuModelManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidDebertaManifest(manifestPath, "root must be a JSON object");
            }

            var allowedProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "schemaVersion",
                "modelId",
                "modelFile",
                "modelLength",
                "modelSha256",
                "tokenizerLength",
                "tokenizerSha256",
                "maximumSequenceLength",
                "batchSize",
                "outputClassCount",
                "positiveLabelIndex"
            };
            var observedProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowedProperties.Contains(property.Name))
                {
                    throw InvalidDebertaManifest(
                        manifestPath,
                        $"unknown property '{property.Name}'");
                }

                if (!observedProperties.Add(property.Name))
                {
                    throw InvalidDebertaManifest(
                        manifestPath,
                        $"duplicate property '{property.Name}'");
                }
            }

            var missingProperties = allowedProperties
                .Where(property => !observedProperties.Contains(property))
                .OrderBy(property => property, StringComparer.Ordinal)
                .ToArray();
            if (missingProperties.Length > 0)
            {
                throw InvalidDebertaManifest(
                    manifestPath,
                    $"missing properties: {string.Join(", ", missingProperties)}");
            }

            manifest = document.RootElement.Deserialize<DebertaCpuModelManifest>(
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = false,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }) ?? throw InvalidDebertaManifest(
                    manifestPath,
                    "manifest deserialized to null");
        }
        catch (RankerException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new RankerException(
                "cpu_model_manifest_invalid",
                $"Invalid DeBERTa CPU manifest '{manifestPath}': {ex.Message}",
                ex);
        }

        ValidateDebertaManifest(manifestPath, manifest);
        var modelSha256 = manifest.ModelSha256!.ToLowerInvariant();
        var tokenizerSha256 = manifest.TokenizerSha256!.ToLowerInvariant();
        var baseModelVersion =
            $"GlossDeBERTa:{manifest.ModelId}:" +
            $"m-{modelSha256[..12]}:t-{tokenizerSha256[..12]}:" +
            $"s{manifest.MaximumSequenceLength}:b{manifest.BatchSize}:" +
            $"c{manifest.OutputClassCount}:p{manifest.PositiveLabelIndex}";
        return new CpuModelConfiguration(
            BundleDirectory: bundleDirectory,
            ModelFileName: manifest.ModelFile!,
            ModelLength: manifest.ModelLength,
            ModelSha256: modelSha256,
            TokenizerLength: manifest.TokenizerLength,
            TokenizerSha256: tokenizerSha256,
            MaximumSequenceLength: manifest.MaximumSequenceLength,
            BatchSize: manifest.BatchSize,
            OutputClassCount: manifest.OutputClassCount,
            PositiveLabelIndex: manifest.PositiveLabelIndex,
            BaseModelVersion: baseModelVersion);
    }

    private static void ValidateDebertaManifest(
        string manifestPath,
        DebertaCpuModelManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw InvalidDebertaManifest(
                manifestPath,
                $"schemaVersion must be 1, received {manifest.SchemaVersion}");
        }

        if (string.IsNullOrWhiteSpace(manifest.ModelId) ||
            manifest.ModelId.Length > 256 ||
            !string.Equals(manifest.ModelId, manifest.ModelId.Trim(), StringComparison.Ordinal) ||
            manifest.ModelId.Any(char.IsControl))
        {
            throw InvalidDebertaManifest(
                manifestPath,
                "modelId must be a trimmed, non-empty identifier of at most 256 characters");
        }

        if (string.IsNullOrWhiteSpace(manifest.ModelFile) ||
            manifest.ModelFile.Length > 128 ||
            Path.IsPathRooted(manifest.ModelFile) ||
            manifest.ModelFile.Contains('/') ||
            manifest.ModelFile.Contains('\\') ||
            !string.Equals(
                Path.GetFileName(manifest.ModelFile),
                manifest.ModelFile,
                StringComparison.Ordinal) ||
            !manifest.ModelFile.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidDebertaManifest(
                manifestPath,
                "modelFile must name one ONNX file directly inside the Cpu directory");
        }

        if (manifest.ModelLength <= 0)
        {
            throw InvalidDebertaManifest(manifestPath, "modelLength must be positive");
        }

        if (manifest.TokenizerLength <= 0)
        {
            throw InvalidDebertaManifest(manifestPath, "tokenizerLength must be positive");
        }

        if (!IsSha256(manifest.ModelSha256))
        {
            throw InvalidDebertaManifest(
                manifestPath,
                "modelSha256 must contain exactly 64 hexadecimal characters");
        }

        if (!IsSha256(manifest.TokenizerSha256))
        {
            throw InvalidDebertaManifest(
                manifestPath,
                "tokenizerSha256 must contain exactly 64 hexadecimal characters");
        }

        if (manifest.MaximumSequenceLength is < 16 or > 512)
        {
            throw InvalidDebertaManifest(
                manifestPath,
                "maximumSequenceLength must be between 16 and 512");
        }

        if (manifest.BatchSize is < 1 or > MaximumCandidates)
        {
            throw InvalidDebertaManifest(
                manifestPath,
                $"batchSize must be between 1 and {MaximumCandidates}");
        }

        if (manifest.OutputClassCount is < 2 or > 16)
        {
            throw InvalidDebertaManifest(
                manifestPath,
                "outputClassCount must be between 2 and 16");
        }

        if (manifest.PositiveLabelIndex < 0 ||
            manifest.PositiveLabelIndex >= manifest.OutputClassCount)
        {
            throw InvalidDebertaManifest(
                manifestPath,
                "positiveLabelIndex must identify one output class");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static RankerException InvalidDebertaManifest(
        string manifestPath,
        string detail) =>
        new(
            "cpu_model_manifest_invalid",
            $"Invalid DeBERTa CPU manifest '{manifestPath}': {detail}.");

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
            .Select(definition => _options.CpuModel == CpuRankerModel.Sandwich
                ? definition.Trim()
                : $"{target.Trim()}: {definition.Trim()}")
            .ToArray();

        try
        {
            if (_options.Provider == RankerProvider.Npu)
            {
                return ScoreNpu(session, tokenizer, markedContext, glossPairs);
            }

            if (_options.CpuModel == CpuRankerModel.Sandwich)
            {
                var verbSession = _sandwichVerbSession
                    ?? throw new RankerException(
                        "model_not_ready",
                        "The SANDWiCH verb encoder session is not initialized.");
                return ScoreSandwich(
                    session,
                    verbSession,
                    tokenizer,
                    markedContext,
                    glossPairs);
            }

            return ScoreCpu(session, tokenizer, markedContext, glossPairs);
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

    private CandidateScore[] ScoreCpu(
        InferenceSession session,
        PairTokenizer tokenizer,
        string markedContext,
        IReadOnlyList<string> candidates)
    {
        var cpuModel = GetCpuModelConfiguration();
        var scores = new CandidateScore[candidates.Count];
        for (var chunkOffset = 0;
            chunkOffset < candidates.Count;
            chunkOffset += cpuModel.BatchSize)
        {
            var realCount = Math.Min(
                cpuModel.BatchSize,
                candidates.Count - chunkOffset);
            var chunk = candidates
                .Skip(chunkOffset)
                .Take(realCount)
                .ToArray();
            var encoded = tokenizer.EncodePairs(
                markedContext,
                chunk,
                cpuModel.MaximumSequenceLength,
                padToLongest: true);
            var rawScores = RunBatch(
                session,
                encoded,
                cpuModel.OutputClassCount,
                cpuModel.PositiveLabelIndex);
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

    private CandidateScore[] ScoreSandwich(
        InferenceSession nonVerbSession,
        InferenceSession verbSession,
        PairTokenizer tokenizer,
        string markedContext,
        IReadOnlyList<string> candidates)
    {
        var cpuModel = GetCpuModelConfiguration();
        var scores = new CandidateScore[candidates.Count];
        for (var chunkOffset = 0;
            chunkOffset < candidates.Count;
            chunkOffset += cpuModel.BatchSize)
        {
            var realCount = Math.Min(
                cpuModel.BatchSize,
                candidates.Count - chunkOffset);
            var chunk = candidates
                .Skip(chunkOffset)
                .Take(realCount)
                .ToArray();
            var encoded = tokenizer.EncodePairs(
                markedContext,
                chunk,
                cpuModel.MaximumSequenceLength,
                padToLongest: true);
            var nonVerbProbabilities = RunBatch(
                nonVerbSession,
                encoded,
                cpuModel.OutputClassCount,
                cpuModel.PositiveLabelIndex);
            var verbProbabilities = RunBatch(
                verbSession,
                encoded,
                cpuModel.OutputClassCount,
                cpuModel.PositiveLabelIndex);
            var combinedProbabilities =
                RankerSelectionLogic.AverageSandwichProbabilities(
                    nonVerbProbabilities,
                    verbProbabilities);
            for (var lane = 0; lane < realCount; lane++)
            {
                var originalIndex = chunkOffset + lane;
                scores[originalIndex] = new CandidateScore(
                    originalIndex,
                    combinedProbabilities[lane]);
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

            var chunkScores = RunBatch(
                session,
                encoded,
                outputClassCount: 2,
                positiveLabelIndex: 1);
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

    private static float[] RunBatch(
        InferenceSession session,
        EncodedBatch encoded,
        int outputClassCount,
        int positiveLabelIndex)
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
        return ExtractCandidateScores(
            tensor,
            encoded.BatchSize,
            outputClassCount,
            positiveLabelIndex);
    }

    internal static float[] ExtractCandidateScores(
        Tensor<float> logits,
        int batchSize,
        int outputClassCount,
        int positiveLabelIndex) =>
        RankerOutputProbabilities.ExtractPositiveClass(
            logits.ToArray(),
            batchSize,
            outputClassCount,
            positiveLabelIndex);

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
        RankerProvider provider,
        CpuRankerModel cpuModel,
        int outputClassCount)
    {
        var tokenTypeIdsRequired = provider == RankerProvider.Npu ||
            cpuModel == CpuRankerModel.GlossBert;
        var expectedInputCount = tokenTypeIdsRequired ? 3 :
            session.InputMetadata.ContainsKey("token_type_ids") ? 3 : 2;
        if (session.InputMetadata.Count != expectedInputCount ||
            !session.InputMetadata.ContainsKey("input_ids") ||
            !session.InputMetadata.ContainsKey("attention_mask") ||
            (tokenTypeIdsRequired &&
                !session.InputMetadata.ContainsKey("token_type_ids")))
        {
            throw new RankerException(
                "unsupported_model",
                tokenTypeIdsRequired
                    ? "GlossBERT must expose exactly the Int64 inputs " +
                        "'input_ids', 'attention_mask', and 'token_type_ids'."
                    : "The CPU definition ranker must expose exactly the Int64 inputs " +
                        "'input_ids' and 'attention_mask', with optional 'token_type_ids'.");
        }

        const string inputIdsName = "input_ids";
        const string attentionMaskName = "attention_mask";
        ValidateRankTwoInput(session, inputIdsName);
        ValidateRankTwoInput(session, attentionMaskName);
        ValidateInputType(session, inputIdsName);
        ValidateInputType(session, attentionMaskName);
        if (session.InputMetadata.ContainsKey("token_type_ids"))
        {
            const string tokenTypeIdsName = "token_type_ids";
            ValidateRankTwoInput(session, tokenTypeIdsName);
            ValidateInputType(session, tokenTypeIdsName);
        }

        if (provider == RankerProvider.Npu)
        {
            ValidateFixedNpuInput(session, inputIdsName);
            ValidateFixedNpuInput(session, attentionMaskName);
            ValidateFixedNpuInput(session, "token_type_ids");
        }
        else
        {
            ValidateDynamicCpuInput(session, inputIdsName);
            ValidateDynamicCpuInput(session, attentionMaskName);
            if (session.InputMetadata.ContainsKey("token_type_ids"))
            {
                ValidateDynamicCpuInput(session, "token_type_ids");
            }
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
            ? outputDimensions[0] == NpuBatchSize && outputDimensions[1] == outputClassCount
            : outputDimensions[0] <= 0 && outputDimensions[1] == outputClassCount;
        if (!outputShapeIsValid)
        {
            throw new RankerException(
                "unsupported_model",
                $"Output 'logits' must be " +
                (provider == RankerProvider.Npu
                    ? $"[{NpuBatchSize},{outputClassCount}]"
                    : $"[dynamic,{outputClassCount}]") +
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

    private void UpdateModelVersion(
        InferenceSession session,
        CpuModelConfiguration cpuModel)
    {
        if (_options.CpuModel == CpuRankerModel.Sandwich)
        {
            // This identity is a host/cache contract. The two artifact hashes,
            // literal-[D] singleton-gloss adapter and tensor contract are all
            // encoded explicitly rather than delegated to mutable ONNX metadata.
            _crossModelVersion = SandwichModelCacheId;
            RefreshModelVersion();
            return;
        }

        var metadata = session.ModelMetadata;
        var artifactTag = _options.Provider == RankerProvider.Npu
            ? ":vitis-epcontext-b4-s64"
            : ":fp32-dynamic";
        var onnxVersionTag = metadata.Version > 0 && metadata.Version < long.MaxValue
            ? $":onnx-{metadata.Version}"
            : string.Empty;
        _crossModelVersion =
            cpuModel.BaseModelVersion + artifactTag + onnxVersionTag;
        RefreshModelVersion();
    }

    private void RefreshModelVersion()
    {
        if (_options.CpuModel == CpuRankerModel.Consec)
        {
            ModelVersion = _crossModelVersion + ":pipeline-consec-independent";
            return;
        }

        if (_options.CpuModel == CpuRankerModel.Sandwich)
        {
            ModelVersion = _crossModelVersion + ":pipeline-sandwich";
            return;
        }

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
        if (!RankerTargetAnchor.TryBuildMarkedContext(
            context,
            target,
            anchor,
            out string markedContext))
        {
            throw new RankerException(
                "target_not_in_context",
                "The selected target does not occur in the source context.");
        }

        return markedContext;
    }

    internal static string BuildSandwichMarkedContext(
        string context,
        string target,
        int? characterIndex)
    {
        int anchor = Math.Clamp(characterIndex ?? 0, 0, context.Length);
        if (!RankerTargetAnchor.TryBuildMarkedContext(
            context,
            target,
            anchor,
            RankerTargetMarker.SandwichD,
            out string markedContext))
        {
            throw new RankerException(
                "target_not_in_context",
                "The selected target does not occur in the source context.");
        }

        return markedContext;
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

internal sealed record CpuModelConfiguration(
    string BundleDirectory,
    string ModelFileName,
    long ModelLength,
    string ModelSha256,
    long TokenizerLength,
    string TokenizerSha256,
    int MaximumSequenceLength,
    int BatchSize,
    int OutputClassCount,
    int PositiveLabelIndex,
    string BaseModelVersion);

internal sealed record DebertaCpuModelManifest
{
    public int SchemaVersion { get; init; }

    public string? ModelId { get; init; }

    public string? ModelFile { get; init; }

    public long ModelLength { get; init; }

    public string? ModelSha256 { get; init; }

    public long TokenizerLength { get; init; }

    public string? TokenizerSha256 { get; init; }

    public int MaximumSequenceLength { get; init; }

    public int BatchSize { get; init; }

    public int OutputClassCount { get; init; }

    public int PositiveLabelIndex { get; init; }
}

internal sealed record TensorShapeExpectation(
    string ElementType,
    IReadOnlyList<int> Dimensions);

internal sealed record NpuSessionSetup(
    SessionOptions Options,
    OrtEpDevice Device,
    string CompatibilityInfo);

internal sealed class NpuArtifactLeases : IDisposable
{
    internal NpuArtifactLeases(
        RankerArtifactReadLease model,
        RankerArtifactReadLease contextBinary,
        RankerArtifactReadLease compatibilitySidecar)
    {
        Model = model;
        ContextBinary = contextBinary;
        CompatibilitySidecar = compatibilitySidecar;
    }

    internal RankerArtifactReadLease Model { get; }

    internal RankerArtifactReadLease ContextBinary { get; }

    internal RankerArtifactReadLease CompatibilitySidecar { get; }

    public void Dispose()
    {
        CompatibilitySidecar.Dispose();
        ContextBinary.Dispose();
        Model.Dispose();
    }
}
