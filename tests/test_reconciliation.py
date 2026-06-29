import threading

from flux_llm_kb import database, host_agent, service as service_module
from flux_llm_kb.service import KnowledgeService
from flux_llm_kb.watcher import WatchEvent, WatchRoot


def test_service_reconciles_enabled_watch_roots_with_reason(monkeypatch):
    roots = [
        {"name": "enabled", "enabled": True, "watch_enabled": True},
        {"name": "watch-off", "enabled": True, "watch_enabled": False},
        {"name": "disabled", "enabled": False, "watch_enabled": True},
    ]
    synced: list[dict] = []
    heartbeats: list[dict] = []

    monkeypatch.setattr(database, "list_monitored_roots", lambda watch_enabled=None: roots)
    monkeypatch.setattr(database, "record_runtime_component_heartbeat", lambda **kwargs: heartbeats.append(kwargs))
    monkeypatch.setattr(
        KnowledgeService,
        "sync_corpus",
        lambda self, **kwargs: synced.append(kwargs)
        or {
            "root_name": kwargs["root_name"],
            "files_seen": 1,
            "files_changed": 1,
            "files_deleted": 0,
            "jobs_queued": 0,
        },
    )

    result = KnowledgeService().reconcile_watch_roots(reason="startup_reconcile")

    assert result["status"] == "completed"
    assert result["reason"] == "startup_reconcile"
    assert result["roots"] == 1
    assert synced == [{"root_name": "enabled", "reason": "startup_reconcile"}]
    assert heartbeats[-1]["name"] == "watch-reconciler:service"
    assert heartbeats[-1]["metadata"]["reason"] == "startup_reconcile"


def test_service_reconcile_empty_root_set_is_clean_noop(monkeypatch):
    monkeypatch.setattr(database, "list_monitored_roots", lambda watch_enabled=None: [])
    heartbeats: list[dict] = []
    monkeypatch.setattr(database, "record_runtime_component_heartbeat", lambda **kwargs: heartbeats.append(kwargs))

    result = KnowledgeService().reconcile_watch_roots(reason="startup_reconcile")

    assert result == {
        "status": "no_enabled_watch_roots",
        "reason": "startup_reconcile",
        "roots": 0,
        "results": [],
    }
    assert heartbeats[-1]["status"] == "idle"


def test_run_watch_reconciles_before_seeding_watcher(monkeypatch):
    calls: list[str] = []

    monkeypatch.setattr(
        "flux_llm_kb.service._load_watch_roots",
        lambda root_name=None: [WatchRoot(name="enabled", root_path=".", watch_enabled=True)],
    )
    monkeypatch.setattr(database, "record_watcher_heartbeat", lambda **_kwargs: None)
    monkeypatch.setattr(
        KnowledgeService,
        "reconcile_watch_roots",
        lambda self, **_kwargs: calls.append("reconcile")
        or {"status": "completed", "roots": 1, "results": []},
    )

    class StopAfterSeedWatcher:
        def poll_once(self, *, seed=False):
            calls.append("seed" if seed else "poll")
            raise KeyboardInterrupt

    monkeypatch.setattr(
        "flux_llm_kb.service.create_corpus_watcher",
        lambda *args, **kwargs: StopAfterSeedWatcher(),
    )

    try:
        KnowledgeService().run_watch(interval_seconds=0.01)
    except KeyboardInterrupt:
        pass

    assert calls[:2] == ["reconcile", "seed"]


