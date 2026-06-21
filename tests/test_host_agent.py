from pathlib import Path

import pytest

from flux_llm_kb import host_agent


@pytest.mark.filterwarnings(
    "ignore:Using `httpx` with `starlette.testclient` is deprecated:starlette.exceptions.StarletteDeprecationWarning"
)
def test_host_agent_sync_endpoint_routes_to_service(monkeypatch):
    from fastapi.testclient import TestClient

    class FakeService:
        def sync_corpus(self, *, root_name=None, path=None, dry_run=False):
            return {
                "root_name": root_name,
                "path": path,
                "dry_run": dry_run,
                "files_seen": 1,
            }

    monkeypatch.setattr("flux_llm_kb.service.KnowledgeService", lambda: FakeService())

    client = TestClient(host_agent.create_app())

    response = client.post("/crawl/sync", json={"path": "E:\\Temp\\watch-test", "dry_run": True})

    assert response.status_code == 200
    assert response.json() == {
        "root_name": None,
        "path": "E:\\Temp\\watch-test",
        "dry_run": True,
        "files_seen": 1,
    }


def test_host_path_validator_accepts_windows_absolute_path(monkeypatch):
    monkeypatch.setattr(Path, "exists", lambda _self: False)

    result = host_agent.validate_host_path("E:\\Temp\\watch-test")

    assert result["absolute"] is True
    assert result["path_style"] == "windows_drive"
    assert result["status"] in {"missing", "ok"}


def test_host_path_validator_rejects_relative_path():
    result = host_agent.validate_host_path("Temp\\watch-test")

    assert result["absolute"] is False
    assert result["status"] == "invalid"
    assert "absolute" in result["message"]


def test_host_agent_status_reports_platform_and_browse_capability(monkeypatch):
    monkeypatch.setattr(host_agent, "_native_browse_supported", lambda: True)
    monkeypatch.setattr(
        "flux_llm_kb.codex_integration.codex_status",
        lambda: {"status": "ready", "installed": True},
    )

    result = host_agent.status_payload()

    assert result["status"] == "running"
    assert result["browse_supported"] is True
    assert "platform" in result
    assert result["codex"]["status"] == "ready"
