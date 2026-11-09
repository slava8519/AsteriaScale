using AsteriaScale.Models;
using AsteriaScale.Services;

internal static class SpeakerGenderPreferenceTests
{
    internal static async Task RunAsync(string tempRoot)
    {
        VerifyGameIdentity();
        VerifyExplicitGameScopedResolution();
        VerifySetAndClear();
        await VerifySettingsNormalizationAsync(tempRoot);
    }

    private static void VerifyGameIdentity()
    {
        SpeakerGameIdentity? pathIdentity = SpeakerGenderPreferences.CreateGameIdentity(
#if WINDOWS
            @"C:\Games\Visual Novel\Novel.exe",
#else
            "/Games/Visual Novel/Novel.exe",
#endif
            "ignored-process-name");
        SpeakerGameIdentity? equivalentPathIdentity =
            SpeakerGenderPreferences.CreateGameIdentity(
#if WINDOWS
                @"c:\games\visual novel\NOVEL.EXE",
#else
                "/games/visual novel/NOVEL.EXE",
#endif
                "Novel");
        Assert(pathIdentity is not null &&
               equivalentPathIdentity is not null &&
               pathIdentity.GameKey.StartsWith("path:", StringComparison.Ordinal) &&
               string.Equals(
                   pathIdentity.GameKey,
                   equivalentPathIdentity.GameKey,
                   StringComparison.OrdinalIgnoreCase) &&
               pathIdentity.DisplayName == "Novel.exe",
            "A readable executable path did not produce the stable per-game identity.");

        SpeakerGameIdentity? processIdentity =
            SpeakerGenderPreferences.CreateGameIdentity(null, "SiglusEngine_SteamEN.exe");
        SpeakerGameIdentity? equivalentProcessIdentity =
            SpeakerGenderPreferences.CreateGameIdentity(null, "siglusengine_steamen");
        Assert(processIdentity is not null &&
               equivalentProcessIdentity is not null &&
               processIdentity.GameKey.StartsWith("process:", StringComparison.Ordinal) &&
               string.Equals(
                   processIdentity.GameKey,
                   equivalentProcessIdentity.GameKey,
                   StringComparison.OrdinalIgnoreCase) &&
               processIdentity.DisplayName == "SiglusEngine_SteamEN.exe",
            "A denied process path did not fall back to a stable executable-name identity.");
        Assert(SpeakerGenderPreferences.CreateGameIdentity(null, null) is null,
            "Missing process metadata produced an unstable game identity.");

        SpeakerGameIdentity? currentProcess =
            SpeakerGenderPreferences.ResolveGameIdentity(Environment.ProcessId);
        Assert(currentProcess is not null &&
               currentProcess.GameKey.Length > 5 &&
               currentProcess.DisplayName.Length > 0,
            "The safe PID resolver could not identify its own process.");
        Assert(SpeakerGenderPreferences.ResolveGameIdentity(-1) is null,
            "An invalid PID produced a game identity.");
    }

    private static void VerifyExplicitGameScopedResolution()
    {
        SpeakerGameIdentity game = SpeakerGenderPreferences.CreateGameIdentity(
#if WINDOWS
            @"C:\Games\Novel\Novel.exe",
#else
            "/Games/Novel/Novel.exe",
#endif
            "Novel")!;
        SpeakerGameIdentity otherGame = SpeakerGenderPreferences.CreateGameIdentity(
#if WINDOWS
            @"C:\Games\Other\Other.exe",
#else
            "/Games/Other/Other.exe",
#endif
            "Other")!;
        AppSettings settings = new()
        {
            SpeakerGenders =
            [
                new SpeakerGenderPreference
                {
                    GameKey = game.GameKey,
                    SpeakerName = "Jos\u00e9 Smith",
                    Gender = SpeakerGenderPreferences.FeminineValue
                },
                new SpeakerGenderPreference
                {
                    GameKey = game.GameKey,
                    SpeakerName = "Narrator",
                    Gender = SpeakerGenderPreferences.IgnoreValue
                }
            ]
        };

        CurrentSpeakerContext resolved = SpeakerGenderPreferences.Resolve(
            settings,
            game,
            "  JOSE\u0301\tSmith  ");
        Assert(resolved is
               {
                   Name: "JOS\u00c9 Smith",
                   Gender: SpeakerGender.Feminine
               } &&
               string.Equals(resolved.GameKey, game.GameKey, StringComparison.OrdinalIgnoreCase),
            "NFC, whitespace, or ordinal case normalization lost an explicit choice.");

        CurrentSpeakerContext ignored =
            SpeakerGenderPreferences.Resolve(settings, game, "Narrator");
        Assert(ignored.Gender == SpeakerGender.Ignore,
            "An explicit ignore choice was treated as an inferred gender.");
        Assert(SpeakerGenderPreferences.Resolve(settings, otherGame, "Jos\u00e9 Smith").Gender ==
               SpeakerGender.Unknown,
            "A speaker choice leaked into another game.");
        Assert(SpeakerGenderPreferences.Resolve(settings, game, "Alice").Gender ==
               SpeakerGender.Unknown,
            "An unknown name was assigned a guessed gender.");
        Assert(SpeakerGenderPreferences.Resolve(settings, null, "Jos\u00e9 Smith").Gender ==
               SpeakerGender.Unknown,
            "A name without a stable game identity inherited a saved choice.");
    }

