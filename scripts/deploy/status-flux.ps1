param(
    [string]$InstallRoot = "D:\FluxLLMKB"
)

$ErrorActionPreference = "Stop"
$appRoot = Join-Path $InstallRoot "app"
$composePath = Join-Path $appRoot "docker-compose.yml"
$appEnvPath = Join-Path $appRoot ".env"

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
