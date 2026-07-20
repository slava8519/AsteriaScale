using System.Diagnostics;
using AsteriaScale.Interop;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class MagpieEngineService : IAsyncDisposable
{
    private const string MainWindowClass = "Magpie_Main";
    private const string ScalingWindowClass = "Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22";
    private const string QuitMessage = "WM_MAGPIE_QUIT";

    private readonly MagpieConfigEditor _configEditor;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly string _configPath;
    private Process? _ownedProcess;
    private string? _activePresetId;
    private int _activeVisualNovelFrameRateLimit;

    public MagpieEngineService(
        MagpieConfigEditor? configEditor = null,
        string? enginePath = null,
        string? configPath = null)
    {
        _configEditor = configEditor ?? new MagpieConfigEditor();
        EnginePath = enginePath ?? FindEnginePath();
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Magpie",
            "config",
            "v4",
            "config.json");
    }

    public string EnginePath { get; }
    public string ConfigPath => _configPath;
    public bool IsAvailable => !string.IsNullOrWhiteSpace(EnginePath) && File.Exists(EnginePath);
    public bool IsRunning
    {
        get
        {
            Process[] processes = Process.GetProcessesByName("Magpie");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }
    }
    public bool IsScaling => NativeMethods.FindWindow(ScalingWindowClass, null) != nint.Zero;
    public string EffectsDirectory => IsAvailable
        ? Path.Combine(Path.GetDirectoryName(EnginePath)!, "effects")
        : string.Empty;

    public event EventHandler<string>? StatusChanged;

    public async Task ScaleAsync(
        WindowInfo target,
        UpscalePreset preset,
        bool windowed,
        int visualNovelFrameRateLimit = VisualNovelFrameRates.Default,
        CancellationToken cancellationToken = default)
    {
        if (!NativeMethods.IsWindow(target.Handle))
        {
            throw new InvalidOperationException("Выбранное окно уже закрыто.");
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureReadyCoreAsync(preset, visualNovelFrameRateLimit, cancellationToken);
            Report(string.Format("Фокус: {0}", target.Title));
            NativeMethods.FocusWindow(target.Handle);
            await Task.Delay(280, cancellationToken);
            NativeMethods.TriggerMagpieAction(windowed);
            Report(windowed ? "Запущено оконное масштабирование" : "Запущено полноэкранное масштабирование");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopScalingAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsScaling)
            {
                return;
            }

            nint scalingWindow = NativeMethods.FindWindow(ScalingWindowClass, null);
            NativeMethods.FocusWindow(scalingWindow);
            await Task.Delay(80, cancellationToken);
            NativeMethods.TriggerMagpieAction(false);
            Report("Масштабирование остановлено");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task EnsureReadyAsync(
        UpscalePreset preset,
        int visualNovelFrameRateLimit = VisualNovelFrameRates.Default,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            InstallBundledEffects();
            await EnsureReadyCoreAsync(preset, visualNovelFrameRateLimit, cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Copies effects shipped with Asteria (currently the MIT ArtCNN port)
    /// into Magpie's effects directory. Idempotent by size; a read-only
    /// install location fails soft with a status hint instead of blocking.
    /// </summary>
    private void InstallBundledEffects()
    {
        string bundledDirectory = Path.Combine(AppContext.BaseDirectory, "Effects");
        string effectsDirectory = EffectsDirectory;
        if (!IsAvailable ||
            !Directory.Exists(bundledDirectory) ||
            string.IsNullOrEmpty(effectsDirectory))
        {
            return;
        }

        foreach (string source in Directory.EnumerateFiles(bundledDirectory, "*.hlsl"))
        {
            string destination = Path.Combine(effectsDirectory, Path.GetFileName(source));
            try
            {
                if (File.Exists(destination) &&
                    new FileInfo(destination).Length == new FileInfo(source).Length)
                {
                    continue;
                }

                File.Copy(source, destination, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or
                                              UnauthorizedAccessException)
            {
                StatusChanged?.Invoke(
                    this,
                    $"Не удалось установить эффект {Path.GetFileName(source)} в Magpie: {exception.Message}");
            }
        }
    }

    public void ShowSettings()
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException("Движок Magpie не найден.");
        }

        nint window = NativeMethods.FindWindow(MainWindowClass, null);
        if (window != nint.Zero)
        {
            NativeMethods.ShowWindow(window, NativeMethods.SwShow);
            NativeMethods.FocusWindow(window);
            return;
        }

        Process.Start(new ProcessStartInfo(EnginePath)
        {
            WorkingDirectory = Path.GetDirectoryName(EnginePath)!,
            UseShellExecute = true
        });
    }

    public async Task StopEngineAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            _ownedProcess?.Dispose();
            _ownedProcess = null;
            _activePresetId = null;
            _activeVisualNovelFrameRateLimit = 0;
            return;
        }

        uint quit = NativeMethods.RegisterWindowMessage(QuitMessage);
        NativeMethods.PostMessage(NativeMethods.HwndBroadcast, quit, nint.Zero, nint.Zero);

        Stopwatch timer = Stopwatch.StartNew();
        while (IsRunning && timer.Elapsed < TimeSpan.FromSeconds(4))
        {
            await Task.Delay(80, cancellationToken);
        }

        if (IsRunning && _ownedProcess is { HasExited: false })
        {
            _ownedProcess.Kill(true);
            await _ownedProcess.WaitForExitAsync(cancellationToken);
        }

        _ownedProcess?.Dispose();
        _ownedProcess = null;
        _activePresetId = null;
        _activeVisualNovelFrameRateLimit = 0;
    }

    private async Task EnsureReadyCoreAsync(
        UpscalePreset preset,
        int visualNovelFrameRateLimit,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException(
                "Движок Magpie не найден. Запустите scripts\\install.ps1 или установите Blinue.Magpie через winget.");
        }

        IReadOnlyList<string> missingEffects = _configEditor.ValidateEffects(EffectsDirectory, preset);
        if (missingEffects.Count > 0)
        {
            throw new InvalidOperationException("В движке отсутствуют эффекты: " + string.Join(", ", missingEffects));
        }

        int effectiveFrameRateLimit = preset.IsVisualNovel
            ? VisualNovelFrameRates.Normalize(visualNovelFrameRateLimit)
            : 0;
        if (IsRunning &&
            string.Equals(_activePresetId, preset.Id, StringComparison.Ordinal) &&
            _activeVisualNovelFrameRateLimit == effectiveFrameRateLimit)
        {
            HideEngineWindow();
            return;
        }

        Report("Подготовка GPU-движка…");
        await StopEngineAsync(cancellationToken);
        await BackupConfigOnceAsync(cancellationToken);
        await _configEditor.ApplyPresetAsync(
            _configPath,
            preset,
            true,
            effectiveFrameRateLimit == 0 ? VisualNovelFrameRates.Default : effectiveFrameRateLimit,
            cancellationToken);

        ProcessStartInfo startInfo = new(EnginePath)
        {
            WorkingDirectory = Path.GetDirectoryName(EnginePath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        _ownedProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить Magpie.");

        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_ownedProcess.HasExited)
            {
                throw new InvalidOperationException("Magpie завершился во время запуска.");
            }

            nint window = NativeMethods.FindWindow(MainWindowClass, null);
            if (window != nint.Zero && NativeMethods.IsWindowVisible(window))
            {
                NativeMethods.ShowWindow(window, NativeMethods.SwHide);
                _activePresetId = preset.Id;
                _activeVisualNovelFrameRateLimit = effectiveFrameRateLimit;
                Report(string.Format("Готово: {0}", preset.Name));
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        HideEngineWindow();
        _activePresetId = preset.Id;
        _activeVisualNovelFrameRateLimit = effectiveFrameRateLimit;
        Report(string.Format("Готово: {0}", preset.Name));
    }

    private void HideEngineWindow()
    {
        nint window = NativeMethods.FindWindow(MainWindowClass, null);
        if (window != nint.Zero)
        {
            NativeMethods.ShowWindow(window, NativeMethods.SwHide);
        }
    }

    private async Task BackupConfigOnceAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configPath))
        {
            return;
        }

        string backup = _configPath + ".before-asteria.json";
        if (!File.Exists(backup))
        {
            await using FileStream source = File.OpenRead(_configPath);
            await using FileStream destination = File.Create(backup);
            await source.CopyToAsync(destination, cancellationToken);
        }
    }

    private void Report(string status) => StatusChanged?.Invoke(this, status);

    private static string FindEnginePath()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("ASTERIA_MAGPIE_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        string bundled = Path.Combine(AppContext.BaseDirectory, "engine", "Magpie.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        string packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WinGet",
            "Packages");
        if (Directory.Exists(packages))
        {
            string? found = Directory.EnumerateDirectories(packages, "Blinue.Magpie*")
                .Select(directory => Path.Combine(directory, "Magpie.exe"))
                .FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string segment in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(segment.Trim(), "Magpie.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        await StopEngineAsync();
        _operationLock.Dispose();
    }
}
