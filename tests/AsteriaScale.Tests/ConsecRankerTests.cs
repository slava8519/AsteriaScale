using System.Security.Cryptography;
using AsteriaScale.RankerWorker;

internal static class ConsecRankerTests
{
    public static Task RunAsync(string tempRoot)
    {
        VerifyDialogueGoldenFixture();
        VerifyOfficialDefinitionCapitalization();
        VerifyLowercaseUnicodeDefinitionCasing();
        VerifyUnsupportedUnicodeCasingFailsClosed();
        VerifyInstalledTokenizerAgainstGoldenIfAvailable();
        VerifyContextPretokenizer();
        VerifySequenceOverflowIsExplicit();
        VerifyMarkerSoftmaxAndStableGroupReduction();
        VerifyExternalDataIsFailClosed(tempRoot);
        VerifyCpuOnlyWorkerOption();
        return Task.CompletedTask;
    }

    private static void VerifyLowercaseUnicodeDefinitionCasing()
    {
        var tokenizer = new RecordingPieceTokenizer();
        _ = new ConsecInputBuilder(tokenizer)
            .BuildFromTokens(
                ["do"],
                targetIndex: 0,
                [
                    "A syllable used in solfège to represent the first and eighth tonic of a major scale."
                ],
                []);

        Check(tokenizer.EncodedPieces.Contains(
                "DEF_SEP A syllable used in solfège to represent the first and eighth tonic of a major scale.",
                StringComparer.Ordinal),
            "ConSeC rejected or changed an already-lowercase Unicode letter in an English definition.");
    }

    private static void VerifyDialogueGoldenFixture()
    {
        ConsecInput input = new ConsecInputBuilder(CreateGoldenTokenizer())
            .BuildFromTokens(
                ["She", "will", "bank", "on", "his", "support", "."],
                targetIndex: 2,
                [
                    "a financial institution that accepts deposits",
                    "to rely on someone or something"
                ],
                [
                    new ConsecFeedbackDefinition(
                        "help or encouragement given to someone",
                        5)
                ]);
        Check(input.SequenceLength == 36,
            "The dialogue ConSeC fixture changed sequence length.");
        Check(input.MarkerPositions.SequenceEqual([11, 19]),
            "The dialogue ConSeC fixture changed DEF_SEP marker positions.");
        Check(
            ConsecInputBuilder.ComputeTensorBundleSha256(input) ==
                "c20dc93bcdd1daa2b005dbcc038f30455cc1265707fa76867c91ed588cfe91a8",
            "The dialogue ConSeC tensors diverged from the official upstream golden.");
    }

    private static void VerifyOfficialDefinitionCapitalization()
    {
        var tokenizer = new RecordingPieceTokenizer();
        _ = new ConsecInputBuilder(tokenizer)
            .BuildFromTokens(
                ["The", "PCR", "assay", "identified", "a", "resistant", "strain", "."],
                targetIndex: 6,
                [
                    "a genetic variant or subtype of a microorganism",
                    "physical deformation caused by stress",
                    "an injury caused by excessive effort"
                ],
                [
                    new ConsecFeedbackDefinition(
                        "PCR DNA laboratory test",
                        1),
                    new ConsecFeedbackDefinition(
                        "a procedure that detects a target in a sample",
                        2)
                ]);
        Check(
            tokenizer.EncodedPieces.Contains(
                "DEF_SEP Pcr dna laboratory test.",
                StringComparer.Ordinal) &&
            !tokenizer.EncodedPieces.Contains(
                "DEF_SEP PCR DNA laboratory test.",
                StringComparer.Ordinal),
            "ConSeC did not reproduce Python str.capitalize() for scientific definitions.");

        tokenizer.EncodedPieces.Clear();
        _ = new ConsecInputBuilder(tokenizer)
            .BuildFromTokens(
                ["test"],
                targetIndex: 0,
                [" PCR DNA", "...PCR DNA"],
                []);
        Check(
            tokenizer.EncodedPieces.Contains(
                "DEF_SEP  pcr dna.",
                StringComparer.Ordinal) &&
            tokenizer.EncodedPieces.Contains(
                "DEF_SEP pcr dna.",
                StringComparer.Ordinal),
            "ConSeC changed Python capitalize/strip order or trimmed leading whitespace.");
    }

