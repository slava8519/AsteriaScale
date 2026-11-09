using System.Text.Json.Nodes;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AsteriaScale;
using AsteriaScale.Models;
using AsteriaScale.Services;

int passed = 0;
string tempRoot = Path.Combine(Path.GetTempPath(), "AsteriaScaleTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);

try
{
#if !WINDOWS
    // Rejects every option and a launch through the dotnet muxer before any
    // test runs, so a gated mode compiled out below can never fall through to
    // a green macOS subset (TestCommandLine.cs explains the hazard).
    MacSmoke.Begin(args);
#endif
    if (args.Any(arg => string.Equals(arg, "--benchmark-only", StringComparison.OrdinalIgnoreCase)))
    {
        int benchmarkOnlyRankerArgument = Array.FindIndex(args, arg =>
            string.Equals(arg, "--ranker-benchmark", StringComparison.OrdinalIgnoreCase));
        int benchmarkOnlyRegisterArgument = Array.FindIndex(args, arg =>
            string.Equals(arg, "--register-benchmark", StringComparison.OrdinalIgnoreCase));
        int benchmarkOnlySentenceArgument = Array.FindIndex(args, arg =>
            string.Equals(
                arg,
                "--ranker-sentence-benchmark",
                StringComparison.OrdinalIgnoreCase));
        int benchmarkOnlyTranslationArgument = Array.FindIndex(args, arg =>
            string.Equals(
                arg,
                "--translation-suite",
                StringComparison.OrdinalIgnoreCase));
        int benchmarkFamilyCount =
            (benchmarkOnlyRankerArgument >= 0 ? 1 : 0) +
            (benchmarkOnlyRegisterArgument >= 0 ? 1 : 0) +
            (benchmarkOnlySentenceArgument >= 0 ? 1 : 0) +
            (benchmarkOnlyTranslationArgument >= 0 ? 1 : 0);
        Assert(benchmarkFamilyCount == 1,
            "--benchmark-only requires exactly one benchmark family per invocation.");

        if (benchmarkOnlyTranslationArgument >= 0)
        {
            TranslationSuiteOptions options = TranslationSuiteOptions.Parse(
                args,
                benchmarkOnlyTranslationArgument);
            await RunAsync(
                $"Translation suite ({options.SystemId}/{options.ProfileId})",
                () => TranslationSuiteRunner.RunAsync(options));
            Console.WriteLine(string.Format("PASS: {0} benchmark", passed));
            return 0;
        }

        if (benchmarkOnlySentenceArgument >= 0)
        {
            SentenceRankerBenchmarkOptions options =
                SentenceRankerBenchmarkOptions.Parse(
                    args,
                    benchmarkOnlySentenceArgument);
            await RunAsync(
                $"Sentence semantic benchmark (CPU/{options.CpuModel})",
                () => RankerSentenceBenchmark.RunAsync(options));
            Console.WriteLine(string.Format("PASS: {0} benchmark", passed));
            return 0;
        }

        if (benchmarkOnlyRankerArgument >= 0)
        {
            const string usage =
                "Usage: --benchmark-only --ranker-benchmark <benchmark.json> " +
                "<workerBaseDirectory> [cpu|npu] " +
                "[--ranker-cpu-model glossbert|consec] " +
                "[--dump <path>] [--ranker-report <path>]";
            Assert(args.Length > benchmarkOnlyRankerArgument + 2, usage);
            ParsedRankerBenchmarkOptions options = ParseRankerBenchmarkOptions(
                args,
                benchmarkOnlyRankerArgument + 3,
                allowDump: true,
                reportOption: "--ranker-report",
                usage: usage);
            await RunAsync(
                $"Sense-ranker calibration benchmark ({options.Provider}/{options.CpuModel})",
                () => RankerCalibrationBenchmark.RunAsync(
                    args[benchmarkOnlyRankerArgument + 1],
                    args[benchmarkOnlyRankerArgument + 2],
                    options.Provider,
                    options.DumpPath,
                    options.ReportPath,
                    options.CpuModel));
            Console.WriteLine(string.Format("PASS: {0} benchmark", passed));
            return 0;
        }

        if (benchmarkOnlyRegisterArgument >= 0)
        {
            const string usage =
                "Usage: --benchmark-only --register-benchmark <register-set.json> " +
                "<workerBaseDirectory> [cpu|npu] " +
                "[--ranker-cpu-model glossbert|consec] " +
                "[--register-report <path>]";
            Assert(args.Length > benchmarkOnlyRegisterArgument + 2, usage);
            ParsedRankerBenchmarkOptions options = ParseRankerBenchmarkOptions(
                args,
                benchmarkOnlyRegisterArgument + 3,
                allowDump: false,
                reportOption: "--register-report",
                usage: usage);
            await RunAsync(
                $"Register fidelity benchmark ({options.Provider}/{options.CpuModel})",
                () => RegisterFidelityBenchmark.RunAsync(
                    args[benchmarkOnlyRegisterArgument + 1],
                    args[benchmarkOnlyRegisterArgument + 2],
                    options.Provider,
                    options.ReportPath,
                    options.CpuModel));
            Console.WriteLine(string.Format("PASS: {0} benchmark", passed));
            return 0;
        }

    }

    Assert(!args.Any(arg => string.Equals(
            arg,
            "--translation-suite",
            StringComparison.OrdinalIgnoreCase)),
        "--translation-suite requires --benchmark-only.");

    // A gated mode deleted from the code used to survive in the documentation
    // and fall through to the default smoke set, reporting a green run for a
    // check that never executed.
    if (TestCommandLine.FindUnknownOption(args) is { } unknownOption)
    {
        Assert(false, TestCommandLine.DescribeUnknownOption(unknownOption));
    }

    await RunAsync("Preset catalog", () =>
    {
        Assert(PresetCatalog.All.Count >= 5, "Expected at least five presets.");
        Assert(PresetCatalog.All.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == PresetCatalog.All.Count,
            "Preset ids must be unique.");
        Assert(PresetCatalog.Find("vn-balanced").Effects.Any(e => e.Name.Contains("CuNNy2", StringComparison.Ordinal)),
            "The HQ visual-novel preset must use CuNNy2.");
        UpscalePreset fast = PresetCatalog.Find("vn-performance");
        Assert(fast.IsRecommended &&
               fast.Effects.Any(e => e.Name == "FSR\\FSR_EASU") &&
               fast.Effects.Any(e => e.Name == "FSR\\FSR_RCAS") &&
               fast.Effects.All(e => !e.Name.Contains("CuNNy2", StringComparison.Ordinal)),
            "The fast visual-novel preset must avoid an unnecessary CNN intermediate frame.");
        UpscalePreset efficient = PresetCatalog.Find("vn-efficient");
        Assert(efficient.IsVisualNovel &&
               efficient.Effects.Any(e => e.Name == "CuNNy2\\CuNNy-4x12-NVL") &&
               efficient.Effects.Any(e => e.Name == "FSR\\FSR_RCAS") &&
               efficient.Effects.All(e => !e.Name.Contains("AdaptiveSharpen", StringComparison.Ordinal)),
            "The balanced visual-novel preset must use the lightweight CNN and sharpening path.");
        return Task.CompletedTask;
    });

    await RunAsync("Settings round trip", async () =>
    {
        string path = Path.Combine(tempRoot, "settings", "settings.json");
        SettingsService service = new(path);
        AppSettings source = new()
        {
            SelectedPresetId = "vn-ultra",
            VisualNovelFrameRateLimit = 30,
            AutomationEnabled = false,
            HoverTranslationEnabled = true,
            HoverTranslationDelayMs = 450,
            HoverTranslationMode = HoverTranslationModes.Dictionary,
            SmartDictionaryBackend = SmartDictionaryBackends.Cpu,
            SmartDictionaryRankerModel = SmartDictionaryRankerModels.Consec,
            SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.Japanese,
            SmartDictionaryKeepWarm = false,
            SmartPhraseTranslationMode = SmartPhraseTranslationModes.TranslateGemma,
            SmartPhraseTranslationBackend = SmartPhraseTranslationBackends.Vulkan,
            DpiOverrides =
            [
                new ManagedDpiOverride
                {
                    ProcessPath = @"C:\Games\Novel\novel.exe",
                    OriginalCompatibilityLayers = "~ WIN7RTM",
                    AppliedCompatibilityLayers = "~ WIN7RTM HIGHDPIAWARE"
                }
            ],
            Profiles =
            [
                new AutomationProfile
                {
                    Name = "Novel",
                    ProcessName = "novel",
                    ProcessPath = @"C:\Games\Novel\novel.exe",
                    ClassName = "NovelWindow",
                    PresetId = "vn-ultra"
                }
            ]
        };
        await service.SaveAsync(source);
        AppSettings loaded = await service.LoadAsync();
        Assert(loaded.SelectedPresetId == "vn-ultra", "Selected preset was not preserved.");
        Assert(loaded.VisualNovelFrameRateLimit == 30, "VN frame-rate limit was not preserved.");
        Assert(loaded.HoverTranslationEnabled && loaded.HoverTranslationDelayMs == 450,
            "Hover translation settings were not preserved.");
        Assert(loaded.HoverTranslationMode == HoverTranslationModes.Dictionary,
            "Hover translation mode was not preserved.");
        Assert(loaded.SmartDictionaryBackend == SmartDictionaryBackends.Cpu &&
               loaded.SmartDictionaryRankerModel == SmartDictionaryRankerModels.Consec &&
               loaded.SmartDictionarySourceLanguage == SmartDictionarySourceLanguages.Japanese &&
               !loaded.SmartDictionaryKeepWarm &&
               loaded.SmartPhraseTranslationMode == SmartPhraseTranslationModes.TranslateGemma &&
               loaded.SmartPhraseTranslationBackend == SmartPhraseTranslationBackends.Vulkan,
            "Smart dictionary settings were not preserved.");
        Assert(HoverTranslationModes.NormalizeUserMode("SCREEN") ==
                   HoverTranslationModes.Smart &&
               HoverTranslationModes.NormalizeUserMode("NEURAL") ==
                   HoverTranslationModes.Smart &&
               HoverTranslationModes.NormalizeUserMode("DICTIONARY") ==
                   HoverTranslationModes.Dictionary &&
               HoverTranslationModes.NormalizeUserMode("SMART") ==
                   HoverTranslationModes.Smart,
            "User-facing translation modes were not normalized.");
        Assert(SmartDictionaryBackends.Normalize("NPU") == SmartDictionaryBackends.Npu &&
               SmartDictionaryBackends.Normalize("CPU") == SmartDictionaryBackends.Cpu &&
               SmartDictionaryBackends.Normalize("GPU") == SmartDictionaryBackends.Cpu &&
               SmartDictionaryBackends.Normalize("unsupported") == SmartDictionaryBackends.Auto,
            "Smart dictionary backend normalization failed.");
        Assert(SmartDictionaryRankerModels.Normalize("CONSEC") ==
                   SmartDictionaryRankerModels.Consec &&
               SmartDictionaryRankerModels.Normalize("unsupported") ==
                   SmartDictionaryRankerModels.GlossBert,
            "Smart dictionary ranker-model normalization failed.");
        Assert(HoverEngineLabelClassifier.SmartAccelerator(
                   GlossBertDictionarySelectorService.CpuEngineLabel + " • НЕ УВЕРЕН") == "CPU" &&
               HoverEngineLabelClassifier.SmartAccelerator(
                   GlossBertDictionarySelectorService.NpuEngineLabel + " • НЕ УВЕРЕН") == "NPU" &&
               HoverEngineLabelClassifier.IsSmartAbstentionEngine(
                   GlossBertDictionarySelectorService.NpuEngineLabel + " • НЕ УВЕРЕН"),
            "GlossBERT overlay accelerator/status recognition failed.");
        Assert(HoverEngineLabelClassifier.IsSmartUnavailableEngine(
                   GlossBertDictionarySelectorService.CpuEngineLabel + " • НЕДОСТУПЕН"),
            "GlossBERT unavailable status recognition failed.");
        // Every Windows engine label, byte for byte. History files and
        // benchmark reports store these strings and the hover card tells card
        // kinds apart by them, so a label for another text source has to sit
        // beside them instead of replacing them.
        Assert(LocalDictionaryService.EngineLabel == "NPU OCR  •  СЛОВАРЬ" &&
               SmartDictionaryHeuristicRanker.EngineLabel == "NPU OCR  •  SMART-FAST" &&
               SmartDictionaryHeuristicRanker.TatoebaEngineLabel ==
                   "NPU OCR  •  SMART-FAST  •  TATOEBA" &&
               SmartDictionaryResultCache.EngineLabel == "NPU OCR  •  SMART-CACHE" &&
               GlossBertDictionarySelectorService.CpuEngineLabel == "NPU OCR  •  CPU-СМЫСЛ" &&
               GlossBertDictionarySelectorService.NpuEngineLabel == "NPU OCR  •  NPU-СМЫСЛ" &&
               TranslateGemmaTranslationService.EngineLabel == "TRANSLATEGEMMA 4B" &&
               TranslateGemmaTranslationService.GetEngineLabel(
                   TranslateGemmaExecutionBackend.Cpu) == "TRANSLATEGEMMA 4B  •  CPU" &&
               TranslateGemmaTranslationService.GetEngineLabel(
                   TranslateGemmaExecutionBackend.Vulkan) == "TRANSLATEGEMMA 4B  •  VULKAN",
            "A Windows engine label changed.");
        // The card kind read from each literal label the pipeline produces,
        // the two ranker suffixes written as SmartHoverPipeline appends them.
        static string CardKind(string engine) => string.Join(
            "+",
            new[]
            {
                HoverEngineLabelClassifier.IsDictionaryEngine(engine) ? "dictionary" : null,
                HoverEngineLabelClassifier.IsFastPathEngine(engine) ? "fast" : null,
                HoverEngineLabelClassifier.IsTatoebaEngine(engine) ? "tatoeba" : null,
                HoverEngineLabelClassifier.IsResultCacheEngine(engine) ? "cache" : null,
                HoverEngineLabelClassifier.SmartAccelerator(engine),
                HoverEngineLabelClassifier.IsSmartAbstentionEngine(engine) ? "abstained" : null,
                HoverEngineLabelClassifier.IsSmartUnavailableEngine(engine) ? "unavailable" : null
            }.Where(part => part is not null));
        (string Engine, string Kind)[] cardKinds =
        [
            ("NPU OCR  •  СЛОВАРЬ", "dictionary"),
            ("NPU OCR  •  SMART-FAST", "fast"),
            ("NPU OCR  •  SMART-FAST  •  TATOEBA", "fast+tatoeba"),
            ("NPU OCR  •  SMART-CACHE", "cache"),
            ("NPU OCR  •  CPU-СМЫСЛ", "CPU"),
            ("NPU OCR  •  NPU-СМЫСЛ", "NPU"),
            ("NPU OCR  •  CPU-СМЫСЛ  •  НЕ УВЕРЕН", "CPU+abstained"),
            ("NPU OCR  •  NPU-СМЫСЛ  •  НЕ УВЕРЕН", "NPU+abstained"),
            ("NPU OCR  •  CPU-СМЫСЛ  •  НЕДОСТУПЕН", "CPU+unavailable"),
            ("NPU OCR  •  NPU-СМЫСЛ  •  НЕДОСТУПЕН", "NPU+unavailable")
        ];
        foreach ((string engine, string kind) in cardKinds)
        {
            Assert(CardKind(engine) == kind,
                $"The card reads '{engine}' as '{CardKind(engine)}' instead of '{kind}'.");
        }

        // The wide-header rule reads the header text the card composes, not
        // the engine string.
        Assert(HoverEngineLabelClassifier.IsLongSmartStatus("EN → RU  ·  NPU · НЕ УВЕРЕН  ·  OCR") &&
               HoverEngineLabelClassifier.IsLongSmartStatus("EN → RU  ·  CPU · НЕДОСТУПЕН  ·  HOOK") &&
               !HoverEngineLabelClassifier.IsLongSmartStatus("EN → RU  ·  CPU КОНТЕКСТ  ·  OCR") &&
               !HoverEngineLabelClassifier.IsLongSmartStatus("EN → RU  ·  TATOEBA  ·  OCR"),
            "The card header no longer reserves width for the long smart statuses.");
        SmartHoverPhraseLine vulkanPhrase = new(
            "We rested on the bank.",
            "Мы отдыхали на берегу.",
            "TRANSLATEGEMMA 4B  •  VULKAN",
            SmartPhraseLineStatus.Translated,
            FromCache: false);
        Assert(HoverEngineLabelClassifier.PhraseEngineLabel(vulkanPhrase) ==
                   "Фраза · TG4B · GPU" &&
               HoverEngineLabelClassifier.PhraseEngineLabel(
                   vulkanPhrase with { IsProvisional = true }) ==
                   "Фраза · TG4B · GPU · уточняется…" &&
               HoverEngineLabelClassifier.PhraseEngineLabel(
                   vulkanPhrase with { Engine = "TRANSLATEGEMMA 4B  •  CPU" }) ==
                   "Фраза · TG4B · CPU",
            "The phrase-line caption does not name the TranslateGemma backend.");
        Assert(SmartDictionarySourceLanguages.Normalize("EN") == SmartDictionarySourceLanguages.English &&
               SmartDictionarySourceLanguages.Normalize("unsupported") == SmartDictionarySourceLanguages.Auto,
            "Smart dictionary source-language normalization failed.");
        Assert(SmartPhraseTranslationModes.Normalize("TRANSLATEGEMMA-4B") ==
               SmartPhraseTranslationModes.TranslateGemma &&
               SmartPhraseTranslationModes.Normalize("unsupported") == SmartPhraseTranslationModes.Off,
            "Smart phrase translation mode normalization failed.");
        Assert(SmartPhraseTranslationBackends.Normalize("VULKAN") ==
                   SmartPhraseTranslationBackends.Vulkan &&
               SmartPhraseTranslationBackends.Normalize("unsupported") ==
                   SmartPhraseTranslationBackends.Cpu,
            "Smart phrase translation backend normalization failed.");
        Assert(loaded.Profiles.Count == 1 && loaded.Profiles[0].ProcessName == "novel", "Profile was not preserved.");
        Assert(loaded.DpiOverrides.Count == 1 &&
               loaded.DpiOverrides[0].OriginalCompatibilityLayers == "~ WIN7RTM",
            "Managed DPI override backup was not preserved.");
        string json = await File.ReadAllTextAsync(path);
        Assert(!json.Contains("\"Details\"", StringComparison.Ordinal), "Computed UI details must not be stored.");

        // Where SettingsService and every other Core default path point.
        await CoreDataLayoutTests.RunAsync(tempRoot);
    });

    await RunAsync(
        "Per-game speaker gender preferences",
        () => SpeakerGenderPreferenceTests.RunAsync(tempRoot));

    await RunAsync("Legacy hidden translation modes migrate to smart", async () =>
    {
        (string Name, string Json)[] legacySettings =
        [
            ("neural", "{\"hoverTranslationMode\":\"neural\"}"),
            ("screen", "{\"hoverTranslationMode\":\"screen\"}"),
            ("unknown", "{\"hoverTranslationMode\":\"unsupported\"}"),
            ("null", "{\"hoverTranslationMode\":null}")
        ];

        foreach ((string name, string json) in legacySettings)
        {
            string path = Path.Combine(
                tempRoot,
                $"legacy-hover-mode-{name}",
                "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);

            AppSettings loaded = await new SettingsService(path).LoadAsync();
            Assert(loaded.HoverTranslationMode == HoverTranslationModes.Smart,
                $"Legacy user-facing mode '{name}' did not migrate to smart.");
        }
    });

    await RunAsync("Legacy smart dictionary defaults", async () =>
    {
        string path = Path.Combine(tempRoot, "legacy-settings", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            "{\"hoverTranslationMode\":\"smart\",\"smartDictionaryFallbackMode\":\"automatic\",\"translationTargetLanguage\":\"ja\"}");

        AppSettings loaded = await new SettingsService(path).LoadAsync();
        Assert(loaded.SmartDictionaryBackend == SmartDictionaryBackends.Auto,
            "Existing settings must default to automatic backend selection.");
        Assert(loaded.SmartDictionaryRankerModel == SmartDictionaryRankerModels.GlossBert,
            "Existing settings must retain GlossBERT as the stable default ranker.");
        Assert(loaded.SmartDictionarySourceLanguage == SmartDictionarySourceLanguages.Auto,
            "Existing settings must default to automatic source-language detection.");
        Assert(loaded.SmartDictionaryKeepWarm,
            "Existing settings must keep the smart dictionary model warm by default.");
        Assert(loaded.SmartPhraseTranslationMode == SmartPhraseTranslationModes.Off,
            "Existing settings must not silently enable the optional phrase model.");
        Assert(loaded.SmartPhraseTranslationBackend == SmartPhraseTranslationBackends.Cpu,
            "Existing settings must retain the compatible CPU phrase backend.");
        await new SettingsService(path).SaveAsync(loaded);
        string migratedJson = await File.ReadAllTextAsync(path);
        Assert(!migratedJson.Contains("SmartDictionaryFallbackMode", StringComparison.OrdinalIgnoreCase) &&
               !migratedJson.Contains("TranslationTargetLanguage", StringComparison.OrdinalIgnoreCase),
            "Obsolete translation fallback settings were written back after migration.");

        string gpuPath = Path.Combine(tempRoot, "legacy-gpu-settings", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(gpuPath)!);
        await File.WriteAllTextAsync(gpuPath, "{\"smartDictionaryBackend\":\"gpu\"}");
        AppSettings migrated = await new SettingsService(gpuPath).LoadAsync();
        Assert(migrated.SmartDictionaryBackend == SmartDictionaryBackends.Cpu,
            "The legacy GPU smart-ranker backend must migrate to CPU.");

        string invalidPhrasePath = Path.Combine(
            tempRoot,
            "invalid-phrase-settings",
            "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(invalidPhrasePath)!);
        await File.WriteAllTextAsync(
            invalidPhrasePath,
            "{\"smartPhraseTranslationMode\":\"unsupported\",\"smartPhraseTranslationBackend\":\"unsupported\"}");
        AppSettings invalidPhrase = await new SettingsService(invalidPhrasePath).LoadAsync();
        Assert(invalidPhrase.SmartPhraseTranslationMode == SmartPhraseTranslationModes.Off &&
               invalidPhrase.SmartPhraseTranslationBackend == SmartPhraseTranslationBackends.Cpu &&
               Directory.EnumerateFiles(
                       Path.GetDirectoryName(invalidPhrasePath)!,
                       "settings.json.broken-*",
                       SearchOption.TopDirectoryOnly)
                   .Any(),
            "Invalid phrase settings were silently coerced instead of backed up and reset.");
    });

    await RunAsync("Removed ranker profiles load as GlossBERT", async () =>
    {
        // SANDWiCH and GlossDeBERTa are no longer product profiles. A settings
        // file that still selects one must fall back to the default ranker in
        // memory without being treated as broken, which would back it up and
        // reset every unrelated choice the user made.
        string[] removedProfiles = ["sandwich", "SANDWiCH", "deberta", "GlossDeBERTa"];
        for (int index = 0; index < removedProfiles.Length; index++)
        {
            string removed = removedProfiles[index];
            string directory = Path.Combine(tempRoot, $"removed-ranker-profile-{index}");
            string path = Path.Combine(directory, "settings.json");
            Directory.CreateDirectory(directory);
            string json =
                "{\"selectedPresetId\":\"vn-ultra\",\"smartDictionaryBackend\":\"cpu\"," +
                $"\"smartDictionaryRankerModel\":\"{removed}\"," +
                "\"smartDictionarySourceLanguage\":\"en\",\"smartDictionaryKeepWarm\":false}";
            await File.WriteAllTextAsync(path, json);

            AppSettings loaded = await new SettingsService(path).LoadAsync();
            Assert(SmartDictionaryRankerModels.Normalize(removed) ==
                       SmartDictionaryRankerModels.GlossBert &&
                   loaded.SmartDictionaryRankerModel == SmartDictionaryRankerModels.GlossBert &&
                   SemanticRankerProfileResolver.FromSettings(loaded) ==
                       RankerCpuModelProfile.GlossBert,
                $"The removed ranker profile '{removed}' did not load as GlossBERT.");
            Assert(loaded.SelectedPresetId == "vn-ultra" &&
                   loaded.SmartDictionaryBackend == SmartDictionaryBackends.Cpu &&
                   loaded.SmartDictionarySourceLanguage == SmartDictionarySourceLanguages.English &&
                   !loaded.SmartDictionaryKeepWarm,
                $"Loading the removed ranker profile '{removed}' reset unrelated settings.");
            Assert(!Directory.EnumerateFiles(
                       directory,
                       "settings.json.broken-*",
                       SearchOption.TopDirectoryOnly).Any() &&
                   await File.ReadAllTextAsync(path) == json,
                $"The removed ranker profile '{removed}' made the settings file look broken.");
        }

        Assert(SmartDictionaryRankerModels.Normalize("CONSEC") ==
                   SmartDictionaryRankerModels.Consec,
            "The ConSeC research profile no longer survives normalization.");
    });

#if WINDOWS
    await RunAsync("Per-app DPI compatibility", () => DpiCompatibilityServiceTests.RunAsync(tempRoot));
#else
    MacSmoke.Skip(
        "Per-app DPI compatibility",
        "DpiCompatibilityService lives in the WPF assembly (registry, user32)");
#endif

    await RunAsync("Model activity lifecycle gate", ModelActivityGateTests.RunAsync);

    await RunAsync(
        "Hover coordinator ordering and lifecycle",
        async () =>
        {
            await HoverCoordinatorTests.RunAsync(tempRoot);
            // Typed provenance and host-supplied labels, through the same
            // coordinator and pipeline under the Windows and the macOS labels.
            await HoverProvenanceTests.RunAsync(tempRoot);
            // What a request-answering host (CoreHost) adds: hover outcomes,
            // the model-status snapshot, host unloads, no phrase model.
            await HoverCoordinatorHostApiTests.RunAsync(tempRoot);
        });

#if WINDOWS
    await RunAsync(
        "Magpie config and backup safety",
        () => MagpieSafetyTests.RunAsync(tempRoot));
#else
    MacSmoke.Skip(
        "Magpie config and backup safety",
        "MagpieConfigEditor, MagpieEngineService and AutoScaleService; copies System32 ping.exe and drives powershell.exe");
#endif

#if WINDOWS
    await RunAsync(
        "Application lifecycle and theme safety",
        ApplicationLifecycleSafetyTests.RunAsync);
#else
    MacSmoke.Skip(
        "Application lifecycle and theme safety",
        "a named EventWaitHandle and PendingShowRequestState from the WPF assembly");
#endif

    await RunAsync(
        "Smart semantic-ranker cold start",
        SemanticRankerColdStartTests.RunAsync);

    await RunAsync("Local dictionary lookup", async () =>
    {
        string mergedMeanings = LocalDictionaryService.MergeBundledMeanings(
            "первый; ба́нк; второй",
            "банк; третий; ПЕРВЫЙ");
        Assert(mergedMeanings == "первый; ба́нк; второй; третий",
            $"Broad dictionary union did not preserve order and remove equivalent meanings: {mergedMeanings}");

        string boundedMeanings = LocalDictionaryService.MergeBundledMeanings(
            string.Join("; ", Enumerable.Range(0, 96).Select(index => $"основной-{index}")),
            string.Join("; ", Enumerable.Range(96, 96).Select(index => $"дополнительный-{index}")));
        Assert(boundedMeanings.Split(';', StringSplitOptions.RemoveEmptyEntries).Length == 32 &&
               boundedMeanings.Length <= 4 * 1024,
            "Broad dictionary union did not enforce its pathological-input bound.");

        long managedBefore = SettledManagedBytes();
        Stopwatch loadTimer = Stopwatch.StartNew();
        string bundledDictionaries = Path.Combine(AppContext.BaseDirectory, "Dictionaries");
        string isolatedUserDictionaries = Path.Combine(tempRoot, "local-dictionary-user");
        Directory.CreateDirectory(isolatedUserDictionaries);
        int managedMemoryReleaseCount = 0;
        LocalDictionaryService dictionary = new(
            bundledDictionaries,
            isolatedUserDictionaries,
            managedMemoryRelease: () => managedMemoryReleaseCount++);
        await dictionary.LoadAsync();
        loadTimer.Stop();
        Assert(dictionary.EntryCount > 880_000,
            "Expected the complete layered English and Japanese indexes.");
        Assert(dictionary.SourceCount >= 11,
            "Expected all base, Wiktionary, dialogue and role-separated VN dictionaries.");

        IReadOnlyList<ScaffoldDictionaryMatch> packagedScaffold =
            await dictionary.FindEnglishScaffoldMatchesAsync(
                "He was on the verge of tears.");
        Assert(packagedScaffold.Any(match =>
                   match.MatchedEnglishKey == "on the verge of" &&
                   match.Translation == "на грани"),
            "The packaged VN scaffold asset was missing or not classified as scaffold-only.");

        string? massiveDialogue = await dictionary.LookupExactEnglishDialogueAsync(
            "Activate robot vacuum cleaner.");
        Assert(massiveDialogue == "включи робот пылесос",
            $"The packaged MASSIVE exact-dialogue layer was missing: {massiveDialogue}");

        DictionaryLookupResult layeredBank = await dictionary.LookupAsync(
            "The bank approved the loan.", "bank", 4);
        int baseBankIndex = layeredBank.Translation.IndexOf(
            "бе́рег",
            StringComparison.OrdinalIgnoreCase);
        int kaikkiBankIndex = layeredBank.Translation.IndexOf(
            "финансовое учреждение",
            StringComparison.OrdinalIgnoreCase);
        Assert(layeredBank.Found && baseBankIndex >= 0 && kaikkiBankIndex > baseBankIndex,
            $"Overlapping FreeDict and Kaikki meanings were not united in source order: {layeredBank.Translation}");

        DictionaryLookupResult phrase = await dictionary.LookupAsync(
            "Good morning!", "morning", 7);
        Assert(phrase.Found && phrase.IsPhrase && phrase.Translation.Contains("добро", StringComparison.OrdinalIgnoreCase),
            "English phrase lookup failed.");

        const string vergeContext =
            "He felt like he was teetering on the verge of utter confusion.";
        DictionaryLookupResult lexicalLongPhrase = await dictionary.LookupAsync(
            vergeContext,
            "verge",
            vergeContext.IndexOf("verge", StringComparison.Ordinal));
        Assert(lexicalLongPhrase.Found &&
               lexicalLongPhrase.IsPhrase &&
               lexicalLongPhrase.Source.Equals(
                   "on the verge of",
                   StringComparison.OrdinalIgnoreCase) &&
               lexicalLongPhrase.Translation.Contains(
                   "гра",
                   StringComparison.OrdinalIgnoreCase),
            $"A four-token lexical MWE was hidden by the dialogue safety limit: {lexicalLongPhrase}");

        const string headContext = "Alice shook her head and sighed.";
        DictionaryLookupResult canonicalHeadPhrase = await dictionary.LookupAsync(
            headContext,
            "shook",
            headContext.IndexOf("shook", StringComparison.Ordinal));
        Assert(canonicalHeadPhrase.Found &&
               canonicalHeadPhrase.IsPhrase &&
               canonicalHeadPhrase.Source.Equals(
                   "shook her head",
                   StringComparison.OrdinalIgnoreCase) &&
               canonicalHeadPhrase.Translation.Contains(
                   "голов",
                   StringComparison.OrdinalIgnoreCase),
            $"Canonical MWE lookup did not normalize shook/her: {canonicalHeadPhrase}");

        const string exactDialogue = "He shook his head";
        DictionaryLookupResult phraseWithDialogueContext = await dictionary.LookupAsync(
            exactDialogue,
            "shook",
            exactDialogue.IndexOf("shook", StringComparison.Ordinal));
        Assert(phraseWithDialogueContext.IsPhrase &&
               phraseWithDialogueContext.ContextTranslation.Contains(
                   "голов",
                   StringComparison.OrdinalIgnoreCase),
            "Exact Tatoeba lookup did not run alongside lexical MWE lookup.");

        const string embeddedDialogue =
            "Yesterday he shook his head and laughed loudly.";
        DictionaryLookupResult safeEmbeddedDialogue = await dictionary.LookupAsync(
            embeddedDialogue,
            "laughed",
            embeddedDialogue.IndexOf("laughed", StringComparison.Ordinal));
        Assert(safeEmbeddedDialogue.ContextTranslation.Length == 0,
            "A long internal Tatoeba fragment was mistaken for the complete OCR sentence.");

        const string separatedContext =
            "Did you get separated from your friends?";
        DictionaryLookupResult separatedPhrase = await dictionary.LookupAsync(
            separatedContext,
            "separated",
            separatedContext.IndexOf("separated", StringComparison.Ordinal));
        Assert(separatedPhrase.Found &&
               separatedPhrase.IsPhrase &&
               separatedPhrase.Source.Equals(
                   "get separated from",
                   StringComparison.OrdinalIgnoreCase) &&
               separatedPhrase.Translation == "отделиться от",
            $"The get-passive collocation was not selected as a phrase: {separatedPhrase}");

        DictionaryLookupResult utterConfusion = await dictionary.LookupAsync(
            vergeContext,
            "confusion",
            vergeContext.IndexOf("confusion", StringComparison.Ordinal));
        Assert(utterConfusion.Found &&
               utterConfusion.IsPhrase &&
               utterConfusion.Source.Equals(
                   "utter confusion",
                   StringComparison.OrdinalIgnoreCase) &&
               utterConfusion.Translation == "полное смятение",
            $"The collocation layer did not compact utter confusion: {utterConfusion}");

        DictionaryLookupResult inflected = await dictionary.LookupAsync(
            "I was waiting for you.", "waiting", 8);
        Assert(inflected.Found && inflected.Translation.Contains("жд", StringComparison.OrdinalIgnoreCase),
            "English inflection fallback failed.");

        DictionaryLookupResult japanese = await dictionary.LookupAsync(
            "星がとても綺麗ですね。", "星がとても綺麗ですね", 1);
        Assert(japanese.Found && japanese.Language == "JA", "Japanese dictionary lookup failed.");

        DictionaryLookupResult dialogue = await dictionary.LookupAsync(
            "Let's try something.", "try", 7);
        Assert(dialogue.Found &&
               dialogue.ContextTranslation.Contains("попроб", StringComparison.OrdinalIgnoreCase),
            "The exact English dialogue sentence was not available as context.");

        DictionaryLookupResult colloquial = await dictionary.LookupAsync(
            "I didn't know.", "didn't", 3);
        Assert(colloquial.Found &&
               colloquial.Translation.Contains("не", StringComparison.OrdinalIgnoreCase),
            "The visual-novel contractions dictionary was not loaded.");

        const string niceToMeet = "\"Nice to meet you!\"";
        DictionaryLookupResult greetingWord = await dictionary.LookupAsync(
            niceToMeet,
            "meet",
            niceToMeet.IndexOf("meet", StringComparison.Ordinal));
        Assert(greetingWord.Found &&
               greetingWord.ContextTranslation.Contains("познаком", StringComparison.OrdinalIgnoreCase),
            $"An exact Tatoeba sentence must accompany the hovered word: {greetingWord.ContextTranslation}");

        DictionaryLookupResult slangContraction = await dictionary.LookupAsync(
            "\"Whaddya say? How about it?\"", "Whaddya", 1);
        Assert(slangContraction.Found &&
               slangContraction.Translation.Contains("что", StringComparison.OrdinalIgnoreCase),
            $"The slang contraction layer must cover 'whaddya': {slangContraction.Translation}");

        // OCR homoglyph repair: a Cyrillic "а" inside Latin text must not
        // break phrase lookup, and genuine Russian must stay untouched.
        string repairedMixed = OcrTextRules.RepairMixedScriptText(
            "She said that happily, almost prancing аrоund.");
        Assert(repairedMixed == "She said that happily, almost prancing around.",
            $"Cyrillic homoglyphs inside Latin tokens were not repaired: {repairedMixed}");
        Assert(OcrTextRules.RepairMixedScriptText("Привет, мир! Hello world") ==
               "Привет, мир! Hello world",
            "Pure-script tokens must never be rewritten.");
        Assert(OcrTextRules.RepairMixedScriptText("Привeт") == "Привет",
            "A Latin homoglyph inside a Russian token must be repaired toward Cyrillic.");

        DictionaryLookupResult beaming = await dictionary.LookupAsync(
            "A beaming, innocent smile appeared on her shining face.", "beaming", 4);
        Assert(beaming.Found &&
               beaming.Translation.Contains("сия", StringComparison.OrdinalIgnoreCase) &&
               !beaming.Translation.Contains("балк", StringComparison.OrdinalIgnoreCase),
            "The VN dictionary must not reduce the adjective 'beaming' to the structural noun 'beam'.");

        DictionaryLookupResult sighed = await dictionary.LookupAsync(
            "He shook his head and sighed.", "sighed", 25);
        Assert(sighed.Found &&
               sighed.Translation.StartsWith(
                   "вздыхать; вздохнуть",
                   StringComparison.Ordinal) &&
               !DictionaryMeaningParser.SplitMeanings(sighed.Translation)
                   .Any(value => value is "вздохнул" or "вздохнула"),
            $"The VN-soft layer must keep 'sighed' gender-neutral while preserving broad senses: {sighed.Translation}");

        DictionaryLookupResult teetering = await dictionary.LookupAsync(
            "He was teetering on the verge of utter confusion.", "teetering", 10);
        Assert(teetering.Found &&
               teetering.Translation.Contains("грани", StringComparison.OrdinalIgnoreCase),
            $"The VN dictionary did not include the contextual sense of 'teetering': {teetering.Source} -> {teetering.Translation}");

        const string prancingContext =
            "She said that happily, almost prancing around.";
        DictionaryLookupResult prancingPhrase = await dictionary.LookupAsync(
            prancingContext,
            "prancing",
            prancingContext.IndexOf("prancing", StringComparison.Ordinal));
        Assert(prancingPhrase.Found &&
               prancingPhrase.IsPhrase &&
               prancingPhrase.Source.Equals(
                   "prancing around",
                   StringComparison.OrdinalIgnoreCase) &&
               prancingPhrase.Translation.Contains(
                   "вприпрыжку",
                   StringComparison.OrdinalIgnoreCase),
            $"Inflected motion MWE lookup failed: {prancingPhrase}");

        const string shiningContext =
            "A beaming, innocent smile appeared on her shining face.";
        DictionaryLookupResult shiningPhrase = await dictionary.LookupAsync(
            shiningContext,
            "shining",
            shiningContext.IndexOf("shining", StringComparison.Ordinal));
        Assert(shiningPhrase.Found &&
               shiningPhrase.IsPhrase &&
               shiningPhrase.Source.Equals(
                   "shining face",
                   StringComparison.OrdinalIgnoreCase) &&
               shiningPhrase.Translation == "сияющее лицо",
            $"The collocation layer did not compact shining face: {shiningPhrase}");

        DictionaryLookupResult beamed = await dictionary.LookupAsync(
            "Her face beamed with joy.", "beamed", 9);
        Assert(beamed.Found &&
               beamed.Translation.Contains("сия", StringComparison.OrdinalIgnoreCase) &&
               beamed.Translation.Contains("балк", StringComparison.OrdinalIgnoreCase),
            $"Lemma-level senses must preserve both meanings of 'beam': {beamed.Translation}");

        DictionaryLookupResult sighing = await dictionary.LookupAsync(
            "He was sighing all night.", "sighing", 7);
        Assert(sighing.Found &&
               sighing.Translation.Contains("вздыхать", StringComparison.OrdinalIgnoreCase),
            $"Lemma-level lookup lost the conversational verb surface for 'sighing': {sighing.Translation}");

        DictionaryLookupResult teetered = await dictionary.LookupAsync(
            "He teetered on the brink of collapse.", "teetered", 3);
        Assert(teetered.Found &&
               teetered.Translation.Contains("грани", StringComparison.OrdinalIgnoreCase),
            $"Lemma-level lookup must expose the brink sense for 'teetered': {teetered.Translation}");

        DictionaryLookupResult ohWell = await dictionary.LookupAsync(
            "Oh well...", "Oh well", 1);
        Assert(ohWell.Found &&
               ohWell.Translation.Contains("что ж", StringComparison.OrdinalIgnoreCase) &&
               !ohWell.Translation.Contains("мдэ", StringComparison.OrdinalIgnoreCase),
            $"The curated VN phrase must override noisy base candidates: {ohWell.Translation}");

        DictionaryLookupResult areYouLost = await dictionary.LookupAsync(
            "Are you lost?", "Are you lost", 2);
        Assert(areYouLost.Found && areYouLost.Translation == "Вы заблудились?",
            $"The dictionary must not guess gender for 'Are you lost?': {areYouLost.Translation}");

        DictionaryLookupResult japaneseDialogue = await dictionary.LookupAsync(
            "何かしてみましょう。", "してみましょう", 5);
        Assert(japaneseDialogue.Found &&
               japaneseDialogue.ContextTranslation.Contains(
                   "попроб",
                   StringComparison.OrdinalIgnoreCase),
            "The exact Japanese dialogue sentence was not available as context.");

        long managedDictionaryBytes = AssertManagedBudget(
            managedBefore,
            256L * 1024 * 1024,
            "Layered dictionaries");
        Console.WriteLine(
            $"    dictionary: {dictionary.EntryCount:N0} entries, {dictionary.SourceCount} sources, " +
            $"{managedDictionaryBytes / 1024d / 1024d:N1} MB managed, {loadTimer.ElapsedMilliseconds:N0} ms load");

        long managedBeforeSenses = SettledManagedBytes();
        Stopwatch senseLoadTimer = Stopwatch.StartNew();
        await dictionary.LoadSenseMetadataAsync();
        senseLoadTimer.Stop();
        Assert(dictionary.SenseMetadataKeyCount > 140_000 &&
               dictionary.SenseMetadataCandidateCount > 390_000,
            "The complete packaged aligned-sense index was unexpectedly truncated.");
        long managedSenseBytes = AssertManagedBudget(
            managedBeforeSenses,
            128L * 1024 * 1024,
            "Aligned sense metadata");
        Console.WriteLine(
            $"    senses: {dictionary.SenseMetadataKeyCount:N0} keys, " +
            $"{dictionary.SenseMetadataCandidateCount:N0} candidates, " +
            $"{managedSenseBytes / 1024d / 1024d:N1} MB managed, " +
            $"{senseLoadTimer.ElapsedMilliseconds:N0} ms load");
        DictionaryLookupResult packagedSenses =
            await dictionary.LookupWithSenseMetadataAsync(
                "charge",
                "charge",
                1);
        Assert(packagedSenses.SenseCandidates.Count > 1 &&
               packagedSenses.SenseCandidates.All(candidate =>
                   candidate.Translation.Length > 0 &&
                   candidate.EnglishDefinition.Length > 0 &&
                   !string.Equals(
                       candidate.EnglishDefinition,
                       "Translations",
                       StringComparison.OrdinalIgnoreCase)),
            "The packaged senses were not aligned to translations and definitions.");
        DictionaryLookupResult packagedRecordSenses =
            await dictionary.LookupWithSenseMetadataAsync(
                "They broke the record.",
                "record",
                15);
        Assert(packagedRecordSenses.SenseCandidates.Count > 1 &&
               packagedRecordSenses.SenseCandidates.All(candidate =>
                   !string.Equals(
                       candidate.EnglishDefinition,
                       "Translations",
                       StringComparison.OrdinalIgnoreCase)),
            "Kaikki section headings leaked into the aligned sense definitions.");

        DictionaryLookupResult packagedSighSenses =
            await dictionary.LookupWithSenseMetadataAsync(
                "He was sighing all night.",
                "sighing",
                7);
        Assert(packagedSighSenses.SenseCandidates.Any(candidate =>
                   candidate.Translation == "вздыхать" &&
                   candidate.PartOfSpeech == "verb") &&
               packagedSighSenses.SenseCandidates.Any(candidate =>
                   candidate.Translation == "вздох" &&
                   candidate.PartOfSpeech == "noun"),
            "The packaged VN-soft sigh inventory did not retain rankable verb/noun senses.");

        SmartDictionarySenseInventory packagedSighInventory =
            SmartDictionarySenseResolver.GroupCandidates(
                "He was sighing all night.",
                packagedSighSenses.Source,
                7,
                packagedSighSenses.SenseCandidates);
        SmartDictionaryRankerPlan packagedSighPlan =
            SmartDictionarySenseResolver.CreateRankerPlan(
                "He was sighing all night.",
                packagedSighSenses.Source,
                7,
                packagedSighInventory.Groups);
        SmartDictionaryHeuristicSelection? packagedSighSelection =
            SmartDictionaryHeuristicRanker.TrySelect(
                "He was sighing all night.",
                packagedSighSenses.Source,
                7,
                packagedSighPlan.Outcomes
                    .Select(outcome => outcome.Translation)
                    .ToArray(),
                "English");
        Assert(packagedSighInventory.InferredPartOfSpeech == "verb" &&
               packagedSighPlan.Outcomes.All(outcome => outcome.Translation != "ах") &&
               packagedSighSelection is { Translation: "вздыхать" },
            $"The packaged sigh senses did not produce the safe progressive plan: {packagedSighSelection}");

        const string packagedTeeterContext =
            "He teetered on the brink of collapse.";
        DictionaryLookupResult packagedTeeterSenses =
            await dictionary.LookupWithSenseMetadataAsync(
                packagedTeeterContext,
                "teetered",
                3);
        Assert(packagedTeeterSenses.SenseCandidates.Any(candidate =>
                   candidate.Translation == "быть на грани" &&
                   candidate.EnglishDefinition.Contains(
                       "undesirable state",
                       StringComparison.Ordinal)) &&
               packagedTeeterSenses.SenseCandidates.Any(candidate =>
                   candidate.Translation == "качаться"),
            "The packaged VN-soft teeter inventory did not expose both figurative and literal senses.");

        SmartDictionarySenseInventory packagedTeeterInventory =
            SmartDictionarySenseResolver.GroupCandidates(
                packagedTeeterContext,
                packagedTeeterSenses.Source,
                3,
                packagedTeeterSenses.SenseCandidates);
        SmartDictionaryRankerPlan packagedTeeterPlan =
            SmartDictionarySenseResolver.CreateRankerPlan(
                packagedTeeterContext,
                packagedTeeterSenses.Source,
                3,
                packagedTeeterInventory.Groups);
        SmartDictionaryHeuristicSelection? packagedTeeterSelection =
            SmartDictionaryHeuristicRanker.TrySelect(
                packagedTeeterContext,
                packagedTeeterSenses.Source,
                3,
                packagedTeeterPlan.Outcomes
                    .Select(outcome => outcome.Translation)
                    .ToArray(),
                "English");
        Assert(packagedTeeterInventory.InferredPartOfSpeech == "verb" &&
               packagedTeeterSelection is { Translation: "быть на грани" },
            $"The packaged teeter senses did not select the figurative brink plan: {packagedTeeterSelection}");

        DictionaryLookupResult packagedLemming =
            await dictionary.LookupWithSenseMetadataAsync(
                "The lemming was small.",
                "lemming",
                4);
        Assert(packagedLemming.Found &&
               !packagedLemming.Translation.Contains(
                   "дай",
                   StringComparison.OrdinalIgnoreCase) &&
               !packagedLemming.Translation.Contains(
                   "позволь",
                   StringComparison.OrdinalIgnoreCase) &&
               packagedLemming.SenseCandidates.All(candidate =>
                   candidate.Translation is not "дай мне" and not "позволь мне"),
            $"The packaged VN-soft 'lemme' entry shadowed the exact noun 'lemming': {packagedLemming}");

        const string damagedArticleContext =
            "He felt like he was teetering on the ver utter confusion.";
        DictionaryLookupResult definiteArticle =
            await dictionary.LookupWithSenseMetadataAsync(
                damagedArticleContext,
                "the",
                damagedArticleContext.IndexOf("the", StringComparison.Ordinal));
        Assert(definiteArticle.Found &&
               definiteArticle.Translation == "определённый артикль" &&
               definiteArticle.SenseCandidates.Count == 0,
            $"The modern definite article was contaminated by an obsolete pronoun sense: {definiteArticle}");

        const string frequentRemark = "[Coco] \"I get that a lot.\"";
        foreach (string hoveredWord in new[] { "get", "that" })
        {
            DictionaryLookupResult frequentRemarkPhrase =
                await dictionary.LookupWithSenseMetadataAsync(
                    frequentRemark,
                    hoveredWord,
                    frequentRemark.IndexOf(hoveredWord, StringComparison.Ordinal));
            Assert(frequentRemarkPhrase.Found &&
                   frequentRemarkPhrase.IsPhrase &&
                   frequentRemarkPhrase.Source.Equals(
                       "I get that a lot",
                       StringComparison.OrdinalIgnoreCase) &&
                   frequentRemarkPhrase.Translation == "я часто это слышу" &&
                   frequentRemarkPhrase.SenseCandidates.Count == 0,
                $"The exact first-person remark was split into ambiguous words: {frequentRemarkPhrase}");
        }
        GC.KeepAlive(dictionary);
        await dictionary.UnloadAndReleaseMemoryAsync();
        Assert(dictionary.EntryCount == 0 &&
               dictionary.SourceCount == 0 &&
               dictionary.SenseMetadataKeyCount == 0 &&
               dictionary.SenseMetadataCandidateCount == 0 &&
               dictionary.VersionToken == "unloaded" &&
               managedMemoryReleaseCount == 1,
            "Dictionary layers were not released after leaving dictionary mode.");
        await dictionary.UnloadAndReleaseMemoryAsync();
        Assert(managedMemoryReleaseCount == 1,
            "An already-empty dictionary repeated the expensive managed-memory release.");
    });

    await RunAsync("Aligned dictionary sense metadata", async () =>
    {
        Assert(LocalDictionaryService.IsSenseMetadataFile(
                   "eng-rus-enwiktionary-senses.tsv.gz") &&
               !LocalDictionaryService.IsSenseMetadataFile(
                   "eng-rus-enwiktionary.tsv.gz"),
            "Sense sidecars were not distinguished from ordinary dictionary layers.");

        string sensePath = Path.Combine(
            tempRoot,
            "eng-rus-enwiktionary-senses.tsv.gz");
        await using (FileStream file = File.Create(sensePath))
        await using (GZipStream gzip = new(file, CompressionLevel.SmallestSize))
        await using (StreamWriter writer = new(gzip, new UTF8Encoding(false)))
        {
            await writer.WriteLineAsync(
                "bank\t\u0431\u0430\u043d\u043a\tan institution that handles money\tfinance");
            await writer.WriteLineAsync(
                "bank\t\u0431\u0430\u043d\u043a\tan  institution that handles money\tfinance");
            await writer.WriteLineAsync(
                "bank\t\u0431\u0435\u0440\u0435\u0433\tthe land alongside a river\tgeography");
            await writer.WriteLineAsync(
                "sideways\t\u0438\u0441\u043a\u043e\u0441\u0430\ttoward or from one side\tadverb");
            await writer.WriteLineAsync("malformed\trow");
        }

        // The bundled folder stays the one the default constructor resolves
        // (next to the executable): the curated "sideways" check below needs
        // the shipped VN layers. Only the user layers move to the temp root,
        // so a load neither creates %LOCALAPPDATA%\AsteriaScale\Dictionaries
        // (~/Library/Application Support on macOS) nor reads whatever user
        // layers the machine running the suite happens to have there.
        LocalDictionaryService dictionary = new(
            Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
                "Dictionaries"),
            Path.Combine(tempRoot, "sense-user-dictionaries"),
            sensePath);
        await dictionary.LoadAsync();
        int ordinaryEntryCount = dictionary.EntryCount;
        int ordinarySourceCount = dictionary.SourceCount;
        string ordinaryVersion = dictionary.VersionToken;
        DictionaryLookupResult ordinary = await dictionary.LookupAsync(
            "The bank approved the loan.", "bank", 4);
        Assert(ordinary.Found && ordinary.SenseCandidates.Count == 0,
            "Ordinary dictionary lookup must not eagerly expose or load sense metadata.");
        Assert(dictionary.VersionToken == ordinaryVersion,
            "Ordinary dictionary lookup unexpectedly loaded the optional sense sidecar.");

        DictionaryLookupResult enriched = await dictionary.LookupWithSenseMetadataAsync(
            "I sat on the bank of the river.", "bank", 13);
        Assert(enriched.Found && enriched.SenseCandidates.Count == 2,
            $"Aligned senses were not loaded or stably deduplicated: {enriched.SenseCandidates.Count}");
        Assert(enriched.SenseCandidates[0] == new DictionarySenseCandidate(
                   "\u0431\u0430\u043d\u043a",
                   "an institution that handles money",
                   "finance") &&
               enriched.SenseCandidates[1] == new DictionarySenseCandidate(
                   "\u0431\u0435\u0440\u0435\u0433",
                   "the land alongside a river",
                   "geography"),
            "Aligned senses did not preserve source order and definition/register alignment.");
        Assert(dictionary.EntryCount == ordinaryEntryCount &&
               dictionary.SourceCount == ordinarySourceCount,
            "The optional sense sidecar leaked into normal entries or source accounting.");
        Assert(dictionary.VersionToken != ordinaryVersion,
            "The smart-result cache token did not incorporate loaded sense metadata.");

        DictionaryLookupResult curated = await dictionary.LookupWithSenseMetadataAsync(
            "She gave him a sideways glance.", "sideways", 15);
        Assert(curated.Found && curated.SenseCandidates.Count == 0,
            "Broad Wiktionary metadata must not supersede a trusted user/VN surface entry.");

        await dictionary.UnloadAsync();
        Assert(dictionary.EntryCount == 0 && dictionary.SourceCount == 0,
            "Sense metadata was not released with the dictionary.");
    });

    await RunAsync("Smart dictionary sense grouping and surface selection", () =>
    {
        DictionarySenseCandidate[] sighSenses =
        [
            new(
                "вздыхать",
                "To inhale deeply and audibly.",
                "",
                "verb"),
            new(
                "вздохнуть",
                "  to INHALE deeply and audibly.  ",
                "",
                "v"),
            new(
                "вздох",
                "A deep audible breath.",
                "",
                "noun"),
            new(
                "ох",
                "An expression of fatigue or sorrow.",
                "разг.",
                "intj"),
            new(
                " ВЗДОХНУТЬ ",
                "To inhale deeply and audibly.",
                "устар.",
                "VERB"),
            new(
                "",
                "Malformed empty translation.",
                "",
                "noun")
        ];

        SmartDictionarySenseInventory verbInventory =
            SmartDictionarySenseResolver.GroupCandidates(
                "She sighed and closed her eyes.",
                "sighed",
                5,
                sighSenses);
        Assert(!verbInventory.PartOfSpeechFilterApplied &&
               verbInventory.InferredPartOfSpeech == "verb" &&
               verbInventory.PartOfSpeechConfidence >= 0.9 &&
               verbInventory.Groups.Count == 3,
            "A POS filter that would collapse the inventory to one sense must fail open.");
        SmartDictionarySenseGroup breathingSense = verbInventory.Groups.Single(
            group => group.PartOfSpeech == "verb");
        Assert(breathingSense.SourceIndex == 0 &&
               breathingSense.PartOfSpeech == "verb" &&
               breathingSense.Surfaces.Count == 2 &&
               breathingSense.Surfaces[0].Translation == "вздыхать" &&
               breathingSense.Surfaces[1].Translation == "вздохнуть",
            "Equivalent definition/POS rows were not grouped, deduplicated and kept in source order.");

        SmartDictionarySenseInventory nounInventory =
            SmartDictionarySenseResolver.GroupCandidates(
                "A sigh escaped her lips.",
                "sigh",
                2,
                sighSenses);
        Assert(!nounInventory.PartOfSpeechFilterApplied &&
               nounInventory.InferredPartOfSpeech == "noun" &&
               nounInventory.Groups.Count == 3 &&
               nounInventory.Groups.Any(group =>
                   group.EnglishDefinition == "A deep audible breath."),
            "A high-confidence noun inference must remain fail-open with one matching sense.");

        SmartDictionarySenseInventory failOpenInventory =
            SmartDictionarySenseResolver.GroupCandidates(
                "She sighed quietly.",
                "sighed",
                5,
                [
                    new DictionarySenseCandidate(
                        "вздохнуть",
                        "To inhale deeply.",
                        "",
                        ""),
                    new DictionarySenseCandidate(
                        "вздох",
                        "A deep audible breath.",
                        "",
                        "noun")
                ]);
        Assert(!failOpenInventory.PartOfSpeechFilterApplied &&
               failOpenInventory.Groups.Count == 2,
            "Incomplete POS metadata must fail open instead of erasing all senses.");

        SmartDictionarySenseInventory sameDefinitionDifferentPos =
            SmartDictionarySenseResolver.GroupCandidates(
                "",
                "record",
                0,
                [
                    new DictionarySenseCandidate(
                        "запись",
                        "A stored account of information.",
                        "",
                        "noun"),
                    new DictionarySenseCandidate(
                        "записывать",
                        "A stored account of information.",
                        "",
                        "verb")
                ]);
        Assert(sameDefinitionDifferentPos.Groups.Count == 2 &&
               sameDefinitionDifferentPos.Groups.Select(group => group.PartOfSpeech)
                   .SequenceEqual(["noun", "verb"]),
            "Identical definition text with different POS must remain separate rankable senses.");

        SmartDictionarySenseInventory leftInventory =
            SmartDictionarySenseResolver.GroupCandidates(
                "He turned left.",
                "left",
                10,
                [
                    new DictionarySenseCandidate(
                        "левый",
                        "Situated on the left side.",
                        "",
                        "adj"),
                    new DictionarySenseCandidate(
                        "налево",
                        "Towards the left side.",
                        "",
                        "adverb")
                ]);
        Assert(!leftInventory.PartOfSpeechFilterApplied &&
               leftInventory.InferredPartOfSpeech == "adv" &&
               leftInventory.Groups.Count == 2,
            "A directional POS hint must not collapse a two-sense inventory to one option.");

        DictionarySenseCandidate[] bedSenses =
        [
            new("кровать", "A piece of furniture for sleeping.", "", "noun"),
            new("постель", "A place prepared for sleeping.", "", "noun"),
            new("укладывать", "To put somebody to bed.", "", "verb"),
            new("встраивать", "To fix something firmly into a base.", "", "verb")
        ];
        const string wentToBed = "She went to bed early.";
        SmartDictionarySenseInventory prepositionalTo =
            SmartDictionarySenseResolver.GroupCandidates(
                wentToBed,
                "bed",
                wentToBed.IndexOf("bed", StringComparison.Ordinal),
                bedSenses);
        Assert(!prepositionalTo.PartOfSpeechFilterApplied &&
               prepositionalTo.Groups.Count == 4,
            "A prepositional 'to' after a motion verb must not erase noun senses.");

        const string decidedToBed = "She decided to bed the seedlings.";
        SmartDictionarySenseInventory infinitiveTo =
            SmartDictionarySenseResolver.GroupCandidates(
                decidedToBed,
                "bed",
                decidedToBed.IndexOf("bed", StringComparison.Ordinal),
                bedSenses);
        Assert(infinitiveTo.PartOfSpeechFilterApplied &&
               infinitiveTo.Groups.Count == 2 &&
               infinitiveTo.Groups.All(group => group.PartOfSpeech == "verb"),
            "A true infinitive marker must still narrow the inventory to verb senses.");

        SmartDictionarySenseGroup pushSense = new(
            0,
            "To push something briefly.",
            "verb",
            [
                new SmartDictionarySenseSurface(0, "толкать", ""),
                new SmartDictionarySenseSurface(1, "толкнуть", "")
            ]);
        SmartDictionarySurfaceSelection? presentNeed =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "I need it now.",
                "need",
                2,
                pushSense);
        Assert(presentNeed is { Translation: "толкать", Reason: "stable dictionary source order" },
            $"'need' must not be parsed as past tense selecting the semelfactive: {presentNeed}");

        // Grammeme-based aspect: "закрыть/закрывать" is a rankable perfective
        // pair even though neither verb ends in "-нуть".
        SmartDictionarySenseGroup closeSense = new(
            0,
            "To shut something.",
            "verb",
            [
                new SmartDictionarySenseSurface(0, "закрывать", ""),
                new SmartDictionarySenseSurface(1, "закрыть", "")
            ]);
        SmartDictionarySurfaceSelection? closedOnce =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "He closed the window once.",
                "closed",
                3,
                closeSense);
        Assert(closedOnce is
        {
            Translation: "закрыть",
            Reason: "single completed action aspect"
        },
            $"Grammeme aspect must prefer the perfective for a completed action: {closedOnce}");

        SmartDictionarySurfaceSelection? progressive =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "He was sighing quietly.",
                "sighing",
                7,
                breathingSense);
        Assert(progressive is
        {
            Translation: "вздыхать",
            Confidence: >= 0.95,
            Reason: "progressive or durative aspect"
        },
            $"A progressive action did not prefer the durative Russian surface: {progressive}");

        SmartDictionarySurfaceSelection? completed =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "He sighed once.",
                "sighed",
                3,
                breathingSense);
        Assert(completed is
        {
            Translation: "вздохнуть",
            Confidence: >= 0.9,
            Reason: "single completed action aspect"
        },
            $"A completed action did not prefer the semelfactive Russian surface: {completed}");

        SmartDictionarySenseGroup pastParadigm = new(
            0,
            "To emit a deep audible breath.",
            "verb",
            [
                new SmartDictionarySenseSurface(0, "вздохнул", ""),
                new SmartDictionarySenseSurface(1, "вздохнула", ""),
                new SmartDictionarySenseSurface(2, "вздохнули", "")
            ]);
        SmartDictionarySurfaceSelection? feminine =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "She sighed before answering.",
                "sighed",
                5,
                pastParadigm);
        Assert(feminine is
        {
            SourceIndex: 1,
            Translation: "вздохнула",
            Confidence: >= 0.98,
            Reason: "Russian past-tense agreement"
        },
            $"The selected sense did not preserve Russian subject agreement: {feminine}");

        SmartDictionarySenseGroup registerVariants = new(
            0,
            "The front part of a person's head.",
            "noun",
            [
                new SmartDictionarySenseSurface(0, "лик", "лит."),
                new SmartDictionarySenseSurface(1, "рожа", "разг. груб."),
                new SmartDictionarySenseSurface(2, "лицо", "")
            ]);
        SmartDictionarySurfaceSelection? safeRegister =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "Her face looked pale.",
                "face",
                4,
                registerVariants);
        // The sense itself carries no marker, so the unmarked surface is the
        // faithful one: roadmap #14.2C forbids strengthening as well as
        // softening. A vulgar sense over the same inventory is covered by
        // SmartDictionaryQualityFoundationTests.
        Assert(safeRegister is
        {
            SourceIndex: 2,
            Translation: "лицо",
            Reason: "matching sense register"
        },
            $"A marked literary/slang form outranked the neutral form: {safeRegister}");

        SmartDictionarySenseGroup stableSynonyms = new(
            7,
            "A brief look.",
            "noun",
            [
                new SmartDictionarySenseSurface(7, "взгляд", ""),
                new SmartDictionarySenseSurface(9, "взор", "")
            ]);
        SmartDictionarySurfaceSelection? stable =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                "She gave him a glance.",
                "glance",
                15,
                stableSynonyms);
        Assert(stable is
        {
            SourceIndex: 7,
            Translation: "взгляд",
            Reason: "stable dictionary source order"
        },
            $"Unmarked synonyms did not retain stable source priority: {stable}");

        SmartDictionarySenseInventory filteredVerbs =
            SmartDictionarySenseResolver.GroupCandidates(
                "She sighed quietly.",
                "sighed",
                5,
                [
                    new DictionarySenseCandidate(
                        "вздохнуть",
                        "To emit a deep breath.",
                        "",
                        "verb"),
                    new DictionarySenseCandidate(
                        "тосковать",
                        "To yearn sadly.",
                        "",
                        "verb"),
                    new DictionarySenseCandidate(
                        "вздох",
                        "A deep audible breath.",
                        "",
                        "noun")
                ]);
        Assert(filteredVerbs.PartOfSpeechFilterApplied &&
               filteredVerbs.Groups.Count == 2 &&
               filteredVerbs.Groups.All(group => group.PartOfSpeech == "verb"),
            "A high-confidence POS with multiple surviving senses was not filtered safely.");

        DictionarySenseCandidate[] headSenses =
        [
            new("body-part", "The upper part of the human body.", "", "noun"),
            new("leader", "A person in charge of an organization.", "", "noun"),
            new("main", "Most important or principal.", "", "adj"),
            new("front", "Situated at the front.", "", "adj")
        ];
        const string coordinatedPredicate = "He shook his head and sighed.";
        SmartDictionarySenseInventory coordinatedNoun =
            SmartDictionarySenseResolver.GroupCandidates(
                coordinatedPredicate,
                "head",
                coordinatedPredicate.IndexOf("head", StringComparison.Ordinal),
                headSenses);
        Assert(coordinatedNoun.PartOfSpeechFilterApplied &&
               coordinatedNoun.InferredPartOfSpeech == "noun" &&
               coordinatedNoun.PartOfSpeechConfidence >= 0.9 &&
               coordinatedNoun.Groups.Count == 2 &&
               coordinatedNoun.Groups.All(group => group.PartOfSpeech == "noun"),
            "A possessive noun before a coordinated predicate was misclassified as an adjective.");

        const string ambiguousCoordination = "He lowered his head and shoulders.";
        SmartDictionarySenseInventory failOpenCoordination =
            SmartDictionarySenseResolver.GroupCandidates(
                ambiguousCoordination,
                "head",
                ambiguousCoordination.IndexOf("head", StringComparison.Ordinal),
                headSenses);
        Assert(!failOpenCoordination.PartOfSpeechFilterApplied &&
               failOpenCoordination.InferredPartOfSpeech.Length == 0 &&
               failOpenCoordination.Groups.Count == headSenses.Length,
            "An ambiguous coordinated phrase must fail open instead of forcing adjective senses.");

        DictionarySenseCandidate[] innocentSenses =
        [
            new("innocent", "Free from guilt or wrongdoing.", "", "adj"),
            new("naive", "Without experience of worldly matters.", "", "adj"),
            new("person", "A person who is not guilty.", "", "noun"),
            new("bystander", "A person uninvolved in an event.", "", "noun")
        ];
        const string adjectivePhrase = "An innocent smile appeared on her face.";
        SmartDictionarySenseInventory prenominalAdjective =
            SmartDictionarySenseResolver.GroupCandidates(
                adjectivePhrase,
                "innocent",
                adjectivePhrase.IndexOf("innocent", StringComparison.Ordinal),
                innocentSenses);
        Assert(prenominalAdjective.PartOfSpeechFilterApplied &&
               prenominalAdjective.InferredPartOfSpeech == "adj" &&
               prenominalAdjective.PartOfSpeechConfidence >= 0.9 &&
               prenominalAdjective.Groups.Count == 2 &&
               prenominalAdjective.Groups.All(group => group.PartOfSpeech == "adj"),
            "The coordinated-predicate noun rule regressed an ordinary prenominal adjective.");

        SmartDictionaryRankerPlan groupedOutcomes =
            SmartDictionarySenseResolver.CreateRankerPlan(
                "We sat on the bank of the river.",
                "bank",
                14,
                [
                    new SmartDictionarySenseGroup(
                        0,
                        "The land beside a river.",
                        "noun",
                        [new SmartDictionarySenseSurface(0, "берег", "")]),
                    new SmartDictionarySenseGroup(
                        1,
                        "A raised edge beside water.",
                        "noun",
                        [new SmartDictionarySenseSurface(1, "Берег", "")]),
                    new SmartDictionarySenseGroup(
                        2,
                        "A financial institution.",
                        "noun",
                        [new SmartDictionarySenseSurface(2, "банк", "")])
                ]);
        Assert(groupedOutcomes.Outcomes.Count == 2 &&
               groupedOutcomes.CandidateGlosses.Count == 3 &&
               groupedOutcomes.CandidateGroupIds.SequenceEqual([0, 0, 1]) &&
               groupedOutcomes.Outcomes[0].Translation == "берег" &&
               groupedOutcomes.Outcomes[1].Translation == "банк",
            "Equivalent visible translations were not grouped before ranker abstention scoring.");

        DictionarySenseCandidate[] signSenses =
        [
            new(
                "знак",
                "A visible fact that shows that something exists or may happen.",
                "",
                "noun"),
            new(
                "примета",
                "A visible fact that shows that something exists or may happen.",
                "",
                "noun"),
            new(
                "признак",
                "a fact that shows that something exists or may happen",
                "",
                "noun"),
            new(
                "признак",
                "A property of the body that indicates a disease.",
                "",
                "noun")
        ];
        SmartDictionarySenseInventory clusteredSign =
            SmartDictionarySenseResolver.GroupCandidates(
                "There was no sign that she would.",
                "sign",
                13,
                signSenses);
        Assert(clusteredSign.Groups.Count == 2 &&
               clusteredSign.Groups[0].Surfaces.Select(surface => surface.Translation)
                   .SequenceEqual(["знак", "примета", "признак"]) &&
               clusteredSign.Groups[1].Surfaces.Single().Translation == "признак",
            "Near-identical dictionary definitions were not safely consolidated.");

        SmartDictionarySenseInventory polarityGuard =
            SmartDictionarySenseResolver.GroupCandidates(
                "The action is permitted.",
                "permitted",
                14,
                [
                    new DictionarySenseCandidate(
                        "разрешённый",
                        "An action that is permitted by the rules.",
                        "",
                        "adj"),
                    new DictionarySenseCandidate(
                        "запрещённый",
                        "An action that is not permitted by the rules.",
                        "",
                        "adj")
                ]);
        Assert(polarityGuard.Groups.Count == 2,
            "Definition consolidation erased a polarity-changing token.");

        DictionarySenseCandidate[] pantsSenses =
        [
            new("брюки", "An outer garment covering both legs.", "", "noun"),
            new("трусы", "An undergarment covering the lower body.", "", "noun"),
            new("пыхтеть", "To breathe quickly after exertion.", "", "verb"),
            new("тосковать", "To languish or sigh.", "", "verb")
        ];
        const string pantsContext =
            "It didn't seem his pants would dry any time soon.";
        SmartDictionarySenseInventory pantsNoun =
            SmartDictionarySenseResolver.GroupCandidates(
                pantsContext,
                "pants",
                pantsContext.IndexOf("pants", StringComparison.Ordinal),
                pantsSenses);
        Assert(pantsNoun.PartOfSpeechFilterApplied &&
               pantsNoun.InferredPartOfSpeech == "noun" &&
               pantsNoun.Groups.Count == 2 &&
               pantsNoun.Groups.All(group => group.PartOfSpeech == "noun"),
            "A possessive noun before a modal was not recognized as a noun.");

        DictionarySenseCandidate[] prancingSenses =
        [
            new("гарцевать", "To move around with lively high steps.", "", "verb"),
            new("важничать", "To strut in a showy manner.", "", "verb"),
            new("гарцевание", "Lively high-stepping movement.", "", "noun"),
            new("важность", "An affected show of importance.", "", "noun")
        ];
        const string prancingSentence =
            "She said that happily, almost prancing around.";
        SmartDictionarySenseInventory prancingVerb =
            SmartDictionarySenseResolver.GroupCandidates(
                prancingSentence,
                "prancing",
                prancingSentence.IndexOf("prancing", StringComparison.Ordinal),
                prancingSenses);
        Assert(prancingVerb.PartOfSpeechFilterApplied &&
               prancingVerb.InferredPartOfSpeech == "verb" &&
               prancingVerb.Groups.Count == 2 &&
               prancingVerb.Groups.All(group => group.PartOfSpeech == "verb"),
            "An -ing verb followed by a particle remained POS-unknown.");

        SmartDictionarySenseGroup separateSense = new(
            0,
            "To disunite from a group or mass; to disconnect.",
            "verb",
            [
                new SmartDictionarySenseSurface(0, "отделять", ""),
                new SmartDictionarySenseSurface(1, "отделить", "")
            ]);
        const string getSeparatedSentence =
            "Did you get separated from your friends?";
        SmartDictionarySurfaceSelection? patientSurface =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                getSeparatedSentence,
                "separated",
                getSeparatedSentence.IndexOf("separated", StringComparison.Ordinal),
                separateSense);
        SmartDictionaryTargetSpan patientSpan =
            SmartDictionarySenseResolver.ResolveTargetSpan(
                getSeparatedSentence,
                "separated",
                getSeparatedSentence.IndexOf("separated", StringComparison.Ordinal));
        Assert(patientSurface is
        {
            Translation: "отделиться от",
            Reason: "passive or result-state construction"
        } &&
               patientSpan is
               {
                   Source: "get separated from",
                   IsPhrase: true
               },
            $"The get-passive was not converted into a compact patient construction: {patientSurface}, {patientSpan}");

        const string perfectSentence = "They have separated the two groups.";
        SmartDictionarySurfaceSelection? perfectSurface =
            SmartDictionarySenseResolver.SelectSurfaceForm(
                perfectSentence,
                "separated",
                perfectSentence.IndexOf("separated", StringComparison.Ordinal),
                separateSense);
        SmartDictionaryTargetSpan perfectSpan =
            SmartDictionarySenseResolver.ResolveTargetSpan(
                perfectSentence,
                "separated",
                perfectSentence.IndexOf("separated", StringComparison.Ordinal));
        Assert(perfectSurface?.Translation != "отделиться от" &&
               !perfectSpan.IsPhrase,
            "A perfect active construction was mistaken for a get-passive.");

        const string attributiveSeparate = "They get separate rooms.";
        SmartDictionaryTargetSpan separateRoomsSpan =
            SmartDictionarySenseResolver.ResolveTargetSpan(
                attributiveSeparate,
                "separate",
                attributiveSeparate.IndexOf("separate", StringComparison.Ordinal));
        Assert(!separateRoomsSpan.IsPhrase,
            "An attributive adjective after get was mistaken for a participle.");

