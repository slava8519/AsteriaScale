using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AsteriaScale.RankerProtocol;
using AsteriaScale.RankerWorker;
using AsteriaScale.Services;

// End-to-end checks of the osx-arm64 worker the macOS subset builds first
// (src/AsteriaScale.RankerWorker.MacOS). They start its apphost the way the
// host would and read the wire output, so the macOS-only behaviour of the
// shared worker sources is covered by a test rather than by manual commands:
// NPU fails loudly with no CPU substitute (AGENTS.md rule 3), and ConSeC
// answers a request from the bundle read in place, never copied (rule 6 for
// the stdout/stderr split).
internal static class MacWorkerTests
{
    // The worker's output folder is bin/<Configuration>/<TargetFramework>/<RID>.
    // MacProjectParityTests pins these two values to the macOS worker project.
    internal const string WorkerTargetFramework = "net10.0";
    internal const string WorkerRuntimeIdentifier = "osx-arm64";

    private const string ExpectedConsecModelVersion =
        "ConSeC:WNGT:9602b5fd:adapter-consec-wngt-positional-extractor-v1:g-0eb0688f3fd2:" +
        "d-1257dc55ebec:t-df91c443c8ea:pretokenizer-v3:context-full-v1:s512:" +
        "pipeline-consec-independent";

    // Generous bounds for a cold start; a worker that hangs must still fail
    // the run instead of stalling it.
    private static readonly TimeSpan NpuRefusalTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConsecSmokeTimeout = TimeSpan.FromMinutes(5);

    public static async Task VerifyNpuRefusedWithoutFallbackAsync()
    {
        string worker = LocateWorkerApphost();

        // The worker runs from its own bin folder with no Models directory, so
        // any error other than npu_provider_unavailable would mean the
        // platform gate no longer comes before artifact access.
        await VerifyNpuRefusalAsync(worker, ["--provider", "npu", "--smoke"], "--provider npu");
        await VerifyNpuRefusalAsync(worker, ["--smoke"], "the default provider");
    }

    public static async Task VerifyConsecSmokeOnRealBundleAsync()
    {
        // A missing apphost is a failure even where the bundle is absent.
        string worker = LocateWorkerApphost();
        string models = Path.Combine(
            MacSmoke.FindRepositoryRoot(),
            "src",
            "AsteriaScale.RankerWorker",
            "Models");
        string bundle = Path.Combine(models, ConsecArtifactBundle.BundleDirectoryName);
        foreach (string relativePath in new[]
                 {
                     ConsecArtifactBundle.TokenizerFileName,
                     ConsecArtifactBundle.ManifestFileName,
                     ConsecArtifactBundle.GraphRelativePath,
                     ConsecArtifactBundle.ExternalDataRelativePath
                 })
        {
            string path = Path.Combine(bundle, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                // The licensed bundle is installed by the owner, never by the
                // build; its absence is reported in the summary line, not hidden.
                MacSmoke.ConsecWorkerSmoke = "absent: " + path;
                return;
            }
        }

        // --model-directory points at the repository folder, so the bundle is
        // read in place; nothing is copied next to the worker.
        WorkerRun run = await RunWorkerAsync(
            worker,
            ["--provider", "cpu", "--cpu-model", "consec", "--model-directory", models, "--smoke"],
            ConsecSmokeTimeout);
        RankerResponse response = SingleResponse(run, "the ConSeC smoke");
        Check(run.ExitCode == 0 &&
              response is { Ok: true, Index: 20, Abstained: false, ErrorCode: null },
            $"The macOS worker did not answer the ConSeC smoke request (exit {run.ExitCode}): " +
            run.Stdout.Trim());
        Check(response.Provider == RankerProviderTraits.Cpu.ExecutionProvider &&
              response.Device == RankerProviderTraits.Cpu.Device &&
              response.Pipeline == "consec-independent",
            $"The ConSeC smoke ran on {response.Provider}/{response.Device} " +
            $"with pipeline {response.Pipeline}.");
        Check(response.ModelVersion == ExpectedConsecModelVersion &&
              response.ModelVersion ==
                  GlossBertDictionarySelectorService.ConsecCpuModelCacheId + ":pipeline-consec-independent",
            $"The macOS worker reported model version '{response.ModelVersion}', which the host " +
            "would not accept as the pinned ConSeC bundle.");
        MacSmoke.ConsecWorkerSmoke = "checked in place";
    }

