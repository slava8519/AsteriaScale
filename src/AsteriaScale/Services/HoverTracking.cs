using AsteriaScale.Interop;

namespace AsteriaScale.Services;

/// <summary>
/// Cursor stability tracking for hover translation: the 90 ms poll timer, the
/// anchor with its 15 px radius, the stability delay and the shared operation
/// cancellation. Extracted from HoverTranslationService (roadmap task #5,
/// stage 2). The facade owns the processing gate and launches the work.
/// </summary>
internal sealed class HoverTracking : IDisposable
{
    internal const long RadiusSquared = 225;

    private readonly Func<bool> _shouldTrack;
    private readonly Func<int> _delayMilliseconds;
    private readonly Action _hideOverlay;
    private readonly Func<bool> _tryBeginProcessing;
    private readonly Action<NativeMethods.Point, CancellationToken> _process;
    private readonly object _stateLock = new();
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _operationCancellation;
    private NativeMethods.Point _anchor;
    private DateTime _stableSinceUtc;
    private bool _hasAnchor;
    private bool _processedAnchor;

    public HoverTracking(
        Func<bool> shouldTrack,
        Func<int> delayMilliseconds,
        Action hideOverlay,
        Func<bool> tryBeginProcessing,
        Action<NativeMethods.Point, CancellationToken> process)
    {
        _shouldTrack = shouldTrack;
        _delayMilliseconds = delayMilliseconds;
        _hideOverlay = hideOverlay;
        _tryBeginProcessing = tryBeginProcessing;
        _process = process;
    }

    public void Start() =>
        _timer ??= new System.Threading.Timer(
            OnTimer,
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(90));

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Reset()
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

    internal static long DistanceSquared(NativeMethods.Point left, NativeMethods.Point right)
    {
        long dx = left.X - right.X;
        long dy = left.Y - right.Y;
        return (dx * dx) + (dy * dy);
    }

    private void OnTimer(object? state)
    {
        if (!_shouldTrack() || !VisibleCursorService.TryGetPosition(out NativeMethods.Point cursor))
        {
            return;
        }

        nint window = NativeMethods.WindowFromPoint(cursor);
        if (window != nint.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint processId);
            if (processId == Environment.ProcessId)
            {
                _hideOverlay();
                return;
            }
        }

        bool hide = false;
        NativeMethods.Point pointToProcess = default;
        CancellationToken token = default;
        bool shouldProcess = false;

        lock (_stateLock)
        {
            if (!_hasAnchor || DistanceSquared(cursor, _anchor) > RadiusSquared)
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
                     DateTime.UtcNow - _stableSinceUtc >=
                     TimeSpan.FromMilliseconds(_delayMilliseconds()) &&
                     _tryBeginProcessing())
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
            _hideOverlay();
        }

        if (shouldProcess)
        {
            _process(pointToProcess, token);
        }
    }

    public void Dispose() => Stop();
}
