using System.Text.Json;
using AsteriaScale.Services;

/// <summary>
/// Measures whether a full-text translation keeps the register of its English
/// source (roadmap #14.2C). Runs the production path: real scaffold, real
/// prompt, live Qwen through Foundry Local.
///
/// Register on free-form output cannot be judged by comparing strings — a reply
/// has many valid renderings — so each case carries stem anchors. A faithful
/// anchor must appear, a softened marker must not (that is the sanitising this
/// phase removes), and a strengthened marker must not appear on a plain source.
///
/// General adequacy is deliberately not gated here: that is what
/// translation-benchmark.json is for. chrF against the authored reference is
/// reported as an indicator only, so a register pass on a broken translation is
/// still visible to a reader.
/// </summary>
internal static class TranslationRegisterBenchmark
{
    private const double MinimumRegisterFidelity = 0.90;
    private const double MinimumNoUnjustifiedStrengthening = 0.95;

    private sealed record RegisterReply(
        string Id,
        string Bucket,
        string Source,
        string Reference,
        string ExpectedRegister,
        IReadOnlyList<string> Faithful,
        IReadOnlyList<string> Softened,
        IReadOnlyList<string> Strengthened);

    private sealed record ReplyOutcome(
        string Id,
        string Bucket,
        string Output,
        bool Faithful,
        bool Softened,
        bool Strengthened,
        bool Refused,
        double ChrF);

    // A model that declines or emits a censorship stub is a hard failure, not a
    // low score: the card requires zero such answers.
    private static readonly string[] RefusalMarkers =
    [
        "не могу", "не буду", "извините", "я не могу помочь", "как ии",
        "as an ai", "i cannot", "i can't", "[цензура]", "***", "censored"
    ];

    public static async Task RunAsync(
        string setPath,
        string? reportPath = null)
    {
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(setPath));
        RegisterReply[] replies = document.RootElement.GetProperty("cases")
            .EnumerateArray()
            .Select(element => new RegisterReply(
                element.GetProperty("id").GetString()!,
                element.GetProperty("bucket").GetString()!,
                element.GetProperty("source").GetString()!,
                element.GetProperty("reference").GetString()!,
                element.GetProperty("expectedRegister").GetString()!,
                ReadStems(element, "faithfulAnchors"),
                ReadStems(element, "softenedMarkers"),
                ReadStems(element, "strengthenedMarkers")))
            .ToArray();
        if (replies.Length == 0)
        {
            throw new InvalidDataException("The register reply set contains no cases.");
        }

