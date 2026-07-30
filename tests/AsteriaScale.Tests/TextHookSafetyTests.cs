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

        const string titledHookLine =
            "Kotarou put in a lot of effort to prepare this for you, Mr. Giraffe. " +
            "I would appreciate it if you could forgive him.";
        hook.IngestLine(
            $"[TITLE:2C64:0:0:0:GetGlyphOutlineW] {titledHookLine}",
            processId: 505);
        TextHookMatch? titledHook = hook.TryGetSentenceContaining(505, "lot");
        Assert(titledHook is not null,
            "The title-abbreviation fixture was not available through Textractor.");
        HoverOcrResult titledOcr = new(
            "lot",
            "lot",
            0,
            Drawing.Rectangle.Empty);
        HoverOcrResult titledUpgrade = HoverTranslationService.ApplyTextHookMatch(
            titledOcr,
            titledHook);
        SmartPhraseContext? titledPhrase = SmartPhraseContextBuilder.Build(
            titledUpgrade.Text,
            titledUpgrade.HoveredWord,
            titledUpgrade.CharacterIndex);
        Assert(titledPhrase is not null &&
               titledPhrase.Value.Text ==
               "Kotarou put in a lot of effort to prepare this for you, Mr. Giraffe.",
            "Textractor context was truncated at an English title abbreviation.");

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

        hook.IngestLine(
            "[AAA:2C64:0:0:0:TextOutA] The gate was still closed.",
            processId: 303);
        hook.IngestLine(
            "[NOISE:2C64:0:0:0:TextOutA] Wrong hook thread output.",
            processId: 303);
        hook.IngestLine(
            "[AAA:2C64:0:0:0:TextOutA] She reached for the handle.",
            processId: 303);
        hook.IngestLine(
            "[AAA:2C64:0:0:0:TextOutA] Then she opened the gate.",
            processId: 404);
        hook.IngestLine(
            "[AAA:2C64:0:0:0:TextOutA] At last she opened the gate.",
            processId: 303);
        TextHookMatch? contextual = hook.TryGetSentenceContaining(303, "opened");
        Assert(contextual is not null &&
               contextual.PreviousContext.SequenceEqual(
                   ["The gate was still closed.", "She reached for the handle."]),
            "Hidden history crossed a process or exact Textractor hook-stream boundary.");

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
            new TextHookMatch("LongSpeaker: bank and another bank", 13)
            {
                PreviousContext = ["The river was quiet."]
            });
        Assert(hookedResult is
        {
            Text: "LongSpeaker: bank and another bank",
            CharacterIndex: 13,
            TextSource: HoverTextSource.TextHook
        } && hookedResult.PreviousContext.SequenceEqual(["The river was quiet."]),
            "A fresh hook match did not preserve explicit Textractor provenance.");
        // Roadmap 14.3 requires the card to name the text source in both
        // directions. Labelling only HOOK left OCR to be inferred from the
        // engine prefix, which reads "NPU OCR" even on a hook match.
        const string ocrBadge = "EN → RU  ·  CPU КОНТЕКСТ";
        string ocrLabelled = HoverTranslationOverlay.AddTextSourceBadge(
            ocrBadge,
            HoverTextSource.Ocr);
        Assert(ocrLabelled.EndsWith("  ·  OCR", StringComparison.Ordinal),
            $"An OCR-backed hover card does not state its provenance: '{ocrLabelled}'");
        Assert(!ocrLabelled.Contains("HOOK", StringComparison.Ordinal),
            "Pure OCR unexpectedly gained a text-hook badge.");
        string hookLabelled = HoverTranslationOverlay.AddTextSourceBadge(
            ocrBadge,
            HoverTextSource.TextHook);
        Assert(hookLabelled.EndsWith("  ·  HOOK", StringComparison.Ordinal),
            "A Textractor-backed hover card does not visibly report HOOK provenance.");
        Assert(!string.Equals(ocrLabelled, hookLabelled, StringComparison.Ordinal),
            "The two provenances are indistinguishable on the card.");

        ProcessTarget diagnosticTarget = new(4242, "Novel.exe", "x86");
        TextHookStatusSnapshot disabledStatus = TextHookStatusSnapshot.Disabled();
        Assert(HoverTranslationOverlay.AddTextSourceBadge(
                ocrBadge,
                HoverTextSource.Ocr,
                disabledStatus).EndsWith("  ·  OCR · HOOK ВЫКЛ", StringComparison.Ordinal),
            "The card did not state that an explicitly configured hook is disabled.");
        TextHookStatusSnapshot unavailableStatus = TextHookStatusSnapshot.Unavailable(
            diagnosticTarget.ProcessId,
            diagnosticTarget,
            string.Empty,
            "Injection failed.");
        Assert(HoverTranslationOverlay.AddTextSourceBadge(
                ocrBadge,
                HoverTextSource.Ocr,
                unavailableStatus).EndsWith("  ·  OCR · HOOK НЕТ", StringComparison.Ordinal),
            "An unavailable injector was presented as ordinary OCR.");
        TextHookStatusSnapshot waitingStatus = TextHookStatusSnapshot.Attached(
            diagnosticTarget.ProcessId,
            diagnosticTarget,
            @"C:\Textractor\x86\TextractorCLI.exe");
        Assert(HoverTranslationOverlay.AddTextSourceBadge(
                ocrBadge,
                HoverTextSource.Ocr,
                waitingStatus).EndsWith("  ·  OCR · HOOK ЖДЁТ", StringComparison.Ordinal),
            "An attached injector without a matching line was presented as active.");
        TextHookStatusSnapshot activeStatus = waitingStatus.WithCurrentMatch(matched: true);
        Assert(activeStatus.State == TextHookRuntimeState.Active &&
               HoverTranslationOverlay.AddTextSourceBadge(
                   ocrBadge,
                   HoverTextSource.TextHook,
                   activeStatus).EndsWith("  ·  HOOK", StringComparison.Ordinal),
             "A matching Textractor line was not presented as an active hook.");
        Assert(HoverTranslationOverlay.AddTextSourceBadge(
                ocrBadge,
                HoverTextSource.Ocr,
                activeStatus).EndsWith("  ·  OCR · HOOK АКТИВЕН", StringComparison.Ordinal),
            "An operational hook was hidden when one card used OCR fallback.");

        AppSettings missingCliSettings = new()
        {
            TextHookEnabled = true,
            TextractorDirectory = Path.Combine(tempRoot, "missing-textractor")
        };
        await using (TextHookService missingCli = new(missingCliSettings))
        {
            missingCli.EnsureAttached(Environment.ProcessId);
            TextHookStatusSnapshot unavailable = missingCli.GetStatusSnapshot(
                Environment.ProcessId);
            Assert(unavailable.State == TextHookRuntimeState.Unavailable &&
                   unavailable.ProcessId == Environment.ProcessId &&
                   unavailable.ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                   unavailable.Details.Contains("PID", StringComparison.Ordinal) &&
                   unavailable.Details.Contains("OCR", StringComparison.Ordinal),
                "Attach diagnostics omitted the target executable, PID, or OCR fallback.");
            Assert(unavailable.ProcessArchitecture is "x86" or "x64" or "архитектура неизвестна",
                "Attach diagnostics omitted the target architecture.");
        }

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
        TextHookStatusSnapshot attachedStatus = attached.GetStatusSnapshot(
            Environment.ProcessId);
        Assert(attachedStatus.State == TextHookRuntimeState.AttachedWaiting &&
               attachedStatus.ProcessId == Environment.ProcessId &&
               attachedStatus.CliPath.EndsWith("TextractorCLI.exe", StringComparison.OrdinalIgnoreCase) &&
               attachedStatus.Details.Contains("PID", StringComparison.Ordinal),
            "The attached status omitted its PID, architecture, or CLI path.");
        Assert(attached.GetStatusSnapshot(
                   Environment.ProcessId,
                   matchedCurrentText: true).State == TextHookRuntimeState.Active &&
               attached.GetStatusSnapshot(Environment.ProcessId).State == TextHookRuntimeState.Active,
            "A verified hook stream did not remain visibly active for the attachment.");
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
