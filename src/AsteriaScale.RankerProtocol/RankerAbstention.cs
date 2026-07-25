using System.Globalization;

namespace AsteriaScale.RankerProtocol;

/// <summary>
/// The frozen margin-only abstention criterion.
///
/// This lives in the shared protocol assembly because it is applied in the
/// worker but decides what the host may cache. A verdict is only meaningful as
/// "what the ranker answered under this threshold": recalibrating it without
/// invalidating stored decisions would keep serving abstentions that the new
/// threshold would have turned into selections — precisely the metric a ranker
/// bake-off (roadmap #8) is trying to move.
///
/// Changing the value requires a --ranker-benchmark run and a note in
/// docs/RESEARCH.md (AGENTS.md rule 4). Because <see cref="CacheTag"/> is part
/// of every decision cache key, a change also invalidates stored verdicts
/// automatically instead of relying on that discipline alone.
/// </summary>
public static class RankerAbstention
{
    /// <summary>
    /// Frozen on the dev set. The ranker abstains when the margin between the
    /// two best groups is below this value.
    /// </summary>
    public const double MarginThreshold = 0.25;

    /// <summary>
    /// Stable textual form for cache keys. Round-trip formatting under the
    /// invariant culture keeps the tag identical across machines and locales.
    /// </summary>
    public static string CacheTag { get; } =
        "abstain-margin=" + MarginThreshold.ToString("R", CultureInfo.InvariantCulture);
}
