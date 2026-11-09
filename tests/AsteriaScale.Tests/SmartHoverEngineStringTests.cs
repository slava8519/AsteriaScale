using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Text;
using AsteriaScale.Models;
using AsteriaScale.RankerProtocol;
using AsteriaScale.Services;

/// <summary>
/// Pins, byte for byte, the engine string of every result the Smart hover
/// pipeline builds: one scenario for each place that creates a
/// <see cref="SmartHoverResult"/> or a <see cref="SmartHoverPhraseLine"/>.
/// The string is shown on the card, kept in the translation history and the
/// logs, and the WPF overlay still derives the kind of card from it.
///
/// The pins are taken on the code as it is, before the hover orchestration
/// moves into Core and the labels become host-supplied, so that refactor has
/// a Windows oracle it cannot follow by accident. That is why every
/// expectation is a string literal and the file names no label constant: a
/// check against the constant would stay green if the constant, or the
/// expression that picks it, changed.
/// </summary>
internal static class SmartHoverEngineStringTests
{
    // The fixture dictionary has an exact sentence translation for the first
    // context and none for the second, and neither translation names one of
    // the two senses of "amber", so the cheap Tatoeba alignment and the
    // heuristics both leave the word to the ranker.
    private const string AmberWithSentence = "Look at the amber.";
    private const string AmberSentenceTranslation = "Посмотри на это.";
    private const string AmberAlone = "Give me the amber.";
    private const string Amber = "amber";

    private const string PhraseContext = "We rested on the bank by the river.";
    private const string PhrasePreview = "Мы отдыхали у реки.";
    private const string PhraseFinal = "Мы отдыхали на берегу реки.";

