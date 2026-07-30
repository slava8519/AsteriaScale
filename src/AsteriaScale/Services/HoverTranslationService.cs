using System.Windows.Threading;
using AsteriaScale.Interop;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class HoverTranslationService : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly WindowsAiOcrService _ocr = new();
    private readonly LocalDictionaryService _dictionary;
    private readonly GlossBertDictionarySelectorService _glossBertSelector;
    private readonly SemanticRankerProfileApplicator _semanticRankerProfile;
    private readonly TranslationScaffoldGlossBertRanker _scaffoldGlossBertRanker;
    private readonly SmartDictionaryResultCache _smartResultCache = new();
    private readonly SmartPhraseTranslationCache _smartPhraseCache = new();
    private readonly TranslateGemmaTranslationService _translateGemma;
    // Shared by the hover pipeline and the full-text scaffold: both ask the
    // worker the same question, so neither may pay for the other's inference.
    private readonly GlossBertDecisionCache _decisionCache = new();
    private readonly HoverTranslationOverlay _overlay;
    private readonly Dispatcher _dispatcher;
    private readonly object _lifecycleLock = new();
    private readonly ModelActivityGate _modelActivity = new();
    private readonly object _taskLock = new();
    private readonly HashSet<Task> _activeTasks = [];
    private readonly List<CancellationTokenSource> _retiredModeCancellations = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SmartHoverPipeline _smartPipeline;
    private readonly HoverTracking _tracking;
    private readonly TextHookService _textHook;
    private readonly TranslationHistoryService _history = new();
    private readonly HoverUnloadIntentState _unloadIntent = new();
    private Task _pendingUnload = Task.CompletedTask;
    private long _unloadRevision;
    private bool _unloadWorkerRunning;
    private CancellationTokenSource? _modeCancellation;
    private bool _enabled;
    private bool _disposed;
    private long _overlayGeneration;
    private string? _lastReportedStatus;
    private int _textHookDiagnosticProcessId;
    private bool _textHookMatchReported;
    private bool? _translateGemmaReady;

    public HoverTranslationService(AppSettings settings)
    {
        _settings = settings;
        _translateGemma = new TranslateGemmaTranslationService(
            backendResolver: () => TranslateGemmaTranslationService.ResolveBackend(
                _settings.SmartPhraseTranslationBackend));
        RankerCpuModelProfile initialRankerProfile =
            SemanticRankerProfileResolver.FromSettings(settings);
        _glossBertSelector = new GlossBertDictionarySelectorService(initialRankerProfile);
        _semanticRankerProfile = new SemanticRankerProfileApplicator(
            settings,
            _glossBertSelector);
        _dictionary = new LocalDictionaryService();
        _scaffoldGlossBertRanker = new(
            _glossBertSelector,
            _decisionCache,
            ResolveGlossBertProvider,
            () => _dictionary.VersionToken);
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _overlay = new HoverTranslationOverlay();
        _enabled = settings.HoverTranslationEnabled;
        _glossBertSelector.ConfigureKeepWarm(settings.SmartDictionaryKeepWarm);
        _smartPipeline = new SmartHoverPipeline(
            settings,
            _dictionary,
            _glossBertSelector,
            _smartResultCache,
            _decisionCache,
            ReportStatus,
            _translateGemma,
            _smartPhraseCache,
            _scaffoldGlossBertRanker.RankAsync);
        _tracking = new HoverTracking(
            () => _enabled,
            EffectiveDelayMs,
            HideOverlay,
            TryBeginProcessing,
            (point, token) => TrackTask(ProcessCursorAsync(point, token)));
        _textHook = new TextHookService(settings);
        RestartModeCancellation();
    }

    public event EventHandler<string>? StatusChanged;

    public void Start()
    {
        QueueUnload(HoverUnloadReason.Startup);

        if (_enabled)
        {
            ConfigureActiveMode();
            QueueWarmUpAfterPendingUnload();
        }
    }

    public void SetEnabled(bool enabled)
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

    private void ConfigureActiveMode()
    {
        if (!_enabled)
        {
            StopTimer();
            return;
        }

        StartTimer();
    }

    public void ApplySmartDictionarySettings()
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

    private void StartTimer() => _tracking.Start();

    private void StopTimer() => _tracking.Stop();

    public void SetMode(string mode)
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

        if (unloadPhraseTranslator)
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
                    $"Подготовка NPU OCR, словарей и контекстной модели на {ResolveSmartBackend().ToUpperInvariant()}…",
                HoverTranslationModes.Dictionary => "Подготовка NPU OCR и локального словаря…",
                _ => "Подготовка NPU OCR и локального словаря…"
            });
            await _ocr.EnsureReadyAsync();
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
            await _translateGemma.WarmUpAsync(cancellationToken);
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

    private async Task ProcessCursorAsync(NativeMethods.Point cursor, CancellationToken cancellationToken)
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
            HoverOcrResult? recognized = await _ocr.RecognizeNearCursorAsync(
                cursor,
                sentenceContext: !IsDictionaryMode,
                cancellationToken);
            if (recognized is null || !_enabled || !CursorIsNear(cursor))
            {
                HideOverlay();
                return;
            }

            recognized = UpgradeWithTextHook(recognized, cursor);
            TextHookStatusSnapshot textHookStatus = _textHook.GetStatusSnapshot(
                _textHook.AttachedProcessId,
                recognized.TextSource == HoverTextSource.TextHook);
            ReportStatus("Text-hook: " + textHookStatus.Details);

            string smartBackend = ResolveSmartBackend();
            string currentEngine = IsSmartMode
                ? SmartHoverPipeline.SmartEngineLabel(smartBackend)
                : LocalDictionaryService.EngineLabel;
            ShowLoading(
                IsSmartMode ? recognized.HoveredWord : recognized.Text,
                currentEngine,
                cursor,
                displayGeneration,
                recognized.TextSource,
                textHookStatus);
            string source;
            string translation;
            string engine;
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
                engine = LocalDictionaryService.EngineLabel;
                dictionaryFound = lookup.Found;
                isPhrase = lookup.IsPhrase;
                language = lookup.Language;
            }

            if (!_enabled || !CursorIsNear(cursor))
            {
                HideOverlay();
                return;
            }

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
                ShowTranslation(
                    source,
                    translation,
                    engine,
                    cursor,
                    dictionaryFound,
                    isPhrase,
                    language,
                    confidence,
                    recognized.Text,
                    displayGeneration,
                    requireCursorNear: true,
                    textSource: recognized.TextSource,
                    textHookStatus: textHookStatus);
            }

            if (resultCacheRequest is not null && resultCacheSelection is not null)
            {
                await _smartPipeline.StoreSmartSelectionSafeAsync(resultCacheRequest, resultCacheSelection);
            }

            SmartHoverPhraseLine? phraseLine = resolvedSmartResult is null
                ? null
                : await _smartPipeline.TranslatePhraseLineAsync(
                    recognized,
                    resolvedSmartResult,
                    cancellationToken);
            if (phraseLine is not null)
            {
                ShowTranslation(
                    source,
                    translation,
                    engine,
                    cursor,
                    dictionaryFound,
                    isPhrase,
                    language,
                    confidence,
                    recognized.Text,
                    displayGeneration,
                    requireCursorNear: true,
                    textSource: recognized.TextSource,
                    phraseLine: phraseLine,
                    textHookStatus: textHookStatus);
            }
            else if (!primaryDisplayable)
            {
                HideOverlay();
            }

        }
        catch (OperationCanceledException)
        {
            HideOverlay();
        }
        catch (Exception ex)
        {
            HideOverlay();
            LogError(ex);
            ReportStatus("Перевод под курсором: " + ex.Message);
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

    /// <summary>
    /// Optional Textractor upgrade: the hovered word and its position stay
    /// OCR-anchored, only the sentence context is replaced when a freshly
    /// hooked sentence contains that word. Any failure keeps the OCR result,
    /// so translation stays universal for arbitrary programs and documents.
    /// </summary>
    private HoverOcrResult UpgradeWithTextHook(HoverOcrResult recognized, NativeMethods.Point cursor)
    {
        if (!_textHook.IsEnabled)
        {
            _textHook.Detach();
            return recognized;
        }

        try
        {
            // OCR has already consumed the visible Magpie destination point.
            // Resolve only the process used by Textractor back to Magpie's
            // source HWND; changing the cursor here would corrupt OCR geometry.
            nint window = VisibleCursorService.GetTextSourceWindow(cursor);
            if (window == nint.Zero)
            {
                _textHook.Detach();
                return recognized;
            }

            NativeMethods.GetWindowThreadProcessId(window, out uint processIdValue);
            int processId = unchecked((int)processIdValue);
            if (processId <= 0 ||
                processId == Environment.ProcessId)
            {
                _textHook.Detach();
                return recognized;
            }

            // Let TextHookService see profile-disabled targets as well. It will
            // keep OCR in charge, but the card can then distinguish an explicit
            // per-game opt-out from a missing or failed Textractor installation.
            _textHook.EnsureAttached(processId);
            if (_textHook.AttachedProcessId != processId)
            {
                return recognized;
            }

            if (_textHookDiagnosticProcessId != processId)
            {
                _textHookDiagnosticProcessId = processId;
                _textHookMatchReported = false;
                ReportStatus("Text-hook: Textractor запущен, совпадающий текст пока не получен");
            }

            TextHookMatch? match = _textHook.TryGetSentenceContaining(
                processId,
                recognized.HoveredWord,
                recognized.Text,
                recognized.CharacterIndex);
            if (match is not null)
            {
                if (!_textHookMatchReported)
                {
                    _textHookMatchReported = true;
                    ReportStatus("Text-hook: используется текст Textractor (HOOK)");
                }

                return ApplyTextHookMatch(recognized, match);
            }
        }
        catch (Exception exception)
        {
            LogError(exception);
        }

        return recognized;
    }

    internal static HoverOcrResult ApplyTextHookMatch(
        HoverOcrResult recognized,
        TextHookMatch? match) =>
        match is null
            ? recognized
            : recognized with
            {
                Text = match.Text,
                CharacterIndex = match.CharacterIndex,
                TextSource = HoverTextSource.TextHook,
                PreviousContext = match.PreviousContext
            };

    private static bool CursorIsNear(NativeMethods.Point expected) =>
        VisibleCursorService.TryGetPosition(out NativeMethods.Point current) &&
        HoverTracking.DistanceSquared(current, expected) <= HoverTracking.RadiusSquared;

    private int EffectiveDelayMs() => Math.Clamp(_settings.HoverTranslationDelayMs, 350, 1500);

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

    private bool SmartPhraseTranslationEnabled =>
        SmartPhraseTranslationModes.Normalize(_settings.SmartPhraseTranslationMode) ==
        SmartPhraseTranslationModes.TranslateGemma;

    private bool ActiveModeUsesGlossBert =>
        IsSmartMode && SmartRankerApplicable;

    private bool SmartRankerApplicable =>
        SmartDictionarySourceLanguages.Normalize(
            _settings.SmartDictionarySourceLanguage) !=
        SmartDictionarySourceLanguages.Japanese;

    private void ResetTracking() => _tracking.Reset();

    private void ShowLoading(
        string source,
        string engine,
        NativeMethods.Point cursor,
        long displayGeneration,
        HoverTextSource textSource = HoverTextSource.Ocr,
        TextHookStatusSnapshot? textHookStatus = null) =>
        _dispatcher.BeginInvoke(
            () =>
            {
                if (!_disposed &&
                    _enabled &&
                    displayGeneration == Volatile.Read(ref _overlayGeneration))
                {
                    _overlay.ShowLoading(
                        source,
                        cursor,
                        engine,
                        textSource,
                        textHookStatus);
                }
            },
            DispatcherPriority.Normal);

    private void ShowTranslation(
        string source,
        string translation,
        string engine,
        NativeMethods.Point cursor,
        bool dictionaryFound,
        bool isPhrase,
        string? language,
        double? confidence,
        string context,
        long displayGeneration,
        bool requireCursorNear,
        HoverTextSource textSource = HoverTextSource.Ocr,
        SmartHoverPhraseLine? phraseLine = null,
        TextHookStatusSnapshot? textHookStatus = null)
    {
        _dispatcher.BeginInvoke(
            () =>
            {
                if (_disposed ||
                    !_enabled ||
                    displayGeneration != Volatile.Read(ref _overlayGeneration) ||
                    requireCursorNear && !CursorIsNear(cursor))
                {
                    return;
                }

                _overlay.ShowTranslation(
                    source,
                    translation,
                    cursor,
                    engine,
                    dictionaryFound,
                    isPhrase,
                    language,
                    confidence,
                    textSource,
                    phraseLine,
                    textHookStatus);
                // The progressive phrase update redraws the same primary card.
                // Record that answer only on its first render; otherwise every
                // successful TranslateGemma call would duplicate history.
                if (dictionaryFound && phraseLine is null)
                {
                    _history.Record(source, translation, engine, context);
                }

                if (phraseLine is
                    {
                        Status: SmartPhraseLineStatus.Translated,
                        Translation.Length: > 0
                    })
                {
                    _history.Record(
                        phraseLine.Source,
                        phraseLine.Translation,
                        phraseLine.Engine,
                        context,
                        "EN");
                }
            },
            DispatcherPriority.Normal);
    }

    /// <summary>History of shown translations, for the review panel and export.</summary>
    public TranslationHistoryService History => _history;

    /// <summary>
    /// Re-reads dictionaries after the user edited their own layer, then warms
    /// them in the background so the next hover is not slowed down.
    /// </summary>
    public async Task ReloadDictionariesAsync()
    {
        using IDisposable maintenance =
            await _modelActivity.BeginMaintenanceWhenIdleAsync(_lifetimeCancellation.Token);
        await _dictionary.UnloadAsync();
        await _dictionary.LoadAsync(_lifetimeCancellation.Token);
    }

    private void HideOverlay()
    {
        Interlocked.Increment(ref _overlayGeneration);
        _dispatcher.BeginInvoke(_overlay.HideOverlay, DispatcherPriority.Normal);
    }

    private void HideAllOverlays() => HideOverlay();

    public void ApplyTextHookSettings()
    {
        _textHookDiagnosticProcessId = 0;
        _textHookMatchReported = false;
        _textHook.ApplySettings();
    }

    private long BeginOverlayOperation() => Interlocked.Increment(ref _overlayGeneration);

    private bool TryBeginProcessing()
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

    private void EndProcessing() => _modelActivity.EndProcessing();

    private Task WaitForProcessingIdleAsync() => _modelActivity.WaitForIdleAsync();

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

    private void RestartModeCancellation()
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

    private void ReportStatus(string status)
    {
        if (string.Equals(_lastReportedStatus, status, StringComparison.Ordinal))
        {
            return;
        }

        _lastReportedStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    internal static void LogError(Exception exception)
    {
        AppendDiagnostic(exception.ToString());
    }

    internal static void LogDiagnostic(string message)
    {
        AppendDiagnostic((message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '));
    }

    private static void AppendDiagnostic(string message)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AsteriaScale");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "hover-translation.log");
            FileInfo log = new(path);
            if (log.Exists && log.Length > 512 * 1024)
            {
                File.Move(path, path + ".1", overwrite: true);
            }

            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {message}\n\n");
        }
        catch
        {
            // Diagnostics must never interfere with hover translation.
        }
    }

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
        await _dispatcher.InvokeAsync(() =>
        {
            _overlay.HideOverlay();
            _overlay.Close();
        });
        _tracking.Dispose();
        _ocr.Dispose();
        _history.Dispose();
        await _textHook.DisposeAsync();
        await _translateGemma.DisposeAsync();
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
