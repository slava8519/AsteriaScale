using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

internal enum RussianPastForm
{
    None,
    Masculine,
    Feminine,
    Neuter,
    Plural
}

/// <summary>
/// Suffix-based Russian morphology shared by the deterministic heuristic
/// ranker and the smart-dictionary sense resolver.
/// </summary>
internal static partial class RussianMorphology
{
    private static readonly string[] RussianAdjectiveEndings =
    [
        "ыми", "ими", "ого", "его", "ому", "ему", "ый", "ий", "ой", "ая",
        "яя", "ое", "ее", "ые", "ие", "ым", "им", "ом", "ем", "ую", "юю",
        "ых", "их"
    ];

    private static readonly HashSet<string> ConservativeAEndings =
        new(["а", "ы", "е", "у", "ой", "ою", "ам", "ами", "ах"], StringComparer.Ordinal);
    private static readonly HashSet<string> ConservativeYaEndings =
        new(["я", "и", "е", "ю", "ей", "ею", "ям", "ями", "ях"], StringComparer.Ordinal);
    private static readonly HashSet<string> ConservativeEEndings =
        new(["е", "я", "ю", "ем", "ей", "й", "ям", "ями", "ях"], StringComparer.Ordinal);
    private static readonly HashSet<string> ConservativeOEndings =
        new(["о", "а", "у", "ом", "е", "", "ов", "ам", "ами", "ах"], StringComparer.Ordinal);
    private static readonly HashSet<string> ConservativeSoftEndings =
        new(["ь", "и", "ю", "ью", "ей", "ям", "ями", "ях"], StringComparer.Ordinal);
    private static readonly HashSet<string> ConservativeYEndings =
        new(["й", "я", "ю", "ем", "е", "и", "ев", "ям", "ями", "ях"], StringComparer.Ordinal);
    private static readonly HashSet<string> ConservativePluralEndings =
        new(["ы", "и", "ов", "ев", "ей", "ам", "ям", "ами", "ями", "ах", "ях"], StringComparer.Ordinal);
    private static readonly HashSet<string> ConservativeConsonantEndings =
        new(["", "а", "у", "ом", "е", "ы", "и", "ов", "ев", "ей", "ам", "ям", "ами", "ями", "ах", "ях"], StringComparer.Ordinal);

    public static string RemoveStressMarks(string value) =>
        value.Replace("\u0301", string.Empty, StringComparison.Ordinal)
            .Replace("\u0341", string.Empty, StringComparison.Ordinal);

    public static string NormalizeForStemComparison(string value) =>
        RemoveStressMarks(value).ToLowerInvariant().Replace('ё', 'е');

    public static int RussianTokenMatchScore(string expected, string actual)
    {
        expected = NormalizeForStemComparison(expected);
        actual = NormalizeForStemComparison(actual);
        if (expected.Length == 0 || actual.Length == 0)
        {
            return 0;
        }

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return 3;
        }

        int shorterLength = Math.Min(expected.Length, actual.Length);
        if (shorterLength < 4)
        {
            return 0;
        }

        int requiredPrefix = shorterLength <= 4 ? 4 : 5;
        if (expected.AsSpan(0, requiredPrefix).SequenceEqual(actual.AsSpan(0, requiredPrefix)) &&
            Math.Abs(expected.Length - actual.Length) <= 4)
        {
            return 2;
        }

        if (!TryGetAdjectiveStem(expected, out string expectedStem) ||
            expectedStem.Length < 4)
        {
            return 0;
        }

        if (TryGetAdjectiveStem(actual, out string actualStem) &&
            string.Equals(expectedStem, actualStem, StringComparison.Ordinal))
        {
            return 2;
        }

