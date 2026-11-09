using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsteriaScale.CoreHost.Protocol;

// Protocol v1. Every frame is one UTF-8 JSON object on one line ending in \n:
//   request : {"version":1,"id":17,"command":"hover.resolve","payload":{...}}
//   response: {"version":1,"id":17,"ok":true,"result":{...}}
//             {"version":1,"id":17,"ok":false,"error":{"code":"...","message":"<ru>","detail":"<en>"}}
//   event   : {"version":1,"event":"hover.primary","ref":17,"payload":{...}}
// Field names follow RankerProtocol's long-name style. The golden transcript
// tests/AsteriaScale.Tests.MacOS/CoreHost/protocol-v1.golden.jsonl pins the
// wire bytes; the Swift app decodes the same file.
internal static class WireProtocol
{
    public const int Version = 1;
    public const int MaxInboundFrameBytes = 1 << 20;        // 1 MiB, connection-fatal
    public const int MaxOutboundFrameBytes = 4 << 20;       // 4 MiB, internal_error for that request
    public const long MaxQueuedOutboundBytes = 16L << 20;   // 16 MiB: the peer stopped reading, exit 5
    public const int MaxOutstandingRequests = 32;           // busy
    public const int MaxJsonDepth = 16;
}

internal static class ExitCodes
{
    public const int Clean = 0;
    public const int Usage = 2;
    public const int ProtocolViolation = 3;
    public const int PeerGone = 4;
    public const int OutputOverflow = 5;
    public const int ShutdownTimeout = 6;
    public const int AlreadyRunning = 7;
    public const int Internal = 70;
}

internal static class ErrorCodes
{
    // Connection-fatal: core.fault, then exit 3.
    public const string InvalidFrame = "invalid_frame";
    public const string FrameTooLong = "frame_too_long";
    public const string InvalidId = "invalid_id";
    public const string UnsupportedVersion = "unsupported_version";

    // Request-level terminals.
    public const string HelloRequired = "hello_required";
    public const string BadRequest = "bad_request";
    public const string UnknownCommand = "unknown_command";
    public const string NotImplemented = "not_implemented";
    public const string NotReady = "not_ready";
    public const string Busy = "busy";
    public const string ShuttingDown = "shutting_down";
    public const string Cancelled = "cancelled";
    public const string Superseded = "superseded";
    public const string InternalError = "internal_error";

    // Lifecycle faults.
    public const string ShutdownTimeout = "shutdown_timeout";
    public const string HoverFailed = "hover_failed";
}

internal sealed class ErrorBody
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Detail { get; set; }
    public long? SupersededBy { get; set; }
}

internal sealed class EmptyPayload;

internal sealed class EmptyResult;

internal sealed class HelloPayload
{
    [JsonRequired] public string Client { get; set; } = "";
    [JsonRequired] public int ProtocolMin { get; set; }
    [JsonRequired] public int ProtocolMax { get; set; }
}

internal sealed class HelloResult
{
    public int Protocol { get; set; }
    public string CoreHostVersion { get; set; } = "";
    public string CoreVersion { get; set; } = "";
    public string Build { get; set; } = "";
    public string Runtime { get; set; } = "";
    public bool Aot { get; set; }
    public int Pid { get; set; }
    public int Pgid { get; set; }
    public bool ProcessGroupLeader { get; set; }
    public string Platform { get; set; } = "";
    public bool TestCommands { get; set; }
    public string[] BlockedSignals { get; set; } = [];
    public HostPaths Paths { get; set; } = new();
    public Capabilities Capabilities { get; set; } = new();
}

internal sealed class HostPaths
{
    public string DataRoot { get; set; } = "";
    public string UserData { get; set; } = "";
    public string LogDir { get; set; } = "";
    public string? CacheDir { get; set; }
    public string? RankerWorker { get; set; }
    public string? RankerModels { get; set; }
}

internal sealed class Capabilities
{
    public string[] HoverModes { get; set; } = [];
    public string[] HoverSources { get; set; } = [];
    public string[] SenseProviders { get; set; } = [];
    public string[] SenseModels { get; set; } = [];
    public string[] PhraseBackends { get; set; } = [];
    public string[] PhraseVariants { get; set; } = [];
}

internal sealed class PingResult
{
    public double UptimeMs { get; set; }
    public int Outstanding { get; set; }
    public long GcHeapBytes { get; set; }
    public long WorkingSetBytes { get; set; }
}

internal sealed class CancelPayload
{
    [JsonRequired] public long Target { get; set; }
}

internal sealed class CancelResult
{
    public bool Cancelled { get; set; }
    public string? Reason { get; set; }
}

internal sealed class HoverResolvePayload
{
    public string? Mode { get; set; }
    [JsonRequired] public SelectionSource Source { get; set; } = new();
    public GameIdentity? Game { get; set; }
}

