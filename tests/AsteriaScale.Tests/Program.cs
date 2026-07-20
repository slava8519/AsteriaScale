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
            SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.Japanese,
            SmartDictionaryFallbackMode = SmartDictionaryFallbackModes.OnDemand,
            SmartDictionaryKeepWarm = false,
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
               loaded.SmartDictionarySourceLanguage == SmartDictionarySourceLanguages.Japanese &&
               loaded.SmartDictionaryFallbackMode == SmartDictionaryFallbackModes.OnDemand &&
               !loaded.SmartDictionaryKeepWarm,
            "Smart dictionary settings were not preserved.");
        Assert(HoverTranslationModes.Normalize("SCREEN") == HoverTranslationModes.Screen,
            "Screen translation mode was not recognized.");
        Assert(HoverTranslationModes.Normalize("SMART") == HoverTranslationModes.Smart,
            "Smart dictionary mode was not recognized.");
        Assert(SmartDictionaryBackends.Normalize("NPU") == SmartDictionaryBackends.Npu &&
               SmartDictionaryBackends.Normalize("CPU") == SmartDictionaryBackends.Cpu &&
               SmartDictionaryBackends.Normalize("GPU") == SmartDictionaryBackends.Cpu &&
               SmartDictionaryBackends.Normalize("unsupported") == SmartDictionaryBackends.Auto,
            "Smart dictionary backend normalization failed.");
        Assert(HoverTranslationOverlay.SmartAccelerator(
                   GlossBertDictionarySelectorService.CpuEngineLabel + " • НЕ УВЕРЕН") == "CPU" &&
               HoverTranslationOverlay.SmartAccelerator(
                   GlossBertDictionarySelectorService.NpuEngineLabel + " • НЕ УВЕРЕН") == "NPU" &&
               HoverTranslationOverlay.IsSmartAbstentionEngine(
                   GlossBertDictionarySelectorService.NpuEngineLabel + " • НЕ УВЕРЕН"),
            "GlossBERT overlay accelerator/status recognition failed.");
        Assert(HoverTranslationOverlay.IsSmartUnavailableEngine(
                   GlossBertDictionarySelectorService.CpuEngineLabel + " • НЕДОСТУПЕН"),
            "GlossBERT unavailable status recognition failed.");
        Assert(SmartDictionarySourceLanguages.Normalize("EN") == SmartDictionarySourceLanguages.English &&
               SmartDictionarySourceLanguages.Normalize("unsupported") == SmartDictionarySourceLanguages.Auto,
            "Smart dictionary source-language normalization failed.");
        Assert(SmartDictionaryFallbackModes.Normalize("AUTOMATIC") == SmartDictionaryFallbackModes.Automatic &&
               SmartDictionaryFallbackModes.Normalize("unsupported") == SmartDictionaryFallbackModes.Never,
            "Smart dictionary fallback normalization failed.");
        Assert(loaded.Profiles.Count == 1 && loaded.Profiles[0].ProcessName == "novel", "Profile was not preserved.");
        string json = await File.ReadAllTextAsync(path);
        Assert(!json.Contains("\"Details\"", StringComparison.Ordinal), "Computed UI details must not be stored.");
    });

    await RunAsync("Legacy smart dictionary defaults", async () =>
    {
        string path = Path.Combine(tempRoot, "legacy-settings", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"hoverTranslationMode\":\"smart\"}");

        AppSettings loaded = await new SettingsService(path).LoadAsync();
        Assert(loaded.SmartDictionaryBackend == SmartDictionaryBackends.Auto,
            "Existing settings must default to automatic backend selection.");
        Assert(loaded.SmartDictionarySourceLanguage == SmartDictionarySourceLanguages.Auto,
            "Existing settings must default to automatic source-language detection.");
        Assert(loaded.SmartDictionaryFallbackMode == SmartDictionaryFallbackModes.Never,
            "Existing settings must not enable full-translation fallback.");
        Assert(loaded.SmartDictionaryKeepWarm,
            "Existing settings must keep the smart dictionary model warm by default.");

        string gpuPath = Path.Combine(tempRoot, "legacy-gpu-settings", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(gpuPath)!);
        await File.WriteAllTextAsync(gpuPath, "{\"smartDictionaryBackend\":\"gpu\"}");
        AppSettings migrated = await new SettingsService(gpuPath).LoadAsync();
        Assert(migrated.SmartDictionaryBackend == SmartDictionaryBackends.Cpu,
            "The legacy GPU smart-ranker backend must migrate to CPU.");
    });

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

        long managedBefore = GC.GetTotalMemory(true);
        Stopwatch loadTimer = Stopwatch.StartNew();
        LocalDictionaryService dictionary = new();
        await dictionary.LoadAsync();
        loadTimer.Stop();
        Assert(dictionary.EntryCount > 850_000,
            "Expected the complete layered English and Japanese indexes.");
        Assert(dictionary.SourceCount >= 8,
            "Expected all base, Wiktionary, dialogue and visual-novel dictionaries.");

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
        Assert(dialogue.Found && dialogue.IsPhrase &&
               dialogue.Translation.Contains("попроб", StringComparison.OrdinalIgnoreCase),
            "The additional English dialogue dictionary was not loaded.");

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
        string repairedMixed = WindowsAiOcrService.RepairMixedScriptText(
            "She said that happily, almost prancing аrоund.");
        Assert(repairedMixed == "She said that happily, almost prancing around.",
            $"Cyrillic homoglyphs inside Latin tokens were not repaired: {repairedMixed}");
        Assert(WindowsAiOcrService.RepairMixedScriptText("Привет, мир! Hello world") ==
               "Привет, мир! Hello world",
            "Pure-script tokens must never be rewritten.");
        Assert(WindowsAiOcrService.RepairMixedScriptText("Привeт") == "Привет",
            "A Latin homoglyph inside a Russian token must be repaired toward Cyrillic.");

        DictionaryLookupResult beaming = await dictionary.LookupAsync(
            "A beaming, innocent smile appeared on her shining face.", "beaming", 4);
        Assert(beaming.Found &&
               beaming.Translation.Contains("сия", StringComparison.OrdinalIgnoreCase) &&
               !beaming.Translation.Contains("балк", StringComparison.OrdinalIgnoreCase),
            "The VN dictionary must not reduce the adjective 'beaming' to the structural noun 'beam'.");

        DictionaryLookupResult sighed = await dictionary.LookupAsync(
            "He shook his head and sighed.", "sighed", 25);
        Assert(sighed.Found && sighed.Translation == "вздохнуть",
            $"The VN dictionary must keep 'sighed' gender-neutral: {sighed.Translation}");

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
               sighing.Translation.Contains("вздыхать", StringComparison.OrdinalIgnoreCase) &&
               !sighing.Translation.Contains("ах", StringComparison.OrdinalIgnoreCase),
            $"Lemma-level lookup must remove interjection noise from 'sighing': {sighing.Translation}");
        SmartDictionaryHeuristicSelection? sighingFastPath =
            SmartDictionaryHeuristicRanker.TrySelect(
                "He was sighing all night.",
                sighing.Source,
                7,
                NpuDictionarySelectorService.SplitMeanings(sighing.Translation),
                "English");
        Assert(sighingFastPath is { Translation: "вздыхать" },
            $"The real dictionary output did not activate the progressive fast path: {sighingFastPath}");

        DictionaryLookupResult teetered = await dictionary.LookupAsync(
            "He teetered on the brink of collapse.", "teetered", 3);
        Assert(teetered.Found &&
               teetered.Translation.Contains("грани", StringComparison.OrdinalIgnoreCase),
            $"Lemma-level lookup must expose the brink sense for 'teetered': {teetered.Translation}");
        SmartDictionaryHeuristicSelection? teeteredFastPath =
            SmartDictionaryHeuristicRanker.TrySelect(
                "He teetered on the brink of collapse.",
                teetered.Source,
                3,
                NpuDictionarySelectorService.SplitMeanings(teetered.Translation),
                "English");
        Assert(teeteredFastPath is { Translation: "быть на грани" },
            $"The real dictionary output did not activate the brink fast path: {teeteredFastPath}");

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
        Assert(japaneseDialogue.Found && japaneseDialogue.IsPhrase &&
               japaneseDialogue.Translation.Contains("попроб", StringComparison.OrdinalIgnoreCase),
            "The additional Japanese dialogue dictionary was not loaded.");

        long managedDictionaryBytes = GC.GetTotalMemory(true) - managedBefore;
        Console.WriteLine(
            $"    dictionary: {dictionary.EntryCount:N0} entries, {dictionary.SourceCount} sources, " +
            $"{managedDictionaryBytes / 1024d / 1024d:N1} MB managed, {loadTimer.ElapsedMilliseconds:N0} ms load");
        Assert(managedDictionaryBytes < 256L * 1024 * 1024,
            "Layered dictionaries exceeded the 256 MB managed-memory budget.");

        long managedBeforeSenses = GC.GetTotalMemory(true);
        Stopwatch senseLoadTimer = Stopwatch.StartNew();
        await dictionary.LoadSenseMetadataAsync();
        senseLoadTimer.Stop();
        long managedSenseBytes = GC.GetTotalMemory(true) - managedBeforeSenses;
        Assert(dictionary.SenseMetadataKeyCount > 140_000 &&
               dictionary.SenseMetadataCandidateCount > 390_000,
            "The complete packaged aligned-sense index was unexpectedly truncated.");
        Console.WriteLine(
            $"    senses: {dictionary.SenseMetadataKeyCount:N0} keys, " +
            $"{dictionary.SenseMetadataCandidateCount:N0} candidates, " +
            $"{managedSenseBytes / 1024d / 1024d:N1} MB managed, " +
            $"{senseLoadTimer.ElapsedMilliseconds:N0} ms load");
        Assert(managedSenseBytes < 128L * 1024 * 1024,
            "Aligned sense metadata exceeded its 128 MB incremental memory budget.");
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
        GC.KeepAlive(dictionary);
        await dictionary.UnloadAsync();
        Assert(dictionary.EntryCount == 0 && dictionary.SourceCount == 0,
            "Dictionary layers were not released after leaving dictionary mode.");
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

        LocalDictionaryService dictionary = new(sensePath);
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
        Assert(safeRegister is
               {
                   SourceIndex: 2,
                   Translation: "лицо",
                   Reason: "safer dictionary register"
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

        Assert(HoverTranslationOverlay.PreferredWidth(
                   "pants",
                   "брюки; трусы; пыхтение; пыхтеть; трепетать; тосковать",
                   dictionaryMode: true) <= 292,
            "A short multi-meaning card still expands to the legacy wide layout.");

        Assert(SmartDictionarySenseResolver.CanonicalizePartOfSpeech("Adjective") == "adj" &&
               SmartDictionarySenseResolver.CanonicalizePartOfSpeech("ADV") == "adv" &&
               SmartDictionarySenseResolver.CanonicalizePartOfSpeech("proper-noun") == "name",
            "Dictionary POS aliases were not normalized for stable grouping.");

        return Task.CompletedTask;
    });

    await RunAsync("Foundry idle shutdown guard", () =>
    {
        Assert(FoundryTranslationService.IsServerRunning("{\"running\":true,\"state\":\"ready\"}"),
            "A running Foundry server was not recognized.");
        Assert(!FoundryTranslationService.IsServerRunning("{\"running\":false,\"state\":\"not_running\"}"),
            "A stopped Foundry server was treated as running.");
        Assert(FoundryTranslationService.IsServerReadyAndIdle(
                "{\"service\":{\"ready\":true},\"models\":{\"loaded\":0}}"),
            "An idle Foundry server was not recognized.");
        Assert(!FoundryTranslationService.IsServerReadyAndIdle(
                "{\"service\":{\"ready\":true},\"models\":{\"loaded\":1}}"),
            "A server with another loaded model must never be stopped.");
        Assert(!FoundryTranslationService.IsServerReadyAndIdle("not json"),
            "Malformed status must fail closed.");
        return Task.CompletedTask;
    });

    await RunAsync("Residual English translation guard", async () =>
    {
        LocalDictionaryService dictionary = new();
        ResidualEnglishTranslationGuard guard = new(dictionary);

        string repaired = await guard.ProtectAsync(
            "She gave him a sideways glance.",
            "Она поглядела sideways на него.");
        Assert(repaired == "Она поглядела искоса на него.",
            $"A trusted VN exact entry did not repair the residual word: {repaired}");

        string repairedPhrase = await guard.ProtectAsync(
            "She gave him a sideways glance.",
            "Она бросила на него sideways glance.");
        Assert(repairedPhrase == "Она бросила на него взгляд искоса.",
            $"The longest trusted VN phrase was not preferred: {repairedPhrase}");

        string protectedTerms = "Alice показала GPU, DirectX12, WinML и H264 для Bob.";
        string unchangedTerms = await guard.ProtectAsync(
            "Alice showed GPU, DirectX12, WinML and H264 to Bob.",
            protectedTerms);
        Assert(unchangedTerms == protectedTerms,
            $"Names, ALLCAPS and mixed technical terms must remain untouched: {unchangedTerms}");

        string alreadyRussian = "Она уже посмотрела на него искоса.";
        Assert(await guard.ProtectAsync(alreadyRussian, alreadyRussian) == alreadyRussian,
            "Already-Russian text must bypass residual-English correction.");

        string untranslatedFailure = "She gave him a sideways glance.";
        Assert(await guard.ProtectAsync(untranslatedFailure, untranslatedFailure) == untranslatedFailure,
            "An all-English model failure must not be rewritten word by word.");

        string hallucinated = await guard.ProtectAsync(
            "She looked away.",
            "Она посмотрела sideways.");
        Assert(hallucinated == "Она посмотрела sideways.",
            "A Latin fragment absent from the source must not be replaced.");

        await dictionary.UnloadAsync();
    });

    await RunAsync("Foundry batch translation response", () =>
    {
        IReadOnlyDictionary<int, string> translated = FoundryTranslationService.ParseBatchResponse(
            "```json\n{\"translations\":[{\"id\":2,\"text\":\"Добрый вечер\"},{\"id\":5,\"text\":\"До встречи\"}]}\n```");
        Assert(translated.Count == 2 && translated[2] == "Добрый вечер" && translated[5] == "До встречи",
            "Structured screen translations were not parsed by id.");
        return Task.CompletedTask;
    });

    await RunAsync("Smart dictionary response", () =>
    {
        NpuDictionarySelectorService.ParsedSelection parsed =
            NpuDictionarySelectorService.ParseSelectionResponse("1", 2);
        Assert(parsed.Index == 1 && parsed.Confidence == 0.9 &&
               parsed.ContextTranslation.Length == 0,
            "The short smart-dictionary class response was not parsed.");
        NpuDictionarySelectorService.ParsedSelection legacyJson =
            NpuDictionarySelectorService.ParseSelectionResponse(
                "```json\n{\"translation\":\"ignored\",\"index\":0,\"confidence\":0.12}\n```",
                2);
        Assert(legacyJson.Index == 0 && legacyJson.ContextTranslation.Length == 0,
            "Legacy JSON must remain readable without using generated translation or confidence.");
        string[] meanings = NpuDictionarySelectorService.SplitMeanings("банк; берег; банк");
        Assert(meanings.SequenceEqual(["банк", "берег"]),
            "Dictionary meanings were not normalized for NPU selection.");
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

    await RunAsync(
        "Dictionary key and sentence normalization",
        () => LocalDictionaryNormalizationTests.RunAsync(tempRoot));

    await RunAsync("Ranker set schema", async () =>
    {
        string? repositoryRoot = AppContext.BaseDirectory;
        while (repositoryRoot is not null &&
               !File.Exists(Path.Combine(repositoryRoot, "AsteriaScale.slnx")))
        {
            repositoryRoot = Path.GetDirectoryName(repositoryRoot);
        }

        Assert(repositoryRoot is not null, "Repository root was not found above the test binary.");
        LocalDictionaryService schemaDictionary = new();
        await schemaDictionary.LoadAsync();
        string rankerDirectory = Path.Combine(repositoryRoot!, "assets", "Ranker");
        int validatedSets = 0;
        foreach (string setPath in Directory.EnumerateFiles(rankerDirectory, "ranker-*.json"))
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

        Assert(validatedSets >= 4, "Expected the benchmark set and three holdout generations.");
    });

    await RunAsync(
        "Persistent Smart dictionary LRU cache",
        () => SmartDictionaryResultCacheTests.RunAsync(tempRoot));

    await RunAsync(
        "GlossBERT ranker protocol and host validation",
        () => GlossBertDictionarySelectorServiceTests.RunAsync(tempRoot));

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
        Assert(context.Text[context.CharacterIndex..].StartsWith("able", StringComparison.Ordinal),
            "The hovered character position was not preserved after line reconstruction.");
        return Task.CompletedTask;
    });

    await RunAsync("Sentence boundaries", () =>
    {
        SentenceContext english = WindowsAiOcrService.ExtractSentence(
            "First sentence. This is the complete second sentence! Last one?",
            28);
        Assert(english.Text == "This is the complete second sentence!",
            "English sentence boundaries were not respected.");

        SentenceContext japanese = WindowsAiOcrService.ExtractSentence(
            "彼は来た。私はここで待っている！次は？",
            10);
        Assert(japanese.Text == "私はここで待っている！",
            "Japanese sentence boundaries were not respected.");
        return Task.CompletedTask;
    });

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

    await RunAsync("Smart OCR unchanged-frame cache guard", () =>
    {
        byte[] frame = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        byte[] sameFrame = frame.ToArray();
        byte[] changedFrame = frame.ToArray();
        changedFrame[2047] ^= 0x40;
        ulong fingerprint = WindowsAiOcrService.ComputeFrameFingerprint(frame);
        Assert(WindowsAiOcrService.ComputeFrameFingerprint(sameFrame) == fingerprint,
            "Identical captured pixels must have the same OCR cache fingerprint.");
        Assert(WindowsAiOcrService.ComputeFrameFingerprint(changedFrame) != fingerprint,
            "Any changed captured pixel must invalidate the OCR cache fingerprint.");

        System.Drawing.Rectangle bounds = new(400, 500, 1600, 720);
        DateTimeOffset cachedAt = DateTimeOffset.UtcNow;
        Assert(WindowsAiOcrService.CanReuseCachedRecognition(
                bounds,
                fingerprint,
                cachedAt,
                bounds,
                fingerprint,
                cachedAt.AddSeconds(5)),
            "An unchanged recent frame should reuse OCR word geometry.");
        Assert(!WindowsAiOcrService.CanReuseCachedRecognition(
                bounds,
                fingerprint,
                cachedAt,
                bounds,
                WindowsAiOcrService.ComputeFrameFingerprint(changedFrame),
                cachedAt.AddSeconds(5)),
            "Changed pixels must never reuse stale OCR word geometry.");
        Assert(!WindowsAiOcrService.CanReuseCachedRecognition(
                bounds,
                fingerprint,
                cachedAt,
                new System.Drawing.Rectangle(401, 500, 1600, 720),
                fingerprint,
                cachedAt.AddSeconds(5)),
            "A different capture region must never reuse cached OCR coordinates.");
        Assert(!WindowsAiOcrService.CanReuseCachedRecognition(
                bounds,
                fingerprint,
                cachedAt,
                bounds,
                fingerprint,
                cachedAt.AddSeconds(31)),
            "The one-entry OCR cache must expire conservatively.");
        return Task.CompletedTask;
    });

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
        MagpieEngineService engine = new();
        try
        {
            await engine.EnsureReadyAsync(PresetCatalog.Default);
            Assert(engine.IsRunning, "Magpie did not stay running after initialization.");
            Assert(File.Exists(engine.ConfigPath + ".before-asteria.json"), "The original engine config was not backed up.");
        }
        finally
        {
            await engine.StopEngineAsync();
        }

        Assert(!engine.IsRunning, "Magpie did not stop cleanly.");
    });

    if (args.Any(arg => string.Equals(arg, "--foundry-live", StringComparison.OrdinalIgnoreCase)))
    {
        await RunAsync("Foundry live idle shutdown", async () =>
        {
            FoundryTranslationService translator = new();
            try
            {
                await translator.WarmUpAsync();
                Assert(Process.GetProcessesByName("foundrylocald").Length > 0,
                    "Foundry server did not start during neural warm-up.");
                IReadOnlyList<string> screenTranslation = await translator.TranslateManyToRussianAsync(
                    ["Good evening.", "We should go home now!"],
                    CancellationToken.None);
                Assert(screenTranslation.Count == 2 &&
                       screenTranslation.All(text => text.Any(character => character is >= 'А' and <= 'я')),
                    "The live model did not return two Russian screen translations.");
            }
            finally
            {
                await translator.DisposeAsync();
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && Process.GetProcessesByName("foundrylocald").Length > 0)
            {
                await Task.Delay(200);
            }

            Assert(Process.GetProcessesByName("foundrylocald").Length == 0,
                "Idle Foundry server remained alive after the neural translator was disposed.");
        });
    }

    if (args.Any(arg => string.Equals(arg, "--smart-live", StringComparison.OrdinalIgnoreCase)))
    {
        await RunAsync("Smart dictionary live NPU", async () =>
        {
            Process currentProcess = Process.GetCurrentProcess();
            currentProcess.Refresh();
            long privateBefore = currentProcess.PrivateMemorySize64;
            await using NpuDictionarySelectorService selector = new();
            Stopwatch npuWarmTimer = Stopwatch.StartNew();
            await selector.WarmUpAsync();
            npuWarmTimer.Stop();
            Stopwatch npuRiverTimer = Stopwatch.StartNew();
            SmartDictionarySelection river = await selector.SelectAsync(
                "I sat on the bank of the river.",
                "bank",
                13,
                ["банк", "берег"],
                CancellationToken.None);
            npuRiverTimer.Stop();
            Console.WriteLine($"    river raw: {river.Translation} | {river.ContextTranslation} | {river.Confidence:P0}");
            Assert(river.Translation == "берег",
                $"River-bank context selected '{river.Translation}' from '{river.ContextTranslation}'.");

            Stopwatch npuAnimalTimer = Stopwatch.StartNew();
            SmartDictionarySelection animal = await selector.SelectAsync(
                "A bat flew out of the cave.",
                "bat",
                2,
                ["летучая мышь", "бита"],
                CancellationToken.None);
            npuAnimalTimer.Stop();
            Console.WriteLine($"    animal raw: {animal.Translation} | {animal.ContextTranslation} | {animal.Confidence:P0}");
            Assert(animal.Translation == "летучая мышь",
                $"Animal-bat context selected '{animal.Translation}'.");

            SmartDictionarySelection teetering = await selector.SelectAsync(
                "He felt like he was teetering on the verge of utter confusion.",
                "teetering",
                20,
                ["балансировать", "шататься", "быть на грани"],
                "English",
                CancellationToken.None);
            Assert(teetering.Translation == "быть на грани",
                $"Teetering context selected '{teetering.Translation}'.");

            SmartDictionarySelection sighed = await selector.SelectAsync(
                "He shook his head and sighed.",
                "sighed",
                23,
                ["вздохнуть"],
                "English",
                CancellationToken.None);
            Assert(sighed.Translation == "вздохнуть",
                $"Sighed context selected '{sighed.Translation}'.");

            SmartDictionarySelection beaming = await selector.SelectAsync(
                "A beaming, innocent smile appeared on her shining face.",
                "beaming",
                3,
                ["сияющий", "лучезарный", "сияющий от счастья"],
                "English",
                CancellationToken.None);
            Assert(beaming.Translation is "сияющий" or "лучезарный" or "сияющий от счастья",
                $"Beaming context selected '{beaming.Translation}'.");

            int stableWorkerProcessId = selector.WorkerProcessId
                                        ?? throw new InvalidOperationException("NPU worker disappeared before cancellation test.");
            bool cancellationObserved = false;
            Stopwatch cancellationTimer = Stopwatch.StartNew();
            using (CancellationTokenSource movedCursor = new(TimeSpan.FromMilliseconds(50)))
            {
                try
                {
                    await selector.SelectAsync(
                        "The crane spread its wings.",
                        "crane",
                        4,
                        ["подъёмный кран", "журавль"],
                        "English",
                        movedCursor.Token);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved = true;
                    // Expected: the UI no longer needs this result.
                }
            }
            cancellationTimer.Stop();

            Assert(cancellationObserved,
                "The abandoned hover request did not observe cursor cancellation.");
            Assert(cancellationTimer.Elapsed < TimeSpan.FromMilliseconds(750),
                $"Cursor cancellation took {cancellationTimer.ElapsedMilliseconds:N0} ms.");
            Assert(selector.WorkerProcessId == stableWorkerProcessId,
                "Cursor cancellation killed the warm NPU worker.");
            SmartDictionarySelection crane = await selector.SelectAsync(
                "The crane spread its wings.",
                "crane",
                4,
                ["подъёмный кран", "журавль"],
                "English",
                CancellationToken.None);
            Assert(crane.Translation == "журавль",
                $"The post-cancellation request selected '{crane.Translation}'.");
            Console.WriteLine(
                $"    NPU smart: bank={river.Translation} ({river.Confidence:P0}), " +
                $"bat={animal.Translation}, teetering={teetering.Translation}, " +
                $"sighed={sighed.Translation}, beaming={beaming.Translation}");
            Console.WriteLine(
                $"    timings: warm={npuWarmTimer.ElapsedMilliseconds:N0} ms, " +
                $"bank={npuRiverTimer.ElapsedMilliseconds:N0} ms, " +
                $"bat={npuAnimalTimer.ElapsedMilliseconds:N0} ms");

            int workerProcessId = selector.WorkerProcessId
                                  ?? throw new InvalidOperationException("NPU worker is not running after inference.");
            using Process workerProcess = Process.GetProcessById(workerProcessId);
            workerProcess.Refresh();
            long workerPrivateLoaded = workerProcess.PrivateMemorySize64;
            currentProcess.Refresh();
            long privateLoaded = currentProcess.PrivateMemorySize64;
            await selector.UnloadAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(1200);
            currentProcess.Refresh();
            long privateUnloaded = currentProcess.PrivateMemorySize64;
            Assert(!IsProcessRunning(workerProcessId),
                "The isolated NPU worker remained alive after leaving smart mode.");
            Console.WriteLine(
                $"    private memory: host before={privateBefore / 1024d / 1024d:N0} MB, " +
                $"host loaded={privateLoaded / 1024d / 1024d:N0} MB, " +
                $"NPU worker={workerPrivateLoaded / 1024d / 1024d:N0} MB, " +
                $"host after stop={privateUnloaded / 1024d / 1024d:N0} MB");

            if (args.Any(arg => string.Equals(
                    arg,
                    "--smart-lifecycle",
                    StringComparison.OrdinalIgnoreCase)))
            {
                await selector.WarmUpAsync();
                int reloadedWorkerProcessId = selector.WorkerProcessId
                                              ?? throw new InvalidOperationException("NPU worker did not restart.");
                await selector.UnloadAsync();
                Assert(!IsProcessRunning(reloadedWorkerProcessId),
                    "The restarted NPU worker remained alive after its second stop.");
                Console.WriteLine("    NPU model unload/reload lifecycle: ok");
            }
        });
    }

    if (args.Any(arg => string.Equals(arg, "--smart-gpu-live", StringComparison.OrdinalIgnoreCase)))
    {
        await RunAsync("Smart dictionary live GPU", async () =>
        {
            await using FoundryTranslationService selector = new();
            Stopwatch warmTimer = Stopwatch.StartNew();
            await selector.WarmUpAsync();
            warmTimer.Stop();
            Stopwatch riverTimer = Stopwatch.StartNew();
            SmartDictionarySelection river = await selector.SelectDictionaryMeaningAsync(
                "I sat on the bank of the river.",
                "bank",
                13,
                ["банк", "берег"],
                "English",
                CancellationToken.None);
            riverTimer.Stop();
            Stopwatch animalTimer = Stopwatch.StartNew();
            SmartDictionarySelection animal = await selector.SelectDictionaryMeaningAsync(
                "A bat flew out of the cave.",
                "bat",
                2,
                ["летучая мышь", "бита"],
                "English",
                CancellationToken.None);
            animalTimer.Stop();
            Assert(river.Translation == "берег",
                $"GPU context selected '{river.Translation}' for river bank.");
            Assert(animal.Translation == "летучая мышь",
                $"GPU context selected '{animal.Translation}' for animal bat.");
            Console.WriteLine(
                $"    GPU timings: warm={warmTimer.ElapsedMilliseconds:N0} ms, " +
                $"bank={riverTimer.ElapsedMilliseconds:N0} ms, " +
                $"bat={animalTimer.ElapsedMilliseconds:N0} ms");
        });
    }

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

    int rankerBenchmarkArgument = Array.FindIndex(args, arg =>
        string.Equals(arg, "--ranker-benchmark", StringComparison.OrdinalIgnoreCase));
    if (rankerBenchmarkArgument >= 0)
    {
        Assert(args.Length > rankerBenchmarkArgument + 2,
            "Usage: --ranker-benchmark <benchmark.json> <workerBaseDirectory> [cpu|npu]");
        GlossBertRankerProvider benchmarkProvider =
            args.Length > rankerBenchmarkArgument + 3 &&
            string.Equals(args[rankerBenchmarkArgument + 3], "npu", StringComparison.OrdinalIgnoreCase)
                ? GlossBertRankerProvider.Npu
                : GlossBertRankerProvider.Cpu;
        int dumpArgument = Array.FindIndex(args, arg =>
            string.Equals(arg, "--dump", StringComparison.OrdinalIgnoreCase));
        string? dumpPath = dumpArgument >= 0 && args.Length > dumpArgument + 1
            ? args[dumpArgument + 1]
            : null;
        await RunAsync($"GlossBERT calibration benchmark ({benchmarkProvider})", () =>
            RankerCalibrationBenchmark.RunAsync(
                args[rankerBenchmarkArgument + 1],
                args[rankerBenchmarkArgument + 2],
                benchmarkProvider,
                dumpPath));
    }

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

    Console.WriteLine(string.Format("PASS: {0} smoke tests", passed));
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
    Console.WriteLine("ok  " + name);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static bool IsProcessRunning(int processId)
{
    try
    {
        using Process process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}