        // Russian predicate adjectives and derived adverbs often replace the
        // full ending with one letter ("тёмный" -> "темно"). Treat only the
        // exact adjective stem plus a closed set of short endings as the same
        // surface; longer noun suffixes such as "темнота" remain distinct.
        return actual.StartsWith(expectedStem, StringComparison.Ordinal) &&
               actual.Length <= expectedStem.Length + 1 &&
               actual[expectedStem.Length..] is "" or "а" or "о" or "ы" or "и"
            ? 2
            : 0;
    }

    public static bool AreConservativeInflectionVariants(string expected, string actual)
    {
        expected = NormalizeForStemComparison(expected);
        actual = NormalizeForStemComparison(actual);
        if (expected.Length == 0 || actual.Length == 0)
        {
            return false;
        }

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        if (RussianMorphologyLexicon.TryGet(expected, out RussianVerbFormInfo expectedVerb) &&
            RussianMorphologyLexicon.TryGet(actual, out RussianVerbFormInfo actualVerb) &&
            string.Equals(expectedVerb.Lemma, actualVerb.Lemma, StringComparison.Ordinal))
        {
            return true;
        }

        if (TryGetAdjectiveStem(expected, out string adjectiveStem) &&
            adjectiveStem.Length >= 4)
        {
            if (TryGetAdjectiveStem(actual, out string actualAdjectiveStem) &&
                string.Equals(adjectiveStem, actualAdjectiveStem, StringComparison.Ordinal))
            {
                return true;
            }

            return actual.StartsWith(adjectiveStem, StringComparison.Ordinal) &&
                   actual.Length <= adjectiveStem.Length + 1 &&
                   actual[adjectiveStem.Length..] is "" or "а" or "о" or "ы" or "и";
        }

        char ending = expected[^1];
        string stem;
        HashSet<string> allowedEndings;
        switch (ending)
        {
            case 'а':
                stem = expected[..^1];
                allowedEndings = ConservativeAEndings;
                break;
            case 'я':
                stem = expected[..^1];
                allowedEndings = ConservativeYaEndings;
                break;
            case 'е':
                stem = expected[..^1];
                allowedEndings = ConservativeEEndings;
                break;
            case 'о':
                stem = expected[..^1];
                allowedEndings = ConservativeOEndings;
                break;
            case 'ь':
                stem = expected[..^1];
                allowedEndings = ConservativeSoftEndings;
                break;
            case 'й':
                stem = expected[..^1];
                allowedEndings = ConservativeYEndings;
                break;
            case 'ы':
            case 'и':
                stem = expected[..^1];
                allowedEndings = ConservativePluralEndings;
                break;
            default:
                if (!char.IsLetter(ending))
                {
                    return false;
                }

                stem = expected;
                allowedEndings = ConservativeConsonantEndings;
                break;
        }

        return stem.Length >= 3 &&
               actual.StartsWith(stem, StringComparison.Ordinal) &&
               allowedEndings.Contains(actual[stem.Length..]);
    }

    private static bool TryGetAdjectiveStem(string word, out string stem)
    {
        foreach (string ending in RussianAdjectiveEndings)
        {
            if (word.Length > ending.Length + 3 &&
                word.EndsWith(ending, StringComparison.Ordinal))
            {
                stem = word[..^ending.Length];
                return true;
            }
        }

        stem = string.Empty;
        return false;
    }

    public static (RussianPastForm Form, string Stem) DetectPastForm(string word)
    {
        if (RussianMorphologyLexicon.TryGet(word, out RussianVerbFormInfo info))
        {
            RussianPastForm knownForm = info.Kind switch
            {
                RussianVerbFormKind.PastMasculine => RussianPastForm.Masculine,
                RussianVerbFormKind.PastFeminine => RussianPastForm.Feminine,
                RussianVerbFormKind.PastNeuter => RussianPastForm.Neuter,
                RussianVerbFormKind.PastPlural => RussianPastForm.Plural,
                _ => RussianPastForm.None
            };
            // The lemma is a paradigm-stable stem: suppletive forms such as
            // "шёл/шла/шли" all group under "идти", which suffix stems cannot do.
            return knownForm == RussianPastForm.None
                ? (RussianPastForm.None, string.Empty)
                : (knownForm, info.Lemma);
        }

        if (word.EndsWith("лась", StringComparison.Ordinal) && word.Length > 5)
        {
            return (RussianPastForm.Feminine, word[..^4]);
        }

        if (word.EndsWith("ла", StringComparison.Ordinal) && word.Length > 3)
        {
            return (RussianPastForm.Feminine, word[..^2]);
        }

        if (word.EndsWith("лись", StringComparison.Ordinal) && word.Length > 5)
        {
            return (RussianPastForm.Plural, word[..^4]);
        }

        if (word.EndsWith("ли", StringComparison.Ordinal) && word.Length > 3)
        {
            return (RussianPastForm.Plural, word[..^2]);
        }

        if (word.EndsWith("лось", StringComparison.Ordinal) && word.Length > 5)
        {
            return (RussianPastForm.Neuter, word[..^4]);
        }

        if (word.EndsWith("ло", StringComparison.Ordinal) && word.Length > 3)
        {
            return (RussianPastForm.Neuter, word[..^2]);
        }

        if (word.EndsWith("лся", StringComparison.Ordinal) && word.Length > 4)
        {
            return (RussianPastForm.Masculine, word[..^3]);
        }

        return word.EndsWith('л') && word.Length > 2
            ? (RussianPastForm.Masculine, word[..^1])
            : (RussianPastForm.None, string.Empty);
    }

    public static bool IsInfinitive(string word) =>
        RussianMorphologyLexicon.TryGet(word, out RussianVerbFormInfo info)
            ? info.Kind == RussianVerbFormKind.Infinitive
            : word.EndsWith("ть", StringComparison.Ordinal) ||
              word.EndsWith("ться", StringComparison.Ordinal) ||
              word.EndsWith("ти", StringComparison.Ordinal) ||
              word.EndsWith("чь", StringComparison.Ordinal);

    // Historically a "-нуть" semelfactive test. With the OpenCorpora lexicon it
    // reports the perfective infinitive, which is what aspect selection needs:
    // "закрыть/закрывать" becomes a rankable pair, not only "-нуть" verbs.
    public static bool IsSemelfactiveInfinitive(string word)
    {
        if (RussianMorphologyLexicon.TryGet(word, out RussianVerbFormInfo info) &&
            info.Kind == RussianVerbFormKind.Infinitive &&
            info.Aspect is RussianAspect.Perfective or RussianAspect.Imperfective)
        {
            return info.Aspect == RussianAspect.Perfective;
        }

        return word.EndsWith("нуть", StringComparison.Ordinal) ||
               word.EndsWith("нуться", StringComparison.Ordinal);
    }

    [GeneratedRegex(
        @"[\p{IsCyrillic}]+(?:-[\p{IsCyrillic}]+)*",
        RegexOptions.CultureInvariant)]
    public static partial Regex TokenRegex();
}
