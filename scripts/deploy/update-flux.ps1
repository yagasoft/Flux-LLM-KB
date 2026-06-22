param(
    [string]$InstallRoot = "D:\FluxLLMKB",
    [string]$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$PythonExe = "",
    [switch]$SkipDashboardBuild,
    [switch]$RecreateVenv,
    [switch]$RestartHostTasks
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
