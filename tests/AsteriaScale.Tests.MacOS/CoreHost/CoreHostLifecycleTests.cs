using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>
/// "macOS: CoreHost lifecycle and stdio isolation". The process-group and
/// shutdown contract the app's supervisor relies on: CoreHost leads its own
/// group and reports the signal mask it inherited; core.shutdown answers once;
/// SIGTERM drains even while stdin stays open; a drain past its bound is a
/// core.fault and exit 6; stdin EOF without core.shutdown means the parent is
/// gone and the group is empty within 1 s (exit 4); a child it starts without
/// redirecting stdin holds exactly /dev/null, the two pipes and nothing else,
/// in the same group; and a second CoreHost on the same user data exits 7
/// without touching the first one's files.
/// </summary>
internal static class CoreHostLifecycleTests
{
    private const uint SigTermMask = 1u << (CoreHostProcess.SigTerm - 1);

    public static async Task RunAsync(string tempRoot)
    {
        await VerifyGroupAndIdleEofAsync(tempRoot);
        await VerifyInheritedMaskAsync(tempRoot);
        await VerifySingleShutdownTerminalAsync(tempRoot);
        await VerifySigtermWithStdinOpenAsync(tempRoot);
        await VerifyDrainTimeoutAsync(tempRoot);
        await VerifyParentGoneAsync(tempRoot);
        await VerifyChildDescriptorsAsync(tempRoot);
        await VerifySecondInstanceAsync(tempRoot);
    }

    private static async Task VerifyGroupAndIdleEofAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "group");
        JsonObject hello = await session.Client.HelloAsync();
        CoreHostFixtures.Check(
            hello["processGroupLeader"]!.GetValue<bool>() &&
            hello["pid"]!.GetValue<int>() == session.Process.Pid &&
            hello["pgid"]!.GetValue<int>() == session.Process.Pid,
            "CoreHost does not lead its own process group: " + hello.ToJsonString());
        CoreHostFixtures.Check(hello["blockedSignals"]!.ToJsonString() == "[]",
            "Spawned with an empty mask, CoreHost reports blocked signals: " + hello["blockedSignals"]!.ToJsonString());

        // Idle EOF without core.shutdown: the parent is gone, exit 4 at once.
        Stopwatch clock = Stopwatch.StartNew();
        session.Client.CloseStdin();
        CoreHostExit exit = await session.Process.WaitExitAsync(TimeSpan.FromSeconds(5), "an idle stdin EOF");
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(4) && clock.Elapsed < TimeSpan.FromSeconds(1),
            $"Idle EOF ended with {exit} after {clock.ElapsedMilliseconds} ms; expected exit 4 within 1 s.");
        await session.FinishAsync();
    }

    private static async Task VerifyInheritedMaskAsync(string tempRoot)
    {
        // A helper spawned from a thread with SIGTERM blocked inherits it;
        // hello must say so, so the supervisor can refuse it.
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "masked", blockedSignals: SigTermMask);
        JsonObject hello = await session.Client.HelloAsync();
        CoreHostFixtures.Check(hello["blockedSignals"]!.ToJsonString() == "[\"SIGTERM\"]",
            "Spawned with SIGTERM blocked, CoreHost reports " + hello["blockedSignals"]!.ToJsonString());
        await session.ShutdownAsync();
    }

    private static async Task VerifySingleShutdownTerminalAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "shutdown");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        int child = (await client.ResultAsync(client.Send("test.spawnLongChild")))["pid"]!.GetValue<int>();
        CoreHostClient.Pending first = client.Send("core.shutdown");
        CoreHostClient.Pending second = client.Send("core.shutdown");
        await client.ResultAsync(first, 15_000);
        await client.ErrorAsync(second, "shutting_down");
        CoreHostExit exit = await session.Process.WaitExitAsync(TimeSpan.FromSeconds(15), "core.shutdown");
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(0), $"core.shutdown ended with {exit}.");
        // The orderly exit itself ended the child, before any group kill here.
        await WaitGoneAsync(child, TimeSpan.FromSeconds(1), "the child of an orderly exit");
        await session.FinishAsync();
        CoreHostFixtures.Check(client.Written == client.Terminals && client.Violation is null,
            client.Violation ?? "Both shutdown requests must be answered.");
    }

    private static async Task VerifySigtermWithStdinOpenAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "sigterm");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        CoreHostClient.Pending sideEffect = client.Send("test.sleep", CoreHostFixtures.Payload(
            ("ms", 300), ("mode", "notCancellable")));
        CoreHostClient.Pending immediate = client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 30_000)));
        await client.ResultAsync(client.Send("core.ping"));
        Stopwatch clock = Stopwatch.StartNew();
        session.Process.Signal(CoreHostProcess.SigTerm);
        // stdin stays open: the drain must not depend on the reader seeing EOF.
        CoreHostExit exit = await session.Process.WaitExitAsync(TimeSpan.FromSeconds(12), "SIGTERM with stdin open");
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(0),
            $"SIGTERM with stdin open ended with {exit} after {clock.ElapsedMilliseconds} ms.");
        CoreHostFixtures.Check(clock.Elapsed >= TimeSpan.FromMilliseconds(200) && clock.Elapsed < TimeSpan.FromSeconds(10),
            $"The SIGTERM drain took {clock.ElapsedMilliseconds} ms; it must wait for the NotCancellable work and stay within the 10 s bound.");
        await client.ResultAsync(sideEffect, 1_000);
        await client.ErrorAsync(immediate, "shutting_down", 1_000);
        await session.FinishAsync();
        CoreHostFixtures.Check(client.Written == client.Terminals && client.Violation is null,
            client.Violation ?? "A drained SIGTERM exit answers every request.");
    }

    private static async Task VerifyDrainTimeoutAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "drain-timeout",
            extraArguments: ["--drain-timeout-ms", "300"]);
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        CoreHostClient.Pending stuck = client.Send("test.sleep", CoreHostFixtures.Payload(
            ("ms", 10_000), ("mode", "notCancellable"), ("ignoreCancel", true)));
        Stopwatch clock = Stopwatch.StartNew();
        await client.ErrorAsync(client.Send("core.shutdown"), "shutdown_timeout", 5_000);
        CoreHostExit exit = await session.FinishAsync();
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(6) && clock.Elapsed < TimeSpan.FromSeconds(3),
            $"A drain past its 300 ms bound ended with {exit} after {clock.ElapsedMilliseconds} ms; expected exit 6.");
        CoreHostFixtures.Check(client.Unsolicited.Any(frame =>
                frame["event"]!.GetValue<string>() == "core.fault" &&
                frame["payload"]!["code"]!.GetValue<string>() == "shutdown_timeout"),
            "No core.fault shutdown_timeout before exit 6.");
        CoreHostFixtures.Check(!stuck.Terminal.Task.IsCompleted && client.Lost == 1 &&
                               client.Written == client.Terminals + 1,
            $"Exit 6 must leave exactly the stuck request unanswered (written {client.Written}, terminals {client.Terminals}).");
    }

    private static async Task VerifyParentGoneAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "parent-gone");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        int child = (await client.ResultAsync(client.Send("test.spawnLongChild")))["pid"]!.GetValue<int>();
        client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 30_000), ("mode", "notCancellable"), ("ignoreCancel", true)));
        await client.ResultAsync(client.Send("core.ping"));
        Stopwatch clock = Stopwatch.StartNew();
        client.CloseStdin();
        CoreHostExit exit = await session.Process.WaitExitAsync(TimeSpan.FromSeconds(5), "stdin EOF without core.shutdown");
        await WaitGoneAsync(child, TimeSpan.FromSeconds(1) - clock.Elapsed, "the child after the parent went away");
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(4) && clock.Elapsed < TimeSpan.FromSeconds(1),
            $"EOF without core.shutdown ended with {exit} after {clock.ElapsedMilliseconds} ms; " +
            "expected exit 4 with the group empty within 1 s.");
        await session.FinishAsync();
    }

    private static async Task VerifyChildDescriptorsAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "child-fds");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        int child = (await client.ResultAsync(client.Send("test.spawnLongChild")))["pid"]!.GetValue<int>();
        string[] fds = await CoreHostFixtures.OpenFileDescriptorsAsync(child);
        string[] numbers = fds.Select(fd => fd[..fd.IndexOf(':')]).ToArray();
        CoreHostFixtures.Check(numbers.SequenceEqual(["0", "1", "2"]) &&
                               fds[0] == "0:CHR /dev/null" &&
                               fds[1].StartsWith("1:PIPE", StringComparison.Ordinal) &&
                               fds[2].StartsWith("2:PIPE", StringComparison.Ordinal),
            "A child started without redirecting stdin must hold exactly /dev/null and its two pipes " +
            "(no protocol fd, no lock file): " + string.Join(" | ", fds));
        CoreHostFixtures.Check(CoreHostProcess.ProcessGroupOf(child) == session.Process.Pgid,
            $"The child is in group {CoreHostProcess.ProcessGroupOf(child)}, not CoreHost's {session.Process.Pgid}.");
        await session.ShutdownAsync();
        CoreHostFixtures.Check(!CoreHostProcess.IsAlive(child), "The child outlived the orderly exit.");
    }

    private static async Task VerifySecondInstanceAsync(string tempRoot)
    {
        (string link, string target) = CoreHostFixtures.CreateLinkToEmptyDirectory(tempRoot);
        using CoreHostSession first = CoreHostSession.Start(tempRoot, "first-instance", userData: target);
        await first.Client.HelloAsync();
        string settings = Path.Combine(target, "settings.json");
        byte[] bytes = "{\"hoverTranslationEnabled\":false}\n"u8.ToArray();
        await File.WriteAllBytesAsync(settings, bytes);
        DateTime written = File.GetLastWriteTimeUtc(settings);

        // The same folder through a symlink: the lock follows the file.
        using CoreHostSession second = CoreHostSession.Start(tempRoot, "second-instance", userData: link);
        CoreHostExit exit = await second.Process.WaitExitAsync(TimeSpan.FromSeconds(10), "a second start on held user data");
        await second.Client.StderrClosed.WaitAsync(TimeSpan.FromSeconds(5));
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(7),
            $"A second CoreHost on the same user data ended with {exit}, expected exit 7. stderr: {second.Client.StderrTail}");
        CoreHostFixtures.Check(second.Client.Trace().Length == 0,
            "The refused second CoreHost wrote protocol frames.");
        await second.FinishAsync(15_000, "settings.json");

        CoreHostFixtures.Check(
            File.ReadAllBytes(settings).AsSpan().SequenceEqual(bytes) && File.GetLastWriteTimeUtc(settings) == written,
            "The refused second CoreHost touched the first one's settings.json.");
        await first.Client.ResultAsync(first.Client.Send("core.ping"));
        await first.ShutdownAsync("settings.json");
    }

    // A killed child of a living parent is a zombie until that parent reaps
    // it, and kill(pid, 0) still finds a zombie; the bound covers the reap.
    private static async Task WaitGoneAsync(int pid, TimeSpan bound, string what)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (CoreHostProcess.IsAlive(pid))
        {
            CoreHostFixtures.Check(clock.Elapsed < bound,
                $"{what} (pid {pid}) is still alive after {clock.ElapsedMilliseconds} ms.");
            await Task.Delay(5);
        }
    }
}
