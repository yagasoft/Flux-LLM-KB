import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def _script(name: str) -> str:
    return (ROOT / "scripts" / "deploy" / name).read_text(encoding="utf-8")


def _dev_script(name: str) -> str:
    return (ROOT / "scripts" / "dev" / name).read_text(encoding="utf-8")


MAX_COMPOSE_MEMORY_MB = 30 * 1024

DEV_MEMORY_LIMITS = {
    "api": "1gb",
    "worker": "1gb",
    "search-index-worker": "1gb",
    "mail-worker": "512mb",
    "outlook-worker": "512mb",
    "automation-worker": "384mb",
    "governance-worker": "384mb",
    "runtime-control-worker": "256mb",
    "gpu-eviction-worker": "256mb",
    "callback-worker": "384mb",
    "event-audit-worker": "256mb",
    "event-dashboard-worker": "256mb",
    "event-diagnostics-worker": "256mb",
    "event-scheduler": "256mb",
    "outbox-relay": "384mb",
    "rabbitmq": "512mb",
    "postgres": "2gb",
}

PRODUCTION_MEMORY_LIMITS = {
    **DEV_MEMORY_LIMITS,
    "asr": "3gb",
    "model-runner": "5gb",
    "paddle-runner": "5gb",
    "ollama": "4gb",
    "vespa": "3gb",
}

DEV_MEMORY_EXPECTED_MB = 9728
PRODUCTION_MEMORY_EXPECTED_MB = 30208

PRODUCTION_CONTAINER_NAMES = {
    "api": "flux-llm-kb-api",
    "worker": "flux-llm-kb-worker",
    "search-index-worker": "flux-llm-kb-search-index-worker",
    "mail-worker": "flux-llm-kb-mail-worker",
    "outlook-worker": "flux-llm-kb-outlook-worker",
    "automation-worker": "flux-llm-kb-automation-worker",
    "governance-worker": "flux-llm-kb-governance-worker",
    "runtime-control-worker": "flux-llm-kb-runtime-control-worker",
    "gpu-eviction-worker": "flux-llm-kb-gpu-eviction-worker",
    "callback-worker": "flux-llm-kb-callback-worker",
    "event-audit-worker": "flux-llm-kb-event-audit-worker",
    "event-dashboard-worker": "flux-llm-kb-event-dashboard-worker",
    "event-diagnostics-worker": "flux-llm-kb-event-diagnostics-worker",
    "event-scheduler": "flux-llm-kb-event-scheduler",
    "outbox-relay": "flux-llm-kb-outbox-relay",
    "asr": "flux-llm-kb-asr",
    "model-runner": "flux-llm-kb-model-runner",
    "paddle-runner": "flux-llm-kb-paddle-runner",
    "ollama": "flux-ollama",
    "vespa": "flux-vespa",
    "rabbitmq": "flux-llm-kb-rabbitmq",
    "postgres": "flux-llm-kb-postgres",
}


def test_production_deploy_scripts_exist_and_use_d_drive_install_root():
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")
    start = _script("start-flux.ps1")
    stop = _script("stop-flux.ps1")
    status = _script("status-flux.ps1")

    assert 'D:\\FluxLLMKB' in install
    assert "D:\\FluxLLMKB" in update
    assert "docker compose" in start
    assert "docker compose" in stop
    assert "FluxKB Host Agent" in install
    assert "FluxKB Outlook Host" in install
    assert "Register-ScheduledTask" in install
    assert "-Hidden" in install
    assert "New-FluxHostTaskTriggers" in install
    assert "New-ScheduledTaskTrigger -Once" in install
    assert "-RepetitionInterval (New-TimeSpan -Minutes 1)" in install
    assert "-MultipleInstances IgnoreNew" in install
    assert "-StartWhenAvailable" in install
    assert "pythonw.exe" in install
    assert "run-host-agent.pyw" in install
    assert "run-outlook-host.pyw" in install
    assert "Remove-FluxLegacyConsoleLaunchers" in install
    assert "run-host-agent.ps1" in install
    assert "run-outlook-host.ps1" in install
    assert '-Execute "pwsh.exe"' not in install
    assert "Resolve-FluxPythonExe" in install
    assert "python\\python.exe" in install
    assert "Invoke-FluxMigration" in install
    assert 'Invoke-FluxDockerImageAvailable -Image "postgres:16"' in install
    assert 'Invoke-FluxDockerImageAvailable -Image "rabbitmq:4.3-management"' in install
    assert "-m flux_llm_kb.cli migrate" in install
    assert "Invoke-FluxCodexPluginInstall" in install
    assert "-m flux_llm_kb.cli codex install-plugin" in install
    assert '"$SourceRoot[api,corpus,mail,mcp,processors]"' in install
    assert '"--force-reinstall", "--no-deps", "--no-build-isolation", $SourceRoot' in install
    assert "pip install production package" in install
    assert "GpuMode" in install
    assert "Assert-FluxGpuAvailable" in install
    assert 'Join-Path $appRoot "plugins"' in install
    assert "E:\\LLM KB" not in install
    assert "private\\runtime" not in install
    assert "127.0.0.1:${ApiPort}:8765" in install
    assert "restart: unless-stopped" in install
    assert "Flux production status" in status
    assert "docker compose down" not in install
    assert "--volumes" not in install
    assert "docker volume rm" not in install
    assert "Flux Docker storage" in status
    assert "Mounts" in status
    assert "Docker-visible memory" in status
    assert "/dev/shm" in status
    assert "flux-llm-kb-postgres" in status
    for protected_path in (
        "$InstallRoot",
        "$privateRoot",
        "$dataRoot",
        "$logsRoot",
        "$runtimeRoot",
        "$backupRoot",
        'Join-Path $dataRoot "postgres"',
    ):
        assert f"Remove-Item -LiteralPath {protected_path}" not in install


