param(
    [string]$InstallRoot = "D:\FluxLLMKB",
    [string]$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [int]$ApiPort = 8765,
    [int]$PostgresPort = 5432,
    [int]$HostAgentPort = 8799,
    [string]$PythonExe = "",
    [ValidateSet("auto", "on", "off")]
    [string]$GpuMode = "auto",
    [switch]$SkipDashboardBuild,
    [switch]$SkipScheduledTasks,
    [int]$DockerBuildTimeoutSeconds = 1200,
    [ValidateSet("auto", "local", "python")]
    [string]$DockerBaseMode = "auto",
    [string]$DockerBaseImage = $env:FLUX_KB_DOCKER_BASE_IMAGE,
    [int]$PipInstallTimeoutSeconds = 900,
    [int]$PipTimeoutSeconds = 30,
    [int]$PipRetries = 2,
    [string]$PipIndexUrl = $env:FLUX_KB_PIP_INDEX_URL,
    [string]$AptDebianMirrorUrl = $env:FLUX_KB_APT_DEBIAN_MIRROR_URL,
    [string]$AptSecurityMirrorUrl = $env:FLUX_KB_APT_SECURITY_MIRROR_URL
)

$ErrorActionPreference = "Stop"

function New-FluxDirectory {
    param([string]$Path)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Set-FluxEnvValue {
    param([string]$TargetPath, [string]$Key, [string]$Value)
    $line = "$Key=$Value"
    if (-not (Test-Path $TargetPath)) {
        Set-Content -Path $TargetPath -Value "$line`n" -Encoding UTF8
        return
    }
    $existing = Get-Content -Raw -Path $TargetPath
    if ($existing -match "(?m)^$([regex]::Escape($Key))=") {
        $updated = [regex]::Replace($existing, "(?m)^$([regex]::Escape($Key))=.*$", $line)
        Set-Content -Path $TargetPath -Value $updated -Encoding UTF8
    } else {
        Add-Content -Path $TargetPath -Value $line -Encoding UTF8
    }
}

function Get-FluxGitSha {
    param([string]$Root)
    try {
        $sha = (& git -C $Root rev-parse --short HEAD 2>$null).Trim()
        if ($sha) { return $sha }
    } catch {}
    return "local"
}

function Resolve-FluxPythonExe {
    param([string]$InstallRoot, [string]$RequestedPython)
    if ($RequestedPython) { return $RequestedPython }
    $installedPython = Join-Path $InstallRoot "python\python.exe"
    if (Test-Path $installedPython) { return $installedPython }
    return "python"
}

function Remove-FluxGpuComposeAccess {
    param([string]$ComposeText)
    $filtered = foreach ($line in ($ComposeText -split "`r?`n")) {
        if ($line -match "^\s+gpus: all\s*$") { continue }
        if ($line -match "^\s+NVIDIA_VISIBLE_DEVICES: all\s*$") { continue }
        if ($line -match "^\s+NVIDIA_DRIVER_CAPABILITIES: compute,utility\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_ASR_DEVICE: cuda\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_ASR_COMPUTE_TYPE: float16\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_LOCAL_INFERENCE_ENABLED: `"true`"\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_LOCAL_INFERENCE_BASE_URL: http://host\.docker\.internal:11434\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_VISION_ENABLED: `"true`"\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_VISION_MODEL: qwen2\.5vl:7b\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_VISION_MAX_IMAGE_PIXELS: `"80000000`"\s*$") { continue }
        $line
    }
    return ($filtered -join "`n")
}

function Assert-FluxGpuAvailable {
    param(
        [string]$ImageTag,
        [ValidateSet("auto", "on", "off")]
        [string]$GpuMode
    )
    if ($GpuMode -eq "off") {
        Write-Host "Flux GPU mode is off; Docker GPU access will not be requested."
        return $false
    }
    $image = "flux-llm-kb-api:$ImageTag"
    docker run --rm --gpus all --entrypoint nvidia-smi $image | Out-Host
    if ($LASTEXITCODE -eq 0) {
        return $true
    }
    $message = "Flux GPU mode '$GpuMode' could not run nvidia-smi through Docker GPU passthrough for image $image."
    if ($GpuMode -eq "on") {
        throw $message
    }
    Write-Warning $message
    return $false
}

function Write-FluxCompose {
    param(
        [string]$TargetPath,
        [string]$ImageTag,
        [int]$ApiPort,
        [int]$PostgresPort,
        [bool]$GpuEnabled
    )
    $compose = @"
services:
  api:
    image: flux-llm-kb-api:`${FLUX_KB_IMAGE_TAG}
    container_name: flux-llm-kb-api
    restart: unless-stopped
    gpus: all
    depends_on:
      postgres:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility
      FLUX_KB_API_HOST: 0.0.0.0
      FLUX_KB_API_PORT: "8765"
      FLUX_KB_INSTALL_ROOT: /app
      FLUX_KB_APP_ROOT: /app
      FLUX_KB_PRIVATE_DIR: /app/private
      FLUX_KB_LOG_DIR: /app/logs
      FLUX_KB_ASR_DEVICE: cuda
      FLUX_KB_ASR_COMPUTE_TYPE: float16
      FLUX_KB_LOCAL_INFERENCE_ENABLED: "true"
      FLUX_KB_LOCAL_INFERENCE_BASE_URL: http://host.docker.internal:11434
      FLUX_KB_VISION_ENABLED: "true"
      FLUX_KB_VISION_MODEL: qwen2.5vl:7b
      FLUX_KB_VISION_MAX_IMAGE_PIXELS: "80000000"
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
    gpus: all
    depends_on:
      postgres:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility
      FLUX_KB_INSTALL_ROOT: /app
      FLUX_KB_APP_ROOT: /app
      FLUX_KB_PRIVATE_DIR: /app/private
      FLUX_KB_LOG_DIR: /app/logs
      FLUX_KB_ASR_DEVICE: cuda
      FLUX_KB_ASR_COMPUTE_TYPE: float16
      FLUX_KB_LOCAL_INFERENCE_ENABLED: "true"
      FLUX_KB_LOCAL_INFERENCE_BASE_URL: http://host.docker.internal:11434
      FLUX_KB_VISION_ENABLED: "true"
      FLUX_KB_VISION_MODEL: qwen2.5vl:7b
      FLUX_KB_VISION_MAX_IMAGE_PIXELS: "80000000"
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
    command: >
      postgres
      -c shared_buffers=1GB
      -c effective_cache_size=12GB
      -c work_mem=32MB
      -c maintenance_work_mem=512MB
      -c effective_io_concurrency=200
      -c random_page_cost=1.1
      -c max_parallel_workers_per_gather=4
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
    if (-not $GpuEnabled) {
        $compose = Remove-FluxGpuComposeAccess -ComposeText $compose
    }
    Set-Content -Path $TargetPath -Value $compose -Encoding UTF8
}

function Write-FluxEnv {
    param(
        [string]$TargetPath,
        [string]$InstallRoot,
        [string]$ImageTag,
        [int]$PostgresPort,
        [bool]$GpuEnabled
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
    if ($GpuEnabled) {
        $envText += "`n" + @"
FLUX_KB_ASR_DEVICE=cuda
FLUX_KB_ASR_COMPUTE_TYPE=float16
FLUX_KB_LOCAL_INFERENCE_ENABLED=true
FLUX_KB_LOCAL_INFERENCE_BASE_URL=http://host.docker.internal:11434
FLUX_KB_VISION_ENABLED=true
FLUX_KB_VISION_MODEL=qwen2.5vl:7b
FLUX_KB_VISION_MAX_IMAGE_PIXELS=80000000
"@
    } else {
        $envText += "`n" + @"
FLUX_KB_ASR_DEVICE=auto
FLUX_KB_ASR_COMPUTE_TYPE=default
FLUX_KB_LOCAL_INFERENCE_ENABLED=false
FLUX_KB_LOCAL_INFERENCE_BASE_URL=http://127.0.0.1:11434
FLUX_KB_VISION_ENABLED=false
FLUX_KB_VISION_MODEL=
FLUX_KB_VISION_MAX_IMAGE_PIXELS=4096000
"@
    }
    if (-not (Test-Path $TargetPath)) {
        Set-Content -Path $TargetPath -Value $envText -Encoding UTF8
        return
    }
    foreach ($line in $envText -split "`r?`n") {
        if (-not $line.Trim()) { continue }
        $parts = $line -split "=", 2
        Set-FluxEnvValue -TargetPath $TargetPath -Key $parts[0] -Value $parts[1]
    }
}

