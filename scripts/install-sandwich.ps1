[CmdletBinding()]
param(
    [switch]$AcceptUpstreamTerms,
    [string]$InstallRoot = '',
    [string]$ArchivePath = (Join-Path $env:LOCALAPPDATA 'AsteriaScale\Downloads\sandwich-models.zip'),
    [string]$PythonExecutable = 'python'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'sandwich-install-state.ps1')

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path $PSScriptRoot '..\src\AsteriaScale.RankerWorker\Models\Sandwich'
}

$repositoryUrl = 'https://github.com/danielguzmanolivares/sandwich'
$paperUrl = 'https://aclanthology.org/2025.naacl-long.358/'
$archiveUrl = 'https://drive.usercontent.google.com/download?id=1D1XoR5aKRyOnD_TI8MROnpMoFLSYeDC3&export=download&confirm=t'
$archiveSize = 957844471L
$archiveSha256 = '98b3cda35a8599c961fc2ac36867d4687bd136e3f0ca1998d4a4754ea8c8e1de'

if (-not $AcceptUpstreamTerms) {
    Write-Host ''
    Write-Host 'SANDWiCH: код и опубликованные веса распространяются по MIT.' -ForegroundColor Yellow
    Write-Host "Репозиторий: $repositoryUrl"
    Write-Host "Статья: $paperUrl"
    Write-Host 'Asteria не устанавливает BabelNet definitions/neighbours из upstream data/.'
    Write-Host 'Будут загружены только два официальных энкодера и преобразованы в локальный ONNX-бандл.'
    Write-Host ''
    $answer = Read-Host 'Введите ПРИНИМАЮ, если вы принимаете условия опубликованных весов'
    if ($null -eq $answer -or
        -not [string]::Equals(
            $answer.Trim(),
            'ПРИНИМАЮ',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Условия SANDWiCH не приняты. Ничего не загружено.'
    }
}

function Test-PinnedFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$Size,
        [Parameter(Mandatory)][string]$Sha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    if ((Get-Item -LiteralPath $Path).Length -ne $Size) {
        return $false
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    return [string]::Equals($actual, $Sha256, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-SourceArtifact {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$Size,
        [Parameter(Mandatory)][string]$Sha256
    )

    if (-not (Test-PinnedFile -Path $Path -Size $Size -Sha256 $Sha256)) {
        throw "Официальный исходный артефакт SANDWiCH не прошёл проверку: $Path"
    }
}

$resolvedArchive = [IO.Path]::GetFullPath($ArchivePath)
$archiveDirectory = Split-Path -Parent $resolvedArchive
New-Item -ItemType Directory -Force -Path $archiveDirectory | Out-Null
if (-not (Test-PinnedFile -Path $resolvedArchive -Size $archiveSize -Sha256 $archiveSha256)) {
    $temporaryArchive = $resolvedArchive + '.download'
    Write-Host "Загрузка официального SANDWiCH ($([Math]::Round($archiveSize / 1GB, 2)) ГБ)..."
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($null -eq $curl) {
        throw 'Для возобновляемой загрузки SANDWiCH требуется curl.exe из Windows.'
    }

    & $curl.Source `
        --fail `
        --location `
        --retry 3 `
        --retry-delay 2 `
        --connect-timeout 30 `
        --max-time 21600 `
        --show-error `
        --progress-bar `
        --proto '=https' `
        --continue-at '-' `
        --output $temporaryArchive `
        $archiveUrl
    if ($LASTEXITCODE -ne 0) {
        throw "curl не смог загрузить SANDWiCH, код $LASTEXITCODE. Повторный запуск продолжит загрузку."
    }

    if (-not (Test-PinnedFile -Path $temporaryArchive -Size $archiveSize -Sha256 $archiveSha256)) {
        throw 'Архив SANDWiCH не прошёл проверку размера/SHA-256.'
    }

    Move-Item -LiteralPath $temporaryArchive -Destination $resolvedArchive -Force
}

$staging = Join-Path $env:TEMP ('asteria-sandwich-install-' + [Guid]::NewGuid().ToString('N'))
$models = Join-Path $staging 'models'
$bundle = Join-Path $staging 'bundle'
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    Write-Host 'Распаковка официальных весов...'
    Expand-Archive -LiteralPath $resolvedArchive -DestinationPath $staging -Force
    Assert-SourceArtifact `
        -Path (Join-Path $models 'encoder-nv\model.safetensors') `
        -Size 567598552L `
        -Sha256 'd759c83cf13c8d367eccbddb33d061c1db6e5d5082e39b86679dcd4dd21ef602'
    Assert-SourceArtifact `
        -Path (Join-Path $models 'encoder-v\model.safetensors') `
        -Size 567598552L `
        -Sha256 'a63626ab5ef52cad6360624bfdaab91743716270e377f2bdf92feabc512d273d'

    $toolingRoot = Join-Path $env:LOCALAPPDATA 'AsteriaScale\Tooling\SandwichOnnx-v1'
    $toolPython = Join-Path $toolingRoot 'Scripts\python.exe'
    $stamp = Join-Path $toolingRoot 'asteria-requirements-v1.txt'
    if (-not (Test-Path -LiteralPath $toolPython -PathType Leaf)) {
        & $PythonExecutable -m venv $toolingRoot
        if ($LASTEXITCODE -ne 0) {
            throw 'Не удалось создать Python-окружение конвертера SANDWiCH.'
        }
    }

    if (-not (Test-Path -LiteralPath $stamp -PathType Leaf)) {
        Write-Host 'Однократная установка закреплённого CPU-конвертера...'
        & $toolPython -m pip install --disable-pip-version-check `
            --index-url https://download.pytorch.org/whl/cpu torch==2.5.1
        if ($LASTEXITCODE -ne 0) {
            throw 'Не удалось установить закреплённый CPU PyTorch.'
        }

        & $toolPython -m pip install --disable-pip-version-check `
            -r (Join-Path $PSScriptRoot '..\tools\requirements-sandwich-export.txt')
        if ($LASTEXITCODE -ne 0) {
            throw 'Не удалось установить зависимости конвертера SANDWiCH.'
        }

        Set-Content -LiteralPath $stamp -Encoding UTF8 -Value @(
            'torch==2.5.1+cpu'
            'transformers==4.46.3'
            'onnx==1.17.0'
            'onnxruntime==1.20.1'
        )
    }

    Write-Host 'Экспорт двух энкодеров в ONNX и проверка PyTorch↔ORT parity...'
    & $toolPython (Join-Path $PSScriptRoot '..\tools\export-sandwich-onnx.py') `
        --models-directory $models `
        --output $bundle
    if ($LASTEXITCODE -ne 0) {
        throw 'Конвертер SANDWiCH завершился с ошибкой.'
    }

    & (Join-Path $PSScriptRoot 'verify-sandwich.ps1') -InstallRoot $bundle

    $acceptance = [ordered]@{
        schemaVersion = 1
        acceptedUtc = [DateTime]::UtcNow.ToString('O')
        repository = $repositoryUrl
        upstreamCommit = 'daca3cad63326204d4027b9ea09fb1405ab1ca0a'
        license = 'MIT'
        archiveSha256 = $archiveSha256
        babelNetDataInstalled = $false
    }
    $verifyScriptPath = Join-Path $PSScriptRoot 'verify-sandwich.ps1'
    $verifyBundle = {
        param($path)
        & $verifyScriptPath -InstallRoot $path
    }.GetNewClosure()
    Publish-SandwichBundleTransactional `
        -SourceBundle $bundle `
        -InstallRoot $InstallRoot `
        -AcceptanceJson ($acceptance | ConvertTo-Json) `
        -VerifyBundle $verifyBundle
    $resolvedInstall = [IO.Path]::GetFullPath($InstallRoot)
    Write-Host 'SANDWiCH установлен. Пересоберите Asteria Scale и выберите модель SANDWiCH.' -ForegroundColor Green
}
finally {
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    $resolvedTemp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if ($resolvedStaging.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
