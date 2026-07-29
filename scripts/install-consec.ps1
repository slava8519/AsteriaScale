[CmdletBinding()]
param(
    [switch]$AcceptUpstreamTerms,
    [string]$SourceBundle = (Join-Path $env:TEMP 'asteria-consec-onnx-foundation-20260729-v5'),
    [string]$InstallRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'consec-install-state.ps1')

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path $PSScriptRoot '..\src\AsteriaScale.RankerWorker\Models\Consec'
}

$repositoryUrl = 'https://github.com/SapienzaNLP/consec'
$license = 'CC-BY-NC-SA-4.0'
if (-not $AcceptUpstreamTerms) {
    Write-Host ''
    Write-Host 'ConSeC предназначен здесь только для личного некоммерческого исследования.' -ForegroundColor Yellow
    Write-Host "Репозиторий: $repositoryUrl"
    Write-Host "Лицензия опубликованных весов: $license"
    Write-Host 'Коммерческое использование этой сборки запрещено.'
    Write-Host ''
    $answer = Read-Host 'Введите ПРИНИМАЮ, если вы принимаете условия'
    if ($null -eq $answer -or
        -not [string]::Equals(
            $answer.Trim(),
            'ПРИНИМАЮ',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Условия ConSeC не приняты. Ничего не установлено.'
    }
}

$resolvedSource = [IO.Path]::GetFullPath($SourceBundle)
& (Join-Path $PSScriptRoot 'verify-consec.ps1') -InstallRoot $resolvedSource

$verifyScriptPath = Join-Path $PSScriptRoot 'verify-consec.ps1'
$verifyBundle = {
    param($path)
    & $verifyScriptPath -InstallRoot $path
}.GetNewClosure()
Publish-ConsecBundleTransactional `
    -SourceBundle $resolvedSource `
    -InstallRoot $InstallRoot `
    -VerifyBundle $verifyBundle

$resolvedInstall = [IO.Path]::GetFullPath($InstallRoot)
Write-Host "ConSeC установлен локально: $resolvedInstall" -ForegroundColor Green
Write-Host 'Пересоберите Asteria Scale, чтобы пакет попал в следующий локальный билд.'