    private static void VerifyUnsupportedUnicodeCasingFailsClosed()
    {
        foreach (string definition in new[]
                 {
                     "ßETA expansion",
                     "ǳETA titlecase",
                     "ΟΣ contextual lowercase"
                 })
        {
            try
            {
                _ = new ConsecInputBuilder(new RecordingPieceTokenizer())
                    .BuildFromTokens(
                        ["test"],
                        targetIndex: 0,
                        [definition],
                        []);
                throw new InvalidOperationException(
                    $"ConSeC accepted unsupported Unicode casing in '{definition}'.");
            }
            catch (RankerException ex) when (
                ex.ErrorCode == "consec_definition_casing_unsupported")
            {
                // Fail-closed validation is the parity contract for English
                // definitions outside the pinned ASCII casing domain.
            }
        }
    }

    private static void VerifyInstalledTokenizerAgainstGoldenIfAvailable()
    {
        string? root = FindRepositoryRoot();
        if (root is null)
        {
            return;
        }

        string path = Path.Combine(
            root,
            "src",
            "AsteriaScale.RankerWorker",
            "Models",
            "Consec",
            "tokenizer.json");
        if (!File.Exists(path))
        {
            return;
        }

        byte[] json = RankerArtifactIntegrity.ReadAllBytesAndValidate(
            path,
            ConsecArtifactBundle.TokenizerLength,
            ConsecArtifactBundle.TokenizerSha256,
            "consec_tokenizer_integrity_failed",
            "test ConSeC tokenizer");
        using var tokenizer = new ConsecTokenizer(path, json);
        var builder = new ConsecInputBuilder(tokenizer);
        ConsecInput input = builder.BuildFromTokens(
            ["She", "will", "bank", "on", "his", "support", "."],
            targetIndex: 2,
            [
                "a financial institution that accepts deposits",
                "to rely on someone or something"
            ],
            [
                new ConsecFeedbackDefinition(
                    "help or encouragement given to someone",
                    5)
            ]);
        Check(
            ConsecInputBuilder.ComputeTensorBundleSha256(input) ==
                "c20dc93bcdd1daa2b005dbcc038f30455cc1265707fa76867c91ed588cfe91a8",
            "Tokenizers.DotNet does not reproduce the official ConSeC preprocessing.");

        ConsecInput scientific = builder.BuildFromTokens(
            ["The", "PCR", "assay", "identified", "a", "resistant", "strain", "."],
            targetIndex: 6,
            [
                "a genetic variant or subtype of a microorganism",
                "physical deformation caused by stress",
                "an injury caused by excessive effort"
            ],
            [
                new ConsecFeedbackDefinition("PCR DNA laboratory test", 1),
                new ConsecFeedbackDefinition(
                    "a procedure that detects a target in a sample",
                    2)
            ]);
        Check(
            scientific.SequenceLength == 61 &&
            scientific.MarkerPositions.SequenceEqual([12, 25, 33]) &&
            ConsecInputBuilder.ComputeTensorBundleSha256(scientific) ==
                "efc5bf2cee347d30941cf6c07cc31c50ff4b176e98ebee8746dd846fc21f81b3",
            "The installed tokenizer diverged from the mixed-case scientific golden.");
    }

    private static void VerifyContextPretokenizer()
    {
        const string context =
            "He can't re-enter the bank; he won't re-enter the bank.";
        int second = context.LastIndexOf("bank", StringComparison.Ordinal);
        ConsecResolvedContext resolved = ConsecInputBuilder.ResolveContext(
            context,
            "bank",
            second + 2);
        Check(
            resolved.Tokens.SequenceEqual(
            [
                "He", "can't", "re-enter", "the", "bank", ";",
                "he", "won't", "re-enter", "the", "bank", "."
            ]),
            "ConSeC pretokenization broke punctuation, contractions, or hyphenated words.");
        Check(resolved.TargetIndex == 10,
            "ConSeC did not anchor the selected repeated target occurrence.");

        resolved = ConsecInputBuilder.ResolveContext(
            "A state-of-the-art assay was re-run.",
            "state-of-the-art",
            4);
        Check(
            resolved.Tokens.SequenceEqual(
                ["A", "state-of-the-art", "assay", "was", "re-run", "."]) &&
            resolved.TargetIndex == 1,
            "ConSeC did not preserve an explicitly selected hyphenated target.");
    }

