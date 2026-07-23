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
    // Recorded CPU/NPU baselines stay above this floor, including the older
    // frozen confirmation sets. The gate detects real selection regressions
    // without changing the runtime's margin-only abstention contract.
    private const double MinimumAcceptedDecisionPrecision = 0.90;
    private const int MinimumBenchmarkCaseCount = 20;
    private const double MinimumExpectedInventoryCoverage = 0.70;
    private const double MinimumDecisionCoverage = 0.45;
    private const double MinimumEndToEndCorrectCoverage = 0.45;

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
        GlossBertRankerProvider provider,
        string? dumpPath = null)
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
        if (cases.Length == 0)
        {
            throw new InvalidDataException("The ranker benchmark contains no cases.");
        }

        string isolatedUserDirectory = Path.Combine(
            Path.GetTempPath(),
            "AsteriaScale",
            "ranker-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedUserDirectory);
        try
        {
            LocalDictionaryService dictionary = new(
                Path.Combine(AppContext.BaseDirectory, "Dictionaries"),
                isolatedUserDirectory);
            await dictionary.LoadSenseMetadataAsync();
            await using GlossBertDictionarySelectorService selector = new(
                workerBaseDirectory,
                TimeSpan.FromMinutes(10));

        List<CaseResult> results = [];
        List<(int Glosses, long Milliseconds)> selectLatencies = [];
        Dictionary<string, int> pipelineCounts = new(StringComparer.Ordinal);
        int skippedNoInventory = 0;
        int skippedPhrase = 0;
        int autoResolved = 0;
        int deterministicResolved = 0;
        int deterministicCorrect = 0;
        foreach (BenchmarkCase benchmarkCase in cases)
        {
            DictionaryLookupResult lookup = await dictionary.LookupWithSenseMetadataAsync(
                benchmarkCase.Context,
                benchmarkCase.Target,
                benchmarkCase.CharacterIndex);
            if (lookup.Found &&
                lookup.IsPhrase &&
                (lookup.IsTrustedPhrase || !lookup.HasMultipleMeanings))
            {
                skippedPhrase++;
                deterministicResolved++;
                bool phraseCorrect = TranslationContainsExpected(
                    lookup.Translation,
                    benchmarkCase.ExpectedTranslation);
                deterministicCorrect += phraseCorrect ? 1 : 0;
                Console.WriteLine(
                    $"  {benchmarkCase.Id,-24} phrase-bypass ({lookup.Source}) " +
                    $"correct={(phraseCorrect ? "yes" : "NO")}");
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
                SmartDictionaryRankerOutcome? outcome = plan.Outcomes.FirstOrDefault();
                if (outcome is not null)
                {
                    deterministicResolved++;
                    bool autoCorrect = RussianLabelsMatch(
                        outcome.Translation,
                        NormalizeRussian(benchmarkCase.ExpectedTranslation));
                    deterministicCorrect += autoCorrect ? 1 : 0;
                }
                Console.WriteLine(
                    $"  {benchmarkCase.Id,-24} auto-resolved without the model " +
                    $"({outcome?.Translation ?? "<none>"})" +
                    (outcome is null
                        ? string.Empty
                        : $" correct={(RussianLabelsMatch(outcome.Translation, NormalizeRussian(benchmarkCase.ExpectedTranslation)) ? "yes" : "NO")}"));
                continue;
            }

            string expected = NormalizeRussian(benchmarkCase.ExpectedTranslation);
            HashSet<int> expectedGroupIds = [];
            foreach (SmartDictionaryRankerOutcome outcome in plan.Outcomes)
            {
                if (RussianLabelsMatch(outcome.Translation, expected))
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
                            RussianLabelsMatch(surface.Translation, expected)))
                    {
                        expectedGroupIds.Add(plan.CandidateGroupIds[index]);
                    }
                }
            }

            System.Diagnostics.Stopwatch selectTimer = System.Diagnostics.Stopwatch.StartNew();
            GlossBertDictionarySelection selection = await selector.SelectAsync(
                benchmarkCase.Context,
                lookup.Source,
                benchmarkCase.CharacterIndex,
                plan.CandidateGlosses,
                plan.CandidateGroupIds,
                "English",
                provider);
            selectTimer.Stop();
            selectLatencies.Add((plan.CandidateGlosses.Count, selectTimer.ElapsedMilliseconds));
            pipelineCounts[selection.Pipeline] =
                pipelineCounts.GetValueOrDefault(selection.Pipeline) + 1;
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
        int coveredCount = results.Count(result => result.ExpectedCovered);
        int decidedCount = results.Count(result => result.ExpectedCovered && !result.Abstained);
        int correctCount = results.Count(result => result.Correct == true);
        Console.WriteLine(
            $"  cases={cases.Length} ranked={results.Count} phrase-bypass={skippedPhrase} " +
            $"no-inventory={skippedNoInventory} auto-resolved={autoResolved}");
        Console.WriteLine(
            $"  expected-label coverage: {coveredCount}/{results.Count} ranked cases");
        Console.WriteLine(
            $"  deterministic resolutions: {deterministicCorrect}/{deterministicResolved} correct; " +
            $"end-to-end correct coverage: " +
            $"{correctCount + deterministicCorrect}/{cases.Length}");
        if (selectLatencies.Count > 0)
        {
            long[] ordered = selectLatencies.Select(entry => entry.Milliseconds)
                .OrderBy(value => value)
                .ToArray();
            long[] large = selectLatencies.Where(entry => entry.Glosses > 12)
                .Select(entry => entry.Milliseconds)
                .OrderBy(value => value)
                .ToArray();
            Console.WriteLine(
                $"  select latency: median={ordered[ordered.Length / 2]} ms" +
                (large.Length > 0
                    ? $", large-inventory (>12 glosses) median={large[large.Length / 2]} ms"
                    : "") +
                $"; pipelines: {string.Join(", ", pipelineCounts.Select(entry => $"{entry.Key}={entry.Value}"))}");
        }
        if (dumpPath is not null)
        {
            await File.WriteAllTextAsync(
                dumpPath,
                System.Text.Json.JsonSerializer.Serialize(
                    results,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        PrintBucket("small inventories (<=32 glosses)", results.Where(result => result.GlossCount <= 32));
        PrintBucket("large inventories (>32 glosses)", results.Where(result => result.GlossCount > 32));
        PrintBucket("all ranked", results);
        VerifyMeasurementCounts(
            cases.Length,
            results.Count,
            coveredCount,
            decidedCount,
            correctCount,
            deterministicResolved,
            deterministicCorrect);
        }
        finally
        {
            try
            {
                Directory.Delete(isolatedUserDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Benchmark isolation matters more than best-effort temp cleanup.
            }
        }
    }

    internal static void VerifyMeasurementCounts(
        int caseCount,
        int rankedCount,
        int coveredCount,
        int decidedCount,
        int correctCount,
        int deterministicResolvedCount,
        int deterministicCorrectCount)
    {
        if (caseCount <= 0)
        {
            throw new InvalidDataException("The ranker benchmark contains no cases.");
        }

        if (rankedCount <= 0)
        {
            throw new InvalidDataException(
                "The ranker benchmark produced no rankable inventories.");
        }

        if (coveredCount <= 0)
        {
            throw new InvalidDataException(
                "The ranker benchmark covered none of its expected labels.");
        }

        if (decidedCount <= 0)
        {
            throw new InvalidDataException(
                "The ranker benchmark made no labeled decisions.");
        }

        if (correctCount < 0 || correctCount > decidedCount)
        {
            throw new InvalidDataException(
                "The ranker benchmark reported inconsistent correctness counts.");
        }

        if (rankedCount > caseCount || coveredCount > rankedCount ||
            decidedCount > coveredCount || deterministicResolvedCount < 0 ||
            deterministicResolvedCount > caseCount - rankedCount ||
            deterministicCorrectCount < 0 ||
            deterministicCorrectCount > deterministicResolvedCount)
        {
            throw new InvalidDataException(
                "The ranker benchmark reported inconsistent coverage counts.");
        }

        if (caseCount < MinimumBenchmarkCaseCount)
        {
            throw new InvalidOperationException(
                $"Ranker benchmark has only {caseCount} cases; at least " +
                $"{MinimumBenchmarkCaseCount} are required by the quality gate.");
        }

        double precision = correctCount / (double)decidedCount;
        if (precision < MinimumAcceptedDecisionPrecision)
        {
            throw new InvalidOperationException(
                $"Ranker accepted-decision precision {precision:P1} is below the " +
                $"{MinimumAcceptedDecisionPrecision:P0} quality gate " +
                $"({correctCount}/{decidedCount}).");
        }

        double inventoryCoverage = coveredCount / (double)rankedCount;
        if (inventoryCoverage < MinimumExpectedInventoryCoverage)
        {
            throw new InvalidOperationException(
                $"Ranker expected-inventory coverage {inventoryCoverage:P1} is below the " +
                $"{MinimumExpectedInventoryCoverage:P0} quality gate " +
                $"({coveredCount}/{rankedCount}).");
        }

        double decisionCoverage = decidedCount / (double)coveredCount;
        if (decisionCoverage < MinimumDecisionCoverage)
        {
            throw new InvalidOperationException(
                $"Ranker labeled-decision coverage {decisionCoverage:P1} is below the " +
                $"{MinimumDecisionCoverage:P0} quality gate " +
                $"({decidedCount}/{coveredCount}).");
        }

        int endToEndCorrect = correctCount + deterministicCorrectCount;
        double endToEndCorrectCoverage = endToEndCorrect / (double)caseCount;
        if (endToEndCorrectCoverage < MinimumEndToEndCorrectCoverage)
        {
            throw new InvalidOperationException(
                $"Ranker end-to-end correct coverage {endToEndCorrectCoverage:P1} is below the " +
                $"{MinimumEndToEndCorrectCoverage:P0} quality gate " +
                $"({endToEndCorrect}/{caseCount}).");
        }
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

    /// <summary>
    /// Inflection-tolerant label comparison for single words: the runtime
    /// shows the dictionary lemma, so "серьёзный" correctly serves an
    /// expected "серьёзным". Multi-word labels stay exact.
    /// </summary>
    private static bool RussianLabelsMatch(string candidate, string normalizedExpected)
    {
        string a = NormalizeRussian(candidate);
        string b = normalizedExpected;
        if (a == b)
        {
            return true;
        }

        if (a.Contains(' ') || b.Contains(' '))
        {
            return false;
        }

        int limit = Math.Min(a.Length, b.Length);
        if (limit < 4 || Math.Abs(a.Length - b.Length) > 4)
        {
            return false;
        }

        int prefix = 0;
        while (prefix < limit && a[prefix] == b[prefix])
        {
            prefix++;
        }

        return prefix >= (limit <= 5 ? 4 : 5);
    }

    private static bool TranslationContainsExpected(
        string translation,
        string expectedTranslation)
    {
        string expected = NormalizeRussian(expectedTranslation);
        return NpuDictionarySelectorService.SplitMeanings(translation)
            .Any(candidate => RussianLabelsMatch(candidate, expected));
    }

    private static string NormalizeRussian(string value) =>
        string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .ToLowerInvariant()
        .Replace('ё', 'е');
}