function Write-FluxHostScripts {
    param([string]$AppRoot, [string]$InstallRoot, [int]$HostAgentPort, [int]$PostgresPort)
    $hostAgent = @"
import os
import runpy
import sys
import traceback
from pathlib import Path

os.environ["FLUX_KB_DATABASE_URL"] = "postgresql://flux:flux@127.0.0.1:${PostgresPort}/flux_llm_kb"
os.environ["FLUX_KB_INSTALL_ROOT"] = r"$InstallRoot"
os.environ["FLUX_KB_APP_ROOT"] = r"$AppRoot"
os.environ["FLUX_KB_PRIVATE_DIR"] = r"$InstallRoot\private"
os.environ["FLUX_KB_DATA_DIR"] = r"$InstallRoot\data"
os.environ["FLUX_KB_LOG_DIR"] = r"$InstallRoot\logs"

log_dir = Path(os.environ["FLUX_KB_LOG_DIR"])
log_dir.mkdir(parents=True, exist_ok=True)
sys.stdout = open(log_dir / "host-agent.log", "a", encoding="utf-8", buffering=1)
sys.stderr = open(log_dir / "host-agent.err.log", "a", encoding="utf-8", buffering=1)
os.chdir(os.environ["FLUX_KB_APP_ROOT"])
sys.argv = ["flux-kb", "host-agent", "run", "--host", "127.0.0.1", "--port", "$HostAgentPort"]

try:
    runpy.run_module("flux_llm_kb.cli", run_name="__main__")
except SystemExit:
    raise
except Exception:
    traceback.print_exc()
    raise
"@
    Set-Content -Path (Join-Path $AppRoot "run-host-agent.pyw") -Value $hostAgent -Encoding UTF8

    $outlookHost = @"
import os
import runpy
import sys
import traceback
from pathlib import Path

os.environ["FLUX_KB_DATABASE_URL"] = "postgresql://flux:flux@127.0.0.1:${PostgresPort}/flux_llm_kb"
os.environ["FLUX_KB_INSTALL_ROOT"] = r"$InstallRoot"
os.environ["FLUX_KB_APP_ROOT"] = r"$AppRoot"
os.environ["FLUX_KB_PRIVATE_DIR"] = r"$InstallRoot\private"
os.environ["FLUX_KB_LOG_DIR"] = r"$InstallRoot\logs"

log_dir = Path(os.environ["FLUX_KB_LOG_DIR"])
log_dir.mkdir(parents=True, exist_ok=True)
sys.stdout = open(log_dir / "outlook-host.log", "a", encoding="utf-8", buffering=1)
sys.stderr = open(log_dir / "outlook-host.err.log", "a", encoding="utf-8", buffering=1)
os.chdir(os.environ["FLUX_KB_APP_ROOT"])
sys.argv = ["flux-kb", "outlook-host", "run"]

try:
    runpy.run_module("flux_llm_kb.cli", run_name="__main__")
except SystemExit:
    raise
except Exception:
    traceback.print_exc()
    raise
"@
    Set-Content -Path (Join-Path $AppRoot "run-outlook-host.pyw") -Value $outlookHost -Encoding UTF8
}

