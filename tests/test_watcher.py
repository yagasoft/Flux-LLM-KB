from pathlib import Path

from flux_llm_kb import database
from flux_llm_kb import watcher as watcher_module
from flux_llm_kb.settings import SettingsService
from flux_llm_kb.watcher import (
    WatchRoot,
    PollingCorpusWatcher,
    ReloadableCorpusWatcher,
    WatchdogCorpusWatcher,
    create_corpus_watcher,
    probe_watcher_backend,
    resolve_watcher_backend,
    summarize_watcher_staleness,
)


def test_polling_watcher_emits_events_for_enabled_roots(tmp_path):
    root = tmp_path / "watched"
    root.mkdir()
    events = []
    watcher = PollingCorpusWatcher(
        [WatchRoot(name="docs", root_path=root, watch_enabled=True)],
        on_change=events.append,
    )

    watcher.poll_once(seed=True)
    (root / "decision.md").write_text("watch this", encoding="utf-8")
    watcher.poll_once()

    assert len(events) == 1
    assert events[0].root_name == "docs"
    assert events[0].relative_path == "decision.md"
    assert events[0].action == "changed"


def test_polling_watcher_skips_excluded_subtrees(tmp_path):
    root = tmp_path / "watched"
    root.mkdir()
    (root / ".worktrees" / "branch").mkdir(parents=True)
    (root / "src").mkdir()
    events = []
    watcher = PollingCorpusWatcher(
        [WatchRoot(name="docs", root_path=root, watch_enabled=True, exclude_globs=(".worktrees/**",))],
        on_change=events.append,
    )

    watcher.poll_once(seed=True)
    (root / ".worktrees" / "branch" / "ignored.py").write_text("ignore", encoding="utf-8")
    (root / "src" / "indexed.py").write_text("index", encoding="utf-8")
    watcher.poll_once()

    assert [event.relative_path for event in events] == ["src/indexed.py"]


def test_polling_watcher_default_excludes_skip_nested_generated_cache_noise(monkeypatch, tmp_path):
    root = tmp_path / "watched"
    root.mkdir()
    (root / "G2B" / "Code" / "foo" / ".vs" / "CopilotIndices").mkdir(parents=True)
    (root / "G2B" / "Business").mkdir(parents=True)
    events = []
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    exclude_globs = tuple(SettingsService().resolve("crawler.global_exclude_globs").raw_value)
    watcher = PollingCorpusWatcher(
        [WatchRoot(name="docs", root_path=root, watch_enabled=True, exclude_globs=exclude_globs)],
        on_change=events.append,
    )

    watcher.poll_once(seed=True)
    (root / "G2B" / "Code" / "foo" / ".vs" / "CopilotIndices" / "x.db-wal").write_bytes(b"noise")
    (root / "G2B" / "Business" / "brief.md").write_text("index this", encoding="utf-8")
    (root / "desktop.ini").write_text("noise", encoding="utf-8")
    watcher.poll_once()

    assert [event.relative_path for event in events] == ["G2B/Business/brief.md"]


def test_polling_watcher_prunes_gitignore_style_nested_directory_excludes(monkeypatch, tmp_path):
    root = tmp_path / "watched"
    root.mkdir()
    (root / "G2B" / "Code" / "foo" / ".vs" / "CopilotIndices").mkdir(parents=True)
    (root / "G2B" / "Code" / "foo" / ".vs" / "CopilotIndices" / "x.db-wal").write_bytes(b"noise")
    (root / "docs").mkdir()
    (root / "docs" / "brief.md").write_text("index this", encoding="utf-8")
    events = []
    original_iterdir = Path.iterdir

    def fail_if_vs_descended(self):
        if self.name == ".vs":
            raise AssertionError("watcher should prune nested .vs directories before descent")
        return original_iterdir(self)

    monkeypatch.setattr(Path, "iterdir", fail_if_vs_descended)
    watcher = PollingCorpusWatcher(
        [WatchRoot(name="docs", root_path=root, watch_enabled=True, exclude_globs=(".vs/",))],
        on_change=events.append,
    )

    watcher.poll_once(seed=True)

    assert events == []


