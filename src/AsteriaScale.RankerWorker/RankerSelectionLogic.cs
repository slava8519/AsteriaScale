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

    public static float[] AverageSandwichProbabilities(
        IReadOnlyList<float> nonVerbProbabilities,
        IReadOnlyList<float> verbProbabilities)
    {
        if (nonVerbProbabilities.Count != verbProbabilities.Count)
        {
            throw new ArgumentException(
                "SANDWiCH encoder outputs must have the same length.",
                nameof(verbProbabilities));
        }

        var combined = new float[nonVerbProbabilities.Count];
        for (var index = 0; index < combined.Length; index++)
        {
            float nonVerb = nonVerbProbabilities[index];
            float verb = verbProbabilities[index];
            if (!float.IsFinite(nonVerb) || nonVerb is < 0 or > 1 ||
                !float.IsFinite(verb) || verb is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nonVerbProbabilities),
                    "SANDWiCH encoder outputs must be finite probabilities.");
            }

            combined[index] = (nonVerb + verb) / 2.0f;
        }

        return combined;
    }

    /// <summary>
    /// Applies the upstream SANDWiCH cluster reducer to Asteria's stable
    /// outcome groups. Each member is weighted by a softmax over its binary
    /// confidence |p(positive)-p(negative)|; this is intentionally not a max
    /// reducer because the model was trained around neighbourhood evidence.
    /// </summary>
    public static IReadOnlyList<GroupScore> AggregateSandwichGroups(
        IReadOnlyList<float> probabilities,
        IReadOnlyList<int> groupIds)
    {
        if (probabilities.Count != groupIds.Count)
        {
            throw new ArgumentException(
                "SANDWiCH probabilities and group ids must have the same length.",
                nameof(groupIds));
        }

        var groupOrder = new List<int>();
        var members = new Dictionary<int, List<double>>();
        for (var index = 0; index < probabilities.Count; index++)
        {
            float probability = probabilities[index];
            if (!float.IsFinite(probability) || probability is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(probabilities),
                    "SANDWiCH group members must be finite probabilities.");
            }

            int groupId = groupIds[index];
            if (!members.TryGetValue(groupId, out List<double>? groupMembers))
            {
                groupOrder.Add(groupId);
                groupMembers = [];
                members.Add(groupId, groupMembers);
            }

            groupMembers.Add(probability);
        }

        return groupOrder
            .Select((groupId, order) => new GroupScore(
                groupId,
                AggregateSandwichCluster(members[groupId]),
                order))
            .OrderByDescending(group => group.Score)
            .ThenBy(group => group.Order)
            .ToArray();
    }

    private static double AggregateSandwichCluster(IReadOnlyList<double> probabilities)
    {
        double maximumConfidence = probabilities.Max(
            probability => Math.Abs((2.0 * probability) - 1.0));
        double weightSum = 0;
        double weightedProbabilitySum = 0;
        foreach (double probability in probabilities)
        {
            double confidence = Math.Abs((2.0 * probability) - 1.0);
            double weight = Math.Exp(confidence - maximumConfidence);
            weightSum += weight;
            weightedProbabilitySum += probability * weight;
        }

        return weightedProbabilitySum / weightSum;
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
