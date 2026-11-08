using AsteriaScale.Models;
using AsteriaScale.RankerProtocol;

namespace AsteriaScale.Services;

internal enum SmartPhraseLineStatus
{
    Translated,
    Unavailable
}

internal sealed record SmartHoverPhraseLine(
    string Source,
    string Translation,
    string Engine,
    SmartPhraseLineStatus Status,
    bool FromCache)
{
    // A guard rejection and a generation failure both surface as Unavailable,
    // but only the first is a policy decision about a translation that exists.
    // Without carrying the reason, coverage losses cannot be attributed to a
    // policy at all: the diagnostic string reaches the log and dies there.
    // Null therefore means "no guard verdict", not "no reason".
    public SmartPhraseRejectionReason? RejectionReason { get; init; }

    // Benchmark diagnostics distinguish evidence supplied to a request from
    // evidence that actually reached the selected generated hypothesis.
    public IReadOnlyList<ScaffoldSenseHint> SenseHints { get; init; } =
        Array.Empty<ScaffoldSenseHint>();

    public IReadOnlyList<ScaffoldSenseHint> AppliedSenseHints { get; init; } =
        Array.Empty<ScaffoldSenseHint>();
}

internal sealed record SmartHoverResult(
        string Source,
        string Translation,
        string Engine,
        bool DictionaryFound,
        bool IsPhrase,
        string? Language,
        double? Confidence,
        SmartDictionaryCacheRequest? CacheRequest = null,
        SmartDictionarySelection? CacheSelection = null)
{
    public IReadOnlyList<ScaffoldSenseHint> SelectedSenseHints { get; init; } =
        Array.Empty<ScaffoldSenseHint>();
}

/// <summary>
/// The Smart-dictionary hover pipeline: dictionary lookup with sense metadata,
/// Tatoeba alignment, deterministic heuristics, result cache and the GlossBERT
/// ranker transaction. Extracted verbatim from HoverTranslationService
/// (roadmap task #5); the facade owns service lifetimes and overlays.
/// </summary>
internal sealed class SmartHoverPipeline : IDisposable
{
    // Same epoch as the shared decision cache: both caches must be invalidated
    // by one bump, otherwise a stale hover result could outlive the sense
    // resolution that produced it.
    private const string SmartRankingPipelineVersion =
        GlossBertDecisionCache.SenseRankingEpoch;
    private readonly AppSettings _settings;
    private readonly LocalDictionaryService _dictionary;
    private readonly GlossBertDictionarySelectorService _glossBertSelector;
    private readonly SmartDictionaryResultCache _smartResultCache;
    private readonly GlossBertDecisionCache _decisionCache;
    private readonly ISmartPhraseTranslator? _smartPhraseTranslator;
    private readonly SmartPhraseTranslationCache? _smartPhraseCache;
    private readonly TranslationScaffoldBuilder _phraseSemanticScaffoldBuilder;
    private readonly TranslationScaffoldBuilder _phraseDeterministicScaffoldBuilder;
    private readonly Action<string> _reportStatus;
    private readonly SemaphoreSlim _smartBackendLock = new(1, 1);
    private string? _activeSmartBackend;
    private bool _phraseSetupFailureReported;

    public SmartHoverPipeline(
        AppSettings settings,
        LocalDictionaryService dictionary,
        GlossBertDictionarySelectorService glossBertSelector,
        SmartDictionaryResultCache smartResultCache,
        GlossBertDecisionCache decisionCache,
        Action<string> reportStatus,
        ISmartPhraseTranslator? smartPhraseTranslator = null,
        SmartPhraseTranslationCache? smartPhraseCache = null,
        TranslationScaffoldNeuralRanker? phraseNeuralRanker = null)
    {
        _settings = settings;
        _dictionary = dictionary;
        _glossBertSelector = glossBertSelector;
        _smartResultCache = smartResultCache;
        _decisionCache = decisionCache;
        _smartPhraseTranslator = smartPhraseTranslator;
        _smartPhraseCache = smartPhraseCache;
        _phraseSemanticScaffoldBuilder = new TranslationScaffoldBuilder(
            dictionary,
            phraseNeuralRanker);
        // Idiom rewrites are synthetic English. Their mandatory anchors still
        // belong to the model input, while WSD must see the original sentence.
        _phraseDeterministicScaffoldBuilder = new TranslationScaffoldBuilder(dictionary);
        _reportStatus = reportStatus;
    }

    public string? ActiveBackend => _activeSmartBackend;

    public void ForgetBackend() => _activeSmartBackend = null;

    public async Task UnloadRankerAsync()
    {
        await _smartBackendLock.WaitAsync();
        try
        {
            await _glossBertSelector.UnloadAsync();
            _activeSmartBackend = null;
        }
        finally
        {
            _smartBackendLock.Release();
        }
    }

    private void ReportStatus(string status) => _reportStatus(status);

    private static void LogError(Exception exception) =>
        HoverTranslationService.LogError(exception);

    public void Dispose() => _smartBackendLock.Dispose();