def test_polling_watcher_prunes_excluded_directories_before_snapshot_descent(monkeypatch, tmp_path):
    root = tmp_path / "watched"
    root.mkdir()
    (root / "src").mkdir()
    (root / "src" / "decision.md").write_text("watch this", encoding="utf-8")
    (root / ".git" / "objects").mkdir(parents=True)
    (root / ".git" / "objects" / "noise").write_text("ignored", encoding="utf-8")
    events = []
    watcher = PollingCorpusWatcher(
        [WatchRoot(name="docs", root_path=root, watch_enabled=True, exclude_globs=(".git/**",))],
        on_change=events.append,
    )

    def fail_rglob(self, *_args, **_kwargs):
        raise AssertionError(f"watch snapshot should prune directories without Path.rglob: {self}")

    monkeypatch.setattr(Path, "rglob", fail_rglob)

    watcher.poll_once(seed=True)

    assert events == []


def test_polling_watcher_suppresses_disabled_roots(tmp_path):
    root = tmp_path / "disabled"
    root.mkdir()
    events = []
    watcher = PollingCorpusWatcher(
        [WatchRoot(name="docs", root_path=root, watch_enabled=False)],
        on_change=events.append,
    )

    (root / "decision.md").write_text("ignore while disabled", encoding="utf-8")
    watcher.poll_once()

    assert events == []


def test_reloadable_watcher_honors_live_enable_disable(tmp_path):
    root = tmp_path / "live"
    root.mkdir()
    roots = [WatchRoot(name="docs", root_path=root, watch_enabled=True)]
    events = []
    watcher = ReloadableCorpusWatcher(lambda: roots, on_change=events.append, debounce_seconds=0)

    watcher.poll_once(seed=True)
    (root / "one.md").write_text("first", encoding="utf-8")
    watcher.poll_once()
    roots[:] = [WatchRoot(name="docs", root_path=root, watch_enabled=False)]
    (root / "two.md").write_text("disabled", encoding="utf-8")
    watcher.poll_once()
    roots[:] = [WatchRoot(name="docs", root_path=root, watch_enabled=True)]
    watcher.poll_once(seed=True)
    (root / "three.md").write_text("enabled again", encoding="utf-8")
    watcher.poll_once()

    assert [event.relative_path for event in events] == ["one.md", "three.md"]


def test_reloadable_watcher_skips_excluded_subtrees(tmp_path):
    root = tmp_path / "live"
    root.mkdir()
    (root / ".worktrees" / "branch").mkdir(parents=True)
    (root / "src").mkdir()
    events = []
    watcher = ReloadableCorpusWatcher(
        lambda: [WatchRoot(name="docs", root_path=root, watch_enabled=True, exclude_globs=(".worktrees/**",))],
        on_change=events.append,
        debounce_seconds=0,
    )

    watcher.poll_once(seed=True)
    (root / ".worktrees" / "branch" / "ignored.py").write_text("ignore", encoding="utf-8")
    (root / "src" / "indexed.py").write_text("index", encoding="utf-8")
    watcher.poll_once()

    assert [event.relative_path for event in events] == ["src/indexed.py"]
    assert [event.relative_path for event in watcher.drain_events()] == ["src/indexed.py"]


