using System.Drawing;
using System.IO.Compression;
using System.Text;
using AsteriaScale.Models;
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
        await VerifyWholeSentenceAndHiddenHistoryAsync(Path.Combine(root, "whole-sentence"));
        await VerifyContinuedEllipsisStaysWholeAsync(Path.Combine(root, "continued-ellipsis"));
        await VerifyDictionaryMissStillGetsPhraseAsync(Path.Combine(root, "miss"));
        await VerifyIdiomaticTranslationIsIndependentAsync(Path.Combine(root, "idiom"));
        await VerifyAbstentionStillGetsPhraseAsync(Path.Combine(root, "abstention"));
        await VerifyFailureStaysOnPrimaryAsync(Path.Combine(root, "fail-open"));
        await VerifyCancellationPropagatesAsync(Path.Combine(root, "cancellation"));
        await VerifyDisabledModeSkipsTranslatorAsync(Path.Combine(root, "disabled"));
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
            Confidence: null,
            UsedFullFallback: false);

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
            Confidence: null,
            UsedFullFallback: false);

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
        Check(fixture.FullFallbackCalls == 0,
            "A phrase setup failure escalated into full-text fallback.");
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
            new AsteriaScale.Interop.NativeMethods.Point { X = 0, Y = 0 },
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
        Check(translator.TranslateCalls == 1 && fixture.FullFallbackCalls == 0,
            "A dictionary miss used the wrong neural fallback path.");
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
            Confidence: 0.97,
            UsedFullFallback: false);

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
            Confidence: null,
            UsedFullFallback: false);

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
            Check(fixture.FullFallbackCalls == 0 &&
                  await fixture.PhraseCache.CountAsync() == 0,
                "A failed phrase generation launched fallback or poisoned its cache.");
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
        Check(fixture.FullFallbackCalls == 0,
            "Phrase cancellation launched full-text fallback.");
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
            Confidence: 0.91,
            UsedFullFallback: false);

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
        public int PrepareCalls { get; private set; }
        public int TranslateCalls { get; private set; }
        public List<string> Events { get; } = [];
        public SmartPhraseTranslationRequest? LastRequest { get; private set; }

        public Task PrepareAsync(CancellationToken cancellationToken)
        {
            PrepareCalls++;
            Events.Add("prepare");
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
                    ElapsedMilliseconds: 11))
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
            ISmartPhraseTranslator translator)
        {
            string bundled = Path.Combine(root, "bundled");
            string user = Path.Combine(root, "user");
            Directory.CreateDirectory(bundled);
            Directory.CreateDirectory(user);
            CreateMinimalDictionary(Path.Combine(bundled, "eng-rus.tsv.gz"));
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
            Pipeline = new SmartHoverPipeline(
                new AppSettings
                {
                    SmartDictionaryBackend = SmartDictionaryBackends.Cpu,
                    SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.English,
                    SmartDictionaryFallbackMode = SmartDictionaryFallbackModes.Never,
                    SmartPhraseTranslationMode = phraseMode
                },
                dictionary,
                _selector,
                _resultCache,
                _decisionCache,
                (_, _) =>
                {
                    FullFallbackCalls++;
                    return Task.FromResult((
                        Source: Context,
                        Translation: "полнотекстовый fallback",
                        Engine: "fixture fallback"));
                },
                static _ => { },
                static (_, _) => { },
                translator,
                PhraseCache);
        }

        public SmartHoverPipeline Pipeline { get; }
        public SmartPhraseTranslationCache PhraseCache { get; }
        public int FullFallbackCalls { get; private set; }

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
            string row = "placeholder\tзаполнитель")
        {
            using FileStream file = File.Create(path);
            using GZipStream gzip = new(file, CompressionLevel.SmallestSize);
            using StreamWriter writer = new(gzip, new UTF8Encoding(false));
            writer.WriteLine(row);
        }
    }
}
