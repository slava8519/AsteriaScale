using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AsteriaScale.Interop;
using AsteriaScale.Models;
using AsteriaScale.Services;

namespace AsteriaScale;

public partial class MainWindow : Window
{
    public IReadOnlyList<UpscalePreset> AvailablePresets => PresetCatalog.All;

    private readonly AppSettings _settings;
    private readonly WindowCatalogService _windowCatalog;
    private readonly MagpieEngineService _engine;
    private readonly StartupService _startup;
    private readonly HoverTranslationService _hoverTranslation;
    private readonly HardwareService _hardware = new();
    private readonly DpiCompatibilityService _dpiCompatibility = new();
    private readonly ObservableCollection<WindowInfo> _visibleWindows = [];
    private readonly ObservableCollection<AutomationProfile> _profiles = [];
    private readonly DispatcherTimer _statusTimer;
    private IReadOnlyList<WindowInfo> _allWindows = [];
    private bool _initializing = true;
    private bool _operationInProgress;
    private static readonly IReadOnlyList<HoverDelayOption> HoverDelays =
    [
        new("Быстро — 0,45 с", 450),
        new("Сбалансировано — 0,7 с", 700),
        new("Спокойно — 1,0 с", 1000)
    ];
    private static readonly IReadOnlyList<TranslationModeOption> TranslationModes =
    [
        new("Умный словарь — выбор значения по контексту", HoverTranslationModes.Smart),
        new("Словарный — быстро и экономно", HoverTranslationModes.Dictionary)
    ];
    private static readonly IReadOnlyList<TranslationSettingOption> SmartBackendOptions =
    [
        new("Авто — CPU (рекомендуется, эталонный)", SmartDictionaryBackends.Auto),
        new("CPU — точнее, требует больше RAM", SmartDictionaryBackends.Cpu),
        new("NPU — быстрее и экономнее", SmartDictionaryBackends.Npu)
    ];
    private static readonly IReadOnlyList<TranslationSettingOption> SmartSourceLanguageOptions =
    [
        new("Авто — определить по тексту", SmartDictionarySourceLanguages.Auto),
        new("Английский → русский", SmartDictionarySourceLanguages.English),
        new("Японский → русский", SmartDictionarySourceLanguages.Japanese)
    ];
    private static readonly IReadOnlyList<MemoryPolicyOption> SmartMemoryPolicyOptions =
    [
        new("Всегда готова — быстрее", true),
        new("Экономить память — выгружать при простое", false)
    ];
    private static readonly IReadOnlyList<TranslationSettingOption> SmartRankerModelOptions =
    [
        new("GlossBERT — стабильный эталон", SmartDictionaryRankerModels.GlossBert),
        new("ConSeC — лучший для науки, CPU", SmartDictionaryRankerModels.Consec)
    ];
    private static readonly IReadOnlyList<TranslationSettingOption> SmartPhraseTranslationOptions =
    [
        new("Словарная форма — быстро и надёжно", SmartPhraseTranslationModes.Off),
        new("TranslateGemma 4B — перевод предложения второй строкой", SmartPhraseTranslationModes.TranslateGemma)
    ];
    private static readonly IReadOnlyList<TranslationSettingOption> SmartPhraseBackendOptions =
    [
        new("Vulkan GPU — быстрее", SmartPhraseTranslationBackends.Vulkan),
        new("CPU — совместимый режим", SmartPhraseTranslationBackends.Cpu)
    ];
    private static readonly IReadOnlyList<FrameRateOption> FrameRateOptions =
    [
        new("30 FPS — экономно, для HQ", 30),
        new("45 FPS — сбалансированно", 45),
        new("60 FPS — максимальная плавность", 60)
    ];

