using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// "macOS: CoreHost protocol conformance". Starts the real CoreHost apphost
/// and checks protocol v1 from the outside: the launch arguments against
/// launch-args-v1.txt, request-level errors (hello first, version mismatch,
/// unknown and reserved commands, unmapped, missing and mistyped members),
/// the four cancel outcomes, the ordering guarantees G1-G4, busy at 32
/// outstanding requests, written = terminals, and the golden transcript
/// byte for byte with volatile fields normalized. Hovers run against the
/// fake hover engine of the test commands, so no Core data is read.
/// </summary>
internal static class CoreHostProtocolTests
{
    private const string Sentence = "He sat on the bank of the river.";
    private const int UsageExit = 2;

    public static async Task RunAsync(string tempRoot)
    {
        await VerifyLaunchArgumentsAsync(tempRoot);
        await VerifyRequestsAsync(tempRoot);
        await VerifyGoldenTranscriptAsync(tempRoot);
    }

    // ---- launch arguments ---------------------------------------------

    private static async Task VerifyLaunchArgumentsAsync(string tempRoot)
    {
        (string Name, string Kind, string Value)[] golden = CoreHostFixtures.ReadLaunchArguments();
        string[] required = golden.Where(entry => entry.Kind == "required").Select(entry => entry.Name).ToArray();
        string[] accepted = golden.Where(entry => entry.Kind != "reserved").Select(entry => entry.Name).ToArray();
        string[] reserved = golden.Where(entry => entry.Kind == "reserved").Select(entry => entry.Name).ToArray();
        CoreHostFixtures.Check(required.SequenceEqual(["--data-root", "--user-data", "--log-dir"]) &&
                               reserved.SequenceEqual(["--phrase-models", "--phrase-runtime"]),
            "launch-args-v1.txt no longer names the required and reserved arguments of protocol v1.");

        List<Task> refusals = [];
        // The names the usage error lists are exactly the golden's, in its order.
        refusals.Add(ExpectUsageAsync(tempRoot, sandbox => [.. Base(sandbox), "--bogus", "/x"], "неизвестный аргумент «--bogus»", stderr =>
        {
            Match listed = Regex.Match(stderr, "Допустимые аргументы: (?<accepted>[^;]*); зарезервированы для 20\\.5: (?<reserved>[^.]*)\\.");
            CoreHostFixtures.Check(listed.Success, "The usage error does not list the accepted arguments: " + stderr);
            CoreHostFixtures.Check(
                listed.Groups["accepted"].Value.Split(", ").SequenceEqual(accepted) &&
                listed.Groups["reserved"].Value.Split(", ").SequenceEqual(reserved),
                "CoreHost accepts other arguments than launch-args-v1.txt names: " + listed.Value);
        }));

        foreach (string name in required)
        {
            refusals.Add(ExpectUsageAsync(tempRoot, sandbox => Without(Base(sandbox), name), $"не задан обязательный аргумент {name}"));
        }

        foreach ((string name, string kind, string value) in golden)
        {
            if (value == "path" && kind != "reserved")
            {
                refusals.Add(ExpectUsageAsync(tempRoot, sandbox => With(Base(sandbox), name, "relative/folder"),
                    $"{name} должен быть абсолютным путём"));
            }

            if (kind == "reserved")
            {
                refusals.Add(ExpectUsageAsync(tempRoot, sandbox => With(Base(sandbox), name, "/reserved"),
                    $"{name} зарезервировано для 20.5"));
            }
        }

        // The spellings the prototypes used must not come back by accident.
        foreach (string[] drift in new[]
                 {
                     new[] { "--no-rehome" },
                     ["--log-file", "/x/hover.log"],
                     ["--protocol", "1"],
                     ["--models", "/x"],
                     ["--logs", "/x"],
                 })
        {
            refusals.Add(ExpectUsageAsync(tempRoot, sandbox => [.. Base(sandbox), .. drift], $"неизвестный аргумент «{drift[0]}»"));
        }

        refusals.Add(ExpectUsageAsync(tempRoot, sandbox => [.. Base(sandbox), "--drain-timeout-ms", "300"],
            "--drain-timeout-ms действует только вместе с --enable-test-commands"));
        refusals.Add(ExpectUsageAsync(tempRoot, sandbox => [.. Base(sandbox), "--enable-test-commands", "--drain-timeout-ms", "soon"],
            "--drain-timeout-ms должен быть целым числом"));
        refusals.Add(ExpectUsageAsync(tempRoot, sandbox => [.. Base(sandbox), "--log-dir", "/x"], "аргумент --log-dir задан дважды"));
        refusals.Add(ExpectUsageAsync(tempRoot, sandbox => [.. Base(sandbox), "--cache-dir"], "у аргумента --cache-dir нет значения"));
        await Task.WhenAll(refusals);

        // Every accepted name at once: hello echoes each path, and nothing
        // is created at the optional ones.
        string unused = Path.Combine(tempRoot, "corehost", "optional-paths");
        string cache = Path.Combine(unused, "cache");
        string worker = Path.Combine(unused, "worker", "AsteriaScale.RankerWorker");
        string models = Path.Combine(unused, "models");
        using (CoreHostSession session = CoreHostSession.Start(tempRoot, "all-arguments", extraArguments:
               [
                   "--cache-dir", cache,
                   "--ranker-worker", worker,
                   "--ranker-models", models,
                   "--drain-timeout-ms", "5000",
               ]))
        {
            JsonObject hello = await session.Client.HelloAsync();
            JsonObject paths = hello["paths"]!.AsObject();
            CoreHostFixtures.Check(
                paths["dataRoot"]!.GetValue<string>() == CoreHostFixtures.DataRoot &&
                paths["userData"]!.GetValue<string>() == session.UserData &&
                paths["logDir"]!.GetValue<string>() == session.Sandbox.Logs &&
                paths["cacheDir"]!.GetValue<string>() == cache &&
                paths["rankerWorker"]!.GetValue<string>() == worker &&
                paths["rankerModels"]!.GetValue<string>() == models,
                "hello does not echo the launch paths: " + paths.ToJsonString());
            await session.ShutdownAsync();
            CoreHostFixtures.Check(!Directory.Exists(unused), "CoreHost created a folder named by an optional argument.");
        }

        // Without --enable-test-commands no test.* command exists.
        using (CoreHostSession session = CoreHostSession.Start(tempRoot, "production", testCommands: false))
        {
            await session.Client.HelloAsync(expectTestCommands: false);
            await session.Client.ErrorAsync(
                session.Client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 1))),
                "unknown_command");
            await session.ShutdownAsync();
        }
    }

    private static List<string> Base(CoreHostSandbox sandbox) => sandbox.Arguments(testCommands: false);

    private static List<string> Without(List<string> arguments, string name)
    {
        arguments.RemoveRange(arguments.IndexOf(name), 2);
        return arguments;
    }

    private static List<string> With(List<string> arguments, string name, string value)
    {
        int index = arguments.IndexOf(name);
        if (index >= 0)
        {
            arguments[index + 1] = value;
        }
        else
        {
            arguments.AddRange([name, value]);
        }

        return arguments;
    }

    // A usage error exits 2 before anything else happens: no frame on stdout,
    // nothing created anywhere (not even the instance lock), one prefixed
    // Russian line on stderr.
    private static async Task ExpectUsageAsync(
        string tempRoot,
        Func<CoreHostSandbox, List<string>> arguments,
        string expected,
        Action<string>? inspect = null)
    {
        CoreHostSandbox sandbox = CoreHostSandbox.Create(tempRoot, "usage");
        List<string> argv = arguments(sandbox);
        using CoreHostProcess process = CoreHostProcess.Start(
            CoreHostFixtures.Apphost,
            argv,
            sandbox.Environment,
            sandbox.Cwd);
        using CoreHostClient client = new(process);
        CoreHostExit exit = await process.WaitExitAsync(TimeSpan.FromSeconds(15), "a launch with bad arguments");
        await client.Eof.WaitAsync(TimeSpan.FromSeconds(5));
        await client.StderrClosed.WaitAsync(TimeSpan.FromSeconds(5));
        string stderr = client.StderrTail;
        string described = string.Join(' ', argv);
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(UsageExit),
            $"CoreHost {described} ended with {exit}, expected exit 2. stderr: {stderr}");
        CoreHostFixtures.Check(stderr.Contains(expected, StringComparison.Ordinal),
            $"CoreHost {described}: stderr does not say «{expected}»: {stderr}");
        CoreHostFixtures.Check(client.Trace().Length == 0 && client.StderrViolation is null,
            $"CoreHost {described} wrote to stdout or unprefixed stderr before exiting 2.");
        CoreHostFixtures.Check(!Directory.EnumerateFileSystemEntries(sandbox.UserData).Any(),
            $"CoreHost {described} created something in user-data before refusing its arguments.");
        sandbox.AssertClean(sandbox.UserData);
        inspect?.Invoke(stderr);
    }

    // ---- request-level behaviour --------------------------------------

    private static async Task VerifyRequestsAsync(string tempRoot)
    {
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "conformance");
        CoreHostClient client = session.Client;

        // Hello first; a version mismatch is a request error and hello stays undone.
        await client.ErrorAsync(client.Send("core.ping"), "hello_required");
        await client.ErrorAsync(client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 1))), "hello_required");
        await client.ErrorAsync(client.Send("core.hello", CoreHostFixtures.Payload(
            ("client", "macos-subset"), ("protocolMin", 2), ("protocolMax", 3))), "unsupported_version");
        await client.ErrorAsync(client.Send("core.ping"), "hello_required");
        await client.ErrorAsync(client.Send("core.hello", CoreHostFixtures.Payload(
            ("client", "macos-subset"), ("protocolMin", 1), ("protocolMax", 1), ("color", "blue"))), "bad_request");
        JsonObject hello = await client.HelloAsync();
        CoreHostFixtures.Check(hello["protocol"]!.GetValue<int>() == 1 && hello["platform"]!.GetValue<string>() == "macos",
            "hello reports another protocol or platform: " + hello.ToJsonString());
        JsonObject capabilities = hello["capabilities"]!.AsObject();
        CoreHostFixtures.Check(
            capabilities["senseProviders"]!.ToJsonString() == "[\"cpu\"]" &&
            capabilities["hoverSources"]!.ToJsonString() == "[\"selection\"]" &&
            capabilities["phraseBackends"]!.ToJsonString() == "[]",
            "hello lists capabilities this platform cannot run (rule 3): " + capabilities.ToJsonString());
        await client.ErrorAsync(client.Send("core.hello", CoreHostFixtures.Payload(
            ("client", "macos-subset"), ("protocolMin", 1), ("protocolMax", 1))), "bad_request");

        // Unknown and reserved commands.
        await client.ErrorAsync(client.Send("no.such"), "unknown_command");
        foreach ((string command, string stage) in new[]
                 {
                     ("ocr.select", "20.3"),
                     ("models.install", "20.5"),
                     ("models.verify", "20.5"),
                     ("phrase.variants", "20.5"),
                 })
        {
            JsonObject error = await client.ErrorAsync(client.Send(command), "not_implemented");
            CoreHostFixtures.Check(error["detail"]!.GetValue<string>().EndsWith(stage, StringComparison.Ordinal),
                $"{command} does not name its stage {stage}: {error.ToJsonString()}");
        }

        JsonObject observations = await client.ErrorAsync(client.Send("hover.resolve", CoreHostFixtures.Payload(
            ("source", CoreHostFixtures.Payload(("kind", "observations"), ("boxes", new JsonArray()))))), "not_implemented");
        CoreHostFixtures.Check(observations["detail"]!.GetValue<string>().EndsWith("20.3", StringComparison.Ordinal),
            "An observations hover does not name stage 20.3.");
        await client.ErrorAsync(client.Send("hover.resolve", Hover("bank")), "not_ready");

        // Unmapped, missing, mistyped and null members are request errors.
        await client.ErrorAsync(client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 1), ("typo", 1))), "bad_request");
        await client.ErrorAsync(client.Send("test.sleep", CoreHostFixtures.Payload(("ms", "1"))), "bad_request");
        await client.ErrorAsync(client.Send("test.sleep", []), "bad_request");
        await client.ErrorAsync(client.Send("request.cancel", CoreHostFixtures.Payload(("target", 1), ("reason", "x"))), "bad_request");
        await client.ErrorAsync(client.Send("core.ping", CoreHostFixtures.Payload(("now", true))), "bad_request");
        JsonObject withExtra = Hover("bank");
        withExtra["source"]!["font"] = "serif";
        await client.ErrorAsync(client.Send("hover.resolve", withExtra), "bad_request");
        JsonObject nullWord = Hover("bank");
        nullWord["source"]!["hoveredWord"] = null;
        await client.ErrorAsync(client.Send("hover.resolve", nullWord), "bad_request");
        JsonObject tooLong = Hover("bank");
        tooLong["source"]!["text"] = new string('a', 4001);
        await client.ErrorAsync(client.Send("hover.resolve", tooLong), "bad_request");
        JsonObject outside = Hover("bank");
        outside["source"]!["characterIndex"] = Sentence.Length + 1;
        await client.ErrorAsync(client.Send("hover.resolve", outside), "bad_request");

        await VerifyCancelAsync(client);
        await VerifyEventsAsync(client);
        await VerifyHoverLaneAsync(client);
        await VerifyBusyAsync(client);

        await session.ShutdownAsync();
    }

    private static async Task VerifyCancelAsync(CoreHostClient client)
    {
        // Immediate: the target's terminal comes first (G4), then cancelled:true.
        CoreHostClient.Pending immediate = client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 10_000)));
        CoreHostClient.Pending cancel = client.Send("request.cancel", CoreHostFixtures.Payload(("target", immediate.Id)));
        JsonObject cancelled = await client.ResultAsync(cancel);
        CoreHostFixtures.Check(cancelled.ToJsonString() == "{\"cancelled\":true}",
            "Cancelling an Immediate request: " + cancelled.ToJsonString());
        await client.ErrorAsync(immediate, "cancelled", 100);
        CoreHostFixtures.Check(immediate.TerminalLine >= 0 && immediate.TerminalLine < cancel.TerminalLine,
            "G4: the cancelled request's terminal must precede the cancel's own terminal.");

        // Cooperative: the token is signalled and the request ends with its own terminal.
        CoreHostClient.Pending cooperative = client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 10_000), ("mode", "cooperative")));
        JsonObject requested = await client.ResultAsync(client.Send("request.cancel", CoreHostFixtures.Payload(("target", cooperative.Id))));
        CoreHostFixtures.Check(requested.ToJsonString() == "{\"cancelled\":false,\"reason\":\"requested\"}",
            "Cancelling a Cooperative request: " + requested.ToJsonString());
        await client.ErrorAsync(cooperative, "cancelled", 5_000);

        // NotCancellable: refused, and the request completes.
        CoreHostClient.Pending sideEffect = client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 200), ("mode", "notCancellable")));
        JsonObject refused = await client.ResultAsync(client.Send("request.cancel", CoreHostFixtures.Payload(("target", sideEffect.Id))));
        CoreHostFixtures.Check(refused.ToJsonString() == "{\"cancelled\":false,\"reason\":\"not_cancellable\"}",
            "Cancelling a NotCancellable request: " + refused.ToJsonString());
        await client.ResultAsync(sideEffect, 5_000);

        // Not in flight: finished, never admitted, or a target that is not earlier.
        JsonObject finished = await client.ResultAsync(client.Send("request.cancel", CoreHostFixtures.Payload(("target", immediate.Id))));
        CoreHostFixtures.Check(finished.ToJsonString() == "{\"cancelled\":false,\"reason\":\"not_in_flight\"}",
            "Cancelling a finished request: " + finished.ToJsonString());
        long own = cancel.Id + 1000;
        await client.ErrorAsync(client.SendWithId(own, "request.cancel", CoreHostFixtures.Payload(("target", own))), "bad_request");
    }

    private static async Task VerifyEventsAsync(CoreHostClient client)
    {
        // G1: events only before their terminal.
        CoreHostClient.Pending ticking = client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 200), ("ticks", 4)));
        await client.ResultAsync(ticking, 5_000);
        CoreHostFixtures.Check(ticking.Events.Count == 4 && ticking.EventLines.All(line => line < ticking.TerminalLine),
            $"G1: expected 4 events before the terminal, got {ticking.Events.Count}.");

        // Work that ignores its cancellation keeps emitting after its
        // terminal; the writer drops those events instead of sending them.
        JsonObject before = await client.ResultAsync(client.Send("test.stats"));
        CoreHostClient.Pending stubborn = client.Send("test.sleep", CoreHostFixtures.Payload(
            ("ms", 300), ("ticks", 6), ("ignoreCancel", true)));
        await WaitForAsync(() => stubborn.Events.Count > 0, "the first tick");
        await client.ResultAsync(client.Send("request.cancel", CoreHostFixtures.Payload(("target", stubborn.Id))));
        await client.ErrorAsync(stubborn, "cancelled", 100);
        await Task.Delay(450);
        JsonObject after = await client.ResultAsync(client.Send("test.stats"));
        CoreHostFixtures.Check(
            after["droppedEvents"]!.GetValue<long>() > before["droppedEvents"]!.GetValue<long>(),
            "The events of a cancelled request were neither seen nor dropped: " + after.ToJsonString());
        CoreHostFixtures.Check(client.Violation is null, client.Violation ?? "");
    }

    private static async Task VerifyHoverLaneAsync(CoreHostClient client)
    {
        // G2: the primary card first, a provisional phrase line before the final one.
        await client.ResultAsync(client.Send("test.fakeCore", CoreHostFixtures.Payload(
            ("phrase", "provisional"), ("phraseDelayMs", 20))));
        CoreHostClient.Pending hover = client.Send("hover.resolve", Hover("bank"));
        JsonObject result = await client.ResultAsync(hover);
        string[] order = hover.Events.Select(frame =>
            frame["event"]!.GetValue<string>() +
            (frame["payload"]!["provisional"] is { } provisional ? ":" + provisional.ToJsonString() : "")).ToArray();
        CoreHostFixtures.Check(order.SequenceEqual(["hover.primary", "hover.phrase:true", "hover.phrase:false"]),
            "G2: hover events in the wrong order: " + string.Join(", ", order));
        JsonObject timings = result["timings"]!.AsObject();
        CoreHostFixtures.Check(
            result["outcome"]!.GetValue<string>() == "completed" &&
            new[] { "queuedMs", "primaryMs", "phraseFirstMs", "phraseFinalMs" }.All(timings.ContainsKey) &&
            timings["primaryMs"]!.GetValue<double>() <= timings["phraseFirstMs"]!.GetValue<double>() &&
            timings["phraseFirstMs"]!.GetValue<double>() <= timings["phraseFinalMs"]!.GetValue<double>(),
            "The hover result lacks its outcome or ordered timings: " + result.ToJsonString());

        // G3: a newer hover supersedes; the old terminal is queued before
        // anything the new hover emits.
        await client.ResultAsync(client.Send("test.fakeCore", CoreHostFixtures.Payload(("primaryDelaysMs", Delay("bank", 10_000)))));
        CoreHostClient.Pending first = client.Send("hover.resolve", Hover("bank"));
        CoreHostClient.Pending second = client.Send("hover.resolve", Hover("river"));
        JsonObject superseded = await client.ErrorAsync(first, "superseded");
        await client.ResultAsync(second);
        CoreHostFixtures.Check(
            superseded["supersededBy"]!.GetValue<long>() == second.Id &&
            first.Events.Count == 0 &&
            second.EventLines.All(line => line > first.TerminalLine),
            "G3: the superseded terminal must name its successor and precede its events.");

        // One hover in the engine at a time: a superseded hover whose engine
        // ignores cancellation holds the next one back until it returns.
        await client.ResultAsync(client.Send("test.fakeCore", CoreHostFixtures.Payload(
            ("primaryDelaysMs", Delay("bank", 400)), ("ignoreCancel", true))));
        CoreHostClient.Pending slow = client.Send("hover.resolve", Hover("bank"));
        await WaitForEngineCallsAsync(client, 1);
        CoreHostClient.Pending next = client.Send("hover.resolve", Hover("river"));
        await client.ErrorAsync(slow, "superseded");
        JsonObject queued = await client.ResultAsync(next);
        CoreHostFixtures.Check(queued["timings"]!["queuedMs"]!.GetValue<double>() >= 250 && slow.Events.Count == 0,
            "The next hover entered the engine before the superseded one returned: " + queued.ToJsonString());

        // An explicit cancel of an in-flight hover.
        await client.ResultAsync(client.Send("test.fakeCore", CoreHostFixtures.Payload(("primaryDelaysMs", Delay("bank", 10_000)))));
        CoreHostClient.Pending inFlight = client.Send("hover.resolve", Hover("bank"));
        CoreHostClient.Pending cancel = client.Send("request.cancel", CoreHostFixtures.Payload(("target", inFlight.Id)));
        CoreHostFixtures.Check((await client.ResultAsync(cancel))["cancelled"]!.GetValue<bool>(), "A hover cancel was refused.");
        await client.ErrorAsync(inFlight, "cancelled");
        CoreHostFixtures.Check(inFlight.TerminalLine < cancel.TerminalLine, "G4 for a hover.");

        // A failing hover is an outcome plus a Russian status, never a dead host.
        await client.ResultAsync(client.Send("test.fakeCore", CoreHostFixtures.Payload(("fail", true))));
        JsonObject failed = await client.ResultAsync(client.Send("hover.resolve", Hover("bank")));
        CoreHostFixtures.Check(failed["outcome"]!.GetValue<string>() == "failed", "A failing hover: " + failed.ToJsonString());
        CoreHostFixtures.Check(client.Unsolicited.Any(frame =>
                frame["event"]!.GetValue<string>() == "status" &&
                frame["payload"]!["area"]!.GetValue<string>() == "hover" &&
                frame["payload"]!["code"]?.GetValue<string>() == "hover_failed"),
            "A failing hover emitted no hover status.");
        await client.ResultAsync(client.Send("core.ping"));
    }

    private static async Task VerifyBusyAsync(CoreHostClient client)
    {
        List<CoreHostClient.Pending> sleeping = [];
        for (int index = 0; index < 32; index++)
        {
            sleeping.Add(client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 30_000))));
        }

        await client.ErrorAsync(client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 1))), "busy");
        // The control lane is not counted: ping and cancel still work.
        JsonObject ping = await client.ResultAsync(client.Send("core.ping"));
        CoreHostFixtures.Check(ping["outstanding"]!.GetValue<int>() == 32,
            "ping does not report 32 outstanding requests: " + ping.ToJsonString());
        foreach (CoreHostClient.Pending pending in sleeping)
        {
            await client.ResultAsync(client.Send("request.cancel", CoreHostFixtures.Payload(("target", pending.Id))));
            await client.ErrorAsync(pending, "cancelled", 100);
        }

        await client.ResultAsync(client.Send("test.sleep", CoreHostFixtures.Payload(("ms", 1))));
        CoreHostFixtures.Check(client.Written == client.Terminals,
            $"Requests written {client.Written}, terminals {client.Terminals}.");
    }

    private static JsonObject Delay(string word, int milliseconds) => CoreHostFixtures.Payload((word, milliseconds));

    // The superseded hover must be inside the engine before its successor
    // arrives, or there would be nothing to hold the successor back.
    private static async Task WaitForEngineCallsAsync(CoreHostClient client, int calls)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while ((await client.ResultAsync(client.Send("test.stats")))["engineCalls"]!.GetValue<int>() < calls)
        {
            CoreHostFixtures.Check(DateTime.UtcNow < deadline, "The fake hover engine was never called.");
            await Task.Delay(5);
        }
    }

    private static JsonObject Hover(string word) => CoreHostFixtures.Payload(("source", CoreHostFixtures.Payload(
        ("kind", "selection"),
        ("text", Sentence),
        ("hoveredWord", word),
        ("characterIndex", Sentence.IndexOf(word, StringComparison.Ordinal)))));

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            CoreHostFixtures.Check(DateTime.UtcNow < deadline, "Timed out waiting for " + what + ".");
            await Task.Delay(5);
        }
    }

    // ---- golden transcript --------------------------------------------

    // Values that differ from run to run; the transcript holds a fixed
    // stand-in of the same JSON kind, and the actual value is checked here
    // before it is replaced.
    private static readonly Dictionary<string, string[]> VolatileMembers = new(StringComparer.Ordinal)
    {
        ["core.hello"] =
        [
            "coreHostVersion", "coreVersion", "build", "runtime", "pid", "pgid",
            "paths.dataRoot", "paths.userData", "paths.logDir",
        ],
        ["core.ping"] = ["uptimeMs", "gcHeapBytes", "workingSetBytes"],
        ["hover.resolve"] = ["timings.queuedMs", "timings.primaryMs", "timings.phraseFirstMs", "timings.phraseFinalMs"],
    };

    private static async Task VerifyGoldenTranscriptAsync(string tempRoot)
    {
        string[] golden = File.ReadAllLines(CoreHostFixtures.GoldenPath("protocol-v1.golden.jsonl"));
        CoreHostFixtures.Check(golden.Length > 0 && golden.All(line => line.Length != 0 && !line.Contains('\r')),
            "protocol-v1.golden.jsonl must hold one LF-terminated frame per line.");
        JsonSerializerOptions output = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using CoreHostSession session = CoreHostSession.Start(tempRoot, "golden");
        CoreHostClient client = session.Client;
        Dictionary<long, string> commands = [];
        int received = 0;
        for (int index = 0; index < golden.Length; index++)
        {
            JsonObject expected = JsonNode.Parse(golden[index])!.AsObject();
            if (expected.ContainsKey("command"))
            {
                commands[expected["id"]!.GetValue<long>()] = expected["command"]!.GetValue<string>();
                client.SendRawRequest(golden[index]);
                continue;
            }

            string actual = await client.LineAsync(received++, 10_000) ??
                throw new InvalidOperationException(
                    $"Golden line {index + 1}: CoreHost closed stdout instead of sending {golden[index]}");
            JsonObject frame = JsonNode.Parse(actual)!.AsObject();
            string? command = frame["id"] is { } id && commands.TryGetValue(id.GetValue<long>(), out string? name) ? name : null;
            string normalized = actual;
            if (command is not null &&
                VolatileMembers.TryGetValue(command, out string[]? members) &&
                frame["result"] is JsonObject result &&
                expected["result"] is JsonObject expectedResult)
            {
                foreach (string member in members)
                {
                    Replace(result, expectedResult, member, session, index + 1);
                }

                normalized = frame.ToJsonString(output);
            }

            CoreHostFixtures.Check(normalized == golden[index],
                $"Golden line {index + 1} differs.\nexpected: {golden[index]}\nactual:   {normalized}");
        }

        CoreHostExit exit = await session.FinishAsync();
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(0) && client.Trace().Length == received,
            $"After the golden transcript CoreHost ended with {exit} and sent {client.Trace().Length - received} extra frame(s).");
        CoreHostFixtures.Check(client.Violation is null && client.Written == client.Terminals,
            client.Violation ?? $"golden: written {client.Written}, terminals {client.Terminals}");
    }

    private static void Replace(JsonObject actual, JsonObject expected, string path, CoreHostSession session, int line)
    {
        string[] parts = path.Split('.');
        JsonObject actualParent = actual, expectedParent = expected;
        foreach (string part in parts[..^1])
        {
            actualParent = actualParent[part]?.AsObject() ?? throw new InvalidOperationException($"Golden line {line}: no {path}.");
            expectedParent = expectedParent[part]?.AsObject() ?? throw new InvalidOperationException($"Golden line {line}: no {path} in the golden.");
        }

        string leaf = parts[^1];
        JsonNode? value = actualParent[leaf];
        JsonNode? stand = expectedParent[leaf];
        if (value is null && stand is null)
        {
            return;
        }

        CoreHostFixtures.Check(value is JsonValue && stand is JsonValue && value.GetValueKind() == stand.GetValueKind(),
            $"Golden line {line}: {path} is {value?.ToJsonString() ?? "absent"}, the golden has {stand?.ToJsonString() ?? "nothing"}.");
        string text = value!.ToJsonString();
        bool plausible = path switch
        {
            "pid" or "pgid" => value.GetValue<int>() == session.Process.Pid,
            "paths.dataRoot" => value.GetValue<string>() == CoreHostFixtures.DataRoot,
            "paths.userData" => value.GetValue<string>() == session.UserData,
            "paths.logDir" => value.GetValue<string>() == session.Sandbox.Logs,
            "coreHostVersion" or "coreVersion" => Regex.IsMatch(value.GetValue<string>(), "^[0-9]+\\.[0-9]+\\.[0-9]+$"),
            "build" or "runtime" => value.GetValue<string>().Length > 0,
            _ => value.GetValue<double>() >= 0,
        };
        CoreHostFixtures.Check(plausible, $"Golden line {line}: {path} has an implausible value {text}.");
        actualParent[leaf] = stand!.DeepClone();
    }
}
