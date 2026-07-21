using AsteriaScale.Models;
using AsteriaScale.Services;

internal static class TextHookSafetyTests
{
    public static async Task RunAsync(string tempRoot)
    {
        AppSettings settings = new();
        await using TextHookService hook = new(settings);

        hook.IngestLine(
            "[FA0:2C64:0:0:0:GetGlyphOutlineW] The heroine is waiting.",
            processId: 101);

        TextHookMatch? sameProcess = hook.TryGetSentenceContaining(101, "heroine");
        Assert(sameProcess is { Text: "The heroine is waiting.", CharacterIndex: 4 },
            "A hooked token was not returned with its exact process-scoped span.");
        Assert(hook.TryGetSentenceContaining(202, "heroine") is null,
            "A hooked sentence leaked into another process.");
        Assert(hook.TryGetSentenceContaining(101, "he") is null,
            "A substring inside 'The' was accepted as a hovered token.");
        Assert(hook.TryGetSentenceContaining(101, "in") is null,
            "A substring inside 'waiting' was accepted as a hovered token.");

        hook.IngestLine(
            "[FA0:2C64:0:0:0:GetGlyphOutlineW] LongSpeaker: bank and another bank",
            processId: 101);
        TextHookMatch? repeated = hook.TryGetSentenceContaining(
            101,
            "bank",
            "bank and another bank",
            ocrCharacterIndex: 18);
        Assert(repeated is { CharacterIndex: 30 },
            "A hook prefix shifted the repeated token to the wrong occurrence.");
        Assert(hook.TryGetSentenceContaining(
                "bank",
                DateTime.UtcNow.AddSeconds(5)) is null,
            "A hook sentence older than the five-second relevance window was reused.");

        string textractorRoot = Path.Combine(tempRoot, "text-hook-profile-detach");
        string cliDirectory = Path.Combine(textractorRoot, "x64");
        Directory.CreateDirectory(cliDirectory);
        File.Copy(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Path.Combine(cliDirectory, "TextractorCLI.exe"));
        string processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        AppSettings profileSettings = new()
        {
            TextHookEnabled = true,
            TextractorDirectory = textractorRoot,
            Profiles =
            [
                new AutomationProfile
                {
                    Name = "fixture",
                    ProcessName = processName,
                    TextHookEnabled = true
                }
            ]
        };
        await using TextHookService attached = new(profileSettings);
        attached.EnsureAttached(Environment.ProcessId);
        Assert(attached.AttachedProcessId == Environment.ProcessId,
            "The profile detach fixture did not start its isolated CLI.");
        profileSettings.Profiles[0].TextHookEnabled = false;
        attached.ApplySettings();
        Assert(attached.AttachedProcessId == 0,
            "Disabling the active profile left Textractor attached.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
