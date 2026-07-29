[CmdletBinding()]
param(
    [string]$InstallRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path $PSScriptRoot '..\src\AsteriaScale.RankerWorker\Models\Consec'
}

$artifacts = @(
    [ordered]@{
        RelativePath = 'Cpu\consec-wngt-fp32.onnx'
        Size = 843194L
        Sha256 = '0eb0688f3fd2af67c6e2155daeed0fec5c5b2f1c7037b91ba526d50516c630ad'
    },
    [ordered]@{
        RelativePath = 'Cpu\consec-wngt-fp32.onnx.data'
        Size = 1621487616L
        Sha256 = '1257dc55ebec8886b7f5af4867f684ae44c7be4308e75b1251963595354011b9'
    },
    [ordered]@{
        RelativePath = 'tokenizer.json'
        Size = 3560546L
        Sha256 = 'df91c443c8eac73a42ff72086f25b16ad3b244ae2bf0303625298265d50f73f1'
    },
    [ordered]@{
        RelativePath = 'model-manifest.json'
        Size = 4407L
        Sha256 = '13a8a14e3938d4f20519f7da79a51f75543eced352bd39456b5a43c4d93402b8'
    }
)

$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Не найден бандл ConSeC: $root"
}

$rootItem = Get-Item -LiteralPath $root -Force
if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Небезопасный reparse-point в корне ConSeC: $root"
}

$cpuDirectory = Join-Path $root 'Cpu'
if (-not (Test-Path -LiteralPath $cpuDirectory -PathType Container)) {
    throw "Не найден каталог CPU ConSeC: $cpuDirectory"
}
$cpuItem = Get-Item -LiteralPath $cpuDirectory -Force
if (($cpuItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Небезопасный reparse-point каталога CPU ConSeC: $cpuDirectory"
}

foreach ($artifact in $artifacts) {
    $path = Join-Path $root $artifact.RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Не найден артефакт ConSeC: $path"
    }

    $file = Get-Item -LiteralPath $path -Force
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Небезопасный reparse-point артефакта ConSeC: $path"
    }

    if ($file.Length -ne $artifact.Size) {
        throw "Неверный размер ConSeC '$path': $($file.Length), ожидалось $($artifact.Size)."
    }

    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            $hash,
            $artifact.Sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Неверный SHA-256 ConSeC: $path"
    }
}

$manifest = Get-Content -LiteralPath (Join-Path $root 'model-manifest.json') -Raw |
    ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or
    -not [string]::Equals(
        [string]$manifest.adapterId,
        'consec-wngt-positional-extractor-v1',
        [StringComparison]::Ordinal) -or
    -not [string]::Equals(
        [string]$manifest.license,
        'CC-BY-NC-SA-4.0',
        [StringComparison]::Ordinal) -or
    $manifest.commercialUseAllowed -ne $false) {
    throw 'Манифест ConSeC не соответствует закреплённой исследовательской сборке.'
}

Write-Host "ConSeC проверен: $root" -ForegroundColor Green
