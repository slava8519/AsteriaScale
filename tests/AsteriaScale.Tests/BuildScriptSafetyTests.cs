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

            # SANDWiCH is optional for a clean checkout, but it becomes one
            # indivisible publish unit as soon as all four source artifacts are
            # installed. A two-model partial download must not advertise a
            # usable profile or weaken the ordinary release contract.
            $sandwichSource = Join-Path $assetsRoot 'src\AsteriaScale.RankerWorker\Models\Sandwich'
            $sandwichCpu = Join-Path $sandwichSource 'Cpu'
            $sandwichOutput = Join-Path $distOutput 'Ranker\Models\Sandwich'
            if (Test-AsteriaSandwichBundleComplete -Root $assetsRoot) {
                throw 'An absent SANDWiCH bundle was reported as complete.'
            }
            if (@(Get-AsteriaRequiredDistFiles -Root $assetsRoot -Output $distOutput) |
                Where-Object { $_.StartsWith($sandwichOutput, [StringComparison]::OrdinalIgnoreCase) }) {
                throw 'An absent optional SANDWiCH bundle became release-required.'
            }

            New-Item -ItemType Directory -Path $sandwichCpu | Out-Null
            Set-Content -LiteralPath (Join-Path $sandwichCpu 'encoder-nv-fp32.onnx') -Value 'nv'
            Set-Content -LiteralPath (Join-Path $sandwichCpu 'encoder-v-fp32.onnx') -Value 'v'
            if (Test-AsteriaSandwichBundleComplete -Root $assetsRoot) {
                throw 'A two-ONNX partial SANDWiCH bundle was reported as complete.'
            }
            if (@(Get-AsteriaRequiredDistFiles -Root $assetsRoot -Output $distOutput) |
                Where-Object { $_.StartsWith($sandwichOutput, [StringComparison]::OrdinalIgnoreCase) }) {
                throw 'A partial SANDWiCH bundle pretended to be publishable.'
            }

            Set-Content -LiteralPath (Join-Path $sandwichSource 'tokenizer.json') -Value '{}'
            Set-Content -LiteralPath (Join-Path $sandwichSource 'model-manifest.json') -Value '{}'
            if (-not (Test-AsteriaSandwichBundleComplete -Root $assetsRoot)) {
                throw 'The complete four-file SANDWiCH bundle was not recognized.'
            }
            $sandwichRequired = @(Get-AsteriaRequiredDistFiles -Root $assetsRoot -Output $distOutput) |
                Where-Object { $_.StartsWith($sandwichOutput, [StringComparison]::OrdinalIgnoreCase) }
            foreach ($relativePath in @(
                'tokenizer.json',
                'model-manifest.json',
                'Cpu\encoder-nv-fp32.onnx',
                'Cpu\encoder-v-fp32.onnx')) {
                $expected = Join-Path $sandwichOutput $relativePath
                if ($sandwichRequired -notcontains $expected) {
                    throw "The enabled SANDWiCH profile does not require: $expected"
                }
            }

            $explicitRoot = Join-Path $root 'explicit-sandwich'
            $explicitOutput = Join-Path $explicitRoot 'dist\AsteriaScale'
            $explicitRequired = @(Get-AsteriaRequiredDistFiles `
                -Root $explicitRoot `
                -Output $explicitOutput `
                -IncludeExperimentalSandwichModel)
            $explicitSandwichOutput = Join-Path $explicitOutput 'Ranker\Models\Sandwich'
            foreach ($relativePath in @(
                'tokenizer.json',
                'model-manifest.json',
                'Cpu\encoder-nv-fp32.onnx',
                'Cpu\encoder-v-fp32.onnx')) {
                $expected = Join-Path $explicitSandwichOutput $relativePath
                if ($explicitRequired -notcontains $expected) {
                    throw "An explicit SANDWiCH publish does not require: $expected"
                }
            }

            # The closed output manifest covers files that cannot be derived
            # from the source tree, especially the worker's self-contained
            # runtime, and detects same-size content corruption as well.
            $integrityOutput = Join-Path $assetsRoot 'integrity-output'
            New-Item -ItemType Directory -Path `
                (Join-Path $integrityOutput 'Ranker'), `
                (Join-Path $integrityOutput 'Dictionaries') | Out-Null
            $workerDll = Join-Path $integrityOutput 'Ranker\AsteriaScale.RankerWorker.dll'
            $dictionaryAsset = Join-Path $integrityOutput 'Dictionaries\eng-rus.tsv.gz'
            Set-Content -LiteralPath (Join-Path $integrityOutput 'AsteriaScale.exe') -Value 'main'
            Set-Content -LiteralPath (Join-Path $integrityOutput 'Ranker\AsteriaScale.RankerWorker.exe') -Value 'worker'
            Set-Content -LiteralPath $workerDll -Value 'managed-worker'
            Set-Content -LiteralPath $dictionaryAsset -Value 'abcd' -NoNewline
            [void](New-AsteriaOutputManifest -Output $integrityOutput)
            if (-not (Test-AsteriaOutputManifest -Output $integrityOutput)) {
                throw 'A freshly generated output manifest was rejected.'
            }
            Remove-Item -LiteralPath $workerDll
            if (Test-AsteriaOutputManifest -Output $integrityOutput) {
                throw 'Deleting an unlisted worker dependency did not invalidate the output manifest.'
            }
            Set-Content -LiteralPath $workerDll -Value 'managed-worker'
            [void](New-AsteriaOutputManifest -Output $integrityOutput)
            Set-Content -LiteralPath $dictionaryAsset -Value 'wxyz' -NoNewline
            if (Test-AsteriaOutputManifest -Output $integrityOutput) {
                throw 'Same-size asset corruption did not invalidate the output manifest.'
            }

            # A package must be structurally valid and signed, not merely a ZIP
            # that happens to contain a file called AppxManifest.xml.
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
            $packageAssets = Join-Path $packageSource 'Assets'
            New-Item -ItemType Directory -Path $packageAssets | Out-Null
            @'
            <?xml version="1.0" encoding="utf-8"?>
            <Package IgnorableNamespaces="uap uap10"
              xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
              xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
              xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10">
              <Identity Name="AsteriaScale" Publisher="CN=Asteria Scale Local" Version="0.9.3.0" ProcessorArchitecture="neutral" />
              <Properties><DisplayName>Asteria Scale</DisplayName><PublisherDisplayName>Asteria Scale Local</PublisherDisplayName><Logo>Assets\storelogo.png</Logo><uap10:AllowExternalContent>true</uap10:AllowExternalContent></Properties>
              <Resources><Resource Language="ru-ru" /></Resources>
              <Applications><Application Id="AsteriaScale" Executable="AsteriaScale.exe" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="win32App"><uap:VisualElements DisplayName="Asteria Scale" Description="test" BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" /></Application></Applications>
            </Package>
            '@ | Set-Content -LiteralPath (Join-Path $packageSource 'AppxManifest.xml') -Encoding UTF8
            @'
            <?xml version="1.0" encoding="utf-8"?>
            <BlockMap xmlns="http://schemas.microsoft.com/appx/2010/blockmap">
              <File Name="AppxManifest.xml" /><File Name="Assets\storelogo.png" />
              <File Name="Assets\Square150x150Logo.png" /><File Name="Assets\Square44x44Logo.png" />
            </BlockMap>
            '@ | Set-Content -LiteralPath (Join-Path $packageSource 'AppxBlockMap.xml') -Encoding UTF8
            '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types" />' |
                Set-Content -LiteralPath (Join-Path $packageSource '[Content_Types].xml') -Encoding UTF8
            Copy-Item -LiteralPath '{{Quote(Path.Combine(repositoryRoot!, "packaging", "Identity", "Assets", "storelogo.png"))}}' -Destination $packageAssets
            Copy-Item -LiteralPath '{{Quote(Path.Combine(repositoryRoot!, "packaging", "Identity", "Assets", "Square150x150Logo.png"))}}' -Destination $packageAssets
            Copy-Item -LiteralPath '{{Quote(Path.Combine(repositoryRoot!, "packaging", "Identity", "Assets", "Square44x44Logo.png"))}}' -Destination $packageAssets
            $unsignedIdentity = Join-Path $assetsRoot 'unsigned-valid.msix'
            [IO.Compression.ZipFile]::CreateFromDirectory($packageSource, $unsignedIdentity)
            if (-not (Test-AsteriaIdentityPackage -Path $unsignedIdentity -AllowUnsigned)) {
                throw 'A structurally complete unsigned identity was rejected before signing.'
            }
            if (Test-AsteriaIdentityPackage -Path $unsignedIdentity) {
                throw 'An unsigned identity package was accepted as release-ready.'
            }
            Remove-Item -LiteralPath (Join-Path $packageAssets 'Square44x44Logo.png')
            $missingAssetIdentity = Join-Path $assetsRoot 'missing-asset.msix'
            [IO.Compression.ZipFile]::CreateFromDirectory($packageSource, $missingAssetIdentity)
            if (Test-AsteriaIdentityPackage -Path $missingAssetIdentity -AllowUnsigned) {
                throw 'An identity package missing a declared logo was accepted.'
            }

            # An unknown deployment error must preserve the old registration;
            # the exact-version replace path must restore it if the new add fails.
            if (-not (Test-AsteriaPackageRoot -Expected 'C:\Asteria' -Actual 'c:\asteria\')) {
                throw 'Equivalent package roots were not recognized.'
            }
            if (Test-AsteriaPackageRoot -Expected 'C:\Asteria' -Actual 'C:\Other') {
                throw 'Different package roots were treated as equivalent.'
            }
            $registrationLocation = Join-Path $assetsRoot 'registered-package'
            New-Item -ItemType Directory -Path $registrationLocation | Out-Null
            $registeredManifest = Join-Path $registrationLocation 'AppxManifest.xml'
            Set-Content -LiteralPath $registeredManifest -Value '<Package Version="current" />' -NoNewline
            $registeredPackage = [pscustomobject]@{
                Name = 'AsteriaScale'
                Publisher = 'CN=Asteria Scale Local'
                Version = [Version]'0.9.3.0'
                InstallLocation = $registrationLocation
            }
            $identityInfo = [pscustomobject]@{
                Name = 'AsteriaScale'
                Publisher = 'CN=Asteria Scale Local'
                Version = [Version]'0.9.3.0'
                ManifestSha256 = (Get-FileHash -LiteralPath $registeredManifest -Algorithm SHA256).Hash
            }
            if (-not (Test-AsteriaIdentityRegistrationCurrent `
                    -Package $registeredPackage `
                    -RegisteredRoot 'C:\Asteria' `
                    -ExpectedExternalLocation 'c:\asteria\' `
                    -IdentityInfo $identityInfo)) {
                throw 'A matching registered identity was rejected.'
            }
            $identityInfo.ManifestSha256 = '0' * 64
            if (Test-AsteriaIdentityRegistrationCurrent `
                    -Package $registeredPackage `
                    -RegisteredRoot 'C:\Asteria' `
                    -ExpectedExternalLocation 'C:\Asteria' `
                    -IdentityInfo $identityInfo) {
                throw 'A stale registered manifest passed the launch identity check.'
            }
            $newPackage = Join-Path $assetsRoot 'new.msix'
            $rollbackPackage = Join-Path $assetsRoot 'rollback.msix'
            Set-Content -LiteralPath $newPackage -Value 'new'
            Set-Content -LiteralPath $rollbackPackage -Value 'old'
            $script:fakePackage = [pscustomobject]@{ Root = 'C:\Old' }
            $script:removed = 0
            $get = { param($name) $script:fakePackage }
            $getRoot = { param($package) $package.Root }
            $remove = { param($package) $script:removed++; $script:fakePackage = $null }
            $unknownAdd = { param($package, $location) throw '0x80070005 access denied' }
            try {
                Invoke-AsteriaIdentityRegistrationUpdate `
                    -PackagePath $newPackage -ExternalLocation 'C:\New' `
                    -RollbackPackagePath $rollbackPackage -RollbackExternalLocation 'C:\Old' `
                    -AddOperation $unknownAdd -RemoveOperation $remove `
                    -GetOperation $get -GetRootOperation $getRoot
                throw 'An unknown registration failure unexpectedly succeeded.'
            } catch {
                if ($_.Exception.Message -notmatch 'left untouched') { throw }
            }
            if ($script:removed -ne 0 -or -not $script:fakePackage) {
                throw 'An unknown registration failure removed the working package.'
            }
            if (-not (Test-AsteriaSameVersionRegistrationError `
                    -ErrorRecord ([Runtime.InteropServices.Marshal]::GetExceptionForHR(-2147009285))) -or
                (Test-AsteriaSameVersionRegistrationError `
                    -ErrorRecord ([Runtime.InteropServices.Marshal]::GetExceptionForHR(-2147009287)))) {
                throw 'The exact-version HRESULT was confused with generic APPX install failure.'
            }

            # A prebuilt next-version package has no matching old MSIX beside it.
            # The transactional update must still be attempted; only the later
            # destructive same-version replacement requires rollback material.
            $script:fakePackage = [pscustomobject]@{
                Root = 'C:\Old'
                Version = [Version]'0.9.3.0'
            }
            $script:removed = 0
            $script:addAttempt = 0
            $versionUpgrade = {
                param($package, $location)
                $script:addAttempt++
                $script:fakePackage = [pscustomobject]@{
                    Root = $location
                    Version = [Version]'0.9.4.0'
                }
            }
            $upgradeResult = Invoke-AsteriaIdentityRegistrationUpdate `
                -PackagePath $newPackage -ExternalLocation 'C:\New' `
                -AddOperation $versionUpgrade -RemoveOperation $remove `
                -GetOperation $get -GetRootOperation $getRoot
            if ($upgradeResult -ne 'updated' -or $script:addAttempt -ne 1 -or
                $script:removed -ne 0 -or $script:fakePackage.Root -ne 'C:\New') {
                throw 'A normal version upgrade without rollback material was blocked or made destructive.'
            }

            # If Add succeeded but independent validation fails and there is no
            # rollback package, retain the observed state instead of deleting the
            # only registration. The explicit error tells the caller recovery is
            # required without falsely claiming the old package is untouched.
            $script:fakePackage = [pscustomobject]@{ Root = 'C:\Old' }
            $script:removed = 0
            $postAddWithoutRollback = {
                param($package, $location)
                $script:fakePackage = [pscustomobject]@{ Root = 'C:\Unexpected' }
            }
            try {
                Invoke-AsteriaIdentityRegistrationUpdate `
                    -PackagePath $newPackage -ExternalLocation 'C:\New' `
                    -AddOperation $postAddWithoutRollback -RemoveOperation $remove `
                    -GetOperation $get -GetRootOperation $getRoot
                throw 'An unvalidated update without rollback unexpectedly succeeded.'
            } catch {
                if ($_.Exception.Message -notmatch 'retained for recovery') { throw }
            }
            if ($script:removed -ne 0 -or $script:fakePackage.Root -ne 'C:\Unexpected') {
                throw 'An unvalidated update without rollback deleted the observed registration.'
            }

            $script:fakePackage = [pscustomobject]@{ Root = 'C:\Old' }
            $script:removed = 0
            $sameVersionWithoutRollback = {
                param($package, $location)
                throw [Runtime.InteropServices.Marshal]::GetExceptionForHR(-2147009285)
            }
            try {
                Invoke-AsteriaIdentityRegistrationUpdate `
                    -PackagePath $newPackage -ExternalLocation 'C:\New' `
                    -AddOperation $sameVersionWithoutRollback -RemoveOperation $remove `
                    -GetOperation $get -GetRootOperation $getRoot
                throw 'An exact-version replacement without rollback unexpectedly succeeded.'
            } catch {
                if ($_.Exception.Message -notmatch 'no verified rollback package' -or
                    $_.Exception.Message -notmatch 'left untouched') { throw }
            }
            if ($script:removed -ne 0 -or -not $script:fakePackage) {
                throw 'An exact-version conflict without rollback removed the working package.'
            }

            # Add-AppxPackage can return before our independent observation of
            # the registered root. If that observation fails, the add may have
            # changed state already and must be rolled back instead of being
            # reported as an untouched failure.
            $script:fakePackage = [pscustomobject]@{ Root = 'C:\Old' }
            $script:removed = 0
            $postAddWrongRoot = {
                param($package, $location)
                if ($package -eq $rollbackPackage) {
                    $script:fakePackage = [pscustomobject]@{ Root = $location }
                } else {
                    $script:fakePackage = [pscustomobject]@{ Root = 'C:\Unexpected' }
                }
            }
            try {
                Invoke-AsteriaIdentityRegistrationUpdate `
                    -PackagePath $newPackage -ExternalLocation 'C:\New' `
                    -RollbackPackagePath $rollbackPackage -RollbackExternalLocation 'C:\Old' `
                    -AddOperation $postAddWrongRoot -RemoveOperation $remove `
                    -GetOperation $get -GetRootOperation $getRoot
                throw 'An update with an unverified post-add root unexpectedly succeeded.'
            } catch {
                if ($_.Exception.Message -notmatch 'previous registration was restored') { throw }
            }
            if (-not $script:fakePackage -or $script:fakePackage.Root -ne 'C:\Old' -or
                $script:removed -ne 1) {
                throw 'A successful add followed by failed root validation was not rolled back.'
            }

            $script:addAttempt = 0
            $replaceThenRollback = {
                param($package, $location)
                if ($package -eq $newPackage) {
                    $script:addAttempt++
                    if ($script:addAttempt -eq 1) {
                        throw [Runtime.InteropServices.Marshal]::GetExceptionForHR(-2147009285)
                    }
                    throw 'replacement failed'
                }
                $script:fakePackage = [pscustomobject]@{ Root = $location }
            }
            try {
                Invoke-AsteriaIdentityRegistrationUpdate `
                    -PackagePath $newPackage -ExternalLocation 'C:\New' `
                    -RollbackPackagePath $rollbackPackage -RollbackExternalLocation 'C:\Old' `
                    -AddOperation $replaceThenRollback -RemoveOperation $remove `
                    -GetOperation $get -GetRootOperation $getRoot
                throw 'A failed replacement unexpectedly succeeded.'
            } catch {
                if ($_.Exception.Message -notmatch 'previous registration was restored') { throw }
            }
            if (-not $script:fakePackage -or $script:fakePackage.Root -ne 'C:\Old') {
                throw 'The same-version replacement did not restore the old registration.'
            }
            """;

        string scriptPath = Path.Combine(root, "build-safety.ps1");
        File.WriteAllText(scriptPath, command, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
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
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
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
