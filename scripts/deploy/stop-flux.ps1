param(
    [string]$InstallRoot = "D:\FluxLLMKB",
    [switch]$StopHostTasks
)

$ErrorActionPreference = "Stop"
$appRoot = Join-Path $InstallRoot "app"
$composePath = Join-Path $appRoot "docker-compose.yml"
$appEnvPath = Join-Path $appRoot ".env"

if ($StopHostTasks) {
    foreach ($task in @("FluxKB Host Agent", "FluxKB Outlook Host")) {
        if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
            Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
        }
    }
}

if (Test-Path $composePath) {
    Push-Location $appRoot
    try {
        docker compose --env-file $appEnvPath -f $composePath stop
    } finally {
        Pop-Location
    }
}

Write-Host "Flux production runtime stopped at $InstallRoot"
