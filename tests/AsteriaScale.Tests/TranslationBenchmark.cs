using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        int FinalViolationCount);

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
        BenchmarkAggregate Overall,
        IReadOnlyDictionary<string, BenchmarkAggregate> PerLanguage,
        IReadOnlyList<CaseResult> Results);

    public static async Task RunAsync(string setPath, string engine, string? dumpPath)
    {
        string normalizedEngine = NormalizeEngine(engine);
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
        try
        {
            LocalDictionaryService dictionary = new(
                Path.Combine(AppContext.BaseDirectory, "Dictionaries"),
                isolatedUserDirectory);
            TranslationScaffoldDiagnostics? currentDiagnostics = null;
            await using FoundryTranslationService translator = new(
                dictionary,
                useTranslationScaffold: normalizedEngine == "qwen-scaffold",
                diagnosticSink: diagnostics => currentDiagnostics = diagnostics);
            await translator.WarmUpAsync();

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
                    SchemaVersion: 2,
                    GeneratedAtUtc: DateTimeOffset.UtcNow,
                    ResolvedSetPath: resolvedSetPath,
                    SetSha256: setSha256,
                    Engine: normalizedEngine,
                    ModelId: FoundryTranslationService.ModelId,
                    FoundryCliVersion: await TryGetFoundryCliVersionAsync(),
                    Overall: overall,
                    PerLanguage: perLanguage,
                    Results: results);
                await WriteDumpAsync(dumpPath, dump);
            }

            EnforceConfiguredThresholds(overall);
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

    internal static string NormalizeEngine(string engine)
    {
        string normalized = (engine ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "qwen" => normalized,
            "qwen-scaffold" => normalized,
            _ => throw new InvalidOperationException(
                $"Engine '{engine}' is not supported; expected 'qwen' or 'qwen-scaffold'.")
        };
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
                $"violations={scaffold.InitialViolationCount}->{scaffold.FinalViolationCount}");
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
        return new ScaffoldDiagnosticsAggregate(
            CasesWithDiagnostics: diagnostics.Length,
            TermCount: diagnostics.Sum(item => item.TermCount),
            SatisfiedTermCount: diagnostics.Sum(item => item.SatisfiedTermCount),
            ExactSentenceUses: diagnostics.Count(item => item.ExactSentenceUsed),
            InitialViolationCount: diagnostics.Sum(item => item.InitialViolationCount),
            RetriesIssued: diagnostics.Count(item => item.RetryIssued),
            FinalViolationCount: diagnostics.Sum(item => item.FinalViolationCount));
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

        string json = JsonSerializer.Serialize(
            dump,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
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
                new("source-one", 3, 2, false, 1, true, 0, [])),
            new("en-2", "en", 0.6, 2, 2, 0.3, 30, "two", null),
            new(
                "ja-1", "ja", 0.4, 1, 0, 0.0, 20, "three",
                new("source-three", 1, 1, true, 0, false, 0, [])),
            new(
                "ja-2", "ja", 0.8, 1, 1, 0.2, 40, "four",
                new("source-four", 2, 1, false, 1, true, 1, []))
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
            overall.ScaffoldDiagnostics.FinalViolationCount == 1,
            "Overall translation benchmark aggregation is inconsistent.");

        IReadOnlyDictionary<string, BenchmarkAggregate> byLanguage =
            AggregateBySourceLanguage(syntheticResults);
        Check(
            byLanguage.Count == 2 &&
            byLanguage["en"].Cases == 2 &&
            Math.Abs(byLanguage["en"].ChrFMedian - 0.4) < 1e-9 &&
            byLanguage["en"].ScaffoldDiagnostics.CasesWithDiagnostics == 1 &&
            Math.Abs(byLanguage["ja"].LatencyMedianMilliseconds - 30.0) < 1e-9 &&
            byLanguage["ja"].ScaffoldDiagnostics.CasesWithDiagnostics == 2 &&
            byLanguage["ja"].ScaffoldDiagnostics.TermCount == 3,
            "Per-language translation benchmark aggregation is inconsistent.");

        Check(NormalizeEngine("qwen") == "qwen",
            "The baseline engine name must stay stable.");
        Check(NormalizeEngine(" QWEN-SCAFFOLD ") == "qwen-scaffold",
            "The scaffold engine name must be trimmed and case-normalized.");
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
