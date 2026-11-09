using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// A strict protocol-v1 client, the C# twin of the Swift app's transport. It
/// enforces the client's half of the contract on every frame: valid UTF-8
/// JSON objects only, version 1, exactly one terminal per id, events only
/// before their id's terminal, no unknown ids, no unknown envelope members,
/// and a Russian message without the word «ядро» (M15) in every error, fault
/// and status. A broken stream is never resynchronised: the first violation
/// kills the whole group, as the app's supervisor does. stderr is always
/// drained (an undrained pipe would block CoreHost) and every line must carry
/// the "[corehost] " prefix.
/// </summary>
internal sealed class CoreHostClient : IDisposable
{
    private static readonly JsonDocumentOptions FrameOptions = new()
    {
        AllowDuplicateProperties = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly ConcurrentDictionary<long, bool> _terminated = new();
    private readonly ConcurrentQueue<JsonObject> _unsolicited = new();
    private readonly TaskCompletionSource _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stderrClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _trace = [];
    private readonly StringBuilder _stderrTail = new();
    private readonly object _writeGate = new();
    private long _nextId;
    private long _written;
    private long _terminals;
    private long _stderrBytes;
    private string? _violation;
    private string? _stderrViolation;
    private StreamReader? _manualReader;

    public CoreHostClient(CoreHostProcess process, bool readStdout = true)
    {
        Process = process;
        new Thread(DrainStderr) { IsBackground = true, Name = "corehost-test-stderr" }.Start();
        if (readStdout)
        {
            new Thread(ReadStdout) { IsBackground = true, Name = "corehost-test-stdout" }.Start();
        }
    }

    public CoreHostProcess Process { get; }

    /// <summary>Completes when stdout reached EOF (or the reader stopped on a violation).</summary>
    public Task Eof => _eof.Task;

    public Task StderrClosed => _stderrClosed.Task;

    public string? Violation => Volatile.Read(ref _violation);

    public string? StderrViolation => Volatile.Read(ref _stderrViolation);

    public long Written => Interlocked.Read(ref _written);

    public long Terminals => Interlocked.Read(ref _terminals);

    public long StderrBytes => Interlocked.Read(ref _stderrBytes);

    /// <summary>Requests that never got a terminal (meaningful after EOF).</summary>
    public int Lost => _pending.Values.Count(pending => !pending.Terminal.Task.IsCompleted);

    public IReadOnlyList<JsonObject> Unsolicited => _unsolicited.ToArray();

    public string StderrTail
    {
        get
        {
            lock (_stderrTail)
            {
                return _stderrTail.ToString();
            }
        }
    }

    public string[] Trace()
    {
        lock (_trace)
        {
            return _trace.ToArray();
        }
    }

    public sealed class Pending(long id, string command)
    {
        public long Id { get; } = id;

        public string Command { get; } = command;

        public List<JsonObject> Events { get; } = [];

        // Line numbers in the stdout trace, for ordering checks.
        public List<int> EventLines { get; } = [];

        public int TerminalLine { get; set; } = -1;

        public TaskCompletionSource<JsonObject> Terminal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Pending Send(string command, JsonObject? payload = null) =>
        SendWithId(Interlocked.Increment(ref _nextId), command, payload);

    public Pending SendWithId(long id, string command, JsonObject? payload = null)
    {
        // Keeps later automatic ids increasing after an explicit one.
        long seen;
        while ((seen = Interlocked.Read(ref _nextId)) < id &&
               Interlocked.CompareExchange(ref _nextId, id, seen) != seen)
        {
        }

        JsonObject frame = new() { ["version"] = 1, ["id"] = id, ["command"] = command };
        if (payload is not null)
        {
            frame["payload"] = payload;
        }

        Pending pending = new(id, command);
        _pending[id] = pending;
        Interlocked.Increment(ref _written);
        WriteRaw(Encoding.UTF8.GetBytes(frame.ToJsonString() + "\n"));
        return pending;
    }

    /// <summary>Sends a request line exactly as given (the golden transcript's bytes).</summary>
    public Pending SendRawRequest(string line)
    {
        JsonObject frame = JsonNode.Parse(line)!.AsObject();
        long id = frame["id"]!.GetValue<long>();
        long seen;
        while ((seen = Interlocked.Read(ref _nextId)) < id &&
               Interlocked.CompareExchange(ref _nextId, id, seen) != seen)
        {
        }

        Pending pending = new(id, frame["command"]!.GetValue<string>());
        _pending[id] = pending;
        Interlocked.Increment(ref _written);
        WriteRaw(Encoding.UTF8.GetBytes(line + "\n"));
        return pending;
    }

    /// <summary>The stdout line at the given index, or null when stdout ended before it.</summary>
    public async Task<string?> LineAsync(int index, int deadlineMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(deadlineMs);
        while (true)
        {
            bool ended = _eof.Task.IsCompleted;
            lock (_trace)
            {
                if (_trace.Count > index)
                {
                    return _trace[index];
                }
            }

            if (ended)
            {
                return null;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No stdout line #{index + 1} within {deadlineMs} ms. stderr: {StderrTail}");
            }

            await Task.Delay(2);
        }
    }

    /// <summary>Writes bytes as they are; a dead helper (EPIPE) is reported through Eof.</summary>
    public void WriteRaw(byte[] bytes)
    {
        lock (_writeGate)
        {
            try
            {
                Process.Stdin.Write(bytes);
                Process.Stdin.Flush();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void CloseStdin()
    {
        lock (_writeGate)
        {
            Process.Stdin.Dispose();
        }
    }

    public async Task<JsonObject> AwaitAsync(Pending pending, int deadlineMs)
    {
        Task finished = await Task.WhenAny(pending.Terminal.Task, _eof.Task, Task.Delay(deadlineMs));
        if (pending.Terminal.Task.IsCompleted)
        {
            return await pending.Terminal.Task;
        }

        throw new TimeoutException(finished == _eof.Task
            ? $"CoreHost closed stdout before answering {pending.Command} #{pending.Id} " +
              $"(violation: {Violation ?? "none"}; stderr: {StderrTail})"
            : $"{pending.Command} #{pending.Id} missed its {deadlineMs} ms deadline");
    }

    /// <summary>Awaits a terminal and requires ok:true; returns its result.</summary>
    public async Task<JsonObject> ResultAsync(Pending pending, int deadlineMs = 10_000)
    {
        JsonObject terminal = await AwaitAsync(pending, deadlineMs);
        CoreHostFixtures.Check(terminal["ok"]!.GetValue<bool>(),
            $"{pending.Command} #{pending.Id} failed: {terminal.ToJsonString()}");
        return terminal["result"]!.AsObject();
    }

    /// <summary>Awaits a terminal and requires the given error code; returns the error.</summary>
    public async Task<JsonObject> ErrorAsync(Pending pending, string code, int deadlineMs = 10_000)
    {
        JsonObject terminal = await AwaitAsync(pending, deadlineMs);
        CoreHostFixtures.Check(!terminal["ok"]!.GetValue<bool>() &&
                               terminal["error"]!["code"]!.GetValue<string>() == code,
            $"{pending.Command} #{pending.Id} should fail with {code}: {terminal.ToJsonString()}");
        return terminal["error"]!.AsObject();
    }

    public async Task<JsonObject> HelloAsync(bool expectTestCommands = true)
    {
        JsonObject result = await ResultAsync(Send("core.hello", new JsonObject
        {
            ["client"] = "macos-subset",
            ["protocolMin"] = 1,
            ["protocolMax"] = 1,
        }));
        CoreHostFixtures.Check(result["testCommands"]!.GetValue<bool>() == expectTestCommands,
            $"hello reported testCommands {result["testCommands"]}, expected {expectTestCommands}");
        return result;
    }

    /// <summary>Manual mode only: reads and checks the next stdout frame on the calling thread.</summary>
    public JsonObject ReadFrameBlocking()
    {
        _manualReader ??= new StreamReader(Process.Stdout, new UTF8Encoding(false, true));
        string line = _manualReader.ReadLine() ??
            throw new IOException("CoreHost closed stdout. stderr: " + StderrTail);
        JsonObject? frame = Accept(line);
        return frame ?? throw new InvalidOperationException(Violation);
    }

    /// <summary>Manual mode only: closes the read end of CoreHost's stdout.</summary>
    public void CloseStdout() => Process.Stdout.Dispose();

    public void Dispose() => Process.Dispose();

    private void ReadStdout()
    {
        try
        {
            using StreamReader reader = new(Process.Stdout, new UTF8Encoding(false, true));
            while (reader.ReadLine() is { } line)
            {
                if (Accept(line) is null)
                {
                    return;
                }
            }
        }
        catch (DecoderFallbackException)
        {
            Fail("stdout is not valid UTF-8");
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _eof.TrySetResult();
        }
    }

    // Returns the frame, or null after a violation.
    private JsonObject? Accept(string line)
    {
        int lineNumber;
        lock (_trace)
        {
            lineNumber = _trace.Count;
            _trace.Add(line);
        }

        JsonObject frame;
        try
        {
            frame = JsonNode.Parse(line, documentOptions: FrameOptions) as JsonObject ??
                throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return Fail("non-JSON line: " + Shorten(line));
        }

        if (frame["version"] is not JsonValue version || !version.TryGetValue(out int number) || number != 1)
        {
            return Fail("frame without version 1: " + Shorten(line));
        }

        if (frame.ContainsKey("event"))
        {
            return AcceptEvent(frame, line, lineNumber);
        }

        if (frame.Any(member => member.Key is not ("version" or "id" or "ok" or "result" or "error")) ||
            frame["id"] is not JsonValue idValue || !idValue.TryGetValue(out long id) ||
            frame["ok"] is not JsonValue okValue || !okValue.TryGetValue(out bool ok))
        {
            return Fail("malformed response: " + Shorten(line));
        }

        if (ok ? frame["result"] is not JsonObject || frame.ContainsKey("error")
               : frame["error"] is not JsonObject || frame.ContainsKey("result"))
        {
            return Fail("a response needs exactly one of result and error: " + Shorten(line));
        }

        if (!ok && CheckRussian(frame["error"]!["message"], "error message") is { } problem)
        {
            return Fail(problem + ": " + Shorten(line));
        }

        if (!_terminated.TryAdd(id, true))
        {
            return Fail($"a second terminal for #{id}");
        }

        if (!_pending.TryGetValue(id, out Pending? pending))
        {
            return Fail($"a terminal for unknown #{id}");
        }

        pending.TerminalLine = lineNumber;
        Interlocked.Increment(ref _terminals);
        pending.Terminal.TrySetResult(frame);
        return frame;
    }

    private JsonObject? AcceptEvent(JsonObject frame, string line, int lineNumber)
    {
        if (frame.Any(member => member.Key is not ("version" or "event" or "ref" or "payload")) ||
            frame["event"] is not JsonValue nameValue || !nameValue.TryGetValue(out string? name) ||
            frame["payload"] is not JsonObject payload)
        {
            return Fail("malformed event: " + Shorten(line));
        }

        if (name is "core.fault" or "status" &&
            CheckRussian(payload[name == "status" ? "text" : "message"], name) is { } problem)
        {
            return Fail(problem + ": " + Shorten(line));
        }

        if (frame["ref"] is null)
        {
            _unsolicited.Enqueue(frame);
            return frame;
        }

        if (frame["ref"] is not JsonValue refValue || !refValue.TryGetValue(out long reference))
        {
            return Fail("an event ref must be an id: " + Shorten(line));
        }

        if (_terminated.ContainsKey(reference))
        {
            return Fail($"event {name} after the terminal of #{reference}");
        }

        if (!_pending.TryGetValue(reference, out Pending? pending))
        {
            return Fail($"event {name} for unknown #{reference}");
        }

        lock (pending.Events)
        {
            pending.Events.Add(frame);
            pending.EventLines.Add(lineNumber);
        }

        return frame;
    }

    // M15: what may reach the user is Russian and never names the host «ядро».
    private static string? CheckRussian(JsonNode? text, string what)
    {
        if (text is not JsonValue value || !value.TryGetValue(out string? message) || message.Length == 0)
        {
            return $"{what} without text";
        }

        if (!message.Any(character => character is >= 'А' and <= 'я' or 'Ё' or 'ё'))
        {
            return $"{what} is not Russian";
        }

        return message.Contains("ядр", StringComparison.OrdinalIgnoreCase)
            ? $"{what} names the host «ядро» (M15)"
            : null;
    }

    private JsonObject? Fail(string message)
    {
        Interlocked.CompareExchange(ref _violation, message, null);
        Process.KillGroup();
        return null;
    }

    private void DrainStderr()
    {
        try
        {
            using StreamReader reader = new(Process.Stderr, new UTF8Encoding(false, false));
            while (reader.ReadLine() is { } line)
            {
                Interlocked.Add(ref _stderrBytes, Encoding.UTF8.GetByteCount(line) + 1);
                if (!line.StartsWith("[corehost] ", StringComparison.Ordinal))
                {
                    Interlocked.CompareExchange(ref _stderrViolation, "stderr line without the prefix: " + Shorten(line), null);
                }

                lock (_stderrTail)
                {
                    if (line.Length < 512)
                    {
                        _stderrTail.Append(line).Append('\n');
                        if (_stderrTail.Length > 16 * 1024)
                        {
                            _stderrTail.Remove(0, _stderrTail.Length - 8 * 1024);
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _stderrClosed.TrySetResult();
        }
    }

    private static string Shorten(string line) => line.Length <= 300 ? line : line[..300] + "…";
}
