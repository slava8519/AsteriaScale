using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// "macOS: CoreHost fault containment". Every way CoreHost can die or hang is
/// seen by the client through the pipes and contained by a group kill: a
/// sudden exit(70) and SIGKILL are EOF, a child orphaned by SIGKILL dies with
/// killpg, a hung request hits its deadline, a 16 MiB stderr flood does not
/// stall a draining client, every inbound framing violation is a core.fault
/// and exit 3, a non-JSON stdout line is caught by the client, a peer that
/// stops reading makes CoreHost give up (exit 5) and a closed stdout is EPIPE
/// (exit 4). No case aborts: SIGABRT and SIGSEGV would make ReportCrash write
/// into the owner's real ~/Library/Logs/DiagnosticReports.
/// </summary>
internal static class CoreHostFaultTests
{
    public static async Task RunAsync(string tempRoot)
    {
        await VerifySuddenExitAsync(tempRoot);
        await VerifySigkillAsync(tempRoot);
        await VerifyOrphanAsync(tempRoot);
        await VerifyHangAsync(tempRoot);
        await VerifyStderrFloodAsync(tempRoot);
        await VerifyInboundViolationsAsync(tempRoot);
        await VerifyOutboundViolationAsync(tempRoot);
        await VerifyPeerStopsReadingAsync(tempRoot);
        await VerifyClosedStdoutAsync(tempRoot);
    }

