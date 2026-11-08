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

    private static bool IsTokenCharacterBefore(string value, int index)
    {
        if (index <= 0)
        {
            return false;
        }

        int scalarStart = index - 1;
        if (char.IsLowSurrogate(value[scalarStart]) &&
            scalarStart > 0 &&
            char.IsHighSurrogate(value[scalarStart - 1]))
        {
            scalarStart--;
        }

        return Rune.TryGetRuneAt(value, scalarStart, out Rune rune) &&
               IsTokenCharacter(rune);
    }

    private static bool IsTokenCharacterAt(string value, int index) =>
        index < value.Length &&
        Rune.TryGetRuneAt(value, index, out Rune rune) &&
        IsTokenCharacter(rune);

    private static bool IsTokenCharacter(Rune value)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(value);
        return Rune.IsLetterOrDigit(value) ||
               category is UnicodeCategory.NonSpacingMark or
                   UnicodeCategory.SpacingCombiningMark or
                   UnicodeCategory.EnclosingMark or
                   UnicodeCategory.DashPunctuation ||
               value.Value is '\'' or '’' or 'ʼ';
    }
}