    public MainWindow(
        AppSettings settings,
        WindowCatalogService windowCatalog,
        MagpieEngineService engine,
        StartupService startup,
        HoverTranslationService hoverTranslation)
    {
        _settings = settings;
        _windowCatalog = windowCatalog;
        _engine = engine;
        _startup = startup;
        _hoverTranslation = hoverTranslation;

        InitializeComponent();

        // A 1366×768 laptop at 125–150 % scaling offers far less work area than
        // the designed size, and a window taller than the work area opens with
        // its footer — where the scaling button lives — below the screen edge.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);

        WindowsList.ItemsSource = _visibleWindows;
        ProfilesList.ItemsSource = _profiles;
        PresetComboBox.ItemsSource = PresetCatalog.All;
        PresetComboBox.SelectedItem = PresetCatalog.Find(_settings.SelectedPresetId);
        VisualNovelFrameRateComboBox.ItemsSource = FrameRateOptions;
        VisualNovelFrameRateComboBox.SelectedItem = FrameRateOptions.First(option =>
            option.FramesPerSecond == VisualNovelFrameRates.Normalize(_settings.VisualNovelFrameRateLimit));
        AutomationCheckBox.IsChecked = _settings.AutomationEnabled;
        RunAtStartupCheckBox.IsChecked = _startup.IsEnabled();
        StartMinimizedCheckBox.IsChecked = _settings.StartMinimized;
        TextHookCheckBox.IsChecked = _settings.TextHookEnabled;
        TextractorPathBox.Text = string.IsNullOrWhiteSpace(_settings.TextractorDirectory)
            ? TextHookService.ProbeDefaultDirectory() ?? string.Empty
            : _settings.TextractorDirectory;
        HoverTranslationCheckBox.IsChecked = _settings.HoverTranslationEnabled;
        TranslationModeComboBox.ItemsSource = TranslationModes;
        TranslationModeComboBox.SelectedItem = TranslationModes.First(option =>
            option.Id == HoverTranslationModes.NormalizeUserMode(_settings.HoverTranslationMode));
        SmartBackendComboBox.ItemsSource = SmartBackendOptions;
        SmartBackendComboBox.SelectedItem = SmartBackendOptions.First(option =>
            option.Id == SmartDictionaryBackends.Normalize(_settings.SmartDictionaryBackend));
        SmartRankerModelComboBox.ItemsSource = SmartRankerModelOptions;
        SmartRankerModelComboBox.SelectedItem = SmartRankerModelOptions.First(option =>
            option.Id == SmartDictionaryRankerModels.Normalize(
                _settings.SmartDictionaryRankerModel));
        SmartSourceLanguageComboBox.ItemsSource = SmartSourceLanguageOptions;
        SmartSourceLanguageComboBox.SelectedItem = SmartSourceLanguageOptions.First(option =>
            option.Id == SmartDictionarySourceLanguages.Normalize(_settings.SmartDictionarySourceLanguage));
        SmartMemoryPolicyComboBox.ItemsSource = SmartMemoryPolicyOptions;
        SmartMemoryPolicyComboBox.SelectedItem = SmartMemoryPolicyOptions.First(option =>
            option.KeepWarm == _settings.SmartDictionaryKeepWarm);
        SmartPhraseTranslationComboBox.ItemsSource = SmartPhraseTranslationOptions;
        SmartPhraseTranslationComboBox.SelectedItem = SmartPhraseTranslationOptions.First(option =>
            option.Id == SmartPhraseTranslationModes.Normalize(_settings.SmartPhraseTranslationMode));
        SmartPhraseBackendComboBox.ItemsSource = SmartPhraseBackendOptions;
        SmartPhraseBackendComboBox.SelectedItem = SmartPhraseBackendOptions.First(option =>
            option.Id == SmartPhraseTranslationBackends.Normalize(
                _settings.SmartPhraseTranslationBackend));
        UpdateSmartPhraseBackendAvailability();
        HoverDelayComboBox.ItemsSource = HoverDelays;
        HoverDelayComboBox.SelectedItem = HoverDelays.FirstOrDefault(
            option => option.Milliseconds == _settings.HoverTranslationDelayMs) ?? HoverDelays[1];
        UpdateTranslationModePresentation();
        UpdateTextHookPresentation();
        UpdateDpiOptimizationPresentation();
        RefreshProfiles();
        // Control event suppression is only needed while initial values are
        // assigned. Keeping it across asynchronous hardware discovery loses
        // real user changes made after the window becomes interactive.
        _initializing = false;

        _engine.StatusChanged += OnEngineStatusChanged;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += (_, _) => UpdateScaleState();
        _statusTimer.Start();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    public void SetStatus(string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(status));
            return;
        }

