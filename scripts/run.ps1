$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "dist\AsteriaScale\AsteriaScale.exe"
. (Join-Path $PSScriptRoot "build-state.ps1")

if (-not (Test-AsteriaDistCurrent -Root $root)) {
    & (Join-Path $PSScriptRoot "build.ps1")
}

Start-Process -FilePath $exe
