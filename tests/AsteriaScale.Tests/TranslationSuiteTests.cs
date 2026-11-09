using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AsteriaScale.RankerProtocol;
using AsteriaScale.Services;

internal static class TranslationSuiteTests
{
    internal static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "translation-suite-runner");
        string worker = Path.Combine(root, "worker");
        Directory.CreateDirectory(worker);
        string dataset = Path.Combine(root, "canonical-suite.jsonl");
        string output = Path.Combine(root, "system-output.jsonl");
        string report = Path.Combine(root, "system-report.json");
        await File.WriteAllTextAsync(
            dataset,
            string.Join('\n', CanonicalFixtureLines()) + '\n',
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string[] commandLine =
        [
            "--benchmark-only",
            "--translation-suite", dataset, worker,
            "--system-id", "fake-smart-v1",
            "--profile", "smart",
            "--phrase-backend", "cpu",
            "--ranker-cpu-model", "consec",
            "--translation-output", output,
            "--translation-report", report,
            "--max-cases", "4",
            "--shard", "1/1"
        ];
        TranslationSuiteOptions options = TranslationSuiteOptions.Parse(commandLine, 1);
        Check(options.SystemId == "fake-smart-v1" &&
              options.Profile == TranslationSuiteProfile.Smart &&
              options.PhraseBackend == TranslateGemmaExecutionBackend.Cpu &&
              options.RankerCpuModel == RankerCpuModelProfile.Consec &&
              options.MaximumCases == 4 &&
              options.ShardIndex == 1 &&
              options.ShardCount == 1,
            "The strict translation-suite parser lost a required option.");
        Check(options.OutputRankerCpuModelId == "consec" &&
              (options with { Profile = TranslationSuiteProfile.Direct })
                  .OutputRankerCpuModelId is null,
            "Direct translation provenance must not claim an unused ranker.");

        VerifyParserRejectsInvalidTails(dataset, worker, output, report);
        VerifyCoopmatOption(dataset, worker, output, report);
        VerifyCachePromptOption(dataset, worker, output, report);
        await VerifySchemaRejectionsAsync(root);

        IReadOnlyList<TranslationSuiteCase> loaded =
            await TranslationSuiteRunner.LoadDatasetAsync(dataset);
        Check(loaded.Count == 4 && loaded[0].References.Count == 1 &&
              loaded[0].PreviousContext.Count == 1 &&
              loaded[0].Speaker?.Gender == SpeakerGender.Feminine &&
              loaded[0].Focus is { Annotated: true, CharacterIndex: 0 },
            "The runner did not accept the canonical reporting/fetcher fixture.");
        Check(loaded[1].ReferenceFree && loaded[1].References.Count == 0,
            "A declared reference-free case was not accepted.");

        TranslationSuiteFocus inferred = TranslationSuiteRunner.ResolveFocus(loaded[1]);
        Check(inferred.Text == "Hello" && inferred.CharacterIndex == 0 && !inferred.Annotated,
            "The deterministic content-token focus was not stable.");
        TranslationSuiteCase oversizedContext = loaded[0] with
        {
            PreviousContext =
            [
                new string('x', PreviousContextLimits.MaximumCharacters + 1)
            ]
        };
        Check(TranslationSuiteRunner.IsSourceWithinModelGuard(oversizedContext) &&
              !TranslationSuiteRunner.IsWithinModelGuard(oversizedContext),
            "The direct source guard incorrectly inherited the Smart context budget.");
        Check(!TranslationSuiteRunner.IsWithinModelGuard(loaded[0] with
              {
                  Source = new string('a', SmartPhraseContextBuilder.MaximumCharacters + 1),
                  PreviousContext = [new string('x', 100_000), new string('y', 100_000)]
              }),
            "An oversized source/context escaped the per-case model guard.");
        IReadOnlyList<TranslationSuiteCase> secondShard =
            TranslationSuiteRunner.SelectCases(
                loaded,
                options with { MaximumCases = null, ShardIndex = 2, ShardCount = 2 });
        Check(secondShard.Select(item => item.Id).SequenceEqual(
                  ["case-reference-free", "case-error"],
                  StringComparer.Ordinal),
            "Translation-suite sharding is not deterministic by dataset order.");

        FakeTranslationSuiteSystem? fake = null;
        TranslationSuiteReport run = await TranslationSuiteRunner.RunAsync(
            options,
            parsed => fake = new FakeTranslationSuiteSystem(parsed));
        Check(fake is { Prepared: true, Disposed: true },
            "The injected translation-suite system lifecycle was incomplete.");
        Check(run.Summary is { Cases: 4, Ok: 2, Unavailable: 1, Errors: 1 } &&
              run.Summary.CacheHits == 1 &&
              run.Summary.Coverage == 0.5,
            "The translation-suite summary did not preserve per-case outcomes.");
        Check(run.SelectedCaseIds.SequenceEqual(
                  ["case-rich", "case-reference-free", "case-unavailable", "case-error"],
                  StringComparer.Ordinal) &&
              IsLowerHexSha256(run.SelectionSha256),
            "The report did not pin the exact selected ids and selection hash.");
        Check(run.Provenance.HarnessArtifact.Name ==
                  Path.GetFileName(typeof(TranslationSuiteRunner).Assembly.Location),
            "The report did not pin the executing benchmark harness.");
        Check(File.Exists(output) && File.Exists(report),
            "The runner did not write both requested artifacts.");
        Check(!Directory.EnumerateFiles(root, "*.tmp-*", SearchOption.TopDirectoryOnly).Any(),
            "An atomic translation-suite write left a temporary artifact behind.");
        VerifySmartMetadataEvidenceContract();

        string[] outputLines = (await File.ReadAllLinesAsync(output))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        Check(outputLines.Length == 4, "The JSONL output cardinality changed.");
        using JsonDocument firstOutput = JsonDocument.Parse(outputLines[0]);
        JsonElement first = firstOutput.RootElement;
        Check(first.GetProperty("id").GetString() == "case-rich" &&
              first.GetProperty("status").GetString() == "ok" &&
              first.GetProperty("sourceSha256").GetString() == loaded[0].SourceSha256 &&
              first.GetProperty("selectionSha256").GetString() == run.SelectionSha256 &&
              first.GetProperty("systemId").GetString() == "fake-smart-v1" &&
              first.GetProperty("profile").GetString() == "smart" &&
              first.GetProperty("phraseBackend").GetString() == "cpu" &&
              first.GetProperty("rankerCpuModel").GetString() == "consec" &&
              first.GetProperty("modelIdentity").GetString() == "fake-translation-model" &&
              first.GetProperty("promptIdentity").GetString() == "fake-prompt-v1" &&
              first.GetProperty("focus").GetProperty("text").GetString() == "Wait" &&
              first.GetProperty("latencyMilliseconds").GetInt64() >= 0,
            "The C# output no longer matches the reporting CLI contract.");
        Check(first.GetProperty("rejectionReason").ValueKind == JsonValueKind.Null,
            "An accepted translation carried an output-guard verdict.");
        using JsonDocument unavailableOutput = JsonDocument.Parse(outputLines[2]);
        // The scorer names guard verdicts in camelCase alongside `status`, so
        // this line is the contract seam between the two languages: the Python
        // run below refuses an unknown or misspelled reason outright.
        Check(unavailableOutput.RootElement
                  .GetProperty("rejectionReason")
                  .GetString() == "residualEnglish",
            "The serialized guard verdict drifted from the scorer contract.");
        await VerifyReportingCliContractAsync(dataset, output, report, root);

        await VerifyDirectProvenanceAsync(options, root);
        await VerifyDatasetSnapshotAndSelectionAsync(options, root);
        await VerifyPrepareFailureDisposesAsync(options, root);
        await VerifyBestEffortCleanupAsync();

        TranslationSuiteCaseOutput wrongSource = run.Cases[0] with
        {
            SourceSha256 = new string('0', 64)
        };
        TranslationSuiteCaseOutput[] mismatched = run.Cases.ToArray();
        mismatched[0] = wrongSource;
        ExpectThrows<InvalidDataException>(
            () => TranslationSuiteRunner.ValidateDatasetOutputMatch(
                loaded,
                mismatched,
                run.SelectionSha256),
            "Dataset/output matching accepted a different source hash.");
        ExpectThrows<InvalidOperationException>(
            () => TranslationSuiteRunner.RejectFrozenHoldout("never-open-holdout.jsonl"),
            "The public runner accepted a frozen holdout path.");
        Check(TranslationSuiteRunner.Percentile([1, 2, 3, 100], 0.50) == 2 &&
              TranslationSuiteRunner.Percentile([1, 2, 3, 100], 0.95) == 100,
            "Pinned nearest-rank latency percentiles changed.");
    }

    private static void VerifySmartMetadataEvidenceContract()
    {
        var selected = new ScaffoldSenseHint(
            "bank",
            "a financial institution",
            "банк",
            ["берег"],
            0.91);
        var dictionary = new SmartHoverResult(
            "bank",
            "банк",
            "CPU",
            DictionaryFound: true,
            IsPhrase: false,
            Language: "en",
            Confidence: 0.91)
        {
            SelectedSenseHints = [selected]
        };
        var translated = new SmartHoverPhraseLine(
            "The bank approved it.",
            "Банк одобрил это.",
            "TG4B",
            SmartPhraseLineStatus.Translated,
            FromCache: false)
        {
            SenseHints = [selected],
            AppliedSenseHints = [selected]
        };

        TranslationSuiteSmartMetadata accepted =
            SmartTranslationSuiteSystem.CreateSmartMetadata(dictionary, translated);
        Check(accepted.SenseEvidence.Count == 1 &&
              accepted.AppliedSenseEvidence.Count == 1 &&
              accepted.AppliedSenseEvidenceSha256 == accepted.SenseEvidenceSha256 &&
              IsLowerHexSha256(accepted.AppliedSenseEvidenceSha256!),
            "Accepted phrase metadata lost the selected sense hints used by translation.");

        TranslationSuiteSmartMetadata directRoute =
            SmartTranslationSuiteSystem.CreateSmartMetadata(
                dictionary,
                translated with
                {
                    AppliedSenseHints = Array.Empty<ScaffoldSenseHint>()
                });
        // The direct route applied nothing, and that is a positive fact: it is
        // fingerprinted as the empty list, never as null. Null would make an
        // accepted direct translation indistinguishable from an output that
        // produced no translation at all, which is what `unavailable` below is.
        const string emptyEvidenceSha256 =
            "b47671aaa3a9798ac4cffcde3bb8eb8794acc37d6b09ed3f2ec8c87782f3903c";
        Check(directRoute.SenseEvidence.Count == 1 &&
              directRoute.AppliedSenseEvidence.Count == 0 &&
              directRoute.AppliedSenseEvidenceSha256 == emptyEvidenceSha256 &&
              directRoute.AppliedSenseEvidenceSha256 != directRoute.SenseEvidenceSha256,
            "A selected direct candidate falsely claimed that WSD evidence reached generation.");

        var unavailable = translated with
        {
            Translation = string.Empty,
            Status = SmartPhraseLineStatus.Unavailable,
            SenseHints = Array.Empty<ScaffoldSenseHint>(),
            AppliedSenseHints = Array.Empty<ScaffoldSenseHint>()
        };
        TranslationSuiteSmartMetadata rejected =
            SmartTranslationSuiteSystem.CreateSmartMetadata(dictionary, unavailable);
        Check(rejected.SenseEvidence.Count == 1 &&
              rejected.AppliedSenseEvidence.Count == 0 &&
              rejected.AppliedSenseEvidenceSha256 is null,
            "Unavailable phrase metadata claimed that sense evidence reached an accepted translation.");

        var equivalent = selected with
        {
            RejectedTranslations = ["побережье", "берег"],
            Confidence = 0.99
        };
        TranslationSuiteSmartMetadata ignoredFields =
            SmartTranslationSuiteSystem.CreateSmartMetadata(
                dictionary,
                translated with
                {
                    SenseHints = [equivalent],
                    AppliedSenseHints = [equivalent]
                });
        Check(ignoredFields.AppliedSenseEvidenceSha256 ==
              accepted.AppliedSenseEvidenceSha256,
            "Confidence or rejected surfaces leaked into prompt-visible sense evidence identity.");

        // Audit card E9. Confidence and origin are reported for analysis and
        // must stay outside the fingerprint: the hash says what the prompt saw,
        // and a run that differs only in diagnostics has to remain comparable.
        var diagnosed = selected with
        {
            Confidence = 0.42,
            Origin = SenseEvidenceOrigin.RankerAccepted
        };
        TranslationSuiteSmartMetadata diagnostics =
            SmartTranslationSuiteSystem.CreateSmartMetadata(
                dictionary,
                translated with
                {
                    SenseHints = [diagnosed],
                    AppliedSenseHints = [diagnosed]
                });
        Check(diagnostics.SenseEvidence[0].Confidence == 0.42 &&
              diagnostics.SenseEvidence[0].Origin == SenseEvidenceOrigin.RankerAccepted &&
              diagnostics.AppliedSenseEvidence[0].Origin ==
              SenseEvidenceOrigin.RankerAccepted,
            "Hint confidence or origin did not reach benchmark telemetry.");
        Check(diagnostics.SenseEvidenceSha256 == accepted.SenseEvidenceSha256 &&
              diagnostics.AppliedSenseEvidenceSha256 ==
              accepted.AppliedSenseEvidenceSha256,
            "Reported hint diagnostics changed the prompt-visible evidence identity.");

        var emojiHint = new ScaffoldSenseHint(
            "😀",
            "smile 😀",
            "translation",
            Array.Empty<string>(),
            1);
        TranslationSuiteSmartMetadata emojiMetadata =
            SmartTranslationSuiteSystem.CreateSmartMetadata(
                dictionary,
                translated with
                {
                    SenseHints = [emojiHint],
                    AppliedSenseHints = [emojiHint]
                });
        Check(emojiMetadata.AppliedSenseEvidenceSha256 ==
              "6f53d613312c5e45bedb95a2e9ac4e724561255a2b81a5bb38e039af6b646b0b",
            "C# smart evidence hash no longer matches the UTF-16 cross-runtime contract.");
    }

    private static async Task VerifyDirectProvenanceAsync(
        TranslationSuiteOptions options,
        string root)
    {
        TranslationSuiteOptions direct = options with
        {
            SystemId = "fake-direct-v1",
            Profile = TranslationSuiteProfile.Direct,
            TranslationOutputPath = Path.Combine(root, "direct-output.jsonl"),
            TranslationReportPath = Path.Combine(root, "direct-report.json")
        };
        TranslationSuiteReport report = await TranslationSuiteRunner.RunAsync(
            direct,
            parsed => new FakeTranslationSuiteSystem(parsed));
        Check(report.RankerCpuModel is null &&
              report.Provenance.RankerModelVersion is null &&
              report.Provenance.RankerProvider is null &&
              report.Provenance.AbstentionTag is null &&
              report.Provenance.HarnessArtifact.Bytes > 0,
            "Direct provenance still claims ranker state or omits the harness.");
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(direct.TranslationReportPath));
        Check(document.RootElement.GetProperty("rankerCpuModel").ValueKind ==
                  JsonValueKind.Null &&
              document.RootElement.GetProperty("provenance")
                  .GetProperty("abstentionTag").ValueKind == JsonValueKind.Null,
            "Serialized direct provenance does not preserve not-applicable ranker fields.");
    }

    private static async Task VerifyDatasetSnapshotAndSelectionAsync(
        TranslationSuiteOptions options,
        string root)
    {
        string path = Path.Combine(root, "mutation-suite.jsonl");
        byte[] original = Encoding.UTF8.GetBytes(
            string.Join('\n', CanonicalFixtureLines()) + '\n');
        await File.WriteAllBytesAsync(path, original);
        string expectedSha = Convert.ToHexString(SHA256.HashData(original));
        TranslationSuiteOptions mutation = options with
        {
            DatasetPath = path,
            TranslationOutputPath = Path.Combine(root, "mutation-output.jsonl"),
            TranslationReportPath = Path.Combine(root, "mutation-report.json")
        };
        TranslationSuiteReport report = await TranslationSuiteRunner.RunAsync(
            mutation,
            parsed => new FakeTranslationSuiteSystem(
                parsed,
                prepareAction: () => File.WriteAllTextAsync(
                    path,
                    "{\"id\":\"changed\",\"source\":\"Changed.\",\"references\":[\"Изменено.\"]}\n")));
        Check(report.Dataset.Sha256 == expectedSha &&
              report.Dataset.Bytes == original.LongLength &&
              report.DatasetCases == CanonicalFixtureLines().Length,
            "The report pinned dataset bytes written after the evaluated snapshot.");

        string semanticA = Path.Combine(root, "semantic-a.jsonl");
        string semanticB = Path.Combine(root, "semantic-b.jsonl");
        await File.WriteAllTextAsync(
            semanticA,
            "{\"id\":\"same\",\"source\":\"Same source.\",\"references\":[\"Один.\"],\"previousContext\":[\"First.\"]}\n");
        await File.WriteAllTextAsync(
            semanticB,
            "{\"id\":\"same\",\"source\":\"Same source.\",\"references\":[\"Два.\"],\"previousContext\":[\"Second.\"]}\n");
        TranslationSuiteDatasetSnapshot first =
            await TranslationSuiteRunner.LoadDatasetSnapshotAsync(semanticA);
        TranslationSuiteDatasetSnapshot second =
            await TranslationSuiteRunner.LoadDatasetSnapshotAsync(semanticB);
        string firstSelection = TranslationSuiteRunner.CreateSelectionSha256(
            first.Cases,
            first.Artifact.Sha256);
        string secondSelection = TranslationSuiteRunner.CreateSelectionSha256(
            second.Cases,
            second.Artifact.Sha256);
        Check(firstSelection != secondSelection,
            "Selection v2 ignored reference/context changes with the same id and source.");
    }

    private static async Task VerifyPrepareFailureDisposesAsync(
        TranslationSuiteOptions options,
        string root)
    {
        FakeTranslationSuiteSystem? failed = null;
        TranslationSuiteOptions failingOptions = options with
        {
            TranslationOutputPath = Path.Combine(root, "prepare-failure-output.jsonl"),
            TranslationReportPath = Path.Combine(root, "prepare-failure-report.json")
        };
        await ExpectThrowsAsync<InvalidOperationException>(
            () => TranslationSuiteRunner.RunAsync(
                failingOptions,
                parsed => failed = new FakeTranslationSuiteSystem(
                    parsed,
                    failPrepare: true)),
            "A prepared-system failure was unexpectedly swallowed.");
        Check(failed is { Disposed: true },
            "A partial Prepare failure bypassed await-using cleanup.");
    }

    private static async Task VerifyBestEffortCleanupAsync()
    {
        bool laterStepRan = false;
        IReadOnlyList<Exception> failures =
            await SmartTranslationSuiteSystem.DisposeStepsBestEffortAsync(
                () => new ValueTask(Task.FromException(
                    new IOException("Expected cleanup failure."))),
                () =>
                {
                    laterStepRan = true;
                    return ValueTask.CompletedTask;
                });
        Check(laterStepRan && failures.Count == 1 && failures[0] is IOException,
            "Best-effort cleanup stopped at the first resource failure.");
    }

    private static string[] CanonicalFixtureLines() =>
    [
        "{\"schemaVersion\":1,\"id\":\"case-rich\",\"source\":\"Wait here, please.\",\"sourceLanguage\":\"en\",\"targetLanguage\":\"ru\",\"references\":[\"Подождите здесь, пожалуйста.\"],\"previousContext\":[\"I will call my mother.\"],\"focus\":{\"text\":\"Wait\",\"characterIndex\":0,\"annotated\":true},\"speaker\":{\"name\":\"Kotori\",\"gender\":\"feminine\"},\"termAnchors\":[{\"source\":\"wait\",\"acceptable\":[\"подождите\"],\"match\":\"stem\"}],\"contrastReferences\":[\"Подождите там.\"],\"contrastSetId\":\"wait-place\",\"domain\":\"vn\",\"tags\":[\"dialogue\"],\"documentId\":\"rewrite\",\"segmentIndex\":4,\"license\":\"test-only\",\"baselineTranslations\":{\"baseline\":\"Ждите здесь.\"},\"metadata\":{\"fixture\":true}}",
        "{\"schemaVersion\":1,\"id\":\"case-reference-free\",\"source\":\"Hello there.\",\"sourceLanguage\":\"en\",\"targetLanguage\":\"ru\",\"references\":[],\"referenceFree\":true,\"termAnchors\":[],\"contrastReferences\":[],\"tags\":[\"qe\"]}",
        "{\"schemaVersion\":1,\"id\":\"case-unavailable\",\"source\":\"This result is intentionally unavailable.\",\"references\":[\"Этот результат намеренно недоступен.\"],\"termAnchors\":[{\"en\":\"unavailable\",\"ru\":\"недоступ\",\"match\":\"stem\"}]}",
        "{\"schemaVersion\":1,\"id\":\"case-error\",\"source\":\"This result throws.\",\"references\":[\"Этот результат вызывает ошибку.\"]}"
    ];

    private static void VerifyParserRejectsInvalidTails(
        string dataset,
        string worker,
        string output,
        string report)
    {
        string[] ValidTail() =>
        [
            "--benchmark-only", "--translation-suite", dataset, worker,
            "--system-id", "fake", "--profile", "direct",
            "--phrase-backend", "vulkan", "--ranker-cpu-model", "glossbert",
            "--translation-output", output, "--translation-report", report
        ];

        (string Name, Func<string[]> Mutate)[] invalid =
        [
            ("unknown option", () => [.. ValidTail(), "--unknown", "value"]),
            ("duplicate option", () => [.. ValidTail(), "--profile", "smart"]),
            ("unsupported ranker", () => ValidTail().Select(value =>
                value == "glossbert" ? "deberta" : value).ToArray()),
            ("removed SANDWiCH ranker", () => ValidTail().Select(value =>
                value == "glossbert" ? "sandwich" : value).ToArray()),
            ("zero maximum", () => [.. ValidTail(), "--max-cases", "0"]),
            ("invalid shard", () => [.. ValidTail(), "--shard", "3/2"]),
            ("missing required", () => ValidTail()[..^2]),
            ("unknown coopmat value", () =>
                [.. ValidTail(), "--phrase-coopmat", "false"]),
            ("duplicate coopmat", () =>
                [.. ValidTail(), "--phrase-coopmat", "off", "--phrase-coopmat", "off"]),
            ("coopmat without value", () => [.. ValidTail(), "--phrase-coopmat"]),
            // Only the Vulkan runner reads the variable: on CPU the switch
            // would change nothing while the run claimed a variant.
            ("coopmat on cpu", () =>
                [.. ValidTail().Select(value => value == "vulkan" ? "cpu" : value),
                 "--phrase-coopmat", "off"]),
            ("explicit coopmat default on cpu", () =>
                [.. ValidTail().Select(value => value == "vulkan" ? "cpu" : value),
                 "--phrase-coopmat", "on"]),
            ("unknown cache-prompt value", () =>
                [.. ValidTail(), "--phrase-cache-prompt", "false"]),
            ("duplicate cache-prompt", () =>
                [.. ValidTail(), "--phrase-cache-prompt", "off",
                 "--phrase-cache-prompt", "off"]),
            // The smart lane builds its translator inside the hover pipeline
            // with the production request, so the switch would not reach it.
            ("cache-prompt on smart", () =>
                [.. ValidTail().Select(value => value == "direct" ? "smart" : value),
                 "--phrase-cache-prompt", "off"]),
            ("explicit cache-prompt default on smart", () =>
                [.. ValidTail().Select(value => value == "direct" ? "smart" : value),
                 "--phrase-cache-prompt", "on"])
        ];
        foreach ((string name, Func<string[]> mutate) in invalid)
        {
            ExpectThrows<InvalidOperationException>(
                () => TranslationSuiteOptions.Parse(mutate(), 1),
                $"The strict parser accepted {name}.");
        }
    }

    /// <summary>
    /// Audit E17 follow-up: the coopmat A/B must run on both lanes, and
    /// each run must name its numeric path in the identity every output
    /// carries.
    /// </summary>
    private static void VerifyCoopmatOption(
        string dataset,
        string worker,
        string output,
        string report)
    {
        string[] Tail(string profile, params string[] extra) =>
        [
            "--benchmark-only", "--translation-suite", dataset, worker,
            "--system-id", "fake", "--profile", profile,
            "--phrase-backend", "vulkan", "--ranker-cpu-model", "consec",
            "--translation-output", output, "--translation-report", report,
            .. extra
        ];

        foreach (string profile in new[] { "direct", "smart" })
        {
            TranslationSuiteOptions shipped = TranslationSuiteOptions.Parse(Tail(profile), 1);
            TranslationSuiteOptions on = TranslationSuiteOptions.Parse(
                Tail(profile, "--phrase-coopmat", "ON"),
                1);
            TranslationSuiteOptions off = TranslationSuiteOptions.Parse(
                Tail(profile, "--phrase-coopmat", "off"),
                1);
            Check(shipped.PhraseVulkanCoopmat == TranslateGemmaTranslationService.ShippedVulkanCoopmat &&
                  on.PhraseVulkanCoopmat == TranslateGemmaTranslationService.VulkanCoopmat.Enabled &&
                  off.PhraseVulkanCoopmat == TranslateGemmaTranslationService.VulkanCoopmat.Disabled,
                $"The {profile} lane did not map --phrase-coopmat to the requested numeric path.");
            Check(TestCommandLine.FindUnknownOption(Tail(profile, "--phrase-coopmat", "off")) is null,
                "The suite-level option guard does not know --phrase-coopmat.");
        }

        // The identity reaches outputs through the translator instance both
        // lanes construct from these options.
        string Identity(TranslateGemmaTranslationService.VulkanCoopmat coopmat) =>
            TranslateGemmaTranslationService.RuntimeIdentity(
                TranslateGemmaTranslationService.TranslateGemmaModelPin.Default,
                TranslateGemmaExecutionBackend.Vulkan,
                coopmat);
        Check(Identity(TranslateGemmaTranslationService.VulkanCoopmat.Enabled) ==
              TranslateGemmaTranslationService.VulkanCoopmatModelIdentity &&
              Identity(TranslateGemmaTranslationService.VulkanCoopmat.Disabled)
                  .EndsWith("/llama.cpp-b9632-vulkan-full-nocoopmat", StringComparison.Ordinal) &&
              Identity(TranslateGemmaTranslationService.ShippedVulkanCoopmat) ==
              TranslateGemmaTranslationService.VulkanModelIdentity,
            "A run on either coopmat path could report the other path's identity.");
    }

    private static void VerifyCachePromptOption(
        string dataset,
        string worker,
        string output,
        string report)
    {
        string[] DirectTail(params string[] extra) =>
        [
            "--benchmark-only", "--translation-suite", dataset, worker,
            "--system-id", "fake", "--profile", "direct",
            "--phrase-backend", "vulkan", "--ranker-cpu-model", "glossbert",
            "--translation-output", output, "--translation-report", report,
            .. extra
        ];

        TranslationSuiteOptions shipped = TranslationSuiteOptions.Parse(DirectTail(), 1);
        TranslationSuiteOptions explicitOn = TranslationSuiteOptions.Parse(
            DirectTail("--phrase-cache-prompt", "ON"),
            1);
        TranslationSuiteOptions off = TranslationSuiteOptions.Parse(
            DirectTail("--phrase-cache-prompt", "off"),
            1);
        Check(shipped.PhraseCachePrompt && explicitOn.PhraseCachePrompt &&
              !off.PhraseCachePrompt,
            "The direct lane must default to the production prompt cache and " +
            "turn it off only on an explicit --phrase-cache-prompt off.");

        string shippedEngine = DirectTranslationSuiteSystem.ResolveEngineLabel(shipped);
        string offEngine = DirectTranslationSuiteSystem.ResolveEngineLabel(off);
        Check(shippedEngine == TranslateGemmaTranslationService.GetEngineLabel(
                  TranslateGemmaExecutionBackend.Vulkan) &&
              offEngine == shippedEngine + TranslationSuiteOptions.CachePromptOffEngineSuffix,
            "A cache-off run must name itself in every output's engine label, and " +
            "a production run must keep the shipped label.");
        Check(TestCommandLine.FindUnknownOption(DirectTail("--phrase-cache-prompt", "off"))
              is null,
            "The suite-level option guard does not know --phrase-cache-prompt.");
    }

    private static async Task VerifySchemaRejectionsAsync(string root)
    {
        (string Name, string Line)[] invalid =
        [
            ("unknown", "{\"id\":\"x\",\"source\":\"Hello.\",\"references\":[\"Привет.\"],\"unexpected\":1}"),
            ("wrong-hash", "{\"id\":\"x\",\"source\":\"Hello.\",\"sourceSha256\":\"" + new string('0', 64) + "\",\"references\":[\"Привет.\"]}"),
            ("empty-references", "{\"id\":\"x\",\"source\":\"Hello.\",\"references\":[]}"),
            ("bad-focus", "{\"id\":\"x\",\"source\":\"Hello.\",\"references\":[\"Привет.\"],\"focus\":{\"text\":\"Hello\",\"characterIndex\":1}}"),
            ("bad-anchor", "{\"id\":\"x\",\"source\":\"Hello.\",\"references\":[\"Привет.\"],\"termAnchors\":[{\"source\":\"hello\",\"acceptable\":[]}]}"),
            ("wrong-language", "{\"id\":\"x\",\"source\":\"Bonjour.\",\"sourceLanguage\":\"fr\",\"references\":[\"Привет.\"]}")
        ];
        foreach ((string name, string line) in invalid)
        {
            string path = Path.Combine(root, $"invalid-{name}.jsonl");
            await File.WriteAllTextAsync(path, line + '\n');
            await ExpectThrowsAsync<InvalidDataException>(
                () => TranslationSuiteRunner.LoadDatasetAsync(path),
                $"The translation-suite schema accepted {name}.");
        }

        string inferredFocusPath = Path.Combine(root, "valid-inferred-focus.jsonl");
        await File.WriteAllTextAsync(
            inferredFocusPath,
            "{\"id\":\"inferred\",\"source\":\"Hello.\",\"references\":[\"Привет.\"]," +
            "\"focus\":{\"text\":\"Hello\",\"characterIndex\":0,\"annotated\":false}}\n");
        IReadOnlyList<TranslationSuiteCase> inferredFocusCases =
            await TranslationSuiteRunner.LoadDatasetAsync(inferredFocusPath);
        Check(inferredFocusCases.Single().Focus is { Annotated: false },
            "An explicit inferred focus was silently rewritten as annotated.");
    }

    private static async Task VerifyReportingCliContractAsync(
        string dataset,
        string output,
        string selectionReport,
        string root)
    {
        string? repositoryRoot = AppContext.BaseDirectory;
        while (repositoryRoot is not null &&
               !File.Exists(Path.Combine(repositoryRoot, "AsteriaScale.slnx")))
        {
            repositoryRoot = Path.GetDirectoryName(repositoryRoot);
        }

        Check(repositoryRoot is not null,
            "Repository root was not found for the reporting CLI contract test.");
        string scorer = Path.Combine(repositoryRoot!, "tools", "translation-benchmark.py");
        Check(File.Exists(scorer), "The translation reporting CLI is missing.");
        string scoredReport = Path.Combine(root, "scored-report.json");
#if WINDOWS
        var startInfo = new ProcessStartInfo("python")
#else
        // macOS ships no 'python' alias; python3 is the documented prerequisite.
        var startInfo = new ProcessStartInfo("python3")
#endif
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
                 {
                     scorer,
                     "score",
                     "--dataset", dataset,
                     "--outputs", output,
                     "--selection-report", selectionReport,
                     "--system-id", "fake-smart-v1",
                     "--output", scoredReport
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The reporting CLI did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string outputText = await stdout;
        string errorText = await stderr;
        Check(process.ExitCode == 0,
            "The Python scorer rejected C# output schema: " + errorText + outputText);
        using JsonDocument scored = JsonDocument.Parse(
            await File.ReadAllTextAsync(scoredReport));
        Check(scored.RootElement.GetProperty("schemaVersion").GetInt32() == 1 &&
              scored.RootElement.GetProperty("kind").GetString() ==
                  "asteria.translation-benchmark-run" &&
              scored.RootElement.GetProperty("cases").GetArrayLength() == 4,
            "The reporting CLI did not align every selected C# output row.");
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ExpectThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task ExpectThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeTranslationSuiteSystem : ITranslationSuiteSystem
    {
        private readonly TranslationSuiteOptions _options;
        private readonly Func<Task>? _prepareAction;
        private readonly bool _failPrepare;

        internal FakeTranslationSuiteSystem(
            TranslationSuiteOptions options,
            Func<Task>? prepareAction = null,
            bool failPrepare = false)
        {
            _options = options;
            _prepareAction = prepareAction;
            _failPrepare = failPrepare;
        }

        internal bool Prepared { get; private set; }

        internal bool Disposed { get; private set; }

        public async Task PrepareAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_prepareAction is not null)
            {
                await _prepareAction();
            }

            Prepared = true;
            if (_failPrepare)
            {
                throw new InvalidOperationException("Expected Prepare failure.");
            }
        }

        public Task<TranslationSuiteSystemResult> TranslateAsync(
            TranslationSuiteCase benchmarkCase,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return benchmarkCase.Id switch
            {
                "case-rich" => Task.FromResult(new TranslationSuiteSystemResult(
                    TranslationSuiteCaseStatus.Ok,
                    "Подождите здесь, пожалуйста.",
                    FromCache: false,
                    TranslationSuiteRunner.ResolveFocus(benchmarkCase),
                    Engine: "fake-cpu",
                    Metadata: CreateSmartMetadata(applied: true))),
                "case-reference-free" => Task.FromResult(new TranslationSuiteSystemResult(
                    TranslationSuiteCaseStatus.Ok,
                    "Здравствуйте.",
                    FromCache: true,
                    TranslationSuiteRunner.ResolveFocus(benchmarkCase),
                    Engine: "fake-cpu",
                    Metadata: CreateSmartMetadata(applied: true))),
                "case-unavailable" => Task.FromResult(new TranslationSuiteSystemResult(
                    TranslationSuiteCaseStatus.Unavailable,
                    Hypothesis: string.Empty,
                    FromCache: false,
                    Focus: null,
                    ErrorCode: "fixture_unavailable",
                    ErrorMessage: "Expected fixture outcome.",
                    RejectionReason: SmartPhraseRejectionReason.ResidualEnglish,
                    Metadata: CreateSmartMetadata(applied: false))),
                "case-error" => throw new InvalidOperationException("Expected fixture failure."),
                _ => throw new InvalidOperationException("Unexpected fixture id.")
            };
        }

        private TranslationSuiteSmartMetadata? CreateSmartMetadata(bool applied)
        {
            if (_options.Profile != TranslationSuiteProfile.Smart)
            {
                return null;
            }

            // One real hint, so the Python scorer below actually validates the
            // reported diagnostics instead of an empty list (audit card E9).
            TranslationSuiteSenseEvidence[] evidence =
            [
                new TranslationSuiteSenseEvidence(
                    "wait",
                    "to remain in place",
                    "подождите",
                    Confidence: 0.73,
                    Origin: SenseEvidenceOrigin.RankerAccepted)
            ];
            string evidenceSha256 = FingerprintFixtureEvidence(evidence);
            return new TranslationSuiteSmartMetadata(
                "fake-ranker",
                DictionaryFound: false,
                DictionaryIsPhrase: false,
                DictionaryTranslation: string.Empty,
                SenseEvidence: evidence,
                SenseEvidenceSha256: evidenceSha256,
                AppliedSenseEvidence: applied
                    ? evidence
                    : Array.Empty<TranslationSuiteSenseEvidence>(),
                AppliedSenseEvidenceSha256: applied
                    ? evidenceSha256
                    : null);
        }

        /// <summary>
        /// Mirrors the runner's prompt-visible fingerprint: three fields, no
        /// diagnostics. Written out here so the fixture cannot drift silently.
        /// </summary>
        private static string FingerprintFixtureEvidence(
            IReadOnlyList<TranslationSuiteSenseEvidence> evidence)
        {
            var canonical = new StringBuilder("asteria-smart-sense-evidence-v1\n");
            foreach (TranslationSuiteSenseEvidence item in evidence)
            {
                foreach (string field in new[]
                         {
                             item.Source,
                             item.EnglishDefinition,
                             item.SelectedTranslation
                         })
                {
                    canonical.Append(field.Length);
                    canonical.Append(':');
                    canonical.Append(field);
                    canonical.Append('\n');
                }
            }

            return Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
                .ToLowerInvariant();
        }

        public async Task<TranslationSuiteSystemProvenance> CaptureProvenanceAsync(
            string datasetPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new TranslationSuiteSystemProvenance(
                "fake-translation-model",
                "fake-prompt-v1",
                _options.Profile == TranslationSuiteProfile.Smart ? "fake-ranker" : null,
                _options.Profile == TranslationSuiteProfile.Smart ? "CPU" : null,
                _options.Profile == TranslationSuiteProfile.Smart
                    ? RankerAbstention.CacheTag
                    : null,
                await RankerBenchmarkProvenance.DescribeArtifactAsync(
                    datasetPath,
                    "fake-application",
                    cancellationToken),
                await RankerBenchmarkProvenance.DescribeArtifactAsync(
                    typeof(TranslationSuiteRunner).Assembly.Location,
                    Path.GetFileName(typeof(TranslationSuiteRunner).Assembly.Location),
                    cancellationToken),
                Ranker: null);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
