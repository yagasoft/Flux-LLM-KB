from pathlib import Path
from types import SimpleNamespace

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


@pytest.mark.filterwarnings(
    "ignore:Using `httpx` with `starlette.testclient` is deprecated:starlette.exceptions.StarletteDeprecationWarning"
)
def test_host_agent_benchmark_endpoint_routes_to_service(monkeypatch):
    from fastapi.testclient import TestClient

    class FakeService:
        def run_benchmark(self, **kwargs):
            return {"benchmark": kwargs, "runs": []}

    monkeypatch.setattr("flux_llm_kb.service.KnowledgeService", lambda: FakeService())

    client = TestClient(host_agent.create_app())

    response = client.post(
        "/acceleration/benchmarks/run",
        json={"scope": "root", "root_name": "watch-test", "mode": "scan", "max_files": 20, "deployment_label": "after", "scenario": "host_cloud"},
    )

    assert response.status_code == 200
    assert response.json()["benchmark"] == {
        "fixture": "all",
        "files": 10,
        "mode": "scan",
        "passes": 1,
        "label": None,
        "compare_label": None,
        "workers": 1,
        "family": "all",
        "scope": "root",
        "root_name": "watch-test",
        "path": None,
        "max_files": 20,
        "deployment_label": "after",
        "scenario": "host_cloud",
        "include_model_probe": False,
    }


@pytest.mark.filterwarnings(
    "ignore:Using `httpx` with `starlette.testclient` is deprecated:starlette.exceptions.StarletteDeprecationWarning"
)
def test_host_agent_startup_runs_watcher_and_worker_loops(monkeypatch):
    from fastapi.testclient import TestClient

    events: list[str] = []

    class FakeWatcherLoop:
        def start(self):
            events.append("watcher-start")

        def stop(self):
            events.append("watcher-stop")

    class FakeWorkerLoop:
        def start(self):
            events.append("worker-start")

        def stop(self):
            events.append("worker-stop")

    monkeypatch.setattr(host_agent, "HostAgentWatcherLoop", lambda: FakeWatcherLoop())
    monkeypatch.setattr(host_agent, "HostAgentWorkerLoop", lambda: FakeWorkerLoop(), raising=False)

    with TestClient(host_agent.create_app(start_watcher=True)):
        assert "watcher-start" in events
        assert "worker-start" in events

    assert "watcher-stop" in events
    assert "worker-stop" in events


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


def test_remote_benchmark_routes_to_host_agent(monkeypatch):
    requests: list[tuple[str, str, dict]] = []

    def fake_request_json(method, url, payload=None, **_kwargs):
        requests.append((method, url, payload or {}))
        return {"status": "ok", "runs": []}

    monkeypatch.setattr(host_agent, "_request_json", fake_request_json)

    result = host_agent.remote_benchmark(
        scope="root",
        root_name="watch-test",
        mode="scan",
        max_files=20,
        deployment_label="after",
        scenario="host_cloud",
        agent_url="http://127.0.0.1:8799",
    )

    assert result["status"] == "ok"
    assert requests == [
        (
            "POST",
            "http://127.0.0.1:8799/acceleration/benchmarks/run",
            {
                "fixture": "all",
                "files": 10,
                "mode": "scan",
                "passes": 1,
                "label": None,
                "compare_label": None,
                "workers": 1,
                "family": "all",
                "scope": "root",
                "root_name": "watch-test",
                "path": None,
                "max_files": 20,
                "deployment_label": "after",
                "scenario": "host_cloud",
                "include_model_probe": False,
            },
        )
    ]


@pytest.mark.filterwarnings(
    "ignore:Using `httpx` with `starlette.testclient` is deprecated:starlette.exceptions.StarletteDeprecationWarning"
)
def test_host_file_action_endpoint_rejects_browser_supplied_path(monkeypatch):
    from fastapi.testclient import TestClient

    monkeypatch.setattr(host_agent, "perform_file_action", lambda **_kwargs: {"state": "opened"})
    client = TestClient(host_agent.create_app())

    response = client.post(
        "/file-actions",
        json={"asset_id": "asset-1", "action": "open", "path": "E:\\Unsafe\\from-browser.txt"},
    )

    assert response.status_code == 422


