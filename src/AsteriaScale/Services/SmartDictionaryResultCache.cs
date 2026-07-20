using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    string DictionaryVersion);

/// <summary>
/// A small persistent LRU for completed Smart Dictionary selections.
/// Cache keys are SHA-256 fingerprints, so source dialogue is never written to disk.
/// </summary>
public sealed partial class SmartDictionaryResultCache : IAsyncDisposable
{
    internal const string EngineLabel = "NPU OCR  •  SMART-CACHE";

    private const int SchemaVersion = 2;
    private const int MaximumCandidates = 128;
    private const int ContextWordRadius = 5;
    private const int ContextCharacterRadius = 96;
    private const int MaximumContextCharacters = 384;
    private const int MaximumStoredTextLength = 1_024;
    private const int DefaultCapacity = 4_096;
    private const long DefaultMaximumFileBytes = 4L * 1024 * 1024;
    private static readonly TimeSpan PersistenceDebounce = TimeSpan.FromMilliseconds(650);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

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

    public SmartDictionaryResultCache(
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
            "smart-dictionary-cache.v2.json"));
        _capacity = capacity;
        _maximumFileBytes = maximumFileBytes;
        _fileLock = FileLocks.GetOrAdd(_cachePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string CachePath => _cachePath;

    /// <summary>
    /// Returns a cached result only when its index and translation still match the
    /// candidates supplied by the current dictionary lookup.
    /// </summary>
    public async Task<SmartDictionarySelection?> TryGetAsync(
        SmartDictionaryCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        NormalizedRequest normalized = NormalizeRequest(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken);
            if (!_entries.TryGetValue(normalized.Key, out PersistedEntry? entry))
            {
                return null;
            }

            if (!EntryMatchesCandidates(entry, normalized.Candidates))
            {
                _entries.Remove(normalized.Key);
                _dirty = true;
                _mutationVersion++;
                return null;
            }

            entry.LastAccess = NextAccessSequence();
            _dirty = true;
            _mutationVersion++;
            return new SmartDictionarySelection(
                entry.Index,
                normalized.Candidates[entry.Index],
                entry.Confidence,
                entry.ContextTranslation);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stores a completed selection. Returns false instead of caching an answer
    /// that is not one of the current dictionary candidates.
    /// </summary>
    public async Task<bool> StoreAsync(
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
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken);
            _entries[normalized.Key] = new PersistedEntry
            {
                Key = normalized.Key,
                Index = selection.Index,
                Translation = normalized.Candidates[selection.Index],
                Confidence = NormalizeConfidence(selection.Confidence),
                ContextTranslation = Truncate(NormalizeText(selection.ContextTranslation)),
                LastAccess = NextAccessSequence()
            };
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

    /// <summary>
    /// Persists pending entries and LRU touches immediately. Normal stores are
    /// debounced so disk I/O never remains in the hover critical path.
    /// </summary>
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

    internal static string CreateKeyFingerprint(SmartDictionaryCacheRequest request) =>
        NormalizeRequest(request).Key;

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
        int occurrence = FindClosestOccurrence(context, rawTarget, characterIndex);
        int anchor = occurrence >= 0
            ? occurrence
            : Math.Clamp(characterIndex, 0, Math.Max(0, context.Length - 1));
        int targetLength = occurrence >= 0 ? rawTarget.Length : 0;

        MatchCollection words = WordTokenRegex().Matches(context);
        int start;
        int end;
        if (words.Count > 1)
        {
            int anchorWord = Enumerable.Range(0, words.Count)
                .MinBy(index => DistanceToMatch(words[index], anchor));
            int firstWord = Math.Max(0, anchorWord - ContextWordRadius);
            int lastWord = Math.Min(words.Count - 1, anchorWord + ContextWordRadius);
            start = words[firstWord].Index;
            end = words[lastWord].Index + words[lastWord].Length;
        }
        else
        {
            start = Math.Max(0, anchor - ContextCharacterRadius);
            end = Math.Min(context.Length, anchor + Math.Max(1, targetLength) + ContextCharacterRadius);
        }

        if (end - start > MaximumContextCharacters)
        {
            int desiredStart = Math.Max(start, anchor - MaximumContextCharacters / 2);
            start = Math.Min(desiredStart, end - MaximumContextCharacters);
            end = start + MaximumContextCharacters;
        }

        string window = context[start..end];
        int localTarget = occurrence - start;
        if (targetLength > 0 &&
            localTarget >= 0 &&
            localTarget + targetLength <= window.Length)
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

        StringBuilder canonical = new();
        AppendComponent(canonical, "asteria-smart-dictionary-cache-key-v2");
        AppendComponent(canonical, NormalizeLanguage(request.SourceLanguage));
        AppendComponent(canonical, CreateLocalContextWindow(
            request.Context,
            request.Target,
            request.CharacterIndex));
        AppendComponent(canonical, target);
        AppendComponent(canonical, backend);
        AppendComponent(canonical, modelId);
        AppendComponent(canonical, dictionaryVersion);
        AppendComponent(canonical, candidates.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string candidate in candidates)
        {
            AppendComponent(canonical, candidate.ToLowerInvariant());
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new NormalizedRequest(Convert.ToHexString(digest), candidates);
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
                CacheDocument? document = await JsonSerializer.DeserializeAsync<CacheDocument>(
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
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A partial/corrupt cache is disposable optimization state. Start clean.
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
                byte[] payload = SerializeSnapshotWithinBudget(snapshot);
                await PersistPayloadAsync(
                    payload,
                    snapshotVersion,
                    CancellationToken.None);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Cache persistence is an optimization. Keep the in-memory entry and
            // retry on the next store/flush instead of delaying or failing hover.
            System.Diagnostics.Debug.WriteLine(
                "Smart Dictionary cache background write failed: " + ex.Message);
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

        byte[] payload = SerializeWithinBudgetLocked();
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
                    Index = entry.Index,
                    Translation = entry.Translation,
                    Confidence = entry.Confidence,
                    ContextTranslation = entry.ContextTranslation,
                    LastAccess = entry.LastAccess
                })
                .ToList()
        };

    private byte[] SerializeSnapshotWithinBudget(CacheDocument snapshot)
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

    private byte[] SerializeWithinBudgetLocked()
    {
        while (true)
        {
            CacheDocument document = new()
            {
                Version = SchemaVersion,
                Entries = _entries.Values
                    .OrderByDescending(entry => entry.LastAccess)
                    .ToList()
            };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (payload.LongLength <= _maximumFileBytes || _entries.Count == 0)
            {
                return payload;
            }

            RemoveLeastRecentlyUsedLocked();
        }
    }

    private void EvictToCapacityLocked()
    {
        while (_entries.Count > _capacity)
        {
            RemoveLeastRecentlyUsedLocked();
        }
    }

    private void RemoveLeastRecentlyUsedLocked()
    {
        string? oldest = _entries
            .MinBy(pair => pair.Value.LastAccess)
            .Key;
        if (oldest is not null)
        {
            _entries.Remove(oldest);
            _dirty = true;
        }
    }

    private long NextAccessSequence()
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

    private static int FindClosestOccurrence(string context, string target, int characterIndex)
    {
        if (target.Length == 0 || target.Length > context.Length)
        {
            return -1;
        }

        int closest = -1;
        int closestDistance = int.MaxValue;
        for (int start = 0; start <= context.Length - target.Length;)
        {
            int index = context.IndexOf(target, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            int distance = Math.Abs(index - characterIndex);
            if (distance < closestDistance)
            {
                closest = index;
                closestDistance = distance;
            }

            start = index + Math.Max(1, target.Length);
        }

        return closest;
    }

    private static int DistanceToMatch(Match match, int anchor)
    {
        if (anchor < match.Index)
        {
            return match.Index - anchor;
        }

        int end = match.Index + match.Length;
        return anchor <= end ? 0 : anchor - end;
    }

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

    private static string NormalizeText(string? value) =>
        value is null
            ? string.Empty
            : WhitespaceRegex().Replace(value.Normalize(NormalizationForm.FormKC), " ").Trim();

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

        // Do not dispose the semaphore: a request that was already queued before
        // disposal must still be able to acquire it, observe _disposed and release
        // it without racing ObjectDisposedException.
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        @"[\p{L}\p{M}\p{N}]+(?:['’\-][\p{L}\p{M}\p{N}]+)*",
        RegexOptions.CultureInvariant)]
    private static partial Regex WordTokenRegex();

    private sealed record NormalizedRequest(string Key, string[] Candidates);

    private sealed class CacheDocument
    {
        public int Version { get; set; }
        public List<PersistedEntry>? Entries { get; set; }
    }

    private sealed class PersistedEntry
    {
        public string Key { get; set; } = string.Empty;
        public int Index { get; set; }
        public string Translation { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string ContextTranslation { get; set; } = string.Empty;
        public long LastAccess { get; set; }
    }
}
