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
        await VerifyHandEditedRowsSurviveAsync(tempRoot);
        await VerifyMalformedLegacyJapaneseRowsSurviveAsync(tempRoot);
        await VerifyUserEntryReachesLookupAsync(tempRoot);
        await VerifyLegacyJapaneseMigrationAsync(tempRoot);
        await VerifyHistoryAsync(tempRoot);
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
        Check(await service.SaveEntryAsync("ありがとう!", "спасибо") == "ありがとう",
            "A Japanese key was not routed through Japanese normalization.");

        IReadOnlyList<UserDictionaryEntry> entries = await service.LoadAsync();
        Check(entries.Count == 3 &&
              entries.Single(entry => entry.Key == "ありがとう").Language == "JA" &&
              File.Exists(service.JapaneseFilePath),
            $"Expected separate English/Japanese entries, got {entries.Count}.");

        // Re-saving the same word replaces instead of duplicating.
        await service.SaveEntryAsync("HELLO", "здравствуйте");
        entries = await service.LoadAsync();
        Check(entries.Count == 3 &&
              entries.Single(entry => entry.Key == "hello").Translation == "здравствуйте",
            "Saving an existing key must replace its translation.");

        Check(await service.RemoveEntryAsync("Hello"), "Removal by non-canonical key failed.");
        Check(!await service.RemoveEntryAsync("absent"), "Removing a missing key must report false.");
        entries = await service.LoadAsync();
        Check(entries.Count == 2 && entries.Any(entry => entry.Key == "don't") &&
              entries.Any(entry => entry.Key == "ありがとう" && entry.Language == "JA"),
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

        try
        {
            await service.SaveEntryAsync(" # hidden", "невидимый");
            throw new InvalidOperationException("A comment-prefixed key was accepted.");
        }
        catch (ArgumentException)
        {
            // Expected: the TSV loader treats leading # as a comment.
        }
    }

    /// <summary>
    /// The user layer is a hand-editable TSV the UI advertises as such, so one
    /// edit made through the editor must not rewrite rows it does not own.
    /// </summary>
    private static async Task VerifyHandEditedRowsSurviveAsync(string tempRoot)
    {
        string directory = Path.Combine(tempRoot, "user-dictionary-hand-edited");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "eng-rus-user.tsv");
        await File.WriteAllLinesAsync(
            path,
            [
                "# мои заметки по глоссарию",
                "hello\tпривет",
                "",
                "строка без табуляции",
                "verge\tграница",
                "# TODO: проверить перевод",
                "verge\tкрай"
            ]);

        UserDictionaryService service = new(directory);
        await service.SaveEntryAsync("verge", "грань");
        Check(await service.RemoveEntryAsync("hello"),
            "Removing an existing hand-typed row reported no change.");
        await service.SaveEntryAsync("teetering", "на грани");

        string[] lines = await File.ReadAllLinesAsync(path);
        Check(lines.Contains("# мои заметки по глоссарию") &&
              lines.Contains("# TODO: проверить перевод") &&
              lines.Contains("строка без табуляции") &&
              lines.Contains(string.Empty),
            "An edit through the editor discarded comments or unparsed rows:\n" +
            string.Join('\n', lines));
        Check(lines.Count(line => line.StartsWith("verge\t", StringComparison.Ordinal)) == 1 &&
              lines.Contains("verge\tгрань"),
            "Saving over a duplicated key must replace the row that wins at load time.");
        Check(!lines.Any(line => line.StartsWith("hello\t", StringComparison.Ordinal)),
            "A removed key stayed in the file.");
        Check(lines.Contains("teetering\tна грани"), "A new key was not appended.");

        IReadOnlyList<UserDictionaryEntry> entries = await service.LoadAsync();
        Check(entries.Count == 2 &&
              entries.Single(entry => entry.Key == "verge").Translation == "грань",
            $"The hand-edited layer did not load back as expected: {entries.Count} entries.");

        // A no-op removal must not touch the file at all.
        string before = await File.ReadAllTextAsync(path);
        Check(!await service.RemoveEntryAsync("absent"),
            "Removing a missing key reported a change.");
        Check(await File.ReadAllTextAsync(path) == before,
            "A no-op removal rewrote the user layer.");
    }

    private static async Task VerifyMalformedLegacyJapaneseRowsSurviveAsync(string tempRoot)
    {
        string directory = Path.Combine(tempRoot, "user-dictionary-malformed-legacy-ja");
        Directory.CreateDirectory(directory);
        string englishPath = Path.Combine(directory, "eng-rus-user.tsv");
        const string malformedJapaneseRow = "かな\t   ";
        await File.WriteAllLinesAsync(
            englishPath,
            [
                "# legacy mixed layer",
                malformedJapaneseRow,
                "ありがとう\tспасибо",
                "hello\tпривет"
            ]);

        UserDictionaryService service = new(directory);
        IReadOnlyList<UserDictionaryEntry> entries = await service.LoadAsync();

        string[] retainedEnglishLines = await File.ReadAllLinesAsync(englishPath);
        Check(retainedEnglishLines.Contains(malformedJapaneseRow, StringComparer.Ordinal),
            "Migrating a valid neighbouring Japanese row discarded an unparsed whitespace-value row.");
        Check(retainedEnglishLines.Contains("# legacy mixed layer", StringComparer.Ordinal) &&
              retainedEnglishLines.Contains("hello\tпривет", StringComparer.Ordinal),
            "Legacy Japanese migration changed unrelated hand-edited English rows.");
        Check(entries.Any(entry => entry is { Key: "ありがとう", Translation: "спасибо", Language: "JA" }),
            "The valid legacy Japanese neighbour was not migrated.");

        await service.SaveEntryAsync("world", "мир");
        retainedEnglishLines = await File.ReadAllLinesAsync(englishPath);
        Check(retainedEnglishLines.Contains(malformedJapaneseRow, StringComparer.Ordinal),
            "A later editor write discarded the retained malformed Japanese-looking row.");
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
        await editor.SaveEntryAsync("こんにちは", "мой японский привет");

        LocalDictionaryService dictionary = new(bundled, user);
        await dictionary.LoadAsync();
        DictionaryLookupResult lookup = await dictionary.LookupAsync("Hello world", "Hello", 0);
        Check(lookup is { Found: true, Translation: "мой привет" },
            $"A user-added entry must override bundled layers: {lookup.Translation}");
        DictionaryLookupResult japaneseLookup = await dictionary.LookupAsync(
            "こんにちは", "こんにちは", 1);
        Check(japaneseLookup is { Found: true, Translation: "мой японский привет", Language: "JA" },
            $"A Japanese user entry did not reach its lookup layer: {japaneseLookup}");
    }

    private static async Task VerifyLegacyJapaneseMigrationAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "legacy-japanese-user");
        string bundled = Path.Combine(root, "bundled");
        string user = Path.Combine(root, "user");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus.tsv"),
            ["hello\tпривет"]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "jpn-rus.tsv"),
            ["こんにちは\tздравствуйте"]);
        string legacyPath = Path.Combine(user, "eng-rus-user.tsv");
        await File.WriteAllLinesAsync(
            legacyPath,
            [
                "# мой комментарий",
                "hello\tмой привет",
                "неизвестная строка",
                "",
                "古語\tстарое слово",
                "旧語\tустаревшее слово"
            ]);
        string japaneseUserPath = Path.Combine(user, "jpn-rus-user.tsv");
        await File.WriteAllLinesAsync(
            japaneseUserPath,
            [
                "# мой японский комментарий",
                "неизвестная японская строка",
                "古語\tновое значение"
            ]);

        LocalDictionaryService compatibleDictionary = new(bundled, user);
        await compatibleDictionary.LoadAsync();
        DictionaryLookupResult compatibleLookup = await compatibleDictionary.LookupAsync(
            "古語",
            "古語",
            0);
        DictionaryLookupResult legacyOnlyLookup = await compatibleDictionary.LookupAsync(
            "旧語",
            "旧語",
            0);
        Check(compatibleLookup is { Found: true, Translation: "новое значение", Language: "JA" } &&
              legacyOnlyLookup is { Found: true, Translation: "устаревшее слово", Language: "JA" },
            "Japanese compatibility lookup did not merge legacy and dedicated user rows.");

        UserDictionaryService editor = new(user);
        IReadOnlyList<UserDictionaryEntry> migrated = await editor.LoadAsync();
        string migratedJapaneseText = File.ReadAllText(japaneseUserPath);
        Check(migrated.Any(entry => entry is
        { Key: "古語", Translation: "новое значение", Language: "JA" }) &&
              migrated.Any(entry => entry is
              { Key: "旧語", Translation: "устаревшее слово", Language: "JA" }) &&
              !File.ReadAllText(legacyPath).Contains("古語", StringComparison.Ordinal) &&
              !File.ReadAllText(legacyPath).Contains("旧語", StringComparison.Ordinal) &&
              migratedJapaneseText.Contains("古語\tновое значение", StringComparison.Ordinal) &&
              !migratedJapaneseText.Contains("古語\tстарое слово", StringComparison.Ordinal) &&
              migratedJapaneseText.Contains("旧語\tустаревшее слово", StringComparison.Ordinal) &&
              migratedJapaneseText.Contains("# мой японский комментарий", StringComparison.Ordinal) &&
              migratedJapaneseText.Contains("неизвестная японская строка", StringComparison.Ordinal) &&
              File.ReadAllText(legacyPath).Contains("# мой комментарий", StringComparison.Ordinal) &&
              File.ReadAllText(legacyPath).Contains("неизвестная строка", StringComparison.Ordinal),
            "Japanese legacy migration lost raw rows or merged with the dedicated layer incorrectly.");
        Check(await editor.RemoveEntryAsync("古語"),
            "A migrated Japanese row could not be removed through the editor.");

        LocalDictionaryService afterRemoval = new(bundled, user);
        await afterRemoval.LoadAsync();
        DictionaryLookupResult removedLookup = await afterRemoval.LookupAsync("古語", "古語", 0);
        Check(!removedLookup.Found,
            "Removing a migrated Japanese entry left a legacy lookup copy behind.");
    }

    private static async Task VerifyHistoryAsync(string tempRoot)
    {
        string path = Path.Combine(tempRoot, "history", "translation-history.v1.json");
        using (TranslationHistoryService history = new(
                   path,
                   TimeSpan.FromMilliseconds(40),
                   TimeSpan.FromMilliseconds(40)))
        {
            history.Record(
                "teetering",
                "быть на грани",
                "SMART-FAST",
                "He was teetering on the verge.",
                "EN");
            history.Record(
                "teetering",
                "быть на грани",
                "SMART-FAST",
                "He was teetering on the verge.",
                "EN");
            history.Record("  verge  ", " на грани ", "СЛОВАРЬ", "Near the verge.");
            Check(history.Snapshot().Count == 2,
                "Consecutive duplicates must collapse and fields must be trimmed.");
            Check(history.Snapshot()[0].Source == "verge",
                "The newest entry must come first.");
            history.Record(string.Empty, "пусто", "СЛОВАРЬ");
            Check(history.Snapshot().Count == 2, "Empty sources must be ignored.");

            await WaitUntilAsync(
                () => File.Exists(path),
                TimeSpan.FromSeconds(2),
                "Debounced history persistence did not create its file.");
        }

        using TranslationHistoryService reopened = new(path);
        Check(reopened.Snapshot().Count == 2, "History did not survive a restart.");
        Check(reopened.Snapshot().Any(entry =>
                entry.Source == "teetering" &&
                entry.Context == "He was teetering on the verge." &&
                entry.Language == "EN"),
            "History dropped the production context or language metadata.");
        reopened.Record(
            "teetering",
            "шататься на грани",
            "SMART",
            "He kept teetering.");
        reopened.Flush();

        string exportPath = Path.Combine(tempRoot, "history", "anki.tsv");
        int exported = reopened.ExportToAnki(exportPath);
        string[] lines = File.ReadAllLines(exportPath);
        Check(exported == 2 && lines.Length == 2,
            $"Anki export wrote {lines.Length} rows for {exported} entries.");
        Check(lines.All(line => line.Split('\t').Length == 3) &&
              lines.Single(line => line.StartsWith("teetering\t", StringComparison.Ordinal))
                  .StartsWith("teetering\tшататься на грани\tHe kept teetering.", StringComparison.Ordinal),
            "Anki export did not keep the newest correction and its context.");

        string emptyExportPath = Path.Combine(tempRoot, "history", "existing-empty-target.tsv");
        await File.WriteAllTextAsync(emptyExportPath, "do not replace");
        using TranslationHistoryService empty = new(
            Path.Combine(tempRoot, "history", "empty-history.json"));
        Check(empty.ExportToAnki(emptyExportPath) == 0 &&
              await File.ReadAllTextAsync(emptyExportPath) == "do not replace",
            "An empty export clobbered an existing Anki file.");

        reopened.Dispose();
        await VerifyHistoryIoRecoveryAsync(path);
        await VerifyBrokenHistoryRecoveryAsync(tempRoot);

        using TranslationHistoryService clear = new(path);
        Check(clear.Clear(), "Clear did not persist a readable history.");
        Check(clear.Snapshot().Count == 0, "Clear did not empty the history.");
        using TranslationHistoryService afterClear = new(path);
        Check(afterClear.Snapshot().Count == 0, "Clear was not persisted.");
    }

    private static async Task VerifyHistoryIoRecoveryAsync(string path)
    {
        using TranslationHistoryService pending = new(
            path,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1));
        using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            pending.Record("pending", "ожидающий", "СЛОВАРЬ", "Pending context.");
            Check(pending.Snapshot().Count == 1,
                "A translation recorded during a transient read failure was lost in memory.");
            pending.Flush();
            Check(!pending.Clear(),
                "Clear reported success while the existing history could not be read.");
        }

        pending.Flush();

        using (TranslationHistoryService loaded = new(path))
        {
            Check(loaded.Snapshot().Any(entry => entry.Source == "teetering") &&
                  loaded.Snapshot().Any(entry => entry.Source == "pending"),
                "A transient load failure caused the old history to be overwritten.");
            loaded.Record("retry", "повтор", "СЛОВАРЬ");
            using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                loaded.Flush();
            }

            loaded.Flush();
        }

        using TranslationHistoryService afterRetry = new(path);
        Check(afterRetry.Snapshot().Any(entry => entry.Source == "retry"),
            "A failed flush cleared the dirty flag instead of retrying.");

        await Task.CompletedTask;
    }

    private static async Task VerifyBrokenHistoryRecoveryAsync(string tempRoot)
    {
        foreach ((string name, string brokenDocument) in new[]
                 {
                     ("invalid-json", "{ definitely not json"),
                     ("unknown-schema", "{\"version\":99,\"entries\":[]}")
                 })
        {
            string path = Path.Combine(tempRoot, "history", name + ".json");
            await File.WriteAllTextAsync(path, brokenDocument);
            using (TranslationHistoryService history = new(path))
            {
                history.Record("new entry", "новая запись", "СЛОВАРЬ");
                history.Flush();
            }

            string[] quarantined = Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".broken-*");
            Check(quarantined.Length == 1 &&
                  await File.ReadAllTextAsync(quarantined[0]) == brokenDocument,
                $"A permanently broken history was not preserved: {name}.");
            using TranslationHistoryService reopened = new(path);
            Check(reopened.Snapshot().Any(entry => entry.Source == "new entry"),
                $"A broken history blocked later persistence: {name}.");
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        string failureMessage)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Check(predicate(), failureMessage);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
