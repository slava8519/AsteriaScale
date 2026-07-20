using System.Windows.Threading;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class AutoScaleService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly WindowCatalogService _windowCatalog;
    private readonly MagpieEngineService _engine;
    private readonly DispatcherTimer _timer;
    private nint _candidateHandle;
    private DateTime _candidateSince;
    private nint _lastTriggeredHandle;
    private bool _busy;

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
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _timer.Tick += OnTimerTick;
    }

    public WindowInfo? LastEligibleWindow { get; private set; }
    public event EventHandler<string>? StatusChanged;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (_busy)
        {
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

        _busy = true;
        try
        {
            StatusChanged?.Invoke(this, string.Format("Автозапуск: {0}", profile.Name));
            await _engine.ScaleAsync(
                foreground,
                PresetCatalog.Find(profile.PresetId),
                profile.Windowed,
                _settings.VisualNovelFrameRateLimit);
            _lastTriggeredHandle = foreground.Handle;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, "Автоматизация: " + ex.Message);
        }
        finally
        {
            ResetCandidate();
            _busy = false;
        }
    }

    private void ResetCandidate()
    {
        _candidateHandle = nint.Zero;
        _candidateSince = default;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