    private static async Task VerifySuddenExitAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "exit70");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        CoreHostClient.Pending inFlight = client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 30_000)));
        client.Send("test.exit");
        await client.Eof.WaitAsync(TimeSpan.FromSeconds(5));
        CoreHostExit exit = await session.FinishAsync();
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(70), $"test.exit ended with {exit}, expected exit 70.");
        CoreHostFixtures.Check(!inFlight.Terminal.Task.IsCompleted && client.Lost == 2 &&
                               client.Written == client.Terminals + client.Lost,
            $"After a sudden exit: written {client.Written}, terminals {client.Terminals}, lost {client.Lost}.");
    }

    private static async Task VerifySigkillAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "sigkill");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 30_000)));
        await client.ResultAsync(client.Send("core.ping"));
        Stopwatch clock = Stopwatch.StartNew();
        session.Process.Signal(CoreHostProcess.SigKill);
        await client.Eof.WaitAsync(TimeSpan.FromSeconds(5));
        CoreHostFixtures.Check(clock.Elapsed < TimeSpan.FromSeconds(1),
            $"SIGKILL reached the client as EOF only after {clock.ElapsedMilliseconds} ms.");
        CoreHostExit exit = await session.FinishAsync();
        CoreHostFixtures.Check(exit == new CoreHostExit(true, CoreHostProcess.SigKill), $"SIGKILL ended with {exit}.");
        CoreHostFixtures.Check(client.Lost == 1, $"SIGKILL lost {client.Lost} requests, expected the one in flight.");
    }

    private static async Task VerifyOrphanAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "orphan");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        int child = (await client.ResultAsync(client.Send("test.spawnLongChild")))["pid"]!.GetValue<int>();
        session.Process.Signal(CoreHostProcess.SigKill);
        await session.Process.WaitExitAsync(TimeSpan.FromSeconds(5), "SIGKILL");
        // Nothing ran inside CoreHost, so its child survives it in the group.
        CoreHostFixtures.Check(CoreHostProcess.IsAlive(child) && CoreHostProcess.ProcessGroupOf(child) == session.Process.Pgid,
            "The orphaned child should still be alive in CoreHost's group after SIGKILL.");
        Stopwatch clock = Stopwatch.StartNew();
        await session.FinishAsync();
        CoreHostFixtures.Check(!CoreHostProcess.IsAlive(child),
            $"killpg did not end the orphan (pid {child}) within {clock.ElapsedMilliseconds} ms.");
    }

    private static async Task VerifyHangAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "hang");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        CoreHostClient.Pending hung = client.Send("test.sleep", CoreHostFixtures.Payload(
            ("ms", 30_000), ("mode", "notCancellable"), ("ignoreCancel", true)));
        Stopwatch clock = Stopwatch.StartNew();
        bool timedOut = false;
        try
        {
            await client.AwaitAsync(hung, 500);
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        CoreHostFixtures.Check(timedOut, "A hung request answered before its deadline.");
        session.Process.KillGroup();
        await client.Eof.WaitAsync(TimeSpan.FromSeconds(5));
        CoreHostExit exit = await session.FinishAsync();
        CoreHostFixtures.Check(exit.Signaled && clock.Elapsed < TimeSpan.FromSeconds(2),
            $"Deadline, group kill and EOF took {clock.ElapsedMilliseconds} ms and ended with {exit}.");
    }

    private static async Task VerifyStderrFloodAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "stderr-flood");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        long before = client.StderrBytes;
        await client.ResultAsync(client.Send("test.stderr", CoreHostFixtures.Payload(("bytes", 16 << 20))), 20_000);
        await client.ResultAsync(client.Send("core.ping"), 3_000);
        CoreHostFixtures.Check(client.StderrBytes - before >= 16 << 20,
            $"Only {client.StderrBytes - before} bytes of the 16 MiB stderr flood were drained.");
        await session.ShutdownAsync();
    }

    private static async Task VerifyInboundViolationsAsync(string tempRoot)
    {
        (string Label, Func<byte[]> Bytes, string Code)[] violations =
        [
            ("non-JSON", () => "hello\n"u8.ToArray(), "invalid_frame"),
            ("invalid UTF-8", () => [.. "{\"version\":1,\"id\":9,\"command\":\"x"u8, 0xC3, 0x28, (byte)'"', (byte)'}', (byte)'\n'], "invalid_frame"),
            ("empty line", () => "\n"u8.ToArray(), "invalid_frame"),
            ("oversized frame", () => Encoding.ASCII.GetBytes(new string('a', (1 << 20) + 10) + "\n"), "frame_too_long"),
            ("reused id", () => "{\"version\":1,\"id\":1,\"command\":\"core.ping\"}\n"u8.ToArray(), "invalid_id"),
            ("bad version", () => "{\"version\":2,\"id\":9,\"command\":\"core.ping\"}\n"u8.ToArray(), "unsupported_version"),
        ];
        await Task.WhenAll(violations.Select(violation => VerifyInboundViolationAsync(tempRoot, violation)));
    }

    private static async Task VerifyInboundViolationAsync(string tempRoot, (string Label, Func<byte[]> Bytes, string Code) violation)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "violation");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        client.WriteRaw(violation.Bytes());
        CoreHostExit exit = await session.FinishAsync();
        JsonObject? fault = client.Unsolicited.FirstOrDefault(frame => frame["event"]!.GetValue<string>() == "core.fault");
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(3) && fault?["payload"]?["code"]?.GetValue<string>() == violation.Code,
            $"Inbound {violation.Label}: {exit}, fault {fault?.ToJsonString() ?? "none"}; expected core.fault {violation.Code} and exit 3.");
        CoreHostFixtures.Check(client.Violation is null &&
                               JsonNode.Parse(client.Trace()[^1])!["event"]?.GetValue<string>() == "core.fault",
            $"Inbound {violation.Label}: core.fault must be the last frame. {client.Violation}");
    }

    private static async Task VerifyOutboundViolationAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "outbound-violation");
        CoreHostClient client = session.Client;
        await client.HelloAsync();
        client.Send("test.violate");
        await client.Eof.WaitAsync(TimeSpan.FromSeconds(5));
        CoreHostExit exit = await session.FinishAsync();
        CoreHostFixtures.Check(client.Violation?.StartsWith("non-JSON line", StringComparison.Ordinal) == true && exit.Signaled,
            $"The client did not stop a host that wrote a non-JSON line ({client.Violation ?? "no violation"}, {exit}).");
    }

    private static async Task VerifyPeerStopsReadingAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "peer-stops-reading", readStdout: false);
        CoreHostClient client = session.Client;
        client.Send("core.hello", CoreHostFixtures.Payload(("client", "macos-subset"), ("protocolMin", 1), ("protocolMax", 1)));
        CoreHostFixtures.Check(client.ReadFrameBlocking()["ok"]!.GetValue<bool>(), "hello failed.");
        // Nobody reads stdout from here on: the pipe fills, the queue grows
        // past 16 MiB, and CoreHost gives up instead of blocking Core.
        client.Send("test.flood", CoreHostFixtures.Payload(("bytes", 24 << 20)));
        CoreHostExit exit = await session.FinishAsync(10_000);
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(5), $"A peer that stopped reading ended CoreHost with {exit}, expected exit 5.");
    }

    private static async Task VerifyClosedStdoutAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "closed-stdout", readStdout: false);
        CoreHostClient client = session.Client;
        client.Send("core.hello", CoreHostFixtures.Payload(("client", "macos-subset"), ("protocolMin", 1), ("protocolMax", 1)));
        CoreHostFixtures.Check(client.ReadFrameBlocking()["ok"]!.GetValue<bool>(), "hello failed.");
        client.CloseStdout();
        client.Send("core.ping");
        CoreHostExit exit = await session.FinishAsync(5_000);
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(4), $"EPIPE on stdout ended CoreHost with {exit}, expected exit 4.");
    }
}
