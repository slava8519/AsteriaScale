using System.Globalization;
using System.Text;

namespace AsteriaScale.RankerProtocol;

/// <summary>
/// Resolves which occurrence of a target surface a request refers to.
///
/// This lives in the shared protocol assembly because two processes must agree
/// on it exactly: the worker anchors the marked target here, and the host uses
/// the same resolution to canonicalize a decision cache key. A host-side copy
/// that drifted by even one occurrence would let a cached decision be reused
/// for an anchor the worker never scored.
/// </summary>
public static class RankerTargetAnchor
{
    /// <summary>
    /// Builds the exact context string scored by the worker, including target
    /// quotes and outer trimming. Keeping this beside anchor resolution lets the
    /// host key the post-normalization model input without applying a raw UTF-16
    /// cursor index to text whose controls or whitespace have already changed.
    /// </summary>
    public static bool TryBuildMarkedContext(
        string context,
        string target,
        int characterIndex,
        out string markedContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        int index = FindClosestOccurrence(context, target, characterIndex);
        if (index < 0)
        {
            markedContext = context.Trim();
            return false;
        }

        markedContext = (
            context[..index] +
            "\"" + context.Substring(index, target.Length) + "\"" +
            context[(index + target.Length)..]).Trim();
        return true;
    }

    /// <summary>
    /// Returns the start index of the target occurrence closest to
    /// <paramref name="characterIndex"/>, or -1 when the target does not appear
    /// as a standalone token. Occurrences glued to surrounding word characters
    /// ("bank" inside "embankment") are never anchors.
    /// </summary>
    public static int FindClosestOccurrence(
        string context,
        string target,
        int characterIndex)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Length == 0 || target.Length > context.Length)
        {
            return -1;
        }

        int closest = -1;
        int closestDistance = int.MaxValue;
        for (int start = 0; start <= context.Length - target.Length;)
        {
            int index = context.IndexOf(
                target,
                start,
                StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            int end = index + target.Length;
            if (IsTokenCharacterBefore(context, index) ||
                IsTokenCharacterAt(context, end))
            {
                start = index + Math.Max(1, target.Length);
                continue;
            }

            int distance = characterIndex >= index && characterIndex <= end
                ? 0
                : Math.Min(
                    Math.Abs(characterIndex - index),
                    Math.Abs(characterIndex - end));
            if (distance < closestDistance)
            {
                closest = index;
                closestDistance = distance;
            }

            start = index + Math.Max(1, target.Length);
        }

        return closest;
    }

    // The anchor must accept every target the dictionary lookup tokenizer
    // produces (LocalDictionaryService.EnglishLookupTokenRegex): a token is a
    // run of letters, marks and digits, and only ', ’ and U+002D join two such
    // runs. A quote or hyphen-minus at a target edge is therefore part of the
    // word only when a word character lies beyond it ("don" in "don't",
    // "known" in "well-known", "clock" in "o'clock"). Every other dash, and a
    // quote that opens or closes a quotation, is a boundary: "don't" inside
    // 'don't go there', "pitchers" before a closing ’, "don't" after "Wait—"
    // and "pitcher" before "--" are whole targets. Counting those characters
    // as letters made the worker reject a lookup target as absent from its
    // own context. U+02BC is a modifier letter and already counts as one.
    // One mismatch remains on purpose: a letter outside the BMP is a word
    // character here, while the lookup's UTF-16 regex sees two surrogates and
    // cuts before it, so a target glued to such a letter is still refused.
    private static bool IsTokenCharacterBefore(string value, int index)
    {
        int scalarStart = ScalarStartBefore(value, index);
        if (scalarStart < 0 ||
            !Rune.TryGetRuneAt(value, scalarStart, out Rune rune))
        {
            return false;
        }

        if (IsWordCharacter(rune))
        {
            return true;
        }

        int beyond = ScalarStartBefore(value, scalarStart);
        return IsJoiner(rune) &&
               beyond >= 0 &&
               Rune.TryGetRuneAt(value, beyond, out Rune beyondRune) &&
               IsWordCharacter(beyondRune);
    }

    private static bool IsTokenCharacterAt(string value, int index)
    {
        if (index >= value.Length ||
            !Rune.TryGetRuneAt(value, index, out Rune rune))
        {
            return false;
        }

        if (IsWordCharacter(rune))
        {
            return true;
        }

        int beyond = index + rune.Utf16SequenceLength;
        return IsJoiner(rune) &&
               beyond < value.Length &&
               Rune.TryGetRuneAt(value, beyond, out Rune beyondRune) &&
               IsWordCharacter(beyondRune);
    }

    private static int ScalarStartBefore(string value, int index)
    {
        if (index <= 0)
        {
            return -1;
        }

        int scalarStart = index - 1;
        if (char.IsLowSurrogate(value[scalarStart]) &&
            scalarStart > 0 &&
            char.IsHighSurrogate(value[scalarStart - 1]))
        {
            scalarStart--;
        }

        return scalarStart;
    }

    private static bool IsWordCharacter(Rune value)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(value);
        return Rune.IsLetterOrDigit(value) ||
               category is UnicodeCategory.NonSpacingMark or
                   UnicodeCategory.SpacingCombiningMark or
                   UnicodeCategory.EnclosingMark;
    }

    private static bool IsJoiner(Rune value) =>
        value.Value is '\'' or '’' or '-';
}
