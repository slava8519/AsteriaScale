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

    // Request-level terminals of the Core-backed commands.
    public const string HoverDisabled = "hover_disabled";
    public const string UnsupportedOnPlatform = "unsupported_on_platform";
    public const string InvalidSettings = "invalid_settings";
    public const string Conflict = "conflict";
    public const string SettingsSaveFailed = "settings_save_failed";
    public const string MorphologyMissing = "morphology_missing";
    public const string DictionaryMissing = "dictionary_missing";
    public const string InvalidEntry = "invalid_entry";
    public const string IoError = "io_error";

    // Request-level terminal of an observations hover with zero observations
    // whose frame CoreHost no longer holds; the app retries once with full
    // observations and shows nothing.
    public const string StaleFrame = "stale_frame";

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

// ---- Apple Vision observations (20.3) -------------------------------------
//
// Units (docs/MACOS.md T18): display, cursor and captureBounds are whole
// pixels of the display under the cursor, origin at its top-left corner, y
// down; word boxes are pixels of the captured frame, which is captureBounds
// 1:1, origin at the frame's top-left corner, y down. A fingerprint is the
// 64-bit value of OcrTextRules.ComputeFrameFingerprint (the first 8 bytes of
// the SHA-256 of the frame's pixels read little-endian) written as exactly 16
// lowercase hex digits of that ulong (ToString("x16")): a JSON number cannot
// hold a ulong. tests/AsteriaScale.Tests.MacOS/CoreHost/frame-fingerprint-v1.txt
// pins it for given bytes.

/// <summary>hover.resolve with source.kind observations; mode is required.</summary>
internal sealed class HoverObservationsPayload
{
    public string? Mode { get; set; }
    [JsonRequired] public ObservationsSource Source { get; set; } = new();
    public GameIdentity? Game { get; set; }
}

internal sealed class ObservationsSource
{
    [JsonRequired] public string Kind { get; set; } = "";
    [JsonRequired] public DisplayInfo Display { get; set; } = new();
    [JsonRequired] public PixelRectangle CaptureBounds { get; set; } = new();
    [JsonRequired] public PixelPoint Cursor { get; set; } = new();
    [JsonRequired] public ObservedFrame[] Observations { get; set; } = [];
    public string? CachedFingerprint { get; set; }
}

internal sealed class DisplayInfo
{
    // CGDirectDisplayID, 0..4294967295.
    [JsonRequired] public long Id { get; set; }
    [JsonRequired] public int Width { get; set; }
    [JsonRequired] public int Height { get; set; }
}

internal sealed class PixelRectangle
{
    [JsonRequired] public int X { get; set; }
    [JsonRequired] public int Y { get; set; }
    [JsonRequired] public int Width { get; set; }
    [JsonRequired] public int Height { get; set; }
}

internal sealed class PixelPoint
{
    [JsonRequired] public int X { get; set; }
    [JsonRequired] public int Y { get; set; }
}

/// <summary>One recognized frame: what Vision read, and the frame's fingerprint.</summary>
internal sealed class ObservedFrame
{
    [JsonRequired] public string Fingerprint { get; set; } = "";
    [JsonRequired] public ObservedLine[] Lines { get; set; } = [];
}

internal sealed class ObservedLine
{
    [JsonRequired] public string Text { get; set; } = "";
    [JsonRequired] public double Confidence { get; set; }
    [JsonRequired] public ObservedWord[] Words { get; set; } = [];
}

internal sealed class ObservedWord
{
    [JsonRequired] public string Text { get; set; } = "";
    [JsonRequired] public double Left { get; set; }
    [JsonRequired] public double Top { get; set; }
    [JsonRequired] public double Right { get; set; }
    [JsonRequired] public double Bottom { get; set; }
}

/// <summary>hover.loading: what the Windows loading card shows, before hover.primary.</summary>
internal sealed class HoverLoadingEvent
{
    // The hovered word in smart mode, the recognized line in dictionary mode.
    public string Source { get; set; } = "";
    public string EngineLabel { get; set; } = "";
    public SpeakerView? Speaker { get; set; }
}

internal sealed class OcrPlanPayload
{
    [JsonRequired] public DisplayInfo Display { get; set; } = new();
    [JsonRequired] public PixelPoint Cursor { get; set; } = new();
}

internal sealed class OcrPlanResult
{
    public string Mode { get; set; } = "";
    public PixelRectangle CaptureBounds { get; set; } = new();
    public int Observations { get; set; }
    public string? CachedFingerprint { get; set; }
}

internal sealed class OcrSelectPayload
{
    public string? Mode { get; set; }
    [JsonRequired] public ObservationsSource Source { get; set; } = new();
}

internal sealed class OcrSelectResult
{
    // Written as null when nothing near the cursor was selected.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public OcrSelection? Selection { get; set; }

