using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;

/// <summary>
/// One CoreHost run's private folders under the run's temporary root. The
/// environment is the curated one the app passes: HOME and
/// CFFIXED_USER_HOME at a home whose Library/Application Support exists but
/// stays empty, a per-run TMPDIR, DOTNET_EnableDiagnostics=0 (no debugger
/// FIFOs or diagnostics socket left in TMPDIR by a killed helper), a fixed
/// PATH and an empty working directory. <see cref="AssertClean"/> then proves
/// CoreHost wrote nothing outside the roots its arguments named, even after
/// SIGKILL.
/// </summary>
internal sealed class CoreHostSandbox
{
    private static int s_counter;

    private CoreHostSandbox(string root)
    {
        Root = root;
        Cwd = Path.Combine(root, "cwd");
        Home = Path.Combine(root, "home");
        Tmp = Path.Combine(root, "tmp");
        UserData = Path.Combine(root, "user");
        Logs = Path.Combine(root, "logs");
    }

    public string Root { get; }

    public string Cwd { get; }

    public string Home { get; }

    public string Tmp { get; }

    public string UserData { get; }

    public string Logs { get; }

    public static CoreHostSandbox Create(string tempRoot, string label)
    {
        string root = Path.Combine(tempRoot, "corehost", $"{Interlocked.Increment(ref s_counter):D2}-{label}");
        CoreHostSandbox sandbox = new(root);
        Directory.CreateDirectory(sandbox.Cwd);
        Directory.CreateDirectory(Path.Combine(sandbox.Home, "Library", "Application Support"));
        Directory.CreateDirectory(sandbox.Tmp);
        Directory.CreateDirectory(sandbox.UserData);
        Directory.CreateDirectory(sandbox.Logs);
        return sandbox;
    }

