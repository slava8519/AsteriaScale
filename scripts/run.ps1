$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "dist\AsteriaScale\AsteriaScale.exe"

if (-not (Test-Path $exe)) {
    & (Join-Path $PSScriptRoot "build.ps1")
}

Start-Process -FilePath $exe
