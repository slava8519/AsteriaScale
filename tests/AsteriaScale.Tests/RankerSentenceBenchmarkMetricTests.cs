using AsteriaScale.Services;
using System.Text.Json;

internal static class RankerSentenceBenchmarkMetricTests
{
    public static Task RunAsync(string _)
    {
        SentenceCaseRunResult[] cases = CreatePredictions();
        SentenceBenchmarkScopeMetrics overall =
            RankerSentenceBenchmarkMetrics.CalculateScope(cases);
        Check(overall.Sentences == 4 &&
              overall.AllTargetSensesCorrectSentences == 1,
            "All-target-senses-correct math did not count one fully correct sentence.");
        Check(overall.Targets == 7 && overall.TargetSenseCorrect == 3,
            "Target sense accuracy used the wrong denominator.");
        Check(overall.Decided == 4 && Close(overall.DecisionCoverage, 4d / 7d),
            "Decision coverage counted abstentions, errors, or overflow as decisions.");
        Check(Close(overall.AcceptedTargetSenseAccuracy, 3d / 4d),
            "Accepted target sense accuracy was not conditioned on decisions.");
        Check(overall.Abstained == 1 && overall.Errors == 1 && overall.Overflows == 1,
            "Abstention, error, and overflow counters were conflated.");
        Check(overall.SurfaceUnambiguousTargets == 4 &&
              overall.SurfaceUnambiguousSenseCorrect == 2 &&
              Close(overall.SurfaceUnambiguousSenseAccuracy, 0.5),
            "Surface-unambiguous sense accuracy used the wrong target set.");
        Check(overall.SurfaceUnambiguousDecided == 3 &&
              Close(overall.AcceptedSurfaceUnambiguousSenseAccuracy, 2d / 3d),
            "Accepted surface-unambiguous sense accuracy used the wrong denominator.");

        SentenceBenchmarkScopeMetrics scientific =
            RankerSentenceBenchmarkMetrics.CalculateScope(
                cases.Where(item => item.Category == "scientific"));
        Check(scientific.Sentences == 2 && scientific.Targets == 4 &&
              scientific.TargetSenseCorrect == 3 &&
              scientific.AllTargetSensesCorrectSentences == 1,
            "The scientific first-class slice was not isolated.");

        VerifyMetricSchemaNames(overall);

        IReadOnlyDictionary<string, SentenceBenchmarkScopeMetrics> categories =
            RankerSentenceBenchmarkMetrics.CalculateSlices(cases, item => item.Category);
        IReadOnlyDictionary<string, SentenceBenchmarkScopeMetrics> subdomains =
            RankerSentenceBenchmarkMetrics.CalculateSlices(cases, item => item.Subdomain);
        Check(categories.Count == 3 && categories["narrative"].Targets == 2,
            "Domain/category metric slicing is inconsistent.");
        Check(subdomains.Count == 3 && subdomains["biology"].Targets == 4,
            "Scientific subdomain slicing is inconsistent.");

        SentenceBenchmarkContrastMetrics contrasts =
            RankerSentenceBenchmarkMetrics.CalculateContrasts(cases);
        Check(contrasts.ContrastPairs == 1 &&
              contrasts.AllTargetSensesCorrectPairs == 0,
            "Contrast all-target-senses-correct math accepted a partly wrong pair.");
        Check(contrasts.PairedTargets == 2 &&
              contrasts.PairedTargetsBothSenseCorrect == 1,
            "Contrast paired-target math is inconsistent.");
        Check(contrasts.ChangedGoldTargetPairs == 1 &&
              contrasts.ChangedGoldTargetPairsBothSenseCorrect == 1,
            "Context-changed gold targets were not measured separately.");
        Check(contrasts.PairsWithAbstention == 1 &&
              contrasts.PairsWithErrorOrOverflow == 0,
            "Contrast failure categories are inconsistent.");

        SentenceBenchmarkLatencyMetrics latency =
            RankerSentenceBenchmarkMetrics.CalculateLatency(500, cases);
        Check(latency.ColdWarmupMilliseconds == 500 &&
              latency.WarmSamples == 6 &&
              latency.WarmP50Milliseconds == 30 &&
              latency.WarmP95Milliseconds == 45,
            "Cold/warm nearest-rank latency percentiles are incorrect.");
        Check(latency.SuccessfulWarmSamples == 5 &&
              latency.SuccessfulWarmP50Milliseconds == 30 &&
              latency.SuccessfulWarmP95Milliseconds == 40,
            "Successful-request latency excluded the wrong samples.");

        SentenceBenchmarkErrorMetrics errors =
            RankerSentenceBenchmarkMetrics.CalculateErrors(cases);
        Check(errors.TargetErrors == 1 && errors.CandidateOverflows == 1 &&
              errors.ErrorCodes.Count == 1 &&
              errors.ErrorCodes["protocol_error"] == 1,
            "Error-code and overflow aggregation is inconsistent.");

        VerifyCommandParser();
        return Task.CompletedTask;
    }

