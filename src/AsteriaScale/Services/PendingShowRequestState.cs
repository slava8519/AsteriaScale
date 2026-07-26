namespace AsteriaScale.Services;

/// <summary>
/// Coalesces cross-process show requests until the WPF window is safe to show.
/// A callback that runs during asynchronous startup must leave the request
/// pending; otherwise StartMinimized can turn a second launch into a no-op.
/// </summary>
internal sealed class PendingShowRequestState
{
    private int _pending;

    internal void Request() => Interlocked.Exchange(ref _pending, 1);

    internal bool TryConsume(bool canShow) =>
        canShow && Interlocked.Exchange(ref _pending, 0) != 0;
}
