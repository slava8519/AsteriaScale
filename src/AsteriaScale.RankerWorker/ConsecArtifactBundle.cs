using System.Text.Json;

namespace AsteriaScale.RankerWorker;

internal sealed class ConsecArtifactBundle : IDisposable
{
    internal const string BundleDirectoryName = "Consec";
    internal const string ManifestFileName = "model-manifest.json";
    internal const string TokenizerFileName = "tokenizer.json";
    internal const string GraphRelativePath = "Cpu/consec-wngt-fp32.onnx";
    internal const string ExternalDataRelativePath = "Cpu/consec-wngt-fp32.onnx.data";
    internal const long ManifestLength = 4_407;
    internal const string ManifestSha256 =
        "13a8a14e3938d4f20519f7da79a51f75543eced352bd39456b5a43c4d93402b8";
    internal const long GraphLength = 843_194;
    internal const string GraphSha256 =
        "0eb0688f3fd2af67c6e2155daeed0fec5c5b2f1c7037b91ba526d50516c630ad";
    internal const long ExternalDataLength = 1_621_487_616;
    internal const string ExternalDataSha256 =
        "1257dc55ebec8886b7f5af4867f684ae44c7be4308e75b1251963595354011b9";
    internal const long TokenizerLength = 3_560_546;
    internal const string TokenizerSha256 =
        "df91c443c8eac73a42ff72086f25b16ad3b244ae2bf0303625298265d50f73f1";
    internal const string OfficialReferenceGoldenFileName =
        "consec-upstream-reference-golden.json";
    internal const long OfficialReferenceGoldenLength = 6_616;
    internal const string OfficialReferenceGoldenSha256 =
        "d6a5a3cdf6429bf4acde6bba5a254c209eff28f76c577ad7d845a7ba086ab22e";
    internal const string ModelCacheId =
        "ConSeC:WNGT:9602b5fd:" +
        "adapter-consec-wngt-positional-extractor-v1:" +
        "g-0eb0688f3fd2:d-1257dc55ebec:t-df91c443c8ea:" +
        "pretokenizer-v3:context-full-v1:s512";

    private static readonly ConsecArtifactSpec Manifest = new(
        ManifestFileName,
        ManifestLength,
        ManifestSha256);
    private static readonly ConsecArtifactSpec Tokenizer = new(
        TokenizerFileName,
        TokenizerLength,
        TokenizerSha256);
    private static readonly ConsecArtifactSpec Graph = new(
        GraphRelativePath,
        GraphLength,
        GraphSha256);
    private static readonly ConsecArtifactSpec ExternalData = new(
        ExternalDataRelativePath,
        ExternalDataLength,
        ExternalDataSha256);

    private RankerArtifactReadLease? _manifestLease;
    private RankerArtifactReadLease? _tokenizerLease;
    private RankerArtifactReadLease? _graphLease;
    private RankerArtifactReadLease? _externalDataLease;

    private ConsecArtifactBundle(
        string bundleDirectory,
        RankerArtifactReadLease manifestLease,
        RankerArtifactReadLease tokenizerLease,
        RankerArtifactReadLease graphLease,
        RankerArtifactReadLease externalDataLease)
    {
        BundleDirectory = bundleDirectory;
        _manifestLease = manifestLease;
        _tokenizerLease = tokenizerLease;
        _graphLease = graphLease;
        _externalDataLease = externalDataLease;
    }

    internal string BundleDirectory { get; }

    internal string TokenizerPath => _tokenizerLease?.Path ??
        throw new ObjectDisposedException(nameof(ConsecArtifactBundle));

    internal string GraphPath => _graphLease?.Path ??
        throw new ObjectDisposedException(nameof(ConsecArtifactBundle));

    internal byte[] ReadTokenizerBytes() =>
        (_tokenizerLease ?? throw new ObjectDisposedException(
            nameof(ConsecArtifactBundle))).ReadAllBytes();

    internal static ConsecArtifactBundle Open(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        string modelRoot = Path.GetFullPath(modelDirectory);
        string bundleDirectory = ResolveSafeArtifactPath(
            modelRoot,
            BundleDirectoryName,
            requireExistingLeaf: true,
            leafMustBeDirectory: true);

        RankerArtifactReadLease? manifestLease = null;
        RankerArtifactReadLease? tokenizerLease = null;
        RankerArtifactReadLease? graphLease = null;
        RankerArtifactReadLease? externalDataLease = null;
        try
        {
            manifestLease = AcquirePinnedArtifact(
                bundleDirectory,
                Manifest,
                "consec_manifest_missing",
                "consec_manifest_integrity_failed",
                "ConSeC model manifest");
            ValidateManifest(manifestLease.ReadAllBytes());

            tokenizerLease = AcquirePinnedArtifact(
                bundleDirectory,
                Tokenizer,
                "consec_tokenizer_missing",
                "consec_tokenizer_integrity_failed",
                "ConSeC tokenizer");
            graphLease = AcquirePinnedArtifact(
                bundleDirectory,
                Graph,
                "consec_model_missing",
                "consec_model_integrity_failed",
                "ConSeC ONNX graph");
            externalDataLease = AcquirePinnedArtifact(
                bundleDirectory,
                ExternalData,
                "consec_external_data_missing",
                "consec_external_data_integrity_failed",
                "ConSeC ONNX external data");

            var result = new ConsecArtifactBundle(
                bundleDirectory,
                manifestLease,
                tokenizerLease,
                graphLease,
                externalDataLease);
            manifestLease = null;
            tokenizerLease = null;
            graphLease = null;
            externalDataLease = null;
            return result;
        }
        finally
        {
            externalDataLease?.Dispose();
            graphLease?.Dispose();
            tokenizerLease?.Dispose();
            manifestLease?.Dispose();
        }
    }

