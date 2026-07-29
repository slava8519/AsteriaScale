using System.Diagnostics;

internal static class SandwichInstallerTransactionTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string repositoryRoot = FindRepositoryRoot();
        string helper = Path.Combine(
            repositoryRoot,
            "scripts",
            "sandwich-install-state.ps1");
        Check(File.Exists(helper), "The transactional SANDWiCH installer helper is missing.");

        string root = Path.Combine(tempRoot, "sandwich-installer-transaction");
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "installed");
        Directory.CreateDirectory(Path.Combine(source, "Cpu"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Cpu", "encoder-nv-fp32.onnx"),
            "new-nv");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Cpu", "encoder-v-fp32.onnx"),
            "new-v");
        await File.WriteAllTextAsync(Path.Combine(source, "tokenizer.json"), "new-tokenizer");
        await File.WriteAllTextAsync(
            Path.Combine(source, "model-manifest.json"),
            "new-manifest");

        string fixture = Path.Combine(root, "invoke-transaction.ps1");
        await File.WriteAllTextAsync(
            fixture,
            """
            param($Helper, $Source, $Destination, $Phase = '')
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            . $Helper
            $verify = {
                param($Path)
                foreach ($relative in @(
                        'Cpu\encoder-nv-fp32.onnx',
                        'Cpu\encoder-v-fp32.onnx',
                        'tokenizer.json',
                        'model-manifest.json')) {
                    if (-not (Test-Path -LiteralPath (Join-Path $Path $relative) -PathType Leaf)) {
                        throw "incomplete fixture bundle: $relative"
                    }
                }
            }
            $arguments = @{
                SourceBundle = $Source
                InstallRoot = $Destination
                AcceptanceJson = '{"accepted":true}'
                VerifyBundle = $verify
            }
            if (-not [string]::IsNullOrWhiteSpace($Phase)) {
                $arguments.FaultInjectionPhase = $Phase
            }
            Publish-SandwichBundleTransactional @arguments
            """,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        foreach (string phase in new[] { "after-backup", "after-install" })
        {
            await SeedPreviousBundleAsync(destination, phase);
            ProcessResult failed = await RunPowerShellAsync(
                fixture,
                helper,
                source,
                destination,
                phase);
            Check(failed.ExitCode != 0,
                $"Fault injection '{phase}' unexpectedly committed the new bundle.");
            Check(await File.ReadAllTextAsync(Path.Combine(destination, "previous.txt")) == phase,
                $"Fault injection '{phase}' did not restore the previous bundle.");
            Check(!Directory.EnumerateDirectories(
                    root,
                    ".installed.asteria-sandwich-*",
                    SearchOption.TopDirectoryOnly).Any(),
                $"Fault injection '{phase}' left transaction directories behind: {failed.Error}");
        }

        await SeedPreviousBundleAsync(destination, "success");
        ProcessResult succeeded = await RunPowerShellAsync(
            fixture,
            helper,
            source,
            destination,
            phase: null);
        Check(succeeded.ExitCode == 0,
            $"Transactional SANDWiCH publish failed: {succeeded.Error}");
        Check(!File.Exists(Path.Combine(destination, "previous.txt")),
            "A successful SANDWiCH publish retained stale files from the previous bundle.");
        Check(await File.ReadAllTextAsync(
                  Path.Combine(destination, "Cpu", "encoder-nv-fp32.onnx")) == "new-nv" &&
              File.Exists(Path.Combine(destination, "UPSTREAM_TERMS_ACCEPTED.json")),
            "A successful SANDWiCH publish did not atomically install the complete new bundle.");
        Check(!Directory.EnumerateDirectories(
                root,
                ".installed.asteria-sandwich-*",
                SearchOption.TopDirectoryOnly).Any(),
            "A successful SANDWiCH publish left transaction directories behind.");
    }

    private static async Task SeedPreviousBundleAsync(string destination, string marker)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "previous.txt"), marker);
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string fixture,
        string helper,
        string source,
        string destination,
        string? phase)
    {
        string executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        ProcessStartInfo startInfo = new(executable)
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
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(fixture);
        startInfo.ArgumentList.Add(helper);
        startInfo.ArgumentList.Add(source);
        startInfo.ArgumentList.Add(destination);
        if (phase is not null)
        {
            startInfo.ArgumentList.Add(phase);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AsteriaScale.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Asteria Scale repository root.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
