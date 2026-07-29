using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AsteriaScale.RankerProtocol;

namespace AsteriaScale.Services;

/// <summary>
/// One completed GlossBERT verdict. Abstentions are cached too: a calibrated
/// margin-only abstention is as deterministic as a selection, and recomputing
/// it costs a full inference.
/// </summary>
public sealed record GlossBertDecision(
    int? SelectedGroupId,
    bool Abstained,
    double? Margin,
    double? Score,
    double? Confidence,
    string ExecutionProvider,
    string Device,
    string ModelVersion,
    string Pipeline);

/// <summary>
/// A persistent LRU shared by both consumers of the GlossBERT worker: the hover
/// Smart Dictionary and the full-text translation scaffold. Both ask the worker
/// the same question with the same arguments, so a decision earned by one must
/// be reusable by the other and must survive a restart.
///
/// The key is a SHA-256 fingerprint of exactly what reaches the worker, so no
/// source dialogue is written to disk. It is deliberately *not* the hover
/// context window used by <see cref="SmartDictionaryResultCache"/>: that one
/// trades precision for hit rate, while this one must never return a verdict
/// the worker did not produce for this input.
/// </summary>
public sealed class GlossBertDecisionCache : IAsyncDisposable
{
    /// <summary>
    /// Epoch of the deterministic sense-resolution stages that run before the
    /// worker (grouping, plan construction, surface selection). Both consumers
    /// share those stages, so the marker is defined once here and folded into
    /// the fingerprint itself: a caller cannot forget it, and the two paths
    /// cannot drift onto different keys for the same worker call.
    /// </summary>
    internal const string SenseRankingEpoch = "smart-sense-v9-semantic-rankers";

    // 3: key text normalization now mirrors the pinned BertNormalizer clean_text
    // stage. Old entries may have merged controls that BERT deletes with spaces
    // that BERT preserves, so they must be discarded rather than reused.
    private const int SchemaVersion = 4;
    private const int DefaultCapacity = 4_096;
    private const long DefaultMaximumFileBytes = 4L * 1024 * 1024;
    private const int MaximumStoredTextLength = 256;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly PersistentLruStore<PersistedEntry> _store;

