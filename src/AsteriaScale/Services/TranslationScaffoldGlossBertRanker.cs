using System.Diagnostics;

namespace AsteriaScale.Services;

/// <summary>
/// Adapts one externally owned GlossBERT worker to scaffold sense requests.
/// Provider selection is explicit for every cache key and call; failures are
/// deliberately allowed to escape so the builder can open its operation-level
/// circuit without attempting a different provider.
/// </summary>
internal sealed class TranslationScaffoldGlossBertRanker
{
    private readonly GlossBertDictionarySelectorService _selector;
    private readonly GlossBertDecisionCache _decisionCache;
    private readonly Func<GlossBertRankerProvider> _providerResolver;
    private readonly Func<string> _dictionaryVersionResolver;

    public TranslationScaffoldGlossBertRanker(
        GlossBertDictionarySelectorService selector,
        GlossBertDecisionCache decisionCache,
        Func<GlossBertRankerProvider> providerResolver,
        Func<string> dictionaryVersionResolver)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _decisionCache = decisionCache ??
                         throw new ArgumentNullException(nameof(decisionCache));
        _providerResolver = providerResolver ??
                            throw new ArgumentNullException(nameof(providerResolver));
        _dictionaryVersionResolver = dictionaryVersionResolver ??
                                     throw new ArgumentNullException(nameof(dictionaryVersionResolver));
    }

    public async Task<TranslationScaffoldNeuralResult> RankAsync(
        TranslationScaffoldNeuralRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch elapsed = Stopwatch.StartNew();

        GlossBertRankerProvider provider = _providerResolver();
        string dictionaryVersion = _dictionaryVersionResolver()?.Trim() ?? string.Empty;
        if (dictionaryVersion.Length == 0)
        {
            throw new InvalidOperationException(
                "A loaded dictionary version is required for a cached scaffold decision.");
        }

        if (_selector.ActiveProvider != provider ||
            string.IsNullOrWhiteSpace(_selector.ActiveModelVersion))
        {
            // Loading the explicitly requested provider establishes the exact
            // validated artifact/pipeline identity before a cache lookup. A
            // cache hit may skip inference, never provider validation.
            await _selector.WarmUpAsync(provider, cancellationToken);
        }

        string modelIdentity = _selector.ActiveModelVersion
            ?? throw new InvalidOperationException(
                "GlossBERT did not report a model identity after warm-up.");
        // One window feeds both the key and the worker, so a cached verdict can
        // never describe a context the model did not actually receive.
        TargetCenteredContext rankerContext = TargetCenteredContextBuilder.Build(
            request.Sentence,
            request.Target,
            request.CharacterIndex);
        string cacheKey = GlossBertDecisionCache.CreateFingerprint(
            rankerContext.Text,
            request.Target,
            rankerContext.CharacterIndex,
            request.Plan,
            provider,
            modelIdentity,
            dictionaryVersion);
        GlossBertDecision? cached = await _decisionCache.TryGetAsync(
            cacheKey,
            request.Plan.Outcomes.Count,
            cancellationToken);
        if (cached is not null)
        {
            return new TranslationScaffoldNeuralResult(
                cached.SelectedGroupId,
                cached.Abstained,
                cached.Margin,
                cached.Score,
                cached.Confidence,
                provider,
                cached.ExecutionProvider,
                cached.Device,
                cached.ModelVersion,
                cached.Pipeline,
                elapsed.ElapsedMilliseconds,
                FromCache: true);
        }

        GlossBertDictionarySelection selection = await _selector.SelectAsync(
            rankerContext.Text,
            request.Target,
            rankerContext.CharacterIndex,
            request.Plan.GlossesFor(provider == GlossBertRankerProvider.Cpu),
            request.Plan.CandidateGroupIds,
            "English",
            provider,
            cancellationToken);
        TranslationScaffoldNeuralResult result = new(
            selection.SelectedGroupId,
            selection.Abstained,
            selection.Margin,
            selection.Score,
            selection.Confidence,
            selection.RequestedProvider,
            selection.ExecutionProvider,
            selection.Device,
            selection.ModelVersion,
            selection.Pipeline,
            elapsed.ElapsedMilliseconds);

        // The selector has already validated the response envelope, provider,
        // model identity and GroupId. Both selected decisions and calibrated
        // abstentions are deterministic reusable results; exceptions never get
        // here and therefore never enter the cache.
        //
        // The key above was built from the identity warm-up established, but the
        // worker can lose its bi-encoder mid-request: it then disposes that
        // pipeline, refreshes its reported identity and answers cross-only.
        // Response validation accepts that (it checks the prefix, not equality),
        // so storing under the pre-inference key would file a cross-only verdict
        // as a "bi+cross" one and serve it back to a healthy worker after a
        // restart. File the verdict under the identity that actually produced it.
        string storageKey = GlossBertDecisionCache.ResolveStorageFingerprint(
            cacheKey,
            modelIdentity,
            selection.ModelVersion,
            resultModelIdentity => GlossBertDecisionCache.CreateFingerprint(
                rankerContext.Text,
                request.Target,
                rankerContext.CharacterIndex,
                request.Plan,
                provider,
                resultModelIdentity,
                dictionaryVersion));
        await _decisionCache.StoreAsync(
            storageKey,
            new GlossBertDecision(
                selection.SelectedGroupId,
                selection.Abstained,
                selection.Margin,
                selection.Score,
                selection.Confidence,
                selection.ExecutionProvider,
                selection.Device,
                selection.ModelVersion,
                selection.Pipeline),
            cancellationToken);
        return result;
    }

    /// <summary>
    /// The scaffold-shaped view of the shared decision key. Both this ranker and
    /// the hover pipeline must produce the same fingerprint for the same worker
    /// call, so the canonical form lives in <see cref="GlossBertDecisionCache"/>.
    /// </summary>
    internal static string CreateCacheKey(
        TranslationScaffoldNeuralRequest request,
        GlossBertRankerProvider provider,
        string modelIdentity,
        string dictionaryVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GlossBertDecisionCache.CreateFingerprint(
            request.Sentence,
            request.Target,
            request.CharacterIndex,
            request.Plan,
            provider,
            modelIdentity,
            dictionaryVersion);
    }
}
