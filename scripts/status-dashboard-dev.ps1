Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$runtimeDir = Join-Path $repoRoot "private\runtime"
$pidFile = Join-Path $runtimeDir "dashboard-dev-api.pid"
$url = "http://127.0.0.1:8765/dashboard"

Push-Location $repoRoot
try {
  $docker = Get-Command docker -ErrorAction SilentlyContinue
  if ($null -ne $docker) {
    docker compose ps api worker search-index-worker mail-worker outlook-worker automation-worker governance-worker runtime-control-worker gpu-eviction-worker callback-worker event-audit-worker event-dashboard-worker event-diagnostics-worker event-scheduler outbox-relay rabbitmq postgres | Out-Host
  } else {
    Write-Host "Docker command not found on PATH."
  }

  if (Test-Path $pidFile) {
    $apiProcessId = [int](Get-Content -Raw $pidFile)
    $process = Get-Process -Id $apiProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
      Write-Host "Local dashboard API running from dashboard-dev-api.pid: $apiProcessId"
    } else {
      Write-Host "dashboard-dev-api.pid exists, but process $apiProcessId is not running."
    }
  } else {
    Write-Host "No local dashboard API PID file."
  }

  Write-Host "Dashboard URL: $url"
} finally {
  Pop-Location
}