    private static void VerifySequenceOverflowIsExplicit()
    {
        string[] sentence = Enumerable.Range(0, 510)
            .Select(index => "w" + index)
            .ToArray();
        try
        {
            _ = new ConsecInputBuilder(new RepeatingPieceTokenizer())
                .BuildFromTokens(
                    sentence,
                    targetIndex: 0,
                    ["one definition"],
                    []);
            throw new InvalidOperationException(
                "ConSeC silently accepted an input beyond its 512-token limit.");
        }
        catch (RankerException ex) when (
            ex.ErrorCode == "consec_sequence_too_long" &&
            ex.Message.Contains("No truncation", StringComparison.Ordinal))
        {
            // The target, context and candidate inventory are indivisible here.
        }
    }

    private static void VerifyMarkerSoftmaxAndStableGroupReduction()
    {
        float[] logits = [0, MathF.Log(4), 0, 0, 0];
        ConsecSelection accepted = ConsecScorer.Select(
            logits,
            [1, 3],
            [7, 8]);
        Check(
            accepted.GroupId == 7 &&
            !accepted.Abstained &&
            Math.Abs(accepted.Margin - 0.6) < 0.000001,
            "ConSeC did not softmax marker logits before applying the group margin.");

        ConsecSelection abstained = ConsecScorer.Select(
            [0, MathF.Log(1.5f), 0, 0, 0],
            [1, 3],
            [7, 8]);
        Check(
            abstained.GroupId is null && abstained.Abstained &&
            abstained.Margin < 0.25,
            "ConSeC used an acceptance rule other than the shared 0.25 group margin.");

        IReadOnlyList<GroupScore> groups = ConsecScorer.AggregateGroups(
            [0.25f, 0.45f, 0.45f, 0.35f],
            [20, 10, 20, 30]);
        Check(groups.Select(group => group.GroupId).SequenceEqual([20, 10, 30]),
            "ConSeC did not max-reduce duplicate GroupIds with stable tie ordering.");
    }

