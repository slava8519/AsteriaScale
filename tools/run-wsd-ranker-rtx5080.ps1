[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$PythonPath,
    [string]$Profile,
    [switch]$Resume,
    [switch]$VerifyOnly,
    [switch]$ListProfiles,
    [switch]$RunReferenceSuite,
    [switch]$RunNextSuite,
    [switch]$RunCapacitySuite,
    [switch]$RunInteractionSuite
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
$pythonVersion = '3.12.10'
$pipVersion = '26.1.2'
$pipWheelSha256 = '382ff9f685ee3bc25864f820aa50505825f10f5458ffff07e30a6d96e5715cab'
$torchWheelSha256 = '2efab1e83604ca628c6d85b9e188c153690980498d1297081a9dad704919303c'
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

function Get-ExpectedProfiles {
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

function Get-ExpectedSuites {
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
        pythonVersion = $pythonVersion
        pythonPointerBits = 64
        pip = $pipVersion
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

function Resolve-PackageRelativeFile {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$PackageRoot
    )

    if ([IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.IndexOf(':') -ge 0) {
        throw "Manifest содержит абсолютный путь: $RelativePath"
    }
    $candidate = [IO.Path]::GetFullPath(
        (Join-Path $PackageRoot $RelativePath.Replace('/', '\')))
    $prefix = $PackageRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest выводит путь за пределы пакета: $RelativePath"
    }
    return Resolve-SafeLocalPath -Path $candidate -MustExist -Leaf
}

function Assert-PackageIntegrity {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$PackageRoot
    )

    $manifestFile = Resolve-SafeLocalPath -Path $ManifestPath -MustExist -Leaf
    $manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if ($manifest.schemaVersion -ne 3 -or $manifest.packageType -ne $packageType) {
        throw "Неподдерживаемый package-manifest: $manifestFile"
    }

    $expectedSharedJson = Get-SharedTrainingProfile |
        ConvertTo-Json -Depth 8 -Compress
    $actualSharedJson = $manifest.sharedTrainingProfile |
        ConvertTo-Json -Depth 8 -Compress
    if (-not [string]::Equals(
        $actualSharedJson, $expectedSharedJson, [StringComparison]::Ordinal)) {
        throw 'Общий профиль обучения в package-manifest не совпадает с launcher.'
    }
    $expectedProfilesJson = @(Get-ExpectedProfiles) |
        ConvertTo-Json -Depth 8 -Compress
    $actualProfilesJson = @($manifest.profiles) |
        ConvertTo-Json -Depth 8 -Compress
    if (-not [string]::Equals(
        $actualProfilesJson, $expectedProfilesJson, [StringComparison]::Ordinal)) {
        throw 'Список профилей в package-manifest не совпадает с launcher.'
    }
    $expectedSuitesJson = @(Get-ExpectedSuites) |
        ConvertTo-Json -Depth 8 -Compress
    $actualSuitesJson = @($manifest.suites) |
        ConvertTo-Json -Depth 8 -Compress
    if (-not [string]::Equals(
        $actualSuitesJson, $expectedSuitesJson, [StringComparison]::Ordinal)) {
        throw 'Список suites в package-manifest не совпадает с launcher.'
    }
    if ($manifest.trainingData.source -ne $trainingDataSource -or
        $manifest.trainingData.license -ne $trainingDataLicense -or
        $manifest.trainingData.version -ne $trainingDataVersion -or
        $manifest.trainingData.datasetBytes -ne $canonicalDatasetLength -or
        $manifest.trainingData.datasetSha256 -ne $canonicalDatasetSha256 -or
        $manifest.trainingData.trainGroups -ne 32019 -or
        $manifest.trainingData.validGroups -ne 1089 -or
        $manifest.trainingData.trainPairs -ne 120453 -or
        $manifest.trainingData.validPairs -ne 4025) {
        throw 'Происхождение обучающего набора в package-manifest не совпадает с launcher.'
    }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($record in @($manifest.files)) {
        $relative = [string]$record.path
        if ([string]::IsNullOrWhiteSpace($relative) -or -not $seen.Add($relative)) {
            throw "Некорректная или повторная запись package-manifest: $relative"
        }
        $file = Resolve-PackageRelativeFile -RelativePath $relative -PackageRoot $PackageRoot
        $item = Get-Item -LiteralPath $file
        if ($item.Length -ne [long]$record.bytes) {
            throw "Размер файла не совпадает с manifest: $relative"
        }
        $actualSha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not [string]::Equals(
            $actualSha256,
            [string]$record.sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "SHA-256 файла не совпадает с manifest: $relative"
        }
    }

    $requiredFiles = @(
        'README.md',
        'EXPERIMENT-ROADMAP.md',
        'tools/train-wsd-ranker.py',
        'tools/requirements-wsd-bootstrap.txt',
        'tools/requirements-wsd-ranker.txt',
        'tools/requirements-wsd-torch-cu130.txt',
        'tools/run-wsd-ranker-rtx5080.ps1',
        'artifacts/wsd-train-shipped.jsonl'
    )
    if ($seen.Count -ne $requiredFiles.Count) {
        throw 'package-manifest должен перечислять только точный обязательный payload.'
    }
    foreach ($required in $requiredFiles) {
        if (-not $seen.Contains($required)) {
            throw "В package-manifest отсутствует обязательный файл: $required"
        }
    }

    return $manifest
}

$root = Resolve-SafeLocalPath -Path $root -MustExist -Container
$manifestPath = Join-Path $root 'package-manifest.json'
$manifest = Assert-PackageIntegrity -ManifestPath $manifestPath -PackageRoot $root
if ($VerifyOnly) {
    if ($ListProfiles -or $RunReferenceSuite -or $RunNextSuite -or
        $RunCapacitySuite -or $RunInteractionSuite -or
        -not [string]::IsNullOrWhiteSpace($Profile) -or $Resume -or
        -not [string]::IsNullOrWhiteSpace($OutputDirectory) -or
        -not [string]::IsNullOrWhiteSpace($PythonPath)) {
        throw '-VerifyOnly нельзя объединять с запуском, resume или выводом профилей.'
    }
    Write-Host 'Пакет WSD проверен: profiles, suites, manifest, размеры и SHA-256 совпадают.'
    return
}

$profiles = @(Get-ExpectedProfiles)
$suites = @(Get-ExpectedSuites)
if ($ListProfiles) {
    if ($RunReferenceSuite -or $RunNextSuite -or $RunCapacitySuite -or
        $RunInteractionSuite -or
        -not [string]::IsNullOrWhiteSpace($Profile) -or $Resume -or
        -not [string]::IsNullOrWhiteSpace($OutputDirectory) -or
        -not [string]::IsNullOrWhiteSpace($PythonPath)) {
        throw '-ListProfiles нельзя объединять с запуском, resume или output.'
    }
    $profiles | ForEach-Object {
        [PSCustomObject]@{
            Id = $_.id
            Stage = $_.stage
            Runnable = $_.runnable
            Model = $_.modelId
            Layers = $_.encoderLayers
            Head = $_.headInitialization
            Seed = $_.seed
        }
    } | Format-Table -AutoSize
    Write-Host 'Suites:'
    foreach ($suite in $suites) {
        Write-Host "  $($suite.id): $([string]::Join(', ', @($suite.profileIds)))"
    }
    return
}

$selectionCount = 0
if (-not [string]::IsNullOrWhiteSpace($Profile)) { $selectionCount++ }
if ($RunReferenceSuite) { $selectionCount++ }
if ($RunNextSuite) { $selectionCount++ }
if ($RunCapacitySuite) { $selectionCount++ }
if ($RunInteractionSuite) { $selectionCount++ }
if ($selectionCount -eq 0) {
    throw 'Выберите -Profile <id> или один из suite-переключателей. Для списка: -ListProfiles.'
}
if ($selectionCount -ne 1) {
    throw 'Разрешён только один режим запуска: одиночный profile или одна suite.'
}
if ($Resume -and [string]::IsNullOrWhiteSpace($Profile)) {
    throw '-Resume разрешён только для одного явно указанного -Profile.'
}
if (($RunReferenceSuite -or $RunNextSuite -or $RunCapacitySuite -or
    $RunInteractionSuite) -and
    -not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw '-OutputDirectory разрешён только для одиночного -Profile.'
}
$outputWasExplicit = $PSBoundParameters.ContainsKey('OutputDirectory') -and
    -not [string]::IsNullOrWhiteSpace($OutputDirectory)
if ($Resume -and -not $outputWasExplicit) {
    throw '-Resume требует явно указать -OutputDirectory существующего запуска.'
}

$selectedProfileIds = if (-not [string]::IsNullOrWhiteSpace($Profile)) {
    @($Profile)
}
else {
    $suiteId = if ($RunReferenceSuite) {
        'reference'
    }
    elseif ($RunNextSuite) {
        'next'
    }
    elseif ($RunCapacitySuite) {
        'capacity'
    }
    else {
        'interaction'
    }
    $selectedSuite = @($suites | Where-Object { $_.id -ceq $suiteId })
    if ($selectedSuite.Count -ne 1) {
        throw "Suite отсутствует или неоднозначен: $suiteId"
    }
    @($selectedSuite[0].profileIds)
}
$selectedProfiles = @()
foreach ($profileId in $selectedProfileIds) {
    $matches = @($profiles | Where-Object { $_.id -ceq $profileId })
    if ($matches.Count -ne 1) {
        throw "Неизвестный или неоднозначный profile: $profileId"
    }
    if (-not [bool]$matches[0].runnable) {
        throw "Reference profile нельзя запускать повторно: $profileId"
    }
    $selectedProfiles += $matches[0]
}
$pythonHashSeed = [string]$selectedProfiles[0].pythonHashSeed

$trainer = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'tools\train-wsd-ranker.py') -MustExist -Leaf
$dataset = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'artifacts\wsd-train-shipped.jsonl') -MustExist -Leaf
$datasetItem = Get-Item -LiteralPath $dataset
$datasetSha256 = (Get-FileHash -LiteralPath $dataset -Algorithm SHA256).Hash.ToLowerInvariant()
if ($datasetItem.Length -ne $canonicalDatasetLength -or
    $datasetSha256 -ne $canonicalDatasetSha256) {
    throw 'Dataset не совпадает с каноническими размером и SHA-256.'
}
$requirements = Resolve-SafeLocalPath `
    -Path (Join-Path $root 'tools\requirements-wsd-ranker.txt') -MustExist -Leaf
$pythonCandidate = if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    Join-Path $root '.venv\Scripts\python.exe'
}
elseif ([IO.Path]::IsPathRooted($PythonPath)) {
    $PythonPath
}
else {
    Join-Path $root $PythonPath
}
$python = Resolve-SafeLocalPath -Path $pythonCandidate -MustExist -Leaf

# A child powershell.exe normally isolates this process-scoped value. Preserve
# and restore it as well so direct invocation cannot alter the caller's session.
$previousCublasWorkspaceConfig = [Environment]::GetEnvironmentVariable(
    'CUBLAS_WORKSPACE_CONFIG',
    [EnvironmentVariableTarget]::Process)
$previousPythonHashSeed = [Environment]::GetEnvironmentVariable(
    'PYTHONHASHSEED',
    [EnvironmentVariableTarget]::Process)

try {
[Environment]::SetEnvironmentVariable(
    'CUBLAS_WORKSPACE_CONFIG',
    $cublasWorkspaceConfig,
    [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable(
    'PYTHONHASHSEED',
    $pythonHashSeed,
    [EnvironmentVariableTarget]::Process)

$preflight = @'
import importlib.metadata as metadata
import json
import os
import platform
import re
import struct
import sys
import torch

def canonical(name):
    return re.sub(r"[-_.]+", "-", name).lower()

expected = {}
with open(sys.argv[1], "r", encoding="utf-8") as stream:
    for number, raw_line in enumerate(stream, 1):
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        match = re.fullmatch(
            r"([A-Za-z0-9_.-]+)==([^\s]+) "
            r"--hash=sha256:([0-9a-f]{64})",
            line,
        )
        if not match:
            raise SystemExit(
                f"Неканоническая строка lock-файла {number}: {line!r}"
            )
        name = canonical(match.group(1))
        if name in expected:
            raise SystemExit(f"Повтор пакета в lock-файле: {name}")
        expected[name] = match.group(2)
expected["torch"] = "2.13.0+cu130"
expected["pip"] = "26.1.2"

errors = []
actual = {}
expected_cublas_workspace_config = ":4096:8"
expected_python_hash_seed = sys.argv[2]
if os.environ.get("CUBLAS_WORKSPACE_CONFIG") != expected_cublas_workspace_config:
    errors.append(
        "CUBLAS_WORKSPACE_CONFIG: ожидалось "
        f"{expected_cublas_workspace_config}, получено "
        f"{os.environ.get('CUBLAS_WORKSPACE_CONFIG')!r}"
    )
if os.environ.get("PYTHONHASHSEED") != expected_python_hash_seed:
    errors.append(
        "PYTHONHASHSEED: ожидалось "
        f"{expected_python_hash_seed}, получено {os.environ.get('PYTHONHASHSEED')!r}"
    )
if platform.python_implementation() != "CPython":
    errors.append(
        f"требуется CPython, получено {platform.python_implementation()}"
    )
if platform.python_version() != "3.12.10":
    errors.append(
        f"требуется Python 3.12.10, получено {platform.python_version()}"
    )
if struct.calcsize("P") * 8 != 64:
    errors.append(f"требуется 64-bit Python, получено {struct.calcsize('P') * 8}-bit")

for distribution in metadata.distributions():
    raw_name = distribution.metadata.get("Name")
    if not raw_name:
        errors.append("установлен пакет без Name metadata")
        continue
    name = canonical(raw_name)
    version = str(distribution.version)
    if name in actual and actual[name] != version:
        errors.append(
            f"конфликтующие версии {name}: {actual[name]} и {version}"
        )
    actual[name] = version

for package, wanted in sorted(expected.items()):
    found = actual.get(package, "не установлена")
    if found != wanted:
        errors.append(f"{package}: ожидалась версия {wanted}, получена {found}")
for package in sorted(set(actual) - set(expected)):
    errors.append(f"лишний пакет вне lock-файла: {package}=={actual[package]}")

if str(torch.__version__) != expected["torch"]:
    errors.append(f"torch runtime: ожидалась {expected['torch']}, получена {torch.__version__}")
if torch.version.cuda != "13.0":
    errors.append(f"Среда CUDA: ожидалась 13.0, получена {torch.version.cuda}")
if not torch.cuda.is_available():
    errors.append("CUDA недоступна; CPU fallback запрещён")

device = None
capability = None
bf16 = False
if torch.cuda.is_available():
    device = torch.cuda.get_device_name(0)
    capability = tuple(torch.cuda.get_device_capability(0))
    if capability < (12, 0):
        errors.append(f"требуется compute capability >= (12, 0), получена {capability}")
    bf16 = bool(torch.cuda.is_bf16_supported())
    if not bf16:
        errors.append("требуется аппаратная поддержка BF16")
    if not errors:
        probe = torch.ones((16, 16), device="cuda", dtype=torch.bfloat16)
        result = probe @ probe
        torch.cuda.synchronize()
        if result.dtype != torch.bfloat16:
            errors.append(f"Проба BF16 вернула {result.dtype}")

if errors:
    raise SystemExit("Проверка RTX-окружения не пройдена:\n" + "\n".join(errors))

print(json.dumps({
    "packages": actual,
    "device": device,
    "capability": capability,
    "bf16": bf16,
    "cublasWorkspaceConfig": os.environ.get("CUBLAS_WORKSPACE_CONFIG"),
    "pythonHashSeed": os.environ.get("PYTHONHASHSEED"),
    "python": {
        "implementation": platform.python_implementation(),
        "version": platform.python_version(),
        "pointerBits": struct.calcsize("P") * 8,
    },
}, sort_keys=True))
'@

Write-Host 'Проверка закреплённого CUDA/BF16 окружения...'
& $python -c $preflight $requirements $pythonHashSeed
if ($LASTEXITCODE -ne 0) {
    throw "CUDA preflight завершился с кодом $LASTEXITCODE; CPU fallback запрещён."
}

$runsRoot = Join-Path $root 'artifacts\runs'
if (-not (Test-Path -LiteralPath $runsRoot)) {
    $artifactsRoot = Resolve-SafeLocalPath `
        -Path (Join-Path $root 'artifacts') -MustExist -Container
    New-Item -ItemType Directory -Path (Join-Path $artifactsRoot 'runs') | Out-Null
}
$runsRoot = Resolve-SafeLocalPath -Path $runsRoot -MustExist -Container

$runStamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$packageManifestSha256 = (
    Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256
).Hash.ToLowerInvariant()

function Invoke-WsdExperimentProfile {
    param([Parameter(Mandatory)]$ExperimentProfile)

    $profileId = [string]$ExperimentProfile.id
    [Environment]::SetEnvironmentVariable(
        'PYTHONHASHSEED',
        [string]$ExperimentProfile.pythonHashSeed,
        [EnvironmentVariableTarget]::Process)

    $outputCandidate = if ($outputWasExplicit) {
        if ([IO.Path]::IsPathRooted($OutputDirectory)) {
            $OutputDirectory
        }
        else {
            Join-Path $root $OutputDirectory
        }
    }
    else {
        Join-Path $runsRoot "wsd-$profileId-rtx5080-$runStamp"
    }
    $output = Resolve-SafeLocalPath -Path $outputCandidate
    $outputParent = Resolve-SafeLocalPath `
        -Path (Split-Path $output -Parent) -MustExist -Container

    $profileRunManifest = [ordered]@{
        schemaVersion = 1
        packageManifestSha256 = $packageManifestSha256
        profile = $ExperimentProfile
        sharedTrainingProfile = Get-SharedTrainingProfile
    }
    $profileRunManifestJson = $profileRunManifest |
        ConvertTo-Json -Depth 8 -Compress

    if ($Resume) {
        $output = Resolve-SafeLocalPath -Path $output -MustExist -Container
        $runState = Resolve-SafeLocalPath `
            -Path (Join-Path $output 'run-state.json') -MustExist -Leaf
        $profileRunManifestPath = Resolve-SafeLocalPath `
            -Path (Join-Path $output 'experiment-profile.json') -MustExist -Leaf
        $actualProfileRunManifestJson = Get-Content `
            -LiteralPath $profileRunManifestPath -Raw -Encoding UTF8 |
            ConvertFrom-Json | ConvertTo-Json -Depth 8 -Compress
        if (-not [string]::Equals(
            $actualProfileRunManifestJson,
            $profileRunManifestJson,
            [StringComparison]::Ordinal)) {
            throw "Resume profile/package не совпадает с output: $output"
        }
    }
    else {
        if (Test-Path -LiteralPath $output) {
            throw "Новый output уже существует; используйте другой путь или явный -Resume: $output"
        }
        New-Item -ItemType Directory -Path $output | Out-Null
        $output = Resolve-SafeLocalPath -Path $output -MustExist -Container
        $profileRunManifestPath = Join-Path $output 'experiment-profile.json'
        [IO.File]::WriteAllText(
            $profileRunManifestPath,
            ($profileRunManifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
    }

    $sessionId = "{0}-{1}" -f `
        [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'),
        [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $logPath = Join-Path $output "training-$sessionId.log"
    $freezePath = Join-Path $output "pip-freeze-$sessionId.txt"
    if ((Test-Path -LiteralPath $logPath) -or
        (Test-Path -LiteralPath $freezePath)) {
        throw 'Не удалось создать уникальные имена журнала запуска.'
    }

    $freeze = @(& $python -m pip freeze)
    if ($LASTEXITCODE -ne 0) {
        throw "pip freeze завершился с кодом $LASTEXITCODE."
    }
    [IO.File]::WriteAllLines($freezePath, $freeze, [Text.UTF8Encoding]::new($false))

    $trainingArguments = @(
        $trainer,
        '--dataset', $dataset,
        '--output-dir', $output,
        '--model', [string]$ExperimentProfile.modelId,
        '--revision', [string]$ExperimentProfile.revision,
        '--license', [string]$ExperimentProfile.license,
        '--pair-variant', 'plain',
        '--seed', [string]$ExperimentProfile.seed,
        '--epochs', '6',
        '--batch-size', '8',
        '--groups-per-batch', '8',
        '--gradient-accumulation-steps', '8',
        '--eval-batch-size', '8',
        '--max-length', '128',
        '--pad-to-multiple-of', '8',
        '--learning-rate', '2e-5',
        '--weight-decay', '0.01',
        '--warmup-ratio', '0.1',
        '--max-grad-norm', '1.0',
        '--group-weighting', 'equal',
        '--loss-mode', 'hybrid',
        '--pairwise-coefficient', '0.25',
        '--positive-class-weight', 'auto',
        '--head-initialization', [string]$ExperimentProfile.headInitialization,
        '--truncate-encoder-layers', [string]$ExperimentProfile.encoderLayers,
        '--freeze-encoder-layers', '0',
        '--device', 'cuda',
        '--precision', 'bf16',
        '--threads', '8',
        '--workers', '0',
        '--log-every-steps', '25',
        '--save-every-steps', '250',
        '--save-total-limit', '2',
        '--selection-metric', 'group_accuracy',
        '--runtime-batch-size', '16',
        '--parity-tolerance', '1e-4'
    )
    if ($Resume) {
        $trainingArguments += @('--resume', 'auto')
    }

    Write-Host "=== Profile: $profileId ==="
    Write-Host "Output: $output"
    Write-Host "Лог: $logPath"
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5.1 wraps native stderr as NativeCommandError when
        # ErrorActionPreference is Stop. Drain both streams and trust the exit code.
        $ErrorActionPreference = 'Continue'
        & $python @trainingArguments 2>&1 |
            ForEach-Object { $_.ToString() } |
            Tee-Object -FilePath $logPath -ErrorAction Stop
        $trainingExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if ($trainingExitCode -ne 0) {
        throw "Profile $profileId завершился с кодом $trainingExitCode. CPU fallback не выполнялся."
    }

    $bundle = Resolve-SafeLocalPath `
        -Path (Join-Path $output 'bundle\GlossDeberta') -MustExist -Container
    $bundleManifest = Resolve-SafeLocalPath `
        -Path (Join-Path $bundle 'manifest.json') -MustExist -Leaf
    $bundleManifestSha256 = (
        Get-FileHash -LiteralPath $bundleManifest -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    Write-Host "Готовый ONNX bundle: $bundle"
    Write-Host "Bundle manifest SHA-256: $bundleManifestSha256"
}

foreach ($experimentProfile in $selectedProfiles) {
    Invoke-WsdExperimentProfile -ExperimentProfile $experimentProfile
}
}
finally {
    [Environment]::SetEnvironmentVariable(
        'PYTHONHASHSEED',
        $previousPythonHashSeed,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        'CUBLAS_WORKSPACE_CONFIG',
        $previousCublasWorkspaceConfig,
        [EnvironmentVariableTarget]::Process)
}
