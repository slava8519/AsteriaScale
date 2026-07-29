using AsteriaScale.Services;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Keeps the four VN data roles independent: hard dictionary overrides remain
/// exact-surface/residual repairs, soft rows retain sense metadata, scaffold
/// anchors constrain full translation only, and dialogue rows match complete sentences.
/// </summary>
internal static class VnDictionaryLayerTests
{
    public static async Task RunAsync(string tempRoot)
    {
        await VerifyPackagedAssetsAsync(tempRoot);

        string root = Path.Combine(tempRoot, "vn-dictionary-layers");
        string bundled = Path.Combine(root, "bundled");
        string user = Path.Combine(root, "user");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);

        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus.tsv"),
            [
                "crimson promise\tбагровое обещание",
                "silent vow\tтихий обет",
                "moonlit\tлунный",
                "glimmered\tблестел",
                "lemming\tлемминг; пеструшка",
                "lolling\tбездельничать",
                "damning\tосуждающий",
                "waited\tждал",
                "saw\tпила",
                "promise\tобещание",
                "oath\tклятва",
                "bell\tколокол"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-vn.tsv"),
            [
                "crimson promise\tалая клятва",
                "silent vow\tтвёрдая клятва",
                "lol\tсмешно; ха-ха",
                "damn\tчёрт; проклятье"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-vn-soft.tsv"),
            [
                "moonlit\tозарённый луной",
                "glimmer\tмерцать",
                "lemme\tдай мне; позволь мне",
                "see\tвидеть"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-vn-senses.tsv"),
            [
                "crimson promise\tалая клятва\ta solemn promise associated with crimson\tcommon\tnoun",
                "moonlit\tлунный\treceiving light from the moon\tcommon\tadjective",
                "moonlit\tозарённый луной\tilluminated by moonlight\tliterary\tadjective",
                "moonlit\tзалитый лунным светом\tcovered in moonlight\tliterary\tadjective",
                "glimmer\tмерцать\tto shine with a faint unsteady light\tcommon\tverb",
                "lemme\tдай мне\ta colloquial contraction of let me\tcolloquial\tphrase",
                "lemme\tпозволь мне\ta colloquial contraction of let me\tcolloquial\tphrase",
                "see\tвидеть\tto perceive with the eyes\tcommon\tverb"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-vn-scaffold.tsv"),
            [
                "moon oath\tлунная клятва",
                "get separated from\tотделиться от",
                "get pulled away from\tоторваться от",
                "prancing\tскачущий"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-massive-dialogue.tsv"),
            [
                "the bell rang at midnight\tВ полночь прозвенел колокол.",
                "evelyn broke the moon oath\tОна всё забыла."
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "jpn-rus.tsv"),
            [
                "月\tлуна"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(user, "eng-rus-user.tsv"),
            [
                "silent vow\tпользовательская клятва",
                "Evelyn\tЭвелин",
                "got separated from\tрасстались с",
                "got pulled away\tвариант один; вариант два",
                "wait\tожидать",
                "prance\tгарцевать"
            ]);

        LocalDictionaryService dictionary = new(bundled, user);
        await dictionary.LoadAsync();

        await VerifyHardAndSoftLayersAsync(dictionary);
        await VerifyScaffoldLayerAsync(dictionary);
        await VerifyDialogueIsolationAsync(dictionary);
        await VerifyConflictingExactDialogueAsync(dictionary);
    }

    private static async Task VerifyHardAndSoftLayersAsync(
        LocalDictionaryService dictionary)
    {
        const string hardContext = "She remembered the crimson promise.";
        DictionaryLookupResult hard = await dictionary.LookupWithSenseMetadataAsync(
            hardContext,
            "crimson",
            hardContext.IndexOf("crimson", StringComparison.Ordinal));
        Check(hard is
        {
            Found: true,
            IsPhrase: true,
            Translation: "алая клятва"
        },
            $"The hard VN row did not override the broad layer: {hard}");
        Check(hard.SenseCandidates.Count == 0,
            "A hard VN override retained broad/sense-sidecar candidates.");

        TranslationScaffold hardScaffold =
            await new TranslationScaffoldBuilder(dictionary).BuildAsync(hardContext);
        Check(!hardScaffold.Terms.Any(term =>
                string.Equals(
                    LocalDictionaryService.NormalizeEnglish(term.Source),
                    "crimson promise",
                    StringComparison.Ordinal)),
            "A display-only hard VN override became a mandatory scaffold term.");

        const string userContext = "They renewed their silent vow.";
        DictionaryLookupResult user = await dictionary.LookupAsync(
            userContext,
            "vow",
            userContext.IndexOf("vow", StringComparison.Ordinal));
        Check(user is
        {
            Found: true,
            IsPhrase: true,
            Translation: "пользовательская клятва"
        },
            $"A hard VN row overrode the authoritative user layer: {user}");

        const string softContext = "Moonlit branches crossed the window.";
        DictionaryLookupResult soft = await dictionary.LookupWithSenseMetadataAsync(
            softContext,
            "Moonlit",
            0);
        string[] softMeanings =
            NpuDictionarySelectorService.SplitMeanings(soft.Translation);
        Check(soft.Found &&
              softMeanings.SequenceEqual(
                  ["лунный", "озарённый луной"],
                  StringComparer.Ordinal),
            $"The soft VN surface did not merge after the broad value: {soft.Translation}");
        Check(soft.SenseCandidates.Count == 3 &&
              soft.SenseCandidates.Select(candidate => candidate.Translation)
                  .SequenceEqual(
                      ["лунный", "озарённый луной", "залитый лунным светом"],
                      StringComparer.Ordinal),
            "The soft VN layer lost or reordered its definition-aligned senses.");
        Check(!dictionary.IsPreferredEnglishKey("moonlit"),
            "A soft VN key was promoted to an authoritative preferred key.");

        const string inflectedContext = "A candle glimmered in the hall.";
        DictionaryLookupResult inflectedSoft =
            await dictionary.LookupWithSenseMetadataAsync(
                inflectedContext,
                "glimmered",
                inflectedContext.IndexOf("glimmered", StringComparison.Ordinal));
        Check(inflectedSoft.Found &&
              inflectedSoft.Translation == "мерцать" &&
              inflectedSoft.SenseCandidates is
              [{ Translation: "мерцать", PartOfSpeech: "verb" }],
            "An exact broad inflection shadowed its VN-soft lemma and sense metadata.");

        const string homographContext = "The saw was sharp.";
        DictionaryLookupResult homograph =
            await dictionary.LookupWithSenseMetadataAsync(
                homographContext,
                "saw",
                homographContext.IndexOf("saw", StringComparison.Ordinal));
        Check(homograph.Found &&
              homograph.Translation == "пила" &&
              homograph.SenseCandidates.Count == 0,
            "An irregular VN-soft lemma displaced an exact broad homograph.");

        const string suffixCollisionContext = "The lemming was small.";
        DictionaryLookupResult suffixCollision =
            await dictionary.LookupWithSenseMetadataAsync(
                suffixCollisionContext,
                "lemming",
                suffixCollisionContext.IndexOf("lemming", StringComparison.Ordinal));
        Check(suffixCollision.Found &&
              suffixCollision.Translation == "лемминг; пеструшка" &&
              suffixCollision.SenseCandidates.Count == 0,
            "A heuristic -ing candidate displaced an exact broad noun.");

        DictionaryLookupResult hardSuffixCollision = await dictionary.LookupAsync(
            "He was lolling on the sofa.",
            "lolling",
            "He was lolling on the sofa.".IndexOf("lolling", StringComparison.Ordinal));
        Check(hardSuffixCollision is
        {
            Found: true,
            Source: "lolling",
            Translation: "бездельничать"
        },
            $"A VN-hard acronym displaced an exact broad -ing form: {hardSuffixCollision}");

        DictionaryLookupResult hardParticipleCollision = await dictionary.LookupAsync(
            "The report contained damning evidence.",
            "damning",
            "The report contained damning evidence.".IndexOf("damning", StringComparison.Ordinal));
        Check(hardParticipleCollision is
        {
            Found: true,
            Source: "damning",
            Translation: "осуждающий"
        },
            $"A VN-hard interjection displaced an exact broad participle: {hardParticipleCollision}");

        DictionaryLookupResult hardDerivedOnly = await dictionary.LookupAsync(
            "They lolled after reading it.",
            "lolled",
            "They lolled after reading it.".IndexOf("lolled", StringComparison.Ordinal));
        Check(!hardDerivedOnly.Found,
            $"A derived VN-hard key was treated as a declared lemma: {hardDerivedOnly}");

        DictionaryLookupResult userLemma = await dictionary.LookupAsync(
            "They waited by the gate.",
            "waited",
            "They waited by the gate.".IndexOf("waited", StringComparison.Ordinal));
        Check(userLemma is
        {
            Found: true,
            Source: "waited",
            Translation: "ожидать"
        },
            $"A safe user lemma stopped overriding an exact broad form: {userLemma}");
    }

    private static async Task VerifyScaffoldLayerAsync(
        LocalDictionaryService dictionary)
    {
        const string source = "She still believed in the moon oath.";
        int anchorIndex = source.IndexOf("moon oath", StringComparison.Ordinal);
        var matches = await dictionary.FindEnglishScaffoldMatchesAsync(
            source,
            CancellationToken.None);
        ScaffoldDictionaryMatch match = matches.Single(value =>
            string.Equals(value.Source, "moon oath", StringComparison.Ordinal));
        Check(match.Translation == "лунная клятва" &&
              match.CharacterIndex == anchorIndex,
            "The dedicated scaffold layer returned the wrong match or position.");

        DictionaryLookupResult lookup = await dictionary.LookupAsync(
            source,
            "oath",
            source.IndexOf("oath", StringComparison.Ordinal));
        Check(lookup is
        {
            Found: true,
            IsPhrase: false,
            Source: "oath",
            Translation: "клятва"
        },
            $"A scaffold-only anchor leaked into ordinary dictionary lookup: {lookup}");

        TranslationScaffold scaffold =
            await new TranslationScaffoldBuilder(dictionary).BuildAsync(source);
        Check(scaffold.Terms.Any(term =>
                term.Source == "moon oath" &&
                term.Translation == "лунная клятва" &&
                term.Confidence >= TranslationScaffoldBuilder.MinimumConfidence),
            "A dedicated scaffold anchor did not become a mandatory term.");

        const string inflectedSource = "They got separated from us.";
        IReadOnlyList<ScaffoldDictionaryMatch> inflectedMatches =
            await dictionary.FindEnglishScaffoldMatchesAsync(inflectedSource);
        ScaffoldDictionaryMatch inflectedMatch = inflectedMatches.Single(match =>
            match.MatchedEnglishKey == "get separated from");
        Check(inflectedMatch.Source == "got separated from" &&
              inflectedMatch.CharacterLength == "got separated from".Length,
            "An inflected scaffold match lost its actual sentence surface or span.");

        TranslationScaffold userOverride =
            await new TranslationScaffoldBuilder(dictionary).BuildAsync(inflectedSource);
        Check(userOverride.Terms.Any(term =>
                  term.Source == "got separated from" &&
                  term.Translation == "расстались с") &&
              !userOverride.Terms.Any(term =>
                  term.Translation == "отделиться от"),
            "An inflected scaffold anchor overrode an overlapping user phrase.");

        const string ambiguousUserSource = "They got pulled away from us.";
        DictionaryLookupResult ambiguousUser = await dictionary.LookupAsync(
            ambiguousUserSource,
            "pulled",
            ambiguousUserSource.IndexOf("pulled", StringComparison.Ordinal));
        Check(ambiguousUser.Found &&
              ambiguousUser.MatchedEnglishKey == "got pulled away" &&
              NpuDictionarySelectorService.SplitMeanings(ambiguousUser.Translation).Length == 2,
            "The overlap regression did not exercise an unresolved user phrase.");

        TranslationScaffold ambiguousOverride =
            await new TranslationScaffoldBuilder(dictionary).BuildAsync(
                ambiguousUserSource);
        Check(!ambiguousOverride.Terms.Any(term =>
                  term.Translation == "оторваться от"),
            "A scaffold anchor survived an overlapping user phrase after user abstention.");

        // The scaffold surface and the user key are different strings at load
        // time ("prancing" against the lemma "prance"), so neither layer sees a
        // conflict. They only collide on the sentence surface, where the user
        // entry must win instead of both candidates cancelling each other out.
        const string lemmaCollisionSource = "She was prancing about.";
        DictionaryLookupResult lemmaCollisionLookup = await dictionary.LookupAsync(
            lemmaCollisionSource,
            "prancing",
            lemmaCollisionSource.IndexOf("prancing", StringComparison.Ordinal));
        Check(lemmaCollisionLookup is
        {
            Found: true,
            Source: "prancing",
            MatchedEnglishKey: "prance",
            Translation: "гарцевать"
        },
            "The lemma-collision regression did not exercise a user lemma behind " +
            $"an inflected scaffold surface: {lemmaCollisionLookup}");
        Check((await dictionary.FindEnglishScaffoldMatchesAsync(lemmaCollisionSource))
                .Any(match => match.MatchedEnglishKey == "prancing"),
            "The lemma-collision regression did not exercise a scaffold surface.");

        TranslationScaffold lemmaCollision =
            await new TranslationScaffoldBuilder(dictionary).BuildAsync(
                lemmaCollisionSource);
        Check(lemmaCollision.Terms.Any(term =>
                  term.Source == "prancing" &&
                  term.Translation == "гарцевать" &&
                  term.Confidence >= TranslationScaffoldBuilder.MinimumConfidence),
            "A user entry lost its mandatory term to a colliding scaffold surface: " +
            string.Join(
                ", ",
                lemmaCollision.Terms.Select(term =>
                    term.Source + " -> " + term.Translation)));
        Check(!lemmaCollision.Terms.Any(term => term.Translation == "скачущий"),
            "A scaffold surface overrode an overlapping user lemma.");
    }

    private static async Task VerifyDialogueIsolationAsync(
        LocalDictionaryService dictionary)
    {
        const string exactSource = "The bell rang at midnight.";
        const string exactTranslation = "В полночь прозвенел колокол.";
        string? exact = await dictionary.LookupExactEnglishDialogueAsync(exactSource);
        Check(exact == exactTranslation,
            $"A full dialogue sentence was not found exactly: {exact}");

        TranslationScaffold exactScaffold =
            await new TranslationScaffoldBuilder(dictionary).BuildAsync(exactSource);
        Check(exactScaffold.ExactSentenceTranslation == exactTranslation,
            "An exact compatible VN dialogue did not reach the full-translation bypass.");

        const string embedded =
            "Everyone heard the bell rang at midnight outside the hall.";
        DictionaryLookupResult embeddedLookup = await dictionary.LookupAsync(
            embedded,
            "bell",
            embedded.IndexOf("bell", StringComparison.Ordinal));
        Check(embeddedLookup is
        {
            Found: true,
            IsPhrase: false,
            Translation: "колокол",
            ContextTranslation: ""
        },
            $"A dialogue sentence leaked into embedded lexical lookup: {embeddedLookup}");
        Check(await dictionary.LookupExactEnglishDialogueAsync(embedded) is null,
            "A dialogue sentence matched a larger source instead of the complete sentence.");
    }

    private static async Task VerifyConflictingExactDialogueAsync(
        LocalDictionaryService dictionary)
    {
        const string source = "Evelyn broke the moon oath.";
        TranslationScaffold scaffold =
            await new TranslationScaffoldBuilder(dictionary).BuildAsync(source);

        Check(scaffold.ExactSentenceTranslation is null,
            "A dialogue translation that violates user/scaffold terms bypassed generation.");
        Check(scaffold.Terms.Any(term =>
                  term.Source == "Evelyn" && term.Translation == "Эвелин") &&
              scaffold.Terms.Any(term =>
                  term.Source == "moon oath" &&
                  term.Translation == "лунная клятва"),
            "The conflicting exact-dialogue check lost a user or scaffold constraint.");
    }

    private static async Task VerifyPackagedAssetsAsync(string tempRoot)
    {
        string dictionaryDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Dictionaries");
        string manifestPath = Path.Combine(
            dictionaryDirectory,
            "vn-build-manifest.json");
        using JsonDocument manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(manifestPath, Encoding.UTF8));
        JsonElement root = manifest.RootElement;
        Check(root.GetProperty("schemaVersion").GetInt32() == 2,
            "The packaged VN manifest schema is stale.");
        JsonElement generator = root.GetProperty("generator");
        Check(generator.GetProperty("fileName").GetString() ==
              "build-dictionaries.py" &&
              generator.GetProperty("bytes").GetInt64() > 0 &&
              generator.GetProperty("sha256").GetString()?.Length == 64,
            "The packaged VN manifest does not pin its generator.");

        HashSet<string> expectedOutputs = new(StringComparer.Ordinal)
        {
            "eng-rus-massive-dialogue.tsv.gz",
            "eng-rus-vn-scaffold.tsv.gz",
            "eng-rus-vn-senses.tsv.gz",
            "eng-rus-vn-soft.tsv.gz",
            "eng-rus-vn.tsv.gz",
            "vn-dialogue-provenance.tsv.gz",
            "vn-provenance.tsv.gz"
        };
        JsonElement[] outputs = root.GetProperty("outputs")
            .EnumerateArray()
            .ToArray();
        Check(outputs.Length == expectedOutputs.Count &&
              outputs.Select(output => output.GetProperty("fileName").GetString())
                  .Where(fileName => fileName is not null)
                  .Select(fileName => fileName!)
                  .ToHashSet(StringComparer.Ordinal)
                  .SetEquals(expectedOutputs),
            "The packaged VN manifest describes a partial or unexpected inventory.");

        foreach (JsonElement output in outputs)
        {
            string fileName = output.GetProperty("fileName").GetString()
                ?? throw new InvalidDataException("VN manifest output has no fileName.");
            string path = Path.Combine(dictionaryDirectory, fileName);
            FileInfo file = new(path);
            Check(file.Exists && file.Length == output.GetProperty("bytes").GetInt64(),
                $"The packaged VN asset size does not match its manifest: {fileName}");
            await using (FileStream input = File.OpenRead(path))
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(input));
                Check(hash == output.GetProperty("sha256").GetString(),
                    $"The packaged VN asset hash does not match its manifest: {fileName}");
            }

            int rows = await CountGzipRowsAsync(
                path,
                fileName.StartsWith("vn-", StringComparison.Ordinal));
            Check(rows == output.GetProperty("rows").GetInt32(),
                $"The packaged VN asset row count does not match its manifest: {fileName}");
        }

        JsonElement sourceLock = root.GetProperty("inputs").GetProperty("sourceLock");
        string sourceLockName = sourceLock.GetProperty("fileName").GetString()
            ?? throw new InvalidDataException("VN manifest has no source-lock fileName.");
        string sourceLockPath = Path.Combine(dictionaryDirectory, sourceLockName);
        FileInfo sourceLockFile = new(sourceLockPath);
        Check(sourceLockFile.Exists &&
              sourceLockFile.Length == sourceLock.GetProperty("bytes").GetInt64(),
            "The packaged VN source lock is missing or has the wrong size.");
        await using FileStream sourceLockInput = File.OpenRead(sourceLockPath);
        string sourceLockHash = Convert.ToHexString(
            await SHA256.HashDataAsync(sourceLockInput));
        Check(sourceLockHash == sourceLock.GetProperty("sha256").GetString(),
            "The packaged VN source-lock hash does not match the manifest.");

        string isolatedUserDirectory = Path.Combine(
            tempRoot,
            "vn-packaged-lemma-user");
        Directory.CreateDirectory(isolatedUserDirectory);
        LocalDictionaryService dictionary = new(
            dictionaryDirectory,
            isolatedUserDirectory);
        DictionaryLookupResult lolling = await dictionary.LookupAsync(
            "He was lolling on the sofa.",
            "lolling",
            "He was lolling on the sofa.".IndexOf("lolling", StringComparison.Ordinal));
        Check(lolling.Found &&
              lolling.Source == "lolling" &&
              RussianMorphology.RemoveStressMarks(lolling.Translation)
                  .Contains("бездельничать", StringComparison.Ordinal) &&
              !lolling.Translation.Contains("ха-ха", StringComparison.Ordinal),
            $"The packaged VN-hard 'lol' entry displaced exact 'lolling': {lolling}");

        DictionaryLookupResult damning = await dictionary.LookupAsync(
            "The report contained damning evidence.",
            "damning",
            "The report contained damning evidence.".IndexOf("damning", StringComparison.Ordinal));
        Check(damning.Found &&
              damning.Source == "damning" &&
              damning.Translation != "чёрт; проклятье",
            $"The packaged VN-hard 'damn' entry displaced exact 'damning': {damning}");

        const string wishContext = "You may leave if you wish.";
        DictionaryLookupResult wish = await dictionary.LookupWithSenseMetadataAsync(
            wishContext,
            "wish",
            wishContext.IndexOf("wish", StringComparison.Ordinal));
        Check(wish is
        {
            Found: true,
            Source: "if you wish",
            IsPhrase: true,
            IsTrustedPhrase: true
        } &&
              wish.Translation.Contains("если хотите", StringComparison.Ordinal),
            $"The packaged longest phrase did not suppress nested 'you wish': {wish}");

        const string butterContext = "She buttered him up before the meeting.";
        DictionaryLookupResult butter = await dictionary.LookupWithSenseMetadataAsync(
            butterContext,
            "buttered",
            butterContext.IndexOf("buttered", StringComparison.Ordinal));
        Check(butter is
        {
            Found: true,
            IsPhrase: true,
            IsTrustedPhrase: false,
            MatchedEnglishKey: "butter someone up"
        } &&
              butter.Translation.Contains("льстить", StringComparison.Ordinal) &&
              butter.SenseCandidates.Count == 2,
            $"The packaged object-pronoun idiom pattern was unreachable: {butter}");

        const string feelContext = "This part always feels off.";
        DictionaryLookupResult feel = await dictionary.LookupWithSenseMetadataAsync(
            feelContext,
            "feels",
            feelContext.IndexOf("feels", StringComparison.Ordinal));
        Check(feel is
        {
            Found: true,
            IsPhrase: true,
            IsTrustedPhrase: false,
            HasMultipleMeanings: true,
            MatchedEnglishKey: "feel off"
        } &&
              feel.SenseCandidates.Count == 4 &&
              feel.SenseCandidates
                  .Select(candidate => candidate.EnglishDefinition)
                  .Distinct(StringComparer.Ordinal)
                  .Count() == 2,
            $"The packaged contextual 'feel off' senses were not rankable: {feel}");
        await dictionary.UnloadAsync();
    }

    private static async Task<int> CountGzipRowsAsync(string path, bool hasHeader)
    {
        await using FileStream file = File.OpenRead(path);
        await using GZipStream gzip = new(file, CompressionMode.Decompress);
        using StreamReader reader = new(gzip, Encoding.UTF8);
        int rows = 0;
        bool first = true;
        while (await reader.ReadLineAsync() is not null)
        {
            if (first && hasHeader)
            {
                first = false;
                continue;
            }

            first = false;
            rows++;
        }

        return rows;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
