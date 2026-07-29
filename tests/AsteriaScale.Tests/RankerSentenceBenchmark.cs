using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AsteriaScale.RankerProtocol;
using AsteriaScale.Services;

internal sealed record SentenceRankerBenchmarkOptions(
    string BenchmarkPath,
    string WorkerBaseDirectory,
    RankerCpuModelProfile CpuModel,
    string? OutputPath)
{
    internal const string Usage =
        "Usage: --ranker-sentence-benchmark <benchmark.json> " +
        "<workerBaseDirectory> [--ranker-cpu-model glossbert|sandwich|consec] " +
        "[--sentence-report <path>]";

    internal static SentenceRankerBenchmarkOptions Parse(
        IReadOnlyList<string> arguments,
        int optionIndex)
    {
        if (optionIndex < 0 || optionIndex + 2 >= arguments.Count)
        {
            throw new InvalidOperationException(Usage);
        }

        string benchmarkPath = arguments[optionIndex + 1];
        string workerBaseDirectory = arguments[optionIndex + 2];
        if (benchmarkPath.StartsWith("--", StringComparison.Ordinal) ||
            workerBaseDirectory.StartsWith("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(Usage);
        }

        RankerCpuModelProfile cpuModel = RankerCpuModelProfile.GlossBert;
        string? outputPath = null;
        bool modelSeen = false;
        for (int index = optionIndex + 3; index < arguments.Count; index++)
        {
            string option = arguments[index];
            if (string.Equals(
                    option,
                    "--ranker-cpu-model",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (modelSeen || index + 1 >= arguments.Count ||
                    arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(Usage);
                }

                cpuModel = ParseCpuModel(arguments[++index]);
                modelSeen = true;
                continue;
            }

            if (string.Equals(option, "--sentence-report", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option, "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (outputPath is not null || index + 1 >= arguments.Count ||
                    arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(Usage);
                }

                outputPath = arguments[++index];
                continue;
            }

            throw new InvalidOperationException(Usage);
        }

        return new SentenceRankerBenchmarkOptions(
            benchmarkPath,
            workerBaseDirectory,
            cpuModel,
            outputPath);
    }

    private static RankerCpuModelProfile ParseCpuModel(string value) =>
        value.ToLowerInvariant() switch
        {
            "glossbert" => RankerCpuModelProfile.GlossBert,
            "sandwich" => RankerCpuModelProfile.Sandwich,
            "consec" => RankerCpuModelProfile.Consec,
            _ => throw new InvalidOperationException(Usage)
        };
}

internal sealed class SentenceBenchmarkDocument
{
    public string Name { get; init; } = string.Empty;
    public int SchemaVersion { get; init; }
    public string LanguagePair { get; init; } = string.Empty;
    public string License { get; init; } = string.Empty;
    public IReadOnlyList<string> ScientificDomains { get; init; } = [];
    public IReadOnlyList<SentenceBenchmarkCase> Cases { get; init; } = [];
}

internal sealed class SentenceBenchmarkCase
{
    public string Id { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string? Subdomain { get; init; }
    public string SourceText { get; init; } = string.Empty;
    public string? PreviousContext { get; init; }
    public string? ContrastSetId { get; init; }
    public string ReferenceTranslation { get; init; } = string.Empty;
    public IReadOnlyList<SentenceBenchmarkTarget> Targets { get; init; } = [];
}

internal sealed class SentenceBenchmarkTarget
{
    public string Text { get; init; } = string.Empty;
    public int CharacterIndex { get; init; }
    public int Length { get; init; }
    public IReadOnlyList<SentenceBenchmarkSenseGroup> Groups { get; init; } = [];
    public int GoldGroupId { get; init; }
    public string ExpectedTranslation { get; init; } = string.Empty;
    public bool? SurfaceScorable { get; init; }
}

internal sealed class SentenceBenchmarkSenseGroup
{
    public int GroupId { get; init; }
    public string Gloss { get; init; } = string.Empty;
    public IReadOnlyList<string> Translations { get; init; } = [];
}

internal sealed record SentenceTargetRunResult(
    int TargetIndex,
    string Text,
    int GoldGroupId,
    string ExpectedTranslation,
    IReadOnlyList<string> GoldTranslations,
    int? SelectedGroupId,
    bool SurfaceUnambiguous,
    bool Abstained,
    bool Overflow,
    string? ErrorCode,
    string? ErrorMessage,
    bool SenseCorrect,
    bool? SurfaceUnambiguousSenseCorrect,
    IReadOnlyList<string> SelectedTranslations,
    double? Margin,
    double? Score,
    double? Confidence,
    string? Pipeline,
    long? LatencyMilliseconds);

internal sealed record SentenceCaseRunResult(
    string Id,
    string Domain,
    string Subdomain,
    string Category,
    string? ContrastSetId,
    bool UsedPreviousContext,
    IReadOnlyList<SentenceTargetRunResult> Targets);

internal sealed record SentenceBenchmarkScopeMetrics(
    int Sentences,
    int AllTargetSensesCorrectSentences,
    double? AllTargetSensesCorrectSentenceAccuracy,
    int Targets,
    int TargetSenseCorrect,
    double? TargetSenseAccuracy,
    int Decided,
    double? DecisionCoverage,
    double? AcceptedTargetSenseAccuracy,
    int Abstained,
    int Errors,
    int Overflows,
    int SurfaceUnambiguousTargets,
    int SurfaceUnambiguousSenseCorrect,
    double? SurfaceUnambiguousSenseAccuracy,
    int SurfaceUnambiguousDecided,
    double? AcceptedSurfaceUnambiguousSenseAccuracy,
    int FullySurfaceUnambiguousSentences,
    int SurfaceUnambiguousAllSensesCorrectSentences,
    double? SurfaceUnambiguousAllSensesCorrectSentenceAccuracy);

internal sealed record SentenceBenchmarkContrastMetrics(
    int ContrastPairs,
    int AllTargetSensesCorrectPairs,
    double? AllTargetSensesCorrectPairAccuracy,
    int PairedTargets,
    int PairedTargetsBothSenseCorrect,
    double? PairedTargetSenseAccuracy,
    int ChangedGoldTargetPairs,
    int ChangedGoldTargetPairsBothSenseCorrect,
    double? ChangedGoldTargetSensePairAccuracy,
    int PairsWithAbstention,
    int PairsWithErrorOrOverflow);

internal sealed record SentenceBenchmarkLatencyMetrics(
    long ColdWarmupMilliseconds,
    int WarmSamples,
    long? WarmP50Milliseconds,
    long? WarmP95Milliseconds,
    long? WarmMaximumMilliseconds,
    int SuccessfulWarmSamples,
    long? SuccessfulWarmP50Milliseconds,
    long? SuccessfulWarmP95Milliseconds);

internal sealed record SentenceBenchmarkErrorMetrics(
    int TargetErrors,
    int CandidateOverflows,
    IReadOnlyDictionary<string, int> ErrorCodes);

internal sealed record SentenceBenchmarkResourceMetrics(
    long WorkerPeakWorkingSetBytes);

internal sealed record SentenceBenchmarkDatasetRecord(
    string Name,
    int SchemaVersion,
    string LanguagePair,
    string License,
    int Cases,
    int Targets,
    IReadOnlyList<string> ScientificDomains);

internal sealed record SentenceRankerBenchmarkReport(
    int SchemaVersion,
    string GeneratedUtc,
    string EvaluationScope,
    string Provider,
    string ModelProfile,
    string ModelVersion,
    string InferenceMode,
    string ContextPolicy,
    string AbstentionTag,
    SentenceBenchmarkDatasetRecord Dataset,
    RankerBenchmarkProvenanceData Provenance,
    long ModelArtifactBytes,
    SentenceBenchmarkScopeMetrics Overall,
    SentenceBenchmarkScopeMetrics Scientific,
    IReadOnlyDictionary<string, SentenceBenchmarkScopeMetrics> Domains,
    IReadOnlyDictionary<string, SentenceBenchmarkScopeMetrics> Subdomains,
    SentenceBenchmarkContrastMetrics Contrasts,
    SentenceBenchmarkLatencyMetrics Latency,
    SentenceBenchmarkResourceMetrics Resources,
    SentenceBenchmarkErrorMetrics Errors,
    IReadOnlyDictionary<string, int> Pipelines,
    IReadOnlyList<SentenceCaseRunResult> Cases);

internal static class RankerSentenceBenchmarkMetrics
{
    internal static SentenceBenchmarkScopeMetrics CalculateScope(
        IEnumerable<SentenceCaseRunResult> source)
    {
        SentenceCaseRunResult[] cases = source.ToArray();
        SentenceTargetRunResult[] targets = cases.SelectMany(item => item.Targets).ToArray();
        int decided = targets.Count(IsDecided);
        int correct = targets.Count(item => item.SenseCorrect);
        SentenceTargetRunResult[] surfaceUnambiguous = targets
            .Where(item => item.SurfaceUnambiguous)
            .ToArray();
        int surfaceUnambiguousDecided = surfaceUnambiguous.Count(IsDecided);
        int surfaceUnambiguousCorrect = surfaceUnambiguous.Count(
            item => item.SurfaceUnambiguousSenseCorrect == true);
        SentenceCaseRunResult[] fullySurfaceUnambiguous = cases
            .Where(item => item.Targets.Count > 0 &&
                           item.Targets.All(target => target.SurfaceUnambiguous))
            .ToArray();
        int allTargetSensesCorrect = cases.Count(AreAllTargetSensesCorrect);
        int surfaceUnambiguousAllSensesCorrect = fullySurfaceUnambiguous.Count(item =>
            item.Targets.All(target => target.SurfaceUnambiguousSenseCorrect == true));

        return new SentenceBenchmarkScopeMetrics(
            Sentences: cases.Length,
            AllTargetSensesCorrectSentences: allTargetSensesCorrect,
            AllTargetSensesCorrectSentenceAccuracy: Ratio(
                allTargetSensesCorrect,
                cases.Length),
            Targets: targets.Length,
            TargetSenseCorrect: correct,
            TargetSenseAccuracy: Ratio(correct, targets.Length),
            Decided: decided,
            DecisionCoverage: Ratio(decided, targets.Length),
            AcceptedTargetSenseAccuracy: Ratio(correct, decided),
            Abstained: targets.Count(item => item.Abstained),
            Errors: targets.Count(item => item.ErrorCode is not null),
            Overflows: targets.Count(item => item.Overflow),
            SurfaceUnambiguousTargets: surfaceUnambiguous.Length,
            SurfaceUnambiguousSenseCorrect: surfaceUnambiguousCorrect,
            SurfaceUnambiguousSenseAccuracy: Ratio(
                surfaceUnambiguousCorrect,
                surfaceUnambiguous.Length),
            SurfaceUnambiguousDecided: surfaceUnambiguousDecided,
            AcceptedSurfaceUnambiguousSenseAccuracy: Ratio(
                surfaceUnambiguousCorrect,
                surfaceUnambiguousDecided),
            FullySurfaceUnambiguousSentences: fullySurfaceUnambiguous.Length,
            SurfaceUnambiguousAllSensesCorrectSentences:
                surfaceUnambiguousAllSensesCorrect,
            SurfaceUnambiguousAllSensesCorrectSentenceAccuracy: Ratio(
                surfaceUnambiguousAllSensesCorrect,
                fullySurfaceUnambiguous.Length));
    }

    internal static SentenceBenchmarkContrastMetrics CalculateContrasts(
        IEnumerable<SentenceCaseRunResult> source)
    {
        SentenceCaseRunResult[][] pairs = source
            .Where(item => item.ContrastSetId is not null)
            .GroupBy(item => item.ContrastSetId!, StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray())
            .ToArray();
        foreach (SentenceCaseRunResult[] pair in pairs)
        {
            if (pair.Length != 2 || pair[0].Targets.Count != pair[1].Targets.Count)
            {
                throw new InvalidDataException(
                    "Each contrast set must contain two results with aligned targets.");
            }
        }

        int pairedTargets = 0;
        int pairedTargetsCorrect = 0;
        int changedTargets = 0;
        int changedTargetsCorrect = 0;
        foreach (SentenceCaseRunResult[] pair in pairs)
        {
            for (int index = 0; index < pair[0].Targets.Count; index++)
            {
                SentenceTargetRunResult left = pair[0].Targets[index];
                SentenceTargetRunResult right = pair[1].Targets[index];
                if (left.TargetIndex != right.TargetIndex ||
                    !string.Equals(left.Text, right.Text, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Contrast targets are not aligned by stable target index.");
                }

                pairedTargets++;
                bool bothCorrect = left.SenseCorrect && right.SenseCorrect;
                pairedTargetsCorrect += bothCorrect ? 1 : 0;
                if (left.GoldGroupId != right.GoldGroupId)
                {
                    changedTargets++;
                    changedTargetsCorrect += bothCorrect ? 1 : 0;
                }
            }
        }

        int allTargetSensesCorrectPairs = pairs.Count(
            pair => pair.All(AreAllTargetSensesCorrect));
        return new SentenceBenchmarkContrastMetrics(
            ContrastPairs: pairs.Length,
            AllTargetSensesCorrectPairs: allTargetSensesCorrectPairs,
            AllTargetSensesCorrectPairAccuracy: Ratio(
                allTargetSensesCorrectPairs,
                pairs.Length),
            PairedTargets: pairedTargets,
            PairedTargetsBothSenseCorrect: pairedTargetsCorrect,
            PairedTargetSenseAccuracy: Ratio(pairedTargetsCorrect, pairedTargets),
            ChangedGoldTargetPairs: changedTargets,
            ChangedGoldTargetPairsBothSenseCorrect: changedTargetsCorrect,
            ChangedGoldTargetSensePairAccuracy: Ratio(
                changedTargetsCorrect,
                changedTargets),
            PairsWithAbstention: pairs.Count(pair =>
                pair.SelectMany(item => item.Targets).Any(target => target.Abstained)),
            PairsWithErrorOrOverflow: pairs.Count(pair =>
                pair.SelectMany(item => item.Targets).Any(target =>
                    target.ErrorCode is not null || target.Overflow)));
    }

    internal static SentenceBenchmarkLatencyMetrics CalculateLatency(
        long coldWarmupMilliseconds,
        IEnumerable<SentenceCaseRunResult> source)
    {
        SentenceTargetRunResult[] invoked = source
            .SelectMany(item => item.Targets)
            .Where(item => item.LatencyMilliseconds.HasValue)
            .ToArray();
        long[] all = invoked
            .Select(item => item.LatencyMilliseconds!.Value)
            .OrderBy(value => value)
            .ToArray();
        long[] successful = invoked
            .Where(item => item.ErrorCode is null && !item.Overflow)
            .Select(item => item.LatencyMilliseconds!.Value)
            .OrderBy(value => value)
            .ToArray();
        return new SentenceBenchmarkLatencyMetrics(
            coldWarmupMilliseconds,
            all.Length,
            Percentile(all, 0.50),
            Percentile(all, 0.95),
            all.Length == 0 ? null : all[^1],
            successful.Length,
            Percentile(successful, 0.50),
            Percentile(successful, 0.95));
    }

    internal static SentenceBenchmarkErrorMetrics CalculateErrors(
        IEnumerable<SentenceCaseRunResult> source)
    {
        SentenceTargetRunResult[] targets = source.SelectMany(item => item.Targets).ToArray();
        var codes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string code in targets
                     .Where(item => item.ErrorCode is not null)
                     .Select(item => item.ErrorCode!))
        {
            codes[code] = codes.GetValueOrDefault(code) + 1;
        }

        return new SentenceBenchmarkErrorMetrics(
            targets.Count(item => item.ErrorCode is not null),
            targets.Count(item => item.Overflow),
            codes);
    }

    internal static IReadOnlyDictionary<string, SentenceBenchmarkScopeMetrics>
        CalculateSlices(
            IEnumerable<SentenceCaseRunResult> source,
            Func<SentenceCaseRunResult, string> keySelector)
    {
        var result = new SortedDictionary<string, SentenceBenchmarkScopeMetrics>(
            StringComparer.Ordinal);
        foreach (IGrouping<string, SentenceCaseRunResult> group in source.GroupBy(
                     keySelector,
                     StringComparer.Ordinal))
        {
            result.Add(group.Key, CalculateScope(group));
        }

        return result;
    }

    internal static long? Percentile(IReadOnlyList<long> orderedValues, double percentile)
    {
        if (orderedValues.Count == 0)
        {
            return null;
        }

        if (percentile is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        int index = Math.Clamp(
            (int)Math.Ceiling(percentile * orderedValues.Count) - 1,
            0,
            orderedValues.Count - 1);
        return orderedValues[index];
    }

    private static bool IsDecided(SentenceTargetRunResult target) =>
        target.SelectedGroupId.HasValue &&
        !target.Abstained &&
        !target.Overflow &&
        target.ErrorCode is null;

    private static bool AreAllTargetSensesCorrect(SentenceCaseRunResult item) =>
        item.Targets.Count > 0 && item.Targets.All(target => target.SenseCorrect);

    private static double? Ratio(int numerator, int denominator) =>
        denominator == 0 ? null : numerator / (double)denominator;
}

internal static class RankerSentenceBenchmark
{
    internal const int ReportSchemaVersion = 2;
    internal const string ReportEvaluationScope = "wsd-sense-selection-only";
    private const int MaximumCandidates = 128;

    private static readonly JsonSerializerOptions InputJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false
        };

    private static readonly JsonSerializerOptions ReportJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    public static async Task RunAsync(SentenceRankerBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RejectFrozenHoldout(options.BenchmarkPath);
        SentenceBenchmarkDocument document = await LoadAndValidateAsync(
            options.BenchmarkPath);

        await using GlossBertDictionarySelectorService selector = new(
            options.WorkerBaseDirectory,
            TimeSpan.FromMinutes(10),
            options.CpuModel);
        selector.ConfigureKeepWarm(true);

        Stopwatch coldTimer = Stopwatch.StartNew();
        await selector.WarmUpAsync(GlossBertRankerProvider.Cpu);
        coldTimer.Stop();
        string modelVersion = selector.ActiveModelVersion
            ?? throw new InvalidOperationException(
                "Ранжировщик не сообщил идентификатор загруженной модели.");
        long peakWorkingSet = ReadWorkerPeakWorkingSet(selector);

        var cases = new List<SentenceCaseRunResult>(document.Cases.Count);
        var pipelines = new SortedDictionary<string, int>(StringComparer.Ordinal);
        HashSet<string> scientificDomains = document.ScientificDomains.ToHashSet(
            StringComparer.Ordinal);
        foreach (SentenceBenchmarkCase benchmarkCase in document.Cases)
        {
            (string context, int indexOffset) = BuildInferenceContext(benchmarkCase);
            var targets = new List<SentenceTargetRunResult>(benchmarkCase.Targets.Count);
            for (int targetIndex = 0; targetIndex < benchmarkCase.Targets.Count; targetIndex++)
            {
                SentenceBenchmarkTarget target = benchmarkCase.Targets[targetIndex];
                SentenceTargetRunResult result;
                if (target.Groups.Count > MaximumCandidates)
                {
                    result = Overflow(targetIndex, target);
                }
                else
                {
                    result = await SelectTargetAsync(
                        selector,
                        context,
                        indexOffset,
                        targetIndex,
                        target,
                        modelVersion);
                    if (result.Pipeline is { } pipeline)
                    {
                        pipelines[pipeline] = pipelines.GetValueOrDefault(pipeline) + 1;
                    }
                }

                targets.Add(result);
                peakWorkingSet = Math.Max(
                    peakWorkingSet,
                    ReadWorkerPeakWorkingSet(selector));
            }

            string subdomain = string.IsNullOrWhiteSpace(benchmarkCase.Subdomain)
                ? benchmarkCase.Domain
                : benchmarkCase.Subdomain;
            cases.Add(new SentenceCaseRunResult(
                benchmarkCase.Id,
                benchmarkCase.Domain,
                subdomain,
                ResolveCategory(benchmarkCase.Domain, scientificDomains),
                benchmarkCase.ContrastSetId,
                benchmarkCase.PreviousContext is not null,
                targets));
        }

        RankerBenchmarkProvenanceData provenance =
            await RankerBenchmarkProvenance.CaptureAsync(
                options.BenchmarkPath,
                options.WorkerBaseDirectory,
                GlossBertRankerProvider.Cpu,
                options.CpuModel,
                modelVersion);
        SentenceBenchmarkScopeMetrics overall =
            RankerSentenceBenchmarkMetrics.CalculateScope(cases);
        SentenceBenchmarkScopeMetrics scientific =
            RankerSentenceBenchmarkMetrics.CalculateScope(
                cases.Where(item => item.Category == "scientific"));
        SentenceBenchmarkErrorMetrics errors =
            RankerSentenceBenchmarkMetrics.CalculateErrors(cases);
        SentenceRankerBenchmarkReport report = new(
            SchemaVersion: ReportSchemaVersion,
            GeneratedUtc: DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            EvaluationScope: ReportEvaluationScope,
            Provider: GlossBertRankerProvider.Cpu.ToString(),
            ModelProfile: options.CpuModel.ToString(),
            ModelVersion: modelVersion,
            InferenceMode: "independent-select-per-target",
            ContextPolicy: "previousContext-newline-sourceText",
            AbstentionTag: RankerAbstention.CacheTag,
            Dataset: new SentenceBenchmarkDatasetRecord(
                document.Name,
                document.SchemaVersion,
                document.LanguagePair,
                document.License,
                document.Cases.Count,
                document.Cases.Sum(item => item.Targets.Count),
                document.ScientificDomains),
            Provenance: provenance,
            ModelArtifactBytes: provenance.ModelArtifacts.Sum(item => item.Bytes),
            Overall: overall,
            Scientific: scientific,
            Domains: RankerSentenceBenchmarkMetrics.CalculateSlices(
                cases,
                item => item.Category),
            Subdomains: RankerSentenceBenchmarkMetrics.CalculateSlices(
                cases,
                item => item.Subdomain),
            Contrasts: RankerSentenceBenchmarkMetrics.CalculateContrasts(cases),
            Latency: RankerSentenceBenchmarkMetrics.CalculateLatency(
                coldTimer.ElapsedMilliseconds,
                cases),
            Resources: new SentenceBenchmarkResourceMetrics(peakWorkingSet),
            Errors: errors,
            Pipelines: pipelines,
            Cases: cases);

        PrintSummary(report);
        if (options.OutputPath is not null)
        {
            await WriteReportAtomicallyAsync(options.OutputPath, report);
            Console.WriteLine(
                $"  JSON-отчёт: {Path.GetFullPath(options.OutputPath)}");
        }
    }

    private static async Task<SentenceTargetRunResult> SelectTargetAsync(
        GlossBertDictionarySelectorService selector,
        string context,
        int indexOffset,
        int targetIndex,
        SentenceBenchmarkTarget target,
        string expectedModelVersion)
    {
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            GlossBertDictionarySelection selection = await selector.SelectAsync(
                context,
                target.Text,
                checked(indexOffset + target.CharacterIndex),
                target.Groups.Select(group => group.Gloss).ToArray(),
                target.Groups.Select(group => group.GroupId).ToArray(),
                "English",
                GlossBertRankerProvider.Cpu);
            timer.Stop();
            if (!string.Equals(
                    selection.ModelVersion,
                    expectedModelVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Идентификатор модели изменился во время одного прогона.");
            }

            SentenceBenchmarkSenseGroup? selectedGroup =
                selection.SelectedGroupId is { } selectedGroupId
                    ? target.Groups.FirstOrDefault(group => group.GroupId == selectedGroupId)
                    : null;
            bool groupCorrect =
                selection.SelectedGroupId == target.GoldGroupId && !selection.Abstained;
            return new SentenceTargetRunResult(
                targetIndex,
                target.Text,
                target.GoldGroupId,
                target.ExpectedTranslation,
                target.Groups[target.GoldGroupId].Translations,
                selection.SelectedGroupId,
                target.SurfaceScorable == true,
                selection.Abstained,
                Overflow: false,
                ErrorCode: null,
                ErrorMessage: null,
                SenseCorrect: groupCorrect,
                SurfaceUnambiguousSenseCorrect: target.SurfaceScorable == true
                    ? groupCorrect
                    : null,
                SelectedTranslations: selectedGroup?.Translations ?? [],
                selection.Margin,
                selection.Score,
                selection.Confidence,
                selection.Pipeline,
                timer.ElapsedMilliseconds);
        }
        catch (Exception exception) when (IsRecoverableTargetFailure(exception))
        {
            timer.Stop();
            return new SentenceTargetRunResult(
                targetIndex,
                target.Text,
                target.GoldGroupId,
                target.ExpectedTranslation,
                target.Groups[target.GoldGroupId].Translations,
                SelectedGroupId: null,
                target.SurfaceScorable == true,
                Abstained: false,
                Overflow: false,
                ErrorCode: ErrorCode(exception),
                ErrorMessage: exception.Message,
                SenseCorrect: false,
                SurfaceUnambiguousSenseCorrect:
                    target.SurfaceScorable == true ? false : null,
                SelectedTranslations: [],
                Margin: null,
                Score: null,
                Confidence: null,
                Pipeline: null,
                timer.ElapsedMilliseconds);
        }
    }

    private static SentenceTargetRunResult Overflow(
        int targetIndex,
        SentenceBenchmarkTarget target) =>
        new(
            targetIndex,
            target.Text,
            target.GoldGroupId,
            target.ExpectedTranslation,
            target.Groups[target.GoldGroupId].Translations,
            SelectedGroupId: null,
            target.SurfaceScorable == true,
            Abstained: false,
            Overflow: true,
            ErrorCode: null,
            ErrorMessage: $"Кандидатов {target.Groups.Count}; максимум {MaximumCandidates}.",
            SenseCorrect: false,
            SurfaceUnambiguousSenseCorrect:
                target.SurfaceScorable == true ? false : null,
            SelectedTranslations: [],
            Margin: null,
            Score: null,
            Confidence: null,
            Pipeline: null,
            LatencyMilliseconds: null);

    private static async Task<SentenceBenchmarkDocument> LoadAndValidateAsync(string path)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        SentenceBenchmarkDocument document =
            await JsonSerializer.DeserializeAsync<SentenceBenchmarkDocument>(
                stream,
                InputJsonOptions)
            ?? throw new InvalidDataException(
                "Набор предложений не удалось прочитать.");
        if (document.SchemaVersion != 1 || document.Cases.Count == 0 ||
            document.ScientificDomains.Count == 0)
        {
            throw new InvalidDataException(
                "Набор предложений не соответствует schemaVersion 1.");
        }

        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (SentenceBenchmarkCase benchmarkCase in document.Cases)
        {
            if (!caseIds.Add(benchmarkCase.Id) ||
                string.IsNullOrWhiteSpace(benchmarkCase.Domain) ||
                string.IsNullOrWhiteSpace(benchmarkCase.SourceText) ||
                benchmarkCase.Targets.Count == 0)
            {
                throw new InvalidDataException(
                    $"Некорректная структура case '{benchmarkCase.Id}'.");
            }

            for (int targetIndex = 0; targetIndex < benchmarkCase.Targets.Count; targetIndex++)
            {
                SentenceBenchmarkTarget target = benchmarkCase.Targets[targetIndex];
                if (target.SurfaceScorable is null || target.Groups.Count < 2 ||
                    target.Length != target.Text.Length || target.CharacterIndex < 0 ||
                    target.CharacterIndex + target.Length > benchmarkCase.SourceText.Length ||
                    !string.Equals(
                        benchmarkCase.SourceText.Substring(target.CharacterIndex, target.Length),
                        target.Text,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Некорректная цель {benchmarkCase.Id}/{targetIndex}.");
                }

                for (int groupIndex = 0; groupIndex < target.Groups.Count; groupIndex++)
                {
                    SentenceBenchmarkSenseGroup group = target.Groups[groupIndex];
                    if (group.GroupId != groupIndex ||
                        string.IsNullOrWhiteSpace(group.Gloss) ||
                        group.Translations.Count == 0)
                    {
                        throw new InvalidDataException(
                            $"Нарушен GroupId == index в " +
                            $"{benchmarkCase.Id}/{target.Text}/{groupIndex}.");
                    }
                }

                if (target.GoldGroupId < 0 || target.GoldGroupId >= target.Groups.Count)
                {
                    throw new InvalidDataException(
                        $"GoldGroupId вне диапазона в {benchmarkCase.Id}/{target.Text}.");
                }
            }
        }

        return document;
    }

    internal static void RejectFrozenHoldout(string path)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.StartsWith("ranker-holdout", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Sentence benchmark запрещено запускать на frozen ranker-holdout*.json.");
        }
    }

    private static (string Context, int IndexOffset) BuildInferenceContext(
        SentenceBenchmarkCase benchmarkCase)
    {
        if (benchmarkCase.PreviousContext is null)
        {
            return (benchmarkCase.SourceText, 0);
        }

        string prefix = benchmarkCase.PreviousContext + "\n";
        return (prefix + benchmarkCase.SourceText, prefix.Length);
    }

    private static string ResolveCategory(
        string domain,
        IReadOnlySet<string> scientificDomains)
    {
        if (scientificDomains.Contains(domain))
        {
            return "scientific";
        }

        return string.Equals(domain, "contextual", StringComparison.Ordinal)
            ? "contextual"
            : domain is "literature" or "visual-novel" or "manga"
                ? "narrative"
                : "other";
    }

    private static bool IsRecoverableTargetFailure(Exception exception) =>
        exception is GlossBertRankerException or InvalidOperationException
            or ArgumentException or IOException or TimeoutException;

    private static string ErrorCode(Exception exception) =>
        exception is GlossBertRankerException ranker
            ? ranker.ErrorCode
            : exception.GetType().Name;

    private static long ReadWorkerPeakWorkingSet(
        GlossBertDictionarySelectorService selector)
    {
        try
        {
            if (selector.WorkerProcessId is not { } processId)
            {
                return 0;
            }

            using Process worker = Process.GetProcessById(processId);
            worker.Refresh();
            return worker.PeakWorkingSet64;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException
                or NotSupportedException)
        {
            return 0;
        }
    }

    private static async Task WriteReportAtomicallyAsync(
        string path,
        SentenceRankerBenchmarkReport report)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(report, ReportJsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void PrintSummary(SentenceRankerBenchmarkReport report)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"WSD-бенчмарк предложений: {report.ModelProfile}, {report.ModelVersion}");
        Console.WriteLine(
            "  Оценивается только выбор смысла; русский и полнотекстовый " +
            "перевод не оцениваются.");
        PrintScope("весь набор", report.Overall);
        PrintScope("научные статьи", report.Scientific);
        foreach ((string subdomain, SentenceBenchmarkScopeMetrics metrics) in
                 report.Subdomains)
        {
            PrintScope(subdomain, metrics);
        }

        Console.WriteLine(
            $"  контрастные WSD-пары (все смыслы верны): " +
            $"{report.Contrasts.AllTargetSensesCorrectPairs}/" +
            $"{report.Contrasts.ContrastPairs}; изменённые контекстом цели: " +
            $"{report.Contrasts.ChangedGoldTargetPairsBothSenseCorrect}/" +
            $"{report.Contrasts.ChangedGoldTargetPairs}");
        Console.WriteLine(
            $"  задержка: cold={report.Latency.ColdWarmupMilliseconds} мс, " +
            $"warm p50={FormatMilliseconds(report.Latency.WarmP50Milliseconds)}, " +
            $"warm p95={FormatMilliseconds(report.Latency.WarmP95Milliseconds)}");
        Console.WriteLine(
            $"  память worker peak: " +
            $"{report.Resources.WorkerPeakWorkingSetBytes / (1024 * 1024)} МБ; " +
            $"ошибки={report.Errors.TargetErrors}, overflow=" +
            $"{report.Errors.CandidateOverflows}");
    }

    private static void PrintScope(
        string label,
        SentenceBenchmarkScopeMetrics metrics)
    {
        Console.WriteLine(
            $"  {label}: WSD-цели={metrics.TargetSenseCorrect}/{metrics.Targets} " +
            $"({FormatPercent(metrics.TargetSenseAccuracy)}), " +
            $"предложения-со-всеми-верными-смыслами=" +
            $"{metrics.AllTargetSensesCorrectSentences}/{metrics.Sentences} " +
            $"({FormatPercent(metrics.AllTargetSensesCorrectSentenceAccuracy)}), " +
            $"WSD-цели-без-межсмысловой-коллизии-перевода=" +
            $"{metrics.SurfaceUnambiguousSenseCorrect}/" +
            $"{metrics.SurfaceUnambiguousTargets} " +
            $"({FormatPercent(metrics.SurfaceUnambiguousSenseAccuracy)}), " +
            $"coverage={FormatPercent(metrics.DecisionCoverage)}");
    }

    private static string FormatPercent(double? value) =>
        value?.ToString("P1", CultureInfo.InvariantCulture) ?? "n/a";

    private static string FormatMilliseconds(long? value) =>
        value is null ? "n/a" : $"{value} мс";
}
