using System.Drawing;
using System.IO.Compression;
using System.Text;
using AsteriaScale.Models;
using AsteriaScale.Services;

/// <summary>
/// Typed hover provenance and host-supplied engine labels (owner decision
/// M12, MACOS.md T3 and T4). The Windows strings themselves are pinned by
/// "Smart hover engine strings" and "Hover coordinator ordering and
/// lifecycle", whose files this change leaves untouched. This class checks
/// the rest: the Windows label set reproduces those strings and the macOS set
/// never says "NPU OCR" or "VULKAN" (labels, phrase labels, statuses); every
/// label of both sets reads back to its provenance through the classifier,
/// also in the collapsed form the history stores; pipeline results and
/// coordinator cards carry the provenance their label names; phrase lines
/// carry their backend.
///
/// Expectations are literals, as in the two oracle files: a check against
/// HoverEngineLabels itself would follow any change to it. No real ranker
/// worker or llama-server starts: the selector has no worker, so the ranker
/// routes end as unavailable, and the phrase translator is a fixture.
/// </summary>
internal static class HoverProvenanceTests
{
    private const GlossBertRankerProvider Cpu = GlossBertRankerProvider.Cpu;
    private const GlossBertRankerProvider Npu = GlossBertRankerProvider.Npu;

    // Every provenance value, its label in both host sets, and the card kind
    // the WPF card reads from the Windows label (the kinds of "Settings round
    // trip"): T3 maps provenance one to one onto those kinds.
    private static readonly (HoverProvenance Provenance, string Windows, string AppleVision, string CardKind)[]
        LabelTable =
        [
            (HoverProvenance.Dictionary,
                "NPU OCR  •  СЛОВАРЬ", "APPLE VISION  •  СЛОВАРЬ", "dictionary"),
            (HoverProvenance.Heuristic,
                "NPU OCR  •  SMART-FAST", "APPLE VISION  •  SMART-FAST", "fast"),
            (HoverProvenance.Tatoeba,
                "NPU OCR  •  SMART-FAST  •  TATOEBA", "APPLE VISION  •  SMART-FAST  •  TATOEBA", "fast+tatoeba"),
            (HoverProvenance.ResultCache,
                "NPU OCR  •  SMART-CACHE", "APPLE VISION  •  SMART-CACHE", "cache"),
            (HoverProvenance.Ranker(Cpu),
                "NPU OCR  •  CPU-СМЫСЛ", "APPLE VISION  •  CPU-СМЫСЛ", "CPU"),
            (HoverProvenance.Ranker(Npu),
                "NPU OCR  •  NPU-СМЫСЛ", "APPLE VISION  •  NPU-СМЫСЛ", "NPU"),
            (HoverProvenance.RankerAbstained(Cpu),
                "NPU OCR  •  CPU-СМЫСЛ  •  НЕ УВЕРЕН", "APPLE VISION  •  CPU-СМЫСЛ  •  НЕ УВЕРЕН", "CPU+abstained"),
            (HoverProvenance.RankerAbstained(Npu),
                "NPU OCR  •  NPU-СМЫСЛ  •  НЕ УВЕРЕН", "APPLE VISION  •  NPU-СМЫСЛ  •  НЕ УВЕРЕН", "NPU+abstained"),
            (HoverProvenance.RankerUnavailable(Cpu),
                "NPU OCR  •  CPU-СМЫСЛ  •  НЕДОСТУПЕН", "APPLE VISION  •  CPU-СМЫСЛ  •  НЕДОСТУПЕН", "CPU+unavailable"),
            (HoverProvenance.RankerUnavailable(Npu),
                "NPU OCR  •  NPU-СМЫСЛ  •  НЕДОСТУПЕН", "APPLE VISION  •  NPU-СМЫСЛ  •  НЕДОСТУПЕН", "NPU+unavailable")
        ];

    private const string PhraseContext = "We rested on the bank by the river.";
    private const string PhrasePreview = "Мы отдыхали у реки.";
    private const string PhraseFinal = "Мы отдыхали на берегу реки.";

    private const string Sentence = "We walked along the river at dusk.";
    private const string AmberAlone = "Give me the amber.";