    private static void VerifyExternalDataIsFailClosed(string tempRoot)
    {
        string bundle = Path.Combine(
            tempRoot,
            "ConsecArtifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(bundle, "Cpu"));
        string path = Path.Combine(bundle, "Cpu", "test.onnx.data");
        byte[] expected = [1, 3, 3, 7];
        string sha256 = Convert.ToHexString(SHA256.HashData(expected))
            .ToLowerInvariant();
        var spec = new ConsecArtifactSpec(
            "Cpu/test.onnx.data",
            expected.LongLength,
            sha256);

        ExpectRankerError(
            "consec_external_data_missing",
            () => ConsecArtifactBundle.AcquirePinnedArtifact(
                bundle,
                spec,
                "consec_external_data_missing",
                "consec_external_data_integrity_failed",
                "test external data"));

        File.WriteAllBytes(path, [1, 3, 3, 8]);
        ExpectRankerError(
            "consec_external_data_integrity_failed",
            () => ConsecArtifactBundle.AcquirePinnedArtifact(
                bundle,
                spec,
                "consec_external_data_missing",
                "consec_external_data_integrity_failed",
                "test external data"));

        File.WriteAllBytes(path, expected);
        using RankerArtifactReadLease lease =
            ConsecArtifactBundle.AcquirePinnedArtifact(
                bundle,
                spec,
                "consec_external_data_missing",
                "consec_external_data_integrity_failed",
                "test external data");
        Check(lease.ReadAllBytes().SequenceEqual(expected),
            "The authenticated ConSeC external-data lease changed bytes.");

        ExpectRankerError(
            "consec_artifact_path_unsafe",
            () => ConsecArtifactBundle.AcquirePinnedArtifact(
                bundle,
                spec with { RelativePath = "../outside.onnx.data" },
                "consec_external_data_missing",
                "consec_external_data_integrity_failed",
                "test external data"));
    }

    private static void VerifyCpuOnlyWorkerOption()
    {
        Check(
            WorkerOptions.TryParse(
                ["--provider", "cpu", "--cpu-model", "consec"],
                out WorkerOptions? options,
                out string? error) &&
            error is null &&
            options is
            {
                Provider: RankerProvider.Cpu,
                CpuModel: CpuRankerModel.Consec
            },
            "The explicit ConSeC CPU worker profile was not parsed.");
        Check(
            !WorkerOptions.TryParse(
                ["--provider", "npu", "--cpu-model", "consec"],
                out _,
                out error) &&
            error?.Contains("CPU-only", StringComparison.Ordinal) == true,
            "The worker accepted ConSeC on the NPU provider.");
    }

    private static MapPieceTokenizer CreateGoldenTokenizer() => new(
        new Dictionary<string, long[]>(StringComparer.Ordinal)
        {
            ["She"] = [264],
            ["will"] = [40],
            ["<d> bank </d>"] = [50_265, 827, 50_266],
            ["on"] = [15],
            ["his"] = [39],
            ["support"] = [323],
            ["."] = [479],
            ["DEF_SEP A financial institution that accepts deposits."] =
                [50_267, 83, 613, 6_786, 14, 14_564, 8_874, 4],
            ["DEF_SEP To rely on someone or something."] =
                [50_267, 598, 5_864, 15, 951, 50, 402, 4],
            ["DEF_SEP Help or encouragement given to someone."] =
                [50_267, 10_310, 50, 18_197, 576, 7, 951, 4]
        });

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AsteriaScale.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static void ExpectRankerError(
        string errorCode,
        Func<IDisposable> action)
    {
        try
        {
            using IDisposable _ = action();
            throw new InvalidOperationException(
                $"Expected ranker error '{errorCode}' was not raised.");
        }
        catch (RankerException ex) when (ex.ErrorCode == errorCode)
        {
            // The exact failure mode participates in worker diagnostics.
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class MapPieceTokenizer(
        IReadOnlyDictionary<string, long[]> pieces) : IConsecPieceTokenizer
    {
        public long ClassifierTokenId => 1;

        public long SeparatorTokenId => 2;

        public long DefinitionMarkerTokenId => 50_267;

        public IReadOnlyList<long> EncodePiece(string text) =>
            pieces.TryGetValue(text, out long[]? ids)
                ? ids
                : throw new InvalidOperationException(
                    $"Golden ConSeC fixture has no token ids for '{text}'.");
    }

    private sealed class RepeatingPieceTokenizer : IConsecPieceTokenizer
    {
        public long ClassifierTokenId => 1;

        public long SeparatorTokenId => 2;

        public long DefinitionMarkerTokenId => 50_267;

        public IReadOnlyList<long> EncodePiece(string text) =>
            text.StartsWith("<d>", StringComparison.Ordinal)
                ? [50_265, 999, 50_266]
                : text.StartsWith("DEF_SEP", StringComparison.Ordinal)
                    ? [50_267, 999]
                    : [999];
    }

    private sealed class RecordingPieceTokenizer : IConsecPieceTokenizer
    {
        internal List<string> EncodedPieces { get; } = [];

        public long ClassifierTokenId => 1;

        public long SeparatorTokenId => 2;

        public long DefinitionMarkerTokenId => 50_267;

        public IReadOnlyList<long> EncodePiece(string text)
        {
            EncodedPieces.Add(text);
            return text.StartsWith("<d>", StringComparison.Ordinal)
                ? [50_265, 999, 50_266]
                : text.StartsWith("DEF_SEP", StringComparison.Ordinal)
                    ? [50_267, 999]
                    : [999];
        }
    }
}