#if WINDOWS
        Assert(HoverTranslationOverlay.PreferredWidth(
                   "pants",
                   "брюки; трусы; пыхтение; пыхтеть; трепетать; тосковать",
                   dictionaryMode: true) <= 292,
            "A short multi-meaning card still expands to the legacy wide layout.");

        const string smartModeLabel = "Умный словарь";
        const string abstentionLabel = "EN → RU  ·  NPU · НЕ УВЕРЕН";
        const string unavailableLabel = "EN → RU  ·  NPU · НЕДОСТУПЕН";
        double abstentionWidth = HoverTranslationOverlay.PreferredWidth(
            "get",
            "получать; приносить; становиться",
            dictionaryMode: true,
            smartModeLabel,
            abstentionLabel);
        double unavailableWidth = HoverTranslationOverlay.PreferredWidth(
            "get",
            "получать; приносить; становиться",
            dictionaryMode: true,
            smartModeLabel,
            unavailableLabel);
        Assert(
            abstentionWidth >= HoverTranslationOverlay.PreferredHeaderWidth(
                smartModeLabel,
                abstentionLabel) &&
            abstentionWidth > 292 &&
            abstentionWidth <= 430,
            "The smart abstention badge can still be clipped by a compact card.");
        Assert(
            unavailableWidth >= abstentionWidth &&
            unavailableWidth >= HoverTranslationOverlay.PreferredHeaderWidth(
                smartModeLabel,
                unavailableLabel) &&
            unavailableWidth <= 430,
            "Longer Russian smart status labels do not reserve enough header width.");
        double phraseBaseWidth = HoverTranslationOverlay.PreferredWidth(
            "88-wide",
            "Нет записи в локальном словаре",
            dictionaryMode: true,
            smartModeLabel,
            "EN → RU  ·  НЕТ · OCR");
        double phraseWidth = HoverTranslationOverlay.PreferredWidth(
            "88-wide",
            "Нет записи в локальном словаре",
            dictionaryMode: true,
            smartModeLabel,
            "EN → RU  ·  НЕТ · OCR",
            "Вы говорите об этих великолепных бёдрах шириной восемьдесят восемь дюймов?");
        Assert(
            phraseWidth > phraseBaseWidth && phraseWidth >= 320 && phraseWidth <= 430,
            "The independent TranslateGemma line does not reserve a readable card width.");
