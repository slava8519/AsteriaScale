using System.Globalization;
using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

internal enum SmartPhraseRejectionReason
{
    None,
    Empty,
    SourceOutOfScope,
    ModelIdentityMismatch,
    InvalidCharacters,
    TooLong,
    PromptEcho,
    Explanation,
    NonRussian,
    ResidualEnglish,
    SemanticTermMissing,
    SenseConflict
}

internal sealed record SmartPhraseGuardResult(
    bool Accepted,
    string Translation,
    SmartPhraseRejectionReason Reason);

/// <summary>
/// Conservative boundary between experimental generation and the hover card.
/// A rejected result is never repaired here: the independent dictionary line
/// remains visible and the phrase line reports that generation was unavailable.
/// </summary>
internal static partial class SmartPhraseTranslationGuard
{
    internal const int MaximumOutputCharacters = 1_600;
    internal const int MaximumOutputWords = 180;

    public static SmartPhraseGuardResult Validate(
        SmartPhraseTranslationRequest request,
        SmartPhraseTranslationResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        string sourcePhrase = request.SourcePhrase ?? string.Empty;
        string modelInputPhrase = request.ModelInputPhrase ?? string.Empty;
        int sourceWordCount = WordRegex().Matches(sourcePhrase).Count;
        int modelInputWordCount = WordRegex().Matches(modelInputPhrase).Count;
        SmartPhraseRejectionReason requestRejection = ValidateRequest(
            request,
            sourceWordCount,
            modelInputWordCount);
        if (requestRejection != SmartPhraseRejectionReason.None)
        {
            return Reject(requestRejection);
        }

        if (!string.Equals(
                request.ModelIdentity,
                result.ModelIdentity,
                StringComparison.Ordinal))
        {
            return Reject(SmartPhraseRejectionReason.ModelIdentityMismatch);
        }

        if (string.IsNullOrWhiteSpace(result.Translation))
        {
            return Reject(SmartPhraseRejectionReason.Empty);
        }

        if (result.Translation.Any(character =>
                char.IsControl(character) ||
                char.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            return Reject(SmartPhraseRejectionReason.InvalidCharacters);
        }

        string translation = NormalizeOutput(result.Translation);
        int maximumOutputWords = Math.Min(
            MaximumOutputWords,
            Math.Max(8, Math.Max(sourceWordCount, modelInputWordCount) * 3));
        if (translation.Length > MaximumOutputCharacters ||
            WordRegex().Matches(translation).Count > maximumOutputWords)
        {
            return Reject(SmartPhraseRejectionReason.TooLong);
        }

        if (ContainsPromptEcho(sourcePhrase, translation) ||
            ContainsPromptEcho(modelInputPhrase, translation))
        {
            return Reject(SmartPhraseRejectionReason.PromptEcho);
        }

        if (ExplanationPrefixRegex().IsMatch(translation) ||
            LeadingListMarkerRegex().IsMatch(translation) ||
            translation.Contains("```", StringComparison.Ordinal))
        {
            return Reject(SmartPhraseRejectionReason.Explanation);
        }

        if (!translation.Any(IsCyrillic) ||
            translation.Any(character =>
                char.IsLetter(character) &&
                !IsCyrillic(character) &&
                !IsAsciiLetter(character)))
        {
            return Reject(SmartPhraseRejectionReason.NonRussian);
        }

        HashSet<string> allowedProperNameTokens = request.ProperNames
            .SelectMany(name => AsciiWordRegex().Matches(name).Cast<Match>())
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (AsciiWordRegex().Matches(translation)
            .Cast<Match>()
            .Any(match => !allowedProperNameTokens.Contains(match.Value)))
        {
            return Reject(SmartPhraseRejectionReason.ResidualEnglish);
        }

        if (SenseFidelityGuard.FindMissingTerms(
                translation,
                request.SemanticTerms).Count > 0)
        {
            return Reject(SmartPhraseRejectionReason.SemanticTermMissing);
        }

        if (NeuralSenseFidelityGuard.FindConflictingHints(
                translation,
                request.SenseHints).Count > 0)
        {
            return Reject(SmartPhraseRejectionReason.SenseConflict);
        }

        return new SmartPhraseGuardResult(
            Accepted: true,
            translation,
            SmartPhraseRejectionReason.None);
    }

    internal static SmartPhraseRejectionReason ValidateRequest(
        SmartPhraseTranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValidateRequest(
            request,
            WordRegex().Matches(request.SourcePhrase ?? string.Empty).Count,
            WordRegex().Matches(request.ModelInputPhrase ?? string.Empty).Count);
    }

    private static SmartPhraseRejectionReason ValidateRequest(
        SmartPhraseTranslationRequest request,
        int sourceWordCount,
        int modelInputWordCount)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePhrase) ||
            string.IsNullOrWhiteSpace(request.ModelInputPhrase) ||
            sourceWordCount is < 2 or > SmartPhraseContextBuilder.MaximumWords ||
            modelInputWordCount is < 2 or > SmartPhraseContextBuilder.MaximumWords ||
            string.IsNullOrWhiteSpace(request.TransformIdentity) ||
            request.TransformIdentity.Length > 128 ||
            request.TransformIdentity.Any(character => char.IsControl(character)) ||
            HasUnsafeSourceControl(request.SourcePhrase) ||
            HasUnsafeSourceControl(request.ModelInputPhrase) ||
            request.SourcePhrase.Contains("<start_of_turn>", StringComparison.Ordinal) ||
            request.SourcePhrase.Contains("<end_of_turn>", StringComparison.Ordinal) ||
            request.ModelInputPhrase.Contains("<start_of_turn>", StringComparison.Ordinal) ||
            request.ModelInputPhrase.Contains("<end_of_turn>", StringComparison.Ordinal) ||
            request.ProperNames is null ||
            request.PreviousContext is null ||
            request.SemanticTerms is null ||
            request.SenseHints is null ||
            request.SourcePhrase.Length > SmartPhraseContextBuilder.MaximumCharacters ||
            request.ModelInputPhrase.Length > SmartPhraseContextBuilder.MaximumCharacters ||
            request.PreviousContext.Count > TextHookService.MaximumContextSentences ||
            request.PreviousContext.Sum(value => value?.Length ?? 0) >
                TextHookService.MaximumContextCharacters ||
            request.PreviousContext.Any(value => !IsSafeContext(value)) ||
            request.SemanticTerms.Count > TranslationScaffoldBuilder.MaximumTermCount ||
            request.SemanticTerms.Any(term =>
                term is null ||
                string.IsNullOrWhiteSpace(term.Source) ||
                string.IsNullOrWhiteSpace(term.Translation) ||
                term.Source.Length > 128 ||
                term.Translation.Length > 256 ||
                HasUnsafeSourceControl(term.Source) ||
                HasUnsafeSourceControl(term.Translation) ||
                !double.IsFinite(term.Confidence)) ||
            request.SenseHints.Count > TranslationScaffoldBuilder.MaximumSenseHintCount ||
            request.SenseHints.Any(hint =>
                hint is null ||
                string.IsNullOrWhiteSpace(hint.Source) ||
                hint.EnglishDefinition is null ||
                string.IsNullOrWhiteSpace(hint.SelectedTranslation) ||
                hint.Source.Length > 128 ||
                hint.EnglishDefinition.Length > 320 ||
                hint.SelectedTranslation.Length > 256 ||
                hint.RejectedTranslations is null ||
                hint.RejectedTranslations.Count > 32 ||
                hint.RejectedTranslations.Any(value =>
                    string.IsNullOrWhiteSpace(value) || value.Length > 256) ||
                HasUnsafeSourceControl(hint.Source) ||
                HasUnsafeSourceControl(hint.EnglishDefinition) ||
                HasUnsafeSourceControl(hint.SelectedTranslation) ||
                !double.IsFinite(hint.Confidence)) ||
            string.IsNullOrWhiteSpace(request.ModelIdentity) ||
            string.IsNullOrWhiteSpace(request.PromptVersion))
        {
            return SmartPhraseRejectionReason.SourceOutOfScope;
        }

        bool unchanged = string.Equals(
            request.SourcePhrase,
            request.ModelInputPhrase,
            StringComparison.Ordinal);
        if (unchanged != string.Equals(
                request.TransformIdentity,
                SmartPhraseIdiomRewriter.NoTransformIdentity,
                StringComparison.Ordinal))
        {
            return SmartPhraseRejectionReason.SourceOutOfScope;
        }

        return SmartPhraseRejectionReason.None;
    }

    internal static string NormalizeOutput(string value)
    {
        string normalized = CollapseWhitespace(value ?? string.Empty);
        normalized = LeadingListMarkerRegex().Replace(normalized, string.Empty, 1);
        normalized = TranslationLabelRegex().Replace(normalized, string.Empty, 1);
        return CollapseWhitespace(normalized);
    }

    private static bool IsSafeContext(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= SmartPhraseContextBuilder.MaximumCharacters &&
        !value.Contains("<start_of_turn>", StringComparison.Ordinal) &&
        !value.Contains("<end_of_turn>", StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool HasUnsafeSourceControl(string value) =>
        value.Any(character =>
            char.IsControl(character) && character is not '\r' and not '\n' and not '\t');

    private static bool ContainsPromptEcho(string source, string translation)
    {
        string normalizedSource = CollapseWhitespace(source);
        return normalizedSource.Length >= 3 &&
               (translation.Contains(
                    normalizedSource,
                    StringComparison.OrdinalIgnoreCase) ||
                ContainsCompleteSourceTokenEcho(source, translation));
    }

    private static bool ContainsCompleteSourceTokenEcho(
        string source,
        string translation)
    {
        string[] sourceTokens = AsciiWordRegex().Matches(source)
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();
        string[] outputTokens = AsciiWordRegex().Matches(translation)
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();
        if (sourceTokens.Length == 0 || outputTokens.Length < sourceTokens.Length)
        {
            return false;
        }

        for (int start = 0; start <= outputTokens.Length - sourceTokens.Length; start++)
        {
            bool matches = true;
            for (int index = 0; index < sourceTokens.Length; index++)
            {
                if (string.Equals(
                        outputTokens[start + index],
                        sourceTokens[index],
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matches = false;
                break;
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static SmartPhraseGuardResult Reject(SmartPhraseRejectionReason reason) =>
        new(false, string.Empty, reason);

    private static string CollapseWhitespace(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsCyrillic(char value) =>
        value is >= '\u0400' and <= '\u052f';

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    [GeneratedRegex(
        @"[\p{L}\p{M}\p{N}]+(?:['’\-][\p{L}\p{M}\p{N}]+)*",
        RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(
        @"[A-Za-z]+(?:['’\-][A-Za-z]+)*",
        RegexOptions.CultureInvariant)]
    private static partial Regex AsciiWordRegex();

    [GeneratedRegex(
        @"^\s*(?:(?:вот\s+)?перевод(?:\s+фразы)?|вариант\s+перевода|ответ|фраза\s+(?:означает|переводится\s+как)|это\s+переводится\s+как|по-русски|translation|russian)\s*[:\-—]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplanationPrefixRegex();

    [GeneratedRegex(
        @"^\s*(?:\d{1,2}[.)]|[-*\u2022])\s+",
        RegexOptions.CultureInvariant)]
    private static partial Regex LeadingListMarkerRegex();

    [GeneratedRegex(
        @"^\s*(?:\u043f\u0435\u0440\u0435\u0432\u043e\u0434(?:\s+\u0444\u0440\u0430\u0437\u044b)?|translation|russian)\s*[:\-\u2014]\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TranslationLabelRegex();
}