def test_production_update_uses_prebuilt_images_not_repo_context_compose_build():
    update = _script("update-flux.ps1")

    assert "docker build" in update
    assert "flux-llm-kb-api:" in update
    assert '"up", "-d", "--no-build"' in update
    assert '"postgres", "rabbitmq", "vespa"' in update
    assert '"worker", "search-index-worker", "mail-worker", "outlook-worker", "automation-worker", "governance-worker", "runtime-control-worker", "gpu-eviction-worker"' in update
    assert '"event-audit-worker", "event-dashboard-worker", "event-diagnostics-worker"' in update
    assert "FLUX_KB_IMAGE_TAG" in update
    assert "FLUX_KB_CALLBACK_SIGNING_SECRET=$callbackSecret" in update
    assert "private\\flux.env" in update
    assert 'Join-Path $appRoot "plugins"' in update
    assert "Resolve-FluxPythonExe" in update
    assert "RecreateVenv" in update
    assert "Invoke-FluxMigration" in update
    assert 'Invoke-FluxDockerImageAvailable -Image "postgres:16"' in update
    assert 'Invoke-FluxDockerImageAvailable -Image "rabbitmq:4.3-management"' in update
    assert "-m flux_llm_kb.cli migrate" in update
    assert "Invoke-FluxCodexPluginInstall" in update
    assert "-m flux_llm_kb.cli codex install-plugin" in update
    assert '"$SourceRoot[api,corpus,mail,mcp,processors]"' in update
    assert '"--force-reinstall", "--no-deps", "--no-build-isolation", $SourceRoot' in update
    assert "pip install production package" in update
    assert "Register-FluxTask" in update
    assert "New-FluxHostTaskTriggers" in update
    assert "New-ScheduledTaskTrigger -Once" in update
    assert "-RepetitionInterval (New-TimeSpan -Minutes 1)" in update
    assert "-MultipleInstances IgnoreNew" in update
    assert "-StartWhenAvailable" in update
    assert "Wait-FluxTaskStopped" in update
    assert "Wait-FluxTcpClosed" in update
    assert "Port = $HostAgentPort" in update
    assert "pythonw.exe" in update
    assert "run-host-agent.pyw" in update
    assert "run-outlook-host.pyw" in update
    assert "Remove-FluxLegacyConsoleLaunchers" in update
    assert "run-host-agent.ps1" in update
    assert "run-outlook-host.ps1" in update
    assert "Stop-FluxOutlookHostLaunchers" in update
    assert "Stop-FluxHostAgentLaunchers" in update
    assert "Get-CimInstance Win32_Process" in update
    assert "run-outlook-host.pyw" in update
    assert "run-host-agent.pyw" in update
    assert '-Execute "pwsh.exe"' not in update
    assert "[int]$HostAgentPort = 8799" in update
    assert "[int]$PostgresPort = 5432" in update
    assert "[int]$OllamaHostPort = 11435" in update
    assert "[int]$AsrHostPort = 8788" in update
    assert "Write-FluxHostScripts -AppRoot $appRoot -InstallRoot $InstallRoot -HostAgentPort $HostAgentPort -PostgresPort $PostgresPort -GpuEnabled $gpuEnabled -OllamaHostPort $OllamaHostPort -AsrHostPort $AsrHostPort" in update
    assert "GpuMode" in update
    assert "Write-FluxCompose" in update
    assert "Assert-FluxGpuAvailable" in update
    assert "build:" not in _embedded_compose_template(update)


def test_production_compose_enables_gpu_and_local_vision_for_api_and_worker():
    install_compose = _embedded_compose_template(_script("install-flux.ps1"))
    update_compose = _embedded_compose_template(_script("update-flux.ps1"))

    for compose in (install_compose, update_compose):
        assert compose.count("gpus: all") == 6
        assert compose.count("NVIDIA_VISIBLE_DEVICES: all") == 6
        assert compose.count("NVIDIA_DRIVER_CAPABILITIES: compute,utility") == 6
        assert "  vespa:" in compose
        assert "image: vespaengine/vespa:8" in compose
        assert "container_name: flux-vespa" in compose
        assert "flux_llm_kb_vespa_var:/opt/vespa/var" in compose
        assert "flux_llm_kb_vespa_logs:/opt/vespa/logs" in compose
        assert "  model-runner:" in compose
        assert "container_name: flux-llm-kb-model-runner" in compose
        assert '"127.0.0.1:8790:8790"' in compose
        assert "python -m flux_llm_kb.model_runner serve --host 0.0.0.0 --port 8790" in compose
        assert 'test: ["CMD", "python", "-m", "flux_llm_kb.model_runner", "health"]' in compose
        assert "  paddle-runner:" in compose
        assert "container_name: flux-llm-kb-paddle-runner" in compose
        assert "/opt/flux-paddle/bin/python -m flux_llm_kb.model_runner serve-paddle --host 0.0.0.0 --port 8791" in compose
        assert 'test: ["CMD", "/opt/flux-paddle/bin/python", "-m", "flux_llm_kb.model_runner", "health", "--role", "paddle-runner"]' in compose
        assert "FLUX_KB_PADDLE_RUNNER_BASE_URL: http://paddle-runner:8791" in compose
        assert "FLUX_KB_MODEL_RUNNER_BASE_URL: http://model-runner:8790" in compose
        assert "FLUX_KB_RETRIEVAL_SEARCH_ENGINE: vespa" in compose
        assert "FLUX_KB_RETRIEVAL_VESPA_BASE_URL: http://vespa:8080" in compose
        assert "FLUX_KB_RETRIEVAL_EMBEDDING_MODEL: Snowflake/snowflake-arctic-embed-l-v2.0" in compose
        assert "FLUX_KB_RETRIEVAL_EMBEDDING_DIMENSIONS: \"1024\"" in compose
        assert "FLUX_KB_RETRIEVAL_RERANKER_MODEL: Qwen/Qwen3-Reranker-4B" in compose
        assert "FLUX_KB_RETRIEVAL_RERANKER_AWQ_MODEL: drawais/Qwen3-Reranker-4B-AWQ-INT4" in compose
        assert "FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION: awq_int4" in compose
        assert "FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION: int4_awq" not in compose
        assert compose.count("FLUX_KB_GPU_SCHEDULER_MODE: postgres") >= 3
        assert compose.count("FLUX_KB_GPU_SCHEDULER_VRAM_BUDGET_MB: \"10240\"") >= 3
        assert compose.count("FLUX_KB_GPU_SCHEDULER_SAFETY_MARGIN_MB: \"1024\"") >= 3
        assert compose.count("FLUX_KB_GPU_SCHEDULER_EVICTION_ENABLED: \"true\"") >= 5
        assert compose.count("FLUX_KB_GPU_SCHEDULER_EVICTION_REQUEST_TIMEOUT_SECONDS: \"10\"") >= 5
        assert compose.count("FLUX_KB_GPU_SCHEDULER_EVICTION_MAX_MODELS: \"4\"") >= 5
        assert compose.count("FLUX_KB_MODEL_RUNNER_BASE_URL: http://model-runner:8790") >= 5
        assert compose.count("FLUX_KB_PADDLE_RUNNER_BASE_URL: http://paddle-runner:8791") >= 5
        assert compose.count("FLUX_KB_ASR_BASE_URL: http://asr:8788") >= 5
        assert compose.count("FLUX_KB_LOCAL_INFERENCE_BASE_URL: http://ollama:11434") >= 5
        assert compose.count("FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb") >= 5
        assert "FLUX_KB_OCR_ENGINE: paddleocr" in compose
        assert "FLUX_KB_OCR_SIMPLE_MODEL: PP-OCRv5" in compose
        assert "FLUX_KB_OCR_DOCUMENT_MODEL: PaddleOCR-VL" in compose
        assert compose.count("PADDLE_PDX_MODEL_SOURCE: bos") == 2
        assert "  ollama:" in compose
        assert "image: flux-ollama:local" in compose
        assert "image: flux-ollama:`${FLUX_KB_IMAGE_TAG}" not in compose
        assert "image: ollama/ollama:latest" not in compose
        assert "container_name: flux-ollama" in compose
        assert compose.count("OLLAMA_LOAD_TIMEOUT: 30m") == 1
        assert compose.count("OLLAMA_KEEP_ALIVE: 2m") == 1
        assert "flux_llm_kb_ollama_models:/root/.ollama" in compose
        assert "../models/ollama:/root/.ollama" not in compose
        assert '"127.0.0.1:${OllamaHostPort}:11434"' in compose
        assert 'test: ["CMD-SHELL", "command -v ffmpeg >/dev/null && command -v ffprobe >/dev/null && ollama list >/dev/null"]' in compose
        assert "ollama:" in compose
        assert "  asr:" in compose
        assert "container_name: flux-llm-kb-asr" in compose
        assert '"127.0.0.1:${AsrHostPort}:8788"' in compose
        assert "flux_llm_kb_asr_models:/models" in compose
        assert "python -m flux_llm_kb.asr_server serve --host 0.0.0.0 --port 8788" in compose
        assert 'test: ["CMD", "python", "-m", "flux_llm_kb.asr_server", "health"]' in compose
        assert "condition: service_healthy" in compose
        assert compose.count("FLUX_KB_ASR_PROVIDER: openai_compatible") == 3
        assert compose.count("FLUX_KB_ASR_MODEL: large-v3-turbo") == 3
        assert compose.count("FLUX_KB_ASR_BASE_URL: http://asr:8788") >= 5
        assert "FLUX_KB_ASR_MODEL_PATH: /models/faster-whisper-large-v3-turbo" in compose
        assert compose.count("FLUX_KB_ASR_DEVICE: cuda") == 3
        assert compose.count("FLUX_KB_ASR_COMPUTE_TYPE: float16") == 3
        assert "FLUX_KB_LOCAL_INFERENCE_ENABLED: \"true\"" in compose
        assert "FLUX_KB_LOCAL_INFERENCE_BASE_URL: http://ollama:11434" in compose
        assert compose.count("FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE: 2m") >= 5
        assert "FLUX_KB_VISION_ENABLED: \"true\"" in compose
        assert "FLUX_KB_VISION_MODEL: qwen3-vl:8b" in compose
        assert "FLUX_KB_VISION_MAX_IMAGE_PIXELS: \"80000000\"" in compose
        assert "host.docker.internal:11434" not in compose
        assert "0.0.0.0:11434:11434" not in compose
        assert "127.0.0.1:11434:11434" not in compose