#else
        MacSmoke.Part(
            "overlay-width",
            "Smart dictionary sense grouping and surface selection",
            "card widths are measured with a WPF Typeface");
#endif

        Assert(SmartDictionarySenseResolver.CanonicalizePartOfSpeech("Adjective") == "adj" &&
               SmartDictionarySenseResolver.CanonicalizePartOfSpeech("ADV") == "adv" &&
               SmartDictionarySenseResolver.CanonicalizePartOfSpeech("proper-noun") == "name",
            "Dictionary POS aliases were not normalized for stable grouping.");

        return Task.CompletedTask;
    });

    await RunAsync("Translation sense fidelity guard", () =>
    {
        TranslationScaffold full = new(
            [
                new ScaffoldTerm("on the verge of", "на гра\u0301ни", 0.995),
                new ScaffoldTerm("Yuki", "Юки", 1)
            ],
            ExactSentenceTranslation: null,
            ProperNames: ["Yuki"])
        {
            SenseHints =
            [
                new ScaffoldSenseHint(
                    "tears",
                    "drops of clear salty liquid secreted from the eyes",
                    "слёзы",
                    ["разрывы"],
                    TranslationScaffoldBuilder.MinimumConfidence)
            ]
        };
        Assert(NeuralSenseFidelityGuard.FindConflictingHints(
                    "Её глаза наполнились разрывами.",
                    full.SenseHints).Count == 1 &&
               NeuralSenseFidelityGuard.FindConflictingHints(
                    "На глазах выступили слёзы, но ткань получила разрывы.",
                    full.SenseHints).Count == 1 &&
               NeuralSenseFidelityGuard.FindConflictingHints(
                    "Она едва сдерживала плач.",
                    full.SenseHints).Count == 0,
            "The sense gate must distinguish conflict, mixed evidence and a natural paraphrase.");
        ScaffoldSenseHint bankHint = new(
            "bank",
            "sloping land beside a river",
            "берег",
            ["банк"],
            TranslationScaffoldBuilder.MinimumConfidence);
        Assert(NeuralSenseFidelityGuard.FindConflictingHints(
                    "Начался праздничный банкет.",
                    [bankHint]).Count == 0 &&
               NeuralSenseFidelityGuard.FindConflictingHints(
                    "Он вышел из банка.",
                    [bankHint]).Count == 1,
            "A short rejected surface matched an unrelated longer Russian word.");
        ScaffoldSenseHint memoryHint = new(
            "memory",
            "the faculty by which information is stored and recalled",
            "воспоминание",
            ["память"],
            TranslationScaffoldBuilder.MinimumConfidence);
        Assert(RussianMorphology.AreConservativeInflectionVariants("память", "памяти") &&
               !RussianMorphology.AreConservativeInflectionVariants("память", "памятник") &&
               NeuralSenseFidelityGuard.FindConflictingHints(
                    "Это старый памятник.",
                    [memoryHint]).Count == 0 &&
               NeuralSenseFidelityGuard.FindConflictingHints(
                    "Это осталось в моей памяти.",
                    [memoryHint]).Count == 1,
            "Rejected-sense detection must accept inflection without matching a derived noun.");

        Assert(SenseFidelityGuard.FindMissingTerms("Юки была на грани слёз.", full.Terms).Count == 0,
            "Short names, stress marks and inflected glossary stems must validate.");
        Assert(SenseFidelityGuard.FindMissingTerms(
                "Она выбрала лёгкое платье.",
                [new ScaffoldTerm("light", "лёгкий", 1)]).Count == 0,
            "Russian adjective endings must not trigger a false semantic retry.");
        Assert(SenseFidelityGuard.FindMissingTerms(
                "Здесь слишком темно.",
                [new ScaffoldTerm("dark", "тёмный", 1)]).Count == 0,
            "A Russian predicative adjective form must not look like a rejected sense.");
        Assert(SenseFidelityGuard.FindMissingTerms(
                "Без Анны мы не начнём.",
                [new ScaffoldTerm("Anna", "Анна", 1)]).Count == 0,
            "A short proper name must tolerate Russian case inflection.");
        Assert(SenseFidelityGuard.FindMissingTerms(
                "Я не видел кота.",
                [new ScaffoldTerm("cat", "кот", 1)]).Count == 0,
            "A three-letter noun must tolerate Russian case inflection.");
        Assert(SenseFidelityGuard.FindMissingTerms(
                "Который из них?",
                [new ScaffoldTerm("cat", "кот", 1)]).Count == 1,
            "A short glossary word must not match inside a longer unrelated token.");
        Assert(SenseFidelityGuard.FindMissingTerms(
                "Котёл уже кипит.",
                [new ScaffoldTerm("cat", "кот", 1)]).Count == 1,
            "A shared three-letter prefix alone must not satisfy a glossary term.");
        Assert(SenseFidelityGuard.FindMissingTerms(
                "Высота пола изменилась.",
                [new ScaffoldTerm("field", "поле", 1)]).Count == 1,
            "Different short nominal paradigms must not be treated as one lexeme.");
        Assert(SenseFidelityGuard.FindMissingTerms(
                "Совсем другой перевод.",
                [new ScaffoldTerm("uncertain", "термин", 0.89)]).Count == 0,
            "Terms below the frozen confidence floor must not trigger a retry.");
        return Task.CompletedTask;
    });

    await RunAsync("Smart dictionary meaning parser", () =>
    {
        string[] meanings = DictionaryMeaningParser.SplitMeanings("банк; берег; банк");
        Assert(meanings.SequenceEqual(["банк", "берег"]),
            "Dictionary meanings were not normalized for semantic ranking.");
        return Task.CompletedTask;
    });

    await RunAsync("Smart dictionary deterministic heuristic ranker", () =>
    {
        SmartDictionaryHeuristicSelection? infinitive =
            SmartDictionaryHeuristicRanker.TrySelect(
                "They decided to record the conversation.",
                "record",
                16,
                ["запись", "записывать"],
                "English");
        Assert(infinitive is { Translation: "записывать", Confidence: >= 0.9 },
            $"An infinitive marker must select the verbal meaning: {infinitive}");

        Assert(RussianMorphologyLexicon.EntryCount > 100_000,
            "The OpenCorpora verb lexicon was not loaded from Morphology/ru-morph.tsv.gz.");

        // Suppletive paradigm: suffix stems ("пошё"/"пош") cannot group these,
        // the lexicon lemma ("пойти") can.
        SmartDictionaryHeuristicSelection? suppletive =
            SmartDictionaryHeuristicRanker.TrySelect(
                "She went home quietly.",
                "went",
                4,
                ["пошёл", "пошла", "пошли"],
                "English");
        Assert(suppletive is { Translation: "пошла", Confidence: >= 0.95 },
            $"The suppletive past paradigm must agree with a feminine subject: {suppletive}");

        // Outside the lexicon the suffix fallback still analyzes unknown verbs.
        Assert(RussianMorphology.DetectPastForm("глокнула") ==
               (RussianPastForm.Feminine, "глокну"),
            "The suffix fallback must survive for words outside the lexicon.");

        SmartDictionaryHeuristicSelection? prepositionalTo =
            SmartDictionaryHeuristicRanker.TrySelect(
                "He went to bed.",
                "bed",
                11,
                ["кровать", "укладывать"],
                "English");
        Assert(prepositionalTo is null,
            $"A prepositional 'to' must not force the verbal meaning: {prepositionalTo}");

        SmartDictionaryHeuristicSelection? presentNeed =
            SmartDictionaryHeuristicRanker.TrySelect(
                "I need it now.",
                "need",
                2,
                ["толкать", "толкнуть"],
                "English");
        Assert(presentNeed is null,
            $"'need' must not be parsed as a past form selecting the semelfactive: {presentNeed}");

        SmartDictionaryHeuristicSelection? noun =
            SmartDictionaryHeuristicRanker.TrySelect(
                "The record of our conversation was damaged.",
                "record",
                5,
                ["запись", "записывать"],
                "English");
        Assert(noun is { Translation: "запись", Confidence: >= 0.9 },
            $"A determiner and prepositional complement must select the noun: {noun}");

        SmartDictionaryHeuristicSelection? adjective =
            SmartDictionaryHeuristicRanker.TrySelect(
                "She wore a light summer dress.",
                "light",
                12,
                ["свет", "лёгкий"],
                "English");
        Assert(adjective is { Translation: "лёгкий", Confidence: >= 0.9 },
            $"Attributive syntax must select the adjective: {adjective}");

        SmartDictionaryHeuristicSelection? adverb =
            SmartDictionaryHeuristicRanker.TrySelect(
                "The child walked quickly.",
                "quickly",
                20,
                ["быстрый", "быстро"],
                "English");
        Assert(adverb is { Translation: "быстро", Confidence: >= 0.9 },
            $"The English -ly form must select the Russian adverb: {adverb}");

        SmartDictionaryHeuristicSelection? feminine =
            SmartDictionaryHeuristicRanker.TrySelect(
                "She sighed and closed her eyes.",
                "sighed",
                5,
                ["вздохнул", "вздохнула", "вздохнули"],
                "English");
        Assert(feminine is { Translation: "вздохнула", Confidence: >= 0.95 },
            $"The past-tense candidate must agree with a feminine subject: {feminine}");

        SmartDictionaryHeuristicSelection? plural =
            SmartDictionaryHeuristicRanker.TrySelect(
                "They sighed before answering.",
                "sighed",
                6,
                ["вздохнул", "вздохнула", "вздохнули"],
                "English");
        Assert(plural is { Translation: "вздохнули", Confidence: >= 0.95 },
            $"The past-tense candidate must agree with a plural subject: {plural}");

        SmartDictionaryHeuristicSelection? nounEndings =
            SmartDictionaryHeuristicRanker.TrySelect(
                "She rested before dinner.",
                "rested",
                4,
                ["стол", "школа", "масло", "грабли"],
                "English");
        Assert(nounEndings is null,
            $"Russian noun endings must not masquerade as a past-tense paradigm: {nounEndings}");

        SmartDictionaryHeuristicSelection? completedAction =
            SmartDictionaryHeuristicRanker.TrySelect(
                "After a pause, he sighed.",
                "sighed",
                18,
                ["вздыхать", "вздохнуть"],
                "English");
        Assert(completedAction is { Translation: "вздохнуть", Confidence: >= 0.9 },
            $"A single completed action must prefer the semelfactive verb: {completedAction}");

        SmartDictionaryHeuristicSelection? progressiveAction =
            SmartDictionaryHeuristicRanker.TrySelect(
                "He was sighing quietly by the window.",
                "sighing",
                8,
                ["вздыхать", "вздохнуть"],
                "English");
        Assert(progressiveAction is { Translation: "вздыхать", Confidence: >= 0.9 },
            $"A progressive action must prefer the non-semelfactive verb: {progressiveAction}");

        SmartDictionaryHeuristicSelection? riverBank =
            SmartDictionaryHeuristicRanker.TrySelect(
                "The fisherman rested beside the river bank.",
                "bank",
                40,
                ["банк", "берег"],
                "English");
        Assert(riverBank is { Translation: "берег", Confidence: >= 0.9 },
            $"River cues must select the geographical bank: {riverBank}");

        SmartDictionaryHeuristicSelection? distantRiver =
            SmartDictionaryHeuristicRanker.TrySelect(
                "After leaving the river, he robbed the bank.",
                "bank",
                39,
                ["банк", "берег"],
                "English");
        Assert(distantRiver is null,
            $"A cue from another clause must not decide the hovered meaning: {distantRiver}");

        const string damagedFileContext = "t those files still in the recycle bin, though?\"";
        SmartDictionaryHeuristicSelection? digitalFile =
            SmartDictionaryHeuristicRanker.TrySelect(
                damagedFileContext,
                "files",
                damagedFileContext.IndexOf("files", StringComparison.Ordinal),
                ["дело", "папка", "досье", "файл"],
                "English");
        Assert(digitalFile is { Translation: "файл", Confidence: >= 0.9 },
            $"Digital-storage cues must select file despite damaged OCR context: {digitalFile}");

        SmartDictionaryHeuristicSelection? animalBat =
            SmartDictionaryHeuristicRanker.TrySelect(
                "At dusk a bat emerged from the cave.",
                "bat",
                10,
                ["бита", "летучая мышь"],
                "English");
        Assert(animalBat is { Translation: "летучая мышь", Confidence: >= 0.9 },
            $"Cave cues must select the animal bat: {animalBat}");

        SmartDictionaryHeuristicSelection? birdCrane =
            SmartDictionaryHeuristicRanker.TrySelect(
                "A crane nested beside the lake and spread its wings.",
                "crane",
                2,
                ["кран", "журавль"],
                "English");
        Assert(birdCrane is { Translation: "журавль", Confidence: >= 0.9 },
            $"Bird cues must select the animal crane: {birdCrane}");

        SmartDictionaryHeuristicSelection? brink =
            SmartDictionaryHeuristicRanker.TrySelect(
                "Their plan was teetering on the brink of failure.",
                "teetering",
                15,
                ["качаться", "колебаться", "быть на грани"],
                "English");
        Assert(brink is { Translation: "быть на грани", Confidence: >= 0.9 },
            $"Brink cues must select the matching idiom: {brink}");

        SmartDictionaryHeuristicSelection? inflectedBeam =
            SmartDictionaryHeuristicRanker.TrySelect(
                "Her smile beamed with joy.",
                "beamed",
                10,
                ["балка", "сиять"],
                "English");
        Assert(inflectedBeam is { Translation: "сиять", Confidence: >= 0.9 },
            $"Semantic rules must apply through inflection rather than exact surface forms: {inflectedBeam}");

        SmartDictionaryHeuristicSelection? structuralBeam =
            SmartDictionaryHeuristicRanker.TrySelect(
                "The roof rested on a steel beam.",
                "beam",
                28,
                ["сиять", "балка"],
                "English");
        Assert(structuralBeam is { Translation: "балка", Confidence: >= 0.9 },
            $"Structural cues must select the construction meaning regardless of candidate order: {structuralBeam}");

        SmartDictionaryHeuristicSelection? habitualAction =
            SmartDictionaryHeuristicRanker.TrySelect(
                "He often sighed during those long meetings.",
                "sighed",
                9,
                ["вздыхать", "вздохнуть"],
                "English");
        Assert(habitualAction is null,
            $"A habitual action must not be forced into a single completed event: {habitualAction}");

        SmartDictionaryHeuristicSelection? ambiguousSpring =
            SmartDictionaryHeuristicRanker.TrySelect(
                "The spring was beautiful.",
                "spring",
                5,
                ["весна", "пружина"],
                "English");
        Assert(ambiguousSpring is null,
            $"Insufficient semantic context must be delegated to the model: {ambiguousSpring}");

        SmartDictionaryHeuristicSelection? synonymChoice =
            SmartDictionaryHeuristicRanker.TrySelect(
                "A beaming smile appeared on her face.",
                "beaming",
                4,
                ["сияющий", "лучезарный", "сияющий от счастья"],
                "English");
        Assert(synonymChoice is null,
            $"Equivalent stylistic synonyms must be delegated to the model: {synonymChoice}");

        SmartDictionaryHeuristicSelection? tatoebaSign =
            SmartDictionaryHeuristicRanker.TrySelectFromContextTranslation(
                "Не было никаких признаков того, что она это сделает.",
                ["знак", "признак", "след", "вывеска"]);
        Assert(tatoebaSign is
        {
            Translation: "признак",
            Confidence: >= 0.99,
            Reason: "exact Tatoeba sentence alignment"
        },
            $"An exact Tatoeba sentence must align an inflected unique meaning: {tatoebaSign}");

        SmartDictionaryHeuristicSelection? tatoebaSharedStem =
            SmartDictionaryHeuristicRanker.TrySelectFromContextTranslation(
                "Они наконец отделились от своих друзей.",
                ["отделять", "отделиться", "разделяться"]);
        Assert(tatoebaSharedStem is null,
            $"Tatoeba must abstain when several outcomes share the same Russian stem: {tatoebaSharedStem}");

        SmartDictionaryHeuristicSelection? unrelatedTatoeba =
            SmartDictionaryHeuristicRanker.TrySelectFromContextTranslation(
                "Она посмотрела в сторону и ничего не сказала.",
                ["знак", "признак", "след", "вывеска"]);
        Assert(unrelatedTatoeba is null,
            $"An unrelated exact-sentence translation must not choose a dictionary sense: {unrelatedTatoeba}");

        SmartDictionaryHeuristicSelection? genderlessYou =
            SmartDictionaryHeuristicRanker.TrySelect(
                "You are lost.",
                "lost",
                8,
                ["Ты потерялся", "Ты потерялась", "Вы потерялись"],
                "English");
        Assert(genderlessYou is null,
            $"English 'you' does not provide enough gender/number evidence: {genderlessYou}");

        SmartDictionaryHeuristicSelection? japanese =
            SmartDictionaryHeuristicRanker.TrySelect(
                "彼は銀行へ行った。",
                "銀行",
                2,
                ["банк", "берег"],
                "Japanese");
        Assert(japanese is null,
            $"The English heuristic must not rank Japanese input: {japanese}");

        return Task.CompletedTask;
    });