def test_run_watch_heartbeats_while_startup_reconcile_is_running(monkeypatch):
    heartbeat_count = 0
    saw_two_heartbeats = threading.Event()
    calls: list[str] = []

    monkeypatch.setattr(
        "flux_llm_kb.service._load_watch_roots",
        lambda root_name=None: [WatchRoot(name="enabled", root_path=".", watch_enabled=True)],
    )
    monkeypatch.setattr(service_module, "WATCHER_HEARTBEAT_INTERVAL_SECONDS", 0.01, raising=False)

    def record_heartbeat(**kwargs):
        nonlocal heartbeat_count
        heartbeat_count += 1
        assert kwargs["root_name"] == "enabled"
        metadata = kwargs.get("metadata") or {}
        assert metadata.get("stage") in {"startup_reconcile", "seed", "poll", "idle"}
        if heartbeat_count >= 2:
            saw_two_heartbeats.set()

    def slow_reconcile(self, **_kwargs):
        calls.append("reconcile")
        assert saw_two_heartbeats.wait(1.0)
        return {"status": "completed", "roots": 1, "results": []}

    class StopAfterSeedWatcher:
        def poll_once(self, *, seed=False):
            calls.append("seed" if seed else "poll")
            raise KeyboardInterrupt

    monkeypatch.setattr(database, "record_watcher_heartbeat", record_heartbeat)
    monkeypatch.setattr(KnowledgeService, "reconcile_watch_roots", slow_reconcile)
    monkeypatch.setattr(
        "flux_llm_kb.service.create_corpus_watcher",
        lambda *args, **kwargs: StopAfterSeedWatcher(),
    )

    try:
        KnowledgeService().run_watch(interval_seconds=0.01)
    except KeyboardInterrupt:
        pass

    assert calls[:2] == ["reconcile", "seed"]


def test_service_watch_event_enqueues_sync_job_without_inline_index(monkeypatch, tmp_path):
    watch_events: list[dict] = []
    queued: list[dict] = []
    errors: list[dict] = []
    event = WatchEvent(
        root_name="docs",
        root_path=tmp_path,
        path=tmp_path / "changed.md",
        relative_path="changed.md",
        action="changed",
    )

    monkeypatch.setattr(database, "record_watch_event", lambda **kwargs: watch_events.append(kwargs))
    monkeypatch.setattr(database, "record_watch_error", lambda **kwargs: errors.append(kwargs))
    monkeypatch.setattr(
        database,
        "enqueue_corpus_sync_job",
        lambda **kwargs: queued.append(kwargs) or {"id": "job-1", "created": True},
        raising=False,
    )
    monkeypatch.setattr(
        KnowledgeService,
        "sync_corpus",
        lambda self, **_kwargs: (_ for _ in ()).throw(AssertionError("watch events must not index inline")),
    )

    KnowledgeService()._handle_watch_event(event)

    assert watch_events[0]["root_name"] == "docs"
    assert queued == [{"root_name": "docs", "path": str(tmp_path / "changed.md"), "reason": "watch_event"}]
    assert errors == []


def test_host_agent_startup_reconciles_host_roots_before_watch_seed(monkeypatch):
    calls: list[str] = []

    monkeypatch.setattr(
        host_agent,
        "_load_host_watch_roots",
        lambda root_name=None: [WatchRoot(name="enabled", root_path=".", watch_enabled=True)],
    )
    monkeypatch.setattr(host_agent.database, "record_watcher_heartbeat", lambda **_kwargs: None)
    monkeypatch.setattr(host_agent.database, "record_watch_error", lambda **_kwargs: None)

    class FakeService:
        def reconcile_watch_roots(self, **_kwargs):
            calls.append("reconcile")
            return {"status": "completed", "roots": 1}

    class StopAfterSeedWatcher:
        def __init__(self, *_args, **_kwargs):
            pass

        def poll_once(self, *, seed=False):
            calls.append("seed" if seed else "poll")
            raise KeyboardInterrupt

        def drain_events(self):
            return []

    loop = host_agent.HostAgentWatcherLoop(
        service_factory=lambda: FakeService(),
        watcher_factory=lambda *args, **kwargs: StopAfterSeedWatcher(),
    )

    try:
        loop._run()
    except KeyboardInterrupt:
        pass

    assert calls[:2] == ["reconcile", "seed"]
