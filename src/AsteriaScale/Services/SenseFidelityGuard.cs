namespace AsteriaScale.Services;

internal enum TranslationSenseReviewOutcome
{
    NotIssued,
    KeptOriginal,
    AppliedCorrection,
    RejectedResponse,
    Failed
}

internal sealed record TranslationScaffoldDiagnostics(
    string Source,
    int TermCount,
    int SatisfiedTermCount,
    bool ExactSentenceUsed,
    int InitialViolationCount,
    bool RetryIssued,
    int FinalViolationCount,
    int InitialSenseConflictCount,
    int FinalSenseConflictCount,
    TranslationSenseReviewOutcome SenseReviewOutcome,
    IReadOnlyList<ScaffoldTerm> Terms,
    IReadOnlyList<TranslationScaffoldNeuralDiagnostic> NeuralDecisions,
    IReadOnlyList<ScaffoldSenseHint> SenseHints);

/// <summary>
/// Checks only distinctive Russian stems from high-confidence glossary terms.
/// Inflection must be tolerated, while substring matches inside unrelated words
/// must not turn a violated constraint into a success.
/// </summary>
internal static class SenseFidelityGuard
{
    private static readonly HashSet<string> ShortAEndings =
        new(["а", "ы", "е", "у", "ой", "ою"], StringComparer.Ordinal);
    private static readonly HashSet<string> ShortYaEndings =
        new(["я", "и", "е", "ю", "ей", "ею"], StringComparer.Ordinal);
    private static readonly HashSet<string> ShortEEndings =
        new(["е", "я", "ю", "ем", "ей", "ям", "ями", "ях"], StringComparer.Ordinal);
    private static readonly HashSet<string> ShortOEndings =
        new(["о", "а", "у", "ом", "е", "", "ов", "ам", "ами", "ах"], StringComparer.Ordinal);
    private static readonly HashSet<string> ShortConsonantEndings =
        new(["", "а", "у", "ом", "е", "ы", "ов", "ам", "ами", "ах"], StringComparer.Ordinal);

    private static readonly HashSet<string> RussianFunctionWords =
        new(StringComparer.Ordinal)
        {
            "а", "без", "бы", "быть", "в", "во", "для", "до", "же", "за", "и",
            "из", "к", "как", "на", "не", "но", "о", "об", "от", "по", "под",
            "при", "с", "со", "у", "что"
        };

    public static IReadOnlyList<ScaffoldTerm> FindMissingTerms(
        string translation,
        IReadOnlyList<ScaffoldTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        string[] outputTokens = RussianMorphology.TokenRegex()
            .Matches(RussianMorphology.RemoveStressMarks(translation ?? string.Empty))
            .Select(match => NormalizeRussianToken(match.Value))
            .Where(token => token.Length > 0)
            .ToArray();
        return terms
            .Where(term => term.Confidence >= TranslationScaffoldBuilder.MinimumConfidence)
            .Where(term => !IsSatisfied(term, outputTokens))
            .ToArray();
    }

    internal static bool IsSatisfied(ScaffoldTerm term, string translation) =>
        !FindMissingTerms(translation, [term]).Any();

    internal static bool IsDistinctSurfaceSatisfied(ScaffoldTerm term, string translation)
    {
        string[] expectedTokens = RussianMorphology.TokenRegex()
            .Matches(RussianMorphology.RemoveStressMarks(term.Translation ?? string.Empty))
            .Select(match => NormalizeRussianToken(match.Value))
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] actualTokens = RussianMorphology.TokenRegex()
            .Matches(RussianMorphology.RemoveStressMarks(translation ?? string.Empty))
            .Select(match => NormalizeRussianToken(match.Value))
            .Where(token => token.Length > 0)
            .ToArray();
        return expectedTokens.Length > 0 && expectedTokens.All(expected =>
            actualTokens.Any(actual =>
                RussianMorphology.AreConservativeInflectionVariants(expected, actual)));
    }

    private static bool IsSatisfied(
        ScaffoldTerm term,
        IReadOnlyList<string> outputTokens)
    {
        string[] allExpected = RussianMorphology.TokenRegex()
            .Matches(RussianMorphology.RemoveStressMarks(term.Translation ?? string.Empty))
            .Select(match => NormalizeRussianToken(match.Value))
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] contentExpected = allExpected
            .Where(token => token.Length >= 3 && !RussianFunctionWords.Contains(token))
            .ToArray();
        IReadOnlyList<string> expected = contentExpected.Length > 0
            ? contentExpected
            : allExpected;
        return expected.All(expectedToken =>
            outputTokens.Any(outputToken =>
                RussianMorphology.RussianTokenMatchScore(expectedToken, outputToken) > 0 ||
                ShortExpectedNominalFormMatches(expectedToken, outputToken)));
    }

    private static bool ShortExpectedNominalFormMatches(string expected, string actual)
    {
        if (expected.Length is < 3 or > 4 || actual.Length < 3)
        {
            return false;
        }

        char ending = expected[^1];
        string stem;
        HashSet<string> allowedEndings;
        switch (ending)
        {
            case 'а':
                stem = expected[..^1];
                allowedEndings = ShortAEndings;
                break;
            case 'я':
                stem = expected[..^1];
                allowedEndings = ShortYaEndings;
                break;
            case 'е':
                stem = expected[..^1];
                allowedEndings = ShortEEndings;
                break;
            case 'о':
                stem = expected[..^1];
                allowedEndings = ShortOEndings;
                break;
            default:
                if (!char.IsLetter(ending) || ending is 'ь' or 'й' or 'ы' or 'и')
                {
                    return false;
                }

                stem = expected;
                allowedEndings = ShortConsonantEndings;
                break;
        }

        return stem.Length >= 3 &&
               actual.StartsWith(stem, StringComparison.Ordinal) &&
               allowedEndings.Contains(actual[stem.Length..]);
    }

    private static string NormalizeRussianToken(string value) =>
        RussianMorphology.NormalizeForStemComparison(value);
}