#if WINDOWS
    await RunAsync("Text hook sentence buffer", async () =>
    {
        AppSettings hookSettings = new() { TextHookEnabled = true, TextractorDirectory = tempRoot };
        await using TextHookService hook = new(hookSettings);
        Assert(hook.IsEnabled, "The hook option must honor settings.");
        Assert(new TextHookService(new AppSettings()) is { IsEnabled: false },
            "The hook must stay off by default so OCR remains the universal path.");

        hook.IngestLine("Textractor: pipe connected");
        hook.IngestLine("[FA0:2C64:0:0:0:GetGlyphOutlineW] Nice to meet you!");
        hook.IngestLine("[FA0:2C64:0:0:0:GetGlyphOutlineW] Nice to meet you!");
        hook.IngestLine("[1B2:2C64:0:0:0:TextOutA]   Она   улыбнулась и   кивнула. ");
        Assert(hook.RecentSentenceCount == 2,
            $"Diagnostics and consecutive duplicates must not enter the buffer: {hook.RecentSentenceCount}");
        Assert(hook.TryGetSentenceContaining("meet") == "Nice to meet you!",
            "A hooked sentence containing the hovered word must be returned.");
        Assert(hook.TryGetSentenceContaining("улыбнулась") == "Она улыбнулась и кивнула.",
            "Hook sentences must be whitespace-normalized.");
        Assert(hook.TryGetSentenceContaining("absent") is null,
            "Containment is the relevance guard: no match means OCR context stays.");
        Assert(hook.TryGetSentenceContaining("meet", DateTime.UtcNow.AddMinutes(5)) is null,
            "Stale hooked sentences must expire.");

        int selfProcessId = Environment.ProcessId;
        string selfProcessName = Process.GetCurrentProcess().ProcessName;
        Assert(hook.IsProcessAllowed(selfProcessId),
            "Without a matching profile the global toggle alone must govern hooking.");
        AppSettings gatedSettings = new()
        {
            TextHookEnabled = true,
            TextractorDirectory = tempRoot,
            Profiles =
            [
                new AutomationProfile
                {
                    Name = "self",
                    ProcessName = selfProcessName,
                    TextHookEnabled = false
                }
            ]
        };
        await using TextHookService gatedHook = new(gatedSettings);
        Assert(!gatedHook.IsProcessAllowed(selfProcessId),
            "A profile with the hook flag off must exclude its game.");
        gatedSettings.Profiles[0].TextHookEnabled = true;
        Assert(gatedHook.IsProcessAllowed(selfProcessId),
            "A profile with the hook flag on must allow its game.");

        List<(string Key, string Code)> hookRows = [];
        TextHookService.ParseHookCodeLines(
            [
                "# comment",
                "windrp\tHS932#-4@27A76:WindRP.exe\tWind ENG",
                "windrp\tHB932#-4*0@27A76:WindRP.exe\tWind ENG alt",
                "WINDRP.exe\tHS932#-4@27A76:WindRP.exe\tduplicate after normalization",
                "broken line without tab",
                "badcode\tnothook\tmust be skipped"
            ],
            hookRows);
        Assert(hookRows.Count == 2 &&
               hookRows.All(row => row.Key == "windrp"),
            $"Hook-code parsing must normalize keys, dedupe and skip junk: {hookRows.Count}");

        // Automatic selection: a user profile code always wins over the base.
        gatedSettings.Profiles[0].TextHookCode = "HQ4@1234:self.exe";
        Assert(gatedHook.ResolveHookCodes(selfProcessId) is ["HQ4@1234:self.exe"],
            "The profile hook code must override the shipped database.");

        string shippedDatabase = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath)!,
            "TextHook",
            "hook-codes.tsv");
        List<(string Key, string Code)> shipped = [];
        TextHookService.ParseHookCodeLines(File.ReadLines(shippedDatabase), shipped);
        Assert(shipped.Count >= 40 &&
               shipped.Count(row => row.Key == "ags") == 6,
            $"The shipped hook-code base must load with multi-code exe support: {shipped.Count}");
    });