internal sealed class SelectionSource
{
    [JsonRequired] public string Kind { get; set; } = "";
    [JsonRequired] public string Text { get; set; } = "";
    [JsonRequired] public string HoveredWord { get; set; } = "";
    [JsonRequired] public int CharacterIndex { get; set; }
    public bool SentenceStable { get; set; } = true;
    public string? SpeakerName { get; set; }
}

internal sealed class GameIdentity
{
    [JsonRequired] public string Key { get; set; } = "";
    [JsonRequired] public string DisplayName { get; set; } = "";
}

internal sealed class HoverResolveResult
{
    public string Outcome { get; set; } = "";
    public HoverTimings Timings { get; set; } = new();
}

internal sealed class HoverTimings
{
    public double QueuedMs { get; set; }
    public double? PrimaryMs { get; set; }
    public double? PhraseFirstMs { get; set; }
    public double? PhraseFinalMs { get; set; }
}

internal sealed class HoverPrimaryEvent
{
    public string Source { get; set; } = "";
    public string Translation { get; set; } = "";
    public string? Language { get; set; }
    public bool IsPhrase { get; set; }
    public bool Found { get; set; }
    public double? Confidence { get; set; }
    public string Provenance { get; set; } = "";
    public string EngineLabel { get; set; } = "";
}

internal sealed class HoverPhraseEvent
{
    public string Source { get; set; } = "";
    public string Translation { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Provisional { get; set; }
    public bool FromCache { get; set; }
    public string? Rejection { get; set; }
    public PhraseModelIdentity Model { get; set; } = new();
}

internal sealed class PhraseModelIdentity
{
    public string Family { get; set; } = "";
    public string Variant { get; set; } = "";
    public string Backend { get; set; } = "";
    public string Identity { get; set; } = "";
}

internal sealed class StatusEvent
{
    public string Area { get; set; } = "";
    public string State { get; set; } = "";
    public string? Code { get; set; }
    public string Text { get; set; } = "";
}

internal sealed class FaultEvent
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Detail { get; set; }
}

// Test commands (only with --enable-test-commands; hello reports it).

internal sealed class TestSleepPayload
{
    [JsonRequired] public int Ms { get; set; }
    public string? Mode { get; set; }
    public bool IgnoreCancel { get; set; }
    public int Ticks { get; set; }
}

internal sealed class TestTickEvent
{
    public int N { get; set; }
    public string? Padding { get; set; }
}

internal sealed class TestFakeCorePayload
{
    // Keyed by the hovered word, so a delay belongs to a request no matter
    // whether a superseded hover reached the engine before it.
    public Dictionary<string, int>? PrimaryDelaysMs { get; set; }
    public string? Phrase { get; set; }
    public int PhraseDelayMs { get; set; }
    public bool IgnoreCancel { get; set; }
    public bool Fail { get; set; }
}

internal sealed class TestBytesPayload
{
    [JsonRequired] public int Bytes { get; set; }
}

internal sealed class TestStatsResult
{
    public long DroppedEvents { get; set; }
    public long FramesWritten { get; set; }
    public int Outstanding { get; set; }
    public int EngineCalls { get; set; }
}

internal sealed class TestChildResult
{
    public int Pid { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    NumberHandling = JsonNumberHandling.Strict,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false,
    MaxDepth = WireProtocol.MaxJsonDepth)]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(EmptyPayload))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(HelloPayload))]
[JsonSerializable(typeof(HelloResult))]
[JsonSerializable(typeof(PingResult))]
[JsonSerializable(typeof(CancelPayload))]
[JsonSerializable(typeof(CancelResult))]
[JsonSerializable(typeof(HoverResolvePayload))]
[JsonSerializable(typeof(HoverResolveResult))]
[JsonSerializable(typeof(HoverPrimaryEvent))]
[JsonSerializable(typeof(HoverPhraseEvent))]
[JsonSerializable(typeof(StatusEvent))]
[JsonSerializable(typeof(FaultEvent))]
[JsonSerializable(typeof(TestSleepPayload))]
[JsonSerializable(typeof(TestTickEvent))]
[JsonSerializable(typeof(TestFakeCorePayload))]
[JsonSerializable(typeof(TestBytesPayload))]
[JsonSerializable(typeof(TestStatsResult))]
[JsonSerializable(typeof(TestChildResult))]
internal sealed partial class WireJson : JsonSerializerContext
{
    // Cyrillic travels as raw UTF-8. Control characters, \n and \r included,
    // are always escaped, and the byte 0x0A never occurs inside a multi-byte
    // UTF-8 sequence, so a frame always stays on one line.
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public static readonly WireJson Strict = new(new JsonSerializerOptions
    {
        Encoder = Encoder,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = WireProtocol.MaxJsonDepth,
    });
}
