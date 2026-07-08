import tomllib
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_dashboard_assets_are_packaged():
    pyproject = (ROOT / "pyproject.toml").read_text(encoding="utf-8")

    assert "dashboard_static/*.html" in pyproject
    assert "dashboard_static/assets/*" in pyproject


def test_docker_api_service_hosts_dashboard():
    compose = (ROOT / "docker-compose.yml").read_text(encoding="utf-8")
    dockerfile = (ROOT / "Dockerfile").read_text(encoding="utf-8")

    assert "api:" in compose
    assert "additional_contexts:" in compose
    assert "flux-wheelhouse: docker-image://flux-llm-kb-wheelhouse:local" in compose
    assert "8765:8765" in compose
    assert "FLUX_KB_DATABASE_URL" in compose
    assert "uvicorn" in dockerfile
    assert "flux_llm_kb.rest_api:create_app" in dockerfile


def test_api_extra_installs_websocket_transport():
    pyproject = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))

    api_extra = pyproject["project"]["optional-dependencies"]["api"]

    assert "uvicorn>=0.30" in api_extra
    assert "websockets>=12" in api_extra


def test_dashboard_dev_scripts_refresh_build_and_runtime():
    start_script = (ROOT / "scripts" / "start-dashboard-dev.ps1").read_text(encoding="utf-8")
    stop_script = (ROOT / "scripts" / "stop-dashboard-dev.ps1").read_text(encoding="utf-8")
    status_script = (ROOT / "scripts" / "status-dashboard-dev.ps1").read_text(encoding="utf-8")

    assert "npm --prefix dashboard run build" in start_script
    assert "docker compose up -d --build postgres rabbitmq api outbox-relay event-scheduler worker" in start_script
    assert "uvicorn flux_llm_kb.rest_api:create_app --factory" in start_script
    assert "--reload" not in start_script
    assert "http://127.0.0.1:8765/dashboard" in start_script
    assert "Stop-Process" in stop_script
    assert "dashboard-dev-api.pid" in status_script
    assert "$pid =" not in stop_script.lower()
    assert "$pid =" not in status_script.lower()
