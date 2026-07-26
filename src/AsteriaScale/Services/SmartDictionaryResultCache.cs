using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AsteriaScale.Services;

/// <summary>
/// Everything that can change the answer produced by a Smart Dictionary ranker.
/// <paramref name="DictionaryVersion"/> should change whenever the effective
/// dictionary layers change.
/// </summary>
public sealed record SmartDictionaryCacheRequest(
    string Context,
    string Target,
    int CharacterIndex,
    IReadOnlyList<string> Candidates,
    string SourceLanguage,
    string Backend,
    string ModelId,
    string DictionaryVersion,
    string RankingPipeline = "cross");

/// <summary>
/// A small persistent LRU for completed Smart Dictionary selections.
/// Cache keys are SHA-256 fingerprints, so source dialogue is never written to disk.
/// </summary>
public sealed class SmartDictionaryResultCache : IAsyncDisposable
{
    internal const string EngineLabel = "NPU OCR  •  SMART-CACHE";

    // 4: key text normalization now mirrors the pinned BertNormalizer clean_text
    // stage, separating controls BERT deletes from whitespace BERT preserves.
    internal const int SchemaVersion = 4;
    private const int MaximumCandidates = 128;
    private const int MaximumStoredTextLength = 1_024;
    private const int DefaultCapacity = 4_096;
    private const long DefaultMaximumFileBytes = 4L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly PersistentLruStore<PersistedEntry> _store;