#else
    MacSmoke.Skip(
        "Text hook sentence buffer",
        "TextHookService (Textractor host) lives in the WPF assembly");
#endif

#if WINDOWS
    await RunAsync(
        "Text hook process isolation and token boundaries",
        () => TextHookSafetyTests.RunAsync(tempRoot));
#else
    MacSmoke.Skip(
        "Text hook process isolation and token boundaries",
        "TextHookService and HoverTranslationOverlay from the WPF assembly; copies cmd.exe as TextractorCLI.exe");
#endif

    await RunAsync(
        "Smart dictionary phrase routing",
        () => SmartDictionaryPhraseRoutingTests.RunAsync(tempRoot));

    await RunAsync(
        "Smart dictionary quality foundation",
        SmartDictionaryQualityFoundationTests.RunAsync);

    await RunAsync(
        "VN dictionary data roles",
        () => VnDictionaryLayerTests.RunAsync(tempRoot));

    await RunAsync(
        "Phrasal verb dictionary layer",
        () => PhrasalVerbLayerTests.RunAsync(tempRoot));

    await RunAsync(
        "Dictionary key and sentence normalization",
        () => LocalDictionaryNormalizationTests.RunAsync(tempRoot));

    await RunAsync(
        "User dictionary editor and translation history",
        () => UserDictionaryAndHistoryTests.RunAsync(tempRoot));

#if WINDOWS
    await RunAsync(
        "Build artifact rollback",
        () => BuildScriptSafetyTests.RunAsync(tempRoot));
#else
    MacSmoke.Skip(
        "Build artifact rollback",
        "drives scripts/build-state.ps1 through Windows PowerShell");
#endif

#if WINDOWS
    await RunAsync(
        "ConSeC bundle install and publish contract",
        () => ConsecBundleInfrastructureTests.RunAsync(tempRoot));
#else
    MacSmoke.Skip(
        "ConSeC bundle install and publish contract",
        "drives the ConSeC install/verify scripts through Windows PowerShell");
