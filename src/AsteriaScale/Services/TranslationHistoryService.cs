using System.IO;
using System.Text;
using System.Text.Json;

namespace AsteriaScale.Services;

public sealed record TranslationHistoryEntry(
    string Source,
    string Translation,
    string Engine,
    string Context,
    DateTimeOffset RecordedAt);

/// <summary>
/// Bounded, persistent history of shown translations. It feeds the review
/// panel and the Anki export, and later the translation memory (roadmap #18).
/// Recording never blocks or fails the hover path: persistence is debounced and
/// every I/O error is swallowed.
/// </summary>
public sealed class TranslationHistoryService
{
    private const int Capacity = 200;
    private const int MaximumFieldLength = 1000;
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly LinkedList<TranslationHistoryEntry> _entries = new();
    private bool _loaded;
    private bool _dirty;

    public TranslationHistoryService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AsteriaScale",
            "translation-history.v1.json"))
    {
    }

    internal TranslationHistoryService(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    /// <summary>
    /// Records one shown translation. Consecutive duplicates of the same source
    /// are collapsed so re-hovering a line does not flood the list.
    /// </summary>
    public void Record(string source, string translation, string engine, string context = "")
    {
        string normalizedSource = Truncate(Collapse(source));
        string normalizedTranslation = Truncate(Collapse(translation));
        if (normalizedSource.Length == 0 || normalizedTranslation.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_entries.First is { } newest &&
                string.Equals(newest.Value.Source, normalizedSource, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(newest.Value.Translation, normalizedTranslation, StringComparison.Ordinal))
            {
                return;
            }

            _entries.AddFirst(new TranslationHistoryEntry(
                normalizedSource,
                normalizedTranslation,
                Truncate(Collapse(engine)),
                Truncate(Collapse(context)),
                DateTimeOffset.Now));
            while (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }

            _dirty = true;
        }
    }

    public IReadOnlyList<TranslationHistoryEntry> Snapshot()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            return _entries.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            _entries.Clear();
            _dirty = true;
        }

        Flush();
    }

    public void Flush()
    {
        TranslationHistoryEntry[] snapshot;
        lock (_gate)
        {
            if (!_loaded || !_dirty)
            {
                return;
            }

            snapshot = _entries.ToArray();
            _dirty = false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = _path + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    new HistoryDocument { Version = SchemaVersion, Entries = snapshot },
                    JsonOptions),
                new UTF8Encoding(false));
            AtomicFile.Replace(temporaryPath, _path);
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException or
                                          JsonException)
        {
            // History is a convenience log; losing it must never affect translation.
        }
    }

    /// <summary>
    /// Writes a tab-separated file Anki imports directly: front, back, context.
    /// No network integration by design.
    /// </summary>
    public int ExportToAnki(string path)
    {
        TranslationHistoryEntry[] entries = Snapshot()
            .Reverse()
            .DistinctBy(entry => entry.Source, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private void EnsureLoadedLocked()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            HistoryDocument? document = JsonSerializer.Deserialize<HistoryDocument>(
                File.ReadAllText(_path),
                JsonOptions);
            if (document?.Version != SchemaVersion || document.Entries is null)
            {
                return;
            }

            foreach (TranslationHistoryEntry entry in document.Entries.Take(Capacity))
            {
                if (!string.IsNullOrWhiteSpace(entry.Source) &&
                    !string.IsNullOrWhiteSpace(entry.Translation))
                {
                    _entries.AddLast(entry);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or
                                          JsonException or
                                          UnauthorizedAccessException)
        {
            _entries.Clear();
        }
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
