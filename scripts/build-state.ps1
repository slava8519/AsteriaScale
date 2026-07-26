function Get-AsteriaSourceRevision {
    param([Parameter(Mandatory = $true)][string]$Root)

    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) {
        return $null
    }

    $revision = & $git.Source -C $Root rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $revision) {
        return $null
    }

    return $revision.Trim()
}

function Get-AsteriaBuildInputFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    $inputRoots = @(
        (Join-Path $Root "src"),
        (Join-Path $Root "assets\Dictionaries"),
        (Join-Path $Root "assets\Morphology"),
        (Join-Path $Root "assets\TextHook"),
        (Join-Path $Root "assets\Ranker"),
        (Join-Path $Root "packaging"),
        (Join-Path $Root "scripts")
    )
    $files = @(foreach ($inputRoot in $inputRoots) {
        if (Test-Path -LiteralPath $inputRoot -PathType Container) {
            Get-ChildItem -LiteralPath $inputRoot -File -Recurse -Force |
                Where-Object {
                    $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
                        $_.Name -notlike "ranker-holdout*.json" -and
                        $_.Name -notlike "ranker-benchmark*.json"
                }
        }
    })

    $files += @(Get-ChildItem -LiteralPath $Root -File -Force |
        Where-Object {
            $_.Name -like "*.sln*" -or
            $_.Name -like "*.props" -or
            $_.Name -like "*.targets" -or
            # Published by AsteriaScale.csproj, so a change to either belongs in
            # the fingerprint even though neither is source.
            ($_.Name -in @("global.json", "NuGet.config", "THIRD_PARTY_NOTICES.md", "LICENSE"))
        })

    # Named one by one rather than by directory: tools\dictionary-source also
    # holds the multi-gigabyte Wiktionary dump, which is not a build input.
    foreach ($publishedInput in @("tools\dictionary-source\vn-sources.lock.json")) {
        $publishedPath = Join-Path $Root $publishedInput
        if (Test-Path -LiteralPath $publishedPath -PathType Leaf) {
            $files += Get-Item -LiteralPath $publishedPath -Force
        }
    }

    return $files | Sort-Object FullName -Unique
}

function Get-AsteriaSourceFingerprint {
    param([Parameter(Mandatory = $true)][string]$Root)

    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $manifest = New-Object Text.StringBuilder
    foreach ($file in Get-AsteriaBuildInputFiles -Root $resolvedRoot) {
        $relativePath = $file.FullName.Substring($resolvedRoot.Length).TrimStart('\', '/')
        [void]$manifest.Append($relativePath.Replace('\', '/'))
        [void]$manifest.Append([char]0x1f)
        [void]$manifest.Append($file.Length)
        [void]$manifest.Append([char]0x1f)
        if ($file.Length -le 16MB) {
            [void]$manifest.Append((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)
        } else {
            # Large pinned model/dictionary artifacts are checked elsewhere by
            # size/SHA contracts; avoid hashing hundreds of MB on every launch.
            [void]$manifest.Append($file.LastWriteTimeUtc.Ticks)
        }
        [void]$manifest.AppendLine()
    }

    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        $statusArguments = @(
            "-C", $resolvedRoot,
            "status", "--porcelain=v1", "--untracked-files=all", "--",
            ":(top)src",
            ":(top)assets/Dictionaries",
            ":(top)assets/Morphology",
            ":(top)assets/TextHook",
            ":(top)assets/Ranker",
            ":(top)packaging",
            ":(top)scripts",
            ":(top,glob)*.sln*",
            ":(top,glob)*.props",
            ":(top,glob)*.targets",
            ":(top)global.json",
            ":(top)NuGet.config",
            ":(top)THIRD_PARTY_NOTICES.md",
            ":(top)LICENSE",
            ":(top)tools/dictionary-source/vn-sources.lock.json",
            ":(top,exclude,glob)assets/Ranker/ranker-holdout*.json",
            ":(top,exclude,glob)assets/Ranker/ranker-benchmark*.json"
        )
        $status = & $git.Source @statusArguments 2>$null
        if ($LASTEXITCODE -eq 0) {
            [void]$manifest.AppendLine("--git-status--")
            foreach ($line in $status) {
                [void]$manifest.AppendLine($line)
            }
        }
    }

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($manifest.ToString())
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "")
    } finally {
        $sha.Dispose()
    }
}

function Get-AsteriaRequiredRankerFiles {
    param([Parameter(Mandatory = $true)][string]$Output)

    $models = Join-Path $Output "Ranker\Models"
    return @(
        (Join-Path $Output "Ranker\AsteriaScale.RankerWorker.exe"),
        (Join-Path $models "GlossBert\tokenizer.json"),
        (Join-Path $models "GlossBert\Cpu\glossbert-fp32-dynamic.onnx"),
        (Join-Path $models "GlossBert\Npu\glossbert-eager-vitis-context.onnx"),
        (Join-Path $models "GlossBert\Npu\glossbert-eager-vitis-context.onnx.compatibility.txt"),
        (Join-Path $models "GlossBert\Npu\glossbert-eager-vitis-context.onnx_VITISAI.bin"),
        (Join-Path $models "GlossEmbeddings\eng-senses.vec"),
        (Join-Path $models "GlossEmbeddings\minilm-l6-v2.onnx"),
        (Join-Path $models "GlossEmbeddings\tokenizer.json")
    )
}

<#
.SYNOPSIS
Every file a complete publish must contain, derived from the source assets.

.DESCRIPTION
Checking only the two executables meant a dist missing a dictionary, a model or
the notices file still counted as current, so the user kept running an
incomplete install until something failed at runtime. The asset lists are read
from the source tree rather than hard-coded, so adding a dictionary does not
silently fall outside the check.
#>
function Get-AsteriaRequiredDistFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Output
    )

    $required = [Collections.Generic.List[string]]::new()
    $required.Add((Join-Path $Output "AsteriaScale.exe"))
    $required.Add((Join-Path $Output "THIRD_PARTY_NOTICES.md"))
    $required.Add((Join-Path $Output "LICENSE"))
    $required.Add((Join-Path $Output "Dictionaries\README.md"))
    $required.Add((Join-Path $Output "Dictionaries\vn-build-manifest.json"))
    $required.Add((Join-Path $Output "Dictionaries\vn-sources.lock.json"))
    foreach ($rankerFile in Get-AsteriaRequiredRankerFiles -Output $Output) {
        $required.Add($rankerFile)
    }

    $publishedAssets = @(
        @{ Source = "assets\Dictionaries"; Filter = "*.tsv.gz"; Target = "Dictionaries" },
        @{ Source = "assets\TextHook"; Filter = "*.tsv"; Target = "TextHook" },
        @{ Source = "assets\Morphology"; Filter = "*.tsv.gz"; Target = "Morphology" }
    )
    foreach ($publishedAsset in $publishedAssets) {
        $sourceDirectory = Join-Path $Root $publishedAsset.Source
        if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
            continue
        }

        foreach ($asset in Get-ChildItem -LiteralPath $sourceDirectory -File -Filter $publishedAsset.Filter) {
            $required.Add((Join-Path $Output (Join-Path $publishedAsset.Target $asset.Name)))
        }
    }

    return $required.ToArray()
}

