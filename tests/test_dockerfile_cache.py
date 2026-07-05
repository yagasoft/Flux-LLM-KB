from __future__ import annotations

from pathlib import Path


def test_dockerfile_installs_dependencies_before_application_code() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    dependency_install = dockerfile.index("-r /tmp/requirements-docker.txt")
    source_copy = dockerfile.index("COPY src ./src")
    plugin_copy = dockerfile.index("COPY plugins ./plugins")

    assert "COPY pyproject.toml ./" in dockerfile
    assert "COPY pyproject.toml README.md ./" not in dockerfile
    assert dependency_install < source_copy
    assert dependency_install < plugin_copy

    readme_copy = dockerfile.index("COPY README.md ./")
    assert dependency_install < readme_copy


def test_dockerfile_uses_pip_buildkit_cache() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    assert "# syntax=docker/dockerfile:" in dockerfile
    assert dockerfile.count("id=flux-llm-kb-pip-wheelhouse") == 1
    assert "--mount=type=cache,id=flux-llm-kb-pip-wheelhouse,target=/opt/flux-wheelhouse,sharing=locked" in dockerfile
    assert "export PIP_CACHE_DIR=/opt/flux-wheelhouse/.pip-cache" in dockerfile
    assert "id=flux-llm-kb-pip-cache" not in dockerfile
    assert "target=/root/.cache/pip" not in dockerfile
    assert "--no-cache-dir" not in dockerfile
    assert "--upgrade pip" not in dockerfile


def test_dockerfile_uses_locked_wheelhouse_constraints() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")
    runtime_lock = Path("docker/requirements-docker.lock").read_text(encoding="utf-8")
    paddle_lock = Path("docker/requirements-paddle.lock").read_text(encoding="utf-8")

    assert "COPY docker/requirements-docker.lock /tmp/requirements-docker.lock" in dockerfile
    assert "COPY docker/requirements-paddle.lock /tmp/requirements-paddle.lock" in dockerfile
    assert "--constraint /tmp/requirements-docker.lock" in dockerfile
    assert "--constraint /tmp/requirements-paddle.lock" in dockerfile
    assert "wheelhouse_find_links=\"--find-links /opt/flux-durable-wheelhouse --find-links /opt/flux-wheelhouse\"" in dockerfile
    assert 'build_requirements = list(config.get("build-system", {}).get("requires", []))' in dockerfile
    assert 'requirements = build_requirements + list(config["project"]["dependencies"])' in dockerfile
    assert "--no-index $wheelhouse_find_links --constraint \"$constraint\"" in dockerfile
    assert "download_requirements python /tmp/requirements-docker.txt /tmp/requirements-docker.lock" in dockerfile
    assert "download_requirements /opt/flux-paddle/bin/python /tmp/requirements-paddle.txt /tmp/requirements-paddle.lock" in dockerfile
    assert "aio-pika==9.6.2" in runtime_lock
    assert "aiormq==6.9.4" in runtime_lock
    assert "pamqp==3.3.0" in runtime_lock
    assert "yarl==1.24.2" in runtime_lock
    assert "multidict==6.7.1" in runtime_lock
    assert "propcache==0.5.2" in runtime_lock
    assert "compressed-tensors==0.17.1" in runtime_lock
    assert "loguru==0.7.3" in runtime_lock
    assert "nvidia-cuda-runtime-cu12==12.6.77" in runtime_lock
    assert "nvidia-cublas-cu12==12.6.4.1" in runtime_lock
    assert "nvidia-cudnn-cu12==9.10.2.21" in runtime_lock
    assert "nvidia-cuda-runtime-cu12==12.6.77" in paddle_lock
    assert "nvidia-cuda-nvrtc-cu12==12.6.77" in paddle_lock
    assert "nvidia-cuda-cupti-cu12==12.6.80" in paddle_lock
    assert "nvidia-cublas-cu12==12.6.4.1" in paddle_lock
    assert "nvidia-cudnn-cu12==9.5.1.17" in paddle_lock


def test_dockerfile_dependency_downloads_are_offline_only() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    assert 'ARG PIP_OFFLINE' not in dockerfile
    assert 'ARG PIP_INDEX_URL=""' not in dockerfile
    assert "PADDLE_GPU_INDEX_URL" not in dockerfile
    assert "PYTORCH_GPU_INDEX_URL" not in dockerfile
    assert "pip_index_args" not in dockerfile
    assert "pip_extra_index_args" not in dockerfile
    assert "--extra-index-url" not in dockerfile
    assert "--index-url" not in dockerfile
    assert "Required Docker wheels are missing from the persistent wheelhouse image" in dockerfile


