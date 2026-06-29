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
    assert "--mount=type=cache,target=/root/.cache/pip" in dockerfile
    assert "--no-cache-dir" not in dockerfile


def test_dockerfile_pip_build_args_do_not_invalidate_system_package_layer() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    apt_install = dockerfile.index("apt-get install")
    apt_mirror_arg = dockerfile.index('ARG APT_DEBIAN_MIRROR_URL=""')
    pip_index_arg = dockerfile.index('ARG PIP_INDEX_URL=""')
    pyproject_copy = dockerfile.index("COPY pyproject.toml ./")

    assert apt_mirror_arg < apt_install
    assert apt_install < pip_index_arg < pyproject_copy


def test_docker_compose_defines_corpus_worker_service() -> None:
    compose = Path("docker-compose.yml").read_text(encoding="utf-8")

    assert "  worker:" in compose
    assert "python -m flux_llm_kb.cli crawl worker run" in compose
    assert "FLUX_KB_DATABASE_URL: postgresql://flux:flux@postgres:5432/flux_llm_kb" in compose


def test_dashboard_dev_scripts_manage_worker_service() -> None:
    start_script = Path("scripts/start-dashboard-dev.ps1").read_text(encoding="utf-8")
    status_script = Path("scripts/status-dashboard-dev.ps1").read_text(encoding="utf-8")
    stop_script = Path("scripts/stop-dashboard-dev.ps1").read_text(encoding="utf-8")

    assert "docker compose up -d --build postgres api worker" in start_script
    assert "docker compose ps api worker postgres" in status_script
    assert "docker compose stop api worker" in stop_script
