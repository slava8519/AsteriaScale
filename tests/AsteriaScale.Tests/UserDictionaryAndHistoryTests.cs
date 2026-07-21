using AsteriaScale.Services;

/// <summary>
/// The user dictionary layer (highest lookup priority) and the translation
/// history that feeds the review panel and the Anki export.
/// </summary>
internal static class UserDictionaryAndHistoryTests
{
    public static async Task RunAsync(string tempRoot)
    {
        await VerifyUserDictionaryAsync(tempRoot);
        await VerifyUserEntryReachesLookupAsync(tempRoot);
        VerifyHistory(tempRoot);
    }

    private static async Task VerifyUserDictionaryAsync(string tempRoot)
    {
        string directory = Path.Combine(tempRoot, "user-dictionary");
        UserDictionaryService service = new(directory);

        // Hand-typed keys are canonicalized on write (invariant 1).
        string canonical = await service.SaveEntryAsync("  Hello, ", "привет");
        Check(canonical == "hello", $"Key was not canonicalized on write: '{canonical}'");
        Check(await service.SaveEntryAsync("Don’t", "не надо") == "don't",
            "A typographic apostrophe must be folded to the straight one.");

        IReadOnlyList<UserDictionaryEntry> entries = await service.LoadAsync();
        Check(entries.Count == 2, $"Expected two stored entries, got {entries.Count}.");

        // Re-saving the same word replaces instead of duplicating.
        await service.SaveEntryAsync("HELLO", "здравствуйте");
        entries = await service.LoadAsync();
        Check(entries.Count == 2 &&
              entries.Single(entry => entry.Key == "hello").Translation == "здравствуйте",
            "Saving an existing key must replace its translation.");

        Check(await service.RemoveEntryAsync("Hello"), "Removal by non-canonical key failed.");
        Check(!await service.RemoveEntryAsync("absent"), "Removing a missing key must report false.");
        entries = await service.LoadAsync();
        Check(entries.Count == 1 && entries[0].Key == "don't",
            "Removal did not leave the remaining entries intact.");

        try
        {
            await service.SaveEntryAsync("   ", "перевод");
            throw new InvalidOperationException("An empty key was accepted.");
        }
        catch (ArgumentException)
        {
            // Expected.
        }
    }

    /// <summary>
    /// The contract users actually care about: an entry added through the
    /// editor must win over the bundled layers at lookup time.
    /// </summary>
    private static async Task VerifyUserEntryReachesLookupAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "user-dictionary-lookup");
        string bundled = Path.Combine(root, "bundled");
        string user = Path.Combine(root, "user");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus.tsv"),
            ["hello\tбандловый привет", "world\tмир"]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "jpn-rus.tsv"),
            ["こんにちは\tздравствуйте"]);

        UserDictionaryService editor = new(user);
        await editor.SaveEntryAsync("Hello", "мой привет");

        LocalDictionaryService dictionary = new(bundled, user);
        await dictionary.LoadAsync();
        DictionaryLookupResult lookup = await dictionary.LookupAsync("Hello world", "Hello", 0);
        Check(lookup is { Found: true, Translation: "мой привет" },
            $"A user-added entry must override bundled layers: {lookup.Translation}");
    }

    private static void VerifyHistory(string tempRoot)
    {
        string path = Path.Combine(tempRoot, "history", "translation-history.v1.json");
        TranslationHistoryService history = new(path);
        history.Record("teetering", "быть на грани", "SMART-FAST", "He was teetering on the verge.");
        history.Record("teetering", "быть на грани", "SMART-FAST", "He was teetering on the verge.");
        history.Record("  verge  ", " на грани ", "СЛОВАРЬ");
        Check(history.Snapshot().Count == 2,
            "Consecutive duplicates must collapse and fields must be trimmed.");
        Check(history.Snapshot()[0].Source == "verge",
            "The newest entry must come first.");
        history.Record(string.Empty, "пусто", "СЛОВАРЬ");
        Check(history.Snapshot().Count == 2, "Empty sources must be ignored.");

        history.Flush();
        TranslationHistoryService reopened = new(path);
        Check(reopened.Snapshot().Count == 2, "History did not survive a restart.");

        string exportPath = Path.Combine(tempRoot, "history", "anki.tsv");
        int exported = reopened.ExportToAnki(exportPath);
        string[] lines = File.ReadAllLines(exportPath);
        Check(exported == 2 && lines.Length == 2,
            $"Anki export wrote {lines.Length} rows for {exported} entries.");
        Check(lines[0].Split('\t').Length == 3 &&
              lines[0].StartsWith("teetering\tбыть на грани\t", StringComparison.Ordinal),
            $"Anki rows must be front/back/context in chronological order: {lines[0]}");

        reopened.Clear();
        Check(reopened.Snapshot().Count == 0, "Clear did not empty the history.");
        Check(new TranslationHistoryService(path).Snapshot().Count == 0,
            "Clear was not persisted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
