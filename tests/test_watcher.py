from flux_llm_kb.watcher import WatchRoot, PollingCorpusWatcher


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
