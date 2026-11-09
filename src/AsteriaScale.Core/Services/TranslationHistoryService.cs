using System.IO;
using System.Text;
using System.Text.Json;

namespace AsteriaScale.Services;

public sealed record TranslationHistoryEntry(
    string Source,
    string Translation,
    string Engine,
    string Context,
    DateTimeOffset RecordedAt)
{
    public string Language { get; init; } = string.Empty;
}

/// <summary>
/// Bounded, persistent history of shown translations. It feeds the review
/// panel and the Anki export, and later the translation memory (roadmap #18).
/// Recording never blocks or fails the hover path: persistence is debounced and
/// every I/O error is swallowed.
/// </summary>
public sealed class TranslationHistoryService : IDisposable
{
    private const int Capacity = 200;
    private const int MaximumFieldLength = 1000;
    private const int SchemaVersion = 1;
    private static readonly TimeSpan DefaultFlushDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly string _path;
    private readonly TimeSpan _flushDelay;
    private readonly TimeSpan _retryDelay;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly LinkedList<TranslationHistoryEntry> _entries = new();
    private readonly List<TranslationHistoryEntry> _pendingEntries = [];
    private System.Threading.Timer? _flushTimer;
    private bool _loaded;
    private bool _dirty;
    private bool _disposing;
    private bool _disposed;
    private long _changeVersion;

    public TranslationHistoryService()
        : this(CoreDataLayout.Current.HistoryFile)
    {
    }

    internal TranslationHistoryService(
        string path,
        TimeSpan? flushDelay = null,
        TimeSpan? retryDelay = null)
    {
        _path = path;
        _flushDelay = flushDelay ?? DefaultFlushDelay;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    public string FilePath => _path;

    /// <summary>
    /// Records one shown translation. Consecutive duplicates of the same source
    /// are collapsed so re-hovering a line does not flood the list.
    /// </summary>
    public void Record(
        string source,
        string translation,
        string engine,
        string context = "",
        string language = "")
    {
        string normalizedSource = Truncate(Collapse(source));
        string normalizedTranslation = Truncate(Collapse(translation));
        if (normalizedSource.Length == 0 || normalizedTranslation.Length == 0)
        {
            return;
        }

        TranslationHistoryEntry entry = new(
            normalizedSource,
            normalizedTranslation,
            Truncate(Collapse(engine)),
            Truncate(Collapse(context)),
            DateTimeOffset.Now)
        {
            Language = NormalizeLanguage(language, normalizedSource)
        };

        lock (_gate)
        {
            if (_disposed || _disposing)
            {
                return;
            }

            bool loaded = EnsureLoadedLocked();
            if (IsDuplicateOfNewestLocked(entry))
            {
                return;
            }

            if (loaded)
            {
                AddFirstBoundedLocked(entry);
            }
            else
            {
                // Keep new hover results in memory until the old file can be
                // read. Writing an empty replacement after a transient read
                // failure would destroy the user's existing history.
                _pendingEntries.Add(entry);
                if (_pendingEntries.Count > Capacity)
                {
                    _pendingEntries.RemoveAt(0);
                }
            }

            _dirty = true;
            _changeVersion++;
            ScheduleFlushLocked(_flushDelay);
        }
    }

    public IReadOnlyList<TranslationHistoryEntry> Snapshot()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_loaded)
            {
                return _entries.ToArray();
            }

            return _pendingEntries.AsEnumerable().Reverse().Take(Capacity).ToArray();
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            if (_disposed || _disposing || !EnsureLoadedLocked())
            {
                return false;
            }

