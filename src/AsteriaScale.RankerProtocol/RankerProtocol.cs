using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsteriaScale.RankerProtocol;

/// <summary>
/// Dependency-free JSON Lines protocol shared by Asteria Scale and the isolated
/// ONNX ranker process. One request always produces exactly one response.
/// </summary>
public static class RankerProtocolV2
{
    // v4: a select response may declare a provider-neutral semantic ranking
    // pipeline. The model identity still pins the exact artifacts; the pipeline
    // tells caches and diagnostics which scoring contract produced the result.
    public const string Version = "4";
    public const string Hello = "hello";
    public const string Warm = "warm";
    public const string Select = "select";
    public const string Shutdown = "shutdown";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}

/// <summary>
/// Source-compatibility bridge for the worker while both projects move to v2.
/// It deliberately advertises v2, so an old binary cannot silently enter the
/// new protocol.
/// </summary>
public static class RankerProtocolV1
{
    public const string Version = RankerProtocolV2.Version;
    public const string Hello = RankerProtocolV2.Hello;
    public const string Warm = RankerProtocolV2.Warm;
    public const string Select = RankerProtocolV2.Select;
    public const string Shutdown = RankerProtocolV2.Shutdown;

    public static JsonSerializerOptions JsonOptions => RankerProtocolV2.JsonOptions;
}

public sealed record RankerRequest
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = RankerProtocolV2.Version;

    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("context")]
    public string? Context { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("characterIndex")]
    public int? CharacterIndex { get; init; }

    [JsonPropertyName("sourceLanguage")]
    public string? SourceLanguage { get; init; }

    [JsonPropertyName("candidates")]
    public IReadOnlyList<string>? Candidates { get; init; }

    /// <summary>
    /// Stable outcome ids parallel to <see cref="Candidates"/>. Multiple glosses
    /// may point to the same outcome; the selected response index is one of
    /// these ids rather than a positional candidate index.
    /// </summary>
    [JsonPropertyName("candidateGroupIds")]
    public IReadOnlyList<int>? CandidateGroupIds { get; init; }
}

public sealed record RankerResponse
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = RankerProtocolV2.Version;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("index")]
    public int? Index { get; init; }

    [JsonPropertyName("abstained")]
    public bool Abstained { get; init; }

    /// <summary>
    /// Difference between the best and second-best outcome scores. It is the
    /// primary calibrated abstention signal for the GlossBERT ranker.
    /// </summary>
    [JsonPropertyName("margin")]
    public double? Margin { get; init; }

    /// <summary>
    /// Raw score of the selected outcome. It is diagnostic, not a probability.
    /// </summary>
    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("device")]
    public string? Device { get; init; }

    [JsonPropertyName("modelVersion")]
    public string? ModelVersion { get; init; }

    /// <summary>
    /// Ranking pipeline used for a select response: "cross" (pure GlossBERT),
    /// "bi+cross" (MiniLM prefilter, then GlossBERT re-ranking), or a named
    /// semantic-ranker adapter such as "sandwich".
    /// </summary>
    [JsonPropertyName("pipeline")]
    public string? Pipeline { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
