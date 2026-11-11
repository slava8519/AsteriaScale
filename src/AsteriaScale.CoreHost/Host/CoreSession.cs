using System.Diagnostics;
using System.Runtime.Versioning;
using AsteriaScale.CoreHost.Protocol;
using AsteriaScale.Models;
using AsteriaScale.Services;

namespace AsteriaScale.CoreHost.Host;

/// <summary>Where CoreHost posts a status line (deduplicated per area by the server).</summary>
internal delegate void StatusSink(string area, string? state, string? code, string text);

/// <summary>
/// Core behind the protocol after core.init: one <see cref="HoverCoordinator{TAnchor}"/>
/// with the macOS labels (Apple Vision, docs/MACOS.md T4), no phrase model
/// before 20.5, every data path from the configured <see cref="CoreDataLayout"/>
/// and the ranker worker the arguments name. CoreHost is the coordinator's
/// host: the recognized text is the selection a request carries, or what
/// Core's OCR geometry selects in the Apple Vision observations it carries
/// (CoreSession.Observations.cs, with the frame cache), the request is the
/// anchor, and posted cards become hover.loading (observations only),
/// hover.primary and hover.phrase events. CoreHost is also the only writer
/// of settings.json: every change joins the settings queue when it is
/// admitted and is applied in that arrival order (hovers, capture plans,
/// warm-ups and unloads admitted later wait for it) and saved before its
/// terminal; a settings.update or speaker.set is saved first, from a copy,
/// and reaches Core only once the write succeeded.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class CoreSession : IHoverEngine, IHoverHost<CoreHostHover>
{
    internal const string FirstRunStatus = "Первый запуск на macOS: модель значений — ConSeC (CPU)";

    private static readonly HoverEngineLabels Labels = HoverEngineLabels.AppleVision;

    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly HoverCoreServices _services;
    private readonly UserDictionaryService _userDictionary;
    private readonly StatusSink _status;
    private readonly HoverCoordinator<CoreHostHover> _coordinator;

    private CoreSession(
        CoreDataLayout layout,
        SettingsService settingsService,
        AppSettings settings,
        HoverCoreServices services,
        StatusSink status)
    {
        _settingsService = settingsService;
        _settings = settings;
        _services = services;
        _status = status;
        _userDictionary = new UserDictionaryService(layout.UserDictionaryDirectory);
        _revision = 1;
        _settingsView = View(settings);
        // The coordinator constructor never calls back into this host, so
        // passing it before the constructor finishes is safe (as on Windows).
        _coordinator = new HoverCoordinator<CoreHostHover>(
            settings,
            PersistSettingsAsync,
            this,
            services,
            Labels);
        _coordinator.StatusChanged += (_, text) => _status("translation", null, null, text);
    }

    /// <summary>
    /// core.init: checks the data first (morphology, then the bundled
    /// dictionaries) so a broken bundle is refused before anything is
    /// written, then reads settings.json or writes the macOS first-run
    /// settings, loads the dictionaries once with a throwaway lookup, and
    /// starts the coordinator. Ranker warm-up, if the settings ask for it,
    /// continues in the background and reports through status events.
    /// </summary>
    public static async Task<(CoreSession Session, InitResult Result)> InitializeAsync(
        HostOptions options,
        StatusSink status)
    {
        CoreDataLayout layout = CoreDataLayout.Current;
        Stopwatch clock = Stopwatch.StartNew();
        int morphologyEntries = RussianMorphologyLexicon.EntryCount;
        if (morphologyEntries == 0)
        {
            // Core falls back to suffix rules without the lexicon; a host must
            // not hide that degradation behind a working-looking start.
            throw new RequestErrorException(Errors.MorphologyMissing(
                $"no morphology entries were read from {layout.MorphologyFile}"));
        }

        if (MissingBundledDictionaries(layout.BundledDictionaryDirectory) is { } missing)
        {
            throw new RequestErrorException(Errors.DictionaryMissing(missing));
        }

        SettingsService settingsService = new(layout.SettingsFile);
        bool firstRun = !File.Exists(settingsService.SettingsPath);
        AppSettings settings = await settingsService.LoadAsync().ConfigureAwait(false);
        if (firstRun)
        {
            // The macOS first-run choice (docs/MACOS.md T7), written explicitly
            // instead of changing the shared AppSettings defaults, which the
            // Windows build reads. Hover translation stays off, as on Windows.
            settings.SmartDictionaryRankerModel = SmartDictionaryRankerModels.Consec;
            settings.SmartDictionaryBackend = SmartDictionaryBackends.Cpu;
        }

        RankerWorkerLocation workerLocation = options.RankerWorker is { } worker && options.RankerModels is { } models
            ? new RankerWorkerLocation(worker, models)
            // Without the two arguments the selector looks beside CoreHost, the
            // Windows companion layout, which a macOS build never has: a smart
            // hover then reports worker_missing instead of ranking with
            // anything else (rule 3).
            : RankerWorkerLocation.ForCurrentProcess();
        HoverCoreServices services = new()
        {
            ResultCache = new SmartDictionaryResultCache(layout.SmartResultCacheFile),
            PhraseCache = new SmartPhraseTranslationCache(layout.PhraseCacheFile),
            DecisionCache = new GlossBertDecisionCache(layout.DecisionCacheFile),
            History = new TranslationHistoryService(layout.HistoryFile),
            // No phrase model before 20.5: the coordinator then never warms,
            // unloads or waits for one and says so in its ready status.
            PhraseModel = null,
            Selector = new GlossBertDictionarySelectorService(
                workerLocation,
                SemanticRankerProfileResolver.FromSettings(settings)),
            Dictionary = new LocalDictionaryService(
                layout.BundledDictionaryDirectory,
                layout.UserDictionaryDirectory),
        };

        double loadMs;
        double warmupMs;
        try
        {
            try
            {
                await services.Dictionary.LoadAsync().ConfigureAwait(false);
            }
            catch (FileNotFoundException exception)
            {
                throw new RequestErrorException(Errors.DictionaryMissing($"{exception.Message} {exception.FileName}"));
            }

            loadMs = clock.Elapsed.TotalMilliseconds;
            // One throwaway lookup pays Core's lazy initialisation here rather
            // than on the user's first hover. Nothing is cached or recorded.
            Stopwatch warm = Stopwatch.StartNew();
            _ = await services.Dictionary.LookupAsync("He sat on the bank of the river.", "bank", 14)
                .ConfigureAwait(false);
            warmupMs = warm.Elapsed.TotalMilliseconds;
            if (firstRun)
            {
                try
                {
                    await settingsService.SaveAsync(settings).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new RequestErrorException(Errors.SettingsSaveFailed(exception.Message));
                }
            }
        }
        catch
        {
            await DisposeServicesAsync(services).ConfigureAwait(false);
            throw;
        }

        CoreSession session = new(layout, settingsService, settings, services, status);
        LocalDictionaryService dictionary = services.Dictionary;
        status("dictionary", "ready", null,
            $"Словари загружены: {dictionary.EntryCount:N0} записей из {dictionary.SourceCount} словарей");
        if (firstRun)
        {
            status("settings", "ready", "first_run", FirstRunStatus);
        }

        InitResult result = new()
        {
            Settings = session.Settings.Settings,
            Revision = session.Settings.Revision,
            Dictionary = new InitDictionary
            {
                Entries = dictionary.EntryCount,
                Sources = dictionary.SourceCount,
                Version = dictionary.VersionToken,
            },
            MorphologyEntries = morphologyEntries,
            LoadMs = loadMs,
            WarmupLookupMs = warmupMs,
            FirstRun = firstRun
                ? new FirstRunSettings
                {
                    SenseModel = SmartDictionaryRankerModels.Consec,
                    SenseBackend = SmartDictionaryBackends.Cpu,
                    HoverEnabled = settings.HoverTranslationEnabled,
                }
                : null,
        };

        // The Windows start: an unload pass (everything while hover
        // translation is off), then tracking and warm-up when it is on.
        session._coordinator.Start();
        return (session, result);
    }

    // The bundled layers Core needs both of: without either, LoadAsync fails
    // or silently answers half the languages.
    private static string? MissingBundledDictionaries(string directory)
    {
        foreach (string prefix in new[] { "eng-rus", "jpn-rus" })
        {
            bool present = Directory.Exists(directory) &&
                           Directory.EnumerateFiles(directory, prefix + "*.tsv.gz", SearchOption.TopDirectoryOnly).Any();
            if (!present)
            {
                return $"no {prefix}*.tsv.gz dictionary in {directory}";
            }
        }

        return null;
    }

    private static async Task DisposeServicesAsync(HoverCoreServices services)
    {
        services.History.Dispose();
        await services.Selector.DisposeAsync().ConfigureAwait(false);
        await services.ResultCache.DisposeAsync().ConfigureAwait(false);
        await services.PhraseCache.DisposeAsync().ConfigureAwait(false);
        await services.DecisionCache.DisposeAsync().ConfigureAwait(false);
    }

    // ---- hover.resolve --------------------------------------------------

    public async Task<string> ResolveAsync(HoverJob job, IHoverEvents events, CancellationToken cancellationToken)
    {
        // Every settings change admitted before this hover joined the queue
        // at its admission, so waiting for the tail applies all of them first,
        // however late the pool ran their work.
        await AppliedSettingsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!_coordinator.Enabled)
        {
            throw new RequestErrorException(Errors.HoverDisabled());
        }

        string activeMode = HoverTranslationModes.NormalizeUserMode(_settings.HoverTranslationMode);
        if (job.Mode is { } mode && !string.Equals(mode, activeMode, StringComparison.Ordinal))
        {
            // The mode is a setting; translating in another one than the app
            // shows would be a silent switch.
            throw new RequestErrorException(Errors.BadRequest(
                $"hover.resolve asked for mode {mode}, the active mode is {activeMode}; change it with settings.update"));
        }

        // A hover with zero observations needs its cached frame, found here,
        // before Core and before the processing gate below: only Core's hover
        // releases that gate, so nothing may fail between winning it and
        // StartHover.
        CoreHostHover anchor = job.Observations is { } observations
            ? new(job.Id, observations, PinFrame(observations), job.Game, events, cancellationToken)
            : new(job.Id, job.Selection!, job.Game, events, cancellationToken);
        // One hover in Core at a time, and none during an unload pass: wait
        // for the gate as the Windows tracker retries on its next tick.
        while (!_coordinator.TryBeginProcessing())
        {
            if (_coordinator.IsDisposed)
            {
                throw new RequestErrorException(Errors.ShuttingDown("the translation services were disposed"));
            }

            await _coordinator.WaitForProcessingIdleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        HoverOutcome outcome = await _coordinator.StartHover(anchor, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case HoverOutcome.Completed:
                return HoverOutcomes.Completed;
            case HoverOutcome.Empty:
                return HoverOutcomes.Empty;
            case HoverOutcome.Failed:
                return HoverOutcomes.Failed;
            case HoverOutcome.AnchorLost:
                cancellationToken.ThrowIfCancellationRequested();
                // IsAnchorCurrent fails only for a cancelled request (the app
                // cancels a hover the cursor left), handled above, so only
                // switching hover translation off meanwhile ends one this way.
                throw new RequestErrorException(Errors.HoverDisabled());
            default:
                cancellationToken.ThrowIfCancellationRequested();
                throw new RequestErrorException(Errors.Cancelled("a settings change cancelled the hover"));
        }
    }

    // ---- IHoverHost -------------------------------------------------------

    // The speaker editor and its hotkey arrive with 20.3 (docs/MACOS.md T17).
    bool IHoverHost<CoreHostHover>.SpeakerEditorSupported => false;

    // A selection is already text, and the app prepares Apple Vision itself
    // before it sends observations.
    Task IHoverHost<CoreHostHover>.EnsureTextSourceReadyAsync() => Task.CompletedTask;

    Task<HoverOcrResult?> IHoverHost<CoreHostHover>.RecognizeAsync(
        CoreHostHover anchor,
        bool sentenceContext,
        CancellationToken cancellationToken) =>
        Task.FromResult(anchor.Observations is { } observations
            ? RecognizeObservations(anchor, observations, sentenceContext)
            : SelectionText.Recognize(anchor.Selection!, sentenceContext));

    // The last look at the speaker name before the coordinator resolves it
    // (ApplySpeakerPreference): a corrected spelling then shows its display
    // name, and only this spelling reaches the entry the next hover reads.
    HoverOcrResult IHoverHost<CoreHostHover>.UpgradeRecognized(CoreHostHover anchor, HoverOcrResult recognized)
    {
        string observed = SpeakerGenderPreferences.NormalizeSpeakerName(recognized.CurrentSpeaker?.Name);
        anchor.ObservedSpeakerName = observed.Length == 0 ? null : observed;
        return recognized;
    }

    SpeakerGameIdentity? IHoverHost<CoreHostHover>.ResolveSpeakerGame(CoreHostHover anchor) =>
        anchor.Game is { } game ? new SpeakerGameIdentity(game.Key, game.DisplayName) : null;

    void IHoverHost<CoreHostHover>.OnRecognized(CoreHostHover anchor, HoverOcrResult recognized)
    {
    }

    bool IHoverHost<CoreHostHover>.IsAnchorCurrent(CoreHostHover anchor) =>
        !anchor.CancellationToken.IsCancellationRequested;

    // hover.loading, only for an observations hover: during capture and
    // recognition the app shows nothing, as Windows shows nothing during OCR,
    // and this is the card Windows shows once the text is known. A selection
    // hover keeps 20.2's events (the app knows its word from the start). A
    // card of a hidden or replaced display generation is stale.
    void IHoverHost<CoreHostHover>.ShowLoading(HoverLoadingCard<CoreHostHover> card)
    {
        if (card.Anchor.Observations is null ||
            card.Anchor.Events is not { } events ||
            !_coordinator.IsCurrentDisplay(card.DisplayGeneration))
        {
            return;
        }

        events.Loading(new HoverLoadingEvent
        {
            Source = card.Source,
            EngineLabel = card.Engine,
            Speaker = card.CurrentSpeaker is { } speaker ? ToSpeakerView(speaker, card.Anchor.ObservedSpeakerName) : null,
        });
    }

    void IHoverHost<CoreHostHover>.ShowTranslation(HoverCard<CoreHostHover> card)
    {
        // A card of a hidden or replaced display generation is stale, exactly
        // as the WPF host drops it on its dispatcher.
        if (card.Anchor.Events is not { } events || !_coordinator.IsCurrentDisplay(card.DisplayGeneration))
        {
            return;
        }

        RememberShown(card);
        if (card.PhraseLine is { } phraseLine)
        {
            events.Phrase(PhraseEvent(phraseLine));
        }
        else
        {
            events.Primary(PrimaryEvent(card));
        }
    }

    // The app hides its overlay itself on every terminal.
    void IHoverHost<CoreHostHover>.HideCard()
    {
    }

    void IHoverHost<CoreHostHover>.CloseAuxiliaryWindows()
    {
    }

    // The app tracks the cursor; CoreHost answers requests.
    void IHoverHost<CoreHostHover>.StartTracking()
    {
    }

    void IHoverHost<CoreHostHover>.StopTracking()
    {
    }

    void IHoverHost<CoreHostHover>.ResetTracking()
    {
    }

    ValueTask IHoverHost<CoreHostHover>.ReleaseHostResourcesAsync() => ValueTask.CompletedTask;

    ValueTask IHoverHost<CoreHostHover>.ReleaseHostResourcesAfterHistoryAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Disposes the coordinator in Core's order: hovers drain, the history is
    /// flushed, the ranker worker is shut down and the caches are written;
    /// then the frame cache is dropped, as WindowsAiOcrService.Dispose drops
    /// its own.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _coordinator.DisposeAsync().ConfigureAwait(false);
        ClearFrameCache();
    }

    private static HoverPrimaryEvent PrimaryEvent(HoverCard<CoreHostHover> card)
    {
        HoverProvenance provenance = card.Provenance ??
            throw new InvalidOperationException("Core posted a card without provenance.");
        return new HoverPrimaryEvent
        {
            Source = card.Source,
            Translation = card.Translation,
            Language = card.Language,
            IsPhrase = card.IsPhrase,
            Found = card.DictionaryFound,
            Confidence = card.Confidence,
            Provenance = provenance.Source switch
            {
                HoverResultSource.Dictionary => "dictionary",
                HoverResultSource.Heuristic => "heuristic",
                HoverResultSource.Tatoeba => "tatoeba",
                HoverResultSource.ResultCache => "resultCache",
                HoverResultSource.Ranker => "ranker",
                HoverResultSource.RankerAbstained => "rankerAbstained",
                HoverResultSource.RankerUnavailable => "rankerUnavailable",
                _ => throw new InvalidOperationException($"Unknown provenance {provenance.Source}."),
            },
            SenseProvider = provenance.SenseProvider is { } provider ? ProviderName(provider) : null,
            EngineLabel = card.Engine,
            Context = card.Context,
            Speaker = card.CurrentSpeaker is { } speaker ? ToSpeakerView(speaker, card.Anchor.ObservedSpeakerName) : null,
        };
    }

    private static HoverPhraseEvent PhraseEvent(SmartHoverPhraseLine line) => new()
    {
        Source = line.Source,
        Translation = line.Translation,
        Status = line.Status == SmartPhraseLineStatus.Translated ? "translated" : "unavailable",
        Provisional = line.IsProvisional,
        FromCache = line.FromCache,
        Rejection = line.RejectionReason is { } reason
            ? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(reason.ToString())
            : null,
        Model = new PhraseModelIdentity
        {
            Family = "translategemma",
            Variant = "4b",
            Backend = line.Backend switch
            {
                TranslateGemmaExecutionBackend.Cpu => "cpu",
                TranslateGemmaExecutionBackend.Vulkan => "gpu",
                _ => "",
            },
            Identity = line.Engine,
        },
    };

    private static string ProviderName(GlossBertRankerProvider provider) => provider switch
    {
        GlossBertRankerProvider.Cpu => "cpu",
        GlossBertRankerProvider.Npu => "npu",
        _ => throw new InvalidOperationException($"Unknown sense provider {provider}."),
    };

    private static SpeakerView ToSpeakerView(CurrentSpeakerContext speaker, string? observedName) => new()
    {
        Name = speaker.Name,
        Gender = speaker.Gender switch
        {
            SpeakerGender.Masculine => SpeakerGenderPreferences.MasculineValue,
            SpeakerGender.Feminine => SpeakerGenderPreferences.FeminineValue,
            SpeakerGender.Ignore => SpeakerGenderPreferences.IgnoreValue,
            _ => "unknown",
        },
        GameKey = speaker.GameKey.Length == 0 ? null : speaker.GameKey,
        ObservedName = observedName,
    };
}
