using AsteriaScale.CoreHost;
using AsteriaScale.CoreHost.Host;
using AsteriaScale.CoreHost.Protocol;

// CoreHost: AsteriaScale.Core as a separate process speaking JSONL protocol v1
// on stdin/stdout (docs/MACOS.md M11). The stdio isolation and the cleanup of
// its process group are POSIX and libproc code, so every other platform is a
// usage error rather than a host without isolation.
if (!OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine(HostLog.Prefix + "CoreHost пока работает только на macOS.");
    return ExitCodes.Usage;
}

return await CoreHostProgram.RunAsync(args);
