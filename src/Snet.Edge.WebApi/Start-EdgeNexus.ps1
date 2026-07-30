[CmdletBinding()]
param(
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$gatewayUrl = 'http://127.0.0.1:5088'
$healthUrl = "$gatewayUrl/health/ready"
$gatewayPath = Join-Path $PSScriptRoot 'Snet.Edge.WebApi.exe'

if (-not (Test-Path -LiteralPath $gatewayPath -PathType Leaf)) {
    throw "Gateway executable not found: $gatewayPath"
}

function Test-GatewayReady {
    try {
        $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2
        return $response.status -eq 'ready'
    }
    catch {
        return $false
    }
}

if (-not (Test-GatewayReady)) {
    $gatewayProcess = Start-Process `
        -FilePath $gatewayPath `
        -ArgumentList '--urls', $gatewayUrl `
        -WorkingDirectory $PSScriptRoot `
        -WindowStyle Hidden `
        -PassThru

    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if (Test-GatewayReady) {
            $ready = $true
            break
        }

        if ($gatewayProcess.HasExited) {
            throw "EdgeNexus gateway exited during startup with code $($gatewayProcess.ExitCode)."
        }

        Start-Sleep -Milliseconds 250
    }

    if (-not $ready) {
        throw 'EdgeNexus gateway startup timed out. Check data\logs for details.'
    }
}

if (-not $NoBrowser) {
    Start-Process $gatewayUrl
}
