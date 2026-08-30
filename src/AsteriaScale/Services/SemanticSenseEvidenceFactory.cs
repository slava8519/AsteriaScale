namespace AsteriaScale.Services;

/// <summary>
/// Converts a provider-neutral semantic-ranker decision into advisory English
/// meaning evidence. Both full-text translation and the Smart Dictionary phrase
/// line must see the same evidence for the same GroupId.
/// </summary>
internal static class SemanticSenseEvidenceFactory
{
    internal const int MaximumDefinitionsPerSense = 2;

    public static IReadOnlyList<ScaffoldSenseHint> Create(
        string source,
        SmartDictionaryRankerPlan plan,
        int selectedGroupId,
        double confidence,
        SenseEvidenceOrigin origin = SenseEvidenceOrigin.Unknown)
    {
        IReadOnlyList<ScaffoldSenseHint> hints = CreateOptional(
            source,
            plan,
            selectedGroupId,
            confidence,
            origin);
        if (hints.Count == 0)
        {
            throw new InvalidOperationException(
                $"Semantic ranker selected GroupId {selectedGroupId} without usable sense evidence.");
        }

        return hints;
    }

    public static IReadOnlyList<ScaffoldSenseHint> CreateOptional(
        string source,
        SmartDictionaryRankerPlan plan,
        int selectedGroupId,
        double confidence,
        SenseEvidenceOrigin origin = SenseEvidenceOrigin.Unknown)
    {
        ArgumentNullException.ThrowIfNull(plan);
        SmartDictionaryRankerOutcome? selectedOutcome = plan.Outcomes.FirstOrDefault(
            outcome => outcome.GroupId == selectedGroupId);
        if (selectedOutcome is null)
        {
            throw new InvalidOperationException(
                $"Semantic ranker selected unknown GroupId {selectedGroupId}.");
        }

        string normalizedSource = TranslationScaffoldBuilder.NormalizeSource(source);
        string selectedTranslation = TranslationScaffoldBuilder.NormalizeRussianTerm(
            selectedOutcome.Translation);
        IReadOnlyList<string> rejectedTranslations = Array.AsReadOnly(plan.Outcomes
            .Where(outcome => outcome.GroupId != selectedGroupId)
            .Select(outcome => TranslationScaffoldBuilder.NormalizeRussianTerm(
                outcome.Translation))
            .Where(translation => translation.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray());
        double stableConfidence = double.IsFinite(confidence)
            ? Math.Clamp(confidence, 0, 1)
            : 0;
        ScaffoldSenseHint[] hints = Enumerable.Range(0, plan.CandidateGlosses.Count)
            .Where(index => plan.CandidateGroupIds[index] == selectedGroupId)
            .Select(index => TranslationScaffoldBuilder.NormalizeEnglishDefinition(
                plan.CandidateGlosses[index]))
            .Where(definition => definition.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumDefinitionsPerSense)
            .Select(definition => new ScaffoldSenseHint(
                normalizedSource,
                definition,
                selectedTranslation,
                rejectedTranslations,
                stableConfidence)
            {
                Origin = origin
            })
            .ToArray();
        if (normalizedSource.Length == 0 || selectedTranslation.Length == 0)
        {
            return Array.Empty<ScaffoldSenseHint>();
        }

        return hints.Length == 0
            ? Array.Empty<ScaffoldSenseHint>()
            : Array.AsReadOnly(hints);
    }
}