    public async Task<SmartHoverResult> ResolveSmartHoverAsync(
        HoverOcrResult recognized,
        string smartBackend,
        CancellationToken cancellationToken)
    {
        DictionaryLookupResult lookup = await _dictionary.LookupWithSenseMetadataAsync(
            recognized.Text,
            recognized.HoveredWord,
            recognized.CharacterIndex,
            cancellationToken);
        if (!lookup.Found)
        {
            return new SmartHoverResult(
                lookup.Source,
                lookup.Translation,
                LocalDictionaryService.EngineLabel,
                DictionaryFound: false,
                lookup.IsPhrase,
                lookup.Language,
                Confidence: null);
        }

        SmartDictionaryTargetSpan targetSpan = lookup.IsPhrase
            ? new SmartDictionaryTargetSpan(lookup.Source, true)
            : SmartDictionarySenseResolver.ResolveTargetSpan(
                recognized.Text,
                lookup.Source,
                recognized.CharacterIndex);
        string displaySource = targetSpan.Source;
        bool displayIsPhrase = lookup.IsPhrase || targetSpan.IsPhrase;
        bool englishLookup =
            string.Equals(lookup.Language, "EN", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                ResolveSmartSourceLanguage(lookup.Language),
                "English",
                StringComparison.Ordinal);
        bool englishWord = !lookup.IsPhrase && englishLookup;
        bool ambiguousBroadPhrase =
            lookup.IsPhrase &&
            englishLookup &&
            !lookup.IsTrustedPhrase &&
            lookup.HasMultipleMeanings;
        bool rankableEnglishTarget = englishWord || ambiguousBroadPhrase;

        // Curated/flat entries have no aligned English glosses, but the cheap
        // context rules are still useful (for example teetering ... verge).
        // Run them before falling back to a multi-chip dictionary card.
        if (rankableEnglishTarget && lookup.SenseCandidates.Count == 0)
        {
            string[] flatTranslations =
                DictionaryMeaningParser.SplitMeanings(lookup.Translation);
            SmartDictionaryHeuristicSelection? tatoebaSelection =
                SmartDictionaryHeuristicRanker.TrySelectFromContextTranslation(
                    lookup.ContextTranslation,
                    flatTranslations);
            if (tatoebaSelection is not null)
            {
                return new SmartHoverResult(
                    displaySource,
                    tatoebaSelection.Translation,
                    SmartDictionaryHeuristicRanker.TatoebaEngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    tatoebaSelection.Confidence);
            }

            SmartDictionaryHeuristicSelection? flatSelection =
                SmartDictionaryHeuristicRanker.TrySelect(
                    recognized.Text,
                    lookup.Source,
                    recognized.CharacterIndex,
                    flatTranslations,
                    "English");
            if (flatSelection is not null)
            {
                return new SmartHoverResult(
                    displaySource,
                    flatSelection.Translation,
                    SmartDictionaryHeuristicRanker.EngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    flatSelection.Confidence);
            }
        }

        // Trusted phrases and clean one-meaning broad phrases are already final.
        // Only an ambiguous broad phrase enters the same definition-aligned
        // routing as a word; curated overrides must never be reinterpreted.
        bool hasRankableEnglishSenses =
            rankableEnglishTarget &&
            lookup.SenseCandidates.Count > 0;
        if (!hasRankableEnglishSenses)
        {
            return LocalSmartResult(lookup, displaySource, displayIsPhrase);
        }

        try
        {
            SmartDictionaryRankerPlan plan =
                SmartDictionarySenseResolver.CreateRankerPlan(
                    recognized.Text,
                    lookup.Source,
                    recognized.CharacterIndex,
                    lookup.SenseCandidates);
            if (plan.Outcomes.Count == 0)
            {
                return LocalSmartResult(lookup, displaySource, displayIsPhrase);
            }

            string[] visibleTranslations = plan.Outcomes
                .OrderBy(outcome => outcome.GroupId)
                .Select(outcome => outcome.Translation)
                .ToArray();
            // Two senses may now render to the same surface and differ only in
            // register. The heuristics below must keep reading clean Russian,
            // but the candidate card and the cache key need the labelled form:
            // the cache validates a stored GroupId against this list, and a
            // duplicate entry would silently break that alignment.
            string[] labelledTranslations = plan.Outcomes
                .OrderBy(outcome => outcome.GroupId)
                .Select(outcome => outcome.Describe())
                .ToArray();
            SmartDictionaryHeuristicSelection? tatoebaSelection =
                SmartDictionaryHeuristicRanker.TrySelectFromContextTranslation(
                    lookup.ContextTranslation,
                    visibleTranslations);
            if (tatoebaSelection is not null)
            {
                return new SmartHoverResult(
                    displaySource,
                    tatoebaSelection.Translation,
                    SmartDictionaryHeuristicRanker.TatoebaEngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    tatoebaSelection.Confidence)
                {
                    SelectedSenseHints = SemanticSenseEvidenceFactory.CreateOptional(
                        displaySource,
                        plan,
                        tatoebaSelection.Index,
                        tatoebaSelection.Confidence,
                        SenseEvidenceOrigin.TatoebaAlignment)
                };
            }

            if (visibleTranslations.Length == 1)
            {
                return new SmartHoverResult(
                    displaySource,
                    visibleTranslations[0],
                    SmartDictionaryHeuristicRanker.EngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    Confidence: 1)
                {
                    // A single aligned sense needs no neural decision, but its
                    // definition is still useful advisory evidence for the
                    // sentence translator (afterlife, elderly, and similar).
                    SelectedSenseHints = SemanticSenseEvidenceFactory.CreateOptional(
                        displaySource,
                        plan,
                        plan.Outcomes[0].GroupId,
                        confidence: 1,
                        SenseEvidenceOrigin.SingleOutcome)
                };
            }

            SmartDictionaryHeuristicSelection? fastSelection =
                SmartDictionaryHeuristicRanker.TrySelect(
                    recognized.Text,
                    lookup.Source,
                    recognized.CharacterIndex,
                    visibleTranslations,
                    "English");
            if (fastSelection is not null)
            {
                return new SmartHoverResult(
                    displaySource,
                    fastSelection.Translation,
                    SmartDictionaryHeuristicRanker.EngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    fastSelection.Confidence)
                {
                    SelectedSenseHints = SemanticSenseEvidenceFactory.CreateOptional(
                        displaySource,
                        plan,
                        fastSelection.Index,
                        fastSelection.Confidence,
                        SenseEvidenceOrigin.Heuristic)
                };
            }

            GlossBertRankerProvider provider = ResolveRankerProvider(smartBackend);
            RankerCpuModelProfile modelProfile =
                SemanticRankerProfileResolver.FromSettings(_settings);
            if (!string.Equals(_activeSmartBackend, smartBackend, StringComparison.Ordinal) ||
                _glossBertSelector.CpuModelProfile != modelProfile ||
                _glossBertSelector.ActiveProvider != provider ||
                string.IsNullOrWhiteSpace(_glossBertSelector.ActiveModelVersion))
            {
                await PrepareSmartBackendAsync(smartBackend, cancellationToken);
            }

            string modelVersion = _glossBertSelector.ActiveModelVersion
                ?? GlossBertDictionarySelectorService.GetModelCacheId(
                    provider,
                    modelProfile);
            string expectedPipeline = ExpectedRankingPipeline(
                provider,
                labelledTranslations.Length,
                modelVersion);
            SmartDictionaryCacheRequest cacheRequest = CreateSmartCacheRequest(
                recognized.Text,
                lookup.Source,
                recognized.CharacterIndex,
                labelledTranslations,
                "English",
                smartBackend,
                modelVersion,
                expectedPipeline);
            SmartDictionarySelection? cachedSelection =
                await TryGetSmartSelectionSafeAsync(cacheRequest, cancellationToken);
            if (cachedSelection is null && expectedPipeline == "bi+cross")
            {
                // Missing target/gloss hashes deterministically fail open to the
                // full cross encoder, so that result has its own reusable key.
                SmartDictionaryCacheRequest crossRequest = cacheRequest with
                {
                    RankingPipeline = "cross"
                };
                cachedSelection =
                    await TryGetSmartSelectionSafeAsync(crossRequest, cancellationToken);
            }

            if (cachedSelection is not null)
            {
                SmartHoverResult cachedResult = new(
                    displaySource,
                    cachedSelection.Translation,
                    SmartDictionaryResultCache.EngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    cachedSelection.Confidence)
                {
                    SelectedSenseHints = SemanticSenseEvidenceFactory.CreateOptional(
                        displaySource,
                        plan,
                        cachedSelection.Index,
                        cachedSelection.Confidence,
                        // The result cache does not record which path decided,
                        // so replaying one must not claim a ranker verdict.
                        SenseEvidenceOrigin.ResultCache)
                };
                return cachedResult;
            }

            // Second chance before inference: the shared decision cache keys on
            // exactly what reaches the worker, so a verdict earned by the
            // full-text scaffold for this word in this line is reusable here.
            // Warm-up above already validated provider and model identity — a
            // cache hit may skip inference, never that validation.
            // Pair scorers use the bounded target-centred window; ConSeC keeps
            // the complete sentence. In both cases the key and worker see the
            // same text.
            TargetCenteredContext rankerContext = TargetCenteredContextBuilder.BuildForRanker(
                recognized.Text,
                lookup.Source,
                recognized.CharacterIndex,
                provider,
                modelProfile);
            // Enrichment is spent only where the sequence budget allows it:
            // measured, an NPU pair already truncates above the median gloss.
            IReadOnlyList<string> rankerGlosses =
                plan.GlossesFor(provider == GlossBertRankerProvider.Cpu);
            string decisionKey = CreateDecisionKey(
                rankerContext.Text,
                lookup.Source,
                rankerContext.CharacterIndex,
                plan,
                provider,
                modelVersion);
            GlossBertDecision? sharedDecision = await TryGetSharedDecisionSafeAsync(
                decisionKey,
                plan.Outcomes.Count,
                cancellationToken);
            if (sharedDecision is { Abstained: false, SelectedGroupId: { } sharedGroupId })
            {
                SmartDictionaryRankerOutcome sharedOutcome = plan.Outcomes
                    .First(outcome => outcome.GroupId == sharedGroupId);
                // Also promote the verdict into the hover cache. Its key is the
                // wider one (a context window rather than the exact worker
                // input), so returning early without promoting would keep
                // re-resolving the plan for neighbouring cursor positions that
                // the hover cache alone would have answered.
                SmartDictionarySelection promoted = new(
                    sharedOutcome.GroupId,
                    sharedOutcome.Translation,
                    sharedDecision.Confidence ?? 0,
                    string.Empty);
                SmartHoverResult sharedResult = new(
                    displaySource,
                    sharedOutcome.Translation,
                    SmartDictionaryResultCache.EngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    promoted.Confidence,
                    // Keyed on the model and pipeline that actually produced the
                    // verdict, exactly as the non-cached path below does.
                    CreateSmartCacheRequest(
                        recognized.Text,
                        lookup.Source,
                        recognized.CharacterIndex,
                        labelledTranslations,
                        "English",
                        smartBackend,
                        sharedDecision.ModelVersion,
                        sharedDecision.Pipeline),
                    promoted)
                {
                    SelectedSenseHints = SemanticSenseEvidenceFactory.CreateOptional(
                        displaySource,
                        plan,
                        sharedOutcome.GroupId,
                        promoted.Confidence,
                        SenseEvidenceOrigin.RankerAccepted)
                };
                return sharedResult;
            }

            // No intermediate candidate card: the overlay keeps its loading
            // state until the ranker delivers the final verdict, so the card
            // renders exactly once instead of flashing candidates first.
            string visibleCandidates = FormatVisibleOutcomes(labelledTranslations);
            GlossBertDictionarySelection ranked = sharedDecision is null
                ? await _glossBertSelector.SelectAsync(
                    rankerContext.Text,
                    lookup.Source,
                    rankerContext.CharacterIndex,
                    rankerGlosses,
                    plan.CandidateGroupIds,
                    "English",
                    provider,
                    cancellationToken)
                // A cached abstention is as deterministic as a cached selection:
                // repeating the inference would return the same shrug.
                : new GlossBertDictionarySelection(
                    Index: null,
                    Abstained: true,
                    sharedDecision.Margin,
                    sharedDecision.Score,
                    sharedDecision.Confidence,
                    provider,
                    sharedDecision.ExecutionProvider,
                    sharedDecision.Device,
                    sharedDecision.ModelVersion,
                    sharedDecision.Pipeline);
            if (sharedDecision is null)
            {
                // Same rule as the scaffold ranker: a worker that lost its
                // bi-encoder mid-request answers cross-only under a refreshed
                // identity, and the key built before inference still names the
                // old one. Key the verdict by the identity that produced it, the
                // way the hover cache request below already does.
                string storageKey = GlossBertDecisionCache.ResolveStorageFingerprint(
                    decisionKey,
                    modelVersion,
                    ranked.ModelVersion,
                    resultModelIdentity => CreateDecisionKey(
                        rankerContext.Text,
                        lookup.Source,
                        rankerContext.CharacterIndex,
                        plan,
                        provider,
                        resultModelIdentity));
                await StoreSharedDecisionSafeAsync(
                    storageKey,
                    new GlossBertDecision(
                        ranked.SelectedGroupId,
                        ranked.Abstained,
                        ranked.Margin,
                        ranked.Score,
                        ranked.Confidence,
                        ranked.ExecutionProvider,
                        ranked.Device,
                        ranked.ModelVersion,
                        ranked.Pipeline));
            }
            cacheRequest = CreateSmartCacheRequest(
                recognized.Text,
                lookup.Source,
                recognized.CharacterIndex,
                labelledTranslations,
                "English",
                smartBackend,
                ranked.ModelVersion,
                ranked.Pipeline);
            if (ranked.Abstained || ranked.SelectedGroupId is not { } groupId)
            {
                // An exact Tatoeba sentence translation outranks a shrug: the
                // user gets the whole line instead of unresolved candidates.
                if (!string.IsNullOrWhiteSpace(lookup.ContextTranslation))
                {
                    ReportStatus(
                        "Умный словарь: слово неоднозначно — показан точный перевод предложения");
                    return new SmartHoverResult(
                        recognized.Text,
                        lookup.ContextTranslation,
                        SmartDictionaryHeuristicRanker.TatoebaEngineLabel,
                        DictionaryFound: true,
                        IsPhrase: true,
                        lookup.Language,
                        Confidence: null);
                }

                ReportStatus(
                    "Умный словарь: контекста недостаточно — показаны безопасные словарные варианты");
                return new SmartHoverResult(
                    displaySource,
                    visibleCandidates,
                    SmartEngineLabel(smartBackend) + "  •  НЕ УВЕРЕН",
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    Confidence: null);
            }

            SmartDictionaryRankerOutcome selectedOutcome = plan.Outcomes
                .First(outcome => outcome.GroupId == groupId);
            SmartDictionarySelection cacheSelection = new(
                selectedOutcome.GroupId,
                selectedOutcome.Translation,
                ranked.Confidence ?? 0,
                string.Empty);
            SmartHoverResult selectedResult = new(
                displaySource,
                selectedOutcome.Translation,
                SmartEngineLabel(smartBackend),
                DictionaryFound: true,
                displayIsPhrase,
                lookup.Language,
                cacheSelection.Confidence,
                cacheRequest,
                cacheSelection)
            {
                SelectedSenseHints = SemanticSenseEvidenceFactory.CreateOptional(
                    displaySource,
                    plan,
                    selectedOutcome.GroupId,
                    cacheSelection.Confidence,
                    SenseEvidenceOrigin.RankerAccepted)
            };
            return selectedResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(ex);
            if (!string.IsNullOrWhiteSpace(lookup.ContextTranslation))
            {
                ReportStatus(
                    "Умный словарь: модель недоступна — показан точный перевод предложения");
                return new SmartHoverResult(
                    recognized.Text,
                    lookup.ContextTranslation,
                    SmartDictionaryHeuristicRanker.TatoebaEngineLabel,
                    DictionaryFound: true,
                    IsPhrase: true,
                    lookup.Language,
                    Confidence: null);
            }

            ReportStatus(
                "Умный словарь: модель недоступна — показаны локальные варианты без полного перевода");
            return UnavailableSmartResult(
                lookup,
                smartBackend,
                displaySource,
                displayIsPhrase);
        }
    }