<#
.SYNOPSIS
Whether the identity package is a readable MSIX rather than a truncated file.
#>
function Test-AsteriaIdentityPackage {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            return $null -ne ($archive.Entries |
                Where-Object { $_.FullName -eq "AppxManifest.xml" } |
                Select-Object -First 1)
        } finally {
            $archive.Dispose()
        }
    } catch {
        return $false
    }
}

function Test-AsteriaDistCurrent {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$RequireIdentity
    )

    $output = Join-Path $Root "dist\AsteriaScale"
    $revisionMarker = Join-Path $output ".build-revision"
    $fingerprintMarker = Join-Path $output ".build-source-fingerprint"
    $requiredFiles = @(Get-AsteriaRequiredDistFiles -Root $Root -Output $output) + @(
        $revisionMarker,
        $fingerprintMarker
    )

    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            return $false
        }
    }

    if ($RequireIdentity -and
        -not (Test-AsteriaIdentityPackage -Path (Join-Path $Root "dist\AsteriaScale.Identity.msix"))) {
        return $false
    }

    $builtRevision = (Get-Content -LiteralPath $revisionMarker -Raw).Trim()
    $sourceRevision = Get-AsteriaSourceRevision -Root $Root
    if ($sourceRevision -and
        -not [string]::Equals($builtRevision, $sourceRevision, [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $builtFingerprint = (Get-Content -LiteralPath $fingerprintMarker -Raw).Trim()
    $sourceFingerprint = Get-AsteriaSourceFingerprint -Root $Root
    return [string]::Equals(
        $builtFingerprint,
        $sourceFingerprint,
        [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-AsteriaBuildSwapRollback {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Output,
        [Parameter(Mandatory = $true)][string]$PreviousOutput,
        [Parameter(Mandatory = $true)][string]$FailedOutput,
        [Parameter(Mandatory = $true)][string]$IdentityPackage,
        [Parameter(Mandatory = $true)][string]$PreviousIdentity,
        [Parameter(Mandatory = $true)][string]$FailedIdentity,
        [switch]$OldOutputMoved,
        [switch]$NewOutputInstalled,
        [switch]$OldIdentityMoved,
        [switch]$NewIdentityInstalled
    )

    $failures = [Collections.Generic.List[Exception]]::new()
    if ($NewIdentityInstalled -and (Test-Path -LiteralPath $IdentityPackage)) {
        try {
            Move-Item -LiteralPath $IdentityPackage -Destination $FailedIdentity
        } catch {
            $failures.Add([InvalidOperationException]::new(
                "Failed to move the newly installed identity package out of the way.",
                $_.Exception))
        }
    }
    if ($OldIdentityMoved -and (Test-Path -LiteralPath $PreviousIdentity)) {
        try {
            Move-Item -LiteralPath $PreviousIdentity -Destination $IdentityPackage
        } catch {
            $failures.Add([InvalidOperationException]::new(
                "Failed to restore the previous identity package.",
                $_.Exception))
        }
    }
    if ($NewOutputInstalled -and (Test-Path -LiteralPath $Output)) {
        try {
            Move-Item -LiteralPath $Output -Destination $FailedOutput
        } catch {
            $failures.Add([InvalidOperationException]::new(
                "Failed to move the newly installed publish out of the way.",
                $_.Exception))
        }
    }
    if ($OldOutputMoved -and (Test-Path -LiteralPath $PreviousOutput)) {
        try {
            Move-Item -LiteralPath $PreviousOutput -Destination $Output
        } catch {
            $failures.Add([InvalidOperationException]::new(
                "Failed to restore the previous publish.",
                $_.Exception))
        }
    }

    return $failures.ToArray()
}

function New-AsteriaBuildSwapException {
    param(
        [Parameter(Mandatory = $true)][Exception]$OriginalError,
        [Exception[]]$RollbackFailures = @()
    )

    if ($RollbackFailures.Count -eq 0) {
        return $OriginalError
    }

    $allFailures = [Collections.Generic.List[Exception]]::new()
    $allFailures.Add($OriginalError)
    foreach ($failure in $RollbackFailures) {
        $allFailures.Add($failure)
    }

    return [AggregateException]::new(
        "The build artifact swap failed and rollback was incomplete.",
        $allFailures)
}
