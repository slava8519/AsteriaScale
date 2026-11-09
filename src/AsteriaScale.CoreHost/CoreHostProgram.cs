using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AsteriaScale.CoreHost.Host;
using AsteriaScale.CoreHost.Posix;
using AsteriaScale.CoreHost.Protocol;
using AsteriaScale.Services;

namespace AsteriaScale.CoreHost;

/// <summary>
/// The start sequence. Exit codes:
/// 0 orderly exit (core.shutdown or SIGTERM, drained in time);
/// 2 usage error or unsupported platform;
/// 3 the peer broke the protocol (core.fault first);
/// 4 the peer is gone: stdin EOF without core.shutdown, or EPIPE on stdout;
/// 5 the peer stopped reading: the outbound queue passed 16 MiB;
/// 6 the orderly drain missed its bound (core.fault shutdown_timeout first);
/// 7 another CoreHost holds this user-data folder;
/// 70 internal error.
/// Whatever the code, CoreHost kills what it owns before it exits.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class CoreHostProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        // The inherited mask of the main thread, read before anything else
        // runs; every thread .NET creates later starts from it.
        string[] blockedSignals = PosixProcess.ReadBlockedSignals();

        if (!LaunchArguments.TryParse(args, out HostOptions? options, out string? usage))
        {
            HostLog.Write("ошибка запуска: " + usage);
            return ExitCodes.Usage;
        }

        int exitCode;
        try
        {
            exitCode = await ServeAsync(options!, blockedSignals);
        }
        catch (Exception exception)
        {
            HostLog.Write("internal error: " + exception);
            exitCode = ExitCodes.Internal;
        }

        int killed = PosixProcess.KillOwnedProcesses();
        if (killed > 0)
        {
            HostLog.Write($"killed {killed} owned process(es) before exit");
        }

        return exitCode;
    }

    private static async Task<int> ServeAsync(HostOptions options, string[] blockedSignals)
    {
        // Before any Core code can run: a child must never inherit the request
        // pipe as its stdin, and native output must never reach the protocol.
        (Stream input, Stream output) = PosixStdio.Rehome();

        // The first Core-touching statement: every root Core reads or writes
        // comes from the arguments, never from HOME or the working directory.
        CoreDataLayout.Configure(new CoreDataLayout(
            options.DataRoot,
            options.UserData,
            cacheRoot: options.CacheDirectory,
            logRoot: options.LogDirectory));

        // Statuses format the same way on every Mac (rule 9); keys stay Ordinal.
        CultureInfo russian = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = russian;
        CultureInfo.DefaultThreadCurrentUICulture = russian;
        CultureInfo.CurrentCulture = russian;
        CultureInfo.CurrentUICulture = russian;

        using InstanceLock? instanceLock = InstanceLock.TryAcquire(options.UserData);
        if (instanceLock is null)
        {
            HostLog.Write($"другая служба перевода уже работает с каталогом {options.UserData}");
            return ExitCodes.AlreadyRunning;
        }

        CoreHostServer server = new(options, blockedSignals, input, output);
        // The handler starts the same drain as core.shutdown without waiting
        // for the reader, so SIGTERM works while stdin is still open.
        using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            server.RequestShutdown("SIGTERM");
        });
        if (options.TestCommands)
        {
            HostLog.Write("test commands are enabled; the app must refuse this helper");
        }

        HostLog.Write($"pid {Environment.ProcessId} ready for core.hello");
        return await server.RunAsync();
    }
}
