param(
    [string]$InstallRoot = $(if ($env:FLUX_KB_INSTALL_ROOT) { $env:FLUX_KB_INSTALL_ROOT } else { "J:\FluxLLMKB" }),
    [string]$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [int]$ApiPort = 8765,
    [int]$HostAgentPort = 8799,
    [int]$PostgresPort = 5432,
    [int]$OllamaHostPort = 11435,
    [int]$AsrHostPort = 8788,
    [string]$PythonExe = "",
    [ValidateSet("auto", "on", "off")]
    [string]$GpuMode = "auto",
    [switch]$SkipDashboardBuild,
    [bool]$NpmOffline = $true,
    [string]$NpmCachePath = $env:FLUX_KB_NPM_CACHE_PATH,
    [switch]$RecreateVenv,
    [switch]$RestartHostTasks,
    [int]$DockerComposeTimeoutSeconds = 3600,
    [int]$DockerBuildTimeoutSeconds = 3600,
    [int]$AsrModelDownloadTimeoutSeconds = 3600,
    [int]$ModelRunnerModelDownloadTimeoutSeconds = 43200,
    [ValidateSet("auto", "local", "python")]
    [string]$DockerBaseMode = "auto",
    [string]$DockerBaseImage = $env:FLUX_KB_DOCKER_BASE_IMAGE,
    [int]$PipInstallTimeoutSeconds = 900,
    [int]$PipTimeoutSeconds = 180,
    [int]$PipRetries = 20,
    [bool]$PipOffline = $true,
    [string]$PipWheelhousePath = $env:FLUX_KB_PIP_WHEELHOUSE_PATH,
    [string]$PipWheelhouseImage = $env:FLUX_KB_PIP_WHEELHOUSE_IMAGE,
    [string]$PipIndexUrl = $env:FLUX_KB_PIP_INDEX_URL,
    [string]$PaddleGpuIndexUrl = $(if ($env:FLUX_KB_PADDLE_GPU_INDEX_URL) { $env:FLUX_KB_PADDLE_GPU_INDEX_URL } else { "https://www.paddlepaddle.org.cn/packages/stable/cu126/" }),
    [string]$PytorchGpuIndexUrl = $(if ($env:FLUX_KB_PYTORCH_GPU_INDEX_URL) { $env:FLUX_KB_PYTORCH_GPU_INDEX_URL } else { "https://download.pytorch.org/whl/cu126" }),
    [string]$AptDebianMirrorUrl = $env:FLUX_KB_APT_DEBIAN_MIRROR_URL,
    [string]$AptSecurityMirrorUrl = $env:FLUX_KB_APT_SECURITY_MIRROR_URL,
    [switch]$SkipWorkerStart,
    [switch]$AllowImagePull,
    [switch]$AllowPackageRefresh
)

$ErrorActionPreference = "Stop"

if ((-not $PipOffline -or -not $NpmOffline) -and -not $AllowPackageRefresh) {
    throw "Network package refresh requires -AllowPackageRefresh."
}

$appRoot = Join-Path $InstallRoot "app"
$composePath = Join-Path $appRoot "docker-compose.yml"
$appEnvPath = Join-Path $appRoot ".env"
$privateEnvPath = Join-Path $InstallRoot "private\flux.env"
$modelsRoot = Join-Path $InstallRoot "models"
$ollamaModelsRoot = Join-Path $modelsRoot "ollama"
$venvPython = Join-Path $appRoot ".venv\Scripts\python.exe"
$venvRoot = Join-Path $appRoot ".venv"

