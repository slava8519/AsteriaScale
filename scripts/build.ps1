[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
$mainProject = Join-Path $root "src\AsteriaScale\AsteriaScale.csproj"
$rankerProject = Join-Path $root "src\AsteriaScale.RankerWorker\AsteriaScale.RankerWorker.csproj"
$distDirectory = [IO.Path]::GetFullPath((Join-Path $root "dist")).TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath((Join-Path $root "dist\AsteriaScale")).TrimEnd('\', '/')
$expectedOutput = [IO.Path]::GetFullPath((Join-Path $root "dist\AsteriaScale")).TrimEnd('\', '/')
$identitySource = Join-Path $root "packaging\Identity"
$identityPackage = Join-Path $root "dist\AsteriaScale.Identity.msix"

if (-not [string]::Equals($output, $expectedOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean an unexpected output path: $output"
}

$workspacePrefix = [IO.Path]::GetFullPath($root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean an output path outside the workspace: $output"
}

if (Test-Path -LiteralPath $distDirectory) {
    $distItem = Get-Item -LiteralPath $distDirectory -Force
    if (($distItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to clean through a reparse-point dist directory: $distDirectory"
    }
}

if (Test-Path -LiteralPath $output) {
    $outputItem = Get-Item -LiteralPath $output -Force
    if (-not $outputItem.PSIsContainer) {
        throw "Refusing to recursively clean a non-directory output path: $output"
    }
    if (($outputItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to recursively clean a reparse-point output directory: $output"
    }

    $resolvedOutput = $outputItem.FullName.TrimEnd('\', '/')
    if (-not [string]::Equals($resolvedOutput, $expectedOutput, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to recursively clean an unexpected resolved path: $resolvedOutput"
    }

    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = $dotnetCommand.Source
if (-not $dotnetPath -and (Test-Path "C:\Program Files\dotnet\dotnet.exe")) {
    $dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
}
if (-not $dotnetPath) {
    throw ".NET SDK was not found. Install Microsoft.DotNet.SDK.10 with winget."
}

& $dotnetPath publish $mainProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableMsixTooling=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "Main application build failed with exit code $LASTEXITCODE."
}

$rankerOutput = Join-Path $output "Ranker"
& $dotnetPath publish $rankerProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $rankerOutput

if ($LASTEXITCODE -ne 0) {
    throw "Ranker worker build failed with exit code $LASTEXITCODE."
}

$requiredRankerFiles = @(
    (Join-Path $rankerOutput "AsteriaScale.RankerWorker.exe"),
    (Join-Path $rankerOutput "Models\GlossBert\tokenizer.json"),
    (Join-Path $rankerOutput "Models\GlossBert\Cpu\glossbert-fp32-dynamic.onnx"),
    (Join-Path $rankerOutput "Models\GlossBert\Npu\glossbert-eager-vitis-context.onnx"),
    (Join-Path $rankerOutput "Models\GlossBert\Npu\glossbert-eager-vitis-context.onnx.compatibility.txt"),
    (Join-Path $rankerOutput "Models\GlossBert\Npu\glossbert-eager-vitis-context.onnx_VITISAI.bin")
)
foreach ($requiredFile in $requiredRankerFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Ranker package is incomplete. Missing: $requiredFile"
    }
}

$sdkTools = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools\10.0.26100.8249\bin\10.0.26100.0\x64"
$makeAppx = Join-Path $sdkTools "makeappx.exe"
$signTool = Join-Path $sdkTools "signtool.exe"
if (-not (Test-Path $makeAppx) -or -not (Test-Path $signTool)) {
    throw "Windows SDK packaging tools were not restored. Run dotnet restore first."
}

& $makeAppx pack /d $identitySource /p $identityPackage /o /nv
if ($LASTEXITCODE -ne 0) {
    throw "Identity package creation failed with exit code $LASTEXITCODE."
}

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object Subject -EQ "CN=Asteria Scale Local" |
    Where-Object HasPrivateKey |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $certificate) {
    throw "Signing certificate 'CN=Asteria Scale Local' with a private key was not found."
}

& $signTool sign /sha1 $certificate.Thumbprint /fd SHA256 $identityPackage
if ($LASTEXITCODE -ne 0) {
    throw "Identity package signing failed with exit code $LASTEXITCODE."
}

Write-Host "Ready: $(Join-Path $output 'AsteriaScale.exe')"
Write-Host "Ranker: $(Join-Path $rankerOutput 'AsteriaScale.RankerWorker.exe')"
Write-Host "Identity: $identityPackage"
