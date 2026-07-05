param(
    [string]$InstallRoot = "D:\FluxLLMKB"
)

$ErrorActionPreference = "Stop"
$appRoot = Join-Path $InstallRoot "app"
$composePath = Join-Path $appRoot "docker-compose.yml"
$appEnvPath = Join-Path $appRoot ".env"
$fluxContainers = @(
    "flux-llm-kb-api",
    "flux-llm-kb-worker",
    "flux-llm-kb-search-index-worker",
    "flux-llm-kb-mail-worker",
    "flux-llm-kb-outlook-worker",
    "flux-llm-kb-automation-worker",
    "flux-llm-kb-governance-worker",
    "flux-llm-kb-runtime-control-worker",
    "flux-llm-kb-gpu-eviction-worker",
    "flux-llm-kb-callback-worker",
    "flux-llm-kb-event-audit-worker",
    "flux-llm-kb-event-dashboard-worker",
    "flux-llm-kb-event-diagnostics-worker",
    "flux-llm-kb-event-scheduler",
    "flux-llm-kb-outbox-relay",
    "flux-llm-kb-model-runner",
    "flux-llm-kb-paddle-runner",
    "flux-llm-kb-asr",
    "flux-ollama",
    "flux-vespa",
    "flux-llm-kb-rabbitmq",
    "flux-llm-kb-postgres"
)

function Format-FluxDockerMemoryLimit {
    param([string]$Value)
    if (-not $Value) {
        return "unavailable"
    }
    $bytes = 0L
    if (-not [long]::TryParse($Value, [ref]$bytes)) {
        return $Value
    }
    if ($bytes -eq 0) {
        return "unbounded"
    }
    $gib = [math]::Round(([double]$bytes / 1GB), 2)
    return "${gib} GiB ($bytes bytes)"
}

function Get-FluxDockerInspectValue {
    param([string]$Container, [string]$Format)
    $value = docker inspect --format $Format $Container 2>$null
    if ($LASTEXITCODE -eq 0 -and $value) {
        return $value.Trim()
    }
    return $null
}

Write-Host "Flux production status"
Write-Host "Install root: $InstallRoot"

if (Test-Path (Join-Path $appRoot "VERSION")) {
    Write-Host "Version: $((Get-Content -Raw (Join-Path $appRoot "VERSION")).Trim())"
}

if (Test-Path $composePath) {
    Push-Location $appRoot
    try {
        docker compose --env-file $appEnvPath -f $composePath ps
    } finally {
        Pop-Location
    }
} else {
    Write-Host "Compose file not found."
}

Write-Host "Flux Docker storage"
$dockerMemory = docker info --format "{{.MemTotal}}" 2>$null
if ($LASTEXITCODE -eq 0 -and $dockerMemory) {
    $dockerMemoryGiB = [math]::Round(([double]$dockerMemory / 1GB), 2)
    Write-Host "Docker-visible memory: ${dockerMemoryGiB} GiB ($dockerMemory bytes)"
} else {
    Write-Host "Docker-visible memory: unavailable"
}

Write-Host "Flux Docker memory limits"
foreach ($container in $fluxContainers) {
    $memory = Get-FluxDockerInspectValue -Container $container -Format "{{.HostConfig.Memory}}"
    $swap = Get-FluxDockerInspectValue -Container $container -Format "{{.HostConfig.MemorySwap}}"
    if ($memory -or $swap) {
        $formattedMemory = Format-FluxDockerMemoryLimit -Value $memory
        $formattedSwap = Format-FluxDockerMemoryLimit -Value $swap
        Write-Host "${container}: HostConfig.Memory=$formattedMemory; HostConfig.MemorySwap=$formattedSwap"
    } else {
        Write-Host "${container}: HostConfig.Memory=unavailable; HostConfig.MemorySwap=unavailable"
    }
}
foreach ($container in @("flux-llm-kb-postgres", "flux-llm-kb-api", "flux-llm-kb-worker", "flux-ollama")) {
    $mounts = docker inspect --format "{{json .Mounts}}" $container 2>$null
    if ($LASTEXITCODE -eq 0 -and $mounts) {
        Write-Host "${container} Mounts: $mounts"
    } else {
        Write-Host "${container} Mounts: unavailable"
    }
}
$postgresMemory = docker exec flux-llm-kb-postgres sh -lc "awk '/MemTotal|MemAvailable/ {print}' /proc/meminfo; df -h /dev/shm" 2>$null
if ($LASTEXITCODE -eq 0 -and $postgresMemory) {
    Write-Host "flux-llm-kb-postgres memory and /dev/shm:"
    $postgresMemory | ForEach-Object { Write-Host $_ }
} else {
    Write-Host "flux-llm-kb-postgres memory and /dev/shm: unavailable"
}

foreach ($task in @("FluxKB Host Agent", "FluxKB Outlook Host")) {
    $existing = Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    if ($existing) {
        $info = Get-ScheduledTaskInfo -TaskName $task
        Write-Host "${task}: $($existing.State), last result $($info.LastTaskResult)"
    } else {
        Write-Host "${task}: not registered"
    }
}

$health = $null
try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:8765/api/dashboard/health" -TimeoutSec 5
    $health | ConvertTo-Json -Depth 6
} catch {
    Write-Host "Dashboard health endpoint unavailable: $($_.Exception.Message)"
}

if ($health) {
    $blocked = @()
    if (-not $health.database -or -not $health.database.checks) {
        $blocked += "database.checks missing"
    } else {
        foreach ($checkProperty in $health.database.checks.PSObject.Properties) {
            $check = $checkProperty.Value
            if ($check.required -ne $false -and $check.ok -ne $true) {
                $message = if ($check.message) { $check.message } else { "blocked" }
                $blocked += "$($checkProperty.Name): $message"
            }
        }
    }
    if ($blocked.Count -gt 0) {
        Write-Host "Dashboard health reported blocked required checks: $($blocked -join '; ')"
        exit 1
    }
}

Write-Host "Flux MCP readiness"
$previousPythonPath = $env:PYTHONPATH
try {
    Push-Location $appRoot
    try {
        $env:PYTHONPATH = Join-Path $appRoot "src"
        $mcpReadiness = python -m flux_llm_kb.cli codex mcp-readiness --json 2>&1
        $mcpExit = $LASTEXITCODE
        $mcpReadiness | ForEach-Object { Write-Host $_ }
        if ($mcpExit -ne 0) {
            Write-Host "Flux MCP readiness failed."
            exit 1
        }
    } finally {
        Pop-Location
    }
} catch {
    Write-Host "Flux MCP readiness failed: $($_.Exception.Message)"
    exit 1
} finally {
    if ($null -eq $previousPythonPath) {
        Remove-Item Env:\PYTHONPATH -ErrorAction SilentlyContinue
    } else {
        $env:PYTHONPATH = $previousPythonPath
    }
}
