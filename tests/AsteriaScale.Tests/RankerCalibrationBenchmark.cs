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
        string Route,
        int GlossCount,
        int OutcomeCount,
        bool SenseExpectedCovered,
        bool VisibleExpectedCovered,
        bool Abstained,
        double? Margin,
        bool? SenseCorrect,
        bool? VisibleCorrect,
        string Selected,
        string ExpectedTranslation);

    internal readonly record struct RankedCorrectness(
        bool SenseExpectedCovered,
        bool VisibleExpectedCovered,
        bool? SenseCorrect,
        bool? VisibleCorrect);

    /// <summary>
    /// Machine-readable baseline record for roadmap #8. Its purpose is to make
    /// "same-build comparison" enforceable rather than aspirational: every
    /// bake-off candidate emits this exact schema, so a reviewer diffs two
    /// files instead of trusting two prose summaries taken on different days.
    /// Inputs are pinned by SHA-256 so a report cannot silently describe a
    /// different benchmark set or model artifact than it claims.
    /// </summary>
    private sealed record BaselineReport(
        int SchemaVersion,
        string GeneratedUtc,
        string Provider,
        string ModelProfile,
        string ModelVersion,
        string AbstentionTag,
        string DictionaryVersion,
        BenchmarkArtifactRecord BenchmarkSet,
        IReadOnlyList<BenchmarkArtifactRecord> RuntimeArtifacts,
        IReadOnlyList<BenchmarkArtifactRecord> DictionaryArtifacts,
        IReadOnlyList<BenchmarkArtifactRecord> ModelArtifacts,
        long ModelArtifactBytes,
        long WorkerPeakWorkingSetBytes,
        RoutingRecord Routing,
        DecisionRecord SenseWsd,
        VisibleTranslationRecord VisibleTranslations,
        LatencyRecord Latency,
        IReadOnlyDictionary<string, int> Pipelines);

    private sealed record RoutingRecord(
        int Cases,
        int Ranked,
        int PhraseBypass,
        int NoInventory,
        int AutoResolved,
        int DeterministicResolved,
        int DeterministicVisibleCorrect);

    private sealed record DecisionRecord(
        int ExpectedCovered,
        int Decided,
        int Correct,
        int Abstained,
        double? AcceptedPrecision,
        double? ExpectedInventoryCoverage,
        double? DecisionCoverage,
        double EndToEndCorrectCoverage,
        double? MedianMargin);

    private sealed record VisibleTranslationRecord(
        int ExpectedCovered,
        int Decided,
        int Correct,
        int Abstained,
        double? AcceptedPrecision,
        double? ExpectedOutcomeCoverage,
        double? DecisionCoverage,
        double RankedCorrectCoverage,
        double EndToEndCorrectCoverage);

    private sealed record LatencyRecord(
        long? MedianMilliseconds,
        long? LargeInventoryMedianMilliseconds,
        long? MaximumMilliseconds,
        int SampleCount);

    public static async Task RunAsync(
        string benchmarkPath,
        string workerBaseDirectory,
        GlossBertRankerProvider provider,
        string? dumpPath = null,
        string? reportPath = null,
        RankerCpuModelProfile cpuModelProfile = RankerCpuModelProfile.GlossBert)
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
                TimeSpan.FromMinutes(10),
                cpuModelProfile);

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
                    results.Add(new CaseResult(
                        benchmarkCase.Id,
                        "phrase-bypass",
                        0,
                        1,
                        SenseExpectedCovered: false,
                        VisibleExpectedCovered: phraseCorrect,
                        Abstained: false,
                        Margin: null,
                        SenseCorrect: null,
                        VisibleCorrect: phraseCorrect,
                        lookup.Translation,
                        benchmarkCase.ExpectedTranslation));
                    Console.WriteLine(
                        $"  {benchmarkCase.Id,-24} phrase-bypass ({lookup.Source}) " +
                        $"visible='{lookup.Translation}' " +
                        $"visibleCorrect={(phraseCorrect ? "yes" : "NO")} " +
                        $"expected='{benchmarkCase.ExpectedTranslation}'");
                    continue;
                }

                if (!lookup.Found || lookup.SenseCandidates.Count == 0)
                {
                    skippedNoInventory++;
                    results.Add(new CaseResult(
                        benchmarkCase.Id,
                        "no-inventory",
                        0,
                        0,
                        SenseExpectedCovered: false,
                        VisibleExpectedCovered: false,
                        Abstained: false,
                        Margin: null,
                        SenseCorrect: null,
                        VisibleCorrect: null,
                        "<none>",
                        benchmarkCase.ExpectedTranslation));
                    Console.WriteLine(
                        $"  {benchmarkCase.Id,-24} no aligned sense inventory " +
                        $"expected='{benchmarkCase.ExpectedTranslation}'");
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
                    bool? autoCorrect = null;
                    if (outcome is not null)
                    {
                        deterministicResolved++;
                        autoCorrect = RussianLabelsMatch(
                            outcome.Translation,
                            NormalizeRussian(benchmarkCase.ExpectedTranslation));
                        deterministicCorrect += autoCorrect.Value ? 1 : 0;
                    }
                    results.Add(new CaseResult(
                        benchmarkCase.Id,
                        "auto-resolved",
                        plan.CandidateGlosses.Count,
                        plan.Outcomes.Count,
                        SenseExpectedCovered: false,
                        VisibleExpectedCovered: autoCorrect == true,
                        Abstained: false,
                        Margin: null,
                        SenseCorrect: null,
                        VisibleCorrect: autoCorrect,
                        outcome?.Translation ?? "<none>",
                        benchmarkCase.ExpectedTranslation));
                    Console.WriteLine(
                        $"  {benchmarkCase.Id,-24} auto-resolved without the model " +
                        $"({outcome?.Translation ?? "<none>"})" +
                        (outcome is null
                            ? string.Empty
                            : $" visibleCorrect={(autoCorrect == true ? "yes" : "NO")}") +
                        $" expected='{benchmarkCase.ExpectedTranslation}'");
                    continue;
                }

                System.Diagnostics.Stopwatch selectTimer = System.Diagnostics.Stopwatch.StartNew();
                GlossBertDictionarySelection selection = await selector.SelectAsync(
                    benchmarkCase.Context,
                    lookup.Source,
                    benchmarkCase.CharacterIndex,
                    plan.GlossesFor(provider == GlossBertRankerProvider.Cpu),
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
                RankedCorrectness correctness = EvaluateRankedCorrectness(
                    plan,
                    inventory.Groups,
                    selection.SelectedGroupId,
                    selection.Abstained,
                    benchmarkCase.ExpectedTranslation);
                CaseResult result = new(
                    benchmarkCase.Id,
                    "ranked",
                    plan.CandidateGlosses.Count,
                    plan.Outcomes.Count,
                    correctness.SenseExpectedCovered,
                    correctness.VisibleExpectedCovered,
                    selection.Abstained,
                    selection.Margin,
                    correctness.SenseCorrect,
                    correctness.VisibleCorrect,
                    selected,
                    benchmarkCase.ExpectedTranslation);
                results.Add(result);
                Console.WriteLine(
                    $"  {result.Id,-24} glosses={result.GlossCount,3} outcomes={result.OutcomeCount,3} " +
                    $"margin={FormatMargin(result.Margin),6} " +
                    (result.Abstained
                        ? "abstained"
                        : $"selected='{result.Selected}' " +
                          $"senseCorrect={FormatCorrect(result.SenseCorrect)} " +
                          $"visibleCorrect={FormatCorrect(result.VisibleCorrect)}") +
                    $" expected='{benchmarkCase.ExpectedTranslation}'" +
                    (result.SenseExpectedCovered ? "" : " [expected-not-in-senses]") +
                    (result.VisibleExpectedCovered ? "" : " [expected-not-visible]"));
            }

            Console.WriteLine();
            if (results.Count != cases.Length)
            {
                throw new InvalidDataException(
                    $"The ranker benchmark recorded {results.Count} case results for " +
                    $"{cases.Length} inputs.");
            }

            CaseResult[] rankedResults = results
                .Where(result => result.Route == "ranked")
                .ToArray();
            int coveredCount = rankedResults.Count(result => result.SenseExpectedCovered);
            int decidedCount = rankedResults.Count(result =>
                result.SenseExpectedCovered && !result.Abstained);
            int correctCount = rankedResults.Count(result => result.SenseCorrect == true);
            int visibleCoveredCount = rankedResults.Count(result => result.VisibleExpectedCovered);
            int visibleDecidedCount = rankedResults.Count(result => !result.Abstained);
            int visibleCorrectCount = rankedResults.Count(result => result.VisibleCorrect == true);
            Console.WriteLine(
                $"  cases={cases.Length} ranked={rankedResults.Length} phrase-bypass={skippedPhrase} " +
                $"no-inventory={skippedNoInventory} auto-resolved={autoResolved}");
            Console.WriteLine(
                $"  sense inventory coverage: {coveredCount}/{rankedResults.Length} ranked cases; " +
                $"sense decisions correct: {correctCount}/{decidedCount}");
            Console.WriteLine(
                $"  visible-outcome coverage: {visibleCoveredCount}/{rankedResults.Length} ranked cases; " +
                $"visible decisions correct: {visibleCorrectCount}/{visibleDecidedCount}");
            Console.WriteLine(
                $"  deterministic visible translations: " +
                $"{deterministicCorrect}/{deterministicResolved} correct; " +
                $"sense-aware end-to-end: {correctCount + deterministicCorrect}/{cases.Length}; " +
                $"visible end-to-end: {visibleCorrectCount + deterministicCorrect}/{cases.Length}");
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

            // Peak memory must be read before the selector is disposed: the worker
            // owns the model, so its peak working set is the number a replacement
            // candidate has to beat, not this harness's own footprint.
            long workerPeakBytes = ReadWorkerPeakWorkingSet(selector);
            if (reportPath is not null)
            {
                string modelVersion = selector.ActiveModelVersion
                    ?? throw new InvalidOperationException(
                        "The ranker report has no active loaded model identity.");
                await WriteBaselineReportAsync(
                    reportPath,
                    benchmarkPath,
                    workerBaseDirectory,
                    provider,
                    cpuModelProfile,
                    modelVersion,
                    dictionary.VersionToken,
                    cases.Length,
                    results,
                    selectLatencies,
                    pipelineCounts,
                    skippedPhrase,
                    skippedNoInventory,
                    autoResolved,
                    deterministicResolved,
                    deterministicCorrect,
                    workerPeakBytes);
                Console.WriteLine($"  baseline report: {Path.GetFullPath(reportPath)}");
            }

            Console.WriteLine(
                $"  worker peak working set: " +
                (workerPeakBytes > 0
                    ? $"{workerPeakBytes / (1024 * 1024)} MB"
                    : "unavailable"));
            PrintBucket("small inventories (<=32 glosses)", rankedResults.Where(result => result.GlossCount <= 32));
            PrintBucket("large inventories (>32 glosses)", rankedResults.Where(result => result.GlossCount > 32));
            PrintBucket("all ranked", rankedResults);
            VerifyMeasurementCounts(
                cases.Length,
                rankedResults.Length,
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

    /// <summary>
    /// Separates semantic sense selection from the Russian surface that the
    /// product actually shows. A group may contain the expected synonym while
    /// deterministic surface selection chooses another one; that is a correct
    /// WSD decision but an incorrect visible translation for this benchmark.
    /// </summary>
    internal static RankedCorrectness EvaluateRankedCorrectness(
        SmartDictionaryRankerPlan plan,
        IReadOnlyList<SmartDictionarySenseGroup> senses,
        int? selectedGroupId,
        bool abstained,
        string expectedTranslation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(senses);

        string expected = NormalizeRussian(expectedTranslation);
        HashSet<int> expectedSenseGroupIds = [];
        foreach (SmartDictionaryRankerOutcome outcome in plan.Outcomes)
        {
            if (RussianLabelsMatch(outcome.Translation, expected))
            {
                expectedSenseGroupIds.Add(outcome.GroupId);
            }
        }

        SmartDictionarySenseGroup[] orderedSenses = senses
            .OrderBy(group => group.SourceIndex)
            .ToArray();
        if (orderedSenses.Length == plan.CandidateGroupIds.Count)
        {
            for (int index = 0; index < orderedSenses.Length; index++)
            {
                if (orderedSenses[index].Surfaces.Any(surface =>
                        RussianLabelsMatch(surface.Translation, expected)))
                {
                    expectedSenseGroupIds.Add(plan.CandidateGroupIds[index]);
                }
            }
        }

        bool visibleExpectedCovered = plan.Outcomes.Any(outcome =>
            RussianLabelsMatch(outcome.Translation, expected));
        SmartDictionaryRankerOutcome? selectedOutcome = selectedGroupId is { } groupId
            ? plan.Outcomes.FirstOrDefault(outcome => outcome.GroupId == groupId)
            : null;
        bool? senseCorrect = abstained || expectedSenseGroupIds.Count == 0
            ? null
            : selectedGroupId is { } selected && expectedSenseGroupIds.Contains(selected);
        bool? visibleCorrect = abstained
            ? null
            : selectedOutcome is not null &&
              RussianLabelsMatch(selectedOutcome.Translation, expected);
        return new RankedCorrectness(
            expectedSenseGroupIds.Count > 0,
            visibleExpectedCovered,
            senseCorrect,
            visibleCorrect);
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

    private static long ReadWorkerPeakWorkingSet(
        GlossBertDictionarySelectorService selector)
    {
        try
        {
            if (selector.WorkerProcessId is not { } processId)
            {
                return 0;
            }

            using System.Diagnostics.Process worker =
                System.Diagnostics.Process.GetProcessById(processId);
            worker.Refresh();
            return worker.PeakWorkingSet64;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException
                or NotSupportedException)
        {
            // The worker may have been recycled by its idle timer. A missing
            // memory sample must not fail a measurement run.
            return 0;
        }
    }

    private static async Task WriteBaselineReportAsync(
        string reportPath,
        string benchmarkPath,
        string workerBaseDirectory,
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile,
        string modelVersion,
        string dictionaryVersion,
        int caseCount,
        IReadOnlyList<CaseResult> results,
        IReadOnlyList<(int Glosses, long Milliseconds)> selectLatencies,
        IReadOnlyDictionary<string, int> pipelineCounts,
        int phraseBypass,
        int noInventory,
        int autoResolved,
        int deterministicResolved,
        int deterministicCorrect,
        long workerPeakBytes)
    {
        CaseResult[] ranked = results
            .Where(result => result.Route == "ranked")
            .ToArray();
        int senseCovered = ranked.Count(result => result.SenseExpectedCovered);
        int senseDecided = ranked.Count(result =>
            result.SenseExpectedCovered && !result.Abstained);
        int senseCorrect = ranked.Count(result => result.SenseCorrect == true);
        int visibleCovered = ranked.Count(result => result.VisibleExpectedCovered);
        int visibleDecided = ranked.Count(result => !result.Abstained);
        int visibleCorrect = ranked.Count(result => result.VisibleCorrect == true);
        double[] margins = ranked
            .Where(result => result.Margin is { } margin && double.IsFinite(margin))
            .Select(result => result.Margin!.Value)
            .OrderBy(value => value)
            .ToArray();
        long[] latencies = selectLatencies
            .Select(entry => entry.Milliseconds)
            .OrderBy(value => value)
            .ToArray();
        long[] largeLatencies = selectLatencies
            .Where(entry => entry.Glosses > 12)
            .Select(entry => entry.Milliseconds)
            .OrderBy(value => value)
            .ToArray();

        RankerBenchmarkProvenanceData provenance =
            await RankerBenchmarkProvenance.CaptureAsync(
                benchmarkPath,
                workerBaseDirectory,
                provider,
                cpuModelProfile,
                modelVersion);

        BaselineReport report = new(
            SchemaVersion: 4,
            GeneratedUtc: DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Provider: provider.ToString(),
            ModelProfile: cpuModelProfile.ToString(),
            ModelVersion: modelVersion,
            AbstentionTag: AsteriaScale.RankerProtocol.RankerAbstention.CacheTag,
            DictionaryVersion: dictionaryVersion,
            BenchmarkSet: provenance.BenchmarkSet,
            RuntimeArtifacts: provenance.RuntimeArtifacts,
            DictionaryArtifacts: provenance.DictionaryArtifacts,
            ModelArtifacts: provenance.ModelArtifacts,
            ModelArtifactBytes: provenance.ModelArtifacts.Sum(
                artifact => artifact.Bytes),
            WorkerPeakWorkingSetBytes: workerPeakBytes,
            Routing: new RoutingRecord(
                caseCount,
                ranked.Length,
                phraseBypass,
                noInventory,
                autoResolved,
                deterministicResolved,
                deterministicCorrect),
            SenseWsd: new DecisionRecord(
                senseCovered,
                senseDecided,
                senseCorrect,
                senseCovered - senseDecided,
                senseDecided == 0 ? null : senseCorrect / (double)senseDecided,
                ranked.Length == 0 ? null : senseCovered / (double)ranked.Length,
                senseCovered == 0 ? null : senseDecided / (double)senseCovered,
                (senseCorrect + deterministicCorrect) / (double)caseCount,
                margins.Length == 0 ? null : margins[margins.Length / 2]),
            VisibleTranslations: new VisibleTranslationRecord(
                visibleCovered,
                visibleDecided,
                visibleCorrect,
                ranked.Length - visibleDecided,
                visibleDecided == 0 ? null : visibleCorrect / (double)visibleDecided,
                ranked.Length == 0 ? null : visibleCovered / (double)ranked.Length,
                ranked.Length == 0 ? null : visibleDecided / (double)ranked.Length,
                ranked.Length == 0 ? 0 : visibleCorrect / (double)ranked.Length,
                (visibleCorrect + deterministicCorrect) / (double)caseCount),
            Latency: new LatencyRecord(
                latencies.Length == 0 ? null : latencies[latencies.Length / 2],
                largeLatencies.Length == 0
                    ? null
                    : largeLatencies[largeLatencies.Length / 2],
                latencies.Length == 0 ? null : latencies[^1],
                latencies.Length),
            Pipelines: new SortedDictionary<string, int>(
                pipelineCounts.ToDictionary(entry => entry.Key, entry => entry.Value),
                StringComparer.Ordinal));

        string? directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static IReadOnlyList<string> EnumerateActiveModelArtifactPaths(
        string workerBaseDirectory,
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile,
        string modelVersion) =>
        RankerBenchmarkProvenance.EnumerateActiveModelArtifactPaths(
            workerBaseDirectory,
            provider,
            cpuModelProfile,
            modelVersion);

    private static void PrintBucket(string label, IEnumerable<CaseResult> bucket)
    {
        CaseResult[] items = bucket.ToArray();
        if (items.Length == 0)
        {
            Console.WriteLine($"  {label}: none");
            return;
        }

        CaseResult[] senseCovered = items
            .Where(item => item.SenseExpectedCovered)
            .ToArray();
        CaseResult[] senseDecided = senseCovered
            .Where(item => !item.Abstained)
            .ToArray();
        int senseCorrect = senseDecided.Count(item => item.SenseCorrect == true);
        int visibleCovered = items.Count(item => item.VisibleExpectedCovered);
        CaseResult[] visibleDecided = items.Where(item => !item.Abstained).ToArray();
        int visibleCorrect = visibleDecided.Count(item => item.VisibleCorrect == true);
        double[] margins = items
            .Where(item => item.Margin is { } margin && double.IsFinite(margin))
            .Select(item => item.Margin!.Value)
            .OrderBy(value => value)
            .ToArray();
        string medianMargin = margins.Length == 0
            ? "n/a"
            : margins[margins.Length / 2].ToString("0.000", CultureInfo.InvariantCulture);
        Console.WriteLine(
            $"  {label}: ranked={items.Length} " +
            $"sense-covered={senseCovered.Length} sense-decided={senseDecided.Length} " +
            $"sense-correct={senseCorrect}; visible-covered={visibleCovered} " +
            $"visible-decided={visibleDecided.Length} visible-correct={visibleCorrect} " +
            $"abstained={items.Count(item => item.Abstained)} medianMargin={medianMargin}");
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
    /// expected "серьёзным". Multi-word labels stay exact. A shared prefix is
    /// deliberately insufficient: scientific near-neighbours such as
    /// "значимый" and "значительный" are different visible translations.
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

        return RussianMorphology.AreConservativeInflectionVariants(a, b);
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