    internal static RankerArtifactReadLease AcquirePinnedArtifact(
        string bundleDirectory,
        ConsecArtifactSpec artifact,
        string missingErrorCode,
        string integrityErrorCode,
        string description)
    {
        string path = ResolveSafeArtifactPath(
            bundleDirectory,
            artifact.RelativePath,
            requireExistingLeaf: false,
            leafMustBeDirectory: false);
        if (!File.Exists(path))
        {
            throw new RankerException(
                missingErrorCode,
                $"The pinned {description} is missing: '{path}'.");
        }

        RejectReparsePoint(path, description);
        return RankerArtifactIntegrity.AcquireReadLeaseAndValidate(
            path,
            artifact.Length,
            artifact.Sha256,
            integrityErrorCode,
            description);
    }

    private static string ResolveSafeArtifactPath(
        string bundleDirectory,
        string relativePath,
        bool requireExistingLeaf,
        bool leafMustBeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath))
        {
            throw UnsafePath(relativePath);
        }

        string[] segments = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.None);
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Contains(Path.AltDirectorySeparatorChar) ||
                segment.Contains(Path.DirectorySeparatorChar)))
        {
            throw UnsafePath(relativePath);
        }

        string root = Path.GetFullPath(bundleDirectory);
        string combined = Path.GetFullPath(Path.Combine([root, .. segments]));
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!string.Equals(combined, root, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw UnsafePath(relativePath);
        }

        if (Directory.Exists(root))
        {
            RejectReparsePoint(root, "ConSeC bundle directory");
        }
        else if (requireExistingLeaf)
        {
            throw new RankerException(
                "consec_bundle_missing",
                $"The ConSeC bundle directory is missing: '{root}'.");
        }

        string current = root;
        for (int index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            bool isLast = index == segments.Length - 1;
            if (Directory.Exists(current))
            {
                RejectReparsePoint(current, "ConSeC artifact directory");
            }
            else if (!isLast || (requireExistingLeaf && leafMustBeDirectory))
            {
                throw new RankerException(
                    "consec_bundle_missing",
                    $"A required ConSeC artifact directory is missing: '{current}'.");
            }
            else if (File.Exists(current))
            {
                RejectReparsePoint(current, "ConSeC artifact");
            }
        }

        return combined;
    }

    private static void RejectReparsePoint(string path, string description)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex)
        {
            throw new RankerException(
                "consec_artifact_path_unsafe",
                $"Could not inspect the {description} path '{path}': {ex.Message}",
                ex);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new RankerException(
                "consec_artifact_path_unsafe",
                $"The {description} must not be a reparse point: '{path}'.");
        }
    }

    private static RankerException UnsafePath(string? relativePath) => new(
        "consec_artifact_path_unsafe",
        $"The ConSeC artifact path is not a direct bundle-relative path: '{relativePath}'.");

    private static void ValidateManifest(byte[] json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            JsonElement root = document.RootElement;
            RequireExactProperties(
                root,
                "manifest",
                "adapterId",
                "artifacts",
                "baseModel",
                "checkpoint",
                "commercialUseAllowed",
                "environment",
                "license",
                "model",
                "parity",
                "schemaVersion",
                "tokenizer",
                "upstreamCommit",
                "upstreamRepository");
            RequireNumber(root, "schemaVersion", 1);
            RequireString(
                root,
                "adapterId",
                "consec-wngt-positional-extractor-v1");
            RequireString(root, "license", "CC-BY-NC-SA-4.0");
            RequireBoolean(root, "commercialUseAllowed", false);
            RequireString(
                root,
                "upstreamCommit",
                "9602b5fd69f57be08a186988d1df34fe4152b63f");

            JsonElement artifacts = root.GetProperty("artifacts");
            if (artifacts.ValueKind != JsonValueKind.Array ||
                artifacts.GetArrayLength() != 3)
            {
                throw InvalidManifest("artifacts must contain exactly three records");
            }

            ConsecArtifactSpec[] expected = [Graph, ExternalData, Tokenizer];
            int index = 0;
            foreach (JsonElement artifact in artifacts.EnumerateArray())
            {
                RequireExactProperties(artifact, $"artifacts[{index}]", "bytes", "file", "sha256");
                RequireString(artifact, "file", expected[index].RelativePath);
                RequireNumber(artifact, "bytes", expected[index].Length);
                RequireString(artifact, "sha256", expected[index].Sha256);
                index++;
            }

            JsonElement model = root.GetProperty("model");
            RequireString(model, "format", "ONNX external data");
            RequireString(model, "precision", "float32");
            RequireString(model, "output", "token_logits");
            JsonElement inputs = model.GetProperty("inputs");
            string[] expectedInputs =
                ["input_ids", "attention_mask", "token_type_ids", "relative_pos"];
            if (inputs.ValueKind != JsonValueKind.Array ||
                !inputs.EnumerateArray()
                    .Select(item => item.GetString())
                    .SequenceEqual(expectedInputs))
            {
                throw InvalidManifest("model.inputs does not match the pinned tensor ABI");
            }

            JsonElement tokenizer = root.GetProperty("tokenizer");
            JsonElement specialTokens = tokenizer.GetProperty("specialTokens");
            (string Token, long Id)[] expectedSpecialTokens =
            [
                ("<d>", 50_265),
                ("</d>", 50_266),
                ("DEF_SEP", 50_267),
                ("DEF_END", 50_268),
                ("CONTEXT_DEFS", 50_269)
            ];
            if (specialTokens.ValueKind != JsonValueKind.Array ||
                specialTokens.GetArrayLength() != expectedSpecialTokens.Length)
            {
                throw InvalidManifest("tokenizer.specialTokens has an unexpected size");
            }

            index = 0;
            foreach (JsonElement token in specialTokens.EnumerateArray())
            {
                RequireExactProperties(token, $"specialTokens[{index}]", "id", "token");
                RequireString(token, "token", expectedSpecialTokens[index].Token);
                RequireNumber(token, "id", expectedSpecialTokens[index].Id);
                index++;
            }

            JsonElement parity = root.GetProperty("parity");
            RequireString(parity, "scope", "adapted-pytorch-to-onnx");
            RequireString(parity, "provider", "CPUExecutionProvider");
            RequireBoolean(
                parity,
                "officialUpstreamEndToEndReferenceVerified",
                false);
            ValidateFixturePins(parity.GetProperty("fixtures"));
        }
        catch (RankerException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new RankerException(
                "consec_manifest_invalid",
                $"The authenticated ConSeC manifest is invalid: {ex.Message}",
                ex);
        }
    }

    private static void ValidateFixturePins(JsonElement fixtures)
    {
        (string Id, string Sha256, int Length, int CandidateCount)[] expected =
        [
            (
                "dialogue-feedback-bank",
                "c20dc93bcdd1daa2b005dbcc038f30455cc1265707fa76867c91ed588cfe91a8",
                36,
                2),
            (
                "scientific-acronym-pcr-strain",
                "efc5bf2cee347d30941cf6c07cc31c50ff4b176e98ebee8746dd846fc21f81b3",
                61,
                3)
        ];
        if (fixtures.ValueKind != JsonValueKind.Array ||
            fixtures.GetArrayLength() != expected.Length)
        {
            throw InvalidManifest("parity.fixtures has an unexpected size");
        }

        int index = 0;
        foreach (JsonElement fixture in fixtures.EnumerateArray())
        {
            RequireExactProperties(
                fixture,
                $"parity.fixtures[{index}]",
                "candidateCount",
                "hasFeedbackContext",
                "id",
                "inputSha256",
                "sequenceLength");
            RequireString(fixture, "id", expected[index].Id);
            RequireString(fixture, "inputSha256", expected[index].Sha256);
            RequireNumber(fixture, "sequenceLength", expected[index].Length);
            RequireNumber(fixture, "candidateCount", expected[index].CandidateCount);
            RequireBoolean(fixture, "hasFeedbackContext", true);
            index++;
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        string location,
        params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidManifest($"{location} must be an object");
        }

        var expected = names.ToHashSet(StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !observed.Add(property.Name))
            {
                throw InvalidManifest(
                    $"{location} contains an unknown or duplicate property '{property.Name}'");
            }
        }

        if (!observed.SetEquals(expected))
        {
            throw InvalidManifest($"{location} does not contain the exact pinned property set");
        }
    }

    private static void RequireString(
        JsonElement element,
        string property,
        string expected)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
        {
            throw InvalidManifest($"{property} does not match the pinned value");
        }
    }

    private static void RequireNumber(
        JsonElement element,
        string property,
        long expected)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            !value.TryGetInt64(out long actual) ||
            actual != expected)
        {
            throw InvalidManifest($"{property} does not match the pinned value");
        }
    }

    private static void RequireBoolean(
        JsonElement element,
        string property,
        bool expected)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            value.GetBoolean() != expected)
        {
            throw InvalidManifest($"{property} does not match the pinned value");
        }
    }

    private static RankerException InvalidManifest(string detail) => new(
        "consec_manifest_invalid",
        $"The authenticated ConSeC manifest is incompatible: {detail}.");

    public void Dispose()
    {
        _externalDataLease?.Dispose();
        _externalDataLease = null;
        _graphLease?.Dispose();
        _graphLease = null;
        _tokenizerLease?.Dispose();
        _tokenizerLease = null;
        _manifestLease?.Dispose();
        _manifestLease = null;
    }
}

internal readonly record struct ConsecArtifactSpec(
    string RelativePath,
    long Length,
    string Sha256);
