using AsteriaScale.Interop;
using AsteriaScale.Models;
using AsteriaScale.Services;
using System.Drawing;

internal static class SmartDictionaryPhraseRoutingTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "smart-phrase-routing");
        string broadBundled = Path.Combine(root, "broad", "bundled");
        string broadUser = Path.Combine(root, "broad", "user");
        await WriteBaseDictionariesAsync(broadBundled, broadUser);

        LocalDictionaryService broadDictionary = new(broadBundled, broadUser);
        const string firstTimeContext = "It was my first time.";
        DictionaryLookupResult broadLookup =
            await broadDictionary.LookupWithSenseMetadataAsync(
                firstTimeContext,
                "first",
                firstTimeContext.IndexOf("first", StringComparison.Ordinal));
        Check(broadLookup.Found &&
              broadLookup.IsPhrase &&
              !broadLookup.IsTrustedPhrase &&
              broadLookup.HasMultipleMeanings &&
              broadLookup.SenseCandidates.Count == 2,
            $"An ambiguous broad phrase was marked ready for bypass: {broadLookup}");

        SmartHoverResult broadResult = await ResolveAsync(
            broadDictionary,
            firstTimeContext,
            "first",
            Path.Combine(root, "broad-cache.json"));
        Check(broadResult is
        {
            Source: "first time",
            Translation: "впервые",
            IsPhrase: true
        } &&
            broadResult.Engine == SmartDictionaryHeuristicRanker.TatoebaEngineLabel,
            $"An ambiguous broad phrase bypassed Smart routing: {broadResult}");

        const string quietContext = "We shared a quiet moment.";
        DictionaryLookupResult quietLookup = await broadDictionary.LookupAsync(
            quietContext,
            "quiet",
            quietContext.IndexOf("quiet", StringComparison.Ordinal));
        SmartHoverResult quietResult = await ResolveAsync(
            broadDictionary,
            quietContext,
            "quiet",
            Path.Combine(root, "quiet-cache.json"));
        Check(quietLookup.Found &&
              quietLookup.IsPhrase &&
              !quietLookup.IsTrustedPhrase &&
              !quietLookup.HasMultipleMeanings &&
              quietResult.Translation == "тихая минута" &&
              quietResult.Engine == LocalDictionaryService.EngineLabel &&
              quietResult.SelectedSenseHints.Count == 0,
            "A clean one-meaning broad phrase lost its fast dictionary bypass.");

        const string afterlifeContext =
            "You'll go to the afterlife after you grow old and die!";
        SmartHoverResult afterlifeResult = await ResolveAsync(
            broadDictionary,
            afterlifeContext,
            "afterlife",
            Path.Combine(root, "afterlife-cache.json"));
        Check(afterlifeResult is
        {
            Source: "afterlife",
            Translation: "загробная жизнь",
            Confidence: 1
        } &&
            afterlifeResult.SelectedSenseHints.Count > 0 &&
            afterlifeResult.SelectedSenseHints.All(hint =>
                hint.Source == "afterlife" &&
                hint.SelectedTranslation == "загробная жизнь" &&
                hint.RejectedTranslations.Count == 0),
            "A trusted single aligned sense was not forwarded as advisory phrase evidence.");

        string vnBundled = Path.Combine(root, "vn-hard", "bundled");
        string vnUser = Path.Combine(root, "vn-hard", "user");
        await WriteBaseDictionariesAsync(vnBundled, vnUser);
        await File.WriteAllLinesAsync(
            Path.Combine(vnBundled, "eng-rus-vn.tsv"),
            [
                "first time\tпервый раз",
                "i get that a lot\tя часто это слышу",
                "if you wish\tесли хотите; если желаете"
            ]);
        LocalDictionaryService vnDictionary = new(vnBundled, vnUser);
        await VerifyTrustedFirstTimeAsync(
            vnDictionary,
            firstTimeContext,
            Path.Combine(root, "vn-cache.json"));

        const string longTrustedContext = "I get that a lot.";
        foreach (string target in new[] { "get", "that" })
        {
            DictionaryLookupResult longTrusted =
                await vnDictionary.LookupWithSenseMetadataAsync(
                    longTrustedContext,
                    target,
                    longTrustedContext.IndexOf(target, StringComparison.Ordinal));
            SmartHoverResult longTrustedResult = await ResolveAsync(
                vnDictionary,
                longTrustedContext,
                target,
                Path.Combine(root, $"vn-{target}-cache.json"));
            Check(longTrusted is
            {
                Found: true,
                Source: "I get that a lot",
                Translation: "я часто это слышу",
                IsPhrase: true,
                IsTrustedPhrase: true,
                HasMultipleMeanings: false
            } &&
                longTrustedResult.Translation == "я часто это слышу" &&
                longTrustedResult.Engine == LocalDictionaryService.EngineLabel,
                $"A five-token VN-hard phrase was unreachable through '{target}'.");
        }

        const string nestedIdiomContext = "You may leave if you wish.";
        foreach (string target in new[] { "if", "wish" })
        {
            DictionaryLookupResult longest =
                await vnDictionary.LookupWithSenseMetadataAsync(
                    nestedIdiomContext,
                    target,
                    nestedIdiomContext.IndexOf(target, StringComparison.Ordinal));
            SmartHoverResult longestResult = await ResolveAsync(
                vnDictionary,
                nestedIdiomContext,
                target,
                Path.Combine(root, $"vn-if-you-wish-{target}-cache.json"));
            Check(longest is
            {
                Found: true,
                Source: "if you wish",
                Translation: "если хотите; если желаете",
                IsPhrase: true,
                IsTrustedPhrase: true
            } &&
                longest.SenseCandidates.Count == 0 &&
                longestResult.Source == "if you wish" &&
                longestResult.Engine == LocalDictionaryService.EngineLabel,
                $"The nested 'you wish' idiom beat the longer curated phrase through '{target}'.");
        }

        string userBundled = Path.Combine(root, "user", "bundled");
        string userDirectory = Path.Combine(root, "user", "user");
        await WriteBaseDictionariesAsync(userBundled, userDirectory);
        await File.WriteAllLinesAsync(
            Path.Combine(userDirectory, "eng-rus-user.tsv"),
            ["first time\tпервый раз"]);
        LocalDictionaryService userDictionary = new(userBundled, userDirectory);
        await VerifyTrustedFirstTimeAsync(
            userDictionary,
            firstTimeContext,
            Path.Combine(root, "user-cache.json"));
    }

    private static async Task VerifyTrustedFirstTimeAsync(
        LocalDictionaryService dictionary,
        string context,
        string cachePath)
    {
        DictionaryLookupResult lookup = await dictionary.LookupWithSenseMetadataAsync(
            context,
            "first",
            context.IndexOf("first", StringComparison.Ordinal));
        SmartHoverResult result = await ResolveAsync(
            dictionary,
            context,
            "first",
            cachePath);
        Check(lookup is
        {
            Found: true,
            Source: "first time",
            Translation: "первый раз",
            IsPhrase: true,
            IsTrustedPhrase: true,
            HasMultipleMeanings: false
        } &&
            lookup.SenseCandidates.Count == 0 &&
            result.Translation == "первый раз" &&
            result.Engine == LocalDictionaryService.EngineLabel,
            $"A trusted first-time override did not bypass with only its value: {lookup}; {result}");
    }

    private static async Task<SmartHoverResult> ResolveAsync(
        LocalDictionaryService dictionary,
        string context,
        string target,
        string cachePath)
    {
        await using GlossBertDictionarySelectorService selector = new(
            Path.GetDirectoryName(cachePath)!,
            TimeSpan.FromMinutes(1));
        await using SmartDictionaryResultCache cache = new(cachePath);
        await using GlossBertDecisionCache decisionCache = new(
            cachePath + ".decisions.json");
        using SmartHoverPipeline pipeline = new(
            new AppSettings
            {
                SmartDictionaryBackend = SmartDictionaryBackends.Cpu,
                SmartDictionarySourceLanguage = SmartDictionarySourceLanguages.English
            },
            dictionary,
            selector,
            cache,
            decisionCache,
            static _ => { });
        return await pipeline.ResolveSmartHoverAsync(
            new HoverOcrResult(
                context,
                target,
                context.IndexOf(target, StringComparison.Ordinal),
                Rectangle.Empty),
            SmartDictionaryBackends.Cpu,
            CancellationToken.None);
    }

    private static async Task WriteBaseDictionariesAsync(
        string bundled,
        string user)
    {
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(user);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus.tsv"),
            [
                "afterlife\tзагробная жизнь",
                "first time\tвпервые; первый половой акт",
                "quiet moment\tтихая минута",
                "you wish\tмечтать не вредно!"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-enwiktionary-senses.tsv"),
            [
                "afterlife\tзагробная жизнь\tlife after death\tcommon\tnoun",
                "first time\tвпервые\tthe initial occurrence of an event\tcommon\tphrase",
                "first time\tпервый половой акт\tan individual's first instance of sexual intercourse\tsexual\tphrase"
            ]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "eng-rus-dialogue.tsv"),
            ["it was my first time\tЯ столкнулся с этим впервые."]);
        await File.WriteAllLinesAsync(
            Path.Combine(bundled, "jpn-rus.tsv"),
            ["月\tлуна"]);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
