param(
    [string]$InstallRoot = "D:\FluxLLMKB",
    [string]$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [int]$HostAgentPort = 8799,
    [int]$PostgresPort = 5432,
    [string]$PythonExe = "",
    [switch]$SkipDashboardBuild,
    [switch]$RecreateVenv,
    [switch]$RestartHostTasks,
    [int]$DockerComposeTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"

$appRoot = Join-Path $InstallRoot "app"
$composePath = Join-Path $appRoot "docker-compose.yml"
$appEnvPath = Join-Path $appRoot ".env"
$privateEnvPath = Join-Path $InstallRoot "private\flux.env"
$venvPython = Join-Path $appRoot ".venv\Scripts\python.exe"
$venvRoot = Join-Path $appRoot ".venv"

function Resolve-FluxPythonExe {
    param([string]$InstallRoot, [string]$RequestedPython)
    if ($RequestedPython) { return $RequestedPython }
    $installedPython = Join-Path $InstallRoot "python\python.exe"
    if (Test-Path $installedPython) { return $installedPython }
    return "python"
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

function Register-FluxTask {
    param([string]$TaskName, [string]$LauncherPath, [string]$AppRoot)
    $pythonw = Resolve-FluxPythonwExe -AppRoot $AppRoot
    $action = New-ScheduledTaskAction -Execute $pythonw -Argument "`"$LauncherPath`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -Hidden
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Description "Flux-LLM-KB local host process" -Force | Out-Null
}

function Wait-FluxTaskStopped {
    param([string]$TaskName, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if (-not $task -or $task.State -ne "Running") { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for scheduled task $TaskName to stop"
}

function Test-FluxTcpOpen {
    param([int]$Port)
    $client = $null
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $async = $client.BeginConnect("127.0.0.1", $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne(500, $false)) { return $false }
        $client.EndConnect($async)
        return $true
    } catch {
        return $false
    } finally {
        if ($client) { $client.Close() }
    }
}

function Wait-FluxTcpClosed {
    param([int]$Port, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (-not (Test-FluxTcpOpen -Port $Port)) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for 127.0.0.1:$Port to close"
}

function Wait-FluxTcpOpen {
    param([int]$Port, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (Test-FluxTcpOpen -Port $Port) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for 127.0.0.1:$Port to open"
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

function Get-FluxContainerStatus {
    param([string]$ContainerName)
    $status = docker inspect --format "{{.State.Status}}" $ContainerName 2>$null
    if ($LASTEXITCODE -ne 0) { return "" }
    return ($status | Select-Object -First 1).Trim()
}

function Start-FluxCreatedContainers {
    param([string[]]$ContainerNames)
    foreach ($containerName in $ContainerNames) {
        $status = Get-FluxContainerStatus -ContainerName $containerName
        if ($status -eq "created") {
            Write-Warning "Container $containerName was left in Created state; starting it directly."
            docker start $containerName | Out-Host
        }
    }
}

function Wait-FluxContainersRunning {
    param([string[]]$ContainerNames, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $notRunning = @()
        foreach ($containerName in $ContainerNames) {
            $status = Get-FluxContainerStatus -ContainerName $containerName
            if ($status -ne "running") {
                $notRunning += "$containerName=$status"
            }
        }
        if ($notRunning.Count -eq 0) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    docker ps -a --filter "name=flux-llm-kb" | Out-Host
    foreach ($containerName in $ContainerNames) {
        Write-Warning "Last logs for $containerName"
        docker logs --tail 80 $containerName 2>&1 | Out-Host
    }
    throw "Timed out waiting for Flux Docker containers to run: $($notRunning -join ', ')"
}

function Invoke-FluxDockerComposeUp {
    param(
        [string]$AppRoot,
        [string]$AppEnvPath,
        [string]$ComposePath,
        [int]$TimeoutSeconds
    )
    $containers = @("flux-llm-kb-postgres", "flux-llm-kb-api", "flux-llm-kb-worker")
    $recoverableContainers = @("flux-llm-kb-api", "flux-llm-kb-worker")
    $arguments = @(
        "compose",
        "--env-file", $AppEnvPath,
        "-f", $ComposePath,
        "up", "-d", "--no-build",
        "postgres", "api", "worker"
    )
    $argumentText = ($arguments | ForEach-Object { ConvertTo-FluxCommandArgument $_ }) -join " "
    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = "docker"
    $processInfo.Arguments = $argumentText
    $processInfo.WorkingDirectory = $AppRoot
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Warning "docker compose up did not exit within $TimeoutSeconds seconds; stopping compose process tree and checking container state."
        Stop-FluxProcessTree -ProcessId $process.Id
        $process.WaitForExit(5000) | Out-Null
        Write-FluxProcessOutput -Stdout (Get-FluxTaskResult -Task $stdoutTask) -Stderr (Get-FluxTaskResult -Task $stderrTask)
        Start-FluxCreatedContainers -ContainerNames $recoverableContainers
        Wait-FluxContainersRunning -ContainerNames $containers
        return
    }

    $process.WaitForExit()
    Write-FluxProcessOutput -Stdout (Get-FluxTaskResult -Task $stdoutTask) -Stderr (Get-FluxTaskResult -Task $stderrTask)
    if ($process.ExitCode -ne 0) {
        Start-FluxCreatedContainers -ContainerNames $recoverableContainers
        try {
            Wait-FluxContainersRunning -ContainerNames $containers
            Write-Warning "docker compose up exited with $($process.ExitCode), but required Flux containers are running after recovery."
            return
        } catch {
            throw "docker compose up failed with exit code $($process.ExitCode)."
        }
    }
    Start-FluxCreatedContainers -ContainerNames $recoverableContainers
    Wait-FluxContainersRunning -ContainerNames $containers
}

if (-not (Test-Path $composePath)) {
    throw "Flux production runtime is not installed at $InstallRoot. Run install-flux.ps1 first."
}

try {
    $imageTag = (& git -C $SourceRoot rev-parse --short HEAD 2>$null).Trim()
    if (-not $imageTag) { $imageTag = "local" }
} catch {
    $imageTag = "local"
}

if (-not $SkipDashboardBuild) {
    npm --prefix (Join-Path $SourceRoot "dashboard") run build
}

$resolvedPython = Resolve-FluxPythonExe -InstallRoot $InstallRoot -RequestedPython $PythonExe

docker build -t "flux-llm-kb-api:$imageTag" -t "flux-llm-kb-api:local" $SourceRoot
docker tag "flux-llm-kb-api:$imageTag" "flux-llm-kb-worker:$imageTag"
docker tag "flux-llm-kb-api:$imageTag" "flux-llm-kb-worker:local"

$pluginSource = Join-Path $SourceRoot "plugins"
$pluginTarget = Join-Path $appRoot "plugins"
if (Test-Path $pluginSource) {
    if (Test-Path $pluginTarget) { Remove-Item -LiteralPath $pluginTarget -Recurse -Force }
    Copy-Item -Path $pluginSource -Destination $pluginTarget -Recurse -Force
}

Set-Content -Path $appEnvPath -Value "FLUX_KB_IMAGE_TAG=$imageTag`n" -Encoding UTF8
Set-Content -Path (Join-Path $appRoot "VERSION") -Value $imageTag -Encoding UTF8
if (Test-Path $privateEnvPath) {
    $envText = Get-Content -Raw -Path $privateEnvPath
    if ($envText -match "(?m)^FLUX_KB_IMAGE_TAG=") {
        $envText = [regex]::Replace($envText, "(?m)^FLUX_KB_IMAGE_TAG=.*$", "FLUX_KB_IMAGE_TAG=$imageTag")
        Set-Content -Path $privateEnvPath -Value $envText -Encoding UTF8
    } else {
        Add-Content -Path $privateEnvPath -Value "FLUX_KB_IMAGE_TAG=$imageTag" -Encoding UTF8
    }
}

if ($RecreateVenv -and (Test-Path $venvRoot)) {
    Remove-Item -LiteralPath $venvRoot -Recurse -Force
}
if (-not (Test-Path $venvPython)) {
    & $resolvedPython -m venv $venvRoot
}
& $venvPython -m pip install --upgrade pip
& $venvPython -m pip install "$SourceRoot[api,corpus,mail,mcp]"
Invoke-FluxCodexPluginInstall -VenvPython $venvPython -InstallRoot $InstallRoot
Write-FluxHostScripts -AppRoot $appRoot -InstallRoot $InstallRoot -HostAgentPort $HostAgentPort -PostgresPort $PostgresPort
Remove-FluxLegacyConsoleLaunchers -AppRoot $appRoot

Push-Location $appRoot
try {
    Invoke-FluxDockerComposeUp -AppRoot $appRoot -AppEnvPath $appEnvPath -ComposePath $composePath -TimeoutSeconds $DockerComposeTimeoutSeconds
} finally {
    Pop-Location
}
Invoke-FluxMigration -VenvPython $venvPython -InstallRoot $InstallRoot -PostgresPort $PostgresPort

foreach ($taskSpec in @(
    @{ Name = "FluxKB Host Agent"; Launcher = (Join-Path $appRoot "run-host-agent.pyw"); Port = $HostAgentPort },
    @{ Name = "FluxKB Outlook Host"; Launcher = (Join-Path $appRoot "run-outlook-host.pyw"); Port = $null }
)) {
    $existing = Get-ScheduledTask -TaskName $taskSpec.Name -ErrorAction SilentlyContinue
    $wasRunning = $false
    if ($existing) {
        $wasRunning = $existing.State -eq "Running"
        if ($wasRunning -or $RestartHostTasks) {
            Stop-ScheduledTask -TaskName $taskSpec.Name -ErrorAction SilentlyContinue
            Wait-FluxTaskStopped -TaskName $taskSpec.Name
            if ($taskSpec.Port) { Wait-FluxTcpClosed -Port $taskSpec.Port }
        }
    }
    Register-FluxTask -TaskName $taskSpec.Name -LauncherPath $taskSpec.Launcher -AppRoot $appRoot
    if ($wasRunning -or $RestartHostTasks) {
        Start-ScheduledTask -TaskName $taskSpec.Name
        if ($taskSpec.Port) { Wait-FluxTcpOpen -Port $taskSpec.Port }
    }
}

Write-Host "Flux production runtime updated at $InstallRoot to image tag $imageTag"
