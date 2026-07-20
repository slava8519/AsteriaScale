using AsteriaScale.Services;

/// <summary>
/// Normalization contract between build-dictionaries.py keys, user TSV files and
/// runtime lookup: internal punctuation in Tatoeba sentences, typographic
/// apostrophes from OCR and non-canonical user-dictionary keys.
/// </summary>
internal static class LocalDictionaryNormalizationTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "local-dictionary-normalization");
        string bundled = Path.Combine(root, "bundled");
        string user = Path.Combine(root, "user");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);

        string longMeanings = string.Join(
            "; ",
            Enumerable.Range(1, 8).Select(index =>
                $"длинное-значение-{index}-с-очень-подробным-пояснением-внутри"));
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus.tsv"),
            [
                "don't\tне",
                "hello\tбандл-значение",
                "know\tзнать",
                $"verbose\t{longMeanings}"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-dialogue.tsv"),
            [
                "well i don't know\tну не знаю"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "jpn-rus.tsv"),
            [
                "こんにちは\tздравствуйте"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(user, "eng-rus-custom.tsv"),
            [
                "Hello\tпользовательский привет",
                "Sudden Word\tвнезапное слово"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(user, "jpn-rus-custom.tsv"),
            [
                "ありがとう!\tспасибо"
            ]);

        LocalDictionaryService dictionary = new(bundled, user);
        await dictionary.LoadAsync();

        // A Tatoeba sentence key contains no punctuation, while the OCR sentence
        // does. The dialogue context must still align with the found word.
        const string punctuatedDialogue = "Well, I don't know.";
        DictionaryLookupResult knownWord = await dictionary.LookupAsync(
            punctuatedDialogue,
            "know",
            punctuatedDialogue.IndexOf("know", StringComparison.Ordinal));
        Check(knownWord is { Found: true, Translation: "знать" } &&
              knownWord.ContextTranslation == "ну не знаю",
            $"Internal punctuation broke the Tatoeba sentence match: {knownWord}");

        // With no entry for the hovered word, the exact sentence itself answers.
        DictionaryLookupResult sentenceFallback = await dictionary.LookupAsync(
            punctuatedDialogue,
            "Well",
            0);
        Check(sentenceFallback is { Found: true, IsPhrase: true, Translation: "ну не знаю" },
            $"The dialogue sentence fallback ignored a punctuated sentence: {sentenceFallback}");

        // OCR reads the typographic apostrophe; the dictionary stores don't.
        const string typographicDialogue = "“Well, I don’t know…”";
        DictionaryLookupResult typographicWord = await dictionary.LookupAsync(
            typographicDialogue,
            "don’t",
            typographicDialogue.IndexOf("don’t", StringComparison.Ordinal));
        Check(typographicWord is { Found: true, Translation: "не" } &&
              typographicWord.ContextTranslation == "ну не знаю",
            $"The typographic apostrophe broke word and sentence lookup: {typographicWord}");

        // User TSV keys are hand-written and must be canonicalized at load.
        DictionaryLookupResult userOverride = await dictionary.LookupAsync(
            "Hello everyone", "Hello", 0);
        Check(userOverride is { Found: true, Translation: "пользовательский привет" },
            $"A capitalized user key was not normalized or did not override: {userOverride}");

        const string userPhraseContext = "A Sudden Word appeared.";
        DictionaryLookupResult userPhrase = await dictionary.LookupAsync(
            userPhraseContext,
            "Sudden",
            userPhraseContext.IndexOf("Sudden", StringComparison.Ordinal));
        Check(userPhrase is { Found: true, IsPhrase: true, Translation: "внезапное слово" },
            $"A capitalized user phrase key was not normalized: {userPhrase}");

        DictionaryLookupResult japaneseUser = await dictionary.LookupAsync(
            "ありがとう", "ありがとう", 1);
        Check(japaneseUser is { Found: true, Translation: "спасибо", Language: "JA" },
            $"A Japanese user key was not canonicalized: {japaneseUser}");

        // A long meaning list is truncated at a meaning boundary, and the
        // trailing "…" marker never becomes a rankable candidate.
        DictionaryLookupResult truncated = await dictionary.LookupAsync(
            "A verbose explanation.", "verbose", 2);
        string[] chunks = NpuDictionarySelectorService.SplitMeanings(truncated.Translation);
        Check(truncated.Found &&
              truncated.Translation.EndsWith("; …", StringComparison.Ordinal) &&
              chunks.Length == 6 &&
              chunks.All(chunk =>
                  chunk.StartsWith("длинное-значение-", StringComparison.Ordinal) &&
                  chunk.EndsWith("-внутри", StringComparison.Ordinal)),
            $"A long meaning list must truncate at a boundary without partial candidates: {truncated.Translation}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