def test_production_compose_sets_hard_container_memory_budget():
    for script_name in ("install-flux.ps1", "update-flux.ps1"):
        compose = _embedded_compose_template(_script(script_name))

        assert "deploy:" not in compose
        assert "resources:" not in compose
        assert _assert_compose_memory_budget(
            compose,
            PRODUCTION_MEMORY_LIMITS,
            PRODUCTION_MEMORY_EXPECTED_MB,
        ) <= MAX_COMPOSE_MEMORY_MB


def test_development_compose_sets_hard_container_memory_budget():
    compose = (ROOT / "docker-compose.yml").read_text(encoding="utf-8")

    assert "deploy:" not in compose
    assert "resources:" not in compose
    assert _assert_compose_memory_budget(
        compose,
        DEV_MEMORY_LIMITS,
        DEV_MEMORY_EXPECTED_MB,
    ) <= MAX_COMPOSE_MEMORY_MB


def test_production_compose_sets_vespa_configserver_jvm_budget():
    for script_name in ("install-flux.ps1", "update-flux.ps1"):
        compose = _embedded_compose_template(_script(script_name))
        vespa_block = _compose_service_blocks(compose)["vespa"]

        assert 'VESPA_CONFIGSERVER_JVMARGS: "-Xms128m -Xmx512m"' in vespa_block
        assert "VESPA_CONFIGPROXY_JVMARGS" not in vespa_block
        assert "JAVA_TOOL_OPTIONS" not in vespa_block
        assert "VESPA_CONTAINER_JVMARGS" not in vespa_block
        assert "-Xms1024m" not in vespa_block
        assert "-Xmx1024m" not in vespa_block


def test_production_status_reports_all_container_memory_limits():
    status = _script("status-flux.ps1")

    assert "Flux Docker memory limits" in status
    assert ".HostConfig.Memory" in status
    assert ".HostConfig.MemorySwap" in status
    assert "unbounded" in status
    for container in PRODUCTION_CONTAINER_NAMES.values():
        assert container in status


def test_production_host_agent_uses_host_loopback_to_docker_ollama():
    for script_name in ("install-flux.ps1", "update-flux.ps1"):
        script = _script(script_name)

        assert "[int]$OllamaHostPort = 11435" in script
        assert "[int]$AsrHostPort = 8788" in script
        assert "param([string]$AppRoot, [string]$InstallRoot, [int]$HostAgentPort, [int]$PostgresPort, [bool]$GpuEnabled, [int]$OllamaHostPort, [int]$AsrHostPort)" in script
        assert 'os.environ["FLUX_KB_RABBITMQ_URL"] = "amqp://flux:flux@127.0.0.1:5672/flux"' in script
        assert 'os.environ["FLUX_KB_RABBITMQ_MANAGEMENT_URL"] = "http://127.0.0.1:15672"' in script
        assert 'os.environ["FLUX_KB_RABBITMQ_USERNAME"] = "flux"' in script
        assert 'os.environ["FLUX_KB_RABBITMQ_PASSWORD"] = "flux"' in script
        assert 'os.environ["FLUX_KB_RABBITMQ_URL"] = "amqp://flux:flux@rabbitmq:5672/flux"' not in script
        assert 'os.environ["FLUX_KB_LOCAL_INFERENCE_ENABLED"] = "true"' in script
        assert 'os.environ["FLUX_KB_LOCAL_INFERENCE_BASE_URL"] = "http://127.0.0.1:${OllamaHostPort}"' in script
        assert 'os.environ["FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE"] = "2m"' in script
        assert 'os.environ["FLUX_KB_VISION_ENABLED"] = "true"' in script
        assert 'os.environ["FLUX_KB_VISION_MODEL"] = "qwen3-vl:8b"' in script
        assert 'os.environ["FLUX_KB_VISION_MAX_IMAGE_PIXELS"] = "80000000"' in script
        assert 'os.environ["FLUX_KB_ASR_PROVIDER"] = "openai_compatible"' in script
        assert 'os.environ["FLUX_KB_ASR_MODEL"] = "large-v3-turbo"' in script
        assert 'os.environ["FLUX_KB_ASR_BASE_URL"] = "http://127.0.0.1:${AsrHostPort}"' in script
        assert 'os.environ["FLUX_KB_LOCAL_INFERENCE_BASE_URL"] = "http://ollama:11434"' not in script
        assert 'svg_renderer = Path(os.environ["FLUX_KB_INSTALL_ROOT"]) / "tools" / "resvg" / "resvg.exe"' in script
        assert 'os.environ["FLUX_KB_SVG_RENDERER"] = str(svg_renderer)' in script