    // The decoded lines of observation 1, in order.
    public string[] Lines { get; set; } = [];
}

internal sealed class OcrSelection
{
    public string Text { get; set; } = "";
    public string HoveredWord { get; set; } = "";
    public int CharacterIndex { get; set; }
    public bool SentenceStable { get; set; }
    public string? SpeakerName { get; set; }
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

    // One of dictionary, heuristic, tatoeba, resultCache, ranker,
    // rankerAbstained, rankerUnavailable (Core's HoverResultSource). The app
    // renders the card kind from it and never parses engineLabel.
    public string Provenance { get; set; } = "";

    // cpu or npu, set exactly for the three ranker provenances.
    public string? SenseProvider { get; set; }

    // Russian, from the host's label set; history stores it as the engine.
    public string EngineLabel { get; set; } = "";

    // The sentence the hover was resolved in (what history records as context).
    public string? Context { get; set; }

    public SpeakerView? Speaker { get; set; }
}

internal sealed class SpeakerView
{
    public string Name { get; set; } = "";

    // unknown, masculine, feminine or ignore.
    public string Gender { get; set; } = "";
    public string? GameKey { get; set; }

    // The spelling the text source saw, which name shows corrected once the
    // user corrected it; speaker.set's observedName takes it back as it is.
    public string? ObservedName { get; set; }
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

    // Null for area "translation": Core's own free text, which the app shows
    // only in its diagnostic window and log, never in the menu or the overlay.
    public string? State { get; set; }
    public string? Code { get; set; }
    public string Text { get; set; } = "";
}

// ---- Core-backed commands ------------------------------------------------

internal sealed class InitResult
{
    public SettingsView Settings { get; set; } = new();
    public long Revision { get; set; }
    public InitDictionary Dictionary { get; set; } = new();
    public int MorphologyEntries { get; set; }
    public double LoadMs { get; set; }
    public double WarmupLookupMs { get; set; }

    // Set when this init found no settings.json and wrote the macOS first-run
    // settings (docs/MACOS.md T7).
    public FirstRunSettings? FirstRun { get; set; }
}

internal sealed class InitDictionary
{
    public int Entries { get; set; }
    public int Sources { get; set; }
    public string Version { get; set; } = "";
}

internal sealed class FirstRunSettings
{
    public string SenseModel { get; set; } = "";
    public string SenseBackend { get; set; } = "";
    public bool HoverEnabled { get; set; }
}

/// <summary>
/// The translation subset of settings.json that the app may read and change.
/// The values are Core's normalized spellings; a settings.json written by
/// Windows may hold npu or translategemma-4b, which are shown as they are.
/// </summary>
internal sealed class SettingsView
{
    public bool HoverEnabled { get; set; }
    public int HoverDelayMs { get; set; }
    public string Mode { get; set; } = "";
    public SenseSettingsView Sense { get; set; } = new();
    public PhraseSettingsView Phrase { get; set; } = new();
}

internal sealed class SenseSettingsView
{
    public string Backend { get; set; } = "";
    public string Model { get; set; } = "";
    public string SourceLanguage { get; set; } = "";
    public bool KeepWarm { get; set; }
}

internal sealed class PhraseSettingsView
{
    public string Mode { get; set; } = "";
    public string Backend { get; set; } = "";
}

internal sealed class SettingsResult
{
    public SettingsView Settings { get; set; } = new();
    public long Revision { get; set; }
}

internal sealed class SettingsUpdatePayload
{
    [JsonRequired] public long BaseRevision { get; set; }
    [JsonRequired] public SettingsPatch Patch { get; set; } = new();
}

/// <summary>Every member optional; an absent member keeps its value.</summary>
internal sealed class SettingsPatch
{
    public bool? HoverEnabled { get; set; }
    public int? HoverDelayMs { get; set; }
    public string? Mode { get; set; }
    public SensePatch? Sense { get; set; }
    public PhrasePatch? Phrase { get; set; }
}

internal sealed class SensePatch
{
    public string? Backend { get; set; }
    public string? Model { get; set; }
    public string? SourceLanguage { get; set; }
    public bool? KeepWarm { get; set; }
}

internal sealed class PhrasePatch
{
    public string? Mode { get; set; }
}

internal sealed class ModelsWarmUpPayload
{
    [JsonRequired] public string[] Targets { get; set; } = [];
}

internal sealed class ModelsStatusResult
{
    public DictionaryModelStatus Dictionary { get; set; } = new();
    public SenseModelStatus Sense { get; set; } = new();
    public PhraseModelStatus Phrase { get; set; } = new();
}

internal sealed class DictionaryModelStatus
{
    public bool Loaded { get; set; }
    public int Entries { get; set; }
    public int Sources { get; set; }
    public string Version { get; set; } = "";
    public int MorphologyEntries { get; set; }
    public int SenseMetadataKeys { get; set; }
}

