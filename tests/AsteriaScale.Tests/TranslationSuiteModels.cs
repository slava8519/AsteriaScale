using System.Text.RegularExpressions;
using AsteriaScale.Services;

internal enum TranslationSuiteProfile
{
    Direct,
    Smart
}

internal sealed record TranslationSuiteOptions(
    string DatasetPath,
    string WorkerBaseDirectory,
    string SystemId,
    TranslationSuiteProfile Profile,
    TranslateGemmaExecutionBackend PhraseBackend,
    RankerCpuModelProfile RankerCpuModel,
    string TranslationOutputPath,
    string TranslationReportPath,
    int? MaximumCases,
    int ShardIndex,
    int ShardCount)
{
    internal const string Usage =
        "Usage: --benchmark-only --translation-suite <dataset.jsonl> " +
        "<workerBaseDirectory> --system-id <id> --profile direct|smart " +
        "--phrase-backend cpu|vulkan --ranker-cpu-model " +
        "glossbert|sandwich|consec --translation-output <output.jsonl> " +
        "--translation-report <report.json> [--max-cases <N>] " +
        "[--shard <index>/<count>]";

    internal static TranslationSuiteOptions Parse(
        IReadOnlyList<string> arguments,
        int optionIndex)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (optionIndex < 0 || optionIndex + 2 >= arguments.Count ||
            !string.Equals(
                arguments[optionIndex],
                "--translation-suite",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Usage);
        }

        string datasetPath = RequirePositional(arguments[optionIndex + 1]);
        string workerBaseDirectory = RequirePositional(arguments[optionIndex + 2]);
        string? systemId = null;
        TranslationSuiteProfile? profile = null;
        TranslateGemmaExecutionBackend? phraseBackend = null;
        RankerCpuModelProfile? rankerCpuModel = null;
        string? translationOutputPath = null;
        string? translationReportPath = null;
        int? maximumCases = null;
        int shardIndex = 1;
        int shardCount = 1;
        bool shardSeen = false;

        for (int index = optionIndex + 3; index < arguments.Count; index++)
        {
            string option = arguments[index];
            string value = RequireOptionValue(arguments, ref index, option);
            switch (option.ToLowerInvariant())
            {
                case "--system-id":
                    RequireUnset(systemId, option);
                    if (!Regex.IsMatch(
                            value,
                            "^[a-z0-9](?:[a-z0-9._-]{0,63})$",
                            RegexOptions.CultureInvariant))
                    {
                        throw new InvalidOperationException(
                            $"Некорректный system-id '{value}'. {Usage}");
                    }

                    systemId = value;
                    break;

                case "--profile":
                    RequireUnset(profile, option);
                    profile = value.ToLowerInvariant() switch
                    {
                        "direct" => TranslationSuiteProfile.Direct,
                        "smart" => TranslationSuiteProfile.Smart,
                        _ => throw new InvalidOperationException(Usage)
                    };
                    break;

                case "--phrase-backend":
                    RequireUnset(phraseBackend, option);
                    phraseBackend = value.ToLowerInvariant() switch
                    {
                        "cpu" => TranslateGemmaExecutionBackend.Cpu,
                        "vulkan" => TranslateGemmaExecutionBackend.Vulkan,
                        _ => throw new InvalidOperationException(Usage)
                    };
                    break;

                case "--ranker-cpu-model":
                    RequireUnset(rankerCpuModel, option);
                    rankerCpuModel = value.ToLowerInvariant() switch
                    {
                        "glossbert" => RankerCpuModelProfile.GlossBert,
                        "sandwich" => RankerCpuModelProfile.Sandwich,
                        "consec" => RankerCpuModelProfile.Consec,
                        _ => throw new InvalidOperationException(Usage)
                    };
                    break;

                case "--translation-output":
                    RequireUnset(translationOutputPath, option);
                    translationOutputPath = value;
                    break;

                case "--translation-report":
                    RequireUnset(translationReportPath, option);
                    translationReportPath = value;
                    break;

                case "--max-cases":
                    RequireUnset(maximumCases, option);
                    if (!int.TryParse(
                            value,
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out int parsedMaximum) ||
                        parsedMaximum <= 0)
                    {
                        throw new InvalidOperationException(Usage);
                    }

                    maximumCases = parsedMaximum;
                    break;

                case "--shard":
                    if (shardSeen)
                    {
                        throw new InvalidOperationException(
                            $"Параметр '{option}' указан повторно. {Usage}");
                    }

                    Match shardMatch = Regex.Match(
                        value,
                        "^(?<index>[1-9][0-9]*)/(?<count>[1-9][0-9]*)$",
                        RegexOptions.CultureInvariant);
                    if (!shardMatch.Success ||
                        !int.TryParse(
                            shardMatch.Groups["index"].Value,
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out shardIndex) ||
                        !int.TryParse(
                            shardMatch.Groups["count"].Value,
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out shardCount) ||
                        shardIndex > shardCount || shardCount > 1_024)
                    {
                        throw new InvalidOperationException(Usage);
                    }

                    shardSeen = true;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Неизвестный параметр '{option}'. {Usage}");
            }
        }

        if (systemId is null || profile is null || phraseBackend is null ||
            rankerCpuModel is null || translationOutputPath is null ||
            translationReportPath is null)
        {
            throw new InvalidOperationException(Usage);
        }

        RequireExtension(datasetPath, ".jsonl", "dataset");
        RequireExtension(translationOutputPath, ".jsonl", "translation output");
        RequireExtension(translationReportPath, ".json", "translation report");
        string datasetFullPath = Path.GetFullPath(datasetPath);
        string outputFullPath = Path.GetFullPath(translationOutputPath);
        string reportFullPath = Path.GetFullPath(translationReportPath);
        if (string.Equals(datasetFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(datasetFullPath, reportFullPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputFullPath, reportFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Dataset, output JSONL and report JSON must use three different paths.");
        }

        return new TranslationSuiteOptions(
            datasetFullPath,
            Path.GetFullPath(workerBaseDirectory),
            systemId,
            profile.Value,
            phraseBackend.Value,
            rankerCpuModel.Value,
            outputFullPath,
            reportFullPath,
            maximumCases,
            shardIndex,
            shardCount);
    }

    internal string ProfileId => Profile == TranslationSuiteProfile.Direct
        ? "direct"
        : "smart";

    internal string PhraseBackendId => PhraseBackend == TranslateGemmaExecutionBackend.Cpu
        ? "cpu"
        : "vulkan";

    internal string RankerCpuModelId => RankerCpuModel switch
    {
        RankerCpuModelProfile.Sandwich => "sandwich",
        RankerCpuModelProfile.Consec => "consec",
        _ => "glossbert"
    };

    internal string? OutputRankerCpuModelId =>
        Profile == TranslationSuiteProfile.Smart ? RankerCpuModelId : null;

    private static string RequirePositional(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(Usage);
        }

        return value;
    }

    private static string RequireOptionValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        if (!option.StartsWith("--", StringComparison.Ordinal) ||
            index + 1 >= arguments.Count ||
            arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Параметру '{option}' требуется значение. {Usage}");
        }

        return arguments[++index];
    }

    private static void RequireUnset<T>(T? current, string option)
    {
        if (current is not null)
        {
            throw new InvalidOperationException(
                $"Параметр '{option}' указан повторно. {Usage}");
        }
    }

    private static void RequireExtension(string path, string extension, string label)
    {
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{label} must use the {extension} extension. {Usage}");
        }
    }
}

