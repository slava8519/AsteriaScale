using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AsteriaScale.Services;

/// <summary>
/// Measures the full-translation pipeline (roadmap tasks #13/#14): chrF against
/// a reference, term-anchor fidelity, residual English, latency and scaffold
/// diagnostics. It drives the production translator, so it needs a live Foundry
/// Local model and stays a gated CLI mode rather than part of the default smoke
/// suite.
/// </summary>
internal static class TranslationBenchmark
{
    private const int DumpSchemaVersion = 3;
    private static readonly JsonSerializerOptions DumpJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private sealed record BenchmarkCase(
        string Id,
        string SourceLanguage,
        string Source,
        string Reference,
        IReadOnlyList<(string English, string Russian)> Anchors);

    private sealed record CaseResult(
        string Id,
        string SourceLanguage,
        double ChrF,
        int AnchorsTotal,
        int AnchorsHit,
        double ResidualEnglish,
        long Milliseconds,
        string Output,
        TranslationScaffoldDiagnostics? ScaffoldDiagnostics);

    private sealed record ScaffoldDiagnosticsAggregate(
        int CasesWithDiagnostics,
        int TermCount,
        int SatisfiedTermCount,
        int ExactSentenceUses,
        int InitialViolationCount,
        int RetriesIssued,
        int FinalViolationCount,
        int InitialSenseConflictCount,
        int FinalSenseConflictCount,
        int SenseRetriesIssued,
        int SenseReviewKeeps,
        int SenseReviewCorrections,
        int SenseReviewRejected,
        int SenseReviewFailures,
        int NeuralRequests,
        int NeuralSelections,
        int NeuralAbstentions,
        int NeuralErrors,
        int NeuralSkipped,
        int NeuralSkippedAfterError,
        int NeuralTermsEmitted,
        int NeuralHintsEmitted,
        int CasesWithNeuralRequests,
        int CasesWithNeuralErrors,
        int CasesWithNeuralSkipped,
        int CasesWithNeuralSkippedAfterError);

    private sealed record BenchmarkAggregate(
        int Cases,
        double ChrFMean,
        double ChrFMedian,
        double ChrFMin,
        int AnchorsTotal,
        int AnchorsHit,
        double AnchorRate,
        double ResidualEnglishMean,
        int LinesWithLatin,
        double LatencyMedianMilliseconds,
        long LatencyMaxMilliseconds,
        ScaffoldDiagnosticsAggregate ScaffoldDiagnostics);

    private sealed record BenchmarkDump(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        string ResolvedSetPath,
        string SetSha256,
        string Engine,
        string ModelId,
        string FoundryCliVersion,
        RankerRuntime? Ranker,
        BenchmarkAggregate Overall,
        IReadOnlyDictionary<string, BenchmarkAggregate> PerLanguage,
        IReadOnlyList<CaseResult> Results);

    private sealed record RankerRuntime(
        string RequestedProvider,
        string? ActiveProvider,
        string ExpectedExecutionProvider,
        string ExpectedDevice,
        string? ModelVersion);

    private sealed record EngineConfiguration(
        string Name,
        TranslationScaffoldMode ScaffoldMode,
        GlossBertRankerProvider? RankerProvider);

    private const string NeuralDisabledAfterErrorDetail = "session-disabled-after-error";