    public SmartDictionaryResultCache(
        string? cachePath = null,
        int capacity = DefaultCapacity,
        long maximumFileBytes = DefaultMaximumFileBytes)
    {
        _store = new PersistentLruStore<PersistedEntry>(
            cachePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AsteriaScale",
                "smart-dictionary-cache.v2.json"),
            SchemaVersion,
            capacity,
            maximumFileBytes,
            IsStructurallyValid,
            JsonOptions);
    }

    public string CachePath => _store.CachePath;

    /// <summary>
    /// Returns a cached result only when its index and translation still match the
    /// candidates supplied by the current dictionary lookup.
    /// </summary>
    public Task<SmartDictionarySelection?> TryGetAsync(
        SmartDictionaryCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        NormalizedRequest normalized = NormalizeRequest(request);
        return _store.UseAsync(access =>
        {
            if (!access.Entries.TryGetValue(normalized.Key, out PersistedEntry? entry))
            {
                return null;
            }

            if (!EntryMatchesCandidates(entry, normalized.Candidates))
            {
                access.Entries.Remove(normalized.Key);
                access.MarkDirty();
                return null;
            }

            entry.LastAccess = access.Touch();
            access.MarkDirty();
            return new SmartDictionarySelection(
                entry.Index,
                normalized.Candidates[entry.Index],
                entry.Confidence,
                entry.ContextTranslation);
        }, cancellationToken);
    }

    /// <summary>
    /// Stores a completed selection. Returns false instead of caching an answer
    /// that is not one of the current dictionary candidates.
    /// </summary>
    public Task<bool> StoreAsync(
        SmartDictionaryCacheRequest request,
        SmartDictionarySelection selection,
        CancellationToken cancellationToken = default)
    {
        NormalizedRequest normalized = NormalizeRequest(request);
        if (selection.Index < 0 ||
            selection.Index >= normalized.Candidates.Length ||
            !string.Equals(
                NormalizeCandidate(selection.Translation),
                normalized.Candidates[selection.Index],
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(false);
        }

        return _store.UseAsync(access =>
        {
            access.Entries[normalized.Key] = new PersistedEntry
            {
                Key = normalized.Key,
                Index = selection.Index,
                Translation = normalized.Candidates[selection.Index],
                Confidence = NormalizeConfidence(selection.Confidence),
                ContextTranslation = Truncate(NormalizeText(selection.ContextTranslation)),
                LastAccess = access.Touch()
            };
            access.MarkDirty();
            return true;
        }, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        _store.ClearAsync(cancellationToken);

    /// <summary>
    /// Persists pending entries and LRU touches immediately. Normal stores are
    /// debounced so disk I/O never remains in the hover critical path.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        _store.FlushAsync(cancellationToken);

    public ValueTask DisposeAsync() => _store.DisposeAsync();

    /// <summary>
    /// Entries currently held. Exists so a test can prove that a cache file
    /// written by an earlier build still loads (audit card A9).
    /// </summary>
    internal Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _store.UseAsync(access => access.Entries.Count, cancellationToken);

    internal static string CreateKeyFingerprint(SmartDictionaryCacheRequest request) =>
        NormalizeRequest(request).Key;

    /// <summary>
    /// The window this key describes is the same one the ranker receives
    /// (roadmap #14.2B). Before that it was narrower than the model's input, so
    /// two replies sharing a neighbourhood could reuse each other's verdict —
    /// a deliberate trade for hit rate that no longer has to be made.
    /// </summary>
    internal static string CreateLocalContextWindow(
        string context,
        string target,
        int characterIndex)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return string.Empty;
        }

        string rawTarget = target.Trim();
        TargetCenteredContext centered = TargetCenteredContextBuilder.Build(
            context,
            rawTarget,
            characterIndex);
        string window = centered.Text;
        int localTarget = centered.CharacterIndex;
        int targetLength = rawTarget.Length;
        if (targetLength > 0 &&
            localTarget >= 0 &&
            localTarget + targetLength <= window.Length &&
            string.Equals(
                window.Substring(localTarget, targetLength),
                rawTarget,
                StringComparison.OrdinalIgnoreCase))
        {
            window = window.Insert(localTarget + targetLength, "</target>")
                .Insert(localTarget, "<target>");
        }

        return NormalizeText(window).ToLowerInvariant();
    }

    private static NormalizedRequest NormalizeRequest(SmartDictionaryCacheRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string target = NormalizeText(request.Target).ToLowerInvariant();
        if (target.Length == 0)
        {
            throw new ArgumentException("Smart Dictionary cache target cannot be empty.", nameof(request));
        }

        string[] candidates = request.Candidates
            .Select(NormalizeCandidate)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumCandidates)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new ArgumentException("Smart Dictionary cache candidates cannot be empty.", nameof(request));
        }

        string backend = NormalizeIdentifier(request.Backend);
        string modelId = NormalizeIdentifier(request.ModelId);
        string dictionaryVersion = NormalizeIdentifier(request.DictionaryVersion);
        string rankingPipeline = NormalizeIdentifier(request.RankingPipeline);
        if (backend.Length == 0)
        {
            throw new ArgumentException("Smart Dictionary cache backend cannot be empty.", nameof(request));
        }

        if (modelId.Length == 0)
        {
            throw new ArgumentException("Smart Dictionary cache model id cannot be empty.", nameof(request));
        }

        if (dictionaryVersion.Length == 0)
        {
            throw new ArgumentException("Smart Dictionary cache dictionary version cannot be empty.", nameof(request));
        }

        if (rankingPipeline is not ("cross" or "bi+cross"))
        {
            throw new ArgumentException(
                "Smart Dictionary cache ranking pipeline must be cross or bi+cross.",
                nameof(request));
        }

        StringBuilder canonical = new();
        AppendComponent(canonical, "asteria-smart-dictionary-cache-key-v4");
        AppendComponent(canonical, NormalizeLanguage(request.SourceLanguage));
        AppendComponent(canonical, CreateLocalContextWindow(
            request.Context,
            request.Target,
            request.CharacterIndex));
        AppendComponent(canonical, target);
        AppendComponent(canonical, backend);
        AppendComponent(canonical, modelId);
        AppendComponent(canonical, rankingPipeline);
        AppendComponent(canonical, dictionaryVersion);
        AppendComponent(canonical, candidates.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string candidate in candidates)
        {
            AppendComponent(canonical, candidate.ToLowerInvariant());
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new NormalizedRequest(Convert.ToHexString(digest), candidates);
    }

    private static bool EntryMatchesCandidates(PersistedEntry entry, IReadOnlyList<string> candidates) =>
        entry.Index >= 0 &&
        entry.Index < candidates.Count &&
        string.Equals(
            NormalizeCandidate(entry.Translation),
            candidates[entry.Index],
            StringComparison.OrdinalIgnoreCase);

    private static bool IsStructurallyValid(PersistedEntry entry) =>
        entry.Key.Length == 64 &&
        entry.Key.All(Uri.IsHexDigit) &&
        entry.Index is >= 0 and < MaximumCandidates &&
        !string.IsNullOrWhiteSpace(entry.Translation) &&
        entry.Translation.Length <= MaximumStoredTextLength &&
        (entry.ContextTranslation?.Length ?? 0) <= MaximumStoredTextLength &&
        double.IsFinite(entry.Confidence) &&
        entry.Confidence is >= 0 and <= 1 &&
        entry.LastAccess >= 0;

    private static string NormalizeLanguage(string? value)
    {
        string language = NormalizeIdentifier(value);
        if (language is "en" or "eng" || language.Contains("english", StringComparison.Ordinal))
        {
            return "en";
        }

        if (language is "ja" or "jpn" or "jp" ||
            language.Contains("japanese", StringComparison.Ordinal))
        {
            return "ja";
        }

        if (language is "ru" or "rus" || language.Contains("russian", StringComparison.Ordinal))
        {
            return "ru";
        }

        return language.Length == 0 ? "auto" : language;
    }

    private static string NormalizeIdentifier(string? value) =>
        NormalizeText(value).ToLowerInvariant();

    private static string NormalizeCandidate(string? value) =>
        Truncate(NormalizeText(value));

    // FormC, not FormKC: compatibility folding merges inputs the ranker can tell
    // apart. The shared helper also follows BertNormalizer clean_text rather
    // than treating every .NET whitespace scalar as an interchangeable space.
    private static string NormalizeText(string? value) =>
        RankerCacheTextNormalizer.Normalize(value);

    private static string Truncate(string value) =>
        value.Length <= MaximumStoredTextLength ? value : value[..MaximumStoredTextLength];

    private static double NormalizeConfidence(double confidence) =>
        double.IsFinite(confidence) ? Math.Clamp(confidence, 0, 1) : 0;

    private static void AppendComponent(StringBuilder builder, string value)
    {
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }


    private sealed record NormalizedRequest(string Key, string[] Candidates);

    private sealed class CacheDocument
    {
        public int Version { get; set; }
        public List<PersistedEntry>? Entries { get; set; }
    }

    private sealed class PersistedEntry : IPersistentLruEntry
    {
        public string Key { get; set; } = string.Empty;
        public int Index { get; set; }
        public string Translation { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string ContextTranslation { get; set; } = string.Empty;
        public long LastAccess { get; set; }
    }
}
