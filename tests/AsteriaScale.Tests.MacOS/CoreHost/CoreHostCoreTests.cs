using System.Diagnostics;
using System.Text.Json.Nodes;
using AsteriaScale.Models;
using AsteriaScale.Services;

/// <summary>
/// "macOS: CoreHost answers a hover through Core". The production CoreHost
/// (no test commands) with real Core behind it: core.init refuses missing
/// morphology and dictionaries, reports the bundled data and writes the macOS
/// first-run settings; settings.update refuses what macOS cannot run and
/// nothing else is coerced, and one whose save fails changes nothing in Core
/// (values, revision, lifecycle); a hover while translation is off is
/// hover_disabled, a dictionary hover answers bank → «берег» with the Apple
/// Vision labels; history records only what hover.displayed reports;
/// «Мой словарь» edits reach the next hover; a speaker gender is saved through
/// Core, and speaker.set is transactional (a failed write changes no gender,
/// reports no save and leaves no save behind), refuses a game key Core's
/// normalization changes again on a second pass, and stores a re-edit under
/// the spelling the card carried as observedName; ranker errors carry their code
/// in Russian without a path; and with
/// the owner's ConSeC bundle read in place a smart hover ranks on ConSeC CPU
/// with the exact pinned identity. A launch of its own runs Apple Vision
/// observations through Core (CoreHostObservationTests: ocr.plan, both
/// modes, the frame cache, stale_frame). One more launch, with test commands
/// for test.delayNextWork (the pool starts the next work-lane request late),
/// pins the admission order: what the app sends right behind a settings
/// change sees it, ocr.plan included; at its end test.fakeCore holds a hover,
/// and ocr.plan must not wait for it. After every run nothing is
/// left outside the configured roots, and inside them only the files Core is
/// meant to keep.
/// </summary>
internal static class CoreHostCoreTests
{
    private const string Sentence = "He sat on the bank of the river.";
    private const string RankedSentence = "The pitcher threw a fast ball past him.";
    private const int DictionaryEntries = 881_924;
    private const int DictionarySources = 12;
    private const int MorphologyEntries = 154_527;
    private const string DictionaryLabel = "APPLE VISION  •  СЛОВАРЬ";
    private const string RankerLabel = "APPLE VISION  •  CPU-СМЫСЛ";
    private const string FirstRunStatus = "Первый запуск на macOS: модель значений — ConSeC (CPU)";
    private const string PhraseUnavailable = "TranslateGemma недоступна на этой платформе";

    // A dictionary-mode hover after enabling waits for Core to reload the
    // dictionaries it unloaded while translation was off.
    private const int HoverDeadlineMs = 30_000;
    private const int InitDeadlineMs = 60_000;

    // Long against the microseconds between two admissions, so a request that
    // read Core's state before the held-back change applied would miss it.
    private const int HoldBackMs = 500;

    // The admission-order launch mostly waits out its held-back requests, so
    // it runs beside the rest; WhenAll lets both finish before a failure is
    // reported, so no session outlives the check.
    public static Task RunAsync(string tempRoot) =>
        Task.WhenAll(VerifyAdmissionOrderAsync(tempRoot), RunInOrderAsync(tempRoot));

    private static async Task RunInOrderAsync(string tempRoot)
    {
        await Task.WhenAll(
            VerifyMissingMorphologyAsync(tempRoot),
            VerifyMissingDictionariesAsync(tempRoot),
            VerifyModelDirectoryErrorsAsync(tempRoot));
        await VerifyHoverSettingsAndHistoryAsync(tempRoot);
        await VerifySpeakerAsync(tempRoot);
        await CoreHostObservationTests.VerifyThroughCoreAsync(tempRoot, InitDeadlineMs, HoverDeadlineMs);
        await VerifyConsecInPlaceAsync(tempRoot);
    }

    // ---- data root --------------------------------------------------------

