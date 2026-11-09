namespace AsteriaScale.Services;

/// <summary>
/// What produced the primary hover card. The seven values map one to one onto
/// the engine labels the WPF card tells apart (MACOS.md T3); whether the
/// dictionary had an entry at all stays in the result's DictionaryFound.
/// </summary>
internal enum HoverResultSource
{
    /// <summary>The plain dictionary entry, found or not.</summary>
    Dictionary,

    /// <summary>The model-free Smart rules or a single aligned sense; no ranker ran.</summary>
    Heuristic,

    /// <summary>The aligned Tatoeba sentence translation.</summary>
    Tatoeba,

    /// <summary>A Smart result-cache or shared decision-cache hit.</summary>
    ResultCache,

    /// <summary>The sense ranker accepted one sense.</summary>
    Ranker,

    /// <summary>The ranker's margin was below the abstention threshold.</summary>
    RankerAbstained,

    /// <summary>The ranker failed; the local dictionary variants are shown.</summary>
    RankerUnavailable
}

/// <summary>
/// Typed provenance of a primary hover result (owner decision M12). A host
/// renders from this value instead of parsing the engine string; that string
/// is derived from it through the host's <see cref="HoverEngineLabels"/> and
/// feeds history, benchmark reports and logs.
/// </summary>
internal readonly record struct HoverProvenance
{
    private HoverProvenance(HoverResultSource source, GlossBertRankerProvider? senseProvider)
    {
        Source = source;
        SenseProvider = senseProvider;
    }

    public HoverResultSource Source { get; }

    /// <summary>Set exactly for the three ranker sources.</summary>
    public GlossBertRankerProvider? SenseProvider { get; }

    public static HoverProvenance Dictionary => new(HoverResultSource.Dictionary, null);

    public static HoverProvenance Heuristic => new(HoverResultSource.Heuristic, null);

    public static HoverProvenance Tatoeba => new(HoverResultSource.Tatoeba, null);

    public static HoverProvenance ResultCache => new(HoverResultSource.ResultCache, null);

    public static HoverProvenance Ranker(GlossBertRankerProvider provider) =>
        new(HoverResultSource.Ranker, Validate(provider));

    public static HoverProvenance RankerAbstained(GlossBertRankerProvider provider) =>
        new(HoverResultSource.RankerAbstained, Validate(provider));

    public static HoverProvenance RankerUnavailable(GlossBertRankerProvider provider) =>
        new(HoverResultSource.RankerUnavailable, Validate(provider));

    /// <summary>
    /// Every value a result can carry. Reading a stored label back walks this
    /// list, so a new sense provider is covered without another edit here.
    /// </summary>
    public static IReadOnlyList<HoverProvenance> All { get; } =
    [
        Dictionary,
        Heuristic,
        Tatoeba,
        ResultCache,
        .. Enum.GetValues<GlossBertRankerProvider>().SelectMany(provider => new[]
        {
            Ranker(provider),
            RankerAbstained(provider),
            RankerUnavailable(provider)
        })
    ];

    private static GlossBertRankerProvider Validate(GlossBertRankerProvider provider) =>
        Enum.IsDefined(provider)
            ? provider
            : throw new ArgumentOutOfRangeException(nameof(provider));
}

/// <summary>
/// A provenance together with the host's label for it. Results take both from
/// one value, so their engine string and provenance cannot disagree.
/// </summary>
internal readonly record struct LabelledProvenance(HoverProvenance Provenance, string Engine);

/// <summary>
/// The engine strings one host stores with each result, and the name its
/// status texts give the text source (owner decision M12, MACOS.md T4). The
/// Windows set returns the label constants the WPF host has always shown and
/// written to history and benchmark reports, so those stay byte for byte.
/// Another host names its own text source, so none of its labels or statuses
/// says "NPU OCR" or "VULKAN".
/// </summary>
internal sealed class HoverEngineLabels
{
    private const string Separator = "  •  ";

    /// <summary>Windows AI OCR on the NPU: the historical labels.</summary>
    public static readonly HoverEngineLabels WindowsAiNpuOcr = new(
        textSourceName: "NPU OCR",
        labelPrefix: null);

    /// <summary>The macOS text source (Apple Vision recognizes text from 20.3 on).</summary>
    public static readonly HoverEngineLabels AppleVision = new(
        textSourceName: "Apple Vision",
        labelPrefix: "APPLE VISION");

    /// <summary>Every host's set, for reading a stored label back.</summary>
    public static IReadOnlyList<HoverEngineLabels> All { get; } = [WindowsAiNpuOcr, AppleVision];

    // Null selects the Windows constants themselves rather than composed equal
    // strings, so the Windows labels are the very expressions the pipeline and
    // the coordinator used before the labels became host-supplied.
    private readonly string? _labelPrefix;

    private HoverEngineLabels(string textSourceName, string? labelPrefix)
    {
        TextSourceName = textSourceName;
        _labelPrefix = labelPrefix;
    }

    /// <summary>The text source as the warm-up statuses name it.</summary>
    public string TextSourceName { get; }

    public LabelledProvenance Labelled(HoverProvenance provenance) =>
        new(provenance, For(provenance));

    public string For(HoverProvenance provenance) => provenance.Source switch
    {
        HoverResultSource.Dictionary => Compose(LocalDictionaryService.EngineLabel, "СЛОВАРЬ"),
        HoverResultSource.Heuristic => Compose(SmartDictionaryHeuristicRanker.EngineLabel, "SMART-FAST"),
        HoverResultSource.Tatoeba => Compose(
            SmartDictionaryHeuristicRanker.TatoebaEngineLabel,
            "SMART-FAST" + Separator + "TATOEBA"),
        HoverResultSource.ResultCache => Compose(SmartDictionaryResultCache.EngineLabel, "SMART-CACHE"),
        HoverResultSource.Ranker => Sense(provenance),
        // The two status suffixes the pipeline appended before, as written there.
        HoverResultSource.RankerAbstained => Sense(provenance) + "  •  НЕ УВЕРЕН",
        HoverResultSource.RankerUnavailable => Sense(provenance) + "  •  НЕДОСТУПЕН",
        _ => throw new ArgumentOutOfRangeException(nameof(provenance))
    };

    // The phrase line names the model and its backend, never the text source.
    // A Mac has no Vulkan runtime (Metal arrives with 20.5), so outside the
    // Windows set a GPU line is named generically rather than by a Windows API.
    public string Phrase(TranslateGemmaExecutionBackend backend) =>
        _labelPrefix is null
            ? TranslateGemmaTranslationService.GetEngineLabel(backend)
            : backend switch
            {
                TranslateGemmaExecutionBackend.Cpu =>
                    TranslateGemmaTranslationService.EngineLabel + Separator + "CPU",
                TranslateGemmaExecutionBackend.Vulkan =>
                    TranslateGemmaTranslationService.EngineLabel + Separator + "GPU",
                _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
            };

    private string Sense(HoverProvenance provenance)
    {
        GlossBertRankerProvider provider = provenance.SenseProvider ??
            throw new ArgumentOutOfRangeException(nameof(provenance));
        return Compose(
            GlossBertDictionarySelectorService.GetEngineLabel(provider),
            provider switch
            {
                GlossBertRankerProvider.Cpu => "CPU-СМЫСЛ",
                GlossBertRankerProvider.Npu => "NPU-СМЫСЛ",
                _ => throw new ArgumentOutOfRangeException(nameof(provenance))
            });
    }

    private string Compose(string windowsLabel, string kind) =>
        _labelPrefix is null ? windowsLabel : _labelPrefix + Separator + kind;
}
