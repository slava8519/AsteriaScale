using AsteriaScale;
using AsteriaScale.Models;
using AsteriaScale.Services;
using Drawing = System.Drawing;

internal static class TextHookSafetyTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string probeRoot = Path.Combine(tempRoot, "text-hook-probe");
        string probeX86 = Path.Combine(probeRoot, "x86");
        Directory.CreateDirectory(probeX86);
        File.WriteAllText(Path.Combine(probeX86, "TextractorCLI.exe"), string.Empty);
        Assert(string.Equals(
                TextHookService.ProbeDefaultDirectory(
                    [Path.Combine(tempRoot, "missing"), probeRoot]),
                Path.GetFullPath(probeRoot),
                StringComparison.OrdinalIgnoreCase),
            "Textractor probing ignored a root-level install with an x86 CLI.");
        Assert(TextHookService.IsTextractorDirectory(probeRoot),
            "A valid x86 Textractor installation was rejected.");
        Assert(TextHookService.DescribeInstallation(probeRoot).Contains("x86", StringComparison.Ordinal),
            "Textractor diagnostics did not report the available architecture.");

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

        HoverOcrResult ocrResult = new(
            "bank and another bank",
            "bank",
            0,
            Drawing.Rectangle.Empty);
        Assert(ReferenceEquals(
                ocrResult,
                HoverTranslationService.ApplyTextHookMatch(ocrResult, match: null)),
            "A missing hook match changed the pure OCR result.");
        HoverOcrResult hookedResult = HoverTranslationService.ApplyTextHookMatch(
            ocrResult,
            new TextHookMatch("LongSpeaker: bank and another bank", 13));
        Assert(hookedResult is
            {
                Text: "LongSpeaker: bank and another bank",
                CharacterIndex: 13,
                TextSource: HoverTextSource.TextHook
            },
            "A fresh hook match did not preserve explicit Textractor provenance.");
        const string ocrBadge = "EN → RU  ·  CPU КОНТЕКСТ";
        Assert(string.Equals(
                HoverTranslationOverlay.AddTextSourceBadge(ocrBadge, HoverTextSource.Ocr),
                ocrBadge,
                StringComparison.Ordinal),
            "Pure OCR unexpectedly gained a text-hook badge.");
        Assert(HoverTranslationOverlay.AddTextSourceBadge(
                ocrBadge,
                HoverTextSource.TextHook).EndsWith("  ·  HOOK", StringComparison.Ordinal),
            "A Textractor-backed hover card does not visibly report HOOK provenance.");

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
