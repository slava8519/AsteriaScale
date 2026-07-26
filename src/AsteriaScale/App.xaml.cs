using System.Threading;
using System.Globalization;
using System.Windows;
using AsteriaScale.Interop;
using AsteriaScale.Models;
using AsteriaScale.Services;

namespace AsteriaScale;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName =
        "Local\\AsteriaScale-0D67E2AA-AC2A-4D77-92B7-45F61A43FE96";
    private const string ShowRequestEventName =
        "Local\\AsteriaScale-Show-0D67E2AA-AC2A-4D77-92B7-45F61A43FE96";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showRequestSignal;
    private readonly ManualResetEvent _showRequestListenerStop = new(false);
    private readonly object _settingsSaveSync = new();
    private readonly HashSet<Task> _pendingSettingsSaves = [];

    public new static App Current => (App)System.Windows.Application.Current;

    public AppSettings Settings { get; private set; } = null!;
    public SettingsService SettingsService { get; private set; } = null!;
    public WindowCatalogService WindowCatalog { get; private set; } = null!;
    public MagpieEngineService Engine { get; private set; } = null!;
    public AutoScaleService AutoScale { get; private set; } = null!;
    public HoverTranslationService HoverTranslation { get; private set; } = null!;
    public StartupService StartupManager { get; private set; } = null!;
    public TrayIconService? Tray { get; private set; }
    public MainWindow? AsteriaWindow { get; private set; }
    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(arg => string.Equals(arg, "--npu-selector-worker", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await NpuDictionarySelectorService.RunWorkerAsync(
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput());
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }

            return;
        }

        int rankerDiagnosticArgument = Array.FindIndex(e.Args, arg =>
            string.Equals(arg, "--ranker-diagnostic", StringComparison.OrdinalIgnoreCase));
        if (rankerDiagnosticArgument >= 0)
        {
            int exitCode = await RunRankerDiagnosticAsync(
                e.Args,
                rankerDiagnosticArgument);
            Shutdown(exitCode);
            return;
        }

        int overlayDiagnosticArgument = Array.FindIndex(e.Args, arg =>
            string.Equals(arg, "--overlay-diagnostic", StringComparison.OrdinalIgnoreCase));
        if (overlayDiagnosticArgument >= 0)
        {
            await RunOverlayDiagnosticAsync(e.Args, overlayDiagnosticArgument);
            Shutdown();
            return;
        }

        int screenDiagnosticArgument = Array.FindIndex(e.Args, arg =>
            string.Equals(arg, "--screen-ocr-diagnostic", StringComparison.OrdinalIgnoreCase));
        if (screenDiagnosticArgument >= 0)
        {
            await RunScreenOcrDiagnosticAsync(e.Args, screenDiagnosticArgument);
            Shutdown();
            return;
        }

        int diagnosticArgument = Array.FindIndex(e.Args, arg =>
            string.Equals(arg, "--ocr-diagnostic", StringComparison.OrdinalIgnoreCase));
        if (diagnosticArgument >= 0)
        {
            await RunOcrDiagnosticAsync(e.Args, diagnosticArgument);
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isFirst);
        if (!isFirst)
        {
            // Launching again is a request to see the running instance, not a
            // no-op: the window may be hidden in the tray, so without this the
            // user clicks the shortcut and nothing whatsoever happens.
            if (EventWaitHandle.TryOpenExisting(
                    ShowRequestEventName,
                    out EventWaitHandle? running))
            {
                using (running)
                {
                    running.Set();
                }
            }

            Shutdown();
            return;
        }

        _showRequestSignal = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ShowRequestEventName);
        StartShowRequestListener(_showRequestSignal);

        try
        {
            SettingsService = new SettingsService();
            Settings = await SettingsService.LoadAsync();
            WindowCatalog = new WindowCatalogService();
            Engine = new MagpieEngineService();
            StartupManager = new StartupService();
            AutoScale = new AutoScaleService(Settings, WindowCatalog, Engine);
            HoverTranslation = new HoverTranslationService(Settings, () => Engine.IsScaling);

            AsteriaWindow = new MainWindow(
                Settings,
                WindowCatalog,
                Engine,
                StartupManager,
                HoverTranslation);
            MainWindow = AsteriaWindow;

            Tray = new TrayIconService(
                Settings.AutomationEnabled,
                Settings.HoverTranslationEnabled,
                Settings.HoverTranslationMode,
                ShowMainWindow,
                ScaleLastWindowAsync,
                SetAutomation,
                SetHoverTranslation,
                SetHoverTranslationMode,
                () => _ = ExitApplicationAsync());

            AutoScale.StatusChanged += (_, status) => AsteriaWindow?.SetStatus(status);
            HoverTranslation.StatusChanged += (_, status) => AsteriaWindow?.SetStatus(status);
            AutoScale.Start();
            HoverTranslation.Start();

            bool commandLineMinimized = e.Args.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));
            bool commandLineShow = e.Args.Any(arg =>
                string.Equals(arg, "--show", StringComparison.OrdinalIgnoreCase));
            if (commandLineShow || (!commandLineMinimized && !Settings.StartMinimized))
            {
                AsteriaWindow.Show();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "Не удалось запустить Asteria Scale:\n" + ex.Message,
                "Asteria Scale",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static async Task<int> RunRankerDiagnosticAsync(
        string[] arguments,
        int argumentIndex)
    {
        string outputPath = arguments.Length > argumentIndex + 1
            ? arguments[argumentIndex + 1]
            : Path.Combine(Path.GetTempPath(), "asteria-ranker-diagnostic.json");
        string providerArgument = arguments.Length > argumentIndex + 2
            ? arguments[argumentIndex + 2]
            : "cpu";
        GlossBertRankerProvider provider =
            string.Equals(providerArgument, "npu", StringComparison.OrdinalIgnoreCase)
                ? GlossBertRankerProvider.Npu
                : GlossBertRankerProvider.Cpu;

        try
        {
            await using GlossBertDictionarySelectorService selector = new();
            await selector.WarmUpAsync(provider);

            string companionBaseDirectory =
                GlossBertDictionarySelectorService.ResolveCompanionBaseDirectory(
                    Environment.ProcessPath,
                    AppContext.BaseDirectory);
            string workerPath = Path.Combine(
                companionBaseDirectory,
                "Ranker",
                "AsteriaScale.RankerWorker.exe");
            bool success = selector.IsWorkerRunning && File.Exists(workerPath);
            var result = new
            {
                success,
                provider = selector.ActiveProvider?.ToString(),
                modelVersion = selector.ActiveModelVersion,
                workerProcessId = selector.WorkerProcessId,
                processPath = Environment.ProcessPath,
                appContextBaseDirectory = AppContext.BaseDirectory,
                companionBaseDirectory,
                workerPath,
                workerExists = File.Exists(workerPath)
            };

            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(
                outputPath,
                System.Text.Json.JsonSerializer.Serialize(
                    result,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true
                    }),
                System.Text.Encoding.UTF8);
            return success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(
                outputPath,
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        success = false,
                        processPath = Environment.ProcessPath,
                        appContextBaseDirectory = AppContext.BaseDirectory,
                        error = ex.ToString()
                    },
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true
                    }),
                System.Text.Encoding.UTF8);
            return 1;
        }
    }

    private static async Task RunOverlayDiagnosticAsync(string[] arguments, int argumentIndex)
    {
        if (arguments.Length <= argumentIndex + 1)
        {
            throw new ArgumentException("Usage: --overlay-diagnostic <output.png> [dictionary|neural|missing]");
        }

        string outputPath = arguments[argumentIndex + 1];
        string mode = arguments.Length > argumentIndex + 2
            ? arguments[argumentIndex + 2]
            : "dictionary";
        HoverTranslationOverlay overlay = new();
        NativeMethods.Point anchor = new() { X = 900, Y = 700 };
        if (string.Equals(mode, "neural", StringComparison.OrdinalIgnoreCase))
        {
            overlay.ShowTranslation(
                "That is of little significance. More importantly, you should be able to enter this establishment.",
                "Это не имеет большого значения. Гораздо важнее, что вы сможете войти в это заведение.",
                anchor,
                FoundryTranslationService.EngineLabel,
                language: "EN");
        }
        else
        {
            bool found = !string.Equals(mode, "missing", StringComparison.OrdinalIgnoreCase);
            overlay.ShowTranslation(
                "You",
                found ? "вы; ты; вам; вас; вами; выкать" : "Нет записи в локальном словаре",
                anchor,
                LocalDictionaryService.EngineLabel,
                dictionaryFound: found,
                language: "EN");
        }

        await Task.Delay(350);
        nint handle = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
        if (!NativeMethods.GetWindowRect(handle, out NativeMethods.Rect bounds))
        {
            overlay.Close();
            throw new InvalidOperationException("Could not read overlay bounds.");
        }

        int width = Math.Max(1, bounds.Right - bounds.Left);
        int height = Math.Max(1, bounds.Bottom - bounds.Top);
        using System.Drawing.Bitmap bitmap = new(width, height);
        using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(width, height));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        overlay.Close();
    }

    private static async Task RunOcrDiagnosticAsync(string[] arguments, int argumentIndex)
    {
        string outputPath = arguments.Length > argumentIndex + 4
            ? arguments[argumentIndex + 4]
            : Path.Combine(Path.GetTempPath(), "asteria-ocr-diagnostic.txt");
        try
        {
            if (arguments.Length <= argumentIndex + 3)
            {
                throw new ArgumentException(
                    "Usage: --ocr-diagnostic <image> <cursor-x> <cursor-y> [output]");
            }

            string imagePath = arguments[argumentIndex + 1];
            int cursorX = int.Parse(arguments[argumentIndex + 2], CultureInfo.InvariantCulture);
            int cursorY = int.Parse(arguments[argumentIndex + 3], CultureInfo.InvariantCulture);
            using WindowsAiOcrService ocr = new();
            HoverOcrDiagnostic diagnostic = await ocr.DiagnoseImageNearPointAsync(
                imagePath,
                cursorX,
                cursorY,
                sentenceContext: arguments.Any(arg =>
                    string.Equals(arg, "--sentence-context", StringComparison.OrdinalIgnoreCase)));
            string selection = diagnostic.Selection is null
                ? "<none>"
                : $"{diagnostic.Selection.HoveredWord} | {diagnostic.Selection.Text} | index={diagnostic.Selection.CharacterIndex}";
            await File.WriteAllTextAsync(
                outputPath,
                diagnostic.Details + Environment.NewLine + "SELECTION: " + selection,
                System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(outputPath, ex.ToString(), System.Text.Encoding.UTF8);
        }
    }

    private static async Task RunScreenOcrDiagnosticAsync(string[] arguments, int argumentIndex)
    {
        string outputPath = arguments.Length > argumentIndex + 2
            ? arguments[argumentIndex + 2]
            : Path.Combine(Path.GetTempPath(), "asteria-screen-ocr-diagnostic.txt");
        try
        {
            if (arguments.Length <= argumentIndex + 1)
            {
                throw new ArgumentException("Usage: --screen-ocr-diagnostic <image> [output]");
            }

            using WindowsAiOcrService ocr = new();
            ScreenOcrResult result = await ocr.RecognizeImageBlocksAsync(arguments[argumentIndex + 1]);
            System.Text.StringBuilder details = new();
            details.AppendLine($"screen={result.CaptureBounds}; blocks={result.Blocks.Count}");
            foreach (ScreenOcrBlock block in result.Blocks)
            {
                details.AppendLine($"{block.Bounds}; lines={block.LineCount}; {block.Text}");
            }

            await File.WriteAllTextAsync(outputPath, details.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(outputPath, ex.ToString(), System.Text.Encoding.UTF8);
        }
    }

    /// <summary>
    /// Waits for another launch to ask for the window. A background thread, so
    /// process exit can never be held open by it; the handles are reclaimed by
    /// teardown rather than disposed out from under the wait.
    /// </summary>
    private void StartShowRequestListener(EventWaitHandle signal)
    {
        Thread listener = new(() =>
        {
            WaitHandle[] handles = [signal, _showRequestListenerStop];
            while (WaitHandle.WaitAny(handles) == 0)
            {
                Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            }
        })
        {
            IsBackground = true,
            Name = "AsteriaScale show-request listener"
        };
        listener.Start();
    }

    public void ShowMainWindow()
    {
        if (AsteriaWindow is null || AsteriaWindow.IsClosed)
        {
            return;
        }

        AsteriaWindow.Show();
        if (AsteriaWindow.WindowState == WindowState.Minimized)
        {
            AsteriaWindow.WindowState = WindowState.Normal;
        }

        AsteriaWindow.Activate();
        AsteriaWindow.Topmost = true;
        AsteriaWindow.Topmost = false;
        AsteriaWindow.Focus();
    }

    public async Task ScaleLastWindowAsync()
    {
        WindowInfo? window = AutoScale.LastEligibleWindow ?? WindowCatalog.GetForegroundEligibleWindow();
        if (window is null)
        {
            throw new InvalidOperationException("Сначала перейдите в окно, которое нужно масштабировать.");
        }

        UpscalePreset preset = PresetCatalog.Find(Settings.SelectedPresetId);
        await Engine.ScaleAsync(window, preset, false, Settings.VisualNovelFrameRateLimit);
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            await SaveSettingsTrackedAsync();
        }
        catch (Exception ex)
        {
            AsteriaWindow?.SetStatus("Не удалось сохранить настройки: " + ex.Message);
        }
    }

    internal Task SaveSettingsTrackedAsync()
    {
        Task save;
        lock (_settingsSaveSync)
        {
            if (IsExiting)
            {
                return Task.CompletedTask;
            }

            save = SettingsService.SaveAsync(Settings);
            _pendingSettingsSaves.Add(save);
        }

        _ = RemoveCompletedSettingsSaveAsync(save);
        return save;
    }

    public void ShowTrayHintOnce()
    {
        if (!Settings.ShowFirstTrayHint || Tray is null)
        {
            return;
        }

        Settings.ShowFirstTrayHint = false;
        Tray.ShowHint("Asteria Scale работает в фоне", "Дважды щёлкните значок в трее, чтобы открыть окно.");
        QueueSettingsSave();
    }

    private void SetAutomation(bool enabled)
    {
        Settings.AutomationEnabled = enabled;
        AsteriaWindow?.SetAutomationUi(enabled);
        QueueSettingsSave();
    }

    private void SetHoverTranslation(bool enabled)
    {
        Settings.HoverTranslationEnabled = enabled;
        HoverTranslation.SetEnabled(enabled);
        AsteriaWindow?.SetHoverTranslationUi(enabled);
        QueueSettingsSave();
    }

    private void SetHoverTranslationMode(string mode)
    {
        string normalized = HoverTranslationModes.Normalize(mode);
        HoverTranslation.SetMode(normalized);
        AsteriaWindow?.SetTranslationModeUi(normalized);
        Tray?.SetTranslationMode(normalized);
        QueueSettingsSave();
    }

    private void QueueSettingsSave()
    {
        _ = SaveSettingsAsync();
    }

    private async Task RemoveCompletedSettingsSaveAsync(Task save)
    {
        try
        {
            await save;
        }
        catch
        {
            // The initiating UI path reports the error; tracking must still drain.
        }
        finally
        {
            lock (_settingsSaveSync)
            {
                _pendingSettingsSaves.Remove(save);
            }
        }
    }

    public async Task ExitApplicationAsync()
    {
        Task[] pendingSettingsSaves;
        lock (_settingsSaveSync)
        {
            if (IsExiting)
            {
                return;
            }

            IsExiting = true;
            pendingSettingsSaves = [.. _pendingSettingsSaves];
        }

        Tray?.Dispose();
        Tray = null;
        await AutoScale.DisposeAsync();
        try
        {
            await Task.WhenAll(pendingSettingsSaves);
        }
        catch
        {
            // A failed settings write was already reported and cannot block exit.
        }

        try
        {
            await HoverTranslation.DisposeAsync();
        }
        catch
        {
            // Shutdown must remain possible if a local model is being unloaded.
        }

        try
        {
            await Engine.DisposeAsync();
        }
        catch
        {
            // Shutdown must remain possible even if the external engine is already gone.
        }

        _showRequestListenerStop.Set();
        _showRequestSignal = null;
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        Shutdown();
    }
}