#endif

    await RunAsync("Benchmark set schema", async () =>
    {
        TranslationBenchmark.VerifyMetrics();
        SmartDictionarySenseGroup synonymSense = new(
            0,
            "Authority or command over something.",
            "noun",
            [
                new SmartDictionarySenseSurface(0, "власть", ""),
                new SmartDictionarySenseSurface(1, "контроль", "")
            ]);
        SmartDictionaryRankerPlan synonymPlan = new(
            [new SmartDictionaryRankerOutcome(0, 0, "власть")],
            [synonymSense.EnglishDefinition],
            [0]);
        RankerCalibrationBenchmark.RankedCorrectness synonymCorrectness =
            RankerCalibrationBenchmark.EvaluateRankedCorrectness(
                synonymPlan,
                [synonymSense],
                selectedGroupId: 0,
                abstained: false,
                expectedTranslation: "контроль");
        Assert(
            synonymCorrectness.SenseExpectedCovered &&
            !synonymCorrectness.VisibleExpectedCovered &&
            synonymCorrectness.SenseCorrect == true &&
            synonymCorrectness.VisibleCorrect == false,
            "The ranker benchmark conflated a correct sense group with a different visible synonym.");

        SmartDictionarySenseGroup scientificNearNeighbourSense = new(
            0,
            "Having statistical importance rather than merely large magnitude.",
            "adjective",
            [new SmartDictionarySenseSurface(0, "значимый", "")]);
        SmartDictionaryRankerPlan scientificNearNeighbourPlan = new(
            [new SmartDictionaryRankerOutcome(0, 0, "значительный")],
            [scientificNearNeighbourSense.EnglishDefinition],
            [0]);
        RankerCalibrationBenchmark.RankedCorrectness scientificNearNeighbourCorrectness =
            RankerCalibrationBenchmark.EvaluateRankedCorrectness(
                scientificNearNeighbourPlan,
                [scientificNearNeighbourSense],
                selectedGroupId: 0,
                abstained: false,
                expectedTranslation: "значимый");
        Assert(
            scientificNearNeighbourCorrectness.SenseCorrect == true &&
            scientificNearNeighbourCorrectness.VisibleCorrect == false,
            "The visible benchmark accepted a scientific near-neighbour as an inflection.");

        RankerCalibrationBenchmark.VerifyMeasurementCounts(20, 12, 10, 5, 5, 8, 5);
        foreach ((int cases, int ranked, int covered, int decided, int correct,
                     int deterministic, int deterministicCorrect) in new[]
                 {
                     (0, 0, 0, 0, 0, 0, 0),
                     (20, 0, 0, 0, 0, 0, 0),
                     (20, 10, 0, 0, 0, 0, 0),
                     (20, 10, 8, 0, 0, 0, 0),
                     (20, 10, 8, 4, 5, 0, 0),
                     (20, 12, 10, 5, 5, 9, 5)
                 })
        {
            try
            {
                RankerCalibrationBenchmark.VerifyMeasurementCounts(
                    cases,
                    ranked,
                    covered,
                    decided,
                    correct,
                    deterministic,
                    deterministicCorrect);
                throw new InvalidOperationException(
                    "A ranker benchmark without usable measurements passed its gate.");
            }
            catch (InvalidDataException)
            {
                // Expected: a diagnostic run with no measurements must fail CI.
            }
        }

        foreach ((int cases, int ranked, int covered, int decided, int correct,
                     int deterministic, int deterministicCorrect) in new[]
                 {
                     (1, 1, 1, 1, 1, 0, 0),
                     (20, 12, 10, 5, 4, 8, 6),
                     (20, 12, 8, 4, 4, 8, 6),
                     (20, 12, 10, 4, 4, 8, 6),
                     (20, 12, 10, 5, 5, 8, 3)
                 })
        {
            try
            {
                RankerCalibrationBenchmark.VerifyMeasurementCounts(
                    cases,
                    ranked,
                    covered,
                    decided,
                    correct,
                    deterministic,
                    deterministicCorrect);
                throw new InvalidOperationException(
                    "An under-covered ranker benchmark unexpectedly passed.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("quality gate", StringComparison.Ordinal))
            {
                // Expected: precision alone is insufficient for quality CI.
            }
        }

        // The register gate is defined per bucket (roadmap #14.2C phase C). An
        // aggregate-only gate let a failing bucket hide behind healthy ones and
        // let a bucket that abstained on every case vanish from the measurement,
        // so a regression could be recorded as a pass.
        RegisterFidelityBenchmark.BucketGate[] healthyRegisterBuckets =
        [
            new("masked-euphemistic", 41, 15, 15, 15, 0),
            new("neutral-vulgar", 30, 18, 18, 18, 0),
            new("sexual-ordinary", 34, 16, 16, 16, 0),
            new("slang-colloquial", 30, 17, 17, 17, 0)
        ];
        RegisterFidelityBenchmark.VerifyGates(healthyRegisterBuckets, unresolvedCount: 0);
        AssertThrows<InvalidOperationException>(
            () => RegisterFidelityBenchmark.VerifyGates(healthyRegisterBuckets, unresolvedCount: 1),
            "An unresolved register case passed the gate.");
        AssertThrows<InvalidOperationException>(
            () => RegisterFidelityBenchmark.VerifyGates([], unresolvedCount: 0),
            "A register run without buckets passed the gate.");
        AssertThrows<InvalidOperationException>(
            () => RegisterFidelityBenchmark.VerifyGates(
                [
                    new("masked-euphemistic", 41, 41, 41, 41, 0),
                    new("neutral-vulgar", 30, 30, 30, 30, 0),
                    new("sexual-ordinary", 34, 34, 34, 34, 0)
                ],
                unresolvedCount: 0),
            "A register run with a missing documented bucket passed the gate.");
        AssertThrows<InvalidOperationException>(
            () => RegisterFidelityBenchmark.VerifyGates(
                [
                    new("masked-euphemistic", 41, 41, 41, 41, 0),
                    new("neutral-vulgar", 30, 30, 30, 30, 0),
                    new("sexual-ordinary", 34, 34, 34, 34, 0),
                    new("slang-renamed", 30, 30, 30, 30, 0)
                ],
                unresolvedCount: 0),
            "A renamed register bucket passed the gate.");
        AssertThrows<InvalidOperationException>(
            () => RegisterFidelityBenchmark.VerifyGates(
                [
                    new("masked-euphemistic", 41, 41, 41, 41, 0),
                    new("neutral-vulgar", 30, 0, 0, 0, 0),
                    new("sexual-ordinary", 34, 34, 34, 34, 0),
                    new("slang-colloquial", 30, 30, 30, 30, 0)
                ],
                unresolvedCount: 0),
            "A register bucket that abstained on every case passed the gate.");
        AssertThrows<InvalidOperationException>(
            () => RegisterFidelityBenchmark.VerifyGates(
                [
                    // 125/130 faithful overall clears 90 %, one bucket at 50 % does not.
                    new("masked-euphemistic", 100, 100, 100, 100, 0),
                    new("neutral-vulgar", 10, 10, 10, 5, 0),
                    new("sexual-ordinary", 10, 10, 10, 10, 0),
                    new("slang-colloquial", 10, 10, 10, 10, 0)
                ],
                unresolvedCount: 0),
            "A bucket below register fidelity hid behind the aggregate.");
        AssertThrows<InvalidOperationException>(
            () => RegisterFidelityBenchmark.VerifyGates(
                [
                    new("masked-euphemistic", 41, 42, 42, 42, 0),
                    new("neutral-vulgar", 30, 30, 30, 30, 0),
                    new("sexual-ordinary", 34, 34, 34, 34, 0),
                    new("slang-colloquial", 30, 30, 30, 30, 0)
                ],
                unresolvedCount: 0),
            "A bucket deciding more cases than it contains passed the gate.");

        const string benchmarkUsage = "strict benchmark tail";
        ParsedRankerBenchmarkOptions defaultOptions = ParseRankerBenchmarkOptions(
            [], 0, allowDump: true, reportOption: "--ranker-report", usage: benchmarkUsage);
        Assert(
            defaultOptions.Provider == GlossBertRankerProvider.Cpu &&
            defaultOptions.CpuModel == RankerCpuModelProfile.GlossBert,
            "Omitted benchmark options did not retain the CPU GlossBERT default.");
        ParsedRankerBenchmarkOptions reorderedOptions = ParseRankerBenchmarkOptions(
            [
                "--ranker-report", "report.json",
                "--ranker-cpu-model", "consec",
                "--dump", "scores.json",
                "cpu"
            ],
            0,
            allowDump: true,
            reportOption: "--ranker-report",
            usage: benchmarkUsage);
        Assert(
            reorderedOptions.Provider == GlossBertRankerProvider.Cpu &&
            reorderedOptions.CpuModel == RankerCpuModelProfile.Consec &&
            reorderedOptions.DumpPath == "scores.json" &&
            reorderedOptions.ReportPath == "report.json",
            "Benchmark provider/options were not accepted in arbitrary order.");
        ParsedRankerBenchmarkOptions consecOptions = ParseRankerBenchmarkOptions(
            ["--ranker-cpu-model", "ConSeC", "cpu"],
            0,
            allowDump: false,
            reportOption: "--register-report",
            usage: benchmarkUsage);
        Assert(
            consecOptions.Provider == GlossBertRankerProvider.Cpu &&
            consecOptions.CpuModel == RankerCpuModelProfile.Consec,
            "The strict benchmark parser rejected the ConSeC CPU profile.");
        ParsedRankerBenchmarkOptions npuOptions = ParseRankerBenchmarkOptions(
            ["--ranker-cpu-model", "glossbert", "NPU"],
            0,
            allowDump: false,
            reportOption: "--register-report",
            usage: benchmarkUsage);
        Assert(npuOptions.Provider == GlossBertRankerProvider.Npu,
            "The strict benchmark parser rejected the explicit NPU provider.");
        foreach (string[] invalid in new[]
                 {
                     new[] { "gpu" },
                     new[] { "cpu", "cpu" },
                     new[] { "--ranker-cpu-model", "consec", "--ranker-cpu-model", "consec" },
                     new[] { "--ranker-cpu-model", "sandwich" },
                     new[] { "--ranker-cpu-model", "deberta" },
                     new[] { "--ranker-report" },
                     new[] { "--ranker-report", "one", "--ranker-report", "two" },
                     new[] { "cpu", "trailing" },
                     new[] { "--unknown", "value" },
                     new[] { "--ranker-cpu-model", "consec", "npu" }
                 })
        {
            AssertThrows<InvalidOperationException>(
                () => ParseRankerBenchmarkOptions(
                    invalid,
                    0,
                    allowDump: true,
                    reportOption: "--ranker-report",
                    usage: benchmarkUsage),
                $"Invalid benchmark tail was accepted: {string.Join(' ', invalid)}");
        }
        AssertThrows<InvalidOperationException>(
            () => ParseRankerBenchmarkOptions(
                ["--dump", "scores.json"],
                0,
                allowDump: false,
                reportOption: "--register-report",
                usage: benchmarkUsage),
            "The register benchmark parser accepted a ranker-only dump option.");

        // A removed gated mode must fail the run instead of falling through to
        // the default smoke set and reporting a pass it never performed.
        foreach (string removedMode in new[]
                 {
                     "--smart-live", "--smart-gpu-live", "--foundry-live",
                     "--translation-benchmark"
                 })
        {
            Assert(
                TestCommandLine.FindUnknownOption([removedMode]) == removedMode,
                $"A removed gated mode {removedMode} was silently ignored.");
        }

        Assert(
            TestCommandLine.FindUnknownOption(
                ["--benchmark-only", "--translation-suite", "set.jsonl", "worker",
                 "--system-id", "rtx5080", "--profile", "smart",
                 "--phrase-backend", "vulkan", "--ranker-cpu-model", "glossbert",
                 "--translation-output", "out.jsonl",
                 "--translation-report", "report.json", "--shard", "1/2",
                 "--max-cases", "10"]) is null,
            "A fully specified translation-suite invocation was rejected.");
        Assert(
            TestCommandLine.FindUnknownOption(
                ["--ranker-benchmark", "set.json", "worker", "cpu",
                 "--ranker-cpu-model", "consec", "--dump", "d.json",
                 "--ranker-report", "r.json"]) is null &&
            TestCommandLine.FindUnknownOption(
                ["--register-benchmark", "set.json", "worker", "npu",
                 "--register-report", "r.json"]) is null &&
            TestCommandLine.FindUnknownOption(
                ["--ranker-sentence-benchmark", "set.json", "worker",
                 "--sentence-report", "s.json", "--output", "o.json"]) is null &&
            TestCommandLine.FindUnknownOption(
                ["--live", "--ocr-image", "a.png", "10", "20",
                 "--screen-ocr-image", "b.png"]) is null,
            "A supported gated mode was reported as unknown.");
        Assert(
            TestCommandLine.FindUnknownOption(["--", "-x", "value"]) is null,
            "The end-of-options separator or a positional value was treated as an option.");

        string artifactBase = Path.Combine(tempRoot, "ranker-artifact-profile");
        string artifactModels = Path.Combine(artifactBase, "Ranker", "Models");
        string glossCpu = Path.Combine(artifactModels, "GlossBert", "Cpu");
        string glossNpu = Path.Combine(artifactModels, "GlossBert", "Npu");
        string embeddings = Path.Combine(artifactModels, "GlossEmbeddings");
        Directory.CreateDirectory(glossCpu);
        Directory.CreateDirectory(glossNpu);
        Directory.CreateDirectory(embeddings);
        File.WriteAllBytes(Path.Combine(artifactModels, "GlossBert", "tokenizer.json"), [1]);
        File.WriteAllBytes(Path.Combine(glossCpu, "glossbert-fp32-dynamic.onnx"), [2]);
        File.WriteAllBytes(Path.Combine(glossCpu, "stale.onnx"), [3]);
        File.WriteAllBytes(Path.Combine(glossNpu, "glossbert-eager-vitis-context.onnx"), [4]);
        File.WriteAllBytes(Path.Combine(embeddings, "eng-senses.vec"), [7]);
        File.WriteAllBytes(Path.Combine(embeddings, "minilm-l6-v2.onnx"), [8]);
        File.WriteAllBytes(Path.Combine(embeddings, "tokenizer.json"), [9]);
        File.WriteAllBytes(Path.Combine(embeddings, "stale-experiment.onnx"), [10]);

        IReadOnlyList<string> biEncoderArtifacts =
            RankerCalibrationBenchmark.EnumerateActiveModelArtifactPaths(
                artifactBase,
                GlossBertRankerProvider.Cpu,
                RankerCpuModelProfile.GlossBert,
                GlossBertDictionarySelectorService.CpuModelCacheId + ":" +
                GlossBertDictionarySelectorService.CpuEmbeddingBundleCacheTag);
        Assert(
            biEncoderArtifacts.Any(path => path.EndsWith("glossbert-fp32-dynamic.onnx", StringComparison.Ordinal)) &&
            biEncoderArtifacts.Any(path => path.EndsWith("eng-senses.vec", StringComparison.Ordinal)) &&
            biEncoderArtifacts.Any(path => path.EndsWith("minilm-l6-v2.onnx", StringComparison.Ordinal)) &&
            biEncoderArtifacts.Any(path =>
#if WINDOWS
                path.EndsWith("GlossEmbeddings\\tokenizer.json", StringComparison.Ordinal)) &&
#else
                path.EndsWith("GlossEmbeddings/tokenizer.json", StringComparison.Ordinal)) &&
#endif
            biEncoderArtifacts.All(path =>
                !path.EndsWith("stale.onnx", StringComparison.Ordinal) &&
                !path.EndsWith("stale-experiment.onnx", StringComparison.Ordinal) &&
                !path.EndsWith("glossbert-eager-vitis-context.onnx", StringComparison.Ordinal)),
            "The GlossBERT bi+cross report omitted active or counted inactive artifacts.");

        IReadOnlyList<string> glossArtifacts =
            RankerCalibrationBenchmark.EnumerateActiveModelArtifactPaths(
                artifactBase,
                GlossBertRankerProvider.Cpu,
                RankerCpuModelProfile.GlossBert,
                GlossBertDictionarySelectorService.CpuModelCacheId + ":pipeline-cross");
        Assert(
            glossArtifacts.Any(path => path.EndsWith("glossbert-fp32-dynamic.onnx", StringComparison.Ordinal)) &&
            glossArtifacts.All(path =>
                !path.EndsWith("stale.onnx", StringComparison.Ordinal) &&
                !path.EndsWith("glossbert-eager-vitis-context.onnx", StringComparison.Ordinal) &&
                !path.EndsWith("minilm-l6-v2.onnx", StringComparison.Ordinal)),
            "The GlossBERT cross-only report counted inactive model bundles.");

        string provenanceBase = Path.Combine(tempRoot, "ranker-provenance-inputs");
        string provenanceDictionaries = Path.Combine(provenanceBase, "Dictionaries");
        string provenanceMorphology = Path.Combine(provenanceBase, "Morphology");
        Directory.CreateDirectory(provenanceDictionaries);
        Directory.CreateDirectory(provenanceMorphology);
        File.WriteAllBytes(Path.Combine(provenanceDictionaries, "eng-rus.tsv.gz"), [1]);
        File.WriteAllBytes(
            Path.Combine(provenanceDictionaries, "eng-rus-vn-senses.tsv.gz"),
            [2]);
        File.WriteAllBytes(Path.Combine(provenanceDictionaries, "jpn-rus.tsv.gz"), [3]);
        File.WriteAllBytes(Path.Combine(provenanceDictionaries, "eng-rus-not-data.json"), [4]);
        File.WriteAllBytes(Path.Combine(provenanceMorphology, "ru-morph.tsv.gz"), [5]);
        File.WriteAllBytes(Path.Combine(provenanceMorphology, "stale.tsv.gz"), [6]);
        IReadOnlyList<string> dictionaryArtifacts =
            RankerBenchmarkProvenance.EnumerateDictionaryArtifactPaths(provenanceBase);
        Assert(
            dictionaryArtifacts.Count == 3 &&
            dictionaryArtifacts.Any(path => path.EndsWith("eng-rus.tsv.gz", StringComparison.Ordinal)) &&
            dictionaryArtifacts.Any(path => path.EndsWith("eng-rus-vn-senses.tsv.gz", StringComparison.Ordinal)) &&
            dictionaryArtifacts.Any(path => path.EndsWith("ru-morph.tsv.gz", StringComparison.Ordinal)),
            "Benchmark provenance did not isolate the exact English dictionary and morphology inputs.");

        string? repositoryRoot = AppContext.BaseDirectory;
        while (repositoryRoot is not null &&
               !File.Exists(Path.Combine(repositoryRoot, "AsteriaScale.slnx")))
        {
            repositoryRoot = Path.GetDirectoryName(repositoryRoot);
        }

        Assert(repositoryRoot is not null, "Repository root was not found above the test binary.");
        string rankerProjectText = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot!,
            "src",
            "AsteriaScale.RankerWorker",
            "AsteriaScale.RankerWorker.csproj"));
        // Owners may still keep legacy SANDWiCH or GlossDeBERTa bundles in the
        // ignored model folders; the worker must never copy them into a build.
        Assert(
            !rankerProjectText.Contains("Models\\Sandwich", StringComparison.OrdinalIgnoreCase) &&
            !rankerProjectText.Contains("Models\\GlossDeberta", StringComparison.OrdinalIgnoreCase),
            "The ranker worker still packages a removed SANDWiCH or GlossDeBERTa bundle.");
        string schemaUserDirectory = Path.Combine(tempRoot, "benchmark-schema-user");
        Directory.CreateDirectory(schemaUserDirectory);
        LocalDictionaryService schemaDictionary = new(
            Path.Combine(AppContext.BaseDirectory, "Dictionaries"),
            schemaUserDirectory);
        await schemaDictionary.LoadAsync();
        string rankerDirectory = Path.Combine(repositoryRoot!, "assets", "Ranker");
        int validatedSets = 0;
        foreach (string setPath in new[]
                 {
                     Path.Combine(rankerDirectory, "ranker-benchmark.json")
                 })
        {
            using System.Text.Json.JsonDocument set =
                System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(setPath));
            foreach (System.Text.Json.JsonElement benchmarkCase in
                     set.RootElement.GetProperty("cases").EnumerateArray())
            {
                string id = benchmarkCase.GetProperty("id").GetString()!;
                string context = benchmarkCase.GetProperty("context").GetString()!;
                string target = benchmarkCase.GetProperty("target").GetString()!;
                Assert(context.Length > 0 && target.Length > 0, $"{id}: empty context or target.");
                int anchor = 0;
                if (benchmarkCase.TryGetProperty("characterIndex", out System.Text.Json.JsonElement indexElement))
                {
                    anchor = indexElement.GetInt32();
                    Assert(anchor >= 0 &&
                           anchor + target.Length <= context.Length &&
                           context.Substring(anchor, target.Length) == target,
                        $"{id}: characterIndex does not point at the target.");
                }

                // Multi-word targets legitimately resolve through phrase or
                // dialogue layers and may be absent; single words must exist.
                if (!target.Contains(' '))
                {
                    DictionaryLookupResult lookup = await schemaDictionary.LookupAsync(context, target, anchor);
                    Assert(lookup.Found, $"{id}: target '{target}' is not covered by the dictionaries.");
                }
            }

            validatedSets++;
        }

        Assert(validatedSets == 1, "Expected exactly the ranker development set.");

        // Translation sets (roadmap #13) carry references and term anchors
        // instead of candidates; validate that shape separately.
        string translationDirectory = Path.Combine(repositoryRoot!, "assets", "Translation");
        int translationSets = 0;
        foreach (string setPath in new[]
                 {
                     Path.Combine(translationDirectory, "translation-benchmark.json")
                 })
        {
            TranslationBenchmark.ValidateSetSchema(setPath);
            using System.Text.Json.JsonDocument set =
                System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(setPath));
            int caseCount = 0;
            foreach (System.Text.Json.JsonElement translationCase in
                     set.RootElement.GetProperty("cases").EnumerateArray())
            {
                string id = translationCase.GetProperty("id").GetString()!;
                string source = translationCase.GetProperty("source").GetString()!;
                string reference = translationCase.GetProperty("reference").GetString()!;
                Assert(source.Trim().Length > 0 && reference.Trim().Length > 0,
                    $"{id}: source and reference must be non-empty.");
                Assert(TranslationBenchmark.ResidualEnglishRate(reference) == 0,
                    $"{id}: the reference translation still contains Latin words.");
                foreach (System.Text.Json.JsonElement anchor in
                         translationCase.GetProperty("termAnchors").EnumerateArray())
                {
                    string russian = anchor.GetProperty("ru").GetString()!;
                    Assert(russian.Trim().Length > 0,
                        $"{id}: an empty term anchor is meaningless.");
                    Assert(TranslationBenchmark.ContainsRussianStem(reference, russian),
                        $"{id}: anchor '{russian}' is absent from its own reference.");
                }

                caseCount++;
            }

            Assert(caseCount >= 30, $"{Path.GetFileName(setPath)}: expected at least 30 cases.");
            translationSets++;
        }

        Assert(translationSets == 1, "Expected exactly the translation development set.");
    });

    await RunAsync(
        "Translation suite runner and reporting contract",
        () => TranslationSuiteTests.RunAsync(tempRoot));

    await RunAsync(
        "Sense-gate trace for early-exit measurement",
        () => SenseGateTraceTests.RunAsync(tempRoot));

    await RunAsync(
        "Sentence semantic benchmark schema",
        () => RankerSentenceBenchmarkTests.RunAsync(tempRoot));

    await RunAsync(
        "Sentence semantic benchmark metric math",
        () => RankerSentenceBenchmarkMetricTests.RunAsync(tempRoot));

    await RunAsync(
        "ConSeC benchmark provenance pins official reference golden",
        () => RankerBenchmarkProvenanceTests.RunAsync(tempRoot));

    await RunAsync(
        "Persistent Smart dictionary LRU cache",
        () => SmartDictionaryResultCacheTests.RunAsync(tempRoot));

    await RunAsync(
        "Smart phrase translation foundation",
        () => SmartPhraseTranslationFoundationTests.RunAsync(tempRoot));

    await RunAsync(
        "Smart phrase translation hover integration",
        () => SmartHoverPhraseIntegrationTests.RunAsync(tempRoot));

    await RunAsync(
        "Smart hover engine strings",
        () => SmartHoverEngineStringTests.RunAsync(tempRoot));

    await RunAsync(
        "Provider-neutral semantic sense evidence",
        SemanticSenseEvidenceFactoryTests.RunAsync);

    await RunAsync(
        "GlossBERT ranker protocol and host validation",
        () => GlossBertDictionarySelectorServiceTests.RunAsync(tempRoot));

    await RunAsync(
        "Shared GlossBERT decision cache",
        () => GlossBertDecisionCacheTests.RunAsync(tempRoot));

    await RunAsync(
        "TranslateGemma isolated runtime protocol",
        () => TranslateGemmaTranslationServiceTests.RunAsync(tempRoot));

    await RunAsync(
        "Atomic generated-output safety",
        () => AtomicOutputSafetyTests.RunAsync(tempRoot));

    await RunAsync(
        "Ranker worker fail-open and target anchoring",
        RankerWorkerRegressionTests.RunAsync);

    await RunAsync(
        "Ranker provider traits",
        RankerProviderTraitsTests.RunAsync);

    await RunAsync(
        "ConSeC official-reference input and scoring parity",
        () => ConsecRankerTests.RunAsync(tempRoot));

#if WINDOWS
    await RunAsync("Magpie visible cursor mapping", () =>
    {
        AsteriaScale.Interop.NativeMethods.Rect source = new()
        {
            Left = 100,
            Top = 200,
            Right = 1100,
            Bottom = 700
        };
        AsteriaScale.Interop.NativeMethods.Rect destination = new()
        {
            Left = -200,
            Top = 50,
            Right = 1800,
            Bottom = 1050
        };

        bool mappedFirst = VisibleCursorService.TryMapFromSourceToDestination(
            new AsteriaScale.Interop.NativeMethods.Point { X = 100, Y = 200 },
            source,
            destination,
            out AsteriaScale.Interop.NativeMethods.Point first);
        Assert(mappedFirst && first.X == -200 && first.Y == 50,
            "The first source pixel must map to the first destination pixel.");

        bool mappedLast = VisibleCursorService.TryMapFromSourceToDestination(
            new AsteriaScale.Interop.NativeMethods.Point { X = 1099, Y = 699 },
            source,
            destination,
            out AsteriaScale.Interop.NativeMethods.Point last);
        Assert(mappedLast && last.X == 1799 && last.Y == 1049,
            "The last source pixel must map to the last destination pixel.");

        AsteriaScale.Interop.NativeMethods.Point outside = new() { X = 1100, Y = 700 };
        bool mappedOutside = VisibleCursorService.TryMapFromSourceToDestination(
            outside,
            source,
            destination,
            out AsteriaScale.Interop.NativeMethods.Point unchanged);
        Assert(!mappedOutside && unchanged.X == outside.X && unchanged.Y == outside.Y,
            "A cursor outside the source window must stay in physical screen coordinates.");

        nint gameWindow = new(101);
        nint scalingWindow = new(202);
        nint magpieChildWindow = new(203);
        nint unrelatedWindow = new(303);
        Assert(VisibleCursorService.SelectTextSourceWindow(
                   scalingWindow,
                   scalingWindow,
                   gameWindow,
                   sourceWindowValid: true,
                   hoveredProcessId: 20,
                   scalingProcessId: 20,
                   pointInsideDestination: true) == gameWindow,
            "Textractor did not resolve the Magpie destination HWND to the game HWND.");
        Assert(VisibleCursorService.SelectTextSourceWindow(
                   magpieChildWindow,
                   scalingWindow,
                   gameWindow,
                   sourceWindowValid: true,
                   hoveredProcessId: 20,
                   scalingProcessId: 20,
                   pointInsideDestination: true) == gameWindow,
            "A Magpie companion HWND inside the destination was not resolved to the game HWND.");
        Assert(VisibleCursorService.SelectTextSourceWindow(
                   unrelatedWindow,
                   scalingWindow,
                   gameWindow,
                   sourceWindowValid: true,
                   hoveredProcessId: 30,
                   scalingProcessId: 20,
                   pointInsideDestination: true) == unrelatedWindow,
            "An unrelated overlay inside the destination was redirected to the game process.");
        Assert(VisibleCursorService.SelectTextSourceWindow(
                   magpieChildWindow,
                   scalingWindow,
                   gameWindow,
                   sourceWindowValid: true,
                   hoveredProcessId: 20,
                   scalingProcessId: 20,
                   pointInsideDestination: false) == magpieChildWindow,
            "A Magpie-owned window outside the active destination was redirected to the game.");
        Assert(VisibleCursorService.SelectTextSourceWindow(
                   scalingWindow,
                   scalingWindow,
                   gameWindow,
                   sourceWindowValid: false,
                   hoveredProcessId: 20,
                   scalingProcessId: 20,
                   pointInsideDestination: true) == scalingWindow,
            "An invalid Magpie.SrcHWND was used as a Textractor target.");
        return Task.CompletedTask;
    });

    await RunAsync("Neural OCR wrapped sentence", () =>
    {
        OcrLineGeometry[] lines =
        [
            new("Sakuya", 155, 70, 390, 125),
            new("\"That is of little significance. More importantly, you", 135, 190, 1800, 238),
            new("should be able to enter this establishment for no charge", 136, 270, 1900, 320),
            new("with these people.\"", 134, 350, 850, 400),
            new("Back     Skip     Close", 250, 535, 1950, 570)
        ];

        SentenceContext context = WindowsAiOcrService.BuildSentenceContext(lines, 2, 10);
        Assert(context.Text ==
               "More importantly, you should be able to enter this establishment for no charge with these people.\"",
            "Wrapped OCR lines were not reconstructed into the sentence containing the cursor.");
        Assert(!context.Text.Contains("Sakuya", StringComparison.Ordinal) &&
               !context.Text.Contains("little significance", StringComparison.Ordinal),
            "The speaker label or preceding sentence leaked into neural translation context.");
        Assert(context.CurrentSpeaker is { Name: "Sakuya", Gender: SpeakerGender.Unknown },
            "The nearest OCR nameplate was not preserved as typed speaker metadata.");
        Assert(context.Text[context.CharacterIndex..].StartsWith("able", StringComparison.Ordinal),
            "The hovered character position was not preserved after line reconstruction.");

        OcrLineGeometry[] interleavedLines =
        [
            new("You will lead the", 120, 180, 690, 230),
            new("Q.Save", 1500, 245, 1680, 285),
            new("rest of your life tortured by paranoia, constantly", 118, 260, 1740, 310),
            new("questioning whether the person has read your writings!", 119, 340, 1810, 390)
        ];
        SentenceContext interleaved = WindowsAiOcrService.BuildSentenceContext(
            interleavedLines,
            0,
            9);
        Assert(interleaved.Text ==
               "You will lead the rest of your life tortured by paranoia, constantly " +
               "questioning whether the person has read your writings!",
            "An unrelated UI line between dialogue rows interrupted spatial sentence assembly.");
        Assert(!interleaved.Text.Contains("Q.Save", StringComparison.Ordinal),
            "An unrelated side control leaked into the dialogue sentence.");
        Assert(interleaved.CurrentSpeaker is null,
            "An unrelated UI control was mistaken for an OCR speaker nameplate.");
        return Task.CompletedTask;
    });
