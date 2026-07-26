$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "dist\AsteriaScale\AsteriaScale.exe"
. (Join-Path $PSScriptRoot "build-state.ps1")

if (-not (Test-AsteriaDistCurrent -Root $root -RequireIdentity)) {
    & (Join-Path $PSScriptRoot "build.ps1")
}

# Windows OCR and the on-device AI APIs are available only to a process that has
# package identity, and identity comes from the registered MSIX rather than from
# the published files. Starting the executable on a machine where install.ps1
# has never run produces an app that looks healthy and silently has no primary
# OCR, so this refuses instead of degrading quietly.
$identityRegistration = Get-AppxPackage -Name AsteriaScale -ErrorAction SilentlyContinue |
    Sort-Object Version -Descending |
    Select-Object -First 1
if (-not $identityRegistration) {
    throw "Asteria Scale has no registered identity package. Run scripts\install.ps1 once; without it Windows OCR is unavailable."
}
$registeredRoot = Get-AsteriaRegisteredPackageRoot -Package $identityRegistration
$expectedRoot = Split-Path $exe -Parent
$identityPackagePath = Join-Path $root "dist\AsteriaScale.Identity.msix"
$identityInfo = Get-AsteriaIdentityPackageInfo -Path $identityPackagePath
if (-not $registeredRoot -or -not $identityInfo -or
    -not (Test-AsteriaIdentityRegistrationCurrent `
        -Package $identityRegistration `
        -RegisteredRoot $registeredRoot `
        -ExpectedExternalLocation $expectedRoot `
        -IdentityInfo $identityInfo)) {
    $actual = if ($registeredRoot) { $registeredRoot } else { "<unknown>" }
    throw "Asteria Scale identity is stale or belongs to another publish ('$actual'). Run scripts\install.ps1 in this checkout to register the current signed manifest for '$expectedRoot'."
}

Start-Process -FilePath $exe