function Resolve-FluxPythonExe {
    param([string]$InstallRoot, [string]$RequestedPython)
    if ($RequestedPython) { return $RequestedPython }
    $installedPython = Join-Path $InstallRoot "python\python.exe"
    if (Test-Path $installedPython) { return $installedPython }
    return "python"
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

function ConvertTo-FluxImageSourceLabel {
    param([string]$Source)
    if (-not $Source) { return "local" }
    try {
        [uri]$sourceUri = $Source
        if ($sourceUri.UserInfo) {
            $builder = [System.UriBuilder]::new($sourceUri)
            $builder.UserName = ""
            $builder.Password = ""
            return $builder.Uri.AbsoluteUri
        }
    } catch {}
    return $Source
}

function Get-FluxBuildMetadata {
    param([string]$Root)
    $dirty = @(git -C $Root status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "Flux source root is not a Git checkout: $Root"
    }
    if ($dirty.Count -gt 0) {
        throw "Flux source checkout is dirty; commit or remove changes before building a traceable production image.`n$($dirty -join "`n")"
    }

    $revision = (git -C $Root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $revision) {
        throw "Could not resolve Flux Git revision for source root: $Root"
    }
    $shortRevision = (git -C $Root rev-parse --short HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $shortRevision) {
        throw "Could not resolve Flux short Git revision for source root: $Root"
    }

    $source = ""
    $sourceResult = @(git -C $Root remote get-url origin 2>$null)
    if ($LASTEXITCODE -eq 0 -and $sourceResult.Count -gt 0) {
        $source = ($sourceResult | Select-Object -First 1).Trim()
    }

    return [pscustomobject]@{
        Revision = $revision
        ShortRevision = $shortRevision
        Source = ConvertTo-FluxImageSourceLabel -Source $source
        Created = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        Version = $shortRevision
    }
}

function Remove-FluxGpuComposeAccess {
    param([string]$ComposeText)
    $skipAsrService = $false
    $skipOllamaService = $false
    $skipAsrDependency = 0
    $skipOllamaDependency = 0
    $filtered = foreach ($line in ($ComposeText -split "`r?`n")) {
        if ($line -match "^  asr:\s*$") {
            $skipAsrService = $true
            continue
        }
        if ($skipAsrService) {
            if ($line -match "^  ollama:\s*$") {
                $skipAsrService = $false
            } else {
                continue
            }
        }
        if ($line -match "^  ollama:\s*$") {
            $skipOllamaService = $true
            continue
        }
        if ($skipOllamaService) {
            if ($line -match "^  postgres:\s*$") {
                $skipOllamaService = $false
            } else {
                continue
            }
        }
        if ($line -match "^\s+ollama:\s*$") {
            $skipOllamaDependency = 1
            continue
        }
        if ($line -match "^\s+asr:\s*$") {
            $skipAsrDependency = 1
            continue
        }
        if ($skipAsrDependency -gt 0) {
            $skipAsrDependency -= 1
            continue
        }
        if ($skipOllamaDependency -gt 0) {
            $skipOllamaDependency -= 1
            continue
        }
        if ($line -match "^\s+gpus: all\s*$") { continue }
        if ($line -match "^\s+NVIDIA_VISIBLE_DEVICES: all\s*$") { continue }
        if ($line -match "^\s+NVIDIA_DRIVER_CAPABILITIES: compute,utility\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_ASR_PROVIDER: openai_compatible\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_ASR_MODEL: large-v3-turbo\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_ASR_BASE_URL: http://asr:8788\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_ASR_MODEL_PATH: /models/faster-whisper-large-v3-turbo\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_ASR_DEVICE: cuda\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_ASR_COMPUTE_TYPE: float16\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_LOCAL_INFERENCE_ENABLED: `"true`"\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_LOCAL_INFERENCE_BASE_URL: http://ollama:11434\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE: 2m\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_VISION_ENABLED: `"true`"\s*$") { continue }
        if ($line -match "^\s+FLUX_KB_VISION_MODEL:\s+.+\s*$") { continue }
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
        [int]$OllamaHostPort,
        [int]$AsrHostPort,
        [bool]$GpuEnabled
    )
    $compose = @"
services:
  api:
    image: flux-llm-kb-api:`${FLUX_KB_IMAGE_TAG}
    labels:
      org.opencontainers.image.revision: `${FLUX_KB_IMAGE_REVISION}
      org.opencontainers.image.source: `${FLUX_KB_IMAGE_SOURCE}
      org.opencontainers.image.created: `${FLUX_KB_IMAGE_CREATED}
      org.opencontainers.image.version: `${FLUX_KB_IMAGE_VERSION}
    container_name: flux-llm-kb-api
    restart: unless-stopped
    mem_limit: "1gb"
    memswap_limit: "1gb"
    gpus: all
    depends_on:
      postgres:
        condition: service_healthy
      vespa:
        condition: service_healthy
      model-runner:
        condition: service_healthy
      ollama:
        condition: service_healthy
      asr:
        condition: service_healthy
      rabbitmq:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_RABBITMQ_URL: amqp://flux:flux@rabbitmq:5672/flux
      FLUX_KB_RABBITMQ_MANAGEMENT_URL: http://rabbitmq:15672
      FLUX_KB_GPU_SCHEDULER_MODE: postgres
      FLUX_KB_GPU_SCHEDULER_VRAM_BUDGET_MB: "10240"
      FLUX_KB_GPU_SCHEDULER_SAFETY_MARGIN_MB: "1024"
      FLUX_KB_GPU_SCHEDULER_BACKGROUND_TIMEOUT_SECONDS: "1"
      FLUX_KB_GPU_SCHEDULER_EVICTION_ENABLED: "true"
      FLUX_KB_GPU_SCHEDULER_EVICTION_REQUEST_TIMEOUT_SECONDS: "10"
      FLUX_KB_GPU_SCHEDULER_EVICTION_MAX_MODELS: "4"
      FLUX_KB_API_HOST: 0.0.0.0
      FLUX_KB_API_PORT: "8765"
      FLUX_KB_INSTALL_ROOT: /app/runtime
      FLUX_KB_APP_ROOT: /app
      FLUX_KB_PRIVATE_DIR: /app/private
      FLUX_KB_DATA_DIR: /app/data
      FLUX_KB_LOG_DIR: /app/logs
      FLUX_KB_CACHE_ROOT: /app/cache
      FLUX_KB_MODEL_RUNNER_BASE_URL: http://model-runner:8790
      FLUX_KB_PADDLE_RUNNER_BASE_URL: http://paddle-runner:8791
      FLUX_KB_RETRIEVAL_SEARCH_ENGINE: vespa
      FLUX_KB_RETRIEVAL_VESPA_BASE_URL: http://vespa:8080
      FLUX_KB_RETRIEVAL_EMBEDDING_MODEL: Snowflake/snowflake-arctic-embed-l-v2.0
      FLUX_KB_RETRIEVAL_EMBEDDING_DIMENSIONS: "1024"
      FLUX_KB_RETRIEVAL_RERANKER_MODEL: Qwen/Qwen3-Reranker-4B
      FLUX_KB_RETRIEVAL_RERANKER_AWQ_MODEL: drawais/Qwen3-Reranker-4B-AWQ-INT4
      FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION: awq_int4
      FLUX_KB_RETRIEVAL_RERANK_TOP_N: "12"
      FLUX_KB_RETRIEVAL_RERANK_MICROBATCH_SIZE: "1"
      FLUX_KB_RETRIEVAL_RERANK_TOTAL_BUDGET_SECONDS: "5"
      FLUX_KB_RETRIEVAL_MAX_RERANK_PASSAGE_TOKENS: "1536"
      FLUX_KB_RETRIEVAL_GPU_VRAM_BUDGET_MB: "10240"
      FLUX_KB_OCR_ENGINE: paddleocr
      FLUX_KB_OCR_SIMPLE_MODEL: PP-OCRv5
      FLUX_KB_OCR_DOCUMENT_MODEL: PaddleOCR-VL
      FLUX_KB_ASR_PROVIDER: openai_compatible
      FLUX_KB_ASR_MODEL: large-v3-turbo
      FLUX_KB_ASR_BASE_URL: http://asr:8788
      FLUX_KB_ASR_DEVICE: cuda
      FLUX_KB_ASR_COMPUTE_TYPE: float16
      FLUX_KB_LOCAL_INFERENCE_ENABLED: "true"
      FLUX_KB_LOCAL_INFERENCE_BASE_URL: http://ollama:11434
      FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE: 2m
      FLUX_KB_VISION_ENABLED: "true"
      FLUX_KB_VISION_MODEL: qwen3-vl:8b
      FLUX_KB_VISION_MAX_IMAGE_PIXELS: "80000000"
    ports:
      - "127.0.0.1:${ApiPort}:8765"
    volumes:
      - ../private:/app/private
      - flux_llm_kb_data:/app/data
      - flux_llm_kb_cache:/app/cache
      - flux_llm_kb_runtime:/app/runtime
      - flux_llm_kb_logs:/app/logs
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m uvicorn flux_llm_kb.rest_api:create_app --factory --host 0.0.0.0 --port 8765"

  worker:
    image: flux-llm-kb-worker:`${FLUX_KB_IMAGE_TAG}
    labels:
      org.opencontainers.image.revision: `${FLUX_KB_IMAGE_REVISION}
      org.opencontainers.image.source: `${FLUX_KB_IMAGE_SOURCE}
      org.opencontainers.image.created: `${FLUX_KB_IMAGE_CREATED}
      org.opencontainers.image.version: `${FLUX_KB_IMAGE_VERSION}
    container_name: flux-llm-kb-worker
    restart: unless-stopped
    mem_limit: "1gb"
    memswap_limit: "1gb"
    gpus: all
    depends_on:
      postgres:
        condition: service_healthy
      vespa:
        condition: service_healthy
      model-runner:
        condition: service_healthy
      ollama:
        condition: service_healthy
      asr:
        condition: service_healthy
      rabbitmq:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_RABBITMQ_URL: amqp://flux:flux@rabbitmq:5672/flux
      FLUX_KB_RABBITMQ_MANAGEMENT_URL: http://rabbitmq:15672
      FLUX_KB_GPU_SCHEDULER_MODE: postgres
      FLUX_KB_GPU_SCHEDULER_VRAM_BUDGET_MB: "10240"
      FLUX_KB_GPU_SCHEDULER_SAFETY_MARGIN_MB: "1024"
      FLUX_KB_GPU_SCHEDULER_BACKGROUND_TIMEOUT_SECONDS: "1"
      FLUX_KB_GPU_SCHEDULER_EVICTION_ENABLED: "true"
      FLUX_KB_GPU_SCHEDULER_EVICTION_REQUEST_TIMEOUT_SECONDS: "10"
      FLUX_KB_GPU_SCHEDULER_EVICTION_MAX_MODELS: "4"
      FLUX_KB_INSTALL_ROOT: /app/runtime
      FLUX_KB_APP_ROOT: /app
      FLUX_KB_PRIVATE_DIR: /app/private
      FLUX_KB_DATA_DIR: /app/data
      FLUX_KB_LOG_DIR: /app/logs
      FLUX_KB_CACHE_ROOT: /app/cache
      FLUX_KB_MODEL_RUNNER_BASE_URL: http://model-runner:8790
      FLUX_KB_PADDLE_RUNNER_BASE_URL: http://paddle-runner:8791
      FLUX_KB_RETRIEVAL_SEARCH_ENGINE: vespa
      FLUX_KB_RETRIEVAL_VESPA_BASE_URL: http://vespa:8080
      FLUX_KB_RETRIEVAL_EMBEDDING_MODEL: Snowflake/snowflake-arctic-embed-l-v2.0
      FLUX_KB_RETRIEVAL_EMBEDDING_DIMENSIONS: "1024"
      FLUX_KB_RETRIEVAL_RERANKER_MODEL: Qwen/Qwen3-Reranker-4B
      FLUX_KB_RETRIEVAL_RERANKER_AWQ_MODEL: drawais/Qwen3-Reranker-4B-AWQ-INT4
      FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION: awq_int4
      FLUX_KB_RETRIEVAL_RERANK_TOP_N: "12"
      FLUX_KB_RETRIEVAL_RERANK_MICROBATCH_SIZE: "1"
      FLUX_KB_RETRIEVAL_RERANK_TOTAL_BUDGET_SECONDS: "5"
      FLUX_KB_RETRIEVAL_MAX_RERANK_PASSAGE_TOKENS: "1536"
      FLUX_KB_RETRIEVAL_GPU_VRAM_BUDGET_MB: "10240"
      FLUX_KB_OCR_ENGINE: paddleocr
      FLUX_KB_OCR_SIMPLE_MODEL: PP-OCRv5
      FLUX_KB_OCR_DOCUMENT_MODEL: PaddleOCR-VL
      FLUX_KB_ASR_PROVIDER: openai_compatible
      FLUX_KB_ASR_MODEL: large-v3-turbo
      FLUX_KB_ASR_BASE_URL: http://asr:8788
      FLUX_KB_ASR_DEVICE: cuda
      FLUX_KB_ASR_COMPUTE_TYPE: float16
      FLUX_KB_LOCAL_INFERENCE_ENABLED: "true"
      FLUX_KB_LOCAL_INFERENCE_BASE_URL: http://ollama:11434
      FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE: 2m
      FLUX_KB_VISION_ENABLED: "true"
      FLUX_KB_VISION_MODEL: qwen3-vl:8b
      FLUX_KB_VISION_MAX_IMAGE_PIXELS: "80000000"
    volumes:
      - ../private:/app/private
      - flux_llm_kb_data:/app/data
      - flux_llm_kb_cache:/app/cache
      - flux_llm_kb_runtime:/app/runtime
      - flux_llm_kb_logs:/app/logs
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event worker run --queue flux.commands.corpus"

  search-index-worker:
    extends:
      service: worker
    container_name: flux-llm-kb-search-index-worker
    mem_limit: "1gb"
    memswap_limit: "1gb"
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event worker run --queue flux.commands.search_index"

  mail-worker:
    extends:
      service: worker
    container_name: flux-llm-kb-mail-worker
    mem_limit: "512mb"
    memswap_limit: "512mb"
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event worker run --queue flux.commands.mail_imap"

  automation-worker:
    extends:
      service: worker
    container_name: flux-llm-kb-automation-worker
    mem_limit: "384mb"
    memswap_limit: "384mb"
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event worker run --queue flux.commands.automation"

  governance-worker:
    extends:
      service: worker
    container_name: flux-llm-kb-governance-worker
    mem_limit: "384mb"
    memswap_limit: "384mb"
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event worker run --queue flux.commands.governance"

  runtime-control-worker:
    extends:
      service: worker
    container_name: flux-llm-kb-runtime-control-worker
    mem_limit: "256mb"
    memswap_limit: "256mb"
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event worker run --queue flux.commands.runtime_control"

  gpu-eviction-worker:
    extends:
      service: worker
    container_name: flux-llm-kb-gpu-eviction-worker
    mem_limit: "256mb"
    memswap_limit: "256mb"
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event worker run --queue flux.commands.gpu_eviction"

  event-scheduler:
    image: flux-llm-kb-worker:`${FLUX_KB_IMAGE_TAG}
    container_name: flux-llm-kb-event-scheduler
    restart: unless-stopped
    mem_limit: "256mb"
    memswap_limit: "256mb"
    depends_on:
      postgres:
        condition: service_healthy
      rabbitmq:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    environment:
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_RABBITMQ_URL: amqp://flux:flux@rabbitmq:5672/flux
      FLUX_KB_RABBITMQ_MANAGEMENT_URL: http://rabbitmq:15672
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event scheduler run --interval 30 --limit 25"

  callback-worker:
    image: flux-llm-kb-worker:`${FLUX_KB_IMAGE_TAG}
    container_name: flux-llm-kb-callback-worker
    restart: unless-stopped
    mem_limit: "384mb"
    memswap_limit: "384mb"
    depends_on:
      postgres:
        condition: service_healthy
      rabbitmq:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    environment:
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_RABBITMQ_URL: amqp://flux:flux@rabbitmq:5672/flux
      FLUX_KB_RABBITMQ_MANAGEMENT_URL: http://rabbitmq:15672
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event callbacks dispatch --queue flux.callbacks.dispatch"

  event-audit-worker:
    image: flux-llm-kb-worker:`${FLUX_KB_IMAGE_TAG}
    container_name: flux-llm-kb-event-audit-worker
    restart: unless-stopped
    mem_limit: "256mb"
    memswap_limit: "256mb"
    depends_on:
      postgres:
        condition: service_healthy
      rabbitmq:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    environment:
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_RABBITMQ_URL: amqp://flux:flux@rabbitmq:5672/flux
      FLUX_KB_RABBITMQ_MANAGEMENT_URL: http://rabbitmq:15672
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event subscriber run --queue flux.events.audit --subscriber audit"

  event-dashboard-worker:
    image: flux-llm-kb-worker:`${FLUX_KB_IMAGE_TAG}
    container_name: flux-llm-kb-event-dashboard-worker
    restart: unless-stopped
    mem_limit: "256mb"
    memswap_limit: "256mb"
    depends_on:
      postgres:
        condition: service_healthy
      rabbitmq:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    environment:
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_RABBITMQ_URL: amqp://flux:flux@rabbitmq:5672/flux
      FLUX_KB_RABBITMQ_MANAGEMENT_URL: http://rabbitmq:15672
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event subscriber run --queue flux.events.dashboard --subscriber dashboard"

  event-diagnostics-worker:
    image: flux-llm-kb-worker:`${FLUX_KB_IMAGE_TAG}
    container_name: flux-llm-kb-event-diagnostics-worker
    restart: unless-stopped
    mem_limit: "256mb"
    memswap_limit: "256mb"
    depends_on:
      postgres:
        condition: service_healthy
      rabbitmq:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    environment:
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_RABBITMQ_URL: amqp://flux:flux@rabbitmq:5672/flux
      FLUX_KB_RABBITMQ_MANAGEMENT_URL: http://rabbitmq:15672
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event subscriber run --queue flux.events.diagnostics --subscriber diagnostics"

  outbox-relay:
    image: flux-llm-kb-worker:`${FLUX_KB_IMAGE_TAG}
    container_name: flux-llm-kb-outbox-relay
    restart: unless-stopped
    mem_limit: "384mb"
    memswap_limit: "384mb"
    depends_on:
      postgres:
        condition: service_healthy
      rabbitmq:
        condition: service_healthy
    env_file:
      - ../private/flux.env
    environment:
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_RABBITMQ_URL: amqp://flux:flux@rabbitmq:5672/flux
      FLUX_KB_RABBITMQ_MANAGEMENT_URL: http://rabbitmq:15672
    command: >
      sh -c "python -m flux_llm_kb.cli migrate &&
             python -m flux_llm_kb.cli event outbox relay --interval 1 --limit 100"

  asr:
    image: flux-llm-kb-api:`${FLUX_KB_IMAGE_TAG}
    labels:
      org.opencontainers.image.revision: `${FLUX_KB_IMAGE_REVISION}
      org.opencontainers.image.source: `${FLUX_KB_IMAGE_SOURCE}
      org.opencontainers.image.created: `${FLUX_KB_IMAGE_CREATED}
      org.opencontainers.image.version: `${FLUX_KB_IMAGE_VERSION}
    container_name: flux-llm-kb-asr
    restart: unless-stopped
    mem_limit: "3gb"
    memswap_limit: "3gb"
    gpus: all
    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_GPU_SCHEDULER_MODE: postgres
      FLUX_KB_GPU_SCHEDULER_VRAM_BUDGET_MB: "10240"
      FLUX_KB_GPU_SCHEDULER_SAFETY_MARGIN_MB: "1024"
      FLUX_KB_GPU_SCHEDULER_BACKGROUND_TIMEOUT_SECONDS: "1"
      FLUX_KB_GPU_SCHEDULER_EVICTION_ENABLED: "true"
      FLUX_KB_GPU_SCHEDULER_EVICTION_REQUEST_TIMEOUT_SECONDS: "10"
      FLUX_KB_GPU_SCHEDULER_EVICTION_MAX_MODELS: "4"
      FLUX_KB_MODEL_RUNNER_BASE_URL: http://model-runner:8790
      FLUX_KB_PADDLE_RUNNER_BASE_URL: http://paddle-runner:8791
      FLUX_KB_ASR_BASE_URL: http://asr:8788
      FLUX_KB_LOCAL_INFERENCE_ENABLED: "true"
      FLUX_KB_LOCAL_INFERENCE_BASE_URL: http://ollama:11434
      FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE: 2m
      FLUX_KB_ASR_PROVIDER: openai_compatible
      FLUX_KB_ASR_MODEL: large-v3-turbo
      FLUX_KB_ASR_MODEL_PATH: /models/faster-whisper-large-v3-turbo
      FLUX_KB_ASR_DEVICE: cuda
      FLUX_KB_ASR_COMPUTE_TYPE: float16
    ports:
      - "127.0.0.1:${AsrHostPort}:8788"
    volumes:
      - flux_llm_kb_asr_models:/models
    command: >
      python -m flux_llm_kb.asr_server serve --host 0.0.0.0 --port 8788
    healthcheck:
      test: ["CMD", "python", "-S", "-c", "from urllib.request import urlopen; urlopen('http://127.0.0.1:8788/livez', timeout=2).read()"]
      interval: 10s
      timeout: 10s
      retries: 30
      start_period: 10s

  model-runner:
    image: flux-llm-kb-api:`${FLUX_KB_IMAGE_TAG}
    labels:
      org.opencontainers.image.revision: `${FLUX_KB_IMAGE_REVISION}
      org.opencontainers.image.source: `${FLUX_KB_IMAGE_SOURCE}
      org.opencontainers.image.created: `${FLUX_KB_IMAGE_CREATED}
      org.opencontainers.image.version: `${FLUX_KB_IMAGE_VERSION}
    container_name: flux-llm-kb-model-runner
    restart: unless-stopped
    mem_limit: "5gb"
    memswap_limit: "5gb"
    gpus: all
    depends_on:
      paddle-runner:
        condition: service_healthy
    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility
      FLUX_KB_MODEL_RUNNER_ROLE: model-runner
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_GPU_SCHEDULER_MODE: postgres
      FLUX_KB_GPU_SCHEDULER_VRAM_BUDGET_MB: "10240"
      FLUX_KB_GPU_SCHEDULER_SAFETY_MARGIN_MB: "1024"
      FLUX_KB_GPU_SCHEDULER_BACKGROUND_TIMEOUT_SECONDS: "1"
      FLUX_KB_GPU_SCHEDULER_EVICTION_ENABLED: "true"
      FLUX_KB_GPU_SCHEDULER_EVICTION_REQUEST_TIMEOUT_SECONDS: "10"
      FLUX_KB_GPU_SCHEDULER_EVICTION_MAX_MODELS: "4"
      FLUX_KB_MODEL_RUNNER_BASE_URL: http://model-runner:8790
      FLUX_KB_PADDLE_RUNNER_BASE_URL: http://paddle-runner:8791
      FLUX_KB_ASR_BASE_URL: http://asr:8788
      FLUX_KB_LOCAL_INFERENCE_ENABLED: "true"
      FLUX_KB_LOCAL_INFERENCE_BASE_URL: http://ollama:11434
      FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE: 2m
      FLUX_KB_RETRIEVAL_EMBEDDING_MODEL: Snowflake/snowflake-arctic-embed-l-v2.0
      FLUX_KB_RETRIEVAL_EMBEDDING_DIMENSIONS: "1024"
      FLUX_KB_RETRIEVAL_RERANKER_MODEL: Qwen/Qwen3-Reranker-4B
      FLUX_KB_RETRIEVAL_RERANKER_AWQ_MODEL: drawais/Qwen3-Reranker-4B-AWQ-INT4
      FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION: awq_int4
      FLUX_KB_OCR_ENGINE: paddleocr
      FLUX_KB_OCR_SIMPLE_MODEL: PP-OCRv5
      FLUX_KB_OCR_DOCUMENT_MODEL: PaddleOCR-VL
      HF_HOME: /models/huggingface
      HF_HUB_DISABLE_XET: "1"
      PADDLEOCR_HOME: /root/.paddleocr
      PADDLE_PDX_CACHE_HOME: /root/.paddleocr/paddlex
      PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK: "True"
      PADDLE_PDX_MODEL_SOURCE: bos
    ports:
      - "127.0.0.1:8790:8790"
    volumes:
      - flux_llm_kb_model_runner_models:/models
      - flux_llm_kb_paddle_cache:/root/.paddleocr
    command: >
      python -m flux_llm_kb.model_runner serve --host 0.0.0.0 --port 8790
    healthcheck:
      test: ["CMD", "python", "-S", "-c", "from urllib.request import urlopen; urlopen('http://127.0.0.1:8790/livez', timeout=2).read()"]
      interval: 10s
      timeout: 10s
      retries: 30
      start_period: 10s

  paddle-runner:
    image: flux-llm-kb-api:`${FLUX_KB_IMAGE_TAG}
    labels:
      org.opencontainers.image.revision: `${FLUX_KB_IMAGE_REVISION}
      org.opencontainers.image.source: `${FLUX_KB_IMAGE_SOURCE}
      org.opencontainers.image.created: `${FLUX_KB_IMAGE_CREATED}
      org.opencontainers.image.version: `${FLUX_KB_IMAGE_VERSION}
    container_name: flux-llm-kb-paddle-runner
    restart: unless-stopped
    mem_limit: "5gb"
    memswap_limit: "5gb"
    gpus: all
    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility
      LD_LIBRARY_PATH: /opt/flux-paddle/lib/python3.12/site-packages/nvidia/cuda_runtime/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/cublas/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/cudnn/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/nccl/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/cufft/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/curand/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/cusolver/lib:/opt/flux-paddle/lib/python3.12/site-packages/nvidia/cusparse/lib
      FLUX_KB_MODEL_RUNNER_ROLE: paddle-runner
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_GPU_SCHEDULER_MODE: postgres
      FLUX_KB_GPU_SCHEDULER_VRAM_BUDGET_MB: "10240"
      FLUX_KB_GPU_SCHEDULER_SAFETY_MARGIN_MB: "1024"
      FLUX_KB_GPU_SCHEDULER_BACKGROUND_TIMEOUT_SECONDS: "1"
      FLUX_KB_GPU_SCHEDULER_EVICTION_ENABLED: "true"
      FLUX_KB_GPU_SCHEDULER_EVICTION_REQUEST_TIMEOUT_SECONDS: "10"
      FLUX_KB_GPU_SCHEDULER_EVICTION_MAX_MODELS: "4"
      FLUX_KB_MODEL_RUNNER_BASE_URL: http://model-runner:8790
      FLUX_KB_PADDLE_RUNNER_BASE_URL: http://paddle-runner:8791
      FLUX_KB_ASR_BASE_URL: http://asr:8788
      FLUX_KB_LOCAL_INFERENCE_ENABLED: "true"
      FLUX_KB_LOCAL_INFERENCE_BASE_URL: http://ollama:11434
      FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE: 2m
      FLUX_KB_OCR_ENGINE: paddleocr
      FLUX_KB_OCR_SIMPLE_MODEL: PP-OCRv5
      FLUX_KB_OCR_DOCUMENT_MODEL: PaddleOCR-VL
      HF_HOME: /models/huggingface
      HF_HUB_DISABLE_XET: "1"
      PADDLEOCR_HOME: /root/.paddleocr
      PADDLE_PDX_CACHE_HOME: /root/.paddleocr/paddlex
      PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK: "True"
      PADDLE_PDX_MODEL_SOURCE: bos
    volumes:
      - flux_llm_kb_model_runner_models:/models
      - flux_llm_kb_paddle_cache:/root/.paddleocr
    command: >
      /opt/flux-paddle/bin/python -m flux_llm_kb.model_runner serve-paddle --host 0.0.0.0 --port 8791
    healthcheck:
      test: ["CMD", "/opt/flux-paddle/bin/python", "-S", "-c", "from urllib.request import urlopen; urlopen('http://127.0.0.1:8791/livez', timeout=2).read()"]
      interval: 10s
      timeout: 10s
      retries: 30
      start_period: 10s

  ollama:
    image: flux-ollama:local
    labels:
      org.opencontainers.image.revision: `${FLUX_KB_IMAGE_REVISION}
      org.opencontainers.image.source: `${FLUX_KB_IMAGE_SOURCE}
      org.opencontainers.image.created: `${FLUX_KB_IMAGE_CREATED}
      org.opencontainers.image.version: `${FLUX_KB_IMAGE_VERSION}
    container_name: flux-ollama
    restart: unless-stopped
    mem_limit: "4gb"
    memswap_limit: "4gb"
    gpus: all
    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility
      OLLAMA_LOAD_TIMEOUT: 30m
      OLLAMA_KEEP_ALIVE: 2m
    ports:
      - "127.0.0.1:${OllamaHostPort}:11434"
    volumes:
      - flux_llm_kb_ollama_models:/root/.ollama
    healthcheck:
      test: ["CMD-SHELL", "command -v ffmpeg >/dev/null && command -v ffprobe >/dev/null && ollama list >/dev/null"]
      interval: 10s
      timeout: 10s
      retries: 30
      start_period: 10s

  vespa:
    image: vespaengine/vespa:8
    container_name: flux-vespa
    restart: unless-stopped
    mem_limit: "4gb"
    memswap_limit: "4gb"
    environment:
      VESPA_CONFIGSERVER_JVMARGS: "-Xms128m -Xmx512m"
    ports:
      - "127.0.0.1:8080:8080"
    volumes:
      - flux_llm_kb_vespa_var:/opt/vespa/var
      - flux_llm_kb_vespa_logs:/opt/vespa/logs
    healthcheck:
      test: ["CMD-SHELL", "curl -fsS http://127.0.0.1:8080/ApplicationStatus >/dev/null || exit 1"]
      interval: 10s
      timeout: 10s
      retries: 60
      start_period: 60s

  rabbitmq:
    image: rabbitmq:4.3-management
    container_name: flux-llm-kb-rabbitmq
    restart: unless-stopped
    mem_limit: "512mb"
    memswap_limit: "512mb"
    environment:
      RABBITMQ_DEFAULT_USER: flux
      RABBITMQ_DEFAULT_PASS: flux
      RABBITMQ_DEFAULT_VHOST: flux
    ports:
      - "127.0.0.1:5672:5672"
      - "127.0.0.1:15672:15672"
    volumes:
      - flux_llm_kb_rabbitmq_data:/var/lib/rabbitmq
    healthcheck:
      test: ["CMD-SHELL", "rabbitmq-diagnostics -q ping"]
      interval: 60s
      timeout: 10s
      start_period: 60s
      start_interval: 5s
      retries: 30

  postgres:
    image: postgres:16
    container_name: flux-llm-kb-postgres
    restart: unless-stopped
    mem_limit: "2gb"
    memswap_limit: "2gb"
    shm_size: "1gb"
    command: >
      postgres
      -c shared_buffers=768MB
      -c effective_cache_size=2GB
      -c work_mem=16MB
      -c maintenance_work_mem=256MB
      -c autovacuum_work_mem=128MB
      -c temp_buffers=16MB
      -c effective_io_concurrency=200
      -c random_page_cost=1.1
      -c max_worker_processes=8
      -c max_parallel_workers=4
      -c max_parallel_workers_per_gather=2
      -c max_parallel_maintenance_workers=2
      -c track_io_timing=on
      -c wal_compression=on
      -c max_wal_size=8GB
      -c min_wal_size=1GB
      -c checkpoint_timeout=15min
      -c checkpoint_completion_target=0.9
    environment:
      POSTGRES_USER: flux
      POSTGRES_PASSWORD: flux
      POSTGRES_DB: flux_llm_kb
    ports:
      - "127.0.0.1:${PostgresPort}:5432"
    volumes:
      - flux_llm_kb_postgres_data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U flux -d flux_llm_kb"]
      interval: 5s
      timeout: 5s
      retries: 20

volumes:
  flux_llm_kb_rabbitmq_data:
    name: flux_llm_kb_rabbitmq_data
  flux_llm_kb_postgres_data:
    name: flux_llm_kb_postgres_data
  flux_llm_kb_data:
    name: flux_llm_kb_data
  flux_llm_kb_cache:
    name: flux_llm_kb_cache
  flux_llm_kb_runtime:
    name: flux_llm_kb_runtime
  flux_llm_kb_logs:
    name: flux_llm_kb_logs
  flux_llm_kb_vespa_var:
    name: flux_llm_kb_vespa_var
  flux_llm_kb_vespa_logs:
    name: flux_llm_kb_vespa_logs
  flux_llm_kb_model_runner_models:
    name: flux_llm_kb_model_runner_models
  flux_llm_kb_paddle_cache:
    name: flux_llm_kb_paddle_cache
  flux_llm_kb_ollama_models:
    name: flux_llm_kb_ollama_models
  flux_llm_kb_asr_models:
    name: flux_llm_kb_asr_models
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
    $callbackSecret = ""
    if (Test-Path $TargetPath) {
        $existingSecret = Select-String -LiteralPath $TargetPath -Pattern "^FLUX_KB_CALLBACK_SIGNING_SECRET=(.*)$" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $existingSecret) {
            $callbackSecret = $existingSecret.Matches[0].Groups[1].Value
        }
    }
    if ([string]::IsNullOrWhiteSpace($callbackSecret)) {
        $callbackSecret = ([guid]::NewGuid().ToString("N") + [guid]::NewGuid().ToString("N"))
    }
    $envText = @"
FLUX_KB_DATABASE_URL=$databaseUrl
FLUX_KB_INSTALL_ROOT=$InstallRoot
FLUX_KB_APP_ROOT=$InstallRoot\app
FLUX_KB_PRIVATE_DIR=$InstallRoot\private
FLUX_KB_DATA_DIR=$InstallRoot\data
FLUX_KB_LOG_DIR=$InstallRoot\logs
FLUX_KB_IMAGE_TAG=$ImageTag
FLUX_KB_HOST_DATABASE_URL=postgresql://flux:flux@127.0.0.1:${PostgresPort}/flux_llm_kb
FLUX_KB_RABBITMQ_URL=amqp://flux:flux@rabbitmq:5672/flux
FLUX_KB_RABBITMQ_MANAGEMENT_URL=http://127.0.0.1:15672
FLUX_KB_RABBITMQ_USERNAME=flux
FLUX_KB_RABBITMQ_PASSWORD=flux
FLUX_KB_CALLBACK_SIGNING_SECRET=$callbackSecret
"@
    if ($GpuEnabled) {
        $envText += "`n" + @"
FLUX_KB_ASR_PROVIDER=openai_compatible
FLUX_KB_ASR_MODEL=large-v3-turbo
FLUX_KB_ASR_BASE_URL=http://asr:8788
FLUX_KB_ASR_DEVICE=cuda
FLUX_KB_ASR_COMPUTE_TYPE=float16
FLUX_KB_GPU_SCHEDULER_MODE=postgres
FLUX_KB_GPU_SCHEDULER_VRAM_BUDGET_MB=10240
FLUX_KB_GPU_SCHEDULER_SAFETY_MARGIN_MB=1024
FLUX_KB_LOCAL_INFERENCE_ENABLED=true
FLUX_KB_LOCAL_INFERENCE_BASE_URL=http://ollama:11434
FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE=2m
FLUX_KB_VISION_ENABLED=true
FLUX_KB_VISION_MODEL=qwen3-vl:8b
FLUX_KB_VISION_MAX_IMAGE_PIXELS=80000000
"@
    } else {
        $envText += "`n" + @"
FLUX_KB_ASR_PROVIDER=local_faster_whisper
FLUX_KB_ASR_MODEL=
FLUX_KB_ASR_BASE_URL=
FLUX_KB_ASR_DEVICE=auto
FLUX_KB_ASR_COMPUTE_TYPE=default
FLUX_KB_GPU_SCHEDULER_ENABLED=false
FLUX_KB_GPU_SCHEDULER_MODE=disabled
FLUX_KB_LOCAL_INFERENCE_ENABLED=false
FLUX_KB_LOCAL_INFERENCE_BASE_URL=http://127.0.0.1:11434
FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE=
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
    param([string]$AppRoot, [string]$InstallRoot, [int]$HostAgentPort, [int]$PostgresPort, [bool]$GpuEnabled, [int]$OllamaHostPort, [int]$AsrHostPort)
    if ($GpuEnabled) {
        $hostAgentAcceleration = @"
os.environ["FLUX_KB_ASR_PROVIDER"] = "openai_compatible"
os.environ["FLUX_KB_ASR_MODEL"] = "large-v3-turbo"
os.environ["FLUX_KB_ASR_BASE_URL"] = "http://127.0.0.1:${AsrHostPort}"
os.environ["FLUX_KB_ASR_DEVICE"] = "cuda"
os.environ["FLUX_KB_ASR_COMPUTE_TYPE"] = "float16"
os.environ["FLUX_KB_LOCAL_INFERENCE_ENABLED"] = "true"
os.environ["FLUX_KB_LOCAL_INFERENCE_BASE_URL"] = "http://127.0.0.1:${OllamaHostPort}"
os.environ["FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE"] = "2m"
os.environ["FLUX_KB_VISION_ENABLED"] = "true"
os.environ["FLUX_KB_VISION_MODEL"] = "qwen3-vl:8b"
os.environ["FLUX_KB_VISION_MAX_IMAGE_PIXELS"] = "80000000"
"@
    } else {
        $hostAgentAcceleration = @"
os.environ["FLUX_KB_ASR_PROVIDER"] = "local_faster_whisper"
os.environ["FLUX_KB_ASR_MODEL"] = ""
os.environ["FLUX_KB_ASR_BASE_URL"] = ""
os.environ["FLUX_KB_ASR_DEVICE"] = "auto"
os.environ["FLUX_KB_ASR_COMPUTE_TYPE"] = "default"
os.environ["FLUX_KB_LOCAL_INFERENCE_ENABLED"] = "false"
os.environ["FLUX_KB_LOCAL_INFERENCE_BASE_URL"] = "http://127.0.0.1:11434"
os.environ["FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE"] = ""
os.environ["FLUX_KB_VISION_ENABLED"] = "false"
os.environ["FLUX_KB_VISION_MODEL"] = ""
os.environ["FLUX_KB_VISION_MAX_IMAGE_PIXELS"] = "4096000"
"@
    }
    $hostAgent = @"
import os
import runpy
import sys
import traceback
from pathlib import Path

os.environ["FLUX_KB_DATABASE_URL"] = "postgresql://flux:flux@127.0.0.1:${PostgresPort}/flux_llm_kb"
os.environ["FLUX_KB_RABBITMQ_URL"] = "amqp://flux:flux@127.0.0.1:5672/flux"
os.environ["FLUX_KB_RABBITMQ_MANAGEMENT_URL"] = "http://127.0.0.1:15672"
os.environ["FLUX_KB_RABBITMQ_USERNAME"] = "flux"
os.environ["FLUX_KB_RABBITMQ_PASSWORD"] = "flux"
os.environ["FLUX_KB_INSTALL_ROOT"] = r"$InstallRoot"
os.environ["FLUX_KB_APP_ROOT"] = r"$AppRoot"
os.environ["FLUX_KB_PRIVATE_DIR"] = r"$InstallRoot\private"
os.environ["FLUX_KB_DATA_DIR"] = r"$InstallRoot\data"
os.environ["FLUX_KB_LOG_DIR"] = r"$InstallRoot\logs"
svg_renderer = Path(os.environ["FLUX_KB_INSTALL_ROOT"]) / "tools" / "resvg" / "resvg.exe"
if svg_renderer.exists():
    os.environ["FLUX_KB_SVG_RENDERER"] = str(svg_renderer)
$hostAgentAcceleration

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
os.environ["FLUX_KB_RABBITMQ_URL"] = "amqp://flux:flux@127.0.0.1:5672/flux"
os.environ["FLUX_KB_RABBITMQ_MANAGEMENT_URL"] = "http://127.0.0.1:15672"
os.environ["FLUX_KB_RABBITMQ_USERNAME"] = "flux"
os.environ["FLUX_KB_RABBITMQ_PASSWORD"] = "flux"
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

function Stop-FluxOutlookHostLaunchers {
    param([string]$InstallRoot)
    $launcherPath = Join-Path (Join-Path $InstallRoot "app") "run-outlook-host.pyw"
    $launcherPattern = [regex]::Escape($launcherPath)
    $processes = Get-CimInstance Win32_Process | Where-Object {
        $_.CommandLine -and
        $_.CommandLine -match $launcherPattern -and
        ($_.Name -eq "python.exe" -or $_.Name -eq "pythonw.exe")
    }
    foreach ($process in $processes) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Stop-FluxHostAgentLaunchers {
    param([string]$InstallRoot)
    $launcherPath = Join-Path (Join-Path $InstallRoot "app") "run-host-agent.pyw"
    $launcherPattern = [regex]::Escape($launcherPath)
    $processes = Get-CimInstance Win32_Process | Where-Object {
        $_.CommandLine -and
        $_.CommandLine -match $launcherPattern -and
        ($_.Name -eq "python.exe" -or $_.Name -eq "pythonw.exe")
    }
    foreach ($process in $processes) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Stop-FluxWorkerContainer {
    $workerId = docker ps --filter "name=^/flux-llm-kb-worker$" --format "{{.ID}}" 2>$null | Select-Object -First 1
    if ($workerId) {
        Write-Host "Stopping Flux worker container so queued jobs stay paused during model cutover."
        docker stop -t 45 flux-llm-kb-worker | Out-Host
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

function Invoke-FluxSettingsCommand {
    param([string]$VenvPython, [string[]]$Arguments)
    & $VenvPython -m flux_llm_kb.cli @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Flux runtime settings command failed with exit code $LASTEXITCODE"
    }
}

function Set-FluxProductionRuntimeSettings {
    param([string]$VenvPython, [string]$InstallRoot, [int]$PostgresPort, [bool]$GpuEnabled, [int]$OllamaHostPort, [int]$AsrHostPort)
    $previousDatabaseUrl = $env:FLUX_KB_DATABASE_URL
    $previousInstallRoot = $env:FLUX_KB_INSTALL_ROOT
    $previousAppRoot = $env:FLUX_KB_APP_ROOT
    $previousPrivateDir = $env:FLUX_KB_PRIVATE_DIR
    $previousDataDir = $env:FLUX_KB_DATA_DIR
    $previousLogDir = $env:FLUX_KB_LOG_DIR
    $previousLocalInferenceEnabled = $env:FLUX_KB_LOCAL_INFERENCE_ENABLED
    $previousLocalInferenceBaseUrl = $env:FLUX_KB_LOCAL_INFERENCE_BASE_URL
    $previousLocalInferenceKeepAlive = $env:FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE
    $previousVisionEnabled = $env:FLUX_KB_VISION_ENABLED
    $previousVisionModel = $env:FLUX_KB_VISION_MODEL
    $previousVisionMaxImagePixels = $env:FLUX_KB_VISION_MAX_IMAGE_PIXELS
    $previousAsrProvider = $env:FLUX_KB_ASR_PROVIDER
    $previousAsrModel = $env:FLUX_KB_ASR_MODEL
    $previousAsrBaseUrl = $env:FLUX_KB_ASR_BASE_URL
    $previousAsrDevice = $env:FLUX_KB_ASR_DEVICE
    $previousAsrComputeType = $env:FLUX_KB_ASR_COMPUTE_TYPE
    try {
        $env:FLUX_KB_DATABASE_URL = "postgresql://flux:flux@127.0.0.1:${PostgresPort}/flux_llm_kb"
        $env:FLUX_KB_INSTALL_ROOT = $InstallRoot
        $env:FLUX_KB_APP_ROOT = Join-Path $InstallRoot "app"
        $env:FLUX_KB_PRIVATE_DIR = Join-Path $InstallRoot "private"
        $env:FLUX_KB_DATA_DIR = Join-Path $InstallRoot "data"
        $env:FLUX_KB_LOG_DIR = Join-Path $InstallRoot "logs"
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.search_engine", "vespa", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.vespa_base_url", "http://127.0.0.1:8080", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.embedding_model", "Snowflake/snowflake-arctic-embed-l-v2.0", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.embedding_dimensions", "1024", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.reranker_model", "Qwen/Qwen3-Reranker-4B", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.reranker_awq_model", "drawais/Qwen3-Reranker-4B-AWQ-INT4", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.reranker_quantization", "awq_int4", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.rerank_top_n", "12", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.rerank_microbatch_size", "1", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.rerank_total_budget_seconds", "5", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.max_rerank_passage_tokens", "1536", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.gpu_vram_budget_mb", "10240", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "model_runner.base_url", "http://127.0.0.1:8790", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "ocr.engine", "paddleocr", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "ocr.simple_model", "PP-OCRv5", "--confirm")
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "ocr.document_model", "PaddleOCR-VL", "--confirm")
        if ($GpuEnabled) {
            $env:FLUX_KB_ASR_PROVIDER = "openai_compatible"
            $env:FLUX_KB_ASR_MODEL = "large-v3-turbo"
            $env:FLUX_KB_ASR_BASE_URL = "http://127.0.0.1:$AsrHostPort"
            $env:FLUX_KB_ASR_DEVICE = "cuda"
            $env:FLUX_KB_ASR_COMPUTE_TYPE = "float16"
            $env:FLUX_KB_LOCAL_INFERENCE_ENABLED = "true"
            $env:FLUX_KB_LOCAL_INFERENCE_BASE_URL = "http://127.0.0.1:$OllamaHostPort"
            $env:FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE = "2m"
            $env:FLUX_KB_VISION_ENABLED = "true"
            $env:FLUX_KB_VISION_MODEL = "qwen3-vl:8b"
            $env:FLUX_KB_VISION_MAX_IMAGE_PIXELS = "80000000"
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "gpu.scheduler.enabled", "true", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "gpu.scheduler.mode", "postgres", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "gpu.scheduler.vram_budget_mb", "10240", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "gpu.scheduler.safety_margin_mb", "1024", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "gpu.scheduler.eviction_enabled", "true", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "gpu.scheduler.eviction_request_timeout_seconds", "10", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "gpu.scheduler.eviction_max_models", "4", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.asr.provider", "openai_compatible", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.asr.model", "large-v3-turbo", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.asr.base_url", "http://127.0.0.1:$AsrHostPort", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.asr.device", "cuda", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.asr.compute_type", "float16", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.local_inference.enabled", "true", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.local_inference.base_url", "http://127.0.0.1:$OllamaHostPort", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.local_inference.keep_alive", "2m", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.vision.enabled", "true", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.vision.model", "qwen3-vl:8b", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.vision.max_image_pixels", "80000000", "--confirm")
        } else {
            $env:FLUX_KB_ASR_PROVIDER = "local_faster_whisper"
            $env:FLUX_KB_ASR_MODEL = ""
            $env:FLUX_KB_ASR_BASE_URL = ""
            $env:FLUX_KB_ASR_DEVICE = "auto"
            $env:FLUX_KB_ASR_COMPUTE_TYPE = "default"
            $env:FLUX_KB_LOCAL_INFERENCE_ENABLED = "false"
            $env:FLUX_KB_LOCAL_INFERENCE_BASE_URL = "http://127.0.0.1:11434"
            $env:FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE = ""
            $env:FLUX_KB_VISION_ENABLED = "false"
            $env:FLUX_KB_VISION_MODEL = ""
            $env:FLUX_KB_VISION_MAX_IMAGE_PIXELS = "4096000"
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "gpu.scheduler.enabled", "false", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "gpu.scheduler.mode", "disabled", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "gpu.scheduler.eviction_enabled", "false", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.asr.provider", "local_faster_whisper", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.asr.model", "", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.asr.base_url", "", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.asr.device", "auto", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.asr.compute_type", "default", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.local_inference.enabled", "false", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.local_inference.base_url", "http://127.0.0.1:11434", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.local_inference.keep_alive", "", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.vision.enabled", "false", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.vision.model", "", "--confirm")
            Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "acceleration.vision.max_image_pixels", "4096000", "--confirm")
        }
    } finally {
        $env:FLUX_KB_DATABASE_URL = $previousDatabaseUrl
        $env:FLUX_KB_INSTALL_ROOT = $previousInstallRoot
        $env:FLUX_KB_APP_ROOT = $previousAppRoot
        $env:FLUX_KB_PRIVATE_DIR = $previousPrivateDir
        $env:FLUX_KB_DATA_DIR = $previousDataDir
        $env:FLUX_KB_LOG_DIR = $previousLogDir
        $env:FLUX_KB_LOCAL_INFERENCE_ENABLED = $previousLocalInferenceEnabled
        $env:FLUX_KB_LOCAL_INFERENCE_BASE_URL = $previousLocalInferenceBaseUrl
        $env:FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE = $previousLocalInferenceKeepAlive
        $env:FLUX_KB_VISION_ENABLED = $previousVisionEnabled
        $env:FLUX_KB_VISION_MODEL = $previousVisionModel
        $env:FLUX_KB_VISION_MAX_IMAGE_PIXELS = $previousVisionMaxImagePixels
        $env:FLUX_KB_ASR_PROVIDER = $previousAsrProvider
        $env:FLUX_KB_ASR_MODEL = $previousAsrModel
        $env:FLUX_KB_ASR_BASE_URL = $previousAsrBaseUrl
        $env:FLUX_KB_ASR_DEVICE = $previousAsrDevice
        $env:FLUX_KB_ASR_COMPUTE_TYPE = $previousAsrComputeType
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

function Invoke-FluxDockerImageAvailable {
    param(
        [string]$Image,
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 300,
        [switch]$AllowImagePull
    )
    if (Test-FluxDockerImageExists -Image $Image) { return }
    if (-not $AllowImagePull) {
        throw "Required Docker image is not available locally: $Image. Re-run explicitly with -AllowImagePull to download it."
    }
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("pull", $Image) -WorkingDirectory $WorkingDirectory -TimeoutSeconds $TimeoutSeconds -StepName "docker pull $Image"
}

function Invoke-FluxOllamaImageBuild {
    param(
        [string]$SourceRoot,
        [pscustomobject]$BuildMetadata,
        [string]$ImageTag,
        [int]$TimeoutSeconds,
        [switch]$AllowImagePull
    )
    $baseImage = "ollama/ollama:latest"
    $localRuntimeImage = "flux-ollama:local"
    if ((-not (Test-FluxDockerImageExists -Image $baseImage)) -and -not $AllowImagePull -and (Test-FluxDockerImageExists -Image $localRuntimeImage)) {
        $existingFingerprint = Get-FluxDockerImageLabel -Image $localRuntimeImage -Label "org.flux_llm_kb.ollama.runtime_fingerprint"
        $existingRevision = Get-FluxDockerImageLabel -Image $localRuntimeImage -Label "org.opencontainers.image.revision"
        Write-Host "Reusing discovered local Ollama runtime image $localRuntimeImage (revision $existingRevision, fingerprint $existingFingerprint); upstream base $baseImage is not available locally."
        return
    }
    Invoke-FluxDockerImageAvailable -Image $baseImage -WorkingDirectory $SourceRoot -TimeoutSeconds 600 -AllowImagePull:$AllowImagePull
    $runtimeFingerprint = Get-FluxOllamaRuntimeFingerprint -SourceRoot $SourceRoot -BaseImage "ollama/ollama:latest"
    $existingFingerprint = Get-FluxDockerImageLabel -Image "flux-ollama:local" -Label "org.flux_llm_kb.ollama.runtime_fingerprint"
    if ($existingFingerprint -eq $runtimeFingerprint) {
        Write-Host "Skipping Ollama runtime image build; flux-ollama:local already matches runtime fingerprint $runtimeFingerprint."
        return
    }
    $ollamaDockerfile = Join-Path $SourceRoot "docker\ollama\Dockerfile"
    $ollamaContext = Join-Path $SourceRoot "docker\ollama"
    $ollamaBuildArgs = @(
        "build",
        "--progress=plain",
        "--pull=false",
        "--build-arg", "OLLAMA_BASE_IMAGE=ollama/ollama:latest",
        "--build-arg", "FLUX_KB_IMAGE_REVISION=$($BuildMetadata.Revision)",
        "--build-arg", "FLUX_KB_IMAGE_SOURCE=$($BuildMetadata.Source)",
        "--build-arg", "FLUX_KB_IMAGE_CREATED=$($BuildMetadata.Created)",
        "--build-arg", "FLUX_KB_IMAGE_VERSION=$($BuildMetadata.Version)",
        "--build-arg", "FLUX_KB_OLLAMA_RUNTIME_FINGERPRINT=$runtimeFingerprint",
        "-f", $ollamaDockerfile,
        "-t", "flux-ollama:local", "-t", "flux-ollama:$imageTag",
        $ollamaContext
    )
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments $ollamaBuildArgs -WorkingDirectory $SourceRoot -TimeoutSeconds $TimeoutSeconds -StepName "docker build ollama runtime"
}

function Invoke-FluxAsrModelDownload {
    param(
        [string]$AppRoot,
        [string]$AppEnvPath,
        [string]$ComposePath,
        [int]$TimeoutSeconds
    )
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("volume", "create", "flux_llm_kb_asr_models") -WorkingDirectory $AppRoot -TimeoutSeconds 60 -StepName "docker create ASR model volume"
    Write-Host "Pre-downloading ASR model with: docker compose run --rm --no-deps asr python -m flux_llm_kb.asr_server download-model --model large-v3-turbo --output-dir /models/faster-whisper-large-v3-turbo"
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @(
        "compose",
        "--env-file", $AppEnvPath,
        "-f", $ComposePath,
        "run", "--pull", "never", "--rm", "--no-deps",
        "asr",
        "python", "-m", "flux_llm_kb.asr_server", "download-model", "--model", "large-v3-turbo", "--output-dir", "/models/faster-whisper-large-v3-turbo"
    ) -WorkingDirectory $AppRoot -TimeoutSeconds $TimeoutSeconds -StepName "download ASR model large-v3-turbo"
}

function Invoke-FluxModelRunnerModelDownload {
    param(
        [string]$AppRoot,
        [string]$AppEnvPath,
        [string]$ComposePath,
        [int]$TimeoutSeconds
    )
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("volume", "create", "flux_llm_kb_model_runner_models") -WorkingDirectory $AppRoot -TimeoutSeconds 60 -StepName "docker create model-runner model volume"
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("volume", "create", "flux_llm_kb_paddle_cache") -WorkingDirectory $AppRoot -TimeoutSeconds 60 -StepName "docker create PaddleOCR cache volume"
    Write-Host "Pre-downloading Snowflake, Qwen reranker, PP-OCRv5, and PaddleOCR-VL models with model-runner."
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @(
        "compose",
        "--env-file", $AppEnvPath,
        "-f", $ComposePath,
        "run", "--pull", "never", "--rm", "--no-deps",
        "model-runner",
        "python", "-m", "flux_llm_kb.model_runner", "download-models", "--models-dir", "/models"
    ) -WorkingDirectory $AppRoot -TimeoutSeconds $TimeoutSeconds -StepName "download model-runner models"
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @(
        "compose",
        "--env-file", $AppEnvPath,
        "-f", $ComposePath,
        "run", "--pull", "never", "--rm", "--no-deps",
        "paddle-runner",
        "/opt/flux-paddle/bin/python", "-m", "flux_llm_kb.model_runner", "download-paddle-models", "--models-dir", "/models"
    ) -WorkingDirectory $AppRoot -TimeoutSeconds $TimeoutSeconds -StepName "download PaddleOCR models"
}

function Copy-FluxVespaApplication {
    param([string]$SourceRoot, [string]$AppRoot)
    $vespaSource = Join-Path $SourceRoot "vespa"
    $vespaTarget = Join-Path $AppRoot "vespa"
    if (-not (Test-Path (Join-Path $vespaSource "services.xml"))) {
        throw "Vespa application package not found at $vespaSource"
    }
    if (Test-Path $vespaTarget) { Remove-Item -LiteralPath $vespaTarget -Recurse -Force }
    Copy-Item -Path $vespaSource -Destination $vespaTarget -Recurse -Force
}

function Invoke-FluxVespaApplicationDeploy {
    param([string]$AppRoot, [int]$TimeoutSeconds = 300)
    $vespaApp = Join-Path $AppRoot "vespa"
    if (-not (Test-Path (Join-Path $vespaApp "services.xml"))) {
        throw "Vespa application package not found at $vespaApp"
    }
    $deployWaitSeconds = [Math]::Max($TimeoutSeconds, 900)
    $deployTimeoutSeconds = $deployWaitSeconds + 60
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @(
        "exec", "flux-vespa", "sh", "-lc",
        "for i in `$(seq 1 120); do curl -fsS http://127.0.0.1:19071/ApplicationStatus >/dev/null && exit 0; sleep 2; done; exit 1"
    ) -WorkingDirectory $AppRoot -TimeoutSeconds $TimeoutSeconds -StepName "wait for Vespa application endpoint"
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("exec", "-u", "root", "flux-vespa", "sh", "-lc", "rm -rf /tmp/flux-vespa-app") -WorkingDirectory $AppRoot -TimeoutSeconds 60 -StepName "clear Vespa application temp package"
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("cp", $vespaApp, "flux-vespa:/tmp/flux-vespa-app") -WorkingDirectory $AppRoot -TimeoutSeconds 120 -StepName "copy Vespa application package"
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("exec", "flux-vespa", "sh", "-lc", "vespa deploy --wait $deployWaitSeconds /tmp/flux-vespa-app") -WorkingDirectory $AppRoot -TimeoutSeconds $deployTimeoutSeconds -StepName "deploy Vespa application package"
}

function Test-FluxDockerImageExists {
    param([string]$Image)
    if (-not $Image) { return $false }
    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = "docker"
    $processInfo.Arguments = "image inspect " + (ConvertTo-FluxCommandArgument $Image)
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    [void]$process.Start()
    $process.StandardOutput.ReadToEnd() | Out-Null
    $process.StandardError.ReadToEnd() | Out-Null
    $process.WaitForExit()
    return $process.ExitCode -eq 0
}

function Get-FluxDockerImageId {
    param([string]$Image)
    if (-not $Image) { return $null }
    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = "docker"
    $processInfo.Arguments = "image inspect --format " + (ConvertTo-FluxCommandArgument "{{.Id}}") + " " + (ConvertTo-FluxCommandArgument $Image)
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $process.StandardError.ReadToEnd() | Out-Null
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { return $null }
    return $stdout.Trim()
}

function Get-FluxDockerImageLabel {
    param([string]$Image, [string]$Label)
    if (-not $Image -or -not $Label) { return $null }
    $template = "{{ index .Config.Labels `"$Label`" }}"
    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = "docker"
    $processInfo.Arguments = "image inspect --format " + (ConvertTo-FluxCommandArgument $template) + " " + (ConvertTo-FluxCommandArgument $Image)
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $process.StandardError.ReadToEnd() | Out-Null
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { return $null }
    $value = $stdout.Trim()
    if (-not $value -or $value -eq "<no value>") { return $null }
    return $value
}

function Add-FluxFingerprintText {
    param([System.IO.Stream]$Stream, [string]$Text)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    if ($bytes.Length -gt 0) {
        $Stream.Write($bytes, 0, $bytes.Length)
    }
}

function Get-FluxOllamaRuntimeFingerprint {
    param([string]$SourceRoot, [string]$BaseImage)
    $ollamaContext = Join-Path $SourceRoot "docker\ollama"
    if (-not (Test-Path $ollamaContext)) {
        throw "Ollama Docker context not found at $ollamaContext"
    }
    $baseImageId = Get-FluxDockerImageId -Image $BaseImage
    if (-not $baseImageId) {
        throw "Unable to inspect Ollama base image: $BaseImage"
    }

    $resolvedContext = (Resolve-Path $ollamaContext).Path.TrimEnd("\", "/")
    $stream = [System.IO.MemoryStream]::new()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        Add-FluxFingerprintText -Stream $stream -Text "base-image`n$BaseImage`n$baseImageId`n"
        $files = @(Get-ChildItem -LiteralPath $resolvedContext -File -Recurse | Sort-Object FullName)
        foreach ($file in $files) {
            $relativePath = $file.FullName.Substring($resolvedContext.Length + 1).Replace("\", "/")
            Add-FluxFingerprintText -Stream $stream -Text "file`n$relativePath`n$($file.Length)`n"
            $fileBytes = [System.IO.File]::ReadAllBytes($file.FullName)
            if ($fileBytes.Length -gt 0) {
                $stream.Write($fileBytes, 0, $fileBytes.Length)
            }
            Add-FluxFingerprintText -Stream $stream -Text "`n"
        }
        $stream.Position = 0
        return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace "-", "").ToLowerInvariant()
    } finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function New-FluxLocalDockerBaseAlias {
    param([string]$Image)
    $imageId = Get-FluxDockerImageId -Image $Image
    if (-not $imageId) {
        throw "Unable to inspect Docker base image: $Image"
    }
    $shortId = $imageId -replace "^sha256:", ""
    if ($shortId.Length -gt 12) {
        $shortId = $shortId.Substring(0, 12)
    }
    $alias = "localhost/flux-llm-kb-build-base:$shortId"
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("tag", $Image, $alias) -WorkingDirectory $SourceRoot -TimeoutSeconds 60 -StepName "tag local Docker build base"
    return $alias
}

function Resolve-FluxDockerBuildBase {
    param(
        [ValidateSet("auto", "local", "python")]
        [string]$DockerBaseMode,
        [string]$DockerBaseImage,
        [switch]$AllowImagePull
    )
    $pythonBase = "python:3.12-slim"
    if ($DockerBaseMode -eq "python") {
        if (-not $AllowImagePull -and -not (Test-FluxDockerImageExists -Image $pythonBase)) {
            throw "Docker build base is not available locally: $pythonBase. Re-run explicitly with -AllowImagePull to download it."
        }
        return [pscustomobject]@{ Image = $pythonBase; SkipSystemPackages = $false }
    }

    $candidateImage = if ($DockerBaseImage) { $DockerBaseImage } else { "flux-llm-kb-api:local" }
    $candidateExists = Test-FluxDockerImageExists -Image $candidateImage
    if ($DockerBaseMode -eq "local" -and -not $candidateExists) {
        throw "Docker base mode 'local' requires an existing image: $candidateImage"
    }
    if ($candidateExists) {
        $baseAlias = New-FluxLocalDockerBaseAlias -Image $candidateImage
        Write-Host "Reusing Docker base image $candidateImage as $baseAlias; Debian system packages will not be reinstalled."
        return [pscustomobject]@{ Image = $baseAlias; SkipSystemPackages = $true }
    }
    if (-not $AllowImagePull) {
        throw "No local Docker build base is available: $candidateImage. Re-run explicitly with -AllowImagePull to permit the python base-image fallback."
    }
    if ($DockerBaseImage) {
        Write-Warning "Requested Docker base image $DockerBaseImage was not found; falling back to $pythonBase."
    }
    return [pscustomobject]@{ Image = $pythonBase; SkipSystemPackages = $false }
}

function Resolve-FluxPipWheelhousePath {
    param(
        [string]$RequestedPath,
        [string]$InstallRoot
    )
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return [System.IO.Path]::GetFullPath($RequestedPath)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $InstallRoot "package-cache\wheelhouse"))
}

function Resolve-FluxPipWheelhouseImage {
    param([string]$RequestedImage)
    if (-not [string]::IsNullOrWhiteSpace($RequestedImage)) {
        return $RequestedImage
    }
    return "flux-llm-kb-wheelhouse:local"
}

function Resolve-FluxNpmCachePath {
    param(
        [string]$RequestedPath,
        [string]$InstallRoot
    )
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return [System.IO.Path]::GetFullPath($RequestedPath)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $InstallRoot "package-cache\npm"))
}

