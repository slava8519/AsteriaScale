[CmdletBinding()]
param(
    [switch]$AcceptGemmaTerms,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'AsteriaScale\Models\TranslateGemma')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http

$termsUrl = 'https://ai.google.dev/gemma/terms'
$policyUrl = 'https://ai.google.dev/gemma/prohibited_use_policy'
if (-not $AcceptGemmaTerms) {
    Write-Host ''
    Write-Host 'TranslateGemma распространяется по Gemma Terms, а не по лицензии Asteria.' -ForegroundColor Yellow
    Write-Host "Условия: $termsUrl"
    Write-Host "Ограничения использования: $policyUrl"
    Write-Host 'Модель занимает около 2,5 ГБ. Она будет храниться только в профиле текущего пользователя.'
    Write-Host ''
    $answer = Read-Host 'Введите ПРИНИМАЮ, если вы прочитали и принимаете эти условия'
    if ($null -eq $answer -or
        -not [string]::Equals($answer.Trim(), 'ПРИНИМАЮ', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Условия Gemma не приняты. Ничего не загружено.'
    }
}

$cpuRunner = [ordered]@{
    Name = 'llama-b9632-bin-win-cpu-x64.zip'
    Url = 'https://github.com/ggml-org/llama.cpp/releases/download/b9632/llama-b9632-bin-win-cpu-x64.zip'
    Size = 16899258L
    Sha256 = 'b835d5c5155dd2a5ed748a0351debf2ede0dc9f808757e0429f8700a11832dcd'
}
$vulkanRunner = [ordered]@{
    Name = 'llama-b9632-bin-win-vulkan-x64.zip'
    Url = 'https://github.com/ggml-org/llama.cpp/releases/download/b9632/llama-b9632-bin-win-vulkan-x64.zip'
    Size = 38545553L
    Sha256 = '90b914ff48167370190477ff3f54fd2c4eb034a305b943913eaa310eda78793d'
}
$model = [ordered]@{
    Name = 'translategemma-4b-it.Q4_K_M.gguf'
    Url = 'https://huggingface.co/mradermacher/translategemma-4b-it-GGUF/resolve/35a7486e128b19642cdc72d7b91b21ba388aaf42/translategemma-4b-it.Q4_K_M.gguf'
    Size = 2489909760L
    Sha256 = '81200d03e843d2ec1ece6eeafe7d13cb6e5211e1fcd336ade55790b683a08330'
}
$license = [ordered]@{
    Name = 'GEMMA_TERMS.txt'
    Url = 'https://registry.ollama.ai/v2/library/translategemma/blobs/sha256:3e2c24001f9ef57bf7ec959a3658fbb49cdad113cdf394c264da9d16f9bdd132'
    Size = 8431L
    Sha256 = '3e2c24001f9ef57bf7ec959a3658fbb49cdad113cdf394c264da9d16f9bdd132'
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

    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne $Size) {
        return $false
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    return [string]::Equals($actual, $Sha256, [StringComparison]::OrdinalIgnoreCase)
}

function Receive-PinnedFile {
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Artifact,
        [Parameter(Mandatory)][string]$Destination
    )

    if (Test-PinnedFile -Path $Destination -Size $Artifact.Size -Sha256 $Artifact.Sha256) {
        Write-Host "Уже проверено: $($Artifact.Name)"
        return
    }

    $temporary = $Destination + '.download'
    [long]$existingLength = 0
    if (Test-Path -LiteralPath $temporary -PathType Leaf) {
        $existingLength = (Get-Item -LiteralPath $temporary).Length
        if ($existingLength -eq $Artifact.Size) {
            if (Test-PinnedFile -Path $temporary -Size $Artifact.Size -Sha256 $Artifact.Sha256) {
                Move-Item -LiteralPath $temporary -Destination $Destination -Force
                Write-Host "Проверено после прерванной загрузки: $($Artifact.Name)"
                return
            }

            Remove-Item -LiteralPath $temporary -Force
            $existingLength = 0
        }
        elseif ($existingLength -gt $Artifact.Size) {
            Remove-Item -LiteralPath $temporary -Force
            $existingLength = 0
        }
    }

    if ($existingLength -gt 0) {
        Write-Host "Возобновление $($Artifact.Name) с $([Math]::Round($existingLength / 1MB, 1)) МБ..."
    }
    else {
        Write-Host "Загрузка $($Artifact.Name) ($([Math]::Round($Artifact.Size / 1GB, 2)) ГБ)..."
    }

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($null -ne $curl) {
        $curlArguments = @(
            '--fail',
            '--location',
            '--retry', '3',
            '--retry-delay', '2',
            '--connect-timeout', '30',
            '--max-time', '21600',
            '--show-error',
            '--progress-bar',
            '--proto', '=https',
            '--output', $temporary
        )
        if ($existingLength -gt 0) {
            $curlArguments += @('--continue-at', '-')
        }
        $curlArguments += $Artifact.Url

        & $curl.Source @curlArguments
        if ($LASTEXITCODE -ne 0) {
            throw "curl не смог загрузить $($Artifact.Name), код $LASTEXITCODE. Повторный запуск продолжит загрузку."
        }

        if (-not (Test-PinnedFile -Path $temporary -Size $Artifact.Size -Sha256 $Artifact.Sha256)) {
            if ((Get-Item -LiteralPath $temporary).Length -ge $Artifact.Size) {
                Remove-Item -LiteralPath $temporary -Force
            }
            throw "Проверка размера/SHA-256 не пройдена: $($Artifact.Name)"
        }

        Move-Item -LiteralPath $temporary -Destination $Destination -Force
        return
    }

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Get,
        $Artifact.Url)
    if ($existingLength -gt 0) {
        $request.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::Parse(
            "bytes=$existingLength-")
    }

    $response = $Client.SendAsync(
        $request,
        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        $response.EnsureSuccessStatusCode()
        $append = $existingLength -gt 0 -and
            $response.StatusCode -eq [System.Net.HttpStatusCode]::PartialContent
        if ($append) {
            $contentRange = $response.Content.Headers.ContentRange
            if ($null -eq $contentRange -or
                -not $contentRange.From.HasValue -or
                $contentRange.From.Value -ne $existingLength -or
                ($contentRange.Length.HasValue -and
                 $contentRange.Length.Value -ne $Artifact.Size)) {
                throw "Сервер вернул неверный диапазон для $($Artifact.Name)."
            }
        }
        else {
            $existingLength = 0
        }

        $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        try {
            $output = [IO.File]::Open(
                $temporary,
                $(if ($append) { [IO.FileMode]::Append } else { [IO.FileMode]::Create }),
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $buffer = [byte[]]::new(4MB)
                [long]$received = $existingLength
                while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $output.Write($buffer, 0, $read)
                    $received += $read
                    $percent = if ($Artifact.Size -gt 0) {
                        [Math]::Min(100, [int](100 * $received / $Artifact.Size))
                    } else { 0 }
                    Write-Progress -Activity "Загрузка $($Artifact.Name)" -PercentComplete $percent
                }
            }
            finally {
                $output.Dispose()
                Write-Progress -Activity "Загрузка $($Artifact.Name)" -Completed
            }
        }
        finally {
            $input.Dispose()
        }
    }
    finally {
        $response.Dispose()
        $request.Dispose()
    }

    if (-not (Test-PinnedFile -Path $temporary -Size $Artifact.Size -Sha256 $Artifact.Sha256)) {
        if ((Get-Item -LiteralPath $temporary).Length -ge $Artifact.Size) {
            Remove-Item -LiteralPath $temporary -Force
        }
        throw "Проверка размера/SHA-256 не пройдена: $($Artifact.Name)"
    }

    Move-Item -LiteralPath $temporary -Destination $Destination -Force
}

