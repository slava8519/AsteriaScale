using System.Windows.Threading;
using AsteriaScale.Interop;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class HoverTranslationService : IAsyncDisposable
{
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
    private readonly object _lifecycleLock = new();
    private readonly SmartHoverPipeline _smartPipeline;
    private readonly ScreenTranslationFlow _screenFlow;
    private readonly HoverTracking _tracking;
    private Task _pendingUnload = Task.CompletedTask;
    private CancellationTokenSource? _screenIdleUnloadCancellation;
    private CancellationTokenSource? _smartFallbackUnloadCancellation;
    private bool _enabled;
    private int _processing;
    private string? _lastReportedStatus;
    private SmartFallbackRequest? _lastSmartFallbackRequest;

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
        _smartPipeline = new SmartHoverPipeline(
            settings,
            _dictionary,
            _glossBertSelector,
            _smartResultCache,
            TranslateSmartFallbackAsync,
            ReportStatus,
            (text, cursor) => _lastSmartFallbackRequest =
                new SmartFallbackRequest(text, cursor, DateTime.UtcNow));
        _screenFlow = new ScreenTranslationFlow(
            _ocr,
            _translator,
            _screenOverlay,
            _dispatcher,
            ReportStatus,
            () => _enabled && IsScreenMode);
        _tracking = new HoverTracking(
            () => _enabled && !IsScreenMode,
            EffectiveDelayMs,
            HideOverlay,
            () => Interlocked.CompareExchange(ref _processing, 1, 0) == 0,
            (point, token) => _ = ProcessCursorAsync(point, token));
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

    private void StartTimer() => _tracking.Start();

    private void StopTimer() => _tracking.Stop();

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
            _smartPipeline.ActiveBackend is not null &&
            !string.Equals(
                _smartPipeline.ActiveBackend,
                ResolveSmartBackend(),
                StringComparison.Ordinal);
        if (!_enabled || !IsSmartMode || !SmartRankerApplicable || backendChanged)
        {
            await _smartPipeline.UnloadRankerAsync();
        }

        if (!_enabled || !IsSmartMode)
        {
            _smartPipeline.ForgetBackend();
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
                    await _smartPipeline.PrepareSmartBackendAsync(
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
        CancellationToken token = _tracking.RestartOperation();

        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
        {
            ReportStatus("Экранный перевод: предыдущая операция отменена, нажмите ещё раз");
            return;
        }

        nint foregroundWindow = NativeMethods.GetForegroundWindow();
        _ = Task.Run(() => RunScreenFlowAsync(foregroundWindow, token), token);
    }

    private async Task RunScreenFlowAsync(nint foregroundWindow, CancellationToken cancellationToken)
    {
        bool translated = false;
        try
        {
            translated = await _screenFlow.ProcessAsync(foregroundWindow, cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
            if (translated && _enabled && IsScreenMode)
            {
                ScheduleScreenIdleUnload();
            }
        }
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
                ? SmartHoverPipeline.SmartEngineLabel(smartBackend)
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
                SmartHoverResult smartResult = await _smartPipeline.ResolveSmartHoverAsync(
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
                await _smartPipeline.StoreSmartSelectionSafeAsync(resultCacheRequest, resultCacheSelection);
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

    private void ResetTracking() => _tracking.Reset();

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

    internal static void LogError(Exception exception)
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
        _smartPipeline.Dispose();
    }

    private sealed record SmartFallbackRequest(
        string Source,
        NativeMethods.Point Cursor,
        DateTime CreatedUtc);
}
