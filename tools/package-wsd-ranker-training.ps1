[CmdletBinding()]
param(
    [string]$PythonPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packageType = 'asteria-wsd-rtx5080-training'
$baseModelId = 'microsoft/deberta-v3-base'
$baseModelRevision = '8ccc9b6f36199bec6961081d44eb72fb3f7353f3'
$baseModelLicense = 'MIT'
$nliModelId = 'cross-encoder/nli-deberta-v3-base'
$nliModelRevision = '6c749ce3425cd33b46d187e45b92bbf96ee12ec7'
$nliModelLicense = 'Apache-2.0'
$trainingDataSource = 'English Wiktionary via Kaikki.org/wiktextract'
$trainingDataLicense = 'CC BY-SA and GFDL'
$trainingDataVersion = 'English Wiktionary dump 2026.07.06; Kaikki extraction 2026.07.16'
$cublasWorkspaceConfig = ':4096:8'
$pipWheelSha256 = '382ff9f685ee3bc25864f820aa50505825f10f5458ffff07e30a6d96e5715cab'
$torchWheelSha256 = '2efab1e83604ca628c6d85b9e188c153690980498d1297081a9dad704919303c'
$trainerSha256 = 'a2e1dec5f08f9d94bbdfea24d8837caf0d74930ff2737ff920e84b3d044ede83'
$canonicalDatasetLength = 32349487
$canonicalDatasetSha256 = '710c7787493da0e0776087dc08c3cdfd9275bc2ff6af64e3978bbe09085196c4'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))

function New-ExperimentProfile {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][bool]$Runnable,
        [Parameter(Mandatory)][string]$ModelId,
        [Parameter(Mandatory)][string]$Revision,
        [Parameter(Mandatory)][string]$License,
        [Parameter(Mandatory)][int]$EncoderLayers,
        [Parameter(Mandatory)][string]$HeadInitialization,
        [Parameter(Mandatory)][int]$Seed
    )

    return [ordered]@{
        id = $Id
        stage = $Stage
        runnable = $Runnable
        modelId = $ModelId
        revision = $Revision
        license = $License
        encoderLayers = $EncoderLayers
        headInitialization = $HeadInitialization
        seed = $Seed
        pythonHashSeed = [string]$Seed
    }
}

