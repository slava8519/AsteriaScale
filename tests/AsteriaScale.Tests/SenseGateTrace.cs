using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AsteriaScale.Services;

/// <summary>
/// Benchmark-only observer for the sense-preservation gate. It records the
/// exact request the production pipeline built, the candidate-A draft the
/// pipeline generated alongside the scaffold (card E18), and when each ran, so
/// the suite can score A with the production gate. Only when no draft exists
/// does the suite regenerate A with the byte-identical official direct prompt.
/// The wrapper never alters what it forwards.
/// </summary>
internal sealed class SenseGateTraceTranslator : ISmartPhraseTranslator
{
    private readonly ISmartPhraseTranslator _inner;
    private SmartPhraseTranslationRequest? _request;
    private SmartPhraseTranslationResult? _result;
    private SmartPhraseDirectDraft? _draft;
    private long _callStarted;
    private long _callFinished;
    private long _draftStarted;
    private long _draftFinished;
    private int _calls;

    internal SenseGateTraceTranslator(ISmartPhraseTranslator inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    internal SmartPhraseTranslationRequest? Request => _request;

    internal SmartPhraseTranslationResult? Result => _result;

    internal long CallStartedTimestamp => _callStarted;

    internal long CallFinishedTimestamp => _callFinished;

    internal int Calls => _calls;

    internal SmartPhraseDirectDraft? Draft => _draft;

    internal long DraftStartedTimestamp => _draftStarted;

    internal long DraftFinishedTimestamp => _draftFinished;

    internal void Reset()
    {
        _request = null;
        _result = null;
        _draft = null;
        _callStarted = 0;
        _callFinished = 0;
        _draftStarted = 0;
        _draftFinished = 0;
        _calls = 0;
    }

    public Task PrepareAsync(
        SmartPhraseTranslationRequest request,
        CancellationToken cancellationToken) =>
        _inner.PrepareAsync(request, cancellationToken);

    public Task<SmartPhraseTranslationResult> TranslateAsync(
        SmartPhraseTranslationRequest request,
        CancellationToken cancellationToken) =>
        TranslateAsync(request, directDraft: null, directPreview: null, cancellationToken);

    // Without these forwards the interface defaults would silently route the
    // benchmark around the draft path that production uses.
    public async Task<SmartPhraseDirectDraft?> GenerateDirectDraftAsync(
        string modelInputPhrase,
        string modelIdentity,
        CancellationToken cancellationToken)
    {
        _draftStarted = Stopwatch.GetTimestamp();
        try
        {
            _draft = await _inner.GenerateDirectDraftAsync(
                modelInputPhrase,
                modelIdentity,
                cancellationToken);
            return _draft;
        }
        finally
        {
            _draftFinished = Stopwatch.GetTimestamp();
        }
    }

    public async Task<SmartPhraseTranslationResult> TranslateAsync(
        SmartPhraseTranslationRequest request,
        SmartPhraseDirectDraft? directDraft,
        Action<SmartPhraseTranslationResult>? directPreview,
        CancellationToken cancellationToken)
    {
        _calls++;
        _request = request;
        _result = null;
        _callStarted = Stopwatch.GetTimestamp();
        try
        {
            _result = await _inner.TranslateAsync(
                request,
                directDraft,
                directPreview,
                cancellationToken);
            return _result;
        }
        finally
        {
            _callFinished = Stopwatch.GetTimestamp();
        }
    }
}

internal sealed record SenseGateTraceSource(
    string Source,
    bool IsPrimary,
    NeuralSenseFidelityState State);

internal sealed record SenseGateTraceCandidate(
    SensePreservationCandidateKind Kind,
    string Translation,
    bool StrictAccepted,
    bool DisplayAccepted,
    int MissingMandatoryTermCount,
    int MissingAdvisoryTermCount,
    int StructuralPenaltyCount,
    IReadOnlyList<string> StructuralViolationCodes,
    bool RequiresTargetedRepair,
    IReadOnlyList<SenseGateTraceSource> Sources)
{
    internal static SenseGateTraceCandidate From(SensePreservationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new SenseGateTraceCandidate(
            candidate.Kind,
            candidate.Translation,
            candidate.StrictAccepted,
            candidate.DisplayAccepted,
            candidate.MissingMandatoryTermCount,
            candidate.MissingAdvisoryTermCount,
            candidate.StructuralPenaltyCount,
            candidate.StructuralViolationCodes,
            SensePreservationGate.RequiresTargetedRepair(candidate),
            candidate.SenseAssessment.Sources
                .Select(source => new SenseGateTraceSource(
                    source.Source,
                    source.Representative.IsPrimary,
                    source.State))
                .ToArray());
    }
}

/// <summary>
/// One line per benchmark case. Schema 1 (E16) split a sequential hover into
/// card, scaffold and gate. Schema 2 (E18) records the progressive hover as it
/// happened: <see cref="ScaffoldMilliseconds"/> now ends when the refinement
/// call starts, so it includes any wait for the overlapped draft, and
/// <see cref="GateMilliseconds"/> excludes a reused draft. First and final
/// line times are measured, not modelled. A replayed A (only when no draft
/// existed) runs after the production call and never leaks into any timing.
/// </summary>
internal sealed record SenseGateTraceEntry(
    int SchemaVersion,
    string Id,
    long CardMilliseconds,
    long? ScaffoldMilliseconds,
    long? GateMilliseconds,
    long? DirectReplayMilliseconds,
    int TranslatorCalls,
    int SenseHintCount,
    SmartPhraseGenerationRoute? FinalRoute,
    string? FinalTranslation,
    SenseGateTraceCandidate? Direct,
    string? DirectReplayError)
{
    internal const int CurrentSchemaVersion = 2;

    public long? DraftMilliseconds { get; init; }

    public long? FirstLineMilliseconds { get; init; }

    public long? FinalLineMilliseconds { get; init; }

    public string? ProvisionalTranslation { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal string ToJsonLine() => JsonSerializer.Serialize(this, SerializerOptions);
}

internal sealed class SenseGateTraceWriter : IAsyncDisposable
{
    private readonly StreamWriter _writer;

    internal SenseGateTraceWriter(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(
            path,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal async Task WriteAsync(SenseGateTraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _writer.WriteAsync(entry.ToJsonLine());
        await _writer.WriteAsync('\n');
        // A multi-hour GPU run must leave a usable prefix if it is interrupted.
        await _writer.FlushAsync();
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