def test_host_file_action_rejects_unknown_asset(monkeypatch):
    audits: list[dict] = []
    monkeypatch.setattr(host_agent.database, "get_source_asset_for_file_action", lambda asset_id: None, raising=False)
    monkeypatch.setattr(host_agent.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    result = host_agent.perform_file_action(asset_id="missing-asset", action="open")

    assert result["state"] == "not_allowed"
    assert audits[0]["event_type"] == "host.file_action"
    assert audits[0]["details"]["state"] == "not_allowed"


def test_host_file_action_reports_deleted_asset(monkeypatch, tmp_path):
    monkeypatch.setattr(
        host_agent.database,
        "get_source_asset_for_file_action",
        lambda asset_id: {
            "id": asset_id,
            "root_path": str(tmp_path),
            "path": "deleted.txt",
            "deleted_at": "2026-06-23T00:00:00+00:00",
            "status": "deleted",
        },
        raising=False,
    )
    monkeypatch.setattr(host_agent.database, "record_audit_event", lambda **_kwargs: None)

    result = host_agent.perform_file_action(asset_id="asset-1", action="reveal")

    assert result["state"] == "deleted"


def test_host_file_action_reports_missing_file(monkeypatch, tmp_path):
    monkeypatch.setattr(
        host_agent.database,
        "get_source_asset_for_file_action",
        lambda asset_id: {
            "id": asset_id,
            "root_path": str(tmp_path),
            "path": "missing.txt",
            "deleted_at": None,
            "status": "indexed",
        },
        raising=False,
    )
    monkeypatch.setattr(host_agent.database, "record_audit_event", lambda **_kwargs: None)

    result = host_agent.perform_file_action(asset_id="asset-1", action="open")

    assert result["state"] == "missing"


def test_host_file_action_rejects_asset_path_outside_root(monkeypatch, tmp_path):
    monkeypatch.setattr(
        host_agent.database,
        "get_source_asset_for_file_action",
        lambda asset_id: {
            "id": asset_id,
            "root_path": str(tmp_path),
            "path": "../outside.txt",
            "deleted_at": None,
            "status": "indexed",
        },
        raising=False,
    )
    monkeypatch.setattr(host_agent.database, "record_audit_event", lambda **_kwargs: None)

    result = host_agent.perform_file_action(asset_id="asset-1", action="open")

    assert result["state"] == "not_allowed"


def test_host_file_action_opens_known_asset_and_audits(monkeypatch, tmp_path):
    target = tmp_path / "known.txt"
    target.write_text("known", encoding="utf-8")
    launched: list[str] = []
    audits: list[dict] = []
    monkeypatch.setattr(
        host_agent.database,
        "get_source_asset_for_file_action",
        lambda asset_id: {
            "id": asset_id,
            "root_path": str(tmp_path),
            "path": "known.txt",
            "deleted_at": None,
            "status": "indexed",
        },
        raising=False,
    )
    monkeypatch.setattr(host_agent, "_launch_default_app", lambda path: launched.append(str(path)), raising=False)
    monkeypatch.setattr(host_agent.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    result = host_agent.perform_file_action(asset_id="asset-1", action="open")

    assert result["state"] == "opened"
    assert launched == [str(target.resolve())]
    assert audits[0]["details"]["state"] == "opened"


def test_host_file_action_reports_locked_when_os_denies_open(monkeypatch, tmp_path):
    target = tmp_path / "locked.txt"
    target.write_text("locked", encoding="utf-8")
    monkeypatch.setattr(
        host_agent.database,
        "get_source_asset_for_file_action",
        lambda asset_id: {
            "id": asset_id,
            "root_path": str(tmp_path),
            "path": "locked.txt",
            "deleted_at": None,
            "status": "indexed",
        },
        raising=False,
    )
    monkeypatch.setattr(host_agent, "_launch_default_app", lambda path: (_ for _ in ()).throw(PermissionError("locked")), raising=False)
    monkeypatch.setattr(host_agent.database, "record_audit_event", lambda **_kwargs: None)

    result = host_agent.perform_file_action(asset_id="asset-1", action="open")

    assert result["state"] == "locked"


def test_remote_file_action_reports_host_agent_offline(monkeypatch):
    def fake_request_json(*_args, **_kwargs):
        raise host_agent.HostAgentClientError("connection refused")

    monkeypatch.setattr(host_agent, "_request_json", fake_request_json)

    result = host_agent.remote_file_action(asset_id="asset-1", action="open", agent_url="http://127.0.0.1:8799")

    assert result["state"] == "host_agent_offline"


def test_host_agent_watcher_loop_records_heartbeat_and_queues_changed_file(monkeypatch, tmp_path):
    heartbeats: list[str] = []
    watch_events: list[str] = []
    queued: list[dict] = []
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
    monkeypatch.setattr(host_agent.database, "record_watcher_heartbeat", lambda *, root_name, metadata=None: heartbeats.append(root_name))
    monkeypatch.setattr(host_agent.database, "record_watch_event", lambda **kwargs: watch_events.append(kwargs))
    monkeypatch.setattr(
        host_agent.database,
        "enqueue_corpus_sync_job",
        lambda **kwargs: queued.append(kwargs) or {"id": "job-1", "created": True},
        raising=False,
    )
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
    assert watch_events[0]["root_name"] == "watch-test"
    assert watch_events[0]["action"] == "changed"
    assert watch_events[0]["metadata"] == {"action": "changed"}
    assert len(watch_events[0]["path_hash"]) == 64
    assert queued == [{"root_name": "watch-test", "path": str(tmp_path / "changed.md"), "reason": "watch_event"}]
    assert synced == []


def test_host_agent_watcher_loop_survives_db_failure_while_reporting_error(monkeypatch):
    attempts: list[bool] = []

    class FakeHeartbeat:
        def __init__(self, **_kwargs):
            pass

        def start(self):
            pass

        def stop(self):
            pass

        def update(self, **_kwargs):
            pass

        def beat_once(self):
            pass

    class FakeWatcher:
        def poll_once(self, *, seed=False):
            pass

        def drain_events(self):
            return []

    monkeypatch.setattr(host_agent, "WatcherHeartbeatRunner", FakeHeartbeat)
    monkeypatch.setattr(host_agent, "_configured_reconcile_on_start", lambda: False)
    monkeypatch.setattr(host_agent, "_configured_reconcile_interval_seconds", lambda: 0)

    loop = host_agent.HostAgentWatcherLoop(
        interval_seconds=0.01,
        watcher_factory=lambda *_args, **_kwargs: FakeWatcher(),
    )

    def fake_run_once(*, seed=False):
        attempts.append(seed)
        if seed:
            return {"status": "seeded"}
        if attempts.count(False) == 1:
            raise RuntimeError("database is shutting down")
        loop._stop.set()
        return {"status": "running"}

    monkeypatch.setattr(loop, "run_once", fake_run_once)
    monkeypatch.setattr(
        host_agent,
        "_load_host_watch_roots",
        lambda _root_name=None: (_ for _ in ()).throw(RuntimeError("database still down")),
    )

    loop.start()
    assert loop._thread is not None
    loop._thread.join(timeout=1)

    assert attempts.count(False) >= 2
    assert not loop._thread.is_alive()


def test_host_agent_watcher_loop_survives_startup_seed_db_failure(monkeypatch):
    attempts: list[bool] = []

    class FakeHeartbeat:
        def __init__(self, **_kwargs):
            pass

        def start(self):
            pass

        def stop(self):
            pass

        def update(self, **_kwargs):
            pass

        def beat_once(self):
            pass

    class FakeWatcher:
        def poll_once(self, *, seed=False):
            pass

        def drain_events(self):
            return []

    monkeypatch.setattr(host_agent, "WatcherHeartbeatRunner", FakeHeartbeat)
    monkeypatch.setattr(host_agent, "_configured_reconcile_on_start", lambda: False)
    monkeypatch.setattr(host_agent, "_configured_reconcile_interval_seconds", lambda: 0)
    monkeypatch.setattr(host_agent, "_record_watcher_loop_error", lambda *_args, **_kwargs: None)

    loop = host_agent.HostAgentWatcherLoop(
        interval_seconds=0.01,
        watcher_factory=lambda *_args, **_kwargs: FakeWatcher(),
    )

    def fake_run_once(*, seed=False):
        attempts.append(seed)
        if seed:
            raise RuntimeError("database is shutting down")
        loop._stop.set()
        return {"status": "running"}

    monkeypatch.setattr(loop, "run_once", fake_run_once)

    loop.start()
    assert loop._thread is not None
    loop._thread.join(timeout=1)

    assert attempts == [True, False]
    assert not loop._thread.is_alive()


def test_host_agent_worker_loop_survives_db_failure_while_reporting_error(monkeypatch):
    attempts = 0
    loop = host_agent.HostAgentWorkerLoop(interval_seconds=0.01)

    def fake_run_once():
        nonlocal attempts
        attempts += 1
        if attempts == 1:
            raise RuntimeError("database is shutting down")
        loop._stop.set()
        return {"status": "running"}

    monkeypatch.setattr(loop, "run_once", fake_run_once)
    monkeypatch.setattr(
        host_agent.database,
        "record_runtime_component_heartbeat",
        lambda **_kwargs: (_ for _ in ()).throw(RuntimeError("database still down")),
    )

    loop.start()
    assert loop._thread is not None
    loop._thread.join(timeout=1)

    assert attempts >= 2
    assert not loop._thread.is_alive()


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
    assert result["vss"]["enabled"] is False
    assert result["vss"]["status"] in {"disabled", "unavailable"}
    assert "message" in result["vss"]


def test_run_server_exits_cleanly_when_host_agent_already_owns_port(monkeypatch):
    def fail_run(*_args, **_kwargs):
        raise AssertionError("uvicorn.run should not be called for an already-running host agent")

    monkeypatch.setattr(host_agent, "remote_status", lambda agent_url=None: {"status": "running", "process_id": 1234})
    monkeypatch.setitem(__import__("sys").modules, "uvicorn", SimpleNamespace(run=fail_run))

    result = host_agent.run_server(host="127.0.0.1", port=8799)

    assert result["status"] == "already_running"
    assert result["process_id"] == 1234


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
