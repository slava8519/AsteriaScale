using AsteriaScale.Interop;
using AsteriaScale.Models;
using AsteriaScale.RankerProtocol;

namespace AsteriaScale.Services;

internal sealed record SmartHoverResult(
        string Source,
        string Translation,
        string Engine,
        bool DictionaryFound,
        bool IsPhrase,
        string? Language,
        double? Confidence,
        bool UsedFullFallback,
        SmartDictionaryCacheRequest? CacheRequest = null,
        SmartDictionarySelection? CacheSelection = null);

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
    private readonly Func<string, CancellationToken, Task<(string Source, string Translation, string Engine)>> _translateFallback;
    private readonly Action<string> _reportStatus;
    private readonly Action<string, NativeMethods.Point> _onDemandFallbackObserved;
    private readonly SemaphoreSlim _smartBackendLock = new(1, 1);
    private string? _activeSmartBackend;

    public SmartHoverPipeline(
        AppSettings settings,
        LocalDictionaryService dictionary,
        GlossBertDictionarySelectorService glossBertSelector,
        SmartDictionaryResultCache smartResultCache,
        GlossBertDecisionCache decisionCache,
        Func<string, CancellationToken, Task<(string Source, string Translation, string Engine)>> translateFallback,
        Action<string> reportStatus,
        Action<string, NativeMethods.Point> onDemandFallbackObserved)
    {
        _settings = settings;
        _dictionary = dictionary;
        _glossBertSelector = glossBertSelector;
        _smartResultCache = smartResultCache;
        _decisionCache = decisionCache;
        _translateFallback = translateFallback;
        _reportStatus = reportStatus;
        _onDemandFallbackObserved = onDemandFallbackObserved;
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

    private bool IsAutomaticFallback =>
        SmartDictionaryFallbackModes.Normalize(_settings.SmartDictionaryFallbackMode) ==
        SmartDictionaryFallbackModes.Automatic;

    private bool IsOnDemandFallback =>
        SmartDictionaryFallbackModes.Normalize(_settings.SmartDictionaryFallbackMode) ==
        SmartDictionaryFallbackModes.OnDemand;

    private Task<(string Source, string Translation, string Engine)> TranslateSmartFallbackAsync(
        string source,
        CancellationToken cancellationToken) =>
        _translateFallback(source, cancellationToken);

    private void ReportStatus(string status) => _reportStatus(status);

    private static void LogError(Exception exception) =>
        HoverTranslationService.LogError(exception);

    public void Dispose() => _smartBackendLock.Dispose();

    public async Task<SmartHoverResult> ResolveSmartHoverAsync(
        HoverOcrResult recognized,
        NativeMethods.Point cursor,
        string smartBackend,
        CancellationToken cancellationToken)
    {
        DictionaryLookupResult lookup = await _dictionary.LookupWithSenseMetadataAsync(
            recognized.Text,
            recognized.HoveredWord,
            recognized.CharacterIndex,
            cancellationToken);
        if (IsOnDemandFallback)
        {
            _onDemandFallbackObserved(recognized.Text, cursor);
        }

        if (!lookup.Found)
        {
            if (IsAutomaticFallback)
            {
                (string fallbackSource, string fallbackTranslation, string fallbackEngine) =
                    await TranslateSmartFallbackAsync(recognized.Text, cancellationToken);
                return new SmartHoverResult(
                    fallbackSource,
                    fallbackTranslation,
                    fallbackEngine,
                    DictionaryFound: true,
                    IsPhrase: false,
                    Language: lookup.Language,
                    Confidence: null,
                    UsedFullFallback: true);
            }

            return new SmartHoverResult(
                lookup.Source,
                IsOnDemandFallback
                    ? "Нет записи в локальном словаре. Alt+Shift+D — перевести реплику целиком."
                    : lookup.Translation,
                IsOnDemandFallback
                    ? SmartEngineLabel(smartBackend)
                    : LocalDictionaryService.EngineLabel,
                DictionaryFound: false,
                lookup.IsPhrase,
                lookup.Language,
                Confidence: null,
                UsedFullFallback: false);
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
                NpuDictionarySelectorService.SplitMeanings(lookup.Translation);
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
                    tatoebaSelection.Confidence,
                    UsedFullFallback: false);
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
                    flatSelection.Confidence,
                    UsedFullFallback: false);
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
                    tatoebaSelection.Confidence,
                    UsedFullFallback: false);
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
                    Confidence: 1,
                    UsedFullFallback: false);
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
                    fastSelection.Confidence,
                    UsedFullFallback: false);
            }

            GlossBertRankerProvider provider = ResolveRankerProvider(smartBackend);
            if (!string.Equals(_activeSmartBackend, smartBackend, StringComparison.Ordinal) ||
                _glossBertSelector.ActiveProvider != provider ||
                string.IsNullOrWhiteSpace(_glossBertSelector.ActiveModelVersion))
            {
                await PrepareSmartBackendAsync(smartBackend, cancellationToken);
            }

            string modelVersion = _glossBertSelector.ActiveModelVersion
                ?? GlossBertDictionarySelectorService.GetModelCacheId(provider);
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
                return new SmartHoverResult(
                    displaySource,
                    cachedSelection.Translation,
                    SmartDictionaryResultCache.EngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    cachedSelection.Confidence,
                    UsedFullFallback: false);
            }

            // Second chance before inference: the shared decision cache keys on
            // exactly what reaches the worker, so a verdict earned by the
            // full-text scaffold for this word in this line is reusable here.
            // Warm-up above already validated provider and model identity — a
            // cache hit may skip inference, never that validation.
            // One window feeds both the key and the worker (roadmap #14.2B), so
            // the key can no longer describe a wider context than the model saw.
            TargetCenteredContext rankerContext = TargetCenteredContextBuilder.Build(
                recognized.Text,
                lookup.Source,
                recognized.CharacterIndex);
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
                return new SmartHoverResult(
                    displaySource,
                    sharedOutcome.Translation,
                    SmartDictionaryResultCache.EngineLabel,
                    DictionaryFound: true,
                    displayIsPhrase,
                    lookup.Language,
                    promoted.Confidence,
                    UsedFullFallback: false,
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
                    promoted);
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
                string storageKey = string.Equals(
                    ranked.ModelVersion,
                    modelVersion,
                    StringComparison.Ordinal)
                    ? decisionKey
                    : CreateDecisionKey(
                        rankerContext.Text,
                        lookup.Source,
                        rankerContext.CharacterIndex,
                        plan,
                        provider,
                        ranked.ModelVersion);
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
                        Confidence: null,
                        UsedFullFallback: false);
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
                    Confidence: null,
                    UsedFullFallback: false);
            }

            SmartDictionaryRankerOutcome selectedOutcome = plan.Outcomes
                .First(outcome => outcome.GroupId == groupId);
            SmartDictionarySelection cacheSelection = new(
                selectedOutcome.GroupId,
                selectedOutcome.Translation,
                ranked.Confidence ?? 0,
                string.Empty);
            return new SmartHoverResult(
                displaySource,
                selectedOutcome.Translation,
                SmartEngineLabel(smartBackend),
                DictionaryFound: true,
                displayIsPhrase,
                lookup.Language,
                cacheSelection.Confidence,
                UsedFullFallback: false,
                cacheRequest,
                cacheSelection);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(ex);
            if (IsAutomaticFallback)
            {
                ReportStatus(
                    "Умный словарь: ранжирование недоступно, запущен разрешённый полный перевод");
                (string fallbackSource, string fallbackTranslation, string fallbackEngine) =
                    await TranslateSmartFallbackAsync(recognized.Text, cancellationToken);
                return new SmartHoverResult(
                    fallbackSource,
                    fallbackTranslation,
                    fallbackEngine,
                    DictionaryFound: true,
                    IsPhrase: false,
                    lookup.Language,
                    Confidence: null,
                    UsedFullFallback: true);
            }

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
                    Confidence: null,
                    UsedFullFallback: false);
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
            Confidence: null,
            UsedFullFallback: false);

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
            Confidence: null,
            UsedFullFallback: false);

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
        provider == GlossBertRankerProvider.Cpu &&
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
