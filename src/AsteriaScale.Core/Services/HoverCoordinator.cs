using AsteriaScale.Models;

namespace AsteriaScale.Services;

/// <summary>
/// The platform-neutral half of hover translation, moved verbatim from the
/// Windows HoverTranslationService (owner decision M12): mode and lifecycle
/// state, coalesced unload and warm-up, the one-hover-in-flight gate,
/// cancellation of stale work, the order loading card → primary card → result
/// cache store → phrase line, history of what was shown, status de-duplication
/// and speaker-gender saving. Text acquisition, cursor tracking, presentation
/// and the host's own resources stay behind <see cref="IHoverHost{TAnchor}"/>.
/// Members keep the order they had in HoverTranslationService, so the move
/// audit can compare the two in order.
/// </summary>
internal sealed class HoverCoordinator<TAnchor> : IAsyncDisposable
    where TAnchor : notnull
{
    private readonly AppSettings _settings;
    private readonly Func<Task> _saveSettingsAsync;
    private readonly IHoverHost<TAnchor> _host;
    private readonly LocalDictionaryService _dictionary;
    private readonly GlossBertDictionarySelectorService _glossBertSelector;
    private readonly SemanticRankerProfileApplicator _semanticRankerProfile;
    private readonly TranslationScaffoldGlossBertRanker _scaffoldGlossBertRanker;
    private readonly SmartDictionaryResultCache _smartResultCache;
    private readonly SmartPhraseTranslationCache _smartPhraseCache;
    // Null on a host without a phrase model (macOS until 20.5).
    private readonly IHoverPhraseModel? _translateGemma;
    // Shared by the hover pipeline and the full-text scaffold: both ask the
    // worker the same question, so neither may pay for the other's inference.
    private readonly GlossBertDecisionCache _decisionCache;
    private readonly object _lifecycleLock = new();
    private readonly ModelActivityGate _modelActivity = new();
    private readonly object _taskLock = new();
    private readonly HashSet<Task> _activeTasks = [];
    private readonly List<CancellationTokenSource> _retiredModeCancellations = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SmartHoverPipeline _smartPipeline;
    private readonly HoverEngineLabels _labels;
    private readonly TranslationHistoryService _history;
    private readonly HoverUnloadIntentState _unloadIntent = new();
    private Task _pendingUnload = Task.CompletedTask;
    private long _unloadRevision;
    private bool _unloadWorkerRunning;
    private CancellationTokenSource? _modeCancellation;
    private bool _enabled;
    private bool _disposed;
    private long _overlayGeneration;
    private string? _lastReportedStatus;
    private bool? _translateGemmaReady;
    private HoverSpeakerTarget<TAnchor>? _visibleSpeakerTarget;

    /// <summary>
    /// Builds every core service and the pipeline. It never calls
    /// <paramref name="host"/>, so the Windows host passes itself before it
    /// creates its windows, as the core services came first before the move.
    /// A null <paramref name="services"/> creates the Windows defaults, and
    /// null <paramref name="engineLabels"/> keeps the Windows labels.
    /// </summary>
    internal HoverCoordinator(
        AppSettings settings,
        Func<Task>? saveSettingsAsync,
        IHoverHost<TAnchor> host,
        HoverCoreServices? services = null,
        HoverEngineLabels? engineLabels = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(host);
        _settings = settings;
        _saveSettingsAsync = saveSettingsAsync ?? (() => Task.CompletedTask);
        _host = host;
        services ??= HoverCoreServices.CreateDefault(settings);
        _smartResultCache = services.ResultCache;
        _smartPhraseCache = services.PhraseCache;
        _decisionCache = services.DecisionCache;
        _history = services.History;
        _translateGemma = services.PhraseModel;
        _glossBertSelector = services.Selector;
        _semanticRankerProfile = new SemanticRankerProfileApplicator(
            settings,
            _glossBertSelector);
        _dictionary = services.Dictionary;
        _scaffoldGlossBertRanker = new(
            _glossBertSelector,
            _decisionCache,
            ResolveGlossBertProvider,
            () => _dictionary.VersionToken);
        _enabled = settings.HoverTranslationEnabled;
        _glossBertSelector.ConfigureKeepWarm(settings.SmartDictionaryKeepWarm);
        // The host names its text source in every label and warm-up status.
        _labels = engineLabels ?? HoverEngineLabels.WindowsAiNpuOcr;
        _smartPipeline = new SmartHoverPipeline(
            settings,
            _dictionary,
            _glossBertSelector,
            _smartResultCache,
            _decisionCache,
            ReportStatus,
            _translateGemma,
            _smartPhraseCache,
            _scaffoldGlossBertRanker.RankAsync,
            _labels);
        RestartModeCancellation();
    }

    public event EventHandler<string>? StatusChanged;

    internal bool Enabled => _enabled;

    internal bool IsDisposed => _disposed;

    internal void Start()
    {
        QueueUnload(HoverUnloadReason.Startup);

        if (_enabled)
        {
            ConfigureActiveMode();
            QueueWarmUpAfterPendingUnload();
        }
    }

    internal void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _settings.HoverTranslationEnabled = enabled;
        RestartModeCancellation();
        ResetTracking();
        if (enabled)
        {
            ConfigureActiveMode();
            QueueWarmUpAfterPendingUnload();
        }
        else
        {
            StopTimer();
            HideAllOverlays();
            QueueUnload(HoverUnloadReason.ExplicitDisable);
            ReportStatus("Перевод текста выключен");
        }
    }

    internal void ConfigureActiveMode()
    {
        if (!_enabled)
        {
            StopTimer();
            return;
        }

        StartTimer();
    }

    internal void ApplySmartDictionarySettings()
    {
        RestartModeCancellation();
        _glossBertSelector.ConfigureKeepWarm(_settings.SmartDictionaryKeepWarm);
        _translateGemmaReady = null;
        ResetTracking();
        ConfigureActiveMode();
        if (_enabled && !IsDictionaryMode)
        {
            QueueUnload(HoverUnloadReason.SmartSettingsChanged);
            QueueWarmUpAfterPendingUnload();
        }
    }

    private void StartTimer() => _host.StartTracking();

    private void StopTimer() => _host.StopTracking();

    internal void SetMode(string mode)
    {
        string normalized = HoverTranslationModes.NormalizeUserMode(mode);
        if (string.Equals(_settings.HoverTranslationMode, normalized, StringComparison.Ordinal))
        {
            ConfigureActiveMode();
            if (_enabled)
            {
                QueueWarmUpAfterPendingUnload();
            }
            return;
        }

        _settings.HoverTranslationMode = normalized;
        RestartModeCancellation();
        ResetTracking();
        HideAllOverlays();
        ConfigureActiveMode();
        if (_enabled)
        {
            QueueUnload(HoverUnloadReason.ModeChanged);
            QueueWarmUpAfterPendingUnload();
        }
    }

    private void QueueUnload(HoverUnloadReason reason)
    {
        TaskCompletionSource? completion = null;
        Task pendingUnload;
        lock (_lifecycleLock)
        {
            _unloadRevision++;
            _unloadIntent.Request(reason);
            if (!_unloadWorkerRunning)
            {
                _unloadWorkerRunning = true;
                completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingUnload = completion.Task;
            }

            pendingUnload = _pendingUnload;
        }

        if (completion is not null)
        {
            TrackTask(pendingUnload);
            _ = RunUnloadUntilStableAsync(completion);
        }
    }

    /// <summary>
    /// Queues an unload on the host's behalf (CoreHost's models.unload) and
    /// returns the task that completes once a pass covering this request has
    /// run. It is the coalesced unload a settings change queues: the pass waits
    /// for the hover in flight and holds the maintenance lease while it runs,
    /// so no hover starts meanwhile, and requests made while a pass waits or
    /// runs share one task and add at most one more pass. Like every pass it
    /// unloads what the current settings do not need (everything while hover
    /// translation is off, the ranker of another mode or backend, the phrase
    /// model in memory-saving mode); a model the settings keep warm stays.
    /// </summary>
    internal Task RequestUnload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        QueueUnload(HoverUnloadReason.HostRequested);
        lock (_lifecycleLock)
        {
            // Whichever loop this is (the one QueueUnload joined or started, or
            // a later one), it completes only after a pass that read the
            // revision this request bumped.
            return _pendingUnload;
        }
    }

    private async Task RunUnloadUntilStableAsync(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                long revision;
                bool releaseDictionaryMemoryRequested;
                lock (_lifecycleLock)
                {
                    revision = _unloadRevision;
                    releaseDictionaryMemoryRequested =
                        _unloadIntent.ConsumeReleaseRequest();
                }

                await UnloadWhenIdleSafeAsync(releaseDictionaryMemoryRequested);

                lock (_lifecycleLock)
                {
                    if (revision != _unloadRevision)
                    {
                        continue;
                    }

                    _unloadWorkerRunning = false;
                    completion.TrySetResult();
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            LogError(exception);
            lock (_lifecycleLock)
            {
                _unloadWorkerRunning = false;
                completion.TrySetResult();
            }
        }
    }

    private void QueueWarmUpAfterPendingUnload()
    {
        Task pendingUnload;
        lock (_lifecycleLock)
        {
            pendingUnload = _pendingUnload;
        }

        CancellationToken cancellationToken = CurrentModeToken;
        TrackTask(WarmUpAfterUnloadSafeAsync(pendingUnload, cancellationToken));
    }

    private async Task WarmUpAfterUnloadSafeAsync(
        Task pendingUnload,
        CancellationToken cancellationToken)
    {
        try
        {
            await pendingUnload.WaitAsync(cancellationToken);
            await WarmUpSafeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task UnloadWhenIdleSafeAsync(bool releaseDictionaryMemoryRequested)
    {
        try
        {
            await UnloadWhenIdleAsync(releaseDictionaryMemoryRequested);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    private async Task UnloadWhenIdleAsync(bool releaseDictionaryMemoryRequested)
    {
        using IDisposable maintenance =
            await _modelActivity.BeginMaintenanceWhenIdleAsync();

        bool backendChanged =
            _enabled &&
            _glossBertSelector.ActiveProvider is { } activeProvider &&
            activeProvider != ResolveGlossBertProvider();
        bool unloadDictionary = !_enabled || (!IsDictionaryMode && !IsSmartMode);
        await UnloadModelOwnersAsync(
            unloadDictionary: unloadDictionary,
            unloadRanker: !_enabled || !ActiveModeUsesGlossBert || backendChanged,
            unloadPhraseTranslator: !_enabled ||
                                      !IsSmartMode ||
                                      !SmartPhraseTranslationEnabled ||
                                      !_settings.SmartDictionaryKeepWarm,
            forgetSmartBackend: !_enabled || !IsSmartMode,
            releaseDictionaryMemory:
                HoverUnloadIntentState.ShouldReleaseDictionaryMemory(
                    releaseDictionaryMemoryRequested,
                    unloadDictionary));
    }

    private async Task UnloadModelOwnersAsync(
        bool unloadDictionary,
        bool unloadRanker,
        bool unloadPhraseTranslator,
        bool forgetSmartBackend,
        bool releaseDictionaryMemory = false)
    {
        List<Exception> failures = [];
        if (unloadDictionary)
        {
            Func<Task> unload = releaseDictionaryMemory
                ? _dictionary.UnloadAndReleaseMemoryAsync
                : _dictionary.UnloadAsync;
            await CaptureUnloadFailureAsync(
                unload,
                failures);
        }

        if (unloadRanker)
        {
            await CaptureUnloadFailureAsync(_smartPipeline.UnloadRankerAsync, failures);
        }

        if (unloadPhraseTranslator && _translateGemma is not null)
        {
            await CaptureUnloadFailureAsync(_translateGemma.UnloadAsync, failures);
        }

        if (forgetSmartBackend)
        {
            _smartPipeline.ForgetBackend();
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more translation model owners failed to unload.", failures);
        }
    }

    private static async Task CaptureUnloadFailureAsync(
        Func<Task> unload,
        ICollection<Exception> failures)
    {
        try
        {
            await unload();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private async Task WarmUpSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            string requestedMode = HoverTranslationModes.NormalizeUserMode(_settings.HoverTranslationMode);
            ReportStatus(requestedMode switch
            {
                HoverTranslationModes.Smart =>
                    $"Подготовка {_labels.TextSourceName}, словарей и контекстной модели на {ResolveSmartBackend().ToUpperInvariant()}…",
                HoverTranslationModes.Dictionary => $"Подготовка {_labels.TextSourceName} и локального словаря…",
                _ => $"Подготовка {_labels.TextSourceName} и локального словаря…"
            });
            await _host.EnsureTextSourceReadyAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_enabled || requestedMode != HoverTranslationModes.NormalizeUserMode(_settings.HoverTranslationMode))
            {
                return;
            }

            if (requestedMode == HoverTranslationModes.Smart)
            {
                await _semanticRankerProfile.ApplyAsync(cancellationToken);
            }

            if (requestedMode == HoverTranslationModes.Smart)
            {
                if (SmartRankerApplicable)
                {
                    await _dictionary.LoadSenseMetadataAsync(cancellationToken);
                }
                else
                {
                    await _dictionary.LoadAsync(cancellationToken);
                }

                _glossBertSelector.ConfigureKeepWarm(_settings.SmartDictionaryKeepWarm);
                if (SmartRankerApplicable && _settings.SmartDictionaryKeepWarm)
                {
                    await _smartPipeline.PrepareSmartBackendAsync(
                        ResolveSmartBackend(),
                        cancellationToken);
                }

                if (SmartRankerApplicable &&
                    SmartPhraseTranslationEnabled &&
                    _settings.SmartDictionaryKeepWarm)
                {
                    await WarmTranslateGemmaSafeAsync(cancellationToken);
                }
            }
            else if (requestedMode == HoverTranslationModes.Dictionary)
            {
                await _dictionary.LoadAsync(cancellationToken);
            }
            if (_enabled &&
                requestedMode == HoverTranslationModes.NormalizeUserMode(_settings.HoverTranslationMode))
            {
                ReportStatus(requestedMode switch
                {
                    HoverTranslationModes.Smart => BuildSmartReadyStatus(),
                    HoverTranslationModes.Dictionary =>
                        $"Быстрый перевод готов: {_dictionary.EntryCount:N0} записей из {_dictionary.SourceCount} словарей",
                    _ => BuildSmartReadyStatus()
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogError(ex);
            ReportStatus("Перевод текста: " + ex.Message);
        }
    }

    private async Task WarmTranslateGemmaSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Reached only while SmartPhraseTranslationEnabled, which requires
            // a phrase model.
            await _translateGemma!.WarmUpAsync(cancellationToken);
            _translateGemmaReady = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _translateGemmaReady = false;
            LogError(exception);
        }
    }

    private string BuildSmartReadyStatus()
    {
        string ranking = !SmartRankerApplicable
            ? "японское направление работает напрямую по словарям"
            : _settings.SmartDictionaryKeepWarm
                ? $"модель значений прогрета на {ResolveSmartBackend().ToUpperInvariant()}"
                : $"модель значений загрузится по требованию на {ResolveSmartBackend().ToUpperInvariant()}";
        // The settings ask for a phrase line this host cannot produce: say so,
        // rather than leave it out silently or promise a model that never loads.
        if (SmartRankerApplicable && SmartPhraseTranslationRequested && _translateGemma is null)
        {
            return $"Умный словарь готов: {_dictionary.EntryCount:N0} записей; {ranking}; " +
                   HoverPhraseStatus.UnavailableOnPlatform;
        }

        if (!SmartRankerApplicable || !SmartPhraseTranslationEnabled)
        {
            return $"Умный словарь готов: {_dictionary.EntryCount:N0} записей; {ranking}";
        }

        string phraseBackend = TranslateGemmaTranslationService.ResolveBackend(
                _settings.SmartPhraseTranslationBackend) ==
            TranslateGemmaExecutionBackend.Vulkan
                ? "Vulkan GPU"
                : "CPU";
        string phrase = _settings.SmartDictionaryKeepWarm switch
        {
            true when _translateGemmaReady == true =>
                $"TranslateGemma 4B прогрета на {phraseBackend}",
            true when _translateGemmaReady == false =>
                $"TranslateGemma 4B ({phraseBackend}) недоступна; словарный результат остаётся рабочим",
            _ => $"TranslateGemma 4B загрузится по требованию на {phraseBackend}"
        };
        return $"Умный словарь готов: {_dictionary.EntryCount:N0} записей; {ranking}; {phrase}";
    }

    /// <summary>
    /// Starts one hover and tracks it until disposal. The caller must have won
    /// <see cref="TryBeginProcessing"/>; the hover releases that gate when it
    /// ends, whatever happens.
    /// The task ends with how the hover ended, which a host answering hover
    /// requests (CoreHost) reports and the Windows host ignores; an exception
    /// or a cancellation becomes an outcome, not a fault.
    /// </summary>
    internal Task<HoverOutcome> StartHover(TAnchor anchor, CancellationToken cancellationToken)
    {
        Task<HoverOutcome> hover = ProcessHoverAsync(anchor, cancellationToken);
        TrackTask(hover);
        return hover;
    }

    private async Task<HoverOutcome> ProcessHoverAsync(TAnchor anchor, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetimeCancellation.Token,
                    CurrentModeToken);
            cancellationToken = linkedCancellation.Token;
            long displayGeneration = BeginOverlayOperation();
            HoverOcrResult? recognized = await _host.RecognizeAsync(
                anchor,
                sentenceContext: !IsDictionaryMode,
                cancellationToken);
            if (recognized is null || !_enabled || !_host.IsAnchorCurrent(anchor))
            {
                HideOverlay();
                return recognized is null ? HoverOutcome.Empty : HoverOutcome.AnchorLost;
            }

            recognized = _host.UpgradeRecognized(anchor, recognized);
            recognized = ApplySpeakerPreference(recognized, anchor);
            _host.OnRecognized(anchor, recognized);

            string smartBackend = ResolveSmartBackend();
            string currentEngine = IsSmartMode
                ? _labels.For(HoverProvenance.Ranker(SmartHoverPipeline.ResolveRankerProvider(smartBackend)))
                : _labels.For(HoverProvenance.Dictionary);
            _host.ShowLoading(new HoverLoadingCard<TAnchor>(
                displayGeneration,
                anchor,
                IsSmartMode ? recognized.HoveredWord : recognized.Text,
                currentEngine,
                recognized.TextSource,
                recognized.CurrentSpeaker,
                SpeakerEditorAvailable));
            string source;
            string translation;
            string engine;
            HoverProvenance? provenance;
            bool dictionaryFound = true;
            bool isPhrase = false;
            string? language = null;
            double? confidence = null;
            SmartDictionaryCacheRequest? resultCacheRequest = null;
            SmartDictionarySelection? resultCacheSelection = null;
            SmartHoverResult? resolvedSmartResult = null;
            if (IsSmartMode)
            {
                resolvedSmartResult = await _smartPipeline.ResolveSmartHoverAsync(
                    recognized,
                    smartBackend,
                    cancellationToken);
                source = resolvedSmartResult.Source;
                translation = resolvedSmartResult.Translation;
                engine = resolvedSmartResult.Engine;
                provenance = resolvedSmartResult.Provenance;
                dictionaryFound = resolvedSmartResult.DictionaryFound;
                isPhrase = resolvedSmartResult.IsPhrase;
                language = resolvedSmartResult.Language;
                confidence = resolvedSmartResult.Confidence;
                resultCacheRequest = resolvedSmartResult.CacheRequest;
                resultCacheSelection = resolvedSmartResult.CacheSelection;
            }
            else
            {
                DictionaryLookupResult lookup = await _dictionary.LookupAsync(
                    recognized.Text,
                    recognized.HoveredWord,
                    recognized.CharacterIndex,
                    cancellationToken);
                source = lookup.Source;
                translation = lookup.Translation;
                LabelledProvenance dictionaryLabel = _labels.Labelled(HoverProvenance.Dictionary);
                engine = dictionaryLabel.Engine;
                provenance = dictionaryLabel.Provenance;
                dictionaryFound = lookup.Found;
                isPhrase = lookup.IsPhrase;
                language = lookup.Language;
            }

            if (!_enabled || !_host.IsAnchorCurrent(anchor))
            {
                HideOverlay();
                return HoverOutcome.AnchorLost;
            }

            // The three posts of this hover (primary, provisional and final
            // phrase line) differ only in the phrase line, as the three
            // argument lists of the Windows host did.
            HoverCard<TAnchor> Card(SmartHoverPhraseLine? phraseLine) =>
                new(
                    displayGeneration,
                    anchor,
                    source,
                    translation,
                    engine,
                    dictionaryFound,
                    isPhrase,
                    language,
                    confidence,
                    recognized.Text,
                    RequireAnchorCurrent: true,
                    recognized.TextSource,
                    phraseLine,
                    recognized.CurrentSpeaker,
                    SpeakerEditorAvailable)
                {
                    Provenance = provenance
                };

            bool primaryDisplayable =
                resolvedSmartResult is not null ||
                !string.IsNullOrWhiteSpace(translation) &&
                !string.Equals(
                    source,
                    translation,
                    StringComparison.CurrentCultureIgnoreCase);
            if (primaryDisplayable)
            {
                // Keep the dictionary fast: render its result before the
                // optional 4B phrase model starts or reads its own cache.
                _host.ShowTranslation(Card(phraseLine: null));
            }

            if (resultCacheRequest is not null && resultCacheSelection is not null)
            {
                await _smartPipeline.StoreSmartSelectionSafeAsync(resultCacheRequest, resultCacheSelection);
            }

            // Candidate A may stand in while the sense gate runs (card E18);
            // the final line below replaces it on the same card.
            void ShowProvisionalPhrase(SmartHoverPhraseLine provisional) =>
                _host.ShowTranslation(Card(phraseLine: provisional));
            SmartHoverPhraseLine? phraseLine = resolvedSmartResult is null
                ? null
                : await _smartPipeline.TranslatePhraseLineAsync(
                    recognized,
                    resolvedSmartResult,
                    cancellationToken,
                    ShowProvisionalPhrase);
            if (phraseLine is not null)
            {
                _host.ShowTranslation(Card(phraseLine: phraseLine));
            }
            else if (!primaryDisplayable)
            {
                HideOverlay();
                return HoverOutcome.Empty;
            }

            return HoverOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            HideOverlay();
            return HoverOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            HideOverlay();
            LogError(ex);
            ReportStatus("Перевод под курсором: " + ex.Message);
            return HoverOutcome.Failed;
        }
        finally
        {
            EndProcessing();
            // A rejected or cancelled generation still loaded the optional
            // phrase model. In memory-saving mode every Smart hover therefore
            // schedules an idempotent unload, including fail-open results.
            if (SmartPhraseTranslationEnabled && !_settings.SmartDictionaryKeepWarm)
            {
                QueueUnload(HoverUnloadReason.PhraseMemorySaving);
            }
        }
    }

    private HoverOcrResult ApplySpeakerPreference(
        HoverOcrResult recognized,
        TAnchor anchor)
    {
        CurrentSpeakerContext? extracted = recognized.CurrentSpeaker;
        if (extracted is null || string.IsNullOrWhiteSpace(extracted.Name))
        {
            _visibleSpeakerTarget = null;
            return recognized;
        }

        SpeakerGameIdentity? game = _host.ResolveSpeakerGame(anchor);
        CurrentSpeakerContext resolved = SpeakerGenderPreferences.Resolve(
            _settings,
            game,
            extracted.Name);
        if (game is not null)
        {
            _visibleSpeakerTarget = new HoverSpeakerTarget<TAnchor>(
                game,
                resolved,
                anchor);
        }
        else
        {
            _visibleSpeakerTarget = null;
        }

        return recognized with { CurrentSpeaker = resolved };
    }

    internal int EffectiveDelayMs() => Math.Clamp(_settings.HoverTranslationDelayMs, 350, 1500);

    private string ResolveSmartBackend()
    {
        string configured = SmartDictionaryBackends.Normalize(_settings.SmartDictionaryBackend);
        if (configured != SmartDictionaryBackends.Auto)
        {
            return configured;
        }

        // CPU uses the unquantized reference artifact. The compiled Vitis
        // model is faster and closely tracks it, but remains an explicit
        // choice so Auto never changes lexical decisions with the accelerator.
        return SmartDictionaryBackends.Cpu;
    }

    private GlossBertRankerProvider ResolveGlossBertProvider() =>
        string.Equals(
            ResolveSmartBackend(),
            SmartDictionaryBackends.Npu,
            StringComparison.Ordinal)
            ? GlossBertRankerProvider.Npu
            : GlossBertRankerProvider.Cpu;

    private bool IsDictionaryMode =>
        HoverTranslationModes.NormalizeUserMode(_settings.HoverTranslationMode) == HoverTranslationModes.Dictionary;

    private bool IsSmartMode =>
        HoverTranslationModes.NormalizeUserMode(_settings.HoverTranslationMode) == HoverTranslationModes.Smart;

    // A host without a phrase model never warms, unloads or waits for one,
    // whatever the settings ask for; the Windows host always has one.
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(_translateGemma))]
    private bool SmartPhraseTranslationEnabled =>
        _translateGemma is not null && SmartPhraseTranslationRequested;

    private bool SmartPhraseTranslationRequested =>
        SmartPhraseTranslationModes.Normalize(_settings.SmartPhraseTranslationMode) ==
        SmartPhraseTranslationModes.TranslateGemma;

    private bool ActiveModeUsesGlossBert =>
        IsSmartMode && SmartRankerApplicable;

    private bool SmartRankerApplicable =>
        SmartDictionarySourceLanguages.Normalize(
            _settings.SmartDictionarySourceLanguage) !=
            SmartDictionarySourceLanguages.Japanese;

    private bool SpeakerEditorAvailable =>
        _host.SpeakerEditorSupported && _visibleSpeakerTarget is not null;

    /// <summary>The speaker of the visible card, or null once it is hidden.</summary>
    internal HoverSpeakerTarget<TAnchor>? VisibleSpeakerTarget => _visibleSpeakerTarget;

    private void ResetTracking() => _host.ResetTracking();

    /// <summary>
    /// Called by the host when it has actually rendered <paramref name="card"/>,
    /// on the thread that rendered it.
    /// </summary>
    internal void RecordShown(HoverCard<TAnchor> card)
    {
        // The progressive phrase update redraws the same primary card.
        // Record that answer only on its first render; otherwise every
        // successful TranslateGemma call would duplicate history.
        if (card.DictionaryFound && card.PhraseLine is null)
        {
            _history.Record(card.Source, card.Translation, card.Engine, card.Context);
        }

        if (HoverPolicy.IsPhraseHistoryEntry(card.PhraseLine))
        {
            _history.Record(
                card.PhraseLine.Source,
                card.PhraseLine.Translation,
                card.PhraseLine.Engine,
                card.Context,
                "EN");
        }
    }

    /// <summary>History of shown translations, for the review panel and export.</summary>
    internal TranslationHistoryService History => _history;

    /// <summary>
    /// What the model owners are doing now, for a host's status line
    /// (CoreHost's models.status). It reads fields only: it waits for no hover,
    /// warm-up or unload and loads nothing.
    /// </summary>
    internal HoverModelStatus GetModelStatus() =>
        new(
            new HoverDictionaryStatus(
                _dictionary.IsLoaded,
                _dictionary.EntryCount,
                _dictionary.SourceCount,
                _dictionary.VersionToken,
                _dictionary.SenseMetadataKeyCount),
            new HoverSenseStatus(
                _enabled && ActiveModeUsesGlossBert,
                ResolveGlossBertProvider(),
                _glossBertSelector.CpuModelProfile,
                _settings.SmartDictionaryKeepWarm,
                _glossBertSelector.IsWorkerRunning,
                _glossBertSelector.WorkerProcessId,
                _glossBertSelector.ActiveProvider,
                _glossBertSelector.ActiveModelVersion,
                _glossBertSelector.LastErrorCode),
            new HoverPhraseStatus(
                _translateGemma is not null,
                SmartPhraseTranslationRequested,
                _translateGemmaReady,
                _translateGemma is null ? HoverPhraseStatus.UnavailableOnPlatform : null));

    /// <summary>
    /// Stores the user's gender choice for <paramref name="target"/> (the
    /// speaker editor's Saved event) and returns the target as it now resolves.
    /// </summary>
    internal HoverSpeakerTarget<TAnchor> SaveSpeakerGender(
        HoverSpeakerTarget<TAnchor> target,
        SpeakerGender gender,
        string name)
    {
        bool changed = SpeakerGenderPreferences.Set(
            _settings,
            target.Game,
            target.Speaker.Name,
            gender,
            name);
        if (!string.Equals(
                target.Speaker.Name,
                name,
                StringComparison.OrdinalIgnoreCase))
        {
            // Keep the observed OCR spelling as an alias while also accepting
            // the user's correction if a later frame recognizes it cleanly.
            changed |= SpeakerGenderPreferences.Set(
                _settings,
                target.Game,
                name,
                gender,
                name);
        }

        HoverSpeakerTarget<TAnchor> updatedTarget = target with
        {
            Speaker = SpeakerGenderPreferences.Resolve(
                _settings,
                target.Game,
                target.Speaker.Name)
        };
        if (changed)
        {
            TrackTask(SaveSpeakerSettingsSafeAsync());
        }

        string genderText = gender switch
        {
            SpeakerGender.Masculine => "мужской",
            SpeakerGender.Feminine => "женский",
            _ => "не учитывать"
        };
        ReportStatus($"{name}: сохранён {genderText} род для {target.Game.DisplayName}");
        return updatedTarget;
    }

    private async Task SaveSpeakerSettingsSafeAsync()
    {
        try
        {
            await _saveSettingsAsync();
        }
        catch (Exception exception)
        {
            LogError(exception);
            ReportStatus("Не удалось сохранить род говорящего: " + exception.Message);
        }
    }

    /// <summary>
    /// Re-reads dictionaries after the user edited their own layer, then warms
    /// them in the background so the next hover is not slowed down.
    /// </summary>
    internal async Task ReloadDictionariesAsync()
    {
        using IDisposable maintenance =
            await _modelActivity.BeginMaintenanceWhenIdleAsync(_lifetimeCancellation.Token);
        await _dictionary.UnloadAsync();
        await _dictionary.LoadAsync(_lifetimeCancellation.Token);
    }

    internal void HideOverlay()
    {
        _visibleSpeakerTarget = null;
        Interlocked.Increment(ref _overlayGeneration);
        _host.HideCard();
    }

    private void HideAllOverlays()
    {
        HideOverlay();
        _host.CloseAuxiliaryWindows();
    }

    private long BeginOverlayOperation() => Interlocked.Increment(ref _overlayGeneration);

    /// <summary>Whether a card posted for <paramref name="displayGeneration"/> may still be shown.</summary>
    internal bool IsCurrentDisplay(long displayGeneration) =>
        displayGeneration == Volatile.Read(ref _overlayGeneration);

    internal bool TryBeginProcessing()
    {
        if (_disposed || !_modelActivity.TryBeginProcessing())
        {
            return false;
        }

        if (!_disposed)
        {
            return true;
        }

        _modelActivity.EndProcessing();
        return false;
    }

    /// <summary>
    /// Releases a gate won by <see cref="TryBeginProcessing"/>. A started hover
    /// does this itself; a host calls it only for a gate it won without
    /// starting one.
    /// </summary>
    internal void EndProcessing() => _modelActivity.EndProcessing();

    internal Task WaitForProcessingIdleAsync() => _modelActivity.WaitForIdleAsync();

    private CancellationToken CurrentModeToken
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _modeCancellation?.Token ?? _lifetimeCancellation.Token;
            }
        }
    }

    internal void RestartModeCancellation()
    {
        CancellationTokenSource replacement = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        CancellationTokenSource? previous;
        lock (_lifecycleLock)
        {
            previous = _modeCancellation;
            _modeCancellation = replacement;
        }

        previous?.Cancel();
        if (previous is not null)
        {
            lock (_lifecycleLock)
            {
                _retiredModeCancellations.Add(previous);
            }
        }
    }

    private void TrackTask(Task task)
    {
        lock (_taskLock)
        {
            _activeTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_taskLock)
                {
                    _activeTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task AwaitActiveTasksAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_taskLock)
            {
                tasks = _activeTasks.ToArray();
            }

            if (tasks.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                LogError(exception);
            }
        }
    }

    /// <summary>
    /// Raises <see cref="StatusChanged"/> unless it repeats the last status.
    /// The host reports its own statuses here too, so both share one
    /// de-duplication state, as they did before the move.
    /// </summary>
    internal void ReportStatus(string status)
    {
        if (string.Equals(_lastReportedStatus, status, StringComparison.Ordinal))
        {
            return;
        }

        _lastReportedStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    // Kept so existing call sites read the same; the log itself lives in the
    // core so platform-neutral pipeline code can write to it.
    private static void LogError(Exception exception) => HoverDiagnostics.LogError(exception);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _enabled = false;
        _lifetimeCancellation.Cancel();
        CancellationTokenSource? modeCancellation;
        lock (_lifecycleLock)
        {
            modeCancellation = _modeCancellation;
            _modeCancellation = null;
        }

        modeCancellation?.Cancel();
        StopTimer();
        ResetTracking();
        HideAllOverlays();
        await WaitForProcessingIdleAsync();
        await AwaitActiveTasksAsync();
        // The Windows order before the move: windows, hotkey, tracker and OCR,
        // then the history, then Textractor, then the model owners.
        await _host.ReleaseHostResourcesAsync();
        _history.Dispose();
        await _host.ReleaseHostResourcesAfterHistoryAsync();
        if (_translateGemma is not null)
        {
            await _translateGemma.DisposeAsync();
        }

        await _glossBertSelector.DisposeAsync();
        await _smartResultCache.DisposeAsync();
        await _smartPhraseCache.DisposeAsync();
        await _decisionCache.DisposeAsync();
        _smartPipeline.Dispose();
        modeCancellation?.Dispose();
        lock (_lifecycleLock)
        {
            foreach (CancellationTokenSource cancellation in _retiredModeCancellations)
            {
                cancellation.Dispose();
            }

            _retiredModeCancellations.Clear();
        }

        _lifetimeCancellation.Dispose();
    }
}
