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
    StructuralMismatch,
    ResidualEnglish,
    IncompleteTranslation,
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
    private const int MinimumSourceWordsForCompletenessCheck = 10;
    private const int MinimumRussianWordPercentage = 35;

    public static SmartPhraseGuardResult Validate(
        SmartPhraseTranslationRequest request,
        SmartPhraseTranslationResult result) =>
        ValidateCore(request, result, enforceSenseFidelity: true);

    public static SmartPhraseGuardResult ValidateForDisplay(
        SmartPhraseTranslationRequest request,
        SmartPhraseTranslationResult result) =>
        ValidateCore(request, result, enforceSenseFidelity: false);

    private static SmartPhraseGuardResult ValidateCore(
        SmartPhraseTranslationRequest request,
        SmartPhraseTranslationResult result,
        bool enforceSenseFidelity)
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
        if (!enforceSenseFidelity)
        {
            translation = NormalizeDisplayStructure(sourcePhrase, translation);
        }

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

        if (HasStructuralMismatch(sourcePhrase, translation))
        {
            return Reject(SmartPhraseRejectionReason.StructuralMismatch);
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

        if (IsIncompleteTranslation(sourcePhrase, translation))
        {
            return Reject(SmartPhraseRejectionReason.IncompleteTranslation);
        }

        // The strict pass uses every lexical clue to request one APE correction.
        // At the display boundary only user/scaffold requirements stay hard;
        // ordinary dictionary selections are evidence, not mandatory wording.
        IReadOnlyList<ScaffoldTerm> requiredTerms = enforceSenseFidelity
            ? request.SemanticTerms
            : request.SemanticTerms
                .Where(ScaffoldTermPolicy.IsMandatory)
                .ToArray();
        if (SenseFidelityGuard.FindMissingTerms(
                translation,
                requiredTerms).Count > 0)
        {
            return Reject(SmartPhraseRejectionReason.SemanticTermMissing);
        }

        if (enforceSenseFidelity &&
            NeuralSenseFidelityGuard.FindConflictingHints(
                translation,
                request.SenseHints,
                request.SourcePhrase).Count > 0)
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
            sourceWordCount is < 1 or > SmartPhraseContextBuilder.MaximumWords ||
            modelInputWordCount is < 1 or > SmartPhraseContextBuilder.MaximumWords ||
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
            request.ProperNames.Any(value =>
                string.IsNullOrWhiteSpace(value) ||
                value.Length > 128 ||
                HasUnsafePromptText(value)) ||
            request.PreviousContext is null ||
            request.CurrentSpeaker is { } currentSpeaker &&
            (string.IsNullOrWhiteSpace(currentSpeaker.Name) ||
             currentSpeaker.Name.Length > 48 ||
             currentSpeaker.Name.Any(char.IsControl) ||
             currentSpeaker.Name.Contains("<start_of_turn>", StringComparison.Ordinal) ||
             currentSpeaker.Name.Contains("<end_of_turn>", StringComparison.Ordinal) ||
             currentSpeaker.GameKey.Length > 1_024 ||
             currentSpeaker.GameKey.Any(char.IsControl)) ||
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
                HasUnsafePromptText(term.Source) ||
                HasUnsafePromptText(term.Translation) ||
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
                    string.IsNullOrWhiteSpace(value) ||
                    value.Length > 256 ||
                    HasUnsafePromptText(value)) ||
                HasUnsafePromptText(hint.Source) ||
                HasUnsafePromptText(hint.EnglishDefinition) ||
                HasUnsafePromptText(hint.SelectedTranslation) ||
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

    private static string NormalizeDisplayStructure(
        string source,
        string translation)
    {
        string normalizedSource = source.Trim();
        string normalizedTranslation = translation.Trim();
        if (HasOuterQuoteWrapper(normalizedSource) ||
            !HasOuterQuoteWrapper(normalizedTranslation))
        {
            return translation;
        }

        // A dangling OCR opening quote commonly becomes a balanced Russian
        // dialogue wrapper. The wrapper is decorative; removing it preserves
        // CURRENT while the stricter pass still asks the model to correct it.
        return CollapseWhitespace(normalizedTranslation[1..^1]);
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

    private static bool HasUnsafePromptText(string value) =>
        HasUnsafeSourceControl(value) ||
        value.Contains("<start_of_turn>", StringComparison.Ordinal) ||
        value.Contains("<end_of_turn>", StringComparison.Ordinal);

    private static bool IsIncompleteTranslation(string source, string translation)
    {
        int sourceWordCount = WordRegex().Matches(source)
            .Cast<Match>()
            .Count(match => match.Value.Any(char.IsLetter));
        if (sourceWordCount < MinimumSourceWordsForCompletenessCheck)
        {
            return false;
        }

        int russianWordCount = WordRegex().Matches(translation)
            .Cast<Match>()
            .Count(match => match.Value.Any(IsCyrillic));
        return russianWordCount * 100 < sourceWordCount * MinimumRussianWordPercentage;
    }

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

    private static bool HasStructuralMismatch(string source, string translation)
    {
        string normalizedSource = source.Trim();
        string normalizedTranslation = translation.Trim();
        if (!StartsWithEllipsis(normalizedSource) &&
            StartsWithEllipsis(normalizedTranslation))
        {
            return true;
        }

        if (!LeadingSpeakerPrefixRegex().IsMatch(normalizedSource) &&
            LeadingSpeakerPrefixRegex().IsMatch(normalizedTranslation))
        {
            return true;
        }

        if (!HasOuterQuoteWrapper(normalizedSource) &&
            HasOuterQuoteWrapper(normalizedTranslation))
        {
            return true;
        }

        string[] outputAsciiTokens = AsciiWordRegex().Matches(normalizedTranslation)
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();
        IEnumerable<Match> quotedSpans = SingleQuotedAsciiSpanRegex()
            .Matches(normalizedSource)
            .Cast<Match>()
            .Concat(DoubleQuotedAsciiSpanRegex().Matches(normalizedSource).Cast<Match>());
        foreach (Match quoted in quotedSpans)
        {
            if (quoted.Index == 0 && quoted.Length == normalizedSource.Length)
            {
                // Dialogue wrappers are punctuation, not atomic English
                // wordplay. Nested quotes are enumerated independently.
                continue;
            }

            string[] quotedTokens = AsciiWordRegex().Matches(quoted.Groups["content"].Value)
                .Cast<Match>()
                .Select(match => match.Value)
                .ToArray();
            if (quotedTokens.Length is < 1 or > 6 ||
                !quotedTokens.Any(sourceToken => outputAsciiTokens.Contains(
                    sourceToken,
                    StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!ContainsOrderedTokenSequence(outputAsciiTokens, quotedTokens))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsOrderedTokenSequence(
        IReadOnlyList<string> haystack,
        IReadOnlyList<string> needle)
    {
        if (needle.Count == 0 || haystack.Count < needle.Count)
        {
            return false;
        }

        for (int start = 0; start <= haystack.Count - needle.Count; start++)
        {
            bool matches = true;
            for (int index = 0; index < needle.Count; index++)
            {
                if (string.Equals(
                        haystack[start + index],
                        needle[index],
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

    private static bool StartsWithEllipsis(string value) =>
        value.StartsWith("...", StringComparison.Ordinal) ||
        value.StartsWith('\u2026');

    private static bool HasOuterQuoteWrapper(string value)
    {
        if (value.Length < 2)
        {
            return false;
        }

        return (value[0], value[^1]) is
            ('"', '"') or
            ('\'', '\'') or
            ('\u201c', '\u201d') or
            ('\u00ab', '\u00bb');
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
        """^\s*["'\u00ab\u201c]?\s*(?:\[[^\]\r\n]{1,48}\]\s*|[\p{L}\p{M}][\p{L}\p{M}'\u2019\-]{0,31}(?:\s+[\p{L}\p{M}][\p{L}\p{M}'\u2019\-]{0,31}){0,2}\s*:\s*)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex LeadingSpeakerPrefixRegex();

    [GeneratedRegex(
        """(?<![A-Za-z])'(?<content>[^'\r\n]{1,96})'(?![A-Za-z])""",
        RegexOptions.CultureInvariant)]
    private static partial Regex SingleQuotedAsciiSpanRegex();

    [GeneratedRegex(
        """"(?<content>[^"\r\n]{1,96})"""",
        RegexOptions.CultureInvariant)]
    private static partial Regex DoubleQuotedAsciiSpanRegex();

    [GeneratedRegex(
        @"^\s*(?:\u043f\u0435\u0440\u0435\u0432\u043e\u0434(?:\s+\u0444\u0440\u0430\u0437\u044b)?|translation|russian)\s*[:\-\u2014]\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TranslationLabelRegex();
}
