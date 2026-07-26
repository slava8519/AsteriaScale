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
if (-not (Get-AppxPackage -Name AsteriaScale -ErrorAction SilentlyContinue)) {
    throw "Asteria Scale has no registered identity package. Run scripts\install.ps1 once; without it Windows OCR is unavailable."
}

Start-Process -FilePath $exe
