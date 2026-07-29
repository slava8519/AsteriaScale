Set-StrictMode -Version Latest

function Test-SafeTransactionChild {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedParent,
        [Parameter(Mandatory)][string]$ExpectedPrefix
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $parent = Split-Path -Parent $fullPath
    $leaf = Split-Path -Leaf $fullPath
    if (-not [string]::Equals(
            $parent,
            [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith($ExpectedPrefix, [StringComparison]::Ordinal)) {
        return $false
    }

    if (Test-Path -LiteralPath $fullPath) {
        $item = Get-Item -LiteralPath $fullPath -Force
        return $item.PSIsContainer -and
               ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0
    }

    return $true
}

function Remove-SafeTransactionChild {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedParent,
        [Parameter(Mandatory)][string]$ExpectedPrefix
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    if (-not (Test-SafeTransactionChild `
            -Path $Path `
            -ExpectedParent $ExpectedParent `
            -ExpectedPrefix $ExpectedPrefix)) {
        throw "Refusing to remove an unsafe SANDWiCH transaction path: $Path"
    }

    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Publish-SandwichBundleTransactional {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceBundle,
        [Parameter(Mandatory)][string]$InstallRoot,
        [Parameter(Mandatory)][string]$AcceptanceJson,
        [Parameter(Mandatory)][scriptblock]$VerifyBundle,
        [ValidateSet('', 'after-backup', 'after-install')]
        [string]$FaultInjectionPhase = ''
    )

    $source = [IO.Path]::GetFullPath($SourceBundle).TrimEnd('\', '/')
    $destination = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
    $destinationRoot = [IO.Path]::GetPathRoot($destination).TrimEnd('\', '/')
    if ([string]::Equals(
            $destination,
            $destinationRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to install SANDWiCH into a filesystem root: $destination"
    }

    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "SANDWiCH transaction source does not exist: $source"
    }

    $sourceItem = Get-Item -LiteralPath $source -Force
    if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing a reparse-point SANDWiCH transaction source: $source"
    }

    $parent = Split-Path -Parent $destination
    $leaf = Split-Path -Leaf $destination
    if ([string]::IsNullOrWhiteSpace($leaf)) {
        throw "SANDWiCH install directory has no leaf name: $destination"
    }

    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $parentItem = Get-Item -LiteralPath $parent -Force
    if (-not $parentItem.PSIsContainer -or
        ($parentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to publish SANDWiCH through an unsafe parent: $parent"
    }

    if (Test-Path -LiteralPath $destination) {
        $destinationItem = Get-Item -LiteralPath $destination -Force
        if (-not $destinationItem.PSIsContainer -or
            ($destinationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to replace an unsafe SANDWiCH install root: $destination"
        }
    }

    $transactionId = [Guid]::NewGuid().ToString('N')
    $prefix = ".$leaf.asteria-sandwich-"
    $staging = Join-Path $parent ($prefix + "staging-$transactionId")
    $backup = Join-Path $parent ($prefix + "backup-$transactionId")
    $failed = Join-Path $parent ($prefix + "failed-$transactionId")
    $oldMoved = $false
    $newInstalled = $false
    $committed = $false
    try {
        New-Item -ItemType Directory -Path (Join-Path $staging 'Cpu') | Out-Null
        foreach ($relativePath in @(
                'Cpu\encoder-nv-fp32.onnx',
                'Cpu\encoder-v-fp32.onnx',
                'tokenizer.json',
                'model-manifest.json')) {
            $sourcePath = Join-Path $source $relativePath
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                throw "SANDWiCH transaction source is incomplete: $sourcePath"
            }

            Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $staging $relativePath)
        }

        Set-Content `
            -LiteralPath (Join-Path $staging 'UPSTREAM_TERMS_ACCEPTED.json') `
            -Value $AcceptanceJson `
            -Encoding UTF8
        & $VerifyBundle $staging

        if (Test-Path -LiteralPath $destination) {
            Move-Item -LiteralPath $destination -Destination $backup
            $oldMoved = $true
        }

        if ($FaultInjectionPhase -eq 'after-backup') {
            throw 'Injected SANDWiCH publish failure after backup.'
        }

        Move-Item -LiteralPath $staging -Destination $destination
        $newInstalled = $true
        if ($FaultInjectionPhase -eq 'after-install') {
            throw 'Injected SANDWiCH publish failure after install.'
        }

        & $VerifyBundle $destination
        $committed = $true
    }
    catch {
        $publishError = $_
        $rollbackErrors = [Collections.Generic.List[string]]::new()
        if ($newInstalled -and (Test-Path -LiteralPath $destination)) {
            try {
                Move-Item -LiteralPath $destination -Destination $failed
                $newInstalled = $false
            }
            catch {
                $rollbackErrors.Add("could not quarantine the failed bundle: $($_.Exception.Message)")
            }
        }

        if ($oldMoved -and (Test-Path -LiteralPath $backup)) {
            try {
                if (Test-Path -LiteralPath $destination) {
                    throw "destination is still occupied: $destination"
                }

                Move-Item -LiteralPath $backup -Destination $destination
                $oldMoved = $false
            }
            catch {
                $rollbackErrors.Add("could not restore the previous bundle: $($_.Exception.Message)")
            }
        }

        if ($rollbackErrors.Count -gt 0) {
            throw [InvalidOperationException]::new(
                "$($publishError.Exception.Message) Rollback incomplete: " +
                ($rollbackErrors -join '; '),
                $publishError.Exception)
        }

        throw
    }
    finally {
        Remove-SafeTransactionChild `
            -Path $staging `
            -ExpectedParent $parent `
            -ExpectedPrefix $prefix
        Remove-SafeTransactionChild `
            -Path $failed `
            -ExpectedParent $parent `
            -ExpectedPrefix $prefix
        if ($committed) {
            Remove-SafeTransactionChild `
                -Path $backup `
                -ExpectedParent $parent `
                -ExpectedPrefix $prefix
        }
    }
}
