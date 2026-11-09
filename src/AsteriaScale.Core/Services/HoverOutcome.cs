namespace AsteriaScale.Services;

/// <summary>
/// How one hover started by <see cref="HoverCoordinator{TAnchor}.StartHover"/>
/// ended. A host that answers every hover request (CoreHost's hover.resolve)
/// reports it; the Windows host ignores it, because its overlay has already
/// been shown or hidden by then.
/// </summary>
internal enum HoverOutcome
{
    /// <summary>
    /// A result card was posted, and then its phrase line if the settings ask
    /// for one. A dictionary miss counts: it is shown as a not-found card.
    /// </summary>
    Completed,

    /// <summary>
    /// Nothing to show: the host recognized no text, or a dictionary result
    /// was empty or only repeated the source. The card is hidden.
    /// </summary>
    Empty,

    /// <summary>
    /// The hover stopped being current before its result was posted: the
    /// anchor moved away, or hover translation was switched off meanwhile. The
    /// card is hidden.
    /// </summary>
    AnchorLost,

    /// <summary>
    /// The hover's token was cancelled (by the host, a mode or settings change,
    /// or disposal). The card is hidden without a status.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Any other exception. The card is hidden, the error is logged and the
    /// status line reports it.
    /// </summary>
    Failed
}
