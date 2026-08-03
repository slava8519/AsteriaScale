namespace AsteriaScale.Services;

internal enum NeuralSenseFidelityState
{
    NotApplicable,
    Satisfied,
    Unresolved,
    Mixed,
    Conflict
}

internal sealed record NeuralSenseSourceAssessment(
    string Source,
    NeuralSenseFidelityState State,
    ScaffoldSenseHint Representative);

internal sealed record NeuralSenseFidelityAssessment(
    NeuralSenseFidelityState State,
    IReadOnlyList<NeuralSenseSourceAssessment> Sources)
{
    public int SatisfiedCount => Count(NeuralSenseFidelityState.Satisfied);

    public int UnresolvedCount => Count(NeuralSenseFidelityState.Unresolved);

    public int MixedCount => Count(NeuralSenseFidelityState.Mixed);

    public int ConflictCount => Count(NeuralSenseFidelityState.Conflict);

    public IReadOnlyList<ScaffoldSenseHint> RepairHints => Sources
        .Where(source => source.State is
            NeuralSenseFidelityState.Conflict or
            NeuralSenseFidelityState.Mixed or
            NeuralSenseFidelityState.Unresolved)
        .Select(source => source.Representative)
        .ToArray();

    public IReadOnlyList<ScaffoldSenseHint> ExplicitConflictHints => Sources
        .Where(source => source.State is
            NeuralSenseFidelityState.Conflict or NeuralSenseFidelityState.Mixed)
        .Select(source => source.Representative)
        .ToArray();

    public bool HasBlockingState => Sources.Any(source => source.State is
        NeuralSenseFidelityState.Conflict or
        NeuralSenseFidelityState.Mixed or
        NeuralSenseFidelityState.Unresolved);

    private int Count(NeuralSenseFidelityState state) =>
        Sources.Count(source => source.State == state);
}

/// <summary>
/// Evaluates provider-neutral WSD evidence against a generated Russian sentence.
/// The result is advisory at the display boundary. Conflict, mixed evidence or
/// an unresolved paraphrase may trigger one targeted retry; unresolved output
/// can still be shown fail-open, but is not eligible for persistent caching.
/// </summary>
internal static class NeuralSenseFidelityGuard
{
    public static NeuralSenseFidelityAssessment Assess(
        string translation,
        IReadOnlyList<ScaffoldSenseHint> hints,
        string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(hints);

        List<NeuralSenseSourceAssessment> sources = [];
        foreach (IGrouping<string, ScaffoldSenseHint> sourceHints in hints
                     .Where(hint => !string.IsNullOrWhiteSpace(hint.Source))
                     .GroupBy(hint => hint.Source, StringComparer.Ordinal))
        {
            ScaffoldSenseHint[] grouped = sourceHints.ToArray();
            ScaffoldSenseHint representative = grouped.FirstOrDefault(hint => hint.IsPrimary) ??
                                                grouped[0];
            if (!string.IsNullOrWhiteSpace(sourceText) &&
                CountSourceOccurrences(sourceText, sourceHints.Key) != 1)
            {
                // WSD evidence currently has no source span. Repeated tokens
                // can legitimately carry different senses, so a source-level
                // constraint must be omitted rather than applied to all of them.
                sources.Add(new NeuralSenseSourceAssessment(
                    sourceHints.Key,
                    NeuralSenseFidelityState.NotApplicable,
                    representative));
                continue;
            }

            string[] selectedSurfaces = grouped
                .Select(hint => hint.SelectedTranslation)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // The evidence contract has no character span. If two accepted
            // decisions for the same source disagree, forcing either one could
            // corrupt a repeated word that legitimately carries two senses.
            if (!AreMutuallyCompatible(selectedSurfaces))
            {
                sources.Add(new NeuralSenseSourceAssessment(
                    sourceHints.Key,
                    NeuralSenseFidelityState.NotApplicable,
                    representative));
                continue;
            }

            string[] rejectedSurfaces = grouped
                .SelectMany(hint => hint.RejectedTranslations ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Where(rejected => !selectedSurfaces.Any(selected =>
                    AreEquivalentSurfaces(selected, rejected)))
                .ToArray();
            string[] translationTokens = TokenizeRussian(translation);
            TokenSpan[] selectedSpans = selectedSurfaces
                .SelectMany(surface => FindSurfaceMatches(surface, translationTokens))
                .ToArray();
            TokenSpan[] rejectedSpans = rejectedSurfaces
                .SelectMany(surface => FindSurfaceMatches(surface, translationTokens))
                .ToArray();
            bool selectedSatisfied = selectedSpans.Length > 0;
            bool rejectedSatisfied = rejectedSpans.Any(rejected =>
                !selectedSpans.Any(selected => selected.Contains(rejected)));
            NeuralSenseFidelityState state = (selectedSatisfied, rejectedSatisfied) switch
            {
                (true, true) => NeuralSenseFidelityState.Mixed,
                (true, false) => NeuralSenseFidelityState.Satisfied,
                (false, true) => NeuralSenseFidelityState.Conflict,
                _ => NeuralSenseFidelityState.Unresolved
            };
            sources.Add(new NeuralSenseSourceAssessment(
                sourceHints.Key,
                state,
                representative));
        }

        NeuralSenseFidelityState aggregate = sources.Any(source =>
                source.State == NeuralSenseFidelityState.Conflict)
                ? NeuralSenseFidelityState.Conflict
                : sources.Any(source => source.State == NeuralSenseFidelityState.Mixed)
                    ? NeuralSenseFidelityState.Mixed
                    : sources.Any(source => source.State == NeuralSenseFidelityState.Unresolved)
                        ? NeuralSenseFidelityState.Unresolved
                        : sources.Any(source =>
                            source.State == NeuralSenseFidelityState.Satisfied)
                            ? NeuralSenseFidelityState.Satisfied
                            : NeuralSenseFidelityState.NotApplicable;
        return new NeuralSenseFidelityAssessment(
            aggregate,
            Array.AsReadOnly(sources.ToArray()));
    }

    public static IReadOnlyList<ScaffoldSenseHint> FindConflictingHints(
        string translation,
        IReadOnlyList<ScaffoldSenseHint> hints,
        string? sourceText = null) =>
        Assess(translation, hints, sourceText).ExplicitConflictHints;

    internal static IReadOnlyList<ScaffoldSenseHint> GetApplicableHints(
        string sourceText,
        IReadOnlyList<ScaffoldSenseHint> hints)
    {
        ArgumentNullException.ThrowIfNull(hints);
        return hints
            .Where(hint => !string.IsNullOrWhiteSpace(hint.Source))
            .GroupBy(hint => hint.Source, StringComparer.Ordinal)
            .Where(group => CountSourceOccurrences(sourceText, group.Key) == 1)
            .Where(group => AreMutuallyCompatible(group
                .Select(hint => hint.SelectedTranslation)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray()))
            .SelectMany(group => group)
            .ToArray();
    }

    internal static bool AreMutuallyCompatible(IReadOnlyList<string> surfaces)
    {
        if (surfaces.Count <= 1)
        {
            return true;
        }

        string first = surfaces[0];
        return surfaces.Skip(1).All(surface => AreEquivalentSurfaces(first, surface));
    }

    internal static bool AreEquivalentSurfaces(string left, string right)
    {
        string[] leftTokens = TokenizeRussian(left);
        string[] rightTokens = TokenizeRussian(right);
        return leftTokens.Length > 0 &&
               leftTokens.Length == rightTokens.Length &&
               leftTokens.Zip(rightTokens).All(pair =>
                   RussianMorphology.AreConservativeInflectionVariants(
                       pair.First,
                       pair.Second));
    }

    internal static int CountSourceOccurrences(string sourceText, string source)
    {
        string[] haystack = TokenizeEnglish(sourceText);
        string[] needle = TokenizeEnglish(source);
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return 0;
        }

        int count = 0;
        for (int start = 0; start <= haystack.Length - needle.Length; start++)
        {
            bool match = true;
            for (int index = 0; index < needle.Length; index++)
            {
                if (string.Equals(
                        haystack[start + index],
                        needle[index],
                        StringComparison.Ordinal))
                {
                    continue;
                }

                match = false;
                break;
            }

            if (match)
            {
                count++;
            }
        }

        return count;
    }

