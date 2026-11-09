using System.Globalization;

namespace AsteriaScale.CoreHost;

internal enum LaunchArgumentKind
{
    Required,
    Optional,
    // Accepted only for the macOS subset's own checks; the app never passes them.
    Test,
    // Named now so a launcher cannot invent another spelling; refused until 20.5.
    Reserved,
}

internal enum LaunchArgumentValue
{
    // An absolute path; a relative one would resolve against whatever cwd the
    // process got.
    Path,
    // A positive number of milliseconds.
    Milliseconds,
    // A flag without a value.
    None,
}

internal sealed record LaunchArgument(string Name, LaunchArgumentKind Kind, LaunchArgumentValue Value);

/// <summary>What CoreHost was started with. Every path is fully qualified.</summary>
internal sealed record HostOptions(
    string DataRoot,
    string UserData,
    string LogDirectory,
    string? CacheDirectory,
    string? RankerWorker,
    string? RankerModels,
    bool TestCommands,
    TimeSpan DrainTimeout);

/// <summary>
/// The launch arguments of protocol v1. They are arguments rather than hello
/// fields because Core needs its roots before any Core code runs, a restart
/// reuses the same argv, and ps and crash reports then show them. The table is
/// mirrored line for line by tests/AsteriaScale.Tests.MacOS/CoreHost/launch-args-v1.txt,
/// which the C# conformance check and the Swift launcher test both read; the
/// usage error prints the accepted names so the check can compare them.
/// </summary>
internal static class LaunchArguments
{
    internal const string ReservedStage = "20.5";

    // The orderly drain waits this long for work that must not be cut short
    // (NotCancellable requests) before giving up with exit 6.
    internal static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(10);

    internal static readonly LaunchArgument[] All =
    [
        new("--data-root", LaunchArgumentKind.Required, LaunchArgumentValue.Path),
        new("--user-data", LaunchArgumentKind.Required, LaunchArgumentValue.Path),
        new("--log-dir", LaunchArgumentKind.Required, LaunchArgumentValue.Path),
        new("--cache-dir", LaunchArgumentKind.Optional, LaunchArgumentValue.Path),
        new("--ranker-worker", LaunchArgumentKind.Optional, LaunchArgumentValue.Path),
        new("--ranker-models", LaunchArgumentKind.Optional, LaunchArgumentValue.Path),
        new("--enable-test-commands", LaunchArgumentKind.Test, LaunchArgumentValue.None),
        new("--drain-timeout-ms", LaunchArgumentKind.Test, LaunchArgumentValue.Milliseconds),
        new("--phrase-models", LaunchArgumentKind.Reserved, LaunchArgumentValue.Path),
        new("--phrase-runtime", LaunchArgumentKind.Reserved, LaunchArgumentValue.Path),
    ];

    private const int MaxDrainTimeoutMs = 600_000;

    internal static bool TryParse(string[] args, out HostOptions? options, out string? error)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            LaunchArgument? argument = Array.Find(All, candidate =>
                string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (argument is null)
            {
                return Fail($"неизвестный аргумент «{name}»", out options, out error);
            }

            if (argument.Kind == LaunchArgumentKind.Reserved)
            {
                return Fail($"{name} зарезервировано для {ReservedStage}", out options, out error);
            }

            if (values.ContainsKey(name))
            {
                return Fail($"аргумент {name} задан дважды", out options, out error);
            }

            if (argument.Value == LaunchArgumentValue.None)
            {
                values[name] = null;
                continue;
            }

            if (index + 1 >= args.Length)
            {
                return Fail($"у аргумента {name} нет значения", out options, out error);
            }

            string value = args[++index];
            if (argument.Value == LaunchArgumentValue.Path &&
                (value.Length == 0 || value.Contains('\0') || !Path.IsPathFullyQualified(value)))
            {
                return Fail($"{name} должен быть абсолютным путём, получено «{value}»", out options, out error);
            }

            if (argument.Value == LaunchArgumentValue.Milliseconds &&
                (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int milliseconds) ||
                 milliseconds < 1 || milliseconds > MaxDrainTimeoutMs))
            {
                return Fail(
                    $"{name} должен быть целым числом миллисекунд от 1 до {MaxDrainTimeoutMs}, получено «{value}»",
                    out options,
                    out error);
            }

            values[name] = value;
        }

        foreach (LaunchArgument argument in All)
        {
            if (argument.Kind == LaunchArgumentKind.Required && !values.ContainsKey(argument.Name))
            {
                return Fail($"не задан обязательный аргумент {argument.Name}", out options, out error);
            }
        }

        // The worker and its model folder only make sense together: Core's
        // RankerWorkerLocation needs both, and guessing the other one would
        // start a worker against a folder nobody named.
        if (values.ContainsKey("--ranker-worker") != values.ContainsKey("--ranker-models"))
        {
            return Fail("--ranker-worker и --ranker-models задаются только вместе", out options, out error);
        }

        bool testCommands = values.ContainsKey("--enable-test-commands");
        TimeSpan drainTimeout = DefaultDrainTimeout;
        if (values.TryGetValue("--drain-timeout-ms", out string? drain))
        {
            // A production launch must never shorten the drain of work that
            // must not be cut short; only the macOS subset's checks may.
            if (!testCommands)
            {
                return Fail("--drain-timeout-ms действует только вместе с --enable-test-commands", out options, out error);
            }

            drainTimeout = TimeSpan.FromMilliseconds(int.Parse(drain!, CultureInfo.InvariantCulture));
        }

        options = new HostOptions(
            values["--data-root"]!,
            values["--user-data"]!,
            values["--log-dir"]!,
            values.GetValueOrDefault("--cache-dir"),
            values.GetValueOrDefault("--ranker-worker"),
            values.GetValueOrDefault("--ranker-models"),
            testCommands,
            drainTimeout);
        error = null;
        return true;
    }

    private static bool Fail(string reason, out HostOptions? options, out string? error)
    {
        options = null;
        error = reason + ". Допустимые аргументы: " +
            string.Join(", ", All.Where(argument => argument.Kind != LaunchArgumentKind.Reserved).Select(argument => argument.Name)) +
            "; зарезервированы для " + ReservedStage + ": " +
            string.Join(", ", All.Where(argument => argument.Kind == LaunchArgumentKind.Reserved).Select(argument => argument.Name)) +
            ".";
        return false;
    }
}