    public static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "smart-hover-engine-strings");
        LocalDictionaryService dictionary = CreateDictionary(Path.Combine(root, "dictionary"));

        await VerifyDictionaryAndHeuristicResultsAsync(dictionary, Path.Combine(root, "local"));
        await VerifyRankerResultsAsync(dictionary, Path.Combine(root, "ranker"));
        await VerifyUnavailableRankerResultsAsync(dictionary, Path.Combine(root, "no-worker"));
        await VerifyPhraseLinesAsync(
            Path.Combine(root, "phrase-cpu"),
            SmartPhraseTranslationBackends.Cpu,
            "TRANSLATEGEMMA 4B  •  CPU");
        await VerifyPhraseLinesAsync(
            Path.Combine(root, "phrase-vulkan"),
            SmartPhraseTranslationBackends.Vulkan,
            "TRANSLATEGEMMA 4B  •  VULKAN");
    }

    // Every result here is decided before the ranker, so the selector has no
    // worker: a route that reached the ranker by mistake would come back
    // labelled as unavailable instead of passing.
    private static async Task VerifyDictionaryAndHeuristicResultsAsync(
        LocalDictionaryService dictionary,
        string root)
    {
        List<string> statuses = [];
        await using GlossBertDictionarySelectorService selector =
            CreateSelectorWithoutWorker(root);
        await using SmartDictionaryResultCache results = new(Path.Combine(root, "results.json"));
        await using GlossBertDecisionCache decisions = new(Path.Combine(root, "decisions.json"));
        using SmartHoverPipeline pipeline = CreatePipeline(
            dictionary,
            selector,
            results,
            decisions,
            statuses);

        SmartHoverResult miss = await HoverAsync(
            pipeline,
            "The zebra ran away.",
            "zebra",
            SmartDictionaryBackends.Cpu);
        ExpectEngine(miss.Engine, "NPU OCR  •  СЛОВАРЬ", "Dictionary miss");
        Check(!miss.DictionaryFound,
            "The dictionary-miss scenario found an entry, so it pins another result.");

        SmartHoverResult flatTatoeba = await HoverAsync(
            pipeline,
            "I lost the key.",
            "key",
            SmartDictionaryBackends.Cpu);
        ExpectEngine(
            flatTatoeba.Engine,
            "NPU OCR  •  SMART-FAST  •  TATOEBA",
            "Sentence alignment of a word without aligned senses");
        Check(flatTatoeba is { Translation: "ключ", IsPhrase: false, Confidence: 0.995 },
            $"The flat sentence-alignment scenario took another route: {flatTatoeba}");

        SmartHoverResult flatHeuristic = await HoverAsync(
            pipeline,
            "The bat flew out of the cave.",
            "bat",
            SmartDictionaryBackends.Cpu);
        ExpectEngine(
            flatHeuristic.Engine,
            "NPU OCR  •  SMART-FAST",
            "Heuristic choice for a word without aligned senses");
        Check(flatHeuristic is { Translation: "летучая мышь", DictionaryFound: true } &&
              flatHeuristic.SelectedSenseHints.Count == 0,
            $"The flat heuristic scenario took another route: {flatHeuristic}");

        SmartHoverResult senseTatoeba = await HoverAsync(
            pipeline,
            "It was my first time.",
            "first",
            SmartDictionaryBackends.Cpu);
        ExpectEngine(
            senseTatoeba.Engine,
            "NPU OCR  •  SMART-FAST  •  TATOEBA",
            "Sentence alignment of aligned senses");
        Check(senseTatoeba is
              {
                  Source: "first time",
                  Translation: "впервые",
                  IsPhrase: true,
                  Confidence: 0.995
              },
            $"The aligned-sense sentence-alignment scenario took another route: {senseTatoeba}");

        SmartHoverResult singleSense = await HoverAsync(
            pipeline,
            "You'll go to the afterlife after you grow old and die!",
            "afterlife",
            SmartDictionaryBackends.Cpu);
        ExpectEngine(
            singleSense.Engine,
            "NPU OCR  •  SMART-FAST",
            "Single aligned sense");
        Check(singleSense is { Translation: "загробная жизнь", Confidence: 1 },
            $"The single-sense scenario took another route: {singleSense}");

        SmartHoverResult senseHeuristic = await HoverAsync(
            pipeline,
            "We rested on the bank by the river.",
            "bank",
            SmartDictionaryBackends.Cpu);
        ExpectEngine(
            senseHeuristic.Engine,
            "NPU OCR  •  SMART-FAST",
            "Heuristic choice between aligned senses");
        Check(senseHeuristic is { Translation: "берег", DictionaryFound: true },
            $"The aligned-sense heuristic scenario took another route: {senseHeuristic}");

        SmartHoverResult local = await HoverAsync(
            pipeline,
            "We shared a quiet moment.",
            "quiet",
            SmartDictionaryBackends.Cpu);
        ExpectEngine(local.Engine, "NPU OCR  •  СЛОВАРЬ", "Final dictionary entry");
        Check(local is { Translation: "тихая минута", DictionaryFound: true },
            $"The final-entry scenario took another route: {local}");

        Check(statuses.Count == 0,
            "A result decided before the ranker reported a ranker status: " +
            string.Join(" | ", statuses));
    }

    private static async Task VerifyRankerResultsAsync(
        LocalDictionaryService dictionary,
        string root)
    {
        List<string> statuses = [];
        await using GlossBertDictionarySelectorService selecting =
            CreateSelectorWithFakeWorker(Path.Combine(root, "selecting"), abstain: false);
        await using GlossBertDictionarySelectorService abstaining =
            CreateSelectorWithFakeWorker(Path.Combine(root, "abstaining"), abstain: true);
        await using SmartDictionaryResultCache results =
            new(Path.Combine(root, "results.json"));
        await using SmartDictionaryResultCache emptyResults =
            new(Path.Combine(root, "empty-results.json"));
        await using GlossBertDecisionCache decisions =
            new(Path.Combine(root, "decisions.json"));
        await using SmartDictionaryResultCache abstentionResults =
            new(Path.Combine(root, "abstention-results.json"));
        await using GlossBertDecisionCache abstentionDecisions =
            new(Path.Combine(root, "abstention-decisions.json"));
        using SmartHoverPipeline pipeline = CreatePipeline(
            dictionary,
            selecting,
            results,
            decisions,
            statuses);

        SmartHoverResult cpu = await HoverAsync(
            pipeline,
            AmberAlone,
            Amber,
            SmartDictionaryBackends.Cpu);
        ExpectEngine(cpu.Engine, "NPU OCR  •  CPU-СМЫСЛ", "CPU ranker selection");
        Check(cpu is { Translation: "янтарь", CacheRequest: not null, CacheSelection: not null },
            $"The CPU ranker scenario took another route: {cpu}");

        // The host stores a ranker verdict once its card is shown; the same
        // hover then replays it.
        await pipeline.StoreSmartSelectionSafeAsync(cpu.CacheRequest!, cpu.CacheSelection!);
        SmartHoverResult replayed = await HoverAsync(
            pipeline,
            AmberAlone,
            Amber,
            SmartDictionaryBackends.Cpu);
        ExpectEngine(replayed.Engine, "NPU OCR  •  SMART-CACHE", "Hover result-cache hit");
        Check(replayed is { Translation: "янтарь", CacheRequest: null, CacheSelection: null },
            $"The result-cache scenario took another route: {replayed}");

        // An empty hover cache over the same decision cache: the verdict the
        // first hover stored there is promoted, carrying a request so the host
        // can store it in the hover cache too.
        using (SmartHoverPipeline promoting = CreatePipeline(
                   dictionary,
                   selecting,
                   emptyResults,
                   decisions,
                   statuses))
        {
            SmartHoverResult promoted = await HoverAsync(
                promoting,
                AmberAlone,
                Amber,
                SmartDictionaryBackends.Cpu);
            ExpectEngine(promoted.Engine, "NPU OCR  •  SMART-CACHE", "Shared decision-cache hit");
            Check(promoted is
                  {
                      Translation: "янтарь",
                      CacheRequest: not null,
                      CacheSelection: not null
                  },
                $"The decision-cache scenario took another route: {promoted}");
        }

        // The fake reports the NPU execution provider, device and pinned NPU
        // model identity, so the host accepts it as the NPU ranker.
        SmartHoverResult npu = await HoverAsync(
            pipeline,
            AmberAlone,
            Amber,
            SmartDictionaryBackends.Npu);
        ExpectEngine(npu.Engine, "NPU OCR  •  NPU-СМЫСЛ", "NPU ranker selection");
        Check(npu is { Translation: "янтарь", CacheRequest: not null, CacheSelection: not null },
            $"The NPU ranker scenario took another route: {npu}");
        Check(statuses.Count == 0,
            "A ranker selection reported a status: " + string.Join(" | ", statuses));

        using SmartHoverPipeline unsure = CreatePipeline(
            dictionary,
            abstaining,
            abstentionResults,
            abstentionDecisions,
            statuses);

        SmartHoverResult sentence = await HoverAsync(
            unsure,
            AmberWithSentence,
            Amber,
            SmartDictionaryBackends.Cpu);
        ExpectEngine(
            sentence.Engine,
            "NPU OCR  •  SMART-FAST  •  TATOEBA",
            "Ranker abstention with an exact sentence translation");
        Check(sentence is
              {
                  Source: AmberWithSentence,
                  Translation: AmberSentenceTranslation,
                  IsPhrase: true,
                  Confidence: null
              } &&
              statuses.SequenceEqual(
                  ["Умный словарь: слово неоднозначно — показан точный перевод предложения"],
                  StringComparer.Ordinal),
            $"The abstention-with-sentence scenario took another route: {sentence}; " +
            string.Join(" | ", statuses));

        statuses.Clear();
        SmartHoverResult cpuUnsure = await HoverAsync(
            unsure,
            AmberAlone,
            Amber,
            SmartDictionaryBackends.Cpu);
        ExpectEngine(
            cpuUnsure.Engine,
            "NPU OCR  •  CPU-СМЫСЛ  •  НЕ УВЕРЕН",
            "CPU ranker abstention");
        Check(cpuUnsure is { Translation: "янтарь; сигнал", Confidence: null } &&
              statuses.SequenceEqual(
                  ["Умный словарь: контекста недостаточно — показаны безопасные словарные варианты"],
                  StringComparer.Ordinal),
            $"The CPU abstention scenario took another route: {cpuUnsure}; " +
            string.Join(" | ", statuses));

        statuses.Clear();
        SmartHoverResult npuUnsure = await HoverAsync(
            unsure,
            AmberAlone,
            Amber,
            SmartDictionaryBackends.Npu);
        ExpectEngine(
            npuUnsure.Engine,
            "NPU OCR  •  NPU-СМЫСЛ  •  НЕ УВЕРЕН",
            "NPU ranker abstention");
        Check(npuUnsure is { Translation: "янтарь; сигнал", Confidence: null } &&
              statuses.SequenceEqual(
                  ["Умный словарь: контекста недостаточно — показаны безопасные словарные варианты"],
                  StringComparer.Ordinal),
            $"The NPU abstention scenario took another route: {npuUnsure}; " +
            string.Join(" | ", statuses));
    }

    // A selector whose worker executable is missing fails every warm-up, which
    // is the transport failure the pipeline turns into a degraded card.
    private static async Task VerifyUnavailableRankerResultsAsync(
        LocalDictionaryService dictionary,
        string root)
    {
        List<string> statuses = [];
        await using GlossBertDictionarySelectorService selector =
            CreateSelectorWithoutWorker(root);
        await using SmartDictionaryResultCache results = new(Path.Combine(root, "results.json"));
        await using GlossBertDecisionCache decisions = new(Path.Combine(root, "decisions.json"));
        using SmartHoverPipeline pipeline = CreatePipeline(
            dictionary,
            selector,
            results,
            decisions,
            statuses);

        SmartHoverResult sentence = await HoverAsync(
            pipeline,
            AmberWithSentence,
            Amber,
            SmartDictionaryBackends.Cpu);
        ExpectEngine(
            sentence.Engine,
            "NPU OCR  •  SMART-FAST  •  TATOEBA",
            "Unavailable ranker with an exact sentence translation");
        Check(sentence is
              {
                  Source: AmberWithSentence,
                  Translation: AmberSentenceTranslation,
                  IsPhrase: true,
                  Confidence: null
              } &&
              statuses.SequenceEqual(
                  ["Умный словарь: модель недоступна — показан точный перевод предложения"],
                  StringComparer.Ordinal),
            $"The unavailable-with-sentence scenario took another route: {sentence}; " +
            string.Join(" | ", statuses));

        foreach ((string backend, string expected) in new[]
                 {
                     (SmartDictionaryBackends.Cpu, "NPU OCR  •  CPU-СМЫСЛ  •  НЕДОСТУПЕН"),
                     (SmartDictionaryBackends.Npu, "NPU OCR  •  NPU-СМЫСЛ  •  НЕДОСТУПЕН")
                 })
        {
            statuses.Clear();
            SmartHoverResult unavailable = await HoverAsync(
                pipeline,
                AmberAlone,
                Amber,
                backend);
            ExpectEngine(unavailable.Engine, expected, $"Unavailable {backend} ranker");
            Check(unavailable is { Translation: "янтарь; сигнал", Confidence: null } &&
                  statuses.SequenceEqual(
                      ["Умный словарь: модель недоступна — показаны локальные варианты без полного перевода"],
                      StringComparer.Ordinal),
                $"The unavailable {backend} scenario took another route: {unavailable}; " +
                string.Join(" | ", statuses));
        }
    }

    // The three phrase lines: candidate A shown while the gate runs, the
    // gate's final line, and the line that reports generation as failed.
    private static async Task VerifyPhraseLinesAsync(
        string root,
        string phraseBackend,
        string expected)
    {
        // Only a placeholder entry, so the sentence scaffold adds no sense
        // evidence that could make the guard refuse the fixture translations.
        string bundled = Path.Combine(root, "bundled");
        Directory.CreateDirectory(bundled);
        WriteGzipDictionary(Path.Combine(bundled, "eng-rus.tsv.gz"), ["placeholder\tзаполнитель"]);
        WriteGzipDictionary(Path.Combine(bundled, "jpn-rus.tsv.gz"), ["仮\tвременный"]);
        LocalDictionaryService dictionary = new(bundled, Path.Combine(root, "user"));
        AppSettings settings = new()
        {
            SmartDictionaryBackend = SmartDictionaryBackends.Cpu,
            SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.English,
            SmartPhraseTranslationMode = SmartPhraseTranslationModes.TranslateGemma,
            SmartPhraseTranslationBackend = phraseBackend
        };
        SmartHoverResult primary = new(
            "bank",
            "берег",
            "NPU OCR  •  CPU-СМЫСЛ",
            DictionaryFound: true,
            IsPhrase: false,
            Language: "EN",
            Confidence: 0.91);
        HoverOcrResult recognized = new(
            PhraseContext,
            "bank",
            PhraseContext.IndexOf("bank", StringComparison.Ordinal),
            Rectangle.Empty);

        await using GlossBertDictionarySelectorService selector =
            CreateSelectorWithoutWorker(root);
        await using SmartDictionaryResultCache results = new(Path.Combine(root, "results.json"));
        await using GlossBertDecisionCache decisions = new(Path.Combine(root, "decisions.json"));
        await using SmartPhraseTranslationCache phraseCache =
            new(Path.Combine(root, "phrase-cache.json"));

        using (SmartHoverPipeline pipeline = new(
                   settings,
                   dictionary,
                   selector,
                   results,
                   decisions,
                   static _ => { },
                   new FixturePhraseTranslator { Preview = PhrasePreview },
                   phraseCache))
        {
            List<SmartHoverPhraseLine> provisional = [];
            SmartHoverPhraseLine? final = await pipeline.TranslatePhraseLineAsync(
                recognized,
                primary,
                CancellationToken.None,
                provisional.Add);
            Check(provisional is [{ IsProvisional: true, Translation: PhrasePreview }],
                $"The {phraseBackend} phrase scenario showed no candidate A.");
            ExpectEngine(
                provisional[0].Engine,
                expected,
                $"Provisional {phraseBackend} phrase line");
            Check(final is
                  {
                      IsProvisional: false,
                      Status: SmartPhraseLineStatus.Translated,
                      Translation: PhraseFinal
                  },
                $"The {phraseBackend} phrase scenario did not end with the gate's line: {final}");
            ExpectEngine(final!.Engine, expected, $"Final {phraseBackend} phrase line");
        }

        using (SmartHoverPipeline pipeline = new(
                   settings,
                   dictionary,
                   selector,
                   results,
                   decisions,
                   static _ => { },
                   new FixturePhraseTranslator { Failure = new IOException("fixture generation failure") },
                   phraseCache))
        {
            SmartHoverPhraseLine? unavailable = await pipeline.TranslatePhraseLineAsync(
                recognized,
                primary,
                CancellationToken.None);
            Check(unavailable is
                  {
                      Status: SmartPhraseLineStatus.Unavailable,
                      RejectionReason: null,
                      Translation: ""
                  },
                $"The failing {phraseBackend} phrase scenario took another route: {unavailable}");
            ExpectEngine(
                unavailable!.Engine,
                expected,
                $"Unavailable {phraseBackend} phrase line");
        }
    }

    private static SmartHoverPipeline CreatePipeline(
        LocalDictionaryService dictionary,
        GlossBertDictionarySelectorService selector,
        SmartDictionaryResultCache results,
        GlossBertDecisionCache decisions,
        List<string> statuses) =>
        new(
            new AppSettings
            {
                SmartDictionaryBackend = SmartDictionaryBackends.Cpu,
                SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.English
            },
            dictionary,
            selector,
            results,
            decisions,
            statuses.Add);

    private static Task<SmartHoverResult> HoverAsync(
        SmartHoverPipeline pipeline,
        string context,
        string target,
        string smartBackend) =>
        pipeline.ResolveSmartHoverAsync(
            new HoverOcrResult(
                context,
                target,
                context.IndexOf(target, StringComparison.Ordinal),
                Rectangle.Empty),
            smartBackend,
            CancellationToken.None);

    private static LocalDictionaryService CreateDictionary(string root)
    {
        string bundled = Path.Combine(root, "bundled");
        Directory.CreateDirectory(bundled);
        // Each entry drives one route: "key" and "bat" have no aligned senses,
        // "first time", "afterlife", "bank" and "amber" have them, and
        // "quiet moment" is a broad phrase with one meaning, already final.
        WriteGzipDictionary(
            Path.Combine(bundled, "eng-rus.tsv.gz"),
            [
                "afterlife\tзагробная жизнь",
                "amber\tянтарь; сигнал",
                "bank\tбанк; берег",
                "bat\tлетучая мышь; бита",
                "first time\tвпервые; первый половой акт",
                "key\tключ; клавиша",
                "quiet moment\tтихая минута"
            ]);
        WriteGzipDictionary(
            Path.Combine(bundled, "eng-rus-senses.tsv.gz"),
            [
                "afterlife\tзагробная жизнь\tlife after death\tcommon\tnoun",
                "amber\tянтарь\tfossilized tree resin\tcommon\tnoun",
                "amber\tсигнал\ta yellow warning signal\tcommon\tnoun",
                "bank\tбанк\ta financial institution that accepts deposits\tcommon\tnoun",
                "bank\tберег\tsloping land beside a body of water\tcommon\tnoun",
                "first time\tвпервые\tthe initial occurrence of an event\tcommon\tphrase",
                "first time\tпервый половой акт\tan individual's first instance of sexual intercourse\tsexual\tphrase"
            ]);
        WriteGzipDictionary(
            Path.Combine(bundled, "eng-rus-dialogue.tsv.gz"),
            [
                "i lost the key\tЯ потерял ключ.",
                "it was my first time\tЯ столкнулся с этим впервые.",
                "look at the amber\t" + AmberSentenceTranslation
            ]);
        WriteGzipDictionary(Path.Combine(bundled, "jpn-rus.tsv.gz"), ["仮\tвременный"]);
        return new LocalDictionaryService(bundled, Path.Combine(root, "user"));
    }

    private static void WriteGzipDictionary(string path, IEnumerable<string> rows)
    {
        using FileStream file = File.Create(path);
        using GZipStream gzip = new(file, CompressionLevel.SmallestSize);
        using StreamWriter writer = new(gzip, new UTF8Encoding(false));
        foreach (string row in rows)
        {
            writer.WriteLine(row);
        }
    }

    private static GlossBertDictionarySelectorService CreateSelectorWithoutWorker(string root) =>
        new(root, TimeSpan.FromMinutes(1));

    private static GlossBertDictionarySelectorService CreateSelectorWithFakeWorker(
        string root,
        bool abstain)
    {
        // The selector starts nothing unless the companion layout exists; the
        // factory then runs the fake in place of the executable.
        string rankerDirectory = Path.Combine(root, "Ranker");
        Directory.CreateDirectory(Path.Combine(rankerDirectory, "Models"));
        File.WriteAllBytes(Path.Combine(rankerDirectory, "AsteriaScale.RankerWorker.exe"), []);
        return new GlossBertDictionarySelectorService(
            root,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(30),
            provider => CreateFakeWorker(provider, abstain));
    }

    // The JSONL ranker protocol answered by a script: hello, warm and shutdown
    // with the requested provider's execution provider, device and pinned
    // GlossBERT identity, and select with either the first candidate group or
    // an abstention.
    private static ProcessStartInfo CreateFakeWorker(
        GlossBertRankerProvider provider,
        bool abstain)
    {
        RankerProviderTraits traits = provider.Traits();
        string model = GlossBertDictionarySelectorService.GetModelCacheId(provider) +
                       ":pipeline-cross";
#if WINDOWS
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Check(File.Exists(powershell),
            "The engine-string fixture requires the inbox Windows PowerShell executable.");
        string selection = abstain
            ? "$response.abstained = $true; $response.margin = 0.01; " +
              "$response.score = 0.3; $response.confidence = 0.3"
            : "$response.index = [int]($request.candidateGroupIds[0]); " +
              "$response.margin = 0.5; $response.score = 0.9; $response.confidence = 0.9";
        ProcessStartInfo startInfo = new(powershell);
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "while (($line = [Console]::In.ReadLine()) -ne $null) { " +
            "$request = $line | ConvertFrom-Json; " +
            "$response = [ordered]@{ " +
            "version = '" + RankerProtocolV2.Version + "'; " +
            "requestId = [string]$request.requestId; ok = $true; " +
            "index = $null; abstained = $false; margin = $null; " +
            "score = $null; confidence = $null; " +
            "provider = '" + traits.ExecutionProvider + "'; " +
            "device = '" + traits.Device + "'; " +
            "modelVersion = '" + model + "'; pipeline = 'cross'; " +
            "errorCode = $null; error = $null }; " +
            "if ($request.command -eq 'select') { " + selection + " }; " +
            "[Console]::Out.WriteLine(($response | ConvertTo-Json -Compress)); " +
            "[Console]::Out.Flush(); " +
            "if ($request.command -eq 'shutdown') { break } }");