function Remove-FluxLegacyConsoleLaunchers {
    param([string]$AppRoot)
    foreach ($legacyLauncher in @("run-host-agent.ps1", "run-outlook-host.ps1")) {
        $legacyPath = Join-Path $AppRoot $legacyLauncher
        if (Test-Path $legacyPath) {
            Remove-Item -LiteralPath $legacyPath -Force
        }
    }
}

function Resolve-FluxPythonwExe {
    param([string]$AppRoot)
    $pythonw = Join-Path $AppRoot ".venv\Scripts\pythonw.exe"
    if (Test-Path $pythonw) { return $pythonw }
    throw "Missing windowless Python launcher: $pythonw"
}

function New-FluxHostTaskTriggers {
    $logonTrigger = New-ScheduledTaskTrigger -AtLogOn
    $watchdogTrigger = New-ScheduledTaskTrigger -Once -At ([DateTime]::Now.AddMinutes(1)) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)
    return @($logonTrigger, $watchdogTrigger)
}

function Register-FluxTask {
    param([string]$TaskName, [string]$LauncherPath, [string]$AppRoot)
    $pythonw = Resolve-FluxPythonwExe -AppRoot $AppRoot
    $action = New-ScheduledTaskAction -Execute $pythonw -Argument "`"$LauncherPath`""
    $triggers = New-FluxHostTaskTriggers
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable -MultipleInstances IgnoreNew -Hidden
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $triggers -Settings $settings -Description "Flux-LLM-KB local host process" -Force | Out-Null
}

function Invoke-FluxMigration {
    param([string]$VenvPython, [string]$InstallRoot, [int]$PostgresPort)
    $previousDatabaseUrl = $env:FLUX_KB_DATABASE_URL
    $previousInstallRoot = $env:FLUX_KB_INSTALL_ROOT
    $previousAppRoot = $env:FLUX_KB_APP_ROOT
    $previousPrivateDir = $env:FLUX_KB_PRIVATE_DIR
    $previousDataDir = $env:FLUX_KB_DATA_DIR
    $previousLogDir = $env:FLUX_KB_LOG_DIR
    try {
        $env:FLUX_KB_DATABASE_URL = "postgresql://flux:flux@127.0.0.1:${PostgresPort}/flux_llm_kb"
        $env:FLUX_KB_INSTALL_ROOT = $InstallRoot
        $env:FLUX_KB_APP_ROOT = Join-Path $InstallRoot "app"
        $env:FLUX_KB_PRIVATE_DIR = Join-Path $InstallRoot "private"
        $env:FLUX_KB_DATA_DIR = Join-Path $InstallRoot "data"
        $env:FLUX_KB_LOG_DIR = Join-Path $InstallRoot "logs"
        & $VenvPython -m flux_llm_kb.cli migrate
    } finally {
        $env:FLUX_KB_DATABASE_URL = $previousDatabaseUrl
        $env:FLUX_KB_INSTALL_ROOT = $previousInstallRoot
        $env:FLUX_KB_APP_ROOT = $previousAppRoot
        $env:FLUX_KB_PRIVATE_DIR = $previousPrivateDir
        $env:FLUX_KB_DATA_DIR = $previousDataDir
        $env:FLUX_KB_LOG_DIR = $previousLogDir
    }
}

function Invoke-FluxCodexPluginInstall {
    param([string]$VenvPython, [string]$InstallRoot)
    $previousAppRoot = $env:FLUX_KB_APP_ROOT
    $previousInstallRoot = $env:FLUX_KB_INSTALL_ROOT
    try {
        $env:FLUX_KB_INSTALL_ROOT = $InstallRoot
        $env:FLUX_KB_APP_ROOT = Join-Path $InstallRoot "app"
        & $VenvPython -m flux_llm_kb.cli codex install-plugin
    } finally {
        $env:FLUX_KB_APP_ROOT = $previousAppRoot
        $env:FLUX_KB_INSTALL_ROOT = $previousInstallRoot
    }
}

function ConvertTo-FluxCommandArgument {
    param([string]$Value)
    if ($Value -match '[\s"]') {
        return '"' + ($Value -replace '"', '\"') + '"'
    }
    return $Value
}

function Get-FluxTaskResult {
    param($Task)
    if ($null -eq $Task) { return "" }
    if ($Task.Wait(5000)) { return $Task.Result }
    return "[log stream did not close within 5 seconds]"
}

function Write-FluxProcessOutput {
    param([string]$Stdout, [string]$Stderr)
    if ($Stdout) { $Stdout | Out-Host }
    if ($Stderr) { $Stderr | Out-Host }
}

function Stop-FluxProcessTree {
    param([int]$ProcessId)
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue)
    foreach ($child in $children) {
        Stop-FluxProcessTree -ProcessId ([int]$child.ProcessId)
    }
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
}

function Invoke-FluxNativeCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds,
        [string]$StepName
    )
    $label = if ($StepName) { $StepName } else { $FilePath }
    $argumentText = ($Arguments | ForEach-Object { ConvertTo-FluxCommandArgument $_ }) -join " "
    Write-Host "Running ${label}: $FilePath $argumentText"

    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $FilePath
    $processInfo.Arguments = $argumentText
    $processInfo.WorkingDirectory = $WorkingDirectory
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    if ($TimeoutSeconds -gt 0 -and -not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Warning "$label did not exit within $TimeoutSeconds seconds; stopping process tree."
        Stop-FluxProcessTree -ProcessId $process.Id
        $process.WaitForExit(5000) | Out-Null
        Write-FluxProcessOutput -Stdout (Get-FluxTaskResult -Task $stdoutTask) -Stderr (Get-FluxTaskResult -Task $stderrTask)
        throw "$label timed out after $TimeoutSeconds seconds."
    }

    $process.WaitForExit()
    Write-FluxProcessOutput -Stdout (Get-FluxTaskResult -Task $stdoutTask) -Stderr (Get-FluxTaskResult -Task $stderrTask)
    if ($process.ExitCode -ne 0) {
        throw "$label failed with exit code $($process.ExitCode)."
    }
}

function Test-FluxDockerImageExists {
    param([string]$Image)
    if (-not $Image) { return $false }
    docker image inspect $Image *> $null
    return $LASTEXITCODE -eq 0
}

function Resolve-FluxDockerBuildBase {
    param(
        [ValidateSet("auto", "local", "python")]
        [string]$DockerBaseMode,
        [string]$DockerBaseImage
    )
    $pythonBase = "python:3.12-slim"
    if ($DockerBaseMode -eq "python") {
        return [pscustomobject]@{ Image = $pythonBase; SkipSystemPackages = $false }
    }

    $candidateImage = if ($DockerBaseImage) { $DockerBaseImage } else { "flux-llm-kb-api:local" }
    $candidateExists = Test-FluxDockerImageExists -Image $candidateImage
    if ($DockerBaseMode -eq "local" -and -not $candidateExists) {
        throw "Docker base mode 'local' requires an existing image: $candidateImage"
    }
    if ($candidateExists) {
        Write-Host "Reusing Docker base image $candidateImage; Debian system packages will not be reinstalled."
        return [pscustomobject]@{ Image = $candidateImage; SkipSystemPackages = $true }
    }
    if ($DockerBaseImage) {
        Write-Warning "Requested Docker base image $DockerBaseImage was not found; falling back to $pythonBase."
    }
    return [pscustomobject]@{ Image = $pythonBase; SkipSystemPackages = $false }
}

$appRoot = Join-Path $InstallRoot "app"
$privateRoot = Join-Path $InstallRoot "private"
$dataRoot = Join-Path $InstallRoot "data"
$logsRoot = Join-Path $InstallRoot "logs"
$runtimeRoot = Join-Path $InstallRoot "runtime"
$backupRoot = Join-Path $InstallRoot "backups"

# Installation is intentionally non-destructive for live runtime state. These
# calls create missing directories but never remove private data, database
# files, mail spools, logs, runtime status, or backups.
foreach ($dir in @($InstallRoot, $appRoot, $privateRoot, $dataRoot, (Join-Path $dataRoot "postgres"), $logsRoot, $runtimeRoot, $backupRoot)) {
    New-FluxDirectory $dir
}

$imageTag = Get-FluxGitSha -Root $SourceRoot
$resolvedPython = Resolve-FluxPythonExe -InstallRoot $InstallRoot -RequestedPython $PythonExe
if (-not $SkipDashboardBuild) {
    npm --prefix (Join-Path $SourceRoot "dashboard") run build
}

$dockerBase = Resolve-FluxDockerBuildBase -DockerBaseMode $DockerBaseMode -DockerBaseImage $DockerBaseImage
$skipSystemPackages = if ($dockerBase.SkipSystemPackages) { "true" } else { "false" }
$dockerBuildArgs = @(
    "build",
    "--progress=plain",
    "--build-arg", "FLUX_KB_DOCKER_BASE_IMAGE=$($dockerBase.Image)",
    "--build-arg", "FLUX_KB_SKIP_SYSTEM_PACKAGES=$skipSystemPackages",
    "--build-arg", "PIP_DEFAULT_TIMEOUT=$PipTimeoutSeconds",
    "--build-arg", "PIP_RETRIES=$PipRetries"
)
if ($AptDebianMirrorUrl) {
    $dockerBuildArgs += @("--build-arg", "APT_DEBIAN_MIRROR_URL=$AptDebianMirrorUrl")
}
if ($AptSecurityMirrorUrl) {
    $dockerBuildArgs += @("--build-arg", "APT_SECURITY_MIRROR_URL=$AptSecurityMirrorUrl")
}
if ($PipIndexUrl) {
    $dockerBuildArgs += @("--build-arg", "PIP_INDEX_URL=$PipIndexUrl")
}
$dockerBuildArgs += @("-t", "flux-llm-kb-api:$imageTag", "-t", "flux-llm-kb-api:local", $SourceRoot)
Invoke-FluxNativeCommand -FilePath "docker" -Arguments $dockerBuildArgs -WorkingDirectory $SourceRoot -TimeoutSeconds $DockerBuildTimeoutSeconds -StepName "docker build"
Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("tag", "flux-llm-kb-api:$imageTag", "flux-llm-kb-worker:$imageTag") -WorkingDirectory $SourceRoot -TimeoutSeconds 60 -StepName "docker tag worker version"
Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("tag", "flux-llm-kb-api:$imageTag", "flux-llm-kb-worker:local") -WorkingDirectory $SourceRoot -TimeoutSeconds 60 -StepName "docker tag worker local"

$gpuEnabled = Assert-FluxGpuAvailable -ImageTag $imageTag -GpuMode $GpuMode

$pluginSource = Join-Path $SourceRoot "plugins"
$pluginTarget = Join-Path $appRoot "plugins"
if (Test-Path $pluginSource) {
    if (Test-Path $pluginTarget) { Remove-Item -LiteralPath $pluginTarget -Recurse -Force }
    Copy-Item -Path $pluginSource -Destination $pluginTarget -Recurse -Force
}

$composePath = Join-Path $appRoot "docker-compose.yml"
$envPath = Join-Path $privateRoot "flux.env"
$appEnvPath = Join-Path $appRoot ".env"
Write-FluxCompose -TargetPath $composePath -ImageTag $imageTag -ApiPort $ApiPort -PostgresPort $PostgresPort -GpuEnabled $gpuEnabled
Write-FluxEnv -TargetPath $envPath -InstallRoot $InstallRoot -ImageTag $imageTag -PostgresPort $PostgresPort -GpuEnabled $gpuEnabled
Set-Content -Path $appEnvPath -Value "FLUX_KB_IMAGE_TAG=$imageTag`n" -Encoding UTF8
Set-Content -Path (Join-Path $appRoot "VERSION") -Value $imageTag -Encoding UTF8

$venvPython = Join-Path $appRoot ".venv\Scripts\python.exe"
if (-not (Test-Path $venvPython)) {
    & $resolvedPython -m venv (Join-Path $appRoot ".venv")
}
$pipCommonArgs = @("--timeout", ([string]$PipTimeoutSeconds), "--retries", ([string]$PipRetries))
$pipIndexArgs = @()
if ($PipIndexUrl) {
    $pipIndexArgs += @("--index-url", $PipIndexUrl)
}
Invoke-FluxNativeCommand -FilePath $venvPython -Arguments (@("-m", "pip", "install") + $pipCommonArgs + $pipIndexArgs + @("--upgrade", "pip")) -WorkingDirectory $SourceRoot -TimeoutSeconds $PipInstallTimeoutSeconds -StepName "pip upgrade"
Invoke-FluxNativeCommand -FilePath $venvPython -Arguments (@("-m", "pip", "install") + $pipCommonArgs + $pipIndexArgs + @("$SourceRoot[api,corpus,mail,mcp,processors]")) -WorkingDirectory $SourceRoot -TimeoutSeconds $PipInstallTimeoutSeconds -StepName "pip install production extras"
Invoke-FluxCodexPluginInstall -VenvPython $venvPython -InstallRoot $InstallRoot
Write-FluxHostScripts -AppRoot $appRoot -InstallRoot $InstallRoot -HostAgentPort $HostAgentPort -PostgresPort $PostgresPort
Remove-FluxLegacyConsoleLaunchers -AppRoot $appRoot

Push-Location $appRoot
try {
    docker compose --env-file $appEnvPath -f $composePath up -d --no-build postgres api worker
} finally {
    Pop-Location
}
Invoke-FluxMigration -VenvPython $venvPython -InstallRoot $InstallRoot -PostgresPort $PostgresPort

if (-not $SkipScheduledTasks) {
    Register-FluxTask -TaskName "FluxKB Host Agent" -LauncherPath (Join-Path $appRoot "run-host-agent.pyw") -AppRoot $appRoot
    Register-FluxTask -TaskName "FluxKB Outlook Host" -LauncherPath (Join-Path $appRoot "run-outlook-host.pyw") -AppRoot $appRoot
}

Write-Host "Flux production runtime installed at $InstallRoot"
Write-Host "Dashboard: http://127.0.0.1:$ApiPort/dashboard"