function Get-ExperimentProfiles {
    $profiles = @()
    foreach ($definition in @(
        @('base7-reset', $baseModelId, $baseModelRevision, $baseModelLicense, 7, 'reset'),
        @('nli7-reset', $nliModelId, $nliModelRevision, $nliModelLicense, 7, 'reset'),
        @('nli7-projected', $nliModelId, $nliModelRevision, $nliModelLicense, 7, 'nli-projected'),
        @('base9-reset', $baseModelId, $baseModelRevision, $baseModelLicense, 9, 'reset'),
        @('base12-reset', $baseModelId, $baseModelRevision, $baseModelLicense, 12, 'reset'),
        @('nli9-reset', $nliModelId, $nliModelRevision, $nliModelLicense, 9, 'reset'),
        @('nli9-projected', $nliModelId, $nliModelRevision, $nliModelLicense, 9, 'nli-projected'),
        @('nli12-reset', $nliModelId, $nliModelRevision, $nliModelLicense, 12, 'reset'),
        @('nli12-projected', $nliModelId, $nliModelRevision, $nliModelLicense, 12, 'nli-projected')
    )) {
        foreach ($seedDefinition in @(
            @('s1', 'screening', 20260727),
            @('s2', 'confirmation', 20260728),
            @('s3', 'confirmation', 20260729)
        )) {
            $profiles += New-ExperimentProfile `
                -Id "$($definition[0])-$($seedDefinition[0])" `
                -Stage $seedDefinition[1] -Runnable $true `
                -ModelId $definition[1] -Revision $definition[2] -License $definition[3] `
                -EncoderLayers $definition[4] -HeadInitialization $definition[5] `
                -Seed $seedDefinition[2]
        }
    }
    return @($profiles)
}

function Get-ExperimentSuites {
    return @(
        [ordered]@{
            id = 'reference'
            profileIds = @('base7-reset-s1')
        },
        [ordered]@{
            id = 'next'
            profileIds = @('nli7-reset-s1', 'nli7-projected-s1')
        },
        [ordered]@{
            id = 'capacity'
            profileIds = @('base9-reset-s1', 'base12-reset-s1')
        },
        [ordered]@{
            id = 'interaction'
            profileIds = @(
                'nli9-reset-s1',
                'nli9-projected-s1',
                'nli12-reset-s1',
                'nli12-projected-s1'
            )
        }
    )
}

function Get-SharedTrainingProfile {
    return [ordered]@{
        torch = '2.13.0+cu130'
        cuda = '13.0'
        minimumComputeCapability = @(12, 0)
        parameterPrecision = 'fp32'
        autocastPrecision = 'bf16'
        cublasWorkspaceConfig = $cublasWorkspaceConfig
        pythonImplementation = 'CPython'
        pythonVersion = '3.12.10'
        pythonPointerBits = 64
        pip = '26.1.2'
        pipWheelSha256 = $pipWheelSha256
        torchWheelSha256 = $torchWheelSha256
        frozenEncoderLayers = 0
        freezeEmbeddings = $false
        epochs = 6
        plannedOptimizerSteps = 3006
        scheduler = 'linear-to-zero'
        pairVariant = 'plain'
        batchSize = 8
        loss = 'hybrid'
        pairwiseCoefficient = 0.25
        positiveClassWeight = 'auto'
        groupWeighting = 'equal'
        groupsPerBatch = 8
        gradientAccumulationSteps = 8
        evalBatchSize = 8
        maxLength = 128
        padToMultipleOf = 8
        learningRate = 0.00002
        weightDecay = 0.01
        warmupRatio = 0.1
        maxGradNorm = 1.0
        device = 'cuda'
        threads = 8
        workers = 0
        logEverySteps = 25
        saveEverySteps = 250
        saveTotalLimit = 2
        selectionMetric = 'group_accuracy'
        runtimeBatchSize = 16
        parityTolerance = 0.0001
        deterministicAlgorithms = 'strict'
        datasetScope = 'full-canonical-train-valid'
    }
}

function Resolve-SafeLocalPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$MustExist,
        [switch]$Leaf,
        [switch]$Container
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Путь не может быть пустым.'
    }

    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($pathRoot) -or
        $pathRoot.StartsWith('\\', [StringComparison]::Ordinal)) {
        throw "Сетевой или неполный путь запрещён: $fullPath"
    }

    try {
        $drive = [IO.DriveInfo]::new($pathRoot)
    }
    catch {
        throw "Не удалось проверить локальный диск для пути '$fullPath': $($_.Exception.Message)"
    }
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) {
        throw "Требуется локальный фиксированный диск, получен '$($drive.DriveType)': $fullPath"
    }

    $current = $pathRoot
    $relative = $fullPath.Substring($pathRoot.Length)
    foreach ($part in $relative.Split(@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        if (-not (Test-Path -LiteralPath $current)) {
            continue
        }

        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse point запрещён в переносимом WSD-пути: $current"
        }
    }

    if ($MustExist -and -not (Test-Path -LiteralPath $fullPath)) {
        throw "Путь не найден: $fullPath"
    }
    if ($Leaf -and -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Ожидался файл: $fullPath"
    }
    if ($Container -and -not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        throw "Ожидался каталог: $fullPath"
    }

    return $fullPath
}

function Resolve-PythonExecutable {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidate = if ([IO.Path]::IsPathRooted($RequestedPath)) {
            $RequestedPath
        }
        else {
            Join-Path $root $RequestedPath
        }
        return Resolve-SafeLocalPath -Path $candidate -MustExist -Leaf
    }

    $workspaceVenv = Join-Path $root 'artifacts\wsd-venv\Scripts\python.exe'
    if (Test-Path -LiteralPath $workspaceVenv -PathType Leaf) {
        return Resolve-SafeLocalPath -Path $workspaceVenv -MustExist -Leaf
    }

    $python = Get-Command python.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $python) {
        throw 'Python не найден. Передайте -PythonPath или создайте artifacts\wsd-venv.'
    }
    return Resolve-SafeLocalPath -Path $python.Source -MustExist -Leaf
}

function Remove-OwnedTemporaryPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedParent,
        [Parameter(Mandatory)][string]$ExpectedPrefix
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::Equals(
            $item.Parent.FullName.TrimEnd('\', '/'),
            $ExpectedParent.TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $item.Name.StartsWith($ExpectedPrefix, [StringComparison]::Ordinal)) {
        throw "Отказ удалять неподтверждённый временный путь: $Path"
    }

    Remove-Item -LiteralPath $item.FullName -Recurse -Force
}

$root = Resolve-SafeLocalPath -Path $root -MustExist -Container
$trainerPath = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'tools\train-wsd-ranker.py') -MustExist -Leaf
$actualTrainerSha256 = (
    Get-FileHash -LiteralPath $trainerPath -Algorithm SHA256
).Hash.ToLowerInvariant()
if ($actualTrainerSha256 -ne $trainerSha256) {
    throw "Trainer SHA-256 не совпадает с проверенной FP32-версией: $actualTrainerSha256"
}
$requirementsPath = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'tools\requirements-wsd-ranker.txt') -MustExist -Leaf
$bootstrapRequirementsPath = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'tools\requirements-wsd-bootstrap.txt') -MustExist -Leaf
$torchRequirementsPath = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'tools\requirements-wsd-torch-cu130.txt') -MustExist -Leaf
$launcherPath = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'tools\run-wsd-ranker-rtx5080.ps1') -MustExist -Leaf
$readmePath = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'tools\README-wsd-rtx5080.md') -MustExist -Leaf
$roadmapPath = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'tools\WSD-EXPERIMENT-ROADMAP.md') -MustExist -Leaf
$dataset = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'artifacts\wsd-train-shipped.jsonl') `
    -MustExist -Leaf
$datasetItem = Get-Item -LiteralPath $dataset
$datasetSha256 = (Get-FileHash -LiteralPath $dataset -Algorithm SHA256).Hash.ToLowerInvariant()
if ($datasetItem.Length -ne $canonicalDatasetLength -or
    $datasetSha256 -ne $canonicalDatasetSha256) {
    throw 'Канонический обучающий набор не совпадает с закреплёнными размером и SHA-256.'
}
$python = Resolve-PythonExecutable -RequestedPath $PythonPath

Write-Host 'Проверка инвариантов тренера...'
& $python $trainerPath --self-check
if ($LASTEXITCODE -ne 0) {
    throw "Самопроверка тренера завершилась с кодом $LASTEXITCODE."
}

Write-Host 'Проверка структуры обучающего набора...'
& $python $trainerPath --dataset $dataset --inspect-only
if ($LASTEXITCODE -ne 0) {
    throw "Проверка обучающего набора завершилась с кодом $LASTEXITCODE."
}

$artifactsRoot = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'artifacts') -MustExist -Container
$utcStamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$outputCandidate = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $artifactsRoot "asteria-wsd-rtx5080-$utcStamp-$($datasetSha256.Substring(0, 12)).zip"
}
elseif ([IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
}
else {
    if ([IO.Path]::GetFileName($OutputPath) -ne $OutputPath) {
        throw '-OutputPath должен быть абсолютным путём внутри artifacts или только именем ZIP-файла.'
    }
    Join-Path $artifactsRoot $OutputPath
}
$output = Resolve-SafeLocalPath -Path $outputCandidate
if (-not $output.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Пакет должен иметь расширение .zip: $output"
}
if (Test-Path -LiteralPath $output) {
    throw "Файл назначения уже существует; перезапись запрещена: $output"
}
$outputParent = Resolve-SafeLocalPath `
    -Path (Split-Path $output -Parent) -MustExist -Container
if (-not [string]::Equals(
    $outputParent.TrimEnd('\', '/'),
    $artifactsRoot.TrimEnd('\', '/'),
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Обучающий ZIP разрешено создавать только непосредственно в artifacts: $artifactsRoot"
}

$packageId = [Guid]::NewGuid().ToString('N')
$temporaryPrefix = ".asteria-wsd-package-$packageId"
$stagingRoot = Join-Path $outputParent $temporaryPrefix
$temporaryZip = Join-Path $outputParent "$temporaryPrefix.zip"

try {
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stagingRoot 'tools') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stagingRoot 'artifacts') | Out-Null

    $payload = @(
        [ordered]@{
            RelativePath = 'README.md'
            Source = $readmePath
        },
        [ordered]@{
            RelativePath = 'EXPERIMENT-ROADMAP.md'
            Source = $roadmapPath
        },
        [ordered]@{
            RelativePath = 'tools/train-wsd-ranker.py'
            Source = $trainerPath
        },
        [ordered]@{
            RelativePath = 'tools/requirements-wsd-bootstrap.txt'
            Source = $bootstrapRequirementsPath
        },
        [ordered]@{
            RelativePath = 'tools/requirements-wsd-ranker.txt'
            Source = $requirementsPath
        },
        [ordered]@{
            RelativePath = 'tools/requirements-wsd-torch-cu130.txt'
            Source = $torchRequirementsPath
        },
        [ordered]@{
            RelativePath = 'tools/run-wsd-ranker-rtx5080.ps1'
            Source = $launcherPath
        },
        [ordered]@{
            RelativePath = 'artifacts/wsd-train-shipped.jsonl'
            Source = $dataset
        }
    )

    $fileRecords = @()
    foreach ($entry in $payload) {
        $relativeWindows = $entry.RelativePath.Replace('/', '\')
        $destination = Join-Path $stagingRoot $relativeWindows
        Copy-Item -LiteralPath $entry.Source -Destination $destination
        $copied = Resolve-SafeLocalPath -Path $destination -MustExist -Leaf
        $fileRecords += [ordered]@{
            path = $entry.RelativePath
            bytes = (Get-Item -LiteralPath $copied).Length
            sha256 = (Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    $manifest = [ordered]@{
        schemaVersion = 3
        packageType = $packageType
        createdUtc = [DateTime]::UtcNow.ToString('o')
        sharedTrainingProfile = Get-SharedTrainingProfile
        profiles = @(Get-ExperimentProfiles)
        suites = @(Get-ExperimentSuites)
        trainingData = [ordered]@{
            source = $trainingDataSource
            sourceUrl = 'https://kaikki.org/dictionary/English/'
            upstreamUrl = 'https://en.wiktionary.org/'
            extractorUrl = 'https://github.com/tatuylonen/wiktextract'
            license = $trainingDataLicense
            licenseUrl = 'https://en.wiktionary.org/wiki/Wiktionary:Copyrights'
            version = $trainingDataVersion
            datasetBytes = $canonicalDatasetLength
            datasetSha256 = $canonicalDatasetSha256
            trainGroups = 32019
            validGroups = 1089
            trainPairs = 120453
            validPairs = 4025
        }
        files = $fileRecords
        exclusions = @('model weights', 'training checkpoints', 'ranker holdouts')
    }
    $manifestPath = Join-Path $stagingRoot 'package-manifest.json'
    $manifestJson = $manifest | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText(
        $manifestPath,
        $manifestJson + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    $archiveInputs = @(Get-ChildItem -LiteralPath $stagingRoot -Force |
        ForEach-Object FullName)
    Compress-Archive -LiteralPath $archiveInputs `
        -DestinationPath $temporaryZip -CompressionLevel Optimal
    $temporaryZip = Resolve-SafeLocalPath -Path $temporaryZip -MustExist -Leaf
    if (Test-Path -LiteralPath $output) {
        throw "Файл назначения появился во время упаковки; перезапись запрещена: $output"
    }
    [IO.File]::Move($temporaryZip, $output)

    $zip = Get-Item -LiteralPath $output
    $zipSha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Готово: $output"
    Write-Host "Размер: $($zip.Length) байт"
    Write-Host "SHA-256: $zipSha256"
}
finally {
    Remove-OwnedTemporaryPath `
        -Path $stagingRoot -ExpectedParent $outputParent -ExpectedPrefix $temporaryPrefix
    Remove-OwnedTemporaryPath `
        -Path $temporaryZip -ExpectedParent $outputParent -ExpectedPrefix $temporaryPrefix
}