            _entries.Clear();
            _pendingEntries.Clear();
            _dirty = true;
            _changeVersion++;
        }

        return TryFlush();
    }

    public void Flush() => TryFlush();

    /// <summary>
    /// Writes a tab-separated file Anki imports directly: front, back, context.
    /// No network integration by design.
    /// </summary>
    public int ExportToAnki(string path)
    {
        // Snapshot is newest-first. Deduplicate before reversing so a corrected
        // translation wins while the exported deck remains chronological.
        TranslationHistoryEntry[] entries = Snapshot()
            .DistinctBy(entry => entry.Source, StringComparer.OrdinalIgnoreCase)
            .Reverse()
            .ToArray();
        if (entries.Length == 0)
        {
            return 0;
        }

        StringBuilder builder = new();
        foreach (TranslationHistoryEntry entry in entries)
        {
            builder
                .Append(Sanitize(entry.Source)).Append('\t')
                .Append(Sanitize(entry.Translation)).Append('\t')
                .AppendLine(Sanitize(entry.Context));
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        return entries.Length;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed || _disposing)
            {
                return;
            }

            _disposing = true;
        }

        Flush();
        lock (_gate)
        {
            _disposed = true;
            _disposing = false;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
    }

    private bool TryFlush()
    {
        _flushGate.Wait();
        try
        {
            TranslationHistoryEntry[] snapshot;
            long snapshotVersion;
            lock (_gate)
            {
                if (_disposed || !EnsureLoadedLocked())
                {
                    if (!_disposed && _dirty)
                    {
                        ScheduleFlushLocked(_retryDelay);
                    }

                    return false;
                }

                if (!_dirty)
                {
                    return true;
                }

                snapshot = _entries.ToArray();
                snapshotVersion = _changeVersion;
            }

            string? temporaryPath = null;
            try
            {
                string? directory = Path.GetDirectoryName(_path);
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }

                temporaryPath = _path + ".tmp." + Guid.NewGuid().ToString("N");
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(
                        new HistoryDocument { Version = SchemaVersion, Entries = snapshot },
                        JsonOptions),
                    new UTF8Encoding(false));
                AtomicFile.Replace(temporaryPath, _path);
                temporaryPath = null;

                lock (_gate)
                {
                    _dirty = _changeVersion != snapshotVersion;
                    if (_dirty)
                    {
                        ScheduleFlushLocked(_flushDelay);
                    }
                }

                return true;
            }
            catch (Exception exception) when (exception is IOException or
                                              UnauthorizedAccessException or
                                              JsonException)
            {
                lock (_gate)
                {
                    // A failed write never acknowledges the snapshot. The timer
                    // retries even if no later hover happens.
                    _dirty = true;
                    ScheduleFlushLocked(_retryDelay);
                }

                return false;
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (Exception exception) when (exception is IOException or
                                                      UnauthorizedAccessException)
                    {
                        // A stale unique temp file cannot replace the history.
                    }
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private bool EnsureLoadedLocked()
    {
        if (_loaded)
        {
            return true;
        }

        try
        {
            HistoryDocument? document = null;
            try
            {
                document = JsonSerializer.Deserialize<HistoryDocument>(
                    File.ReadAllText(_path),
                    JsonOptions);
                if (document?.Version != SchemaVersion || document.Entries is null)
                {
                    throw new JsonException("Unsupported translation history schema.");
                }
            }
            catch (FileNotFoundException)
            {
                // A genuinely absent history is a valid empty initial state.
            }
            catch (DirectoryNotFoundException)
            {
                // The application data directory is created on first flush.
            }
            catch (JsonException)
            {
                if (!TryQuarantineBrokenHistoryLocked())
                {
                    return false;
                }

                document = null;
            }

            _entries.Clear();
            if (document?.Entries is { } persistedEntries)
            {
                foreach (TranslationHistoryEntry entry in persistedEntries.Take(Capacity))
                {
                    if (!string.IsNullOrWhiteSpace(entry.Source) &&
                        !string.IsNullOrWhiteSpace(entry.Translation))
                    {
                        _entries.AddLast(entry);
                    }
                }
            }

            _loaded = true;
            if (_pendingEntries.Count > 0)
            {
                TranslationHistoryEntry[] pendingEntries = _pendingEntries.ToArray();
                _pendingEntries.Clear();
                foreach (TranslationHistoryEntry entry in pendingEntries)
                {
                    if (!IsDuplicateOfNewestLocked(entry))
                    {
                        AddFirstBoundedLocked(entry);
                    }
                }

                _dirty = true;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException)
        {
            // Keep _loaded false so a later Snapshot/Flush retries the read.
            return false;
        }
    }

    private bool TryQuarantineBrokenHistoryLocked()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return true;
            }

            string brokenPath = _path + ".broken-" +
                                DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                "-" + Guid.NewGuid().ToString("N");
            File.Move(_path, brokenPath);
            PruneQuarantinedHistories();
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException)
        {
            // A file that cannot be preserved must never be overwritten.
            return false;
        }
    }

    /// <summary>
    /// Keeps only the newest quarantined copies. Repeated corruption would
    /// otherwise grow an unbounded pile of dead files next to the live history,
    /// each holding the user's source text.
    /// </summary>
    private void PruneQuarantinedHistories()
    {
        const int retainedQuarantineCount = 3;
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            foreach (string stale in Directory
                         .EnumerateFiles(
                             directory,
                             Path.GetFileName(_path) + ".broken-*")
                         .OrderByDescending(path => path, StringComparer.Ordinal)
                         .Skip(retainedQuarantineCount))
            {
                File.Delete(stale);
            }
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException or
                                          DirectoryNotFoundException or
                                          ArgumentException)
        {
            // Housekeeping must never break loading the history itself.
        }
    }

    private void ScheduleFlushLocked(TimeSpan delay)
    {
        if (_disposed || _disposing)
        {
            return;
        }

        _flushTimer ??= new System.Threading.Timer(
            static state => ((TranslationHistoryService)state!).FlushFromTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _flushTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void FlushFromTimer()
    {
        try
        {
            TryFlush();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown won the race with the one-shot timer.
        }
    }

    private bool IsDuplicateOfNewestLocked(TranslationHistoryEntry entry)
    {
        TranslationHistoryEntry? newest = _pendingEntries.Count > 0
            ? _pendingEntries[^1]
            : _entries.First?.Value;
        return newest is not null &&
               string.Equals(newest.Source, entry.Source, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(newest.Translation, entry.Translation, StringComparison.Ordinal);
    }

    private void AddFirstBoundedLocked(TranslationHistoryEntry entry)
    {
        _entries.AddFirst(entry);
        while (_entries.Count > Capacity)
        {
            _entries.RemoveLast();
        }
    }

    private static string NormalizeLanguage(string value, string source)
    {
        if (string.Equals(value, "JA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "JPN", StringComparison.OrdinalIgnoreCase) ||
            LocalDictionaryService.NormalizeJapanese(source).Length > 0)
        {
            return "JA";
        }

        return "EN";
    }

    private static string Collapse(string? value) =>
        string.Join(
            " ",
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string value) =>
        value.Length <= MaximumFieldLength ? value : value[..MaximumFieldLength];

    // Tabs and newlines would break the Anki row layout.
    private static string Sanitize(string value) =>
        value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private sealed class HistoryDocument
    {
        public int Version { get; set; }
        public IReadOnlyList<TranslationHistoryEntry>? Entries { get; set; }
    }
}
