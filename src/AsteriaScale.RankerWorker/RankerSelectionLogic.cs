using System.Globalization;
using System.Text;

namespace AsteriaScale.RankerWorker;

/// <summary>
/// Pure selection primitives kept separate from ONNX state so fail-open and
/// cursor-anchoring invariants can be exercised without loading native models.
/// </summary>
internal static class RankerSelectionLogic
{
    private const int PrefilterTopGroups = 12;
    private const int PrefilterEngageGroups = 16;

    public static bool ShouldApplyBiEncoder(bool cpuProvider, int distinctGroupCount) =>
        cpuProvider && distinctGroupCount > PrefilterEngageGroups;

    public static HashSet<int>? SelectPrefilterGroups(
        IReadOnlyList<int> groupIds,
        IReadOnlyList<double?> similarities)
    {
        if (groupIds.Count != similarities.Count)
        {
            throw new ArgumentException(
                "Group ids and similarities must have the same length.",
                nameof(similarities));
        }

        if (similarities.Any(value => value is null || !double.IsFinite(value.Value)))
        {
            return null;
        }

        var groupBest = new Dictionary<int, double>();
        for (var index = 0; index < groupIds.Count; index++)
        {
            double similarity = similarities[index]!.Value;
            groupBest[groupIds[index]] = Math.Max(
                groupBest.GetValueOrDefault(groupIds[index], double.NegativeInfinity),
                similarity);
        }

        return groupBest
            .OrderByDescending(entry => entry.Value)
            .Take(PrefilterTopGroups)
            .Select(entry => entry.Key)
            .ToHashSet();
    }

    public static int FindClosestTargetOccurrence(
        string context,
        string target,
        int characterIndex)
    {
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
            if (IsEnglishTokenCharacterBefore(context, index) ||
                IsEnglishTokenCharacterAt(context, end))
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

    private static bool IsEnglishTokenCharacterBefore(string value, int index)
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
               IsEnglishTokenCharacter(rune);
    }

    private static bool IsEnglishTokenCharacterAt(string value, int index) =>
        index < value.Length &&
        Rune.TryGetRuneAt(value, index, out Rune rune) &&
        IsEnglishTokenCharacter(rune);

    private static bool IsEnglishTokenCharacter(Rune value)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(value);
        return Rune.IsLetterOrDigit(value) ||
               category is UnicodeCategory.NonSpacingMark or
                   UnicodeCategory.SpacingCombiningMark or
                   UnicodeCategory.EnclosingMark or
                   UnicodeCategory.DashPunctuation ||
               value.Value is '\'' or '\u2019' or '\u02BC';
    }
}