    private static async Task VerifyMissingMorphologyAsync(string tempRoot)
    {
        string dataRoot = Path.Combine(tempRoot, "corehost-data", "empty");
        Directory.CreateDirectory(dataRoot);
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "no-morphology", testCommands: false, dataRoot: dataRoot);
        CoreHostClient client = session.Client;
        await client.HelloAsync(expectTestCommands: false);
        await client.ErrorAsync(client.Send("core.init"), "morphology_missing", InitDeadlineMs);
        // A failed init leaves nothing usable and nothing written.
        await client.ErrorAsync(client.Send("settings.get"), "not_ready");
        await client.ErrorAsync(client.Send("hover.resolve", Hover(Sentence, "bank")), "not_ready");
        await client.ErrorAsync(client.Send("core.init"), "bad_request");
        await session.ShutdownAsync();
        CoreHostFixtures.Check(!Directory.EnumerateFileSystemEntries(dataRoot).Any(),
            "CoreHost wrote into an empty data root.");
    }

    private static async Task VerifyMissingDictionariesAsync(string tempRoot)
    {
        // Morphology alone, copied from the CoreHost build output (data, not a model).
        string dataRoot = Path.Combine(tempRoot, "corehost-data", "morphology-only");
        Directory.CreateDirectory(Path.Combine(dataRoot, "Morphology"));
        File.Copy(
            Path.Combine(CoreHostFixtures.DataRoot, "Morphology", "ru-morph.tsv.gz"),
            Path.Combine(dataRoot, "Morphology", "ru-morph.tsv.gz"));
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "no-dictionaries", testCommands: false, dataRoot: dataRoot);
        CoreHostClient client = session.Client;
        await client.HelloAsync(expectTestCommands: false);
        await client.ErrorAsync(client.Send("core.init"), "dictionary_missing", InitDeadlineMs);
        await client.ErrorAsync(client.Send("models.status"), "not_ready");
        await session.ShutdownAsync();
    }

    // ---- ranker errors ----------------------------------------------------

    private static async Task VerifyModelDirectoryErrorsAsync(string tempRoot)
    {
        string worker = MacWorkerTests.LocateWorkerApphost();
        string folder = Path.Combine(tempRoot, "corehost-models");
        string empty = Path.Combine(folder, "empty");
        string partial = Path.Combine(folder, "partial");
        string missing = Path.Combine(folder, "missing");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(Path.Combine(partial, "Consec"));
        (string link, string target) = CoreHostFixtures.CreateLinkToEmptyDirectory(tempRoot);
        await Task.WhenAll(
            ExpectSenseWarmUpErrorAsync(tempRoot, "models-empty", worker, empty, "consec_bundle_missing"),
            ExpectSenseWarmUpErrorAsync(tempRoot, "models-partial", worker, partial, "consec_manifest_missing"),
            ExpectSenseWarmUpErrorAsync(tempRoot, "models-link", worker, link, "consec_artifact_path_unsafe"),
            ExpectSenseWarmUpErrorAsync(tempRoot, "models-missing", worker, missing, "model_directory_missing"));
        CoreHostFixtures.Check(
            !Directory.EnumerateFileSystemEntries(empty).Any() &&
            Directory.EnumerateFileSystemEntries(partial).SequenceEqual([Path.Combine(partial, "Consec")]) &&
            !Directory.EnumerateFileSystemEntries(Path.Combine(partial, "Consec")).Any() &&
            !Directory.EnumerateFileSystemEntries(target).Any() &&
            !Path.Exists(missing),
            "CoreHost or the worker wrote into a model directory.");
    }

    private static async Task ExpectSenseWarmUpErrorAsync(
        string tempRoot,
        string label,
        string worker,
        string models,
        string code)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, label, testCommands: false,
            extraArguments: ["--ranker-worker", worker, "--ranker-models", models]);
        CoreHostClient client = session.Client;
        await client.HelloAsync(expectTestCommands: false);
        await client.ResultAsync(client.Send("core.init"), InitDeadlineMs);
        JsonObject error = await client.ErrorAsync(
            client.Send("models.warmUp", CoreHostFixtures.Payload(("targets", new JsonArray("sense")))),
            code,
            60_000);
        string message = error["message"]!.GetValue<string>();
        CheckRankerText(message, code);
        CoreHostFixtures.Check(error["detail"]!.GetValue<string>().Contains('/'),
            $"The {code} detail lost the path the log needs: {error.ToJsonString()}");
        JsonObject sense = (await client.ResultAsync(client.Send("models.status")))["sense"]!.AsObject();
        JsonObject? senseError = sense["error"] as JsonObject;
        CoreHostFixtures.Check(
            senseError is not null &&
            senseError["code"]!.GetValue<string>() == code &&
            senseError["message"]!.GetValue<string>() == message &&
            !sense["running"]!.GetValue<bool>(),
            $"models.status does not report the {code} the warm-up hit: {sense.ToJsonString()}");
        // Loading the dictionaries creates the empty «Мой словарь» folder.
        await session.ShutdownAsync("settings.json", "Dictionaries");
    }

    // M10 and rule 9: the status line names the code in Russian and never
    // shows a path (paths live in detail, for the log).
    private static void CheckRankerText(string message, string code) =>
        CoreHostFixtures.Check(
            message.Any(character => character is >= 'А' and <= 'я') &&
            message.Contains(code, StringComparison.Ordinal) &&
            !message.Contains('/'),
            $"The ranker error text for {code} must be Russian, name the code and hold no path: {message}");

    // ---- hover, settings, history, «Мой словарь», speaker ----------------

    private static async Task VerifyHoverSettingsAndHistoryAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "core-hover", testCommands: false);
        CoreHostClient client = session.Client;
        await client.HelloAsync(expectTestCommands: false);

        // Before core.init every Core command is not_ready.
        foreach (string command in new[] { "settings.get", "models.status", "history.list", "userDictionary.list" })
        {
            await client.ErrorAsync(client.Send(command), "not_ready");
        }

        await client.ErrorAsync(client.Send("hover.resolve", Hover(Sentence, "bank")), "not_ready");

        // core.init: the bundled data, and the first-run settings written explicitly.
        CoreHostClient.Pending init = client.Send("core.init");
        JsonObject initResult = await client.ResultAsync(init, InitDeadlineMs);
        JsonObject dictionary = initResult["dictionary"]!.AsObject();
        CoreHostFixtures.Check(
            dictionary["entries"]!.GetValue<int>() == DictionaryEntries &&
            dictionary["sources"]!.GetValue<int>() == DictionarySources &&
            initResult["morphologyEntries"]!.GetValue<int>() == MorphologyEntries,
            $"core.init reports other data than the bundle: {initResult.ToJsonString()}");
        CoreHostFixtures.Check(
            initResult["firstRun"]?.ToJsonString() == "{\"senseModel\":\"consec\",\"senseBackend\":\"cpu\",\"hoverEnabled\":false}" &&
            initResult["revision"]!.GetValue<long>() == 1,
            $"A fresh core.init must report the macOS first-run settings: {initResult.ToJsonString()}");
        JsonObject settings = initResult["settings"]!.AsObject();
        CoreHostFixtures.Check(
            settings.ToJsonString() ==
            "{\"hoverEnabled\":false,\"hoverDelayMs\":700,\"mode\":\"smart\"," +
            "\"sense\":{\"backend\":\"cpu\",\"model\":\"consec\",\"sourceLanguage\":\"auto\",\"keepWarm\":true}," +
            "\"phrase\":{\"mode\":\"off\",\"backend\":\"cpu\"}}",
            "The first-run settings differ from docs/MACOS.md T7: " + settings.ToJsonString());
        int firstRunLine = Array.FindIndex(client.Trace(), line =>
            line.Contains("\"event\":\"status\"", StringComparison.Ordinal) &&
            line.Contains("\"code\":\"first_run\"", StringComparison.Ordinal) &&
            line.Contains(FirstRunStatus, StringComparison.Ordinal));
        CoreHostFixtures.Check(firstRunLine >= 0 && firstRunLine < init.TerminalLine,
            "The first-run status must be reported before the core.init terminal.");
        await client.ErrorAsync(client.Send("core.init"), "bad_request");

        // settings.json reloads through Core with the first-run values, no
        // .broken copy, and the shared AppSettings defaults are unchanged.
        string settingsFile = Path.Combine(session.UserData, "settings.json");
        AppSettings saved = await new SettingsService(settingsFile).LoadAsync();
        AppSettings defaults = new();
        CoreHostFixtures.Check(
            saved.SmartDictionaryRankerModel == SmartDictionaryRankerModels.Consec &&
            saved.SmartDictionaryBackend == SmartDictionaryBackends.Cpu &&
            !saved.HoverTranslationEnabled &&
            defaults.SmartDictionaryRankerModel == SmartDictionaryRankerModels.GlossBert &&
            defaults.SmartDictionaryBackend == SmartDictionaryBackends.Auto,
            "settings.json does not hold the first-run choice, or the shared defaults changed.");
        CheckNoBrokenSettings(session.UserData);

        // Translation is off on a first run.
        await client.ErrorAsync(client.Send("hover.resolve", Hover(Sentence, "bank")), "hover_disabled");

        // Nothing is coerced: what macOS cannot run is unsupported, the rest invalid.
        await RefuseUpdateAsync(client, "unsupported_on_platform", 1, ("sense", CoreHostFixtures.Payload(("backend", "npu"))));
        await RefuseUpdateAsync(client, "unsupported_on_platform", 1, ("phrase", CoreHostFixtures.Payload(("mode", "translategemma-4b"))));
        await RefuseUpdateAsync(client, "invalid_settings", 1, ("sense", CoreHostFixtures.Payload(("backend", "metal"))));
        await RefuseUpdateAsync(client, "invalid_settings", 1, ("hoverDelayMs", 100));
        await RefuseUpdateAsync(client, "invalid_settings", 1, ("mode", "Smart"));
        await RefuseUpdateAsync(client, "invalid_settings", 1, ("sense", CoreHostFixtures.Payload(("model", "deberta"))));
        await RefuseUpdateAsync(client, "bad_request", 1, ("upscale", true));
        await RefuseUpdateAsync(client, "conflict", 0, ("hoverEnabled", true));
        JsonObject unchanged = await client.ResultAsync(client.Send("settings.get"));
        CoreHostFixtures.Check(unchanged["revision"]!.GetValue<long>() == 1 &&
                               unchanged["settings"]!.ToJsonString() == settings.ToJsonString(),
            "A refused settings.update changed the settings: " + unchanged.ToJsonString());
        await VerifyFailedSaveChangesNothingAsync(client, session.UserData, settingsFile, settings);

        // Enabled in dictionary mode; the hover sent right behind the update
        // already sees it.
        CoreHostClient.Pending enable = client.Send("settings.update",
            Update(1, ("hoverEnabled", true), ("mode", "dictionary"), ("hoverDelayMs", 500)));
        CoreHostClient.Pending hover = client.Send("hover.resolve", Hover(Sentence, "bank"));
        JsonObject enabled = await client.ResultAsync(enable);
        CoreHostFixtures.Check(
            enabled["revision"]!.GetValue<long>() == 2 &&
            enabled["settings"]!["hoverEnabled"]!.GetValue<bool>() &&
            enabled["settings"]!["mode"]!.GetValue<string>() == "dictionary" &&
            enabled["settings"]!["hoverDelayMs"]!.GetValue<int>() == 500,
            "settings.update did not apply: " + enabled.ToJsonString());
        JsonObject primary = await SinglePrimaryAsync(client, hover);
        string translation = primary["translation"]!.GetValue<string>();
        CoreHostFixtures.Check(
            primary["source"]!.GetValue<string>() == "bank" &&
            translation.Replace("́", "", StringComparison.Ordinal).Contains("берег", StringComparison.Ordinal) &&
            primary["found"]!.GetValue<bool>() &&
            primary["provenance"]!.GetValue<string>() == "dictionary" &&
            primary["engineLabel"]!.GetValue<string>() == DictionaryLabel &&
            primary["context"]!.GetValue<string>() == Sentence,
            "The dictionary hover over «bank» did not come through Core: " + primary.ToJsonString());
        AppSettings enabledOnDisk = await new SettingsService(settingsFile).LoadAsync();
        CoreHostFixtures.Check(
            enabledOnDisk.HoverTranslationEnabled &&
            enabledOnDisk.HoverTranslationMode == HoverTranslationModes.Dictionary &&
            enabledOnDisk.HoverTranslationDelayMs == 500,
            "settings.update answered before settings.json held the change.");
        CheckNoBrokenSettings(session.UserData);

        // A mode the settings do not select is refused, not switched to.
        JsonObject otherMode = Hover(Sentence, "bank");
        otherMode["mode"] = "smart";
        await client.ErrorAsync(client.Send("hover.resolve", otherMode), "bad_request");

        await VerifyHistoryAsync(client, hover.Id);
        await VerifyUserDictionaryAsync(client);

        // The status snapshot, and a ranker error without --ranker-* (the
        // Windows companion path beside CoreHost, which never exists here).
        JsonObject status = await client.ResultAsync(client.Send("models.status"));
        CoreHostFixtures.Check(
            status["dictionary"]!["loaded"]!.GetValue<bool>() &&
            status["dictionary"]!["entries"]!.GetValue<int>() == DictionaryEntries &&
            status["dictionary"]!["morphologyEntries"]!.GetValue<int>() == MorphologyEntries &&
            status["sense"]!["provider"]!.GetValue<string>() == "cpu" &&
            status["sense"]!["model"]!.GetValue<string>() == "consec" &&
            !status["sense"]!["running"]!.GetValue<bool>() &&
            status["sense"]!["error"] is null &&
            !status["phrase"]!["available"]!.GetValue<bool>() &&
            status["phrase"]!["status"]!.GetValue<string>() == PhraseUnavailable,
            "models.status does not describe this host: " + status.ToJsonString());
        await client.ErrorAsync(client.Send("models.warmUp", CoreHostFixtures.Payload(("targets", new JsonArray("gpu")))), "bad_request");
        JsonObject workerMissing = await client.ErrorAsync(
            client.Send("models.warmUp", CoreHostFixtures.Payload(("targets", new JsonArray("sense")))),
            "worker_missing");
        CheckRankerText(workerMissing["message"]!.GetValue<string>(), "worker_missing");
        JsonObject afterError = await client.ResultAsync(client.Send("models.status"));
        CoreHostFixtures.Check(
            afterError["sense"]!["error"]!["code"]!.GetValue<string>() == "worker_missing" &&
            afterError["sense"]!["error"]!["detail"]!.GetValue<string>().Contains('/'),
            "models.status does not keep the worker error with its detail: " + afterError.ToJsonString());
        JsonObject unloaded = await client.ResultAsync(client.Send("models.unload"), 30_000);
        CoreHostFixtures.Check(unloaded["dictionary"]!["loaded"]!.GetValue<bool>(),
            "models.unload unloaded the dictionaries the enabled dictionary mode needs: " + unloaded.ToJsonString());

        CheckLabelsAndStatuses(client);
        await session.ShutdownAsync(
            "settings.json",
            "translation-history.v1.json",
            "Dictionaries",
            Path.Combine("Dictionaries", "eng-rus-user.tsv"));
    }

    // settings.update is transactional: when settings.json cannot be written
    // (here the user-data folder is read-only), the answer is
    // settings_save_failed and Core keeps everything as it was: the values
    // and the revision settings.get reports, the file, and the lifecycle
    // (translation stays off, so a hover is still hover_disabled). The app
    // keeps showing its previous snapshot after a refusal, so anything else
    // would show the user a state CoreHost no longer has.
    private static async Task VerifyFailedSaveChangesNothingAsync(
        CoreHostClient client,
        string userData,
        string settingsFile,
        JsonObject settings)
    {
        // The subset runs only on macOS; this guard is what the platform
        // analyzer (CA1416, a build error) needs before the Unix file modes.
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The CoreHost checks run only on macOS.");
        }

        byte[] before = await File.ReadAllBytesAsync(settingsFile);
        UnixFileMode mode = File.GetUnixFileMode(userData);
        File.SetUnixFileMode(userData, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await RefuseUpdateAsync(client, "settings_save_failed", 1,
                ("hoverEnabled", true), ("mode", "dictionary"), ("hoverDelayMs", 900));
        }
        finally
        {
            File.SetUnixFileMode(userData, mode);
        }

        JsonObject after = await client.ResultAsync(client.Send("settings.get"));
        CoreHostFixtures.Check(after["revision"]!.GetValue<long>() == 1 &&
                               after["settings"]!.ToJsonString() == settings.ToJsonString(),
            "A settings.update whose save failed still changed Core's settings: " + after.ToJsonString());
        CoreHostFixtures.Check((await File.ReadAllBytesAsync(settingsFile)).AsSpan().SequenceEqual(before),
            "A settings.update whose save failed changed settings.json.");
        await client.ErrorAsync(client.Send("hover.resolve", Hover(Sentence, "bank")), "hover_disabled");
    }

    private static async Task VerifyHistoryAsync(CoreHostClient client, long hoverId)
    {
        // Only what the app reports as shown reaches history, once per part.
        JsonObject before = await client.ResultAsync(client.Send("history.list"));
        CoreHostFixtures.Check(before["entries"]!.AsArray().Count == 0,
            "A hover reached history before hover.displayed: " + before.ToJsonString());
        await client.ErrorAsync(client.Send("hover.displayed", CoreHostFixtures.Payload(("hoverId", hoverId), ("part", "card"))), "bad_request");
        CoreHostFixtures.Check(await DisplayedAsync(client, hoverId, "primary"), "The shown primary card was not recorded.");
        CoreHostFixtures.Check(!await DisplayedAsync(client, hoverId, "primary"), "The same card was recorded twice.");
        CoreHostFixtures.Check(!await DisplayedAsync(client, hoverId, "phrase"), "A phrase line nobody produced was recorded.");
        CoreHostFixtures.Check(!await DisplayedAsync(client, 999_999, "primary"), "An unknown hover was recorded.");
        JsonArray entries = (await client.ResultAsync(client.Send("history.list")))["entries"]!.AsArray();
        CoreHostFixtures.Check(
            entries.Count == 1 &&
            entries[0]!["source"]!.GetValue<string>() == "bank" &&
            entries[0]!["engine"]!.GetValue<string>() == "APPLE VISION • СЛОВАРЬ" &&
            entries[0]!["context"]!.GetValue<string>() == Sentence &&
            entries[0]!["language"]!.GetValue<string>() == "EN",
            "History does not hold exactly the shown card: " + entries.ToJsonString());
        JsonObject anki = await client.ResultAsync(client.Send("history.exportAnki"));
        string tsv = anki["tsv"]!.GetValue<string>();
        CoreHostFixtures.Check(
            anki["count"]!.GetValue<int>() == 1 &&
            tsv.StartsWith("bank\t", StringComparison.Ordinal) &&
            tsv.EndsWith("\t" + Sentence + "\n", StringComparison.Ordinal) &&
            !tsv.StartsWith('﻿'),
            "The Anki export is not the history as text: " + anki.ToJsonString());
        JsonObject cleared = await client.ResultAsync(client.Send("history.clear"));
        JsonObject after = await client.ResultAsync(client.Send("history.list"));
        CoreHostFixtures.Check(cleared["cleared"]!.GetValue<bool>() && after["entries"]!.AsArray().Count == 0,
            "history.clear left entries: " + after.ToJsonString());
    }

    private static async Task VerifyUserDictionaryAsync(CoreHostClient client)
    {
        // A user entry outranks the bundle and reaches the very next hover.
        const string Text = "A   zyzzyvax sat there.";
        JsonObject savedEntry = await client.ResultAsync(client.Send("userDictionary.save", CoreHostFixtures.Payload(
            ("key", "Zyzzyvax"), ("translation", "тестовое слово"))), 30_000);
        CoreHostFixtures.Check(savedEntry["key"]!.GetValue<string>() == "zyzzyvax",
            "userDictionary.save did not return Core's canonical key: " + savedEntry.ToJsonString());
        JsonArray listed = (await client.ResultAsync(client.Send("userDictionary.list")))["entries"]!.AsArray();
        CoreHostFixtures.Check(
            listed.Count == 1 &&
            listed[0]!["key"]!.GetValue<string>() == "zyzzyvax" &&
            listed[0]!["translation"]!.GetValue<string>() == "тестовое слово" &&
            listed[0]!["language"]!.GetValue<string>() == "EN",
            "«Мой словарь» does not list the saved entry: " + listed.ToJsonString());
        // The index is in UTF-16 units of the text as sent; collapsing the
        // run of spaces must keep it on the same word.
        JsonObject primary = await SinglePrimaryAsync(client, client.Send("hover.resolve", Hover(Text, "zyzzyvax")));
        CoreHostFixtures.Check(
            primary["translation"]!.GetValue<string>() == "тестовое слово" &&
            primary["context"]!.GetValue<string>() == "A zyzzyvax sat there.",
            "The next hover does not see the user entry: " + primary.ToJsonString());
        await client.ErrorAsync(client.Send("userDictionary.save", CoreHostFixtures.Payload(
            ("key", "#comment"), ("translation", "нет"))), "invalid_entry");

        // Smart mode takes the sentence around the hovered word, found by the
        // index the app sent: the white space collapsed before the word must
        // not push it into the next sentence. keepWarm off: no ranker starts.
        const string TwoSentences = "The                              zyzzyvax ran. Another sentence follows here.";
        JsonObject smart = await client.ResultAsync(client.Send("settings.update", Update(2,
            ("mode", "smart"),
            ("sense", CoreHostFixtures.Payload(("keepWarm", false))))));
        CoreHostFixtures.Check(smart["revision"]!.GetValue<long>() == 3 && smart["settings"]!["mode"]!.GetValue<string>() == "smart",
            "settings.update did not switch to smart mode: " + smart.ToJsonString());
        JsonObject sentence = await SinglePrimaryAsync(client, client.Send("hover.resolve", Hover(TwoSentences, "zyzzyvax")));
        CoreHostFixtures.Check(
            sentence["translation"]!.GetValue<string>() == "тестовое слово" &&
            sentence["context"]!.GetValue<string>() == "The zyzzyvax ran.",
            "The selection was not mapped onto the sentence that holds the hovered word: " + sentence.ToJsonString());
        JsonObject removed = await client.ResultAsync(client.Send("userDictionary.remove", CoreHostFixtures.Payload(("key", "zyzzyvax"))), 30_000);
        JsonObject again = await client.ResultAsync(client.Send("userDictionary.remove", CoreHostFixtures.Payload(("key", "zyzzyvax"))));
        CoreHostFixtures.Check(removed["removed"]!.GetValue<bool>() && !again["removed"]!.GetValue<bool>(),
            "userDictionary.remove did not remove exactly once.");
    }

    // ---- speaker gender ------------------------------------------------------

    private const string SpeakerGameKey = "process:asteria-test";

    // Its own launch: a speaker reaches the card only in smart mode, and a
    // smart hover without a ranker worker leaves worker_missing in
    // models.status, which the core-hover session checks is clear.
    private static async Task VerifySpeakerAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "core-speaker", testCommands: false);
        CoreHostClient client = session.Client;
        await client.HelloAsync(expectTestCommands: false);
        await client.ResultAsync(client.Send("core.init"), InitDeadlineMs);
        // keepWarm off: no ranker starts; each hover answers rankerUnavailable.
        JsonObject smart = await client.ResultAsync(client.Send("settings.update", Update(1,
            ("hoverEnabled", true),
            ("mode", "smart"),
            ("sense", CoreHostFixtures.Payload(("keepWarm", false))))));
        CoreHostFixtures.Check(smart["revision"]!.GetValue<long>() == 2,
            "settings.update did not enable smart mode: " + smart.ToJsonString());
        string settingsFile = Path.Combine(session.UserData, "settings.json");

        JsonObject result = await client.ResultAsync(client.Send("speaker.set",
            SpeakerSet("Kotori", "Kotori", "feminine")));
        CoreHostFixtures.Check(
            result["speaker"]!.ToJsonString() ==
            "{\"name\":\"Kotori\",\"gender\":\"feminine\",\"gameKey\":\"process:asteria-test\",\"observedName\":\"Kotori\"}",
            "speaker.set did not resolve the saved speaker: " + result.ToJsonString());
        AppSettings saved = await new SettingsService(settingsFile).LoadAsync();
        CoreHostFixtures.Check(
            saved.SpeakerGenders.Any(preference =>
                preference.GameKey == SpeakerGameKey &&
                preference.SpeakerName == "Kotori" &&
                preference.Gender == "feminine"),
            "speaker.set answered before settings.json held the gender.");
        await client.ErrorAsync(client.Send("speaker.set", SpeakerSet("Kotori", "Kotori", "female")), "bad_request");
        JsonObject otherGame = SpeakerSet("Kotori", "Kotori", "feminine");
        otherGame["game"]!["key"] = "steam:42";
        await client.ErrorAsync(client.Send("speaker.set", otherGame), "bad_request");
        // A key Core's normalization changes again on a second pass never
        // resolves, and Core's own call after the swap would store the choice
        // again and save it itself; before the fix CoreHost stored it.
        foreach (string unstableKey in new[] { "process:asteria-test.exe.exe", "process:asteria-test .exe", "path:/Games/Asteria Test.exe \"" })
        {
            JsonObject unstable = SpeakerSet("Kotori", "Kotori", "masculine");
            unstable["game"]!["key"] = unstableKey;
            await client.ErrorAsync(client.Send("speaker.set", unstable), "bad_request");
        }

        await VerifyFailedSpeakerSaveChangesNothingAsync(client, session.UserData, settingsFile);
        await VerifySpeakerReEditAsync(client, settingsFile);
        CheckLabelsAndStatuses(client);
        await session.ShutdownAsync("settings.json", "Dictionaries");
        // Core's own save after speaker.set would mean CoreHost's copy of
        // Core's alias rule fell behind it; CoreHost logs that save. Read once
        // the shutdown closed stderr, StderrTail keeps the lines under 512
        // characters and only the last 8 to 16 thousand characters of them,
        // which still hold this session's whole stderr (a few short lines).
        CoreHostFixtures.Check(
            !client.StderrTail.Contains("Core saved the settings itself", StringComparison.Ordinal),
            "Core queued a save of its own after speaker.set: " + client.StderrTail);
    }

    // speaker.set is transactional (docs/MACOS.md T30): a failed write of
    // settings.json is settings_save_failed and leaves the gender in memory
    // as it was. Before the fix Core stored the gender and reported it saved
    // before CoreHost wrote the file, and queued a save of its own: the next
    // hover saw the refused gender, and that save could still write it.
    private static async Task VerifyFailedSpeakerSaveChangesNothingAsync(
        CoreHostClient client,
        string userData,
        string settingsFile)
    {
        // The subset runs only on macOS; this guard is what the platform
        // analyzer (CA1416, a build error) needs before the Unix file modes.
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The CoreHost checks run only on macOS.");
        }

        byte[] before = await File.ReadAllBytesAsync(settingsFile);
        DateTime writtenBefore = File.GetLastWriteTimeUtc(settingsFile);
        int statusesBefore = client.Unsolicited.Count;
        UnixFileMode mode = File.GetUnixFileMode(userData);
        File.SetUnixFileMode(userData, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await client.ErrorAsync(client.Send("speaker.set", SpeakerSet("Kotori", "Kotori", "masculine")),
                "settings_save_failed");
        }
        finally
        {
            File.SetUnixFileMode(userData, mode);
        }

        // A refused settings.update runs under the same queue and gate, so a
        // save queued behind the failed change has had its turn once it
        // answers; the poll then gives a late one a bounded second more.
        await RefuseUpdateAsync(client, "conflict", 0, ("hoverDelayMs", 900));
        Stopwatch quiet = Stopwatch.StartNew();
        while (quiet.Elapsed < TimeSpan.FromSeconds(1))
        {
            CoreHostFixtures.Check(
                (await File.ReadAllBytesAsync(settingsFile)).AsSpan().SequenceEqual(before) &&
                File.GetLastWriteTimeUtc(settingsFile) == writtenBefore,
                $"settings.json was written after a speaker.set whose save failed ({quiet.ElapsedMilliseconds} ms after write access returned).");
            await Task.Delay(50);
        }

        JsonObject speaker = await SpeakerOfHoverAsync(client, "Kotori");
        CoreHostFixtures.Check(
            speaker["name"]!.GetValue<string>() == "Kotori" &&
            speaker["gender"]!.GetValue<string>() == "feminine",
            "After a speaker.set whose save failed the next hover resolved another gender: " + speaker.ToJsonString());

        string[] speakerStatuses = client.Unsolicited
            .Skip(statusesBefore)
            .Where(frame => frame["event"]!.GetValue<string>() == "status")
            .Select(frame => frame["payload"]!["text"]!.GetValue<string>())
            .Where(text =>
                (text.Contains(": сохранён ", StringComparison.Ordinal) && text.Contains(" род для ", StringComparison.Ordinal)) ||
                text.StartsWith("Не удалось сохранить род говорящего", StringComparison.Ordinal))
            .ToArray();
        CoreHostFixtures.Check(speakerStatuses.Length == 0,
            "A speaker.set whose save failed reported a save: " + string.Join(" | ", speakerStatuses));

        // With write access back the same choice saves.
        JsonObject retried = await client.ResultAsync(client.Send("speaker.set", SpeakerSet("Kotori", "Kotori", "masculine")));
        AppSettings saved = await new SettingsService(settingsFile).LoadAsync();
        CoreHostFixtures.Check(
            retried["speaker"]!["gender"]!.GetValue<string>() == "masculine" &&
            saved.SpeakerGenders.Any(preference =>
                preference.GameKey == SpeakerGameKey && preference.SpeakerName == "Kotori" && preference.Gender == "masculine"),
            "speaker.set did not save once write access returned: " + retried.ToJsonString());
    }

    // The re-edit defect (docs/MACOS.md T30): a card shows the corrected
    // name, so speaker.set must store the choice under the spelling OCR saw,
    // which the card carries as observedName, and use it as it is. Before
    // the fix CoreHost resolved observedName to the display name again, and
    // a second edit never reached the entry the next hover reads.
    private static async Task VerifySpeakerReEditAsync(CoreHostClient client, string settingsFile)
    {
        const string Observed = "Hlnata";
        const string Corrected = "Hinata";
        JsonObject seen = await SpeakerOfHoverAsync(client, Observed);
        JsonObject first = await client.ResultAsync(client.Send("speaker.set",
            SpeakerSet(Observed, Corrected, "feminine")));
        JsonObject corrected = await SpeakerOfHoverAsync(client, Observed);
        CoreHostFixtures.Check(
            corrected["name"]!.GetValue<string>() == Corrected &&
            corrected["gender"]!.GetValue<string>() == "feminine",
            "The corrected name did not reach the next hover: " + corrected.ToJsonString());

        // The second edit, from the card that shows the corrected name.
        JsonObject reEdited = await client.ResultAsync(client.Send("speaker.set",
            SpeakerSet(Observed, Corrected, "masculine")));
        JsonObject after = await SpeakerOfHoverAsync(client, Observed);
        CoreHostFixtures.Check(
            after["name"]!.GetValue<string>() == Corrected &&
            after["gender"]!.GetValue<string>() == "masculine",
            $"A re-edit with the corrected name did not reach the OCR spelling «{Observed}»: " + after.ToJsonString());
        JsonObject alias = await SpeakerOfHoverAsync(client, Corrected);
        CoreHostFixtures.Check(alias["gender"]!.GetValue<string>() == "masculine",
            "A re-edit did not reach the corrected spelling: " + alias.ToJsonString());

        // The card carries the spelling OCR saw, whatever name it shows, and
        // speaker.set answers with the one it stored the choice under.
        string View(string name, string gender) =>
            $"{{\"name\":\"{name}\",\"gender\":\"{gender}\",\"gameKey\":\"{SpeakerGameKey}\",\"observedName\":\"{Observed}\"}}";
        CoreHostFixtures.Check(
            seen.ToJsonString() == View(Observed, "unknown") &&
            first["speaker"]!.ToJsonString() == View(Corrected, "feminine") &&
            corrected.ToJsonString() == View(Corrected, "feminine") &&
            reEdited["speaker"]!.ToJsonString() == View(Corrected, "masculine") &&
            after.ToJsonString() == View(Corrected, "masculine"),
            "The speaker views do not carry the OCR spelling: " + string.Join(" ",
                new[] { seen, first["speaker"]!, corrected, reEdited["speaker"]!, after }.Select(node => node.ToJsonString())));

        // Core's alias rule: the OCR spelling shows the corrected name, and
        // the corrected spelling is stored too.
        AppSettings saved = await new SettingsService(settingsFile).LoadAsync();
        string[] entries = saved.SpeakerGenders
            .Where(preference => preference.GameKey == SpeakerGameKey && preference.SpeakerName != "Kotori")
            .Select(preference => $"{preference.SpeakerName}>{preference.DisplayName}:{preference.Gender}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        CoreHostFixtures.Check(
            entries.SequenceEqual([$"{Corrected}>{Corrected}:masculine", $"{Observed}>{Corrected}:masculine"]),
            "settings.json does not hold both spellings of the re-edited speaker: " + string.Join(", ", entries));
    }

    private static JsonObject SpeakerSet(string observedName, string name, string gender) => CoreHostFixtures.Payload(
        ("game", CoreHostFixtures.Payload(("key", SpeakerGameKey), ("displayName", "Asteria Test"))),
        ("observedName", observedName),
        ("name", name),
        ("gender", gender));

    private static async Task<JsonObject> SpeakerOfHoverAsync(CoreHostClient client, string speakerName)
    {
        JsonObject hover = Hover(Sentence, "bank");
        hover["source"]!["speakerName"] = speakerName;
        hover["game"] = CoreHostFixtures.Payload(("key", SpeakerGameKey), ("displayName", "Asteria Test"));
        JsonObject primary = await SinglePrimaryAsync(client, client.Send("hover.resolve", hover));
        return primary["speaker"] as JsonObject ??
            throw new InvalidOperationException("A smart hover with a speaker name carried no speaker: " + primary.ToJsonString());
    }

    // ---- admission order ----------------------------------------------------

    // A settings change takes its place in Core's settings queue when it is
    // admitted, not when the pool gets round to its work: test.delayNextWork
    // holds the change back by HoldBackMs, and what the app sent right behind
    // it must still see it. Each pair below failed (hover_disabled, the old
    // mode, an unknown speaker, conflict, a kept dictionary) while the change
    // was queued from the work task, and fails if the waiting side stops
    // waiting for the queue.
    private static async Task VerifyAdmissionOrderAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "core-order");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        await client.ResultAsync(client.Send("core.init"), InitDeadlineMs);

        // A hover right behind the update that enables translation and picks
        // its mode.
        await HoldBackNextWorkAsync(client);
        CoreHostClient.Pending enable = client.Send("settings.update",
            Update(1, ("hoverEnabled", true), ("mode", "dictionary")));
        JsonObject dictionaryHover = Hover(Sentence, "bank");
        dictionaryHover["mode"] = "dictionary";
        JsonObject primary = await SinglePrimaryAsync(client, client.Send("hover.resolve", dictionaryHover));
        CoreHostFixtures.Check(
            primary["translation"]!.GetValue<string>().Replace("́", "", StringComparison.Ordinal)
                .Contains("берег", StringComparison.Ordinal),
            "The hover behind the enabling update did not translate «bank»: " + primary.ToJsonString());
        CoreHostFixtures.Check((await client.ResultAsync(enable))["revision"]!.GetValue<long>() == 2,
            "The enabling settings.update did not become revision 2.");

        // A warm-up right behind a switch to smart mode loads what smart mode
        // reads: the sense metadata, which dictionary mode never loads.
        await HoldBackNextWorkAsync(client);
        CoreHostClient.Pending smart = client.Send("settings.update",
            Update(2, ("mode", "smart"), ("sense", CoreHostFixtures.Payload(("keepWarm", false)))));
        JsonObject warm = await client.ResultAsync(client.Send("models.warmUp",
            CoreHostFixtures.Payload(("targets", new JsonArray("dictionary")))), 60_000);
        CoreHostFixtures.Check(warm["dictionary"]!["senseMetadataKeys"]!.GetValue<int>() > 0,
            "models.warmUp right behind the switch to smart mode warmed dictionary mode: " + warm.ToJsonString());
        CoreHostFixtures.Check((await client.ResultAsync(smart))["revision"]!.GetValue<long>() == 3,
            "The switch to smart mode did not become revision 3.");

        // A hover right behind speaker.set resolves the speaker's new gender.
        await HoldBackNextWorkAsync(client);
        JsonObject game = CoreHostFixtures.Payload(("key", "process:asteria-order"), ("displayName", "Asteria Order"));
        CoreHostClient.Pending speaker = client.Send("speaker.set", CoreHostFixtures.Payload(
            ("game", game.DeepClone()), ("observedName", "Kotori"), ("name", "Kotori"), ("gender", "feminine")));
        JsonObject spoken = Hover(Sentence, "bank");
        spoken["source"]!["speakerName"] = "Kotori";
        spoken["game"] = game.DeepClone();
        JsonObject withSpeaker = await SinglePrimaryAsync(client, client.Send("hover.resolve", spoken));
        CoreHostFixtures.Check(
            withSpeaker["speaker"]?.ToJsonString() ==
            "{\"name\":\"Kotori\",\"gender\":\"feminine\",\"gameKey\":\"process:asteria-order\",\"observedName\":\"Kotori\"}",
            "The hover behind speaker.set did not see the saved gender: " + withSpeaker.ToJsonString());
        await client.ResultAsync(speaker);

        // Two updates in a row apply in the order they were sent: the second
        // one's base revision is the first one's result.
        await HoldBackNextWorkAsync(client);
        CoreHostClient.Pending first = client.Send("settings.update", Update(3, ("hoverDelayMs", 400)));
        CoreHostClient.Pending second = client.Send("settings.update", Update(4, ("hoverDelayMs", 600)));
        JsonObject secondResult = await client.ResultAsync(second);
        JsonObject firstResult = await client.ResultAsync(first);
        JsonObject latest = await client.ResultAsync(client.Send("settings.get"));
        CoreHostFixtures.Check(
            firstResult["revision"]!.GetValue<long>() == 4 &&
            secondResult["revision"]!.GetValue<long>() == 5 &&
            latest["revision"]!.GetValue<long>() == 5 &&
            latest["settings"]!["hoverDelayMs"]!.GetValue<int>() == 600,
            $"Two pipelined updates did not apply in arrival order: {firstResult.ToJsonString()} {secondResult.ToJsonString()} {latest.ToJsonString()}");

        // An unload right behind switching translation off unloads everything.
        await HoldBackNextWorkAsync(client);
        CoreHostClient.Pending disable = client.Send("settings.update", Update(5, ("hoverEnabled", false)));
        JsonObject unloaded = await client.ResultAsync(client.Send("models.unload"), 30_000);
        CoreHostFixtures.Check(
            !unloaded["dictionary"]!["loaded"]!.GetValue<bool>() &&
            unloaded["dictionary"]!["senseMetadataKeys"]!.GetValue<int>() == 0,
            "models.unload right behind switching translation off kept the dictionaries: " + unloaded.ToJsonString());
        await client.ResultAsync(disable);

        AppSettings saved = await new SettingsService(Path.Combine(session.UserData, "settings.json")).LoadAsync();
        CoreHostFixtures.Check(
            !saved.HoverTranslationEnabled &&
            saved.HoverTranslationMode == HoverTranslationModes.Smart &&
            saved.HoverTranslationDelayMs == 600 &&
            saved.SpeakerGenders.Any(preference => preference.GameKey == "process:asteria-order" && preference.Gender == "feminine"),
            "settings.json does not hold every change in arrival order.");

        // ocr.plan right behind a held-back switch to dictionary mode, and
        // beside a hover held in the test engine (last: it replaces Core's
        // hover engine for the rest of this launch).
        await CoreHostObservationTests.VerifyPlanBehindSettingsAsync(client, 6, HoldBackNextWorkAsync);
        await CoreHostObservationTests.VerifyPlanBesideHoverAsync(client);
        await session.ShutdownAsync("settings.json", "Dictionaries");
    }

    private static async Task HoldBackNextWorkAsync(CoreHostClient client) =>
        await client.ResultAsync(client.Send("test.delayNextWork", CoreHostFixtures.Payload(("ms", HoldBackMs))));

    // ---- ConSeC read in place ---------------------------------------------

    private static async Task VerifyConsecInPlaceAsync(string tempRoot)
    {
        string worker = MacWorkerTests.LocateWorkerApphost();
        string models = MacWorkerTests.RepositoryModelDirectory;
        if (MacWorkerTests.MissingConsecBundleFile(models) is { } missing)
        {
            // The licensed bundle is the owner's; its absence is reported, not hidden.
            MacSmoke.CoreHostSmartHover = "absent: " + missing;
            return;
        }

        // --ranker-models is the repository folder itself: the worker reads
        // the bundle in place, nothing is copied or linked.
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "consec", testCommands: false,
            extraArguments: ["--ranker-worker", worker, "--ranker-models", models]);
        CoreHostClient client = session.Client;
        await client.HelloAsync(expectTestCommands: false);
        await client.ResultAsync(client.Send("core.init"), InitDeadlineMs);
        JsonObject updated = await client.ResultAsync(client.Send("settings.update", Update(1,
            ("hoverEnabled", true),
            ("mode", "smart"),
            ("sense", CoreHostFixtures.Payload(("model", "consec"), ("backend", "cpu"))))));
        CoreHostFixtures.Check(
            updated["settings"]!["sense"]!["model"]!.GetValue<string>() == "consec" &&
            updated["settings"]!["sense"]!["backend"]!.GetValue<string>() == "cpu",
            "settings.update did not select ConSeC on the CPU: " + updated.ToJsonString());

        // Keep-warm starts the worker; models.status then reports its identity.
        JsonObject sense = await WaitForRunningSenseAsync(client);
        int workerPid = sense["pid"]!.GetValue<int>();
        CoreHostFixtures.Check(
            sense["modelVersion"]!.GetValue<string>() == MacWorkerTests.ExpectedConsecModelVersion &&
            sense["activeProvider"]!.GetValue<string>() == "cpu" &&
            sense["inUse"]!.GetValue<bool>() &&
            CoreHostProcess.ProcessGroupOf(workerPid) == session.Process.Pgid,
            $"models.status does not report the pinned ConSeC worker on the CPU in CoreHost's group: {sense.ToJsonString()}");

        JsonObject primary = await SinglePrimaryAsync(client, client.Send("hover.resolve", Hover(RankedSentence, "pitcher")));
        CoreHostFixtures.Check(
            primary["provenance"]!.GetValue<string>() == "ranker" &&
            primary["senseProvider"]!.GetValue<string>() == "cpu" &&
            primary["engineLabel"]!.GetValue<string>() == RankerLabel &&
            primary["found"]!.GetValue<bool>() &&
            primary["translation"]!.GetValue<string>().Any(character => character is >= 'а' and <= 'я'),
            "The smart hover did not rank on ConSeC CPU: " + primary.ToJsonString());

        CheckLabelsAndStatuses(client);
        // CoreHost's orderly exit itself must end the worker: checked before
        // the harness kills the group, which would end it anyway.
        await session.ShutdownAsync(
            [workerPid],
            "settings.json",
            "Dictionaries",
            "smart-dictionary-cache.v2.json",
            "glossbert-decisions.v1.json");
        MacSmoke.CoreHostSmartHover = "checked in place";
    }

    private static async Task<JsonObject> WaitForRunningSenseAsync(CoreHostClient client)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (true)
        {
            JsonObject sense = (await client.ResultAsync(client.Send("models.status")))["sense"]!.AsObject();
            CoreHostFixtures.Check(sense["error"] is null, "The ConSeC warm-up failed: " + sense.ToJsonString());
            if (sense["running"]!.GetValue<bool>() && sense["modelVersion"] is not null)
            {
                return sense;
            }

            CoreHostFixtures.Check(clock.Elapsed < TimeSpan.FromMinutes(5),
                "The ConSeC worker did not start within 5 minutes: " + sense.ToJsonString());
            await Task.Delay(100);
        }
    }

    // ---- helpers ------------------------------------------------------------

    private static JsonObject Hover(string text, string word) => CoreHostFixtures.Payload(("source", CoreHostFixtures.Payload(
        ("kind", "selection"),
        ("text", text),
        ("hoveredWord", word),
        ("characterIndex", text.IndexOf(word, StringComparison.Ordinal)))));

    private static JsonObject Update(long baseRevision, params (string Name, JsonNode? Value)[] patch) =>
        CoreHostFixtures.Payload(("baseRevision", baseRevision), ("patch", CoreHostFixtures.Payload(patch)));

    private static Task<JsonObject> RefuseUpdateAsync(
        CoreHostClient client,
        string code,
        long baseRevision,
        params (string Name, JsonNode? Value)[] patch) =>
        client.ErrorAsync(client.Send("settings.update", Update(baseRevision, patch)), code);

    private static async Task<JsonObject> SinglePrimaryAsync(CoreHostClient client, CoreHostClient.Pending hover)
    {
        JsonObject result = await client.ResultAsync(hover, HoverDeadlineMs);
        List<JsonObject> events;
        lock (hover.Events)
        {
            events = [.. hover.Events];
        }

        CoreHostFixtures.Check(
            result["outcome"]!.GetValue<string>() == "completed" &&
            events.Count == 1 &&
            events[0]["event"]!.GetValue<string>() == "hover.primary",
            $"A hover must end completed after exactly one primary card (no phrase model on macOS): " +
            $"{result.ToJsonString()} events {string.Join(" ", events.Select(frame => frame.ToJsonString()))}");
        return events[0]["payload"]!.AsObject();
    }

    private static async Task<bool> DisplayedAsync(CoreHostClient client, long hoverId, string part) =>
        (await client.ResultAsync(client.Send("hover.displayed",
            CoreHostFixtures.Payload(("hoverId", hoverId), ("part", part)))))["recorded"]!.GetValue<bool>();

    private static void CheckNoBrokenSettings(string userData) =>
        CoreHostFixtures.Check(
            !Directory.EnumerateFiles(userData, "settings.json.broken-*").Any(),
            "Core found the settings.json CoreHost wrote broken and kept a .broken copy.");

    // DoD 3: on macOS no label and no status names the Windows text source or
    // GPU runtime; Core's free text arrives as area "translation" without a
    // state, so the app keeps it out of the menu and the overlay.
    private static void CheckLabelsAndStatuses(CoreHostClient client)
    {
        string[] trace = client.Trace();
        string? windowsName = trace.FirstOrDefault(line =>
            line.Contains("NPU OCR", StringComparison.Ordinal) ||
            line.Contains("VULKAN", StringComparison.Ordinal));
        CoreHostFixtures.Check(windowsName is null, "A macOS label or status names a Windows engine: " + windowsName);
        JsonObject[] statuses = client.Unsolicited
            .Where(frame => frame["event"]!.GetValue<string>() == "status")
            .Select(frame => frame["payload"]!.AsObject())
            .ToArray();
        CoreHostFixtures.Check(
            statuses.Any(status => status["area"]!.GetValue<string>() == "translation") &&
            statuses.All(status => (status["area"]!.GetValue<string>() == "translation") == (status["state"] is null)),
            "Core's free-text statuses must be area translation without a state: " +
            string.Join(" ", statuses.Select(status => status.ToJsonString())));
    }
}
