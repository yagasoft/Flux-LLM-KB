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


def test_dockerfile_materializes_persistent_wheelhouse_before_install() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    dependency_stage = dockerfile.index("AS runtime-deps")
    wheelhouse = dockerfile.index(
        "--mount=type=cache,id=flux-llm-kb-pip-wheelhouse,target=/opt/flux-wheelhouse"
    )
    download = dockerfile.index("download_requirements python /tmp/requirements-docker.txt")
    install = dockerfile.index("python -m pip install --no-index --find-links /opt/flux-wheelhouse")
    runtime_stage = dockerfile.index("FROM runtime-deps AS runtime")

    assert dependency_stage < wheelhouse
    assert wheelhouse < download
    assert download < install
    assert install < runtime_stage
    assert "--find-links /opt/flux-wheelhouse" in dockerfile
    assert "pip install --dry-run" not in dockerfile
    assert "--no-index --find-links /opt/flux-wheelhouse --dest /opt/flux-wheelhouse -r \"$requirements\"" in dockerfile
    assert "--dest /opt/flux-wheelhouse -r \"$requirements\"" in dockerfile
    assert 'ARG PIP_OFFLINE=false' in dockerfile
    assert 'if [ "$PIP_OFFLINE" = "true" ]' in dockerfile


def test_dockerfile_builds_isolated_paddle_runtime_from_same_wheelhouse() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    assert "/tmp/requirements-docker.txt" in dockerfile
    assert "/tmp/requirements-paddle.txt" in dockerfile
    assert 'write_requirements("/tmp/requirements-docker.txt", ("api", "corpus", "mcp", "processors", "asr_gpu"))' in dockerfile
    assert 'write_requirements("/tmp/requirements-paddle.txt", ("api", "ocr_paddle"))' in dockerfile
    assert "python -m venv /opt/flux-paddle" in dockerfile
    assert "download_requirements /opt/flux-paddle/bin/python /tmp/requirements-paddle.txt" in dockerfile
    assert "/opt/flux-paddle/bin/python -m pip install --no-index --find-links /opt/flux-wheelhouse" in dockerfile
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
    assert "paddleocr" not in processors
    assert "paddlex" not in processors
    assert "paddlepaddle-gpu" not in processors
    assert "paddleocr==3.7.0" in ocr_paddle
    assert "paddlex[ocr]==3.7.2" in ocr_paddle
    assert '"paddlepaddle-gpu==3.3.1; platform_system == \'Linux\'"' in ocr_paddle


def test_dockerfile_installs_mcp_extra_for_containerized_codex_retrieval() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    assert 'write_requirements("/tmp/requirements-docker.txt", ("api", "corpus", "mcp", "processors", "asr_gpu"))' in dockerfile
    assert "nvidia/cublas/lib" in dockerfile
    assert "nvidia/cudnn/lib" in dockerfile
    assert "nvidia/nccl/lib" in dockerfile
    assert 'ARG PADDLE_GPU_INDEX_URL="https://www.paddlepaddle.org.cn/packages/stable/cu126/"' in dockerfile
    assert 'ARG PYTORCH_GPU_INDEX_URL="https://download.pytorch.org/whl/cu126"' in dockerfile
    assert 'pip_extra_index_args="$pip_extra_index_args --extra-index-url $PADDLE_GPU_INDEX_URL"' in dockerfile
    assert 'pip_extra_index_args="$pip_extra_index_args --extra-index-url $PYTORCH_GPU_INDEX_URL"' in dockerfile


def test_dockerfile_keeps_compiler_for_quantized_qwen_runtime() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    assert "gcc \\" in dockerfile
    assert "g++ \\" in dockerfile
    assert "ccache \\" in dockerfile


def test_dockerfile_pip_build_args_do_not_invalidate_system_package_layer() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    apt_install = dockerfile.index("apt-get install")
    apt_mirror_arg = dockerfile.index('ARG APT_DEBIAN_MIRROR_URL=""')
    pip_index_arg = dockerfile.index('ARG PIP_INDEX_URL=""')
    pyproject_copy = dockerfile.index("COPY pyproject.toml ./")

    assert apt_mirror_arg < apt_install
    assert apt_install < pip_index_arg < pyproject_copy


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

    assert "  worker:" in compose
    assert "python -m flux_llm_kb.cli crawl worker run" in compose
    assert "--limit 10" not in compose
    assert "FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb" in compose


def test_dashboard_dev_scripts_manage_worker_service() -> None:
    start_script = Path("scripts/start-dashboard-dev.ps1").read_text(encoding="utf-8")
    status_script = Path("scripts/status-dashboard-dev.ps1").read_text(encoding="utf-8")
    stop_script = Path("scripts/stop-dashboard-dev.ps1").read_text(encoding="utf-8")

    assert "docker compose up -d --build postgres api worker" in start_script
    assert "docker compose ps api worker postgres" in status_script
    assert "docker compose stop api worker" in stop_script
