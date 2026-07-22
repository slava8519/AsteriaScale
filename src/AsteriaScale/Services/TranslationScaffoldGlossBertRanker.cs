using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AsteriaScale.RankerProtocol;

namespace AsteriaScale.Services;

/// <summary>
/// Adapts one externally owned GlossBERT worker to scaffold sense requests.
/// Provider selection is explicit for every cache key and call; failures are
/// deliberately allowed to escape so the builder can open its operation-level
/// circuit without attempting a different provider.
/// </summary>
internal sealed class TranslationScaffoldGlossBertRanker
{
    private const int DefaultCacheCapacity = 512;

    private readonly GlossBertDictionarySelectorService _selector;
    private readonly Func<GlossBertRankerProvider> _providerResolver;
    private readonly Func<string> _dictionaryVersionResolver;
    private readonly int _cacheCapacity;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private long _accessSequence;

    public TranslationScaffoldGlossBertRanker(
        GlossBertDictionarySelectorService selector,
        Func<GlossBertRankerProvider> providerResolver,
        Func<string> dictionaryVersionResolver,
        int cacheCapacity = DefaultCacheCapacity)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _providerResolver = providerResolver ??
                            throw new ArgumentNullException(nameof(providerResolver));
        _dictionaryVersionResolver = dictionaryVersionResolver ??
                                     throw new ArgumentNullException(nameof(dictionaryVersionResolver));
        if (cacheCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
        }

        _cacheCapacity = cacheCapacity;
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
        string cacheKey = CreateCacheKey(
            request,
            provider,
            modelIdentity,
            dictionaryVersion);
        if (TryGetCached(cacheKey, out TranslationScaffoldNeuralResult cached))
        {
            return cached with
            {
                ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                FromCache = true
            };
        }

        GlossBertDictionarySelection selection = await _selector.SelectAsync(
            request.Sentence,
            request.Target,
            request.CharacterIndex,
            request.Plan.CandidateGlosses,
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
        StoreCached(cacheKey, result);
        return result;
    }

    internal static string CreateCacheKey(
        TranslationScaffoldNeuralRequest request,
        GlossBertRankerProvider provider,
        string modelIdentity,
        string dictionaryVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryVersion);

        StringBuilder canonical = new();
        Append(canonical, "asteria-translation-scaffold-glossbert-cache-v1");
        Append(canonical, RankerProtocolV2.Version);
        Append(canonical, provider.ToString());
        Append(canonical, modelIdentity);
        Append(canonical, dictionaryVersion);
        Append(canonical, NormalizeText(request.Sentence));
        Append(canonical, NormalizeText(request.Target));
        Append(
            canonical,
            request.CharacterIndex.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        foreach (SmartDictionaryRankerOutcome outcome in request.Plan.Outcomes)
        {
            Append(canonical, "outcome");
            Append(canonical, outcome.GroupId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            Append(canonical, outcome.SourceIndex.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            Append(canonical, NormalizeText(outcome.Translation));
        }

        for (int index = 0; index < request.Plan.CandidateGlosses.Count; index++)
        {
            Append(canonical, "candidate");
            Append(canonical, NormalizeText(request.Plan.CandidateGlosses[index]));
            Append(canonical, request.Plan.CandidateGroupIds[index].ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private bool TryGetCached(
        string key,
        out TranslationScaffoldNeuralResult result)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out CacheEntry? entry))
            {
                result = null!;
                return false;
            }

            entry.LastAccess = NextAccessSequenceLocked();
            result = entry.Result;
            return true;
        }
    }

    private void StoreCached(string key, TranslationScaffoldNeuralResult result)
    {
        lock (_cacheLock)
        {
            _cache[key] = new CacheEntry(result, NextAccessSequenceLocked());
            while (_cache.Count > _cacheCapacity)
            {
                string oldest = _cache.MinBy(pair => pair.Value.LastAccess).Key;
                _cache.Remove(oldest);
            }
        }
    }

    private long NextAccessSequenceLocked()
    {
        if (_accessSequence == long.MaxValue)
        {
            long sequence = 0;
            foreach (CacheEntry entry in _cache.Values.OrderBy(entry => entry.LastAccess))
            {
                entry.LastAccess = ++sequence;
            }

            _accessSequence = sequence;
        }

        return ++_accessSequence;
    }

    private static string NormalizeText(string value) =>
        string.Join(
                " ",
                (value ?? string.Empty).Normalize(NormalizationForm.FormKC)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim();

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }

    private sealed class CacheEntry(
        TranslationScaffoldNeuralResult result,
        long lastAccess)
    {
        public TranslationScaffoldNeuralResult Result { get; } = result;
        public long LastAccess { get; set; } = lastAccess;
    }
}