    private static IReadOnlyList<TokenSpan> FindSurfaceMatches(
        string surface,
        IReadOnlyList<string> actual)
    {
        string[] expected = TokenizeRussian(surface);
        if (expected.Length == 0 || actual.Count < expected.Length)
        {
            return Array.Empty<TokenSpan>();
        }

        // Sense evidence is negative as well as positive. Requiring one
        // contiguous, token-aligned phrase prevents a lemma from matching a
        // longer unrelated word. Spans also let a selected phrase own a nested
        // rejected surface without hiding another occurrence elsewhere.
        List<TokenSpan> matches = [];
        for (int start = 0; start <= actual.Count - expected.Length; start++)
        {
            bool match = true;
            for (int index = 0; index < expected.Length; index++)
            {
                if (RussianMorphology.AreConservativeInflectionVariants(
                        expected[index],
                        actual[start + index]))
                {
                    continue;
                }

                match = false;
                break;
            }

            if (match)
            {
                matches.Add(new TokenSpan(start, expected.Length));
            }
        }

        return matches;
    }

    private readonly record struct TokenSpan(int Start, int Length)
    {
        public int End => Start + Length;

        public bool Contains(TokenSpan other) =>
            other.Start >= Start && other.End <= End;
    }

    private static string[] TokenizeRussian(string value) =>
        RussianMorphology.TokenRegex()
            .Matches(RussianMorphology.RemoveStressMarks(value ?? string.Empty))
            .Select(match => RussianMorphology.NormalizeForStemComparison(match.Value))
            .Where(token => token.Length > 0)
            .ToArray();

    private static string[] TokenizeEnglish(string value) =>
        EnglishContextLexicon.TokenRegex()
            .Matches(value ?? string.Empty)
            .Select(match => EnglishContextLexicon.NormalizeToken(match.Value))
            .Where(token => token.Length > 0)
            .ToArray();
}
