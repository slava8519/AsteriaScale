using AsteriaScale.Services;

/// <summary>
/// The curated phrasal-verb layer (eng-rus-phrasal.tsv.gz) is an ordinary broad
/// bundled file, so multi-word keys join _lexicalEnglishPhrases and resolve as
/// phrases with priority over the single hovered word. Base surface forms are
/// stored; inflected occurrences canonicalize onto them. The packaged-asset
/// check fails if the shipped layer is missing or stops covering core verbs.
/// </summary>
internal static class PhrasalVerbLayerTests
{
    public static async Task RunAsync(string tempRoot)
    {
        await VerifyMechanismAsync(tempRoot);
        await VerifyPackagedLayerAsync(tempRoot);
    }

    private static async Task VerifyMechanismAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "phrasal-verb-mechanism");
        string bundled = Path.Combine(root, "bundled");
        string user = Path.Combine(root, "user");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);

        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus.tsv"),
            [
                "give\tдавать",
                "up\tвверх",
                "burst\tвзрыв; лопаться",
                "out\tнаружу",
                "laughing\tсмеясь"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-phrasal.tsv"),
            [
                "give up\tсдаваться; бросать",
                "burst out\tвоскликнуть; вырваться; разразиться"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "jpn-rus.tsv"),
            ["鳥\tптица"]);

        LocalDictionaryService dictionary = new(bundled, user);
        await dictionary.LoadAsync();

        // The contiguous phrasal verb wins over the single hovered verb.
        const string giveUp = "Don't give up now.";
        DictionaryLookupResult phrase = await dictionary.LookupAsync(
            giveUp, "give", giveUp.IndexOf("give", StringComparison.Ordinal));
        Check(phrase is { Found: true, IsPhrase: true, Translation: "сдаваться; бросать" },
            $"A curated phrasal verb did not take priority over the single word: {phrase}");

        // The same particle hovered inside the phrase resolves the phrase too.
        DictionaryLookupResult particle = await dictionary.LookupAsync(
            giveUp, "up", giveUp.IndexOf("up", StringComparison.Ordinal));
        Check(particle is { Found: true, IsPhrase: true, Translation: "сдаваться; бросать" },
            $"Hovering the particle did not resolve the phrasal verb: {particle}");

        // Without the particle the single verb is still returned untouched.
        const string plainVerb = "I give money to charity.";
        DictionaryLookupResult single = await dictionary.LookupAsync(
            plainVerb, "give", plainVerb.IndexOf("give", StringComparison.Ordinal));
        Check(single is { Found: true, IsPhrase: false, Translation: "давать" },
            $"A bare verb was wrongly upgraded to a phrasal verb: {single}");

        // Base forms are stored; an inflected verb canonicalizes onto the key.
        const string inflected = "She bursts out laughing.";
        DictionaryLookupResult inflectedPhrase = await dictionary.LookupAsync(
            inflected, "bursts", inflected.IndexOf("bursts", StringComparison.Ordinal));
        Check(inflectedPhrase is
        { Found: true, IsPhrase: true, Translation: "воскликнуть; вырваться; разразиться" },
            $"An inflected phrasal verb did not canonicalize onto its base key: {inflectedPhrase}");
    }

    private static async Task VerifyPackagedLayerAsync(string tempRoot)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "Dictionaries");
        Check(File.Exists(Path.Combine(bundled, "eng-rus-phrasal.tsv.gz")),
            "The packaged phrasal-verb layer eng-rus-phrasal.tsv.gz is missing.");

        string user = Path.Combine(tempRoot, "phrasal-verb-packaged-user");
        Directory.CreateDirectory(user);
        LocalDictionaryService dictionary = new(bundled, user);
        await dictionary.LoadAsync();

        // These keys exist only in this layer (not in any broad/curated/dialogue
        // file), so a match proves the packaged file loaded and was not shadowed.
        // Short contexts avoid a longer broad phrase (e.g. "burst out laughing")
        // winning; "weighing up" also exercises inflected canonicalization onto
        // the base "weigh up" key.
        foreach ((string context, string hovered, string expectedContains) in new[]
                 {
                     ("He burst out.", "burst", "вырваться"),
                     ("Just chill out.", "chill", "расслабиться"),
                     ("Don't space out.", "space", "витать"),
                     ("He is weighing up.", "weighing", "взвешивать")
                 })
        {
            DictionaryLookupResult lookup = await dictionary.LookupAsync(
                context, hovered, context.IndexOf(hovered, StringComparison.Ordinal));
            Check(lookup is { Found: true, IsPhrase: true } &&
                  lookup.Translation.Contains(expectedContains, StringComparison.Ordinal),
                $"The packaged phrasal layer did not cover '{context}': {lookup}");
        }

        await dictionary.UnloadAsync();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
