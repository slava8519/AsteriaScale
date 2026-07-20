using System.Windows.Threading;
using AsteriaScale.Interop;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class HoverTranslationService : IAsyncDisposable
{
    private const long HoverRadiusSquared = 225;
    private const string SmartRankingPipelineVersion = "smart-sense-v7-mwe-tatoeba";
    private readonly AppSettings _settings;
    private readonly WindowsAiOcrService _ocr = new();
    private readonly FoundryTranslationService _translator;
    private readonly LocalDictionaryService _dictionary;
    private readonly GlossBertDictionarySelectorService _glossBertSelector = new();
    private readonly SmartDictionaryResultCache _smartResultCache = new();
    private readonly HoverTranslationOverlay _overlay;
    private readonly ScreenTranslationOverlay _screenOverlay;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly Dispatcher _dispatcher;
    private readonly object _stateLock = new();
    private readonly object _lifecycleLock = new();
    private readonly SemaphoreSlim _smartBackendLock = new(1, 1);
    private System.Threading.Timer? _timer;
    private Task _pendingUnload = Task.CompletedTask;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _screenIdleUnloadCancellation;
    private CancellationTokenSource? _smartFallbackUnloadCancellation;
    private NativeMethods.Point _anchor;
    private DateTime _stableSinceUtc;
    private bool _hasAnchor;
    private bool _processedAnchor;
    private bool _enabled;
    private int _processing;
    private string? _lastReportedStatus;
    private SmartFallbackRequest? _lastSmartFallbackRequest;
    private string? _activeSmartBackend;

    public HoverTranslationService(AppSettings settings, Func<bool>? isScaling = null)
    {
        _settings = settings;
        _ = isScaling; // Kept for source compatibility with existing callers.
        _dictionary = new LocalDictionaryService();
        _translator = new FoundryTranslationService(_dictionary);
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _overlay = new HoverTranslationOverlay();
        _screenOverlay = new ScreenTranslationOverlay();
        _hotkeys = new GlobalHotkeyService();
        _hotkeys.ScreenTranslationPressed += OnScreenTranslationPressed;
        _hotkeys.SmartFallbackPressed += OnSmartFallbackPressed;
        _enabled = settings.HoverTranslationEnabled;
        _glossBertSelector.ConfigureKeepWarm(settings.SmartDictionaryKeepWarm);
    }

    public event EventHandler<string>? StatusChanged;

    public void Start()
    {
        if (!_enabled || IsDictionaryMode || IsSmartMode || IsScreenMode)
        {
            QueueUnload();
        }

        if (_enabled)
        {
            ConfigureActiveMode();
            _ = WarmUpSafeAsync();
        }
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _settings.HoverTranslationEnabled = enabled;
        ResetTracking();
        CancelScreenIdleUnload();
        CancelSmartFallbackUnload();
        if (enabled)
        {
            ConfigureActiveMode();
            _ = WarmUpSafeAsync();
        }
        else
        {
            StopTimer();
            _hotkeys.UnregisterScreenTranslation();
            _hotkeys.UnregisterSmartFallback();
            HideAllOverlays();
            QueueUnload();
            ReportStatus("Перевод текста выключен");
        }
    }

    private void ConfigureActiveMode()
    {
        if (!_enabled)
        {
            StopTimer();
            _hotkeys.UnregisterScreenTranslation();
            _hotkeys.UnregisterSmartFallback();
            return;
        }

        if (IsScreenMode)
        {
            StopTimer();
            _hotkeys.UnregisterSmartFallback();
            if (!_hotkeys.RegisterScreenTranslation())
            {
                ReportStatus("Не удалось назначить Alt+Shift+T — сочетание занято другой программой");
            }
        }
        else
        {
            _hotkeys.UnregisterScreenTranslation();
            if (IsSmartMode && IsOnDemandFallback)
            {
                if (!_hotkeys.RegisterSmartFallback())
                {
                    ReportStatus("Не удалось назначить Alt+Shift+D — сочетание занято другой программой");
                }
            }
            else
            {
                _hotkeys.UnregisterSmartFallback();
            }

            StartTimer();
        }
    }

    public void ApplySmartDictionarySettings()
    {
        _glossBertSelector.ConfigureKeepWarm(_settings.SmartDictionaryKeepWarm);
        _lastSmartFallbackRequest = null;
        ResetTracking();
        ConfigureActiveMode();
        if (_enabled && IsSmartMode)
        {
            QueueUnload();
            _ = WarmUpSafeAsync();
        }
    }

    private void StartTimer() =>
        _timer ??= new System.Threading.Timer(OnTimer, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(90));

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void SetMode(string mode)
    {
        string normalized = HoverTranslationModes.Normalize(mode);
        if (string.Equals(_settings.HoverTranslationMode, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _settings.HoverTranslationMode = normalized;
        ResetTracking();
        CancelScreenIdleUnload();
        CancelSmartFallbackUnload();
        HideAllOverlays();
        ConfigureActiveMode();
        if (_enabled)
        {
            QueueUnload();

            _ = WarmUpSafeAsync();
        }
    }

    private void QueueUnload()
    {
        lock (_lifecycleLock)
        {
            if (_pendingUnload.IsCompleted)
            {
                _pendingUnload = UnloadWhenIdleSafeAsync();
            }
        }
    }

    private async Task UnloadWhenIdleSafeAsync()
    {
        try
        {
            await UnloadWhenIdleAsync();
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    private async Task UnloadWhenIdleAsync()
    {
        for (int attempt = 0; attempt < 100 && Volatile.Read(ref _processing) != 0; attempt++)
        {
            await Task.Delay(50);
        }

        if (!_enabled || IsDictionaryMode || IsScreenMode || IsSmartMode)
        {
            await _translator.UnloadAsync();
        }

        if (!_enabled || (!IsDictionaryMode && !IsSmartMode))
        {
            await _dictionary.UnloadAsync();
        }

        bool backendChanged =
            _enabled &&
            IsSmartMode &&
            _activeSmartBackend is not null &&
            !string.Equals(
                _activeSmartBackend,
                ResolveSmartBackend(),
                StringComparison.Ordinal);
        if (!_enabled || !IsSmartMode || !SmartRankerApplicable || backendChanged)
        {
            await _smartBackendLock.WaitAsync();
            try
            {
                await _glossBertSelector.UnloadAsync();
                _activeSmartBackend = null;
            }
            finally
            {
                _smartBackendLock.Release();
            }
        }

        if (!_enabled || !IsSmartMode)
        {
            _activeSmartBackend = null;
        }
    }

    private async Task WarmUpSafeAsync()
    {
        try
        {
            string requestedMode = HoverTranslationModes.Normalize(_settings.HoverTranslationMode);
            ReportStatus(requestedMode switch
            {
                HoverTranslationModes.Smart =>
                    $"Подготовка NPU OCR, словарей и контекстной модели на {ResolveSmartBackend().ToUpperInvariant()}…",
                HoverTranslationModes.Dictionary => "Подготовка NPU OCR и локального словаря…",
                HoverTranslationModes.Screen => "Подготовка экранного NPU OCR…",
                _ => "Подготовка NPU OCR и локального переводчика…"
            });
            await _ocr.EnsureReadyAsync();
            if (!_enabled || requestedMode != HoverTranslationModes.Normalize(_settings.HoverTranslationMode))
            {
                return;
            }

            if (requestedMode == HoverTranslationModes.Smart)
            {
                if (SmartRankerApplicable)
                {
                    await _dictionary.LoadSenseMetadataAsync();
                }
                else
                {
                    await _dictionary.LoadAsync();
                }

                _glossBertSelector.ConfigureKeepWarm(_settings.SmartDictionaryKeepWarm);
                if (SmartRankerApplicable && _settings.SmartDictionaryKeepWarm)
                {
                    await PrepareSmartBackendAsync(
                        ResolveSmartBackend(),
                        CancellationToken.None);
                }
            }
            else if (requestedMode == HoverTranslationModes.Dictionary)
            {
                await _dictionary.LoadAsync();
            }
            else if (requestedMode != HoverTranslationModes.Screen)
            {
                await _translator.WarmUpAsync();
            }

            if (_enabled && requestedMode == HoverTranslationModes.Normalize(_settings.HoverTranslationMode))
            {
                ReportStatus(requestedMode switch
                {
                    HoverTranslationModes.Smart =>
                        $"Умный словарь готов: {_dictionary.EntryCount:N0} записей; " +
                        (!SmartRankerApplicable
                            ? "японское направление работает напрямую по словарям"
                            : _settings.SmartDictionaryKeepWarm
                                ? $"модель значений прогрета на {ResolveSmartBackend().ToUpperInvariant()}"
                                : $"модель значений загрузится по требованию на {ResolveSmartBackend().ToUpperInvariant()}"),
                    HoverTranslationModes.Dictionary =>
                        $"Быстрый перевод готов: {_dictionary.EntryCount:N0} записей из {_dictionary.SourceCount} словарей",
                    HoverTranslationModes.Screen => "Экранный перевод готов: Alt+Shift+T — перевести все реплики",
                    _ => "Перевод под курсором готов: NPU OCR + локальная Qwen"
                });
            }
        }
        catch (Exception ex)
        {
            LogError(ex);
            ReportStatus("Перевод текста: " + ex.Message);
        }
    }

    private void OnTimer(object? state)
    {
        if (!_enabled || IsScreenMode || !VisibleCursorService.TryGetPosition(out NativeMethods.Point cursor))
        {
            return;
        }

        nint window = NativeMethods.WindowFromPoint(cursor);
        if (window != nint.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint processId);
            if (processId == Environment.ProcessId)
            {
                HideOverlay();
                return;
            }
        }

        bool hide = false;
        NativeMethods.Point pointToProcess = default;
        CancellationToken token = default;
        bool shouldProcess = false;

        lock (_stateLock)
        {
            if (!_hasAnchor || DistanceSquared(cursor, _anchor) > HoverRadiusSquared)
            {
                _anchor = cursor;
                _hasAnchor = true;
                _stableSinceUtc = DateTime.UtcNow;
                _processedAnchor = false;
                _operationCancellation?.Cancel();
                _operationCancellation?.Dispose();
                _operationCancellation = null;
                hide = true;
            }
            else if (!_processedAnchor &&
                     DateTime.UtcNow - _stableSinceUtc >= TimeSpan.FromMilliseconds(EffectiveDelayMs()) &&
                     Interlocked.CompareExchange(ref _processing, 1, 0) == 0)
            {
                _processedAnchor = true;
                _operationCancellation = new CancellationTokenSource();
                token = _operationCancellation.Token;
                pointToProcess = cursor;
                shouldProcess = true;
            }
        }

        if (hide)
        {
            HideOverlay();
        }

        if (shouldProcess)
        {
            _ = ProcessCursorAsync(pointToProcess, token);
        }
    }

    private void OnScreenTranslationPressed(object? sender, EventArgs e) => TranslateScreen();

    private void OnSmartFallbackPressed(object? sender, EventArgs e)
    {
        if (!_enabled || !IsSmartMode || !IsOnDemandFallback)
        {
            ReportStatus("Полный перевод по команде доступен в умном словаре с fallback «по команде»");
            return;
        }

        SmartFallbackRequest? request = _lastSmartFallbackRequest;
        if (request is null || DateTime.UtcNow - request.CreatedUtc > TimeSpan.FromSeconds(45))
        {
            ReportStatus("Сначала наведите курсор на слово или реплику, затем нажмите Alt+Shift+D");
            return;
        }

        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
        {
            ReportStatus("Перевод ещё выполняется — повторите Alt+Shift+D через секунду");
            return;
        }

        _ = ProcessSmartFallbackOnDemandAsync(request);
    }

    private async Task ProcessSmartFallbackOnDemandAsync(SmartFallbackRequest request)
    {
        try
        {
            NativeMethods.Point cursor = VisibleCursorService.TryGetPosition(out NativeMethods.Point current)
                ? current
                : request.Cursor;
            ShowLoading(request.Source, FoundryTranslationService.EngineLabel, cursor);
            (string source, string translation, string engine) = await TranslateSmartFallbackAsync(
                request.Source,
                CancellationToken.None);
            if (!_enabled || !IsSmartMode || !IsOnDemandFallback ||
                string.IsNullOrWhiteSpace(translation))
            {
                HideOverlay();
                return;
            }

            ShowTranslation(
                source,
                translation,
                engine,
                cursor,
                dictionaryFound: true,
                isPhrase: false,
                language: null,
                confidence: null);
            ReportStatus("Полная реплика переведена по Alt+Shift+D");
            ScheduleSmartFallbackUnload();
        }
        catch (Exception ex)
        {
            HideOverlay();
            LogError(ex);
            ReportStatus("Полный перевод по команде: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    public void TranslateScreen()
    {
        if (!_enabled || !IsScreenMode)
        {
            ReportStatus("Сначала включите режим «Экран по клавише»");
            return;
        }

        CancelScreenIdleUnload();
        CancellationToken token;
        lock (_stateLock)
        {
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _operationCancellation = new CancellationTokenSource();
            token = _operationCancellation.Token;
        }

        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
        {
            ReportStatus("Экранный перевод: предыдущая операция отменена, нажмите ещё раз");
            return;
        }

        nint foregroundWindow = NativeMethods.GetForegroundWindow();
        _ = Task.Run(() => ProcessScreenAsync(foregroundWindow, token), token);
    }

    private async Task ProcessScreenAsync(nint foregroundWindow, CancellationToken cancellationToken)
    {
        bool translatedSuccessfully = false;
        try
        {
            await _dispatcher.InvokeAsync(_screenOverlay.HideOverlay, DispatcherPriority.Send, cancellationToken);
            await Task.Delay(90, cancellationToken);
            ReportStatus("Экранный перевод: распознаю все текстовые области…");
            ScreenOcrResult recognized = await _ocr.RecognizeScreenAsync(foregroundWindow, cancellationToken);
            if (!_enabled || !IsScreenMode)
            {
                return;
            }

            if (recognized.Blocks.Count == 0)
            {
                await ShowScreenMessageAsync(recognized.CaptureBounds, "Текст для перевода не найден", cancellationToken);
                return;
            }

            await _dispatcher.InvokeAsync(
                () => _screenOverlay.ShowLoading(
                    recognized.CaptureBounds,
                    $"Перевожу блоков: {recognized.Blocks.Count}…"),
                DispatcherPriority.Normal,
                cancellationToken);
            ReportStatus($"Экранный перевод: перевожу {recognized.Blocks.Count} текстовых блоков локально…");

            IReadOnlyList<string> translations = await _translator.TranslateManyToRussianAsync(
                recognized.Blocks.Select(block => block.Text).ToArray(),
                cancellationToken);
            List<ScreenTranslationVisual> visuals = [];
            for (int index = 0; index < recognized.Blocks.Count && index < translations.Count; index++)
            {
                string translation = translations[index].Trim();
                ScreenOcrBlock source = recognized.Blocks[index];
                if (translation.Length == 0 ||
                    string.Equals(source.Text, translation, StringComparison.CurrentCultureIgnoreCase))
                {
                    continue;
                }

                visuals.Add(new ScreenTranslationVisual(source, translation));
            }

            if (!_enabled || !IsScreenMode)
            {
                return;
            }

            if (visuals.Count == 0)
            {
                await ShowScreenMessageAsync(
                    recognized.CaptureBounds,
                    "На экране уже русский текст или перевод не требуется",
                    cancellationToken);
                return;
            }

            await _dispatcher.InvokeAsync(
                () => _screenOverlay.ShowTranslations(recognized.CaptureBounds, visuals),
                DispatcherPriority.Normal,
                cancellationToken);
            translatedSuccessfully = true;
            ReportStatus($"Экранный перевод: наложено реплик — {visuals.Count}; Alt+Shift+T обновит экран");
        }
        catch (OperationCanceledException)
        {
            HideScreenOverlay();
        }
        catch (Exception ex)
        {
            HideScreenOverlay();
            LogError(ex);
            ReportStatus("Экранный перевод: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
            if (translatedSuccessfully && _enabled && IsScreenMode)
            {
                ScheduleScreenIdleUnload();
            }
        }
    }

    private async Task ShowScreenMessageAsync(
        System.Drawing.Rectangle bounds,
        string message,
        CancellationToken cancellationToken)
    {
        await _dispatcher.InvokeAsync(
            () => _screenOverlay.ShowMessage(bounds, message),
            DispatcherPriority.Normal,
            cancellationToken);
        await Task.Delay(2400, cancellationToken);
        HideScreenOverlay();
    }

    private async Task ProcessCursorAsync(NativeMethods.Point cursor, CancellationToken cancellationToken)
    {
        try
        {
            HoverOcrResult? recognized = await _ocr.RecognizeNearCursorAsync(
                cursor,
                sentenceContext: !IsDictionaryMode,
                cancellationToken);
            if (recognized is null || !_enabled || !CursorIsNear(cursor))
            {
                HideOverlay();
                return;
            }

            string smartBackend = ResolveSmartBackend();
            string currentEngine = IsSmartMode
                ? SmartEngineLabel(smartBackend)
                : IsDictionaryMode ? LocalDictionaryService.EngineLabel : FoundryTranslationService.EngineLabel;
            ShowLoading(IsSmartMode ? recognized.HoveredWord : recognized.Text, currentEngine, cursor);
            string source;
            string translation;
            string engine;
            bool dictionaryFound = true;
            bool isPhrase = false;
            string? language = null;
            double? confidence = null;
            bool usedFullFallback = false;
            SmartDictionaryCacheRequest? resultCacheRequest = null;
            SmartDictionarySelection? resultCacheSelection = null;
            if (IsSmartMode)
            {
                SmartHoverResult smartResult = await ResolveSmartHoverAsync(
                    recognized,
                    cursor,
                    smartBackend,
                    cancellationToken);
                source = smartResult.Source;
                translation = smartResult.Translation;
                engine = smartResult.Engine;
                dictionaryFound = smartResult.DictionaryFound;
                isPhrase = smartResult.IsPhrase;
                language = smartResult.Language;
                confidence = smartResult.Confidence;
                usedFullFallback = smartResult.UsedFullFallback;
                resultCacheRequest = smartResult.CacheRequest;
                resultCacheSelection = smartResult.CacheSelection;
            }
            else if (IsDictionaryMode)
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
            else
            {
                source = recognized.Text;
                translation = await _translator.TranslateToRussianAsync(recognized.Text, cancellationToken);
                engine = FoundryTranslationService.EngineLabel;
            }

            if (!_enabled || !CursorIsNear(cursor) ||
                string.IsNullOrWhiteSpace(translation) ||
                string.Equals(source, translation, StringComparison.CurrentCultureIgnoreCase))
            {
                HideOverlay();
                return;
            }

            ShowTranslation(
                source,
                translation,
                engine,
                cursor,
                dictionaryFound,
                isPhrase,
                language,
                confidence);
            if (resultCacheRequest is not null && resultCacheSelection is not null)
            {
                await StoreSmartSelectionSafeAsync(resultCacheRequest, resultCacheSelection);
            }

            if (usedFullFallback)
            {
                ScheduleSmartFallbackUnload();
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
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    private async Task<SmartHoverResult> ResolveSmartHoverAsync(
        HoverOcrResult recognized,
        NativeMethods.Point cursor,
        string smartBackend,
        CancellationToken cancellationToken)
    {
        DictionaryLookupResult lookup = await _dictionary.LookupWithSenseMetadataAsync(
            recognized.Text,
            recognized.HoveredWord,
            recognized.CharacterIndex,
            cancellationToken);
        if (IsOnDemandFallback)
        {
            _lastSmartFallbackRequest = new SmartFallbackRequest(
                recognized.Text,
                cursor,
                DateTime.UtcNow);
        }

        if (!lookup.Found)
        {
            if (IsAutomaticFallback)
            {
                (string fallbackSource, string fallbackTranslation, string fallbackEngine) =
                    await TranslateSmartFallbackAsync(recognized.Text, cancellationToken);
                return new SmartHoverResult(
                    fallbackSource,
                    fallbackTranslation,
                    fallbackEngine,
                    DictionaryFound: true,
                    IsPhrase: false,
                    Language: lookup.Language,
                    Confidence: null,
                    UsedFullFallback: true);
            }

            return new SmartHoverResult(
                lookup.Source,
                IsOnDemandFallback
                    ? "Нет записи в локальном словаре. Alt+Shift+D — перевести реплику целиком."
                    : lookup.Translation,
                IsOnDemandFallback
                    ? SmartEngineLabel(smartBackend)
                    : LocalDictionaryService.EngineLabel,
                DictionaryFound: false,
                lookup.IsPhrase,
                lookup.Language,
                Confidence: null,
                UsedFullFallback: false);
        }

        SmartDictionaryTargetSpan targetSpan = lookup.IsPhrase
            ? new SmartDictionaryTargetSpan(lookup.Source, true)
            : SmartDictionarySenseResolver.ResolveTargetSpan(
                recognized.Text,
                lookup.Source,
                recognized.CharacterIndex);
        string displaySource = targetSpan.Source;
        bool displayIsPhrase = lookup.IsPhrase || targetSpan.IsPhrase;
        bool englishWord =
            !lookup.IsPhrase &&
            string.Equals(lookup.Language, "EN", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                ResolveSmartSourceLanguage(lookup.Language),
                "English",
                StringComparison.Ordinal);

        // Curated/flat entries have no aligned English glosses, but the cheap
        // context rules are still useful (for example teetering ... verge).
        // Run them before falling back to a multi-chip dictionary card.
        if (englishWord && lookup.SenseCandidates.Count == 0)
        {
            string[] flatTranslations =
                NpuDictionarySelectorService.SplitMeanings(lookup.Translation);
            SmartDictionaryHeuristicSelection? tatoebaSelection =
                SmartDictionaryHeuristicRanker.TrySelectFromContextTranslation(
                    lookup.ContextTranslation,
                    flatTranslations);
            if (tatoebaSelection is not null)
            {
                return new SmartHoverResult(
                    displaySource,
                    tatoebaSelection.Translation,
                    SmartDictionaryHeuristicRanker.TatoebaEngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    tatoebaSelection.Confidence,
                    UsedFullFallback: false);
            }

            SmartDictionaryHeuristicSelection? flatSelection =
                SmartDictionaryHeuristicRanker.TrySelect(
                    recognized.Text,
                    lookup.Source,
                    recognized.CharacterIndex,
                    flatTranslations,
                    "English");
            if (flatSelection is not null)
            {
                return new SmartHoverResult(
                    displaySource,
                    flatSelection.Translation,
                    SmartDictionaryHeuristicRanker.EngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    flatSelection.Confidence,
                    UsedFullFallback: false);
            }
        }

        // GlossBERT is an English single-word sense disambiguator. Curated
        // phrases already contain their intended translation and must never be
        // broken into an artificial word-level ranking request.
        bool rankableEnglishWord =
            englishWord &&
            lookup.SenseCandidates.Count > 0;
        if (!rankableEnglishWord)
        {
            return LocalSmartResult(lookup, displaySource, displayIsPhrase);
        }

        try
        {
            SmartDictionarySenseInventory inventory =
                SmartDictionarySenseResolver.GroupCandidates(
                    recognized.Text,
                    lookup.Source,
                    recognized.CharacterIndex,
                    lookup.SenseCandidates);
            SmartDictionaryRankerPlan plan =
                SmartDictionarySenseResolver.CreateRankerPlan(
                    recognized.Text,
                    lookup.Source,
                    recognized.CharacterIndex,
                    inventory.Groups);
            if (plan.Outcomes.Count == 0)
            {
                return LocalSmartResult(lookup, displaySource, displayIsPhrase);
            }

            string[] visibleTranslations = plan.Outcomes
                .OrderBy(outcome => outcome.GroupId)
                .Select(outcome => outcome.Translation)
                .ToArray();
            SmartDictionaryHeuristicSelection? tatoebaSelection =
                SmartDictionaryHeuristicRanker.TrySelectFromContextTranslation(
                    lookup.ContextTranslation,
                    visibleTranslations);
            if (tatoebaSelection is not null)
            {
                return new SmartHoverResult(
                    displaySource,
                    tatoebaSelection.Translation,
                    SmartDictionaryHeuristicRanker.TatoebaEngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    tatoebaSelection.Confidence,
                    UsedFullFallback: false);
            }

            if (visibleTranslations.Length == 1)
            {
                return new SmartHoverResult(
                    displaySource,
                    visibleTranslations[0],
                    SmartDictionaryHeuristicRanker.EngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    Confidence: 1,
                    UsedFullFallback: false);
            }

            SmartDictionaryHeuristicSelection? fastSelection =
                SmartDictionaryHeuristicRanker.TrySelect(
                    recognized.Text,
                    lookup.Source,
                    recognized.CharacterIndex,
                    visibleTranslations,
                    "English");
            if (fastSelection is not null)
            {
                return new SmartHoverResult(
                    displaySource,
                    fastSelection.Translation,
                    SmartDictionaryHeuristicRanker.EngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    fastSelection.Confidence,
                    UsedFullFallback: false);
            }

            SmartDictionaryCacheRequest cacheRequest = CreateSmartCacheRequest(
                recognized.Text,
                lookup.Source,
                recognized.CharacterIndex,
                visibleTranslations,
                "English",
                smartBackend);
            SmartDictionarySelection? cachedSelection =
                await TryGetSmartSelectionSafeAsync(cacheRequest, cancellationToken);
            if (cachedSelection is not null)
            {
                return new SmartHoverResult(
                    displaySource,
                    cachedSelection.Translation,
                    SmartDictionaryResultCache.EngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    cachedSelection.Confidence,
                    UsedFullFallback: false);
            }

            // No intermediate candidate card: the overlay keeps its loading
            // state until the ranker delivers the final verdict, so the card
            // renders exactly once instead of flashing candidates first.
            string visibleCandidates = FormatVisibleOutcomes(visibleTranslations);
            await PrepareSmartBackendAsync(smartBackend, cancellationToken);
            GlossBertRankerProvider provider = ResolveRankerProvider(smartBackend);
            GlossBertDictionarySelection ranked =
                await _glossBertSelector.SelectAsync(
                    recognized.Text,
                    lookup.Source,
                    recognized.CharacterIndex,
                    plan.CandidateGlosses,
                    plan.CandidateGroupIds,
                    "English",
                    provider,
                    cancellationToken);
            if (ranked.Abstained || ranked.SelectedGroupId is not { } groupId)
            {
                ReportStatus(
                    "Умный словарь: контекста недостаточно — показаны безопасные словарные варианты");
                return new SmartHoverResult(
                    displaySource,
                    visibleCandidates,
                    SmartEngineLabel(smartBackend) + "  •  НЕ УВЕРЕН",
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    Confidence: null,
                    UsedFullFallback: false);
            }

            SmartDictionaryRankerOutcome selectedOutcome = plan.Outcomes
                .First(outcome => outcome.GroupId == groupId);
            SmartDictionarySelection cacheSelection = new(
                selectedOutcome.GroupId,
                selectedOutcome.Translation,
                ranked.Confidence ?? 0,
                string.Empty);
            return new SmartHoverResult(
                displaySource,
                selectedOutcome.Translation,
                SmartEngineLabel(smartBackend),
                DictionaryFound: true,
                displayIsPhrase,
                lookup.Language,
                cacheSelection.Confidence,
                UsedFullFallback: false,
                cacheRequest,
                cacheSelection);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(ex);
            if (IsAutomaticFallback)
            {
                ReportStatus(
                    "Умный словарь: ранжирование недоступно, запущен разрешённый полный перевод");
                (string fallbackSource, string fallbackTranslation, string fallbackEngine) =
                    await TranslateSmartFallbackAsync(recognized.Text, cancellationToken);
                return new SmartHoverResult(
                    fallbackSource,
                    fallbackTranslation,
                    fallbackEngine,
                    DictionaryFound: true,
                    IsPhrase: false,
                    lookup.Language,
                    Confidence: null,
                    UsedFullFallback: true);
            }

            ReportStatus(
                "Умный словарь: модель недоступна — показаны локальные варианты без полного перевода");
            return UnavailableSmartResult(
                lookup,
                smartBackend,
                displaySource,
                displayIsPhrase);
        }
    }

    private static SmartHoverResult LocalSmartResult(
        DictionaryLookupResult lookup,
        string? source = null,
        bool? isPhrase = null) =>
        new(
            source ?? lookup.Source,
            lookup.Translation,
            LocalDictionaryService.EngineLabel,
            DictionaryFound: true,
            isPhrase ?? lookup.IsPhrase,
            lookup.Language,
            Confidence: null,
            UsedFullFallback: false);

    private static SmartHoverResult UnavailableSmartResult(
        DictionaryLookupResult lookup,
        string smartBackend,
        string? source = null,
        bool? isPhrase = null) =>
        new(
            source ?? lookup.Source,
            lookup.Translation,
            SmartEngineLabel(smartBackend) + "  •  НЕДОСТУПЕН",
            DictionaryFound: true,
            isPhrase ?? lookup.IsPhrase,
            lookup.Language,
            Confidence: null,
            UsedFullFallback: false);

    private static string FormatVisibleOutcomes(IReadOnlyList<string> translations)
    {
        const int maximumVisibleOutcomes = 8;
        const int maximumLength = 380;
        string value = string.Join(
            "; ",
            translations
                .Where(translation => !string.IsNullOrWhiteSpace(translation))
                .Take(maximumVisibleOutcomes));
        if (translations.Count > maximumVisibleOutcomes)
        {
            value += "; …";
        }

        return value.Length <= maximumLength
            ? value
            : value[..(maximumLength - 1)].TrimEnd() + "…";
    }

    private bool CursorIsNear(NativeMethods.Point expected) =>
        VisibleCursorService.TryGetPosition(out NativeMethods.Point current) &&
        DistanceSquared(current, expected) <= HoverRadiusSquared;

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

    private string ResolveSmartSourceLanguage(string? detectedLanguage)
    {
        string configured = SmartDictionarySourceLanguages.Normalize(
            _settings.SmartDictionarySourceLanguage);
        if (configured == SmartDictionarySourceLanguages.English)
        {
            return "English";
        }

        if (configured == SmartDictionarySourceLanguages.Japanese)
        {
            return "Japanese";
        }

        return string.Equals(detectedLanguage, "JA", StringComparison.OrdinalIgnoreCase)
            ? "Japanese"
            : "English";
    }

    private static GlossBertRankerProvider ResolveRankerProvider(string backend) =>
        backend == SmartDictionaryBackends.Npu
            ? GlossBertRankerProvider.Npu
            : GlossBertRankerProvider.Cpu;

    private static string SmartEngineLabel(string backend) =>
        GlossBertDictionarySelectorService.GetEngineLabel(
            ResolveRankerProvider(backend));

    private SmartDictionaryCacheRequest CreateSmartCacheRequest(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<string> candidates,
        string sourceLanguage,
        string backend)
    {
        GlossBertRankerProvider provider = ResolveRankerProvider(backend);
        return new SmartDictionaryCacheRequest(
            context,
            target,
            characterIndex,
            candidates,
            sourceLanguage,
            backend,
            GlossBertDictionarySelectorService.GetModelCacheId(provider),
            _dictionary.VersionToken + "." + SmartRankingPipelineVersion);
    }

    private async Task<SmartDictionarySelection?> TryGetSmartSelectionSafeAsync(
        SmartDictionaryCacheRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _smartResultCache.TryGetAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return null;
        }
    }

    private async Task StoreSmartSelectionSafeAsync(
        SmartDictionaryCacheRequest request,
        SmartDictionarySelection selection)
    {
        try
        {
            // The result is already on screen. Persisting it must neither delay the UI
            // nor be discarded merely because the cursor moved a moment later.
            await _smartResultCache.StoreAsync(request, selection, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    private async Task PrepareSmartBackendAsync(string backend, CancellationToken cancellationToken)
    {
        await _smartBackendLock.WaitAsync(cancellationToken);
        try
        {
            _glossBertSelector.ConfigureKeepWarm(_settings.SmartDictionaryKeepWarm);
            await _glossBertSelector.WarmUpAsync(
                ResolveRankerProvider(backend),
                cancellationToken);
            _activeSmartBackend = backend;
        }
        finally
        {
            _smartBackendLock.Release();
        }
    }

    private bool IsDictionaryMode =>
        HoverTranslationModes.Normalize(_settings.HoverTranslationMode) == HoverTranslationModes.Dictionary;

    private bool IsSmartMode =>
        HoverTranslationModes.Normalize(_settings.HoverTranslationMode) == HoverTranslationModes.Smart;

    private bool IsScreenMode =>
        HoverTranslationModes.Normalize(_settings.HoverTranslationMode) == HoverTranslationModes.Screen;

    private bool IsAutomaticFallback =>
        SmartDictionaryFallbackModes.Normalize(_settings.SmartDictionaryFallbackMode) ==
        SmartDictionaryFallbackModes.Automatic;

    private bool IsOnDemandFallback =>
        SmartDictionaryFallbackModes.Normalize(_settings.SmartDictionaryFallbackMode) ==
        SmartDictionaryFallbackModes.OnDemand;

    private bool SmartRankerApplicable =>
        SmartDictionarySourceLanguages.Normalize(
            _settings.SmartDictionarySourceLanguage) !=
        SmartDictionarySourceLanguages.Japanese;

    private static long DistanceSquared(NativeMethods.Point left, NativeMethods.Point right)
    {
        long dx = left.X - right.X;
        long dy = left.Y - right.Y;
        return (dx * dx) + (dy * dy);
    }

    private void ResetTracking()
    {
        lock (_stateLock)
        {
            _hasAnchor = false;
            _processedAnchor = false;
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _operationCancellation = null;
        }
    }

    private void ShowLoading(string source, string engine, NativeMethods.Point cursor) =>
        _dispatcher.BeginInvoke(() => _overlay.ShowLoading(source, cursor, engine), DispatcherPriority.Normal);

    private void ShowTranslation(
        string source,
        string translation,
        string engine,
        NativeMethods.Point cursor,
        bool dictionaryFound,
        bool isPhrase,
        string? language,
        double? confidence) =>
        _dispatcher.BeginInvoke(
            () => _overlay.ShowTranslation(
                source,
                translation,
                cursor,
                engine,
                dictionaryFound,
                isPhrase,
                language,
                confidence),
            DispatcherPriority.Normal);

    private async Task<(string Source, string Translation, string Engine)> TranslateSmartFallbackAsync(
        string source,
        CancellationToken cancellationToken)
    {
        CancelSmartFallbackUnload();
        string translation = await _translator.TranslateToRussianAsync(source, cancellationToken);
        return (source, translation, FoundryTranslationService.EngineLabel);
    }

    private void HideOverlay() =>
        _dispatcher.BeginInvoke(_overlay.HideOverlay, DispatcherPriority.Normal);

    private void HideScreenOverlay() =>
        _dispatcher.BeginInvoke(_screenOverlay.HideOverlay, DispatcherPriority.Normal);

    private void HideAllOverlays()
    {
        HideOverlay();
        HideScreenOverlay();
    }

    private void ScheduleScreenIdleUnload()
    {
        CancelScreenIdleUnload();
        CancellationTokenSource cancellation = new();
        _screenIdleUnloadCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), cancellation.Token);
                if (_enabled && IsScreenMode && Volatile.Read(ref _processing) == 0)
                {
                    await _translator.UnloadAsync();
                    ReportStatus("Экранный перевод готов: Qwen выгружена после простоя, Alt+Shift+T загрузит её снова");
                }
            }
            catch (OperationCanceledException)
            {
                // A new screen request keeps the local model warm.
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        });
    }

    private void CancelScreenIdleUnload()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(
            ref _screenIdleUnloadCancellation,
            null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void ScheduleSmartFallbackUnload()
    {
        CancelSmartFallbackUnload();
        CancellationTokenSource cancellation = new();
        _smartFallbackUnloadCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45), cancellation.Token);
                if (_enabled && IsSmartMode &&
                    Volatile.Read(ref _processing) == 0)
                {
                    await _translator.UnloadAsync();
                    ReportStatus("Умный словарь готов: резервная полная модель выгружена после простоя");
                }
            }
            catch (OperationCanceledException)
            {
                // Another fallback request keeps the translator warm briefly.
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        });
    }

    private void CancelSmartFallbackUnload()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(
            ref _smartFallbackUnloadCancellation,
            null);
        cancellation?.Cancel();
        cancellation?.Dispose();
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

    private static void LogError(Exception exception)
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

            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {exception}\n\n");
        }
        catch
        {
            // Diagnostics must never interfere with hover translation.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _enabled = false;
        StopTimer();
        ResetTracking();
        CancelScreenIdleUnload();
        CancelSmartFallbackUnload();
        _hotkeys.UnregisterScreenTranslation();
        _hotkeys.UnregisterSmartFallback();
        QueueUnload();
        Task pendingUnload;
        lock (_lifecycleLock)
        {
            pendingUnload = _pendingUnload;
        }

        await pendingUnload;
        await _dispatcher.InvokeAsync(() =>
        {
            _overlay.HideOverlay();
            _overlay.Close();
            _screenOverlay.HideOverlay();
            _screenOverlay.Close();
            _hotkeys.ScreenTranslationPressed -= OnScreenTranslationPressed;
            _hotkeys.SmartFallbackPressed -= OnSmartFallbackPressed;
            _hotkeys.Dispose();
        });
        _ocr.Dispose();
        await _translator.DisposeAsync();
        await _glossBertSelector.DisposeAsync();
        await _smartResultCache.DisposeAsync();
        _smartBackendLock.Dispose();
    }

    private sealed record SmartHoverResult(
        string Source,
        string Translation,
        string Engine,
        bool DictionaryFound,
        bool IsPhrase,
        string? Language,
        double? Confidence,
        bool UsedFullFallback,
        SmartDictionaryCacheRequest? CacheRequest = null,
        SmartDictionarySelection? CacheSelection = null);

    private sealed record SmartFallbackRequest(
        string Source,
        NativeMethods.Point Cursor,
        DateTime CreatedUtc);
}
