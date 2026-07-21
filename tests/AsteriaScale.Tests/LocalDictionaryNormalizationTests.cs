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
        VerifyGeneratorParityContract();
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
                "bird\tптица",
                "hello\tбандл-значение",
                "know\tзнать",
                "101\tсто один",
                "24/7\tкруглосуточно",
                "1 chronicles\tпервая книга паралипоменон",
                "3d printer\tтрёхмерный принтер",
                $"verbose\t{longMeanings}"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-dialogue.tsv"),
            [
                "well i don't know\tну не знаю",
                "a bird flies\tлетит птица"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "jpn-rus.tsv"),
            [
                "こんにちは\tздравствуйте",
                "鳥\tптица"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "jpn-rus-dialogue.tsv"),
            [
                "鳥飛ぶ\tлетит птица"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(user, "eng-rus-custom.tsv"),
            [
                "Hello\tпользовательский привет",
                "Rock & Roll 2\tрок-н-ролл",
                "Straße\tулица",
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

        const string punctuatedUserContext = "We played ROCK & ROLL 2 all night.";
        DictionaryLookupResult punctuatedUserPhrase = await dictionary.LookupAsync(
            punctuatedUserContext,
            "ROLL",
            punctuatedUserContext.IndexOf("ROLL", StringComparison.Ordinal));
        Check(punctuatedUserPhrase is { Found: true, IsPhrase: true, Translation: "рок-н-ролл" },
            $"Internal punctuation and digits produced an unreachable key: {punctuatedUserPhrase}");

        foreach ((string context, string hovered, string expected, bool phrase) in new[]
                 {
                     ("101", "101", "сто один", false),
                     ("Available 24/7", "24/7", "круглосуточно", false),
                     ("Read 1 Chronicles tonight", "Chronicles", "первая книга паралипоменон", true),
                     ("The 3D printer hummed", "printer", "трёхмерный принтер", true)
                 })
        {
            DictionaryLookupResult numericLookup = await dictionary.LookupAsync(
                context,
                hovered,
                context.IndexOf(hovered, StringComparison.OrdinalIgnoreCase));
            Check(numericLookup.Found &&
                  numericLookup.IsPhrase == phrase &&
                  numericLookup.Translation == expected,
                $"A numeric lexical key was unreachable: {context} -> {numericLookup}");
        }

        DictionaryLookupResult unicodeFold = await dictionary.LookupAsync(
            "STRASSE", "STRASSE", 0);
        Check(unicodeFold is { Found: true, Translation: "улица" },
            $"Unicode case folding differs between stored and lookup keys: {unicodeFold}");
        Check(LocalDictionaryService.NormalizeEnglish("  Straße, ROCK & ROLL 2  ") ==
              "strasse, rock & roll 2",
            "Runtime canonical normalization changed for punctuation, digits or Unicode folding.");
        Check(LocalDictionaryService.NormalizeEnglish("mp3") !=
              LocalDictionaryService.NormalizeEnglish("mp") &&
              LocalDictionaryService.NormalizeEnglish("C++") !=
              LocalDictionaryService.NormalizeEnglish("C"),
            "Lexical normalization merged distinct punctuation or digit-bearing keys.");

        const string embeddedDialogue = "Everyone watched as a bird flies away.";
        DictionaryLookupResult embeddedDialogueLookup = await dictionary.LookupAsync(
            embeddedDialogue,
            "bird",
            embeddedDialogue.IndexOf("bird", StringComparison.Ordinal));
        Check(embeddedDialogueLookup is
            { Found: true, IsPhrase: false, Translation: "птица", ContextTranslation: "" },
            $"A dialogue-only sentence leaked into generic phrase lookup: {embeddedDialogueLookup}");

        const string embeddedJapaneseDialogue = "空で鳥飛ぶ姿";
        DictionaryLookupResult embeddedJapaneseLookup = await dictionary.LookupAsync(
            embeddedJapaneseDialogue,
            "鳥",
            embeddedJapaneseDialogue.IndexOf('鳥'));
        Check(embeddedJapaneseLookup is
            { Found: true, IsPhrase: false, Translation: "птица", ContextTranslation: "" },
            $"A Japanese dialogue sentence leaked into substring lookup: {embeddedJapaneseLookup}");

        DictionaryLookupResult exactJapaneseDialogue = await dictionary.LookupAsync(
            "鳥飛ぶ",
            "飛",
            1);
        Check(exactJapaneseDialogue is
            { Found: true, IsPhrase: true, Translation: "летит птица", ContextTranslation: "летит птица" },
            $"An exact Japanese dialogue sentence stopped working: {exactJapaneseDialogue}");

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

    private static void VerifyGeneratorParityContract()
    {
        string? repositoryRoot = AppContext.BaseDirectory;
        while (repositoryRoot is not null &&
               !File.Exists(Path.Combine(repositoryRoot, "AsteriaScale.slnx")))
        {
            repositoryRoot = Path.GetDirectoryName(repositoryRoot);
        }

        Check(repositoryRoot is not null, "Repository root was not found for normalization fixtures.");
        string contractPath = Path.Combine(
            repositoryRoot!,
            "scripts",
            "dictionary-normalization-contract.tsv");
        foreach (string line in File.ReadLines(contractPath))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] fields = line.Split('\t');
            Check(fields.Length == 3, $"Malformed normalization contract row: {line}");
            string lexicalActual = LocalDictionaryService.NormalizeEnglish(fields[0]);
            string sentenceActual = LocalDictionaryService.NormalizeEnglishSentenceKey(fields[0]);
            Check(lexicalActual == fields[1] && sentenceActual == fields[2],
                $"Runtime normalization differs from the generator contract: '{fields[0]}' -> " +
                $"('{lexicalActual}', '{sentenceActual}'), expected ('{fields[1]}', '{fields[2]}').");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
