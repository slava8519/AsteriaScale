using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AsteriaScale.RankerProtocol;

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
    internal const int SchemaVersion = 5;
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
                RemoveRegisterLabel(entry.Translation),
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
            !CandidateMatchesTranslation(
                normalized.Candidates[selection.Index],
                NormalizeCandidate(selection.Translation)))
        {
            return Task.FromResult(false);
        }

        return _store.UseAsync(access =>
        {
            access.Entries[normalized.Key] = new PersistedEntry
            {
                Key = normalized.Key,
                Index = selection.Index,
                Translation = NormalizeCandidate(selection.Translation),
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
    /// Compatibility component for pair-model cache keys. ConSeC additionally
    /// keys its complete exact source below, because its worker receives the
    /// full sentence rather than this bounded target-centred window.
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

        if (rankingPipeline is not ("cross" or "bi+cross" or "consec-independent"))
        {
            throw new ArgumentException(
                "Smart Dictionary cache ranking pipeline must be cross, bi+cross " +
                "or consec-independent.",
                nameof(request));
        }

        bool consec = rankingPipeline == "consec-independent";
        bool exactText = !IsPinnedGlossBertModel(request.ModelId);
        StringBuilder canonical = new();
        // Preserve the existing pinned GlossBERT key space. Earlier builds
        // applied BERT clean_text/lowercase to every model; non-BERT profiles
        // get a new exact domain, making those old entries harmless misses.
        AppendComponent(
            canonical,
            exactText
                ? "asteria-smart-dictionary-cache-key-v5-model-exact"
                : "asteria-smart-dictionary-cache-key-v4");
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

        if (exactText)
        {
            ExactMarkedContext exactContext = consec
                ? CreateConsecExactContext(
                    request.Context,
                    request.Target,
                    request.CharacterIndex)
                : CreateExactMarkedContext(
                    request.Context,
                    request.Target,
                    request.CharacterIndex);
            AppendComponent(canonical, "pipeline");
            AppendComponent(canonical, rankingPipeline);
            AppendComponent(canonical, exactContext.MarkedContext);
            AppendComponent(canonical, PreserveExact(request.Target));
            AppendComponent(canonical, exactContext.Anchored ? "anchored" : "raw");
            if (consec || !exactContext.Anchored)
            {
                AppendComponent(
                    canonical,
                    exactContext.CharacterIndex.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
            }

            // These identifiers are validated through their normalized forms
            // above, but their exact spelling is retained for the experimental
            // tokenizer's fail-safe key contract.
            AppendComponent(canonical, PreserveExact(request.SourceLanguage));
            AppendComponent(canonical, PreserveExact(request.Backend));
            AppendComponent(canonical, PreserveExact(request.ModelId));
            AppendComponent(canonical, PreserveExact(request.RankingPipeline));
            AppendComponent(canonical, PreserveExact(request.DictionaryVersion));
            AppendComponent(
                canonical,
                request.Candidates.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            for (int index = 0; index < request.Candidates.Count; index++)
            {
                AppendComponent(
                    canonical,
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AppendComponent(canonical, PreserveExact(request.Candidates[index]));
            }
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new NormalizedRequest(Convert.ToHexString(digest), candidates);
    }

    private static bool EntryMatchesCandidates(PersistedEntry entry, IReadOnlyList<string> candidates) =>
        entry.Index >= 0 &&
        entry.Index < candidates.Count &&
        CandidateMatchesTranslation(
            candidates[entry.Index],
            NormalizeCandidate(entry.Translation));

    // Register labels are part of candidate identity and therefore of the key,
    // but they are not part of the Russian surface shown to the user. Keeping
    // those two representations separate lets a labelled sense be cached
    // without either losing its identity or leaking the label into the card.
    private static bool CandidateMatchesTranslation(string candidate, string translation) =>
        string.Equals(candidate, translation, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            RemoveRegisterLabel(candidate),
            translation,
            StringComparison.OrdinalIgnoreCase);

    private static string RemoveRegisterLabel(string value)
    {
        foreach (SenseRegister register in Enum.GetValues<SenseRegister>())
        {
            string? label = SenseRegisters.DisplayLabel(register);
            if (label is null)
            {
                continue;
            }

            string suffix = " (" + label + ")";
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return value[..^suffix.Length];
            }
        }

        return value;
    }

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

    private static string PreserveExact(string? value) =>
        RankerCacheTextNormalizer.PreserveExact(value);

    private static ExactMarkedContext CreateExactMarkedContext(
        string context,
        string target,
        int characterIndex)
    {
        TargetCenteredContext centered = TargetCenteredContextBuilder.Build(
            context ?? string.Empty,
            target ?? string.Empty,
            characterIndex);
        bool anchored = RankerTargetAnchor.TryBuildMarkedContext(
            centered.Text,
            target ?? string.Empty,
            centered.CharacterIndex,
            out string markedContext);
        return new ExactMarkedContext(
            PreserveExact(markedContext),
            centered.CharacterIndex,
            anchored);
    }

    private static ExactMarkedContext CreateConsecExactContext(
        string context,
        string target,
        int characterIndex)
    {
        TargetCenteredContext centered = TargetCenteredContextBuilder.PreserveFull(
            context ?? string.Empty,
            target ?? string.Empty,
            characterIndex);
        int anchor = RankerTargetAnchor.FindClosestOccurrence(
            centered.Text,
            target ?? string.Empty,
            centered.CharacterIndex);
        return new ExactMarkedContext(
            PreserveExact(centered.Text),
            anchor >= 0 ? anchor : centered.CharacterIndex,
            anchor >= 0);
    }

    private static bool IsPinnedGlossBertModel(string? modelIdentity) =>
        modelIdentity?.StartsWith(
            GlossBertDictionarySelectorService.ExpectedModelVersionPrefix,
            StringComparison.Ordinal) == true;

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

    private readonly record struct ExactMarkedContext(
        string MarkedContext,
        int CharacterIndex,
        bool Anchored);

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