    private static void VerifySetAndClear()
    {
        SpeakerGameIdentity game = SpeakerGenderPreferences.CreateGameIdentity(
            null,
            "Novel.exe")!;
        AppSettings settings = new();
        Assert(SpeakerGenderPreferences.Set(
                settings,
                game,
                "  Kotori  ",
                SpeakerGender.Feminine) &&
               settings.SpeakerGenders.Count == 1,
            "A new explicit speaker choice was not stored.");
        Assert(SpeakerGenderPreferences.Set(
                settings,
                game with { GameKey = "PROCESS:novel.exe" },
                "KOTORI",
                SpeakerGender.Masculine) &&
               settings.SpeakerGenders.Count == 1 &&
               SpeakerGenderPreferences.Resolve(settings, game, "Kotori").Gender ==
               SpeakerGender.Masculine,
            "Updating a canonical speaker entry created a duplicate or kept the old choice.");
        Assert(!SpeakerGenderPreferences.Set(
                settings,
                game,
                "KOTORI",
                SpeakerGender.Masculine),
            "Writing an identical canonical choice reported a settings change.");
        Assert(SpeakerGenderPreferences.Set(
                settings,
                game,
                "KOTORI",
                SpeakerGender.Masculine,
                "Котори") &&
               SpeakerGenderPreferences.Resolve(settings, game, "Kotori") is
               {
                   Name: "Котори",
                   Gender: SpeakerGender.Masculine
               },
            "A corrected display name was not associated with the observed OCR alias.");
        Assert(SpeakerGenderPreferences.Set(
                settings,
                game,
                "Kotori",
                SpeakerGender.Unknown) &&
               settings.SpeakerGenders.Count == 0,
            "Clearing a choice did not remove its canonical entry.");
        Assert(!SpeakerGenderPreferences.Set(
                settings,
                game,
                new string('A', SpeakerMetadataParser.MaximumNameLength + 1),
                SpeakerGender.Feminine) &&
               !SpeakerGenderPreferences.Set(
                   settings,
                   null,
                   "Kotori",
                   SpeakerGender.Feminine),
            "Invalid name or game metadata created a preference.");
    }

    private static async Task VerifySettingsNormalizationAsync(string tempRoot)
    {
        string path = Path.Combine(
            tempRoot,
            "speaker-gender-normalization",
            "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """
            {
              "speakerGenders": [
                { "gameKey": "PROCESS:Novel.EXE", "speakerName": "  Jose\u0301   Smith ", "gender": "FEMININE" },
                { "gameKey": "process:novel", "speakerName": "Jos\u00e9 Smith", "gender": "masculine" },
                { "gameKey": "process:novel", "speakerName": "Narrator", "gender": "IGNORE" },
                { "gameKey": "", "speakerName": "Alice", "gender": "feminine" },
                { "gameKey": "process:novel", "speakerName": "Alice", "gender": "unknown" },
                { "gameKey": "legacy:global", "speakerName": "Alice", "gender": "feminine" },
                null
              ]
            }
            """);

        SettingsService service = new(path);
        AppSettings loaded = await service.LoadAsync();
        Assert(loaded.SpeakerGenders.Count == 2,
            "Invalid or duplicate persisted speaker choices survived settings load.");
        SpeakerGenderPreference jose = loaded.SpeakerGenders.Single(preference =>
            string.Equals(
                preference.SpeakerName,
                "Jos\u00e9 Smith",
                StringComparison.OrdinalIgnoreCase));
        Assert(jose.GameKey == "process:novel" &&
               jose.DisplayName == "Jos\u00e9 Smith" &&
               jose.Gender == SpeakerGenderPreferences.MasculineValue,
            "The latest duplicate did not win with canonical NFC values.");
        SpeakerGenderPreference narrator = loaded.SpeakerGenders.Single(preference =>
            preference.SpeakerName == "Narrator");
        Assert(narrator.Gender == SpeakerGenderPreferences.IgnoreValue,
            "A valid explicit ignore choice was discarded during migration.");

        await service.SaveAsync(loaded);
        AppSettings reloaded = await service.LoadAsync();
        Assert(reloaded.SpeakerGenders.SequenceEqual(loaded.SpeakerGenders),
            "Canonical speaker choices were not stable across a settings round trip.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
