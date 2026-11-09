using AsteriaScale.Models;

namespace AsteriaScale.Services;

/// <summary>
/// The model owners one <see cref="HoverCoordinator{TAnchor}"/> composes and
/// disposes. <see cref="CreateDefault"/> is exactly what the Windows
/// HoverTranslationService constructed before the move; a test (and later the
/// macOS host) passes its own paths and fakes through the same shape.
/// </summary>
internal sealed class HoverCoreServices
{
    public required SmartDictionaryResultCache ResultCache { get; init; }

    public required SmartPhraseTranslationCache PhraseCache { get; init; }

    // Shared by the hover pipeline and the full-text scaffold: both ask the
    // worker the same question, so neither may pay for the other's inference.
    public required GlossBertDecisionCache DecisionCache { get; init; }

    public required TranslationHistoryService History { get; init; }

    /// <summary>
    /// The optional phrase model. A host without one (macOS until 20.5) passes
    /// null explicitly: the coordinator then never warms, unloads or waits for
    /// a phrase model and reports <see cref="HoverPhraseStatus.UnavailableOnPlatform"/>.
    /// </summary>
    public required IHoverPhraseModel? PhraseModel { get; init; }

    public required GlossBertDictionarySelectorService Selector { get; init; }

    public required LocalDictionaryService Dictionary { get; init; }

    /// <summary>
    /// The default owners, created in the order the Windows host created them:
    /// its field initializers (the three caches and the history), then its
    /// constructor body (TranslateGemma, the selector, the dictionary).
    /// </summary>
    public static HoverCoreServices CreateDefault(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new HoverCoreServices
        {
            ResultCache = new SmartDictionaryResultCache(),
            PhraseCache = new SmartPhraseTranslationCache(),
            DecisionCache = new GlossBertDecisionCache(),
            History = new TranslationHistoryService(),
            PhraseModel = new TranslateGemmaTranslationService(
                backendResolver: () => TranslateGemmaTranslationService.ResolveBackend(
                    settings.SmartPhraseTranslationBackend)),
            Selector = new GlossBertDictionarySelectorService(
                SemanticRankerProfileResolver.FromSettings(settings)),
            Dictionary = new LocalDictionaryService()
        };
    }
}