def test_reloadable_watcher_skips_inaccessible_fingerprints_and_keeps_other_roots(monkeypatch, tmp_path):
    root = tmp_path / "primary"
    other_root = tmp_path / "secondary"
    root.mkdir()
    other_root.mkdir()
    inaccessible = root / "aaa-inaccessible.bin"
    valid = root / "valid.md"
    other_valid = other_root / "other.md"
    inaccessible.write_bytes(b"locked")
    valid.write_text("old", encoding="utf-8")
    other_valid.write_text("old", encoding="utf-8")
    original_fingerprint = watcher_module._fingerprint

    def flaky_fingerprint(path):
        if path == inaccessible:
            raise OSError("entry is temporarily inaccessible")
        return original_fingerprint(path)

    monkeypatch.setattr(watcher_module, "_fingerprint", flaky_fingerprint)
    watcher = ReloadableCorpusWatcher(
        lambda: [
            WatchRoot(name="primary", root_path=root, watch_enabled=True),
            WatchRoot(name="secondary", root_path=other_root, watch_enabled=True),
        ],
        debounce_seconds=0,
    )

    watcher.poll_once(seed=True)
    valid.write_text("new content", encoding="utf-8")
    other_valid.write_text("new content", encoding="utf-8")
    events = watcher.poll_once()

    assert [(event.root_name, event.relative_path) for event in events] == [
        ("primary", "valid.md"),
        ("secondary", "other.md"),
    ]


def test_reloadable_watcher_skips_inaccessible_child_type_checks(monkeypatch, tmp_path):
    root = tmp_path / "watched"
    root.mkdir()
    inaccessible = root / "aaa-inaccessible.tmp"
    valid = root / "valid.md"
    inaccessible.write_text("locked", encoding="utf-8")
    valid.write_text("old", encoding="utf-8")
    original_is_dir = Path.is_dir

    def flaky_is_dir(self):
        if self == inaccessible:
            raise OSError("entry type is temporarily inaccessible")
        return original_is_dir(self)

    monkeypatch.setattr(Path, "is_dir", flaky_is_dir)
    watcher = ReloadableCorpusWatcher(
        lambda: [WatchRoot(name="docs", root_path=root, watch_enabled=True)],
        debounce_seconds=0,
    )

    watcher.poll_once(seed=True)
    valid.write_text("new content", encoding="utf-8")
    events = watcher.poll_once()

    assert [event.relative_path for event in events] == ["valid.md"]


def test_reloadable_watcher_debounces_and_bounds_events(tmp_path):
    root = tmp_path / "bounded"
    root.mkdir()
    watcher = ReloadableCorpusWatcher(
        lambda: [WatchRoot(name="docs", root_path=root, watch_enabled=True)],
        debounce_seconds=60,
        max_queue_size=1,
    )

    watcher.poll_once(seed=True)
    (root / "one.md").write_text("one", encoding="utf-8")
    watcher.poll_once()
    (root / "one.md").write_text("one again", encoding="utf-8")
    watcher.poll_once()
    (root / "two.md").write_text("two", encoding="utf-8")
    watcher.poll_once()

    assert [event.relative_path for event in watcher.drain_events()] == ["one.md"]


def test_reloadable_watcher_waits_for_stable_fingerprint_before_change(tmp_path):
    root = tmp_path / "stable"
    root.mkdir()
    clock = {"now": 100.0}
    watcher = ReloadableCorpusWatcher(
        lambda: [WatchRoot(name="docs", root_path=root, watch_enabled=True)],
        debounce_seconds=0,
        stability_quiet_seconds=2.0,
        clock=lambda: clock["now"],
    )

    watcher.poll_once(seed=True)
    (root / "draft.md").write_text("first", encoding="utf-8")
    assert watcher.poll_once() == []

    clock["now"] += 1.0
    (root / "draft.md").write_text("second", encoding="utf-8")
    assert watcher.poll_once() == []

    clock["now"] += 2.1
    events = watcher.poll_once()

    assert [event.relative_path for event in events] == ["draft.md"]
    assert events[0].action == "changed"
    assert [event.relative_path for event in watcher.drain_events()] == ["draft.md"]


