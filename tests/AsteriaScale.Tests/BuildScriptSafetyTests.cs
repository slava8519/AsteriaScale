using System.Diagnostics;
using System.Text;

internal static class BuildScriptSafetyTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string? repositoryRoot = AppContext.BaseDirectory;
        while (repositoryRoot is not null &&
               !File.Exists(Path.Combine(repositoryRoot, "AsteriaScale.slnx")))
        {
            repositoryRoot = Path.GetDirectoryName(repositoryRoot);
        }

        Check(repositoryRoot is not null, "Repository root was not found for build rollback tests.");
        string library = Path.Combine(repositoryRoot!, "scripts", "build-state.ps1");
        string root = Path.Combine(tempRoot, "build-swap-rollback");
        Directory.CreateDirectory(root);
        static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

        string command = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{Quote(library)}}'
            $root = '{{Quote(root)}}'

            $success = Join-Path $root 'success'
            $output = Join-Path $success 'AsteriaScale'
            $previousOutput = Join-Path $success 'previous-output'
            $failedOutput = Join-Path $success 'failed-output'
            $identity = Join-Path $success 'AsteriaScale.Identity.msix'
            $previousIdentity = Join-Path $success 'previous-identity.msix'
            $failedIdentity = Join-Path $success 'failed-identity.msix'
            New-Item -ItemType Directory -Path $output, $previousOutput | Out-Null
            Set-Content -LiteralPath (Join-Path $output 'new.txt') -Value 'new-output'
            Set-Content -LiteralPath (Join-Path $previousOutput 'old.txt') -Value 'old-output'
            Set-Content -LiteralPath $identity -Value 'new-identity'
            Set-Content -LiteralPath $previousIdentity -Value 'old-identity'
            $failures = @(Invoke-AsteriaBuildSwapRollback `
                -Output $output -PreviousOutput $previousOutput -FailedOutput $failedOutput `
                -IdentityPackage $identity -PreviousIdentity $previousIdentity -FailedIdentity $failedIdentity `
                -OldOutputMoved -NewOutputInstalled -OldIdentityMoved -NewIdentityInstalled)
            if ($failures.Count -ne 0) { throw 'A clean rollback reported failures.' }
            if ((Get-Content -LiteralPath (Join-Path $output 'old.txt') -Raw).Trim() -ne 'old-output' -or
                (Get-Content -LiteralPath $identity -Raw).Trim() -ne 'old-identity') {
                throw 'A clean rollback did not restore both previous artifacts.'
            }

            $failure = Join-Path $root 'failure'
            $output = Join-Path $failure 'AsteriaScale'
            $previousOutput = Join-Path $failure 'previous-output'
            $failedOutput = Join-Path $failure 'failed-output'
            $identity = Join-Path $failure 'AsteriaScale.Identity.msix'
            $previousIdentity = Join-Path $failure 'previous-identity.msix'
            $failedIdentity = Join-Path $failure 'failed-identity.msix'
            New-Item -ItemType Directory -Path $output, $previousOutput | Out-Null
            Set-Content -LiteralPath (Join-Path $output 'new.txt') -Value 'new-output'
            Set-Content -LiteralPath (Join-Path $previousOutput 'old.txt') -Value 'old-output'
            Set-Content -LiteralPath $identity -Value 'new-identity'
            Set-Content -LiteralPath $previousIdentity -Value 'old-identity'
            Set-Content -LiteralPath $failedIdentity -Value 'collision'
            $failures = @(Invoke-AsteriaBuildSwapRollback `
                -Output $output -PreviousOutput $previousOutput -FailedOutput $failedOutput `
                -IdentityPackage $identity -PreviousIdentity $previousIdentity -FailedIdentity $failedIdentity `
                -OldOutputMoved -NewOutputInstalled -OldIdentityMoved -NewIdentityInstalled)
            if ($failures.Count -ne 2) { throw "Expected two identity rollback failures, got $($failures.Count)." }
            if ((Get-Content -LiteralPath (Join-Path $output 'old.txt') -Raw).Trim() -ne 'old-output') {
                throw 'A failed identity rollback prevented the independent publish restoration.'
            }

            $original = [InvalidOperationException]::new('original swap failure')
            $combined = New-AsteriaBuildSwapException -OriginalError $original -RollbackFailures $failures
            if ($combined -isnot [AggregateException] -or $combined.InnerExceptions.Count -ne 3 -or
                -not [object]::ReferenceEquals($combined.InnerExceptions[0], $original)) {
                throw 'The aggregate rollback error did not preserve the original failure first.'
            }
            $preserved = New-AsteriaBuildSwapException -OriginalError $original
            if (-not [object]::ReferenceEquals($preserved, $original)) {
                throw 'A clean rollback did not preserve the original failure object.'
            }

            # A publish counts as complete only when every artifact the source
            # tree publishes is present. Checking two executables let a dist
            # with a deleted dictionary or model pass as current, so the user
            # kept running an incomplete install until it failed at runtime.
            $assetsRoot = Join-Path $root 'dist-completeness'
            New-Item -ItemType Directory -Path `
                (Join-Path $assetsRoot 'assets\Dictionaries'), `
                (Join-Path $assetsRoot 'assets\TextHook'), `
                (Join-Path $assetsRoot 'assets\Morphology') | Out-Null
            Set-Content -LiteralPath (Join-Path $assetsRoot 'assets\Dictionaries\eng-rus.tsv.gz') -Value 'x'
            Set-Content -LiteralPath (Join-Path $assetsRoot 'assets\TextHook\hooks.tsv') -Value 'x'
            Set-Content -LiteralPath (Join-Path $assetsRoot 'assets\Morphology\forms.tsv.gz') -Value 'x'
            $distOutput = Join-Path $assetsRoot 'dist\AsteriaScale'
            $requiredDist = @(Get-AsteriaRequiredDistFiles -Root $assetsRoot -Output $distOutput)
            foreach ($expected in @(
                (Join-Path $distOutput 'AsteriaScale.exe'),
                (Join-Path $distOutput 'Dictionaries\eng-rus.tsv.gz'),
                (Join-Path $distOutput 'TextHook\hooks.tsv'),
                (Join-Path $distOutput 'Morphology\forms.tsv.gz'),
                (Join-Path $distOutput 'THIRD_PARTY_NOTICES.md'),
                (Join-Path $distOutput 'LICENSE'),
                (Join-Path $distOutput 'Ranker\Models\GlossEmbeddings\minilm-l6-v2.onnx'))) {
                if ($requiredDist -notcontains $expected) {
                    throw "The dist completeness check ignores a published artifact: $expected"
                }
            }

            # A truncated or half-written package must force a rebuild rather
            # than count as an installed identity.
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $brokenIdentity = Join-Path $assetsRoot 'broken.msix'
            Set-Content -LiteralPath $brokenIdentity -Value 'not a package'
            if (Test-AsteriaIdentityPackage -Path $brokenIdentity) {
                throw 'A corrupt identity package was accepted.'
            }
            if (Test-AsteriaIdentityPackage -Path (Join-Path $assetsRoot 'absent.msix')) {
                throw 'A missing identity package was accepted.'
            }
            $packageSource = Join-Path $assetsRoot 'package-source'
            New-Item -ItemType Directory -Path $packageSource | Out-Null
            Set-Content -LiteralPath (Join-Path $packageSource 'AppxManifest.xml') -Value '<Package />'
            $validIdentity = Join-Path $assetsRoot 'valid.msix'
            [IO.Compression.ZipFile]::CreateFromDirectory($packageSource, $validIdentity)
            if (-not (Test-AsteriaIdentityPackage -Path $validIdentity)) {
                throw 'A readable identity package was rejected.'
            }
            """;

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        ProcessStartInfo startInfo = new(powershell)
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
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell rollback test did not start.");
        string outputText = await process.StandardOutput.ReadToEndAsync();
        string errorText = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0,
            $"PowerShell rollback test failed: {errorText}\n{outputText}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
