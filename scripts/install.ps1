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

if (-not (Test-AsteriaDistCurrent -Root $root -RequireIdentity)) {
    & (Join-Path $PSScriptRoot "build.ps1")
}

Get-AppxPackage AsteriaScale | Remove-AppxPackage -ErrorAction SilentlyContinue
Add-AppxPackage -Path $identityPackage -ExternalLocation (Split-Path $exe -Parent)

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
