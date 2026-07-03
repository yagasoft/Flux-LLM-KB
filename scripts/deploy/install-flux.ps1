param(
    [string]$InstallRoot = "D:\FluxLLMKB",
    [string]$SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [int]$ApiPort = 8765,
    [int]$PostgresPort = 5432,
    [int]$HostAgentPort = 8799,
    [int]$OllamaHostPort = 11435,
    [int]$AsrHostPort = 8788,
    [string]$PythonExe = "",
    [ValidateSet("auto", "on", "off")]
    [string]$GpuMode = "auto",
    [switch]$SkipDashboardBuild,
    [switch]$SkipScheduledTasks,
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
    [string]$PipIndexUrl = $env:FLUX_KB_PIP_INDEX_URL,
    [string]$PaddleGpuIndexUrl = $(if ($env:FLUX_KB_PADDLE_GPU_INDEX_URL) { $env:FLUX_KB_PADDLE_GPU_INDEX_URL } else { "https://www.paddlepaddle.org.cn/packages/stable/cu126/" }),
    [string]$PytorchGpuIndexUrl = $(if ($env:FLUX_KB_PYTORCH_GPU_INDEX_URL) { $env:FLUX_KB_PYTORCH_GPU_INDEX_URL } else { "https://download.pytorch.org/whl/cu126" }),
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

function Resolve-FluxPythonExe {
    param([string]$InstallRoot, [string]$RequestedPython)
    if ($RequestedPython) { return $RequestedPython }
    $installedPython = Join-Path $InstallRoot "python\python.exe"
    if (Test-Path $installedPython) { return $installedPython }
    return "python"
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
    mem_limit: "2gb"
    memswap_limit: "2gb"
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
    env_file:
      - ../private/flux.env
    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_GPU_SCHEDULER_MODE: postgres
      FLUX_KB_GPU_SCHEDULER_VRAM_BUDGET_MB: "10240"
      FLUX_KB_GPU_SCHEDULER_SAFETY_MARGIN_MB: "1024"
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
      FLUX_KB_RETRIEVAL_RERANK_TOP_N: "80"
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
    mem_limit: "2gb"
    memswap_limit: "2gb"
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
    env_file:
      - ../private/flux.env
    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_GPU_SCHEDULER_MODE: postgres
      FLUX_KB_GPU_SCHEDULER_VRAM_BUDGET_MB: "10240"
      FLUX_KB_GPU_SCHEDULER_SAFETY_MARGIN_MB: "1024"
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
      FLUX_KB_RETRIEVAL_RERANK_TOP_N: "80"
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
             python -m flux_llm_kb.cli crawl worker run --exclude-host-agent-roots --interval 5"

  asr:
    image: flux-llm-kb-api:`${FLUX_KB_IMAGE_TAG}
    labels:
      org.opencontainers.image.revision: `${FLUX_KB_IMAGE_REVISION}
      org.opencontainers.image.source: `${FLUX_KB_IMAGE_SOURCE}
      org.opencontainers.image.created: `${FLUX_KB_IMAGE_CREATED}
      org.opencontainers.image.version: `${FLUX_KB_IMAGE_VERSION}
    container_name: flux-llm-kb-asr
    restart: unless-stopped
    mem_limit: "4gb"
    memswap_limit: "4gb"
    gpus: all
    environment:
      NVIDIA_VISIBLE_DEVICES: all
      NVIDIA_DRIVER_CAPABILITIES: compute,utility
      FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb
      FLUX_KB_GPU_SCHEDULER_MODE: postgres
      FLUX_KB_GPU_SCHEDULER_VRAM_BUDGET_MB: "10240"
      FLUX_KB_GPU_SCHEDULER_SAFETY_MARGIN_MB: "1024"
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
      test: ["CMD", "python", "-m", "flux_llm_kb.asr_server", "health"]
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
    mem_limit: "10gb"
    memswap_limit: "10gb"
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
      test: ["CMD", "python", "-m", "flux_llm_kb.model_runner", "health"]
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
    mem_limit: "8gb"
    memswap_limit: "8gb"
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
      test: ["CMD", "/opt/flux-paddle/bin/python", "-m", "flux_llm_kb.model_runner", "health", "--role", "paddle-runner"]
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
    mem_limit: "6gb"
    memswap_limit: "6gb"
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
    mem_limit: "5gb"
    memswap_limit: "5gb"
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

  postgres:
    image: postgres:16
    container_name: flux-llm-kb-postgres
    restart: unless-stopped
    mem_limit: "3gb"
    memswap_limit: "3gb"
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
        Invoke-FluxSettingsCommand -VenvPython $VenvPython -Arguments @("settings", "set", "retrieval.rerank_top_n", "80", "--confirm")
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
        [int]$TimeoutSeconds = 300
    )
    if (Test-FluxDockerImageExists -Image $Image) { return }
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("pull", $Image) -WorkingDirectory $WorkingDirectory -TimeoutSeconds $TimeoutSeconds -StepName "docker pull $Image"
}

function Invoke-FluxOllamaImageBuild {
    param(
        [string]$SourceRoot,
        [pscustomobject]$BuildMetadata,
        [string]$ImageTag,
        [int]$TimeoutSeconds
    )
    Invoke-FluxDockerImageAvailable -Image "ollama/ollama:latest" -WorkingDirectory $SourceRoot -TimeoutSeconds 600
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
        "run", "--rm", "--no-deps",
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
        "run", "--rm", "--no-deps",
        "model-runner",
        "python", "-m", "flux_llm_kb.model_runner", "download-models", "--models-dir", "/models"
    ) -WorkingDirectory $AppRoot -TimeoutSeconds $TimeoutSeconds -StepName "download model-runner models"
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @(
        "compose",
        "--env-file", $AppEnvPath,
        "-f", $ComposePath,
        "run", "--rm", "--no-deps",
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
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @(
        "exec", "flux-vespa", "sh", "-lc",
        "for i in $(seq 1 120); do curl -fsS http://127.0.0.1:8080/ApplicationStatus >/dev/null && exit 0; sleep 2; done; exit 1"
    ) -WorkingDirectory $AppRoot -TimeoutSeconds $TimeoutSeconds -StepName "wait for Vespa application endpoint"
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("exec", "-u", "root", "flux-vespa", "sh", "-lc", "rm -rf /tmp/flux-vespa-app") -WorkingDirectory $AppRoot -TimeoutSeconds 60 -StepName "clear Vespa application temp package"
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("cp", $vespaApp, "flux-vespa:/tmp/flux-vespa-app") -WorkingDirectory $AppRoot -TimeoutSeconds 120 -StepName "copy Vespa application package"
    Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("exec", "flux-vespa", "sh", "-lc", "vespa deploy --wait 300 /tmp/flux-vespa-app") -WorkingDirectory $AppRoot -TimeoutSeconds $TimeoutSeconds -StepName "deploy Vespa application package"
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
        $baseAlias = New-FluxLocalDockerBaseAlias -Image $candidateImage
        Write-Host "Reusing Docker base image $candidateImage as $baseAlias; Debian system packages will not be reinstalled."
        return [pscustomobject]@{ Image = $baseAlias; SkipSystemPackages = $true }
    }
    if ($DockerBaseImage) {
        Write-Warning "Requested Docker base image $DockerBaseImage was not found; falling back to $pythonBase."
    }
    return [pscustomobject]@{ Image = $pythonBase; SkipSystemPackages = $false }
}

function Invoke-FluxDashboardBuild {
    param([string]$DashboardRoot)
    $viteCmd = Join-Path $DashboardRoot "node_modules\.bin\vite.cmd"
    if (-not (Test-Path -LiteralPath $viteCmd)) {
        npm --prefix $DashboardRoot ci
        if ($LASTEXITCODE -ne 0) {
            throw "npm ci failed for dashboard dependencies with exit code $LASTEXITCODE"
        }
    }
    npm --prefix $DashboardRoot run build
    if ($LASTEXITCODE -ne 0) {
        throw "npm run build failed for dashboard with exit code $LASTEXITCODE"
    }
}

$appRoot = Join-Path $InstallRoot "app"
$privateRoot = Join-Path $InstallRoot "private"
$dataRoot = Join-Path $InstallRoot "data"
$logsRoot = Join-Path $InstallRoot "logs"
$modelsRoot = Join-Path $InstallRoot "models"
$ollamaModelsRoot = Join-Path $modelsRoot "ollama"
$runtimeRoot = Join-Path $InstallRoot "runtime"
$backupRoot = Join-Path $InstallRoot "backups"

# Installation is intentionally non-destructive for live runtime state. These
# calls create missing directories but never remove private data, database
# files, mail spools, logs, runtime status, or backups.
foreach ($dir in @($InstallRoot, $appRoot, $privateRoot, $dataRoot, (Join-Path $dataRoot "postgres"), $logsRoot, $modelsRoot, $ollamaModelsRoot, $runtimeRoot, $backupRoot)) {
    New-FluxDirectory $dir
}

$buildMetadata = Get-FluxBuildMetadata -Root $SourceRoot
$imageTag = $buildMetadata.ShortRevision
$resolvedPython = Resolve-FluxPythonExe -InstallRoot $InstallRoot -RequestedPython $PythonExe
if (-not $SkipDashboardBuild) {
    Invoke-FluxDashboardBuild -DashboardRoot (Join-Path $SourceRoot "dashboard")
}

$dockerBase = Resolve-FluxDockerBuildBase -DockerBaseMode $DockerBaseMode -DockerBaseImage $DockerBaseImage
$skipSystemPackages = if ($dockerBase.SkipSystemPackages) { "true" } else { "false" }
$pipOfflineValue = if ($PipOffline) { "true" } else { "false" }
$dockerBuildNetwork = if ($PipOffline -and $dockerBase.SkipSystemPackages) { "none" } else { "default" }
$dockerBuildArgs = @(
    "build",
    "--progress=plain",
    "--pull=false",
    "--network", $dockerBuildNetwork,
    "--build-arg", "FLUX_KB_IMAGE_REVISION=$($buildMetadata.Revision)",
    "--build-arg", "FLUX_KB_IMAGE_SOURCE=$($buildMetadata.Source)",
    "--build-arg", "FLUX_KB_IMAGE_CREATED=$($buildMetadata.Created)",
    "--build-arg", "FLUX_KB_IMAGE_VERSION=$($buildMetadata.Version)",
    "--build-arg", "FLUX_KB_DOCKER_BASE_IMAGE=$($dockerBase.Image)",
    "--build-arg", "FLUX_KB_SKIP_SYSTEM_PACKAGES=$skipSystemPackages",
    "--build-arg", "PIP_OFFLINE=$pipOfflineValue",
    "--build-arg", "PIP_DEFAULT_TIMEOUT=$PipTimeoutSeconds",
    "--build-arg", "PIP_RETRIES=$PipRetries",
    "--build-arg", "PADDLE_GPU_INDEX_URL=$PaddleGpuIndexUrl",
    "--build-arg", "PYTORCH_GPU_INDEX_URL=$PytorchGpuIndexUrl"
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
Invoke-FluxOllamaImageBuild -SourceRoot $SourceRoot -BuildMetadata $buildMetadata -ImageTag $imageTag -TimeoutSeconds $DockerBuildTimeoutSeconds
Invoke-FluxDockerImageAvailable -Image "postgres:16" -WorkingDirectory $SourceRoot -TimeoutSeconds 300

$gpuEnabled = Assert-FluxGpuAvailable -ImageTag $imageTag -GpuMode $GpuMode

$pluginSource = Join-Path $SourceRoot "plugins"
$pluginTarget = Join-Path $appRoot "plugins"
if (Test-Path $pluginSource) {
    if (Test-Path $pluginTarget) { Remove-Item -LiteralPath $pluginTarget -Recurse -Force }
    Copy-Item -Path $pluginSource -Destination $pluginTarget -Recurse -Force
}
Copy-FluxVespaApplication -SourceRoot $SourceRoot -AppRoot $appRoot

$composePath = Join-Path $appRoot "docker-compose.yml"
$envPath = Join-Path $privateRoot "flux.env"
$appEnvPath = Join-Path $appRoot ".env"
& (Join-Path $PSScriptRoot "migrate-postgres-to-docker-volume.ps1") -InstallRoot $InstallRoot -PostgresPort $PostgresPort -BackupRoot $backupRoot
Write-FluxCompose -TargetPath $composePath -ImageTag $imageTag -ApiPort $ApiPort -PostgresPort $PostgresPort -OllamaHostPort $OllamaHostPort -AsrHostPort $AsrHostPort -GpuEnabled $gpuEnabled
Write-FluxEnv -TargetPath $envPath -InstallRoot $InstallRoot -ImageTag $imageTag -PostgresPort $PostgresPort -GpuEnabled $gpuEnabled
$imageMetadataEnv = @"
FLUX_KB_IMAGE_TAG=$imageTag
FLUX_KB_IMAGE_REVISION=$($buildMetadata.Revision)
FLUX_KB_IMAGE_SOURCE=$($buildMetadata.Source)
FLUX_KB_IMAGE_CREATED=$($buildMetadata.Created)
FLUX_KB_IMAGE_VERSION=$($buildMetadata.Version)
"@
Set-Content -Path $appEnvPath -Value "$imageMetadataEnv`n" -Encoding UTF8
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
Invoke-FluxNativeCommand -FilePath $venvPython -Arguments (@("-m", "pip", "install") + $pipCommonArgs + $pipIndexArgs + @("--force-reinstall", "--no-deps", "--no-cache-dir", $SourceRoot)) -WorkingDirectory $SourceRoot -TimeoutSeconds $PipInstallTimeoutSeconds -StepName "pip install production package"
Invoke-FluxCodexPluginInstall -VenvPython $venvPython -InstallRoot $InstallRoot
Write-FluxHostScripts -AppRoot $appRoot -InstallRoot $InstallRoot -HostAgentPort $HostAgentPort -PostgresPort $PostgresPort -GpuEnabled $gpuEnabled -OllamaHostPort $OllamaHostPort -AsrHostPort $AsrHostPort
Remove-FluxLegacyConsoleLaunchers -AppRoot $appRoot
if ($gpuEnabled) {
    Invoke-FluxAsrModelDownload -AppRoot $appRoot -AppEnvPath $appEnvPath -ComposePath $composePath -TimeoutSeconds $AsrModelDownloadTimeoutSeconds
    Invoke-FluxModelRunnerModelDownload -AppRoot $appRoot -AppEnvPath $appEnvPath -ComposePath $composePath -TimeoutSeconds $ModelRunnerModelDownloadTimeoutSeconds
}

Push-Location $appRoot
try {
    $composeServices = @("postgres", "vespa")
    docker compose --env-file $appEnvPath -f $composePath up -d --no-build @composeServices
    Invoke-FluxVespaApplicationDeploy -AppRoot $appRoot -TimeoutSeconds 300
    $composeServices = @("paddle-runner", "model-runner", "api", "worker")
    if ($gpuEnabled) {
        $composeServices = @("paddle-runner", "model-runner", "ollama", "asr", "api", "worker")
    }
    docker compose --env-file $appEnvPath -f $composePath up -d --no-build @composeServices
} finally {
    Pop-Location
}
Invoke-FluxMigration -VenvPython $venvPython -InstallRoot $InstallRoot -PostgresPort $PostgresPort
Set-FluxProductionRuntimeSettings -VenvPython $venvPython -InstallRoot $InstallRoot -PostgresPort $PostgresPort -GpuEnabled $gpuEnabled -OllamaHostPort $OllamaHostPort -AsrHostPort $AsrHostPort

if (-not $SkipScheduledTasks) {
    Register-FluxTask -TaskName "FluxKB Host Agent" -LauncherPath (Join-Path $appRoot "run-host-agent.pyw") -AppRoot $appRoot
    Register-FluxTask -TaskName "FluxKB Outlook Host" -LauncherPath (Join-Path $appRoot "run-outlook-host.pyw") -AppRoot $appRoot
}

Write-Host "Flux production runtime installed at $InstallRoot"
Write-Host "Dashboard: http://127.0.0.1:$ApiPort/dashboard"
if ($gpuEnabled) {
    Write-Host "Install the Docker Ollama vision model with: docker exec flux-ollama ollama pull qwen3-vl:8b"
    Write-Host "Docker Ollama host-agent URL: http://127.0.0.1:$OllamaHostPort"
    Write-Host "Docker ASR host-agent URL: http://127.0.0.1:$AsrHostPort"
}