def test_dockerfile_materializes_persistent_wheelhouse_before_install() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    dependency_stage = dockerfile.index("AS runtime-deps")
    wheelhouse = dockerfile.index(
        "--mount=type=cache,id=flux-llm-kb-pip-wheelhouse,target=/opt/flux-wheelhouse"
    )
    download = dockerfile.index("download_requirements python /tmp/requirements-docker.txt")
    install = dockerfile.index("python -m pip install --no-index $wheelhouse_find_links")
    runtime_stage = dockerfile.index("FROM runtime-deps AS runtime")

    assert dependency_stage < wheelhouse
    assert wheelhouse < download
    assert download < install
    assert install < runtime_stage
    assert "--find-links /opt/flux-durable-wheelhouse --find-links /opt/flux-wheelhouse" in dockerfile
    assert "pip install --dry-run" not in dockerfile
    assert "--no-index $wheelhouse_find_links --constraint \"$constraint\" --dest /opt/flux-wheelhouse -r \"$requirements\"" in dockerfile
    assert "--dest /opt/flux-wheelhouse -r \"$requirements\"" in dockerfile
    assert "PIP_OFFLINE" not in dockerfile


def test_dockerfile_builds_isolated_paddle_runtime_from_same_wheelhouse() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    assert "/tmp/requirements-docker.txt" in dockerfile
    assert "/tmp/requirements-paddle.txt" in dockerfile
    assert 'write_requirements("/tmp/requirements-docker.txt", ("api", "corpus", "mcp", "processors", "asr_gpu"))' in dockerfile
    assert 'write_requirements("/tmp/requirements-paddle.txt", ("api", "ocr_paddle"))' in dockerfile
    assert "python -m venv /opt/flux-paddle" in dockerfile
    assert "download_requirements /opt/flux-paddle/bin/python /tmp/requirements-paddle.txt /tmp/requirements-paddle.lock" in dockerfile
    assert "/opt/flux-paddle/bin/python -m pip install --no-index $wheelhouse_find_links" in dockerfile
    assert "/opt/flux-paddle/bin/python -m pip install --no-index $wheelhouse_find_links --constraint /tmp/requirements-paddle.lock" in dockerfile
    assert "FLUX_KB_PADDLE_PYTHON=/opt/flux-paddle/bin/python" in dockerfile


def test_pyproject_splits_torch_and_paddle_dependency_groups() -> None:
    pyproject = Path("pyproject.toml").read_text(encoding="utf-8")

    processors_start = pyproject.index("processors = [")
    ocr_paddle_start = pyproject.index("ocr_paddle = [")
    processors = pyproject[processors_start:ocr_paddle_start]
    ocr_paddle = pyproject[ocr_paddle_start:]

    assert "torch==2.12.1+cu126" in processors
    assert "sentence-transformers==5.6.0" in processors
    assert "transformers==4.57.6" in processors
    assert "accelerate==1.14.0" in processors
    assert "compressed-tensors==0.17.1" in processors
    assert "paddleocr" not in processors
    assert "paddlex" not in processors
    assert "paddlepaddle-gpu" not in processors
    assert "paddleocr==3.7.0" in ocr_paddle
    assert "paddlex[ocr]==3.7.2" in ocr_paddle
    assert '"paddlepaddle-gpu==3.3.1; platform_system == \'Linux\'"' in ocr_paddle


def test_pyproject_pins_gpu_wheels_to_cached_versions() -> None:
    pyproject = Path("pyproject.toml").read_text(encoding="utf-8")

    assert "nvidia-cuda-runtime-cu12==12.6.77" in pyproject
    assert "nvidia-cublas-cu12==12.6.4.1" in pyproject
    assert "nvidia-cudnn-cu12==9.10.2.21" in pyproject
    assert "nvidia-cuda-runtime-cu12>=12" not in pyproject
    assert "nvidia-cublas-cu12>=12" not in pyproject
    assert "nvidia-cudnn-cu12>=9" not in pyproject


def test_dockerfile_installs_mcp_extra_for_containerized_codex_retrieval() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    assert 'write_requirements("/tmp/requirements-docker.txt", ("api", "corpus", "mcp", "processors", "asr_gpu"))' in dockerfile
    assert "nvidia/cublas/lib" in dockerfile
    assert "nvidia/cudnn/lib" in dockerfile
    assert "nvidia/nccl/lib" in dockerfile


def test_dockerfile_keeps_compiler_for_quantized_qwen_runtime() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    assert "gcc \\" in dockerfile
    assert "g++ \\" in dockerfile
    assert "ccache \\" in dockerfile