def test_production_deploy_persists_host_side_qwen_runtime_settings():
    for script_name in ("install-flux.ps1", "update-flux.ps1"):
        script = _script(script_name)

        assert "function Set-FluxProductionRuntimeSettings" in script
        assert "Set-FluxProductionRuntimeSettings -VenvPython $venvPython -InstallRoot $InstallRoot -PostgresPort $PostgresPort -GpuEnabled $gpuEnabled -OllamaHostPort $OllamaHostPort -AsrHostPort $AsrHostPort" in script
        assert '"settings", "set", "acceleration.local_inference.enabled", "true", "--confirm"' in script
        assert '"settings", "set", "acceleration.local_inference.base_url", "http://127.0.0.1:$OllamaHostPort", "--confirm"' in script
        assert '"settings", "set", "acceleration.local_inference.keep_alive", "2m", "--confirm"' in script
        assert '"settings", "set", "acceleration.vision.enabled", "true", "--confirm"' in script
        assert '"settings", "set", "acceleration.vision.model", "qwen3-vl:8b", "--confirm"' in script
        assert '"settings", "set", "acceleration.vision.max_image_pixels", "80000000", "--confirm"' in script
        assert '"settings", "set", "acceleration.asr.provider", "openai_compatible", "--confirm"' in script
        assert '"settings", "set", "acceleration.asr.model", "large-v3-turbo", "--confirm"' in script
        assert '"settings", "set", "acceleration.asr.base_url", "http://127.0.0.1:$AsrHostPort", "--confirm"' in script
        assert '"settings", "set", "retrieval.search_engine", "vespa", "--confirm"' in script
        assert '"settings", "set", "retrieval.vespa_base_url", "http://127.0.0.1:8080", "--confirm"' in script
        assert '"settings", "set", "retrieval.embedding_model", "Snowflake/snowflake-arctic-embed-l-v2.0", "--confirm"' in script
        assert '"settings", "set", "retrieval.embedding_dimensions", "1024", "--confirm"' in script
        assert '"settings", "set", "retrieval.reranker_model", "Qwen/Qwen3-Reranker-4B", "--confirm"' in script
        assert '"settings", "set", "retrieval.reranker_awq_model", "drawais/Qwen3-Reranker-4B-AWQ-INT4", "--confirm"' in script
        assert '"settings", "set", "retrieval.reranker_quantization", "awq_int4", "--confirm"' in script
        assert '"settings", "set", "retrieval.reranker_quantization", "int4_awq", "--confirm"' not in script
        assert '"settings", "set", "retrieval.rerank_top_n", "12", "--confirm"' in script
        assert '"settings", "set", "retrieval.rerank_microbatch_size", "1", "--confirm"' in script
        assert '"settings", "set", "retrieval.rerank_total_budget_seconds", "5", "--confirm"' in script
        assert '"settings", "set", "retrieval.max_rerank_passage_tokens", "1536", "--confirm"' in script
        assert '"settings", "set", "retrieval.gpu_vram_budget_mb", "10240", "--confirm"' in script
        assert '"settings", "set", "ocr.engine", "paddleocr", "--confirm"' in script
        assert '"settings", "set", "ocr.simple_model", "PP-OCRv5", "--confirm"' in script
        assert '"settings", "set", "ocr.document_model", "PaddleOCR-VL", "--confirm"' in script


def test_production_deploy_scripts_surface_docker_ollama_model_steps():
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")

    for script in (install, update):
        assert "docker exec flux-ollama ollama pull qwen3-vl:8b" in script
        assert "Invoke-FluxAsrModelDownload" in script
        assert "python -m flux_llm_kb.asr_server download-model --model large-v3-turbo --output-dir /models/faster-whisper-large-v3-turbo" in script
        assert "Invoke-FluxModelRunnerModelDownload" in script
        assert '"flux_llm_kb.model_runner", "download-models", "--models-dir", "/models"' in script
        assert '"flux_llm_kb.model_runner", "download-paddle-models", "--models-dir", "/models"' in script
        assert "Snowflake, Qwen reranker, PP-OCRv5, and PaddleOCR-VL" in script
        assert "Invoke-FluxVespaApplicationDeploy" in script
        assert '("exec", "-u", "root", "flux-vespa", "sh", "-lc", "rm -rf /tmp/flux-vespa-app")' in script
        assert script.index("rm -rf /tmp/flux-vespa-app") < script.index('"cp", $vespaApp, "flux-vespa:/tmp/flux-vespa-app"')
        assert "$deployWaitSeconds = [Math]::Max($TimeoutSeconds, 900)" in script
        assert "$deployTimeoutSeconds = $deployWaitSeconds + 60" in script
        assert "vespa deploy --wait $deployWaitSeconds /tmp/flux-vespa-app" in script
        assert '-TimeoutSeconds $deployTimeoutSeconds -StepName "deploy Vespa application package"' in script
        assert "vespa deploy --wait 300 /tmp/flux-vespa-app" not in script
        assert "Copy-FluxVespaApplication -SourceRoot $SourceRoot -AppRoot $appRoot" in script
        assert "qwen3-vl:32b" not in script


def test_production_deploy_builds_derived_ollama_runtime_image():
    ollama_dockerfile = ROOT / "docker" / "ollama" / "Dockerfile"
    assert ollama_dockerfile.exists()
    dockerfile = ollama_dockerfile.read_text(encoding="utf-8")

    assert "FROM ${OLLAMA_BASE_IMAGE}" in dockerfile
    assert 'ARG FLUX_KB_OLLAMA_RUNTIME_FINGERPRINT=""' in dockerfile
    assert "org.flux_llm_kb.ollama.runtime_fingerprint=${FLUX_KB_OLLAMA_RUNTIME_FINGERPRINT}" in dockerfile
    assert "apt-get install -y --no-install-recommends ffmpeg" in dockerfile
    assert "command -v ffmpeg" in dockerfile
    assert "command -v ffprobe" in dockerfile

    for script_name in ("install-flux.ps1", "update-flux.ps1"):
        script = _script(script_name)
        compose = _embedded_compose_template(script)

        assert 'Join-Path $SourceRoot "docker\\ollama\\Dockerfile"' in script
        assert '"--build-arg", "OLLAMA_BASE_IMAGE=ollama/ollama:latest"' in script
        assert '"--build-arg", "FLUX_KB_OLLAMA_RUNTIME_FINGERPRINT=$runtimeFingerprint"' in script
        assert 'Get-FluxOllamaRuntimeFingerprint -SourceRoot $SourceRoot -BaseImage "ollama/ollama:latest"' in script
        assert 'Get-FluxDockerImageLabel -Image "flux-ollama:local" -Label "org.flux_llm_kb.ollama.runtime_fingerprint"' in script
        assert 'Write-Host "Skipping Ollama runtime image build; flux-ollama:local already matches runtime fingerprint $runtimeFingerprint."' in script
        assert '"-t", "flux-ollama:local", "-t", "flux-ollama:$imageTag"' in script
        assert script.index("Skipping Ollama runtime image build") < script.index('StepName "docker build ollama runtime"')
        assert 'StepName "docker build ollama runtime"' in script
        assert "image: flux-ollama:local" in compose
        assert "image: flux-ollama:`${FLUX_KB_IMAGE_TAG}" not in compose


def test_deploy_ollama_vision_smoke_script_checks_media_runtime_and_decode_path():
    script_path = ROOT / "scripts" / "deploy" / "test-ollama-vision.ps1"
    assert script_path.exists()
    script = script_path.read_text(encoding="utf-8")

    assert "[int]$OllamaHostPort = 11435" in script
    assert '[string]$Model = "qwen3-vl:8b"' in script
    assert 'docker exec flux-ollama sh -lc "command -v ffmpeg >/dev/null && command -v ffprobe >/dev/null"' in script
    assert "/api/generate" in script
    assert "images" in script
    assert "iVBORw0KGgo" in script
    assert "ffprobe" in script
    assert "decode" in script.lower()
    assert "thinking" in script
    assert "$response.done -ne $true" in script


def test_model_runner_download_avoids_hf_xet_and_allows_large_model_cache_warmup():
    for script_name in ("install-flux.ps1", "update-flux.ps1"):
        script = _script(script_name)
        compose = _embedded_compose_template(script)

        assert "[int]$ModelRunnerModelDownloadTimeoutSeconds = 43200" in script
        assert "HF_HUB_DISABLE_XET: \"1\"" in compose
        assert "HF_HOME: /models/huggingface" in compose


