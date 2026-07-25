using System.Collections.Concurrent;
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
    internal const string SenseRankingEpoch = "smart-sense-v8-bicross";

    private const int SchemaVersion = 1;
    private const int DefaultCapacity = 4_096;
    private const long DefaultMaximumFileBytes = 4L * 1024 * 1024;
    private const int MaximumStoredTextLength = 256;
    private static readonly TimeSpan PersistenceDebounce = TimeSpan.FromMilliseconds(650);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cachePath;
    private readonly int _capacity;
    private readonly long _maximumFileBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _fileLock;
    private readonly Dictionary<string, PersistedEntry> _entries = new(StringComparer.Ordinal);
    private long _accessSequence;
    private bool _loaded;
    private bool _dirty;
    private bool _disposed;
    private Task? _backgroundPersistTask;
    private long _mutationVersion;
    private long _lastCommittedVersion;

    public GlossBertDecisionCache(
        string? cachePath = null,
        int capacity = DefaultCapacity,
        long maximumFileBytes = DefaultMaximumFileBytes)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (maximumFileBytes < 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        _cachePath = Path.GetFullPath(cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AsteriaScale",
            "glossbert-decisions.v1.json"));
        _capacity = capacity;
        _maximumFileBytes = maximumFileBytes;
        _fileLock = FileLocks.GetOrAdd(_cachePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string CachePath => _cachePath;

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
    /// </summary>
    internal static string CreateFingerprint(
        string sentence,
        string target,
        int characterIndex,
        SmartDictionaryRankerPlan plan,
        GlossBertRankerProvider provider,
        string modelIdentity,
        string dictionaryVersion,
        string abstentionTag)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(abstentionTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryVersion);

        string normalizedSentence = NormalizeText(sentence);
        string normalizedTarget = NormalizeText(target);
        int anchor = RankerTargetAnchor.FindClosestOccurrence(
            normalizedSentence,
            normalizedTarget,
            characterIndex);

        StringBuilder canonical = new();
        Append(canonical, "asteria-glossbert-decision-cache-v1");
        Append(canonical, RankerProtocolV2.Version);
        Append(canonical, SenseRankingEpoch);
        // A cached verdict is only "what the ranker answered under this
        // threshold". Abstentions are cached, so a recalibration that should
        // turn them into selections must not keep reading the old answers.
        Append(canonical, abstentionTag);
        Append(canonical, provider.ToString());
        Append(canonical, modelIdentity);
        Append(canonical, dictionaryVersion);
        Append(canonical, normalizedSentence);
        Append(canonical, normalizedTarget);
        // A target that does not occur as a standalone token has no shared
        // anchor, so the raw index stays in the key instead of merging unrelated
        // requests onto one entry.
        Append(
            canonical,
            (anchor >= 0 ? anchor : characterIndex).ToString(
                CultureInfo.InvariantCulture));
        Append(canonical, anchor >= 0 ? "anchored" : "raw");
        foreach (SmartDictionaryRankerOutcome outcome in plan.Outcomes)
        {
            Append(canonical, "outcome");
            Append(canonical, outcome.GroupId.ToString(CultureInfo.InvariantCulture));
            Append(canonical, outcome.SourceIndex.ToString(CultureInfo.InvariantCulture));
            Append(canonical, NormalizeText(outcome.Translation));
        }

        for (int index = 0; index < plan.CandidateGlosses.Count; index++)
        {
            Append(canonical, "candidate");
            Append(canonical, NormalizeText(plan.CandidateGlosses[index]));
            Append(
                canonical,
                plan.CandidateGroupIds[index].ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>
    /// Returns a stored decision, or null on a miss. A selected GroupId that is
    /// no longer addressable in the current plan is dropped rather than served:
    /// GroupId is an index into the caller's outcomes (invariant 2).
    /// </summary>
    public async Task<GlossBertDecision?> TryGetAsync(
        string fingerprint,
        int outcomeCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken);
            if (!_entries.TryGetValue(fingerprint, out PersistedEntry? entry))
            {
                return null;
            }

            if (!entry.Abstained &&
                (entry.SelectedGroupId is not { } groupId ||
                 groupId < 0 ||
                 groupId >= outcomeCount))
            {
                _entries.Remove(fingerprint);
                _dirty = true;
                _mutationVersion++;
                return null;
            }

            entry.LastAccess = NextAccessSequenceLocked();
            _dirty = true;
            _mutationVersion++;
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
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stores a completed decision. Returns false for a structurally impossible
    /// verdict instead of persisting it; errors are never stored by contract.
    /// </summary>
    public async Task<bool> StoreAsync(
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
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken);
            candidate.LastAccess = NextAccessSequenceLocked();
            _entries[fingerprint] = candidate;
            _dirty = true;
            _mutationVersion++;
            EvictToCapacityLocked();
            SchedulePersistLocked();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken);
            _entries.Clear();
            _dirty = true;
            _mutationVersion++;
            await PersistLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_loaded && _dirty)
            {
                await PersistLockedAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal int Count
    {
        get
        {
            _gate.Wait();
            try
            {
                return _entries.Count;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task EnsureLoadedLockedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_cachePath))
            {
                _loaded = true;
                return;
            }

            FileInfo file = new(_cachePath);
            if (file.Length > _maximumFileBytes)
            {
                _loaded = true;
                _dirty = true;
                _mutationVersion++;
                return;
            }

            try
            {
                await using FileStream stream = new(
                    _cachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                CacheDocument? document =
                    await JsonSerializer.DeserializeAsync<CacheDocument>(
                        stream,
                        JsonOptions,
                        cancellationToken);
                if (document?.Version == SchemaVersion)
                {
                    foreach (PersistedEntry entry in document.Entries ?? [])
                    {
                        if (!IsStructurallyValid(entry))
                        {
                            continue;
                        }

                        if (!_entries.TryGetValue(entry.Key, out PersistedEntry? existing) ||
                            entry.LastAccess > existing.LastAccess)
                        {
                            _entries[entry.Key] = entry;
                            _accessSequence = Math.Max(_accessSequence, entry.LastAccess);
                        }
                    }
                }
            }
            catch (Exception ex) when (
                ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A partial or corrupt file is disposable optimization state.
                _entries.Clear();
                _accessSequence = 0;
                _dirty = true;
                _mutationVersion++;
            }

            EvictToCapacityLocked();
            _loaded = true;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private void SchedulePersistLocked()
    {
        if (_backgroundPersistTask is { IsCompleted: false })
        {
            return;
        }

        _backgroundPersistTask = Task.Run(PersistAfterDebounceAsync);
    }

    private async Task PersistAfterDebounceAsync()
    {
        await Task.Delay(PersistenceDebounce);
        CacheDocument? snapshot = null;
        long snapshotVersion = 0;
        bool persistenceFailed = false;
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                _backgroundPersistTask = null;
                return;
            }

            if (_loaded && _dirty)
            {
                snapshot = CreateSnapshotLocked();
                snapshotVersion = _mutationVersion;
            }
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            if (snapshot is not null)
            {
                byte[] payload = SerializeWithinBudget(snapshot);
                await PersistPayloadAsync(payload, snapshotVersion, CancellationToken.None);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Persistence is an optimization: keep the in-memory entries and try
            // again on the next store instead of delaying or failing a hover.
            System.Diagnostics.Debug.WriteLine(
                "GlossBERT decision cache background write failed: " + ex.Message);
            persistenceFailed = true;
        }

        await _gate.WaitAsync();
        try
        {
            if (snapshot is not null && snapshotVersion == _mutationVersion)
            {
                _dirty = false;
            }

            _backgroundPersistTask = null;
            if (_dirty && !_disposed && !persistenceFailed)
            {
                SchedulePersistLocked();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistLockedAsync(CancellationToken cancellationToken)
    {
        if (!_dirty)
        {
            return;
        }

        byte[] payload = SerializeWithinBudget(CreateSnapshotLocked());
        await PersistPayloadAsync(payload, _mutationVersion, cancellationToken);
        _dirty = false;
    }

    private async Task PersistPayloadAsync(
        byte[] payload,
        long version,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_cachePath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (version < _lastCommittedVersion)
            {
                return;
            }

            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(payload, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, _cachePath, overwrite: true);
                _lastCommittedVersion = version;
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // The completed destination is already durable.
                }
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private CacheDocument CreateSnapshotLocked() =>
        new()
        {
            Version = SchemaVersion,
            Entries = _entries.Values
                .OrderByDescending(entry => entry.LastAccess)
                .Select(entry => new PersistedEntry
                {
                    Key = entry.Key,
                    SelectedGroupId = entry.SelectedGroupId,
                    Abstained = entry.Abstained,
                    Margin = entry.Margin,
                    Score = entry.Score,
                    Confidence = entry.Confidence,
                    ExecutionProvider = entry.ExecutionProvider,
                    Device = entry.Device,
                    ModelVersion = entry.ModelVersion,
                    Pipeline = entry.Pipeline,
                    LastAccess = entry.LastAccess
                })
                .ToList()
        };

    private byte[] SerializeWithinBudget(CacheDocument snapshot)
    {
        List<PersistedEntry> entries = snapshot.Entries ?? [];
        while (true)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            if (payload.LongLength <= _maximumFileBytes || entries.Count == 0)
            {
                return payload;
            }

            entries.RemoveAt(entries.Count - 1);
        }
    }

    private void EvictToCapacityLocked()
    {
        while (_entries.Count > _capacity)
        {
            string? oldest = _entries.MinBy(pair => pair.Value.LastAccess).Key;
            if (oldest is null)
            {
                return;
            }

            _entries.Remove(oldest);
            _dirty = true;
        }
    }

    private long NextAccessSequenceLocked()
    {
        if (_accessSequence == long.MaxValue)
        {
            long sequence = 0;
            foreach (PersistedEntry entry in _entries.Values.OrderBy(entry => entry.LastAccess))
            {
                entry.LastAccess = ++sequence;
            }

            _accessSequence = sequence;
        }

        return ++_accessSequence;
    }

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

    private static string NormalizeText(string? value) =>
        value is null
            ? string.Empty
            : string.Join(
                " ",
                value.Normalize(NormalizationForm.FormKC)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        Task? pendingPersistence = null;
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            if (_loaded && _dirty)
            {
                await PersistLockedAsync(CancellationToken.None);
            }

            _disposed = true;
            pendingPersistence = _backgroundPersistTask;
        }
        finally
        {
            _gate.Release();
        }

        if (pendingPersistence is not null)
        {
            await pendingPersistence;
        }

        // The semaphore stays alive on purpose: a request queued before disposal
        // must still acquire it, observe _disposed and release without racing an
        // ObjectDisposedException.
    }

    private sealed class CacheDocument
    {
        public int Version { get; set; }
        public List<PersistedEntry>? Entries { get; set; }
    }

    private sealed class PersistedEntry
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
