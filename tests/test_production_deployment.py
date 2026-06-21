from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def _script(name: str) -> str:
    return (ROOT / "scripts" / "deploy" / name).read_text(encoding="utf-8")


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
    assert "E:\\LLM KB" not in install
    assert "private\\runtime" not in install
    assert "127.0.0.1:${ApiPort}:8765" in install
    assert "restart: unless-stopped" in install
    assert "Flux production status" in status


def test_production_update_uses_prebuilt_images_not_repo_context_compose_build():
    update = _script("update-flux.ps1")

    assert "docker build" in update
    assert "flux-llm-kb-api:" in update
    assert "up -d --no-build postgres api worker" in update
    assert "FLUX_KB_IMAGE_TAG" in update
    assert "build:" not in _embedded_compose_template(update)


def test_docs_describe_production_runtime_boundary():
    setup = (ROOT / "docs" / "setup.md").read_text(encoding="utf-8")
    architecture = (ROOT / "docs" / "architecture.md").read_text(encoding="utf-8")

    assert "D:\\FluxLLMKB" in setup
    assert "production" in setup.lower()
    assert "startup reconciliation" in architecture.lower()
    assert "periodic reconciliation" in architecture.lower()
    assert "repository remains source code only" in setup.lower()


def _embedded_compose_template(script: str) -> str:
    marker = "@'"
    start = script.find(marker)
    if start == -1:
        return ""
    end = script.find("'@", start + len(marker))
    return script[start:end]
