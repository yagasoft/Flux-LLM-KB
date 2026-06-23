from datetime import datetime, timezone
import json
from pathlib import Path

import pytest

from flux_llm_kb import database
from flux_llm_kb.database import forget_episode


def test_forget_episode_rejects_invalid_uuid_without_database():
    assert forget_episode("not-a-uuid") is False


def test_backfill_episode_workspace_scope_updates_explicit_episode_ids(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 2

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

    result = database.backfill_episode_workspace_scope(
        episode_ids=["episode-1", "episode-2"],
        metadata_patch={"workspace_key": "path:e:/repo", "cwd": "E:/Repo"},
        dry_run=False,
    )

    sql, params = executed[0]
    assert "UPDATE episodes" in sql
    assert "metadata = metadata || %s::jsonb" in sql
    assert "id = ANY(%s::uuid[])" in sql
    assert params == (
        json.dumps({"workspace_key": "path:e:/repo", "cwd": "E:/Repo"}, sort_keys=True),
        ["episode-1", "episode-2"],
    )
    assert result == {
        "updated": 2,
        "dry_run": False,
        "episode_ids": ["episode-1", "episode-2"],
        "metadata_patch": {"workspace_key": "path:e:/repo", "cwd": "E:/Repo"},
    }


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


def test_search_corpus_chunks_includes_freshness_stream():
    source = Path(database.__file__).read_text(encoding="utf-8")
    function = source.split("def search_corpus_chunks", 1)[1].split("\ndef ", 1)[0]

    assert "corpus_lexical" in function
    assert "corpus_fuzzy" in function
    assert "corpus_vector" in function
    assert "corpus_trust" in function
    assert "corpus_freshness" in function
    assert "EXTRACT(EPOCH FROM (now() - c.updated_at))" in function


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


def test_mail_post_process_event_helpers_persist_events_and_update_messages(monkeypatch):
    executed = []
    timestamp = datetime(2026, 6, 23, 10, 0, tzinfo=timezone.utc)

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql, _params = executed[-1]
            if "SELECT id FROM mail_profiles" in sql:
                return ("profile-1",)
            if "INSERT INTO mail_post_process_events" in sql:
                return (
                    "event-1",
                    "gmail-capture",
                    "run-1",
                    "mail-1",
                    "gmail",
                    "move_to_processed",
                    "gmail_move_label",
                    "applied",
                    False,
                    [{"command": "STORE"}],
                    None,
                    {"uid": 42},
                    timestamp,
                )
            return None

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

    result = database.record_mail_post_process_event(
        profile_name="gmail-capture",
        sync_run_id="run-1",
        mail_message_id="mail-1",
        provider="gmail",
        policy="move_to_processed",
        action="gmail_move_label",
        status="applied",
        dry_run=False,
        commands=[{"command": "STORE"}],
        metadata={"uid": 42},
    )

    sql = "\n".join(statement for statement, _params in executed)
    assert result["id"] == "event-1"
    assert result["status"] == "applied"
    assert "mail_post_process_events" in sql
    assert "post_process_status" in sql
    assert "post_process_policy" in sql
    assert "post_process_metadata" in sql


def test_record_mail_post_process_event_rejects_unknown_profile(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return None

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

    with pytest.raises(ValueError, match="mail profile not found"):
        database.record_mail_post_process_event(
            profile_name="missing",
            provider="gmail",
            policy="remove_label",
            action="gmail_remove_label",
            status="planned",
        )

    sql = "\n".join(statement for statement, _params in executed)
    assert "SELECT id FROM mail_profiles" in sql
    assert "INSERT INTO mail_post_process_events" not in sql


def test_list_mail_post_process_events_filters_by_profile(monkeypatch):
    executed = []
    timestamp = datetime(2026, 6, 23, 10, 0, tzinfo=timezone.utc)

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
                    "event-1",
                    "gmail-capture",
                    "run-1",
                    "mail-1",
                    "gmail",
                    "remove_label",
                    "gmail_remove_label",
                    "planned",
                    True,
                    [{"command": "STORE"}],
                    None,
                    {"uid": 42},
                    timestamp,
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

    events = database.list_mail_post_process_events(profile_name="gmail-capture", limit=500)

    sql, params = executed[0]
    assert "p.name = %s" in sql
    assert params == ("gmail-capture", 200)
    assert events[0]["profile_name"] == "gmail-capture"
    assert events[0]["dry_run"] is True
    assert "body" not in json.dumps(events[0]).lower()


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


def test_list_claims_filters_review_state_search_and_marks_reasons(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            timestamp = datetime(2026, 6, 23, 10, 0, tzinfo=timezone.utc)
            return [
                (
                    "claim-1",
                    None,
                    "entity-1",
                    "uses",
                    "PostgreSQL",
                    0.8,
                    None,
                    timestamp,
                    timestamp,
                    "stale",
                    0,
                    0,
                    1,
                    None,
                    "deprioritize",
                    {},
                    timestamp,
                    None,
                    timestamp,
                    "entity-1",
                    "project",
                    "Flux",
                    {},
                    timestamp,
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

    claims = database.list_claims(review="needs_review", state="stale", q="postgres", limit=500)

    sql, params = executed[0]
    assert "retention_action <> 'keep'" in sql
    assert "c.lifecycle_state = %s" in sql
    assert "ILIKE %s" in sql
    assert "LIMIT %s" in sql
    assert params[-1] == 200
    assert claims[0]["subject"]["name"] == "Flux"
    assert claims[0]["review_reasons"] == ["stale", "retention:deprioritize"]


def test_claim_review_counts_returns_total_current_and_review_breakdown(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return (6, 2, 4, 1, 1, 1, 1, 2)

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

    counts = database.claim_review_counts()

    assert "FILTER" in executed[0][0]
    assert counts == {
        "total": 6,
        "current": 2,
        "needs_review": 4,
        "stale": 1,
        "contradicted": 1,
        "superseded": 1,
        "retired": 1,
        "retention_action": 2,
    }


def test_list_capture_review_jobs_returns_pending_review_metadata_only(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            timestamp = datetime(2026, 6, 23, 10, 0, tzinfo=timezone.utc)
            return [
                (
                    "job-1",
                    "codex_backfill",
                    "pending",
                    {"status": "pending_review", "path": "sessions/session.json", "content": "raw text"},
                    0,
                    None,
                    timestamp,
                    timestamp,
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

    jobs = database.list_capture_review_jobs(limit=500)

    sql, params = executed[0]
    assert "status = 'pending_review'" in sql
    assert "payload->>'status' = 'pending_review'" in sql
    assert params == (200,)
    assert jobs[0]["payload"] == {"status": "pending_review", "path": "sessions/session.json"}


def test_review_capture_job_updates_payload_sanitizes_and_audits(monkeypatch):
    executed = []
    timestamp = datetime(2026, 6, 23, 10, 0, tzinfo=timezone.utc)

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql, params = executed[-1]
            if "SELECT id::text, job_type, status, payload" in sql:
                return (
                    "job-1",
                    "codex_backfill",
                    "pending",
                    {"status": "pending_review", "path": "sessions/session.json", "content": "raw text"},
                    0,
                    None,
                    timestamp,
                    timestamp,
                )
            if "INSERT INTO audit_events" in sql:
                return ("audit-1",)
            if "UPDATE capture_jobs" in sql:
                review_payload = json.loads(params[1])
                return (
                    "job-1",
                    "codex_backfill",
                    "approved",
                    {
                        "status": "approved",
                        "path": "sessions/session.json",
                        "content": "raw text",
                        "review": review_payload,
                    },
                    0,
                    None,
                    timestamp,
                    timestamp,
                )
            raise AssertionError(sql)

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

    result = database.review_capture_job(
        job_id="job-1",
        decision="approve",
        rationale=f" {'x' * 1200} ",
        actor="dashboard",
    )

    sql = "\n".join(item[0] for item in executed)
    assert "FOR UPDATE" in sql
    assert "jsonb_set" in sql
    audit_params = next(params for statement, params in executed if "INSERT INTO audit_events" in statement)
    assert "capture.review_approved" in audit_params
    assert result["job"]["status"] == "approved"
    assert result["job"]["payload"] == {
        "status": "approved",
        "path": "sessions/session.json",
        "review": result["review"],
    }
    assert result["review"]["decision"] == "approve"
    assert result["review"]["rationale"] == "x" * 1000
    assert result["review"]["actor"] == "dashboard"
    assert result["review"]["audit_event_id"] == "audit-1"
    assert result["audit_event_id"] == "audit-1"
    assert result["audit_event"] == {"id": "audit-1", "event_type": "capture.review_approved"}
    assert result["links"][0]["audit_event_id"] == "audit-1"


def test_review_capture_job_rejects_invalid_inputs_without_database():
    with pytest.raises(ValueError, match="decision"):
        database.review_capture_job(job_id="job-1", decision="maybe", rationale="because")
    with pytest.raises(ValueError, match="rationale"):
        database.review_capture_job(job_id="job-1", decision="approve", rationale="   ")


def test_review_capture_job_conflicts_when_job_is_not_pending(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql, _params = executed[-1]
            if "SELECT id::text, job_type, status, payload" in sql:
                return ("job-1", "codex_backfill", "completed", {"status": "completed"}, 0, None, datetime.now(timezone.utc), datetime.now(timezone.utc))
            raise AssertionError(sql)

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

    with pytest.raises(RuntimeError, match="already decided"):
        database.review_capture_job(
            job_id="job-1",
            decision="reject",
            rationale="not useful",
            actor="dashboard",
        )
