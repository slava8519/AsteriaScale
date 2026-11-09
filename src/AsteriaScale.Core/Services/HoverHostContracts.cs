namespace AsteriaScale.Services;

/// <summary>
/// What a hover host (the WPF app on Windows, CoreHost on macOS) supplies to
/// <see cref="HoverCoordinator{TAnchor}"/>: the text source, the presentation,
/// the cursor tracker and the host-owned resources. The anchor is opaque to the
/// core and is handed back unchanged (a screen point on Windows, a request on
/// macOS). The coordinator constructor never calls the host, so a host may pass
/// itself before its own fields are set. No member may block: cards are posted,
/// never awaited.
/// </summary>
internal interface IHoverHost<TAnchor>
    where TAnchor : notnull
{
    /// <summary>
    /// Whether the host can edit a recognized speaker's gender. Read on the
    /// hover task whenever a card is built.
    /// </summary>
    bool SpeakerEditorSupported { get; }

    /// <summary>Prepares the text source on the warm-up task (Windows AI OCR).</summary>
    Task EnsureTextSourceReadyAsync();

    /// <summary>Acquires the hovered text; the first await of every hover.</summary>
    Task<HoverOcrResult?> RecognizeAsync(
        TAnchor anchor,
        bool sentenceContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Host-only refinement of the recognized line (the Textractor upgrade on
    /// Windows), on the hover task. It must not throw: returning the input
    /// keeps OCR in charge.
    /// </summary>
    HoverOcrResult UpgradeRecognized(TAnchor anchor, HoverOcrResult recognized);

    /// <summary>The game the hovered window belongs to, for per-game speaker genders.</summary>
    SpeakerGameIdentity? ResolveSpeakerGame(TAnchor anchor);

    /// <summary>
    /// Called once per hover on the hover task after the text is final and
    /// before any card, so the host can capture its own per-hover state.
    /// </summary>
    void OnRecognized(TAnchor anchor, HoverOcrResult recognized);

    /// <summary>Whether the hover is still where it started (the cursor within the radius).</summary>
    bool IsAnchorCurrent(TAnchor anchor);

    /// <summary>Posts the loading card from the hover task.</summary>
    void ShowLoading(HoverLoadingCard<TAnchor> card);

    /// <summary>
    /// Posts a card from the hover task, or from the phrase translator's
    /// callback for a provisional line. When the host actually renders it, the
    /// host calls <see cref="HoverCoordinator{TAnchor}.RecordShown"/> with the
    /// same card on its render thread: history holds only what the user saw.
    /// </summary>
    void ShowTranslation(HoverCard<TAnchor> card);

    /// <summary>
    /// Hides the card. Called from the hover task, the control thread or the
    /// host's own tracker; the coordinator has already retired the display
    /// generation, so a card posted earlier is no longer current.
    /// </summary>
    void HideCard();

    /// <summary>
    /// Closes host-only windows that pause hovering (the speaker editor), on
    /// the control thread when hover translation is disabled, the mode changes
    /// or the coordinator is disposed.
    /// </summary>
    void CloseAuxiliaryWindows();

    /// <summary>Starts cursor tracking, on the control thread.</summary>
    void StartTracking();

    /// <summary>Stops cursor tracking, on the control thread.</summary>
    void StopTracking();

    /// <summary>Forgets the tracked anchor and cancels its hover, on the control thread.</summary>
    void ResetTracking();

    /// <summary>
    /// First phase of disposal, after every hover task drained: the host
    /// releases its windows, hotkeys, tracker and text source. The translation
    /// history is still open here.
    /// </summary>
    ValueTask ReleaseHostResourcesAsync();

    /// <summary>
    /// Second phase of disposal, right after the translation history was
    /// disposed and before the core services are: the Windows host stops
    /// Textractor here, exactly where it did before the move.
    /// </summary>
    ValueTask ReleaseHostResourcesAfterHistoryAsync();
}

/// <summary>The loading card of one hover, posted before translation starts.</summary>
internal sealed record HoverLoadingCard<TAnchor>(
    long DisplayGeneration,
    TAnchor Anchor,
    string Source,
    string Engine,
    HoverTextSource TextSource,
    CurrentSpeakerContext? CurrentSpeaker,
    bool SpeakerEditorAvailable);

/// <summary>
/// One posted result card: the primary result, and on later posts of the same
/// hover its provisional or final phrase line.
/// </summary>
internal sealed record HoverCard<TAnchor>(
    long DisplayGeneration,
    TAnchor Anchor,
    string Source,
    string Translation,
    string Engine,
    bool DictionaryFound,
    bool IsPhrase,
    string? Language,
    double? Confidence,
    string Context,
    bool RequireAnchorCurrent,
    HoverTextSource TextSource,
    SmartHoverPhraseLine? PhraseLine,
    CurrentSpeakerContext? CurrentSpeaker,
    bool SpeakerEditorAvailable)
{
    /// <summary>
    /// What produced the primary result, so a host renders the card kind from
    /// a type instead of parsing <see cref="Engine"/>; the WPF card still reads
    /// the engine string (MACOS.md T2). The coordinator sets it on every card
    /// it posts. It is nullable only because a result built from a bare engine
    /// string, which only tests do, has none.
    /// </summary>
    public HoverProvenance? Provenance { get; init; }
}

/// <summary>The speaker of the visible card, as the speaker editor edits it.</summary>
internal sealed record HoverSpeakerTarget<TAnchor>(
    SpeakerGameIdentity Game,
    CurrentSpeakerContext Speaker,
    TAnchor Anchor);

/// <summary>
/// The optional phrase model as the hover lifecycle sees it. TranslateGemma
/// implements it; tests substitute a fake without starting llama-server.
/// A host without one passes null in <see cref="HoverCoreServices.PhraseModel"/>.
/// </summary>
internal interface IHoverPhraseModel : ISmartPhraseTranslator, IAsyncDisposable
{
    Task WarmUpAsync(CancellationToken cancellationToken);

    Task UnloadAsync();
}

/// <summary>Hover rules a host needs without a coordinator instance.</summary>
internal static class HoverPolicy
{
    // A provisional line is followed by the final one on the same card, so
    // recording both would duplicate history with a hypothesis the sense gate
    // may have rejected.
    internal static bool IsPhraseHistoryEntry(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] SmartHoverPhraseLine? phraseLine) =>
        phraseLine is
        {
            Status: SmartPhraseLineStatus.Translated,
            Translation.Length: > 0,
            IsProvisional: false
        };
}
