using System.Collections.Concurrent;
using System.Text.Json;

namespace AsteriaScale.Services;

/// <summary>
/// An entry a <see cref="PersistentLruStore{TEntry}"/> can track. The store
/// needs only these two: everything else about an entry belongs to its owner.
/// </summary>
internal interface IPersistentLruEntry
{
    string Key { get; set; }
    long LastAccess { get; set; }
}

/// <summary>
/// Guarded access to the store's entries. Handed to a caller only while the
/// gate is held, so the dictionary can be touched directly without each
/// consumer re-implementing the locking.
/// </summary>
internal readonly struct PersistentLruAccess<TEntry>
    where TEntry : class, IPersistentLruEntry
{
    private readonly PersistentLruStore<TEntry> _store;

    internal PersistentLruAccess(
        PersistentLruStore<TEntry> store,
        Dictionary<string, TEntry> entries)
    {
        _store = store;
        Entries = entries;
    }

    public Dictionary<string, TEntry> Entries { get; }

    /// <summary>Next LRU stamp. Call when an entry is read or written.</summary>
    public long Touch() => _store.NextAccessSequenceLocked();

    /// <summary>Marks the store for persistence after the callback returns.</summary>
    public void MarkDirty() => _store.MarkDirtyLocked();
}

/// <summary>
/// Disk-backed LRU shared by the dictionary result cache and the GlossBERT
/// decision cache (audit card A9).
///
/// Both previously carried their own copy of this machinery — load, debounce,
/// atomic replace, size budget, eviction, disposal — about 250 duplicated
/// lines. The duplication was deliberate at the time: extracting a base class
/// while simultaneously introducing the second cache would have changed a
/// tested component on the hover critical path in one step.
///
/// The on-disk shape is deliberately unchanged: the document still serialises
/// as <c>{"version": N, "entries": [...]}</c> with the owner's own entry type,
/// so caches users have already accumulated keep loading.
/// </summary>
internal sealed class PersistentLruStore<TEntry> : IAsyncDisposable
    where TEntry : class, IPersistentLruEntry
{
    private static readonly TimeSpan PersistenceDebounce = TimeSpan.FromMilliseconds(650);

    // One lock per path, not per instance: two stores over the same file must
    // not interleave their atomic replaces.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cachePath;
    private readonly int _schemaVersion;
    private readonly int _capacity;
    private readonly long _maximumFileBytes;
    private readonly Func<TEntry, bool> _isStructurallyValid;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _fileLock;
    private readonly Dictionary<string, TEntry> _entries = new(StringComparer.Ordinal);
    private long _accessSequence;
    private bool _loaded;
    private bool _dirty;
    private bool _disposed;
    private Task? _backgroundPersistTask;
    private long _mutationVersion;
    private long _lastCommittedVersion;

    public PersistentLruStore(
        string cachePath,
        int schemaVersion,
        int capacity,
        long maximumFileBytes,
        Func<TEntry, bool> isStructurallyValid,
        JsonSerializerOptions jsonOptions)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (maximumFileBytes < 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        _cachePath = Path.GetFullPath(cachePath);
        _schemaVersion = schemaVersion;
        _capacity = capacity;
        _maximumFileBytes = maximumFileBytes;
        _isStructurallyValid = isStructurallyValid;
        _jsonOptions = jsonOptions;
        _fileLock = FileLocks.GetOrAdd(_cachePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string CachePath => _cachePath;

    /// <summary>
    /// Runs <paramref name="body"/> against the loaded entries while holding the
    /// gate. If the body marked the store dirty, eviction and a debounced write
    /// are scheduled before the gate is released.
    /// </summary>
    public async Task<TResult> UseAsync<TResult>(
        Func<PersistentLruAccess<TEntry>, TResult> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken);
            bool dirtyBefore = _dirty;
            TResult result = body(new PersistentLruAccess<TEntry>(this, _entries));
            if (_dirty && !dirtyBefore)
            {
                EvictToCapacityLocked();
                SchedulePersistLocked();
            }

            return result;
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
            MarkDirtyLocked();
            await PersistLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Persists pending entries and LRU touches immediately. Ordinary writes are
    /// debounced so disk I/O never sits in the hover critical path.
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

    internal long NextAccessSequenceLocked()
    {
        if (_accessSequence == long.MaxValue)
        {
            long sequence = 0;
            foreach (TEntry entry in _entries.Values.OrderBy(entry => entry.LastAccess))
            {
                entry.LastAccess = ++sequence;
            }

            _accessSequence = sequence;
        }

        return ++_accessSequence;
    }

    internal void MarkDirtyLocked()
    {
        _dirty = true;
        _mutationVersion++;
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
                MarkDirtyLocked();
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
                        _jsonOptions,
                        cancellationToken);
                if (document?.Version == _schemaVersion)
                {
                    foreach (TEntry entry in document.Entries ?? [])
                    {
                        if (!_isStructurallyValid(entry))
                        {
                            continue;
                        }

                        if (!_entries.TryGetValue(entry.Key, out TEntry? existing) ||
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
                MarkDirtyLocked();
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
            // again on the next write instead of delaying or failing a hover.
            System.Diagnostics.Debug.WriteLine(
                "Persistent cache background write failed: " + ex.Message);
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
            Version = _schemaVersion,
            Entries = _entries.Values
                .OrderByDescending(entry => entry.LastAccess)
                .ToList()
        };

    private byte[] SerializeWithinBudget(CacheDocument snapshot)
    {
        List<TEntry> entries = snapshot.Entries ?? [];
        while (true)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, _jsonOptions);
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
        public List<TEntry>? Entries { get; set; }
    }
}
