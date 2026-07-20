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
    public static (RussianPastForm Form, string Stem) DetectPastForm(string word)
    {
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
        word.EndsWith("ть", StringComparison.Ordinal) ||
        word.EndsWith("ться", StringComparison.Ordinal) ||
        word.EndsWith("ти", StringComparison.Ordinal) ||
        word.EndsWith("чь", StringComparison.Ordinal);

    public static bool IsSemelfactiveInfinitive(string word) =>
        word.EndsWith("нуть", StringComparison.Ordinal) ||
        word.EndsWith("нуться", StringComparison.Ordinal);

    [GeneratedRegex(
        @"[\p{IsCyrillic}]+(?:-[\p{IsCyrillic}]+)*",
        RegexOptions.CultureInvariant)]
    public static partial Regex TokenRegex();
}
