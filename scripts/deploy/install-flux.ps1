param(
    [string]$InstallRoot = "D:\FluxLLMKB",
    [string]$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [int]$ApiPort = 8765,
    [int]$PostgresPort = 5432,
    [int]$HostAgentPort = 8799,
    [switch]$SkipDashboardBuild,
    [switch]$SkipScheduledTasks
)

$ErrorActionPreference = "Stop"

function New-FluxDirectory {
    param([string]$Path)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Get-FluxGitSha {
    param([string]$Root)
    try {
        $sha = (& git -C $Root rev-parse --short HEAD 2>$null).Trim()
        if ($sha) { return $sha }
    } catch {}
    return "local"
}

function Write-FluxCompose {
    param(
        [string]$TargetPath,
        [string]$ImageTag,
        [int]$ApiPort,
        [int]$PostgresPort
    )
    $compose = @"
services:
  api:
    image: flux-llm-kb-api:`${FLUX_KB_IMAGE_TAG}
    container_name: flux-llm-kb-api
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    environment:
      FLUX_KB_API_HOST: 0.0.0.0
      FLUX_KB_API_PORT: "8765"
      FLUX_KB_APP_ROOT: /app
    ports:
      - "127.0.0.1:${ApiPort}:8765"
    volumes:
      - ../private:/app/private
      - ../logs:/app/logs
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m uvicorn flux_llm_kb.rest_api:create_app --factory --host 0.0.0.0 --port 8765"

  worker:
    image: flux-llm-kb-worker:`${FLUX_KB_IMAGE_TAG}
    container_name: flux-llm-kb-worker
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    volumes:
      - ../private:/app/private
      - ../logs:/app/logs
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli crawl worker run --exclude-host-agent-roots --interval 5 --limit 10"

  postgres:
    image: pgvector/pgvector:pg16
    container_name: flux-llm-kb-postgres
    restart: unless-stopped
    environment:
      POSTGRES_USER: flux
      POSTGRES_PASSWORD: flux
      POSTGRES_DB: flux_llm_kb
    ports:
      - "127.0.0.1:${PostgresPort}:5432"
    volumes:
      - ../data/postgres:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U flux -d flux_llm_kb"]
      interval: 5s
      timeout: 5s
      retries: 20
"@
    Set-Content -Path $TargetPath -Value $compose -Encoding UTF8
}

function Write-FluxEnv {
    param(
        [string]$TargetPath,
        [string]$InstallRoot,
        [string]$ImageTag,
        [int]$PostgresPort
    )
    $databaseUrl = "postgresql://flux:flux@postgres:5432/flux_llm_kb"
    $envText = @"
FLUX_KB_DATABASE_URL=$databaseUrl
FLUX_KB_INSTALL_ROOT=$InstallRoot
FLUX_KB_APP_ROOT=$InstallRoot\app
FLUX_KB_PRIVATE_DIR=$InstallRoot\private
FLUX_KB_DATA_DIR=$InstallRoot\data
FLUX_KB_LOG_DIR=$InstallRoot\logs
FLUX_KB_IMAGE_TAG=$ImageTag
FLUX_KB_HOST_DATABASE_URL=postgresql://flux:flux@127.0.0.1:${PostgresPort}/flux_llm_kb
"@
    if (-not (Test-Path $TargetPath)) {
        Set-Content -Path $TargetPath -Value $envText -Encoding UTF8
        return
    }
    $existing = Get-Content -Raw -Path $TargetPath
    foreach ($line in $envText -split "`r?`n") {
        if (-not $line.Trim()) { continue }
        $key = ($line -split "=", 2)[0]
        if ($existing -notmatch "(?m)^$([regex]::Escape($key))=") {
            Add-Content -Path $TargetPath -Value $line -Encoding UTF8
        }
    }
}

function Write-FluxHostScripts {
    param([string]$AppRoot, [string]$InstallRoot, [int]$HostAgentPort, [int]$PostgresPort)
    $hostAgent = @"
`$ErrorActionPreference = "Stop"
`$env:FLUX_KB_DATABASE_URL = "postgresql://flux:flux@127.0.0.1:${PostgresPort}/flux_llm_kb"
`$env:FLUX_KB_INSTALL_ROOT = "$InstallRoot"
`$env:FLUX_KB_APP_ROOT = "$AppRoot"
`$env:FLUX_KB_PRIVATE_DIR = "$InstallRoot\private"
`$env:FLUX_KB_DATA_DIR = "$InstallRoot\data"
`$env:FLUX_KB_LOG_DIR = "$InstallRoot\logs"
& "$AppRoot\.venv\Scripts\python.exe" -m flux_llm_kb.cli host-agent run --host 127.0.0.1 --port $HostAgentPort
"@
    Set-Content -Path (Join-Path $AppRoot "run-host-agent.ps1") -Value $hostAgent -Encoding UTF8

    $outlookHost = @"
`$ErrorActionPreference = "Stop"
`$env:FLUX_KB_DATABASE_URL = "postgresql://flux:flux@127.0.0.1:${PostgresPort}/flux_llm_kb"
`$env:FLUX_KB_INSTALL_ROOT = "$InstallRoot"
`$env:FLUX_KB_APP_ROOT = "$AppRoot"
`$env:FLUX_KB_PRIVATE_DIR = "$InstallRoot\private"
`$env:FLUX_KB_LOG_DIR = "$InstallRoot\logs"
& "$AppRoot\.venv\Scripts\python.exe" -m flux_llm_kb.cli outlook-host run
"@
    Set-Content -Path (Join-Path $AppRoot "run-outlook-host.ps1") -Value $outlookHost -Encoding UTF8
}

function Register-FluxTask {
    param([string]$TaskName, [string]$ScriptPath)
    $action = New-ScheduledTaskAction -Execute "pwsh.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Description "Flux-LLM-KB local host process" -Force | Out-Null
}

$appRoot = Join-Path $InstallRoot "app"
$privateRoot = Join-Path $InstallRoot "private"
$dataRoot = Join-Path $InstallRoot "data"
$logsRoot = Join-Path $InstallRoot "logs"
$runtimeRoot = Join-Path $InstallRoot "runtime"
$backupRoot = Join-Path $InstallRoot "backups"

foreach ($dir in @($InstallRoot, $appRoot, $privateRoot, $dataRoot, (Join-Path $dataRoot "postgres"), $logsRoot, $runtimeRoot, $backupRoot)) {
    New-FluxDirectory $dir
}

$imageTag = Get-FluxGitSha -Root $SourceRoot
if (-not $SkipDashboardBuild) {
    npm --prefix (Join-Path $SourceRoot "dashboard") run build
}

docker build -t "flux-llm-kb-api:$imageTag" -t "flux-llm-kb-api:local" $SourceRoot
docker tag "flux-llm-kb-api:$imageTag" "flux-llm-kb-worker:$imageTag"
docker tag "flux-llm-kb-api:$imageTag" "flux-llm-kb-worker:local"

$composePath = Join-Path $appRoot "docker-compose.yml"
$envPath = Join-Path $privateRoot "flux.env"
$appEnvPath = Join-Path $appRoot ".env"
Write-FluxCompose -TargetPath $composePath -ImageTag $imageTag -ApiPort $ApiPort -PostgresPort $PostgresPort
Write-FluxEnv -TargetPath $envPath -InstallRoot $InstallRoot -ImageTag $imageTag -PostgresPort $PostgresPort
Set-Content -Path $appEnvPath -Value "FLUX_KB_IMAGE_TAG=$imageTag`n" -Encoding UTF8
Set-Content -Path (Join-Path $appRoot "VERSION") -Value $imageTag -Encoding UTF8

$venvPython = Join-Path $appRoot ".venv\Scripts\python.exe"
if (-not (Test-Path $venvPython)) {
    python -m venv (Join-Path $appRoot ".venv")
}
& $venvPython -m pip install --upgrade pip
& $venvPython -m pip install "$SourceRoot[api,corpus,mail]"
Write-FluxHostScripts -AppRoot $appRoot -InstallRoot $InstallRoot -HostAgentPort $HostAgentPort -PostgresPort $PostgresPort

Push-Location $appRoot
try {
    docker compose --env-file $appEnvPath -f $composePath up -d --no-build postgres api worker
} finally {
    Pop-Location
}

if (-not $SkipScheduledTasks) {
    Register-FluxTask -TaskName "FluxKB Host Agent" -ScriptPath (Join-Path $appRoot "run-host-agent.ps1")
    Register-FluxTask -TaskName "FluxKB Outlook Host" -ScriptPath (Join-Path $appRoot "run-outlook-host.ps1")
}

Write-Host "Flux production runtime installed at $InstallRoot"
Write-Host "Dashboard: http://127.0.0.1:$ApiPort/dashboard"
