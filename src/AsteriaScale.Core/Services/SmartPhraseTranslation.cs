namespace AsteriaScale.Services;

/// <summary>
/// The complete, deterministic input to the Smart Dictionary sentence
/// translator and its cache. <see cref="SourcePhrase"/> is the one current
/// sentence shown in the card. Earlier hook lines and semantic evidence are
/// separate post-edit inputs and must never be rendered as current dialogue.
/// </summary>
internal sealed record SmartPhraseTranslationRequest(
    string SourcePhrase,
    string ModelInputPhrase,
    string TransformIdentity,
    IReadOnlyList<string> ProperNames,
    string ModelIdentity,
    string PromptVersion)
{
    public IReadOnlyList<string> PreviousContext { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Metadata for the author of the current utterance. It is never part of
    /// the text to translate; a known gender is used only by the post-editor
    /// to inflect first-person and omitted-subject forms in Russian.
    /// </summary>
    public CurrentSpeakerContext? CurrentSpeaker { get; init; }

    public IReadOnlyList<ScaffoldTerm> SemanticTerms { get; init; } =
        Array.Empty<ScaffoldTerm>();

    public IReadOnlyList<ScaffoldSenseHint> SenseHints { get; init; } =
        Array.Empty<ScaffoldSenseHint>();
}

/// <summary>
/// Raw model output. Acceptance is deliberately separate: an implementation can
/// only generate text; <see cref="SmartPhraseTranslationGuard"/> decides whether
/// that text is safe to display as an independent phrase translation.
/// </summary>
internal enum SmartPhraseGenerationRoute
{
    Direct,
    ContextPostEdit,
    SenseConditioned,
    SenseRepair
}

internal sealed record SmartPhraseTranslationResult(
    string Translation,
    string ModelIdentity,
    long ElapsedMilliseconds,
    bool FromCache = false,
    bool Cacheable = true,
    SmartPhraseGenerationRoute GenerationRoute = SmartPhraseGenerationRoute.Direct)
{
    public IReadOnlyList<ScaffoldSenseHint> AppliedSenseHints { get; init; } =
        Array.Empty<ScaffoldSenseHint>();
}

/// <summary>
/// Outcome of the passes after candidate A, before timing and identity are
/// attached to the public result.
/// </summary>
internal sealed record SmartPhraseRefinement(
    string Translation,
    SmartPhraseGenerationRoute Route,
    IReadOnlyList<ScaffoldSenseHint> AppliedSenseHints,
    bool Cacheable);

/// <summary>
/// Candidate A generated before the full request exists. The official direct
/// prompt depends only on the model input, so it can run while the semantic
/// scaffold is still being built (card E18).
/// </summary>
internal sealed record SmartPhraseDirectDraft(
    string ModelInputPhrase,
    string ModelIdentity,
    string Translation,
    long ElapsedMilliseconds);

/// <summary>
/// Narrow DI seam for an optional local phrase model. Failure or rejection is
/// handled by the caller without changing the primary dictionary result.
/// </summary>
internal interface ISmartPhraseTranslator
{
    /// <summary>
    /// The identity every request to this translator must carry and every
    /// result will report. It comes from the instance, not a constant, so a
    /// translator running a measured variant (another quantization or Vulkan
    /// numeric path) gets requests the identity guard can still match.
    /// </summary>
    string ModelIdentityFor(TranslateGemmaExecutionBackend backend) =>
        TranslateGemmaTranslationService.GetModelIdentity(backend);

    Task PrepareAsync(
        SmartPhraseTranslationRequest request,
        CancellationToken cancellationToken) => Task.CompletedTask;

    Task<SmartPhraseTranslationResult> TranslateAsync(
        SmartPhraseTranslationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Generates candidate A from the model input alone. Null means the
    /// translator has no separable direct pass; the caller then falls back to
    /// the one-shot <see cref="TranslateAsync(SmartPhraseTranslationRequest, CancellationToken)"/>.
    /// </summary>
    Task<SmartPhraseDirectDraft?> GenerateDirectDraftAsync(
        string modelInputPhrase,
        string modelIdentity,
        CancellationToken cancellationToken) =>
        Task.FromResult<SmartPhraseDirectDraft?>(null);

    /// <summary>
    /// The one-shot contract, except that a draft generated for the same model
    /// input and identity replaces the candidate-A pass, and candidate A is
    /// reported through <paramref name="directPreview"/> only when a later pass
    /// may still replace it. The preview is a raw hypothesis: the caller owns
    /// the display decision exactly as it does for the final result.
    /// </summary>
    Task<SmartPhraseTranslationResult> TranslateAsync(
        SmartPhraseTranslationRequest request,
        SmartPhraseDirectDraft? directDraft,
        Action<SmartPhraseTranslationResult>? directPreview,
        CancellationToken cancellationToken) =>
        TranslateAsync(request, cancellationToken);
}
