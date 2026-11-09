using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Text;
using AsteriaScale.Models;
using AsteriaScale.RankerProtocol;
using AsteriaScale.Services;

/// <summary>
/// The additive coordinator API a host that answers requests (CoreHost on
/// macOS) needs and the Windows host does not use: the outcome of every
/// terminal branch of a hover, the model-status snapshot before and after load
/// and unload, a host-requested unload that waits for the hover in flight,
/// holds the maintenance lease and coalesces, and a host without a phrase
/// model.
///
/// Expectations are literals. No real ranker worker or llama-server starts:
/// where the snapshot needs a running worker, a scripted fake answers the
/// worker protocol (python3 on macOS, the inbox Windows PowerShell on Windows,
/// as in "Hover coordinator ordering and lifecycle").
/// </summary>
internal static class HoverCoordinatorHostApiTests
{
    private const string Sentence = "We walked along the river at dusk.";
    private const string Word = "river";
    // Two senses and no sentence translation, so only the ranker decides.
    private const string AmberContext = "Give me the amber.";
    private const string Amber = "amber";
    // Its only translation repeats the source, which a dictionary card never shows.
    private const string EchoContext = "They ate sushi.";
    private const string Echo = "sushi";

    public static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "hover-host-api");
        Directory.CreateDirectory(root);
        await VerifyOutcomesAsync(Path.Combine(root, "outcomes"));
        await VerifyModelStatusAsync(Path.Combine(root, "status"));
        await VerifyRankerErrorInStatusAsync(Path.Combine(root, "ranker-error"));
        await VerifyHostUnloadAsync(Path.Combine(root, "host-unload"));
        await VerifyNullPhraseModelAsync(Path.Combine(root, "no-phrase-model"));
    }

    // One case per terminal branch of the hover, each releasing the gate.
    private static async Task VerifyOutcomesAsync(string root)
    {
        await using (Fixture fixture = new(Path.Combine(root, "dictionary"), HoverTranslationModes.Dictionary))
        {
            await ExpectOutcomeAsync(fixture, "hit", HoverOutcome.Completed, ["loading", "card"]);
            Check(fixture.Host.Cards is [{ Translation: "река" }], "The dictionary hit showed another card.");

            // A miss is shown as a not-found card, so it completes.
            fixture.Host.Recognized = Recognized("Xyzzy plugh frobnicate.", "plugh");
            await ExpectOutcomeAsync(fixture, "miss", HoverOutcome.Completed, ["loading", "card"]);
            Check(fixture.Host.Cards is [{ DictionaryFound: false }], "The miss was not a not-found card.");

            fixture.Host.Recognized = Recognized(EchoContext, Echo);
            await ExpectOutcomeAsync(fixture, "echo", HoverOutcome.Empty, ["loading", "hide"]);

            fixture.Host.Recognized = null;
            await ExpectOutcomeAsync(fixture, "nothing recognized", HoverOutcome.Empty, ["hide"]);

            fixture.Host.Recognized = Recognized(Sentence, Word);
            fixture.Host.AnchorCurrent = [false];
            await ExpectOutcomeAsync(fixture, "moved before the result", HoverOutcome.AnchorLost, ["hide"]);

            fixture.Host.AnchorCurrent = [true, false];
            await ExpectOutcomeAsync(fixture, "moved after the result", HoverOutcome.AnchorLost, ["loading", "hide"]);

            fixture.Host.AnchorCurrent = [];
            fixture.Host.RecognizeFailure = new InvalidOperationException("ocr fixture failure");
            await ExpectOutcomeAsync(fixture, "recognizer failure", HoverOutcome.Failed, ["hide"]);
            Check(fixture.Statuses[^1] == "Перевод под курсором: ocr fixture failure",
                "A failed hover lost its status.");
            fixture.Host.RecognizeFailure = null;

            // Cancelled by the host's token while the recognizer waits on it.
            int statusCount = fixture.Statuses.Count;
            fixture.Host.Clear();
            fixture.Host.BlockRecognition = true;
            using CancellationTokenSource moved = new();
            Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
            Task<HoverOutcome> cancelled = fixture.Coordinator.StartHover("cancel", moved.Token);
            await fixture.Host.RecognitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            moved.Cancel();
            Check(await cancelled.WaitAsync(TimeSpan.FromSeconds(5)) == HoverOutcome.Cancelled &&
                  fixture.Host.Events[^1] == "hide" &&
                  fixture.Statuses.Count == statusCount,
                "A cancelled hover did not end as Cancelled, hidden and without a status.");
            AssertGateFree(fixture, "A cancelled hover kept the gate.");
            fixture.Host.BlockRecognition = false;

            // Switched off while a recognizer that ignores the token finishes:
            // the result is no longer current.
            fixture.Host.Clear();
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Host.RecognitionGate = release.Task;
            Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
            Task<HoverOutcome> disabled = fixture.Coordinator.StartHover("disabled", CancellationToken.None);
            await fixture.Host.RecognitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Coordinator.SetEnabled(false);
            release.SetResult();
            Check(await disabled.WaitAsync(TimeSpan.FromSeconds(5)) == HoverOutcome.AnchorLost,
                "A hover whose translation was switched off meanwhile did not end as AnchorLost.");
            // The disable queued an unload that takes the lease once the hover
            // ends; join it before checking the gate.
            await fixture.Coordinator.RequestUnload().WaitAsync(TimeSpan.FromSeconds(10));
            AssertGateFree(fixture, "A hover switched off meanwhile kept the gate.");
        }

        FakePhraseModel phrase = new() { Translation = "Мы шли вдоль реки в сумерках." };
        await using (Fixture fixture = new(Path.Combine(root, "smart"), HoverTranslationModes.Smart, phrase))
        {
            await ExpectOutcomeAsync(fixture, "smart with a phrase line", HoverOutcome.Completed, ["loading", "card", "card"]);
            Check(fixture.Host.Cards[^1].PhraseLine is { Status: SmartPhraseLineStatus.Translated },
                "The smart hover completed without its phrase line.");
        }
    }

    private static async Task ExpectOutcomeAsync(
        Fixture fixture,
        string name,
        HoverOutcome expected,
        string[] events)
    {
        fixture.Host.Clear();
        Check(fixture.Coordinator.TryBeginProcessing(), $"{name}: the gate was unavailable.");
        HoverOutcome outcome = await fixture.Coordinator
            .StartHover(name, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));
        List<string> seen = fixture.Host.Events.Where(item => !item.StartsWith("recognize:", StringComparison.Ordinal)).ToList();
        Check(outcome == expected && seen.SequenceEqual(events, StringComparer.Ordinal),
            $"{name}: outcome {outcome} with host calls {string.Join(", ", seen)}; " +
            $"expected {expected} with {string.Join(", ", events)}.");
        AssertGateFree(fixture, $"{name}: the hover kept the gate.");
    }

    // Before anything loads, after the keep-warm warm-up, and after disabling
    // plus a host unload: every field comes from the owners themselves.
    private static async Task VerifyModelStatusAsync(string root)
    {
        int releases = 0;
        FakePhraseModel phrase = new();
        await using Fixture fixture = new(
            root,
            HoverTranslationModes.Smart,
            phrase,
            fakeRankerWorker: true,
            managedMemoryRelease: () => Interlocked.Increment(ref releases));

        HoverModelStatus before = fixture.Coordinator.GetModelStatus();
        Check(before.Dictionary == new HoverDictionaryStatus(false, 0, 0, "unloaded", 0),
            $"The dictionary status before loading is {before.Dictionary}.");
        Check(before.Sense == new HoverSenseStatus(
                  InUse: true,
                  GlossBertRankerProvider.Cpu,
                  RankerCpuModelProfile.GlossBert,
                  KeepWarm: true,
                  Running: false,
                  ProcessId: null,
                  ActiveProvider: null,
                  ActiveModelVersion: null,
                  LastErrorCode: null),
            $"The sense status before loading is {before.Sense}.");
        Check(before.Phrase == new HoverPhraseStatus(true, true, null, null),
            $"The phrase status before loading is {before.Phrase}.");

        fixture.Coordinator.Start();
        await fixture.WaitUntilAsync(() => fixture.Statuses.Count >= 2, TimeSpan.FromSeconds(60));
        Check(fixture.Statuses[^1] ==
              "Умный словарь готов: 4 записей; модель значений прогрета на CPU; TranslateGemma 4B прогрета на CPU",
            "The warm-up did not finish: " + string.Join(" | ", fixture.Statuses));
        HoverModelStatus loaded = fixture.Coordinator.GetModelStatus();
        Check(loaded.Dictionary is { Loaded: true, Entries: 4, Sources: 2, SenseMetadataKeys: 1 } &&
              loaded.Dictionary.Version != "unloaded",
            $"The dictionary status after loading is {loaded.Dictionary}.");
        Check(loaded.Sense is
              {
                  InUse: true,
                  Running: true,
                  ProcessId: > 0,
                  ActiveProvider: GlossBertRankerProvider.Cpu,
                  LastErrorCode: null
              } &&
              loaded.Sense.ActiveModelVersion == FakeWorkerModel,
            $"The sense status after warm-up is {loaded.Sense}.");
        Check(loaded.Phrase == new HoverPhraseStatus(true, true, true, null),
            $"The phrase status after warm-up is {loaded.Phrase}.");
        int workerId = loaded.Sense.ProcessId!.Value;

        fixture.Coordinator.SetEnabled(false);
        await fixture.Coordinator.RequestUnload().WaitAsync(TimeSpan.FromSeconds(30));
        HoverModelStatus unloaded = fixture.Coordinator.GetModelStatus();
        Check(unloaded.Dictionary == new HoverDictionaryStatus(false, 0, 0, "unloaded", 0),
            $"The dictionary status after unloading is {unloaded.Dictionary}.");
        Check(unloaded.Sense == new HoverSenseStatus(
                  InUse: false,
                  GlossBertRankerProvider.Cpu,
                  RankerCpuModelProfile.GlossBert,
                  KeepWarm: true,
                  Running: false,
                  ProcessId: null,
                  ActiveProvider: null,
                  ActiveModelVersion: null,
                  LastErrorCode: null),
            $"The sense status after unloading is {unloaded.Sense}.");
        Check(phrase.UnloadCalls >= 1 && Volatile.Read(ref releases) == 1,
            "Disabling did not unload the phrase model and release the dictionary memory once.");
        Check(ProcessExited(workerId), $"The unloaded ranker worker {workerId} is still running.");
    }

    // DoD 20.2 item 9: without a usable worker the card is marked as the
    // unavailable ranker and the snapshot names the worker's error code.
    private static async Task VerifyRankerErrorInStatusAsync(string root)
    {
        await using Fixture fixture = new(root, HoverTranslationModes.Smart, keepWarm: false);
        fixture.Host.Recognized = Recognized(AmberContext, Amber);
        await ExpectOutcomeAsync(fixture, "no worker", HoverOutcome.Completed, ["loading", "card"]);
        HoverModelStatus status = fixture.Coordinator.GetModelStatus();
        Check(fixture.Host.Cards is [{ Provenance: { } provenance }] &&
              provenance == HoverProvenance.RankerUnavailable(GlossBertRankerProvider.Cpu) &&
              status.Sense is { Running: false, LastErrorCode: "worker_missing" },
            $"A hover without a worker gave {fixture.Host.Cards.SingleOrDefault()?.Provenance} " +
            $"and the sense status {status.Sense}.");
    }

    // The pass waits for the hover in flight and holds the maintenance lease
    // while it runs; requests made meanwhile share its task and add one pass.
    private static async Task VerifyHostUnloadAsync(string root)
    {
        HoverUnloadIntentState intent = new();
        intent.Request(HoverUnloadReason.HostRequested);
        Check(!intent.ConsumeReleaseRequest(),
            "A host-requested unload asked for the compacting dictionary GC on its own.");

        // Dictionary mode: every pass unloads the phrase model, so the fake
        // counts passes.
        FakePhraseModel phrase = new();
        Fixture fixture = new(root, HoverTranslationModes.Dictionary, phrase);
        await using (fixture)
        {
            Check(fixture.Coordinator.TryBeginProcessing(), "Gate unavailable.");
            Task first = fixture.Coordinator.RequestUnload();
            Task second = fixture.Coordinator.RequestUnload();
            Task third = fixture.Coordinator.RequestUnload();
            Check(ReferenceEquals(first, second) && ReferenceEquals(second, third),
                "Host unload requests made before the pass ran did not share one task.");
            await Task.Delay(200);
            Check(!first.IsCompleted && phrase.UnloadCalls == 0,
                "A host unload ran while a hover held the gate.");

            TaskCompletionSource unblock = new(TaskCreationOptions.RunContinuationsAsynchronously);
            phrase.UnloadGate = unblock.Task;
            fixture.Coordinator.EndProcessing();
            await fixture.WaitUntilAsync(() => phrase.UnloadCalls == 1);
            Check(!fixture.Coordinator.TryBeginProcessing() && !first.IsCompleted,
                "A hover could start while a host unload held the maintenance lease.");
            phrase.UnloadGate = null;
            unblock.SetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            Check(phrase.UnloadCalls == 2,
                $"Three host requests ran {phrase.UnloadCalls} passes; the two that arrived " +
                "after the first pass read its revision must share one more pass.");
            AssertGateFree(fixture, "A finished host unload kept the maintenance lease.");

            Task later = fixture.Coordinator.RequestUnload();
            Check(!ReferenceEquals(later, first), "A request after the passes joined a finished task.");
            await later.WaitAsync(TimeSpan.FromSeconds(10));
            Check(phrase.UnloadCalls == 3, "A request on an idle coordinator did not run its own pass.");
        }

        try
        {
            _ = fixture.Coordinator.RequestUnload();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        throw new InvalidOperationException("A disposed coordinator accepted a host unload.");
    }

    // macOS until 20.5: the settings may ask for TranslateGemma, but the host
    // has no phrase model. One explicit status, no phrase line, no failure.
    private static async Task VerifyNullPhraseModelAsync(string root)
    {
        await using Fixture fixture = new(
            root,
            HoverTranslationModes.Smart,
            phrase: null,
            phraseRequested: true,
            fakeRankerWorker: true,
            labels: HoverEngineLabels.AppleVision);
        Check(fixture.Settings.SmartPhraseTranslationMode == SmartPhraseTranslationModes.TranslateGemma,
            "The fixture does not ask for a phrase line.");
        fixture.Coordinator.Start();
        await fixture.WaitUntilAsync(() => fixture.Statuses.Count >= 2, TimeSpan.FromSeconds(60));
        Check(fixture.Statuses.SequenceEqual(
                  [
                      "Подготовка Apple Vision, словарей и контекстной модели на CPU…",
                      "Умный словарь готов: 4 записей; модель значений прогрета на CPU; " +
                      "TranslateGemma недоступна на этой платформе"
                  ],
                  StringComparer.Ordinal),
            "Without a phrase model the warm-up reported " + string.Join(" | ", fixture.Statuses));
        Check(fixture.Coordinator.GetModelStatus().Phrase ==
              new HoverPhraseStatus(false, true, null, "TranslateGemma недоступна на этой платформе"),
            $"The phrase status without a model is {fixture.Coordinator.GetModelStatus().Phrase}.");

        await ExpectOutcomeAsync(fixture, "no phrase model", HoverOutcome.Completed, ["loading", "card"]);
        Check(fixture.Host.Cards is [{ PhraseLine: null, Translation: "река" }] &&
              !fixture.Statuses.Any(status => status.StartsWith("TranslateGemma недоступна —", StringComparison.Ordinal)),
            "A host without a phrase model got a phrase line or a phrase failure.");

        // A disable unloads every owner; the missing one is skipped.
        fixture.Coordinator.SetEnabled(false);
        await fixture.Coordinator.RequestUnload().WaitAsync(TimeSpan.FromSeconds(30));
        Check(fixture.Coordinator.GetModelStatus() is { Dictionary.Loaded: false, Sense.Running: false },
            "Unloading without a phrase model left a model owner loaded.");
    }

    private static bool ProcessExited(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void AssertGateFree(Fixture fixture, string message)
    {
        Check(fixture.Coordinator.TryBeginProcessing(), message);
        fixture.Coordinator.EndProcessing();
    }

    private static HoverOcrResult Recognized(string context, string target) =>
        new(
            context,
            target,
            context.IndexOf(target, StringComparison.Ordinal),
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
            bool phraseRequested = false,
            bool fakeRankerWorker = false,
            HoverEngineLabels? labels = null,
            Action? managedMemoryRelease = null)
        {
            string bundled = Path.Combine(root, "bundled");
            string user = Path.Combine(root, "user");
            Directory.CreateDirectory(bundled);
            Directory.CreateDirectory(user);
            WriteGzip(
                Path.Combine(bundled, "eng-rus.tsv.gz"),
                ["amber\tянтарь; сигнал", "river\tрека", "sushi\tsushi"]);
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
                SmartDictionaryBackend = SmartDictionaryBackends.Cpu,
                SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.English,
                SmartDictionaryKeepWarm = keepWarm,
                SmartPhraseTranslationMode = phrase is null && !phraseRequested
                    ? SmartPhraseTranslationModes.Off
                    : SmartPhraseTranslationModes.TranslateGemma,
                SmartPhraseTranslationBackend = SmartPhraseTranslationBackends.Cpu
            };
            HoverCoreServices services = new()
            {
                ResultCache = new SmartDictionaryResultCache(Path.Combine(root, "result-cache.json")),
                PhraseCache = new SmartPhraseTranslationCache(Path.Combine(root, "phrase-cache.json")),
                DecisionCache = new GlossBertDecisionCache(Path.Combine(root, "decision-cache.json")),
                History = new TranslationHistoryService(
                    Path.Combine(root, "history.json"),
                    flushDelay: TimeSpan.FromMilliseconds(10)),
                PhraseModel = phrase,
                // Without the fake no worker exists under this directory, so a
                // ranker request fails with worker_missing, as on a clean clone.
                Selector = fakeRankerWorker
                    ? CreateSelectorWithFakeWorker(Path.Combine(root, "worker"))
                    : new GlossBertDictionarySelectorService(root, TimeSpan.FromMinutes(1)),
                Dictionary = new LocalDictionaryService(
                    bundled,
                    user,
                    managedMemoryRelease: managedMemoryRelease ?? (() => { }))
            };
            Host = new FakeHost();
            Coordinator = new HoverCoordinator<string>(Settings, null, Host, services, labels);
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

    // Records what the coordinator asks of it. IsAnchorCurrent answers from a
    // script, one value per call, then true.
    private sealed class FakeHost : IHoverHost<string>
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];
        private readonly List<HoverCard<string>> _cards = [];
        private readonly Queue<bool> _anchorCurrent = new();

        public HoverOcrResult? Recognized { get; set; } = HoverCoordinatorHostApiTests.Recognized(Sentence, Word);
        public Exception? RecognizeFailure { get; set; }
        public bool BlockRecognition { get; set; }
        // A recognizer that ignores the hover's token until this completes.
        public Task? RecognitionGate { get; set; }
        public TaskCompletionSource RecognitionStarted { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IEnumerable<bool> AnchorCurrent
        {
            set
            {
                lock (_gate)
                {
                    _anchorCurrent.Clear();
                    foreach (bool current in value)
                    {
                        _anchorCurrent.Enqueue(current);
                    }
                }
            }
        }

        public bool SpeakerEditorSupported => false;

        public List<string> Events { get { lock (_gate) { return [.. _events]; } } }
        public List<HoverCard<string>> Cards { get { lock (_gate) { return [.. _cards]; } } }

        public void Clear()
        {
            lock (_gate)
            {
                _events.Clear();
                _cards.Clear();
                RecognitionGate = null;
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

        public Task EnsureTextSourceReadyAsync() => Task.CompletedTask;

        public async Task<HoverOcrResult?> RecognizeAsync(
            string anchor,
            bool sentenceContext,
            CancellationToken cancellationToken)
        {
            Add("recognize:" + anchor);
            RecognitionStarted.TrySetResult();
            if (BlockRecognition)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (RecognitionGate is { } gate)
            {
                await gate;
            }

            await Task.Yield();
            return RecognizeFailure is null ? Recognized : throw RecognizeFailure;
        }

        public HoverOcrResult UpgradeRecognized(string anchor, HoverOcrResult recognized) => recognized;

        public SpeakerGameIdentity? ResolveSpeakerGame(string anchor) => null;

        public void OnRecognized(string anchor, HoverOcrResult recognized)
        {
        }

        public bool IsAnchorCurrent(string anchor)
        {
            lock (_gate)
            {
                return !_anchorCurrent.TryDequeue(out bool current) || current;
            }
        }

        public void ShowLoading(HoverLoadingCard<string> card) => Add("loading");

        public void ShowTranslation(HoverCard<string> card)
        {
            lock (_gate)
            {
                _events.Add("card");
                _cards.Add(card);
            }
        }

        public void HideCard() => Add("hide");

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

    private sealed class FakePhraseModel : IHoverPhraseModel
    {
        private int _unloadCalls;

        public string Translation { get; init; } = "Перевод фразы.";
        public int UnloadCalls => Volatile.Read(ref _unloadCalls);
        // While set, an unload waits for it: the pass is then visibly running.
        public Task? UnloadGate { get; set; }

        public Task<SmartPhraseTranslationResult> TranslateAsync(
            SmartPhraseTranslationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SmartPhraseTranslationResult(
                Translation,
                request.ModelIdentity,
                ElapsedMilliseconds: 1));
        }

        public Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task UnloadAsync()
        {
            Interlocked.Increment(ref _unloadCalls);
            if (UnloadGate is { } gate)
            {
                await gate;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // The identity the fake worker reports: the pinned GlossBERT CPU artifact.
    private const string FakeWorkerModel =
        "GlossBERT:0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a:fp32-dynamic:pipeline-cross";

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
    // GlossBERT identity, and select with the first candidate group. The same
    // script as the fake of "Hover coordinator ordering and lifecycle".
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
            "The hover host API fixture requires the inbox Windows PowerShell executable.");
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
        // python3 is already a prerequisite of the macOS subset; -I keeps the
        // fake isolated from the working directory and user site-packages.
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
