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

function Get-AsteriaSandwichBundleRelativeFiles {
    return @(
        "tokenizer.json",
        "model-manifest.json",
        "Cpu\encoder-nv-fp32.onnx",
        "Cpu\encoder-v-fp32.onnx"
    )
}

function Test-AsteriaSandwichBundleComplete {
    param([Parameter(Mandatory = $true)][string]$Root)

    $bundle = Join-Path $Root "src\AsteriaScale.RankerWorker\Models\Sandwich"
    foreach ($relativePath in Get-AsteriaSandwichBundleRelativeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $bundle $relativePath) -PathType Leaf)) {
            return $false
        }
    }

    return $true
}

function Get-AsteriaConsecBundleRelativeFiles {
    return @(
        "tokenizer.json",
        "model-manifest.json",
        "Cpu\consec-wngt-fp32.onnx",
        "Cpu\consec-wngt-fp32.onnx.data"
    )
}

function Test-AsteriaConsecBundleComplete {
    param([Parameter(Mandatory = $true)][string]$Root)

    $bundle = Join-Path $Root "src\AsteriaScale.RankerWorker\Models\Consec"
    foreach ($relativePath in Get-AsteriaConsecBundleRelativeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $bundle $relativePath) -PathType Leaf)) {
            return $false
        }
    }

    return $true
}

function Get-AsteriaRequiredRankerFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Output,
        [string]$Root = "",
        [switch]$IncludeExperimentalSandwichModel,
        [switch]$IncludeExperimentalConsecModel
    )

    $models = Join-Path $Output "Ranker\Models"
    $required = [Collections.Generic.List[string]]::new()
    foreach ($path in @(
        (Join-Path $Output "Ranker\AsteriaScale.RankerWorker.exe"),
        (Join-Path $models "GlossBert\tokenizer.json"),
        (Join-Path $models "GlossBert\Cpu\glossbert-fp32-dynamic.onnx"),
        (Join-Path $models "GlossBert\Npu\glossbert-eager-vitis-context.onnx"),
        (Join-Path $models "GlossBert\Npu\glossbert-eager-vitis-context.onnx.compatibility.txt"),
        (Join-Path $models "GlossBert\Npu\glossbert-eager-vitis-context.onnx_VITISAI.bin"),
        (Join-Path $models "GlossEmbeddings\eng-senses.vec"),
        (Join-Path $models "GlossEmbeddings\minilm-l6-v2.onnx"),
        (Join-Path $models "GlossEmbeddings\tokenizer.json")
    )) {
        $required.Add($path)
    }

    $includeSandwich = $IncludeExperimentalSandwichModel -or
        (-not [string]::IsNullOrWhiteSpace($Root) -and
            (Test-AsteriaSandwichBundleComplete -Root $Root))
    if ($includeSandwich) {
        $sandwichOutput = Join-Path $models "Sandwich"
        foreach ($relativePath in Get-AsteriaSandwichBundleRelativeFiles) {
            $required.Add((Join-Path $sandwichOutput $relativePath))
        }
    }

    $includeConsec = $IncludeExperimentalConsecModel -or
        (-not [string]::IsNullOrWhiteSpace($Root) -and
            (Test-AsteriaConsecBundleComplete -Root $Root))
    if ($includeConsec) {
        $consecOutput = Join-Path $models "Consec"
        foreach ($relativePath in Get-AsteriaConsecBundleRelativeFiles) {
            $required.Add((Join-Path $consecOutput $relativePath))
        }
    }

    return $required
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
        [Parameter(Mandatory = $true)][string]$Output,
        [switch]$IncludeExperimentalSandwichModel,
        [switch]$IncludeExperimentalConsecModel
    )

    $required = [Collections.Generic.List[string]]::new()
    $required.Add((Join-Path $Output "AsteriaScale.exe"))
    $required.Add((Join-Path $Output "THIRD_PARTY_NOTICES.md"))
    $required.Add((Join-Path $Output "LICENSE"))
    $required.Add((Join-Path $Output "Dictionaries\README.md"))
    $required.Add((Join-Path $Output "Dictionaries\vn-build-manifest.json"))
    $required.Add((Join-Path $Output "Dictionaries\vn-sources.lock.json"))
    foreach ($rankerFile in Get-AsteriaRequiredRankerFiles `
        -Output $Output `
        -Root $Root `
        -IncludeExperimentalSandwichModel:$IncludeExperimentalSandwichModel `
        -IncludeExperimentalConsecModel:$IncludeExperimentalConsecModel) {
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

function Get-AsteriaOutputManifestPath {
    param([Parameter(Mandatory = $true)][string]$Output)

    return Join-Path $Output ".build-output-manifest.json"
}

function Get-AsteriaOutputPayloadFiles {
    param([Parameter(Mandatory = $true)][string]$Output)

    $resolvedOutput = [IO.Path]::GetFullPath($Output).TrimEnd('\', '/')
    $excluded = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @(
        ".build-output-manifest.json",
        ".build-revision",
        ".build-source-fingerprint")) {
        [void]$excluded.Add((Join-Path $resolvedOutput $name))
    }

    return @(Get-ChildItem -LiteralPath $resolvedOutput -File -Recurse -Force |
        Where-Object { -not $excluded.Contains($_.FullName) } |
        Sort-Object FullName)
}

<#
.SYNOPSIS
Writes a closed manifest of every published byte under dist\AsteriaScale.

.DESCRIPTION
The source fingerprint proves which inputs a build used; it says nothing about
an output DLL or asset deleted/corrupted afterwards. This second manifest covers
the complete publish, including the self-contained worker runtime. The manifest
and the two source markers are excluded to avoid a self-referential digest.
#>
function New-AsteriaOutputManifest {
    param([Parameter(Mandatory = $true)][string]$Output)

    $resolvedOutput = [IO.Path]::GetFullPath($Output).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $resolvedOutput -PathType Container)) {
        throw "Cannot create an output manifest for a missing directory: $resolvedOutput"
    }

    $manifestPath = Get-AsteriaOutputManifestPath -Output $resolvedOutput
    $entries = @(foreach ($file in Get-AsteriaOutputPayloadFiles -Output $resolvedOutput) {
        $relativePath = $file.FullName.Substring($resolvedOutput.Length).TrimStart('\', '/')
        [ordered]@{
            path = $relativePath.Replace('\', '/')
            size = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    })
    if ($entries.Count -eq 0) {
        throw "Refusing to create an empty output manifest: $resolvedOutput"
    }

    $document = [ordered]@{
        schemaVersion = 1
        files = $entries
    }
    $temporaryPath = "$manifestPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $document | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath $temporaryPath -Encoding UTF8
        Move-Item -LiteralPath $temporaryPath -Destination $manifestPath -Force
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }

    return $manifestPath
}

function Test-AsteriaOutputManifest {
    param([Parameter(Mandatory = $true)][string]$Output)

    $resolvedOutput = [IO.Path]::GetFullPath($Output).TrimEnd('\', '/')
    $outputPrefix = $resolvedOutput + [IO.Path]::DirectorySeparatorChar
    $manifestPath = Get-AsteriaOutputManifestPath -Output $resolvedOutput
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return $false
    }

    try {
        $document = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($document.schemaVersion -ne 1) {
            return $false
        }

        $expected = [Collections.Generic.Dictionary[string, object]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in @($document.files)) {
            $relativePath = [string]$entry.path
            if ([string]::IsNullOrWhiteSpace($relativePath) -or
                $relativePath.Contains('\') -or
                [IO.Path]::IsPathRooted($relativePath) -or
                @($relativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0 -or
                -not ([string]$entry.sha256 -match '^[0-9A-Fa-f]{64}$') -or
                [long]$entry.size -lt 0 -or
                $expected.ContainsKey($relativePath)) {
                return $false
            }

            $fullPath = [IO.Path]::GetFullPath(
                (Join-Path $resolvedOutput $relativePath.Replace('/', '\')))
            if (-not $fullPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }
            $expected.Add($relativePath, $entry)
        }

        $actualFiles = @(Get-AsteriaOutputPayloadFiles -Output $resolvedOutput)
        if ($expected.Count -eq 0 -or $actualFiles.Count -ne $expected.Count) {
            return $false
        }

        foreach ($file in $actualFiles) {
            $relativePath = $file.FullName.Substring($resolvedOutput.Length).TrimStart('\', '/')
            $relativePath = $relativePath.Replace('\', '/')
            $entry = $null
            if (-not $expected.TryGetValue($relativePath, [ref]$entry)) {
                return $false
            }
            if ($file.Length -ne [long]$entry.size -or
                -not [string]::Equals(
                    (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash,
                    [string]$entry.sha256,
                    [StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }
        }

        return $true
    } catch {
        return $false
    }
}

function Get-AsteriaZipEntry {
    param(
        [Parameter(Mandatory = $true)]$Archive,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return $Archive.Entries |
        Where-Object {
            [string]::Equals(
                $_.FullName.Replace('\', '/'),
                $Name.Replace('\', '/'),
                [StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object -First 1
}

function Read-AsteriaZipEntryBytes {
    param([Parameter(Mandatory = $true)]$Entry)

    $stream = $Entry.Open()
    $memory = New-Object IO.MemoryStream
    try {
        $stream.CopyTo($memory)
        return $memory.ToArray()
    } finally {
        $stream.Dispose()
        $memory.Dispose()
    }
}

function ConvertFrom-AsteriaXmlBytes {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $memory = New-Object IO.MemoryStream(, $Bytes)
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($memory, $settings)
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    } finally {
        if ($reader) {
            $reader.Dispose()
        }
        $memory.Dispose()
    }
}

function Test-AsteriaPngDimensions {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes,
        [Parameter(Mandatory = $true)][int]$Width,
        [Parameter(Mandatory = $true)][int]$Height
    )

    Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
    $memory = New-Object IO.MemoryStream(, $Bytes)
    $image = $null
    try {
        $image = [Drawing.Image]::FromStream($memory, $true, $true)
        return $image.Width -eq $Width -and
            $image.Height -eq $Height -and
            $image.RawFormat.Guid -eq [Drawing.Imaging.ImageFormat]::Png.Guid
    } catch {
        return $false
    } finally {
        if ($image) {
            $image.Dispose()
        }
        $memory.Dispose()
    }
}

<#
.SYNOPSIS
Returns validated identity metadata, or null for a malformed/incomplete MSIX.

.DESCRIPTION
MakeAppx must use /nv for this AllowExternalContent package. Consequently this
check owns the validation that /nv skips: package structure, the project identity
contract, every declared logo and (after signing) the Authenticode signature.
#>
function Get-AsteriaIdentityPackageInfo {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$AllowUnsigned
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $manifestEntry = Get-AsteriaZipEntry -Archive $archive -Name "AppxManifest.xml"
            $blockMapEntry = Get-AsteriaZipEntry -Archive $archive -Name "AppxBlockMap.xml"
            $contentTypesEntry = Get-AsteriaZipEntry -Archive $archive -Name "[Content_Types].xml"
            if (-not $manifestEntry -or $manifestEntry.Length -le 0 -or
                -not $blockMapEntry -or $blockMapEntry.Length -le 0 -or
                -not $contentTypesEntry -or $contentTypesEntry.Length -le 0) {
                return $null
            }

            $manifestBytes = Read-AsteriaZipEntryBytes -Entry $manifestEntry
            $manifest = ConvertFrom-AsteriaXmlBytes -Bytes $manifestBytes
            $blockMap = ConvertFrom-AsteriaXmlBytes `
                -Bytes (Read-AsteriaZipEntryBytes -Entry $blockMapEntry)
            $contentTypes = ConvertFrom-AsteriaXmlBytes `
                -Bytes (Read-AsteriaZipEntryBytes -Entry $contentTypesEntry)
            if ($manifest.DocumentElement.LocalName -ne "Package" -or
                $blockMap.DocumentElement.LocalName -ne "BlockMap" -or
                $contentTypes.DocumentElement.LocalName -ne "Types") {
                return $null
            }

            $namespaces = New-Object Xml.XmlNamespaceManager($manifest.NameTable)
            $namespaces.AddNamespace(
                "f",
                "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
            $namespaces.AddNamespace(
                "uap",
                "http://schemas.microsoft.com/appx/manifest/uap/windows10")
            $namespaces.AddNamespace(
                "uap10",
                "http://schemas.microsoft.com/appx/manifest/uap/windows10/10")
            $identity = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespaces)
            $application = $manifest.SelectSingleNode(
                "/f:Package/f:Applications/f:Application[@Id='AsteriaScale']",
                $namespaces)
            $externalContent = $manifest.SelectSingleNode(
                "/f:Package/f:Properties/uap10:AllowExternalContent",
                $namespaces)
            if (-not $identity -or
                $identity.GetAttribute("Name") -ne "AsteriaScale" -or
                $identity.GetAttribute("Publisher") -ne "CN=Asteria Scale Local" -or
                -not $application -or
                $application.GetAttribute("Executable") -ne "AsteriaScale.exe" -or
                -not $externalContent -or
                $externalContent.InnerText.Trim() -ne "true") {
                return $null
            }

            $version = $null
            if (-not [Version]::TryParse($identity.GetAttribute("Version"), [ref]$version)) {
                return $null
            }

            $assetReferences = [Collections.Generic.HashSet[string]]::new(
                [StringComparer]::OrdinalIgnoreCase)
            foreach ($attribute in $manifest.SelectNodes("//@*")) {
                if ($attribute.Value -match '^Assets[\\/][^\\/]+$') {
                    [void]$assetReferences.Add($attribute.Value.Replace('\', '/'))
                }
            }
            foreach ($logo in $manifest.SelectNodes("//f:Logo", $namespaces)) {
                if ($logo.InnerText.Trim() -match '^Assets[\\/][^\\/]+$') {
                    [void]$assetReferences.Add($logo.InnerText.Trim().Replace('\', '/'))
                }
            }
            $requiredAssets = @{
                "Assets/storelogo.png" = @(50, 50)
                "Assets/Square150x150Logo.png" = @(150, 150)
                "Assets/Square44x44Logo.png" = @(44, 44)
            }
            foreach ($requiredAsset in $requiredAssets.Keys) {
                if (-not $assetReferences.Contains($requiredAsset)) {
                    return $null
                }
            }

            $blockNamespaces = New-Object Xml.XmlNamespaceManager($blockMap.NameTable)
            $blockNamespaces.AddNamespace(
                "b",
                "http://schemas.microsoft.com/appx/2010/blockmap")
            $blockFiles = [Collections.Generic.HashSet[string]]::new(
                [StringComparer]::OrdinalIgnoreCase)
            foreach ($fileNode in $blockMap.SelectNodes("/b:BlockMap/b:File", $blockNamespaces)) {
                [void]$blockFiles.Add($fileNode.GetAttribute("Name").Replace('\', '/'))
            }
            foreach ($requiredEntry in @("AppxManifest.xml") + @($assetReferences)) {
                $entry = Get-AsteriaZipEntry -Archive $archive -Name $requiredEntry
                if (-not $entry -or $entry.Length -le 0 -or
                    -not $blockFiles.Contains($requiredEntry.Replace('\', '/'))) {
                    return $null
                }
                $normalizedEntry = $requiredEntry.Replace('\', '/')
                if ($requiredAssets.ContainsKey($normalizedEntry)) {
                    $dimensions = $requiredAssets[$normalizedEntry]
                    if (-not (Test-AsteriaPngDimensions `
                        -Bytes (Read-AsteriaZipEntryBytes -Entry $entry) `
                        -Width $dimensions[0] `
                        -Height $dimensions[1])) {
                        return $null
                    }
                }
            }

            if (-not $AllowUnsigned) {
                $signatureEntry = Get-AsteriaZipEntry -Archive $archive -Name "AppxSignature.p7x"
                if (-not $signatureEntry -or $signatureEntry.Length -le 0) {
                    return $null
                }
            }

            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $manifestSha256 = ([BitConverter]::ToString(
                    $sha.ComputeHash($manifestBytes))).Replace("-", "")
            } finally {
                $sha.Dispose()
            }
        } finally {
            $archive.Dispose()
        }

        if (-not $AllowUnsigned) {
            $signature = Get-AuthenticodeSignature -LiteralPath $Path
            if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
                -not $signature.SignerCertificate -or
                $signature.SignerCertificate.Subject -ne "CN=Asteria Scale Local") {
                return $null
            }
        }

        return [pscustomobject]@{
            Name = $identity.GetAttribute("Name")
            Publisher = $identity.GetAttribute("Publisher")
            Version = $version
            ManifestSha256 = $manifestSha256
            Assets = @($assetReferences | Sort-Object)
        }
    } catch {
        return $null
    }
}

function Test-AsteriaIdentityPackage {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$AllowUnsigned
    )

    return $null -ne (Get-AsteriaIdentityPackageInfo `
        -Path $Path `
        -AllowUnsigned:$AllowUnsigned)
}

function Test-AsteriaPackageRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Actual
    )

    try {
        return [string]::Equals(
            [IO.Path]::GetFullPath($Expected).TrimEnd('\', '/'),
            [IO.Path]::GetFullPath($Actual).TrimEnd('\', '/'),
            [StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Get-AsteriaRegisteredPackageRoot {
    param([Parameter(Mandatory = $true)]$Package)

    $registryPath = Join-Path `
        "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages" `
        $Package.PackageFullName
    try {
        return (Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop).PackageRootFolder
    } catch {
        return $null
    }
}

function Test-AsteriaIdentityRegistrationCurrent {
    param(
        [Parameter(Mandatory = $true)]$Package,
        [Parameter(Mandatory = $true)][string]$RegisteredRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedExternalLocation,
        [Parameter(Mandatory = $true)]$IdentityInfo
    )

    try {
        $registeredManifest = Join-Path $Package.InstallLocation "AppxManifest.xml"
        return `
            (Test-AsteriaPackageRoot `
                -Expected $ExpectedExternalLocation `
                -Actual $RegisteredRoot) -and `
            $Package.Name -eq $IdentityInfo.Name -and `
            $Package.Publisher -eq $IdentityInfo.Publisher -and `
            [Version]$Package.Version -eq [Version]$IdentityInfo.Version -and `
            (Test-Path -LiteralPath $registeredManifest -PathType Leaf) -and `
            [string]::Equals(
                (Get-FileHash -LiteralPath $registeredManifest -Algorithm SHA256).Hash,
                $IdentityInfo.ManifestSha256,
                [StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Test-AsteriaSameVersionRegistrationError {
    param([Parameter(Mandatory = $true)]$ErrorRecord)

    $exception = if ($ErrorRecord -is [Management.Automation.ErrorRecord]) {
        $ErrorRecord.Exception
    } elseif ($ErrorRecord -is [Exception]) {
        $ErrorRecord
    } else {
        $null
    }
    while ($exception) {
        if ($exception.HResult -eq -2147009285) { # 0x80073CFB, PACKAGE_ALREADY_EXISTS
            return $true
        }
        $exception = $exception.InnerException
    }
    return $false
}

function Invoke-AsteriaIdentityRegistrationRollback {
    param(
        [Parameter(Mandatory = $true)][bool]$RestorePrevious,
        [string]$RollbackPackagePath,
        [string]$RollbackExternalLocation,
        [Parameter(Mandatory = $true)][string]$PackageName,
        [Parameter(Mandatory = $true)][scriptblock]$AddOperation,
        [Parameter(Mandatory = $true)][scriptblock]$RemoveOperation,
        [Parameter(Mandatory = $true)][scriptblock]$GetOperation,
        [Parameter(Mandatory = $true)][scriptblock]$GetRootOperation
    )

    $failures = [Collections.Generic.List[Exception]]::new()
    try {
        $partial = & $GetOperation $PackageName
        if ($partial) {
            & $RemoveOperation $partial
        }
    } catch {
        $failures.Add($_.Exception)
    }

    if ($RestorePrevious) {
        try {
            & $AddOperation $RollbackPackagePath $RollbackExternalLocation
            $restored = & $GetOperation $PackageName
            $restoredRoot = if ($restored) { & $GetRootOperation $restored } else { $null }
            if (-not $restored -or
                -not $restoredRoot -or
                -not (Test-AsteriaPackageRoot `
                    -Expected $RollbackExternalLocation `
                    -Actual $restoredRoot)) {
                throw [InvalidOperationException]::new(
                    "Rollback did not restore the former external location.")
            }
        } catch {
            $failures.Add($_.Exception)
        }
    }

    return $failures.ToArray()
}

function Invoke-AsteriaIdentityRegistrationUpdate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$ExternalLocation,
        [string]$RollbackPackagePath,
        [string]$RollbackExternalLocation,
        [string]$PackageName = "AsteriaScale",
        [scriptblock]$AddOperation,
        [scriptblock]$RemoveOperation,
        [scriptblock]$GetOperation,
        [scriptblock]$GetRootOperation
    )

    if (-not $AddOperation) {
        $AddOperation = {
            param($package, $location)
            Add-AppxPackage `
                -Path $package `
                -ExternalLocation $location `
                -ForceUpdateFromAnyVersion `
                -ForceApplicationShutdown `
                -RetainFilesOnFailure `
                -ErrorAction Stop
        }
    }
    if (-not $RemoveOperation) {
        $RemoveOperation = {
            param($package)
            if ($package) {
                $package | Remove-AppxPackage -ErrorAction Stop
            }
        }
    }
    if (-not $GetOperation) {
        $GetOperation = {
            param($name)
            Get-AppxPackage -Name $name -ErrorAction SilentlyContinue |
                Sort-Object Version -Descending |
                Select-Object -First 1
        }
    }
    if (-not $GetRootOperation) {
        $GetRootOperation = {
            param($package)
            Get-AsteriaRegisteredPackageRoot -Package $package
        }
    }

    $existing = & $GetOperation $PackageName
    $hasRollback =
        -not [string]::IsNullOrWhiteSpace($RollbackPackagePath) -and
        (Test-Path -LiteralPath $RollbackPackagePath -PathType Leaf) -and
        -not [string]::IsNullOrWhiteSpace($RollbackExternalLocation)

    $updateFailure = $null
    try {
        & $AddOperation $PackagePath $ExternalLocation
    } catch {
        $updateFailure = $_
    }

    if (-not $updateFailure) {
        try {
            $registered = & $GetOperation $PackageName
            $registeredRoot = if ($registered) { & $GetRootOperation $registered } else { $null }
            if (-not $registered -or
                -not $registeredRoot -or
                -not (Test-AsteriaPackageRoot -Expected $ExternalLocation -Actual $registeredRoot)) {
                throw [InvalidOperationException]::new(
                    "The identity package update completed without registering the expected external location.")
            }
            return "updated"
        } catch {
            $validationFailure = $_.Exception
        }

        # Add-AppxPackage returned success, so a later observation failure may
        # already have replaced the registration. It is not safe to classify
        # this as an untouched Add failure; remove the partial state and restore
        # the captured package before the outer install restores its binaries.
        if ($existing -and -not $hasRollback) {
            throw [InvalidOperationException]::new(
                "The identity update completed but could not be validated. " +
                "No verified rollback package is available, so the observed registration was retained for recovery.",
                $validationFailure)
        }

        $rollbackFailures = @(Invoke-AsteriaIdentityRegistrationRollback `
            -RestorePrevious:([bool]$existing) `
            -RollbackPackagePath $RollbackPackagePath `
            -RollbackExternalLocation $RollbackExternalLocation `
            -PackageName $PackageName `
            -AddOperation $AddOperation `
            -RemoveOperation $RemoveOperation `
            -GetOperation $GetOperation `
            -GetRootOperation $GetRootOperation)
        if ($rollbackFailures.Count -gt 0) {
            $failures = [Collections.Generic.List[Exception]]::new()
            $failures.Add($validationFailure)
            foreach ($failure in $rollbackFailures) {
                $failures.Add($failure)
            }
            throw [AggregateException]::new(
                "The identity update could not be validated and rollback was incomplete.",
                $failures)
        }

        $message = if ($existing) {
            "The identity update could not be validated; the previous registration was restored."
        } else {
            "The identity update could not be validated; the partial registration was removed."
        }
        throw [InvalidOperationException]::new($message, $validationFailure)
    }

    $sameVersionConflict = Test-AsteriaSameVersionRegistrationError `
        -ErrorRecord $updateFailure
    if (-not $existing -or -not $sameVersionConflict) {
        throw [InvalidOperationException]::new(
            "Identity package update failed; the previous registration was left untouched.",
            $updateFailure.Exception)
    }
    if (-not $hasRollback) {
        throw [InvalidOperationException]::new(
            "The exact-version identity package cannot be replaced safely because no verified rollback package is available. " +
            "The previous registration was left untouched.",
            $updateFailure.Exception)
    }

    # Sparse packages cannot update an exact same version. Only this documented
    # deployment error enters the controlled replace path, and only after a
    # signed rollback package plus its former ExternalLocation were captured.
    & $RemoveOperation $existing
    try {
        & $AddOperation $PackagePath $ExternalLocation
        $registered = & $GetOperation $PackageName
        $registeredRoot = if ($registered) { & $GetRootOperation $registered } else { $null }
        if (-not $registered -or
            -not $registeredRoot -or
            -not (Test-AsteriaPackageRoot -Expected $ExternalLocation -Actual $registeredRoot)) {
            throw [InvalidOperationException]::new(
                "The replacement identity did not bind to the expected external location.")
        }
        return "replaced"
    } catch {
        $replacementFailure = $_.Exception
        $rollbackFailures = @(Invoke-AsteriaIdentityRegistrationRollback `
            -RestorePrevious:$true `
            -RollbackPackagePath $RollbackPackagePath `
            -RollbackExternalLocation $RollbackExternalLocation `
            -PackageName $PackageName `
            -AddOperation $AddOperation `
            -RemoveOperation $RemoveOperation `
            -GetOperation $GetOperation `
            -GetRootOperation $GetRootOperation)

        if ($rollbackFailures.Count -gt 0) {
            $failures = [Collections.Generic.List[Exception]]::new()
            $failures.Add($replacementFailure)
            foreach ($failure in $rollbackFailures) {
                $failures.Add($failure)
            }
            throw [AggregateException]::new(
                "The new identity registration failed and rollback was incomplete.",
                $failures)
        }

        throw [InvalidOperationException]::new(
            "The new identity registration failed; the previous registration was restored.",
            $replacementFailure)
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
    $outputManifest = Get-AsteriaOutputManifestPath -Output $output
    $requiredFiles = @(Get-AsteriaRequiredDistFiles -Root $Root -Output $output) + @(
        $revisionMarker,
        $fingerprintMarker,
        $outputManifest
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

    if (-not (Test-AsteriaOutputManifest -Output $output)) {
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
