Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$runtimeDir = Join-Path $repoRoot "private\runtime"
$pidFile = Join-Path $runtimeDir "dashboard-dev-api.pid"

Push-Location $repoRoot
try {
  $docker = Get-Command docker -ErrorAction SilentlyContinue
  if ($null -ne $docker) {
    docker compose stop api worker search-index-worker mail-worker outlook-worker automation-worker governance-worker runtime-control-worker callback-worker event-scheduler outbox-relay | Out-Host
  }

  if (Test-Path $pidFile) {
    $apiProcessId = [int](Get-Content -Raw $pidFile)
    $process = Get-Process -Id $apiProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
      Stop-Process -Id $apiProcessId -Force
    }
    Remove-Item -LiteralPath $pidFile -Force
    Write-Host "Stopped local dashboard API process $apiProcessId"
  } else {
    Write-Host "No local dashboard PID file found."
  }
} finally {
  Pop-Location
}