    private static SmartHoverResult LocalSmartResult(
        DictionaryLookupResult lookup,
        string? source = null,
        bool? isPhrase = null) =>
        new(
            source ?? lookup.Source,
            lookup.Translation,
            LocalDictionaryService.EngineLabel,
            DictionaryFound: true,
            isPhrase ?? lookup.IsPhrase,
            lookup.Language,
            Confidence: null);

    private static SmartHoverResult UnavailableSmartResult(
        DictionaryLookupResult lookup,
        string smartBackend,
        string? source = null,
        bool? isPhrase = null) =>
        new(
            source ?? lookup.Source,
            lookup.Translation,
            SmartEngineLabel(smartBackend) + "  •  НЕДОСТУПЕН",
            DictionaryFound: true,
            isPhrase ?? lookup.IsPhrase,
            lookup.Language,
            Confidence: null);

    private bool PhraseTranslationEnabled =>
        SmartPhraseTranslationModes.Normalize(_settings.SmartPhraseTranslationMode) ==
        SmartPhraseTranslationModes.TranslateGemma;

    internal async Task<SmartHoverPhraseLine?> TranslatePhraseLineAsync(
        HoverOcrResult recognized,
        SmartHoverResult dictionaryResult,
        CancellationToken cancellationToken)
    {
        if (!PhraseTranslationEnabled ||
            _smartPhraseTranslator is null ||
            _smartPhraseCache is null ||
            !HasReliablePhraseContext(recognized) ||
            !string.Equals(dictionaryResult.Language, "EN", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Settings can change while the optional generation is in flight.
        // One request must keep one backend identity and label throughout;
        // the facade cancels/restarts the runtime separately.
        TranslateGemmaExecutionBackend phraseBackend = PhraseBackend;

        SmartPhraseContext? phrase = SmartPhraseContextBuilder.Build(
            recognized.Text,
            recognized.HoveredWord,
            recognized.CharacterIndex,
            recognized.CurrentSpeaker);
        if (phrase is null)
        {
            return null;
        }

        SmartPhraseModelInput modelInput = SmartPhraseIdiomRewriter.Rewrite(
            phrase.Value.Text,
            recognized.HoveredWord,
            phrase.Value.CharacterIndex);
        IReadOnlyCollection<string> preResolvedNeuralSources = new[]
            {
                recognized.HoveredWord,
                dictionaryResult.Source
            }
            .Concat(dictionaryResult.SelectedSenseHints.Select(hint => hint.Source))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        TranslationScaffold semanticScaffold =
            await _phraseSemanticScaffoldBuilder.BuildAsync(
                modelInput.SourcePhrase,
                neuralSession: null,
                cancellationToken: cancellationToken,
                preResolvedNeuralSources: preResolvedNeuralSources);
        TranslationScaffold modelScaffold =
            string.Equals(
                modelInput.TransformIdentity,
                SmartPhraseIdiomRewriter.NoTransformIdentity,
                StringComparison.Ordinal)
                ? semanticScaffold
                : await _phraseDeterministicScaffoldBuilder.BuildAsync(
                    modelInput.ModelInputPhrase,
                    cancellationToken);
        IReadOnlyList<ScaffoldSenseHint> senseHints = MergePhraseSenseHints(
            semanticScaffold.SenseHints,
            dictionaryResult.SelectedSenseHints);
        SmartPhraseTranslationRequest request = CreatePhraseTranslationRequest(
            modelInput,
            recognized.PreviousContext,
            recognized.CurrentSpeaker ?? phrase.Value.CurrentSpeaker,
            modelScaffold.Terms,
            senseHints,
            modelScaffold.ProperNames,
            phraseBackend);
        SmartPhraseRejectionReason requestRejection =
            SmartPhraseTranslationGuard.ValidateRequest(request);
        if (requestRejection != SmartPhraseRejectionReason.None)
        {
            HoverTranslationService.LogDiagnostic(FormatPhraseRejectionDiagnostic(
                "request",
                requestRejection,
                request,
                result: null));
            return UnavailablePhraseLine(
                phrase.Value.Text,
                phraseBackend,
                requestRejection);
        }
        try
        {
            // A phrase-cache hit may skip generation, never artifact/provider
            // validation: otherwise replacing the GGUF could keep serving an
            // answer that claims the pinned model identity.
            await _smartPhraseTranslator.PrepareAsync(request, cancellationToken);
            SmartPhraseTranslationResult? translated =
                await _smartPhraseCache.TryGetAsync(request, cancellationToken);
            if (translated is null)
            {
                translated = await _smartPhraseTranslator.TranslateAsync(
                    request,
                    cancellationToken);
            }

            SmartPhraseGuardResult guarded = SmartPhraseTranslationGuard.ValidateForDisplay(
                request,
                translated);
            if (!guarded.Accepted)
            {
                HoverTranslationService.LogDiagnostic(FormatPhraseRejectionDiagnostic(
                    "display",
                    guarded.Reason,
                    request,
                    translated));
                return UnavailablePhraseLine(
                    phrase.Value.Text,
                    phraseBackend,
                    guarded.Reason);
            }

            if (!translated.FromCache && translated.Cacheable)
            {
                await _smartPhraseCache.StoreAsync(
                    request,
                    translated,
                    CancellationToken.None);
            }

            _phraseSetupFailureReported = false;
            return new SmartHoverPhraseLine(
                phrase.Value.Text,
                guarded.Translation,
                TranslateGemmaTranslationService.GetEngineLabel(phraseBackend),
                SmartPhraseLineStatus.Translated,
                translated.FromCache)
            {
                SenseHints = senseHints,
                AppliedSenseHints = translated.AppliedSenseHints
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The primary dictionary result is already visible. Keeping this
            // failure inside the optional phase prevents the outer ranker catch
            // from replacing a useful dictionary answer with an unavailable phrase line.
            LogError(exception);
            if (!_phraseSetupFailureReported)
            {
                _phraseSetupFailureReported = true;
                ReportStatus(
                    "TranslateGemma недоступна — словарный результат сохранён без перевода фразы");
            }

            // Generation failed, so no guard ever ruled on a translation.
            return UnavailablePhraseLine(
                phrase.Value.Text,
                phraseBackend,
                rejectionReason: null);
        }
    }

    internal static IReadOnlyList<ScaffoldSenseHint> MergePhraseSenseHints(
        IReadOnlyList<ScaffoldSenseHint> sentenceHints,
        IReadOnlyList<ScaffoldSenseHint> hoveredHints)
    {
        // Deduplication is by (source, selected surface), not by definition:
        // one accepted sense emits one hint per gloss describing it, so a word
        // routinely arrives two or three times with the same Russian anchor and
        // the same rejected surfaces. Those repeats constrain nothing and spend
        // the bounded prompt that the hovered word competes for.
        // Only evidence firm enough to steer generation reaches the prompt.
        // Untrusted hints are not weak evidence, they are close to a coin flip
        // (card E3), and conditioning on one pushes the generator toward a word
        // the disambiguator itself was unsure about.
        ScaffoldSenseHint[] distinctHoveredHints = hoveredHints
            .Where(SenseEvidenceTrustPolicy.Accepts)
            .GroupBy(hint => (hint.Source, hint.SelectedTranslation))
            .Select(group => group.Last())
            .Select(hint => hint with { IsPrimary = true })
            .Take(TranslationScaffoldBuilder.MaximumSenseHintCount)
            .ToArray();
        HashSet<(string Source, string Translation)> hoveredKeys =
            distinctHoveredHints
                .Select(hint => (hint.Source, hint.SelectedTranslation))
                .ToHashSet();
        int sentenceCapacity = TranslationScaffoldBuilder.MaximumSenseHintCount -
                               distinctHoveredHints.Length;
        // Primary hover evidence is retained first. The prompt budgeter also
        // understands IsPrimary, so this ordering is deterministic rather than
        // being an accidental last-item-wins convention.
        return distinctHoveredHints
            .Concat(sentenceHints
            .Where(hint => !hoveredKeys.Contains(
                (hint.Source, hint.SelectedTranslation)))
            // A closed-class word carries no disambiguation for the sentence.
            // The hovered word is exempt above: an explicit lookup is intent,
            // not noise, even when the user points at "no".
            .Where(hint => !IsFunctionWord(hint.Source))
            .Where(SenseEvidenceTrustPolicy.Accepts)
            .GroupBy(hint => (hint.Source, hint.SelectedTranslation))
            .Select(group => group.Last())
            .Take(sentenceCapacity))
            .ToArray();
    }

    internal static bool HasReliablePhraseContext(HoverOcrResult recognized) =>
        recognized.TextSource == HoverTextSource.TextHook ||
        recognized.IsSentenceContextStable;

    internal static string FormatPhraseRejectionDiagnostic(
        string stage,
        SmartPhraseRejectionReason reason,
        SmartPhraseTranslationRequest request,
        SmartPhraseTranslationResult? result)
    {
        string sourcePhrase = request.SourcePhrase ?? string.Empty;
        string fingerprint;
        try
        {
            fingerprint = SmartPhraseTranslationCache.CreateFingerprint(request);
        }
        catch (Exception)
        {
            // Request rejection diagnostics run before the cache contract is
            // valid. Their privacy-safe correlation key must never turn the
            // optional phrase line into an exception that hides the dictionary.
            fingerprint = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(sourcePhrase)));
        }

        int sourceWords = sourcePhrase.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).Length;
        int outputWords = result?.Translation?.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
        int termCount = request.SemanticTerms?.Count ?? 0;
        int hintCount = request.SenseHints?.Count ?? 0;
        return "TranslateGemma phrase rejected " +
               $"stage={stage} reason={reason} sourceWords={sourceWords} " +
               $"outputWords={outputWords} terms={termCount} " +
               $"hints={hintCount} fingerprint={fingerprint[..12]}";
    }

