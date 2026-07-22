namespace AsteriaScale.Services;

/// <summary>
/// Detects only an explicit semantic substitution: the generated text contains
/// a rejected Russian surface while omitting every selected surface for the
/// same source. Merely omitting the selected dictionary wording is allowed, so
/// Qwen remains free to render the selected sense naturally.
/// </summary>
internal static class NeuralSenseFidelityGuard
{
    public static IReadOnlyList<ScaffoldSenseHint> FindConflictingHints(
        string translation,
        IReadOnlyList<ScaffoldSenseHint> hints)
    {
        ArgumentNullException.ThrowIfNull(hints);

        List<ScaffoldSenseHint> conflicts = [];
        foreach (IGrouping<string, ScaffoldSenseHint> sourceHints in hints
                     .Where(hint => !string.IsNullOrWhiteSpace(hint.Source))
                     .GroupBy(hint => hint.Source, StringComparer.Ordinal))
        {
            ScaffoldSenseHint[] grouped = sourceHints.ToArray();
            bool selectedSatisfied = grouped
                .Select(hint => hint.SelectedTranslation)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Any(surface => IsSelectedSurfaceSatisfied(
                    sourceHints.Key,
                    surface,
                    translation));
            if (selectedSatisfied)
            {
                continue;
            }

            bool rejectedSatisfied = grouped
                .SelectMany(hint => hint.RejectedTranslations ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Any(surface => IsRejectedSurfaceSatisfied(
                    sourceHints.Key,
                    surface,
                    translation));
            if (rejectedSatisfied)
            {
                // One representative per Ordinal source is sufficient for a
                // targeted retry, even when that sense carries two gloss hints.
                conflicts.Add(grouped[0]);
            }
        }

        return Array.AsReadOnly(conflicts.ToArray());
    }

    // A broad match on the selected side only suppresses intervention. Negative
    // evidence is deliberately stricter so "банк" cannot match "банкет" and
    // manufacture a semantic conflict.
    private static bool IsSelectedSurfaceSatisfied(
        string source,
        string surface,
        string translation) =>
        SenseFidelityGuard.IsSatisfied(
            new ScaffoldTerm(
                source,
                surface,
                TranslationScaffoldBuilder.MinimumConfidence),
            translation ?? string.Empty);

    private static bool IsRejectedSurfaceSatisfied(
        string source,
        string surface,
        string translation) =>
        SenseFidelityGuard.IsDistinctSurfaceSatisfied(
            new ScaffoldTerm(
                source,
                surface,
                TranslationScaffoldBuilder.MinimumConfidence),
            translation ?? string.Empty);
}
