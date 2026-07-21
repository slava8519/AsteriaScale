using AsteriaScale.RankerWorker;

internal static class RankerWorkerRegressionTests
{
    public static Task RunAsync()
    {
        VerifyNpuNeverUsesCpuPrefilter();
        VerifyMissingGlossFailsOpen();
        VerifyClosestTargetOccurrenceIsMarked();
        return Task.CompletedTask;
    }

    private static void VerifyNpuNeverUsesCpuPrefilter()
    {
        Check(!RankerSelectionLogic.ShouldApplyBiEncoder(cpuProvider: false, 64),
            "An NPU selection was allowed to use the CPU-only bi-encoder prefilter.");
        Check(RankerSelectionLogic.ShouldApplyBiEncoder(cpuProvider: true, 17),
            "A large CPU inventory did not enable its pinned bi-encoder prefilter.");
        Check(!RankerSelectionLogic.ShouldApplyBiEncoder(cpuProvider: true, 16),
            "The CPU prefilter engaged below its calibrated inventory threshold.");
    }

    private static void VerifyMissingGlossFailsOpen()
    {
        int[] groupIds = Enumerable.Range(0, 17).ToArray();
        double?[] similarities = Enumerable.Range(0, 17)
            .Select(index => index < 12 ? (double?)null : index / 100.0)
            .ToArray();

        HashSet<int>? kept = RankerSelectionLogic.SelectPrefilterGroups(
            groupIds,
            similarities);
        Check(kept is null,
            "Missing gloss hashes pruned covered groups instead of restoring the full inventory.");

        double?[] complete = Enumerable.Range(0, 17)
            .Select(index => (double?)index)
            .ToArray();
        kept = RankerSelectionLogic.SelectPrefilterGroups(groupIds, complete);
        Check(kept is { Count: 12 } && kept.Contains(16) && !kept.Contains(0),
            "A fully covered inventory did not retain the twelve highest-scoring groups.");

        complete[0] = double.NaN;
        Check(RankerSelectionLogic.SelectPrefilterGroups(groupIds, complete) is null,
            "A NaN embedding similarity pruned the inventory instead of failing open.");
        complete[0] = double.PositiveInfinity;
        Check(RankerSelectionLogic.SelectPrefilterGroups(groupIds, complete) is null,
            "An infinite embedding similarity pruned the inventory instead of failing open.");
    }

    private static void VerifyClosestTargetOccurrenceIsMarked()
    {
        const string context =
            "He sat by the bank, then visited the bank for a loan.";
        int second = context.LastIndexOf("bank", StringComparison.Ordinal);
        int selected = RankerSelectionLogic.FindClosestTargetOccurrence(
            context,
            "bank",
            second + 2);
        Check(selected == second,
            "The ranker marked the first repeated target instead of the hovered occurrence.");

        const string substringContext = "The answer he gave was brief.";
        selected = RankerSelectionLogic.FindClosestTargetOccurrence(
            substringContext,
            "he",
            1);
        Check(selected == substringContext.IndexOf("he", 4, StringComparison.Ordinal),
            "The ranker accepted a target substring inside a different English token.");

        const string combiningContext = "The cafe\u0301 closed; the cafe opened.";
        int plainCafe = combiningContext.LastIndexOf("cafe", StringComparison.Ordinal);
        selected = RankerSelectionLogic.FindClosestTargetOccurrence(
            combiningContext,
            "cafe",
            4);
        Check(selected == plainCafe,
            "A combining mark was treated as a token boundary for target anchoring.");

        const string accentedContext = "The na\u00EFve answer differed.";
        selected = RankerSelectionLogic.FindClosestTargetOccurrence(
            accentedContext,
            "ve",
            accentedContext.IndexOf("ve", StringComparison.Ordinal));
        Check(selected == -1,
            "A target substring inside a Unicode English token was accepted.");

        const string astralContext = "𐐀bank and bank𐐀";
        selected = RankerSelectionLogic.FindClosestTargetOccurrence(
            astralContext,
            "bank",
            3);
        Check(selected == -1,
            "An astral Unicode letter was treated as a token boundary.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
