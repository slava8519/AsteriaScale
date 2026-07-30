using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AsteriaScale.Models;
using AsteriaScale.Services;

internal static class MagpieSafetyTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "magpie-safety");
        Directory.CreateDirectory(root);

        await VerifyProfileIndexesSurviveModeReplacementAsync(root);
        await VerifyBackupIsAtomicAndFirstWriterWinsAsync(root);
        await VerifyExternalProcessSurvivesShutdownAsync(root);
        await VerifyBoundedStateConfirmationAsync();
        VerifyCursorLifecycleGuard();
        await VerifyBuildFingerprintTracksContentAndDeletionAsync(root);
    }

    private static async Task VerifyProfileIndexesSurviveModeReplacementAsync(string root)
    {
        string path = Path.Combine(root, "profiles.json");
        string priorAsteriaMode = PresetCatalog.Find("vn-balanced").EngineModeName;
        JsonObject config = new()
        {
            ["scalingModes"] = new JsonArray
            {
                CreateMode("Custom Alpha"),
                CreateMode(priorAsteriaMode),
                CreateMode("Custom Beta")
            },
            ["profiles"] = new JsonArray
            {
                CreateProfile(0, "Asteria managed default"),
                CreateProfile(2, "Custom Beta profile"),
                CreateProfile(1, "Existing Asteria profile"),
                CreateProfile(0, "Custom Alpha profile")
            },
            ["shortcuts"] = new JsonObject()
        };
        await File.WriteAllTextAsync(path, config.ToJsonString());

        MagpieConfigEditor editor = new();
        UpscalePreset selected = PresetCatalog.Find("vn-ultra");
        await editor.ApplyPresetAsync(path, selected);

        JsonObject updated = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        JsonArray modes = updated["scalingModes"]!.AsArray();
        JsonArray profiles = updated["profiles"]!.AsArray();
        Check(ProfileModeName(profiles, modes, 0) == selected.EngineModeName,
            "The Asteria-managed default profile did not select the requested preset.");
        Check(ProfileModeName(profiles, modes, 1) == "Custom Beta",
            "A user profile was redirected after Asteria modes were rebuilt.");
        Check(ProfileModeName(profiles, modes, 2) == priorAsteriaMode,
            "A non-default profile lost its existing Asteria mode.");
        Check(ProfileModeName(profiles, modes, 3) == "Custom Alpha",
            "A user profile pointing before an Asteria mode was changed.");

        string firstEdit = await File.ReadAllTextAsync(path);
        await editor.ApplyPresetAsync(path, selected);
        string secondEdit = await File.ReadAllTextAsync(path);
        Check(firstEdit == secondEdit, "Repeated Magpie config edits were not idempotent.");
    }

    private static async Task VerifyBackupIsAtomicAndFirstWriterWinsAsync(string root)
    {
        string path = Path.Combine(root, "backup-source.json");
        string original = "{\"payload\":\"" + new string('x', 1_048_576) + "\"}";
        await File.WriteAllTextAsync(path, original);

        await Task.WhenAll(
            MagpieEngineService.BackupConfigOnceAsync(path),
            MagpieEngineService.BackupConfigOnceAsync(path));

        string backupPath = path + ".before-asteria.json";
        Check(await File.ReadAllTextAsync(backupPath) == original,
            "The first Magpie backup was incomplete.");
        Check(!Directory.EnumerateFiles(root, "*.tmp").Any(),
            "A completed backup left a temporary file behind.");

        await File.WriteAllTextAsync(path, "{\"payload\":\"new\"}");
        await MagpieEngineService.BackupConfigOnceAsync(path);
        Check(await File.ReadAllTextAsync(backupPath) == original,
            "A later config edit overwrote the before-Asteria backup.");

        string cancelledPath = Path.Combine(root, "cancelled.json");
        await File.WriteAllTextAsync(cancelledPath, original);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        try
        {
            await MagpieEngineService.BackupConfigOnceAsync(cancelledPath, cancellation.Token);
            throw new InvalidOperationException("A cancelled backup unexpectedly succeeded.");
        }
        catch (OperationCanceledException)
        {
        }

        Check(!File.Exists(cancelledPath + ".before-asteria.json"),
            "A cancelled backup published a partial final file.");
        Check(!Directory.EnumerateFiles(root, "*.tmp").Any(),
            "A cancelled backup left a temporary file behind.");
    }

    private static async Task VerifyExternalProcessSurvivesShutdownAsync(string root)
    {
        string fakeEngine = Path.Combine(root, "Magpie.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), fakeEngine, overwrite: true);
        using Process externalProcess = Process.Start(new ProcessStartInfo(fakeEngine)
        {
            Arguments = "127.0.0.1 -n 30 -w 1000",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("The external-process test stub did not start.");

        try
        {
            await Task.Delay(100);
            Check(!externalProcess.HasExited, "The external-process test stub exited too early.");
            Stopwatch shutdownTimer = Stopwatch.StartNew();
            MagpieEngineService service = new(
                enginePath: fakeEngine,
                configPath: Path.Combine(root, "external-config.json"));
            Check(!service.IsScaling,
                "An unowned Magpie process was reported as Asteria scaling state.");
            Check(!service.IsScalingSourceLost,
                "An unowned Magpie process was treated as a lost Asteria scaling source.");
            await service.StopScalingAsync();
            await service.DisposeAsync();
            shutdownTimer.Stop();
            externalProcess.Refresh();
            Check(!externalProcess.HasExited,
                "Disposing Asteria stopped a Magpie process that it did not start.");
            Check(shutdownTimer.Elapsed < TimeSpan.FromSeconds(2),
                "Asteria waited for an external Magpie process during shutdown.");
        }
        finally
        {
            if (!externalProcess.HasExited)
            {
                externalProcess.Kill(entireProcessTree: true);
                await externalProcess.WaitForExitAsync();
            }
        }
    }

    private static async Task VerifyBoundedStateConfirmationAsync()
    {
        bool state = false;
        Task<bool> transition = MagpieEngineService.WaitForStateAsync(
            () => state,
            expectedState: true,
            TimeSpan.FromSeconds(1));
        await Task.Delay(80);
        state = true;
        Check(await transition, "A confirmed Magpie state transition was missed.");

        Stopwatch timeout = Stopwatch.StartNew();
        Check(!await MagpieEngineService.WaitForStateAsync(
                () => false,
                expectedState: true,
                TimeSpan.FromMilliseconds(120)),
            "A missing Magpie state transition was reported as successful.");
        Check(timeout.Elapsed < TimeSpan.FromSeconds(1),
            "Magpie state confirmation was not bounded by its timeout.");
    }

    private static void VerifyCursorLifecycleGuard()
    {
        nint scalingWindow = new(24);
        nint sourceWindow = new(42);
        Check(!MagpieEngineService.IsScalingWindowActiveState(
                scalingWindow,
                isVisible: false),
            "A hidden Magpie initialization window was reported as active scaling.");
        Check(MagpieEngineService.IsScalingWindowActiveState(
                scalingWindow,
                isVisible: true),
            "A visible Magpie scaling window was not reported as active.");
        Check(!MagpieEngineService.IsScalingWindowReadyForTargetState(
                scalingWindow: scalingWindow,
                isVisible: false,
                sourceWindow: nint.Zero,
                isSourceWindow: false,
                expectedSourceWindow: sourceWindow),
            "A pre-render Magpie window without source properties was reported as ready.");
        Check(!MagpieEngineService.IsScalingWindowReadyForTargetState(
                scalingWindow: scalingWindow,
                isVisible: true,
                sourceWindow: new nint(41),
                isSourceWindow: true,
                expectedSourceWindow: sourceWindow),
            "A Magpie session for another source window was reported as ready.");
        Check(MagpieEngineService.IsScalingWindowReadyForTargetState(
                scalingWindow: scalingWindow,
                isVisible: true,
                sourceWindow: sourceWindow,
                isSourceWindow: true,
                expectedSourceWindow: sourceWindow),
            "A visible Magpie session for the expected source was not reported as ready.");

        Check(MagpieEngineService.IsScalingSourceLostState(
                nint.Zero,
                isWindow: false,
                isIconic: false,
                isCloaked: false),
            "A scaling window without Magpie.SrcHWND did not invalidate the session.");
        Check(MagpieEngineService.IsScalingSourceLostState(
                sourceWindow,
                isWindow: false,
                isIconic: false,
                isCloaked: false),
            "A destroyed scaling source did not invalidate the session.");
        Check(!MagpieEngineService.IsScalingSourceLostState(
                sourceWindow,
                isWindow: true,
                isIconic: true,
                isCloaked: false),
            "Magpie's intentionally minimized scaling source invalidated the session.");
        Check(MagpieEngineService.IsScalingSourceLostState(
                sourceWindow,
                isWindow: true,
                isIconic: false,
                isCloaked: true),
            "A virtual-desktop scaling source did not invalidate the session.");
        Check(!MagpieEngineService.IsScalingSourceLostState(
                sourceWindow,
                isWindow: true,
                isIconic: false,
                isCloaked: false),
            "A valid visible scaling source was treated as lost.");
        Check(MagpieEngineService.IsScalingSourceDestroyedState(
                nint.Zero,
                isWindow: false),
            "A missing scaling source was not classified as destroyed.");
        Check(MagpieEngineService.IsScalingSourceDestroyedState(
                sourceWindow,
                isWindow: false),
            "A stale scaling source handle was not classified as destroyed.");
        Check(!MagpieEngineService.IsScalingSourceDestroyedState(
                sourceWindow,
                isWindow: true),
            "A live scaling source was classified as destroyed.");
        Check(MagpieEngineService.IsScalingSourceForegroundState(sourceWindow, sourceWindow),
            "The active scaling source was not recognized as foreground.");
        Check(!MagpieEngineService.IsScalingSourceForegroundState(sourceWindow, new nint(41)),
            "An unrelated foreground window was treated as the scaling source.");
        Check(MagpieEngineService.IsScalingSessionForegroundState(
                sourceWindow,
                isSourceWindow: true,
                scalingWindow,
                scalingWindow),
            "Magpie's active scaling surface was treated as an external application.");
        Check(!MagpieEngineService.IsScalingSessionForegroundState(
                sourceWindow,
                isSourceWindow: false,
                scalingWindow,
                scalingWindow),
            "A stale scaling surface retained cursor ownership after its source was destroyed.");

        Check(!MagpieEngineService.IsWindowedScalingModeState(nint.Zero),
            "A fullscreen Magpie session selected the windowed stop action.");
        Check(MagpieEngineService.IsWindowedScalingModeState(new nint(1)),
            "A windowed Magpie session selected the fullscreen stop action.");

        // The scaling hotkey is a toggle: repeating a request that is already
        // satisfied would switch scaling off, and the asynchronous confirmation
        // would still report success from the previous session's state.
        Check(MagpieEngineService.PlanScalingCommand(
                  isScalingActive: true,
                  activeSourceWindow: sourceWindow,
                  activeWindowedMode: true,
                  requestedSourceWindow: sourceWindow,
                  requestedWindowedMode: true) ==
              MagpieEngineService.ScalingCommand.AlreadyActive,
            "Re-requesting the running session would have toggled scaling off.");
        Check(MagpieEngineService.PlanScalingCommand(
                  isScalingActive: false,
                  activeSourceWindow: nint.Zero,
                  activeWindowedMode: false,
                  requestedSourceWindow: sourceWindow,
                  requestedWindowedMode: true) ==
              MagpieEngineService.ScalingCommand.Start,
            "An idle engine was not asked to start scaling.");
        Check(MagpieEngineService.PlanScalingCommand(
                  isScalingActive: true,
                  activeSourceWindow: sourceWindow,
                  activeWindowedMode: false,
                  requestedSourceWindow: sourceWindow,
                  requestedWindowedMode: true) ==
              MagpieEngineService.ScalingCommand.RestartAfterStop,
            "A mode change must stop the running mode before starting the new one.");
        Check(MagpieEngineService.PlanScalingCommand(
                  isScalingActive: true,
                  activeSourceWindow: new nint(41),
                  activeWindowedMode: true,
                  requestedSourceWindow: sourceWindow,
                  requestedWindowedMode: true) ==
              MagpieEngineService.ScalingCommand.RestartAfterStop,
            "Switching to another source window must stop the running session first.");

        Check(AutoScaleService.PlanDesktopCursorAction(
                  isScaling: true,
                  isScalingSessionForeground: false,
                  overrideActive: false,
                  magnificationInitialized: false,
                  scalingWasObserved: false) == AutoScaleService.DesktopCursorAction.Show,
            "The desktop transition did not request a visible Windows cursor.");
        Check(AutoScaleService.PlanDesktopCursorAction(
                  isScaling: true,
                  isScalingSessionForeground: false,
                  overrideActive: true,
                  magnificationInitialized: true,
                  scalingWasObserved: true) == AutoScaleService.DesktopCursorAction.None,
            "The active desktop cursor override was repeatedly applied.");
        Check(AutoScaleService.PlanDesktopCursorAction(
                  isScaling: true,
                  isScalingSessionForeground: true,
                  overrideActive: true,
                  magnificationInitialized: true,
                  scalingWasObserved: true) == AutoScaleService.DesktopCursorAction.Hide,
            "Returning to the source did not return cursor ownership to Magpie.");
        Check(AutoScaleService.PlanDesktopCursorAction(
                  isScaling: true,
                  isScalingSessionForeground: false,
                  overrideActive: false,
                  magnificationInitialized: false,
                  scalingWasObserved: true) == AutoScaleService.DesktopCursorAction.Show,
            "A direct Alt+Tab to an ordinary application did not expose the Windows cursor.");
        Check(AutoScaleService.PlanDesktopCursorAction(
                  isScaling: false,
                  isScalingSessionForeground: false,
                  overrideActive: false,
                  magnificationInitialized: true,
                  scalingWasObserved: true) == AutoScaleService.DesktopCursorAction.Release,
            "Ending scaling after a Hide call did not restore the system cursor.");
        Check(AutoScaleService.PlanDesktopCursorAction(
                  isScaling: false,
                  isScalingSessionForeground: false,
                  overrideActive: false,
                  magnificationInitialized: false,
                  scalingWasObserved: true) == AutoScaleService.DesktopCursorAction.Release,
            "Ending scaling without an earlier override did not restore Magpie's hidden cursor.");
        Check(AutoScaleService.PlanDesktopCursorAction(
                  isScaling: false,
                  isScalingSessionForeground: false,
                  overrideActive: false,
                  magnificationInitialized: false,
                  scalingWasObserved: false) == AutoScaleService.DesktopCursorAction.None,
            "An idle app initialized the cursor override API without a scaling session.");
        nint nextScalingWindow = new nint(73);
        Check(AutoScaleService.PlanScalingSourceTracking(
                  isScaling: true,
                  currentEngineProcessId: 501,
                  currentScalingWindow: nextScalingWindow,
                  isSourceReady: false,
                  sourceReadyWasObserved: true,
                  trackedEngineProcessId: 500,
                  trackedScalingWindow: scalingWindow,
                  unavailableDuration: TimeSpan.FromSeconds(5)) ==
              AutoScaleService.ScalingSourceTrackingAction.IgnoreUntilReady,
            "A new pre-ready scaling session inherited stale state from the previous HWND.");
        Check(AutoScaleService.PlanScalingSourceTracking(
                  isScaling: true,
                  currentEngineProcessId: 500,
                  currentScalingWindow: scalingWindow,
                  isSourceReady: false,
                  sourceReadyWasObserved: true,
                  trackedEngineProcessId: 500,
                  trackedScalingWindow: scalingWindow,
                  unavailableDuration: TimeSpan.FromMilliseconds(700)) ==
              AutoScaleService.ScalingSourceTrackingAction.WaitForGrace,
            "One transient missing-source sample stopped the Magpie session.");
        Check(AutoScaleService.PlanScalingSourceTracking(
                  isScaling: true,
                  currentEngineProcessId: 500,
                  currentScalingWindow: scalingWindow,
                  isSourceReady: false,
                  sourceReadyWasObserved: true,
                  trackedEngineProcessId: 500,
                  trackedScalingWindow: scalingWindow,
                  unavailableDuration: TimeSpan.FromMilliseconds(800)) ==
              AutoScaleService.ScalingSourceTrackingAction.VerifyAndStop,
            "A stale source in the same observed session skipped the guarded stop check.");
        Check(AutoScaleService.PlanScalingSourceTracking(
                  isScaling: true,
                  currentEngineProcessId: 501,
                  currentScalingWindow: nextScalingWindow,
                  isSourceReady: true,
                  sourceReadyWasObserved: false,
                  trackedEngineProcessId: 0,
                  trackedScalingWindow: nint.Zero,
                  unavailableDuration: TimeSpan.Zero) ==
              AutoScaleService.ScalingSourceTrackingAction.ObserveReady,
            "A ready replacement session was not adopted as the new identity.");

        MagpieEngineService.ScalingSessionIdentity expectedSession = new(
            EngineProcessId: 500,
            scalingWindow,
            sourceWindow,
            SourceProcessId: 77);
        MagpieEngineService.ScalingSessionSnapshot staleSession = new(
            EngineProcessId: 500,
            scalingWindow,
            SourceWindow: nint.Zero,
            SourceProcessId: 0,
            IsActive: true,
            IsSourceReady: false);
        Check(MagpieEngineService.ShouldStopOwnedEngineForStaleSource(
                expectedSession,
                staleSession,
                expectedSourceStillAlive: false),
            "A rechecked stale session whose game process exited was not stopped.");
        Check(!MagpieEngineService.ShouldStopOwnedEngineForStaleSource(
                expectedSession,
                staleSession,
                expectedSourceStillAlive: true),
            "A live game window was killed only because Magpie temporarily cleared its source property.");
        Check(!MagpieEngineService.ShouldStopOwnedEngineForStaleSource(
                expectedSession,
                staleSession with { EngineProcessId = 501 },
                expectedSourceStillAlive: false),
            "A delayed timer observation killed a replacement Magpie process.");
        Check(!MagpieEngineService.ShouldStopOwnedEngineForStaleSource(
                expectedSession,
                staleSession with { ScalingWindow = nextScalingWindow },
                expectedSourceStillAlive: false),
            "A delayed timer observation killed a different replacement scaling session.");
        Check(!MagpieEngineService.ShouldStopOwnedEngineForStaleSource(
                expectedSession,
                staleSession with
                {
                    SourceWindow = sourceWindow,
                    SourceProcessId = 77,
                    IsSourceReady = true
                },
                expectedSourceStillAlive: false),
            "A source recovered while waiting for the operation lock but was still killed.");
        Check(AutoScaleService.PlanAutomationLatch(
                  automationEnabled: true,
                  isScaling: false,
                  foregroundHandle: sourceWindow,
                  foregroundProcessId: 77,
                  lastTriggeredHandle: sourceWindow,
                  lastTriggeredProcessId: 77) == AutoScaleService.AutomationLatchAction.Keep,
            "A transient Magpie dropout re-armed the same game window.");
        Check(AutoScaleService.PlanAutomationLatch(
                  automationEnabled: true,
                  isScaling: false,
                  foregroundHandle: nint.Zero,
                  foregroundProcessId: 0,
                  lastTriggeredHandle: sourceWindow,
                  lastTriggeredProcessId: 77) == AutoScaleService.AutomationLatchAction.Keep,
            "A temporarily invisible legacy game cleared the one-shot latch.");
        Check(AutoScaleService.PlanAutomationLatch(
                  automationEnabled: true,
                  isScaling: false,
                  foregroundHandle: sourceWindow,
                  foregroundProcessId: 78,
                  lastTriggeredHandle: sourceWindow,
                  lastTriggeredProcessId: 77) == AutoScaleService.AutomationLatchAction.Eligible,
            "A new game process reusing an HWND was blocked by the old latch.");
        Check(AutoScaleService.PlanAutomationLatch(
                  automationEnabled: false,
                  isScaling: true,
                  foregroundHandle: sourceWindow,
                  foregroundProcessId: 77,
                  lastTriggeredHandle: sourceWindow,
                  lastTriggeredProcessId: 77) == AutoScaleService.AutomationLatchAction.Clear,
            "Disabling automation did not explicitly re-arm its one-shot latch.");
        Check(AutoScaleService.PollingIntervalFor(isScaling: true) <=
              TimeSpan.FromMilliseconds(100),
            "Active scaling is not polled quickly enough to restore the cursor.");
        Check(AutoScaleService.PollingIntervalFor(isScaling: false) >=
              TimeSpan.FromMilliseconds(500),
            "Fast cursor polling remained enabled while scaling was inactive.");
    }

    private static async Task VerifyBuildFingerprintTracksContentAndDeletionAsync(string root)
    {
        string? repositoryRoot = AppContext.BaseDirectory;
        while (repositoryRoot is not null &&
               !File.Exists(Path.Combine(repositoryRoot, "AsteriaScale.slnx")))
        {
            repositoryRoot = Path.GetDirectoryName(repositoryRoot);
        }

        Check(repositoryRoot is not null, "Repository root was not found for build-state tests.");
        string script = Path.Combine(repositoryRoot!, "scripts", "build-state.ps1");
        string fingerprintRoot = Path.Combine(root, "fingerprint-root");
        string sourceDirectory = Path.Combine(fingerprintRoot, "src");
        Directory.CreateDirectory(sourceDirectory);
        string sourceFile = Path.Combine(sourceDirectory, "input.cs");
        string globalJson = Path.Combine(fingerprintRoot, "global.json");
        string rankerDirectory = Path.Combine(fingerprintRoot, "assets", "Ranker");
        Directory.CreateDirectory(rankerDirectory);
        string rankerHoldout = Path.Combine(rankerDirectory, "ranker-holdout-v3.json");
        string rankerBenchmark = Path.Combine(rankerDirectory, "ranker-benchmark.json");
        await File.WriteAllTextAsync(sourceFile, "source");
        await File.WriteAllTextAsync(globalJson, "alpha");
        await File.WriteAllTextAsync(rankerHoldout, "frozen-a");
        await File.WriteAllTextAsync(rankerBenchmark, "dev-a");
        await RunGitAsync(fingerprintRoot, "init", "--quiet");
        await RunGitAsync(fingerprintRoot, "add", ".");
        DateTime stableTimestamp = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(globalJson, stableTimestamp);
        string original = await ReadFingerprintAsync(script, fingerprintRoot);

        await File.WriteAllTextAsync(rankerHoldout, "frozen-b");
        await File.WriteAllTextAsync(rankerBenchmark, "dev-b");
        string researchDataChanged = await ReadFingerprintAsync(script, fingerprintRoot);
        Check(researchDataChanged == original,
            "The dist fingerprint consumed ranker benchmark or frozen holdout data.");

        await File.WriteAllTextAsync(globalJson, "bravo");
        File.SetLastWriteTimeUtc(globalJson, stableTimestamp);
        string contentChanged = await ReadFingerprintAsync(script, fingerprintRoot);
        Check(contentChanged != original,
            $"The dist fingerprint missed a same-size root input content change: '{original}' / '{contentChanged}'.");

        File.Delete(sourceFile);
        string deleted = await ReadFingerprintAsync(script, fingerprintRoot);
        Check(deleted != contentChanged,
            "The dist fingerprint missed a deleted source input.");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git fingerprint fixture did not start.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0,
            $"Git fingerprint fixture failed: {error}\n{output}");
    }

    private static async Task<string> ReadFingerprintAsync(string script, string root)
    {
        static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

        string command =
            $". '{Quote(script)}'; Get-AsteriaSourceFingerprint -Root '{Quote(root)}'";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        ProcessStartInfo startInfo = new(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell fingerprint test did not start.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0,
            $"PowerShell fingerprint test failed: {error}");
        return output.Trim();
    }

    private static JsonObject CreateMode(string name) => new()
    {
        ["name"] = name,
        ["effects"] = new JsonArray()
    };

    private static JsonObject CreateProfile(int scalingMode, string name) => new()
    {
        ["name"] = name,
        ["scalingMode"] = scalingMode
    };

    private static string ProfileModeName(JsonArray profiles, JsonArray modes, int profileIndex)
    {
        int modeIndex = profiles[profileIndex]!["scalingMode"]!.GetValue<int>();
        return modes[modeIndex]!["name"]!.GetValue<string>();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
