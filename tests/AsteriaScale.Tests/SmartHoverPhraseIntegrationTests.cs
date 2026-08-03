using System.Drawing;
using System.IO.Compression;
using System.Text;
using AsteriaScale.Models;
using AsteriaScale.RankerProtocol;
using AsteriaScale.Services;

internal static class SmartHoverPhraseIntegrationTests
{
    private const string Context = "We rested on the bank by the river.";
    private const string Target = "bank";
    private const string PhraseTranslation = "Мы отдыхали у реки.";

    public static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "smart-hover-phrase-integration");
        Directory.CreateDirectory(root);

        await VerifyGenerationAndCacheHitAsync(Path.Combine(root, "cache-hit"));
        await VerifyBackendCacheIsolationAsync(Path.Combine(root, "backend-cache"));
        await VerifyBackendSnapshotAsync(Path.Combine(root, "backend-snapshot"));
        await VerifyNonCacheableResultIsRetriedAsync(Path.Combine(root, "non-cacheable"));
        await VerifySentenceWideSenseHintsAsync(Path.Combine(root, "sentence-wide-wsd"));
        await VerifyIdiomRewriteRanksOriginalSourceAsync(Path.Combine(root, "idiom-wsd"));
        await VerifyWholeSentenceAndHiddenHistoryAsync(Path.Combine(root, "whole-sentence"));
        await VerifyContinuedEllipsisStaysWholeAsync(Path.Combine(root, "continued-ellipsis"));
        await VerifyDictionaryMissStillGetsPhraseAsync(Path.Combine(root, "miss"));
        await VerifyIdiomaticTranslationIsIndependentAsync(Path.Combine(root, "idiom"));
        await VerifyAbstentionStillGetsPhraseAsync(Path.Combine(root, "abstention"));
        await VerifyFailureStaysOnPrimaryAsync(Path.Combine(root, "fail-open"));
        await VerifyCancellationPropagatesAsync(Path.Combine(root, "cancellation"));
        await VerifyDisabledModeSkipsTranslatorAsync(Path.Combine(root, "disabled"));
    }

    private static async Task VerifyNonCacheableResultIsRetriedAsync(string root)
    {
        RecordingPhraseTranslator translator = new()
        {
            Cacheable = false
        };
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator);

        SmartHoverPhraseLine? first = await RunPhrasePhaseAsync(
            fixture,
            CreatePrimaryResult(),
            CancellationToken.None);
        SmartHoverPhraseLine? second = await RunPhrasePhaseAsync(
            fixture,
            CreatePrimaryResult(),
            CancellationToken.None);

        Check(first is { FromCache: false } &&
              second is { FromCache: false } &&
              translator.TranslateCalls == 2 &&
              await fixture.PhraseCache.CountAsync() == 0,
            "A gender-sensitive direct fallback was persisted as a completed post-edit result.");
    }

    private static async Task VerifyBackendSnapshotAsync(string root)
    {
        RecordingPhraseTranslator translator = new();
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator);
        translator.OnPrepare = () =>
            fixture.Settings.SmartPhraseTranslationBackend =
                SmartPhraseTranslationBackends.Vulkan;

        SmartHoverPhraseLine? line = await RunPhrasePhaseAsync(
            fixture,
            CreatePrimaryResult(),
            CancellationToken.None);

        Check(line is { FromCache: false } &&
              line.Engine.Contains("CPU", StringComparison.Ordinal) &&
              translator.LastPreparedRequest?.ModelIdentity ==
              TranslateGemmaTranslationService.ModelIdentity &&
              translator.LastRequest?.ModelIdentity ==
              TranslateGemmaTranslationService.ModelIdentity,
            "A settings change altered the identity or label of one in-flight CPU request.");
    }

    private static async Task VerifyBackendCacheIsolationAsync(string root)
    {
        RecordingPhraseTranslator translator = new();
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator);

        SmartHoverPhraseLine? cpu = await RunPhrasePhaseAsync(
            fixture,
            CreatePrimaryResult(),
            CancellationToken.None);
        Check(cpu is { FromCache: false } &&
              cpu.Engine.Contains("CPU", StringComparison.Ordinal) &&
              translator.LastRequest?.ModelIdentity ==
              TranslateGemmaTranslationService.ModelIdentity,
            "The CPU phrase result did not use the CPU-specific identity.");

        fixture.Settings.SmartPhraseTranslationBackend =
            SmartPhraseTranslationBackends.Vulkan;
        SmartHoverPhraseLine? vulkan = await RunPhrasePhaseAsync(
            fixture,
            CreatePrimaryResult(),
            CancellationToken.None);
        Check(vulkan is { FromCache: false } &&
              vulkan.Engine.Contains("VULKAN", StringComparison.Ordinal) &&
              translator.TranslateCalls == 2 &&
              translator.LastRequest?.ModelIdentity ==
              TranslateGemmaTranslationService.VulkanModelIdentity &&
              await fixture.PhraseCache.CountAsync() == 2,
            "Switching to Vulkan reused a CPU phrase-cache entry.");

        fixture.Settings.SmartPhraseTranslationBackend =
            SmartPhraseTranslationBackends.Cpu;
        SmartHoverPhraseLine? cachedCpu = await RunPhrasePhaseAsync(
            fixture,
            CreatePrimaryResult(),
            CancellationToken.None);
        Check(cachedCpu is { FromCache: true } && translator.TranslateCalls == 2,
            "Switching back to CPU did not recover the isolated CPU cache entry.");
    }

    private static async Task VerifySentenceWideSenseHintsAsync(string root)
    {
        const string context = "I compared amber, birch, and cedar.";
        const string target = "amber";
        List<TranslationScaffoldNeuralRequest> neuralRequests = [];
        RecordingPhraseTranslator translator = new()
        {
            Translation = "Я сравнил янтарь, берёзу и кедр."
        };
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator,
            CreateAcceptingPhraseRanker(neuralRequests));
        SmartHoverResult primary = new(
            target,
            "янтарь",
            GlossBertDictionarySelectorService.CpuEngineLabel,
            DictionaryFound: true,
            IsPhrase: false,
            Language: "EN",
            Confidence: 0.95)
        {
            SelectedSenseHints =
            [
                new ScaffoldSenseHint(
                    target,
                    "fossilized tree resin",
                    "янтарь",
                    ["сигнал"],
                    0.95)
            ]
        };

        SmartHoverPhraseLine? line = await fixture.Pipeline.TranslatePhraseLineAsync(
            Recognized(context, target),
            primary,
            CancellationToken.None);

        Check(line is { Status: SmartPhraseLineStatus.Translated } &&
              neuralRequests.Select(request => request.Target)
                  .SequenceEqual(["birch", "cedar"], StringComparer.Ordinal) &&
              neuralRequests.All(request =>
                  string.Equals(request.Sentence, context, StringComparison.Ordinal)) &&
              neuralRequests.All(request => request.Plan.Outcomes
                  .Select(outcome => outcome.GroupId)
                  .SequenceEqual(Enumerable.Range(0, request.Plan.Outcomes.Count))) &&
              translator.LastRequest?.SenseHints.Select(hint => hint.Source)
                  .SequenceEqual(["amber", "birch", "cedar"], StringComparer.Ordinal) == true &&
              translator.LastRequest.SenseHints[0].IsPrimary &&
              translator.LastRequest.SenseHints.Skip(1).All(hint => !hint.IsPrimary),
            "The phrase request did not preserve GroupIds or prioritize the hovered sense over sentence-wide evidence.");
    }

    private static async Task VerifyIdiomRewriteRanksOriginalSourceAsync(string root)
    {
        const string context = "You're trying to butter me up beside the birch.";
        const string target = "butter";
        List<TranslationScaffoldNeuralRequest> neuralRequests = [];
        RecordingPhraseTranslator translator = new()
        {
            Translation = "Ты пытаешься мне польстить рядом с берёзой."
        };
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator,
            CreateAcceptingPhraseRanker(neuralRequests));
        SmartHoverResult primary = new(
            target,
            "намаслить",
            LocalDictionaryService.EngineLabel,
            DictionaryFound: true,
            IsPhrase: false,
            Language: "EN",
            Confidence: null);

        SmartHoverPhraseLine? line = await fixture.Pipeline.TranslatePhraseLineAsync(
            Recognized(context, target),
            primary,
            CancellationToken.None);

        Check(line is { Status: SmartPhraseLineStatus.Translated } &&
              neuralRequests is [{ Sentence: context, Target: "birch" }] &&
              translator.LastRequest is
              {
                  SourcePhrase: context,
                  ModelInputPhrase:
                      "You're flattering me to get what you want beside the birch."
              } request &&
              request.SenseHints.Select(hint => hint.Source)
                  .SequenceEqual(["birch"], StringComparer.Ordinal),
            "An idiom rewrite was ranked as synthetic English instead of preserving original-source WSD.");
    }

    private static async Task VerifyContinuedEllipsisStaysWholeAsync(string root)
    {
        const string context =
            "Sounds professional... and kind of like 'Cry Ant.'";
        const string target = "professional";
        RecordingPhraseTranslator translator = new()
        {
            Translation = "Звучит профессионально и немного похоже на «Плачущего муравья»."
        };
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator);
        HoverOcrResult recognized = new(
            context,
            target,
            context.IndexOf(target, StringComparison.Ordinal),
            Rectangle.Empty,
            HoverTextSource.Ocr);
        SmartHoverResult primary = new(
            target,
            "профессиональный",
            LocalDictionaryService.EngineLabel,
            DictionaryFound: true,
            IsPhrase: false,
            Language: "EN",
            Confidence: null);

        SmartHoverPhraseLine? line = await fixture.Pipeline.TranslatePhraseLineAsync(
            recognized,
            primary,
            CancellationToken.None);

        Check(line is
              {
                  Source: context,
                  Status: SmartPhraseLineStatus.Translated
              } &&
              translator.LastRequest is
              {
                  SourcePhrase: context,
                  ModelInputPhrase: context
              },
            "The hover phrase phase sent only the text before a continued ellipsis.");
    }

    private static async Task VerifyWholeSentenceAndHiddenHistoryAsync(string root)
    {
        const string current =
            "A maiden who cannot act quickly in this situation will eventually end up making the same mistake again.";
        const string target = "end up";
        string[] previous =
        [
            "Kotori warned everyone to stay calm.",
            "The others ignored her advice."
        ];
        RecordingPhraseTranslator translator = new()
        {
            Translation =
                "Девушка, которая не сможет быстро действовать в этой ситуации, в конце концов снова совершит ту же ошибку."
        };
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator);
        HoverOcrResult recognized = new(
            current,
            target,
            current.IndexOf(target, StringComparison.Ordinal),
            Rectangle.Empty,
            HoverTextSource.TextHook)
        {
            PreviousContext = previous
        };
        SmartHoverResult primary = new(
            target,
            "заканчиваться",
            LocalDictionaryService.EngineLabel,
            DictionaryFound: true,
            IsPhrase: true,
            Language: "EN",
            Confidence: null);

        SmartHoverPhraseLine? line = await fixture.Pipeline.TranslatePhraseLineAsync(
            recognized,
            primary,
            CancellationToken.None);
        Check(line is
              {
                  Source: current,
                  Status: SmartPhraseLineStatus.Translated
              } &&
              translator.LastRequest is { SourcePhrase: current } request &&
              request.PreviousContext.SequenceEqual(previous),
            "A complete long sentence or its hidden Textractor history was truncated.");
        SmartHoverPhraseLine visible = line ??
            throw new InvalidOperationException("The whole-sentence line was unavailable.");
        Check(!visible.Source.Contains(previous[0], StringComparison.Ordinal) &&
              !visible.Translation.Contains(previous[0], StringComparison.Ordinal),
            "Hidden Textractor history leaked into the visible phrase line.");
    }

    private static async Task VerifyGenerationAndCacheHitAsync(string root)
    {
        RecordingPhraseTranslator translator = new();
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator);

        SmartHoverResult primary = CreatePrimaryResult();
        SmartHoverPhraseLine? generated = await RunPhrasePhaseAsync(
            fixture,
            primary,
            CancellationToken.None);

        Check(translator.PrepareCalls == 1 && translator.TranslateCalls == 1,
            "An enabled Smart hover did not invoke the independent phrase translator.");
        Check(translator.Events.SequenceEqual(["prepare", "translate"]),
            "TranslateGemma generation ran before artifact/provider preparation.");
        Check(generated is
        {
            Source: Context,
            Translation: PhraseTranslation,
            Status: SmartPhraseLineStatus.Translated,
            FromCache: false
        }, "A guarded translation was not returned as an independent phrase line.");
        Check(primary is
        {
            Source: Target,
            Translation: "берег",
            Engine: GlossBertDictionarySelectorService.CpuEngineLabel
        }, "Phrase generation mutated the primary dictionary result.");
        Check(translator.LastRequest is
              {
                  SourcePhrase: Context,
                  ModelInputPhrase: Context,
                  TransformIdentity: SmartPhraseIdiomRewriter.NoTransformIdentity
              } request &&
              request.ProperNames.Count == 0,
            "The independent phrase request lost its exact target-centred model input.");
        Check(await fixture.PhraseCache.CountAsync() == 1,
            "A guarded independent phrase translation was not cached.");

        translator.Events.Clear();
        SmartHoverPhraseLine? cached = await RunPhrasePhaseAsync(
            fixture,
            primary,
            CancellationToken.None);
        Check(translator.PrepareCalls == 2,
            "A phrase-cache hit skipped TranslateGemma preparation and identity validation.");
        Check(translator.TranslateCalls == 1 &&
              translator.Events.SequenceEqual(["prepare"]),
            "A phrase-cache hit generated the same phrase a second time.");
        Check(cached is
        {
            Translation: PhraseTranslation,
            Status: SmartPhraseLineStatus.Translated,
            FromCache: true
        }, "The guarded phrase-cache hit was not returned to hover.");

        translator.PrepareFailure = new InvalidOperationException("fixture prepare failure");
        SmartHoverPhraseLine? unavailable = await RunPhrasePhaseAsync(
            fixture,
            primary,
            CancellationToken.None);
        Check(unavailable is { Status: SmartPhraseLineStatus.Unavailable } &&
              translator.PrepareCalls == 3 &&
              translator.TranslateCalls == 1,
            "A phrase-cache hit was served before current runtime validation completed.");
    }

    private static async Task VerifyDictionaryMissStillGetsPhraseAsync(string root)
    {
        const string context =
            "Are you talking about these magnificent 88-wide hips?";
        const string target = "88-wide";
        RecordingPhraseTranslator translator = new()
        {
            Translation = "Вы говорите об этих великолепных бёдрах шириной 88 дюймов?"
        };
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator);
        HoverOcrResult recognized = Recognized(context, target);

        SmartHoverResult miss = await fixture.Pipeline.ResolveSmartHoverAsync(
            recognized,
            SmartDictionaryBackends.Cpu,
            CancellationToken.None);
        SmartHoverPhraseLine? phrase = await fixture.Pipeline.TranslatePhraseLineAsync(
            recognized,
            miss,
            CancellationToken.None);

        Check(!miss.DictionaryFound,
            "The dictionary-miss fixture unexpectedly found a primary entry.");
        Check(phrase is
        {
            Status: SmartPhraseLineStatus.Translated,
            Translation: "Вы говорите об этих великолепных бёдрах шириной 88 дюймов?"
        }, "A dictionary miss bypassed TranslateGemma instead of producing the second line.");
        Check(translator.TranslateCalls == 1,
            "A dictionary miss did not use TranslateGemma for its second line.");
    }

    private static async Task VerifyIdiomaticTranslationIsIndependentAsync(string root)
    {
        const string context = "You're trying to butter me up.";
        const string target = "butter";
        RecordingPhraseTranslator translator = new()
        {
            Translation = "Ты пытаешься мне льстить."
        };
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator);
        SmartHoverResult literalPrimary = new(
            Source: target,
            Translation: "намаслить",
            Engine: SmartDictionaryHeuristicRanker.EngineLabel,
            DictionaryFound: true,
            IsPhrase: false,
            Language: "EN",
            Confidence: 0.97);

        SmartHoverPhraseLine? phrase = await fixture.Pipeline.TranslatePhraseLineAsync(
            Recognized(context, target),
            literalPrimary,
            CancellationToken.None);
        Check(literalPrimary.Translation == "намаслить" &&
              phrase is
              {
                  Translation: "Ты пытаешься мне льстить.",
                  Source: context,
                  Status: SmartPhraseLineStatus.Translated
              },
            "The idiomatic second line was forced to repeat the literal dictionary surface.");
        Check(translator.LastRequest is
              {
                  SourcePhrase: context,
                  ModelInputPhrase: "You're flattering me to get what you want.",
                  TransformIdentity: "idiom:butter-object-up@v4"
              },
            "The independent translator did not receive the deterministic idiom paraphrase.");
    }

    private static async Task VerifyAbstentionStillGetsPhraseAsync(string root)
    {
        const string context = "This is the part that always feels off.";
        const string target = "feels";
        RecordingPhraseTranslator translator = new()
        {
            Translation = "Вот эта часть всегда кажется какой-то неправильной."
        };
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator);
        SmartHoverResult abstained = new(
            Source: target,
            Translation: "щупать; чувствовать; сочувствовать",
            Engine: GlossBertDictionarySelectorService.CpuEngineLabel + "  •  НЕ УВЕРЕН",
            DictionaryFound: true,
            IsPhrase: false,
            Language: "EN",
            Confidence: null);

        SmartHoverPhraseLine? phrase = await fixture.Pipeline.TranslatePhraseLineAsync(
            Recognized(context, target),
            abstained,
            CancellationToken.None);
        Check(abstained.Translation.Contains(';') &&
              phrase is { Status: SmartPhraseLineStatus.Translated },
            "GlossBERT abstention prevented an independent contextual phrase line.");
        Check(translator.LastRequest is
              {
                  SourcePhrase: context,
                  ModelInputPhrase: context,
                  TransformIdentity: SmartPhraseIdiomRewriter.NoTransformIdentity
              },
            "A phrase that was already translated naturally was rewritten before TG4B.");
    }

    private static async Task VerifyFailureStaysOnPrimaryAsync(string root)
    {
        RecordingPhraseTranslator throwingTranslator = new()
        {
            TranslationFailure = new InvalidOperationException("fixture generation failure")
        };
        await using (PipelineFixture fixture = new(
                         Path.Combine(root, "exception"),
                         SmartPhraseTranslationModes.TranslateGemma,
                         throwingTranslator))
        {
            SmartHoverResult primary = CreatePrimaryResult();
            SmartHoverPhraseLine? result = await RunPhrasePhaseAsync(
                fixture,
                primary,
                CancellationToken.None);
            Check(result is { Status: SmartPhraseLineStatus.Unavailable } &&
                  primary.Translation == "берег",
                "A TranslateGemma exception discarded the primary dictionary result.");
            Check(await fixture.PhraseCache.CountAsync() == 0,
                "A failed phrase generation poisoned its cache.");
        }

        RecordingPhraseTranslator invalidTranslator = new()
        {
            Translation = "Фраза означает: мы отдыхали у реки."
        };
        await using (PipelineFixture fixture = new(
                         Path.Combine(root, "guard"),
                         SmartPhraseTranslationModes.TranslateGemma,
                         invalidTranslator))
        {
            SmartHoverResult primary = CreatePrimaryResult();
            SmartHoverPhraseLine? result = await RunPhrasePhaseAsync(
                fixture,
                primary,
                CancellationToken.None);
            Check(result is { Status: SmartPhraseLineStatus.Unavailable } &&
                  primary.Translation == "берег" &&
                  await fixture.PhraseCache.CountAsync() == 0,
                "A guard-rejected phrase changed primary output or entered the cache.");
        }
    }

    private static async Task VerifyCancellationPropagatesAsync(string root)
    {
        RecordingPhraseTranslator translator = new() { CancelDuringPrepare = true };
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.TranslateGemma,
            translator);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        try
        {
            _ = await RunPhrasePhaseAsync(
                fixture,
                CreatePrimaryResult(),
                cancellation.Token);
            throw new InvalidOperationException(
                "Phrase cancellation was swallowed as a display fail-open.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A stale hover must never append its second line to a newer card.
        }

        Check(translator.PrepareCalls == 0 && translator.TranslateCalls == 0,
            "Cancellation did not stop before scaffold or phrase generation.");
    }

    private static async Task VerifyDisabledModeSkipsTranslatorAsync(string root)
    {
        RecordingPhraseTranslator translator = new();
        await using PipelineFixture fixture = new(
            root,
            SmartPhraseTranslationModes.Off,
            translator);

        SmartHoverPhraseLine? result = await RunPhrasePhaseAsync(
            fixture,
            CreatePrimaryResult(),
            CancellationToken.None);
        Check(result is null,
            "Disabled phrase mode added a second line.");
        Check(translator.PrepareCalls == 0 && translator.TranslateCalls == 0 &&
              await fixture.PhraseCache.CountAsync() == 0,
            "Disabled phrase mode touched TranslateGemma or its cache.");
    }

    private static Task<SmartHoverPhraseLine?> RunPhrasePhaseAsync(
        PipelineFixture fixture,
        SmartHoverResult primary,
        CancellationToken cancellationToken) =>
        fixture.Pipeline.TranslatePhraseLineAsync(
            Recognized(Context, Target),
            primary,
            cancellationToken);

    private static HoverOcrResult Recognized(string context, string target) =>
        new(
            context,
            target,
            context.IndexOf(target, StringComparison.OrdinalIgnoreCase),
            Rectangle.Empty);

    private static SmartHoverResult CreatePrimaryResult() =>
        new(
            Source: Target,
            Translation: "берег",
            Engine: GlossBertDictionarySelectorService.CpuEngineLabel,
            DictionaryFound: true,
            IsPhrase: false,
            Language: "EN",
            Confidence: 0.91);

    private static TranslationScaffoldNeuralRanker CreateAcceptingPhraseRanker(
        List<TranslationScaffoldNeuralRequest> requests) =>
        (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            return Task.FromResult(new TranslationScaffoldNeuralResult(
                request.Plan.Outcomes[0].GroupId,
                Abstained: false,
                Margin: 0.5,
                Score: 0.8,
                Confidence: 0.8,
                GlossBertRankerProvider.Cpu,
                GlossBertDictionarySelectorService.CpuExecutionProvider,
                "CPU",
                "test-model",
                "test-independent",
                ElapsedMilliseconds: 1));
        };

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecordingPhraseTranslator : ISmartPhraseTranslator
    {
        public string Translation { get; set; } = PhraseTranslation;
        public Exception? PrepareFailure { get; set; }
        public Exception? TranslationFailure { get; set; }
        public bool CancelDuringPrepare { get; set; }
        public bool Cacheable { get; set; } = true;
        public Action? OnPrepare { get; set; }
        public int PrepareCalls { get; private set; }
        public int TranslateCalls { get; private set; }
        public List<string> Events { get; } = [];
        public SmartPhraseTranslationRequest? LastRequest { get; private set; }
        public SmartPhraseTranslationRequest? LastPreparedRequest { get; private set; }

        public Task PrepareAsync(
            SmartPhraseTranslationRequest request,
            CancellationToken cancellationToken)
        {
            PrepareCalls++;
            Events.Add("prepare");
            LastPreparedRequest = request;
            OnPrepare?.Invoke();
            if (CancelDuringPrepare)
            {
                return Task.FromCanceled(cancellationToken);
            }

            return PrepareFailure is null
                ? Task.CompletedTask
                : Task.FromException(PrepareFailure);
        }

        public Task<SmartPhraseTranslationResult> TranslateAsync(
            SmartPhraseTranslationRequest request,
            CancellationToken cancellationToken)
        {
            TranslateCalls++;
            Events.Add("translate");
            LastRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            return TranslationFailure is null
                ? Task.FromResult(new SmartPhraseTranslationResult(
                    Translation,
                    request.ModelIdentity,
                    ElapsedMilliseconds: 11,
                    Cacheable: Cacheable))
                : Task.FromException<SmartPhraseTranslationResult>(TranslationFailure);
        }
    }

    private sealed class PipelineFixture : IAsyncDisposable
    {
        private readonly GlossBertDictionarySelectorService _selector;
        private readonly SmartDictionaryResultCache _resultCache;
        private readonly GlossBertDecisionCache _decisionCache;

        public PipelineFixture(
            string root,
            string phraseMode,
            ISmartPhraseTranslator translator,
            TranslationScaffoldNeuralRanker? phraseNeuralRanker = null)
        {
            string bundled = Path.Combine(root, "bundled");
            string user = Path.Combine(root, "user");
            Directory.CreateDirectory(bundled);
            Directory.CreateDirectory(user);
            CreateGzipDictionary(
                Path.Combine(bundled, "eng-rus.tsv.gz"),
                phraseNeuralRanker is null
                    ? ["placeholder\tзаполнитель"]
                    :
                    [
                        "placeholder\tзаполнитель",
                        "amber\tянтарь; сигнал",
                        "birch\tберёза; розга",
                        "cedar\tкедр; шкаф"
                    ]);
            if (phraseNeuralRanker is not null)
            {
                CreateGzipDictionary(
                    Path.Combine(bundled, "eng-rus-senses.tsv.gz"),
                    [
                        "amber\tянтарь\tfossilized tree resin\tcommon\tnoun",
                        "amber\tсигнал\ta yellow warning signal\tcommon\tnoun",
                        "birch\tберёза\ta slender tree with pale bark\tcommon\tnoun",
                        "birch\tрозга\ta switch used for punishment\tcommon\tnoun",
                        "cedar\tкедр\tan evergreen coniferous tree\tcommon\tnoun",
                        "cedar\tшкаф\ta storage cabinet made of cedar\tcommon\tnoun"
                    ]);
            }
            CreateMinimalDictionary(
                Path.Combine(bundled, "jpn-rus.tsv.gz"),
                "仮\tвременный");

            LocalDictionaryService dictionary = new(bundled, user);
            _selector = new GlossBertDictionarySelectorService(
                root,
                TimeSpan.FromMinutes(1));
            _resultCache = new SmartDictionaryResultCache(
                Path.Combine(root, "smart-result-cache.json"));
            _decisionCache = new GlossBertDecisionCache(
                Path.Combine(root, "decision-cache.json"));
            PhraseCache = new SmartPhraseTranslationCache(
                Path.Combine(root, "phrase-cache.json"));
            Settings = new AppSettings
            {
                SmartDictionaryBackend = SmartDictionaryBackends.Cpu,
                SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.English,
                SmartPhraseTranslationMode = phraseMode,
                SmartPhraseTranslationBackend = SmartPhraseTranslationBackends.Cpu
            };
            Pipeline = new SmartHoverPipeline(
                Settings,
                dictionary,
                _selector,
                _resultCache,
                _decisionCache,
                static _ => { },
                translator,
                PhraseCache,
                phraseNeuralRanker);
        }

        public SmartHoverPipeline Pipeline { get; }
        public AppSettings Settings { get; }
        public SmartPhraseTranslationCache PhraseCache { get; }
        public async ValueTask DisposeAsync()
        {
            Pipeline.Dispose();
            await PhraseCache.DisposeAsync();
            await _decisionCache.DisposeAsync();
            await _resultCache.DisposeAsync();
            await _selector.DisposeAsync();
        }

        private static void CreateMinimalDictionary(
            string path,
            string row = "placeholder\tзаполнитель") =>
            CreateGzipDictionary(path, [row]);

        private static void CreateGzipDictionary(
            string path,
            IEnumerable<string> rows)
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
}
