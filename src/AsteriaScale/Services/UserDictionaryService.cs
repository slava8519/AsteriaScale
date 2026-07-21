using System.IO;
using System.Text;

namespace AsteriaScale.Services;

public sealed record UserDictionaryEntry(
    string Key,
    string Translation,
    string Language = "EN");

/// <summary>
/// Read/write access to the user's own English and Japanese dictionary layers,
/// which have the highest lookup priority and survive application updates.
/// Keys use the exact runtime lookup normalization (invariant 1).
/// </summary>
public sealed class UserDictionaryService
{
    private const string EnglishFileName = "eng-rus-user.tsv";
    private const string JapaneseFileName = "jpn-rus-user.tsv";
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

    public string FilePath => Path.Combine(_directory, EnglishFileName);
    public string JapaneseFilePath => Path.Combine(_directory, JapaneseFileName);

    public async Task<IReadOnlyList<UserDictionaryEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            MigrateLegacyJapaneseLocked();
            return ReadLocked(FilePath, "EN")
                .Concat(ReadLocked(JapaneseFilePath, "JA"))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .ThenBy(entry => entry.Language, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds or replaces one entry. Japanese source text is routed to the
    /// jpn-rus user layer; other keys go to eng-rus.
    /// </summary>
    public async Task<string> SaveEntryAsync(
        string key,
        string translation,
        CancellationToken cancellationToken = default)
    {
        string rawKey = key ?? string.Empty;
        if (rawKey.TrimStart().StartsWith('#'))
        {
            throw new ArgumentException(
                "Ключ не может начинаться с «#»: такие строки являются комментариями TSV.",
                nameof(key));
        }

        string japaneseKey = LocalDictionaryService.NormalizeJapanese(rawKey);
        bool japanese = japaneseKey.Length > 0;
        string canonicalKey = japanese
            ? japaneseKey
            : LocalDictionaryService.NormalizeEnglish(rawKey);
        string value = CollapseWhitespace(translation ?? string.Empty);
        if (canonicalKey.Length == 0 || value.Length == 0)
        {
            throw new ArgumentException("Слово и перевод не могут быть пустыми.", nameof(key));
        }

        if (canonicalKey.Length > MaximumEntryLength || value.Length > MaximumEntryLength)
        {
            throw new ArgumentException("Слишком длинная запись словаря.", nameof(key));
        }

        string language = japanese ? "JA" : "EN";
        string path = japanese ? JapaneseFilePath : FilePath;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            MigrateLegacyJapaneseLocked();
            List<UserDictionaryEntry> entries = ReadLocked(path, language)
                .Where(entry => !string.Equals(entry.Key, canonicalKey, StringComparison.Ordinal))
                .ToList();
            entries.Add(new UserDictionaryEntry(canonicalKey, value, language));
            WriteLocked(path, entries, language);
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
        string rawKey = key ?? string.Empty;
        string japaneseKey = LocalDictionaryService.NormalizeJapanese(rawKey);
        bool japanese = japaneseKey.Length > 0;
        string canonicalKey = japanese
            ? japaneseKey
            : LocalDictionaryService.NormalizeEnglish(rawKey);
        string language = japanese ? "JA" : "EN";
        string path = japanese ? JapaneseFilePath : FilePath;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            MigrateLegacyJapaneseLocked();
            List<UserDictionaryEntry> entries = ReadLocked(path, language);
            int removed = entries.RemoveAll(entry =>
                string.Equals(entry.Key, canonicalKey, StringComparison.Ordinal));
            if (removed > 0)
            {
                WriteLocked(path, entries, language);
            }

            return removed > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void MigrateLegacyJapaneseLocked()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        List<string> retainedEnglishLines = [];
        List<UserDictionaryEntry> legacyJapaneseEntries = [];
        HashSet<string> japaneseKeys = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(FilePath, Encoding.UTF8))
        {
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                retainedEnglishLines.Add(line);
                continue;
            }

            int separator = line.IndexOf('\t');
            if (separator <= 0 || separator == line.Length - 1)
            {
                retainedEnglishLines.Add(line);
                continue;
            }

            string rawKey = line[..separator];
            string value = CollapseWhitespace(line[(separator + 1)..]);
            string japaneseKey = LocalDictionaryService.NormalizeJapanese(rawKey);
            if (japaneseKey.Length > 0)
            {
                if (value.Length > 0 && japaneseKeys.Add(japaneseKey))
                {
                    legacyJapaneseEntries.Add(
                        new UserDictionaryEntry(japaneseKey, value, "JA"));
                }

                continue;
            }

            // Migration must not rewrite hand-edited English rows, comments or
            // unknown lines merely because one Japanese row was present.
            retainedEnglishLines.Add(line);
        }