    private SmartPhraseTranslationRequest CreatePhraseTranslationRequest(
        SmartPhraseModelInput modelInput,
        IReadOnlyList<string> previousContext,
        CurrentSpeakerContext? currentSpeaker,
        IReadOnlyList<ScaffoldTerm> semanticTerms,
        IReadOnlyList<ScaffoldSenseHint> senseHints,
        IReadOnlyList<string> scaffoldProperNames,
        TranslateGemmaExecutionBackend phraseBackend) =>
        new SmartPhraseTranslationRequest(
            modelInput.SourcePhrase,
            modelInput.ModelInputPhrase,
            modelInput.TransformIdentity,
            ExtractStableProperNames(modelInput.SourcePhrase)
                .Concat(scaffoldProperNames)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            (_smartPhraseTranslator ?? throw new InvalidOperationException(
                    "TranslateGemma phrase request without a translator."))
                .ModelIdentityFor(phraseBackend),
            TranslateGemmaTranslationService.PromptIdentity)
        {
            PreviousContext = previousContext,
            CurrentSpeaker = currentSpeaker,
            SemanticTerms = semanticTerms,
            SenseHints = senseHints
        };

    // The reason is a required argument rather than an optional one: every
    // unavailable line has to state whether a guard refused an existing
    // translation or generation never produced one.
    private static SmartHoverPhraseLine UnavailablePhraseLine(
        string source,
        TranslateGemmaExecutionBackend phraseBackend,
        SmartPhraseRejectionReason? rejectionReason) =>
        new(
            source,
            string.Empty,
            TranslateGemmaTranslationService.GetEngineLabel(phraseBackend),
            SmartPhraseLineStatus.Unavailable,
            FromCache: false)
        {
            RejectionReason = rejectionReason
        };

