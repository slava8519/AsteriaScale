using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

internal readonly record struct SmartPhraseModelInput(
    string SourcePhrase,
    string ModelInputPhrase,
    string TransformIdentity);

/// <summary>
/// Rewrites only narrowly proven English idioms into unambiguous English before
/// the independent direct translator. It never reads the Russian dictionary
/// result or GlossBERT evidence, so the primary line remains authoritative and
/// the model still receives one ordinary translation payload.
/// </summary>
internal static partial class SmartPhraseIdiomRewriter
{
    internal const string NoTransformIdentity = "none";

    public static SmartPhraseModelInput Rewrite(
        string sourcePhrase,
        string target,
        int characterIndex)
    {
        if (string.IsNullOrWhiteSpace(sourcePhrase) ||
            string.IsNullOrWhiteSpace(target) ||
            characterIndex < 0 ||
            characterIndex + target.Length > sourcePhrase.Length ||
            !string.Equals(
                sourcePhrase.Substring(characterIndex, target.Length),
                target,
                StringComparison.OrdinalIgnoreCase))
        {
            return Unchanged(sourcePhrase ?? string.Empty);
        }

        List<RewriteCandidate> candidates = [];
        AddButterCandidates(sourcePhrase, characterIndex, target.Length, candidates);

        // Multiple overlapping interpretations are deliberately a no-op. The
        // direct model is safer than an arbitrary deterministic rewrite.
        if (candidates.Count != 1)
        {
            return Unchanged(sourcePhrase);
        }

        RewriteCandidate candidate = candidates[0];
        string rewritten = string.Concat(
            sourcePhrase.AsSpan(0, candidate.Start),
            candidate.Replacement,
            sourcePhrase.AsSpan(candidate.Start + candidate.Length));
        if (!IsValidRewrite(sourcePhrase, rewritten))
        {
            return Unchanged(sourcePhrase);
        }

        return new SmartPhraseModelInput(
            sourcePhrase,
            rewritten,
            candidate.Identity);
    }

    private static void AddButterCandidates(
        string source,
        int targetStart,
        int targetLength,
        List<RewriteCandidate> candidates)
    {
        foreach (Match match in ButterObjectUpRegex().Matches(source))
        {
            if (!Overlaps(match, targetStart, targetLength))
            {
                continue;
            }

            Match subjectMatch = HumanSubjectBeforeIdiomRegex().Match(
                source[..match.Index]);
            if (!subjectMatch.Success)
            {
                continue;
            }

            string verb = match.Groups["verb"].Value;
            Group attempt = subjectMatch.Groups["attempt"];
            string replacementVerb = verb.ToLowerInvariant() switch
            {
                "butter" => "flatter",
                "butters" => "flatters",
                "buttered" => "flattered",
                "buttering" => "flattering",
                _ => string.Empty
            };
            if (attempt.Success)
            {
                replacementVerb = attempt.Value.ToLowerInvariant() switch
                {
                    "try to" => "flatter",
                    "tries to" => "flatters",
                    "tried to" => "flattered",
                    "trying to" => "flattering",
                    _ => string.Empty
                };
            }

            if (replacementVerb.Length == 0)
            {
                continue;
            }

            replacementVerb = MatchInitialCase(
                attempt.Success ? attempt.Value : verb,
                replacementVerb);
            string objectPronoun = match.Groups["object"].Value;
            string subject = subjectMatch.Groups["subject"].Value.ToLowerInvariant();
            string purposeSubject = subject == "i" ? "I" : subject;
            bool pastPurpose = verb.Equals("buttered", StringComparison.OrdinalIgnoreCase) ||
                (attempt.Success &&
                 attempt.Value.StartsWith("tried", StringComparison.OrdinalIgnoreCase));
            string purposeVerb = pastPurpose
                ? "wanted"
                : subject is "he" or "she" ? "wants" : "want";

            int rewriteStart = attempt.Success ? attempt.Index : match.Index;
            candidates.Add(new RewriteCandidate(
                rewriteStart,
                match.Index + match.Length - rewriteStart,
                replacementVerb + " " + objectPronoun + " to get what " +
                purposeSubject + " " + purposeVerb,
                "idiom:butter-object-up@v4"));
        }
    }

    private static bool IsValidRewrite(string source, string rewritten)
    {
        if (string.Equals(source, rewritten, StringComparison.Ordinal) ||
            rewritten.Contains("<start_of_turn>", StringComparison.Ordinal) ||
            rewritten.Contains("<end_of_turn>", StringComparison.Ordinal) ||
            rewritten.Any(character => char.IsControl(character)))
        {
            return false;
        }

        int wordCount = EnglishContextLexicon.TokenRegex().Matches(rewritten).Count;
        return wordCount is >= 2 and <= SmartPhraseContextBuilder.MaximumWords;
    }

    private static bool Overlaps(Match match, int targetStart, int targetLength) =>
        match.Index < targetStart + targetLength &&
        targetStart < match.Index + match.Length;

    private static string MatchInitialCase(string source, string replacement) =>
        source.Length > 0 && char.IsUpper(source[0])
            ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
            : replacement;

    private static SmartPhraseModelInput Unchanged(string source) =>
        new(source, source, NoTransformIdentity);

    private sealed record RewriteCandidate(
        int Start,
        int Length,
        string Replacement,
        string Identity);

    [GeneratedRegex(
        @"(?<![A-Za-z])(?<verb>butter|butters|buttered|buttering)\s+(?<object>me|you|him|her|us|them)\s+up(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ButterObjectUpRegex();

    [GeneratedRegex(
        @"(?<![A-Za-z])(?<subject>i|you|he|she|we|they)(?:['’](?:m|re|s|d|ll|ve)|\s+(?:am|are|is|was|were|have|has|had|will|would|can|could|may|might|must|should|do|does|did))?(?:\s+(?:really|just|always|still|probably|maybe))*(?:\s+(?<attempt>(?:try|tries|tried|trying)\s+to))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HumanSubjectBeforeIdiomRegex();

}
