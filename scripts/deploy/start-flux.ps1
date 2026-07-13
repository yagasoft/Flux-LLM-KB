param(
    [string]$InstallRoot = $(if ($env:FLUX_KB_INSTALL_ROOT) { $env:FLUX_KB_INSTALL_ROOT } else { "J:\FluxLLMKB" })
)

$ErrorActionPreference = "Stop"
$appRoot = Join-Path $InstallRoot "app"
$composePath = Join-Path $appRoot "docker-compose.yml"
$appEnvPath = Join-Path $appRoot ".env"

if (-not (Test-Path $composePath)) {
    throw "Flux production runtime is not installed at $InstallRoot."
}

Push-Location $appRoot
try {
    docker compose --env-file $appEnvPath -f $composePath up -d --no-build postgres api worker
} finally {
    Pop-Location
}

foreach ($task in @("FluxKB Host Agent", "FluxKB Outlook Host")) {
    if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
        Start-ScheduledTask -TaskName $task
    }
}

Write-Host "Flux production runtime started from $InstallRoot"