function Get-FluxWheelhouseFileCount {
    param([string]$WheelhousePath)
    if (-not (Test-Path -LiteralPath $WheelhousePath)) {
        return 0
    }
    return @(
        Get-ChildItem -LiteralPath $WheelhousePath -File -Filter "*.whl" -ErrorAction SilentlyContinue
    ).Count
}

function Assert-FluxWheelhouseCacheReady {
    param(
        [string]$WheelhousePath,
        [string]$WheelhouseImage
    )
    if ((Get-FluxWheelhouseFileCount -WheelhousePath $WheelhousePath) -eq 0) {
        throw "Durable pip wheelhouse is empty at $WheelhousePath. Seed it explicitly with: .\scripts\deploy\update-flux.ps1 -PipOffline:`$false"
    }
    if (-not (Test-FluxDockerImageExists -Image $WheelhouseImage)) {
        throw "Pip wheelhouse image $WheelhouseImage is missing after offline cache validation. Prefetch missing wheels into the persistent wheelhouse, then rebuild the wheelhouse image."
    }
}

function Invoke-FluxBuildWheelhouseImage {
    param(
        [string]$SourceRoot,
        [string]$WheelhousePath,
        [string]$WheelhouseImage,
        [int]$TimeoutSeconds
    )
    $wheelhouseDockerfile = Join-Path $SourceRoot "docker\wheelhouse.Dockerfile"
    if (-not (Test-Path -LiteralPath $wheelhouseDockerfile)) {
        throw "Wheelhouse image Dockerfile not found at $wheelhouseDockerfile"
    }
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @(
        "build",
        "--progress=plain",
        "--pull=false",
        "-f", $wheelhouseDockerfile,
        "-t", $WheelhouseImage,
        $WheelhousePath
    ) -WorkingDirectory $SourceRoot -TimeoutSeconds $TimeoutSeconds -StepName "build pip wheelhouse image"
}