internal sealed record TranslationSuiteFocus(
    string Text,
    int CharacterIndex,
    bool Annotated);

internal sealed record TranslationSuiteSpeaker(
    string Name,
    SpeakerGender Gender);

internal sealed record TranslationSuiteCase(
    string Id,
    string Source,
    string SourceSha256,
    IReadOnlyList<string> References,
    bool ReferenceFree,
    IReadOnlyList<string> PreviousContext,
    TranslationSuiteFocus? Focus,
    TranslationSuiteSpeaker? Speaker);

internal sealed record TranslationSuiteDatasetSnapshot(
    IReadOnlyList<TranslationSuiteCase> Cases,
    BenchmarkArtifactRecord Artifact);

internal enum TranslationSuiteCaseStatus
{
    Ok,
    Unavailable,
    Error
}

internal sealed record TranslationSuiteSystemResult(
    TranslationSuiteCaseStatus Status,
    string Hypothesis,
    bool FromCache,
    TranslationSuiteFocus? Focus,
    string? Engine = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

internal sealed record TranslationSuiteCaseOutput(
    int SchemaVersion,
    string Id,
    string SourceSha256,
    string SelectionSha256,
    string Hypothesis,
    TranslationSuiteCaseStatus Status,
    long LatencyMilliseconds,
    string? ErrorCode,
    string? ErrorMessage,
    TranslationSuiteFocus? Focus,
    bool FromCache,
    string SystemId,
    string Profile,
    string PhraseBackend,
    string? RankerCpuModel,
    string? Engine,
    string ModelIdentity,
    string PromptIdentity);

internal sealed record TranslationSuiteLatencySummary(
    int Samples,
    long? P50Milliseconds,
    long? P95Milliseconds,
    long? MaximumMilliseconds,
    int SuccessfulSamples,
    long? SuccessfulP50Milliseconds,
    long? SuccessfulP95Milliseconds);

internal sealed record TranslationSuiteSummary(
    int Cases,
    int Ok,
    int Unavailable,
    int Errors,
    double Coverage,
    int CacheHits,
    TranslationSuiteLatencySummary Latency);

internal sealed record TranslationSuiteSystemProvenance(
    string TranslationModelIdentity,
    string TranslationPromptIdentity,
    string? RankerModelVersion,
    string? RankerProvider,
    string? AbstentionTag,
    BenchmarkArtifactRecord ApplicationArtifact,
    BenchmarkArtifactRecord HarnessArtifact,
    RankerBenchmarkProvenanceData? Ranker);

internal sealed record TranslationSuiteReport(
    int SchemaVersion,
    string GeneratedUtc,
    string EvaluationScope,
    string SystemId,
    string Profile,
    string PhraseBackend,
    string? RankerCpuModel,
    int DatasetCases,
    int? MaximumCases,
    int ShardIndex,
    int ShardCount,
    string SelectionSha256,
    IReadOnlyList<string> SelectedCaseIds,
    BenchmarkArtifactRecord Dataset,
    TranslationSuiteSystemProvenance Provenance,
    TranslationSuiteSummary Summary,
    IReadOnlyList<TranslationSuiteCaseOutput> Cases);

internal interface ITranslationSuiteSystem : IAsyncDisposable
{
    Task PrepareAsync(CancellationToken cancellationToken);

    Task<TranslationSuiteSystemResult> TranslateAsync(
        TranslationSuiteCase benchmarkCase,
        CancellationToken cancellationToken);

    Task<TranslationSuiteSystemProvenance> CaptureProvenanceAsync(
        string datasetPath,
        CancellationToken cancellationToken);
}
