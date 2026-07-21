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
