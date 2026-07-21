using System.Windows.Threading;
using AsteriaScale.Interop;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class AutoScaleService : IAsyncDisposable
{
    private static readonly TimeSpan IdlePollingInterval = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan ScalingPollingInterval = TimeSpan.FromMilliseconds(75);

    private readonly AppSettings _settings;
    private readonly WindowCatalogService _windowCatalog;
    private readonly MagpieEngineService _engine;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private nint _candidateHandle;
    private DateTime _candidateSince;
    private nint _lastTriggeredHandle;
    private Task? _activeTick;
    private bool _disposed;

    public AutoScaleService(
        AppSettings settings,
        WindowCatalogService windowCatalog,
        MagpieEngineService engine)
    {
        _settings = settings;
        _windowCatalog = windowCatalog;
        _engine = engine;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = IdlePollingInterval
        };
        _timer.Tick += OnTimerTick;
    }

    public WindowInfo? LastEligibleWindow { get; private set; }
    public event EventHandler<string>? StatusChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Interval = PollingIntervalFor(_engine.IsScaling);
        if (_disposed || _activeTick is { IsCompleted: false })
        {
            return;
        }

        Task tick = RunTimerTickAsync(_lifetimeCancellation.Token);
        _activeTick = tick;
        _ = ObserveTimerTickAsync(tick);
    }

    private async Task ObserveTimerTickAsync(Task tick)
    {
        try
        {
            await tick;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Disposal owns this cancellation and waits for the same operation below.
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, "Автоматизация: " + ex.Message);
        }
        finally
        {
            _timer.Interval = PollingIntervalFor(_engine.IsScaling);
            if (ReferenceEquals(_activeTick, tick))
            {
                _activeTick = null;
            }
        }
    }

    private async Task RunTimerTickAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Magpie keeps the system cursor hidden while it draws its scaled
        // cursor. Minimized and virtual-desktop source windows intentionally
        // remain scaled upstream, so Asteria must end its owned session.
        bool isScaling = _engine.IsScaling;
        _timer.Interval = PollingIntervalFor(isScaling);
        if (isScaling && (IsDesktopForeground() || _engine.IsScalingSourceLost))
        {
            await _engine.StopScalingAsync(cancellationToken);
            return;
        }

        WindowInfo? foreground = _windowCatalog.GetForegroundEligibleWindow();
        if (foreground is not null)
        {
            LastEligibleWindow = foreground;
        }

        if (!_settings.AutomationEnabled || _engine.IsScaling || foreground is null)
        {
            if (!_engine.IsScaling)
            {
                _lastTriggeredHandle = nint.Zero;
            }

            ResetCandidate();
            return;
        }

        AutomationProfile? profile = _settings.Profiles.FirstOrDefault(p => p.Matches(foreground));
        if (profile is null || foreground.Handle == _lastTriggeredHandle)
        {
            ResetCandidate();
            return;
        }

        if (_candidateHandle != foreground.Handle)
        {
            _candidateHandle = foreground.Handle;
            _candidateSince = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow - _candidateSince < TimeSpan.FromMilliseconds(900))
        {
            return;
        }

        try
        {
            StatusChanged?.Invoke(this, string.Format("Автозапуск: {0}", profile.Name));
            await _engine.ScaleAsync(
                foreground,
                PresetCatalog.Find(profile.PresetId),
                profile.Windowed,
                _settings.VisualNovelFrameRateLimit,
                cancellationToken);
            _lastTriggeredHandle = foreground.Handle;
        }
        finally
        {
            ResetCandidate();
        }
    }

    private void ResetCandidate()
    {
        _candidateHandle = nint.Zero;
        _candidateSince = default;
    }

    private static bool IsDesktopForeground()
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return false;
        }

        string className = NativeMethods.ReadClassName(foreground);
        return IsDesktopWindowClass(className);
    }

    internal static bool IsDesktopWindowClass(string className) =>
        className is "Progman" or "WorkerW" or "Shell_TrayWnd";

    internal static TimeSpan PollingIntervalFor(bool isScaling) =>
        isScaling ? ScalingPollingInterval : IdlePollingInterval;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _lifetimeCancellation.Cancel();

        Task? activeTick = _activeTick;
        if (activeTick is not null)
        {
            try
            {
                await activeTick;
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch
            {
                // The observer already reports operational failures; shutdown only drains the task.
            }
        }

        _lifetimeCancellation.Dispose();
    }
}