def test_production_compose_overrides_host_paths_inside_api_and_worker():
    for script_name in ("install-flux.ps1", "update-flux.ps1"):
        compose = _embedded_compose_template(_script(script_name))

        assert compose.count("FLUX_KB_INSTALL_ROOT: /app/runtime") == 2
        assert compose.count("FLUX_KB_DATA_DIR: /app/data") == 2
        assert compose.count("FLUX_KB_CACHE_ROOT: /app/cache") == 2
        assert compose.count("FLUX_KB_PRIVATE_DIR: /app/private") == 2
        assert compose.count("FLUX_KB_LOG_DIR: /app/logs") == 2
        assert "FLUX_KB_PRIVATE_DIR: D:\\FluxLLMKB\\private" not in compose


def test_production_compose_uses_docker_volumes_for_container_owned_state():
    for script_name in ("install-flux.ps1", "update-flux.ps1"):
        compose = _embedded_compose_template(_script(script_name))

        assert "flux_llm_kb_postgres_data:/var/lib/postgresql/data" in compose
        assert "../data/postgres:/var/lib/postgresql/data" not in compose
        for volume_name, mount_path in (
            ("flux_llm_kb_data", "/app/data"),
            ("flux_llm_kb_cache", "/app/cache"),
            ("flux_llm_kb_runtime", "/app/runtime"),
            ("flux_llm_kb_logs", "/app/logs"),
        ):
            assert compose.count(f"{volume_name}:{mount_path}") == 2
            assert f"  {volume_name}:" in compose
            assert f"    name: {volume_name}" in compose
        assert "flux_llm_kb_vespa_var:/opt/vespa/var" in compose
        assert "flux_llm_kb_vespa_logs:/opt/vespa/logs" in compose
        assert compose.count("flux_llm_kb_model_runner_models:/models") == 2
        assert compose.count("flux_llm_kb_paddle_cache:/root/.paddleocr") == 2
        assert "  flux_llm_kb_vespa_var:" in compose
        assert "  flux_llm_kb_vespa_logs:" in compose
        assert "  flux_llm_kb_model_runner_models:" in compose
        assert "  flux_llm_kb_paddle_cache:" in compose
        assert "  flux_llm_kb_postgres_data:" in compose
        assert "    name: flux_llm_kb_postgres_data" in compose
        assert "  flux_llm_kb_ollama_models:" in compose
        assert "    name: flux_llm_kb_ollama_models" in compose
        assert "  flux_llm_kb_asr_models:" in compose
        assert "    name: flux_llm_kb_asr_models" in compose
        assert compose.count("flux_llm_kb_asr_models:/models") == 1
        assert compose.count("../private:/app/private") == 2
        assert "../logs:/app/logs" not in compose


def test_production_deploy_migrates_legacy_postgres_bind_data_before_volume_compose():
    migration_script = _script("migrate-postgres-to-docker-volume.ps1")

    assert "flux_llm_kb_postgres_data" in migration_script
    assert "data\\postgres" in migration_script
    assert "PG_VERSION" in migration_script
    assert "pg_dump" in migration_script
    assert "pg_restore" in migration_script
    assert "docker volume create" in migration_script
    assert "Copy-FluxDirectoryToDockerVolume" in migration_script
    assert "private\\cache" in migration_script
    assert "models\\ollama" in migration_script
    assert "flux_llm_kb_cache" in migration_script
    assert "flux_llm_kb_runtime" in migration_script
    assert "flux_llm_kb_logs" in migration_script
    assert "flux_llm_kb_ollama_models" in migration_script
    assert "docker compose down" not in migration_script
    assert "--volumes" not in migration_script
    assert "docker volume rm" not in migration_script

    for script_name in ("install-flux.ps1", "update-flux.ps1"):
        script = _script(script_name)
        assert "migrate-postgres-to-docker-volume.ps1" in script
        assert script.find("migrate-postgres-to-docker-volume.ps1") < script.find("Write-FluxCompose -TargetPath")


def test_container_state_migration_skips_transient_log_locks():
    migration_script = _script("migrate-postgres-to-docker-volume.ps1")

    assert 'Copy-FluxDirectoryToDockerVolume -SourcePath (Join-Path $InstallRoot "logs") -VolumeName "flux_llm_kb_logs" -CopyCommand' in migration_script
    assert "--exclude='*.lock'" in migration_script
    assert "tar -C /to -xf -" in migration_script


def test_production_deploy_scripts_install_dashboard_dependencies_and_fail_on_build_errors():
    for script_name in ("install-flux.ps1", "update-flux.ps1"):
        script = _script(script_name)

        assert "function Invoke-FluxDashboardBuild" in script
        assert "npm --prefix $DashboardRoot ci" in script
        assert 'throw "npm ci failed for dashboard dependencies with exit code $LASTEXITCODE"' in script
        assert 'throw "npm run build failed for dashboard with exit code $LASTEXITCODE"' in script


def test_production_env_gpu_settings_start_on_new_lines():
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")

    for script in (install, update):
        assert '$envText += "`n" + @"' in script
        assert "\nFLUX_KB_HOST_DATABASE_URL=postgresql://flux:flux@127.0.0.1:${PostgresPort}/flux_llm_kb\n" in script
        assert "\n    FLUX_KB_HOST_DATABASE_URL=" not in script
        assert "FLUX_KB_HOST_DATABASE_URL=postgresql://flux:flux@127.0.0.1:${PostgresPort}/flux_llm_kb`nFLUX_KB_ASR_DEVICE" not in script


def test_postgres_compose_uses_performance_first_local_tuning():
    dev_compose = (ROOT / "docker-compose.yml").read_text(encoding="utf-8")
    install_compose = _embedded_compose_template(_script("install-flux.ps1"))

    for compose in (dev_compose, install_compose):
        assert "-c shared_buffers=768MB" in compose
        assert "-c effective_cache_size=2GB" in compose
        assert "-c work_mem=16MB" in compose
        assert "-c maintenance_work_mem=256MB" in compose
        assert "-c autovacuum_work_mem=128MB" in compose
        assert "-c temp_buffers=16MB" in compose
        assert "-c max_worker_processes=8" in compose
        assert "-c max_parallel_workers=4" in compose
        assert "-c effective_io_concurrency=200" in compose
        assert "-c random_page_cost=1.1" in compose
        assert "-c max_parallel_workers_per_gather=2" in compose
        assert "-c max_parallel_maintenance_workers=2" in compose
        assert "-c track_io_timing=on" in compose
        assert "-c wal_compression=on" in compose
        assert "-c max_wal_size=8GB" in compose
        assert "-c min_wal_size=1GB" in compose
        assert "-c checkpoint_timeout=15min" in compose
        assert "-c checkpoint_completion_target=0.9" in compose

    for compose in (dev_compose, install_compose):
        assert 'shm_size: "1gb"' in compose


