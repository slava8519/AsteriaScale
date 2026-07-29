[CmdletBinding()]
param(
    [string]$InstallRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path $PSScriptRoot '..\src\AsteriaScale.RankerWorker\Models\Sandwich'
}

$artifacts = @(
    [ordered]@{
        RelativePath = 'Cpu\encoder-nv-fp32.onnx'
        Size = 568094742L
        Sha256 = 'b6a21c42c974f145a39a20e5c5b62b740cb0b63ed698499fa4c5e4b397998739'
    },
    [ordered]@{
        RelativePath = 'Cpu\encoder-v-fp32.onnx'
        Size = 568094742L
        Sha256 = '21b778dc9552f84732d1061ca1ace23f9f6c071e68fe4ab0615109f192e78f6b'
    },
    [ordered]@{
        RelativePath = 'tokenizer.json'
        Size = 8656779L
        Sha256 = '2689db9b56b94ae9fac86daeb5fe7df0be0c5f1a9dce76a0edda7afa93dd1304'
    },
    [ordered]@{
        RelativePath = 'model-manifest.json'
        Size = 1108L
        Sha256 = 'b4d20c4f82cc876805f5d4a1c848da88ae3e1c9d635a349ce1d34e7266dca0a3'
    }
)

$root = [IO.Path]::GetFullPath($InstallRoot)
foreach ($artifact in $artifacts) {
    $path = Join-Path $root $artifact.RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Не найден артефакт SANDWiCH: $path"
    }

    $file = Get-Item -LiteralPath $path
    if ($file.Length -ne $artifact.Size) {
        throw "Неверный размер SANDWiCH '$path': $($file.Length), ожидалось $($artifact.Size)."
    }

    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            $hash,
            $artifact.Sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Неверный SHA-256 SANDWiCH: $path"
    }
}

Write-Host "SANDWiCH проверен: $root" -ForegroundColor Green
