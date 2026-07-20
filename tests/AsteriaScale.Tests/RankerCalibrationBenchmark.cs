using System.Globalization;
using System.Text.Json;
using AsteriaScale.Services;

/// <summary>
/// Drives the production Smart-dictionary pipeline (sense sidecar, grouping,
/// ranker plan, isolated GlossBERT worker) over a CC0 regression set and
/// reports accepted-decision precision and abstention split by inventory size.
/// Used to calibrate the margin-only abstention policy on full Wiktionary
/// inventories; see assets/Ranker/README.md.
/// </summary>
internal static class RankerCalibrationBenchmark
{
    private sealed record BenchmarkCase(
        string Id,
        string Context,
        string Target,
        int CharacterIndex,
        string ExpectedTranslation);

    private sealed record CaseResult(
        string Id,
        int GlossCount,
        int OutcomeCount,
        bool ExpectedCovered,
        bool Abstained,
        double? Margin,
        bool? Correct,
        string Selected);

    public static async Task RunAsync(
        string benchmarkPath,
        string workerBaseDirectory,
        GlossBertRankerProvider provider)
    {
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(benchmarkPath));
        BenchmarkCase[] cases = document.RootElement.GetProperty("cases")
            .EnumerateArray()
            .Select(element => new BenchmarkCase(
                element.GetProperty("id").GetString()!,
                element.GetProperty("context").GetString()!,
                element.GetProperty("target").GetString()!,
                element.GetProperty("characterIndex").GetInt32(),
                element.GetProperty("expectedTranslation").GetString()!))
            .ToArray();

        LocalDictionaryService dictionary = new();
        await dictionary.LoadSenseMetadataAsync();
        await using GlossBertDictionarySelectorService selector = new(
            workerBaseDirectory,
            TimeSpan.FromMinutes(10));

        List<CaseResult> results = [];
        int skippedNoInventory = 0;
        int skippedPhrase = 0;
        int autoResolved = 0;
        foreach (BenchmarkCase benchmarkCase in cases)
        {
            DictionaryLookupResult lookup = await dictionary.LookupWithSenseMetadataAsync(
                benchmarkCase.Context,
                benchmarkCase.Target,
                benchmarkCase.CharacterIndex);
            if (lookup.Found && lookup.IsPhrase)
            {
                skippedPhrase++;
                Console.WriteLine($"  {benchmarkCase.Id,-24} phrase-bypass ({lookup.Source})");
                continue;
            }

            if (!lookup.Found || lookup.SenseCandidates.Count == 0)
            {
                skippedNoInventory++;
                Console.WriteLine($"  {benchmarkCase.Id,-24} no aligned sense inventory");
                continue;
            }

            SmartDictionarySenseInventory inventory =
                SmartDictionarySenseResolver.GroupCandidates(
                    benchmarkCase.Context,
                    lookup.Source,
                    benchmarkCase.CharacterIndex,
                    lookup.SenseCandidates);
            SmartDictionaryRankerPlan plan =
                SmartDictionarySenseResolver.CreateRankerPlan(
                    benchmarkCase.Context,
                    lookup.Source,
                    benchmarkCase.CharacterIndex,
                    inventory.Groups);
            if (plan.Outcomes.Count < 2)
            {
                autoResolved++;
                Console.WriteLine(
                    $"  {benchmarkCase.Id,-24} auto-resolved without the model " +
                    $"({plan.Outcomes.FirstOrDefault()?.Translation ?? "<none>"})");
                continue;
            }

            string expected = NormalizeRussian(benchmarkCase.ExpectedTranslation);
            HashSet<int> expectedGroupIds = [];
            foreach (SmartDictionaryRankerOutcome outcome in plan.Outcomes)
            {
                if (NormalizeRussian(outcome.Translation) == expected)
                {
                    expectedGroupIds.Add(outcome.GroupId);
                }
            }

            SmartDictionarySenseGroup[] orderedSenses = inventory.Groups
                .OrderBy(group => group.SourceIndex)
                .ToArray();
            if (orderedSenses.Length == plan.CandidateGroupIds.Count)
            {
                for (int index = 0; index < orderedSenses.Length; index++)
                {
                    if (orderedSenses[index].Surfaces.Any(surface =>
                            NormalizeRussian(surface.Translation) == expected))
                    {
                        expectedGroupIds.Add(plan.CandidateGroupIds[index]);
                    }
                }
            }

            GlossBertDictionarySelection selection = await selector.SelectAsync(
                benchmarkCase.Context,
                lookup.Source,
                benchmarkCase.CharacterIndex,
                plan.CandidateGlosses,
                plan.CandidateGroupIds,
                "English",
                provider);
            string selected = selection.SelectedGroupId is { } groupId
                ? plan.Outcomes.First(outcome => outcome.GroupId == groupId).Translation
                : "<abstained>";
            bool? correct = selection.Abstained || expectedGroupIds.Count == 0
                ? null
                : expectedGroupIds.Contains(selection.SelectedGroupId!.Value);
            CaseResult result = new(
                benchmarkCase.Id,
                plan.CandidateGlosses.Count,
                plan.Outcomes.Count,
                expectedGroupIds.Count > 0,
                selection.Abstained,
                selection.Margin,
                correct,
                selected);
            results.Add(result);
            Console.WriteLine(
                $"  {result.Id,-24} glosses={result.GlossCount,3} outcomes={result.OutcomeCount,3} " +
                $"margin={FormatMargin(result.Margin),6} " +
                (result.Abstained
                    ? "abstained"
                    : $"selected='{result.Selected}' correct={FormatCorrect(result.Correct)}") +
                $" expected='{benchmarkCase.ExpectedTranslation}'" +
                (result.ExpectedCovered ? "" : " [expected-not-covered]"));
        }

