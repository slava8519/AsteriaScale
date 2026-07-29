using System.Diagnostics;
using AsteriaScale.Interop;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class MagpieEngineService : IAsyncDisposable
{
    private const string MainWindowClass = "Magpie_Main";
    private const string ScalingWindowClass = "Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22";
    private const string QuitMessage = "WM_MAGPIE_QUIT";
    private static readonly Version MinimumMagpieVersion = new(0, 12, 1, 0);

    private readonly MagpieConfigEditor _configEditor;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly string _configPath;
    private Process? _ownedProcess;
    private string? _activePresetId;
    private int _activeVisualNovelFrameRateLimit;
    private bool _disposed;

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
    public bool IsScaling
    {
        get
        {
            nint scalingWindow = FindOwnedWindow(ScalingWindowClass);
            // Magpie creates this HWND before renderer initialization and publishes
            // source properties only immediately before showing the first frame.
            return IsScalingWindowActiveState(
                scalingWindow,
                scalingWindow != nint.Zero && NativeMethods.IsWindowVisible(scalingWindow));
        }
    }
    internal bool IsScalingSourceLost
    {
        get
        {
            nint scalingWindow = FindOwnedWindow(ScalingWindowClass);
            if (!IsScalingWindowActiveState(
                    scalingWindow,
                    scalingWindow != nint.Zero && NativeMethods.IsWindowVisible(scalingWindow)))
            {
                return false;
            }

            nint sourceWindow = NativeMethods.GetProp(scalingWindow, "Magpie.SrcHWND");
            bool isWindow = sourceWindow != nint.Zero && NativeMethods.IsWindow(sourceWindow);
            return IsScalingSourceLostState(
                sourceWindow,
                isWindow,
                isWindow && NativeMethods.IsIconic(sourceWindow),
                isWindow && NativeMethods.IsCloaked(sourceWindow));
        }
    }
    internal bool IsScalingSourceForeground
    {
        get
        {
            nint scalingWindow = FindOwnedWindow(ScalingWindowClass);
            if (!IsScalingWindowActiveState(
                    scalingWindow,
                    scalingWindow != nint.Zero && NativeMethods.IsWindowVisible(scalingWindow)))
            {
                return false;
            }

            return IsScalingSourceForegroundState(
                NativeMethods.GetProp(scalingWindow, "Magpie.SrcHWND"),
                NativeMethods.GetForegroundWindow());
        }
    }
    internal bool IsOwnedEngineRunning => IsOwnedProcessRunning();
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
        ValidateTargetWindow(target);

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EnsureReadyCoreAsync(preset, visualNovelFrameRateLimit, cancellationToken);
            ValidateTargetWindow(target);
            Report(string.Format("Фокус: {0}", target.Title));
            NativeMethods.FocusWindow(target.Handle);
            await Task.Delay(280, cancellationToken);
            ValidateTargetWindow(target);
            if (NativeMethods.GetForegroundWindow() != target.Handle)
            {
                throw new InvalidOperationException("Не удалось перевести фокус на выбранное окно.");
            }

            int ownedProcessId = GetOwnedProcessIdOrThrow();
            // WM_HOTKEY is a mode-specific toggle, not a start command: sending
            // it while that same mode is already scaling *stops* the session.
            // The message is asynchronous, so the confirmation below would still
            // observe the stale "true" and report success — the user would be
            // told scaling started at the moment it turned off.
            bool active = TryGetActiveScaling(out nint activeSource, out bool activeWindowed);
            switch (PlanScalingCommand(active, activeSource, activeWindowed, target.Handle, windowed))
            {
                case ScalingCommand.AlreadyActive:
                    Report(windowed
                        ? "Оконное масштабирование уже запущено"
                        : "Полноэкранное масштабирование уже запущено");
                    return;
                case ScalingCommand.RestartAfterStop:
                    // Another source or another mode: the running session must be
                    // toggled off with *its* mode first, and confirmed stopped, so
                    // the start below cannot read the previous session's state.
                    await StopScalingLockedAsync(ownedProcessId, activeWindowed, cancellationToken);
                    break;
            }

            if (!NativeMethods.TriggerMagpieAction(
                    windowed,
                    ownedProcessId,
                    allowKeyboardFallback: !HasExternalMagpieProcess()))
            {
                throw new InvalidOperationException("Magpie не принял команду масштабирования.");
            }

            if (!await WaitForStateAsync(
                    () => IsScalingReadyForTarget(target.Handle),
                    expectedState: true,
                    TimeSpan.FromSeconds(5),
                    cancellationToken))
            {
                throw new InvalidOperationException("Magpie не подтвердил запуск масштабирования.");
            }

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
            ThrowIfDisposed();
            if (!TryGetOwnedProcessId(out int ownedProcessId))
            {
                return;
            }

            nint scalingWindow = NativeMethods.FindTopLevelWindow(
                ScalingWindowClass,
                ownedProcessId);
            if (scalingWindow == nint.Zero)
            {
                return;
            }

            bool activeWindowedMode = IsWindowedScalingModeState(
                NativeMethods.GetProp(scalingWindow, "Magpie.Windowed"));
            await StopScalingLockedAsync(ownedProcessId, activeWindowedMode, cancellationToken);
            Report("Масштабирование остановлено");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Stops the running session. WM_HOTKEY selects a mode-specific toggle, not
    /// a generic stop: sending the active mode stops it, sending the other mode
    /// can switch modes instead. The caller already holds the operation lock.
    /// </summary>
    private async Task StopScalingLockedAsync(
        int ownedProcessId,
        bool activeWindowedMode,
        CancellationToken cancellationToken)
    {
        if (!NativeMethods.TriggerMagpieAction(
                activeWindowedMode,
                ownedProcessId,
                allowKeyboardFallback: !HasExternalMagpieProcess()))
        {
            throw new InvalidOperationException("Magpie не принял команду остановки масштабирования.");
        }

        if (!await WaitForStateAsync(
                () => IsScaling,
                expectedState: false,
                TimeSpan.FromSeconds(5),
                cancellationToken))
        {
            throw new InvalidOperationException("Magpie не подтвердил остановку масштабирования.");
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
            ThrowIfDisposed();
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
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await StopOwnedEngineCoreAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task StopOwnedEngineCoreAsync(CancellationToken cancellationToken)
    {
        Process? process = _ownedProcess;
        if (process is null)
        {
            ResetOwnedProcessState();
            return;
        }

        bool cancellationRequested = false;
        try
        {
            if (!process.HasExited)
            {
                nint window = NativeMethods.FindTopLevelWindow(MainWindowClass, process.Id);
                if (window != nint.Zero)
                {
                    uint quit = NativeMethods.RegisterWindowMessage(QuitMessage);
                    NativeMethods.PostMessage(window, quit, nint.Zero, nint.Zero);
                }

                Stopwatch timer = Stopwatch.StartNew();
                while (!process.HasExited && timer.Elapsed < TimeSpan.FromSeconds(4))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancellationRequested = true;
                        break;
                    }

                    await Task.Delay(80, CancellationToken.None);
                    process.Refresh();
                }

                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            process.Dispose();
            ResetOwnedProcessState();
        }

        if (cancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
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

        ValidateEngineCompatibility();

        IReadOnlyList<string> missingEffects = _configEditor.ValidateEffects(EffectsDirectory, preset);
        if (missingEffects.Count > 0)
        {
            throw new InvalidOperationException("В движке отсутствуют эффекты: " + string.Join(", ", missingEffects));
        }

        int effectiveFrameRateLimit = preset.IsVisualNovel
            ? VisualNovelFrameRates.Normalize(visualNovelFrameRateLimit)
            : 0;
        if (HasExternalMagpieProcess())
        {
            throw new InvalidOperationException(
                "Magpie уже запущен вне Asteria Scale. Закройте его перед применением пресета.");
        }

        if (IsOwnedProcessRunning() &&
            string.Equals(_activePresetId, preset.Id, StringComparison.Ordinal) &&
            _activeVisualNovelFrameRateLimit == effectiveFrameRateLimit)
        {
            HideEngineWindow();
            return;
        }

        Report("Подготовка GPU-движка…");
        await StopOwnedEngineCoreAsync(cancellationToken);
        await BackupConfigOnceAsync(_configPath, cancellationToken);
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

            nint window = NativeMethods.FindTopLevelWindow(
                MainWindowClass,
                _ownedProcess.Id);
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
        nint window = FindOwnedWindow(MainWindowClass);
        if (window != nint.Zero)
        {
            NativeMethods.ShowWindow(window, NativeMethods.SwHide);
        }
    }

    internal static async Task BackupConfigOnceAsync(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(configPath))
        {
            return;
        }

        string backup = configPath + MagpieConfigEditor.BackupSuffix;
        if (File.Exists(backup))
        {
            return;
        }

        string temporaryPath = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream source = new(
                configPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream destination = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, backup, overwrite: false);
            }
            catch (IOException) when (File.Exists(backup))
            {
                // Another Asteria process won the no-overwrite move; its complete backup is authoritative.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Cleanup must not hide the copy, flush, move, or cancellation failure.
                }
            }
        }
    }

    internal static async Task<bool> WaitForStateAsync(
        Func<bool> readState,
        bool expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readState);
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readState() == expectedState)
            {
                return true;
            }

            await Task.Delay(50, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return readState() == expectedState;
    }

    private bool IsOwnedProcessRunning()
    {
        try
        {
            return _ownedProcess is { HasExited: false };
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryGetOwnedProcessId(out int processId)
    {
        processId = 0;
        try
        {
            if (_ownedProcess is not { HasExited: false } process)
            {
                return false;
            }

            processId = process.Id;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          ObjectDisposedException)
        {
            return false;
        }
    }

    private int GetOwnedProcessIdOrThrow() =>
        TryGetOwnedProcessId(out int processId)
            ? processId
            : throw new InvalidOperationException("Запущенный Asteria процесс Magpie недоступен.");

    private nint FindOwnedWindow(string className) =>
        TryGetOwnedProcessId(out int processId)
            ? NativeMethods.FindTopLevelWindow(className, processId)
            : nint.Zero;

    internal static bool IsScalingSourceLostState(
        nint sourceWindow,
        bool isWindow,
        bool isIconic,
        bool isCloaked)
    {
        // Fullscreen Magpie intentionally iconifies some source windows while
        // its scaling window remains active. Treating that transition as a
        // lost source stops and immediately restarts automatic scaling.
        _ = isIconic;
        return sourceWindow == nint.Zero || !isWindow || isCloaked;
    }

    internal static bool IsScalingSourceForegroundState(
        nint sourceWindow,
        nint foregroundWindow) =>
        sourceWindow != nint.Zero && sourceWindow == foregroundWindow;

    internal static bool IsScalingWindowActiveState(
        nint scalingWindow,
        bool isVisible) =>
        scalingWindow != nint.Zero && isVisible;

    internal static bool IsScalingWindowReadyForTargetState(
        nint scalingWindow,
        bool isVisible,
        nint sourceWindow,
        bool isSourceWindow,
        nint expectedSourceWindow) =>
        IsScalingWindowActiveState(scalingWindow, isVisible) &&
        sourceWindow != nint.Zero &&
        isSourceWindow &&
        sourceWindow == expectedSourceWindow;

    internal static bool IsWindowedScalingModeState(nint windowedProperty) =>
        windowedProperty != nint.Zero;

    internal enum ScalingCommand
    {
        /// <summary>Nothing is scaling this target: send the mode toggle.</summary>
        Start,

        /// <summary>Requested target and mode are already running: send nothing.</summary>
        AlreadyActive,

        /// <summary>Another target or mode is running: stop it, then start.</summary>
        RestartAfterStop
    }

    /// <summary>
    /// What <see cref="ScaleAsync"/> must actually send. Kept pure because the
    /// consequence of getting it wrong — toggling off a session the user just
    /// asked for — cannot be reproduced without a live Magpie.
    /// </summary>
    internal static ScalingCommand PlanScalingCommand(
        bool isScalingActive,
        nint activeSourceWindow,
        bool activeWindowedMode,
        nint requestedSourceWindow,
        bool requestedWindowedMode)
    {
        if (!isScalingActive)
        {
            return ScalingCommand.Start;
        }

        return activeSourceWindow == requestedSourceWindow &&
               activeWindowedMode == requestedWindowedMode
            ? ScalingCommand.AlreadyActive
            : ScalingCommand.RestartAfterStop;
    }

    /// <summary>
    /// The session Magpie is running right now, if any: its source window and
    /// mode. Because the hotkey is a toggle, a caller has to know what is
    /// already running before it can decide whether to send anything.
    /// </summary>
    private bool TryGetActiveScaling(out nint sourceWindow, out bool windowedMode)
    {
        sourceWindow = nint.Zero;
        windowedMode = false;
        nint scalingWindow = FindOwnedWindow(ScalingWindowClass);
        if (!IsScalingWindowActiveState(
                scalingWindow,
                scalingWindow != nint.Zero && NativeMethods.IsWindowVisible(scalingWindow)))
        {
            return false;
        }

        sourceWindow = NativeMethods.GetProp(scalingWindow, "Magpie.SrcHWND");
        windowedMode = IsWindowedScalingModeState(
            NativeMethods.GetProp(scalingWindow, "Magpie.Windowed"));
        return sourceWindow != nint.Zero && NativeMethods.IsWindow(sourceWindow);
    }

    private bool IsScalingReadyForTarget(nint expectedSourceWindow)
    {
        nint scalingWindow = FindOwnedWindow(ScalingWindowClass);
        bool isVisible = scalingWindow != nint.Zero && NativeMethods.IsWindowVisible(scalingWindow);
        nint sourceWindow = scalingWindow == nint.Zero
            ? nint.Zero
            : NativeMethods.GetProp(scalingWindow, "Magpie.SrcHWND");
        return IsScalingWindowReadyForTargetState(
            scalingWindow,
            isVisible,
            sourceWindow,
            sourceWindow != nint.Zero && NativeMethods.IsWindow(sourceWindow),
            expectedSourceWindow);
    }

    private bool HasExternalMagpieProcess()
    {
        int ownedProcessId = IsOwnedProcessRunning() ? _ownedProcess!.Id : -1;
        Process[] processes = Process.GetProcessesByName("Magpie");
        try
        {
            return processes.Any(process => process.Id != ownedProcessId);
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void ValidateTargetWindow(WindowInfo target)
    {
        if (!NativeMethods.IsWindow(target.Handle) ||
            NativeMethods.GetWindowThreadProcessId(target.Handle, out uint processId) == 0 ||
            target.ProcessId <= 0 ||
            processId != (uint)target.ProcessId)
        {
            throw new InvalidOperationException("Выбранное окно уже закрыто или было заменено.");
        }
    }

    private void ResetOwnedProcessState()
    {
        _ownedProcess = null;
        _activePresetId = null;
        _activeVisualNovelFrameRateLimit = 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
                .Where(File.Exists)
                .OrderByDescending(GetFileVersion)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
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

    private void ValidateEngineCompatibility()
    {
        FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(EnginePath);
        if (!string.Equals(versionInfo.ProductName, "Magpie", StringComparison.OrdinalIgnoreCase))
        {
            // Tests and developer overrides may use a stub executable with no Magpie metadata.
            return;
        }

        Version version = GetFileVersion(EnginePath);
        if (version < MinimumMagpieVersion)
        {
            throw new InvalidOperationException(
                $"Требуется Magpie {MinimumMagpieVersion} или новее с конфигурацией v4; найден {version}.");
        }
    }

    private static Version GetFileVersion(string path)
    {
        FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
        return new Version(
            Math.Max(0, version.FileMajorPart),
            Math.Max(0, version.FileMinorPart),
            Math.Max(0, version.FileBuildPart),
            Math.Max(0, version.FilePrivatePart));
    }

    public async ValueTask DisposeAsync()
    {
        await _operationLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopOwnedEngineCoreAsync(CancellationToken.None);
        }
        finally
        {
            _operationLock.Release();
        }
    }
}