    private static async Task VerifyNpuRefusalAsync(string worker, string[] arguments, string label)
    {
        WorkerRun run = await RunWorkerAsync(worker, arguments, NpuRefusalTimeout);
        RankerResponse response = SingleResponse(run, label);
        Check(run.ExitCode == 1 &&
              response is { Ok: false, Index: null, ErrorCode: "npu_provider_unavailable" },
            $"With {label} the macOS worker must fail with npu_provider_unavailable and exit 1 " +
            $"(exit {run.ExitCode}): {run.Stdout.Trim()}");
        Check(response.Provider == RankerProviderTraits.Npu.ExecutionProvider &&
              response.Device == RankerProviderTraits.Npu.Device,
            $"With {label} the refusal was attributed to {response.Provider}/{response.Device}; " +
            "a CPU attribution would hide a silent fallback.");
    }

    private static string LocateWorkerApphost()
    {
        string? configuration = typeof(MacWorkerTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "AsteriaScaleBuildConfiguration")
            ?.Value;
        Check(!string.IsNullOrEmpty(configuration),
            "The test assembly does not record its build Configuration.");
        string path = Path.Combine(
            MacSmoke.FindRepositoryRoot(),
            "src",
            "AsteriaScale.RankerWorker.MacOS",
            "bin",
            configuration!,
            WorkerTargetFramework,
            WorkerRuntimeIdentifier,
            "AsteriaScale.RankerWorker");
        Check(File.Exists(path),
            $"The macOS worker apphost is missing at {path}. Building " +
            "tests/AsteriaScale.Tests.MacOS builds src/AsteriaScale.RankerWorker.MacOS first " +
            "with the same Configuration.");
        return path;
    }

    private static async Task<WorkerRun> RunWorkerAsync(
        string worker,
        string[] arguments,
        TimeSpan timeout)
    {
        ProcessStartInfo startInfo = new(worker)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            WorkingDirectory = Path.GetDirectoryName(worker)!
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"The macOS worker did not start: {worker}");
        // --smoke never reads stdin; closing it means a worker that waited for
        // a request would see end of input instead of hanging.
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource deadline = new(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"The macOS worker did not exit within {timeout.TotalSeconds:N0} s: " +
                string.Join(' ', arguments));
        }

        return new WorkerRun(process.ExitCode, await stdout, await stderr);
    }

    private static RankerResponse SingleResponse(WorkerRun run, string label)
    {
        // Rule 6: stdout carries exactly the protocol lines, stderr only
        // [ranker] diagnostics.
        string[] stdoutLines = run.Stdout.Split('\n');
        Check(stdoutLines.Length == 2 && stdoutLines[1].Length == 0 && stdoutLines[0].Length != 0,
            $"With {label} the macOS worker must write exactly one stdout line: {run.Stdout}");
        string[] stderrLines = run.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(stderrLines.Length != 0 &&
              stderrLines.All(line => line.StartsWith("[ranker]", StringComparison.Ordinal)),
            $"With {label} the macOS worker wrote non-diagnostic stderr: {run.Stderr}");

        RankerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<RankerResponse>(stdoutLines[0], RankerProtocolV2.JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"With {label} the macOS worker stdout is not a protocol response: {stdoutLines[0]}",
                exception);
        }

        Check(response is { Version: RankerProtocolV2.Version, RequestId: "smoke" },
            $"With {label} the macOS worker answered outside protocol v{RankerProtocolV2.Version}: " +
            stdoutLines[0]);
        return response!;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record WorkerRun(int ExitCode, string Stdout, string Stderr);
}
