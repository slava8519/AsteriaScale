using System.Text;
using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

/// <summary>
/// Repairs isolated English fragments left in an otherwise Russian neural translation.
/// Only exact user/VN dictionary entries are eligible; the large general dictionaries
/// are intentionally excluded because their noisy senses are unsafe for automatic edits.
/// </summary>
internal sealed partial class ResidualEnglishTranslationGuard
{
    private const int MaximumPhraseTokens = 6;
    private readonly LocalDictionaryService _dictionary;

    public ResidualEnglishTranslationGuard(LocalDictionaryService dictionary)
    {
        _dictionary = dictionary;
    }

    public async Task<string> ProtectAsync(
        string source,
        string translation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            string.IsNullOrWhiteSpace(translation) ||
            IsPredominantlyCyrillic(source) ||
            !translation.Any(IsCyrillic) ||
            !translation.Any(IsAsciiLetter))
        {
            return translation;
        }

        Match[] outputTerms = ResidualTermRegex()
            .Matches(translation)
            .Cast<Match>()
            .ToArray();
        if (outputTerms.Length == 0)
        {
            return translation;
        }

        IReadOnlyDictionary<string, SourcePhrase> sourcePhrases = BuildSafeSourcePhrases(source);
        if (sourcePhrases.Count == 0)
        {
            return translation;
        }

        List<Replacement> replacements = [];
        for (int termIndex = 0; termIndex < outputTerms.Length;)
        {
            Match first = outputTerms[termIndex];
            if (!IsSafeSurface(first.Value))
            {
                termIndex++;
                continue;
            }

            int contiguousTerms = CountContiguousSafeTerms(outputTerms, termIndex, translation);
            Replacement? replacement = null;
            int consumedTerms = 1;
            for (int length = Math.Min(MaximumPhraseTokens, contiguousTerms); length >= 1; length--)
            {
                string phrase = string.Join(
                    " ",
                    outputTerms
                        .Skip(termIndex)
                        .Take(length)
                        .Select(match => match.Value));
                string key = NormalizePhrase(phrase);
                if (!sourcePhrases.TryGetValue(key, out SourcePhrase? sourcePhrase))
                {
                    continue;
                }

                string? dictionaryValue = await _dictionary.LookupTrustedEnglishExactAsync(
                    phrase,
                    cancellationToken);
                string? trustedTranslation = SelectTrustedTranslation(
                    source,
                    sourcePhrase,
                    dictionaryValue);
                if (trustedTranslation is null)
                {
                    continue;
                }

                Match last = outputTerms[termIndex + length - 1];
                replacement = new Replacement(
                    first.Index,
                    last.Index + last.Length - first.Index,
                    trustedTranslation);
                consumedTerms = length;
                break;
            }

            if (replacement is not null)
            {
                replacements.Add(replacement);
            }

            termIndex += consumedTerms;
        }

        if (replacements.Count == 0)
        {
            return translation;
        }

        StringBuilder result = new(translation);
        foreach (Replacement replacement in replacements.OrderByDescending(value => value.Index))
        {
            result.Remove(replacement.Index, replacement.Length);
            result.Insert(replacement.Index, replacement.Value);
        }

        return result.ToString();
    }

    private static IReadOnlyDictionary<string, SourcePhrase> BuildSafeSourcePhrases(string source)
    {
        Match[] terms = ResidualTermRegex()
            .Matches(source)
            .Cast<Match>()
            .ToArray();
        Dictionary<string, SourcePhrase> phrases = new(StringComparer.Ordinal);
        for (int start = 0; start < terms.Length; start++)
        {
            if (!IsSafeSurface(terms[start].Value))
            {
                continue;
            }

            int contiguousTerms = CountContiguousSafeTerms(terms, start, source);
            for (int length = 1; length <= Math.Min(MaximumPhraseTokens, contiguousTerms); length++)
            {
                string phrase = string.Join(
                    " ",
                    terms.Skip(start).Take(length).Select(match => match.Value));
                phrases.TryAdd(
                    NormalizePhrase(phrase),
                    new SourcePhrase(phrase, terms[start].Index));
            }
        }

        return phrases;
    }

    private static int CountContiguousSafeTerms(
        IReadOnlyList<Match> terms,
        int firstIndex,
        string text)
    {
        int count = 1;
        for (int index = firstIndex + 1;
             index < terms.Count && count < MaximumPhraseTokens;
             index++)
        {
            Match previous = terms[index - 1];
            Match current = terms[index];
            string separator = text.Substring(
                previous.Index + previous.Length,
                current.Index - previous.Index - previous.Length);
            if (!IsSafeSurface(current.Value) ||
                separator.Length == 0 ||
                separator.Any(character => !char.IsWhiteSpace(character)))
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static string? SelectTrustedTranslation(
        string source,
        SourcePhrase sourcePhrase,
        string? dictionaryValue)
    {
        if (string.IsNullOrWhiteSpace(dictionaryValue))
        {
            return null;
        }

        string[] candidates = NpuDictionarySelectorService.SplitMeanings(dictionaryValue)
            .Where(IsSafeRussianReplacement)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        if (candidates.Length > 1)
        {
            SmartDictionaryHeuristicSelection? heuristic =
                SmartDictionaryHeuristicRanker.TrySelect(
                    source,
                    sourcePhrase.Value,
                    sourcePhrase.Index,
                    candidates,
                    "English");
            if (heuristic is { Confidence: >= 0.9 } &&
                candidates.Contains(heuristic.Translation, StringComparer.CurrentCultureIgnoreCase))
            {
                return heuristic.Translation;
            }
        }

        // User and curated VN dictionaries are deliberately ordered with the preferred
        // conversational sense first, so their first safe value is deterministic.
        return candidates[0];
    }

    private static bool IsSafeRussianReplacement(string value) =>
        value.Any(IsCyrillic) &&
        !value.Any(IsAsciiLetter);

    private static bool IsSafeSurface(string value)
    {
        if (value.Length < 2 ||
            value.Any(char.IsDigit) ||
            value.IndexOfAny(['_', '+', '.', '/', '\\', '#']) >= 0)
        {
            return false;
        }

        char[] letters = value.Where(IsAsciiLetter).ToArray();
        return letters.Length >= 2 &&
               letters.All(character => character is >= 'a' and <= 'z');
    }

    private static string NormalizePhrase(string value) =>
        string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace('’', '\'')
            .ToLowerInvariant();

    private static bool IsPredominantlyCyrillic(string value)
    {
        int letters = 0;
        int cyrillic = 0;
        foreach (char character in value)
        {
            if (!char.IsLetter(character))
            {
                continue;
            }

            letters++;
            if (IsCyrillic(character))
            {
                cyrillic++;
            }
        }

        return letters > 0 && cyrillic >= Math.Ceiling(letters * 0.7);
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsCyrillic(char character) =>
        character is >= '\u0400' and <= '\u052F';

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}_])(?:[A-Za-z][+#]{1,3}|[A-Za-z0-9]+(?:['_’+./#\\-][A-Za-z0-9]+)*)(?![\p{L}\p{N}_])",
        RegexOptions.CultureInvariant)]
    private static partial Regex ResidualTermRegex();

    private sealed record SourcePhrase(string Value, int Index);
    private sealed record Replacement(int Index, int Length, string Value);
}
