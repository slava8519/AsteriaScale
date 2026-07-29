using System.Diagnostics;

internal static class ConsecBundleInfrastructureTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string repositoryRoot = FindRepositoryRoot();
        string transactionHelper = Path.Combine(
            repositoryRoot,
            "scripts",
            "consec-install-state.ps1");
        string verifier = Path.Combine(repositoryRoot, "scripts", "verify-consec.ps1");
        string buildState = Path.Combine(repositoryRoot, "scripts", "build-state.ps1");
        Check(File.Exists(transactionHelper), "The transactional ConSeC installer helper is missing.");
        Check(File.Exists(verifier), "The pinned ConSeC verifier is missing.");

        string root = Path.Combine(tempRoot, "consec-bundle-infrastructure");
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "installed");
        await SeedSourceBundleAsync(source);

        string transactionFixture = Path.Combine(root, "invoke-transaction.ps1");
        await File.WriteAllTextAsync(
            transactionFixture,
            """
            param($Helper, $Source, $Destination, $Phase = '')
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            . $Helper
            $expected = [ordered]@{
                'Cpu\consec-wngt-fp32.onnx' = 'new-graph'
                'Cpu\consec-wngt-fp32.onnx.data' = 'new-data'
                'tokenizer.json' = 'new-tokenizer'
                'model-manifest.json' = 'new-manifest'
            }
            $verify = {
                param($Path)
                foreach ($entry in $expected.GetEnumerator()) {
                    $candidate = Join-Path $Path $entry.Key
                    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf) -or
                        (Get-Content -LiteralPath $candidate -Raw) -ne $entry.Value) {
                        throw "corrupt fixture bundle: $($entry.Key)"
                    }
                }
            }.GetNewClosure()
            $arguments = @{
                SourceBundle = $Source
                InstallRoot = $Destination
                VerifyBundle = $verify
            }
            if (-not [string]::IsNullOrWhiteSpace($Phase)) {
                $arguments.FaultInjectionPhase = $Phase
            }
            Publish-ConsecBundleTransactional @arguments
            """,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        foreach (string phase in new[] { "after-backup", "after-install" })
        {
            await SeedPreviousBundleAsync(destination, phase);
            ProcessResult failed = await RunPowerShellAsync(
                transactionFixture,
                transactionHelper,
                source,
                destination,
                phase);
            Check(failed.ExitCode != 0,
                $"Fault injection '{phase}' unexpectedly committed the ConSeC bundle.");
            Check(await File.ReadAllTextAsync(Path.Combine(destination, "previous.txt")) == phase,
                $"Fault injection '{phase}' did not restore the previous ConSeC bundle.");
            AssertNoTransactionDirectories(root);
        }

        await SeedPreviousBundleAsync(destination, "corrupt-source");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Cpu", "consec-wngt-fp32.onnx"),
            "corrupt");
        ProcessResult corrupt = await RunPowerShellAsync(
            transactionFixture,
            transactionHelper,
            source,
            destination);
        Check(corrupt.ExitCode != 0, "A corrupt ConSeC source bundle was installed.");
        Check(await File.ReadAllTextAsync(Path.Combine(destination, "previous.txt")) == "corrupt-source",
            "Source corruption changed the previously installed ConSeC bundle.");
        AssertNoTransactionDirectories(root);

        await SeedSourceBundleAsync(source);
        await SeedPreviousBundleAsync(destination, "success");
        ProcessResult succeeded = await RunPowerShellAsync(
            transactionFixture,
            transactionHelper,
            source,
            destination);
        Check(succeeded.ExitCode == 0,
            $"Transactional ConSeC publish failed: {succeeded.Error}");
        Check(!File.Exists(Path.Combine(destination, "previous.txt")),
            "A successful ConSeC publish retained stale files from the previous bundle.");
        Check(await File.ReadAllTextAsync(
                  Path.Combine(destination, "Cpu", "consec-wngt-fp32.onnx.data")) == "new-data",
            "A successful ConSeC publish did not atomically install the complete bundle.");
        AssertNoTransactionDirectories(root);

        await VerifyPinnedVerifierRejectsCorruptionAsync(root, verifier);
        await VerifyConditionalBuildStateAsync(root, buildState);
    }

    private static async Task VerifyPinnedVerifierRejectsCorruptionAsync(
        string root,
        string verifier)
    {
        string corruptBundle = Path.Combine(root, "pinned-corrupt");
        string cpu = Path.Combine(corruptBundle, "Cpu");
        Directory.CreateDirectory(cpu);
        await using (FileStream graph = new(
            Path.Combine(cpu, "consec-wngt-fp32.onnx"),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            graph.SetLength(843_194);
        }

        ProcessResult result = await RunPowerShellAsync(
            verifier,
            "-InstallRoot",
            corruptBundle);
        Check(result.ExitCode != 0 && result.Error.Contains("SHA-256", StringComparison.Ordinal),
            "The pinned ConSeC verifier did not reject same-size content corruption.");
    }

    private static async Task VerifyConditionalBuildStateAsync(string root, string buildState)
    {
        string fixtureRoot = Path.Combine(root, "build-state");
        string output = Path.Combine(fixtureRoot, "dist", "AsteriaScale");
        string fixture = Path.Combine(root, "verify-build-state.ps1");
        await File.WriteAllTextAsync(
            fixture,
            """
            param($Helper, $Root, $Output)
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            . $Helper
            $source = Join-Path $Root 'src\AsteriaScale.RankerWorker\Models\Consec'
            $cpu = Join-Path $source 'Cpu'
            $published = Join-Path $Output 'Ranker\Models\Consec'
            if (Test-AsteriaConsecBundleComplete -Root $Root) {
                throw 'An absent ConSeC bundle was reported as complete.'
            }
            if (@(Get-AsteriaRequiredDistFiles -Root $Root -Output $Output) |
                Where-Object { $_.StartsWith($published, [StringComparison]::OrdinalIgnoreCase) }) {
                throw 'An absent optional ConSeC bundle became release-required.'
            }

            New-Item -ItemType Directory -Path $cpu -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $cpu 'consec-wngt-fp32.onnx') -Value 'graph'
            Set-Content -LiteralPath (Join-Path $cpu 'consec-wngt-fp32.onnx.data') -Value 'data'
            Set-Content -LiteralPath (Join-Path $source 'tokenizer.json') -Value '{}'
            if (Test-AsteriaConsecBundleComplete -Root $Root) {
                throw 'A partial ConSeC bundle was reported as complete.'
            }

            Set-Content -LiteralPath (Join-Path $source 'model-manifest.json') -Value '{}'
            if (-not (Test-AsteriaConsecBundleComplete -Root $Root)) {
                throw 'The complete four-file ConSeC bundle was not recognized.'
            }
            $required = @(Get-AsteriaRequiredDistFiles -Root $Root -Output $Output) |
                Where-Object { $_.StartsWith($published, [StringComparison]::OrdinalIgnoreCase) }
            foreach ($relative in Get-AsteriaConsecBundleRelativeFiles) {
                $expected = Join-Path $published $relative
                if ($required -notcontains $expected) {
                    throw "The enabled ConSeC bundle does not require: $expected"
                }
            }

            $explicitRoot = Join-Path $Root 'explicit-absent'
            $explicitOutput = Join-Path $explicitRoot 'dist\AsteriaScale'
            $explicitPublished = Join-Path $explicitOutput 'Ranker\Models\Consec'
            $explicit = @(Get-AsteriaRequiredDistFiles `
                -Root $explicitRoot `
                -Output $explicitOutput `
                -IncludeExperimentalConsecModel)
            foreach ($relative in Get-AsteriaConsecBundleRelativeFiles) {
                $expected = Join-Path $explicitPublished $relative
                if ($explicit -notcontains $expected) {
                    throw "An explicit ConSeC publish does not require: $expected"
                }
            }
            """,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        ProcessResult result = await RunPowerShellAsync(
            fixture,
            buildState,
            fixtureRoot,
            output);
        Check(result.ExitCode == 0, $"ConSeC conditional publish contract failed: {result.Error}");
    }

    private static async Task SeedSourceBundleAsync(string source)
    {
        if (Directory.Exists(source))
        {
            Directory.Delete(source, recursive: true);
        }

        Directory.CreateDirectory(Path.Combine(source, "Cpu"));
        await File.WriteAllTextAsync(
            Path.Combine(source, "Cpu", "consec-wngt-fp32.onnx"),
            "new-graph");
        await File.WriteAllTextAsync(
            Path.Combine(source, "Cpu", "consec-wngt-fp32.onnx.data"),
            "new-data");
        await File.WriteAllTextAsync(Path.Combine(source, "tokenizer.json"), "new-tokenizer");
        await File.WriteAllTextAsync(Path.Combine(source, "model-manifest.json"), "new-manifest");
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

    private static void AssertNoTransactionDirectories(string root)
    {
        Check(!Directory.EnumerateDirectories(
                root,
                ".installed.asteria-consec-*",
                SearchOption.TopDirectoryOnly).Any(),
            "A ConSeC publish left transaction directories behind.");
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string script,
        params string[] arguments)
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
        startInfo.ArgumentList.Add(script);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
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
