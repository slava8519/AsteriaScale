using System.IO;
using System.Text;

namespace AsteriaScale.Services;

public sealed record UserDictionaryEntry(string Key, string Translation);

/// <summary>
/// Read/write access to the user's own dictionary layer
/// (%LOCALAPPDATA%\AsteriaScale\Dictionaries\eng-rus-user.tsv), which has the
/// highest lookup priority and survives application updates.
///
/// Keys are canonicalized with <see cref="LocalDictionaryService.NormalizeEnglish"/>
/// on write, so a hand-typed "Hello" or a typographic apostrophe still resolves
/// at lookup time (invariant 1).
/// </summary>
public sealed class UserDictionaryService
{
    private const string FileName = "eng-rus-user.tsv";
    private const int MaximumEntryLength = 2000;

    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UserDictionaryService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AsteriaScale",
            "Dictionaries"))
    {
    }

    internal UserDictionaryService(string directory)
    {
        _directory = directory;
    }

    public string FilePath => Path.Combine(_directory, FileName);

    public async Task<IReadOnlyList<UserDictionaryEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return ReadLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds or replaces one entry. Returns the canonical key that was stored so
    /// callers can report what the dictionary will actually match.
    /// </summary>
    public async Task<string> SaveEntryAsync(
        string key,
        string translation,
        CancellationToken cancellationToken = default)
    {
        string canonicalKey = LocalDictionaryService.NormalizeEnglish(key ?? string.Empty);
        string value = CollapseWhitespace(translation ?? string.Empty);
        if (canonicalKey.Length == 0 || value.Length == 0)
        {
            throw new ArgumentException("Слово и перевод не могут быть пустыми.", nameof(key));
        }

        if (canonicalKey.Length > MaximumEntryLength || value.Length > MaximumEntryLength)
        {
            throw new ArgumentException("Слишком длинная запись словаря.", nameof(key));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<UserDictionaryEntry> entries = ReadLocked()
                .Where(entry => !string.Equals(entry.Key, canonicalKey, StringComparison.Ordinal))
                .ToList();
            entries.Add(new UserDictionaryEntry(canonicalKey, value));
            WriteLocked(entries);
            return canonicalKey;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveEntryAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        string canonicalKey = LocalDictionaryService.NormalizeEnglish(key ?? string.Empty);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<UserDictionaryEntry> entries = ReadLocked().ToList();
            int removed = entries.RemoveAll(entry =>
                string.Equals(entry.Key, canonicalKey, StringComparison.Ordinal));
            if (removed > 0)
            {
                WriteLocked(entries);
            }

            return removed > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<UserDictionaryEntry> ReadLocked()
    {
        List<UserDictionaryEntry> entries = [];
        string path = FilePath;
        if (!File.Exists(path))
        {
            return entries;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int separator = line.IndexOf('\t');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            string key = LocalDictionaryService.NormalizeEnglish(line[..separator]);
            string value = CollapseWhitespace(line[(separator + 1)..]);
            if (key.Length > 0 && value.Length > 0 && seen.Add(key))
            {
                entries.Add(new UserDictionaryEntry(key, value));
            }
        }

        return entries;
    }

    private void WriteLocked(IEnumerable<UserDictionaryEntry> entries)
    {
        Directory.CreateDirectory(_directory);
        StringBuilder builder = new();
        builder.AppendLine("# Пользовательский словарь Asteria Scale.");
        builder.AppendLine("# Формат: слово или фраза <TAB> перевод. Ключи нормализуются автоматически.");
        foreach (UserDictionaryEntry entry in entries.OrderBy(
                     entry => entry.Key,
                     StringComparer.Ordinal))
        {
            builder.Append(entry.Key).Append('\t').AppendLine(entry.Translation);
        }

        // Atomic replace: a half-written user layer would silently change lookup.
        string temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, builder.ToString(), new UTF8Encoding(false));
        AtomicFile.Replace(temporaryPath, FilePath);
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
