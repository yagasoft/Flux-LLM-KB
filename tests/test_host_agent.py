from pathlib import Path

import pytest

from flux_llm_kb import host_agent
from flux_llm_kb.watcher import WatchEvent


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


@pytest.mark.filterwarnings(
    "ignore:Using `httpx` with `starlette.testclient` is deprecated:starlette.exceptions.StarletteDeprecationWarning"
)
def test_host_agent_backfill_endpoint_routes_to_service(monkeypatch):
    from fastapi.testclient import TestClient

    class FakeService:
        def run_corpus_backfill(self, **kwargs):
            return {"backfill": kwargs, "completed": 3}

    monkeypatch.setattr("flux_llm_kb.service.KnowledgeService", lambda: FakeService())

    client = TestClient(host_agent.create_app())

    response = client.post(
        "/crawl/backfill",
        json={"kind": "all", "limit": 10, "workers": 1, "root_name": "watch-test"},
    )

    assert response.status_code == 200
    assert response.json()["backfill"] == {
        "kind": "all",
        "limit": 10,
        "workers": 1,
        "root_name": "watch-test",
    }


def test_remote_backfill_allows_host_root_processing(monkeypatch):
    requests: list[tuple[str, str, dict]] = []

    def fake_request_json(method, url, payload=None, **_kwargs):
        requests.append((method, url, payload or {}))
        return {"status": "ok", "completed": 2}

    monkeypatch.setattr(host_agent, "_request_json", fake_request_json)

    result = host_agent.remote_backfill(
        kind="all",
        limit=10,
        workers=1,
        root_name="watch-test",
        agent_url="http://127.0.0.1:8799",
    )

    assert result["completed"] == 2
    assert requests == [
        (
            "POST",
            "http://127.0.0.1:8799/crawl/backfill",
            {"kind": "all", "limit": 10, "workers": 1, "root_name": "watch-test"},
        )
    ]


def test_host_agent_watcher_loop_records_heartbeat_and_syncs_changed_file(monkeypatch, tmp_path):
    heartbeats: list[str] = []
    watch_events: list[str] = []
    synced: list[dict] = []

    monkeypatch.setattr(
        host_agent.database,
        "list_monitored_roots",
        lambda watch_enabled=None: [
            {
                "name": "watch-test",
                "root_path": str(tmp_path),
                "enabled": True,
                "watch_enabled": True,
                "recursive": True,
                "metadata": {"host_access": "host_agent"},
            },
            {
                "name": "docker-root",
                "root_path": "/data/docs",
                "enabled": True,
                "watch_enabled": True,
                "recursive": True,
                "metadata": {"host_access": "direct"},
            },
        ],
    )
    monkeypatch.setattr(host_agent.database, "record_watcher_heartbeat", lambda *, root_name: heartbeats.append(root_name))
    monkeypatch.setattr(host_agent.database, "record_watch_event", lambda *, root_name: watch_events.append(root_name))
    monkeypatch.setattr(host_agent.database, "record_watch_error", lambda **_kwargs: None)

    class FakeService:
        def sync_corpus(self, **kwargs):
            synced.append(kwargs)
            return {"ok": True}

    class FakeWatcher:
        def __init__(self, load_roots, **_kwargs):
            self.load_roots = load_roots

        def poll_once(self, *, seed=False):
            assert [root.name for root in self.load_roots()] == ["watch-test"]
            return []

        def drain_events(self):
            return [
                WatchEvent(
                    root_name="watch-test",
                    root_path=tmp_path,
                    path=tmp_path / "changed.md",
                    relative_path="changed.md",
                    action="changed",
                )
            ]

    loop = host_agent.HostAgentWatcherLoop(
        service_factory=lambda: FakeService(),
        watcher_factory=lambda load_roots, **kwargs: FakeWatcher(load_roots, **kwargs),
    )

    result = loop.run_once(seed=False)

    assert result == {"status": "running", "roots": 1, "events": 1}
    assert heartbeats == ["watch-test"]
    assert watch_events == ["watch-test"]
    assert synced == [{"root_name": "watch-test", "path": str(tmp_path / "changed.md")}]


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
        host_agent,
        "_host_runtime_checks",
        lambda: {"git": {"ok": True, "message": "git", "required": True}},
    )
    monkeypatch.setattr(
        "flux_llm_kb.codex_integration.codex_status",
        lambda: {"status": "ready", "installed": True},
    )

    result = host_agent.status_payload()

    assert result["status"] == "running"
    assert result["browse_supported"] is True
    assert "platform" in result
    assert result["codex"]["status"] == "ready"
    assert result["runtime"]["git"]["ok"] is True


def test_remote_browse_folder_allows_user_interaction_time(monkeypatch):
    timeouts: list[float | None] = []

    class FakeResponse:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

        def read(self):
            return b'{"status": "selected", "path": "E:\\\\Temp\\\\watch-test"}'

    def fake_urlopen(_request, timeout=None):
        timeouts.append(timeout)
        return FakeResponse()

    monkeypatch.setattr(host_agent.request, "urlopen", fake_urlopen)

    result = host_agent.remote_browse_folder(agent_url="http://127.0.0.1:8799")

    assert result["status"] == "selected"
    assert result["path"] == "E:\\Temp\\watch-test"
    assert timeouts == [host_agent.HOST_AGENT_BROWSE_TIMEOUT_SECONDS]
    assert host_agent.HOST_AGENT_BROWSE_TIMEOUT_SECONDS >= 120


def test_remote_status_keeps_short_timeout(monkeypatch):
    timeouts: list[float | None] = []

    class FakeResponse:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

        def read(self):
            return b'{"status": "running"}'

    def fake_urlopen(_request, timeout=None):
        timeouts.append(timeout)
        return FakeResponse()

    monkeypatch.setattr(host_agent.request, "urlopen", fake_urlopen)

    result = host_agent.remote_status(agent_url="http://127.0.0.1:8799")

    assert result["status"] == "running"
    assert timeouts == [host_agent.HOST_AGENT_REQUEST_TIMEOUT_SECONDS]