    public GlossBertDecisionCache(
        string? cachePath = null,
        int capacity = DefaultCapacity,
        long maximumFileBytes = DefaultMaximumFileBytes)
    {
        _store = new PersistentLruStore<PersistedEntry>(
            cachePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AsteriaScale",
                "glossbert-decisions.v1.json"),
            SchemaVersion,
            capacity,
            maximumFileBytes,
            IsStructurallyValid,
            JsonOptions);
    }

    public string CachePath => _store.CachePath;

    /// <summary>
    /// Builds the shared decision key. <paramref name="characterIndex"/> is
    /// canonicalized to the target occurrence the worker would anchor, using the
    /// worker's own resolution: the hover path passes a cursor position and the
    /// scaffold passes a token start, and both must reach the same entry.
    /// </summary>
    internal static string CreateFingerprint(
        string sentence,
        string target,
        int characterIndex,
        SmartDictionaryRankerPlan plan,
        GlossBertRankerProvider provider,
        string modelIdentity,
        string dictionaryVersion) =>
        CreateFingerprint(
            sentence,
            target,
            characterIndex,
            plan,
            provider,
            modelIdentity,
            dictionaryVersion,
            RankerAbstention.CacheTag);

    /// <summary>
    /// Threshold-parameterized form. Production callers must use the overload
    /// above so the frozen threshold cannot be forgotten; this one exists so a
    /// test can prove that recalibrating it separates the key space.
    /// <paramref name="enrichmentEnabled"/> exists for the same reason: it lets
    /// a test prove the key follows the gloss rendering while roadmap #8
    /// candidate 5 is off, so reopening it cannot silently reuse old verdicts.
    /// </summary>
    internal static string CreateFingerprint(
        string sentence,
        string target,
        int characterIndex,
        SmartDictionaryRankerPlan plan,
        GlossBertRankerProvider provider,
        string modelIdentity,
        string dictionaryVersion,
        string abstentionTag,
        bool enrichmentEnabled = SmartDictionaryRankerPlan.EnrichmentEnabled)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(abstentionTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryVersion);

        // Resolve and mark on the raw UTF-16 input, exactly as the worker does,
        // before clean_text removes controls or collapses whitespace. Applying
        // the original cursor after normalization can select a different one of
        // two repeated targets and reuse a verdict for the wrong occurrence.
        bool sandwich = IsSandwichModel(modelIdentity);
        bool consec = IsConsecModel(modelIdentity);
        bool exactText = !IsPinnedGlossBertModel(modelIdentity);
        RankerTargetMarker marker = sandwich
            ? RankerTargetMarker.SandwichD
            : RankerTargetMarker.Quoted;
        int consecAnchor = consec
            ? RankerTargetAnchor.FindClosestOccurrence(sentence, target, characterIndex)
            : -1;
        bool anchored;
        string markedSentence;
        if (consec)
        {
            anchored = consecAnchor >= 0;
            markedSentence = sentence;
        }
        else
        {
            anchored = RankerTargetAnchor.TryBuildMarkedContext(
                sentence,
                target,
                characterIndex,
                marker,
                out markedSentence);
        }
        string fingerprintSentence = exactText
            ? PreserveExact(anchored ? markedSentence : sentence)
            : NormalizeText(anchored ? markedSentence : sentence);
        string fingerprintTarget = exactText
            ? PreserveExact(target)
            : NormalizeText(target);

        StringBuilder canonical = new();
        // Keep the current pinned GlossBERT key domain byte-for-byte stable
        // within this sense-ranking epoch. The epoch/schema migration already
        // invalidates older releases globally; this branch avoids an additional
        // unnecessary invalidation for current GlossBERT entries. Every other
        // tokenizer gets a new exact domain and cannot read unsafe BERT-cleaned
        // entries written by an earlier build.
        Append(
            canonical,
            exactText
                ? "asteria-semantic-ranker-decision-cache-v5-model-exact"
                : "asteria-semantic-ranker-decision-cache-v4");
        Append(canonical, RankerProtocolV2.Version);
        Append(canonical, SenseRankingEpoch);
        // A cached verdict is only "what the ranker answered under this
        // threshold". Abstentions are cached, so a recalibration that should
        // turn them into selections must not keep reading the old answers.
        Append(canonical, abstentionTag);
        Append(canonical, provider.ToString());
        Append(canonical, modelIdentity);
        Append(canonical, dictionaryVersion);
        if (exactText)
        {
            // Pipeline identity is explicit even though the pinned model id
            // also carries it. A future artifact cannot silently reuse a
            // verdict after changing the adapter around the same weights.
            Append(canonical, "pipeline");
            Append(canonical, ResolvePipelineIdentity(modelIdentity));
        }

        Append(canonical, fingerprintSentence);
        Append(canonical, fingerprintTarget);
        // ConSeC keeps the source sentence unmodified and represents the target
        // span in its token tensors, so its resolved anchor must be explicit.
        // Other models carry the occurrence in their synthetic input markers.
        Append(canonical, anchored ? "anchored" : "raw");
        if (consec && anchored)
        {
            Append(canonical, consecAnchor.ToString(CultureInfo.InvariantCulture));
        }
        else if (!anchored)
        {
            Append(canonical, characterIndex.ToString(CultureInfo.InvariantCulture));
        }
        foreach (SmartDictionaryRankerOutcome outcome in plan.Outcomes)
        {
            Append(canonical, "outcome");
            Append(canonical, outcome.GroupId.ToString(CultureInfo.InvariantCulture));
            Append(canonical, outcome.SourceIndex.ToString(CultureInfo.InvariantCulture));
            Append(
                canonical,
                exactText
                    ? PreserveExact(outcome.Translation)
                    : NormalizeText(outcome.Translation));
        }

        // The key must describe the glosses the worker actually received. A plan
        // can render them per provider (roadmap #8 candidate 5 appends a hint
        // where the sequence budget allows), so keying the unrendered glosses
        // would serve a verdict computed from different input.
        IReadOnlyList<string> glosses = plan.GlossesFor(
            provider == GlossBertRankerProvider.Cpu,
            enrichmentEnabled);
        for (int index = 0; index < glosses.Count; index++)
        {
            Append(canonical, "candidate");
            Append(
                canonical,
                exactText
                    ? PreserveExact(glosses[index])
                    : NormalizeText(glosses[index]));
            Append(
                canonical,
                plan.CandidateGroupIds[index].ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>
    /// Chooses the storage key from the model identity reported by the response.
    /// A worker can lose its bi-encoder after warm-up and answer cross-only; that
    /// verdict must not be filed under the earlier bi+cross lookup identity.
    /// </summary>
    internal static string ResolveStorageFingerprint(
        string lookupFingerprint,
        string lookupModelIdentity,
        string resultModelIdentity,
        Func<string, string> createFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lookupFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(lookupModelIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultModelIdentity);
        ArgumentNullException.ThrowIfNull(createFingerprint);

        return string.Equals(
            lookupModelIdentity,
            resultModelIdentity,
            StringComparison.Ordinal)
            ? lookupFingerprint
            : createFingerprint(resultModelIdentity);
    }

    /// <summary>
    /// Returns a stored decision, or null on a miss. A selected GroupId that is
    /// no longer addressable in the current plan is dropped rather than served:
    /// GroupId is an index into the caller's outcomes (invariant 2).
    /// </summary>
    public Task<GlossBertDecision?> TryGetAsync(
        string fingerprint,
        int outcomeCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        return _store.UseAsync(access =>
        {
            if (!access.Entries.TryGetValue(fingerprint, out PersistedEntry? entry))
            {
                return null;
            }

            if (!entry.Abstained &&
                (entry.SelectedGroupId is not { } groupId ||
                 groupId < 0 ||
                 groupId >= outcomeCount))
            {
                access.Entries.Remove(fingerprint);
                access.MarkDirty();
                return null;
            }

            entry.LastAccess = access.Touch();
            access.MarkDirty();
            return new GlossBertDecision(
                entry.Abstained ? null : entry.SelectedGroupId,
                entry.Abstained,
                entry.Margin,
                entry.Score,
                entry.Confidence,
                entry.ExecutionProvider,
                entry.Device,
                entry.ModelVersion,
                entry.Pipeline);
        }, cancellationToken);
    }

    /// <summary>
    /// Stores a completed decision. Returns false for a structurally impossible
    /// verdict instead of persisting it; errors are never stored by contract.
    /// </summary>
    public Task<bool> StoreAsync(
        string fingerprint,
        GlossBertDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(decision);
        PersistedEntry candidate = new()
        {
            Key = fingerprint,
            SelectedGroupId = decision.Abstained ? null : decision.SelectedGroupId,
            Abstained = decision.Abstained,
            Margin = NormalizeNumber(decision.Margin),
            Score = NormalizeNumber(decision.Score),
            Confidence = NormalizeNumber(decision.Confidence),
            ExecutionProvider = Truncate(NormalizeText(decision.ExecutionProvider)),
            Device = Truncate(NormalizeText(decision.Device)),
            ModelVersion = Truncate(NormalizeText(decision.ModelVersion)),
            Pipeline = Truncate(NormalizeText(decision.Pipeline)),
            LastAccess = 0
        };
        if (!IsStructurallyValid(candidate))
        {
            return Task.FromResult(false);
        }

        return _store.UseAsync(access =>
        {
            candidate.LastAccess = access.Touch();
            access.Entries[fingerprint] = candidate;
            access.MarkDirty();
            return true;
        }, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        _store.ClearAsync(cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        _store.FlushAsync(cancellationToken);

    internal int Count =>
        _store.UseAsync(access => access.Entries.Count).GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => _store.DisposeAsync();

    private static bool IsStructurallyValid(PersistedEntry entry) =>
        entry.Key.Length == 64 &&
        entry.Key.All(Uri.IsHexDigit) &&
        // An abstention has no group; a selection must address one.
        (entry.Abstained
            ? entry.SelectedGroupId is null
            : entry.SelectedGroupId is >= 0) &&
        entry.ModelVersion.Length > 0 &&
        entry.ModelVersion.Length <= MaximumStoredTextLength &&
        entry.ExecutionProvider.Length <= MaximumStoredTextLength &&
        entry.Device.Length <= MaximumStoredTextLength &&
        entry.Pipeline.Length <= MaximumStoredTextLength &&
        IsFiniteOrNull(entry.Margin) &&
        IsFiniteOrNull(entry.Score) &&
        IsFiniteOrNull(entry.Confidence) &&
        entry.LastAccess >= 0;

    private static bool IsFiniteOrNull(double? value) =>
        value is null || double.IsFinite(value.Value);

    private static double? NormalizeNumber(double? value) =>
        value is null || !double.IsFinite(value.Value) ? null : value;

    // FormC, not FormKC: compatibility folding merges inputs the model can tell
    // apart. The shared helper also distinguishes whitespace BERT maps to a
    // space from control characters BERT deletes entirely.
    private static string NormalizeText(string? value) =>
        RankerCacheTextNormalizer.Normalize(value);

    private static string PreserveExact(string? value) =>
        RankerCacheTextNormalizer.PreserveExact(value);

    private static bool IsSandwichModel(string modelIdentity) =>
        modelIdentity.StartsWith(
            GlossBertDictionarySelectorService.SandwichExpectedModelVersionPrefix,
            StringComparison.Ordinal);

    private static bool IsConsecModel(string modelIdentity) =>
        modelIdentity.StartsWith(
            GlossBertDictionarySelectorService.ConsecExpectedModelVersionPrefix,
            StringComparison.Ordinal);

    private static bool IsPinnedGlossBertModel(string modelIdentity) =>
        modelIdentity.StartsWith(
            GlossBertDictionarySelectorService.ExpectedModelVersionPrefix,
            StringComparison.Ordinal);

    private static string ResolvePipelineIdentity(string modelIdentity)
    {
        if (IsSandwichModel(modelIdentity))
        {
            return "sandwich";
        }

        if (IsConsecModel(modelIdentity))
        {
            return "consec-independent";
        }

        return modelIdentity.EndsWith(
            ":" + GlossBertDictionarySelectorService.CpuEmbeddingBundleCacheTag,
            StringComparison.Ordinal)
                ? "bi+cross"
                : modelIdentity.EndsWith(":pipeline-cross", StringComparison.Ordinal)
                    ? "cross"
                    : "model-bound";
    }

    private static string Truncate(string value) =>
        value.Length <= MaximumStoredTextLength
            ? value
            : value[..MaximumStoredTextLength];

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }

    private sealed class PersistedEntry : IPersistentLruEntry
    {
        public string Key { get; set; } = string.Empty;
        public int? SelectedGroupId { get; set; }
        public bool Abstained { get; set; }
        public double? Margin { get; set; }
        public double? Score { get; set; }
        public double? Confidence { get; set; }
        public string ExecutionProvider { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public string ModelVersion { get; set; } = string.Empty;
        public string Pipeline { get; set; } = string.Empty;
        public long LastAccess { get; set; }
    }
}
