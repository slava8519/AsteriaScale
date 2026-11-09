using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Text;
using AsteriaScale.Models;
using AsteriaScale.RankerProtocol;
using AsteriaScale.Services;

/// <summary>
/// Characterizes the hover orchestration that moved from the Windows
/// HoverTranslationService into the core (owner decision M12) by running the
/// moved code against a fake host: card order and fields, hiding,
/// cancellation, history of what was shown, lifecycle calls into the host,
/// speaker saving, both disposal phases, the primary card before the result
/// cache store, and the loading-card engines and status texts.
///
/// Every engine string and status is a literal, and the file names no label
/// constant: the host-supplied labels that come later must keep these exact
/// Windows strings, and a check against a constant would follow the constant.
/// No real ranker worker or llama-server starts; where the order needs a
/// ranker verdict, a scripted fake answers the worker protocol.
/// </summary>
internal static class HoverCoordinatorTests
{
    private const string Sentence = "We walked along the river at dusk.";
    private const string Word = "river";
    // Two senses and no sentence translation, so only the ranker decides.
    private const string AmberContext = "Give me the amber.";
    private const string Amber = "amber";

    public static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "hover-coordinator");
        Directory.CreateDirectory(root);
        await VerifyDictionaryHoverOrderAsync(Path.Combine(root, "dictionary"));
        await VerifyMissAndMoveHideAsync(Path.Combine(root, "miss"));
        await VerifyFailuresNeverEscapeAsync(Path.Combine(root, "failures"));
        await VerifySmartPhraseOrderAndHistoryAsync(Path.Combine(root, "smart"));
        await VerifyPhraseFailureKeepsPrimaryAsync(Path.Combine(root, "phrase-failure"));
        await VerifyLifecycleAsync(Path.Combine(root, "lifecycle"));
        await VerifySpeakerAsync(Path.Combine(root, "speaker"));
        await VerifyDisposeDrainsAsync(Path.Combine(root, "dispose"));
        await VerifyDisposeOrderAsync(Path.Combine(root, "dispose-order"));
        await VerifyPrimaryBeforeResultCacheStoreAsync(Path.Combine(root, "cache-store"));
        await VerifyLoadingEnginesAsync(Path.Combine(root, "loading"));
        await VerifyWarmUpStatusesAsync(Path.Combine(root, "warm-up"));
    }

    private static async Task VerifyDictionaryHoverOrderAsync(string root)
    {
        await using Fixture fixture = new(root, HoverTranslationModes.Dictionary);
        Check(fixture.Coordinator.TryBeginProcessing(), "The first hover did not acquire the gate.");
        Check(!fixture.Coordinator.TryBeginProcessing(), "A second hover started while one was in flight.");
        await fixture.Coordinator.StartHover("a1", CancellationToken.None);

        Check(fixture.Host.Events.SequenceEqual(
                  ["recognize:a1:word", "upgrade:a1", "recognized:a1", "loading", "card"]),
            "Dictionary hover order changed: " + string.Join(", ", fixture.Host.Events));
        HoverLoadingCard<string> loading = fixture.Host.Loading.Single();
        HoverCard<string> card = fixture.Host.Cards.Single();
        Check(loading.Source == Sentence &&
              loading.Engine == "NPU OCR  •  СЛОВАРЬ" &&
              card.Engine == "NPU OCR  •  СЛОВАРЬ" &&
              card.Translation == "река" &&
              card.Context == Sentence &&
              card.RequireAnchorCurrent &&
              card.PhraseLine is null &&
              card.DisplayGeneration == loading.DisplayGeneration &&
              fixture.Coordinator.IsCurrentDisplay(card.DisplayGeneration),
            "The dictionary card did not carry the WPF host's fields.");
        AssertGateFree(fixture, "A finished hover kept the gate.");
        fixture.Coordinator.HideOverlay();
        Check(!fixture.Coordinator.IsCurrentDisplay(card.DisplayGeneration),
            "Hiding did not retire the posted card's generation.");

        fixture.Coordinator.RecordShown(card);
        fixture.Coordinator.RecordShown(card with { DictionaryFound = false });
        IReadOnlyList<TranslationHistoryEntry> history = fixture.Coordinator.History.Snapshot();
        Check(history is [{ Translation: "река", Context: Sentence, Engine: "NPU OCR • СЛОВАРЬ" }],
            "History did not record exactly the shown dictionary card.");
    }

    private static async Task VerifyMissAndMoveHideAsync(string root)
    {
        await using Fixture fixture = new(root, HoverTranslationModes.Dictionary);
        fixture.Host.Recognized = Recognized("Xyzzy plugh frobnicate.", "plugh");
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        await fixture.Coordinator.StartHover("miss", CancellationToken.None);
        // Dictionary mode shows a miss rather than hiding; only an empty or
        // echoed translation hides the card.
        Check(fixture.Host.Cards is [{ DictionaryFound: false, Translation: "Нет записи в локальном словаре" }] &&
              !fixture.Host.Events.Contains("hide"),
            "A dictionary miss was not shown as a not-found card: " +
            string.Join(", ", fixture.Host.Events));
        fixture.Coordinator.RecordShown(fixture.Host.Cards[0]);
        Check(fixture.Coordinator.History.Snapshot().Count == 0,
            "A not-found card reached the translation history.");

        fixture.Host.Clear();
        fixture.Host.Recognized = Recognized(Sentence, Word);
        fixture.Host.AnchorCurrent = false;
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        await fixture.Coordinator.StartHover("moved", CancellationToken.None);
        Check(fixture.Host.Events.SequenceEqual(["recognize:moved:word", "hide"]),
            "A hover whose cursor moved away did not hide: " + string.Join(", ", fixture.Host.Events));

        fixture.Host.Clear();
        fixture.Host.Recognized = null;
        fixture.Host.AnchorCurrent = true;
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        await fixture.Coordinator.StartHover("empty", CancellationToken.None);
        Check(fixture.Host.Events.SequenceEqual(["recognize:empty:word", "hide"]),
            "An empty recognition did not hide.");
    }

    private static async Task VerifyFailuresNeverEscapeAsync(string root)
    {
        await using Fixture fixture = new(root, HoverTranslationModes.Dictionary);
        fixture.Host.RecognizeFailure = new InvalidOperationException("ocr fixture failure");
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        await fixture.Coordinator.StartHover("fail", CancellationToken.None);
        Check(fixture.Host.Events[^1] == "hide" &&
              fixture.Statuses[^1] == "Перевод под курсором: ocr fixture failure",
            "A recognizer failure escaped or lost its status.");
        AssertGateFree(fixture, "A failed hover kept the gate.");
        fixture.Coordinator.HideOverlay();
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        await fixture.Coordinator.StartHover("fail", CancellationToken.None);
        Check(fixture.Statuses.Count(status => status.StartsWith("Перевод под курсором", StringComparison.Ordinal)) == 1,
            "A repeated status was reported twice.");

        fixture.Host.Clear();
        fixture.Host.RecognizeFailure = null;
        fixture.Host.BlockRecognition = true;
        int statusCount = fixture.Statuses.Count;
        using CancellationTokenSource moved = new();
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        // Kept at the type StartHover returns: declaring a Task<T> as Task makes
        // CsWinRT's AOT generator add a vtable entry to the Windows test build.
        Task<HoverOutcome> hover = fixture.Coordinator.StartHover("cancel", moved.Token);
        await fixture.Host.RecognitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        moved.Cancel();
        await hover.WaitAsync(TimeSpan.FromSeconds(5));
        Check(fixture.Host.Events[^1] == "hide" && fixture.Statuses.Count == statusCount,
            "A cancelled hover did not hide quietly.");
        AssertGateFree(fixture, "A cancelled hover kept the gate.");
    }

    private static async Task VerifySmartPhraseOrderAndHistoryAsync(string root)
    {
        FakePhraseModel phrase = new() { Translation = "Мы шли вдоль реки в сумерках." };
        await using Fixture fixture = new(
            root,
            HoverTranslationModes.Smart,
            phrase,
            keepWarm: false);
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        await fixture.Coordinator.StartHover("s1", CancellationToken.None);

        Check(fixture.Host.Events.SequenceEqual(
                  ["recognize:s1:sentence", "upgrade:s1", "recognized:s1", "loading", "card", "card"]),
            "Smart hover order changed: " + string.Join(", ", fixture.Host.Events));
        HoverLoadingCard<string> loading = fixture.Host.Loading.Single();
        HoverCard<string> primary = fixture.Host.Cards[0];
        HoverCard<string> final = fixture.Host.Cards[1];
        Check(loading.Source == Word &&
              loading.Engine == "NPU OCR  •  CPU-СМЫСЛ",
            "The smart loading card did not show the hovered word and ranker label.");
        Check(primary.PhraseLine is null &&
              final.PhraseLine is { Status: SmartPhraseLineStatus.Translated, IsProvisional: false } &&
              final.PhraseLine.Translation == phrase.Translation &&
              final.Translation == primary.Translation,
            "The phrase line did not follow the primary card on the same card.");

        fixture.Coordinator.RecordShown(primary);
        fixture.Coordinator.RecordShown(final with
        {
            PhraseLine = final.PhraseLine! with { IsProvisional = true }
        });
        fixture.Coordinator.RecordShown(final);
        IReadOnlyList<TranslationHistoryEntry> history = fixture.Coordinator.History.Snapshot();
        Check(history.Count == 2 &&
              history[0].Translation == phrase.Translation &&
              history[0].Language == "EN" &&
              history[1].Translation == primary.Translation,
            "History did not hold the primary once and only the final phrase line.");

        await phrase.Unloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(phrase.UnloadCalls >= 1,
            "Memory-saving mode did not unload the phrase model after a smart hover.");
    }

    private static async Task VerifyPhraseFailureKeepsPrimaryAsync(string root)
    {
        FakePhraseModel phrase = new() { Failure = new InvalidOperationException("llama fixture failure") };
        await using Fixture fixture = new(root, HoverTranslationModes.Smart, phrase, keepWarm: true);
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        await fixture.Coordinator.StartHover("f1", CancellationToken.None);
        Check(!fixture.Host.Events.Contains("hide") &&
              fixture.Host.Cards is [{ PhraseLine: null }, { PhraseLine.Status: SmartPhraseLineStatus.Unavailable }] &&
              fixture.Statuses.Contains(
                  "TranslateGemma недоступна — словарный результат сохранён без перевода фразы"),
            "A phrase failure removed the primary card or lost its status.");
        Check(phrase.UnloadCalls == 0, "Keep-warm mode unloaded the phrase model after a hover.");
    }

    private static async Task VerifyLifecycleAsync(string root)
    {
        int releases = 0;
        await using Fixture fixture = new(
            root,
            HoverTranslationModes.Dictionary,
            managedMemoryRelease: () => Interlocked.Increment(ref releases));
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        await fixture.Coordinator.StartHover("l1", CancellationToken.None);

        fixture.Host.Clear();
        fixture.Coordinator.SetMode(HoverTranslationModes.Dictionary);
        Check(fixture.Host.Events.SequenceEqual(["start-tracking", "ensure-ready"]),
            "Re-selecting the active mode did more than re-arm tracking and warm up: " +
            string.Join(", ", fixture.Host.Events));

        fixture.Host.Clear();
        fixture.Coordinator.SetEnabled(false);
        Check(fixture.Host.Events.Take(4).SequenceEqual(
                  ["reset-tracking", "stop-tracking", "hide", "close-aux"]) &&
              fixture.Statuses[^1] == "Перевод текста выключен" &&
              !fixture.Settings.HoverTranslationEnabled,
            "Disabling did not stop, hide and report in the host's old order: " +
            string.Join(", ", fixture.Host.Events));
        await fixture.WaitUntilAsync(() => Volatile.Read(ref releases) == 1);

        fixture.Host.Clear();
        fixture.Coordinator.SetMode(HoverTranslationModes.Smart);
        Check(fixture.Settings.HoverTranslationMode == HoverTranslationModes.Smart &&
              fixture.Host.Events.SequenceEqual(
                  ["reset-tracking", "hide", "close-aux", "stop-tracking"]),
            "A mode change while disabled did not reset, hide and keep tracking stopped: " +
            string.Join(", ", fixture.Host.Events));
    }

    private static void AssertGateFree(Fixture fixture, string message)
    {
        Check(fixture.Coordinator.TryBeginProcessing(), message);
        fixture.Coordinator.EndProcessing();
    }

    private static async Task VerifySpeakerAsync(string root)
    {
        int saves = 0;
        await using Fixture fixture = new(
            root,
            HoverTranslationModes.Dictionary,
            save: () =>
            {
                Interlocked.Increment(ref saves);
                return Task.CompletedTask;
            });
        fixture.Host.SpeakerEditorSupported = true;
        fixture.Host.Game = new SpeakerGameIdentity("process:rewrite", "Rewrite");
        fixture.Host.Recognized = Recognized(Sentence, Word) with
        {
            CurrentSpeaker = new CurrentSpeakerContext("Kotori")
        };
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        await fixture.Coordinator.StartHover("sp", CancellationToken.None);
        HoverSpeakerTarget<string>? target = fixture.Coordinator.VisibleSpeakerTarget;
        Check(target is { Anchor: "sp", Speaker.Name: "Kotori" } &&
              fixture.Host.Loading.Single().SpeakerEditorAvailable &&
              fixture.Host.Cards.Single().SpeakerEditorAvailable,
            "A recognized speaker did not become the editable target.");

        HoverSpeakerTarget<string> saved = fixture.Coordinator.SaveSpeakerGender(
            target!,
            SpeakerGender.Feminine,
            "Котори");
        await fixture.WaitUntilAsync(() => Volatile.Read(ref saves) == 1);
        Check(saved.Speaker.Gender == SpeakerGender.Feminine &&
              fixture.Statuses[^1] == "Котори: сохранён женский род для Rewrite",
            "Saving the speaker gender did not resolve, persist and report.");

        fixture.Coordinator.HideOverlay();
        Check(fixture.Coordinator.VisibleSpeakerTarget is null,
            "Hiding the card kept the speaker target.");
    }

    private static async Task VerifyDisposeDrainsAsync(string root)
    {
        FakePhraseModel phrase = new();
        Fixture fixture = new(root, HoverTranslationModes.Dictionary, phrase);
        fixture.Host.BlockRecognition = true;
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        Task<HoverOutcome> hover = fixture.Coordinator.StartHover("d1", CancellationToken.None);
        await fixture.Host.RecognitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        List<string> events = fixture.Host.Events;
        Check(hover.IsCompleted &&
              events.FindIndex(item => item.StartsWith("release-host:", StringComparison.Ordinal)) >
              events.LastIndexOf("hide") &&
              events.Count(item => item.StartsWith("release-host", StringComparison.Ordinal)) == 2 &&
              phrase.Disposed &&
              !fixture.Coordinator.TryBeginProcessing(),
            "Dispose did not drain the in-flight hover before releasing the host: " +
            string.Join(", ", events));
        await fixture.DisposeAsync();
        Check(fixture.Host.Events.Count(item => item.StartsWith("release-host", StringComparison.Ordinal)) == 2,
            "A second dispose released the host again.");
    }

    // Before the move the Windows host disposed its windows, hotkey, tracker
    // and OCR, then the history, then Textractor, then the model owners. The
    // history flushes its debounced write when disposed, so the file on disk
    // says on which side of each host phase that happened.
    private static async Task VerifyDisposeOrderAsync(string root)
    {
        FakePhraseModel phrase = new();
        Fixture fixture = new(
            root,
            HoverTranslationModes.Dictionary,
            phrase,
            historyFlushDelay: TimeSpan.FromHours(1));
        fixture.Host.Recognized = Recognized(Sentence, Word);
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        await fixture.Coordinator.StartHover("o1", CancellationToken.None);
        fixture.Coordinator.RecordShown(fixture.Host.Cards.Single());
        Check(!File.Exists(fixture.HistoryFile),
            "The history wrote before disposal; the order check would prove nothing.");
        fixture.Host.Clear();

        await fixture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        string[] expected =
        [
            "stop-tracking",
            "reset-tracking",
            "hide",
            "close-aux",
            "release-host:history-written=False:phrase-disposed=False",
            "release-host-late:history-written=True:phrase-disposed=False"
        ];
        Check(fixture.Host.Events.SequenceEqual(expected) && phrase.Disposed,
            "Disposal left the order host release → history → late host release → core: " +
            string.Join(", ", fixture.Host.Events));
    }

    // The primary card is posted before the selection is stored, so a slow or
    // failing cache write never delays the answer.
    private static async Task VerifyPrimaryBeforeResultCacheStoreAsync(string root)
    {
        await using Fixture fixture = new(
            root,
            HoverTranslationModes.Smart,
            fakeRankerWorker: true);
        List<int> countsAtPrimary = [];
        fixture.Host.BeforeTranslationRecorded = card =>
        {
            if (card.PhraseLine is null)
            {
                // Hover tasks run on the thread pool, so waiting here cannot
                // deadlock; the store is idle until this post returns.
                countsAtPrimary.Add(fixture.ResultCache.CountAsync().GetAwaiter().GetResult());
            }
        };
        fixture.Host.Recognized = Recognized(AmberContext, Amber);
        Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
        await fixture.Coordinator.StartHover("c1", CancellationToken.None);

        Check(fixture.Host.Cards is [{ Translation: "янтарь", Engine: "NPU OCR  •  CPU-СМЫСЛ", PhraseLine: null }],
            "The ranker scenario took another route: " +
            string.Join(" | ", fixture.Host.Cards.Select(card => $"{card.Engine}: {card.Translation}")) +
            " / " + string.Join(" | ", fixture.Statuses));
        int countAfterHover = await fixture.ResultCache.CountAsync();
        Check(countsAtPrimary is [0] && countAfterHover == 1,
            $"The primary card was not posted before the result-cache store: " +
            $"{string.Join(", ", countsAtPrimary)} entries at the primary card, {countAfterHover} after the hover.");
    }

    private static async Task VerifyLoadingEnginesAsync(string root)
    {
        (string Mode, string Backend, string Source, string Engine)[] cases =
        [
            (HoverTranslationModes.Dictionary, SmartDictionaryBackends.Cpu, Sentence, "NPU OCR  •  СЛОВАРЬ"),
            (HoverTranslationModes.Smart, SmartDictionaryBackends.Cpu, Word, "NPU OCR  •  CPU-СМЫСЛ"),
            (HoverTranslationModes.Smart, SmartDictionaryBackends.Npu, Word, "NPU OCR  •  NPU-СМЫСЛ"),
            (HoverTranslationModes.Smart, SmartDictionaryBackends.Auto, Word, "NPU OCR  •  CPU-СМЫСЛ")
        ];
        foreach ((string mode, string backend, string source, string engine) in cases)
        {
            await using Fixture fixture = new(
                Path.Combine(root, mode + "-" + backend),
                mode,
                backend: backend);
            Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
            await fixture.Coordinator.StartHover("e1", CancellationToken.None);
            HoverLoadingCard<string> loading = fixture.Host.Loading.Single();
            Check(loading.Engine == engine && loading.Source == source,
                $"The {mode}/{backend} loading card shows '{loading.Engine}' over '{loading.Source}', " +
                $"expected '{engine}' over '{source}'.");
        }
    }

    private static async Task VerifyWarmUpStatusesAsync(string root)
    {
        await ExpectWarmUpStatusesAsync(
            Path.Combine(root, "dictionary"),
            HoverTranslationModes.Dictionary,
            SmartDictionaryBackends.Cpu,
            keepWarm: true,
            phrase: null,
            fakeRankerWorker: false,
            sourceLanguage: SmartDictionarySourceLanguages.English,
            "Подготовка NPU OCR и локального словаря…",
            "Быстрый перевод готов: 4 записей из 2 словарей");
        await ExpectWarmUpStatusesAsync(
            Path.Combine(root, "keep-warm"),
            HoverTranslationModes.Smart,
            SmartDictionaryBackends.Cpu,
            keepWarm: true,
            phrase: null,
            fakeRankerWorker: true,
            sourceLanguage: SmartDictionarySourceLanguages.English,
            "Подготовка NPU OCR, словарей и контекстной модели на CPU…",
            "Умный словарь готов: 4 записей; модель значений прогрета на CPU");
        await ExpectWarmUpStatusesAsync(
            Path.Combine(root, "keep-warm-phrase"),
            HoverTranslationModes.Smart,
            SmartDictionaryBackends.Cpu,
            keepWarm: true,
            phrase: new FakePhraseModel(),
            fakeRankerWorker: true,
            sourceLanguage: SmartDictionarySourceLanguages.English,
            "Подготовка NPU OCR, словарей и контекстной модели на CPU…",
            "Умный словарь готов: 4 записей; модель значений прогрета на CPU; TranslateGemma 4B прогрета на CPU");
        await ExpectWarmUpStatusesAsync(
            Path.Combine(root, "keep-warm-phrase-failure"),
            HoverTranslationModes.Smart,
            SmartDictionaryBackends.Cpu,
            keepWarm: true,
            phrase: new FakePhraseModel { WarmUpFailure = new InvalidOperationException("llama fixture failure") },
            fakeRankerWorker: true,
            sourceLanguage: SmartDictionarySourceLanguages.English,
            "Подготовка NPU OCR, словарей и контекстной модели на CPU…",
            "Умный словарь готов: 4 записей; модель значений прогрета на CPU; TranslateGemma 4B (CPU) недоступна; словарный результат остаётся рабочим");
        await ExpectWarmUpStatusesAsync(
            Path.Combine(root, "on-demand-npu"),
            HoverTranslationModes.Smart,
            SmartDictionaryBackends.Npu,
            keepWarm: false,
            phrase: null,
            fakeRankerWorker: false,
            sourceLanguage: SmartDictionarySourceLanguages.English,
            "Подготовка NPU OCR, словарей и контекстной модели на NPU…",
            "Умный словарь готов: 4 записей; модель значений загрузится по требованию на NPU");
        await ExpectWarmUpStatusesAsync(
            Path.Combine(root, "on-demand-phrase"),
            HoverTranslationModes.Smart,
            SmartDictionaryBackends.Cpu,
            keepWarm: false,
            phrase: new FakePhraseModel(),
            fakeRankerWorker: false,
            sourceLanguage: SmartDictionarySourceLanguages.English,
            "Подготовка NPU OCR, словарей и контекстной модели на CPU…",
            "Умный словарь готов: 4 записей; модель значений загрузится по требованию на CPU; TranslateGemma 4B загрузится по требованию на CPU");
        await ExpectWarmUpStatusesAsync(
            Path.Combine(root, "japanese"),
            HoverTranslationModes.Smart,
            SmartDictionaryBackends.Cpu,
            keepWarm: true,
            phrase: null,
            fakeRankerWorker: false,
            sourceLanguage: SmartDictionarySourceLanguages.Japanese,
            "Подготовка NPU OCR, словарей и контекстной модели на CPU…",
            "Умный словарь готов: 4 записей; японское направление работает напрямую по словарям");
    }

    private static async Task ExpectWarmUpStatusesAsync(
        string root,
        string mode,
        string backend,
        bool keepWarm,
        FakePhraseModel? phrase,
        bool fakeRankerWorker,
        string sourceLanguage,
        string preparing,
        string ready)
    {
        await using Fixture fixture = new(
            root,
            mode,
            phrase,
            keepWarm,
            backend: backend,
            sourceLanguage: sourceLanguage,
            fakeRankerWorker: fakeRankerWorker);
        fixture.Coordinator.Start();
        try
        {
            await fixture.WaitUntilAsync(
                () => fixture.Statuses.Count >= 2,
                TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            // Fall through: the check below names what was reported.
        }

        Check(fixture.Statuses.SequenceEqual([preparing, ready]) &&
              fixture.Host.Events.SequenceEqual(["start-tracking", "ensure-ready"]),
            $"Warm-up in {Path.GetFileName(root)} reported " +
            string.Join(" | ", fixture.Statuses) +
            $" with host calls {string.Join(", ", fixture.Host.Events)}; expected {preparing} | {ready}.");
    }

    private static HoverOcrResult Recognized(string context, string target) =>
        new(
            context,
            target,
            context.IndexOf(target, StringComparison.OrdinalIgnoreCase),
            Rectangle.Empty);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly object _statusGate = new();
        private readonly List<string> _statuses = [];

        public Fixture(
            string root,
            string mode,
            FakePhraseModel? phrase = null,
            bool keepWarm = true,
            Action? managedMemoryRelease = null,
            Func<Task>? save = null,
            TimeSpan? historyFlushDelay = null,
            string backend = SmartDictionaryBackends.Cpu,
            string sourceLanguage = SmartDictionarySourceLanguages.English,
            bool fakeRankerWorker = false)
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
            Settings = new AppSettings
            {
                HoverTranslationEnabled = true,
                HoverTranslationMode = mode,
                SmartDictionaryBackend = backend,
                SmartDictionarySourceLanguage = sourceLanguage,
                SmartDictionaryKeepWarm = keepWarm,
                SmartPhraseTranslationMode = phrase is null
                    ? SmartPhraseTranslationModes.Off
                    : SmartPhraseTranslationModes.TranslateGemma,
                SmartPhraseTranslationBackend = SmartPhraseTranslationBackends.Cpu
            };
            HistoryFile = Path.Combine(root, "history.json");
            ResultCache = new SmartDictionaryResultCache(Path.Combine(root, "result-cache.json"));
            PhraseModel = phrase ?? new FakePhraseModel();
            HoverCoreServices services = new()
            {
                ResultCache = ResultCache,
                PhraseCache = new SmartPhraseTranslationCache(Path.Combine(root, "phrase-cache.json")),
                DecisionCache = new GlossBertDecisionCache(Path.Combine(root, "decision-cache.json")),
                History = new TranslationHistoryService(
                    HistoryFile,
                    flushDelay: historyFlushDelay ?? TimeSpan.FromMilliseconds(10)),
                PhraseModel = PhraseModel,
                // Without the fake, no worker exists under this directory: a
                // ranker request fails loudly and the pipeline degrades, as
                // on a clean clone.
                Selector = fakeRankerWorker
                    ? CreateSelectorWithFakeWorker(Path.Combine(root, "worker"))
                    : new GlossBertDictionarySelectorService(root, TimeSpan.FromMinutes(1)),
                Dictionary = new LocalDictionaryService(
                    bundled,
                    user,
                    managedMemoryRelease: managedMemoryRelease ?? (() => { }))
            };
            Host = new FakeHost(this);
            Coordinator = new HoverCoordinator<string>(Settings, save, Host, services);
            Coordinator.StatusChanged += (_, status) =>
            {
                lock (_statusGate)
                {
                    _statuses.Add(status);
                }
            };
        }

        public FakeHost Host { get; }

        public AppSettings Settings { get; }

        public string HistoryFile { get; }

        public SmartDictionaryResultCache ResultCache { get; }

        public FakePhraseModel PhraseModel { get; }

        public HoverCoordinator<string> Coordinator { get; }

        public List<string> Statuses
        {
            get
            {
                lock (_statusGate)
                {
                    return [.. _statuses];
                }
            }
        }

        public async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The coordinator did not reach the expected state.");
                }

                await Task.Delay(10);
            }
        }

        public ValueTask DisposeAsync() => Coordinator.DisposeAsync();

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
    }

    private sealed class FakeHost(Fixture fixture) : IHoverHost<string>
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];
        private readonly List<HoverLoadingCard<string>> _loading = [];
        private readonly List<HoverCard<string>> _cards = [];

        public HoverOcrResult? Recognized { get; set; } = HoverCoordinatorTests.Recognized(Sentence, Word);
        public Exception? RecognizeFailure { get; set; }
        public bool BlockRecognition { get; set; }
        public bool AnchorCurrent { get; set; } = true;
        public SpeakerGameIdentity? Game { get; set; }
        public bool SpeakerEditorSupported { get; set; }
        public Action<HoverCard<string>>? BeforeTranslationRecorded { get; set; }
        public TaskCompletionSource RecognitionStarted { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Events { get { lock (_gate) { return [.. _events]; } } }
        public List<HoverLoadingCard<string>> Loading { get { lock (_gate) { return [.. _loading]; } } }
        public List<HoverCard<string>> Cards { get { lock (_gate) { return [.. _cards]; } } }

        public void Clear()
        {
            lock (_gate)
            {
                _events.Clear();
                _loading.Clear();
                _cards.Clear();
                RecognitionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private void Add(string item)
        {
            lock (_gate)
            {
                _events.Add(item);
            }
        }

        public Task EnsureTextSourceReadyAsync()
        {
            Add("ensure-ready");
            return Task.CompletedTask;
        }

        public async Task<HoverOcrResult?> RecognizeAsync(
            string anchor,
            bool sentenceContext,
            CancellationToken cancellationToken)
        {
            Add($"recognize:{anchor}:{(sentenceContext ? "sentence" : "word")}");
            RecognitionStarted.TrySetResult();
            if (BlockRecognition)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            await Task.Yield();
            return RecognizeFailure is null ? Recognized : throw RecognizeFailure;
        }

        public HoverOcrResult UpgradeRecognized(string anchor, HoverOcrResult recognized)
        {
            Add("upgrade:" + anchor);
            return recognized;
        }

        public SpeakerGameIdentity? ResolveSpeakerGame(string anchor) => Game;

        public void OnRecognized(string anchor, HoverOcrResult recognized) => Add("recognized:" + anchor);

        public bool IsAnchorCurrent(string anchor) => AnchorCurrent;

        public void ShowLoading(HoverLoadingCard<string> card)
        {
            lock (_gate)
            {
                _events.Add("loading");
                _loading.Add(card);
            }
        }

        public void ShowTranslation(HoverCard<string> card)
        {
            BeforeTranslationRecorded?.Invoke(card);
            lock (_gate)
            {
                _events.Add("card");
                _cards.Add(card);
            }
        }

        public void HideCard() => Add("hide");

        public void CloseAuxiliaryWindows() => Add("close-aux");

        public void StartTracking() => Add("start-tracking");

        public void StopTracking() => Add("stop-tracking");

        public void ResetTracking() => Add("reset-tracking");

        public ValueTask ReleaseHostResourcesAsync()
        {
            Add("release-host:" + DisposalState());
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseHostResourcesAfterHistoryAsync()
        {
            Add("release-host-late:" + DisposalState());
            return ValueTask.CompletedTask;
        }

        private string DisposalState() =>
            $"history-written={File.Exists(fixture.HistoryFile)}:" +
            $"phrase-disposed={fixture.PhraseModel.Disposed}";
    }

    private sealed class FakePhraseModel : IHoverPhraseModel
    {
        private int _unloadCalls;
        private int _disposed;

        public string Translation { get; init; } = "Перевод фразы.";
        public Exception? Failure { get; init; }
        public Exception? WarmUpFailure { get; init; }
        public int UnloadCalls => Volatile.Read(ref _unloadCalls);
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public TaskCompletionSource Unloaded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SmartPhraseTranslationResult> TranslateAsync(
            SmartPhraseTranslationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure is null
                ? Task.FromResult(new SmartPhraseTranslationResult(
                    Translation,
                    request.ModelIdentity,
                    ElapsedMilliseconds: 1))
                : Task.FromException<SmartPhraseTranslationResult>(Failure);
        }

        public Task WarmUpAsync(CancellationToken cancellationToken) =>
            WarmUpFailure is null ? Task.CompletedTask : Task.FromException(WarmUpFailure);

        public Task UnloadAsync()
        {
            Interlocked.Increment(ref _unloadCalls);
            Unloaded.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private static GlossBertDictionarySelectorService CreateSelectorWithFakeWorker(string root)
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
            CreateFakeWorker);
    }

    // The JSONL ranker protocol answered by a script: hello, warm and shutdown
    // with the requested provider's execution provider, device and pinned
    // GlossBERT identity, and select with the first candidate group.
    private static ProcessStartInfo CreateFakeWorker(GlossBertRankerProvider provider)
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
            "The hover coordinator fixture requires the inbox Windows PowerShell executable.");
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
            "if ($request.command -eq 'select') { " +
            "$response.index = [int]($request.candidateGroupIds[0]); " +
            "$response.margin = 0.5; $response.score = 0.9; $response.confidence = 0.9 }; " +
            "[Console]::Out.WriteLine(($response | ConvertTo-Json -Compress)); " +
            "[Console]::Out.Flush(); " +
            "if ($request.command -eq 'shutdown') { break } }");
#else
        // The same contract in python3, already a prerequisite of the macOS
        // subset; -I keeps the fake isolated from the working directory and
        // user site-packages.
        const string workerScript = """
            import json, sys
            model, version, provider, device = sys.argv[1:5]
            for line in sys.stdin:
                request = json.loads(line)
                response = {"version": version, "requestId": str(request.get("requestId")), "ok": True,
                            "index": None, "abstained": False, "margin": None, "score": None,
                            "confidence": None, "provider": provider, "device": device,
                            "modelVersion": model, "pipeline": "cross", "errorCode": None, "error": None}
                if request.get("command") == "select":
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
#endif
        return startInfo;
    }
}