def test_worker_compose_commands_use_settings_driven_parallelism_defaults():
    dev_compose = (ROOT / "docker-compose.yml").read_text(encoding="utf-8")
    install_compose = _embedded_compose_template(_script("install-flux.ps1"))
    update_compose = _embedded_compose_template(_script("update-flux.ps1"))

    for compose in (dev_compose, install_compose, update_compose):
        assert "python -m flux_llm_kb.cli event worker run --queue flux.commands.corpus" in compose
        assert "python -m flux_llm_kb.cli event worker run --queue flux.commands.search_index" in compose
        assert "python -m flux_llm_kb.cli event worker run --queue flux.commands.mail_imap" in compose
        assert "python -m flux_llm_kb.cli event worker run --queue flux.commands.outlook" in compose
        assert "python -m flux_llm_kb.cli event worker run --queue flux.commands.automation" in compose
        assert "python -m flux_llm_kb.cli event worker run --queue flux.commands.governance" in compose
        assert "python -m flux_llm_kb.cli event worker run --queue flux.commands.runtime_control" in compose
        assert "python -m flux_llm_kb.cli event worker run --queue flux.commands.gpu_eviction" in compose
        assert "python -m flux_llm_kb.cli event outbox relay --interval 1 --limit 100" in compose
        assert "python -m flux_llm_kb.cli event scheduler run --interval 30 --limit 25" in compose
        assert "python -m flux_llm_kb.cli event callbacks dispatch --queue flux.callbacks.dispatch" in compose
        assert "python -m flux_llm_kb.cli event subscriber run --queue flux.events.audit --subscriber audit" in compose
        assert "python -m flux_llm_kb.cli event subscriber run --queue flux.events.dashboard --subscriber dashboard" in compose
        assert "python -m flux_llm_kb.cli event subscriber run --queue flux.events.diagnostics --subscriber diagnostics" in compose
        assert "crawl worker run --limit 10" not in compose
        assert "--workers 1" not in compose


def test_dockerfile_installs_practical_extractor_pack():
    dockerfile = (ROOT / "Dockerfile").read_text(encoding="utf-8")
    pyproject = (ROOT / "pyproject.toml").read_text(encoding="utf-8")
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")

    assert 'write_requirements("/tmp/requirements-docker.txt", ("api", "corpus", "mcp", "processors", "asr_gpu"))' in dockerfile
    assert 'write_requirements("/tmp/requirements-paddle.txt", ("api", "ocr_paddle"))' in dockerfile
    assert 'for extra in ("api", "corpus", "processors", "gpu")' not in dockerfile
    assert "nvidia/cublas/lib" in dockerfile
    assert "nvidia/cudnn/lib" in dockerfile
    for package in (
        "libreoffice",
        "antiword",
        "catdoc",
        "wv",
        "poppler-utils",
        "p7zip-full",
        "libarchive-tools",
        "unar",
        "zstd",
        "lz4",
        "binutils",
        "ccache",
        "rpm2cpio",
        "ffmpeg",
        "libgl1",
        "libglib2.0-0",
        "calibre",
        "pst-utils",
        "libemail-address-perl",
        "libemail-outlook-message-perl",
        "libimage-exiftool-perl",
        "librsvg2-bin",
        "pandoc",
    ):
        assert package in dockerfile
    assert "tesseract-ocr" not in dockerfile
    for dependency in ("defusedxml", "duckdb", "pyarrow", "faster-whisper", "torch==2.12.1+cu126", "sentence-transformers==5.6.0", "transformers==4.57.6", "accelerate==1.14.0", "paddleocr==3.7.0", "paddlex[ocr]==3.7.2", "paddlepaddle-gpu", "onnxruntime-gpu", "nvidia-cublas-cu12", "nvidia-cudnn-cu12"):
        assert dependency in pyproject
    assert '"paddlepaddle-gpu==3.3.1; platform_system == \'Linux\'"' in pyproject
    assert "paddlepaddle>=3.0" not in pyproject
    assert "PADDLE_GPU_INDEX_URL" not in dockerfile
    assert "PYTORCH_GPU_INDEX_URL" not in dockerfile
    assert "PaddleGpuIndexUrl" in install
    assert "PytorchGpuIndexUrl" in install
    assert "PaddleGpuIndexUrl" in update
    assert "PytorchGpuIndexUrl" in update
    assert '"$SourceRoot[api,corpus,mail,mcp,processors]"' in install
    assert '"$SourceRoot[api,corpus,mail,mcp,processors]"' in update
    assert '"$SourceRoot[api,corpus,mail,mcp,processors,gpu]"' not in install
    assert '"$SourceRoot[api,corpus,mail,mcp,processors,gpu]"' not in update


def test_production_update_bounds_compose_up_and_recovers_created_services():
    update = _script("update-flux.ps1")

    assert "[int]$DockerComposeTimeoutSeconds" in update
    assert "[int]$DockerComposeTimeoutSeconds = 3600" in update
    assert "Invoke-FluxDockerComposeUp" in update
    assert "Invoke-FluxNativeCommand" in update
    assert "[System.Diagnostics.ProcessStartInfo]" in update
    assert "UseShellExecute = $false" in update
    assert "CreateNoWindow = $true" in update
    assert "RedirectStandardOutput = $true" in update
    assert "RedirectStandardError = $true" in update
    assert "ReadToEndAsync()" in update
    assert "WaitForExit($TimeoutSeconds * 1000)" in update
    assert "ExitCode" in update
    assert "Stop-FluxProcessTree" in update
    assert "Start-FluxCreatedContainers" in update
    assert "docker inspect" in update
    assert "{{.State.Status}}" in update
    assert "docker start" in update
    assert "flux-llm-kb-api" in update
    assert "flux-llm-kb-worker" in update
    assert "flux-llm-kb-event-scheduler" in update
    assert "flux-llm-kb-callback-worker" in update
    assert "flux-llm-kb-event-audit-worker" in update
    assert "flux-llm-kb-event-dashboard-worker" in update
    assert "flux-llm-kb-event-diagnostics-worker" in update
    assert "flux-llm-kb-asr" in update
    assert "`$(seq 1 120)" in update
    assert "19071/ApplicationStatus" in update
    assert 'Start-Process -FilePath "docker"' not in update


def test_production_update_can_keep_worker_paused_during_model_cutover():
    update = _script("update-flux.ps1")
    complete_feature = _dev_script("complete-feature.ps1")

    assert "[switch]$SkipWorkerStart" in update
    assert "Stop-FluxWorkerContainer" in update
    assert 'docker ps --filter "name=^/flux-llm-kb-worker$"' in update
    assert 'docker stop -t 45 flux-llm-kb-worker' in update
    assert '$commandWorkerServices -notcontains $_' in update
    assert '$commandWorkerContainers -notcontains $_' in update
    assert "Invoke-FluxDockerComposeUp" in update
    assert "-SkipWorkerStart ([bool]$SkipWorkerStart)" in update
    assert "if ($SkipWorkerStart) {" in update
    assert "Disable-ScheduledTask -TaskName $taskSpec.Name" in update
    assert "continue" in update
    assert "[switch]$SkipWorkerStart" in complete_feature
    assert "$deployCommand += ' -SkipWorkerStart'" in complete_feature


def test_complete_feature_deploys_offline_by_default_with_pip_specific_escape_hatch():
    complete_feature = _dev_script("complete-feature.ps1")

    assert "[switch]$AllowPipDownloads" in complete_feature
    assert "[switch]$RefreshPipDependencies" in complete_feature
    assert "$pipOffline = -not ($AllowPipDownloads -or $RefreshPipDependencies)" in complete_feature
    assert "$deployPipOfflineValue = if ($pipOffline) { '$true' } else { '$false' }" in complete_feature
    assert "-PipOffline:$deployPipOfflineValue" in complete_feature
    assert "If deploy pip dependencies are missing from cache, rerun this closeout with -AllowPipDownloads only." in complete_feature