def test_reloadable_watcher_emits_deletes_without_stability_wait(tmp_path):
    root = tmp_path / "delete"
    root.mkdir()
    target = root / "old.md"
    target.write_text("remove me", encoding="utf-8")
    watcher = ReloadableCorpusWatcher(
        lambda: [WatchRoot(name="docs", root_path=root, watch_enabled=True)],
        debounce_seconds=0,
        stability_quiet_seconds=60.0,
    )

    watcher.poll_once(seed=True)
    target.unlink()
    events = watcher.poll_once()

    assert [event.action for event in events] == ["deleted"]
    assert [event.relative_path for event in events] == ["old.md"]


def test_summarize_watcher_staleness_marks_old_heartbeats():
    payload = summarize_watcher_staleness(
        [
            {
                "root_name": "fresh",
                "status": "running",
                "heartbeat_age_seconds": 10,
            },
            {
                "root_name": "old",
                "status": "running",
                "heartbeat_age_seconds": 999,
            },
        ],
        stale_after_seconds=60,
    )

    assert payload["stale_count"] == 1
    assert payload["states"][1]["status"] == "stale"


def test_resolve_watcher_backend_prefers_watchdog_in_auto_when_available():
    status = resolve_watcher_backend("auto", module_finder=lambda name: object() if name == "watchdog" else None)

    assert status == {
        "policy": "auto",
        "selected_backend": "watchdog",
        "native": True,
        "fallback_reason": None,
        "message": "watchdog available",
    }


def test_resolve_watcher_backend_falls_back_to_polling_in_auto_when_watchdog_missing():
    status = resolve_watcher_backend("auto", module_finder=lambda _name: None)

    assert status["selected_backend"] == "polling"
    assert status["native"] is False
    assert status["fallback_reason"] == "watchdog_missing"


def test_resolve_watcher_backend_rejects_explicit_watchdog_when_missing():
    try:
        resolve_watcher_backend("watchdog", module_finder=lambda _name: None)
    except RuntimeError as exc:
        assert "watchdog is not installed" in str(exc)
    else:  # pragma: no cover - proves the test failed
        raise AssertionError("explicit watchdog backend should fail when watchdog is unavailable")


def test_watchdog_poll_skips_snapshot_when_no_stability_candidates(monkeypatch, tmp_path):
    root = tmp_path / "watched"
    root.mkdir()
    monkeypatch.setattr(watcher_module.importlib.util, "find_spec", lambda _name: object())
    watcher = WatchdogCorpusWatcher(
        lambda: [WatchRoot(name="docs", root_path=root, watch_enabled=True)],
        on_change=None,
        stability_quiet_seconds=2.0,
    )
    monkeypatch.setattr(watcher, "_ensure_observer", lambda: None)
    monkeypatch.setattr(watcher, "_sync_watches", lambda: None)

    def fail_snapshot(_root):
        raise AssertionError("watchdog polling should not snapshot roots without pending stable files")

    monkeypatch.setattr(watcher_module, "_snapshot", fail_snapshot)

    assert watcher.poll_once(seed=False) == []


def test_create_corpus_watcher_honors_explicit_polling_backend(tmp_path):
    root = tmp_path / "policy"
    root.mkdir()

    watcher = create_corpus_watcher(
        lambda: [WatchRoot(name="docs", root_path=root, watch_enabled=True)],
        backend_policy="polling",
    )

    assert isinstance(watcher, ReloadableCorpusWatcher)
    assert not isinstance(watcher, WatchdogCorpusWatcher)
    assert watcher.backend_status["selected_backend"] == "polling"


def test_probe_watcher_backend_uses_temp_files_and_reports_normalized_events():
    payload = probe_watcher_backend(backend_policy="polling", timeout_seconds=1.0)

    assert payload["status"] == "ok"
    assert payload["backend"]["selected_backend"] == "polling"
    assert payload["expected_events"] == ["changed", "changed", "deleted"]
    assert payload["observed_event_count"] >= 3
    assert set(payload["observed_actions"]) >= {"changed", "deleted"}
    assert payload["path_scope"] == "temporary"
