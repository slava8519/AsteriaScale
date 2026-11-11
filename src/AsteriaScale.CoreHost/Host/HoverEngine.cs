using AsteriaScale.CoreHost.Protocol;

namespace AsteriaScale.CoreHost.Host;

/// <summary>
/// One validated hover.resolve request as an engine sees it: exactly one of
/// <paramref name="Selection"/> (text the app read itself) and
/// <paramref name="Observations"/> (what Apple Vision read in the app's
/// captures) is set.
/// </summary>
internal sealed record HoverJob(
    long Id,
    string? Mode,
    SelectionSource? Selection,
    ObservationsSource? Observations,
    GameIdentity? Game);

/// <summary>
/// Where an engine posts the cards of one hover. Posting never blocks, and a
/// card posted after the hover's terminal (superseded, cancelled) is dropped
/// by the writer, so an engine never has to check whether it is still current.
/// </summary>
internal interface IHoverEvents
{
    /// <summary>hover.loading, only for an observations hover, before its primary card.</summary>
    void Loading(HoverLoadingEvent card);

    void Primary(HoverPrimaryEvent card);

    void Phrase(HoverPhraseEvent line);
}

/// <summary>
/// What resolves hovers behind the hover lane: Core after core.init
/// (<see cref="CoreSession"/>), or the scripted engine of test.fakeCore. The
/// lane guarantees one call at a time: the next hover starts only after the
/// previous call returned, which is what Core's ModelActivityGate expects.
/// Returns the wire outcome (completed, empty or failed); throwing
/// OperationCanceledException means the hover was cancelled, a
/// RequestErrorException is answered as that error (hover_disabled,
/// stale_frame), and any other exception is a failed hover, never a dead host.
/// </summary>
internal interface IHoverEngine : IAsyncDisposable
{
    Task<string> ResolveAsync(HoverJob job, IHoverEvents events, CancellationToken cancellationToken);
}

internal static class HoverOutcomes
{
    public const string Completed = "completed";
    public const string Empty = "empty";
    public const string Failed = "failed";
}