def test_production_deploy_defaults_match_prefilled_wheel_cache_args():
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")

    for script in (install, update):
        assert "[int]$PipTimeoutSeconds = 180" in script
        assert "[int]$PipRetries = 20" in script
        assert '[bool]$PipOffline = $true' in script
        assert "[string]$PipWheelhousePath = $env:FLUX_KB_PIP_WHEELHOUSE_PATH" in script
        assert "[string]$PipWheelhouseImage = $env:FLUX_KB_PIP_WHEELHOUSE_IMAGE" in script
        assert '"--pull=false"' in script
        assert '"--network", $dockerBuildNetwork' in script
        assert "Resolve-FluxPipWheelhousePath" in script
        assert "Resolve-FluxPipWheelhouseImage" in script
        assert "Invoke-FluxSeedDockerWheelhouse" in script
        assert "Invoke-FluxSeedHostPipWheelhouse" in script
        assert "Invoke-FluxBuildWheelhouseImage" in script
        assert "Assert-FluxWheelhouseCacheReady" in script
        assert 'build_requirements = list(config.get("build-system", {}).get("requires", []))' in script
        assert 'requirements = ["pip"]' in script
        assert 'requirements.extend(config.get("build-system", {}).get("requires", []))' in script
        assert '"--build-context", "flux-wheelhouse=docker-image://$resolvedPipWheelhouseImage"' in script
        assert "flux-wheelhouse=$resolvedPipWheelhousePath" not in script
        assert '"--build-arg", "PIP_OFFLINE' not in script
        assert '$dockerBuildNetwork = if ($dockerBase.SkipSystemPackages) { "none" } else { "default" }' in script
        assert "Durable pip wheelhouse is empty" in script
        assert "Pip wheelhouse image $WheelhouseImage is missing" in script

    assert ".\\scripts\\deploy\\install-flux.ps1 -PipOffline:`$false" in install
    assert ".\\scripts\\deploy\\update-flux.ps1 -PipOffline:`$false" in update


def test_production_deploy_bounds_docker_build_and_pip_installs():
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")

    for script in (install, update):
        assert "[int]$DockerBuildTimeoutSeconds" in script
        assert "[int]$PipInstallTimeoutSeconds" in script
        assert "[int]$PipTimeoutSeconds" in script
        assert "[int]$PipRetries" in script
        assert "Invoke-FluxNativeCommand" in script
        assert "StepName \"docker build\"" in script
        assert "TimeoutSeconds $DockerBuildTimeoutSeconds" in script
        assert "StepName \"pip install production extras\"" in script
        assert "TimeoutSeconds $PipInstallTimeoutSeconds" in script
        assert '"--timeout", ([string]$PipTimeoutSeconds)' in script
        assert '"--retries", ([string]$PipRetries)' in script
        assert '"--cache-dir", $resolvedPipCachePath' in script
        assert '"--no-index", "--find-links", $resolvedPipWheelhousePath' in script


def test_production_package_cache_paths_are_not_pruned_or_deleted_by_default_scripts():
    scripts = {
        "install": _script("install-flux.ps1"),
        "update": _script("update-flux.ps1"),
        "complete_feature": _dev_script("complete-feature.ps1"),
    }

    for script in scripts.values():
        lowered = script.lower()
        assert "docker builder prune" not in lowered
        assert "docker buildx prune" not in lowered
        assert "buildx prune" not in lowered
        assert "builder prune" not in lowered
        assert "docker system prune" not in lowered
        assert "docker image prune" not in lowered
        assert "docker volume prune" not in lowered
        assert "remove-item -literalpath $resolvedpipwheelhousepath" not in lowered
        assert "remove-item -literalpath $resolvedpipcachepath" not in lowered
        assert "remove-item -literalpath $resolvednpmcachepath" not in lowered
        assert "rm -rf /wheelhouse" not in lowered
        assert "rm -rf /npm-cache" not in lowered


def test_dockerfile_declares_oci_image_traceability_labels():
    dockerfile = (ROOT / "Dockerfile").read_text(encoding="utf-8")

    for arg_name in (
        "FLUX_KB_IMAGE_REVISION",
        "FLUX_KB_IMAGE_SOURCE",
        "FLUX_KB_IMAGE_CREATED",
        "FLUX_KB_IMAGE_VERSION",
    ):
        assert f'ARG {arg_name}=""' in dockerfile

    for label, arg_name in (
        ("org.opencontainers.image.revision", "FLUX_KB_IMAGE_REVISION"),
        ("org.opencontainers.image.source", "FLUX_KB_IMAGE_SOURCE"),
        ("org.opencontainers.image.created", "FLUX_KB_IMAGE_CREATED"),
        ("org.opencontainers.image.version", "FLUX_KB_IMAGE_VERSION"),
    ):
        assert f"{label}=${arg_name}" in dockerfile


def test_production_deploy_scripts_pass_authoritative_image_metadata_to_docker_build():
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")

    for script in (install, update):
        compose = _embedded_compose_template(script)
        service_blocks = _compose_service_blocks(compose)
        assert "function Get-FluxBuildMetadata" in script
        assert "git -C $Root rev-parse HEAD" in script
        assert "git -C $Root rev-parse --short HEAD" in script
        assert "git -C $Root status --porcelain" in script
        assert 'throw "Flux source checkout is dirty' in script
        assert 'git -C $Root remote get-url origin' in script
        assert "[uri]$sourceUri" in script
        assert 'ToString("yyyy-MM-ddTHH:mm:ssZ")' in script
        assert "$imageTag = $buildMetadata.ShortRevision" in script
        assert '"--build-arg", "FLUX_KB_IMAGE_REVISION=$($buildMetadata.Revision)"' in script
        assert '"--build-arg", "FLUX_KB_IMAGE_SOURCE=$($buildMetadata.Source)"' in script
        assert '"--build-arg", "FLUX_KB_IMAGE_CREATED=$($buildMetadata.Created)"' in script
        assert '"--build-arg", "FLUX_KB_IMAGE_VERSION=$($buildMetadata.Version)"' in script
        assert "FLUX_KB_IMAGE_REVISION=$($buildMetadata.Revision)" in script
        assert "FLUX_KB_IMAGE_SOURCE=$($buildMetadata.Source)" in script
        assert "FLUX_KB_IMAGE_CREATED=$($buildMetadata.Created)" in script
        assert "FLUX_KB_IMAGE_VERSION=$($buildMetadata.Version)" in script
        for label, env_name in (
            ("org.opencontainers.image.revision", "FLUX_KB_IMAGE_REVISION"),
            ("org.opencontainers.image.source", "FLUX_KB_IMAGE_SOURCE"),
            ("org.opencontainers.image.created", "FLUX_KB_IMAGE_CREATED"),
            ("org.opencontainers.image.version", "FLUX_KB_IMAGE_VERSION"),
        ):
            assert compose.count(f"{label}: `${{{env_name}}}") == 6

        for service in ("api", "worker", "asr", "model-runner", "paddle-runner", "ollama"):
            block = service_blocks[service]
            assert "\n      org.opencontainers.image.version: `${FLUX_KB_IMAGE_VERSION}" in block
            assert "\n    org.opencontainers.image.version: `${FLUX_KB_IMAGE_VERSION}" not in block


