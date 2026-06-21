from pathlib import Path

from flux_llm_kb import database
from flux_llm_kb.database import forget_episode


def test_forget_episode_rejects_invalid_uuid_without_database():
    assert forget_episode("not-a-uuid") is False


def test_claim_corpus_jobs_uses_skip_locked(monkeypatch):
    executed_sql = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed_sql.append(sql)

        def fetchall(self):
            return [
                (
                    "job-1",
                    "corpus_extract_video",
                    "running",
                    {"path": "clip.mp4"},
                    1,
                    None,
                )
            ]

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    jobs = database.claim_corpus_jobs(limit=1, worker_id="worker-a")

    assert jobs[0]["id"] == "job-1"
    assert any("FOR UPDATE SKIP LOCKED" in sql for sql in executed_sql)


def test_delete_monitored_root_purges_index_rows_and_jobs(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 1

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return ("root-1", "docs")

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.delete_monitored_root(root_id="root-1", purge_index=True, actor="tester")

    sql = "\n".join(item[0] for item in executed)
    assert result == {"id": "root-1", "name": "docs", "deleted": True, "purged_index": True}
    assert "UPDATE monitored_roots" in sql
    assert "DELETE FROM embeddings" in sql
    assert "DELETE FROM asset_chunks" in sql
    assert "DELETE FROM source_assets" in sql
    assert "DELETE FROM capture_jobs" in sql
    assert "DELETE FROM crawl_runs" in sql
    assert "DELETE FROM watcher_state" in sql
    assert "DELETE FROM monitored_roots" in sql
    assert "monitored_root.deleted" in sql


def test_cancel_duplicate_corpus_jobs_marks_pending_duplicate_jobs_terminal(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 2

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return (2,)

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.cancel_duplicate_corpus_jobs(root_name="docs")

    sql = "\n".join(item[0] for item in executed)
    assert result == {"root_name": "docs", "cancelled": 2}
    assert "cancelled_duplicate" in sql
    assert "duplicate_suppressed" in sql
    assert "payload->>'root_name' = %s" in sql


def test_repair_extracted_corpus_asset_statuses_marks_chunked_queued_assets_indexed(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return (7,)

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.repair_extracted_corpus_asset_statuses(root_name="watch-test")

    sql = "\n".join(item[0] for item in executed)
    assert result == {"root_name": "watch-test", "repaired": 7}
    assert "EXISTS" in sql
    assert "asset_chunks" in sql
    assert "extraction_status = 'queued'" in sql
    assert "r.name = %s" in sql


def test_persist_crawl_plan_does_not_reset_unchanged_deferred_asset_status():
    source = Path(database.__file__).read_text(encoding="utf-8")

    assert "source_assets.quick_hash IS DISTINCT FROM EXCLUDED.quick_hash" in source
    assert "source_assets.extraction_status IN ('indexed', 'metadata_only', 'blocked_missing_dependency')" in source
