param(
    [string]$InstallRoot = "D:\FluxLLMKB",
    [string]$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [switch]$SkipDashboardBuild,
    [switch]$RestartHostTasks
)

$ErrorActionPreference = "Stop"

$appRoot = Join-Path $InstallRoot "app"
$composePath = Join-Path $appRoot "docker-compose.yml"
$appEnvPath = Join-Path $appRoot ".env"
$venvPython = Join-Path $appRoot ".venv\Scripts\python.exe"

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

docker build -t "flux-llm-kb-api:$imageTag" -t "flux-llm-kb-api:local" $SourceRoot
docker tag "flux-llm-kb-api:$imageTag" "flux-llm-kb-worker:$imageTag"
docker tag "flux-llm-kb-api:$imageTag" "flux-llm-kb-worker:local"

Set-Content -Path $appEnvPath -Value "FLUX_KB_IMAGE_TAG=$imageTag`n" -Encoding UTF8
Set-Content -Path (Join-Path $appRoot "VERSION") -Value $imageTag -Encoding UTF8

if (-not (Test-Path $venvPython)) {
    python -m venv (Join-Path $appRoot ".venv")
}
& $venvPython -m pip install --upgrade pip
& $venvPython -m pip install "$SourceRoot[api,corpus,mail]"

Push-Location $appRoot
try {
    docker compose --env-file $appEnvPath -f $composePath up -d --no-build postgres api worker
} finally {
    Pop-Location
}

if ($RestartHostTasks) {
    foreach ($task in @("FluxKB Host Agent", "FluxKB Outlook Host")) {
        $existing = Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
        if ($existing) {
            Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
            Start-ScheduledTask -TaskName $task
        }
    }
}

Write-Host "Flux production runtime updated at $InstallRoot to image tag $imageTag"