def test_verify_image_traceability_script_checks_image_and_container_labels():
    verify = _script("verify-image-traceability.ps1")

    assert "docker image inspect" in verify
    assert "docker inspect" in verify
    assert "org.opencontainers.image.revision" in verify
    assert "org.opencontainers.image.source" in verify
    assert "org.opencontainers.image.created" in verify
    assert "org.opencontainers.image.version" in verify
    assert "$ExpectedRevision" in verify
    assert "exit 1" in verify


def test_production_deploy_can_reuse_local_docker_base_for_fast_updates():
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")
    dockerfile = (ROOT / "Dockerfile").read_text(encoding="utf-8")

    assert "ARG FLUX_KB_DOCKER_BASE_IMAGE=python:3.12-slim" in dockerfile
    assert "ARG FLUX_KB_SKIP_SYSTEM_PACKAGES=false" in dockerfile
    for script in (install, update):
        assert '[ValidateSet("auto", "local", "python")]' in script
        assert '[string]$DockerBaseMode = "auto"' in script
        assert "[string]$DockerBaseImage = $env:FLUX_KB_DOCKER_BASE_IMAGE" in script
        assert "Resolve-FluxDockerBuildBase" in script
        assert "Test-FluxDockerImageExists" in script
        assert "flux-llm-kb-api:local" in script
        assert '"--build-arg", "FLUX_KB_DOCKER_BASE_IMAGE=$($dockerBase.Image)"' in script
        assert '"--build-arg", "FLUX_KB_SKIP_SYSTEM_PACKAGES=$skipSystemPackages"' in script


def test_production_deploy_tags_local_base_with_localhost_alias_to_avoid_registry_lookup():
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")

    for script in (install, update):
        assert "function New-FluxLocalDockerBaseAlias" in script
        assert '"localhost/flux-llm-kb-build-base:$shortId"' in script
        assert 'Invoke-FluxNativeCommand -FilePath "docker" -Arguments @("tag", $Image, $alias)' in script
        assert "$baseAlias = New-FluxLocalDockerBaseAlias -Image $candidateImage" in script
        assert 'return [pscustomobject]@{ Image = $baseAlias; SkipSystemPackages = $true }' in script


def test_production_docker_base_probe_handles_missing_local_image_without_stderr_failure():
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")

    for script in (install, update):
        assert "function Test-FluxDockerImageExists" in script
        assert "[System.Diagnostics.ProcessStartInfo]::new()" in script
        assert '$processInfo.RedirectStandardError = $true' in script
        assert "$process.StandardError.ReadToEnd() | Out-Null" in script
        assert "return $process.ExitCode -eq 0" in script
        assert "docker image inspect $Image *> $null" not in script


def test_production_deploy_supports_custom_pip_index_for_gpu_wheels():
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")

    for script in (install, update):
        assert "[string]$PipIndexUrl = $env:FLUX_KB_PIP_INDEX_URL" in script
        assert '"PIP_INDEX_URL=$PipIndexUrl"' in script
        assert '"--index-url", $PipIndexUrl' in script


def test_production_deploy_supports_custom_apt_mirrors_for_slow_system_packages():
    dockerfile = (ROOT / "Dockerfile").read_text(encoding="utf-8")
    install = _script("install-flux.ps1")
    update = _script("update-flux.ps1")

    assert 'ARG APT_DEBIAN_MIRROR_URL=""' in dockerfile
    assert 'ARG APT_SECURITY_MIRROR_URL=""' in dockerfile
    assert "APT_DEBIAN_MIRROR_URL" in dockerfile
    assert "APT_SECURITY_MIRROR_URL" in dockerfile
    assert "/etc/apt/sources.list.d/debian.sources" in dockerfile
    for script in (install, update):
        assert "[string]$AptDebianMirrorUrl = $env:FLUX_KB_APT_DEBIAN_MIRROR_URL" in script
        assert "[string]$AptSecurityMirrorUrl = $env:FLUX_KB_APT_SECURITY_MIRROR_URL" in script
        assert '"--build-arg", "APT_DEBIAN_MIRROR_URL=$AptDebianMirrorUrl"' in script
        assert '"--build-arg", "APT_SECURITY_MIRROR_URL=$AptSecurityMirrorUrl"' in script


def test_docs_describe_production_runtime_boundary():
    setup = (ROOT / "docs" / "setup.md").read_text(encoding="utf-8")
    architecture = (ROOT / "docs" / "architecture.md").read_text(encoding="utf-8")

    assert "D:\\FluxLLMKB" in setup
    assert "production" in setup.lower()
    assert "docker named volumes" in setup.lower()
    assert "postgresql bind-mounted data on the d drive" not in setup.lower()
    assert "container-owned persistent state" in architecture.lower()
    assert "postgresql bind-mounted data under" not in architecture.lower()
    assert "startup reconciliation" in architecture.lower()
    assert "periodic reconciliation" in architecture.lower()
    assert "repository remains source code only" in setup.lower()
    assert ".\\scripts\\deploy\\verify-image-traceability.ps1" in setup
    assert "docker image inspect" in setup.lower()


def _embedded_compose_template(script: str) -> str:
    function_start = script.find("function Write-FluxCompose")
    if function_start == -1:
        return ""
    for marker, terminator in (('@"', '"@'), ("@'", "'@")):
        start = script.find(marker, function_start)
        if start == -1:
            continue
        end = script.find(terminator, start + len(marker))
        return script[start + len(marker) : end]
    return ""


def _compose_service_blocks(compose: str) -> dict[str, str]:
    blocks: dict[str, list[str]] = {}
    current_service = ""
    current_lines: list[str] = []
    in_services = False
    for line in compose.splitlines():
        if line == "services:":
            in_services = True
            continue
        if not in_services:
            continue
        if line and not line.startswith(" "):
            break
        match = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
        if match:
            if current_service:
                blocks[current_service] = current_lines
            current_service = match.group(1)
            current_lines = [line]
            continue
        if current_service:
            current_lines.append(line)
    if current_service:
        blocks[current_service] = current_lines
    return {service: "\n".join(lines) for service, lines in blocks.items()}


def _service_compose_value(service_block: str, key: str) -> str:
    match = re.search(rf"^    {re.escape(key)}:\s+\"?([^\"\n]+)\"?\s*$", service_block, re.MULTILINE)
    assert match is not None, f"{key} not found in service block:\n{service_block}"
    return match.group(1)


def _compose_memory_mb(value: str) -> int:
    match = re.fullmatch(r"(\d+)(gb|mb)", value)
    assert match is not None
    quantity = int(match.group(1))
    unit = match.group(2)
    if unit == "gb":
        return quantity * 1024
    return quantity


def _assert_compose_memory_budget(
    compose: str,
    expected_limits: dict[str, str],
    expected_total_mb: int,
) -> int:
    service_blocks = _compose_service_blocks(compose)
    assert set(service_blocks) == set(expected_limits)

    total_mb = 0
    for service, expected_memory in expected_limits.items():
        block = service_blocks[service]
        memory = _service_compose_value(block, "mem_limit")
        swap = _service_compose_value(block, "memswap_limit")

        assert memory == expected_memory
        assert swap == memory
        total_mb += _compose_memory_mb(memory)

    assert total_mb == expected_total_mb
    return total_mb