        Console.WriteLine();
        Console.WriteLine(
            $"  cases={cases.Length} ranked={results.Count} phrase-bypass={skippedPhrase} " +
            $"no-inventory={skippedNoInventory} auto-resolved={autoResolved}");
        PrintBucket("small inventories (<=32 glosses)", results.Where(result => result.GlossCount <= 32));
        PrintBucket("large inventories (>32 glosses)", results.Where(result => result.GlossCount > 32));
        PrintBucket("all ranked", results);
    }

    private static void PrintBucket(string label, IEnumerable<CaseResult> bucket)
    {
        CaseResult[] items = bucket.ToArray();
        if (items.Length == 0)
        {
            Console.WriteLine($"  {label}: none");
            return;
        }

        CaseResult[] covered = items.Where(item => item.ExpectedCovered).ToArray();
        CaseResult[] decided = covered.Where(item => !item.Abstained).ToArray();
        int correct = decided.Count(item => item.Correct == true);
        double[] margins = items
            .Where(item => item.Margin is { } margin && double.IsFinite(margin))
            .Select(item => item.Margin!.Value)
            .OrderBy(value => value)
            .ToArray();
        string medianMargin = margins.Length == 0
            ? "n/a"
            : margins[margins.Length / 2].ToString("0.000", CultureInfo.InvariantCulture);
        Console.WriteLine(
            $"  {label}: ranked={items.Length} labeled={covered.Length} " +
            $"decided={decided.Length} correct={correct} " +
            $"abstained={covered.Length - decided.Length} medianMargin={medianMargin}");
    }

    private static string FormatMargin(double? margin) =>
        margin is { } value && double.IsFinite(value)
            ? value.ToString("0.000", CultureInfo.InvariantCulture)
            : "n/a";

    private static string FormatCorrect(bool? correct) =>
        correct is null ? "unlabeled" : correct.Value ? "yes" : "NO";

    private static string NormalizeRussian(string value) =>
        string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .ToLowerInvariant()
        .Replace('ё', 'е');
}
