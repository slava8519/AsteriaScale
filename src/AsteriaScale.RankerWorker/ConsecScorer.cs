using AsteriaScale.RankerProtocol;

namespace AsteriaScale.RankerWorker;

internal static class ConsecScorer
{
    internal static ConsecSelection Select(
        IReadOnlyList<float> tokenLogits,
        IReadOnlyList<int> markerPositions,
        IReadOnlyList<int> candidateGroupIds)
    {
        if (markerPositions.Count == 0 ||
            markerPositions.Count != candidateGroupIds.Count)
        {
            throw new RankerException(
                "consec_invalid_output",
                "ConSeC marker positions and candidate groups must have the same non-zero size.");
        }

        float[] probabilities = SoftmaxMarkerLogits(
            tokenLogits,
            markerPositions);
        IReadOnlyList<GroupScore> groups = AggregateGroups(
            probabilities,
            candidateGroupIds);
        GroupScore best = groups[0];
        double margin = groups.Count == 1
            ? 1.0
            : best.Score - groups[1].Score;
        bool abstained = groups.Count > 1 &&
            margin < RankerAbstention.MarginThreshold;
        return new ConsecSelection(
            abstained ? null : best.GroupId,
            abstained,
            margin,
            best.Score);
    }

    internal static float[] SoftmaxMarkerLogits(
        IReadOnlyList<float> tokenLogits,
        IReadOnlyList<int> markerPositions)
    {
        if (tokenLogits.Count == 0 || markerPositions.Count == 0)
        {
            throw new RankerException(
                "consec_invalid_output",
                "ConSeC returned no token logits or candidate markers.");
        }

        float maximum = float.NegativeInfinity;
        foreach (int position in markerPositions)
        {
            if (position < 0 || position >= tokenLogits.Count)
            {
                throw new RankerException(
                    "consec_invalid_output",
                    $"ConSeC candidate marker {position} is outside the token-logit vector.");
            }

            float logit = tokenLogits[position];
            if (!float.IsFinite(logit))
            {
                throw new RankerException(
                    "consec_invalid_output",
                    "ConSeC returned a non-finite candidate logit.");
            }
            maximum = Math.Max(maximum, logit);
        }

        var probabilities = new float[markerPositions.Count];
        double sum = 0;
        for (int index = 0; index < markerPositions.Count; index++)
        {
            double value = Math.Exp(tokenLogits[markerPositions[index]] - maximum);
            probabilities[index] = (float)value;
            sum += value;
        }
        if (!double.IsFinite(sum) || sum <= 0)
        {
            throw new RankerException(
                "consec_invalid_output",
                "ConSeC candidate logits could not be normalized.");
        }
        for (int index = 0; index < probabilities.Length; index++)
        {
            probabilities[index] = (float)(probabilities[index] / sum);
        }
        return probabilities;
    }

    internal static IReadOnlyList<GroupScore> AggregateGroups(
        IReadOnlyList<float> probabilities,
        IReadOnlyList<int> candidateGroupIds)
    {
        if (probabilities.Count == 0 ||
            probabilities.Count != candidateGroupIds.Count)
        {
            throw new RankerException(
                "consec_invalid_output",
                "ConSeC probabilities and candidate groups must have the same non-zero size.");
        }

        var groupOrder = new List<int>();
        var maxima = new Dictionary<int, double>();
        for (int index = 0; index < probabilities.Count; index++)
        {
            float probability = probabilities[index];
            if (!float.IsFinite(probability) || probability is < 0 or > 1)
            {
                throw new RankerException(
                    "consec_invalid_output",
                    "ConSeC candidate probabilities must be finite values in [0,1].");
            }

            int groupId = candidateGroupIds[index];
            if (!maxima.TryGetValue(groupId, out double current))
            {
                groupOrder.Add(groupId);
                maxima[groupId] = probability;
            }
            else if (probability > current)
            {
                maxima[groupId] = probability;
            }
        }

        return groupOrder
            .Select((groupId, order) => new GroupScore(
                groupId,
                maxima[groupId],
                order))
            .OrderByDescending(group => group.Score)
            .ThenBy(group => group.Order)
            .ToArray();
    }
}

internal sealed record ConsecSelection(
    int? GroupId,
    bool Abstained,
    double Margin,
    double Score);
