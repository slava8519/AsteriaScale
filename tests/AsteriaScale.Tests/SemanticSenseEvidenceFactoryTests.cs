using AsteriaScale.Services;

internal static class SemanticSenseEvidenceFactoryTests
{
    public static Task RunAsync()
    {
        SmartDictionaryRankerPlan plan = new(
            [
                new SmartDictionaryRankerOutcome(0, 0, "банк"),
                new SmartDictionaryRankerOutcome(1, 2, "берег")
            ],
            [
                "A financial institution that receives deposits.",
                "A business that keeps and lends money.",
                "The sloping land beside a river."
            ],
            [0, 0, 1]);

        IReadOnlyList<ScaffoldSenseHint> evidence =
            SemanticSenseEvidenceFactory.Create("bank", plan, 0, 0.93);
        Check(evidence.Count == 2,
            "Semantic evidence dropped equivalent definitions from one GroupId.");
        Check(evidence.All(hint => hint.SelectedTranslation == "банк"),
            "Semantic evidence lost the selected Russian surface.");
        Check(evidence.All(hint =>
                hint.RejectedTranslations.SequenceEqual(["берег"], StringComparer.Ordinal)),
            "Semantic evidence lost the rejected alternative.");
        Check(evidence.Select(hint => hint.EnglishDefinition).SequenceEqual(
                plan.CandidateGlosses.Take(2),
                StringComparer.Ordinal),
            "Semantic evidence no longer follows CandidateGroupIds in stable order.");
        Check(plan.Outcomes.Select(outcome => outcome.GroupId).SequenceEqual([0, 1]),
            "Semantic evidence renumbered ranker outcomes.");

        bool unknownRejected = false;
        try
        {
            _ = SemanticSenseEvidenceFactory.Create("bank", plan, 7, 0.5);
        }
        catch (InvalidOperationException)
        {
            unknownRejected = true;
        }

        Check(unknownRejected, "Unknown semantic GroupId was accepted.");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
