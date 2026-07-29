[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'AsteriaScale\Models\TranslateGemma')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedRoot = [IO.Path]::GetFullPath($InstallRoot)
$runtimeRoot = Join-Path $resolvedRoot 'Runtime'
$verificationRoot = Join-Path $runtimeRoot ('verification-' + [guid]::NewGuid().ToString('N'))
$serverProcess = $null

try {
    New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
    $archivePath = Join-Path $resolvedRoot 'llama-b9632-bin-win-cpu-x64.zip'
    $modelPath = Join-Path $resolvedRoot 'translategemma-4b-it.Q4_K_M.gguf'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $verificationRoot

    $serverExecutables = @(
        Get-ChildItem -LiteralPath $verificationRoot -Recurse -Filter 'llama-server.exe'
    )
    if ($serverExecutables.Count -ne 1) {
        throw 'The archive must contain exactly one llama-server.exe.'
    }

    $portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $portProbe.Start()
    $port = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
    $portProbe.Stop()

    $apiKey = [guid]::NewGuid().ToString('N')
    $stdoutPath = Join-Path $verificationRoot 'server.out.log'
    $stderrPath = Join-Path $verificationRoot 'server.err.log'
    $serverArguments = @(
        '--model', ('"' + $modelPath + '"'),
        '--host', '127.0.0.1',
        '--port', $port,
        '--ctx-size', '2048',
        '--n-gpu-layers', '0',
        '--parallel', '1',
        '--api-key', $apiKey,
        '--no-jinja',
        '--no-webui'
    )
    $serverProcess = Start-Process `
        -FilePath $serverExecutables[0].FullName `
        -ArgumentList $serverArguments `
        -WorkingDirectory $serverExecutables[0].DirectoryName `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru

    $headers = @{ Authorization = 'Bearer ' + $apiKey }
    $healthy = $false
    for ($attempt = 0; $attempt -lt 900; $attempt++) {
        if ($serverProcess.HasExited) {
            $tail = (Get-Content -LiteralPath $stderrPath -Tail 25) -join [Environment]::NewLine
            throw "llama-server exited with code $($serverProcess.ExitCode).`n$tail"
        }

        try {
            $health = Invoke-WebRequest `
                -Uri "http://127.0.0.1:$port/health" `
                -Headers $headers `
                -UseBasicParsing `
                -TimeoutSec 2
            if ($health.StatusCode -eq 200) {
                $healthy = $true
                break
            }
        }
        catch {
            # The socket appears only after the GGUF has been loaded.
        }

        Start-Sleep -Milliseconds 200
    }

    if (-not $healthy) {
        throw 'llama-server did not become ready.'
    }

    Write-Host 'MODEL_HEALTH=OK' -ForegroundColor Green
    $instruction =
        'You are a professional English (en) to Russian (ru) translator. ' +
        'Your goal is to accurately convey the meaning and nuances of the original English text ' +
        'while adhering to Russian grammar, vocabulary, and cultural sensitivities.' + "`n" +
        'Produce only the Russian translation, without any additional explanations or commentary. ' +
        'Please translate the following English text into Russian:'
    # These tuples mirror SmartPhraseIdiomRewriter's production output. The
    # direct prompt stays official: only ModelInput is placed in its source slot.
    $probes = @(
        [pscustomobject][ordered]@{
            Source = "You're trying to butter me up."
            ModelInput = "You're flattering me to get what you want."
            Transform = 'idiom:butter-object-up@v4'
        },
        [pscustomobject][ordered]@{
            Source = 'This is the part that always feels off.'
            ModelInput = 'This is the part that always feels off.'
            Transform = 'none'
        },
        [pscustomobject][ordered]@{
            Source = "This illness isn't normal."
            ModelInput = "This illness isn't normal."
            Transform = 'none'
        },
        [pscustomobject][ordered]@{
            Source = 'Are you talking about these magnificent 88-wide hips?'
            ModelInput = 'Are you talking about these magnificent 88-wide hips?'
            Transform = 'none'
        }
    )

    foreach ($probe in $probes) {
        Write-Host ''
        Write-Host "SOURCE: $($probe.Source)"
        Write-Host "MODEL_INPUT: $($probe.ModelInput)"
        Write-Host "TRANSFORM: $($probe.Transform)"
        $prompt =
            "<start_of_turn>user`n" + $instruction + "`n`n`n" + $probe.ModelInput +
            "<end_of_turn>`n<start_of_turn>model`n"
        $payload = [ordered]@{
            prompt = $prompt
            n_predict = 192
            temperature = 0.0
            top_k = 1
            top_p = 1.0
            min_p = 0.0
            seed = 0
            stream = $false
            cache_prompt = $true
            stop = @('<end_of_turn>')
        }
        $json = $payload | ConvertTo-Json -Compress
        $answer = Invoke-RestMethod `
            -Uri "http://127.0.0.1:$port/completion" `
            -Method Post `
            -Headers $headers `
            -ContentType 'application/json; charset=utf-8' `
            -Body ([Text.Encoding]::UTF8.GetBytes($json)) `
            -TimeoutSec 120
        Write-Host "RESULT: $($answer.content.Trim())"
    }
}
finally {
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        $serverProcess.WaitForExit(5000) | Out-Null
    }

    $ownedPrefix = [IO.Path]::GetFullPath($runtimeRoot).TrimEnd('\') + '\'
    $ownedCandidate = [IO.Path]::GetFullPath($verificationRoot)
    if ($ownedCandidate.StartsWith($ownedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $ownedCandidate)) {
        Remove-Item -LiteralPath $ownedCandidate -Recurse -Force
    }
}