        StatusText.Text = status;
    }

    public void SetAutomationUi(bool enabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetAutomationUi(enabled));
            return;
        }

        _initializing = true;
        AutomationCheckBox.IsChecked = enabled;
        _initializing = false;
    }

    public void SetHoverTranslationUi(bool enabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetHoverTranslationUi(enabled));
            return;
        }

        _initializing = true;
        HoverTranslationCheckBox.IsChecked = enabled;
        _initializing = false;
    }

    public void SetTranslationModeUi(string mode)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetTranslationModeUi(mode));
            return;
        }

        string normalized = HoverTranslationModes.NormalizeUserMode(mode);
        _initializing = true;
        TranslationModeComboBox.SelectedItem = TranslationModes.First(option => option.Id == normalized);
        _initializing = false;
        UpdateTranslationModePresentation();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshWindowsAsync();
        HardwareInfo hardware = await _hardware.DetectAsync();
        HardwareSummaryText.Text = hardware.Summary;
        HardwareSummaryText.ToolTip = string.Format("{0}\n{1}", hardware.CpuName, hardware.GpuName);
        NpuExplanationText.Text = hardware.NpuExplanation;
        EngineStatusText.Text = _engine.IsAvailable ? "GPU-движок готов" : "Нужна установка движка";
        UpdatePresetDescription();
        _initializing = false;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        // Light "sandy" theme: keep the DWM title bar light, not dark.
        int darkMode = 0;
        _ = NativeMethods.DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
    }

    private async Task RefreshWindowsAsync()
    {
        nint selectedHandle = (WindowsList.SelectedItem as WindowInfo)?.Handle ?? nint.Zero;
        SetStatus("Обновление списка окон…");
        _allWindows = await Task.Run(_windowCatalog.GetWindows);
        ApplyWindowFilter();
        if (selectedHandle != nint.Zero)
        {
            WindowsList.SelectedItem = _visibleWindows.FirstOrDefault(w => w.Handle == selectedHandle);
        }

        if (WindowsList.SelectedItem is null && _visibleWindows.Count > 0)
        {
            WindowsList.SelectedIndex = 0;
        }

        SetStatus(string.Format("Найдено окон: {0}", _allWindows.Count));
        UpdateDpiOptimizationPresentation();
    }

    private void ApplyWindowFilter()
    {
        string query = WindowSearchBox.Text?.Trim() ?? string.Empty;
        IEnumerable<WindowInfo> filtered = _allWindows;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(w =>
                w.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                w.ProcessName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        _visibleWindows.Clear();
        foreach (WindowInfo window in filtered)
        {
            _visibleWindows.Add(window);
        }

        WindowsEmptyText.Visibility = _visibleWindows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRefreshWindowsClick(object sender, RoutedEventArgs e) => await RefreshWindowsAsync();

    private void OnWindowSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyWindowFilter();
        }
    }

    private void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddProfileButton.IsEnabled = WindowsList.SelectedItem is WindowInfo;
        UpdateDpiOptimizationPresentation();
    }

    private void UpdateDpiOptimizationPresentation()
    {
        if (WindowsList.SelectedItem is not WindowInfo window)
        {
            DpiOptimizationStatusText.Text = "Выберите окно игры слева";
            DpiOptimizationButton.Content = "Оптимизировать";
            DpiOptimizationButton.IsEnabled = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(window.ProcessPath))
        {
            DpiOptimizationStatusText.Text = "Windows не разрешила определить путь к EXE";
            DpiOptimizationButton.Content = "Недоступно";
            DpiOptimizationButton.IsEnabled = false;
            return;
        }

        try
        {
            DpiCompatibilityState state = _dpiCompatibility.GetState(
                _settings,
                window.ProcessPath,
                window.Handle);

            if (state.HasExternalConflict)
            {
                DpiOptimizationStatusText.Text =
                    "Параметры совместимости изменены вне Asteria; проверьте свойства EXE вручную";
                DpiOptimizationButton.Content = "Есть конфликт";
                DpiOptimizationButton.IsEnabled = false;
            }
            else if (state.IsEnabled)
            {
                DpiOptimizationStatusText.Text = state.RuntimeIsDpiAware
                    ? state.CanRestore
                        ? "Оптимизировано: Windows не растягивает исходный кадр"
                        : "Оптимизировано вручную: Windows не растягивает исходный кадр"
                    : "Настройка записана — перезапустите игру, чтобы она вступила в силу";
                DpiOptimizationButton.Content = state.CanRestore ? "Отменить" : "Уже настроено";
                DpiOptimizationButton.IsEnabled = state.CanRestore;
            }
            else if (state.RuntimeIsDpiAware)
            {
                DpiOptimizationStatusText.Text = "Игра уже поддерживает DPI и не требует override";
                DpiOptimizationButton.Content = "Не требуется";
                DpiOptimizationButton.IsEnabled = false;
            }
            else
            {
                DpiOptimizationStatusText.Text = state.IsManaged
                    ? "Настройка была снята вне Asteria — её можно включить снова"
                    : "Для старых игр убирает размытие Windows и заметно снижает нагрузку Magpie";
                DpiOptimizationButton.Content = state.IsManaged ? "Включить снова" : "Оптимизировать";
                DpiOptimizationButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            DpiOptimizationStatusText.Text = ex.Message;
            DpiOptimizationButton.Content = "Недоступно";
            DpiOptimizationButton.IsEnabled = false;
        }
    }

    private async void OnDpiOptimizationClick(object sender, RoutedEventArgs e)
    {
        if (WindowsList.SelectedItem is not WindowInfo window || string.IsNullOrWhiteSpace(window.ProcessPath))
        {
            return;
        }

        try
        {
            DpiCompatibilityState state = _dpiCompatibility.GetState(
                _settings,
                window.ProcessPath,
                window.Handle);
            bool disable = state.CanRestore;
            string prompt = disable
                ? "Вернуть прежние параметры DPI для этой игры? Изменение применится после её перезапуска."
                : "Отключить предварительное DPI-растяжение Windows только для этой игры? Текущую игру потребуется перезапустить.";
            if (System.Windows.MessageBox.Show(
                    this,
                    prompt,
                    "DPI игры",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            DpiOverrideMutation mutation = disable
                ? _dpiCompatibility.Disable(_settings, window.ProcessPath)
                : _dpiCompatibility.Enable(_settings, window.ProcessPath);
            if (!mutation.Changed)
            {
                UpdateDpiOptimizationPresentation();
                return;
            }

            if (!await PersistSettingsAsync())
            {
                mutation.Rollback();
                UpdateDpiOptimizationPresentation();
                return;
            }

            UpdateDpiOptimizationPresentation();
            string result = disable
                ? "Прежние параметры DPI восстановлены. Перезапустите игру."
                : "DPI-оптимизация включена. Закройте и снова запустите игру перед масштабированием.";
            SetStatus(result);
            System.Windows.MessageBox.Show(
                this,
                result,
                "Asteria Scale",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetStatus("DPI игры: " + ex.Message);
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Asteria Scale",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            UpdateDpiOptimizationPresentation();
        }
    }

    private async void OnPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not UpscalePreset preset)
        {
            return;
        }

        _settings.SelectedPresetId = preset.Id;
        UpdatePresetDescription();
        if (!_initializing)
        {
            if (await PersistSettingsAsync())
            {
                SetStatus(string.Format("Выбран пресет: {0}", preset.Name));
            }
        }
    }

    private void UpdatePresetDescription()
    {
        UpscalePreset preset = PresetComboBox.SelectedItem as UpscalePreset ?? PresetCatalog.Default;
        PresetDescriptionText.Text = preset.DetailedDescription;
        PresetPerformanceText.Text = preset.PerformanceHint;
    }

    private async void OnScaleClick(object sender, RoutedEventArgs e)
    {
        if (_engine.IsScaling)
        {
            await RunOperationAsync(() => _engine.StopScalingAsync());
            return;
        }

        await StartScaleAsync(false);
    }

    private async void OnWindowedScaleClick(object sender, RoutedEventArgs e) => await StartScaleAsync(true);

    private async Task StartScaleAsync(bool windowed)
    {
        WindowInfo? target = WindowsList.SelectedItem as WindowInfo;
        if (target is null)
        {
            SetStatus("Выберите окно слева");
            return;
        }

        UpscalePreset preset = PresetComboBox.SelectedItem as UpscalePreset ?? PresetCatalog.Default;
        await RunOperationAsync(() => _engine.ScaleAsync(
            target,
            preset,
            windowed,
            _settings.VisualNovelFrameRateLimit));
    }

    private async void OnVisualNovelFrameRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || VisualNovelFrameRateComboBox.SelectedItem is not FrameRateOption option)
        {
            return;
        }

        _settings.VisualNovelFrameRateLimit = option.FramesPerSecond;
        if (await PersistSettingsAsync())
        {
            SetStatus(string.Format(
                "Лимит VN: {0} FPS — применится при следующем запуске масштабирования",
                option.FramesPerSecond));
        }
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (_operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        ScaleButton.IsEnabled = false;
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            System.Windows.MessageBox.Show(this, ex.Message, "Asteria Scale", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _operationInProgress = false;
            ScaleButton.IsEnabled = true;
            UpdateScaleState();
        }
    }

    private async void OnAddProfileClick(object sender, RoutedEventArgs e)
    {
        if (WindowsList.SelectedItem is not WindowInfo window)
        {
            return;
        }

        UpscalePreset preset = PresetComboBox.SelectedItem as UpscalePreset ?? PresetCatalog.Default;
        AutomationProfile? existing = _settings.Profiles.FirstOrDefault(p =>
            (!string.IsNullOrWhiteSpace(window.ProcessPath) &&
             string.Equals(p.ProcessPath, window.ProcessPath, StringComparison.OrdinalIgnoreCase)) ||
            (string.IsNullOrWhiteSpace(window.ProcessPath) &&
             string.Equals(p.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase)));

        if (existing is null)
        {
            _settings.Profiles.Add(new AutomationProfile
            {
                Name = window.ProcessName,
                ProcessName = window.ProcessName,
                ProcessPath = window.ProcessPath,
                ClassName = window.ClassName,
                PresetId = preset.Id,
                Windowed = false
            });
        }
        else
        {
            existing.Name = window.ProcessName;
            existing.ProcessName = window.ProcessName;
            existing.ProcessPath = window.ProcessPath;
            existing.ClassName = window.ClassName;
            existing.PresetId = preset.Id;
            existing.Enabled = true;
        }

        _settings.AutomationEnabled = true;
        SetAutomationUi(true);
        App.Current.Tray?.SetAutomationChecked(true);
        RefreshProfiles();
        if (await PersistSettingsAsync())
        {
            SetStatus(string.Format("Автопрофиль сохранён: {0}", window.ProcessName));
        }
    }

    private async void OnRemoveProfileClick(object sender, RoutedEventArgs e)
    {
        AutomationProfile? profile = (sender as FrameworkElement)?.Tag as AutomationProfile
            ?? ProfilesList.SelectedItem as AutomationProfile;
        if (profile is null)
        {
            SetStatus("Выберите профиль для удаления");
            return;
        }

        _settings.Profiles.RemoveAll(p => p.Id == profile.Id);
        RefreshProfiles();
        if (await PersistSettingsAsync())
        {
            SetStatus(string.Format("Профиль удалён: {0}", profile.Name));
        }
    }

    private async void OnProfilePresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || sender is not System.Windows.Controls.ComboBox
            {
                DataContext: AutomationProfile profile,
                SelectedItem: UpscalePreset preset
            } || string.Equals(profile.PresetId, preset.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        profile.PresetId = preset.Id;
        if (await PersistSettingsAsync())
        {
            SetStatus(string.Format("{0}: выбран режим {1}", profile.Name, preset.Name));
        }
    }

    private void RefreshProfiles()
    {
        _profiles.Clear();
        foreach (AutomationProfile profile in _settings.Profiles)
        {
            _profiles.Add(profile);
        }
    }

    private async void OnAutomationChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.AutomationEnabled = AutomationCheckBox.IsChecked == true;
        App.Current.Tray?.SetAutomationChecked(_settings.AutomationEnabled);
        if (await PersistSettingsAsync())
        {
            SetStatus(_settings.AutomationEnabled ? "Автоматизация включена" : "Автоматизация приостановлена");
        }
    }

    private async void OnHoverTranslationChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        bool enabled = HoverTranslationCheckBox.IsChecked == true;
        _settings.HoverTranslationEnabled = enabled;
        _hoverTranslation.SetEnabled(enabled);
        App.Current.Tray?.SetHoverTranslationChecked(enabled);
        if (await PersistSettingsAsync())
        {
            SetStatus(enabled
                ? "Перевод под курсором включён — локальная модель подготавливается"
                : "Перевод текста выключен");
        }
    }

    private async void OnHoverDelayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || HoverDelayComboBox.SelectedItem is not HoverDelayOption option)
        {
            return;
        }

        _settings.HoverTranslationDelayMs = option.Milliseconds;
        await PersistSettingsAsync();
    }

    private async void OnSmartDictionarySettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing ||
            SmartBackendComboBox.SelectedItem is not TranslationSettingOption backend ||
            SmartRankerModelComboBox.SelectedItem is not TranslationSettingOption rankerModel ||
            SmartSourceLanguageComboBox.SelectedItem is not TranslationSettingOption sourceLanguage ||
            SmartMemoryPolicyComboBox.SelectedItem is not MemoryPolicyOption memoryPolicy ||
            SmartPhraseTranslationComboBox.SelectedItem is not TranslationSettingOption phraseTranslation ||
            SmartPhraseBackendComboBox.SelectedItem is not TranslationSettingOption phraseBackend)
        {
            return;
        }

        if (rankerModel.Id == SmartDictionaryRankerModels.Consec &&
            backend.Id == SmartDictionaryBackends.Npu)
        {
            backend = SmartBackendOptions.First(option =>
                option.Id == SmartDictionaryBackends.Cpu);
            _initializing = true;
            SmartBackendComboBox.SelectedItem = backend;
            _initializing = false;
            SetStatus("ConSeC работает на CPU — ускоритель явно переключён на CPU");
        }

        _settings.SmartDictionaryBackend = SmartDictionaryBackends.Normalize(backend.Id);
        _settings.SmartDictionaryRankerModel =
            SmartDictionaryRankerModels.Normalize(rankerModel.Id);
        _settings.SmartDictionarySourceLanguage =
            SmartDictionarySourceLanguages.Normalize(sourceLanguage.Id);
        _settings.SmartDictionaryKeepWarm = memoryPolicy.KeepWarm;
        _settings.SmartPhraseTranslationMode =
            SmartPhraseTranslationModes.Normalize(phraseTranslation.Id);
        _settings.SmartPhraseTranslationBackend =
            SmartPhraseTranslationBackends.Normalize(phraseBackend.Id);
        UpdateSmartPhraseBackendAvailability();
        _hoverTranslation.ApplySmartDictionarySettings();
        UpdateSmartDictionarySettingsHint();

        if (await PersistSettingsAsync())
        {
            SetStatus("Настройки умного словаря сохранены");
        }
    }

    private async void OnTranslationModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || TranslationModeComboBox.SelectedItem is not TranslationModeOption option)
        {
            return;
        }

        _hoverTranslation.SetMode(option.Id);
        App.Current.Tray?.SetTranslationMode(option.Id);
        UpdateTranslationModePresentation();
        if (await PersistSettingsAsync())
        {
            SetStatus(option.Id switch
            {
                HoverTranslationModes.Smart => "Умный словарь включён — ускоритель и язык можно настроить ниже",
                HoverTranslationModes.Dictionary => "Быстрый словарный перевод включён — без контекстной модели и перевода предложения",
                _ => "Умный словарь включён"
            });
        }
    }

    private void UpdateTranslationModePresentation()
    {
        string mode = TranslationModeComboBox.SelectedItem is TranslationModeOption option
            ? HoverTranslationModes.NormalizeUserMode(option.Id)
            : HoverTranslationModes.NormalizeUserMode(_settings.HoverTranslationMode);
        bool smart = mode == HoverTranslationModes.Smart;
        HoverDelayPanel.Visibility = Visibility.Visible;
        SmartDictionarySettingsPanel.Visibility = smart ? Visibility.Visible : Visibility.Collapsed;
        TranslationDescriptionText.Text = mode switch
        {
            HoverTranslationModes.Smart =>
                "Наведите мышь на слово — словарь покажет значение, а TranslateGemma добавит перевод полного предложения второй строкой",
            HoverTranslationModes.Dictionary =>
                "Задержите мышь над словом или выражением — быстрый перевод появится рядом",
            _ => "Наведите мышь на слово — словарь покажет перевод по контексту"
        };
        UpdateSmartDictionarySettingsHint();
    }

    private void UpdateSmartDictionarySettingsHint()
    {
        string backend = SmartDictionaryBackends.Normalize(_settings.SmartDictionaryBackend) switch
        {
            SmartDictionaryBackends.Cpu => "CPU — эталонная FP32-точность; использует больше оперативной памяти",
            SmartDictionaryBackends.Npu => "NPU ответит быстрее, использует меньше RAM и оставит GPU свободным",
            _ => "автоматически используется рекомендуемый CPU"
        };
        string ranker = SmartDictionaryRankerModels.Normalize(
            _settings.SmartDictionaryRankerModel) switch
        {
            SmartDictionaryRankerModels.Consec =>
                "модель: ConSeC (научный исследовательский CPU-профиль, около 1,5 ГБ RAM)",
            _ => "модель: GlossBERT"
        };
        string language = SmartDictionarySourceLanguages.Normalize(_settings.SmartDictionarySourceLanguage) switch
        {
            SmartDictionarySourceLanguages.English => "язык: английский",
            SmartDictionarySourceLanguages.Japanese => "язык: японский",
            _ => "язык определяется автоматически"
        };
        string memory = _settings.SmartDictionaryKeepWarm
            ? "модель остаётся готовой, пока режим включён"
            : "при простое модель освобождает память";
        string phrase = SmartPhraseTranslationModes.Normalize(_settings.SmartPhraseTranslationMode) ==
                        SmartPhraseTranslationModes.TranslateGemma
            ? $"TranslateGemma независимо переводит полное EN→RU предложение второй строкой на {SmartPhraseBackendDisplayName()}"
            : "показывается только словарный результат";

        SmartDictionarySettingsHint.Text =
            $"{backend}; {ranker}; {language}; {phrase}; {memory}.";
    }

    private void UpdateSmartPhraseBackendAvailability()
    {
        SmartPhraseBackendComboBox.IsEnabled =
            SmartPhraseTranslationModes.Normalize(_settings.SmartPhraseTranslationMode) ==
            SmartPhraseTranslationModes.TranslateGemma;
    }

    private string SmartPhraseBackendDisplayName() =>
        SmartPhraseTranslationBackends.Normalize(_settings.SmartPhraseTranslationBackend) ==
        SmartPhraseTranslationBackends.Vulkan
            ? "Vulkan GPU"
            : "CPU";

    private async void OnRunAtStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        try
        {
            bool enabled = RunAtStartupCheckBox.IsChecked == true;
            _startup.SetEnabled(enabled);
            _settings.RunAtStartup = enabled;
            if (await PersistSettingsAsync())
            {
                SetStatus(enabled ? "Автозапуск Windows включён" : "Автозапуск Windows выключен");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Автозапуск: " + ex.Message);
        }
    }

    private async void OnTextHookChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.TextHookEnabled = TextHookCheckBox.IsChecked == true;
        if (_settings.TextHookEnabled && string.IsNullOrWhiteSpace(TextractorPathBox.Text))
        {
            TextractorPathBox.Text = TextHookService.ProbeDefaultDirectory() ?? string.Empty;
        }

        _settings.TextractorDirectory = TextractorPathBox.Text.Trim();
        UpdateTextHookPresentation();
        _hoverTranslation.ApplyTextHookSettings();
        await PersistSettingsAsync();
    }

    private async void OnTextractorPathChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.TextractorDirectory = TextractorPathBox.Text.Trim();
        UpdateTextHookPresentation();
        _hoverTranslation.ApplyTextHookSettings();
        await PersistSettingsAsync();
    }

    private void UpdateTextHookPresentation()
    {
        string directory = TextractorPathBox.Text.Trim();
        string installation = TextHookService.DescribeInstallation(directory);
        TextHookStatusText.Text = TextHookCheckBox.IsChecked == true
            ? TextHookService.IsTextractorDirectory(directory)
                ? installation + "; подключение к разрешённой игре выполняется автоматически"
                : installation
            : TextHookService.IsTextractorDirectory(directory)
                ? installation + "; включите text-hook, чтобы использовать его вместо OCR-контекста"
                : "Text-hook выключен; используется OCR-контекст";
    }

    private async void OnProfileTextHookChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _hoverTranslation.ApplyTextHookSettings();
        await PersistSettingsAsync();
    }

    private void OnOpenUserDictionaryClick(object sender, RoutedEventArgs e)
    {
        UserDictionaryWindow window = new(
            new UserDictionaryService(),
            _hoverTranslation.History,
            _hoverTranslation.ReloadDictionariesAsync)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async void OnStartMinimizedChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
        await PersistSettingsAsync();
    }

    private async Task<bool> PersistSettingsAsync()
    {
        try
        {
            await App.Current.SaveSettingsTrackedAsync();
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось сохранить настройки: " + ex.Message);
            return false;
        }
    }

    private void OnOpenEngineClick(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsAvailable)
        {
            SetStatus("Magpie не установлен");
            return;
        }

        try
        {
            _engine.ShowSettings();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void OnEngineStatusChanged(object? sender, string status)
    {
        Dispatcher.Invoke(() =>
        {
            EngineStatusText.Text = status;
            SetStatus(status);
        });
    }

    private void UpdateScaleState()
    {
        bool scaling = _engine.IsScaling;
        ScaleButton.Content = scaling ? "Остановить масштабирование" : "Запустить на весь экран";
        EngineStatusText.Text = scaling
            ? "Масштабирование активно"
            : (_engine.IsAvailable ? "GPU-движок готов" : "Движок не найден");
    }

    internal bool IsClosed { get; private set; }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        base.OnClosed(e);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (App.Current.IsExiting)
        {
            return;
        }

        if (!_settings.CloseToTray)
        {
            // ShutdownMode is OnExplicitShutdown, so simply letting the window
            // close would leave a running process with no window and no way
            // back: the tray "Show" command would call Show() on a closed
            // window and throw. Closing with CloseToTray off means quit.
            e.Cancel = true;
            _ = App.Current.ExitApplicationAsync();
            return;
        }

        e.Cancel = true;
        Hide();
        App.Current.ShowTrayHintOnce();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            App.Current.ShowTrayHintOnce();
        }
    }

    private sealed record HoverDelayOption(string Name, int Milliseconds);
    private sealed record TranslationModeOption(string Name, string Id);
    private sealed record TranslationSettingOption(string Name, string Id);
    private sealed record MemoryPolicyOption(string Name, bool KeepWarm);
    private sealed record FrameRateOption(string Name, int FramesPerSecond);
}
