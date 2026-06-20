from flux_llm_kb.watcher import WatchRoot, PollingCorpusWatcher, ReloadableCorpusWatcher, summarize_watcher_staleness


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
