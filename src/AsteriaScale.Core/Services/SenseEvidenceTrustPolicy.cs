namespace AsteriaScale.Services;

/// <summary>
/// Which sense evidence is allowed to condition generation.
/// </summary>
/// <remarks>
/// Measured on the open dev201 slice (audit card E9). Hint accuracy is not a
/// function of confidence alone, because confidence is not comparable across
/// paths: a lone aligned sense reports 1 by construction and is still only
/// 57% right, since a dictionary with one sense can simply be wrong about the
/// context. Splitting by origin and confidence gives:
///
///   rankerAccepted >= 0.90   n=38  76.3% correct,  3 wrong
///   rankerAccepted 0.75-0.90 n=25  64.0% correct,  1 wrong
///   rankerAccepted &lt; 0.75  n=42  47.6% correct, 13 wrong
///   singleOutcome  (1.0)     n=21  57.1% correct,  8 wrong
///   heuristic      (1.0)     n= 8  62.5% correct,  3 wrong
///
/// Accepting only ranker verdicts at or above 0.75 keeps 63 of 137 hints and
/// cuts the share of wrong evidence from 20.4% to 6.3%. Untrusted evidence is
/// not "slightly weaker": it is close to a coin flip, and conditioning on it
/// pushes the generator toward a word the disambiguator was not sure about.
///
/// The threshold is tuned on the open development slice only. It is deliberately
/// separate from <see cref="RankerAbstention.MarginThreshold"/>, which decides
/// whether the ranker answers at all; this one decides whether an answer is firm
/// enough to steer a translation.
///
/// <para><b>Measured and switched off (card E3, 17 August 2026).</b> Filtering on
/// this rule made the whole track worse, not safer: final target sense fell from
/// 91/201 to 82/201 and the advantage over plain TranslateGemma stopped being
/// significant (+1.99 pp, McNemar p=0.34, against +6.47 pp and p=0.0044 with all
/// evidence). The reason is that the sense-preservation gate is already the
/// safety net this policy was duplicating: it generates a conditioned candidate
/// and keeps it only when a deterministic comparison prefers it, so a wrong hint
/// usually loses that comparison, while a withheld hint removes the chance to fix
/// the case at all. Evidence at coin-flip accuracy still pays for itself
/// downstream. The rule is kept, disabled, because the numbers that reject it are
/// only meaningful next to the mechanism that produced them.</para>
/// </remarks>
internal static class SenseEvidenceTrustPolicy
{
    /// <summary>Folded into the composite prompt/policy identity, so a change cannot silently reuse cached translations.</summary>
    internal const string Identity = "sense-trust-off-v1";

    /// <summary>Rejected by measurement; see the remarks above before enabling.</summary>
    internal const bool Enabled = false;

    internal const double MinimumRankerConfidence = 0.75;

    /// <summary>What the pipeline calls: the shipped decision, not the experiment.</summary>
    internal static bool Accepts(ScaffoldSenseHint hint) => !Enabled || IsTrusted(hint);

    /// <summary>The rejected rule itself, kept measurable in isolation.</summary>
    internal static bool IsTrusted(ScaffoldSenseHint hint)
    {
        ArgumentNullException.ThrowIfNull(hint);
        return hint.Origin == SenseEvidenceOrigin.RankerAccepted &&
               double.IsFinite(hint.Confidence) &&
               hint.Confidence >= MinimumRankerConfidence;
    }
}
