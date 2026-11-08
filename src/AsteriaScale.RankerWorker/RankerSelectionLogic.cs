using AsteriaScale.RankerProtocol;

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

    // The host canonicalizes decision cache keys with the same resolution, so
    // the implementation is shared through the protocol assembly rather than
    // duplicated on each side of the process boundary.
    public static int FindClosestTargetOccurrence(
        string context,
        string target,
        int characterIndex) =>
        RankerTargetAnchor.FindClosestOccurrence(context, target, characterIndex);
}

internal readonly record struct GroupScore(int GroupId, double Score, int Order);
