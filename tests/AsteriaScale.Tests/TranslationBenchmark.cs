using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AsteriaScale.Services;

/// <summary>
/// Measures the full-translation pipeline (roadmap task #13): chrF against a
/// reference, term-anchor fidelity, residual English and latency. It drives the
/// production translator, so it needs a live Foundry Local model and stays a
/// gated CLI mode rather than part of the default smoke suite.
/// </summary>
internal static class TranslationBenchmark
{
    private sealed record BenchmarkCase(
        string Id,
        string Source,
        string Reference,
        IReadOnlyList<(string English, string Russian)> Anchors);

    private sealed record CaseResult(
        string Id,
        double ChrF,
        int AnchorsTotal,
        int AnchorsHit,
        double ResidualEnglish,
        long Milliseconds,
        string Output);

    public static async Task RunAsync(string setPath, string engine, string? dumpPath)
    {
        BenchmarkCase[] cases = LoadCases(setPath);
        Console.WriteLine($"  set={Path.GetFileName(setPath)} cases={cases.Length} engine={engine}");
        if (!string.Equals(engine, "qwen", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Engine '{engine}' is not implemented yet; only 'qwen' exists before roadmap #14/#16.");
        }

        await using FoundryTranslationService translator = new();
        await translator.WarmUpAsync();

        List<CaseResult> results = [];
        foreach (BenchmarkCase benchmarkCase in cases)
        {
            Stopwatch timer = Stopwatch.StartNew();
            string output = await translator.TranslateToRussianAsync(
                benchmarkCase.Source,
                CancellationToken.None);
            timer.Stop();

            int hits = benchmarkCase.Anchors.Count(
                anchor => ContainsRussianStem(output, anchor.Russian));
            CaseResult result = new(
                benchmarkCase.Id,
                ChrF(output, benchmarkCase.Reference),
                benchmarkCase.Anchors.Count,
                hits,
                ResidualEnglishRate(output),
                timer.ElapsedMilliseconds,
                output);
            results.Add(result);
            Console.WriteLine(
                $"  {result.Id,-18} chrF={result.ChrF:F3} " +
                $"anchors={result.AnchorsHit}/{result.AnchorsTotal} " +
                $"en={result.ResidualEnglish:F2} {result.Milliseconds,5} ms  {result.Output}");
        }

        Report(results);
        if (dumpPath is not null)
        {
            await File.WriteAllTextAsync(
                dumpPath,
                JsonSerializer.Serialize(
                    results,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void Report(IReadOnlyList<CaseResult> results)
    {
        if (results.Count == 0)
        {
            Console.WriteLine("  no results");
            return;
        }

        int anchorsTotal = results.Sum(result => result.AnchorsTotal);
        int anchorsHit = results.Sum(result => result.AnchorsHit);
        long[] latencies = results.Select(result => result.Milliseconds)
            .OrderBy(value => value)
            .ToArray();
        double[] chrF = results.Select(result => result.ChrF)
            .OrderBy(value => value)
            .ToArray();
        Console.WriteLine();
        Console.WriteLine(
            $"  chrF mean={chrF.Average():F3} median={chrF[chrF.Length / 2]:F3} " +
            $"min={chrF[0]:F3}");
        Console.WriteLine(
            $"  term anchors={anchorsHit}/{anchorsTotal} " +
            $"({(anchorsTotal == 0 ? 0 : 100.0 * anchorsHit / anchorsTotal):F1} %)");
        Console.WriteLine(
            $"  residual English mean={results.Average(result => result.ResidualEnglish):F3}; " +
            $"lines with any Latin={results.Count(result => result.ResidualEnglish > 0)}");
        Console.WriteLine(
            $"  latency median={latencies[latencies.Length / 2]} ms " +
            $"max={latencies[^1]} ms");
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
        return document.RootElement.GetProperty("cases")
            .EnumerateArray()
            .Select(element => new BenchmarkCase(
                element.GetProperty("id").GetString()!,
                element.GetProperty("source").GetString()!,
                element.GetProperty("reference").GetString()!,
                element.TryGetProperty("termAnchors", out JsonElement anchors)
                    ? anchors.EnumerateArray()
                        .Select(anchor => (
                            anchor.GetProperty("en").GetString()!,
                            anchor.GetProperty("ru").GetString()!))
                        .ToArray()
                    : []))
            .ToArray();
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
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
