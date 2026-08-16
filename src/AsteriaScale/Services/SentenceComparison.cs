using System.Text;
using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

/// <summary>
/// Token-level comparison of two observations of one on-screen utterance.
/// Two consumers ask exactly this question and must answer it identically: the
/// OCR stabilizer compares two captures of the same frame, and the text-hook
/// aligner compares an OCR sentence against a Textractor line. Both accept a
/// richer rendering of the same words and reject a different utterance, so a
/// second copy that drifted would let one path bind text the other refused.
/// </summary>
internal static partial class SentenceComparison
{
    /// <summary>
    /// Case- and apostrophe-folded word tokens. Punctuation, spacing and line
    /// wrapping differ freely between two renderings of one line, so only the
    /// words themselves may carry the comparison.
    /// </summary>
    internal static string[] Tokens(string? value) =>
        TokenRegex()
            .Matches((value ?? string.Empty).Normalize(NormalizationForm.FormC))
            .Select(match => LocalDictionaryService.NormalizeEnglish(match.Value))
            .ToArray();

    /// <summary>
    /// Whether every token of <paramref name="candidate"/> appears in
    /// <paramref name="sequence"/> in the same order. A partially revealed or
    /// clipped observation is therefore compatible with the complete one, while
    /// a substituted or reordered word is not.
    /// </summary>
    internal static bool IsOrderedSubsequence(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string> sequence)
    {
        if (candidate.Count > sequence.Count)
        {
            return false;
        }

        int candidateIndex = 0;
        foreach (string token in sequence)
        {
            if (candidateIndex < candidate.Count &&
                string.Equals(candidate[candidateIndex], token, StringComparison.Ordinal))
            {
                candidateIndex++;
            }
        }

        return candidateIndex == candidate.Count;
    }

    [GeneratedRegex(
        @"[\p{L}\p{M}\p{N}]+(?:['’\-][\p{L}\p{M}\p{N}]+)*",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
