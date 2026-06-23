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


def test_codex_hook_capture_exists_checks_session_and_turn_metadata(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return (1,)

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

    assert database.codex_hook_capture_exists(session_id="session-1", turn_id="turn-1") is True

    sql, params = executed[0]
    assert "metadata->>'source' = 'codex_hook_stop'" in sql
    assert "metadata->>'session_id' = %s" in sql
    assert "metadata->>'turn_id' = %s" in sql
    assert params == ("session-1", "turn-1")


def test_codex_hook_reference_exists_checks_indexed_reference_audit(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return (1,)

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

    assert database.codex_hook_reference_exists(
        session_id="session-1",
        turn_id="turn-1",
        reference="https://developers.openai.com/codex/mcp",
    ) is True

    sql, params = executed[0]
    assert "event_type = 'codex_hook.reference_indexed'" in sql
    assert "details->>'session_id' = %s" in sql
    assert "details->>'turn_id' = %s" in sql
    assert "details->>'reference' = %s" in sql
    assert params == ("session-1", "turn-1", "https://developers.openai.com/codex/mcp")


def test_recent_codex_hook_audit_events_filters_hook_events(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return [
                (
                    "audit-1",
                    "codex_hook.preflight_injected",
                    "system",
                    "episodes",
                    "episode-1",
                    {"reason": "matched"},
                    type("Created", (), {"isoformat": lambda self: "2026-06-23T10:00:00+00:00"})(),
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

    events = database.recent_codex_hook_audit_events(limit=3)

    sql, params = executed[0]
    assert "event_type LIKE 'codex_hook.%'" in sql
    assert params == (3,)
    assert events[0]["event_type"] == "codex_hook.preflight_injected"
    assert events[0]["details"] == {"reason": "matched"}


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


def test_complete_corpus_job_clears_previous_error(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

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

    database.complete_corpus_job(job_id="job-1")

    sql = "\n".join(item[0] for item in executed)
    assert "last_error = NULL" in sql


def test_clear_completed_corpus_job_errors_clears_legacy_errors(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return (5,)

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

    result = database.clear_completed_corpus_job_errors(root_name="watch-test")

    sql = "\n".join(item[0] for item in executed)
    assert result == {"root_name": "watch-test", "cleared": 5}
    assert "status = 'completed'" in sql
    assert "last_error IS NOT NULL" in sql
    assert "payload->>'root_name' = %s" in sql


def test_persist_crawl_plan_does_not_reset_unchanged_deferred_asset_status():
    source = Path(database.__file__).read_text(encoding="utf-8")

    assert "source_assets.quick_hash IS DISTINCT FROM EXCLUDED.quick_hash" in source
    assert "source_assets.extraction_status IN ('indexed', 'metadata_only', 'blocked_missing_dependency')" in source


def test_persist_crawl_plan_requeues_legacy_metadata_only_documents():
    source = Path(database.__file__).read_text(encoding="utf-8")

    assert "source_assets.extraction_status = 'metadata_only'" in source
    assert "source_assets.extension IN ('.doc', '.rtf')" in source
    assert "EXCLUDED.extraction_status = 'queued'" in source


def test_corpus_status_queries_include_lock_tolerant_states():
    source = Path(database.__file__).read_text(encoding="utf-8")

    assert "asset.extraction_status" in source
    assert "pending_stable" in source
    assert "retrying_locked" in source
    assert "blocked_locked" in source
    assert "status IN ('pending', 'retrying_locked')" in source


def test_imap_mail_schedule_has_due_query_and_advances_after_sync_run():
    source = Path(database.__file__).read_text(encoding="utf-8")

    assert "def list_due_imap_mail_profiles" in source
    due_function = source.split("def list_due_imap_mail_profiles", 1)[1].split("def create_outlook_sync_request", 1)[0]
    assert "source_type = 'imap'" in due_function
    assert "next_sync_at <= now()" in due_function

    record_function = source.split("def record_mail_sync_run", 1)[1].split("def record_mail_message", 1)[0]
    assert "UPDATE mail_profiles" in record_function
    assert "last_sync_at = now()" in record_function
    assert "make_interval(secs => sync_interval_seconds)" in record_function


def test_claim_lifecycle_primitives_record_events_audit_and_relations():
    source = Path(database.__file__).read_text(encoding="utf-8")

    assert "def upsert_entity" in source
    assert "def upsert_claim" in source
    assert "def transition_claim" in source
    transition = source.split("def transition_claim", 1)[1].split("def ", 1)[0]
    assert "claim_lifecycle_events" in transition
    assert "claim_relations" in transition
    assert "audit_events" in transition
    assert "claim.reinforced" in transition
    assert "claim.superseded" in transition
    assert "claim.contradicted" in transition
    assert "claim.retired" in transition


def test_graph_traversal_query_is_bounded_typed_stable_and_cycle_safe():
    source = Path(database.__file__).read_text(encoding="utf-8")

    assert "def traverse_entity_graph" in source
    traversal = source.split("def traverse_entity_graph", 1)[1].split("def ", 1)[0]
    assert "WITH RECURSIVE relation_edges" in traversal
    assert "graph AS" in traversal
    assert "depth < %s" in traversal
    assert "relation_type = ANY" in traversal
    assert "NOT next_entity_id = ANY(path)" in traversal
    assert "ORDER BY depth ASC, relation_type ASC" in traversal