    public static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "hover-provenance");
        Directory.CreateDirectory(root);
        VerifyLabelSets();
        VerifyRankerProviderResolution();
        VerifyReadBack(Path.Combine(root, "history"));
        await VerifyPipelineProvenanceAsync(Path.Combine(root, "pipeline"));
        await VerifyPhraseLineBackendsAsync(Path.Combine(root, "phrase"));
        await VerifyCoordinatorLabelsAsync(Path.Combine(root, "coordinator"));
    }

    private static void VerifyLabelSets()
    {
        Check(LabelTable.Length == HoverProvenance.All.Count &&
              LabelTable.Select(row => row.Provenance).ToHashSet().SetEquals(HoverProvenance.All),
            "HoverProvenance.All and the label table no longer list the same values: " +
            string.Join(", ", HoverProvenance.All));
        foreach ((HoverProvenance provenance, string windows, string appleVision, string cardKind) in LabelTable)
        {
            ExpectText(HoverEngineLabels.WindowsAiNpuOcr.For(provenance), windows, $"Windows label of {provenance}");
            ExpectText(HoverEngineLabels.AppleVision.For(provenance), appleVision, $"Apple Vision label of {provenance}");
            ExpectNoWindowsSourceName(HoverEngineLabels.AppleVision.For(provenance), $"Apple Vision label of {provenance}");
            Check(HoverEngineLabels.WindowsAiNpuOcr.Labelled(provenance) == new LabelledProvenance(provenance, windows) &&
                  HoverEngineLabels.AppleVision.Labelled(provenance) == new LabelledProvenance(provenance, appleVision),
                $"A labelled {provenance} does not pair the provenance with its own label.");
            Check(CardKind(windows) == cardKind,
                $"The WPF card reads the Windows label of {provenance} as '{CardKind(windows)}' instead of '{cardKind}'.");
        }

        ExpectText(HoverEngineLabels.WindowsAiNpuOcr.TextSourceName, "NPU OCR", "Windows text-source name");
        ExpectText(HoverEngineLabels.AppleVision.TextSourceName, "Apple Vision", "Apple Vision text-source name");
        ExpectNoWindowsSourceName(HoverEngineLabels.AppleVision.TextSourceName, "Apple Vision text-source name");
        (TranslateGemmaExecutionBackend Backend, string Windows, string AppleVision)[] phraseLabels =
        [
            (TranslateGemmaExecutionBackend.Cpu, "TRANSLATEGEMMA 4B  •  CPU", "TRANSLATEGEMMA 4B  •  CPU"),
            (TranslateGemmaExecutionBackend.Vulkan, "TRANSLATEGEMMA 4B  •  VULKAN", "TRANSLATEGEMMA 4B  •  GPU")
        ];
        foreach ((TranslateGemmaExecutionBackend backend, string windows, string appleVision) in phraseLabels)
        {
            ExpectText(HoverEngineLabels.WindowsAiNpuOcr.Phrase(backend), windows, $"Windows {backend} phrase label");
            ExpectText(HoverEngineLabels.AppleVision.Phrase(backend), appleVision, $"Apple Vision {backend} phrase label");
            ExpectNoWindowsSourceName(HoverEngineLabels.AppleVision.Phrase(backend), $"Apple Vision {backend} phrase label");
        }

        SmartHoverResult labelled = new(
            "bank",
            "берег",
            HoverEngineLabels.AppleVision.Labelled(HoverProvenance.Ranker(Cpu)),
            DictionaryFound: true,
            IsPhrase: false,
            Language: "EN",
            Confidence: 0.9);
        SmartHoverResult bare = new(
            "bank",
            "берег",
            "NPU OCR  •  CPU-СМЫСЛ",
            DictionaryFound: true,
            IsPhrase: false,
            Language: "EN",
            Confidence: 0.9);
        Check(labelled is { Engine: "APPLE VISION  •  CPU-СМЫСЛ", Translation: "берег", Confidence: 0.9 } &&
              labelled.Provenance == HoverProvenance.Ranker(Cpu) &&
              bare.Provenance is null,
            $"A result does not take its engine and provenance from one labelled value: {labelled}; {bare}");

        bool rejected = false;
        try
        {
            _ = HoverProvenance.Ranker((GlossBertRankerProvider)99);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }

        Check(rejected, "A ranker provenance accepted an undefined sense provider.");
    }

    // The loading card and the three ranker provenances name the provider the
    // pipeline itself will ask for; Auto ranks on the CPU.
    private static void VerifyRankerProviderResolution()
    {
        Check(SmartHoverPipeline.ResolveRankerProvider(SmartDictionaryBackends.Cpu) == Cpu &&
              SmartHoverPipeline.ResolveRankerProvider(SmartDictionaryBackends.Auto) == Cpu &&
              SmartHoverPipeline.ResolveRankerProvider(SmartDictionaryBackends.Npu) == Npu,
            "The smart backend no longer resolves cpu and auto to the CPU ranker and npu to the NPU ranker.");
    }

    // A macOS history holds macOS labels, collapsed to single spaces the way
    // TranslationHistoryService stores every field. Both the live label and
    // the stored one must read back to the same provenance.
    private static void VerifyReadBack(string root)
    {
        Directory.CreateDirectory(root);
        List<(string Source, HoverProvenance Provenance, string Label)> recorded = [];
        using (TranslationHistoryService history = new(
                   Path.Combine(root, "history.json"),
                   flushDelay: TimeSpan.FromHours(1)))
        {
            foreach ((HoverProvenance provenance, string windows, string appleVision, _) in LabelTable)
            {
                foreach (string label in new[] { windows, appleVision })
                {
                    // Distinct sources: the history collapses a repeated source.
                    string source = $"word {recorded.Count}";
                    history.Record(source, "перевод", label);
                    recorded.Add((source, provenance, label));
                }
            }

            IReadOnlyList<TranslationHistoryEntry> entries = history.Snapshot();
            Check(entries.Count == recorded.Count,
                $"The history kept {entries.Count} of {recorded.Count} labelled entries.");
            foreach ((string source, HoverProvenance provenance, string label) in recorded)
            {
                TranslationHistoryEntry entry = entries.Single(item => item.Source == source);
                string storedForm = label.Replace("  ", " ", StringComparison.Ordinal);
                ExpectText(entry.Engine, storedForm, $"Stored label of {provenance}");
                Check(HoverEngineLabelClassifier.ReadProvenance(label) == provenance &&
                      HoverEngineLabelClassifier.ReadProvenance(entry.Engine) == provenance,
                    $"'{label}' (stored as '{entry.Engine}') reads back as " +
                    $"{HoverEngineLabelClassifier.ReadProvenance(label)} / " +
                    $"{HoverEngineLabelClassifier.ReadProvenance(entry.Engine)}, expected {provenance}.");
            }
        }

        foreach (string notPrimary in new[]
                 {
                     "TRANSLATEGEMMA 4B  •  CPU",
                     "TRANSLATEGEMMA 4B  •  VULKAN",
                     "TRANSLATEGEMMA 4B  •  GPU",
                     "TRANSLATEGEMMA 4B • CPU",
                     "",
                     "APPLE VISION",
                     "NPU OCR  •  CPU-СМЫСЛ  •  ЧТО-ТО",
                     "VISION OCR  •  СЛОВАРЬ"
                 })
        {
            Check(HoverEngineLabelClassifier.ReadProvenance(notPrimary) is null,
                $"'{notPrimary}' is not a primary-result label but reads back as " +
                $"{HoverEngineLabelClassifier.ReadProvenance(notPrimary)}.");
        }
    }

    // The routes that need no ranker verdict, under the default and the macOS
    // labels: the pipeline's provenance does not depend on the host, and the
    // engine string is exactly the host's label for it.
    private static async Task VerifyPipelineProvenanceAsync(string root)
    {
        LocalDictionaryService dictionary = CreateDictionary(Path.Combine(root, "dictionary"));
        (string Context, string Target, string Backend, HoverProvenance Provenance, bool Found)[] routes =
        [
            ("The zebra ran away.", "zebra", SmartDictionaryBackends.Cpu, HoverProvenance.Dictionary, false),
            ("I lost the key.", "key", SmartDictionaryBackends.Cpu, HoverProvenance.Tatoeba, true),
            ("The bat flew out of the cave.", "bat", SmartDictionaryBackends.Cpu, HoverProvenance.Heuristic, true),
            ("We shared a quiet moment.", "quiet", SmartDictionaryBackends.Cpu, HoverProvenance.Dictionary, true),
            ("Look at the amber.", "amber", SmartDictionaryBackends.Cpu, HoverProvenance.Tatoeba, true),
            (AmberAlone, "amber", SmartDictionaryBackends.Cpu, HoverProvenance.RankerUnavailable(Cpu), true),
            (AmberAlone, "amber", SmartDictionaryBackends.Npu, HoverProvenance.RankerUnavailable(Npu), true)
        ];
        foreach ((string name, HoverEngineLabels? hostLabels, Func<HoverProvenance, string> label) in LabelSets())
        {
            string setRoot = Path.Combine(root, name);
            List<string> statuses = [];
            await using GlossBertDictionarySelectorService selector = new(setRoot, TimeSpan.FromMinutes(1));
            await using SmartDictionaryResultCache results = new(Path.Combine(setRoot, "results.json"));
            await using GlossBertDecisionCache decisions = new(Path.Combine(setRoot, "decisions.json"));
            using SmartHoverPipeline pipeline = new(
                new AppSettings
                {
                    SmartDictionaryBackend = SmartDictionaryBackends.Cpu,
                    SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.English
                },
                dictionary,
                selector,
                results,
                decisions,
                statuses.Add,
                engineLabels: hostLabels);
            foreach ((string context, string target, string backend, HoverProvenance provenance, bool found) in routes)
            {
                SmartHoverResult result = await pipeline.ResolveSmartHoverAsync(
                    new HoverOcrResult(
                        context,
                        target,
                        context.IndexOf(target, StringComparison.Ordinal),
                        Rectangle.Empty),
                    backend,
                    CancellationToken.None);
                Check(result.Provenance == provenance &&
                      result.DictionaryFound == found &&
                      string.Equals(result.Engine, label(provenance), StringComparison.Ordinal),
                    $"{name}: '{target}' in '{context}' on {backend} gave {result.Provenance} '{result.Engine}', " +
                    $"expected {provenance} '{label(provenance)}'.");
            }

            if (hostLabels is not null)
            {
                foreach (string status in statuses)
                {
                    ExpectNoWindowsSourceName(status, $"{name} pipeline status");
                }
            }
        }
    }

    // All three phrase-line sites (candidate A, the gate's final line, the
    // failed generation) carry the backend the request was made for and the
    // host's label for it.
    private static async Task VerifyPhraseLineBackendsAsync(string root)
    {
        foreach ((string name, HoverEngineLabels? hostLabels, _) in LabelSets())
        {
            foreach ((string setting, TranslateGemmaExecutionBackend backend, string expected) in PhraseCases(hostLabels))
            {
                string caseRoot = Path.Combine(root, name + "-" + setting);
                string bundled = Path.Combine(caseRoot, "bundled");
                Directory.CreateDirectory(bundled);
                // Only a placeholder entry, so the sentence scaffold adds no
                // sense evidence that could make the guard refuse the fixtures.
                WriteGzip(Path.Combine(bundled, "eng-rus.tsv.gz"), ["placeholder\tзаполнитель"]);
                WriteGzip(Path.Combine(bundled, "jpn-rus.tsv.gz"), ["仮\tвременный"]);
                LocalDictionaryService dictionary = new(bundled, Path.Combine(caseRoot, "user"));
                AppSettings settings = new()
                {
                    SmartDictionaryBackend = SmartDictionaryBackends.Cpu,
                    SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.English,
                    SmartPhraseTranslationMode = SmartPhraseTranslationModes.TranslateGemma,
                    SmartPhraseTranslationBackend = setting
                };
                SmartHoverResult primary = new(
                    "bank",
                    "берег",
                    (hostLabels ?? HoverEngineLabels.WindowsAiNpuOcr).Labelled(HoverProvenance.Ranker(Cpu)),
                    DictionaryFound: true,
                    IsPhrase: false,
                    Language: "EN",
                    Confidence: 0.91);
                HoverOcrResult recognized = new(
                    PhraseContext,
                    "bank",
                    PhraseContext.IndexOf("bank", StringComparison.Ordinal),
                    Rectangle.Empty);
                await using GlossBertDictionarySelectorService selector = new(caseRoot, TimeSpan.FromMinutes(1));
                await using SmartDictionaryResultCache results = new(Path.Combine(caseRoot, "results.json"));
                await using GlossBertDecisionCache decisions = new(Path.Combine(caseRoot, "decisions.json"));
                await using SmartPhraseTranslationCache phraseCache = new(Path.Combine(caseRoot, "phrase-cache.json"));

                List<SmartHoverPhraseLine> lines = [];
                using (SmartHoverPipeline pipeline = new(
                           settings,
                           dictionary,
                           selector,
                           results,
                           decisions,
                           static _ => { },
                           new FixturePhraseTranslator { Preview = PhrasePreview },
                           phraseCache,
                           engineLabels: hostLabels))
                {
                    List<SmartHoverPhraseLine> provisional = [];
                    SmartHoverPhraseLine? final = await pipeline.TranslatePhraseLineAsync(
                        recognized,
                        primary,
                        CancellationToken.None,
                        provisional.Add);
                    Check(provisional is [{ IsProvisional: true, Translation: PhrasePreview }] &&
                          final is { IsProvisional: false, Status: SmartPhraseLineStatus.Translated, Translation: PhraseFinal },
                        $"{name}/{setting}: the phrase scenario did not show candidate A and then the final line.");
                    lines.AddRange(provisional);
                    lines.Add(final!);
                }

                using (SmartHoverPipeline pipeline = new(
                           settings,
                           dictionary,
                           selector,
                           results,
                           decisions,
                           static _ => { },
                           new FixturePhraseTranslator { Failure = new IOException("fixture generation failure") },
                           phraseCache,
                           engineLabels: hostLabels))
                {
                    SmartHoverPhraseLine? unavailable = await pipeline.TranslatePhraseLineAsync(
                        recognized,
                        primary,
                        CancellationToken.None);
                    Check(unavailable is { Status: SmartPhraseLineStatus.Unavailable, Translation: "" },
                        $"{name}/{setting}: the failing phrase scenario took another route: {unavailable}");
                    lines.Add(unavailable!);
                }

                foreach (SmartHoverPhraseLine line in lines)
                {
                    Check(line.Backend == backend &&
                          string.Equals(line.Engine, expected, StringComparison.Ordinal),
                        $"{name}/{setting}: the {line.Status} phrase line (provisional: {line.IsProvisional}) " +
                        $"carries {line.Backend} '{line.Engine}', expected {backend} '{expected}'.");
                    if (hostLabels is not null)
                    {
                        ExpectNoWindowsSourceName(line.Engine, $"{name} phrase line");
                    }
                }
            }
        }
    }

    private static (string Setting, TranslateGemmaExecutionBackend Backend, string Label)[] PhraseCases(
        HoverEngineLabels? hostLabels) =>
        hostLabels is null
            ?
            [
                (SmartPhraseTranslationBackends.Cpu, TranslateGemmaExecutionBackend.Cpu, "TRANSLATEGEMMA 4B  •  CPU"),
                (SmartPhraseTranslationBackends.Vulkan, TranslateGemmaExecutionBackend.Vulkan, "TRANSLATEGEMMA 4B  •  VULKAN")
            ]
            :
            [
                (SmartPhraseTranslationBackends.Cpu, TranslateGemmaExecutionBackend.Cpu, "TRANSLATEGEMMA 4B  •  CPU"),
                (SmartPhraseTranslationBackends.Vulkan, TranslateGemmaExecutionBackend.Vulkan, "TRANSLATEGEMMA 4B  •  GPU")
            ];

    // The coordinator under each label set: the warm-up statuses name the
    // host's text source, the loading card and the result card carry the
    // host's labels, the card carries the provenance its label names, and the
    // shown card reaches the history in a form that reads back to it.
    private static async Task VerifyCoordinatorLabelsAsync(string root)
    {
        foreach ((string name, HoverEngineLabels? hostLabels, Func<HoverProvenance, string> label) in LabelSets())
        {
            string textSource = hostLabels is null ? "NPU OCR" : "Apple Vision";
            (string Mode, string Backend, HoverOcrResult Recognized, string Preparing, string Ready,
                HoverProvenance Loading, HoverProvenance Result)[] cases =
            [
                (HoverTranslationModes.Dictionary, SmartDictionaryBackends.Cpu, Recognized(Sentence, "river"),
                    $"Подготовка {textSource} и локального словаря…",
                    "Быстрый перевод готов: 4 записей из 2 словарей",
                    HoverProvenance.Dictionary, HoverProvenance.Dictionary),
                (HoverTranslationModes.Smart, SmartDictionaryBackends.Cpu, Recognized(AmberAlone, "amber"),
                    $"Подготовка {textSource}, словарей и контекстной модели на CPU…",
                    "Умный словарь готов: 4 записей; модель значений загрузится по требованию на CPU",
                    HoverProvenance.Ranker(Cpu), HoverProvenance.RankerUnavailable(Cpu)),
                (HoverTranslationModes.Smart, SmartDictionaryBackends.Npu, Recognized(AmberAlone, "amber"),
                    $"Подготовка {textSource}, словарей и контекстной модели на NPU…",
                    "Умный словарь готов: 4 записей; модель значений загрузится по требованию на NPU",
                    HoverProvenance.Ranker(Npu), HoverProvenance.RankerUnavailable(Npu))
            ];
            foreach ((string mode, string backend, HoverOcrResult recognized, string preparing, string ready,
                         HoverProvenance loadingProvenance, HoverProvenance resultProvenance) in cases)
            {
                string caseRoot = Path.Combine(root, $"{name}-{mode}-{backend}");
                CoordinatorHost host = new() { Recognized = recognized };
                List<string> statuses = [];
                HoverCoordinator<string> coordinator = CreateCoordinator(caseRoot, mode, backend, host, hostLabels);
                await using (coordinator)
                {
                    coordinator.StatusChanged += (_, status) =>
                    {
                        lock (statuses)
                        {
                            statuses.Add(status);
                        }
                    };
                    coordinator.Start();
                    await WaitUntilAsync(() => Count(statuses) >= 2, $"{name}/{mode}/{backend} warm-up");
                    Check(coordinator.TryBeginProcessing(), "The hover gate was unavailable.");
                    await coordinator.StartHover("p1", CancellationToken.None);

                    HoverLoadingCard<string> loading = host.Loading.Single();
                    HoverCard<string> card = host.Cards.Single();
                    Check(Snapshot(statuses).Take(2).SequenceEqual([preparing, ready], StringComparer.Ordinal),
                        $"{name}/{mode}/{backend}: warm-up reported {string.Join(" | ", Snapshot(statuses))}; " +
                        $"expected {preparing} | {ready}.");
                    Check(string.Equals(loading.Engine, label(loadingProvenance), StringComparison.Ordinal) &&
                          string.Equals(card.Engine, label(resultProvenance), StringComparison.Ordinal) &&
                          card.Provenance == resultProvenance,
                        $"{name}/{mode}/{backend}: loading '{loading.Engine}', card {card.Provenance} '{card.Engine}'; " +
                        $"expected loading '{label(loadingProvenance)}', card {resultProvenance} '{label(resultProvenance)}'.");

                    coordinator.RecordShown(card);
                    TranslationHistoryEntry entry = coordinator.History.Snapshot().Single();
                    Check(HoverEngineLabelClassifier.ReadProvenance(entry.Engine) == resultProvenance,
                        $"{name}/{mode}/{backend}: the history entry '{entry.Engine}' reads back as " +
                        $"{HoverEngineLabelClassifier.ReadProvenance(entry.Engine)}, expected {resultProvenance}.");

                    if (hostLabels is not null)
                    {
                        foreach (string text in Snapshot(statuses).Append(loading.Engine).Append(card.Engine))
                        {
                            ExpectNoWindowsSourceName(text, $"{name}/{mode}/{backend} status or label");
                        }
                    }
                }
            }
        }
    }

    // The default (null, which the Windows host passes) and the macOS set,
    // each with its literal labels from the table.
    private static IEnumerable<(string Name, HoverEngineLabels? HostLabels, Func<HoverProvenance, string> Label)>
        LabelSets()
    {
        yield return ("default", null, provenance => LabelTable.Single(row => row.Provenance == provenance).Windows);
        yield return ("apple-vision", HoverEngineLabels.AppleVision,
            provenance => LabelTable.Single(row => row.Provenance == provenance).AppleVision);
    }

    private static HoverCoordinator<string> CreateCoordinator(
        string root,
        string mode,
        string backend,
        CoordinatorHost host,
        HoverEngineLabels? hostLabels)
    {
        string bundled = Path.Combine(root, "bundled");
        string user = Path.Combine(root, "user");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);
        WriteGzip(
            Path.Combine(bundled, "eng-rus.tsv.gz"),
            ["amber\tянтарь; сигнал", "placeholder\tзаполнитель", "river\tрека"]);
        WriteGzip(
            Path.Combine(bundled, "eng-rus-senses.tsv.gz"),
            [
                "amber\tянтарь\tfossilized tree resin\tcommon\tnoun",
                "amber\tсигнал\ta yellow warning signal\tcommon\tnoun"
            ]);
        WriteGzip(Path.Combine(bundled, "jpn-rus.tsv.gz"), ["仮\tвременный"]);
        AppSettings settings = new()
        {
            HoverTranslationEnabled = true,
            HoverTranslationMode = mode,
            SmartDictionaryBackend = backend,
            SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.English,
            SmartDictionaryKeepWarm = false,
            SmartPhraseTranslationMode = SmartPhraseTranslationModes.Off
        };
        HoverCoreServices services = new()
        {
            ResultCache = new SmartDictionaryResultCache(Path.Combine(root, "result-cache.json")),
            PhraseCache = new SmartPhraseTranslationCache(Path.Combine(root, "phrase-cache.json")),
            DecisionCache = new GlossBertDecisionCache(Path.Combine(root, "decision-cache.json")),
            History = new TranslationHistoryService(
                Path.Combine(root, "history.json"),
                flushDelay: TimeSpan.FromMilliseconds(10)),
            PhraseModel = new UnusedPhraseModel(),
            // No worker under this directory: a ranker request fails and the
            // pipeline degrades to the unavailable card, as on a clean clone.
            Selector = new GlossBertDictionarySelectorService(root, TimeSpan.FromMinutes(1)),
            Dictionary = new LocalDictionaryService(bundled, user)
        };
        return new HoverCoordinator<string>(settings, null, host, services, hostLabels);
    }

    private static LocalDictionaryService CreateDictionary(string root)
    {
        string bundled = Path.Combine(root, "bundled");
        Directory.CreateDirectory(bundled);
        // "key" and "bat" have no aligned senses, "amber" has two and an exact
        // sentence translation for one context only, and "quiet moment" is a
        // broad phrase with one meaning, already final.
        WriteGzip(
            Path.Combine(bundled, "eng-rus.tsv.gz"),
            [
                "amber\tянтарь; сигнал",
                "bat\tлетучая мышь; бита",
                "key\tключ; клавиша",
                "quiet moment\tтихая минута"
            ]);
        WriteGzip(
            Path.Combine(bundled, "eng-rus-senses.tsv.gz"),
            [
                "amber\tянтарь\tfossilized tree resin\tcommon\tnoun",
                "amber\tсигнал\ta yellow warning signal\tcommon\tnoun"
            ]);
        WriteGzip(
            Path.Combine(bundled, "eng-rus-dialogue.tsv.gz"),
            [
                "i lost the key\tЯ потерял ключ.",
                "look at the amber\tПосмотри на это."
            ]);
        WriteGzip(Path.Combine(bundled, "jpn-rus.tsv.gz"), ["仮\tвременный"]);
        return new LocalDictionaryService(bundled, Path.Combine(root, "user"));
    }

    // The card kind the WPF card reads, as "Settings round trip" composes it.
    private static string CardKind(string engine) => string.Join(
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

    private static HoverOcrResult Recognized(string context, string target) =>
        new(
            context,
            target,
            context.IndexOf(target, StringComparison.Ordinal),
            Rectangle.Empty);

    private static int Count(List<string> statuses)
    {
        lock (statuses)
        {
            return statuses.Count;
        }
    }

    private static List<string> Snapshot(List<string> statuses)
    {
        lock (statuses)
        {
            return [.. statuses];
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{what} did not finish.");
            }

            await Task.Delay(10);
        }
    }

    private static void WriteGzip(string path, IEnumerable<string> rows)
    {
        using FileStream file = File.Create(path);
        using GZipStream gzip = new(file, CompressionLevel.SmallestSize);
        using StreamWriter writer = new(gzip, new UTF8Encoding(false));
        foreach (string row in rows)
        {
            writer.WriteLine(row);
        }
    }

    private static void ExpectText(string actual, string expected, string what)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{what} is '{actual}', expected '{expected}'.");
        }
    }

    // DoD 20.2 item 3: on macOS no label and no status names the Windows text
    // source or the Windows GPU API, in any letter case.
    private static void ExpectNoWindowsSourceName(string text, string what)
    {
        Check(!text.Contains("NPU OCR", StringComparison.OrdinalIgnoreCase) &&
              !text.Contains("VULKAN", StringComparison.OrdinalIgnoreCase),
            $"{what} names the Windows text source or GPU API: '{text}'.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    // A host that recognizes one fixed line and records what it is shown.
    private sealed class CoordinatorHost : IHoverHost<string>
    {
        private readonly object _gate = new();
        private readonly List<HoverLoadingCard<string>> _loading = [];
        private readonly List<HoverCard<string>> _cards = [];

        public required HoverOcrResult Recognized { get; init; }

        public bool SpeakerEditorSupported => false;

        public List<HoverLoadingCard<string>> Loading { get { lock (_gate) { return [.. _loading]; } } }

        public List<HoverCard<string>> Cards { get { lock (_gate) { return [.. _cards]; } } }

        public Task EnsureTextSourceReadyAsync() => Task.CompletedTask;

        public async Task<HoverOcrResult?> RecognizeAsync(
            string anchor,
            bool sentenceContext,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            return Recognized;
        }

        public HoverOcrResult UpgradeRecognized(string anchor, HoverOcrResult recognized) => recognized;

        public SpeakerGameIdentity? ResolveSpeakerGame(string anchor) => null;

        public void OnRecognized(string anchor, HoverOcrResult recognized)
        {
        }

        public bool IsAnchorCurrent(string anchor) => true;

        public void ShowLoading(HoverLoadingCard<string> card)
        {
            lock (_gate)
            {
                _loading.Add(card);
            }
        }

        public void ShowTranslation(HoverCard<string> card)
        {
            lock (_gate)
            {
                _cards.Add(card);
            }
        }

        public void HideCard()
        {
        }

        public void CloseAuxiliaryWindows()
        {
        }

        public void StartTracking()
        {
        }

        public void StopTracking()
        {
        }

        public void ResetTracking()
        {
        }

        public ValueTask ReleaseHostResourcesAsync() => ValueTask.CompletedTask;

        public ValueTask ReleaseHostResourcesAfterHistoryAsync() => ValueTask.CompletedTask;
    }

    // Phrase translation is off in the coordinator cases; any call is a bug.
    private sealed class UnusedPhraseModel : IHoverPhraseModel
    {
        public Task<SmartPhraseTranslationResult> TranslateAsync(
            SmartPhraseTranslationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<SmartPhraseTranslationResult>(
                new InvalidOperationException("Phrase translation is off in this fixture."));

        public Task WarmUpAsync(CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Phrase translation is off in this fixture."));

        public Task UnloadAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // One-shot generation, optionally offering a candidate A first; a failure
    // never offers one, so the pipeline has to report the line as unavailable.
    private sealed class FixturePhraseTranslator : ISmartPhraseTranslator
    {
        public string? Preview { get; init; }
        public Exception? Failure { get; init; }

        public Task<SmartPhraseTranslationResult> TranslateAsync(
            SmartPhraseTranslationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Not cacheable, so every line comes from this generation and
            // never from the shared phrase cache.
            return Failure is null
                ? Task.FromResult(new SmartPhraseTranslationResult(
                    PhraseFinal,
                    request.ModelIdentity,
                    ElapsedMilliseconds: 7,
                    Cacheable: false))
                : Task.FromException<SmartPhraseTranslationResult>(Failure);
        }

        public Task<SmartPhraseTranslationResult> TranslateAsync(
            SmartPhraseTranslationRequest request,
            SmartPhraseDirectDraft? directDraft,
            Action<SmartPhraseTranslationResult>? directPreview,
            CancellationToken cancellationToken)
        {
            if (Failure is null && Preview is not null && directPreview is not null)
            {
                directPreview(new SmartPhraseTranslationResult(
                    Preview,
                    request.ModelIdentity,
                    ElapsedMilliseconds: 0,
                    Cacheable: false));
            }

            return TranslateAsync(request, cancellationToken);
        }
    }
}
