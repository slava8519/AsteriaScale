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
    int ShardCount,
    TranslateGemmaTranslationService.TranslateGemmaModelPin? PhraseModelPin = null,
    string? SenseGateTracePath = null,
    TranslateGemmaTranslationService.VulkanCoopmat PhraseVulkanCoopmat =
        TranslateGemmaTranslationService.ShippedVulkanCoopmat)
{
    internal const string Usage =
        "Usage: --benchmark-only --translation-suite <dataset.jsonl> " +
        "<workerBaseDirectory> --system-id <id> --profile direct|smart " +
        "--phrase-backend cpu|vulkan --ranker-cpu-model " +
        "glossbert|sandwich|consec --translation-output <output.jsonl> " +
        "--translation-report <report.json> [--max-cases <N>] " +
        "[--shard <index>/<count>] [--phrase-quant q6_k] " +
        "[--sense-gate-trace <trace.jsonl>] [--phrase-coopmat on|off]";

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
        TranslateGemmaTranslationService.TranslateGemmaModelPin? phraseModelPin = null;
        string? senseGateTracePath = null;
        TranslateGemmaTranslationService.VulkanCoopmat? phraseVulkanCoopmat = null;

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

                case "--phrase-quant":
                    RequireUnset(phraseModelPin, option);
                    phraseModelPin = AlternativeModelPins.Resolve(value);
                    break;

                case "--sense-gate-trace":
                    RequireUnset(senseGateTracePath, option);
                    senseGateTracePath = value;
                    break;

                case "--phrase-coopmat":
                    RequireUnset(phraseVulkanCoopmat, option);
                    phraseVulkanCoopmat = value.ToLowerInvariant() switch
                    {
                        "on" => TranslateGemmaTranslationService.VulkanCoopmat.Enabled,
                        "off" => TranslateGemmaTranslationService.VulkanCoopmat.Disabled,
                        _ => throw new InvalidOperationException(Usage)
                    };
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

        // The smart lane builds its own translator inside the hover pipeline,
        // so an alternative quantization would be silently ignored there. A
        // gated option that quietly does nothing is exactly defect C1.
        if (phraseModelPin is not null && profile != TranslationSuiteProfile.Direct)
        {
            throw new InvalidOperationException(
                "--phrase-quant поддерживается только профилем direct. " + Usage);
        }

        // The direct profile never runs the sense gate, so a trace there would
        // be an empty file that looks like a measurement (defect C1 again).
        if (senseGateTracePath is not null && profile != TranslationSuiteProfile.Smart)
        {
            throw new InvalidOperationException(
                "--sense-gate-trace поддерживается только профилем smart. " + Usage);
        }

        // Only the Vulkan runner reads GGML_VK_DISABLE_COOPMAT. On the CPU
        // backend the switch would change nothing while the run claimed a
        // variant, which is defect C1 again. Both profiles accept it: the
        // smart lane passes the same translator instance into the hover
        // pipeline, and the pipeline takes its request identity from it.
        if (phraseVulkanCoopmat is not null &&
            phraseBackend != TranslateGemmaExecutionBackend.Vulkan)
        {
            throw new InvalidOperationException(
                "--phrase-coopmat поддерживается только с --phrase-backend vulkan. " + Usage);
        }

        RequireExtension(datasetPath, ".jsonl", "dataset");
        RequireExtension(translationOutputPath, ".jsonl", "translation output");
        RequireExtension(translationReportPath, ".json", "translation report");
        if (senseGateTracePath is not null)
        {
            RequireExtension(senseGateTracePath, ".jsonl", "sense-gate trace");
        }

        string datasetFullPath = Path.GetFullPath(datasetPath);
        string outputFullPath = Path.GetFullPath(translationOutputPath);
        string reportFullPath = Path.GetFullPath(translationReportPath);
        string? traceFullPath = senseGateTracePath is null
            ? null
            : Path.GetFullPath(senseGateTracePath);
        string[] distinctPaths = traceFullPath is null
            ? [datasetFullPath, outputFullPath, reportFullPath]
            : [datasetFullPath, outputFullPath, reportFullPath, traceFullPath];
        if (distinctPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            distinctPaths.Length)
        {
            throw new InvalidOperationException(
                "Dataset, output JSONL, report JSON and trace JSONL must use different paths.");
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
            shardCount,
            phraseModelPin,
            traceFullPath,
            phraseVulkanCoopmat ?? TranslateGemmaTranslationService.ShippedVulkanCoopmat);
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

internal sealed record TranslationSuiteSenseEvidence(
    string Source,
    string EnglishDefinition,
    string SelectedTranslation,
    // Reported, but deliberately outside the evidence fingerprint: the hash
    // identifies what the prompt saw, and neither number reaches the prompt.
    // Keeping them out is what lets two runs with identical prompts compare.
    double Confidence,
    SenseEvidenceOrigin Origin);

internal sealed record TranslationSuiteSmartMetadata(
    string DictionaryEngine,
    bool DictionaryFound,
    bool DictionaryIsPhrase,
    string DictionaryTranslation,
    IReadOnlyList<TranslationSuiteSenseEvidence> SenseEvidence,
    string SenseEvidenceSha256,
    IReadOnlyList<TranslationSuiteSenseEvidence> AppliedSenseEvidence,
    string? AppliedSenseEvidenceSha256);

internal sealed record TranslationSuiteSystemResult(
    TranslationSuiteCaseStatus Status,
    string Hypothesis,
    bool FromCache,
    TranslationSuiteFocus? Focus,
    string? Engine = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    // Which output guard refused an existing translation, when one did.
    // `errorCode` only says that the production lane returned nothing.
    SmartPhraseRejectionReason? RejectionReason = null,
    TranslationSuiteSmartMetadata? Metadata = null);

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
    SmartPhraseRejectionReason? RejectionReason,
    TranslationSuiteFocus? Focus,
    bool FromCache,
    string SystemId,
    string Profile,
    string PhraseBackend,
    string? RankerCpuModel,
    string? Engine,
    string ModelIdentity,
    string PromptIdentity,
    TranslationSuiteSmartMetadata? Metadata = null);

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