function Invoke-FluxSeedDockerWheelhouse {
    param(
        [string]$SourceRoot,
        [string]$WheelhousePath,
        [string]$SeederImage,
        [string]$PipIndexUrl,
        [string]$PaddleGpuIndexUrl,
        [string]$PytorchGpuIndexUrl,
        [int]$PipTimeoutSeconds,
        [int]$PipRetries,
        [int]$TimeoutSeconds
    )

    $seedScript = @'
set -eu
cd /tmp
cp /src/pyproject.toml ./pyproject.toml
cp /src/docker/requirements-docker.lock /tmp/requirements-docker.lock
cp /src/docker/requirements-paddle.lock /tmp/requirements-paddle.lock
python - <<'PY'
import tomllib
from pathlib import Path

config = tomllib.loads(Path("pyproject.toml").read_text(encoding="utf-8"))
optional = config["project"].get("optional-dependencies", {})
build_requirements = list(config.get("build-system", {}).get("requires", []))

def write_requirements(path: str, extras: tuple[str, ...]) -> None:
    requirements = build_requirements + list(config["project"]["dependencies"])
    for extra in extras:
        requirements.extend(optional.get(extra, []))
    Path(path).write_text("\n".join(requirements) + "\n", encoding="utf-8")

write_requirements("/tmp/requirements-docker.txt", ("api", "corpus", "mcp", "processors", "asr_gpu"))
write_requirements("/tmp/requirements-paddle.txt", ("api", "ocr_paddle"))
PY
python -m venv /tmp/flux-paddle
export PIP_CACHE_DIR=/wheelhouse/.pip-cache
mkdir -p /wheelhouse "$PIP_CACHE_DIR"
pip_extra_index_args=""
if [ -n "$PADDLE_GPU_INDEX_URL" ]; then pip_extra_index_args="$pip_extra_index_args --extra-index-url $PADDLE_GPU_INDEX_URL"; fi
if [ -n "$PYTORCH_GPU_INDEX_URL" ]; then pip_extra_index_args="$pip_extra_index_args --extra-index-url $PYTORCH_GPU_INDEX_URL"; fi
if [ -n "$PIP_INDEX_URL" ]; then
    pip_index_args="--index-url $PIP_INDEX_URL"
else
    pip_index_args=""
fi
download_requirements() {
    python_bin="$1"
    requirements="$2"
    constraint="$3"
    "$python_bin" -m pip download --only-binary=:all: --timeout "$PIP_DEFAULT_TIMEOUT" --retries "$PIP_RETRIES" --find-links /wheelhouse $pip_index_args $pip_extra_index_args --constraint "$constraint" --dest /wheelhouse -r "$requirements"
}
download_requirements python /tmp/requirements-docker.txt /tmp/requirements-docker.lock
download_requirements /tmp/flux-paddle/bin/python /tmp/requirements-paddle.txt /tmp/requirements-paddle.lock
'@

    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @(
        "run", "--rm",
        "--network", "default",
        "-e", "PIP_INDEX_URL=$PipIndexUrl",
        "-e", "PADDLE_GPU_INDEX_URL=$PaddleGpuIndexUrl",
        "-e", "PYTORCH_GPU_INDEX_URL=$PytorchGpuIndexUrl",
        "-e", "PIP_DEFAULT_TIMEOUT=$PipTimeoutSeconds",
        "-e", "PIP_RETRIES=$PipRetries",
        "-v", "${SourceRoot}:/src:ro",
        "-v", "${WheelhousePath}:/wheelhouse",
        $SeederImage,
        "sh", "-lc", $seedScript
    ) -WorkingDirectory $SourceRoot -TimeoutSeconds $TimeoutSeconds -StepName "seed durable pip wheelhouse"
}