internal sealed class SenseModelStatus
{
    public bool InUse { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public bool KeepWarm { get; set; }
    public bool Running { get; set; }
    public int? Pid { get; set; }
    public string? ActiveProvider { get; set; }

    // The identity the running worker reported (for ConSeC the exact bundle).
    public string? ModelVersion { get; set; }
    public SenseError? Error { get; set; }
}

/// <summary>
/// The last failed worker transaction: the worker's or the selector's code,
/// a Russian sentence naming that code for the status line (no paths), and
/// the original text, which may hold paths, for the log.
/// </summary>
internal sealed class SenseError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Detail { get; set; }
}

internal sealed class PhraseModelStatus
{
    public bool Available { get; set; }
    public bool Requested { get; set; }
    public bool? WarmedUp { get; set; }
    public string? Status { get; set; }
}

internal sealed class HoverDisplayedPayload
{
    [JsonRequired] public long HoverId { get; set; }
    [JsonRequired] public string Part { get; set; } = "";
}

internal sealed class HoverDisplayedResult
{
    public bool Recorded { get; set; }
}

internal sealed class UserDictionaryItem
{
    public string Key { get; set; } = "";
    public string Translation { get; set; } = "";
    public string Language { get; set; } = "";
}

internal sealed class UserDictionaryListResult
{
    public UserDictionaryItem[] Entries { get; set; } = [];
}

internal sealed class UserDictionarySavePayload
{
    [JsonRequired] public string Key { get; set; } = "";
    [JsonRequired] public string Translation { get; set; } = "";
}

internal sealed class UserDictionarySaveResult
{
    public string Key { get; set; } = "";
}

internal sealed class UserDictionaryRemovePayload
{
    [JsonRequired] public string Key { get; set; } = "";
}

internal sealed class UserDictionaryRemoveResult
{
    public bool Removed { get; set; }
}

internal sealed class HistoryItem
{
    public string Source { get; set; } = "";
    public string Translation { get; set; } = "";
    public string Engine { get; set; } = "";
    public string Context { get; set; } = "";
    public string Language { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

internal sealed class HistoryListResult
{
    public HistoryItem[] Entries { get; set; } = [];
}

internal sealed class HistoryExportResult
{
    // The Anki file's text (front, back, context per line); the app writes it
    // where the user chose. CoreHost never writes outside its own roots.
    public string Tsv { get; set; } = "";
    public int Count { get; set; }
}

internal sealed class HistoryClearResult
{
    public bool Cleared { get; set; }
}

internal sealed class SpeakerSetPayload
{
    [JsonRequired] public GameIdentity Game { get; set; } = new();
    [JsonRequired] public string ObservedName { get; set; } = "";
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string Gender { get; set; } = "";
}

internal sealed class SpeakerSetResult
{
    public SpeakerView Speaker { get; set; } = new();
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

internal sealed class TestDelayNextWorkPayload
{
    [JsonRequired] public int Ms { get; set; }
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
[JsonSerializable(typeof(HoverObservationsPayload))]
[JsonSerializable(typeof(HoverLoadingEvent))]
[JsonSerializable(typeof(OcrPlanPayload))]
[JsonSerializable(typeof(OcrPlanResult))]
[JsonSerializable(typeof(OcrSelectPayload))]
[JsonSerializable(typeof(OcrSelectResult))]
[JsonSerializable(typeof(StatusEvent))]
[JsonSerializable(typeof(FaultEvent))]
[JsonSerializable(typeof(InitResult))]
[JsonSerializable(typeof(SettingsResult))]
[JsonSerializable(typeof(SettingsUpdatePayload))]
[JsonSerializable(typeof(ModelsWarmUpPayload))]
[JsonSerializable(typeof(ModelsStatusResult))]
[JsonSerializable(typeof(HoverDisplayedPayload))]
[JsonSerializable(typeof(HoverDisplayedResult))]
[JsonSerializable(typeof(UserDictionaryListResult))]
[JsonSerializable(typeof(UserDictionarySavePayload))]
[JsonSerializable(typeof(UserDictionarySaveResult))]
[JsonSerializable(typeof(UserDictionaryRemovePayload))]
[JsonSerializable(typeof(UserDictionaryRemoveResult))]
[JsonSerializable(typeof(HistoryListResult))]
[JsonSerializable(typeof(HistoryExportResult))]
[JsonSerializable(typeof(HistoryClearResult))]
[JsonSerializable(typeof(SpeakerSetPayload))]
[JsonSerializable(typeof(SpeakerSetResult))]
[JsonSerializable(typeof(TestSleepPayload))]
[JsonSerializable(typeof(TestTickEvent))]
[JsonSerializable(typeof(TestFakeCorePayload))]
[JsonSerializable(typeof(TestDelayNextWorkPayload))]
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