    private static void VerifyMetricSchemaNames(SentenceBenchmarkScopeMetrics metrics)
    {
        Check(RankerSentenceBenchmark.ReportSchemaVersion == 2 &&
              RankerSentenceBenchmark.ReportEvaluationScope ==
              "wsd-sense-selection-only",
            "The report schema does not declare its WSD-only evaluation scope.");

        string json = JsonSerializer.Serialize(
            metrics,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(json.Contains(
                  "\"allTargetSensesCorrectSentences\"",
                  StringComparison.Ordinal) &&
              json.Contains(
                  "\"surfaceUnambiguousSenseCorrect\"",
                  StringComparison.Ordinal) &&
              !json.Contains("\"sentenceExact\"", StringComparison.Ordinal) &&
              !json.Contains("\"visibleSurfaceCorrect\"", StringComparison.Ordinal),
            "The report metric schema still implies full or visible translation scoring.");
    }

    private static void VerifyCommandParser()
    {
        SentenceRankerBenchmarkOptions options =
            SentenceRankerBenchmarkOptions.Parse(
                [
                    "--ranker-sentence-benchmark",
                    "sentences.json",
                    "dist",
                    "--sentence-report",
                    "report.json",
                    "--ranker-cpu-model",
                    "consec"
                ],
                0);
        Check(options.CpuModel == RankerCpuModelProfile.Consec &&
              options.OutputPath == "report.json",
            "The sentence benchmark parser rejected ConSeC or reordered options.");

        SentenceRankerBenchmarkOptions aliased =
            SentenceRankerBenchmarkOptions.Parse(
                [
                    "--ranker-sentence-benchmark",
                    "sentences.json",
                    "dist",
                    "--ranker-cpu-model",
                    "CONSEC",
                    "--output",
                    "consec.json"
                ],
                0);
        Check(aliased.CpuModel == RankerCpuModelProfile.Consec &&
              aliased.OutputPath == "consec.json",
            "The sentence benchmark output alias or a case-insensitive profile was rejected.");

        foreach (string removedProfile in new[] { "sandwich", "deberta" })
        {
            CheckThrows(() => SentenceRankerBenchmarkOptions.Parse(
                    [
                        "--ranker-sentence-benchmark",
                        "sentences.json",
                        "dist",
                        "--ranker-cpu-model",
                        removedProfile
                    ],
                    0),
                $"The removed '{removedProfile}' profile leaked into the sentence selector profiles.");
        }
        CheckThrows(() => SentenceRankerBenchmarkOptions.Parse(
                [
                    "--ranker-sentence-benchmark",
                    "sentences.json",
                    "dist",
                    "--output",
                    "one.json",
                    "--sentence-report",
                    "two.json"
                ],
                0),
            "Two output paths were accepted for one sentence benchmark.");
        CheckThrows(
            () => RankerSentenceBenchmark.RejectFrozenHoldout(
#if WINDOWS
                @"C:\research\ranker-holdout-v4.json"),
#else
                "/research/ranker-holdout-v4.json"),
#endif
            "The open sentence runner accepted a frozen holdout path.");
    }

    private static SentenceCaseRunResult[] CreatePredictions() =>
    [
        new(
            "scientific-left",
            "biology",
            "biology",
            "scientific",
            "sample-contrast",
            UsedPreviousContext: true,
            Targets:
            [
                Target(0, "sample", 0, 0, surface: true, latency: 10),
                Target(1, "cells", 1, null, surface: false, abstained: true, latency: 20)
            ]),
        new(
            "scientific-right",
            "biology",
            "biology",
            "scientific",
            "sample-contrast",
            UsedPreviousContext: true,
            Targets:
            [
                Target(0, "sample", 1, 1, surface: true, latency: 30),
                Target(1, "cells", 1, 1, surface: false, latency: 35)
            ]),
        new(
            "narrative",
            "literature",
            "literature",
            "narrative",
            ContrastSetId: null,
            UsedPreviousContext: false,
            Targets:
            [
                Target(0, "bank", 0, 1, surface: true, latency: 40),
                Target(
                    1,
                    "draft",
                    0,
                    null,
                    surface: true,
                    errorCode: "protocol_error",
                    latency: 45)
            ]),
        new(
            "contextual",
            "contextual",
            "contextual",
            "contextual",
            ContrastSetId: null,
            UsedPreviousContext: true,
            Targets:
            [
                Target(0, "trial", 1, null, surface: false, overflow: true)
            ])
    ];

    private static SentenceTargetRunResult Target(
        int index,
        string text,
        int gold,
        int? selected,
        bool surface,
        bool abstained = false,
        bool overflow = false,
        string? errorCode = null,
        long? latency = null)
    {
        bool decided = selected.HasValue && !abstained && !overflow && errorCode is null;
        bool correct = decided && selected == gold;
        return new SentenceTargetRunResult(
            index,
            text,
            gold,
            ExpectedTranslation: "ожидаемый",
            GoldTranslations: ["ожидаемый"],
            selected,
            surface,
            abstained,
            overflow,
            errorCode,
            ErrorMessage: null,
            SenseCorrect: correct,
            SurfaceUnambiguousSenseCorrect: surface ? correct : null,
            SelectedTranslations: [],
            Margin: null,
            Score: null,
            Confidence: null,
            Pipeline: decided || abstained ? "fake" : null,
            latency);
    }

    private static bool Close(double? actual, double expected) =>
        actual.HasValue && Math.Abs(actual.Value - expected) < 1e-12;

    private static void CheckThrows(Action action, string message)
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