#else
    MacSmoke.Skip(
        "Magpie visible cursor mapping",
        "VisibleCursorService and Interop.NativeMethods live in the WPF assembly");
    MacSmoke.Skip(
        "Neural OCR wrapped sentence",
        "WindowsAiOcrService (Windows AI OCR) lives in the WPF assembly");
#endif

    await RunAsync("Sentence boundaries", () =>
    {
        SentenceContext english = OcrTextRules.ExtractSentence(
            "First sentence. This is the complete second sentence! Last one?",
            28);
        Assert(english.Text == "This is the complete second sentence!",
            "English sentence boundaries were not respected.");

        const string titledAddressee =
            "Kotarou put in a lot of effort to prepare this for you, Mr. Giraffe. " +
            "I would appreciate it if you could forgive him.";
        const string expectedTitledSentence =
            "Kotarou put in a lot of effort to prepare this for you, Mr. Giraffe.";
        foreach (string titledTarget in new[] { "effort", "Giraffe" })
        {
            SentenceContext titled = OcrTextRules.ExtractSentence(
                titledAddressee,
                titledAddressee.IndexOf(titledTarget, StringComparison.Ordinal));
            Assert(titled.Text == expectedTitledSentence,
                $"An English title truncated the OCR sentence around '{titledTarget}': '{titled.Text}'");
        }

        const string scientificAbbreviations =
            "Dr. Rivera reviewed Fig. 2 carefully. The result was conclusive.";
        SentenceContext scientific = OcrTextRules.ExtractSentence(
            scientificAbbreviations,
            scientificAbbreviations.IndexOf("reviewed", StringComparison.Ordinal));
        Assert(scientific.Text == "Dr. Rivera reviewed Fig. 2 carefully.",
            $"Scientific abbreviations corrupted the OCR sentence: '{scientific.Text}'");

        foreach ((string source, string target, string expected) in new[]
                 {
                     ("No. I refuse.", "I", "I refuse."),
                     ("Wait 5 sec. Then go.", "Wait", "Wait 5 sec."),
                     ("Option A. Next choice.", "Option", "Option A."),
                     ("John Smith Jr. He left.", "John", "John Smith Jr."),
                     ("He only said \"Dr.\" I left.", "Dr", "He only said \"Dr.\"")
                 })
        {
            SentenceContext separate = OcrTextRules.ExtractSentence(
                source,
                source.IndexOf(target, StringComparison.Ordinal));
            Assert(separate.Text == expected,
                $"An ambiguous abbreviation merged separate sentences: '{separate.Text}'");
        }

        SentenceContext japanese = OcrTextRules.ExtractSentence(
            "彼は来た。私はここで待っている！次は？",
            10);
        Assert(japanese.Text == "私はここで待っている！",
            "Japanese sentence boundaries were not respected.");

        // Roadmap 14.3: an ellipsis is one mark, not two or three terminators.
        // Visual novels use it constantly, and splitting on its first dot hands
        // the translator a fragment - which no model can recover from.
        const string hesitation = "I... don't really know what to say.";
        SentenceContext middle = OcrTextRules.ExtractSentence(
            hesitation,
            hesitation.IndexOf("know", StringComparison.Ordinal));
        Assert(middle.Text == hesitation,
            $"A mid-utterance ellipsis truncated the reply: '{middle.Text}'");

        const string leadingAnchor = "I... don't really know what to say.";
        SentenceContext beforeEllipsis = OcrTextRules.ExtractSentence(
            leadingAnchor,
            0);
        Assert(beforeEllipsis.Text == leadingAnchor,
            $"An anchor before the ellipsis lost the rest of the reply: '{beforeEllipsis.Text}'");

        // A trailing ellipsis does close the utterance: the next capitalised
        // clause is a separate reply, exactly like a full stop.
        const string trailing = "I don't know... Maybe we should just go.";
        SentenceContext firstHalf = OcrTextRules.ExtractSentence(
            trailing,
            trailing.IndexOf("know", StringComparison.Ordinal));
        Assert(firstHalf.Text == "I don't know...",
            $"A trailing ellipsis did not close the utterance: '{firstHalf.Text}'");
        SentenceContext secondHalf = OcrTextRules.ExtractSentence(
            trailing,
            trailing.IndexOf("Maybe", StringComparison.Ordinal));
        Assert(secondHalf.Text == "Maybe we should just go.",
            $"The clause after an ellipsis was not a separate reply: '{secondHalf.Text}'");

        SentenceContext unicodeEllipsis = OcrTextRules.ExtractSentence(
            "That's… not what I meant at all.",
            "That's… not what I meant at all.".IndexOf("meant", StringComparison.Ordinal));
        Assert(unicodeEllipsis.Text == "That's… not what I meant at all.",
            $"A unicode ellipsis truncated the reply: '{unicodeEllipsis.Text}'");

        SentenceContext quoted = OcrTextRules.ExtractSentence(
            "\"Wait...\" she said. Then she left.",
            5);
        Assert(quoted.Text == "\"Wait...\"",
            $"A quoted ellipsis lost its closing quote: '{quoted.Text}'");

        const string damagedFuture = "\"You'11 go to the afterlife after you grow old and die!";
        SentenceContext repairedFuture = OcrTextRules.ExtractSentence(
            damagedFuture,
            damagedFuture.IndexOf("afterlife", StringComparison.Ordinal));
        Assert(repairedFuture.Text == "You'll go to the afterlife after you grow old and die!",
            $"A proven OCR contraction or unmatched dialogue quote was not repaired: '{repairedFuture.Text}'");
        Assert(repairedFuture.Text[repairedFuture.CharacterIndex..]
                .StartsWith("afterlife", StringComparison.Ordinal),
            "OCR edge repair displaced the hovered word index.");

        string longSentence = string.Join(" ", Enumerable.Repeat("word", 100)) + ".";
        SentenceContext preservedLong = OcrTextRules.ExtractSentence(longSentence, 250);
        Assert(preservedLong.Text == longSentence && preservedLong.Text.Length > 420,
            "OCR silently sliced a long sentence instead of preserving or rejecting it as a whole.");
        return Task.CompletedTask;
    });

#if WINDOWS
    await RunAsync("Smart OCR capture geometry", () =>
    {
        System.Drawing.Rectangle monitor = new(0, 0, 3200, 2000);
        AsteriaScale.Interop.NativeMethods.Point cursor = new() { X = 1600, Y = 1100 };
        System.Drawing.Rectangle first = WindowsAiOcrService.CalculateSentenceCaptureBounds(
            monitor,
            cursor);
        Assert(first.Width == 2400 && first.Height == 720,
            "Smart OCR must not capture the previous full-screen-width 3200x1000 region.");
        Assert(monitor.Contains(first) && first.Contains(cursor.X, cursor.Y),
            "The reduced Smart OCR capture must stay on the selected monitor and contain the cursor.");

        AsteriaScale.Interop.NativeMethods.Point nearby = new() { X = 2150, Y = 1200 };
        System.Drawing.Rectangle reused = WindowsAiOcrService.CalculateSentenceCaptureBounds(
            monitor,
            nearby,
            first);
        Assert(reused == first,
            "Moving over another word in the same dialogue area must keep stable capture coordinates.");

        AsteriaScale.Interop.NativeMethods.Point distant = new() { X = 3150, Y = 1900 };
        System.Drawing.Rectangle moved = WindowsAiOcrService.CalculateSentenceCaptureBounds(
            monitor,
            distant,
            first);
        Assert(moved != first && monitor.Contains(moved) && moved.Contains(distant.X, distant.Y),
            "The capture must be repositioned when the cursor leaves the safe reuse area.");

        System.Drawing.Rectangle leftMonitor = new(-1920, 0, 1920, 1080);
        System.Drawing.Rectangle leftCapture = WindowsAiOcrService.CalculateSentenceCaptureBounds(
            leftMonitor,
            new AsteriaScale.Interop.NativeMethods.Point { X = -1850, Y = 80 });
        Assert(leftMonitor.Contains(leftCapture) &&
               leftCapture.Width == 1920 &&
               leftCapture.Height == 720,
            "Smart OCR geometry must support monitors with negative desktop coordinates.");
        return Task.CompletedTask;
    });
#else
    MacSmoke.Skip(
        "Smart OCR capture geometry",
        "WindowsAiOcrService and Interop.NativeMethods live in the WPF assembly");
#endif

    await RunAsync("Smart OCR unchanged-frame cache guard", () =>
    {
        byte[] frame = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        byte[] sameFrame = frame.ToArray();
        byte[] changedFrame = frame.ToArray();
        changedFrame[2047] ^= 0x40;
        ulong fingerprint = OcrTextRules.ComputeFrameFingerprint(frame);
        Assert(OcrTextRules.ComputeFrameFingerprint(sameFrame) == fingerprint,
            "Identical captured pixels must have the same OCR cache fingerprint.");
        Assert(OcrTextRules.ComputeFrameFingerprint(changedFrame) != fingerprint,
            "Any changed captured pixel must invalidate the OCR cache fingerprint.");

        System.Drawing.Rectangle bounds = new(400, 500, 1600, 720);
        DateTimeOffset cachedAt = DateTimeOffset.UtcNow;
        Assert(OcrTextRules.CanReuseCachedRecognition(
                bounds,
                fingerprint,
                cachedAt,
                bounds,
                fingerprint,
                cachedAt.AddSeconds(5)),
            "An unchanged recent frame should reuse OCR word geometry.");
        Assert(!OcrTextRules.CanReuseCachedRecognition(
                bounds,
                fingerprint,
                cachedAt,
                bounds,
                OcrTextRules.ComputeFrameFingerprint(changedFrame),
                cachedAt.AddSeconds(5)),
            "Changed pixels must never reuse stale OCR word geometry.");
        Assert(!OcrTextRules.CanReuseCachedRecognition(
                bounds,
                fingerprint,
                cachedAt,
                new System.Drawing.Rectangle(401, 500, 1600, 720),
                fingerprint,
                cachedAt.AddSeconds(5)),
            "A different capture region must never reuse cached OCR coordinates.");
        Assert(!OcrTextRules.CanReuseCachedRecognition(
                bounds,
                fingerprint,
                cachedAt,
                bounds,
                fingerprint,
                cachedAt.AddSeconds(31)),
            "The one-entry OCR cache must expire conservatively.");

        // A fingerprint hit proves the frame, not the selection. A static VN
        // frame makes every hover after the first a cache hit, so the
        // per-selection gates have to run again on each of them.
        System.Drawing.Rectangle cacheBounds = new(400, 500, 1600, 720);
        HoverOcrResult clipped = new(
            "he opened the gate and walked",
            "gate",
            13,
            cacheBounds)
        {
            CurrentSpeaker = new CurrentSpeakerContext("Kotori")
        };
        Assert(OcrTextRules.ApplyConfirmedFrameGates(clipped, "Kotori") is
               {
                   IsSentenceContextStable: false
               },
            "A cached frame reported a clipped sentence as stable phrase context.");
        HoverOcrResult wholeSentence = clipped with
        {
            Text = "He opened the gate and walked in."
        };
        Assert(OcrTextRules.ApplyConfirmedFrameGates(wholeSentence, "Kotori") is
               {
                   IsSentenceContextStable: true,
                   CurrentSpeaker.Name: "Kotori"
               },
            "A complete sentence from a confirmed frame lost its stability or speaker.");
        Assert(OcrTextRules.ApplyConfirmedFrameGates(wholeSentence, null)
                   ?.CurrentSpeaker is null,
            "A cached frame resurrected a nameplate that two observations rejected.");
        Assert(OcrTextRules.ApplyConfirmedFrameGates(wholeSentence, "Sakuya")
                   ?.CurrentSpeaker is null,
            "A cached frame accepted a nameplate that was never confirmed twice.");
        Assert(OcrTextRules.ApplyConfirmedFrameGates(null, "Kotori") is null,
            "A missing cached selection must stay missing.");

        // The proportional estimate assumes uniform character width, so on a
        // real line it lands several characters away from the word box under
        // the cursor. Resolving the occurrence must not then latch onto the
        // same letters inside a neighbouring word: "in" lives in "waiting"
        // twice over, and anchoring there hands the dictionary that token.
        const string hoveredLine = "I am waiting in line";
        int resolvedIndex = OcrTextRules.ResolveLineCharacterIndex(
            hoveredLine,
            "in",
            cursorX: 210,
            lineLeft: 0,
            lineRight: 400,
            wordLeft: 205,
            wordRight: 225);
        Assert(resolvedIndex == 13,
            "The hovered word anchored inside a neighbouring word rather than " +
            $"on its own occurrence: index {resolvedIndex} in '{hoveredLine}'.");

        // A word the line text cannot spell keeps the geometric estimate.
        Assert(OcrTextRules.ResolveLineCharacterIndex(
                hoveredLine,
                "queue",
                cursorX: 200,
                lineLeft: 0,
                lineRight: 400,
                wordLeft: 195,
                wordRight: 215) == 10,
            "An unresolvable OCR word did not fall back to the proportional estimate.");
        return Task.CompletedTask;
    });

    await RunAsync("Smart OCR sentence stabilization", () =>
    {
        System.Drawing.Rectangle bounds = new(400, 500, 1600, 720);
        HoverOcrResult incomplete = new(
            "Wait here for a little Kotori.",
            "Wait",
            1,
            bounds);
        const string completeText = "Wait here for a little if you can, Kotori.";
        HoverOcrResult complete = new(
            completeText,
            "Wait",
            1,
            bounds);

        HoverOcrResult? richer = OcrTextRules.StabilizeSentenceContext(
            incomplete,
            complete,
            out bool usedSecond);
        Assert(richer is {
                   Text: completeText,
                   IsSentenceContextStable: true
               } && usedSecond,
            "A compatible richer OCR observation did not replace the incomplete sentence.");

        HoverOcrResult? richerFirst = OcrTextRules.StabilizeSentenceContext(
            complete,
            incomplete,
            out usedSecond);
        Assert(richerFirst is {
                   Text: completeText,
                   IsSentenceContextStable: true
               } && !usedSecond,
            "A later compatible truncation displaced the richer OCR sentence.");

        HoverOcrResult? identical = OcrTextRules.StabilizeSentenceContext(
            complete,
            complete with { CharacterIndex = 2 },
            out usedSecond);
        Assert(identical is {
                   Text: completeText,
                   CharacterIndex: 1,
                   IsSentenceContextStable: true
               } && !usedSecond,
            "Identical OCR observations should stabilize without replacing the first geometry.");

        HoverOcrResult namedComplete = complete with
        {
            CurrentSpeaker = new CurrentSpeakerContext("Sakuya")
        };
        HoverOcrResult? stableNamed = OcrTextRules.StabilizeSentenceContext(
            namedComplete,
            namedComplete,
            out usedSecond);
        Assert(stableNamed?.CurrentSpeaker is { Name: "Sakuya" },
            "Two matching OCR nameplates did not preserve current speaker metadata.");
        HoverOcrResult? conflictingNameplates =
            OcrTextRules.StabilizeSentenceContext(
                namedComplete,
                namedComplete with
                {
                    CurrentSpeaker = new CurrentSpeakerContext("Sakvya")
                },
                out usedSecond);
        Assert(conflictingNameplates?.CurrentSpeaker is null,
            "Conflicting OCR nameplates assigned an unstable speaker identity.");

        HoverOcrResult dangling = complete with
        {
            Text = "You will lead the",
            HoveredWord = "lead",
            CharacterIndex = 9
        };
        HoverOcrResult? danglingStable = OcrTextRules.StabilizeSentenceContext(
            dangling,
            dangling,
            out usedSecond);
        Assert(danglingStable is { IsSentenceContextStable: false },
            "Two identical OCR fragments without a proven sentence ending were marked complete.");

        HoverOcrResult missingStart = complete with
        {
            Text = "t those files still in the recycle bin, though?\"",
            HoveredWord = "files",
            CharacterIndex = 8
        };
        HoverOcrResult? missingStartStable = OcrTextRules.StabilizeSentenceContext(
            missingStart,
            missingStart,
            out usedSecond);
        Assert(missingStartStable is { IsSentenceContextStable: false },
            "An OCR fragment with a visibly missing sentence start reached TranslateGemma.");

        HoverOcrResult speakerPrefixedMissingStart = missingStart with
        {
            Text = "[Park staff] t those files still in the recycle bin, though?\"",
            CharacterIndex = 21
        };
        HoverOcrResult? speakerPrefixedMissingStartStable =
            OcrTextRules.StabilizeSentenceContext(
                speakerPrefixedMissingStart,
                speakerPrefixedMissingStart,
                out usedSecond);
        Assert(speakerPrefixedMissingStartStable is { IsSentenceContextStable: false },
            "A speaker label disguised the missing start of an OCR dialogue fragment.");

        HoverOcrResult incompatible = complete with
        {
            Text = "Wait until the train arrives.",
            CharacterIndex = 1
        };
        HoverOcrResult? guarded = OcrTextRules.StabilizeSentenceContext(
            incomplete,
            incompatible,
            out usedSecond);
        Assert(guarded is {
                   Text: "Wait here for a little Kotori.",
                   IsSentenceContextStable: false
               } && !usedSecond,
            "Incompatible OCR sentences were merged or allowed to drive phrase translation.");
        Assert(!SmartHoverPipeline.HasReliablePhraseContext(guarded!),
            "An unstable OCR sentence must keep the dictionary but suppress TranslateGemma.");
        Assert(SmartHoverPipeline.HasReliablePhraseContext(
                guarded! with { TextSource = HoverTextSource.TextHook }),
            "A process-scoped text-hook match should supersede OCR instability.");

        HoverOcrResult numericConflict = incomplete with
        {
            Text = "Wait here for 10 minutes.",
            CharacterIndex = 1
        };
        HoverOcrResult? numericGuarded = OcrTextRules.StabilizeSentenceContext(
            incomplete with { Text = "Wait here for 5 minutes." },
            numericConflict,
            out usedSecond);
        Assert(numericGuarded is { IsSentenceContextStable: false },
            "Different OCR numbers were erased while checking sentence compatibility.");

        HoverOcrResult? single = OcrTextRules.StabilizeSentenceContext(
            incomplete,
            null,
            out usedSecond);
        Assert(single is { IsSentenceContextStable: false } && !usedSecond,
            "A lone OCR observation was incorrectly marked stable.");
        return Task.CompletedTask;
    });

#if WINDOWS
    await RunAsync("Screen OCR block grouping", () =>
    {
        OcrLineGeometry[] lines =
        [
            new("Sakuya", 120, 80, 360, 132),
            new("That is of little significance. More importantly, you", 105, 260, 1700, 312),
            new("should be able to enter this establishment for no charge", 106, 324, 1820, 376),
            new("with these people.", 104, 388, 720, 440),
            new("Back", 220, 900, 350, 940),
            new("Skip", 1500, 900, 1630, 940)
        ];

        IReadOnlyList<ScreenOcrBlock> blocks = WindowsAiOcrService.BuildScreenBlocks(
            lines,
            new System.Drawing.Rectangle(10, 20, 1920, 1080));
        ScreenOcrBlock dialogue = blocks.Single(block => block.LineCount == 3);
        Assert(dialogue.Text.Contains("establishment", StringComparison.Ordinal) &&
               dialogue.Text.EndsWith("with these people.", StringComparison.Ordinal),
            "Wrapped screen dialogue was not merged into one translation block.");
        Assert(dialogue.Bounds.Left == 114 && dialogue.Bounds.Top == 280,
            "Screen OCR block coordinates were not mapped back to the physical display.");
        Assert(blocks.Count(block => block.LineCount == 1) == 3,
            "Separate speaker and interface labels were incorrectly merged.");
        return Task.CompletedTask;
    });
#else
    MacSmoke.Skip(
        "Screen OCR block grouping",
        "WindowsAiOcrService (Windows AI OCR) lives in the WPF assembly");
#endif

    await RunAsync("Concurrent settings saves", async () =>
    {
        string path = Path.Combine(tempRoot, "concurrent", "settings.json");
        SettingsService service = new(path);
        AppSettings settings = new()
        {
            SelectedPresetId = "vn-balanced",
            Profiles = [new AutomationProfile { Name = "Concurrent", ProcessName = "concurrent" }]
        };

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => service.SaveAsync(settings)));
        AppSettings loaded = await service.LoadAsync();
        Assert(loaded.Profiles.Count == 1, "Concurrent saves corrupted the settings file.");
        Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(),
            "Temporary settings files were not cleaned up.");
    });

