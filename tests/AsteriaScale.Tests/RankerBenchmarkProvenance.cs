using System.Security.Cryptography;
using System.Text.Json;
using AsteriaScale.RankerProtocol;
using AsteriaScale.RankerWorker;
using AsteriaScale.Services;

internal sealed record BenchmarkArtifactRecord(
    string Name,
    long Bytes,
    string Sha256);

internal sealed record RankerBenchmarkProvenanceData(
    BenchmarkArtifactRecord BenchmarkSet,
    IReadOnlyList<BenchmarkArtifactRecord> RuntimeArtifacts,
    IReadOnlyList<BenchmarkArtifactRecord> DictionaryArtifacts,
    IReadOnlyList<BenchmarkArtifactRecord> ModelArtifacts);

internal static class RankerBenchmarkProvenance
{
    internal static async Task<RankerBenchmarkProvenanceData> CaptureAsync(
        string benchmarkPath,
        string workerBaseDirectory,
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile,
        string modelVersion,
        CancellationToken cancellationToken = default)
    {
        string modelDirectory = Path.Combine(
            workerBaseDirectory,
            "Ranker",
            "Models");
        IReadOnlyList<BenchmarkArtifactRecord> runtimeArtifacts =
            await DescribeArtifactsAsync(
                EnumerateRuntimeArtifactPaths(workerBaseDirectory),
                cancellationToken);
        IReadOnlyList<BenchmarkArtifactRecord> dictionaryArtifacts =
            await DescribeArtifactsAsync(
                EnumerateDictionaryArtifactPaths(AppContext.BaseDirectory)
                    .Select(path => new NamedArtifactPath(
                        Path.GetRelativePath(AppContext.BaseDirectory, path)
                            .Replace('\\', '/'),
                        path)),
                cancellationToken);
        List<BenchmarkArtifactRecord> modelArtifacts =
            (await DescribeArtifactsAsync(
                EnumerateActiveModelArtifactPaths(
                        workerBaseDirectory,
                        provider,
                        cpuModelProfile,
                        modelVersion)
                    .Select(path => new NamedArtifactPath(
                        Path.GetRelativePath(modelDirectory, path)
                            .Replace('\\', '/'),
                        path)),
                cancellationToken))
            .ToList();
        if (cpuModelProfile == RankerCpuModelProfile.Consec)
        {
            modelArtifacts.Add(await DescribePinnedConsecReferenceGoldenAsync(
                ResolveConsecReferenceGoldenPath(AppContext.BaseDirectory),
                cancellationToken));
            modelArtifacts.Sort((left, right) =>
                StringComparer.Ordinal.Compare(left.Name, right.Name));
            ValidatePinnedConsecArtifacts(modelArtifacts);
        }

        return new RankerBenchmarkProvenanceData(
            await DescribeArtifactAsync(
                benchmarkPath,
                Path.GetFileName(benchmarkPath),
                cancellationToken),
            runtimeArtifacts,
            dictionaryArtifacts,
            modelArtifacts);
    }

    internal static async Task<BenchmarkArtifactRecord> DescribeArtifactAsync(
        string path,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = stream.Length;
        string hash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return new BenchmarkArtifactRecord(
            name ?? Path.GetFileName(path),
            length,
            hash);
    }

    internal static async Task<BenchmarkArtifactRecord>
        DescribePinnedConsecReferenceGoldenAsync(
            string path,
            CancellationToken cancellationToken = default)
    {
        BenchmarkArtifactRecord artifact = await DescribeArtifactAsync(
            path,
            "Consec/Reference/" + ConsecArtifactBundle.OfficialReferenceGoldenFileName,
            cancellationToken);
        if (artifact.Bytes !=
                ConsecArtifactBundle.OfficialReferenceGoldenLength ||
            !string.Equals(
                artifact.Sha256,
                ConsecArtifactBundle.OfficialReferenceGoldenSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The official ConSeC reference golden does not match its " +
                $"pinned artifact identity: '{path}'.");
        }

        return artifact;
    }

