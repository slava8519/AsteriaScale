[CmdletBinding()]
param(
    [switch]$DesktopShortcut,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "dist\AsteriaScale\AsteriaScale.exe"
$identityPackage = Join-Path $root "dist\AsteriaScale.Identity.msix"
. (Join-Path $PSScriptRoot "build-state.ps1")

$minimumMagpieVersion = [Version]"0.12.1.0"
$magpieCandidates = @(Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Directory -ErrorAction SilentlyContinue |
    Where-Object Name -Like "Blinue.Magpie*" |
    ForEach-Object { Join-Path $_.FullName "Magpie.exe" } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
$compatibleMagpie = $magpieCandidates |
    Where-Object {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($_)
        [Version]::new(
            [Math]::Max(0, $version.FileMajorPart),
            [Math]::Max(0, $version.FileMinorPart),
            [Math]::Max(0, $version.FileBuildPart),
            [Math]::Max(0, $version.FilePrivatePart)) -ge $minimumMagpieVersion
    } |
    Select-Object -First 1

if (-not $compatibleMagpie) {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw "winget was not found. Install Magpie 0.12.1+ manually from github.com/Blinue/Magpie."
    }

    $wingetAction = if ($magpieCandidates.Count -gt 0) { "upgrade" } else { "install" }
    & $winget.Source $wingetAction --id Blinue.Magpie --exact --source winget `
        --accept-source-agreements --accept-package-agreements --silent --disable-interactivity
    if ($LASTEXITCODE -ne 0) {
        throw "Magpie installation or upgrade failed."
    }

    $compatibleMagpie = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -Like "Blinue.Magpie*" |
        ForEach-Object { Join-Path $_.FullName "Magpie.exe" } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Where-Object {
            $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($_)
            [Version]::new(
                [Math]::Max(0, $version.FileMajorPart),
                [Math]::Max(0, $version.FileMinorPart),
                [Math]::Max(0, $version.FileBuildPart),
                [Math]::Max(0, $version.FilePrivatePart)) -ge $minimumMagpieVersion
        } |
        Select-Object -First 1
    if (-not $compatibleMagpie) {
        throw "Magpie $minimumMagpieVersion or newer was not found after winget completed."
    }
}

$externalLocation = Split-Path $exe -Parent
$distDirectory = Split-Path $externalLocation -Parent
$requiresBuild = -not (Test-AsteriaDistCurrent -Root $root -RequireIdentity)
$existingPackage = Get-AppxPackage -Name AsteriaScale -ErrorAction SilentlyContinue |
    Sort-Object Version -Descending |
    Select-Object -First 1
$existingRoot = if ($existingPackage) {
    Get-AsteriaRegisteredPackageRoot -Package $existingPackage
} else {
    $null
}

# Preserve the exact signed package behind the working registration before a
# rebuild can replace dist. It is used only for the documented same-version
# sparse-package conflict and makes the otherwise remove/re-add path reversible.
$rollbackDirectory = $null
$rollbackPackage = $null
if ($existingPackage -and $existingRoot -and
    (Test-AsteriaIdentityPackage -Path $identityPackage)) {
    $candidateInfo = Get-AsteriaIdentityPackageInfo -Path $identityPackage
    $installedManifest = Join-Path $existingPackage.InstallLocation "AppxManifest.xml"
    if ($candidateInfo -and
        (Test-Path -LiteralPath $installedManifest -PathType Leaf) -and
        $candidateInfo.Name -eq $existingPackage.Name -and
        $candidateInfo.Publisher -eq $existingPackage.Publisher -and
        $candidateInfo.Version -eq [Version]$existingPackage.Version -and
        [string]::Equals(
            $candidateInfo.ManifestSha256,
            (Get-FileHash -LiteralPath $installedManifest -Algorithm SHA256).Hash,
            [StringComparison]::OrdinalIgnoreCase)) {
        $rollbackDirectory = Join-Path `
            ([IO.Path]::GetTempPath()) `
            "asteria-identity-rollback-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $rollbackDirectory | Out-Null
        $rollbackPackage = Join-Path $rollbackDirectory "AsteriaScale.Identity.msix"
        Copy-Item -LiteralPath $identityPackage -Destination $rollbackPackage
    }
}

$publishRollbackRoot = $null
$previousOutput = $null
$previousIdentity = $null
$failedOutput = $null
$failedIdentity = $null
$oldOutputMoved = $false
$oldIdentityMoved = $false
$buildAttempted = $false
$publishRollbackSafeToClean = $true

function Remove-AsteriaInstallRollbackRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::Equals(
            $item.Parent.FullName.TrimEnd('\', '/'),
            $distDirectory.TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase) -or
        $item.Name -notlike ".asteria-install-rollback-*") {
        throw "Refusing to clean an unsafe install rollback directory: $Path"
    }

    Remove-Item -LiteralPath $item.FullName -Recurse -Force
}

try {
    if ($requiresBuild) {
        # Registration happens after publish. Retain the old external bytes
        # until both steps succeed; restoring only the sparse-package record
        # would otherwise bind the old manifest to newly published binaries.
        $publishRollbackRoot = Join-Path `
            $distDirectory `
            ".asteria-install-rollback-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $publishRollbackRoot | Out-Null
        $previousOutput = Join-Path $publishRollbackRoot "previous-AsteriaScale"
        $previousIdentity = Join-Path $publishRollbackRoot "previous-identity.msix"
        $failedOutput = Join-Path $publishRollbackRoot "failed-AsteriaScale"
        $failedIdentity = Join-Path $publishRollbackRoot "failed-identity.msix"

        if (Test-Path -LiteralPath $externalLocation) {
            $outputItem = Get-Item -LiteralPath $externalLocation -Force
            if (-not $outputItem.PSIsContainer -or
                ($outputItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to preserve an unsafe publish directory: $externalLocation"
            }
            Move-Item -LiteralPath $externalLocation -Destination $previousOutput
            $oldOutputMoved = $true
        }
        if (Test-Path -LiteralPath $identityPackage -PathType Leaf) {
            Move-Item -LiteralPath $identityPackage -Destination $previousIdentity
            $oldIdentityMoved = $true
        }

        $buildAttempted = $true
        & (Join-Path $PSScriptRoot "build.ps1")
    }

    $newInfo = Get-AsteriaIdentityPackageInfo -Path $identityPackage
    if (-not $newInfo) {
        throw "The identity package failed validation: $identityPackage"
    }

    $registeredPackage = Get-AppxPackage -Name AsteriaScale -ErrorAction SilentlyContinue |
        Sort-Object Version -Descending |
        Select-Object -First 1
    $registeredRoot = if ($registeredPackage) {
        Get-AsteriaRegisteredPackageRoot -Package $registeredPackage
    } else {
        $null
    }
    $identityAlreadyCurrent =
        $registeredPackage -and
        $registeredRoot -and
        (Test-AsteriaIdentityRegistrationCurrent `
            -Package $registeredPackage `
            -RegisteredRoot $registeredRoot `
            -ExpectedExternalLocation $externalLocation `
            -IdentityInfo $newInfo)

    if (-not $identityAlreadyCurrent) {
        [void](Invoke-AsteriaIdentityRegistrationUpdate `
            -PackagePath $identityPackage `
            -ExternalLocation $externalLocation `
            -RollbackPackagePath $rollbackPackage `
            -RollbackExternalLocation $existingRoot)
    }
} catch {
    $installFailure = $_
    if ($requiresBuild -and $publishRollbackRoot) {
        $rollbackFailures = @(Invoke-AsteriaBuildSwapRollback `
            -Output $externalLocation `
            -PreviousOutput $previousOutput `
            -FailedOutput $failedOutput `
            -IdentityPackage $identityPackage `
            -PreviousIdentity $previousIdentity `
            -FailedIdentity $failedIdentity `
            -OldOutputMoved:$oldOutputMoved `
            -NewOutputInstalled:($buildAttempted -and (Test-Path -LiteralPath $externalLocation)) `
            -OldIdentityMoved:$oldIdentityMoved `
            -NewIdentityInstalled:($buildAttempted -and (Test-Path -LiteralPath $identityPackage)))
        if ($rollbackFailures.Count -gt 0) {
            $publishRollbackSafeToClean = $false
            Write-Warning "Install rollback artifacts were retained at '$publishRollbackRoot'."
            throw (New-AsteriaBuildSwapException `
                -OriginalError $installFailure.Exception `
                -RollbackFailures $rollbackFailures)
        }
    }
    throw
} finally {
    if ($rollbackPackage -and (Test-Path -LiteralPath $rollbackPackage)) {
        try {
            Remove-Item -LiteralPath $rollbackPackage -Force
        } catch {
            Write-Warning "Identity rollback cleanup left '$rollbackPackage': $($_.Exception.Message)"
        }
    }
    if ($rollbackDirectory -and (Test-Path -LiteralPath $rollbackDirectory)) {
        try {
            Remove-Item -LiteralPath $rollbackDirectory -Recurse -Force
        } catch {
            Write-Warning "Identity rollback cleanup left '$rollbackDirectory': $($_.Exception.Message)"
        }
    }
    if ($publishRollbackRoot -and $publishRollbackSafeToClean -and
        (Test-Path -LiteralPath $publishRollbackRoot)) {
        try {
            Remove-AsteriaInstallRollbackRoot -Path $publishRollbackRoot
        } catch {
            Write-Warning "Install rollback cleanup left '$publishRollbackRoot': $($_.Exception.Message)"
        }
    }
}

$shell = New-Object -ComObject WScript.Shell
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Asteria Scale.lnk"
$shortcut = $shell.CreateShortcut($startMenu)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = Split-Path $exe -Parent
$shortcut.Description = "Real-time upscaling for visual novels"
$shortcut.Save()

if ($DesktopShortcut) {
    $desktop = [Environment]::GetFolderPath("Desktop")
    $desktopLink = Join-Path $desktop "Asteria Scale.lnk"
    $shortcut = $shell.CreateShortcut($desktopLink)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = Split-Path $exe -Parent
    $shortcut.Description = "Real-time upscaling for visual novels"
    $shortcut.Save()
}

Write-Host "Asteria Scale is installed. Shortcut: $startMenu"
if (-not $NoLaunch) {
    Start-Process -FilePath $exe
}
