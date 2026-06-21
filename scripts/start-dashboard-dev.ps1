Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$runtimeDir = Join-Path $repoRoot "private\runtime"
$pidFile = Join-Path $runtimeDir "dashboard-dev-api.pid"
$logFile = Join-Path $runtimeDir "dashboard-dev-api.log"
$errFile = Join-Path $runtimeDir "dashboard-dev-api.err.log"
$url = "http://127.0.0.1:8765/dashboard"

New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null

Push-Location $repoRoot
try {
  npm --prefix dashboard run build

  $docker = Get-Command docker -ErrorAction SilentlyContinue
  if ($null -ne $docker) {
    docker compose up -d --build postgres api worker
    if ($LASTEXITCODE -eq 0) {
      Write-Host "Dashboard deployment refreshed at $url"
      return
    }
    Write-Warning "Docker dashboard refresh failed; falling back to a local FastAPI dashboard process."
  } else {
    Write-Host "Docker was not found on PATH; starting a local FastAPI dashboard process instead."
  }

  if (Test-Path $pidFile) {
    $existingPid = [int](Get-Content -Raw $pidFile)
    $existing = Get-Process -Id $existingPid -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
      Stop-Process -Id $existingPid -Force
    }
    Remove-Item -LiteralPath $pidFile -Force
  }

  $python = $env:FLUX_KB_PYTHON
  if ([string]::IsNullOrWhiteSpace($python)) {
    $bundled = Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
    if (Test-Path $bundled) {
      $python = $bundled
    } else {
      $python = "python"
    }
  }

  $env:PYTHONPATH = (Resolve-Path "src").Path
  $arguments = "-m uvicorn flux_llm_kb.rest_api:create_app --factory --host 127.0.0.1 --port 8765"
  $process = Start-Process -FilePath $python -ArgumentList $arguments -WorkingDirectory $repoRoot -WindowStyle Hidden -RedirectStandardOutput $logFile -RedirectStandardError $errFile -PassThru
  Set-Content -Path $pidFile -Value $process.Id
  Write-Host "Dashboard deployment refreshed at $url"
  Write-Host "Local API PID: $($process.Id)"
} finally {
  Pop-Location
}
