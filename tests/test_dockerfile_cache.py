from __future__ import annotations

from pathlib import Path


def test_dockerfile_installs_dependencies_before_application_code() -> None:
    dockerfile = Path("Dockerfile").read_text(encoding="utf-8")

    dependency_install = dockerfile.index("pip install -r /tmp/requirements-docker.txt")
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