        if (legacyJapaneseEntries.Count == 0)
        {
            return;
        }

        if (File.Exists(JapaneseFilePath))
        {
            List<string> japaneseLines = File.ReadLines(JapaneseFilePath, Encoding.UTF8).ToList();
            HashSet<string> destinationKeys = new(StringComparer.Ordinal);
            foreach (string line in japaneseLines)
            {
                int separator = line.IndexOf('\t');
                if (separator <= 0 || separator == line.Length - 1)
                {
                    continue;
                }

                string key = LocalDictionaryService.NormalizeJapanese(line[..separator]);
                string value = CollapseWhitespace(line[(separator + 1)..]);
                if (key.Length > 0 && value.Length > 0)
                {
                    destinationKeys.Add(key);
                }
            }

            bool destinationChanged = false;
            foreach (UserDictionaryEntry entry in legacyJapaneseEntries)
            {
                if (destinationKeys.Add(entry.Key))
                {
                    japaneseLines.Add($"{entry.Key}\t{entry.Translation}");
                    destinationChanged = true;
                }
            }

            // Preserve hand-edited comments, unknown rows and spelling in the
            // dedicated layer. Existing valid rows stay newer than legacy data.
            if (destinationChanged)
            {
                WriteRawLinesLocked(JapaneseFilePath, japaneseLines);
            }
        }
        else
        {
            WriteLocked(JapaneseFilePath, legacyJapaneseEntries, "JA");
        }

        // Write the destination first. A crash between these two atomic moves
        // can leave a harmless duplicate, never a lost Japanese translation.
        WriteRawLinesLocked(FilePath, retainedEnglishLines);
    }

    private static List<UserDictionaryEntry> ReadLocked(string path, string language)
    {
        List<UserDictionaryEntry> entries = [];
        if (!File.Exists(path))
        {
            return entries;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('\t');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            string key = language == "JA"
                ? LocalDictionaryService.NormalizeJapanese(line[..separator])
                : LocalDictionaryService.NormalizeEnglish(line[..separator]);
            string value = CollapseWhitespace(line[(separator + 1)..]);
            if (key.Length > 0 && value.Length > 0 && seen.Add(key))
            {
                entries.Add(new UserDictionaryEntry(key, value, language));
            }
        }

        return entries;
    }

    private void WriteLocked(
        string path,
        IEnumerable<UserDictionaryEntry> entries,
        string language)
    {
        Directory.CreateDirectory(_directory);
        StringBuilder builder = new();
        builder.AppendLine("# Пользовательский словарь Asteria Scale.");
        builder.AppendLine("# Формат: слово или фраза <TAB> перевод. Ключи нормализуются автоматически.");
        builder.Append("# Язык исходного текста: ").AppendLine(language == "JA" ? "японский." : "английский.");
        foreach (UserDictionaryEntry entry in entries.OrderBy(
                     entry => entry.Key,
                     StringComparer.Ordinal))
        {
            builder.Append(entry.Key).Append('\t').AppendLine(entry.Translation);
        }

        // Atomic replace: a half-written user layer would silently change lookup.
        ReplaceTextLocked(path, builder.ToString());
    }

    private static void WriteRawLinesLocked(string path, IReadOnlyList<string> lines)
    {
        string text = lines.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, lines) + Environment.NewLine;
        ReplaceTextLocked(path, text);
    }

    private static void ReplaceTextLocked(string path, string text)
    {
        string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, text, new UTF8Encoding(false));
            AtomicFile.Replace(temporaryPath, path);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or
                                                  UnauthorizedAccessException)
                {
                    // A unique temp cannot be mistaken for a dictionary layer.
                }
            }
        }
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
