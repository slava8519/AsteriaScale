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
            ($_.Name -in @("global.json", "NuGet.config"))
        })

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

function Test-AsteriaDistCurrent {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$RequireIdentity
    )

    $output = Join-Path $Root "dist\AsteriaScale"
    $revisionMarker = Join-Path $output ".build-revision"
    $fingerprintMarker = Join-Path $output ".build-source-fingerprint"
    $requiredFiles = @(
        (Join-Path $output "AsteriaScale.exe"),
        (Join-Path $output "Ranker\AsteriaScale.RankerWorker.exe"),
        $revisionMarker,
        $fingerprintMarker
    )
    if ($RequireIdentity) {
        $requiredFiles += Join-Path $Root "dist\AsteriaScale.Identity.msix"
    }

    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            return $false
        }
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