    public IReadOnlyDictionary<string, string> Environment
    {
        get
        {
            Dictionary<string, string> environment = new(StringComparer.Ordinal)
            {
                ["HOME"] = Home,
                ["CFFIXED_USER_HOME"] = Home,
                ["TMPDIR"] = Tmp + "/",
                ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin",
                ["DOTNET_EnableDiagnostics"] = "0",
            };
            // The framework-dependent apphost finds the runtime through the
            // install location; a developer's DOTNET_ROOT is passed on.
            if (System.Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
            {
                environment["DOTNET_ROOT"] = dotnetRoot;
            }

            return environment;
        }
    }

    /// <summary>The required arguments with this sandbox's roots.</summary>
    public List<string> Arguments(bool testCommands = true, string? userData = null)
    {
        List<string> arguments =
        [
            "--data-root", CoreHostFixtures.DataRoot,
            "--user-data", userData ?? UserData,
            "--log-dir", Logs,
        ];
        if (testCommands)
        {
            arguments.Add("--enable-test-commands");
        }

        return arguments;
    }

    /// <summary>
    /// cwd, TMPDIR and the log folder are empty, home holds only its empty
    /// Library/Application Support, and the user-data folder the run used
    /// only the instance lock plus what the test itself put there.
    /// </summary>
    public void AssertClean(string userData, params string[] testOwnedUserData)
    {
        string[] stray = Entries(Cwd)
            .Concat(Entries(Tmp))
            .Concat(Entries(Logs))
            .Concat(Entries(Home).Where(entry =>
                entry != Path.Combine(Home, "Library") &&
                entry != Path.Combine(Home, "Library", "Application Support")))
            .Concat(userData == UserData ? [] : Entries(UserData))
            .Concat(Entries(userData).Where(entry =>
                entry != Path.Combine(userData, "Runtime") &&
                entry != Path.Combine(userData, "Runtime", "corehost.lock") &&
                !testOwnedUserData.Contains(Path.GetRelativePath(userData, entry))))
            .ToArray();
        CoreHostFixtures.Check(stray.Length == 0,
            "CoreHost left entries outside its roots or its instance lock: " +
            string.Join(", ", stray.Select(entry => Path.GetRelativePath(Root, entry))));
    }

    private static IEnumerable<string> Entries(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            : [];
}

/// <summary>One started CoreHost with its sandbox and client.</summary>
internal sealed class CoreHostSession : IDisposable
{
    private CoreHostSession(CoreHostSandbox sandbox, CoreHostClient client, string userData)
    {
        Sandbox = sandbox;
        Client = client;
        UserData = userData;
    }

    public CoreHostSandbox Sandbox { get; }

    /// <summary>The --user-data this run got (the sandbox's own folder unless overridden).</summary>
    public string UserData { get; }

    public CoreHostClient Client { get; }

    public CoreHostProcess Process => Client.Process;

    public static CoreHostSession Start(
        string tempRoot,
        string label,
        bool testCommands = true,
        IEnumerable<string>? extraArguments = null,
        uint blockedSignals = 0,
        bool readStdout = true,
        string? userData = null)
    {
        CoreHostSandbox sandbox = CoreHostSandbox.Create(tempRoot, label);
        List<string> arguments = sandbox.Arguments(testCommands, userData);
        arguments.AddRange(extraArguments ?? []);
        CoreHostProcess process = CoreHostProcess.Start(
            CoreHostFixtures.Apphost,
            arguments,
            sandbox.Environment,
            sandbox.Cwd,
            blockedSignals);
        return new CoreHostSession(sandbox, new CoreHostClient(process, readStdout), userData ?? sandbox.UserData);
    }

    /// <summary>
    /// Waits for the exit, reaps (killing the whole group first), requires an
    /// empty group within the bound and a clean sandbox, and returns the exit.
    /// </summary>
    public async Task<CoreHostExit> FinishAsync(int exitDeadlineMs = 15_000, params string[] testOwnedUserData)
    {
        CoreHostExit exit = await Process.WaitExitAsync(TimeSpan.FromMilliseconds(exitDeadlineMs), "the step under test");
        CoreHostFixtures.Check(await Process.ReapAndWaitGroupEmptyAsync(TimeSpan.FromSeconds(2)),
            $"The process group of CoreHost {Process.Pid} still has members after its exit and a group kill.");
        await Client.StderrClosed.WaitAsync(TimeSpan.FromSeconds(5));
        CoreHostFixtures.Check(Client.StderrViolation is null, Client.StderrViolation ?? "");
        Sandbox.AssertClean(UserData, testOwnedUserData);
        return exit;
    }

    /// <summary>Asks for core.shutdown and requires its single terminal, exit 0 and a clean end.</summary>
    public async Task ShutdownAsync(params string[] testOwnedUserData)
    {
        await Client.ResultAsync(Client.Send("core.shutdown"), 15_000);
        CoreHostExit exit = await FinishAsync(15_000, testOwnedUserData);
        CoreHostFixtures.Check(exit == CoreHostExit.Exited(0), $"core.shutdown ended with {exit}.");
        CoreHostFixtures.Check(Client.Violation is null, Client.Violation ?? "");
        CoreHostFixtures.Check(Client.Written == Client.Terminals,
            $"Requests written {Client.Written}, terminals {Client.Terminals}: an orderly exit answers every request.");
    }

    public void Dispose() => Client.Dispose();
}

internal static class CoreHostFixtures
{
    // The CoreHost output folder is bin/<Configuration>/<TargetFramework>:
    // the project has no RuntimeIdentifier (MacProjectParityTests pins both).
    internal const string TargetFramework = "net10.0";

    private static readonly Lazy<string> ApphostPath = new(LocateApphost);

    public static string Apphost => ApphostPath.Value;

    /// <summary>The CoreHost build output, which holds Dictionaries/ and Morphology/.</summary>
    public static string DataRoot => Path.GetDirectoryName(Apphost)!;

    public static JsonObject Payload(params (string Name, JsonNode? Value)[] members)
    {
        JsonObject payload = [];
        foreach ((string name, JsonNode? value) in members)
        {
            payload[name] = value;
        }

        return payload;
    }

    /// <summary>
    /// The only helper in the test sources that creates a symbolic link
    /// (MacProjectParityTests checks it). The link and its target both live
    /// in a fresh folder under the run's temporary root, and the target is a
    /// new empty directory, so the link can never lead anywhere else.
    /// </summary>
    public static (string Link, string Target) CreateLinkToEmptyDirectory(string tempRoot)
    {
        string folder = Path.Combine(tempRoot, "links", Guid.NewGuid().ToString("N"));
        string target = Path.Combine(folder, "target");
        string link = Path.Combine(folder, "link");
        Directory.CreateDirectory(target);
        Check(!Directory.EnumerateFileSystemEntries(target).Any(), $"{target} is not empty.");
        Directory.CreateSymbolicLink(link, target);
        return (link, target);
    }

    /// <summary>The numeric fds a process holds, as "fd:type name" (lsof -F ftn).</summary>
    public static async Task<string[]> OpenFileDescriptorsAsync(int pid)
    {
        ProcessStartInfo startInfo = new("/usr/sbin/lsof")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-n", "-P", "-a", "-p", pid.ToString(), "-d", "0-1023", "-F", "ftn" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process lsof = Process.Start(startInfo) ??
            throw new InvalidOperationException("lsof did not start");
        Task<string> stdout = lsof.StandardOutput.ReadToEndAsync();
        Task<string> stderr = lsof.StandardError.ReadToEndAsync();
        await lsof.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        List<string> fds = [];
        string current = "";
        foreach (string line in (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (line[0])
            {
                case 'f':
                    current = line[1..];
                    break;
                case 't':
                    current += ":" + line[1..];
                    break;
                case 'n':
                    fds.Add(current + " " + line[1..]);
                    break;
            }
        }

        _ = await stderr;
        return fds.ToArray();
    }

    /// <summary>The names, kinds and value types of launch-args-v1.txt, in file order.</summary>
    public static (string Name, string Kind, string Value)[] ReadLaunchArguments() =>
        File.ReadAllLines(GoldenPath("launch-args-v1.txt"))
            .Where(line => line.Length != 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' '))
            .Select(parts =>
            {
                Check(parts.Length == 3 &&
                      parts[1] is "required" or "optional" or "test" or "reserved" &&
                      parts[2] is "path" or "ms" or "none",
                    $"launch-args-v1.txt has a malformed line: {string.Join(' ', parts)}");
                return (parts[0], parts[1], parts[2]);
            })
            .ToArray();

    public static string GoldenPath(string fileName) =>
        Path.Combine(MacSmoke.FindRepositoryRoot(), "tests", "AsteriaScale.Tests.MacOS", "CoreHost", fileName);

    public static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string LocateApphost()
    {
        string? configuration = typeof(CoreHostFixtures).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "AsteriaScaleBuildConfiguration")
            ?.Value;
        Check(!string.IsNullOrEmpty(configuration), "The test assembly does not record its build Configuration.");
        string path = Path.Combine(
            MacSmoke.FindRepositoryRoot(),
            "src",
            "AsteriaScale.CoreHost",
            "bin",
            configuration!,
            TargetFramework,
            "AsteriaScale.CoreHost");
        Check(File.Exists(path),
            $"The CoreHost apphost is missing at {path}. Building tests/AsteriaScale.Tests.MacOS builds " +
            "src/AsteriaScale.CoreHost first with the same Configuration.");
        return path;
    }
}
