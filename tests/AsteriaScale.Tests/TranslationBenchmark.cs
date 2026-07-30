using System.Text;
using System.Text.Json;

/// <summary>
/// Offline translation-set schema checks and model-independent quality metrics.
/// Model execution belongs to the active translation pipeline, not the smoke suite.
/// </summary>
internal static class TranslationBenchmark
{
    private sealed record BenchmarkCase(
        string Id,
        string SourceLanguage,
        string Source,
        string Reference,
        IReadOnlyList<(string English, string Russian)> Anchors);

    /// <summary>
    /// Validates the stable input schema without starting a translation model.
    /// </summary>
    internal static void ValidateSetSchema(string setPath) =>
        _ = LoadCases(setPath);

    /// <summary>
    /// chrF with beta=2 (recall weighted): character n-gram F-score for n=1..6
    /// over whitespace-stripped text, the standard chrF preprocessing.
    /// </summary>
    internal static double ChrF(
        string hypothesis,
        string reference,
        int maximumN = 6,
        double beta = 2.0)
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

    /// <summary>
    /// A term anchor is satisfied when any Russian word in the output starts
    /// with the expected stem, which tolerates inflection.
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

    /// <summary>Share of Latin-script words in a translation.</summary>
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

    /// <summary>Offline self-checks for the schema and metrics.</summary>
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
        Check(ChrF(string.Empty, "текст") == 0,
            "chrF of an empty hypothesis must be 0.");

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

        BenchmarkCase validCase = new("valid", "en", "Hello", "Привет", []);
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
            throw new InvalidOperationException(
                "The translation benchmark must contain a 'cases' array.");
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
            cases.Add(new BenchmarkCase(
                id,
                ReadString(element, "sourceLanguage", index),
                ReadString(element, "source", index),
                ReadString(element, "reference", index),
                ReadAnchors(element, id, index)));
            index++;
        }

        BenchmarkCase[] result = cases.ToArray();
        ValidateCases(result);
        return result;
    }

    private static string ReadString(
        JsonElement element,
        string propertyName,
        int caseIndex)
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
                throw new InvalidOperationException(
                    "Translation benchmark case ids must be nonempty.");
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