        string isolatedUserDirectory = Path.Combine(
            Path.GetTempPath(),
            "AsteriaScale",
            "translation-register-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedUserDirectory);
        try
        {
            LocalDictionaryService dictionary = new(
                Path.Combine(AppContext.BaseDirectory, "Dictionaries"),
                isolatedUserDirectory);
            await using FoundryTranslationService translator = new(
                dictionary,
                TranslationScaffoldMode.Deterministic,
                diagnosticSink: null);
            await translator.WarmUpAsync();

            List<ReplyOutcome> outcomes = [];
            foreach (RegisterReply reply in replies)
            {
                string output = await translator.TranslateToRussianAsync(
                    reply.Source,
                    CancellationToken.None);
                string probe = Normalize(output);
                bool refused = probe.Length == 0 ||
                               RefusalMarkers.Any(marker =>
                                   probe.Contains(marker, StringComparison.Ordinal));
                ReplyOutcome outcome = new(
                    reply.Id,
                    reply.Bucket,
                    output,
                    Faithful: !refused &&
                              reply.Faithful.Any(stem => probe.Contains(stem, StringComparison.Ordinal)) &&
                              !reply.Softened.Any(stem => probe.Contains(stem, StringComparison.Ordinal)),
                    Softened: reply.Softened.Any(stem => probe.Contains(stem, StringComparison.Ordinal)),
                    Strengthened: reply.Strengthened.Any(stem => probe.Contains(stem, StringComparison.Ordinal)),
                    refused,
                    TranslationBenchmark.ChrF(output, reply.Reference));
                outcomes.Add(outcome);
                Console.WriteLine(
                    $"  {outcome.Id,-10} {(outcome.Faithful ? "ok  " : "MISS")} " +
                    $"{(outcome.Softened ? "softened " : "")}" +
                    $"{(outcome.Strengthened ? "STRENGTHENED " : "")}" +
                    $"{(outcome.Refused ? "REFUSED " : "")}" +
                    $"chrF={outcome.ChrF:F3}  {Trim(output)}");
            }

            Report(outcomes, reportPath);
        }
        finally
        {
            try
            {
                Directory.Delete(isolatedUserDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory must not fail a measurement run.
            }
        }
    }

    private static IReadOnlyList<string> ReadStems(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement array)
            ? array.EnumerateArray()
                .Select(value => Normalize(value.GetString() ?? string.Empty))
                .Where(value => value.Length > 0)
                .ToArray()
            : [];

    // Stems are matched against a lowercased, ё-folded form so that authored
    // anchors do not have to spell every orthographic variant.
    private static string Normalize(string value) =>
        string.Join(
            " ",
            value.ToLowerInvariant().Replace('ё', 'е')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Trim(string value) =>
        value.Length <= 58 ? value : value[..57] + "…";

    private static void Report(
        IReadOnlyList<ReplyOutcome> outcomes,
        string? reportPath)
    {
        int faithful = outcomes.Count(outcome => outcome.Faithful);
        int softened = outcomes.Count(outcome => outcome.Softened);
        int strengthened = outcomes.Count(outcome => outcome.Strengthened);
        int refused = outcomes.Count(outcome => outcome.Refused);

        Console.WriteLine();
        foreach (IGrouping<string, ReplyOutcome> bucket in outcomes
                     .GroupBy(outcome => outcome.Bucket)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"  {bucket.Key,-20} n={bucket.Count(),3} " +
                $"faithful={bucket.Count(outcome => outcome.Faithful),3} " +
                $"softened={bucket.Count(outcome => outcome.Softened),3} " +
                $"strengthened={bucket.Count(outcome => outcome.Strengthened),3} " +
                $"medianChrF={Median(bucket.Select(outcome => outcome.ChrF)):F3}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"  register fidelity           : {faithful}/{outcomes.Count} " +
            $"({faithful / (double)outcomes.Count:P1})");
        Console.WriteLine(
            $"  no unjustified strengthening: {outcomes.Count - strengthened}/{outcomes.Count} " +
            $"({(outcomes.Count - strengthened) / (double)outcomes.Count:P1})");
        Console.WriteLine($"  softened answers            : {softened}");
        Console.WriteLine($"  refusals / censored / empty : {refused}");
        Console.WriteLine(
            $"  median chrF (indicator only): " +
            $"{Median(outcomes.Select(outcome => outcome.ChrF)):F3}");

        if (reportPath is not null)
        {
            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(
                    new { SchemaVersion = 1, Outcomes = outcomes },
                    new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  report: {Path.GetFullPath(reportPath)}");
        }

        VerifyGates(outcomes.Count, faithful, strengthened, refused);
    }

    internal static void VerifyGates(
        int total,
        int faithfulCount,
        int strengthenedCount,
        int refusedCount)
    {
        if (total == 0)
        {
            throw new InvalidOperationException("The register reply set measured nothing.");
        }

        if (refusedCount > 0)
        {
            throw new InvalidOperationException(
                $"{refusedCount} reply/replies were refused, censored or empty; the gate is zero.");
        }

        double fidelity = faithfulCount / (double)total;
        if (fidelity < MinimumRegisterFidelity)
        {
            throw new InvalidOperationException(
                $"Full-text register fidelity is {fidelity:P1}, below the " +
                $"{MinimumRegisterFidelity:P0} minimum ({faithfulCount}/{total}).");
        }

        double noStrengthening = (total - strengthenedCount) / (double)total;
        if (noStrengthening < MinimumNoUnjustifiedStrengthening)
        {
            throw new InvalidOperationException(
                $"Full-text no-unjustified-strengthening is {noStrengthening:P1}, below the " +
                $"{MinimumNoUnjustifiedStrengthening:P0} minimum.");
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.OrderBy(value => value).ToArray();
        return ordered.Length == 0 ? 0 : ordered[ordered.Length / 2];
    }
}