#else
        // The same contract in python3, already a prerequisite of the macOS
        // subset; -I keeps the fake isolated from the working directory and
        // user site-packages.
        const string workerScript = """
            import json, sys
            model, version, provider, device, mode = sys.argv[1:6]
            for line in sys.stdin:
                request = json.loads(line)
                response = {"version": version, "requestId": str(request.get("requestId")), "ok": True,
                            "index": None, "abstained": False, "margin": None, "score": None,
                            "confidence": None, "provider": provider, "device": device,
                            "modelVersion": model, "pipeline": "cross", "errorCode": None, "error": None}
                if request.get("command") == "select":
                    if mode == "abstain":
                        response.update(abstained=True, margin=0.01, score=0.3, confidence=0.3)
                    else:
                        response.update(index=request["candidateGroupIds"][0],
                                        margin=0.5, score=0.9, confidence=0.9)
                sys.stdout.write(json.dumps(response, separators=(",", ":")) + "\n")
                sys.stdout.flush()
                if request.get("command") == "shutdown":
                    break
            """;
        ProcessStartInfo startInfo = new("python3");
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(workerScript);
        startInfo.ArgumentList.Add(model);
        startInfo.ArgumentList.Add(RankerProtocolV2.Version);
        startInfo.ArgumentList.Add(traits.ExecutionProvider);
        startInfo.ArgumentList.Add(traits.Device);
        startInfo.ArgumentList.Add(abstain ? "abstain" : "select");
#endif
        return startInfo;
    }

    private static void ExpectEngine(string actual, string expected, string scenario)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{scenario}: the engine string is '{actual}', expected '{expected}'.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
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
            // Not cacheable, so the line comes straight from the generation
            // and never from the phrase cache.
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
