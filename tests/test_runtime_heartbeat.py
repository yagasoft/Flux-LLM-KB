from types import SimpleNamespace

from flux_llm_kb.runtime_heartbeat import WatcherHeartbeatRunner


def test_watcher_heartbeat_runner_start_survives_initial_record_failure():
    runner = WatcherHeartbeatRunner(
        load_roots=lambda: [SimpleNamespace(name="docs")],
        record=lambda _root_name, _metadata: (_ for _ in ()).throw(RuntimeError("database is restarting")),
        interval_seconds=999,
    )

    runner.start()
    try:
        assert runner._thread is not None
        assert runner._thread.is_alive()
    finally:
        runner.stop()