    internal static IReadOnlyList<string> EnumerateDictionaryArtifactPaths(
        string applicationBaseDirectory)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string dictionaryDirectory = Path.Combine(
            applicationBaseDirectory,
            "Dictionaries");
        if (Directory.Exists(dictionaryDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(
                         dictionaryDirectory,
                         "eng-rus*.tsv*",
                         SearchOption.TopDirectoryOnly)
                     .Where(IsDictionaryDataFile))
            {
                paths.Add(Path.GetFullPath(path));
            }
        }

        string morphologyPath = Path.Combine(
            applicationBaseDirectory,
            "Morphology",
            "ru-morph.tsv.gz");
        if (File.Exists(morphologyPath))
        {
            paths.Add(Path.GetFullPath(morphologyPath));
        }

        return paths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> EnumerateActiveModelArtifactPaths(
        string workerBaseDirectory,
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile,
        string modelVersion)
    {
        string modelDirectory = Path.Combine(
            workerBaseDirectory,
            "Ranker",
            "Models");
        if (!Directory.Exists(modelDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The ranker model directory is missing: '{modelDirectory}'.");
        }

        if (cpuModelProfile == RankerCpuModelProfile.Deberta &&
            (provider != GlossBertRankerProvider.Cpu ||
             !modelVersion.StartsWith(
                 GlossBertDictionarySelectorService.DebertaCpuModelCacheId,
                 StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "A DeBERTa report requires the exact pinned CPU artifact identity.");
        }
        if (cpuModelProfile == RankerCpuModelProfile.Sandwich &&
            (provider != GlossBertRankerProvider.Cpu ||
             !modelVersion.StartsWith(
                 GlossBertDictionarySelectorService.SandwichCpuModelCacheId,
                 StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "A SANDWiCH report requires the exact pinned CPU artifact identity.");
        }
        if (cpuModelProfile == RankerCpuModelProfile.Consec &&
            (provider != GlossBertRankerProvider.Cpu ||
             !string.Equals(
                 modelVersion,
                 GlossBertDictionarySelectorService.ConsecCpuModelCacheId +
                    ":pipeline-consec-independent",
                 StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "A ConSeC report requires the exact pinned independent CPU pipeline identity.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cpuModelProfile == RankerCpuModelProfile.Deberta)
        {
            string bundle = Path.Combine(modelDirectory, "GlossDeberta");
            string manifestPath = Path.Combine(bundle, "model-manifest.json");
            string modelFile = ReadPinnedDebertaModelFile(manifestPath);
            AddRequiredArtifact(paths, Path.Combine(bundle, "tokenizer.json"));
            AddRequiredArtifact(paths, manifestPath);
            AddRequiredArtifact(paths, Path.Combine(bundle, "Cpu", modelFile));
        }
        else if (cpuModelProfile == RankerCpuModelProfile.Sandwich)
        {
            string bundle = Path.Combine(modelDirectory, "Sandwich");
            AddRequiredArtifact(paths, Path.Combine(bundle, "model-manifest.json"));
            AddRequiredArtifact(paths, Path.Combine(bundle, "tokenizer.json"));
            AddRequiredArtifact(
                paths,
                Path.Combine(bundle, "Cpu", "encoder-nv-fp32.onnx"));
            AddRequiredArtifact(
                paths,
                Path.Combine(bundle, "Cpu", "encoder-v-fp32.onnx"));
        }
        else if (cpuModelProfile == RankerCpuModelProfile.Consec)
        {
            string bundle = Path.Combine(modelDirectory, "Consec");
            AddRequiredArtifact(paths, Path.Combine(bundle, "model-manifest.json"));
            AddRequiredArtifact(paths, Path.Combine(bundle, "tokenizer.json"));
            AddRequiredArtifact(
                paths,
                Path.Combine(bundle, "Cpu", "consec-wngt-fp32.onnx"));
            AddRequiredArtifact(
                paths,
                Path.Combine(bundle, "Cpu", "consec-wngt-fp32.onnx.data"));
        }
        else
        {
            string bundle = Path.Combine(modelDirectory, "GlossBert");
            AddRequiredArtifact(paths, Path.Combine(bundle, "tokenizer.json"));
            if (provider == GlossBertRankerProvider.Cpu)
            {
                AddRequiredArtifact(
                    paths,
                    Path.Combine(bundle, "Cpu", "glossbert-fp32-dynamic.onnx"));
            }
            else
            {
                string npu = Path.Combine(bundle, "Npu");
                AddRequiredArtifact(
                    paths,
                    Path.Combine(npu, "glossbert-eager-vitis-context.onnx"));
                AddRequiredArtifact(
                    paths,
                    Path.Combine(npu, "glossbert-eager-vitis-context.onnx_VITISAI.bin"));
                AddRequiredArtifact(
                    paths,
                    Path.Combine(
                        npu,
                        "glossbert-eager-vitis-context.onnx.compatibility.txt"));
            }
        }

        bool usesBiEncoder =
            provider == GlossBertRankerProvider.Cpu &&
            modelVersion.EndsWith(
                ":" + GlossBertDictionarySelectorService.CpuEmbeddingBundleCacheTag,
                StringComparison.Ordinal);
        if (usesBiEncoder)
        {
            string embeddingsDirectory = Path.Combine(
                modelDirectory,
                "GlossEmbeddings");
            AddRequiredArtifact(
                paths,
                Path.Combine(embeddingsDirectory, "eng-senses.vec"));
            AddRequiredArtifact(
                paths,
                Path.Combine(embeddingsDirectory, "minilm-l6-v2.onnx"));
            AddRequiredArtifact(
                paths,
                Path.Combine(embeddingsDirectory, "tokenizer.json"));
        }

        return paths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<BenchmarkArtifactRecord>>
        DescribeArtifactsAsync(
            IEnumerable<NamedArtifactPath> paths,
            CancellationToken cancellationToken)
    {
        var artifacts = new List<BenchmarkArtifactRecord>();
        foreach (NamedArtifactPath artifact in paths.OrderBy(
                     item => item.Name,
                     StringComparer.Ordinal))
        {
            artifacts.Add(await DescribeArtifactAsync(
                artifact.Path,
                artifact.Name,
                cancellationToken));
        }

        return artifacts;
    }

    internal static void ValidatePinnedConsecArtifacts(
        IReadOnlyList<BenchmarkArtifactRecord> artifacts)
    {
        var expected = new Dictionary<string, (long Bytes, string Sha256)>(
            StringComparer.Ordinal)
        {
            ["Consec/model-manifest.json"] = (
                ConsecArtifactBundle.ManifestLength,
                ConsecArtifactBundle.ManifestSha256),
            ["Consec/tokenizer.json"] = (
                ConsecArtifactBundle.TokenizerLength,
                ConsecArtifactBundle.TokenizerSha256),
            ["Consec/Cpu/consec-wngt-fp32.onnx"] = (
                ConsecArtifactBundle.GraphLength,
                ConsecArtifactBundle.GraphSha256),
            ["Consec/Cpu/consec-wngt-fp32.onnx.data"] = (
                ConsecArtifactBundle.ExternalDataLength,
                ConsecArtifactBundle.ExternalDataSha256),
            ["Consec/Reference/" +
             ConsecArtifactBundle.OfficialReferenceGoldenFileName] = (
                ConsecArtifactBundle.OfficialReferenceGoldenLength,
                ConsecArtifactBundle.OfficialReferenceGoldenSha256)
        };
        if (artifacts.Count != expected.Count)
        {
            throw new InvalidDataException(
                "A ConSeC report must record exactly manifest, tokenizer, ONNX " +
                "graph, external data and the official reference golden.");
        }

        foreach (BenchmarkArtifactRecord artifact in artifacts)
        {
            if (!expected.TryGetValue(artifact.Name, out var pin) ||
                artifact.Bytes != pin.Bytes ||
                !string.Equals(
                    artifact.Sha256,
                    pin.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The ConSeC benchmark artifact is not pinned: " +
                    $"{artifact.Name} ({artifact.Bytes}, {artifact.Sha256}).");
            }
        }
    }

    internal static string ResolveConsecReferenceGoldenPath(
        string applicationBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        string path = Path.Combine(
            applicationBaseDirectory,
            "Ranker",
            "Reference",
            ConsecArtifactBundle.OfficialReferenceGoldenFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The official ConSeC reference golden is missing while " +
                "recording benchmark provenance.",
                path);
        }

        return Path.GetFullPath(path);
    }

    private static IReadOnlyList<NamedArtifactPath> EnumerateRuntimeArtifactPaths(
        string workerBaseDirectory)
    {
        var paths = new SortedDictionary<string, string>(StringComparer.Ordinal);
        AddRequiredArtifact(
            paths,
            "Harness/" + Path.GetFileName(typeof(RankerBenchmarkProvenance).Assembly.Location),
            typeof(RankerBenchmarkProvenance).Assembly.Location);
        AddRequiredArtifact(
            paths,
            "Application/" + Path.GetFileName(typeof(LocalDictionaryService).Assembly.Location),
            typeof(LocalDictionaryService).Assembly.Location);
        AddRequiredArtifact(
            paths,
            "Protocol/" + Path.GetFileName(typeof(RankerProtocolV2).Assembly.Location),
            typeof(RankerProtocolV2).Assembly.Location);

        string workerDirectory = Path.Combine(workerBaseDirectory, "Ranker");
        if (!Directory.Exists(workerDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The ranker worker directory is missing: '{workerDirectory}'.");
        }

        foreach (string path in Directory.EnumerateFiles(
                     workerDirectory,
                     "*",
                     SearchOption.AllDirectories)
                 .Where(path =>
                     path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            string relative = Path.GetRelativePath(workerDirectory, path)
                .Replace('\\', '/');
            AddRequiredArtifact(paths, "Worker/" + relative, path);
        }

        return paths
            .Select(item => new NamedArtifactPath(item.Key, item.Value))
            .ToArray();
    }

    private static string ReadPinnedDebertaModelFile(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException(
                $"The pinned DeBERTa manifest is missing: '{manifestPath}'.");
        }

        byte[] bytes = File.ReadAllBytes(manifestPath);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.LongLength !=
                GlossBertDictionarySelectorService.DebertaManifestLength ||
            !string.Equals(
                sha256,
                GlossBertDictionarySelectorService.DebertaManifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The DeBERTa report manifest does not match the pinned runtime " +
                $"bundle: '{manifestPath}'.");
        }

        using JsonDocument document = JsonDocument.Parse(bytes);
        string? modelFile = document.RootElement
            .GetProperty("modelFile")
            .GetString();
        if (string.IsNullOrWhiteSpace(modelFile) ||
            Path.IsPathRooted(modelFile) ||
            !string.Equals(Path.GetFileName(modelFile), modelFile, StringComparison.Ordinal) ||
            !modelFile.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The pinned DeBERTa manifest has an invalid modelFile: '{manifestPath}'.");
        }

        return modelFile;
    }

    private static bool IsDictionaryDataFile(string path) =>
        path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tsv.gz", StringComparison.OrdinalIgnoreCase);

    private static void AddRequiredArtifact(ISet<string> paths, string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "An active ranker artifact is missing while recording provenance.",
                path);
        }

        paths.Add(Path.GetFullPath(path));
    }

    private static void AddRequiredArtifact(
        IDictionary<string, string> paths,
        string name,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException(
                $"An executing benchmark artifact is missing: '{name}'.",
                path);
        }

        paths.Add(name, Path.GetFullPath(path));
    }

    private readonly record struct NamedArtifactPath(string Name, string Path);
}