    private TranslateGemmaExecutionBackend PhraseBackend =>
        TranslateGemmaTranslationService.ResolveBackend(
            _settings.SmartPhraseTranslationBackend);

    internal static IReadOnlyList<string> ExtractStableProperNames(string phrase)
    {
        string[] tokens = phrase.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => new string(segment
                .Where(character =>
                    character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
                .ToArray()))
            .Where(token => token.Length >= 2)
            .ToArray();
        return tokens
            .Where((token, index) =>
                IsStableProperName(token) &&
                // Sentence-initial title case alone is not name evidence:
                // Sounds/Indeed/Maybe otherwise become legal residual English.
                (index > 0 || token.Skip(1).Any(character =>
                    character is >= 'A' and <= 'Z' or >= '0' and <= '9')))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsStableProperName(string token)
    {
        bool hasUppercase = token.Any(character => character is >= 'A' and <= 'Z');
        bool hasLowercase = token.Any(character => character is >= 'a' and <= 'z');
        if (!hasUppercase || !hasLowercase)
        {
            // OCR frequently capitalizes whole UI words. Treating BANK as a
            // name would let untranslated English through the output guard.
            return false;
        }

        return !IsFunctionWord(token);
    }

    /// <summary>
    /// A closed-class English word: never a name, and never worth a slot in a
    /// bounded sense prompt. One definition of the set, two consumers.
    /// </summary>
    internal static bool IsFunctionWord(string token)
    {
        string normalized = EnglishContextLexicon.NormalizeToken(token);
        return EnglishContextLexicon.DefinitionStopWords.Contains(normalized) ||
               EnglishContextLexicon.SubjectPronouns.Contains(normalized) ||
               EnglishContextLexicon.AuxiliaryBe.Contains(normalized) ||
               EnglishContextLexicon.AuxiliaryHave.Contains(normalized) ||
               EnglishContextLexicon.AuxiliaryDo.Contains(normalized) ||
               EnglishContextLexicon.Modals.Contains(normalized) ||
               EnglishContextLexicon.Determiners.Contains(normalized) ||
               EnglishContextLexicon.Possessives.Contains(normalized);
    }

    private static string FormatVisibleOutcomes(IReadOnlyList<string> translations)
    {
        const int maximumVisibleOutcomes = 8;
        const int maximumLength = 380;
        string value = string.Join(
            "; ",
            translations
                .Where(translation => !string.IsNullOrWhiteSpace(translation))
                .Take(maximumVisibleOutcomes));
        if (translations.Count > maximumVisibleOutcomes)
        {
            value += "; …";
        }

        return value.Length <= maximumLength
            ? value
            : value[..(maximumLength - 1)].TrimEnd() + "…";
    }

    private string ResolveSmartSourceLanguage(string? detectedLanguage)
    {
        string configured = SmartDictionarySourceLanguages.Normalize(
            _settings.SmartDictionarySourceLanguage);
        if (configured == SmartDictionarySourceLanguages.English)
        {
            return "English";
        }

        if (configured == SmartDictionarySourceLanguages.Japanese)
        {
            return "Japanese";
        }

        return string.Equals(detectedLanguage, "JA", StringComparison.OrdinalIgnoreCase)
            ? "Japanese"
            : "English";
    }

    private static GlossBertRankerProvider ResolveRankerProvider(string backend) =>
        backend == SmartDictionaryBackends.Npu
            ? GlossBertRankerProvider.Npu
            : GlossBertRankerProvider.Cpu;

    internal static string SmartEngineLabel(string backend) =>
        GlossBertDictionarySelectorService.GetEngineLabel(
            ResolveRankerProvider(backend));

    private SmartDictionaryCacheRequest CreateSmartCacheRequest(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<string> candidates,
        string sourceLanguage,
        string backend,
        string modelVersion,
        string rankingPipeline)
    {
        return new SmartDictionaryCacheRequest(
            context,
            target,
            characterIndex,
            candidates,
            sourceLanguage,
            backend,
            modelVersion,
            // The abstention threshold decides whether a verdict exists at all,
            // so it belongs in this key too: a recalibration must not leave a
            // stored selection readable that the new threshold would refuse.
            _dictionary.VersionToken + "." + SmartRankingPipelineVersion +
            "." + RankerAbstention.CacheTag,
            rankingPipeline);
    }

    internal static string ExpectedRankingPipeline(
        GlossBertRankerProvider provider,
        int distinctGroupCount,
        string modelVersion) =>
        modelVersion.StartsWith(
            GlossBertDictionarySelectorService.ConsecExpectedModelVersionPrefix,
            StringComparison.Ordinal)
            ? "consec-independent"
            : modelVersion.StartsWith(
                GlossBertDictionarySelectorService.SandwichExpectedModelVersionPrefix,
                StringComparison.Ordinal)
                ? "sandwich"
                : provider == GlossBertRankerProvider.Cpu &&
                  distinctGroupCount > 16 &&
                  modelVersion.EndsWith(
                      ":" + GlossBertDictionarySelectorService.CpuEmbeddingBundleCacheTag,
                      StringComparison.Ordinal)
                    ? "bi+cross"
                    : "cross";

    private string CreateDecisionKey(
        string context,
        string target,
        int characterIndex,
        SmartDictionaryRankerPlan plan,
        GlossBertRankerProvider provider,
        string modelVersion) =>
        GlossBertDecisionCache.CreateFingerprint(
            context,
            target,
            characterIndex,
            plan,
            provider,
            modelVersion,
            // Exactly what the scaffold ranker passes: the epoch is folded in
            // by the fingerprint itself, so the two paths share one entry.
            _dictionary.VersionToken);

    private async Task<GlossBertDecision?> TryGetSharedDecisionSafeAsync(
        string fingerprint,
        int outcomeCount,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _decisionCache.TryGetAsync(
                fingerprint,
                outcomeCount,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A cache is an optimization; hover must degrade to inference.
            LogError(ex);
            return null;
        }
    }

    private async Task StoreSharedDecisionSafeAsync(
        string fingerprint,
        GlossBertDecision decision)
    {
        try
        {
            // The verdict is already being rendered. Persisting it must neither
            // delay the card nor be discarded because the cursor moved.
            await _decisionCache.StoreAsync(
                fingerprint,
                decision,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    private async Task<SmartDictionarySelection?> TryGetSmartSelectionSafeAsync(
        SmartDictionaryCacheRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _smartResultCache.TryGetAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return null;
        }
    }

    public async Task StoreSmartSelectionSafeAsync(
        SmartDictionaryCacheRequest request,
        SmartDictionarySelection selection)
    {
        try
        {
            // The result is already on screen. Persisting it must neither delay the UI
            // nor be discarded merely because the cursor moved a moment later.
            await _smartResultCache.StoreAsync(request, selection, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    public async Task PrepareSmartBackendAsync(string backend, CancellationToken cancellationToken)
    {
        await _smartBackendLock.WaitAsync(cancellationToken);
        try
        {
            _glossBertSelector.ConfigureKeepWarm(_settings.SmartDictionaryKeepWarm);
            await _glossBertSelector.ConfigureCpuModelProfileAsync(
                SemanticRankerProfileResolver.FromSettings(_settings),
                cancellationToken);
            await _glossBertSelector.WarmUpAsync(
                ResolveRankerProvider(backend),
                cancellationToken);
            _activeSmartBackend = backend;
        }
        finally
        {
            _smartBackendLock.Release();
        }
    }

}