function Invoke-FluxSeedHostPipWheelhouse {
    param(
        [string]$SourceRoot,
        [string]$WheelhousePath,
        [string]$PythonExe,
        [string]$PipIndexUrl,
        [int]$PipTimeoutSeconds,
        [int]$PipRetries,
        [int]$TimeoutSeconds
    )
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("flux-host-wheelhouse-" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    try {
        $requirementsPath = Join-Path $tempRoot "requirements-host.txt"
        $generator = @'
import sys
import tomllib
from pathlib import Path

source_root = Path(sys.argv[1])
requirements_path = Path(sys.argv[2])
config = tomllib.loads((source_root / "pyproject.toml").read_text(encoding="utf-8"))
optional = config["project"].get("optional-dependencies", {})
requirements = ["pip"]
requirements.extend(config.get("build-system", {}).get("requires", []))
requirements.extend(config["project"]["dependencies"])
for extra in ("api", "corpus", "mail", "mcp", "processors"):
    requirements.extend(optional.get(extra, []))
requirements_path.write_text("\n".join(requirements) + "\n", encoding="utf-8")
'@
        Invoke-FluxNativeCommand -FilePath $PythonExe -Arguments @(
            "-c", $generator,
            $SourceRoot,
            $requirementsPath
        ) -WorkingDirectory $SourceRoot -TimeoutSeconds 60 -StepName "write host pip wheelhouse requirements"
        $pipDownloadArgs = @(
            "-m", "pip", "download",
            "--only-binary=:all:",
            "--timeout", ([string]$PipTimeoutSeconds),
            "--retries", ([string]$PipRetries),
            "--cache-dir", (Join-Path $WheelhousePath ".pip-cache"),
            "--find-links", $WheelhousePath,
            "--dest", $WheelhousePath,
            "-r", $requirementsPath
        )
        if ($PipIndexUrl) {
            $pipDownloadArgs += @("--index-url", $PipIndexUrl)
        }
        Invoke-FluxNativeCommand -FilePath $PythonExe -Arguments $pipDownloadArgs -WorkingDirectory $SourceRoot -TimeoutSeconds $TimeoutSeconds -StepName "seed host pip wheelhouse"
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-FluxDashboardBuild {
    param(
        [string]$DashboardRoot,
        [bool]$NpmOffline,
        [string]$NpmCachePath
    )
    $viteCmd = Join-Path $DashboardRoot "node_modules\.bin\vite.cmd"
    if (-not (Test-Path -LiteralPath $viteCmd)) {
        if ($NpmOffline) {
            throw "Dashboard dependency cache is incomplete. Missing $viteCmd. Seed npm dependencies explicitly with: npm --prefix $DashboardRoot ci --include=dev --cache `"$NpmCachePath`" --prefer-offline"
        }
        New-Item -ItemType Directory -Force -Path $NpmCachePath | Out-Null
        npm --prefix $DashboardRoot ci --include=dev --cache $NpmCachePath --prefer-offline
        if ($LASTEXITCODE -ne 0) {
            throw "npm ci failed for dashboard dependencies with exit code $LASTEXITCODE"
        }
    }
    npm --prefix $DashboardRoot run build
    if ($LASTEXITCODE -ne 0) {
        throw "npm run build failed for dashboard with exit code $LASTEXITCODE"
    }
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
        [bool]$GpuEnabled,
        [bool]$SkipWorkerStart,
        [int]$TimeoutSeconds
    )
    Invoke-FluxDockerComposeServicesUp `
        -AppRoot $AppRoot `
        -AppEnvPath $AppEnvPath `
        -ComposePath $ComposePath `
        -Services @("postgres", "rabbitmq", "vespa") `
        -Containers @("flux-llm-kb-postgres", "flux-llm-kb-rabbitmq", "flux-vespa") `
        -RecoverableContainers @("flux-llm-kb-rabbitmq", "flux-vespa") `
        -TimeoutSeconds $TimeoutSeconds
    Invoke-FluxVespaApplicationDeploy -AppRoot $AppRoot -TimeoutSeconds 300
    $commandWorkerServices = @("worker", "search-index-worker", "mail-worker", "automation-worker", "governance-worker", "runtime-control-worker", "gpu-eviction-worker")
    $commandWorkerContainers = @("flux-llm-kb-worker", "flux-llm-kb-search-index-worker", "flux-llm-kb-mail-worker", "flux-llm-kb-automation-worker", "flux-llm-kb-governance-worker", "flux-llm-kb-runtime-control-worker", "flux-llm-kb-gpu-eviction-worker")
    $eventSubscriberServices = @("event-audit-worker", "event-dashboard-worker", "event-diagnostics-worker")
    $eventSubscriberContainers = @("flux-llm-kb-event-audit-worker", "flux-llm-kb-event-dashboard-worker", "flux-llm-kb-event-diagnostics-worker")
    $services = @("paddle-runner", "model-runner", "api") + $commandWorkerServices + @("event-scheduler", "callback-worker") + $eventSubscriberServices + @("outbox-relay")
    $containers = @("flux-llm-kb-paddle-runner", "flux-llm-kb-model-runner", "flux-llm-kb-api") + $commandWorkerContainers + @("flux-llm-kb-event-scheduler", "flux-llm-kb-callback-worker") + $eventSubscriberContainers + @("flux-llm-kb-outbox-relay")
    $recoverableContainers = @("flux-llm-kb-paddle-runner", "flux-llm-kb-model-runner", "flux-llm-kb-api") + $commandWorkerContainers + @("flux-llm-kb-event-scheduler", "flux-llm-kb-callback-worker") + $eventSubscriberContainers + @("flux-llm-kb-outbox-relay")
    if ($GpuEnabled) {
        $services = @("paddle-runner", "model-runner", "ollama", "asr", "api") + $commandWorkerServices + @("event-scheduler", "callback-worker") + $eventSubscriberServices + @("outbox-relay")
        $containers = @("flux-llm-kb-paddle-runner", "flux-llm-kb-model-runner", "flux-ollama", "flux-llm-kb-asr", "flux-llm-kb-api") + $commandWorkerContainers + @("flux-llm-kb-event-scheduler", "flux-llm-kb-callback-worker") + $eventSubscriberContainers + @("flux-llm-kb-outbox-relay")
        $recoverableContainers = @("flux-llm-kb-paddle-runner", "flux-llm-kb-model-runner", "flux-ollama", "flux-llm-kb-asr", "flux-llm-kb-api") + $commandWorkerContainers + @("flux-llm-kb-event-scheduler", "flux-llm-kb-callback-worker") + $eventSubscriberContainers + @("flux-llm-kb-outbox-relay")
    }
    if ($SkipWorkerStart) {
        $services = @($services | Where-Object { $commandWorkerServices -notcontains $_ })
        $containers = @($containers | Where-Object { $commandWorkerContainers -notcontains $_ })
        $recoverableContainers = @($recoverableContainers | Where-Object { $commandWorkerContainers -notcontains $_ })
    }
    Invoke-FluxDockerComposeServicesUp `
        -AppRoot $AppRoot `
        -AppEnvPath $AppEnvPath `
        -ComposePath $ComposePath `
        -Services $services `
        -Containers $containers `
        -RecoverableContainers $recoverableContainers `
        -TimeoutSeconds $TimeoutSeconds
}

function Invoke-FluxDockerComposeServicesUp {
    param(
        [string]$AppRoot,
        [string]$AppEnvPath,
        [string]$ComposePath,
        [string[]]$Services,
        [string[]]$Containers,
        [string[]]$RecoverableContainers,
        [int]$TimeoutSeconds
    )
    $arguments = @(
        "compose",
        "--env-file", $AppEnvPath,
        "-f", $ComposePath,
        "up", "-d", "--no-build", "--pull", "never"
    ) + $Services
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
        Start-FluxCreatedContainers -ContainerNames $RecoverableContainers
        Wait-FluxContainersRunning -ContainerNames $Containers
        return
    }

    $process.WaitForExit()
    Write-FluxProcessOutput -Stdout (Get-FluxTaskResult -Task $stdoutTask) -Stderr (Get-FluxTaskResult -Task $stderrTask)
    if ($process.ExitCode -ne 0) {
        Start-FluxCreatedContainers -ContainerNames $RecoverableContainers
        try {
            Wait-FluxContainersRunning -ContainerNames $Containers
            Write-Warning "docker compose up exited with $($process.ExitCode), but required Flux containers are running after recovery."
            return
        } catch {
            throw "docker compose up failed with exit code $($process.ExitCode)."
        }
    }
    Start-FluxCreatedContainers -ContainerNames $RecoverableContainers
    Wait-FluxContainersRunning -ContainerNames $Containers
}

if (-not (Test-Path $composePath)) {
    throw "Flux production runtime is not installed at $InstallRoot. Run install-flux.ps1 first."
}

$buildMetadata = Get-FluxBuildMetadata -Root $SourceRoot
$imageTag = $buildMetadata.ShortRevision
$resolvedPipWheelhousePath = Resolve-FluxPipWheelhousePath -RequestedPath $PipWheelhousePath -InstallRoot $InstallRoot
$resolvedPipWheelhouseImage = Resolve-FluxPipWheelhouseImage -RequestedImage $PipWheelhouseImage
$resolvedPipCachePath = Join-Path $resolvedPipWheelhousePath ".pip-cache"
$resolvedNpmCachePath = Resolve-FluxNpmCachePath -RequestedPath $NpmCachePath -InstallRoot $InstallRoot
New-Item -ItemType Directory -Force -Path $resolvedPipWheelhousePath, $resolvedPipCachePath, $resolvedNpmCachePath | Out-Null

if (-not $SkipDashboardBuild) {
    Invoke-FluxDashboardBuild -DashboardRoot (Join-Path $SourceRoot "dashboard") -NpmOffline $NpmOffline -NpmCachePath $resolvedNpmCachePath
}

$resolvedPython = Resolve-FluxPythonExe -InstallRoot $InstallRoot -RequestedPython $PythonExe

$dockerBase = Resolve-FluxDockerBuildBase -DockerBaseMode $DockerBaseMode -DockerBaseImage $DockerBaseImage -AllowImagePull:$AllowImagePull
if (-not $PipOffline) {
    Invoke-FluxSeedDockerWheelhouse `
        -SourceRoot $SourceRoot `
        -WheelhousePath $resolvedPipWheelhousePath `
        -SeederImage $dockerBase.Image `
        -PipIndexUrl $PipIndexUrl `
        -PaddleGpuIndexUrl $PaddleGpuIndexUrl `
        -PytorchGpuIndexUrl $PytorchGpuIndexUrl `
        -PipTimeoutSeconds $PipTimeoutSeconds `
        -PipRetries $PipRetries `
        -TimeoutSeconds $DockerBuildTimeoutSeconds
    Invoke-FluxSeedHostPipWheelhouse `
        -SourceRoot $SourceRoot `
        -WheelhousePath $resolvedPipWheelhousePath `
        -PythonExe $resolvedPython `
        -PipIndexUrl $PipIndexUrl `
        -PipTimeoutSeconds $PipTimeoutSeconds `
        -PipRetries $PipRetries `
        -TimeoutSeconds $PipInstallTimeoutSeconds
    Invoke-FluxBuildWheelhouseImage `
        -SourceRoot $SourceRoot `
        -WheelhousePath $resolvedPipWheelhousePath `
        -WheelhouseImage $resolvedPipWheelhouseImage `
        -TimeoutSeconds $DockerBuildTimeoutSeconds
}
if ($PipOffline -and -not (Test-FluxDockerImageExists -Image $resolvedPipWheelhouseImage) -and ((Get-FluxWheelhouseFileCount -WheelhousePath $resolvedPipWheelhousePath) -gt 0)) {
    Invoke-FluxBuildWheelhouseImage `
        -SourceRoot $SourceRoot `
        -WheelhousePath $resolvedPipWheelhousePath `
        -WheelhouseImage $resolvedPipWheelhouseImage `
        -TimeoutSeconds $DockerBuildTimeoutSeconds
}
Assert-FluxWheelhouseCacheReady -WheelhousePath $resolvedPipWheelhousePath -WheelhouseImage $resolvedPipWheelhouseImage
$skipSystemPackages = if ($dockerBase.SkipSystemPackages) { "true" } else { "false" }
$dockerBuildNetwork = if ($dockerBase.SkipSystemPackages) { "none" } else { "default" }
$dockerBuildArgs = @(
    "build",
    "--progress=plain",
    "--pull=false",
    "--network", $dockerBuildNetwork,
    "--build-context", "flux-wheelhouse=docker-image://$resolvedPipWheelhouseImage",
    "--build-arg", "FLUX_KB_IMAGE_REVISION=$($buildMetadata.Revision)",
    "--build-arg", "FLUX_KB_IMAGE_SOURCE=$($buildMetadata.Source)",
    "--build-arg", "FLUX_KB_IMAGE_CREATED=$($buildMetadata.Created)",
    "--build-arg", "FLUX_KB_IMAGE_VERSION=$($buildMetadata.Version)",
    "--build-arg", "FLUX_KB_DOCKER_BASE_IMAGE=$($dockerBase.Image)",
    "--build-arg", "FLUX_KB_SKIP_SYSTEM_PACKAGES=$skipSystemPackages"
)
if ($AptDebianMirrorUrl) {
    $dockerBuildArgs += @("--build-arg", "APT_DEBIAN_MIRROR_URL=$AptDebianMirrorUrl")
}
if ($AptSecurityMirrorUrl) {
    $dockerBuildArgs += @("--build-arg", "APT_SECURITY_MIRROR_URL=$AptSecurityMirrorUrl")
}
$dockerBuildArgs += @("-t", "flux-llm-kb-api:$imageTag", "-t", "flux-llm-kb-api:local", $SourceRoot)
Invoke-FluxNativeCommand -FilePath "docker" -Arguments $dockerBuildArgs -WorkingDirectory $SourceRoot -TimeoutSeconds $DockerBuildTimeoutSeconds -StepName "docker build"
Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("tag", "flux-llm-kb-api:$imageTag", "flux-llm-kb-worker:$imageTag") -WorkingDirectory $SourceRoot -TimeoutSeconds 60 -StepName "docker tag worker version"
Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("tag", "flux-llm-kb-api:$imageTag", "flux-llm-kb-worker:local") -WorkingDirectory $SourceRoot -TimeoutSeconds 60 -StepName "docker tag worker local"
Invoke-FluxOllamaImageBuild -SourceRoot $SourceRoot -BuildMetadata $buildMetadata -ImageTag $imageTag -TimeoutSeconds $DockerBuildTimeoutSeconds -AllowImagePull:$AllowImagePull
Invoke-FluxDockerImageAvailable -Image "postgres:16" -WorkingDirectory $SourceRoot -TimeoutSeconds 300 -AllowImagePull:$AllowImagePull
Invoke-FluxDockerImageAvailable -Image "rabbitmq:4.3-management" -WorkingDirectory $SourceRoot -TimeoutSeconds 300 -AllowImagePull:$AllowImagePull

$gpuEnabled = Assert-FluxGpuAvailable -ImageTag $imageTag -GpuMode $GpuMode

$pluginSource = Join-Path $SourceRoot "plugins"
$pluginTarget = Join-Path $appRoot "plugins"
if (Test-Path $pluginSource) {
    if (Test-Path $pluginTarget) { Remove-Item -LiteralPath $pluginTarget -Recurse -Force }
    Copy-Item -Path $pluginSource -Destination $pluginTarget -Recurse -Force
}
Copy-FluxVespaApplication -SourceRoot $SourceRoot -AppRoot $appRoot

& (Join-Path $PSScriptRoot "migrate-postgres-to-docker-volume.ps1") -InstallRoot $InstallRoot -PostgresPort $PostgresPort
Write-FluxCompose -TargetPath $composePath -ImageTag $imageTag -ApiPort $ApiPort -PostgresPort $PostgresPort -OllamaHostPort $OllamaHostPort -AsrHostPort $AsrHostPort -GpuEnabled $gpuEnabled
$imageMetadataEnv = @"
FLUX_KB_IMAGE_TAG=$imageTag
FLUX_KB_IMAGE_REVISION=$($buildMetadata.Revision)
FLUX_KB_IMAGE_SOURCE=$($buildMetadata.Source)
FLUX_KB_IMAGE_CREATED=$($buildMetadata.Created)
FLUX_KB_IMAGE_VERSION=$($buildMetadata.Version)
"@
Set-Content -Path $appEnvPath -Value "$imageMetadataEnv`n" -Encoding UTF8
Set-Content -Path (Join-Path $appRoot "VERSION") -Value $imageTag -Encoding UTF8
Write-FluxEnv -TargetPath $privateEnvPath -InstallRoot $InstallRoot -ImageTag $imageTag -PostgresPort $PostgresPort -GpuEnabled $gpuEnabled
New-Item -ItemType Directory -Force -Path $modelsRoot, $ollamaModelsRoot | Out-Null

if ($RecreateVenv -and (Test-Path $venvRoot)) {
    Remove-Item -LiteralPath $venvRoot -Recurse -Force
}
if (-not (Test-Path $venvPython)) {
    & $resolvedPython -m venv $venvRoot
}
$pipCommonArgs = @(
    "--timeout", ([string]$PipTimeoutSeconds),
    "--retries", ([string]$PipRetries),
    "--cache-dir", $resolvedPipCachePath,
    "--no-index", "--find-links", $resolvedPipWheelhousePath
)
Invoke-FluxNativeCommand -FilePath $venvPython -Arguments (@("-m", "pip", "install") + $pipCommonArgs + @("--upgrade", "pip")) -WorkingDirectory $SourceRoot -TimeoutSeconds $PipInstallTimeoutSeconds -StepName "pip upgrade"
Invoke-FluxNativeCommand -FilePath $venvPython -Arguments (@("-m", "pip", "install") + $pipCommonArgs + @("$SourceRoot[api,corpus,mail,mcp,processors]")) -WorkingDirectory $SourceRoot -TimeoutSeconds $PipInstallTimeoutSeconds -StepName "pip install production extras"
Invoke-FluxNativeCommand -FilePath $venvPython -Arguments (@("-m", "pip", "install") + $pipCommonArgs + @("--force-reinstall", "--no-deps", "--no-build-isolation", $SourceRoot)) -WorkingDirectory $SourceRoot -TimeoutSeconds $PipInstallTimeoutSeconds -StepName "pip install production package"
Invoke-FluxCodexPluginInstall -VenvPython $venvPython -InstallRoot $InstallRoot
Write-FluxHostScripts -AppRoot $appRoot -InstallRoot $InstallRoot -HostAgentPort $HostAgentPort -PostgresPort $PostgresPort -GpuEnabled $gpuEnabled -OllamaHostPort $OllamaHostPort -AsrHostPort $AsrHostPort
Remove-FluxLegacyConsoleLaunchers -AppRoot $appRoot
Stop-FluxOutlookHostLaunchers -InstallRoot $InstallRoot
if ($SkipWorkerStart) {
    Stop-FluxWorkerContainer
}
if ($gpuEnabled) {
    Invoke-FluxAsrModelDownload -AppRoot $appRoot -AppEnvPath $appEnvPath -ComposePath $composePath -TimeoutSeconds $AsrModelDownloadTimeoutSeconds
    Invoke-FluxModelRunnerModelDownload -AppRoot $appRoot -AppEnvPath $appEnvPath -ComposePath $composePath -TimeoutSeconds $ModelRunnerModelDownloadTimeoutSeconds
}

Push-Location $appRoot
try {
    Invoke-FluxDockerComposeUp -AppRoot $appRoot -AppEnvPath $appEnvPath -ComposePath $composePath -GpuEnabled $gpuEnabled -SkipWorkerStart ([bool]$SkipWorkerStart) -TimeoutSeconds $DockerComposeTimeoutSeconds
} finally {
    Pop-Location
}
Invoke-FluxMigration -VenvPython $venvPython -InstallRoot $InstallRoot -PostgresPort $PostgresPort
Set-FluxProductionRuntimeSettings -VenvPython $venvPython -InstallRoot $InstallRoot -PostgresPort $PostgresPort -GpuEnabled $gpuEnabled -OllamaHostPort $OllamaHostPort -AsrHostPort $AsrHostPort

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
            if ($taskSpec.Port) { Stop-FluxHostAgentLaunchers -InstallRoot $InstallRoot }
            Wait-FluxTaskStopped -TaskName $taskSpec.Name
            if ($taskSpec.Port) { Wait-FluxTcpClosed -Port $taskSpec.Port }
        }
    }
    Register-FluxTask -TaskName $taskSpec.Name -LauncherPath $taskSpec.Launcher -AppRoot $appRoot
    if ($SkipWorkerStart) {
        Disable-ScheduledTask -TaskName $taskSpec.Name | Out-Null
        continue
    }
    if ($wasRunning -or $RestartHostTasks) {
        Start-ScheduledTask -TaskName $taskSpec.Name
        if ($taskSpec.Port) { Wait-FluxTcpOpen -Port $taskSpec.Port }
    }
}

Write-Host "Flux production runtime updated at $InstallRoot to image tag $imageTag"
if ($gpuEnabled) {
    Write-Host "Install the Docker Ollama vision model with: docker exec flux-ollama ollama pull qwen3-vl:8b"
    Write-Host "Docker Ollama host-agent URL: http://127.0.0.1:$OllamaHostPort"
    Write-Host "Docker ASR host-agent URL: http://127.0.0.1:$AsrHostPort"
}
