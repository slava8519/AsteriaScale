[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
. (Join-Path $PSScriptRoot "build-state.ps1")

$mainProject = Join-Path $root "src\AsteriaScale\AsteriaScale.csproj"
$rankerProject = Join-Path $root "src\AsteriaScale.RankerWorker\AsteriaScale.RankerWorker.csproj"
$distDirectory = [IO.Path]::GetFullPath((Join-Path $root "dist")).TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath((Join-Path $distDirectory "AsteriaScale")).TrimEnd('\', '/')
$identitySource = Join-Path $root "packaging\Identity"
$identityPackage = Join-Path $distDirectory "AsteriaScale.Identity.msix"
$buildId = [Guid]::NewGuid().ToString("N")
$stagingRoot = Join-Path $distDirectory ".asteria-staging-$buildId"
$stagingOutput = Join-Path $stagingRoot "AsteriaScale"
$stagingIdentity = Join-Path $stagingRoot "AsteriaScale.Identity.msix"
$previousOutput = Join-Path $distDirectory ".asteria-previous-$buildId"
$previousIdentity = Join-Path $distDirectory ".asteria-identity-previous-$buildId.msix"

$expectedOutput = [IO.Path]::GetFullPath((Join-Path $root "dist\AsteriaScale")).TrimEnd('\', '/')
if (-not [string]::Equals($output, $expectedOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace an unexpected output path: $output"
}

$workspacePrefix = [IO.Path]::GetFullPath($root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace an output path outside the workspace: $output"
}

if (Test-Path -LiteralPath $distDirectory) {
    $distItem = Get-Item -LiteralPath $distDirectory -Force
    if (-not $distItem.PSIsContainer -or
        ($distItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to publish through an unsafe dist directory: $distDirectory"
    }
} else {
    New-Item -ItemType Directory -Path $distDirectory | Out-Null
}

if (Test-Path -LiteralPath $output) {
    $outputItem = Get-Item -LiteralPath $output -Force
    if (-not $outputItem.PSIsContainer -or
        ($outputItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::Equals(
            $outputItem.FullName.TrimEnd('\', '/'),
            $expectedOutput,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace an unsafe output directory: $output"
    }
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = $dotnetCommand.Source
if (-not $dotnetPath -and (Test-Path "C:\Program Files\dotnet\dotnet.exe")) {
    $dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
}
if (-not $dotnetPath) {
    throw ".NET SDK was not found. Install Microsoft.DotNet.SDK.10 with winget."
}

# NUGET_PACKAGES relocates the global package folder; assuming %USERPROFILE%
# makes the build fail on any machine that sets it.
$packagesRoot = if ($env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES
} else {
    Join-Path $env:USERPROFILE ".nuget\packages"
}
$sdkTools = Join-Path $packagesRoot "microsoft.windows.sdk.buildtools\10.0.26100.8249\bin\10.0.26100.0\x64"
$makeAppx = Join-Path $sdkTools "makeappx.exe"
$signTool = Join-Path $sdkTools "signtool.exe"
if (-not (Test-Path -LiteralPath $makeAppx -PathType Leaf) -or
    -not (Test-Path -LiteralPath $signTool -PathType Leaf)) {
    throw "Windows SDK packaging tools were not restored in '$packagesRoot'. Run dotnet restore first."
}

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object Subject -EQ "CN=Asteria Scale Local" |
    Where-Object HasPrivateKey |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $certificate) {
    throw "Signing certificate 'CN=Asteria Scale Local' with a private key was not found."
}

function Remove-SafeBuildDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::Equals($item.Parent.FullName, $distDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unsafe build directory: $Path"
    }

    Remove-Item -LiteralPath $item.FullName -Recurse -Force
}

$swapSucceeded = $false
$oldOutputMoved = $false
$newOutputInstalled = $false
$oldIdentityMoved = $false
$newIdentityInstalled = $false
try {
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null

    # Captured before anything is published. Taken afterwards, the fingerprint
    # would stamp a binary compiled from the older sources with the identity of
    # whatever the tree happens to contain once the publish finishes.
    $sourceRevision = Get-AsteriaSourceRevision -Root $root
    if (-not $sourceRevision) {
        $sourceRevision = "unknown"
    }
    $sourceFingerprint = Get-AsteriaSourceFingerprint -Root $root

    & $dotnetPath publish $mainProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:EnableMsixTooling=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $stagingOutput

    if ($LASTEXITCODE -ne 0) {
        throw "Main application build failed with exit code $LASTEXITCODE."
    }

    $rankerOutput = Join-Path $stagingOutput "Ranker"
    & $dotnetPath publish $rankerProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $rankerOutput

    if ($LASTEXITCODE -ne 0) {
        throw "Ranker worker build failed with exit code $LASTEXITCODE."
    }

    # One list, shared with the dist freshness check, so a publish that passes
    # here cannot be reported as incomplete later or vice versa.
    foreach ($requiredFile in Get-AsteriaRequiredDistFiles -Root $root -Output $stagingOutput) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Publish is incomplete. Missing: $requiredFile"
        }
    }

    # /nv is not optional here: this is an AllowExternalContent identity package,
    # so the executable it declares deliberately lives outside the package and
    # full manifest validation rejects that by design. Everything /nv stops
    # checking is therefore ours to check — a manifest naming a logo that does
    # not exist used to pack, sign and install without a word.
    $identityManifest = Join-Path $identitySource "AppxManifest.xml"
    $declaredAssets = [regex]::Matches(
        (Get-Content -LiteralPath $identityManifest -Raw),
        '"(Assets\\[^"]+)"') |
        ForEach-Object { $_.Groups[1].Value } |
        Sort-Object -Unique
    foreach ($declaredAsset in $declaredAssets) {
        if (-not (Test-Path -LiteralPath (Join-Path $identitySource $declaredAsset) -PathType Leaf)) {
            throw "Identity manifest references a missing asset: $declaredAsset"
        }
    }

    & $makeAppx pack /d $identitySource /p $stagingIdentity /o /nv
    if ($LASTEXITCODE -ne 0) {
        throw "Identity package creation failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-AsteriaIdentityPackage -Path $stagingIdentity -AllowUnsigned)) {
        throw "Identity package is not a readable MSIX: $stagingIdentity"
    }

    & $signTool sign /sha1 $certificate.Thumbprint /fd SHA256 $stagingIdentity
    if ($LASTEXITCODE -ne 0) {
        throw "Identity package signing failed with exit code $LASTEXITCODE."
    }

    & $signTool verify /pa /all /v $stagingIdentity
    if ($LASTEXITCODE -ne 0) {
        throw "Identity package signature verification failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-AsteriaIdentityPackage -Path $stagingIdentity)) {
        throw "The signed identity package failed structural or signature validation: $stagingIdentity"
    }

    # Close over every published byte, including the worker's self-contained
    # runtime. Freshness checks later verify this manifest rather than assuming
    # that the presence of one launcher EXE proves the whole publish survived.
    [void](New-AsteriaOutputManifest -Output $stagingOutput)

    if ((Get-AsteriaSourceFingerprint -Root $root) -ne $sourceFingerprint) {
        throw "Build inputs changed while publishing. Re-run the build so dist matches the sources it was built from."
    }

    Set-Content -LiteralPath (Join-Path $stagingOutput ".build-revision") `
        -Value $sourceRevision -Encoding Ascii -NoNewline
    Set-Content -LiteralPath (Join-Path $stagingOutput ".build-source-fingerprint") `
        -Value $sourceFingerprint -Encoding Ascii -NoNewline

    try {
        if (Test-Path -LiteralPath $output) {
            Move-Item -LiteralPath $output -Destination $previousOutput
            $oldOutputMoved = $true
        }

        Move-Item -LiteralPath $stagingOutput -Destination $output
        $newOutputInstalled = $true

        if (Test-Path -LiteralPath $identityPackage) {
            Move-Item -LiteralPath $identityPackage -Destination $previousIdentity
            $oldIdentityMoved = $true
        }

        Move-Item -LiteralPath $stagingIdentity -Destination $identityPackage
        $newIdentityInstalled = $true
        $swapSucceeded = $true
    } catch {
        $swapError = $_
        $rollbackFailures = @(Invoke-AsteriaBuildSwapRollback `
            -Output $output `
            -PreviousOutput $previousOutput `
            -FailedOutput (Join-Path $stagingRoot "failed-AsteriaScale") `
            -IdentityPackage $identityPackage `
            -PreviousIdentity $previousIdentity `
            -FailedIdentity $stagingIdentity `
            -OldOutputMoved:$oldOutputMoved `
            -NewOutputInstalled:$newOutputInstalled `
            -OldIdentityMoved:$oldIdentityMoved `
            -NewIdentityInstalled:$newIdentityInstalled)
        if ($rollbackFailures.Count -eq 0) {
            throw
        }

        throw (New-AsteriaBuildSwapException `
            -OriginalError $swapError.Exception `
            -RollbackFailures $rollbackFailures)
    }
} finally {
    if ($swapSucceeded) {
        if (Test-Path -LiteralPath $previousIdentity) {
            try {
                Remove-Item -LiteralPath $previousIdentity -Force
            } catch {
                Write-Warning "The previous identity package remains at '$previousIdentity': $($_.Exception.Message)"
            }
        }
        if (Test-Path -LiteralPath $previousOutput) {
            try {
                Remove-SafeBuildDirectory -Path $previousOutput
            } catch {
                Write-Warning "The previous publish remains at '$previousOutput': $($_.Exception.Message)"
            }
        }
    }

    if (Test-Path -LiteralPath $stagingRoot) {
        try {
            Remove-SafeBuildDirectory -Path $stagingRoot
        } catch {
            Write-Warning "The staging directory remains at '$stagingRoot': $($_.Exception.Message)"
        }
    }
}

Write-Host "Ready: $(Join-Path $output 'AsteriaScale.exe')"
Write-Host "Ranker: $(Join-Path $output 'Ranker\AsteriaScale.RankerWorker.exe')"
Write-Host "Identity: $identityPackage"
