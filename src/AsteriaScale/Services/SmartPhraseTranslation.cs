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
internal sealed record SmartPhraseTranslationResult(
    string Translation,
    string ModelIdentity,
    long ElapsedMilliseconds,
    bool FromCache = false);

/// <summary>
/// Narrow DI seam for an optional local phrase model. Failure or rejection is
/// handled by the caller without changing the primary dictionary result.
/// </summary>
internal interface ISmartPhraseTranslator
{
    Task PrepareAsync(
        SmartPhraseTranslationRequest request,
        CancellationToken cancellationToken) => Task.CompletedTask;

    Task<SmartPhraseTranslationResult> TranslateAsync(
        SmartPhraseTranslationRequest request,
        CancellationToken cancellationToken);
}