$resolvedRoot = [IO.Path]::GetFullPath($InstallRoot)
New-Item -ItemType Directory -Path $resolvedRoot -Force | Out-Null

$handler = $null
$client = $null
try {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromHours(6)

    foreach ($artifact in @($license, $cpuRunner, $vulkanRunner, $model)) {
        Receive-PinnedFile `
            -Client $client `
            -Artifact $artifact `
            -Destination (Join-Path $resolvedRoot $artifact.Name)
    }

    # The former Ollama blob omits mandatory Gemma 3 metadata and cannot be
    # loaded by the pinned upstream llama.cpp runtime. Reclaim it only when it
    # is byte-for-byte the known obsolete artifact; never remove a user file.
    $legacyModel = Join-Path $resolvedRoot 'translategemma-4b-it-q4_K_M.gguf'
    if (Test-PinnedFile `
            -Path $legacyModel `
            -Size 3298866368L `
            -Sha256 'bdbf939b402e2f88fbe3e918beb777813009335756b4c17be7fe008dfe4815d4') {
        Remove-Item -LiteralPath $legacyModel -Force
        Write-Host 'Удалён несовместимый прежний Ollama-GGUF (3,3 ГБ).'
    }

    $manifest = [ordered]@{
        schemaVersion = 3
        acceptedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        termsUrl = $termsUrl
        prohibitedUsePolicyUrl = $policyUrl
        model = $model
        runners = [ordered]@{
            cpu = $cpuRunner
            vulkan = $vulkanRunner
        }
        license = $license
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content `
        -LiteralPath (Join-Path $resolvedRoot 'install-manifest.json') `
        -Encoding UTF8
}
finally {
    if ($null -ne $client) {
        $client.Dispose()
    }
    if ($null -ne $handler) {
        $handler.Dispose()
    }
}

Write-Host ''
Write-Host 'TranslateGemma 4B установлена и проверена.' -ForegroundColor Green
Write-Host 'Установлены оба закреплённых runtime: CPU и Vulkan GPU.'
Write-Host 'В Asteria выберите: Умный словарь → Перевод фразы второй строкой → TranslateGemma 4B,'
Write-Host 'затем явно выберите ускоритель CPU или Vulkan GPU.'