def test_dockerfile_installs_focused_media_ocr_runtime_libraries() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    assert "ffmpeg \\" in dockerfile
    assert "libgl1 \\" in dockerfile
    assert "libglib2.0-0 \\" in dockerfile


def test_dockerfile_pip_build_args_do_not_invalidate_system_package_layer() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    apt_install = dockerfile.index("apt-get install")
    apt_mirror_arg = dockerfile.index('ARG APT_DEBIAN_MIRROR_URL=""')
    pyproject_copy = dockerfile.index("COPY pyproject.toml ./")

    assert apt_mirror_arg < apt_install
    assert apt_install < pyproject_copy


def test_dockerfile_can_reuse_existing_runtime_base_for_updates() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    assert "ARG FLUX_KB_DOCKER_BASE_IMAGE=python:3.12-slim" in dockerfile
    assert "FROM ${FLUX_KB_DOCKER_BASE_IMAGE}" in dockerfile
    assert "ARG FLUX_KB_SKIP_SYSTEM_PACKAGES=false" in dockerfile
    assert 'if [ "$FLUX_KB_SKIP_SYSTEM_PACKAGES" = "true" ]' in dockerfile
    assert "Skipping system package installation" in dockerfile
    assert "--mount=type=cache,id=flux-llm-kb-apt-cache,target=/var/cache/apt,sharing=locked" in dockerfile
    assert "--mount=type=cache,id=flux-llm-kb-apt-lists,target=/var/lib/apt/lists,sharing=locked" in dockerfile


def test_docker_compose_defines_corpus_worker_service() -> None:
    compose = Path("docker-compose.yml").read_text(encoding="utf-8")

    assert "additional_contexts:" in compose
    assert "flux-wheelhouse: docker-image://flux-llm-kb-wheelhouse:local" in compose
    assert "flux-wheelhouse=." not in compose
    assert "  worker:" in compose
    assert "python -m flux_llm_kb.cli event worker run --queue flux.commands.corpus" in compose
    assert "  outlook-worker:" in compose
    assert "python -m flux_llm_kb.cli event worker run --queue flux.commands.outlook" in compose
    assert "  gpu-eviction-worker:" in compose
    assert "python -m flux_llm_kb.cli event worker run --queue flux.commands.gpu_eviction" in compose
    assert "  rabbitmq:" in compose
    assert "image: rabbitmq:4.3-management" in compose
    assert "  outbox-relay:" in compose
    assert "python -m flux_llm_kb.cli event outbox relay" in compose
    assert "  event-scheduler:" in compose
    assert "python -m flux_llm_kb.cli event scheduler run --interval 30 --limit 25" in compose
    assert "  callback-worker:" in compose
    assert "python -m flux_llm_kb.cli event callbacks dispatch --queue flux.callbacks.dispatch" in compose
    assert "  event-audit-worker:" in compose
    assert "python -m flux_llm_kb.cli event subscriber run --queue flux.events.audit --subscriber audit" in compose
    assert "  event-dashboard-worker:" in compose
    assert "python -m flux_llm_kb.cli event subscriber run --queue flux.events.dashboard --subscriber dashboard" in compose
    assert "  event-diagnostics-worker:" in compose
    assert "python -m flux_llm_kb.cli event subscriber run --queue flux.events.diagnostics --subscriber diagnostics" in compose
    assert "crawl worker run --limit 10" not in compose
    assert "FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb" in compose
    assert "FLUX_KB_RABBITMQ_URL: amqp://flux:flux@rabbitmq:5672/flux" in compose


def test_dashboard_dev_scripts_manage_worker_service() -> None:
    start_script = Path("scripts/start-dashboard-dev.ps1").read_text(encoding="utf-8")
    status_script = Path("scripts/status-dashboard-dev.ps1").read_text(encoding="utf-8")
    stop_script = Path("scripts/stop-dashboard-dev.ps1").read_text(encoding="utf-8")

    assert "docker compose up -d --build postgres rabbitmq api outbox-relay event-scheduler worker search-index-worker mail-worker outlook-worker automation-worker governance-worker runtime-control-worker gpu-eviction-worker callback-worker event-audit-worker event-dashboard-worker event-diagnostics-worker" in start_script
    assert "docker compose ps api worker search-index-worker mail-worker outlook-worker automation-worker governance-worker runtime-control-worker gpu-eviction-worker callback-worker event-audit-worker event-dashboard-worker event-diagnostics-worker event-scheduler outbox-relay rabbitmq postgres" in status_script
    assert "docker compose stop api worker search-index-worker mail-worker outlook-worker automation-worker governance-worker runtime-control-worker gpu-eviction-worker callback-worker event-audit-worker event-dashboard-worker event-diagnostics-worker event-scheduler outbox-relay" in stop_script