    public static async Task RunAsync(
        string setPath,
        string engine,
        string? dumpPath,
        string? rankerBaseDirectory = null)
    {
        EngineConfiguration configuration = ResolveEngine(engine);
        string normalizedEngine = configuration.Name;
        ValidateRankerBase(configuration, rankerBaseDirectory);
        string resolvedSetPath = Path.GetFullPath(setPath);
        BenchmarkCase[] cases = LoadCases(resolvedSetPath);
        if (cases.Length == 0)
        {
            throw new InvalidOperationException("The translation benchmark set contains no cases.");
        }

        string setSha256 = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(resolvedSetPath)))
            .ToLowerInvariant();
        Console.WriteLine(
            $"  set={Path.GetFileName(resolvedSetPath)} cases={cases.Length} engine={normalizedEngine}");

        string isolatedUserDirectory = Path.Combine(
            Path.GetTempPath(),
            "AsteriaScale",
            "translation-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedUserDirectory);
        GlossBertDictionarySelectorService? rankerSelector = null;
        // The benchmark must neither read nor grow the user's decision cache:
        // a warm cache would silently replace the ranker under measurement.
        await using GlossBertDecisionCache benchmarkDecisionCache = new(
            Path.Combine(isolatedUserDirectory, "glossbert-decisions.v1.json"));
        try
        {
            LocalDictionaryService dictionary = new(
                Path.Combine(AppContext.BaseDirectory, "Dictionaries"),
                isolatedUserDirectory);
            TranslationScaffoldDiagnostics? currentDiagnostics = null;
            TranslationScaffoldNeuralRanker? neuralRanker = null;
            if (configuration.RankerProvider is { } provider)
            {
                rankerSelector = new GlossBertDictionarySelectorService(
                    Path.GetFullPath(rankerBaseDirectory!),
                    TimeSpan.FromMinutes(5));
                rankerSelector.ConfigureKeepWarm(true);
                TranslationScaffoldGlossBertRanker ranker = new(
                    rankerSelector,
                    benchmarkDecisionCache,
                    () => provider,
                    () => dictionary.VersionToken);
                neuralRanker = ranker.RankAsync;
            }

            await using FoundryTranslationService translator = new(
                dictionary,
                configuration.ScaffoldMode,
                diagnosticSink: diagnostics => currentDiagnostics = diagnostics,
                neuralRanker: neuralRanker);
            if (configuration.RankerProvider is { } rankerProvider)
            {
                await Task.WhenAll(
                    translator.WarmUpAsync(),
                    rankerSelector!.WarmUpAsync(rankerProvider));
            }
            else
            {
                await translator.WarmUpAsync();
            }

            List<CaseResult> results = [];
            foreach (BenchmarkCase benchmarkCase in cases)
            {
                currentDiagnostics = null;
                Stopwatch timer = Stopwatch.StartNew();
                string output = await translator.TranslateToRussianAsync(
                    benchmarkCase.Source,
                    CancellationToken.None);
                timer.Stop();
                if (string.IsNullOrWhiteSpace(output))
                {
                    throw new InvalidOperationException(
                        $"{benchmarkCase.Id}: the translation engine returned an empty result.");
                }

                int hits = benchmarkCase.Anchors.Count(
                    anchor => ContainsRussianStem(output, anchor.Russian));
                CaseResult result = new(
                    benchmarkCase.Id,
                    benchmarkCase.SourceLanguage,
                    ChrF(output, benchmarkCase.Reference),
                    benchmarkCase.Anchors.Count,
                    hits,
                    ResidualEnglishRate(output),
                    timer.ElapsedMilliseconds,
                    output,
                    currentDiagnostics);
                results.Add(result);
                Console.WriteLine(
                    $"  {result.Id,-18} chrF={result.ChrF:F3} " +
                    $"anchors={result.AnchorsHit}/{result.AnchorsTotal} " +
                    $"en={result.ResidualEnglish:F2} {result.Milliseconds,5} ms  {result.Output}");
            }

            BenchmarkAggregate overall = Aggregate(results);
            IReadOnlyDictionary<string, BenchmarkAggregate> perLanguage =
                AggregateBySourceLanguage(results);
            Report(overall, perLanguage);
            if (dumpPath is not null)
            {
                BenchmarkDump dump = new(
                    SchemaVersion: DumpSchemaVersion,
                    GeneratedAtUtc: DateTimeOffset.UtcNow,
                    ResolvedSetPath: resolvedSetPath,
                    SetSha256: setSha256,
                    Engine: normalizedEngine,
                    ModelId: FoundryTranslationService.ModelId,
                    FoundryCliVersion: await TryGetFoundryCliVersionAsync(),
                    Ranker: CreateRankerRuntime(configuration, rankerSelector),
                    Overall: overall,
                    PerLanguage: perLanguage,
                    Results: results);
                await WriteDumpAsync(dumpPath, dump);
            }

            EnforceNeuralExecution(configuration, overall);
            EnforceConfiguredThresholds(overall);
        }
        finally
        {
            try
            {
                if (rankerSelector is not null)
                {
                    await rankerSelector.DisposeAsync();
                }
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
    }

    internal static string NormalizeEngine(string engine)
        => ResolveEngine(engine).Name;

    internal static bool RequiresRankerBase(string engine) =>
        ResolveEngine(engine).RankerProvider is not null;

    private static EngineConfiguration ResolveEngine(string engine)
    {
        string normalized = (engine ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "qwen" => new EngineConfiguration(
                normalized,
                TranslationScaffoldMode.Disabled,
                RankerProvider: null),
            "qwen-scaffold" => new EngineConfiguration(
                normalized,
                TranslationScaffoldMode.Deterministic,
                RankerProvider: null),
            "qwen-scaffold-glossbert-cpu" => new EngineConfiguration(
                normalized,
                TranslationScaffoldMode.GlossBert,
                GlossBertRankerProvider.Cpu),
            "qwen-scaffold-glossbert-npu" => new EngineConfiguration(
                normalized,
                TranslationScaffoldMode.GlossBert,
                GlossBertRankerProvider.Npu),
            _ => throw new InvalidOperationException(
                $"Engine '{engine}' is not supported; expected 'qwen', 'qwen-scaffold', " +
                "'qwen-scaffold-glossbert-cpu' or 'qwen-scaffold-glossbert-npu'.")
        };
    }

    private static void ValidateRankerBase(
        EngineConfiguration configuration,
        string? rankerBaseDirectory)
    {
        if (configuration.RankerProvider is not null &&
            string.IsNullOrWhiteSpace(rankerBaseDirectory))
        {
            throw new InvalidOperationException(
                $"Engine '{configuration.Name}' requires --ranker-base.");
        }

        if (configuration.RankerProvider is null &&
            !string.IsNullOrWhiteSpace(rankerBaseDirectory))
        {
            throw new InvalidOperationException(
                "--ranker-base is valid only for a GlossBERT translation engine.");
        }
    }

    private static RankerRuntime? CreateRankerRuntime(
        EngineConfiguration configuration,
        GlossBertDictionarySelectorService? selector)
    {
        if (configuration.RankerProvider is not { } provider)
        {
            return null;
        }

        return new RankerRuntime(
            RequestedProvider: ProviderName(provider),
            ActiveProvider: selector?.ActiveProvider is { } activeProvider
                ? ProviderName(activeProvider)
                : null,
            ExpectedExecutionProvider: provider == GlossBertRankerProvider.Npu
                ? GlossBertDictionarySelectorService.NpuExecutionProvider
                : GlossBertDictionarySelectorService.CpuExecutionProvider,
            ExpectedDevice: provider == GlossBertRankerProvider.Npu ? "NPU" : "CPU",
            ModelVersion: selector?.ActiveModelVersion);
    }

    private static string ProviderName(GlossBertRankerProvider provider) =>
        provider == GlossBertRankerProvider.Npu ? "npu" : "cpu";

    private static void EnforceNeuralExecution(
        EngineConfiguration configuration,
        BenchmarkAggregate overall)
    {
        if (configuration.RankerProvider is null)
        {
            return;
        }

        ScaffoldDiagnosticsAggregate neural = overall.ScaffoldDiagnostics;
        if (neural.NeuralRequests == 0)
        {
            throw new InvalidOperationException(
                $"Engine '{configuration.Name}' made no GlossBERT requests; " +
                "the benchmark did not exercise its declared neural pipeline.");
        }

        if (neural.NeuralErrors > 0)
        {
            throw new InvalidOperationException(
                $"Engine '{configuration.Name}' recorded {neural.NeuralErrors} GlossBERT " +
                "error(s). Provider fallback is forbidden; inspect the schema-v3 dump.");
        }

        if (neural.NeuralTermsEmitted > 0)
        {
            throw new InvalidOperationException(
                $"Engine '{configuration.Name}' emitted {neural.NeuralTermsEmitted} mandatory " +
                "GlossBERT term(s). Neural senses must remain soft Qwen hints.");
        }

        if (neural.NeuralHintsEmitted == 0)
        {
            throw new InvalidOperationException(
                $"Engine '{configuration.Name}' produced no GlossBERT sense metadata.");
        }

        int senseReviews = neural.SenseReviewKeeps +
                           neural.SenseReviewCorrections +
                           neural.SenseReviewRejected +
                           neural.SenseReviewFailures;
        if (senseReviews == 0)
        {
            throw new InvalidOperationException(
                $"Engine '{configuration.Name}' never exercised the Qwen sense-review path.");
        }
    }

    private static void Report(
        BenchmarkAggregate overall,
        IReadOnlyDictionary<string, BenchmarkAggregate> perLanguage)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  chrF mean={overall.ChrFMean:F3} median={overall.ChrFMedian:F3} " +
            $"min={overall.ChrFMin:F3}");
        Console.WriteLine(
            $"  term anchors={overall.AnchorsHit}/{overall.AnchorsTotal} " +
            $"({100.0 * overall.AnchorRate:F1} %)");
        Console.WriteLine(
            $"  residual English mean={overall.ResidualEnglishMean:F3}; " +
            $"lines with any Latin={overall.LinesWithLatin}");
        Console.WriteLine(
            $"  latency median={overall.LatencyMedianMilliseconds:F1} ms " +
            $"max={overall.LatencyMaxMilliseconds} ms");
        ScaffoldDiagnosticsAggregate scaffold = overall.ScaffoldDiagnostics;
        if (scaffold.CasesWithDiagnostics > 0)
        {
            Console.WriteLine(
                $"  scaffold cases={scaffold.CasesWithDiagnostics} " +
                $"terms={scaffold.SatisfiedTermCount}/{scaffold.TermCount} " +
                $"exact={scaffold.ExactSentenceUses} retries={scaffold.RetriesIssued} " +
                $"violations={scaffold.InitialViolationCount}->{scaffold.FinalViolationCount} " +
                $"sense-conflicts={scaffold.InitialSenseConflictCount}->{scaffold.FinalSenseConflictCount} " +
                $"sense-retries={scaffold.SenseRetriesIssued}");
            if (scaffold.SenseRetriesIssued > 0)
            {
                Console.WriteLine(
                    $"  sense reviews keep={scaffold.SenseReviewKeeps} " +
                    $"fix={scaffold.SenseReviewCorrections} rejected={scaffold.SenseReviewRejected} " +
                    $"failed={scaffold.SenseReviewFailures}");
            }
            if (scaffold.NeuralRequests > 0 ||
                scaffold.NeuralSkipped > 0)
            {
                Console.WriteLine(
                    $"  GlossBERT requests={scaffold.NeuralRequests} " +
                    $"selected={scaffold.NeuralSelections} abstained={scaffold.NeuralAbstentions} " +
                    $"errors={scaffold.NeuralErrors} skipped={scaffold.NeuralSkipped} " +
                    $"after-error={scaffold.NeuralSkippedAfterError} " +
                    $"terms={scaffold.NeuralTermsEmitted} " +
                    $"hints={scaffold.NeuralHintsEmitted}");
            }
        }

        foreach ((string sourceLanguage, BenchmarkAggregate aggregate) in perLanguage)
        {
            Console.WriteLine(
                $"  {sourceLanguage}: cases={aggregate.Cases} chrF={aggregate.ChrFMean:F3} " +
                $"anchors={aggregate.AnchorRate:F3} residual={aggregate.ResidualEnglishMean:F3} " +
                $"latency={aggregate.LatencyMedianMilliseconds:F1} ms");
        }
    }

    private static BenchmarkAggregate Aggregate(IReadOnlyList<CaseResult> results)
    {
        if (results.Count == 0)
        {
            throw new InvalidOperationException("The translation benchmark produced no results.");
        }

        int anchorsTotal = results.Sum(result => result.AnchorsTotal);
        int anchorsHit = results.Sum(result => result.AnchorsHit);
        return new BenchmarkAggregate(
            Cases: results.Count,
            ChrFMean: results.Average(result => result.ChrF),
            ChrFMedian: Median(results.Select(result => result.ChrF)),
            ChrFMin: results.Min(result => result.ChrF),
            AnchorsTotal: anchorsTotal,
            AnchorsHit: anchorsHit,
            AnchorRate: anchorsTotal == 0 ? 1 : anchorsHit / (double)anchorsTotal,
            ResidualEnglishMean: results.Average(result => result.ResidualEnglish),
            LinesWithLatin: results.Count(result => result.ResidualEnglish > 0),
            LatencyMedianMilliseconds: Median(results.Select(result => (double)result.Milliseconds)),
            LatencyMaxMilliseconds: results.Max(result => result.Milliseconds),
            ScaffoldDiagnostics: AggregateScaffoldDiagnostics(results));
    }

    private static ScaffoldDiagnosticsAggregate AggregateScaffoldDiagnostics(
        IReadOnlyList<CaseResult> results)
    {
        TranslationScaffoldDiagnostics[] diagnostics = results
            .Select(result => result.ScaffoldDiagnostics)
            .OfType<TranslationScaffoldDiagnostics>()
            .ToArray();
        TranslationScaffoldNeuralDiagnostic[] neuralDecisions = diagnostics
            .SelectMany(item => item.NeuralDecisions)
            .ToArray();
        int neuralSelections = neuralDecisions.Count(item =>
            item.Status == TranslationScaffoldNeuralStatus.Selected);
        int neuralAbstentions = neuralDecisions.Count(item =>
            item.Status == TranslationScaffoldNeuralStatus.Abstained);
        int neuralErrors = neuralDecisions.Count(item =>
            item.Status == TranslationScaffoldNeuralStatus.Error);
        int neuralSkipped = neuralDecisions.Count(item =>
            item.Status == TranslationScaffoldNeuralStatus.Skipped);
        int neuralSkippedAfterError = neuralDecisions.Count(item =>
            item.Status == TranslationScaffoldNeuralStatus.Skipped &&
            string.Equals(
                item.Detail,
                NeuralDisabledAfterErrorDetail,
                StringComparison.Ordinal));
        return new ScaffoldDiagnosticsAggregate(
            CasesWithDiagnostics: diagnostics.Length,
            TermCount: diagnostics.Sum(item => item.TermCount),
            SatisfiedTermCount: diagnostics.Sum(item => item.SatisfiedTermCount),
            ExactSentenceUses: diagnostics.Count(item => item.ExactSentenceUsed),
            InitialViolationCount: diagnostics.Sum(item => item.InitialViolationCount),
            RetriesIssued: diagnostics.Count(item => item.RetryIssued),
            FinalViolationCount: diagnostics.Sum(item => item.FinalViolationCount),
            InitialSenseConflictCount: diagnostics.Sum(item => item.InitialSenseConflictCount),
            FinalSenseConflictCount: diagnostics.Sum(item => item.FinalSenseConflictCount),
            SenseRetriesIssued: diagnostics.Count(item =>
                item.RetryIssued && item.InitialSenseConflictCount > 0),
            SenseReviewKeeps: diagnostics.Count(item =>
                item.SenseReviewOutcome == TranslationSenseReviewOutcome.KeptOriginal),
            SenseReviewCorrections: diagnostics.Count(item =>
                item.SenseReviewOutcome == TranslationSenseReviewOutcome.AppliedCorrection),
            SenseReviewRejected: diagnostics.Count(item =>
                item.SenseReviewOutcome == TranslationSenseReviewOutcome.RejectedResponse),
            SenseReviewFailures: diagnostics.Count(item =>
                item.SenseReviewOutcome == TranslationSenseReviewOutcome.Failed),
            NeuralRequests: neuralSelections + neuralAbstentions + neuralErrors,
            NeuralSelections: neuralSelections,
            NeuralAbstentions: neuralAbstentions,
            NeuralErrors: neuralErrors,
            NeuralSkipped: neuralSkipped,
            NeuralSkippedAfterError: neuralSkippedAfterError,
            NeuralTermsEmitted: diagnostics.Sum(item => item.Terms.Count(term =>
                term.Provenance == ScaffoldTermProvenance.GlossBert)),
            NeuralHintsEmitted: diagnostics.Sum(item => item.SenseHints.Count),
            CasesWithNeuralRequests: diagnostics.Count(item => item.NeuralDecisions.Any(decision =>
                decision.Status is TranslationScaffoldNeuralStatus.Selected or
                    TranslationScaffoldNeuralStatus.Abstained or
                    TranslationScaffoldNeuralStatus.Error)),
            CasesWithNeuralErrors: diagnostics.Count(item => item.NeuralDecisions.Any(decision =>
                decision.Status == TranslationScaffoldNeuralStatus.Error)),
            CasesWithNeuralSkipped: diagnostics.Count(item => item.NeuralDecisions.Any(decision =>
                decision.Status == TranslationScaffoldNeuralStatus.Skipped)),
            CasesWithNeuralSkippedAfterError: diagnostics.Count(item =>
                item.NeuralDecisions.Any(decision =>
                    decision.Status == TranslationScaffoldNeuralStatus.Skipped &&
                    string.Equals(
                        decision.Detail,
                        NeuralDisabledAfterErrorDetail,
                        StringComparison.Ordinal))));
    }

    private static IReadOnlyDictionary<string, BenchmarkAggregate> AggregateBySourceLanguage(
        IReadOnlyList<CaseResult> results)
    {
        SortedDictionary<string, BenchmarkAggregate> aggregates = new(StringComparer.Ordinal);
        foreach (IGrouping<string, CaseResult> group in results.GroupBy(
                     result => result.SourceLanguage,
                     StringComparer.Ordinal))
        {
            aggregates.Add(group.Key, Aggregate(group.ToArray()));
        }

        return aggregates;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            throw new InvalidOperationException("A median requires at least one value.");
        }

        int upper = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[upper - 1] / 2.0) + (ordered[upper] / 2.0)
            : ordered[upper];
    }

    private static async Task WriteDumpAsync(string dumpPath, BenchmarkDump dump)
    {
        string resolvedDumpPath = Path.GetFullPath(dumpPath);
        string? directory = Path.GetDirectoryName(resolvedDumpPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(dump, DumpJsonOptions);
        await File.WriteAllTextAsync(resolvedDumpPath, json, new UTF8Encoding(false));
        Console.WriteLine($"  dump={resolvedDumpPath}");
    }

    private static async Task<string> TryGetFoundryCliVersionAsync()
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "foundry",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("--version");
            if (!process.Start())
            {
                return "unknown";
            }

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process may exit between the timeout and cleanup.
                }

                return "unknown";
            }

            string output = (await standardOutput).Trim();
            string error = (await standardError).Trim();
            if (process.ExitCode != 0)
            {
                return "unknown";
            }

            string version = string.IsNullOrWhiteSpace(output) ? error : output;
            string? firstLine = version.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(firstLine) ? "unknown" : firstLine.Trim();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                IOException or
                UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or
                PlatformNotSupportedException)
        {
            return "unknown";
        }
    }

    private static void EnforceConfiguredThresholds(BenchmarkAggregate aggregate)
    {
        EnforceMinimum("ASTERIA_TRANSLATION_MIN_CHRF", aggregate.ChrFMean);
        EnforceMinimum("ASTERIA_TRANSLATION_MIN_ANCHOR_RATE", aggregate.AnchorRate);
        EnforceMaximum("ASTERIA_TRANSLATION_MAX_RESIDUAL_ENGLISH", aggregate.ResidualEnglishMean);
    }

    private static void EnforceMinimum(string variable, double actual)
    {
        if (ReadThreshold(variable) is { } threshold && actual < threshold)
        {
            throw new InvalidOperationException(
                $"Translation quality gate {variable}={threshold:F3} failed: {actual:F3}.");
        }
    }

    private static void EnforceMaximum(string variable, double actual)
    {
        if (ReadThreshold(variable) is { } threshold && actual > threshold)
        {
            throw new InvalidOperationException(
                $"Translation quality gate {variable}={threshold:F3} failed: {actual:F3}.");
        }
    }

    private static double? ReadThreshold(string variable)
    {
        string? raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double threshold) ||
            !double.IsFinite(threshold) ||
            threshold is < 0 or > 1)
        {
            throw new InvalidOperationException(
                $"{variable} must be a number from 0 to 1 using a dot decimal separator.");
        }

        return threshold;
    }

    /// <summary>
    /// chrF with beta=2 (recall weighted): character n-gram F-score for n=1..6
    /// over whitespace-stripped text, the standard chrF preprocessing.
    /// </summary>
    internal static double ChrF(string hypothesis, string reference, int maximumN = 6, double beta = 2.0)
    {
        string left = StripForChrF(hypothesis);
        string right = StripForChrF(reference);
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        double precisionSum = 0;
        double recallSum = 0;
        int counted = 0;
        for (int n = 1; n <= maximumN; n++)
        {
            Dictionary<string, int> hypothesisGrams = CountNGrams(left, n);
            Dictionary<string, int> referenceGrams = CountNGrams(right, n);
            if (hypothesisGrams.Count == 0 || referenceGrams.Count == 0)
            {
                continue;
            }

            int common = 0;
            foreach ((string gram, int count) in hypothesisGrams)
            {
                if (referenceGrams.TryGetValue(gram, out int referenceCount))
                {
                    common += Math.Min(count, referenceCount);
                }
            }

            precisionSum += common / (double)hypothesisGrams.Values.Sum();
            recallSum += common / (double)referenceGrams.Values.Sum();
            counted++;
        }

        if (counted == 0)
        {
            return 0;
        }

        double precision = precisionSum / counted;
        double recall = recallSum / counted;
        double betaSquared = beta * beta;
        double denominator = (betaSquared * precision) + recall;
        return denominator <= 0
            ? 0
            : (1 + betaSquared) * precision * recall / denominator;
    }

    private static string StripForChrF(string value) =>
        new(value.Where(character => !char.IsWhiteSpace(character))
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static Dictionary<string, int> CountNGrams(string value, int n)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        for (int index = 0; index + n <= value.Length; index++)
        {
            string gram = value.Substring(index, n);
            counts[gram] = counts.GetValueOrDefault(gram) + 1;
        }

        return counts;
    }

    /// <summary>
    /// A term anchor is satisfied when any Russian word in the output starts
    /// with the expected stem, which tolerates inflection the same way the
    /// ranker benchmark does.
    /// </summary>
    internal static bool ContainsRussianStem(string output, string stem)
    {
        string normalizedStem = stem.ToLowerInvariant().Replace('ё', 'е');
        foreach (string word in Tokenize(output))
        {
            string normalized = word.ToLowerInvariant().Replace('ё', 'е');
            if (normalized.StartsWith(normalizedStem, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Share of Latin-script words: untranslated fragments.</summary>
    internal static double ResidualEnglishRate(string output)
    {
        string[] words = Tokenize(output).ToArray();
        if (words.Length == 0)
        {
            return 0;
        }

        int latin = words.Count(word =>
            word.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z'));
        return latin / (double)words.Length;
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        StringBuilder builder = new();
        foreach (char character in value)
        {
            if (char.IsLetter(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static BenchmarkCase[] LoadCases(string setPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(setPath));
        if (!document.RootElement.TryGetProperty("cases", out JsonElement casesElement) ||
            casesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The translation benchmark must contain a 'cases' array.");
        }

        List<BenchmarkCase> cases = [];
        int index = 0;
        foreach (JsonElement element in casesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Translation benchmark case #{index + 1} must be an object.");
            }

            string id = ReadString(element, "id", index);
            IReadOnlyList<(string English, string Russian)> anchors =
                ReadAnchors(element, id, index);
            cases.Add(new BenchmarkCase(
                id,
                ReadString(element, "sourceLanguage", index),
                ReadString(element, "source", index),
                ReadString(element, "reference", index),
                anchors));
            index++;
        }

        BenchmarkCase[] result = cases.ToArray();
        ValidateCases(result);
        return result;
    }

    private static string ReadString(JsonElement element, string propertyName, int caseIndex)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"Translation benchmark case #{caseIndex + 1} must contain string '{propertyName}'.");
        }

        return property.GetString() ?? string.Empty;
    }

    private static IReadOnlyList<(string English, string Russian)> ReadAnchors(
        JsonElement element,
        string caseId,
        int caseIndex)
    {
        if (!element.TryGetProperty("termAnchors", out JsonElement anchors))
        {
            return [];
        }

        if (anchors.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Translation benchmark case '{DisplayId(caseId, caseIndex)}' has a non-array termAnchors value.");
        }

        List<(string English, string Russian)> result = [];
        foreach (JsonElement anchor in anchors.EnumerateArray())
        {
            if (anchor.ValueKind != JsonValueKind.Object ||
                !anchor.TryGetProperty("en", out JsonElement english) ||
                english.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(english.GetString()) ||
                !anchor.TryGetProperty("ru", out JsonElement russian) ||
                russian.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(russian.GetString()))
            {
                throw new InvalidOperationException(
                    $"Translation benchmark case '{DisplayId(caseId, caseIndex)}' has an invalid term anchor.");
            }

            result.Add((english.GetString()!, russian.GetString()!));
        }

        return result;
    }

    private static string DisplayId(string caseId, int caseIndex) =>
        string.IsNullOrWhiteSpace(caseId) ? $"#{caseIndex + 1}" : caseId;

    private static void ValidateCases(IReadOnlyList<BenchmarkCase> cases)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (BenchmarkCase benchmarkCase in cases)
        {
            if (string.IsNullOrWhiteSpace(benchmarkCase.Id))
            {
                throw new InvalidOperationException("Translation benchmark case ids must be nonempty.");
            }

            if (!ids.Add(benchmarkCase.Id))
            {
                throw new InvalidOperationException(
                    $"Translation benchmark case id '{benchmarkCase.Id}' is duplicated.");
            }

            if (benchmarkCase.SourceLanguage is not ("en" or "ja"))
            {
                throw new InvalidOperationException(
                    $"Translation benchmark case '{benchmarkCase.Id}' has unsupported sourceLanguage " +
                    $"'{benchmarkCase.SourceLanguage}'; expected 'en' or 'ja'.");
            }

            if (string.IsNullOrWhiteSpace(benchmarkCase.Source))
            {
                throw new InvalidOperationException(
                    $"Translation benchmark case '{benchmarkCase.Id}' has an empty source.");
            }

            if (string.IsNullOrWhiteSpace(benchmarkCase.Reference))
            {
                throw new InvalidOperationException(
                    $"Translation benchmark case '{benchmarkCase.Id}' has an empty reference.");
            }
        }
    }

    /// <summary>Offline self-checks for the metrics themselves.</summary>
    internal static void VerifyMetrics()
    {
        double identical = ChrF("Приятно познакомиться!", "Приятно познакомиться!");
        Check(identical > 0.999, $"chrF of identical strings must be 1: {identical}");
        double unrelated = ChrF("Совершенно другое предложение", "Приятно познакомиться");
        Check(unrelated < identical && unrelated >= 0,
            $"chrF must rank an unrelated string lower: {unrelated}");
        double nearMatch = ChrF("Приятно познакомиться", "Приятно познакомиться!");
        Check(nearMatch > 0.9 && nearMatch < 1.0,
            $"chrF must tolerate light punctuation differences: {nearMatch}");
        Check(ChrF(string.Empty, "текст") == 0, "chrF of an empty hypothesis must be 0.");

        Check(ContainsRussianStem("Он был на грани смятения.", "гран"),
            "An inflected anchor must be found by its stem.");
        Check(ContainsRussianStem("Её щёки горели", "горе"),
            "The anchor matcher must fold ё to е.");
        Check(!ContainsRussianStem("Совсем другое", "гран"),
            "An absent anchor must not be reported.");

        Check(ResidualEnglishRate("Полностью русский текст") == 0,
            "Pure Russian must show no residual English.");
        Check(Math.Abs(ResidualEnglishRate("Я забыл umbrella дома") - 0.25) < 1e-9,
            "Residual English must be the share of Latin words.");

        Check(Math.Abs(Median([1.0, 9.0, 3.0]) - 3.0) < 1e-9,
            "An odd-sized median must select the middle ordered value.");
        Check(Math.Abs(Median([10.0, 30.0]) - 20.0) < 1e-9,
            "An even-sized median must average the two middle values.");

        CaseResult[] syntheticResults =
        [
            new(
                "en-1", "en", 0.2, 2, 1, 0.1, 10, "one",
                new(
                    Source: "source-one",
                    TermCount: 3,
                    SatisfiedTermCount: 2,
                    ExactSentenceUsed: false,
                    InitialViolationCount: 1,
                    RetryIssued: true,
                    FinalViolationCount: 0,
                    InitialSenseConflictCount: 1,
                    FinalSenseConflictCount: 0,
                    SenseReviewOutcome: TranslationSenseReviewOutcome.KeptOriginal,
                    Terms: [new ScaffoldTerm(
                        "ceremony",
                        "церемония",
                        Confidence: 1)],
                    NeuralDecisions: [new TranslationScaffoldNeuralDiagnostic(
                        "bank",
                        4,
                        TranslationScaffoldNeuralStatus.Selected,
                        CandidateGroupCount: 2,
                        CandidateGlossCount: 2,
                        SelectedGroupId: 1,
                        SelectedTranslation: "берег",
                        Margin: 0.4,
                        Score: 0.8,
                        Confidence: 0.9,
                        RequestedProvider: GlossBertRankerProvider.Cpu,
                        ExecutionProvider: GlossBertDictionarySelectorService.CpuExecutionProvider,
                        Device: "CPU",
                        ModelVersion: GlossBertDictionarySelectorService.CpuModelCacheId,
                        Pipeline: "cross",
                        ElapsedMilliseconds: 12)],
                    SenseHints: [new ScaffoldSenseHint(
                        "bank",
                        "sloping land alongside a river",
                        "берег",
                        ["банк"],
                        TranslationScaffoldBuilder.MinimumConfidence)])),
            new("en-2", "en", 0.6, 2, 2, 0.3, 30, "two", null),
            new(
                "ja-1", "ja", 0.4, 1, 0, 0.0, 20, "three",
                new(
                    Source: "source-three",
                    TermCount: 1,
                    SatisfiedTermCount: 1,
                    ExactSentenceUsed: true,
                    InitialViolationCount: 0,
                    RetryIssued: false,
                    FinalViolationCount: 0,
                    InitialSenseConflictCount: 0,
                    FinalSenseConflictCount: 0,
                    SenseReviewOutcome: TranslationSenseReviewOutcome.NotIssued,
                    Terms: [],
                    NeuralDecisions: [new TranslationScaffoldNeuralDiagnostic(
                        "light",
                        2,
                        TranslationScaffoldNeuralStatus.Abstained,
                        CandidateGroupCount: 3,
                        CandidateGlossCount: 4,
                        Margin: 0.1,
                        Score: 0.6,
                        Confidence: 0.7,
                        RequestedProvider: GlossBertRankerProvider.Cpu,
                        ExecutionProvider: GlossBertDictionarySelectorService.CpuExecutionProvider,
                        Device: "CPU",
                        ModelVersion: GlossBertDictionarySelectorService.CpuModelCacheId,
                        Pipeline: "cross",
                        ElapsedMilliseconds: 15)],
                    SenseHints: [])),
            new(
                "ja-2", "ja", 0.8, 1, 1, 0.2, 40, "four",
                new(
                    Source: "source-four",
                    TermCount: 2,
                    SatisfiedTermCount: 1,
                    ExactSentenceUsed: false,
                    InitialViolationCount: 1,
                    RetryIssued: true,
                    FinalViolationCount: 1,
                    InitialSenseConflictCount: 0,
                    FinalSenseConflictCount: 0,
                    SenseReviewOutcome: TranslationSenseReviewOutcome.NotIssued,
                    Terms: [],
                    NeuralDecisions:
                    [
                        new TranslationScaffoldNeuralDiagnostic(
                            "party",
                            3,
                            TranslationScaffoldNeuralStatus.Error,
                            CandidateGroupCount: 2,
                            CandidateGlossCount: 3,
                            ElapsedMilliseconds: 7,
                            ErrorCode: "synthetic_error",
                            Detail: "synthetic failure"),
                        new TranslationScaffoldNeuralDiagnostic(
                            "locked",
                            9,
                            TranslationScaffoldNeuralStatus.Skipped,
                            CandidateGroupCount: 2,
                            CandidateGlossCount: 2,
                            ErrorCode: NeuralDisabledAfterErrorDetail,
                            Detail: NeuralDisabledAfterErrorDetail)
                    ],
                    SenseHints: []))
        ];
        BenchmarkAggregate overall = Aggregate(syntheticResults);
        Check(
            overall.Cases == 4 &&
            Math.Abs(overall.ChrFMean - 0.5) < 1e-9 &&
            Math.Abs(overall.ChrFMedian - 0.5) < 1e-9 &&
            overall.AnchorsTotal == 6 &&
            overall.AnchorsHit == 4 &&
            Math.Abs(overall.AnchorRate - (2.0 / 3.0)) < 1e-9 &&
            Math.Abs(overall.ResidualEnglishMean - 0.15) < 1e-9 &&
            overall.LinesWithLatin == 3 &&
            Math.Abs(overall.LatencyMedianMilliseconds - 25.0) < 1e-9 &&
            overall.LatencyMaxMilliseconds == 40 &&
            overall.ScaffoldDiagnostics.CasesWithDiagnostics == 3 &&
            overall.ScaffoldDiagnostics.TermCount == 6 &&
            overall.ScaffoldDiagnostics.SatisfiedTermCount == 4 &&
            overall.ScaffoldDiagnostics.ExactSentenceUses == 1 &&
            overall.ScaffoldDiagnostics.InitialViolationCount == 2 &&
            overall.ScaffoldDiagnostics.RetriesIssued == 2 &&
            overall.ScaffoldDiagnostics.FinalViolationCount == 1 &&
            overall.ScaffoldDiagnostics.InitialSenseConflictCount == 1 &&
            overall.ScaffoldDiagnostics.FinalSenseConflictCount == 0 &&
            overall.ScaffoldDiagnostics.SenseRetriesIssued == 1 &&
            overall.ScaffoldDiagnostics.SenseReviewKeeps == 1 &&
            overall.ScaffoldDiagnostics.SenseReviewCorrections == 0 &&
            overall.ScaffoldDiagnostics.SenseReviewRejected == 0 &&
            overall.ScaffoldDiagnostics.SenseReviewFailures == 0 &&
            overall.ScaffoldDiagnostics.NeuralRequests == 3 &&
            overall.ScaffoldDiagnostics.NeuralSelections == 1 &&
            overall.ScaffoldDiagnostics.NeuralAbstentions == 1 &&
            overall.ScaffoldDiagnostics.NeuralErrors == 1 &&
            overall.ScaffoldDiagnostics.NeuralSkipped == 1 &&
            overall.ScaffoldDiagnostics.NeuralSkippedAfterError == 1 &&
            overall.ScaffoldDiagnostics.NeuralTermsEmitted == 0 &&
            overall.ScaffoldDiagnostics.NeuralHintsEmitted == 1 &&
            overall.ScaffoldDiagnostics.CasesWithNeuralRequests == 3 &&
            overall.ScaffoldDiagnostics.CasesWithNeuralErrors == 1 &&
            overall.ScaffoldDiagnostics.CasesWithNeuralSkipped == 1 &&
            overall.ScaffoldDiagnostics.CasesWithNeuralSkippedAfterError == 1,
            "Overall translation benchmark aggregation is inconsistent.");

        IReadOnlyDictionary<string, BenchmarkAggregate> byLanguage =
            AggregateBySourceLanguage(syntheticResults);
        Check(
            byLanguage.Count == 2 &&
            byLanguage["en"].Cases == 2 &&
            Math.Abs(byLanguage["en"].ChrFMedian - 0.4) < 1e-9 &&
            byLanguage["en"].ScaffoldDiagnostics.CasesWithDiagnostics == 1 &&
            byLanguage["en"].ScaffoldDiagnostics.NeuralSelections == 1 &&
            byLanguage["en"].ScaffoldDiagnostics.NeuralTermsEmitted == 0 &&
            byLanguage["en"].ScaffoldDiagnostics.NeuralHintsEmitted == 1 &&
            Math.Abs(byLanguage["ja"].LatencyMedianMilliseconds - 30.0) < 1e-9 &&
            byLanguage["ja"].ScaffoldDiagnostics.CasesWithDiagnostics == 2 &&
            byLanguage["ja"].ScaffoldDiagnostics.TermCount == 3 &&
            byLanguage["ja"].ScaffoldDiagnostics.NeuralAbstentions == 1 &&
            byLanguage["ja"].ScaffoldDiagnostics.NeuralErrors == 1,
            "Per-language translation benchmark aggregation is inconsistent.");

        EngineConfiguration neuralCpu = ResolveEngine("qwen-scaffold-glossbert-cpu");
        BenchmarkAggregate validNeuralOverall = overall with
        {
            ScaffoldDiagnostics = overall.ScaffoldDiagnostics with { NeuralErrors = 0 }
        };
        EnforceNeuralExecution(neuralCpu, validNeuralOverall);
        ExpectInvalid(
            () => EnforceNeuralExecution(
                neuralCpu,
                validNeuralOverall with
                {
                    ScaffoldDiagnostics = validNeuralOverall.ScaffoldDiagnostics with
                    {
                        NeuralTermsEmitted = 1
                    }
                }),
            "A neural translation benchmark accepted a mandatory GlossBERT term.");
        ExpectInvalid(
            () => EnforceNeuralExecution(
                neuralCpu,
                validNeuralOverall with
                {
                    ScaffoldDiagnostics = validNeuralOverall.ScaffoldDiagnostics with
                    {
                        NeuralHintsEmitted = 0
                    }
                }),
            "A neural translation benchmark accepted a run without sense metadata.");
        ExpectInvalid(
            () => EnforceNeuralExecution(
                neuralCpu,
                validNeuralOverall with
                {
                    ScaffoldDiagnostics = validNeuralOverall.ScaffoldDiagnostics with
                    {
                        SenseReviewKeeps = 0,
                        SenseReviewCorrections = 0,
                        SenseReviewRejected = 0,
                        SenseReviewFailures = 0
                    }
                }),
            "A neural translation benchmark accepted senses that never reached Qwen review.");
        ExpectInvalid(
            () => EnforceNeuralExecution(neuralCpu, overall),
            "A neural translation benchmark accepted a ranker error.");
        ExpectInvalid(
            () => EnforceNeuralExecution(
                neuralCpu,
                overall with
                {
                    ScaffoldDiagnostics = overall.ScaffoldDiagnostics with
                    {
                        NeuralRequests = 0,
                        NeuralErrors = 0
                    }
                }),
            "A neural translation benchmark accepted a run without ranker requests.");
        EnforceNeuralExecution(ResolveEngine("qwen-scaffold"), overall);

        string schemaJson = JsonSerializer.Serialize(
            new
            {
                SchemaVersion = DumpSchemaVersion,
                Overall = overall,
                Result = syntheticResults[0]
            },
            DumpJsonOptions);
        Check(
            schemaJson.Contains("\"schemaVersion\": 3", StringComparison.Ordinal) &&
            schemaJson.Contains("\"neuralRequests\": 3", StringComparison.Ordinal) &&
            schemaJson.Contains("\"neuralTermsEmitted\": 0", StringComparison.Ordinal) &&
            schemaJson.Contains("\"neuralHintsEmitted\": 1", StringComparison.Ordinal) &&
            schemaJson.Contains("\"status\": \"selected\"", StringComparison.Ordinal) &&
            schemaJson.Contains(
                "\"englishDefinition\": \"sloping land alongside a river\"",
                StringComparison.Ordinal) &&
            schemaJson.Contains("\"candidateGroupCount\": 2", StringComparison.Ordinal),
            "Translation benchmark schema v3 omitted neural diagnostics or enum names.");

        Check(NormalizeEngine("qwen") == "qwen",
            "The baseline engine name must stay stable.");
        Check(NormalizeEngine(" QWEN-SCAFFOLD ") == "qwen-scaffold",
            "The scaffold engine name must be trimmed and case-normalized.");
        Check(
            NormalizeEngine(" QWEN-SCAFFOLD-GLOSSBERT-CPU ") ==
            "qwen-scaffold-glossbert-cpu" &&
            RequiresRankerBase("qwen-scaffold-glossbert-cpu"),
            "The CPU GlossBERT engine must be explicit and require ranker assets.");
        Check(
            NormalizeEngine("qwen-scaffold-glossbert-npu") ==
            "qwen-scaffold-glossbert-npu" &&
            RequiresRankerBase("qwen-scaffold-glossbert-npu"),
            "The NPU GlossBERT engine must be explicit and require ranker assets.");
        Check(!RequiresRankerBase("qwen-scaffold"),
            "The deterministic scaffold engine must not require ranker assets.");
        RankerRuntime cpuRuntime = CreateRankerRuntime(
            ResolveEngine("qwen-scaffold-glossbert-cpu"),
            selector: null)!;
        Check(
            cpuRuntime.RequestedProvider == "cpu" &&
            cpuRuntime.ActiveProvider is null &&
            cpuRuntime.ExpectedExecutionProvider ==
            GlossBertDictionarySelectorService.CpuExecutionProvider &&
            cpuRuntime.ExpectedDevice == "CPU",
            "The CPU ranker identity in schema v3 is inconsistent.");
        ValidateRankerBase(
            ResolveEngine("qwen-scaffold-glossbert-cpu"),
            "synthetic-ranker-base");
        ExpectInvalid(
            () => ValidateRankerBase(
                ResolveEngine("qwen-scaffold-glossbert-cpu"),
                rankerBaseDirectory: null),
            "A neural translation engine accepted a missing ranker base directory.");
        ExpectInvalid(
            () => ValidateRankerBase(
                ResolveEngine("qwen-scaffold"),
                "synthetic-ranker-base"),
            "A deterministic translation engine accepted --ranker-base.");
        ExpectInvalid(
            () => NormalizeEngine("foundry"),
            "Unsupported translation benchmark engines must be rejected.");

        BenchmarkCase validCase = new("valid", "en", "Hello", "Russian reference", []);
        ValidateCases([validCase]);
        ExpectInvalid(
            () => ValidateCases([validCase, validCase with { Source = "Different" }]),
            "Duplicate benchmark ids must be rejected.");
        ExpectInvalid(
            () => ValidateCases([validCase with { SourceLanguage = "fr" }]),
            "Unsupported benchmark source languages must be rejected.");
        ExpectInvalid(
            () => ValidateCases([validCase with { Id = " " }]),
            "Empty benchmark ids must be rejected.");
        ExpectInvalid(
            () => ValidateCases([validCase with { Source = string.Empty }]),
            "Empty benchmark sources must be rejected.");
        ExpectInvalid(
            () => ValidateCases([validCase with { Reference = string.Empty }]),
            "Empty benchmark references must be rejected.");
    }

    private static void ExpectInvalid(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