#if WINDOWS
    await RunAsync("Magpie config edit", async () =>
    {
        string path = Path.Combine(tempRoot, "magpie", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            "{\"customValue\":42,\"disableEffectCache\":true,\"disableFP16\":true,\"minFrameRate\":60," +
            "\"scalingModes\":[{\"name\":\"Lanczos\",\"effects\":[]}]," +
            "\"profiles\":[{\"scalingMode\":0,\"3DGameMode\":true,\"disableDirectFlip\":true}],\"shortcuts\":{}}");

        MagpieConfigEditor editor = new();
        await editor.ApplyPresetAsync(path, PresetCatalog.Find("vn-ultra"));
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        JsonArray modes = root["scalingModes"]!.AsArray();
        int selected = root["profiles"]![0]!["scalingMode"]!.GetValue<int>();
        Assert(root["customValue"]!.GetValue<int>() == 42, "Unrelated config values must be preserved.");
        Assert(modes.Count == PresetCatalog.All.Count + 1, "All Asteria modes must be injected exactly once.");
        Assert(modes[selected]!["name"]!.GetValue<string>() == PresetCatalog.Find("vn-ultra").EngineModeName,
            "Selected mode index is incorrect.");
        Assert(root["showNotifyIcon"]!.GetValue<bool>() == false, "The helper engine tray icon should be hidden.");
        Assert(root["duplicateFrameDetectionMode"]!.GetValue<int>() == 0,
            "Visual-novel presets must check every frame for duplicates.");
        Assert(root["profiles"]![0]!["frameRateLimiterEnabled"]!.GetValue<bool>() &&
               root["profiles"]![0]!["maxFrameRate"]!.GetValue<double>() == 60.0,
            "Visual-novel presets must keep enough GPU headroom with a 60 FPS limit.");
        Assert(!root["profiles"]![0]!["3DGameMode"]!.GetValue<bool>(),
            "Visual-novel presets must disable 3D game mode so duplicate detection remains active.");
        Assert(!root["profiles"]![0]!["disableDirectFlip"]!.GetValue<bool>(),
            "Visual-novel presets should retain the low-latency DirectFlip path.");
        Assert(root["minFrameRate"]!.GetValue<double>() == 10.0,
            "Visual-novel presets must use the balanced 10 FPS redraw floor.");
        Assert(!root["disableEffectCache"]!.GetValue<bool>() && !root["disableFP16"]!.GetValue<bool>(),
            "Visual-novel presets must retain the shader cache and the faster FP16 path.");

        await editor.ApplyPresetAsync(
            path,
            PresetCatalog.Find("vn-balanced"),
            visualNovelFrameRateLimit: 30);
        root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert(root["profiles"]![0]!["maxFrameRate"]!.GetValue<double>() == 30.0,
            "The configurable VN frame-rate limit must be written to Magpie.");

        foreach (UpscalePreset visualNovelPreset in PresetCatalog.All.Where(preset => preset.IsVisualNovel))
        {
            await editor.ApplyPresetAsync(path, visualNovelPreset);
            root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Assert(root["duplicateFrameDetectionMode"]!.GetValue<int>() == 0 &&
                   root["profiles"]![0]!["frameRateLimiterEnabled"]!.GetValue<bool>() &&
                   root["profiles"]![0]!["maxFrameRate"]!.GetValue<double>() == 60.0 &&
                   !root["profiles"]![0]!["3DGameMode"]!.GetValue<bool>(),
                string.Format("VN performance policy was not applied to {0}.", visualNovelPreset.Id));
        }

        await editor.ApplyPresetAsync(path, PresetCatalog.Default);
        root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        modes = root["scalingModes"]!.AsArray();
        Assert(modes.Count == PresetCatalog.All.Count + 1, "Repeated edits must not duplicate Asteria modes.");

        await editor.ApplyPresetAsync(path, PresetCatalog.Find("general"));
        root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert(root["duplicateFrameDetectionMode"]!.GetValue<int>() == 1 &&
               !root["profiles"]![0]!["frameRateLimiterEnabled"]!.GetValue<bool>(),
            "Non-VN presets must retain dynamic duplicate detection and an unrestricted frame rate.");

        // The written config must describe the preset selected now, not the
        // presets applied before it. Without a backup to restore from, the VN
        // redraw floor is dropped rather than guessed at.
        Assert(!root.ContainsKey("minFrameRate"),
            "The visual-novel redraw floor leaked into a general preset.");
        Assert(!root["profiles"]![0]!["3DGameMode"]!.GetValue<bool>() &&
               !root["profiles"]![0]!["disableDirectFlip"]!.GetValue<bool>(),
            "A general preset must write the same profile policy as a fresh profile.");

        // With the one-time backup present, the user's own value comes back
        // instead of being dropped.
        await File.WriteAllTextAsync(
            path + MagpieConfigEditor.BackupSuffix,
            "{\"minFrameRate\":55}");
        await editor.ApplyPresetAsync(path, PresetCatalog.Find("vn-ultra"));
        root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert(root["minFrameRate"]!.GetValue<double>() == 10.0,
            "The visual-novel redraw floor was not applied over a restored value.");
        await editor.ApplyPresetAsync(path, PresetCatalog.Find("general"));
        root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert(root["minFrameRate"]!.GetValue<double>() == 55.0,
            "Leaving a visual-novel preset did not restore the user's own redraw floor.");
    });

    await RunAsync("Installed effects", () =>
    {
        MagpieEngineService engine = new();
        Assert(engine.IsAvailable, "Magpie should be installed for the integration test.");
        MagpieConfigEditor editor = new();
        foreach (UpscalePreset preset in PresetCatalog.All)
        {
            IReadOnlyList<string> missing = editor.ValidateEffects(engine.EffectsDirectory, preset);
            Assert(missing.Count == 0, string.Format("Missing effects for {0}: {1}", preset.Name, string.Join(", ", missing)));
        }

        return Task.CompletedTask;
    });

    await RunAsync("Engine lifecycle", async () =>
    {
        string configPath = Path.Combine(tempRoot, "magpie-engine", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, "{}");
        MagpieEngineService engine = new(configPath: configPath);
        try
        {
            await engine.EnsureReadyAsync(PresetCatalog.Default);
            Assert(engine.IsOwnedEngineRunning,
                "The Magpie process owned by the integration test did not stay running.");
            Assert(File.Exists(configPath + ".before-asteria.json"), "The original engine config was not backed up.");
        }
        finally
        {
            await engine.DisposeAsync();
        }

        Assert(!engine.IsOwnedEngineRunning,
            "The Magpie process owned by the integration test did not stop cleanly.");
    });
#else
    MacSmoke.Skip(
        "Magpie config edit",
        "MagpieConfigEditor lives in the WPF assembly");
    MacSmoke.Skip(
        "Installed effects",
        "MagpieEngineService and an installed Magpie 0.12.1");
    MacSmoke.Skip(
        "Engine lifecycle",
        "MagpieEngineService starts the real Magpie engine");
#endif

#if WINDOWS
    int screenOcrImageArgument = Array.FindIndex(args, arg =>
        string.Equals(arg, "--screen-ocr-image", StringComparison.OrdinalIgnoreCase));
    if (screenOcrImageArgument >= 0)
    {
        Assert(args.Length > screenOcrImageArgument + 1, "Usage: --screen-ocr-image <path>");
        string imagePath = args[screenOcrImageArgument + 1];
        await RunAsync("Screen OCR image diagnostics", async () =>
        {
            using WindowsAiOcrService ocr = new();
            ScreenOcrResult result = await ocr.RecognizeImageBlocksAsync(imagePath);
            Console.WriteLine($"SCREEN: {result.CaptureBounds}; BLOCKS: {result.Blocks.Count}");
            foreach (ScreenOcrBlock block in result.Blocks)
            {
                Console.WriteLine($"[{block.Bounds}] lines={block.LineCount}: {block.Text}");
            }
        });
    }

    int ocrImageArgument = Array.FindIndex(args, arg =>
        string.Equals(arg, "--ocr-image", StringComparison.OrdinalIgnoreCase));
    if (ocrImageArgument >= 0)
    {
        Assert(args.Length > ocrImageArgument + 3,
            "Usage: --ocr-image <path> <cursor-x> <cursor-y>");
        string imagePath = args[ocrImageArgument + 1];
        int cursorX = int.Parse(args[ocrImageArgument + 2], System.Globalization.CultureInfo.InvariantCulture);
        int cursorY = int.Parse(args[ocrImageArgument + 3], System.Globalization.CultureInfo.InvariantCulture);
        await RunAsync("OCR image diagnostics", async () =>
        {
            using WindowsAiOcrService ocr = new();
            HoverOcrDiagnostic diagnostic = await ocr.DiagnoseImageNearPointAsync(imagePath, cursorX, cursorY);
            Console.WriteLine(diagnostic.Details);
            Console.WriteLine("SELECTION: " + (diagnostic.Selection is null
                ? "<none>"
                : $"{diagnostic.Selection.HoveredWord} | {diagnostic.Selection.Text} | index={diagnostic.Selection.CharacterIndex}"));
        });
    }
#endif

    int rankerBenchmarkArgument = Array.FindIndex(args, arg =>
        string.Equals(arg, "--ranker-benchmark", StringComparison.OrdinalIgnoreCase));
    if (rankerBenchmarkArgument >= 0)
    {
        const string rankerBenchmarkUsage =
            "Usage: --ranker-benchmark <benchmark.json> <workerBaseDirectory> " +
            "[cpu|npu] [--ranker-cpu-model glossbert|consec] " +
            "[--dump <path>] [--ranker-report <path>]";
        Assert(args.Length > rankerBenchmarkArgument + 2,
            rankerBenchmarkUsage);
        ParsedRankerBenchmarkOptions benchmarkOptions = ParseRankerBenchmarkOptions(
            args,
            rankerBenchmarkArgument + 3,
            allowDump: true,
            reportOption: "--ranker-report",
            usage: rankerBenchmarkUsage);
        // Roadmap #8 compares candidates same-build. The report is the artifact
        // that makes that comparison mechanical instead of narrative.
        await RunAsync(
            $"Sense-ranker calibration benchmark ({benchmarkOptions.Provider}/{benchmarkOptions.CpuModel})",
            () =>
            RankerCalibrationBenchmark.RunAsync(
                args[rankerBenchmarkArgument + 1],
                args[rankerBenchmarkArgument + 2],
                benchmarkOptions.Provider,
                benchmarkOptions.DumpPath,
                benchmarkOptions.ReportPath,
                benchmarkOptions.CpuModel));
    }

    int sentenceBenchmarkArgument = Array.FindIndex(args, arg =>
        string.Equals(
            arg,
            "--ranker-sentence-benchmark",
            StringComparison.OrdinalIgnoreCase));
    if (sentenceBenchmarkArgument >= 0)
    {
        SentenceRankerBenchmarkOptions sentenceOptions =
            SentenceRankerBenchmarkOptions.Parse(args, sentenceBenchmarkArgument);
        await RunAsync(
            $"Sentence semantic benchmark (CPU/{sentenceOptions.CpuModel})",
            () => RankerSentenceBenchmark.RunAsync(sentenceOptions));
    }

    int registerBenchmarkArgument = Array.FindIndex(args, arg =>
        string.Equals(arg, "--register-benchmark", StringComparison.OrdinalIgnoreCase));
    if (registerBenchmarkArgument >= 0)
    {
        const string registerBenchmarkUsage =
            "Usage: --register-benchmark <register-set.json> <workerBaseDirectory> " +
            "[cpu|npu] [--ranker-cpu-model glossbert|consec] " +
            "[--register-report <path>]";
        Assert(args.Length > registerBenchmarkArgument + 2,
            registerBenchmarkUsage);
        ParsedRankerBenchmarkOptions registerOptions = ParseRankerBenchmarkOptions(
            args,
            registerBenchmarkArgument + 3,
            allowDump: false,
            reportOption: "--register-report",
            usage: registerBenchmarkUsage);
        await RunAsync(
            $"Register fidelity benchmark ({registerOptions.Provider}/{registerOptions.CpuModel})",
            () =>
            RegisterFidelityBenchmark.RunAsync(
                args[registerBenchmarkArgument + 1],
                args[registerBenchmarkArgument + 2],
                registerOptions.Provider,
                registerOptions.ReportPath,
                registerOptions.CpuModel));
    }

#if WINDOWS
    if (args.Any(arg => string.Equals(arg, "--live", StringComparison.OrdinalIgnoreCase)))
    {
        await RunAsync("Live scaling handshake", async () =>
        {
            Process? targetProcess = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            MagpieEngineService engine = new();
            try
            {
                WindowCatalogService catalog = new();
                WindowInfo? target = null;
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && target is null)
                {
                    await Task.Delay(250);
                    target = catalog.GetWindows().FirstOrDefault(window =>
                        string.Equals(window.ProcessName, "Notepad", StringComparison.OrdinalIgnoreCase));
                }

                Assert(target is not null, "The live test target window did not appear.");
                await engine.ScaleAsync(target!, PresetCatalog.Find("general"), false);

                deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline && !engine.IsScaling)
                {
                    await Task.Delay(150);
                }

                Assert(engine.IsScaling, "The scaling window was not created after the global hotkey.");
                await engine.StopScalingAsync();
            }
            finally
            {
                await engine.StopEngineAsync();
                if (targetProcess is { HasExited: false })
                {
                    targetProcess.CloseMainWindow();
                    if (!targetProcess.WaitForExit(1500))
                    {
                        targetProcess.Kill(true);
                    }
                }

                targetProcess?.Dispose();
            }
        });
    }
#endif

#if WINDOWS
    Console.WriteLine(string.Format("PASS: {0} smoke tests", passed));
#else
    // The macOS-only checks run after the shared set so the shared count and
    // order stay comparable with the Windows run. The project parity check
    // reads this file as each build compiles it and fails unless the Windows
    // default-path registrations and the ones run or skipped here have the same
    // names, in the same order, numbering MacSmoke.SharedRegistrationCount;
    // Summarize then fails if a registration was neither run nor skipped.
    int sharedPassed = passed;
    await RunAsync(
        MacSmoke.MacOnlyPrefix + "project parity with the Windows projects",
        MacProjectParityTests.RunAsync);
    await RunAsync(
        MacSmoke.MacOnlyPrefix + "worker refuses NPU without CPU fallback",
        MacWorkerTests.VerifyNpuRefusedWithoutFallbackAsync);
    await RunAsync(
        MacSmoke.MacOnlyPrefix + "worker answers a ConSeC request on the real bundle",
        MacWorkerTests.VerifyConsecSmokeOnRealBundleAsync);
    // CoreHost (docs/MACOS.md M11), started as the app's supervisor starts it,
    // in its own process group with a private home, TMPDIR and cwd.
    await RunAsync(
        MacSmoke.MacOnlyPrefix + "CoreHost protocol conformance",
        () => CoreHostProtocolTests.RunAsync(tempRoot));
    await RunAsync(
        MacSmoke.MacOnlyPrefix + "CoreHost lifecycle and stdio isolation",
        () => CoreHostLifecycleTests.RunAsync(tempRoot));
    await RunAsync(
        MacSmoke.MacOnlyPrefix + "CoreHost fault containment",
        () => CoreHostFaultTests.RunAsync(tempRoot));
    await RunAsync(
        MacSmoke.MacOnlyPrefix + "CoreHost answers a hover through Core",
        () => CoreHostCoreTests.RunAsync(tempRoot));
    Console.WriteLine(MacSmoke.Summarize(sharedPassed, passed - sharedPassed));
#endif
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL: " + ex);
    return 1;
}
finally
{
    try
    {
        Directory.Delete(tempRoot, true);
    }
    catch
    {
        // Best-effort cleanup of test data.
    }
}

async Task RunAsync(string name, Func<Task> test)
{
    await test();
    passed++;
#if !WINDOWS
    MacSmoke.Record(name);
#endif
    Console.WriteLine("ok  " + name);
}

// Anything that was not exactly "npu" used to become CPU without a word, so a
// typo produced a full benchmark report attributed to the wrong device — the
// measurement equivalent of the silent provider fallback invariant 3 forbids.
static ParsedRankerBenchmarkOptions ParseRankerBenchmarkOptions(
    string[] args,
    int firstOptionalArgument,
    bool allowDump,
    string reportOption,
    string usage)
{
    GlossBertRankerProvider provider = GlossBertRankerProvider.Cpu;
    RankerCpuModelProfile cpuModel = RankerCpuModelProfile.GlossBert;
    string? dumpPath = null;
    string? reportPath = null;
    bool providerSeen = false;
    bool modelSeen = false;
    bool dumpSeen = false;
    bool reportSeen = false;

    for (int index = firstOptionalArgument; index < args.Length; index++)
    {
        string argument = args[index];
        if (string.Equals(argument, "cpu", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "npu", StringComparison.OrdinalIgnoreCase))
        {
            Assert(!providerSeen,
                $"The ranker provider may be specified only once. {usage}");
            providerSeen = true;
            provider = string.Equals(argument, "npu", StringComparison.OrdinalIgnoreCase)
                ? GlossBertRankerProvider.Npu
                : GlossBertRankerProvider.Cpu;
            continue;
        }

        if (string.Equals(
                argument,
                "--ranker-cpu-model",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert(!modelSeen,
                $"--ranker-cpu-model may be specified only once. {usage}");
            modelSeen = true;
            string requested = RequireBenchmarkOptionValue(
                args,
                ref index,
                argument,
                usage);
            if (string.Equals(requested, "glossbert", StringComparison.OrdinalIgnoreCase))
            {
                cpuModel = RankerCpuModelProfile.GlossBert;
            }
            else
            {
                Assert(string.Equals(requested, "consec", StringComparison.OrdinalIgnoreCase),
                    $"Unknown CPU ranker model '{requested}'. {usage}");
                cpuModel = RankerCpuModelProfile.Consec;
            }

            continue;
        }

        if (allowDump &&
            string.Equals(argument, "--dump", StringComparison.OrdinalIgnoreCase))
        {
            Assert(!dumpSeen, $"--dump may be specified only once. {usage}");
            dumpSeen = true;
            dumpPath = RequireBenchmarkOptionValue(args, ref index, argument, usage);
            continue;
        }

        if (string.Equals(argument, reportOption, StringComparison.OrdinalIgnoreCase))
        {
            Assert(!reportSeen,
                $"{reportOption} may be specified only once. {usage}");
            reportSeen = true;
            reportPath = RequireBenchmarkOptionValue(args, ref index, argument, usage);
            continue;
        }

        throw new InvalidOperationException(
            $"Unknown or trailing benchmark argument '{argument}'. {usage}");
    }

    Assert(
        provider == GlossBertRankerProvider.Cpu ||
        cpuModel == RankerCpuModelProfile.GlossBert,
        $"The selected semantic ranker is CPU-only; NPU remains pinned to GlossBERT. {usage}");
    return new ParsedRankerBenchmarkOptions(
        provider,
        cpuModel,
        dumpPath,
        reportPath);
}

static string RequireBenchmarkOptionValue(
    string[] args,
    ref int index,
    string option,
    string usage)
{
    Assert(
        index + 1 < args.Length &&
        !args[index + 1].StartsWith("--", StringComparison.Ordinal),
        $"{option} requires a value. {usage}");
    index++;
    return args[index];
}

// GC.GetTotalMemory is documented as an approximation, and even with
// forceFullCollection it only waits a short interval for the collection to
// finish. A single sample is therefore not a sound basis for a budget: one run
// in seven reported 146.8 MB for a sense index that measured 71.9 MB on every
// other run, with an identical key and candidate count both times. Blocking,
// compacting collections repeated until the figure stops falling measure what
// the index retains instead of sampling an instant.
static long SettledManagedBytes()
{
    long settled = long.MaxValue;
    for (int attempt = 0; attempt < 4; attempt++)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        long current = GC.GetTotalMemory(forceFullCollection: true);
        if (current >= settled)
        {
            break;
        }

        settled = current;
    }

    return settled;
}

/// <summary>
/// Asserts a retained-memory budget and returns the measured size. A figure
/// over budget is re-measured before it fails the run, because the budget
/// describes what the index holds rather than what one instant sampled — a real
/// regression retains the memory and stays over on the second look. The
/// mechanism behind the single outlier was never reproduced, so this guard is
/// deliberately built not to depend on knowing it.
/// </summary>
static long AssertManagedBudget(long baselineBytes, long budgetBytes, string subject)
{
    long used = SettledManagedBytes() - baselineBytes;
    if (used >= budgetBytes)
    {
        used = Math.Min(used, SettledManagedBytes() - baselineBytes);
    }

#if WINDOWS
    Assert(used < budgetBytes,
        $"{subject} exceeded its {budgetBytes / 1024 / 1024} MB managed-memory budget: " +
        $"{used / 1024d / 1024d:N1} MB retained.");
#else
    // The budgets were calibrated on Windows. On macOS the settled figure
    // still includes memory a later collection releases (a later subject
    // measured negative), so it is reported until a macOS budget is measured
    // (ROADMAP card 20 follow-up) instead of being loosened without data.
    MacSmoke.Part(
        "managed-memory-budget",
        subject,
        $"retained {used / 1024d / 1024d:N1} MB against the Windows " +
        $"{budgetBytes / 1024 / 1024} MB budget; NOT ENFORCED on macOS");
#endif
    return used;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action, string message)
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

internal readonly record struct ParsedRankerBenchmarkOptions(
    GlossBertRankerProvider Provider,
    RankerCpuModelProfile CpuModel,
    string? DumpPath,
    string? ReportPath);
