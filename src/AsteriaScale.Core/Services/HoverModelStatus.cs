namespace AsteriaScale.Services;

/// <summary>
/// What the model owners of one <see cref="HoverCoordinator{TAnchor}"/> are
/// doing when it is read, for a host's status line (CoreHost's models.status).
/// Each field is read once, without waiting for a hover, a warm-up or an
/// unload, and nothing is loaded or started to fill it; so a host may read it
/// at any time from any thread, and a snapshot taken while a model loads or
/// unloads may show either side of that change.
/// </summary>
internal sealed record HoverModelStatus(
    HoverDictionaryStatus Dictionary,
    HoverSenseStatus Sense,
    HoverPhraseStatus Phrase);

/// <summary>The layered dictionaries.</summary>
/// <param name="Loaded">A load finished and no unload has run since.</param>
/// <param name="Entries">Entries in memory; 0 while unloaded.</param>
/// <param name="Sources">Dictionary files the last load read.</param>
/// <param name="Version">The version token the caches key on; "unloaded" while unloaded.</param>
/// <param name="SenseMetadataKeys">English keys with aligned sense metadata, which only Smart mode loads.</param>
internal sealed record HoverDictionaryStatus(
    bool Loaded,
    int Entries,
    int Sources,
    string Version,
    int SenseMetadataKeys);

/// <summary>The sense ranker and its isolated worker.</summary>
/// <param name="InUse">
/// Hover translation is on and its mode ranks senses: Smart with a source
/// language other than Japanese.
/// </param>
/// <param name="Provider">The provider the settings select; Auto resolves to CPU.</param>
/// <param name="Model">The CPU model profile the selector starts its worker with.</param>
/// <param name="KeepWarm">The settings keep the worker loaded between hovers.</param>
/// <param name="Running">A worker process is running.</param>
/// <param name="ProcessId">The worker's process id while it runs.</param>
/// <param name="ActiveProvider">The provider the running worker was started for.</param>
/// <param name="ActiveModelVersion">
/// The model identity the running worker reported (for ConSeC, the exact
/// bundle identity); null until it answered.
/// </param>
/// <param name="LastErrorCode">
/// The code of the selector's last failed worker transaction (a
/// <see cref="GlossBertRankerException.ErrorCode"/> such as worker_missing or
/// consec_bundle_missing), or null once a later transaction succeeded.
/// </param>
internal sealed record HoverSenseStatus(
    bool InUse,
    GlossBertRankerProvider Provider,
    RankerCpuModelProfile Model,
    bool KeepWarm,
    bool Running,
    int? ProcessId,
    GlossBertRankerProvider? ActiveProvider,
    string? ActiveModelVersion,
    string? LastErrorCode);

/// <summary>The optional phrase model (TranslateGemma).</summary>
/// <param name="Available">The host supplied a phrase model.</param>
/// <param name="Requested">The settings ask for a phrase line under the card.</param>
/// <param name="WarmedUp">
/// The result of the last keep-warm warm-up since the smart settings last
/// changed; null when none ran.
/// </param>
/// <param name="UnavailableStatus">
/// <see cref="UnavailableOnPlatform"/> when the host supplied no phrase model;
/// null while one exists.
/// </param>
internal sealed record HoverPhraseStatus(
    bool Available,
    bool Requested,
    bool? WarmedUp,
    string? UnavailableStatus)
{
    /// <summary>
    /// The one status a host without a phrase model (macOS until 20.5) shows,
    /// here and in the Smart ready status, instead of promising a TranslateGemma
    /// that will never load.
    /// </summary>
    internal const string UnavailableOnPlatform = "TranslateGemma недоступна на этой платформе";
}
