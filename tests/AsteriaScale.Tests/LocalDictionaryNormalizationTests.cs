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
                // Masked-spelling fixtures: the plain words a censored form
                // stands for, plus the real words a letter-substitution guess
                // would land on instead.
                "shit\tдерьмо",
                "shat\tиспражнялся",
                "bastard\tублюдок",
                "bustard\tдрофа",
                "hell\tад",
                "hill\tхолм",
                "s**t\tмаскированное дерьмо",
                $"verbose\t{longMeanings}"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-dialogue.tsv"),
            [
                "well i don't know\tну не знаю",
                "a bird flies\tлетит птица",
                // Built from "A bird flies at 119 mph": the generator drops the
                // number, so every other speed shares this key.
                "a bird flies at mph\tптица летит со скоростью 119 миль в час"
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
                "鳥飛ぶ\tлетит птица",
                // Built from "鳥が119羽飛ぶ" — Japanese keys keep only kana and
                // kanji, so the count is dropped exactly as in English.
                "鳥が羽飛ぶ\tптицы летят числом 119"
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

        // Digits are absent from sentence keys on both sides of the generator
        // contract, so every numeric variant of a sentence collapses onto one
        // key. Such a hit names a number the user is not looking at, which is
        // worse than no sentence translation at all.
        const string numericDialogue = "A bird flies at 911 mph.";
        DictionaryLookupResult numericWord = await dictionary.LookupAsync(
            numericDialogue,
            "bird",
            numericDialogue.IndexOf("bird", StringComparison.Ordinal));
        Check(numericWord is { Found: true, Translation: "птица" } &&
              numericWord.ContextTranslation.Length == 0,
            "A digit-colliding sentence key reached the hover card: " +
            $"'{numericWord.ContextTranslation}'");

        DictionaryLookupResult numericFallback = await dictionary.LookupAsync(
            numericDialogue,
            "mph",
            numericDialogue.IndexOf("mph", StringComparison.Ordinal));
        Check(!numericFallback.Found,
            $"A digit-colliding sentence key answered a hover: {numericFallback.Translation}");
        Check(await dictionary.LookupExactEnglishDialogueAsync(numericDialogue) is null,
            "The exact dialogue API returned a digit-colliding sentence translation.");

        // The very same key still answers for a source that carries no digits,
        // where the stored translation is the only thing it can mean.
        Check(await dictionary.LookupExactEnglishDialogueAsync("A bird flies at mph") ==
              "птица летит со скоростью 119 миль в час",
            "The digit guard suppressed a verifiable exact sentence match.");

        const string numericJapaneseDialogue = "鳥が911羽飛ぶ";
        DictionaryLookupResult numericJapanese = await dictionary.LookupAsync(
            numericJapaneseDialogue,
            "鳥",
            0);
        Check(numericJapanese is { Found: true, Translation: "птица" } &&
              numericJapanese.ContextTranslation.Length == 0,
            "A digit-colliding Japanese sentence key reached the hover card: " +
            $"'{numericJapanese.ContextTranslation}'");

        // Sentence-key normalization drops every Unicode Number scalar, not
        // only decimal ASCII digits. Fractions, circled and Roman numerals, and
        // supplementary-plane mathematical digits must therefore get the same
        // collision guard in both dialogue languages.
        foreach (string number in new[] { "\u00BD", "\u2460", "\u2160", "\U0001D7D9" })
        {
            string english = $"A bird flies at {number} mph.";
            Check(!LocalDictionaryService.CanTrustExactSentenceTranslation(english),
                $"Unicode number U+{char.ConvertToUtf32(number, 0):X} passed the exact-dialogue guard.");
            DictionaryLookupResult englishFallback = await dictionary.LookupAsync(
                english,
                "mph",
                english.IndexOf("mph", StringComparison.Ordinal));
            Check(!englishFallback.Found,
                $"Unicode number '{number}' let a colliding English sentence answer a hover.");
            Check(await dictionary.LookupExactEnglishDialogueAsync(english) is null,
                $"Unicode number '{number}' let the exact English dialogue API return a collision.");

            string japanese = $"\u9CE5\u304C{number}\u7FBD\u98DB\u3076";
            DictionaryLookupResult japaneseWord = await dictionary.LookupAsync(
                japanese,
                "\u9CE5",
                0);
            Check(japaneseWord.Found && japaneseWord.ContextTranslation.Length == 0,
                $"Unicode number '{number}' exposed a colliding Japanese dialogue translation.");
        }

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
        string[] chunks = DictionaryMeaningParser.SplitMeanings(truncated.Translation);
        Check(truncated.Found &&
              truncated.Translation.EndsWith("; …", StringComparison.Ordinal) &&
              chunks.Length == 6 &&
              chunks.All(chunk =>
                  chunk.StartsWith("длинное-значение-", StringComparison.Ordinal) &&
                  chunk.EndsWith("-внутри", StringComparison.Ordinal)),
            $"A long meaning list must truncate at a boundary without partial candidates: {truncated.Translation}");
        Check(truncated.Translation.Length <=
              LocalDictionaryService.MaximumDisplayedValueLength,
            $"A displayed value exceeded its ceiling: {truncated.Translation.Length}");

        // The merge budget must stay strictly above the display ceiling. If the
        // two were equal, Limit could never truncate and never append the
        // ellipsis, so a cut meaning list would look complete to the user.
        Check(LocalDictionaryService.MergeBundledMeanings(
                  string.Join("; ", Enumerable.Range(0, 40).Select(
                      index => $"значение-{index}-достаточно-длинное-чтобы-переполнить-бюджет")),
                  "хвост-низкоприоритетного-слоя")
              .Length > LocalDictionaryService.MaximumDisplayedValueLength,
            "The merge budget no longer exceeds the display ceiling, so a truncated " +
            "meaning list would lose its continuation marker.");

        await VerifyMaskedSpellingsAsync(dictionary);

        await VerifyTranslationScaffoldAsync(root);
        await dictionary.UnloadAsync();
        await VerifyManagedMemoryReleaseIntentAsync(root);
    }

    private static async Task VerifyManagedMemoryReleaseIntentAsync(string root)
    {
        string bundled = Path.Combine(root, "memory-release", "bundled");
        string user = Path.Combine(root, "memory-release", "user");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);
        await File.WriteAllTextAsync(
            Path.Combine(bundled, "eng-rus.tsv"),
            "memory\tпамять\n");
        await File.WriteAllTextAsync(
            Path.Combine(bundled, "jpn-rus.tsv"),
            "記憶\tпамять\n");

        int releaseCount = 0;
        LocalDictionaryService observed = null!;
        observed = new LocalDictionaryService(
            bundled,
            user,
            managedMemoryRelease: () =>
            {
                Check(observed.EntryCount == 0 &&
                      observed.SourceCount == 0 &&
                      observed.SenseMetadataKeyCount == 0 &&
                      observed.SenseMetadataCandidateCount == 0 &&
                      observed.VersionToken == "unloaded",
                    "Managed-memory release ran before dictionary state was cleared.");
                releaseCount++;
            });

        await observed.LoadAsync();
        await observed.UnloadAsync();
        Check(releaseCount == 0,
            "An ordinary dictionary reload path triggered an aggressive full GC.");

        await observed.LoadAsync();
        await observed.UnloadAndReleaseMemoryAsync();
        Check(releaseCount == 1,
            "An explicit mode exit did not request managed dictionary-memory release.");
        await observed.UnloadAndReleaseMemoryAsync();
        Check(releaseCount == 1,
            "An empty dictionary repeated the expensive managed-memory release.");
    }

    /// <summary>
    /// Masked spellings resolve through a curated table, never by guessing the
    /// hidden letters (roadmap #14.2C). Measured against the shipped
    /// dictionary, a single-letter substitution turns "b*stard" into "bustard"
    /// (a bird) and "h*ll" into "hill" — a confident mistranslation is worse
    /// than no result, so the guess must not exist at all.
    /// </summary>
    private static async Task VerifyMaskedSpellingsAsync(LocalDictionaryService dictionary)
    {
        const string masked = "You are a real b*stard, you know that?";
        DictionaryLookupResult bastard = await dictionary.LookupAsync(
            masked,
            "b*stard",
            masked.IndexOf("b*stard", StringComparison.Ordinal));
        Check(bastard is { Found: true, Translation: "ублюдок" },
            $"A masked spelling did not reach its curated word: {bastard}");
        Check(bastard.Source.Contains('*', StringComparison.Ordinal),
            $"The card lost the masked span the user actually saw: '{bastard.Source}'");
        Check(bastard.Translation != "дрофа",
            "A masked spelling was resolved by letter substitution to a different real word.");

        const string hellLine = "What the h*ll happened here?";
        DictionaryLookupResult hell = await dictionary.LookupAsync(
            hellLine,
            "h*ll",
            hellLine.IndexOf("h*ll", StringComparison.Ordinal));
        Check(hell is { Found: true, Translation: "ад" },
            $"'h*ll' did not resolve to its curated word: {hell}");
        Check(hell.Translation != "холм",
            "'h*ll' was resolved by substitution to 'hill'.");

        // A dictionary that carries the masked spelling itself must keep
        // winning: the exact form is tried before the curated fallback.
        const string ownEntry = "This is complete s**t.";
        DictionaryLookupResult own = await dictionary.LookupAsync(
            ownEntry,
            "s**t",
            ownEntry.IndexOf("s**t", StringComparison.Ordinal));
        Check(own is { Found: true, Translation: "маскированное дерьмо" },
            $"An exact masked entry lost to the curated fallback: {own}");

        // An unlisted masked form stays unresolved rather than being guessed.
        const string unlisted = "That was a m*ss of an evening.";
        DictionaryLookupResult unknown = await dictionary.LookupAsync(
            unlisted,
            "m*ss",
            unlisted.IndexOf("m*ss", StringComparison.Ordinal));
        Check(!unknown.Found,
            $"An unlisted masked spelling was guessed instead of skipped: {unknown}");

        // Normalization itself is untouched (invariant 1).
        Check(LocalDictionaryService.NormalizeEnglish("b*stard") == "b*stard",
            "Masked-spelling support changed the canonical key form.");
    }

    private static async Task VerifyTranslationScaffoldAsync(string root)
    {
        string scaffoldRoot = Path.Combine(root, "translation-scaffold");
        string bundled = Path.Combine(scaffoldRoot, "bundled");
        string user = Path.Combine(scaffoldRoot, "user");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);

        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus.tsv"),
            [
                "on the verge of\tна грани",
                "lantern\tфонарь",
                "bank\tбанк; берег",
                "lock\tзакрыть; закрывать",
                "shrine\tрака",
                "cipher\tшифр English",
                "party\tпартия; вечеринка",
                "alpha\tальфа",
                "beta\tбета",
                "gamma\tгамма",
                "delta\tдельта",
                "epsilon\tэпсилон",
                "zeta\tдзета",
                "eta\tэта",
                "theta\tтета",
                "iota\tйота",
                "kappa\tкаппа",
                "lambda\tлямбда",
                "mu\tмю"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-dialogue.tsv"),
            [
                "Alice stood on the verge of collapse\tАлиса стояла на грани краха",
                "Alice lost the lantern\tОна потеряла фонарь",
                "I deposited money at the bank\tЯ отнёс сбережения в кредитную организацию",
                "They waited by the gate\tОни танцевали у ворот",
                "Abraham had eight children one of them was called Isaak\tУ Авраама было восемь детей; одного из них звали Исаак.",
                "The event starts at 1 p.m.\tСобытие начинается в час дня.",
                "The event starts at 9 p.m.\tСобытие начинается в девять вечера.",
                "Alice stood on the verge of alpha alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu\tАлиса стояла на грани: альфа бета гамма дельта эпсилон дзета эта тета йота каппа."
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-vn.tsv"),
            [
                "lantern\tфонарь",
                "on the verge of\tна грани",
                "party was a total blast\tвечеринка",
                "alpha\tальфа",
                "beta\tбета",
                "gamma\tгамма",
                "delta\tдельта",
                "epsilon\tэпсилон",
                "zeta\tдзета",
                "eta\tэта",
                "theta\tтета",
                "iota\tйота",
                "kappa\tкаппа",
                "lambda\tлямбда",
                "mu\tмю"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-vn-scaffold.tsv"),
            [
                "lantern\tфонарь",
                "on the verge of\tна грани",
                "party was a total blast\tвечеринка",
                "alpha\tальфа",
                "beta\tбета",
                "gamma\tгамма",
                "delta\tдельта",
                "epsilon\tэпсилон",
                "zeta\tдзета",
                "eta\tэта",
                "theta\tтета",
                "iota\tйота",
                "kappa\tкаппа",
                "lambda\tлямбда",
                "mu\tмю"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-senses.tsv"),
            [
                "lantern\tфонарь\ta portable light protected by a case\tcommon\tnoun",
                "bank\tбанк\ta financial institution\tcommon\tnoun",
                "bank\tберег\tland alongside a river\tcommon\tnoun",
                "lock\tзакрыть\tto make something closed\tcommon\tverb",
                "lock\tзакрывать\tto make something closed repeatedly\tcommon\tverb"
            ]);
        string[] neuralCapKeys =
        [
            "amber", "birch", "cedar", "denim", "ember", "fable", "gable",
            "hazel", "ivory", "joker", "karma", "lemon", "maple"
        ];
        await File.AppendAllLinesAsync(
            Path.Combine(bundled, "eng-rus.tsv"),
            neuralCapKeys.Select(key => $"{key}\tальфа; омега"));
        await File.AppendAllLinesAsync(
            Path.Combine(bundled, "eng-rus-senses.tsv"),
            neuralCapKeys.SelectMany(key => new[]
            {
                $"{key}\tальфа\tthe first synthetic sense of {key}\tcommon\tnoun",
                $"{key}\tомега\tthe second synthetic sense of {key}\tcommon\tnoun"
            }));
        // Two unrelated definitions that resolve to one visible surface: they
        // stay separate glosses inside a single outcome, so one accepted
        // request emits two hints and the hint cap is reachable below the
        // per-sentence request cap.
        string[] senseHintCapKeys =
        [
            "opal", "quartz", "raven", "sable", "topaz", "umber", "viola"
        ];
        await File.AppendAllLinesAsync(
            Path.Combine(bundled, "eng-rus.tsv"),
            senseHintCapKeys.Select(key => $"{key}\tальфа; омега"));
        await File.AppendAllLinesAsync(
            Path.Combine(bundled, "eng-rus-senses.tsv"),
            senseHintCapKeys.SelectMany(key => new[]
            {
                $"{key}\tальфа\ta mineral prized by collectors near {key}\tcommon\tnoun",
                $"{key}\tальфа\tloud brass music played during {key} festivals\tcommon\tnoun",
                $"{key}\tомега\tthe second synthetic sense of {key}\tcommon\tnoun"
            }));
        await File.AppendAllLinesAsync(
            Path.Combine(bundled, "eng-rus.tsv"),
            [
                "acorn\tжелудь; вымысел",
                "basket\tкорзина; вымысел",
                "candle\tсвеча; вымысел",
                "dragon\tдракон; вымысел",
                "engine\tдвигатель; вымысел",
                "forest\tлес; вымысел",
                "garden\tсад; вымысел",
                "hammer\tмолоток; вымысел",
                "island\tостров; вымысел",
                "jacket\tкуртка; вымысел",
                "kettle\tчайник; вымысел",
                "meadow\tлуг; вымысел"
            ]);
        await File.AppendAllLinesAsync(
            Path.Combine(bundled, "eng-rus-dialogue.tsv"),
            [
                "Acorn basket candle dragon engine forest garden hammer island jacket kettle meadow wait\t" +
                "желудь корзина свеча дракон двигатель лес сад молоток остров куртка чайник луг"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "jpn-rus.tsv"),
            ["鳥\tптица"]);
        await File.WriteAllLinesAsync(
            Path.Combine(user, "eng-rus-user.tsv"),
            [
                "Alice\tАлиса",
                "wait\tожидать",
                "do it\tсделать это"
            ]);

        LocalDictionaryService dictionary = new(bundled, user);
        TranslationScaffoldBuilder builder = new(dictionary);

        const string exactSource = "Alice stood on the verge of collapse.";
        TranslationScaffold exact = await builder.BuildAsync(exactSource);
        Check(exact.ExactSentenceTranslation == "Алиса стояла на грани краха",
            $"An exact dialogue translation was not retained: {exact.ExactSentenceTranslation}");
        Check(exact.Terms.Any(term =>
                  term.Source == "on the verge of" &&
                  term.Translation == "на грани" &&
                  term.Confidence >= TranslationScaffoldBuilder.MinimumConfidence &&
                  term.Provenance == ScaffoldTermProvenance.Deterministic),
            "The deterministic scaffold omitted a multi-word expression.");
        Check(exact.Terms.Any(term =>
                  term.Source == "Alice" &&
                  term.Translation == "Алиса" &&
                  term.Provenance == ScaffoldTermProvenance.Deterministic) &&
              exact.ProperNames.SequenceEqual(["Alice"], StringComparer.Ordinal),
            "A capitalized user-dictionary key was not preserved as a proper name term.");

        TranslationScaffold semicolonExact = await builder.BuildAsync(
            "Abraham had eight children; one of them was called Isaak.");
        Check(semicolonExact.ExactSentenceTranslation ==
              "У Авраама было восемь детей; одного из них звали Исаак.",
            $"An internal semicolon truncated an exact dialogue sentence: {semicolonExact.ExactSentenceTranslation}");

        TranslationScaffold numericExact = await builder.BuildAsync(
            "The event starts at 9 p.m.");
        Check(numericExact.ExactSentenceTranslation is null,
            "A digit-stripping dialogue-key collision was allowed to bypass the model.");

        TranslationScaffold inflectedUser = await builder.BuildAsync(
            "They waited by the gate.");
        Check(inflectedUser.ExactSentenceTranslation is null &&
              inflectedUser.Terms.Any(term =>
                  term.Source == "waited" &&
                  term.Translation == "ожидать" &&
                  term.Provenance == ScaffoldTermProvenance.Deterministic),
            "An inflected user lemma lost its provenance or was overridden by dialogue data.");

        TranslationScaffold stopwordPhrase = await builder.BuildAsync("Please do it.");
        Check(stopwordPhrase.Terms.Any(term =>
                term.Source == "do it" && term.Translation == "сделать это"),
            "A user phrase made entirely of stopwords was skipped before dictionary lookup.");

        TranslationScaffold contextualParty = await builder.BuildAsync(
            "That party was a total blast.");
        Check(contextualParty.Terms.Any(term =>
                term.Source == "party was a total blast" &&
                term.Translation == "вечеринка"),
            "A context-specific curated phrase did not enter the scaffold.");
        TranslationScaffold politicalParty = await builder.BuildAsync(
            "The political party won the election.");
        Check(politicalParty.Terms.All(term => term.Translation != "вечеринка"),
            "A context-specific party sense leaked into political context.");

        TranslationScaffold uniqueSense = await builder.BuildAsync("The lantern glowed.");
        Check(uniqueSense.Terms is [{ Source: "lantern", Translation: "фонарь" }],
            "A trusted single deterministic sense did not enter the scaffold.");

        TranslationScaffold advisoryExact = await builder.BuildAsync(
            "I deposited money at the bank.");
        Check(advisoryExact.ExactSentenceTranslation ==
              "Я отнёс сбережения в кредитную организацию" &&
              advisoryExact.Terms is
              [
                  {
                      Source: "bank",
                      Translation: "банк",
                      Provenance: ScaffoldTermProvenance.AdvisoryDictionary
                  }
              ],
            "A broad heuristic synonym became mandatory or discarded a trusted exact sentence.");

        TranslationScaffold unresolved = await builder.BuildAsync("I saw the bank.");
        Check(unresolved.Terms.Count == 0,
            "An unresolved multi-sense dictionary entry must be omitted.");

        bool neuralCalled = false;
        TranslationScaffoldNeuralRequest? capturedNeuralRequest = null;
        TranslationScaffoldBuilder neuralBuilder = new(
            dictionary,
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                neuralCalled = true;
                capturedNeuralRequest = request;
                Check(request.Plan.CandidateGroupIds.All(groupId =>
                        request.Plan.Outcomes.Any(outcome => outcome.GroupId == groupId)),
                    "Neural scaffold candidate GroupIds lost their outcome mapping.");
                SmartDictionaryRankerOutcome selected = request.Plan.Outcomes.Single(
                    outcome => outcome.Translation == "берег");
                return Task.FromResult(new TranslationScaffoldNeuralResult(
                    selected.GroupId,
                    Abstained: false,
                    Margin: 0.42,
                    Score: 0.81,
                    Confidence: 0.81,
                    GlossBertRankerProvider.Cpu,
                    GlossBertDictionarySelectorService.CpuExecutionProvider,
                    "CPU",
                    GlossBertDictionarySelectorService.CpuModelCacheId + ":test",
                    "cross",
                    ElapsedMilliseconds: 7));
            });
        TranslationScaffold neural = await neuralBuilder.BuildAsync("I saw the bank.");
        Check(neuralCalled &&
              neural.Terms.Count == 0 &&
              neural.SenseHints is
              [
                  {
                      Source: "bank",
                      EnglishDefinition: "land alongside a river",
                      SelectedTranslation: "берег",
                      RejectedTranslations: ["банк"],
                      // The ranker's reported confidence, not the acceptance
                      // floor: the trust policy reads this number (card E3).
                      Confidence: 0.81,
                      Origin: SenseEvidenceOrigin.RankerAccepted
                  }
              ] &&
              neural.NeuralDecisions is
              [
              {
                  Status: TranslationScaffoldNeuralStatus.Selected,
                  SelectedTranslation: "берег",
                  SelectedGroupId: not null,
                  CandidateGroupCount: 2,
                  CandidateGlossCount: 2,
                  Margin: 0.42,
                  ElapsedMilliseconds: 7,
                  Provenance: ScaffoldTermProvenance.GlossBert
              }
              ],
            "An accepted margin-only neural sense became a hard term or lost its soft hint.");
        string scaffoldCacheKey = TranslationScaffoldGlossBertRanker.CreateCacheKey(
            capturedNeuralRequest!,
            GlossBertRankerProvider.Cpu,
            "model-v1",
            "dictionary-v1");
        Check(scaffoldCacheKey.Length == 64 &&
              scaffoldCacheKey == TranslationScaffoldGlossBertRanker.CreateCacheKey(
                  capturedNeuralRequest!,
                  GlossBertRankerProvider.Cpu,
                  "model-v1",
                  "dictionary-v1") &&
              scaffoldCacheKey != TranslationScaffoldGlossBertRanker.CreateCacheKey(
                  capturedNeuralRequest!,
                  GlossBertRankerProvider.Npu,
                  "model-v1",
                  "dictionary-v1") &&
              scaffoldCacheKey != TranslationScaffoldGlossBertRanker.CreateCacheKey(
                  capturedNeuralRequest!,
                  GlossBertRankerProvider.Cpu,
                  "model-v2",
                  "dictionary-v1") &&
              scaffoldCacheKey != TranslationScaffoldGlossBertRanker.CreateCacheKey(
                  capturedNeuralRequest!,
                  GlossBertRankerProvider.Cpu,
                  "model-v1",
                  "dictionary-v2") &&
              scaffoldCacheKey != TranslationScaffoldGlossBertRanker.CreateCacheKey(
                  capturedNeuralRequest! with { Sentence = "I crossed the bank." },
                  GlossBertRankerProvider.Cpu,
                  "model-v1",
                  "dictionary-v1"),
            "The scaffold ranker cache key omitted provider/model/dictionary/context identity.");

        int duplicateNeuralCalls = 0;
        TranslationScaffoldBuilder duplicateNeuralBuilder = new(
            dictionary,
            (request, _) =>
            {
                duplicateNeuralCalls++;
                return Task.FromResult(new TranslationScaffoldNeuralResult(
                    request.Plan.Outcomes[0].GroupId,
                    Abstained: false,
                    Margin: 0.5,
                    Score: 0.7,
                    Confidence: 0.7,
                    GlossBertRankerProvider.Cpu,
                    GlossBertDictionarySelectorService.CpuExecutionProvider,
                    "CPU",
                    "test-model",
                    "cross",
                    ElapsedMilliseconds: 1));
            });
        TranslationScaffold duplicateNeural = await duplicateNeuralBuilder.BuildAsync(
            "The bank faced the bank.");
        Check(duplicateNeuralCalls == 1 &&
              duplicateNeural.Terms.Count == 0 &&
              duplicateNeural.SenseHints.Count == 1 &&
              duplicateNeural.NeuralDecisions.Count(decision =>
                  decision.Status == TranslationScaffoldNeuralStatus.Selected) == 1 &&
              duplicateNeural.NeuralDecisions.Count(decision =>
                  decision.Status == TranslationScaffoldNeuralStatus.Skipped &&
                  decision.ErrorCode == "duplicate-source") == 1,
            "A repeated normalized source triggered duplicate neural work.");

        int cappedNeuralCalls = 0;
        TranslationScaffoldBuilder cappedNeuralBuilder = new(
            dictionary,
            (request, _) =>
            {
                cappedNeuralCalls++;
                return Task.FromResult(new TranslationScaffoldNeuralResult(
                    request.Plan.Outcomes[0].GroupId,
                    Abstained: false,
                    Margin: 0.5,
                    Score: 0.7,
                    Confidence: 0.7,
                    GlossBertRankerProvider.Cpu,
                    GlossBertDictionarySelectorService.CpuExecutionProvider,
                    "CPU",
                    "test-model",
                    "cross",
                    ElapsedMilliseconds: 1));
            });
        TranslationScaffold cappedNeural = await cappedNeuralBuilder.BuildAsync(
            string.Join(' ', neuralCapKeys) + ".");
        Check(cappedNeuralCalls == TranslationScaffoldBuilder.MaximumNeuralRequestCount &&
              cappedNeural.Terms.Count == 0 &&
              cappedNeural.SenseHints.Count ==
              TranslationScaffoldBuilder.MaximumNeuralRequestCount &&
              cappedNeural.NeuralDecisions.Count(decision =>
                  decision.Status == TranslationScaffoldNeuralStatus.Skipped &&
                  decision.ErrorCode == "sentence-request-limit") == 1,
            "The per-sentence neural request cap was not enforced.");

        int hintCapNeuralCalls = 0;
        TranslationScaffoldBuilder hintCapBuilder = new(
            dictionary,
            (request, _) =>
            {
                hintCapNeuralCalls++;
                return Task.FromResult(new TranslationScaffoldNeuralResult(
                    request.Plan.Outcomes.Single(
                        outcome => outcome.Translation == "альфа").GroupId,
                    Abstained: false,
                    Margin: 0.5,
                    Score: 0.7,
                    Confidence: 0.7,
                    GlossBertRankerProvider.Cpu,
                    GlossBertDictionarySelectorService.CpuExecutionProvider,
                    "CPU",
                    "test-model",
                    "cross",
                    ElapsedMilliseconds: 1));
            });
        TranslationScaffold hintCapped = await hintCapBuilder.BuildAsync(
            string.Join(' ', senseHintCapKeys) + ".");
        Check(hintCapNeuralCalls == senseHintCapKeys.Length &&
              hintCapNeuralCalls < TranslationScaffoldBuilder.MaximumNeuralRequestCount,
            "The sense-hint cap case must stay below the neural request cap: " +
            $"{hintCapNeuralCalls} call(s).");
        Check(hintCapped.SenseHints.Count ==
              TranslationScaffoldBuilder.MaximumSenseHintCount,
            "Sense hints were not capped: " +
            $"{hintCapped.SenseHints.Count} hint(s) from {hintCapNeuralCalls} request(s).");
        Check(hintCapped.SenseHints
                .Select(hint => hint.Source)
                .Distinct(StringComparer.Ordinal)
                .SequenceEqual(
                    senseHintCapKeys.Take(
                        TranslationScaffoldBuilder.MaximumSenseHintCount / 2),
                    StringComparer.Ordinal),
            "Capped sense hints lost their stable sentence order.");

        TranslationScaffoldBuilder abstainingNeuralBuilder = new(
            dictionary,
            (_, _) => Task.FromResult(new TranslationScaffoldNeuralResult(
                GroupId: null,
                Abstained: true,
                Margin: 0.12,
                Score: 0.55,
                Confidence: 0.55,
                GlossBertRankerProvider.Cpu,
                GlossBertDictionarySelectorService.CpuExecutionProvider,
                "CPU",
                "test-model",
                "cross",
                ElapsedMilliseconds: 2)));
        TranslationScaffold abstainedNeural = await abstainingNeuralBuilder.BuildAsync(
            "I saw the bank.");
        Check(abstainedNeural.Terms.Count == 0 &&
              abstainedNeural.SenseHints.Count == 0 &&
              abstainedNeural.NeuralDecisions is
              [
              {
                  Status: TranslationScaffoldNeuralStatus.Abstained,
                  SelectedGroupId: null,
                  SelectedTranslation: null,
                  Margin: 0.12
              }],
            "A margin-only abstention was not preserved as a non-term decision.");

        ScaffoldSenseHint gateHint = new(
            "gate",
            "a movable barrier in a fence",
            "ворота",
            Array.AsReadOnly(new[] { "шлюз" }),
            TranslationScaffoldBuilder.MinimumConfidence);
        Check(NeuralSenseFidelityGuard.FindConflictingHints(
                  "Он ждал у ворот.",
                  [gateHint]).Count == 0,
            "An inflected selected neural surface was treated as a conflict.");

        ScaffoldSenseHint bankHint = new(
            "bank",
            "land alongside a river",
            "берег",
            Array.AsReadOnly(new[] { "банк" }),
            TranslationScaffoldBuilder.MinimumConfidence);
        ScaffoldSenseHint duplicateBankHint = bankHint with
        {
            EnglishDefinition = "the edge of a river"
        };
        IReadOnlyList<ScaffoldSenseHint> rejectedBank =
            NeuralSenseFidelityGuard.FindConflictingHints(
                "Она вошла в банк.",
                [bankHint, duplicateBankHint]);
        Check(rejectedBank is [{ Source: "bank" }],
            "An explicit rejected bank surface was not detected or source-deduplicated.");
        Check(NeuralSenseFidelityGuard.FindConflictingHints(
                  "Она ждала снаружи.",
                  [bankHint]).Count == 0,
            "Omitting both selected and rejected surfaces became a false conflict.");
        Check(NeuralSenseFidelityGuard.FindConflictingHints(
                  "После повтора он всё ещё работает в банке.",
                  [bankHint]).Count == 1,
            "A rejected inflected surface escaped the neural retry guard.");

        int failingNeuralCalls = 0;
        TranslationScaffoldBuilder failingNeuralBuilder = new(
            dictionary,
            (_, _) =>
            {
                failingNeuralCalls++;
                return Task.FromException<TranslationScaffoldNeuralResult>(
                    new InvalidOperationException("synthetic neural scaffold failure"));
            });
        TranslationScaffoldNeuralSession neuralSession = new();
        TranslationScaffold neuralFailedOpen = await failingNeuralBuilder.BuildAsync(
            "Alice stood on the verge of the bank and locked the gate.",
            neuralSession);
        Check(failingNeuralCalls == 1 &&
              neuralFailedOpen.Terms.Any(term => term.Source == "Alice") &&
              neuralFailedOpen.Terms.Any(term => term.Source == "on the verge of") &&
              neuralFailedOpen.Terms.All(term => term.Source is not "bank" and not "locked") &&
              neuralFailedOpen.NeuralDecisions.Any(decision =>
                  decision.Status == TranslationScaffoldNeuralStatus.Error &&
                  decision.ErrorCode == nameof(InvalidOperationException)) &&
              neuralFailedOpen.NeuralDecisions.Any(decision =>
                  decision.Status == TranslationScaffoldNeuralStatus.Skipped &&
                  decision.ErrorCode == "session-disabled-after-error"),
            "A neural failure retried within one sentence or discarded deterministic terms.");

        TranslationScaffold disabledAcrossOperation = await failingNeuralBuilder.BuildAsync(
            "I saw the bank.",
            neuralSession);
        Check(failingNeuralCalls == 1 &&
              disabledAcrossOperation.NeuralDecisions is
              [
                  {
                      Status: TranslationScaffoldNeuralStatus.Skipped,
                      ErrorCode: "session-disabled-after-error"
                  }],
            "A shared neural session retried after the first operation error.");

        using CancellationTokenSource neuralCancellation = new();
        TranslationScaffoldBuilder cancelingNeuralBuilder = new(
            dictionary,
            (_, cancellationToken) =>
            {
                neuralCancellation.Cancel();
                return Task.FromCanceled<TranslationScaffoldNeuralResult>(
                    cancellationToken);
            });
        try
        {
            await cancelingNeuralBuilder.BuildAsync(
                "I saw the bank.",
                neuralCancellation.Token);
            throw new InvalidOperationException("Neural scaffold cancellation was swallowed.");
        }
        catch (OperationCanceledException)
        {
            // A caller cancellation is not a ranker failure and must propagate.
        }

        TranslationScaffold lowConfidenceHeuristic = await builder.BuildAsync(
            "He locked the door.");
        Check(lowConfidenceHeuristic.Terms.All(term => term.Source != "locked"),
            "A 0.90-0.94 hover heuristic became a mandatory model constraint.");

        TranslationScaffold untrustedSingle = await builder.BuildAsync("A shrine stood there.");
        Check(untrustedSingle.Terms.Count == 0,
            "A lone broad-dictionary value must remain below the mandatory-confidence floor.");

        TranslationScaffold unsafeRussian = await builder.BuildAsync("The cipher vanished.");
        Check(unsafeRussian.Terms.Count == 0,
            "A mixed English/Russian dictionary value entered a mandatory scaffold.");

        TranslationScaffold conflictingDialogue =
            await builder.BuildAsync("Alice lost the lantern.");
        Check(conflictingDialogue.ExactSentenceTranslation is null &&
              conflictingDialogue.Terms.Any(term => term.Source == "Alice"),
            "A low-priority dialogue row overrode an authoritative user term.");

        const string cappedSource =
            "Alice stood on the verge of alpha alpha beta gamma delta epsilon zeta eta " +
            "theta iota kappa lambda mu.";
        TranslationScaffold capped = await builder.BuildAsync(cappedSource);
        Check(capped.Terms.Count == TranslationScaffoldBuilder.MaximumTermCount,
            $"The scaffold cap changed: {capped.Terms.Count}");
        Check(capped.Terms[0].Source == "on the verge of" &&
              capped.Terms[1].Source == "Alice",
            "Scaffold priority must be MWE, then user proper names, then words.");
        Check(capped.Terms.Select(term => LocalDictionaryService.NormalizeEnglish(term.Source))
                  .Distinct(StringComparer.Ordinal).Count() == capped.Terms.Count &&
              capped.Terms.Count(term => term.Source == "alpha") == 1,
            "Repeated lookups were not deduplicated stably before applying the cap.");
        Check(capped.ExactSentenceTranslation is null,
            "Exact dialogue bypass ignored an authoritative term beyond the twelve-term prompt cap.");

        const string advisoryCappedSource =
            "Acorn basket candle dragon engine forest garden hammer island jacket kettle meadow wait.";
        TranslationScaffold advisoryCapped = await builder.BuildAsync(advisoryCappedSource);
        Check(advisoryCapped.Terms.Count == TranslationScaffoldBuilder.MaximumTermCount &&
              advisoryCapped.Terms[^1] is
              {
                  Source: "wait",
                  Translation: "ожидать",
                  Provenance: ScaffoldTermProvenance.Deterministic
              } &&
              advisoryCapped.Terms.Count(term =>
                  term.Provenance == ScaffoldTermProvenance.AdvisoryDictionary) == 11 &&
              advisoryCapped.Terms.All(term => term.Source != "meadow"),
            "Early advisory terms evicted a later user term or changed stable scaffold order: " +
            string.Join(" | ", advisoryCapped.Terms.Select(term =>
                $"{term.Source}->{term.Translation} [{term.Provenance}]")));
        Check(advisoryCapped.ExactSentenceTranslation is null,
            "A dialogue shortcut bypassed a user term that was reserved inside the prompt cap.");

        string missingBundled = Path.Combine(scaffoldRoot, "missing-bundled");
        string missingUser = Path.Combine(scaffoldRoot, "missing-user");
        TranslationScaffoldBuilder failingBuilder = new(
            new LocalDictionaryService(missingBundled, missingUser));
        TranslationScaffold failedOpen = await failingBuilder.BuildAsync("A lantern glowed.");
        Check(ReferenceEquals(failedOpen, TranslationScaffold.Empty),
            "A dictionary failure did not degrade to the empty scaffold.");

        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        try
        {
            await builder.BuildAsync("A lantern glowed.", canceled.Token);
            throw new InvalidOperationException("Scaffold cancellation was swallowed.");
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the sole exception that must leave the builder.
        }
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
