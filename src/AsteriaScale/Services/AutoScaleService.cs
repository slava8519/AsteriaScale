using System.Windows.Threading;
using AsteriaScale.Interop;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class AutoScaleService : IAsyncDisposable
{
    private static readonly TimeSpan IdlePollingInterval = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan ScalingPollingInterval = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan ScalingSourceGracePeriod = TimeSpan.FromMilliseconds(750);

    private readonly AppSettings _settings;
    private readonly WindowCatalogService _windowCatalog;
    private readonly MagpieEngineService _engine;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private nint _candidateHandle;
    private DateTime _candidateSince;
    private nint _lastTriggeredHandle;
    private int _lastTriggeredProcessId;
    private Task? _activeTick;
    private bool _desktopCursorOverrideActive;
    private bool _magnificationInitialized;
    private bool _scalingObserved;
    private bool _cursorRestorePending;
    private bool _scalingSourceReadyObserved;
    private MagpieEngineService.ScalingSessionIdentity _scalingSessionIdentity;
    private DateTime _scalingSourceUnavailableSince;
    private bool _disposed;

    internal enum DesktopCursorAction
    {
        None,
        Show,
        Hide,
        Release
    }

    internal enum AutomationLatchAction
    {
        Keep,
        Clear,
        Eligible
    }

    internal enum ScalingSourceTrackingAction
    {
        Reset,
        ObserveReady,
        IgnoreUntilReady,
        WaitForGrace,
        VerifyAndStop
    }

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
        // cursor. Expose the Windows cursor whenever focus leaves the source,
        // while preserving the running session across Win+D and Alt+Tab.
        bool isScaling = _engine.IsScaling;
        _timer.Interval = PollingIntervalFor(isScaling);
        if (isScaling)
        {
            _scalingObserved = true;
            _cursorRestorePending = true;
        }

        bool isScalingSessionForeground =
            isScaling && _engine.IsScalingSessionForeground;
        DesktopCursorAction cursorAction = PlanDesktopCursorAction(
            isScaling,
            isScalingSessionForeground,
            _desktopCursorOverrideActive,
            _magnificationInitialized,
            _cursorRestorePending);
        if (!UpdateDesktopCursorOverride(cursorAction))
        {
            // Cursor visibility is best-effort and retried on the next tick.
            // Turning a transient Magnification API failure into a Magpie
            // toggle creates the exact stop/restart loop this guard prevents.
            StatusChanged?.Invoke(this, "Не удалось вернуть курсор Windows — повторная попытка");
        }
        else if (cursorAction == DesktopCursorAction.Release)
        {
            _cursorRestorePending = false;
        }

        if (!isScaling)
        {
            _scalingObserved = false;
        }

        MagpieEngineService.ScalingSessionSnapshot scalingSession = isScaling
            ? _engine.GetScalingSessionSnapshot()
            : default;
        DateTime now = DateTime.UtcNow;
        TimeSpan unavailableDuration = _scalingSourceUnavailableSince == default
            ? TimeSpan.Zero
            : now - _scalingSourceUnavailableSince;
        ScalingSourceTrackingAction sourceAction = PlanScalingSourceTracking(
            isScaling,
            scalingSession.EngineProcessId,
            scalingSession.ScalingWindow,
            scalingSession.IsSourceReady,
            _scalingSourceReadyObserved,
            _scalingSessionIdentity.EngineProcessId,
            _scalingSessionIdentity.ScalingWindow,
            unavailableDuration);
        switch (sourceAction)
        {
            case ScalingSourceTrackingAction.Reset:
            case ScalingSourceTrackingAction.IgnoreUntilReady:
                ResetScalingSourceTracking();
                break;
            case ScalingSourceTrackingAction.ObserveReady:
                _scalingSourceReadyObserved = true;
                _scalingSessionIdentity = scalingSession.Identity;
                _scalingSourceUnavailableSince = default;
                break;
            case ScalingSourceTrackingAction.WaitForGrace:
                if (_scalingSourceUnavailableSince == default)
                {
                    _scalingSourceUnavailableSince = now;
                }

                break;
            case ScalingSourceTrackingAction.VerifyAndStop:
                // The engine repeats all identity and liveness checks after
                // acquiring its operation lock. A recovered/new session or a
                // still-live game window is therefore never killed here.
                await _engine.StopOwnedEngineIfSourceStillUnavailableAsync(
                    _scalingSessionIdentity,
                    cancellationToken);
                _scalingSourceUnavailableSince = now;
                return;
            default:
                throw new ArgumentOutOfRangeException();
        }

        WindowInfo? foreground = _windowCatalog.GetForegroundEligibleWindow();
        if (foreground is not null)
        {
            LastEligibleWindow = foreground;
        }

        AutomationLatchAction latchAction = PlanAutomationLatch(
            _settings.AutomationEnabled,
            _engine.IsScaling,
            foreground?.Handle ?? nint.Zero,
            foreground?.ProcessId ?? 0,
            _lastTriggeredHandle,
            _lastTriggeredProcessId);
        if (latchAction != AutomationLatchAction.Eligible)
        {
            if (latchAction == AutomationLatchAction.Clear)
            {
                _lastTriggeredHandle = nint.Zero;
                _lastTriggeredProcessId = 0;
            }

            ResetCandidate();
            return;
        }

        // Eligible guarantees a real foreground window distinct from the
        // one-shot latch, while keeping the nullability proof local.
        WindowInfo eligibleForeground = foreground ?? throw new InvalidOperationException(
            "Automation latch admitted an empty foreground window.");
        AutomationProfile? profile = _settings.Profiles.FirstOrDefault(
            p => p.Matches(eligibleForeground));
        if (profile is null)
        {
            ResetCandidate();
            return;
        }

        if (_candidateHandle != eligibleForeground.Handle)
        {
            _candidateHandle = eligibleForeground.Handle;
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
                eligibleForeground,
                PresetCatalog.Find(profile.PresetId),
                profile.Windowed,
                _settings.VisualNovelFrameRateLimit,
                cancellationToken);
            _lastTriggeredHandle = eligibleForeground.Handle;
            _lastTriggeredProcessId = eligibleForeground.ProcessId;
            _scalingObserved = true;
            _cursorRestorePending = true;
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

    private void ResetScalingSourceTracking()
    {
        _scalingSourceReadyObserved = false;
        _scalingSessionIdentity = default;
        _scalingSourceUnavailableSince = default;
    }

    internal static DesktopCursorAction PlanDesktopCursorAction(
        bool isScaling,
        bool isScalingSessionForeground,
        bool overrideActive,
        bool magnificationInitialized,
        bool scalingWasObserved)
    {
        if (!isScaling)
        {
            return magnificationInitialized || scalingWasObserved
                ? DesktopCursorAction.Release
                : DesktopCursorAction.None;
        }

        if (!isScalingSessionForeground)
        {
            return overrideActive
                ? DesktopCursorAction.None
                : DesktopCursorAction.Show;
        }

        return overrideActive
            ? DesktopCursorAction.Hide
            : DesktopCursorAction.None;
    }

    internal static ScalingSourceTrackingAction PlanScalingSourceTracking(
        bool isScaling,
        int currentEngineProcessId,
        nint currentScalingWindow,
        bool isSourceReady,
        bool sourceReadyWasObserved,
        int trackedEngineProcessId,
        nint trackedScalingWindow,
        TimeSpan unavailableDuration)
    {
        if (!isScaling)
        {
            return ScalingSourceTrackingAction.Reset;
        }

        if (isSourceReady)
        {
            return ScalingSourceTrackingAction.ObserveReady;
        }

        if (!sourceReadyWasObserved ||
            currentEngineProcessId <= 0 ||
            currentScalingWindow == nint.Zero ||
            currentEngineProcessId != trackedEngineProcessId ||
            currentScalingWindow != trackedScalingWindow)
        {
            return ScalingSourceTrackingAction.IgnoreUntilReady;
        }

        return unavailableDuration >= ScalingSourceGracePeriod
            ? ScalingSourceTrackingAction.VerifyAndStop
            : ScalingSourceTrackingAction.WaitForGrace;
    }

    internal static AutomationLatchAction PlanAutomationLatch(
        bool automationEnabled,
        bool isScaling,
        nint foregroundHandle,
        int foregroundProcessId,
        nint lastTriggeredHandle,
        int lastTriggeredProcessId)
    {
        if (!automationEnabled)
        {
            return AutomationLatchAction.Clear;
        }

        if (isScaling || foregroundHandle == nint.Zero)
        {
            return AutomationLatchAction.Keep;
        }

        return foregroundHandle == lastTriggeredHandle &&
               foregroundProcessId == lastTriggeredProcessId
            ? AutomationLatchAction.Keep
            : AutomationLatchAction.Eligible;
    }

    private bool UpdateDesktopCursorOverride(DesktopCursorAction action)
    {
        switch (action)
        {
            case DesktopCursorAction.None:
                return true;
            case DesktopCursorAction.Show:
                if (!TrySetSystemCursorVisible(visible: true))
                {
                    return false;
                }

                _desktopCursorOverrideActive = true;
                return true;
            case DesktopCursorAction.Hide:
                if (TrySetSystemCursorVisible(visible: false))
                {
                    _desktopCursorOverrideActive = false;
                }

                // A visible cursor is safer than hiding it when ownership is uncertain.
                return true;
            case DesktopCursorAction.Release:
                if (!TrySetSystemCursorVisible(visible: true))
                {
                    return false;
                }

                _desktopCursorOverrideActive = false;

                if (_magnificationInitialized && !NativeMethods.MagUninitialize())
                {
                    return false;
                }

                _magnificationInitialized = false;
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private bool TrySetSystemCursorVisible(bool visible)
    {
        if (!_magnificationInitialized)
        {
            _magnificationInitialized = NativeMethods.MagInitialize();
            if (!_magnificationInitialized)
            {
                return false;
            }
        }

        return NativeMethods.MagShowSystemCursor(visible);
    }

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

        if (_magnificationInitialized || _scalingObserved || _cursorRestorePending)
        {
            // Hide is also our override. Always restore visibility before
            // releasing the Magnification API, even if scaling ended mid-tick.
            if (TrySetSystemCursorVisible(visible: true))
            {
                _desktopCursorOverrideActive = false;
            }

            if (_magnificationInitialized)
            {
                NativeMethods.MagUninitialize();
                _magnificationInitialized = false;
            }

            _scalingObserved = false;
            _cursorRestorePending = false;
        }

        _lifetimeCancellation.Dispose();
    }
}
