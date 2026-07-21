from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from flux_llm_kb import database, settings
from flux_llm_kb.crawler import AssetChunk, CrawlPlan, DiscoveredAsset
from flux_llm_kb.database import forget_episode
from flux_llm_kb.embeddings import EmbeddingResult
from flux_llm_kb.gpu_scheduler import GpuLeaseDeferred, GpuLeaseRejected, GpuLeaseTimeout
from flux_llm_kb.gpu_reconciliation import GpuReconciliationObservation, RuntimeInventorySnapshot
from flux_llm_kb.model_runner import ModelRunnerBusy, ModelRunnerError


def test_gpu_control_lock_uses_session_advisory_lock_and_releases(monkeypatch):
    executed = []

    class Cursor:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def execute(self, sql, params=()): executed.append((sql, params))
        def fetchone(self): return (True,)

    class Connection:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def cursor(self): return Cursor()

    connection = Connection()
    connection.autocommit = False
    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_a, **_k: connection))

    with database.gpu_control_lock(timeout_seconds=0.1):
        pass

    sql = "\n".join(statement for statement, _ in executed)
    assert "pg_try_advisory_lock(hashtextextended('flux.gpu.control', 0))" in sql
    assert "pg_advisory_unlock(hashtextextended('flux.gpu.control', 0))" in sql
    assert connection.autocommit is True


def test_gpu_vram_calibration_keeps_latest_numeric_samples_and_ignores_stale_or_contended_rows(monkeypatch):
    executed = []

    class Cursor:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def execute(self, sql, params=()): executed.append((sql, params))
        def fetchall(self):
            return [
                (100 + index, 20, 1000.0 + index, False)
                for index in range(5)
            ] + [
                (900, 10, 1.0, True),
                (900, 10, 1.0, False),
            ]

    class Transaction:
        def __enter__(self): return self
        def __exit__(self, *_): return None

    class Connection:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def cursor(self): return Cursor()
        def transaction(self): return Transaction()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_a, **_k: Connection()))
    for index in range(33):
        database.record_gpu_vram_sample(
            task_type="embedding",
            model_id="Snowflake/test",
            shape_bucket="embedding|count=9-16|chars=*|item=*|dims=1024",
            load_delta_mb=100 + index,
            working_set_mb=20,
            allocator_capability="measured",
            tracker_overlapped=False,
            observed_at=1000.0 + index,
        )

    calibration = database.resolve_gpu_vram_calibration(
        task_type="embedding",
        model_id="Snowflake/test",
        shape_bucket="embedding|count=9-16|chars=*|item=*|dims=1024",
        now=1010.0,
        max_age_seconds=60,
    )

    assert calibration.sample_count == 5
    assert calibration.load_delta_mb == 104
    assert calibration.working_set_mb == 20
    assert calibration.source == "measured"
    sql = "\n".join(statement for statement, _params in executed)
    assert "LIMIT 32" in sql
    assert "DELETE FROM gpu_vram_samples" in sql
    assert "tracker_overlapped = false" in sql


def test_persist_gpu_runtime_observation_marks_failed_component_unknown_not_absent(monkeypatch):
    executed = []

    class Cursor:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def execute(self, sql, params=()): executed.append((sql, params))

    class Transaction:
        def __enter__(self): return self
        def __exit__(self, *_): return None

    class Connection:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def cursor(self): return Cursor()
        def transaction(self): return Transaction()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_a, **_k: Connection()))
    observation = GpuReconciliationObservation(
        observation_id="obs-1", state="inventory_incomplete", driver_used_mb=10, driver_free_mb=20,
        raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
        inventories=(RuntimeInventorySnapshot(component="asr", owner_component="asr", state="unknown", error_code="timeout", error_metadata={"message": "x" * 500}),),
    )

    database.persist_gpu_runtime_observation(observation)

    statements = "\n".join(statement for statement, _ in executed)
    assert any(params and params[0] == "unknown" for _statement, params in executed)
    assert "runtime_state = 'absent'" not in statements
    inserted_params = next(params for statement, params in executed if "INSERT INTO gpu_runtime_inventory" in statement)
    assert len(json.loads(inserted_params[-2])["message"]) <= 300


def test_persist_gpu_runtime_observation_does_not_choose_an_owner_for_a_conflict(monkeypatch):
    executed = []

    class Cursor:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def execute(self, sql, params=()): executed.append((sql, params))

    class Transaction:
        def __enter__(self): return self
        def __exit__(self, *_): return None

    class Connection:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def cursor(self): return Cursor()
        def transaction(self): return Transaction()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_a, **_k: Connection()))
    observation = GpuReconciliationObservation(
        observation_id="obs-conflict", state="inventory_incomplete", driver_used_mb=10, driver_free_mb=20,
        raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
        inventories=(RuntimeInventorySnapshot(component="model-runner", state="conflicted", models=({"task_type": "embedding", "model_id": "snowflake"},)),),
    )

    database.persist_gpu_runtime_observation(observation)

    statements = "\n".join(statement for statement, _ in executed)
    assert any(params and params[0] == "conflicted" for _statement, params in executed)
    assert "runtime_state = 'absent'" not in statements
    assert "ON CONFLICT (model_id, task_type)" not in statements


def test_persist_gpu_runtime_observation_supersedes_prior_generation_and_marks_omissions_absent(monkeypatch):
    executed = []

    class Cursor:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def execute(self, sql, params=()): executed.append((sql, params))

    class Transaction:
        def __enter__(self): return self
        def __exit__(self, *_): return None

    class Connection:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def cursor(self): return Cursor()
        def transaction(self): return Transaction()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_a, **_k: Connection()))
    observation = GpuReconciliationObservation(
        observation_id="obs-present", state="healthy", driver_used_mb=10, driver_free_mb=20,
        raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
        inventories=(RuntimeInventorySnapshot(component="asr", owner_component="asr", process_generation="new", state="present"),),
    )

    database.persist_gpu_runtime_observation(observation)

    absent_update = next(params for statement, params in executed if "runtime_state = 'absent'" in statement)
    assert absent_update[1] == "asr"


def test_persist_gpu_runtime_observation_uses_the_observation_timestamp_for_absent_rows(monkeypatch):
    executed = []

    class Cursor:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def execute(self, sql, params=()): executed.append((sql, params))

    class Transaction:
        def __enter__(self): return self
        def __exit__(self, *_): return None

    class Connection:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def cursor(self): return Cursor()
        def transaction(self): return Transaction()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_a, **_k: Connection()))
    observed_at = 1_700_000_000.0
    observation = GpuReconciliationObservation(
        observation_id="obs-timestamp", state="healthy", driver_used_mb=10, driver_free_mb=20,
        raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
        inventories=(RuntimeInventorySnapshot(
            component="model-runner", owner_component="model-runner", process_generation="generation-1",
            state="present", observed_at=observed_at,
            models=({"task_type": "embedding", "model_id": "snowflake", "in_flight": 0},),
        ),),
    )

    database.persist_gpu_runtime_observation(observation)

    absent_sql, absent_params = next((sql, params) for sql, params in executed if "runtime_state = 'absent'" in sql)
    assert "runtime_observed_at = to_timestamp(%s)" in absent_sql
    assert absent_params[-1] == observed_at


def test_persist_gpu_runtime_observation_records_runtime_operation_timestamps_for_idle_fencing(monkeypatch):
    executed = []

    class Cursor:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def execute(self, sql, params=()): executed.append((sql, params))

    class Transaction:
        def __enter__(self): return self
        def __exit__(self, *_): return None

    class Connection:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def cursor(self): return Cursor()
        def transaction(self): return Transaction()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_a, **_k: Connection()))
    observation = GpuReconciliationObservation(
        observation_id="obs-idle", state="healthy", driver_used_mb=10, driver_free_mb=20,
        raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
        inventories=(RuntimeInventorySnapshot(
            component="model-runner", owner_component="model-runner", process_generation="generation-1", state="present",
            models=({
                "task_type": "embedding", "model_id": "snowflake", "in_flight": 0,
                "last_started_at": 1_700_000_000.0, "last_activity_at": 1_700_000_012.0,
            },),
        ),),
    )

    database.persist_gpu_runtime_observation(observation)

    sql, params = next((statement, params) for statement, params in executed if "ON CONFLICT (model_id, task_type)" in statement)
    assert "last_operation_started_at" in sql
    assert "last_operation_completed_at" in sql
    assert 1_700_000_000.0 in params
    assert 1_700_000_012.0 in params


def test_generation_fence_prevents_delayed_observation_overwriting_newer_runtime(monkeypatch):
    executed = []

    class Cursor:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def execute(self, sql, params=()): executed.append((sql, params))

    class Transaction:
        def __enter__(self): return self
        def __exit__(self, *_): return None

    class Connection:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def cursor(self): return Cursor()
        def transaction(self): return Transaction()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_a, **_k: Connection()))
    observation = GpuReconciliationObservation(
        observation_id="old", state="healthy", driver_used_mb=10, driver_free_mb=20,
        raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
        inventories=(RuntimeInventorySnapshot(
            component="asr", owner_component="asr", process_generation="old", state="present", observed_at=100.0,
            models=({"task_type": "asr", "model_id": "whisper"},),
        ),),
    )

    database.persist_gpu_runtime_observation(observation)

    statements = "\n".join(statement for statement, _ in executed)
    assert "runtime_observed_at <= to_timestamp(%s)" in statements
    assert "WHERE gpu_model_residency.runtime_observed_at IS NULL" in statements


def test_present_model_refreshes_after_same_snapshot_marks_old_generation_absent(monkeypatch):
    """A new-generation snapshot must restore its own present model row.

    The omission pass runs before the model upsert.  It advances an old
    generation's observation time to the current snapshot time while marking it
    absent, so the present-model fence must accept that same timestamp.
    """
    executed = []

    class Cursor:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def execute(self, sql, params=()): executed.append((sql, params))

    class Transaction:
        def __enter__(self): return self
        def __exit__(self, *_): return None

    class Connection:
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def cursor(self): return Cursor()
        def transaction(self): return Transaction()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_a, **_k: Connection()))
    observation = GpuReconciliationObservation(
        observation_id="new-generation-present", state="healthy", driver_used_mb=10, driver_free_mb=20,
        raw_residual_mb=0, unresolved_known_owner_mb=0, unattributed_mb=0,
        inventories=(RuntimeInventorySnapshot(
            component="model-runner", owner_component="model-runner", process_generation="new", state="present",
            observed_at=200.0, models=({"task_type": "embedding", "model_id": "snowflake"},),
        ),),
    )

    database.persist_gpu_runtime_observation(observation)

    upsert_sql = next(statement for statement, _params in executed if "ON CONFLICT (model_id, task_type)" in statement)
    compact_sql = " ".join(upsert_sql.split())
    assert (
        "gpu_model_residency.runtime_generation <> EXCLUDED.runtime_generation "
        "AND gpu_model_residency.runtime_observed_at <= EXCLUDED.runtime_observed_at"
    ) in compact_sql


def test_forget_episode_rejects_invalid_uuid_without_database():
    assert forget_episode("not-a-uuid") is False


def test_normalize_root_path_preserves_absolute_posix_container_path():
    assert database._normalize_root_path("/app/private/mail-spool/gmail-capture/ready") == (
        "/app/private/mail-spool/gmail-capture/ready"
    )


def test_json_helpers_strip_postgres_nul_from_nested_strings():
    payload = database._json(
        {
            "clean": "Arabic نص\tkept",
            "bad\x00key": "bad\x00value",
            "nested": [{"item": "a\x00b"}],
        }
    )

    assert "\x00" not in payload
    assert json.loads(payload) == {
        "clean": "Arabic نص\tkept",
        "badkey": "badvalue",
        "nested": [{"item": "ab"}],
    }


def test_update_monitored_root_blocks_existing_metadata_only_assets_when_strict(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, name FROM monitored_roots" in sql:
                return ("root-1", "docs")
            if "UPDATE monitored_roots" in sql:
                return (
                    "root-1",
                    "docs",
                    "/app/private/docs",
                    True,
                    True,
                    False,
                    500,
                    [],
                    [],
                    "extend",
                    1024,
                    2048,
                    {"strict_indexing": True},
                )
            return None

        def fetchall(self):
            if "SELECT id::text" in executed[-1][0] and "FROM source_assets" in executed[-1][0]:
                return [("asset-1",), ("asset-2",)]
            return []

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

    result = database.update_monitored_root(
        root_id="docs",
        name="docs",
        root_path="/app/private/docs",
        metadata={"strict_indexing": True},
    )

    sql = "\n".join(statement for statement, _params in executed)
    assert result["metadata"]["strict_indexing"] is True
    assert "extraction_status = 'metadata_only'" in sql
    assert "DELETE FROM asset_chunks WHERE asset_id = ANY(%s::uuid[])" in sql
    assert "SET extraction_status = 'blocked_by_policy'" in sql
    update_params = next(
        params
        for statement, params in executed
        if "SET extraction_status = 'blocked_by_policy'" in statement
    )
    assert update_params[1] == ["asset-1", "asset-2"]
    assert json.loads(update_params[0])["metadata_only_blocked"] is True
    assert json.loads(update_params[0])["readiness_status"] == "blocked_by_policy"


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
                    "media",
                    "gpu",
                    50,
                    900,
                    "running",
                    {"path": "clip.mp4"},
                    1,
                    None,
                    {},
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
    assert any("delete_requested_at IS NULL" in sql for sql in executed_sql)


def test_claim_corpus_jobs_skips_sync_root_when_same_root_is_running(monkeypatch):
    executed_sql = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed_sql.append(sql)

        def fetchall(self):
            return []

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

    database.claim_corpus_jobs(limit=2, worker_id="worker-a")

    sql = "\n".join(executed_sql)
    assert "other.job_type = 'corpus_sync_root'" in sql
    assert "other.status = 'running'" in sql
    assert "other.payload->>'root_name' = capture_jobs.payload->>'root_name'" in sql
    assert "first_sync.job_type = 'corpus_sync_root'" in sql
    assert "first_sync.payload->>'root_name' = capture_jobs.payload->>'root_name'" in sql


def test_code_index_status_filters_selected_root_without_join_leakage(monkeypatch):
    executed: list[tuple[str, tuple]] = []
    result_sets = [
        [("app", 3, 1, 2)],
        [("app", 5)],
        [("app", 7)],
        [("app", 11)],
        [("python", 4)],
        [("parsed", 6), ("fallback", 1)],
        [("src/app.py", 1200)],
    ]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, tuple(params)))

        def fetchall(self):
            return result_sets.pop(0)

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

    report = database.code_index_status(root_name="app")

    asset_sql, asset_params = executed[0]
    joined_sql = "\n".join(sql for sql, _params in executed)
    assert "COUNT(a.id)::integer AS asset_count" in asset_sql
    assert "WHERE r.name = %s" in asset_sql
    assert asset_params == ("app",)
    assert "LEFT JOIN code_symbols cs" not in asset_sql
    assert "LEFT JOIN code_references cr" not in asset_sql
    assert all(
        not (
            "LEFT JOIN asset_chunks c" in sql
            and "LEFT JOIN code_symbols cs" in sql
            and "LEFT JOIN code_references cr" in sql
        )
        for sql, _params in executed
    )
    assert report["roots"] == [
        {
            "root_name": "app",
            "asset_count": 3,
            "chunk_count": 5,
            "symbol_count": 7,
            "reference_count": 11,
            "fallback_count": 1,
            "generated_count": 2,
            "languages": {"python": 4},
            "parser_statuses": {"parsed": 6, "fallback": 1},
            "slow_files": [{"path": "src/app.py", "duration_ms": 1200}],
        }
    ]


def test_claim_corpus_jobs_filters_by_family_and_orders_by_priority(monkeypatch):
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
                    "job-1",
                    "corpus_extract_video",
                    "media",
                    "gpu",
                    50,
                    900,
                    "running",
                    {"path": "clip.mp4"},
                    1,
                    None,
                    {},
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

    jobs = database.claim_corpus_jobs(
        limit=2,
        worker_id="worker-a",
        job_families=["media", "archive"],
        host_agent_roots=False,
    )

    sql, params = executed[0]
    assert "job_family = ANY(%s)" in sql
    assert "ORDER BY priority DESC, created_at" in sql
    assert params[1] == ["media", "archive"]
    assert jobs == [
        {
            "id": "job-1",
            "job_type": "corpus_extract_video",
            "job_family": "media",
            "resource_class": "gpu",
            "priority": 50,
            "time_budget_seconds": 900,
            "status": "running",
            "payload": {"path": "clip.mp4"},
            "attempts": 1,
            "last_error": None,
            "telemetry": {},
        }
    ]


def test_complete_corpus_job_records_duration_and_telemetry(monkeypatch):
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

    database.complete_corpus_job(job_id="job-1", duration_ms=42, telemetry={"family": "media"})

    sql, params = executed[0]
    assert "completed_at = now()" in sql
    assert "last_duration_ms = %s" in sql
    assert "WHEN job_type = 'search_index_sync'" in sql
    assert "- 'search_index_errors'" in sql
    assert "AND status = 'running'" in sql
    assert params == (42, json.dumps({"family": "media", "progress_label": "Completed"}, sort_keys=True), "job-1")


def test_complete_corpus_job_adds_terminal_progress_label(monkeypatch):
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

    database.complete_corpus_job(job_id="job-1", duration_ms=42, telemetry={"result_status": "indexed"})

    _, params = executed[0]
    assert params == (
        42,
        json.dumps({"progress_label": "Indexed", "result_status": "indexed"}, sort_keys=True),
        "job-1",
    )


def test_complete_search_index_sync_job_clears_stale_failure_telemetry_on_success(monkeypatch):
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

    database.complete_corpus_job(
        job_id="job-search",
        duration_ms=25,
        telemetry={"result_status": "indexed", "search_index_failed": 0},
    )

    sql, params = executed[0]
    assert "WHEN job_type = 'search_index_sync'" in sql
    assert "- 'search_index_errors'" in sql
    assert "- 'error_type'" in sql
    assert "- 'error'" in sql
    assert "- 'last_error'" in sql
    assert "- 'failed_stage'" in sql
    assert "- 'failure_stage'" in sql
    assert "- 'failed_error'" in sql
    assert "- 'failure_error'" in sql
    assert "- 'failed_reason'" in sql
    assert "- 'failure_reason'" in sql
    assert "ELSE telemetry" in sql
    assert "END || %s::jsonb" in sql
    assert params == (
        25,
        json.dumps({"progress_label": "Indexed", "result_status": "indexed", "search_index_failed": 0}, sort_keys=True),
        "job-search",
    )


def _search_index_row() -> dict[str, object]:
    return {
        "vespa_document_id": "id:flux:flux_evidence::asset_chunks--chunk-1",
        "owner_table": "asset_chunks",
        "owner_id": "11111111-1111-1111-1111-111111111111",
        "root_id": "22222222-2222-2222-2222-222222222222",
        "root_name": "docs",
        "source_hash": "hash-1",
        "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
        "embedding_dimensions": 1024,
        "model_generation": "snowflake-qwen-paddleocr-v1",
    }


def test_upsert_search_index_record_clears_failed_stage_after_success():
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

    database._upsert_search_index_record(
        FakeCursor(),
        row=_search_index_row(),
        status="indexed",
        last_error=None,
        metadata={"rank_profile": "hybrid"},
    )

    sql, params = executed[0]
    normalized_sql = " ".join(sql.split())
    assert "EXCLUDED.index_status IN ('indexed', 'deleted', 'skipped')" in sql
    assert "search_index_records.metadata - 'failed_stage'" in normalized_sql
    assert "- 'failed_stage_last_error'" in sql
    assert "- 'failed_stage_error'" in sql
    assert "- 'failed_last_error'" in sql
    assert "- 'failed_error'" in sql
    assert "- 'failure_error'" in sql
    assert "- 'error_type'" in sql
    assert "- 'last_error'" in sql
    assert "ELSE search_index_records.metadata" in sql
    assert json.loads(params[-1]) == {"rank_profile": "hybrid"}


def test_upsert_search_index_record_keeps_failed_stage_for_failed_status():
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

    database._upsert_search_index_record(
        FakeCursor(),
        row=_search_index_row(),
        status="failed",
        last_error="embedding failed",
        metadata={"failed_stage": "embedding"},
    )

    sql, params = executed[0]
    assert "EXCLUDED.index_status IN ('indexed', 'deleted', 'skipped')" in sql
    assert params[9] == "failed"
    assert params[10] == "embedding failed"
    assert json.loads(params[-1]) == {"failed_stage": "embedding"}


def test_mark_search_index_record_deleted_clears_failed_stage_metadata():
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

    database._mark_search_index_record_deleted(
        FakeCursor(),
        record={"vespa_document_id": "id:flux:flux_evidence::asset_chunks--chunk-1"},
    )

    sql, params = executed[0]
    normalized_sql = " ".join(sql.split())
    assert "metadata - 'failed_stage'" in normalized_sql
    assert "- 'failed_stage_last_error'" in sql
    assert "- 'failed_stage_error'" in sql
    assert "- 'failed_last_error'" in sql
    assert "- 'failed_error'" in sql
    assert "- 'failure_error'" in sql
    assert "- 'error_type'" in sql
    assert "- 'last_error'" in sql
    assert "jsonb_build_object('deleted_from_vespa', true)" in sql
    assert params == ("id:flux:flux_evidence::asset_chunks--chunk-1",)


def test_purge_derived_cache_entries_removes_root_owned_json_and_thumbnail_caches(monkeypatch, tmp_path):
    cache_root = tmp_path / "cache"
    cache_dirs = {
        name: cache_root / name
        for name in ("models", "ocr", "asr", "vision", "thumbnails", "parser", "embeddings", "mail_content", "temp")
    }
    for path in cache_dirs.values():
        path.mkdir(parents=True)

    root_hash = "a" * 64
    shared_hash = "b" * 64
    stale_hash = "c" * 64
    root_ocr = cache_dirs["ocr"] / "root.json"
    shared_ocr = cache_dirs["ocr"] / "shared.json"
    stale_ocr = cache_dirs["ocr"] / "stale.json"
    root_ocr.write_text(json.dumps({"schema": "flux-paddleocr-cache-v2", "source_hash": root_hash, "text": "root"}), encoding="utf-8")
    shared_ocr.write_text(
        json.dumps({"schema": "flux-paddleocr-cache-v2", "source_hash": shared_hash, "text": "shared"}),
        encoding="utf-8",
    )
    stale_ocr.write_text(json.dumps({"schema": "flux-paddleocr-cache-v2", "source_hash": stale_hash, "text": "stale"}), encoding="utf-8")
    root_asr = cache_dirs["asr"] / "root.json"
    root_asr.write_text(
        json.dumps({"schema": "flux-asr-cache-v1", "source_hash": root_hash, "model_key": "m", "text": "root"}),
        encoding="utf-8",
    )

    timestamp_key = "4.000"
    thumb_key = hashlib.sha256(f"flux-thumbnail-cache-v1:{root_hash}:{timestamp_key}".encode("utf-8")).hexdigest()
    thumbnail = cache_dirs["thumbnails"] / f"{thumb_key}.png"
    thumbnail.write_bytes(b"root-thumbnail")
    thumbnail_hash = hashlib.sha256(b"root-thumbnail").hexdigest()
    frame_vision = cache_dirs["vision"] / "frame.json"
    frame_vision.write_text(
        json.dumps({"schema": "flux-vision-cache-v1", "source_hash": thumbnail_hash, "text": "frame"}),
        encoding="utf-8",
    )

    monkeypatch.setattr(
        "flux_llm_kb.acceleration.resolve_cache_layout",
        lambda: {
            "root": str(cache_root),
            "source": "test",
            "directories": {name: str(path) for name, path in cache_dirs.items()},
        },
    )

    result = database.purge_derived_cache_entries(
        source_hashes={root_hash, shared_hash},
        active_source_hashes={shared_hash},
        frame_timestamps_by_hash={root_hash: [4.0]},
        purge_unreferenced=True,
        dry_run=False,
    )

    assert result["deleted"]["ocr"] == 2
    assert result["deleted"]["asr"] == 1
    assert result["deleted"]["vision"] == 1
    assert result["deleted"]["thumbnails"] == 1
    assert result["skipped_shared"]["ocr"] == 1
    assert not root_ocr.exists()
    assert shared_ocr.exists()
    assert not stale_ocr.exists()
    assert not root_asr.exists()
    assert not thumbnail.exists()
    assert not frame_vision.exists()


def test_delete_monitored_root_runs_sidecar_cache_and_search_cleanup_before_db_removal(monkeypatch):
    executed = []
    root_hash = "a" * 64
    shared_hash = "b" * 64

    class FakeCursor:
        rowcount = 1

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))
            self.rowcount = 1

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, name FROM monitored_roots" in sql:
                return ("root-1", "docs")
            return None

        def fetchall(self):
            sql = executed[-1][0]
            if "FROM source_assets a" in sql and "LEFT JOIN asset_chunks" in sql:
                return [
                    (
                        root_hash,
                        {"frame_sampling": {"timestamps": [4.0]}, "staged_jobs": [{"payload": {"segment_index": 0}}]},
                    ),
                    (shared_hash, {}),
                ]
            if "SELECT DISTINCT a.content_hash" in sql:
                return [(shared_hash, {})]
            return []

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

    calls = []
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(
        database,
        "delete_managed_mail_sidecars_for_root",
        lambda **kwargs: calls.append(("sidecars", kwargs)) or {"deleted": 1, "missing": 0, "failed": 0, "blocked": 0},
    )
    monkeypatch.setattr(
        database,
        "purge_derived_cache_entries",
        lambda **kwargs: calls.append(("cache", kwargs)) or {"deleted": {}, "missing": {}, "skipped_shared": {}, "errors": []},
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "purge_corpus_search_index_for_roots",
        lambda **kwargs: calls.append(("search", kwargs))
        or {"vespa_documents_deleted": 2, "search_index_records_deleted": 2, "errors": []},
        raising=False,
    )

    result = database.delete_monitored_root(root_id="docs", purge_index=True, actor="cli")

    assert result["deleted"] is True
    assert [name for name, _kwargs in calls] == ["sidecars", "cache", "search"]
    assert calls[1][1]["source_hashes"] == {root_hash, shared_hash}
    assert calls[1][1]["active_source_hashes"] == {shared_hash}
    assert calls[1][1]["frame_timestamps_by_hash"] == {root_hash: [4.0]}
    assert calls[2][1]["root_names"] == ["docs"]
    delete_chunk_index = next(index for index, (sql, _params) in enumerate(executed) if "DELETE FROM asset_chunks" in sql)
    assert calls[0][1] == {"root_name": "docs"}
    assert any("SELECT DISTINCT a.content_hash" in sql for sql, _params in executed[:delete_chunk_index])


def test_worker_family_stats_aggregates_queue_counts_and_durations(monkeypatch):
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
                    "media",
                    "gpu",
                    2,
                    1,
                    1,
                    0,
                    24,
                    95,
                    120,
                    5,
                    2,
                    3,
                    1,
                    7,
                    9,
                    4,
                    5,
                    2,
                    6,
                    7,
                    8,
                    9,
                    10,
                    11,
                    12,
                    13,
                    14,
                    15,
                    16,
                    17,
                    18,
                    19,
                    20,
                    21,
                    120,
                    2,
                    1,
                    [{"id": "job-1", "path": "private/root/clip.mp4", "duration_ms": 900}],
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

    rows = database.worker_family_stats()

    sql = executed[0][0]
    assert "FILTER (WHERE status = 'pending')" in sql
    assert "percentile_disc(0.95)" in sql
    assert "ocr_cache_hits" in sql
    assert "ocr_cache_misses" in sql
    assert "asr_cache_hits" in sql
    assert "asr_cache_misses" in sql
    assert "asr_segments" in sql
    assert "container_member_count" in sql
    assert "container_parsed_child_count" in sql
    assert "container_skipped_child_count" in sql
    assert "container_blocked_dependency_count" in sql
    assert "vision_cache_hits" in sql
    assert "vision_cache_misses" in sql
    assert "vision_descriptions" in sql
    assert "vision_blocked_dependency_count" in sql
    assert "decorative_image_skips" in sql
    assert "frame_sample_count" in sql
    assert "thumbnail_cache_hits" in sql
    assert "thumbnail_cache_misses" in sql
    assert "embedding_vectors" in sql
    assert "embedding_skipped_unchanged" in sql
    assert "embedding_batches" in sql
    assert "embedding_cache_hits" in sql
    assert "embedding_cache_misses" in sql
    assert "parser_cache_hits" in sql
    assert "parser_cache_misses" in sql
    assert "manifest_skipped_unchanged" in sql
    assert "oldest_pending_age_seconds" in sql
    assert "slowest_recent_jobs" in sql
    assert rows == [
        {
            "family": "media",
            "resource_class": "gpu",
            "pending": 2,
            "running": 1,
            "blocked": 1,
            "failed": 0,
            "avg_duration_ms": 24,
            "p95_duration_ms": 95,
            "max_duration_ms": 120,
            "ocr_cache_hits": 5,
            "ocr_cache_misses": 2,
            "asr_cache_hits": 3,
            "asr_cache_misses": 1,
            "asr_segments": 7,
            "container_member_count": 9,
            "container_parsed_child_count": 4,
            "container_skipped_child_count": 5,
            "container_blocked_dependency_count": 2,
            "vision_cache_hits": 6,
            "vision_cache_misses": 7,
            "vision_descriptions": 8,
            "vision_blocked_dependency_count": 9,
            "decorative_image_skips": 10,
            "frame_sample_count": 11,
            "thumbnail_cache_hits": 12,
            "thumbnail_cache_misses": 13,
            "embedding_vectors": 14,
            "embedding_skipped_unchanged": 15,
            "embedding_batches": 16,
            "embedding_cache_hits": 17,
            "embedding_cache_misses": 18,
            "parser_cache_hits": 19,
            "parser_cache_misses": 20,
            "manifest_skipped_unchanged": 21,
            "oldest_pending_age_seconds": 120,
            "retrying_locked": 2,
            "blocked_locked": 1,
            "slowest_recent_jobs": [{"id": "job-1", "path": "clip.mp4", "duration_ms": 900}],
        }
    ]


def test_claim_corpus_jobs_applies_family_caps(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return []

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

    database.claim_corpus_jobs(limit=4, worker_id="worker-a", family_caps={"media": 1, "archive": 2})

    sql, params = executed[0]
    assert "running_family_counts" in sql
    assert "family_caps" in sql
    assert "delete_requested_at IS NULL" in sql
    assert params[0] == json.dumps({"archive": 2, "media": 1}, sort_keys=True)


def test_claim_corpus_jobs_limits_each_family_to_remaining_capacity(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return []

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

    database.claim_corpus_jobs(limit=20, worker_id="worker-a", family_caps={"image": 2, "office": 3})

    sql, _params = executed[0]
    assert "row_number() OVER (PARTITION BY job_family" in sql
    assert "ranked.family_rank <= ranked.family_capacity_available" in sql
    assert "GREATEST(0, family_cap - running_count)" in sql


def test_claim_corpus_jobs_orders_family_cap_filter_params(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return []

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

    database.claim_corpus_jobs(
        limit=5,
        worker_id="worker-a",
        root_name="docs",
        job_families=["image"],
        family_caps={"image": 2},
    )

    sql, params = executed[0]
    assert "WITH family_caps AS" in sql
    assert json.loads(params[0]) == {"image": 2}
    assert params[1] == ["image"]
    assert params[2] == "docs"
    assert params[3] == "worker-a"
    assert params[4] == 5


def test_benchmark_runs_insert_list_and_compute_previous_delta(monkeypatch):
    executed = []
    timestamp = datetime(2026, 6, 24, 10, 0, tzinfo=timezone.utc)

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return ("run-2", timestamp)

        def fetchall(self):
            return [
                (
                    "run-2",
                    "image-heavy",
                    "scan",
                    "nightly",
                    "baseline",
                    "completed",
                    10,
                    1000,
                    10.0,
                    75,
                    120,
                    180,
                    "warm",
                    7,
                    3,
                    10,
                    10,
                    0,
                    2,
                    4,
                    3,
                    9,
                    {"image": {"completed": 10}},
                    {"provider": "synthetic", "private_path": "E:/secret/root"},
                    "monitored_root",
                    "sha256:scope",
                    "after-update",
                    {"version": "0.1.0"},
                    {"hash_parallelism": 4, "private_path": "E:/secret/root"},
                    {"local_model": {"state": "disabled"}, "raw_text": "nope"},
                    {"settings_mutated": False, "root_path": "E:/secret/root"},
                    timestamp,
                    1250,
                    8.0,
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

    inserted = database.record_benchmark_run(
        fixture="image-heavy",
        file_count=10,
        elapsed_ms=1000,
        timings_ms=[50, 75, 120, 180],
        mode="scan",
        label="nightly",
        compare_label="baseline",
        warm_state="warm",
        pass_index=2,
        hash_parallelism=4,
        worker_count=3,
        manifest_skipped_unchanged=9,
        cache_hits=7,
        cache_misses=3,
        worker_family_breakdown={"image": {"completed": 10}},
        metadata={"private_path": "E:/secret/root", "provider": "synthetic", "raw_text": "do not store"},
        scope_type="monitored_root",
        scope_hash="sha256:scope",
        deployment_label="after-update",
        build_metadata={"version": "0.1.0"},
        settings_snapshot={"hash_parallelism": 4, "private_path": "E:/secret/root"},
        model_telemetry={"local_model": {"state": "disabled"}, "raw_text": "do not store"},
        recommendation_metadata={"settings_mutated": False, "root_path": "E:/secret/root"},
    )
    rows = database.list_benchmark_runs(
        fixture="image-heavy",
        mode="scan",
        label="nightly",
        warm_state="warm",
        scope_type="monitored_root",
        deployment_label="after-update",
        limit=5,
    )

    insert_sql, insert_params = executed[0]
    assert "INSERT INTO acceleration_benchmark_runs" in insert_sql
    assert "mode" in insert_sql
    assert "label" in insert_sql
    assert "manifest_skipped_unchanged" in insert_sql
    assert "scope_type" in insert_sql
    assert "deployment_label" in insert_sql
    assert "model_telemetry" in insert_sql
    assert insert_params[0:5] == ("image-heavy", "scan", "nightly", "baseline", "completed")
    assert json.loads(insert_params[-9]) == {"image": {"completed": 10}}
    assert json.loads(insert_params[-8]) == {"provider": "synthetic"}
    assert json.loads(insert_params[-3]) == {"hash_parallelism": 4}
    assert json.loads(insert_params[-2]) == {"local_model": {"state": "disabled"}}
    assert json.loads(insert_params[-1]) == {"settings_mutated": False}
    list_sql, list_params = executed[1]
    assert "mode = %s" in list_sql
    assert "label = %s" in list_sql
    assert "warm_state = %s" in list_sql
    assert "scope_type = %s" in list_sql
    assert "deployment_label = %s" in list_sql
    assert "LEFT JOIN LATERAL" in list_sql
    assert "prior.label = current_run.compare_label" in list_sql
    assert list_params[:6] == ("image-heavy", "scan", "nightly", "warm", "monitored_root", "after-update")
    assert inserted["id"] == "run-2"
    assert inserted["mode"] == "scan"
    assert rows[0]["previous_elapsed_delta_ms"] == -250
    assert rows[0]["previous_throughput_delta"] == 2.0
    assert rows[0]["mode"] == "scan"
    assert rows[0]["label"] == "nightly"
    assert rows[0]["compare_label"] == "baseline"
    assert rows[0]["pass_index"] == 2
    assert rows[0]["hash_parallelism"] == 4
    assert rows[0]["worker_count"] == 3
    assert rows[0]["manifest_skipped_unchanged"] == 9
    assert rows[0]["scope_type"] == "monitored_root"
    assert rows[0]["scope_hash"] == "sha256:scope"
    assert rows[0]["deployment_label"] == "after-update"
    assert rows[0]["build_metadata"] == {"version": "0.1.0"}
    assert rows[0]["settings_snapshot"] == {"hash_parallelism": 4}
    assert rows[0]["model_telemetry"] == {"local_model": {"state": "disabled"}}
    assert rows[0]["recommendation_metadata"] == {"settings_mutated": False}
    assert "private_path" not in json.dumps(rows)
    assert "raw_text" not in json.dumps(rows)


def test_retrieval_benchmark_runs_insert_list_and_sanitize_metadata(monkeypatch):
    executed = []
    timestamp = datetime(2026, 6, 25, 10, 0, tzinfo=timezone.utc)

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return ("retrieval-run-2", timestamp)

        def fetchall(self):
            return [
                (
                    "retrieval-run-2",
                    "standard",
                    "nightly",
                    "baseline",
                    "completed",
                    5,
                    4,
                    1,
                    {"top1_accuracy": 0.8, "raw_text": "nope"},
                    [{"case_id": "case-1", "query_hash": "sha256:abc", "raw_query": "private"}],
                    {"provider": "synthetic", "private_path": "E:/secret/root"},
                    {"settings_mutated": False, "root_path": "E:/secret/root"},
                    timestamp,
                    {"top1_accuracy": 0.7},
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

    inserted = database.record_retrieval_benchmark_run(
        suite="standard",
        label="nightly",
        compare_label="baseline",
        status="completed",
        query_count=5,
        passed_count=4,
        failed_count=1,
        metrics={"top1_accuracy": 0.8, "raw_text": "do not store"},
        case_results=[{"case_id": "case-1", "query_hash": "sha256:abc", "raw_query": "do not store"}],
        metadata={"provider": "synthetic", "private_path": "E:/secret/root"},
        recommendation_metadata={"settings_mutated": False, "root_path": "E:/secret/root"},
    )
    rows = database.list_retrieval_benchmark_runs(suite="standard", label="nightly", limit=5)

    insert_sql, insert_params = executed[0]
    assert "INSERT INTO retrieval_benchmark_runs" in insert_sql
    assert insert_params[:4] == ("standard", "nightly", "baseline", "completed")
    assert json.loads(insert_params[7]) == {"top1_accuracy": 0.8}
    assert json.loads(insert_params[8]) == [{"case_id": "case-1", "query_hash": "sha256:abc"}]
    assert json.loads(insert_params[9]) == {"provider": "synthetic"}
    assert json.loads(insert_params[10]) == {"settings_mutated": False}
    list_sql, list_params = executed[1]
    assert "suite = %s" in list_sql
    assert "label = %s" in list_sql
    assert "LEFT JOIN LATERAL" in list_sql
    assert "prior.label = current_run.compare_label" in list_sql
    assert list_params == ["standard", "nightly", 5]
    assert inserted["id"] == "retrieval-run-2"
    assert rows[0]["previous_metrics"] == {"top1_accuracy": 0.7}
    assert rows[0]["metric_deltas"] == {"top1_accuracy": 0.1}
    assert rows[0]["case_results"] == [{"case_id": "case-1", "query_hash": "sha256:abc"}]
    assert "private_path" not in json.dumps(rows)
    assert "raw_query" not in json.dumps(rows)
    assert "raw_text" not in json.dumps(rows)


def test_recent_retrieval_explain_diagnostics_reads_current_run_schema(monkeypatch):
    executed = []
    timestamp = datetime(2026, 6, 25, 10, 0, tzinfo=timezone.utc)

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
                    3,
                    {"confidence_band": "high", "case_count": 3},
                    [
                        {"query_hash": "sha256:first", "passed": True},
                        {"query_hash": "sha256:failed", "passed": False},
                        {"case_id": "missing-hash", "passed": False},
                    ],
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

    rows = database.recent_retrieval_explain_diagnostics(limit=1)

    sql, params = executed[0]
    assert "query_count" in sql
    assert "metrics" in sql
    assert "case_results" in sql
    assert "query_hashes" not in sql
    assert "aggregate_metrics" not in sql
    assert "failed_cases" not in sql
    assert params == (1,)
    assert rows == [
        {
            "query_hash": "sha256:first",
            "result_count": 3,
            "confidence": "high",
            "failed_case_count": 2,
            "created_at": timestamp.isoformat(),
        }
    ]


def test_code_feedback_events_insert_summary_and_sanitize_private_values(monkeypatch):
    executed = []
    timestamp = datetime(2026, 6, 25, 10, 0, tzinfo=timezone.utc)

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return ("feedback-1", timestamp)

        def fetchall(self):
            return [
                (
                    "missing_symbol",
                    "docs",
                    2,
                    0,
                    3,
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

    inserted = database.record_code_feedback_event(
        query="Find private implementation details",
        root_name="docs",
        result_count=0,
        surface="dashboard",
        miss_category="missing_symbol",
        expected_symbol="PrivateService.build",
        path="E:/private/repo/src/private_service.py",
        metadata={"raw_query": "do not store", "private_path": "E:/private/repo", "note": "safe"},
    )
    summary = database.code_feedback_summary(root_name="docs", limit=5)

    insert_sql, insert_params = executed[0]
    assert "INSERT INTO code_retrieval_feedback_events" in insert_sql
    assert insert_params[0] == "docs"
    assert str(insert_params[1]).startswith("sha256:")
    assert str(insert_params[2]).startswith("sha256:")
    assert insert_params[3] == 0
    assert insert_params[4] == "dashboard"
    assert insert_params[5] == "missing_symbol"
    assert str(insert_params[6]).startswith("sha256:")
    assert insert_params[7] == "private_service.py"
    assert json.loads(insert_params[8]) == {"note": "safe"}
    list_sql, list_params = executed[1]
    assert "miss_category" in list_sql
    assert "root_name = %s" in list_sql
    assert list_params == ["docs", 5]
    assert inserted["id"] == "feedback-1"
    assert summary["settings_mutated"] is False
    assert summary["rows"][0]["miss_category"] == "missing_symbol"
    serialized = json.dumps({"inserted": inserted, "summary": summary}).lower()
    assert "private implementation" not in serialized
    assert "privateservice" not in serialized
    assert "e:/private" not in serialized


def test_code_symbol_search_filters_path_glob_and_generated_defaults(monkeypatch):
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
                    "OrderService.build_invoice",
                    "build_invoice",
                    "method",
                    "python",
                    "src/orders.py",
                    5,
                    7,
                    "parsed",
                    1.0,
                    "app",
                    {"generated": False, "routes": ["/orders/{order_id}"]},
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

    rows = database.search_code_symbols(
        query="build_invoice",
        root_name="app",
        language="python",
        symbol_kind="method",
        relationship="call",
        path_glob="src/*.py",
        include_generated=False,
        limit=5,
    )

    sql, params = executed[0]
    assert "a.path LIKE %s ESCAPE '\\'" in sql
    assert "COALESCE(cs.metadata->>'generated', 'false') <> 'true'" in sql
    assert "cr_filter.relationship_kind = %s" in sql
    assert "src/%\\.py" not in params
    assert rows[0]["is_generated"] is False
    assert rows[0]["route"] == "/orders/{order_id}"


def test_watcher_events_store_metadata_counts_and_sanitized_paths(monkeypatch):
    executed = []
    timestamp = datetime(2026, 6, 24, 10, 0, tzinfo=timezone.utc)

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            if "SELECT id::text FROM monitored_roots" in executed[-1][0]:
                return ("root-1",)
            return ("event-1",)

        def fetchall(self):
            return [("event-1", "docs", "changed", "sha256:abc", {"backend": "polling"}, timestamp)]

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

    database.record_watcher_heartbeat(root_name="docs", metadata={"backend": "polling"})
    database.record_watch_event(root_name="docs", action="changed", path_hash="sha256:abc", metadata={"private_path": "E:/secret/file.txt", "backend": "polling"})
    rows = database.list_watch_events(limit=10)

    sql = "\n".join(statement for statement, _params in executed)
    assert "metadata = watcher_state.metadata || EXCLUDED.metadata" in sql
    assert "event_count = watcher_state.event_count + CASE" in sql
    assert "INSERT INTO watcher_events" in sql
    event_params = next(params for statement, params in executed if "INSERT INTO watcher_events" in statement)
    assert json.loads(event_params[-1]) == {"backend": "polling"}
    assert rows[0]["path_hash"] == "sha256:abc"
    assert "private_path" not in json.dumps(rows)


def test_scan_manifest_upsert_and_lookup_are_metadata_only(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            if "SELECT id::text FROM monitored_roots" in executed[-1][0]:
                return ("root-1",)
            if "RETURNING" in executed[-1][0]:
                return ("docs/readme.md",)
            return ("docs/readme.md", 12, 42, "quick", "content", {"fixture": "text-heavy"}, "indexed", False, 2)

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

    database.upsert_scan_manifest(
        root_name="docs",
        path="docs/readme.md",
        size_bytes=12,
        mtime_ns=42,
        quick_hash="quick",
        content_hash="content",
        metadata={"fixture": "text-heavy", "raw_text": "do not store"},
    )
    row = database.lookup_scan_manifest(root_name="docs", path="docs/readme.md")

    sql = "\n".join(statement for statement, _params in executed)
    assert "INSERT INTO crawl_path_manifests" in sql
    assert "SELECT m.path, m.size_bytes, m.mtime_ns, m.quick_hash, m.content_hash" in sql
    assert "count(c.id)::integer AS chunk_count" in sql
    lookup_function = Path(database.__file__).read_text(encoding="utf-8").split("def lookup_scan_manifest", 1)[1].split("def apply_extraction_result", 1)[0]
    assert "a.deleted_at IS NULL" not in lookup_function
    manifest_params = next(params for statement, params in executed if "INSERT INTO crawl_path_manifests" in statement)
    assert json.loads(manifest_params[-1]) == {"fixture": "text-heavy"}
    assert row["content_hash"] == "content"
    assert row["source_asset_status"] == "indexed"
    assert row["source_asset_deleted"] is False
    assert row["chunk_count"] == 2


def test_load_scan_manifest_returns_path_manifest_map(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            if "SELECT id::text FROM monitored_roots" in executed[-1][0]:
                return ("root-1",)
            return None

        def fetchall(self):
            return [
                ("docs/readme.md", 12, 42, "quick", "content", {"fixture": "text-heavy"}, "indexed", False, 2),
                ("docs/missing.md", 10, 40, "quick2", None, {}, "queued", True, 0),
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

    manifest = database.load_scan_manifest(root_name="docs")

    sql = "\n".join(statement for statement, _params in executed)
    assert "SELECT m.path, m.size_bytes, m.mtime_ns, m.quick_hash, m.content_hash" in sql
    assert "WHERE m.root_id = %s" in sql
    assert "AND m.path = %s" not in sql
    assert manifest["docs/readme.md"]["content_hash"] == "content"
    assert manifest["docs/readme.md"]["chunk_count"] == 2
    assert manifest["docs/missing.md"]["source_asset_deleted"] is True


def test_persist_crawl_plan_replaces_chunks_for_manifest_missing_chunk_repair():
    source = Path(database.__file__).read_text(encoding="utf-8")
    function = source.split("def persist_crawl_plan", 1)[1].split("def crawl_status", 1)[0]

    assert "existing_chunk_count" in function
    assert "SELECT count(*) FROM asset_chunks WHERE asset_id = %s" in function
    assert "existing_chunk_count <= 0" in function
    assert "repaired_missing_chunks" in function
    assert 'asset.metadata.get("manifest_repaired_missing_chunks")' in function
    assert "or repaired_missing_chunks" in function
    assert "_replace_asset_chunks" in function


def test_search_corpus_chunks_includes_freshness_stream():
    source = Path(database.__file__).read_text(encoding="utf-8")
    function = source.split("def search_corpus_chunks", 1)[1].split("\ndef ", 1)[0]

    assert "corpus_lexical" in function
    assert "corpus_fuzzy" in function
    assert "corpus_vector" not in function
    assert "corpus_trust" in function
    assert "corpus_freshness" in function
    assert "EXTRACT(EPOCH FROM (now() - c.updated_at))" in source


def test_search_corpus_chunks_uses_bounded_lexical_title_path_fallback_without_pgvector():
    source = Path(database.__file__).read_text(encoding="utf-8")
    function = source.split("def search_corpus_chunks", 1)[1].split("\ndef ", 1)[0]

    assert "WITH nearest_embeddings AS MATERIALIZED" not in function
    assert "emb.embedding <=>" not in function
    assert "set_config('hnsw.ef_search'" not in function
    assert "c.title %% %s" in function
    assert "a.path %% %s" in function
    assert "c.body %% %s" not in function
    assert "cs.qualified_name %% %s" in function
    assert "cs.name %% %s" in function
    assert "set_config('pg_trgm.similarity_threshold'" in function


def test_search_corpus_chunks_hydrates_candidates_once_and_records_diagnostics(monkeypatch):
    executed: list[tuple[str, tuple[object, ...]]] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, tuple(params)))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text FROM monitored_roots" in sql:
                return ("root-1",)
            return None

        def fetchall(self):
            sql = executed[-1][0]
            if "plainto_tsquery" in sql and "SELECT c.id::text" in sql:
                return [("chunk-1", 0.9)]
            if "greatest(similarity(c.title" in sql:
                return [("chunk-1", 0.7)]
            if "FROM code_symbols" in sql:
                return [("chunk-1", 2.0)]
            if "WHERE c.id = ANY" in sql:
                return [
                    (
                        "chunk-1",
                        "asset-1",
                        "SearchService.search",
                        "Search implementation details",
                        "src/flux_llm_kb/service.py",
                        0,
                        500,
                        "llm-kb",
                        0.5,
                        0.75,
                        "code",
                        {"language": "python"},
                        {},
                    )
                ]
            return []

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    diagnostics: dict[str, object] = {}
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_add_semantic_duplicate_metadata", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(database, "_search_mail_sidecar_rows", lambda *_args, **_kwargs: [])

    rows = database.search_corpus_chunks("SearchService.search", root_name="llm-kb", diagnostics=diagnostics)

    hydrate_queries = [sql for sql, _params in executed if "WHERE c.id = ANY" in sql and "JOIN source_assets" in sql]
    assert len(hydrate_queries) == 1
    assert rows[0]["id"] == "chunk-1"
    assert "corpus_vector" not in rows[0]["raw_scores"]
    assert "corpus_vector" not in diagnostics["streams"]
    assert diagnostics["streams"]["corpus_hydration"]["rows"] == 1


def test_search_corpus_chunks_skips_fuzzy_when_cheaper_streams_satisfy_result_target(monkeypatch):
    executed: list[tuple[str, tuple[object, ...]]] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, tuple(params)))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text FROM monitored_roots" in sql:
                return ("root-1",)
            return None

        def fetchall(self):
            sql = executed[-1][0]
            if "plainto_tsquery" in sql and "SELECT c.id::text" in sql:
                return [(f"chunk-{index}", 1.0 - index / 100.0) for index in range(3)]
            if "greatest(similarity(c.title" in sql:
                raise AssertionError("fuzzy corpus search should be skipped once cheap streams are sufficient")
            if "WHERE c.id = ANY" in sql:
                return [
                    (
                        "chunk-0",
                        "asset-1",
                        "Scholarship_ER_Analysis_Summary.xlsx",
                        "Scholarship lookup and equivalency summary",
                        "G2B/Analysis/Scholarship_ER_Analysis_Summary.xlsx",
                        0,
                        500,
                        "mohesr-documents",
                        0.5,
                        0.75,
                        "document",
                        {},
                        {},
                    )
                ]
            return []

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    diagnostics: dict[str, object] = {}
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_add_semantic_duplicate_metadata", lambda *_args, **_kwargs: None)
    monkeypatch.setattr(database, "_search_mail_sidecar_rows", lambda *_args, **_kwargs: [])

    rows = database.search_corpus_chunks(
        "student scholarship equivalency university",
        limit=3,
        root_name="mohesr-documents",
        diagnostics=diagnostics,
    )

    sql = "\n".join(statement for statement, _params in executed)
    assert "greatest(similarity(c.title" not in sql
    assert rows[0]["id"] == "chunk-0"
    assert diagnostics["streams"]["corpus_fuzzy"] == {
        "duration_ms": 0.0,
        "rows": 0,
        "plan": "skipped_sufficient_candidates",
        "candidate_limit": 12,
        "available_candidates": 3,
    }


def test_search_corpus_chunks_promotes_exact_code_definition():
    streams = {
        "corpus_lexical": ["caller", "definition", "test"],
        "corpus_fuzzy": ["caller", "definition", "test"],
        "code_symbol_exact": ["definition", "caller", "test"],
        "corpus_trust": ["caller", "test", "definition"],
        "corpus_freshness": ["caller", "test", "definition"],
    }
    details = {
        "caller": {
            "title": "src/flux_llm_kb/acceleration.py::collect_acceleration_status",
            "raw_scores": {"code_symbol_exact": 0.32},
            "code": {
                "primary_symbol": "collect_acceleration_status",
                "relationship": "definition",
            },
        },
        "definition": {
            "title": "src/flux_llm_kb/acceleration.py::_watcher_backend_status",
            "raw_scores": {"code_symbol_exact": 3.0},
            "code": {
                "primary_symbol": "_watcher_backend_status",
                "relationship": "definition",
            },
        },
        "test": {
            "title": "tests/test_acceleration.py::test_watcher_backend_status",
            "raw_scores": {"code_symbol_exact": 0.24},
            "code": {
                "primary_symbol": "test_watcher_backend_status",
                "relationship": "test",
            },
        },
    }

    ranked = database._rank_corpus_candidates(
        "src/flux_llm_kb/acceleration.py _watcher_backend_status",
        streams=streams,
        details=details,
        filters={"file_kinds": ["code"]},
    )

    assert ranked[0].item_id == "definition"
    assert "code_rank_adjustment" in ranked[0].streams
    assert details["definition"]["raw_scores"]["code_rank_adjustment"] == 0.1
    assert details["caller"]["raw_scores"]["code_rank_adjustment"] == 0.025
    assert "code_rank_adjustment" not in details["test"]["raw_scores"]


def test_generic_policy_query_promotes_exact_non_code_over_weak_code_chunks():
    streams = {
        "corpus_lexical": ["code-a", "code-b", "agents"],
        "corpus_fuzzy": ["code-a", "agents", "code-b"],
        "code_symbol_exact": ["code-a", "code-b"],
        "corpus_trust": ["code-a", "code-b", "agents"],
        "corpus_freshness": ["code-a", "code-b", "agents"],
    }
    details = {
        "agents": {
            "title": "AGENTS.md",
            "raw_scores": {"corpus_lexical": 0.98, "corpus_fuzzy": 0.84},
            "file_kind": "text",
        },
        "code-a": {
            "title": "scripts/dev/complete-feature.ps1::Invoke-FeatureCloseout",
            "raw_scores": {"code_symbol_exact": 0.24, "corpus_lexical": 0.12},
            "file_kind": "code",
            "code": {
                "primary_symbol": "Invoke-FeatureCloseout",
                "relationship": "definition",
            },
        },
        "code-b": {
            "title": "src/flux_llm_kb/hooks.py::failed_step",
            "raw_scores": {"code_symbol_exact": 0.18, "corpus_lexical": 0.11},
            "file_kind": "code",
            "code": {
                "primary_symbol": "failed_step",
                "relationship": "definition",
            },
        },
    }

    ranked = database._rank_corpus_candidates(
        "If complete-feature.ps1 fails, report failed_step and log_path from AGENTS.md.",
        streams=streams,
        details=details,
        filters=None,
    )

    assert ranked[0].item_id == "agents"
    assert ranked[0].score > ranked[1].score
    assert "balanced_non_code_guardrail" in ranked[0].streams
    assert not database._has_code_implementation_intent("failed_step log_path")
    assert "code_rank_adjustment" not in details["code-b"]["raw_scores"]


def test_direct_corpus_filters_accept_singular_file_kind_code():
    assert database._filters_request_code_focus({"file_kind": "code"})
    assert database._filters_request_code_focus({"file_kinds": ["code"]})
    assert not database._filters_request_code_focus({"language": "python", "path_glob": "src/*.py"})

    sql, params = database._corpus_code_filter_sql({"file_kind": "code", "path_glob": "src/*.py"})

    assert "AND a.file_kind = ANY(%s::text[])" in sql
    assert "a.path LIKE %s" in sql
    assert params[:2] == [["code"], "src/%.py"]


def test_search_index_record_populates_asset_chunk_root_id():
    source = Path(database.__file__).read_text(encoding="utf-8")
    function = source.split("def _upsert_search_index_record", 1)[1].split("\ndef ", 1)[0]

    assert "root_id" in function
    assert "NULLIF(%s, '')::uuid" in function
    assert "search_index_records" in function


def test_semantic_duplicate_cluster_builder_selects_canonical_without_deleting_members():
    candidates = {
        "chunk-a": {
            "owner_table": "asset_chunks",
            "owner_id": "chunk-a",
            "memory_class": "corpus",
            "label": "RFP response draft",
            "source_path": "client/RFP Response v1.docx",
            "root_name": "docs",
            "workspace_key": "root:docs",
            "trust_rank": 500,
            "text_length": 200,
            "updated_at": datetime(2026, 6, 20, tzinfo=timezone.utc),
        },
        "chunk-b": {
            "owner_table": "asset_chunks",
            "owner_id": "chunk-b",
            "memory_class": "corpus",
            "label": "RFP response final",
            "source_path": "client/RFP Response final.docx",
            "root_name": "docs",
            "workspace_key": "root:docs",
            "trust_rank": 900,
            "text_length": 180,
            "updated_at": datetime(2026, 6, 21, tzinfo=timezone.utc),
        },
        "chunk-c": {
            "owner_table": "asset_chunks",
            "owner_id": "chunk-c",
            "memory_class": "corpus",
            "label": "RFP response copy",
            "source_path": "client/RFP Response copy.docx",
            "root_name": "docs",
            "workspace_key": "root:docs",
            "trust_rank": 500,
            "text_length": 240,
            "updated_at": datetime(2026, 6, 22, tzinfo=timezone.utc),
        },
    }

    clusters = database._build_semantic_duplicate_clusters(
        candidates,
        [("chunk-a", "chunk-b", 0.94), ("chunk-a", "chunk-c", 0.91)],
        memory_class="corpus",
        threshold=0.9,
    )

    assert len(clusters) == 1
    cluster = clusters[0]
    assert cluster["canonical_owner_id"] == "chunk-b"
    assert cluster["workspace_key"] == "root:docs"
    assert cluster["root_name"] == "docs"
    assert cluster["suppressed_count"] == 2
    assert cluster["max_similarity"] == 0.94
    assert {member["owner_id"] for member in cluster["members"]} == {"chunk-a", "chunk-b", "chunk-c"}
    assert [member for member in cluster["members"] if member["member_role"] == "canonical"][0]["owner_id"] == "chunk-b"
    assert {member["owner_id"]: member["similarity"] for member in cluster["members"] if member["member_role"] == "duplicate"} == {
        "chunk-a": 0.94,
        "chunk-c": 0.91,
    }
    assert all(member["member_role"] in {"canonical", "duplicate"} for member in cluster["members"])


def test_semantic_duplicate_workspace_key_sql_parenthesizes_json_extraction():
    class FakeCursor:
        def __init__(self):
            self.sql = ""

        def execute(self, sql, _params=()):
            self.sql = sql

        def fetchall(self):
            return []

    for memory_class, alias in (("episode", "e"), ("claim", "c")):
        cursor = FakeCursor()
        database._fetch_semantic_duplicate_candidates(cursor, memory_class=memory_class, root_name=None, limit=10)

        expected = f"THEN 'root:' || ({alias}.metadata->>'root_name')"
        assert expected in cursor.sql
        assert "JOIN embeddings" not in cursor.sql


def test_list_semantic_duplicate_clusters_returns_sanitized_members(monkeypatch):
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
                    "cluster-1",
                    "corpus",
                    "active",
                    "snowflake-vespa-cosine-v1",
                    0.9,
                    "root:docs",
                    "docs",
                    "asset_chunks",
                    "chunk-canonical",
                    {"suppressed_count": 1},
                    timestamp,
                    timestamp,
                    [
                        {
                            "owner_table": "asset_chunks",
                            "owner_id": "chunk-canonical",
                            "member_role": "canonical",
                            "similarity": 1.0,
                            "label": "Architecture",
                            "source_path": "docs/architecture.md",
                        },
                        {
                            "owner_table": "asset_chunks",
                            "owner_id": "chunk-copy",
                            "member_role": "duplicate",
                            "similarity": 0.93,
                            "label": "Architecture Copy",
                            "source_path": "docs/architecture copy.md",
                        },
                    ],
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

    payload = database.list_semantic_duplicate_clusters(memory_class="corpus", root_name="docs", limit=10)

    assert payload["summary"] == {"total": 1, "by_class": {"corpus": 1}, "suppressed_count": 1}
    assert payload["clusters"][0]["canonical"]["owner_id"] == "chunk-canonical"
    assert payload["clusters"][0]["members"][1]["source_path"] == "docs/architecture copy.md"
    assert "body" not in json.dumps(payload).lower()
    sql, params = executed[0]
    assert "semantic_duplicate_clusters" in sql
    assert "semantic_duplicate_members" in sql
    assert params[-1] == 10


def test_upsert_claim_does_not_record_legacy_embedding_rows():
    source = Path(database.__file__).read_text(encoding="utf-8")
    function = source.split("def upsert_claim", 1)[1].split("\ndef ", 1)[0]

    assert "_embedding_result_for_text" not in function
    assert "INSERT INTO embeddings" not in function
    assert "DELETE FROM embeddings" not in function


def test_enqueue_search_index_sync_creates_search_index_job(monkeypatch):
    executed: list[tuple[str, object]] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status" in sql:
                return None
            if "INSERT INTO capture_jobs" in sql:
                return ("job-search-index", "pending")
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.enqueue_search_index_sync(owner_class="corpus", root_name="docs", limit=25)

    sql, params = next((statement, params) for statement, params in executed if "INSERT INTO capture_jobs" in statement)
    payload = json.loads(params[1])
    assert "INSERT INTO capture_jobs" in sql
    assert "search_index_sync" in params
    assert payload == {
        "owner_class": "corpus",
        "root_name": "docs",
        "limit": 25,
        "page_size": 25,
        "page_sequence": 0,
        "search_engine": "vespa",
        "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
        "embedding_dimensions": 1024,
    }
    assert params[5:9] == ("embedding", "gpu", 35, 300)
    assert result == {
        "queued": 1,
        "job_id": "job-search-index",
        "deduped": False,
        "reused": False,
        "owner_class": "corpus",
        "root_name": "docs",
        "limit": 25,
        "page_size": 25,
        "page_sequence": 0,
        "search_engine": "vespa",
        "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
        "embedding_dimensions": 1024,
    }


def test_enqueue_search_index_sync_clamps_large_requests_to_safe_job_limit(monkeypatch):
    monkeypatch.delenv("FLUX_KB_SEARCH_INDEX_JOB_LIMIT", raising=False)
    executed: list[tuple[str, object]] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status" in sql:
                return None
            if "INSERT INTO capture_jobs" in sql:
                return ("job-search-index", "pending")
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.enqueue_search_index_sync(owner_class="corpus", root_name="docs", limit=5000)

    _sql, params = next((statement, params) for statement, params in executed if "INSERT INTO capture_jobs" in statement)
    payload = json.loads(params[1])
    assert payload["limit"] == 250
    assert payload["page_size"] == 100
    assert payload["page_sequence"] == 0
    assert result["limit"] == 250
    assert result["page_size"] == 100


def test_asset_triggered_search_index_sync_uses_safe_job_limit(monkeypatch):
    executed: list[tuple[str, object]] = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT r.name" in sql:
                return ("docs",)
            if "SELECT id::text, status" in sql:
                return None
            if "INSERT INTO capture_jobs" in sql:
                return ("job-search-index", "pending")
            return None

    result = database._enqueue_corpus_search_index_sync_for_asset(FakeCursor(), asset_id="asset-1")

    _sql, params = next((statement, params) for statement, params in executed if "INSERT INTO capture_jobs" in statement)
    payload = json.loads(params[1])
    assert payload["limit"] == 250
    assert payload["page_size"] == 100
    assert payload["page_sequence"] == 0
    assert result["deduped"] is False


def test_search_index_status_reports_missing_corpus_records_without_changing_status_counts(monkeypatch):
    executed: list[tuple[str, object]] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            sql = executed[-1][0]
            if "GROUP BY index_status" in sql:
                return [("failed", 2), ("indexed", 5), ("pending", 3), ("syncing", 1)]
            if "FROM search_index_records" in sql and "ORDER BY updated_at DESC" in sql:
                return [
                    (
                        "id:flux:flux_evidence::episodes--episode-1",
                        "episodes",
                        "episode-1",
                        "llm-kb",
                        "episode-hash",
                        "Snowflake/snowflake-arctic-embed-l-v2.0",
                        1024,
                        "indexed",
                        None,
                        None,
                        None,
                        None,
                    )
                ]
            if "FROM asset_chunks c" in sql and "rec.owner_id IS NULL" in sql:
                return [(7,)]
            return []

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    status = database.search_index_status(root_name="llm-kb")

    assert status["summary"]["by_status"] == {"failed": 2, "indexed": 5, "pending": 3, "syncing": 1}
    assert status["summary"]["total"] == 11
    assert status["summary"]["missing"] == 7
    assert status["summary"]["pending_work"] == 13
    assert status["missing"]["corpus"]["asset_chunks"] == 7
    missing_sql = next(sql for sql, _params in executed if "rec.owner_id IS NULL" in sql)
    assert "FROM asset_chunks c" in missing_sql
    assert "FROM episodes" not in missing_sql
    assert "FROM claims" not in missing_sql


def test_enqueue_capture_job_reuses_active_duplicate(monkeypatch):
    executed: list[tuple[str, object]] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "identity_key = capture_job_identity" in sql:
                return ("job-active", "pending")
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    job_id = database.enqueue_capture_job(
        job_type="codex_backfill",
        payload={"path": "history/thread.json", "status": "pending_review", "reason": "manual"},
    )

    sql = "\n".join(statement for statement, _params in executed)
    assert job_id == "job-active"
    assert "pg_advisory_xact_lock" in sql
    assert "identity_key = capture_job_identity(%s, %s::jsonb)" in sql
    assert "FOR UPDATE" in sql
    assert "INSERT INTO capture_jobs" not in sql
    assert "capture_job.queued" not in sql


def test_enqueue_capture_job_reuses_inactive_duplicate_and_audits(monkeypatch):
    executed: list[tuple[str, object]] = []
    rows = [("job-old", "failed"), ("job-old", "pending")]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "identity_key = capture_job_identity" in sql:
                return rows.pop(0)
            if "UPDATE capture_jobs" in sql:
                return rows.pop(0)
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    job_id = database.enqueue_capture_job(
        job_type="codex_backfill",
        payload={"path": "history/thread.json", "status": "pending_review", "requested_by": "dashboard"},
    )

    sql = "\n".join(statement for statement, _params in executed)
    assert job_id == "job-old"
    assert "pg_advisory_xact_lock" in sql
    assert "DELETE FROM capture_job_tool_invocations" in sql
    assert "UPDATE capture_jobs" in sql
    assert "attempts = 0" in sql
    assert "last_error = %s" in sql
    assert "delete_requested_at = NULL" in sql
    assert "delete_requested_by = NULL" in sql
    assert "delete_reason = NULL" in sql
    assert "created_at = now()" in sql
    assert "VALUES ('capture_job.identity_reused'" in sql
    assert "INSERT INTO capture_jobs" not in sql


def test_sync_search_index_batches_candidates_and_feeds_vespa(monkeypatch):
    fed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class FakeProvider:
        name = "model_runner"

        def embed_batch(self, inputs):
            return [
                EmbeddingResult(
                    owner_table=item.owner_table,
                    owner_id=item.owner_id,
                    model="Snowflake/snowflake-arctic-embed-l-v2.0",
                    dimensions=1024,
                    vector=[1.0] + [0.0] * 1023,
                    metadata={"source_hash": "source-hash"},
                )
                for item in inputs
            ]

    class FakeAdapter:
        def feed(self, document):
            fed.append(document)
            return {"ok": True}

        def delete(self, _document_id):
            return {"ok": True}

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_fetch_stale_search_index_records", lambda *_args, **_kwargs: [])
    monkeypatch.setattr(
        database,
        "_fetch_search_index_rows",
        lambda *_args, **_kwargs: [
            {
                "vespa_document_id": "id:flux:flux_evidence::asset_chunks--chunk-1",
                "owner_table": "asset_chunks",
                "owner_id": "chunk-1",
                "asset_id": "asset-1",
                "root_id": "33333333-3333-3333-3333-333333333333",
                "root_name": "docs",
                "title": "Chunk One",
                "body": "body",
                "source_path": "docs/chunk.md",
                "symbols": [],
                "language": "",
                "file_kind": "text",
                "lifecycle_state": "active",
                "deleted": False,
                "canonical": True,
                "source_hash": "old-hash",
                "index_text": "Chunk One\nbody",
                "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
                "embedding_dimensions": 1024,
                "existing_source_hash": None,
                "existing_index_status": None,
                "existing_embedding_model": None,
            }
        ],
    )
    monkeypatch.setattr(database, "_upsert_search_index_record", lambda *_args, **_kwargs: None)

    result = database.sync_search_index(owner_class="all", root_name="docs", limit=20, embedding_provider=FakeProvider(), adapter=FakeAdapter())

    assert result["owner_class"] == "all"
    assert result["root_name"] == "docs"
    assert result["requested"] == 1
    assert result["indexed"] == 1
    assert result["skipped_unchanged"] == 0
    assert result["embedding_model"] == "Snowflake/snowflake-arctic-embed-l-v2.0"
    assert result["embedding_dimensions"] == 1024
    assert fed[0]["fields"]["owner_table"] == "asset_chunks"


def test_sync_search_index_embeds_pending_rows_in_bounded_batches(monkeypatch):
    batch_calls = []
    fed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class FakeProvider:
        name = "model_runner"

        def embed_batch(self, inputs):
            batch_calls.append([item.owner_id for item in inputs])
            return [
                EmbeddingResult(
                    owner_table=item.owner_table,
                    owner_id=item.owner_id,
                    model="Snowflake/snowflake-arctic-embed-l-v2.0",
                    dimensions=1024,
                    vector=[1.0] + [0.0] * 1023,
                    metadata={"source_hash": f"hash-{item.owner_id}"},
                )
                for item in inputs
            ]

    class FakeAdapter:
        def feed(self, document):
            fed.append(document)
            return {"ok": True}

        def delete(self, _document_id):
            return {"ok": True}

    def candidate(index):
        return {
            "vespa_document_id": f"id:flux:flux_evidence::asset_chunks--chunk-{index}",
            "owner_table": "asset_chunks",
            "owner_id": f"chunk-{index}",
            "asset_id": f"asset-{index}",
            "root_id": "33333333-3333-3333-3333-333333333333",
            "root_name": "docs",
            "title": f"Chunk {index}",
            "body": f"body {index}",
            "source_path": f"docs/chunk-{index}.md",
            "symbols": [],
            "language": "",
            "file_kind": "text",
            "lifecycle_state": "active",
            "deleted": False,
            "canonical": True,
            "source_hash": f"old-hash-{index}",
            "index_text": f"Chunk {index}\nbody {index}",
            "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
            "embedding_dimensions": 1024,
            "existing_source_hash": None,
            "existing_index_status": None,
            "existing_embedding_model": None,
        }

    monkeypatch.setenv("FLUX_KB_SEARCH_INDEX_EMBEDDING_BATCH_SIZE", "2")
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_fetch_stale_search_index_records", lambda *_args, **_kwargs: [])
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [candidate(index) for index in range(5)])
    monkeypatch.setattr(database, "_upsert_search_index_record", lambda *_args, **_kwargs: None)

    result = database.sync_search_index(owner_class="corpus", root_name="docs", limit=5, embedding_provider=FakeProvider(), adapter=FakeAdapter())

    assert batch_calls == [["chunk-0", "chunk-1"], ["chunk-2", "chunk-3"], ["chunk-4"]]
    assert result["embedding_batch_size"] == 2
    assert result["embedding_batches"] == 3
    assert result["requested"] == 5
    assert result["indexed"] == 5
    assert len(fed) == 5


def test_sync_search_index_default_provider_uses_bulk_embedding_timeout(monkeypatch):
    captured: dict[str, object] = {}

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class FakeProvider:
        name = "model_runner"

        def __init__(self, **kwargs):
            captured.update(kwargs)

        def embed_batch(self, _inputs):  # pragma: no cover - no pending rows in this test
            raise AssertionError("no rows should be embedded")

    class FakeAdapter:
        def feed(self, _document):  # pragma: no cover - no pending rows in this test
            raise AssertionError("no documents should be fed")

        def delete(self, _document_id):  # pragma: no cover - no stale rows in this test
            raise AssertionError("no documents should be deleted")

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_fetch_stale_search_index_records", lambda *_args, **_kwargs: [])
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [])
    monkeypatch.setattr("flux_llm_kb.embeddings.SnowflakeEmbeddingProvider", FakeProvider)

    result = database.sync_search_index(owner_class="corpus", root_name="docs", limit=1, adapter=FakeAdapter())

    assert result["failed"] == 0
    assert captured["timeout_seconds"] == 60.0


def test_sync_search_index_embeds_outside_database_connection(monkeypatch):
    state = {"connection_open": False}

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

    class FakeConnection:
        def __enter__(self):
            state["connection_open"] = True
            return self

        def __exit__(self, *_args):
            state["connection_open"] = False
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class FakeProvider:
        name = "model_runner"

        def embed_batch(self, inputs):
            assert state["connection_open"] is False
            return [
                EmbeddingResult(
                    owner_table=item.owner_table,
                    owner_id=item.owner_id,
                    model="Snowflake/snowflake-arctic-embed-l-v2.0",
                    dimensions=1024,
                    vector=[1.0] + [0.0] * 1023,
                    metadata={"source_hash": f"hash-{item.owner_id}"},
                )
                for item in inputs
            ]

    class FakeAdapter:
        def feed(self, _document):
            assert state["connection_open"] is False
            return {"ok": True}

        def delete(self, _document_id):
            return {"ok": True}

    row = {
        "vespa_document_id": "id:flux:flux_evidence::asset_chunks--chunk-1",
        "owner_table": "asset_chunks",
        "owner_id": "chunk-1",
        "asset_id": "asset-1",
        "root_id": "33333333-3333-3333-3333-333333333333",
        "root_name": "docs",
        "title": "Chunk One",
        "body": "body",
        "source_path": "docs/chunk.md",
        "symbols": [],
        "language": "",
        "file_kind": "text",
        "lifecycle_state": "active",
        "deleted": False,
        "canonical": True,
        "source_hash": "old-hash",
        "index_text": "Chunk One\nbody",
        "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
        "embedding_dimensions": 1024,
        "existing_source_hash": None,
        "existing_index_status": None,
        "existing_embedding_model": None,
    }

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_fetch_stale_search_index_records", lambda *_args, **_kwargs: [])
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [row])
    monkeypatch.setattr(database, "_upsert_search_index_record", lambda *_args, **_kwargs: None)

    result = database.sync_search_index(owner_class="corpus", root_name="docs", limit=1, embedding_provider=FakeProvider(), adapter=FakeAdapter())

    assert result["indexed"] == 1


def test_sync_search_index_reraises_retryable_embedding_timeout_without_failed_records(monkeypatch):
    from flux_llm_kb.model_runner import ModelRunnerBusy

    statuses: list[str] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class TimeoutProvider:
        name = "model_runner"

        def embed_batch(self, _inputs):
            raise ModelRunnerBusy("model-runner embedding request timed out", retry_after_seconds=1.0)

    class FakeAdapter:
        def feed(self, _document):  # pragma: no cover - embedding fails before feed
            raise AssertionError("feed should not run")

        def delete(self, _document_id):
            return {"ok": True}

    row = {
        "vespa_document_id": "id:flux:flux_evidence::asset_chunks--chunk-1",
        "owner_table": "asset_chunks",
        "owner_id": "chunk-1",
        "asset_id": "asset-1",
        "root_id": "33333333-3333-3333-3333-333333333333",
        "root_name": "docs",
        "title": "Chunk One",
        "body": "body",
        "source_path": "docs/chunk.md",
        "symbols": [],
        "language": "",
        "file_kind": "text",
        "lifecycle_state": "active",
        "deleted": False,
        "canonical": True,
        "source_hash": "old-hash",
        "index_text": "Chunk One\nbody",
        "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
        "embedding_dimensions": 1024,
        "existing_source_hash": None,
        "existing_index_status": None,
        "existing_embedding_model": None,
    }

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_fetch_stale_search_index_records", lambda *_args, **_kwargs: [])
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [row])
    monkeypatch.setattr(database, "_upsert_search_index_record", lambda *_args, **kwargs: statuses.append(kwargs["status"]))

    with pytest.raises(ModelRunnerBusy):
        database.sync_search_index(owner_class="corpus", root_name="docs", limit=1, embedding_provider=TimeoutProvider(), adapter=FakeAdapter())

    assert "syncing" in statuses
    assert "failed" not in statuses


def test_sync_search_index_splits_only_unschedulable_embedding_batches(monkeypatch):
    from flux_llm_kb.gpu_scheduler import GpuLeaseRejected

    batch_calls: list[list[str]] = []
    fed: list[str] = []
    records: list[dict] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class CapacityProvider:
        name = "model_runner"

        def embed_batch(self, inputs):
            batch_calls.append([item.owner_id for item in inputs])
            if len(inputs) > 4:
                error = GpuLeaseRejected("batch exceeds embedding capacity")
                error.capacity_state = "unschedulable"
                raise error
            return [
                EmbeddingResult(
                    owner_table=item.owner_table,
                    owner_id=item.owner_id,
                    model="Snowflake/snowflake-arctic-embed-l-v2.0",
                    dimensions=1024,
                    vector=[1.0] + [0.0] * 1023,
                    metadata={"source_hash": f"hash-{item.owner_id}"},
                )
                for item in inputs
            ]

    class FakeAdapter:
        def feed(self, document):
            fed.append(document["fields"]["owner_id"])
            return {"ok": True}

        def delete(self, _document_id):
            return {"ok": True}

    def candidate(index):
        return {
            "vespa_document_id": f"id:flux:flux_evidence::asset_chunks--chunk-{index}",
            "owner_table": "asset_chunks",
            "owner_id": f"chunk-{index}",
            "asset_id": f"asset-{index}",
            "root_id": "33333333-3333-3333-3333-333333333333",
            "root_name": "docs",
            "title": f"Chunk {index}",
            "body": f"body {index}",
            "source_path": f"docs/chunk-{index}.md",
            "symbols": [],
            "language": "",
            "file_kind": "text",
            "lifecycle_state": "active",
            "deleted": False,
            "canonical": True,
            "source_hash": f"old-hash-{index}",
            "index_text": f"Chunk {index}\nbody {index}",
            "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
            "embedding_dimensions": 1024,
            "existing_source_hash": None,
            "existing_index_status": None,
            "existing_embedding_model": None,
        }

    monkeypatch.setenv("FLUX_KB_SEARCH_INDEX_EMBEDDING_BATCH_SIZE", "16")
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_fetch_stale_search_index_records", lambda *_args, **_kwargs: [])
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [candidate(index) for index in range(16)])
    monkeypatch.setattr(database, "_upsert_search_index_record", lambda *_args, **kwargs: records.append(kwargs))

    result = database.sync_search_index(owner_class="corpus", root_name="docs", limit=16, embedding_provider=CapacityProvider(), adapter=FakeAdapter())

    assert batch_calls == [
        [f"chunk-{index}" for index in range(16)],
        [f"chunk-{index}" for index in range(8)],
        [f"chunk-{index}" for index in range(4)],
        [f"chunk-{index}" for index in range(4, 8)],
        [f"chunk-{index}" for index in range(8, 16)],
        [f"chunk-{index}" for index in range(8, 12)],
        [f"chunk-{index}" for index in range(12, 16)],
    ]
    assert fed == [f"chunk-{index}" for index in range(16)]
    assert [item["row"]["owner_id"] for item in records if item["status"] == "syncing"] == [f"chunk-{index}" for index in range(16)]
    assert [item["row"]["owner_id"] for item in records if item["status"] == "indexed"] == [f"chunk-{index}" for index in range(16)]
    assert result["embedding_attempted_size_histogram"] == {"4": 4, "8": 2, "16": 1}
    assert result["embedding_split_count"] == 3
    assert result["embedding_smallest_attempted_size"] == 4
    assert result["embedding_capacity_state"] == "unschedulable"


def test_sync_search_index_blocks_unschedulable_batch_one_without_resetting_unchanged_rows(monkeypatch):
    from flux_llm_kb.gpu_scheduler import GpuLeaseRejected

    batch_calls: list[list[str]] = []
    records: list[dict] = []
    row = {
        "vespa_document_id": "id:flux:flux_evidence::asset_chunks--chunk-1",
        "owner_table": "asset_chunks",
        "owner_id": "chunk-1",
        "asset_id": "asset-1",
        "root_id": "33333333-3333-3333-3333-333333333333",
        "root_name": "docs",
        "title": "Chunk One",
        "body": "body",
        "source_path": "docs/chunk.md",
        "symbols": [],
        "language": "",
        "file_kind": "text",
        "lifecycle_state": "active",
        "deleted": False,
        "canonical": True,
        "source_hash": "source-hash",
        "owner_content_hash": "content-hash",
        "index_text": "Chunk One\nbody",
        "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
        "embedding_dimensions": 1024,
        "existing_source_hash": None,
        "existing_index_status": None,
        "existing_embedding_model": None,
    }

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class CapacityProvider:
        def embed_batch(self, inputs):
            batch_calls.append([item.owner_id for item in inputs])
            if inputs[0].owner_id == "chunk-1":
                error = GpuLeaseRejected("single item exceeds embedding capacity")
                error.capacity_state = "unschedulable"
                raise error
            return [
                EmbeddingResult(
                    owner_table=item.owner_table,
                    owner_id=item.owner_id,
                    model="Snowflake/snowflake-arctic-embed-l-v2.0",
                    dimensions=1024,
                    vector=[1.0] + [0.0] * 1023,
                    metadata={"source_hash": f"hash-{item.owner_id}"},
                )
                for item in inputs
            ]

    class FakeAdapter:
        def feed(self, _document):
            return {"ok": True}

        def delete(self, _document_id):
            return {"ok": True}

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_fetch_stale_search_index_records", lambda *_args, **_kwargs: [])
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [row])
    monkeypatch.setattr(database, "_upsert_search_index_record", lambda *_args, **kwargs: records.append(kwargs))

    result = database.sync_search_index(owner_class="corpus", root_name="docs", limit=1, embedding_provider=CapacityProvider(), adapter=FakeAdapter())

    assert batch_calls == [["chunk-1"]]
    assert result["failed"] == 1
    assert result["non_retryable"] is True
    assert result["non_retryable_blocker"] == "embedding_capacity"
    assert result["embedding_capacity_state"] == "unschedulable"
    assert records[-1]["status"] == "blocked_embedding_capacity"
    assert records[-1]["metadata"] == {
        "failed_stage": "embedding_capacity",
        "capacity_state": "unschedulable",
        "capacity_source_content_hash": "content-hash",
        "retryable": False,
    }

    unchanged = {
        **row,
        "existing_source_hash": "source-hash",
        "existing_index_status": "blocked_embedding_capacity",
        "existing_embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
        "existing_capacity_source_content_hash": "content-hash",
        "existing_last_error": "single item exceeds embedding capacity",
        "search_index_hydrated_body_chars": 0,
        "search_index_truncated_chars": 0,
    }
    batch_calls.clear()
    records.clear()
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [unchanged])

    unchanged_result = database.sync_search_index(owner_class="corpus", root_name="docs", limit=1, embedding_provider=CapacityProvider(), adapter=FakeAdapter())

    assert unchanged_result["skipped_unchanged"] == 1
    assert batch_calls == []
    assert records == []

    legacy = {
        **unchanged,
        "existing_capacity_source_content_hash": " \t ",
    }
    batch_calls.clear()
    records.clear()
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [legacy])

    legacy_result = database.sync_search_index(
        owner_class="corpus", root_name="docs", limit=1, embedding_provider=CapacityProvider(), adapter=FakeAdapter()
    )

    assert legacy_result["skipped_capacity_blockers"] == 1
    assert batch_calls == []
    assert [item["status"] for item in records] == ["blocked_embedding_capacity"]
    assert records[-1]["last_error"] == "single item exceeds embedding capacity"
    assert records[-1]["metadata"] == {"capacity_source_content_hash": "content-hash"}

    raw_content_only = {
        **unchanged,
        "owner_content_hash": "changed-content-hash",
    }
    batch_calls.clear()
    records.clear()
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [raw_content_only])

    raw_content_only_result = database.sync_search_index(
        owner_class="corpus", root_name="docs", limit=1, embedding_provider=CapacityProvider(), adapter=FakeAdapter()
    )

    assert raw_content_only_result["skipped_capacity_blockers"] == 1
    assert batch_calls == []
    assert records[-1]["metadata"] == {"capacity_source_content_hash": "changed-content-hash"}

    missing_current_hash = {
        **unchanged,
        "owner_content_hash": "",
    }
    batch_calls.clear()
    records.clear()
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [missing_current_hash])

    missing_hash_result = database.sync_search_index(
        owner_class="corpus", root_name="docs", limit=1, embedding_provider=CapacityProvider(), adapter=FakeAdapter()
    )

    assert missing_hash_result["skipped_capacity_blockers"] == 1
    assert batch_calls == []
    assert records == []

    later = {
        **row,
        "vespa_document_id": "id:flux:flux_evidence::asset_chunks--chunk-2",
        "owner_id": "chunk-2",
        "asset_id": "asset-2",
        "source_hash": "later-source-hash",
        "index_text": "Chunk Two\nbody",
        "existing_source_hash": None,
        "existing_index_status": None,
        "existing_embedding_model": None,
    }
    batch_calls.clear()
    records.clear()
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [unchanged, later])

    later_result = database.sync_search_index(owner_class="corpus", root_name="docs", limit=2, embedding_provider=CapacityProvider(), adapter=FakeAdapter())

    assert later_result["skipped_unchanged"] == 1
    assert later_result["indexed"] == 1
    assert batch_calls == [["chunk-2"]]
    assert [item["row"]["owner_id"] for item in records] == ["chunk-2", "chunk-2"]

    # Once the legacy blocker has been normalised, repeated LIMIT 1 pages can
    # advance to later pending rows rather than repeatedly consuming it.
    for pending_id in ("chunk-3", "chunk-4"):
        limited_pending = {
            **later,
            "vespa_document_id": f"id:flux:flux_evidence::asset_chunks--{pending_id}",
            "owner_id": pending_id,
            "asset_id": f"asset-{pending_id[-1]}",
            "source_hash": f"{pending_id}-source-hash",
            "index_text": f"{pending_id}\nbody",
        }
        batch_calls.clear()
        records.clear()
        monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, row=limited_pending, **_kwargs: [row])

        limited_page_result = database.sync_search_index(
            owner_class="corpus", root_name="docs", limit=1, embedding_provider=CapacityProvider(), adapter=FakeAdapter()
        )

        assert limited_page_result["indexed"] == 1
        assert batch_calls == [[pending_id]]

    # Metadata-only owner revisions must leave the capacity blocker unchanged so
    # later pending work remains eligible on each cursorless sync page.
    for revision, pending_id in (("2030-01-01T00:00:00Z", "chunk-3"), ("2030-01-02T00:00:00Z", "chunk-4")):
        timestamp_only = {**unchanged, "owner_updated_at": revision}
        pending = {
            **later,
            "vespa_document_id": f"id:flux:flux_evidence::asset_chunks--{pending_id}",
            "owner_id": pending_id,
            "asset_id": f"asset-{pending_id[-1]}",
            "source_hash": f"{pending_id}-source-hash",
            "index_text": f"{pending_id}\nbody",
        }
        batch_calls.clear()
        records.clear()
        monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, rows=[timestamp_only, pending], **_kwargs: rows)

        timestamp_only_result = database.sync_search_index(
            owner_class="corpus", root_name="docs", limit=2, embedding_provider=CapacityProvider(), adapter=FakeAdapter()
        )

        assert timestamp_only_result["skipped_unchanged"] == 1
        assert timestamp_only_result["indexed"] == 1
        assert batch_calls == [[pending_id]]
        assert [item["row"]["owner_id"] for item in records] == [pending_id, pending_id]

    changed = {
        **unchanged,
        "source_hash": "changed-source-hash",
        "index_text": "Chunk One\nchanged body",
    }
    batch_calls.clear()
    records.clear()
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [changed])

    changed_result = database.sync_search_index(owner_class="corpus", root_name="docs", limit=1, embedding_provider=CapacityProvider(), adapter=FakeAdapter())

    assert changed_result["non_retryable"] is True
    assert batch_calls == [["chunk-1"]]
    assert [item["status"] for item in records] == ["syncing", "blocked_embedding_capacity"]

    batch_calls.clear()
    records.clear()
    retry_result = database.sync_search_index(
        owner_class="corpus",
        root_name="docs",
        limit=1,
        retry_capacity_blockers=True,
        embedding_provider=CapacityProvider(),
        adapter=FakeAdapter(),
    )

    assert retry_result["non_retryable"] is True
    assert batch_calls == [["chunk-1"]]


def test_sync_search_index_splits_scheduler_unschedulable_model_runner_responses(monkeypatch):
    from io import BytesIO
    from urllib.error import HTTPError

    from flux_llm_kb.embeddings import SnowflakeEmbeddingProvider
    from flux_llm_kb.gpu_scheduler import GpuLeaseRejected, GpuSchedulerConfig, GpuTaskProfile, InProcessGpuScheduler
    from flux_llm_kb.model_runner import ModelRunnerClient, _gpu_busy_detail

    scheduler = InProcessGpuScheduler(
        GpuSchedulerConfig(vram_budget_mb=1_000, safety_margin_mb=0),
        reconciliation_provider=lambda: None,
    )
    with pytest.raises(GpuLeaseRejected) as exc_info:
        scheduler.acquire(GpuTaskProfile(task_type="embedding", model_id="Snowflake/test", estimated_vram_mb=2_000))
    detail = _gpu_busy_detail(exc_info.value)
    fed: list[str] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class FakeResponse:
        def __init__(self, payload):
            self.payload = payload

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def read(self):
            return json.dumps(self.payload).encode("utf-8")

    class FakeAdapter:
        def feed(self, document):
            fed.append(document["fields"]["owner_id"])
            return {"ok": True}

        def delete(self, _document_id):
            return {"ok": True}

    def fake_urlopen(request, timeout):
        payload = json.loads(request.data.decode("utf-8"))
        text_count = len(payload["texts"])
        if text_count > 4:
            raise HTTPError(
                url=request.full_url,
                code=429,
                msg="Too Many Requests",
                hdrs={},
                fp=BytesIO(json.dumps({"detail": detail}).encode("utf-8")),
            )
        return FakeResponse({"ok": True, "vectors": [[0.0] * 1024 for _ in range(text_count)]})

    def candidate(index):
        return {
            "vespa_document_id": f"id:flux:flux_evidence::asset_chunks--chunk-{index}",
            "owner_table": "asset_chunks",
            "owner_id": f"chunk-{index}",
            "asset_id": f"asset-{index}",
            "root_id": "33333333-3333-3333-3333-333333333333",
            "root_name": "docs",
            "title": f"Chunk {index}",
            "body": f"body {index}",
            "source_path": f"docs/chunk-{index}.md",
            "symbols": [],
            "language": "",
            "file_kind": "text",
            "lifecycle_state": "active",
            "deleted": False,
            "canonical": True,
            "source_hash": f"old-hash-{index}",
            "index_text": f"Chunk {index}\nbody {index}",
            "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
            "embedding_dimensions": 1024,
            "existing_source_hash": None,
            "existing_index_status": None,
            "existing_embedding_model": None,
        }

    monkeypatch.setenv("FLUX_KB_SEARCH_INDEX_EMBEDDING_BATCH_SIZE", "8")
    monkeypatch.setattr("flux_llm_kb.model_runner.urlopen", fake_urlopen)
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_fetch_stale_search_index_records", lambda *_args, **_kwargs: [])
    monkeypatch.setattr(database, "_fetch_search_index_rows", lambda *_args, **_kwargs: [candidate(index) for index in range(8)])
    monkeypatch.setattr(database, "_upsert_search_index_record", lambda *_args, **_kwargs: None)

    result = database.sync_search_index(
        owner_class="corpus",
        root_name="docs",
        limit=8,
        embedding_provider=SnowflakeEmbeddingProvider(model_runner=ModelRunnerClient("http://model-runner:8790")),
        adapter=FakeAdapter(),
    )

    assert result["indexed"] == 8
    assert result["embedding_attempted_size_histogram"] == {"4": 2, "8": 1}
    assert result["embedding_capacity_state"] == "unschedulable"
    assert fed == [f"chunk-{index}" for index in range(8)]


def test_fetch_corpus_search_index_rows_prioritises_content_changed_capacity_blockers():
    executed: list[tuple[str, tuple]] = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return []

    database._fetch_corpus_search_index_rows(
        FakeCursor(),
        root_name="docs",
        limit=1,
        embedding_model="Snowflake/snowflake-arctic-embed-l-v2.0",
        retry_capacity_blockers=False,
    )

    sql, params = executed[0]
    assert "WHEN rec.index_status = 'blocked_embedding_capacity' AND (%s OR (NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS NOT NULL AND NULLIF(a.content_hash, '') IS NOT NULL AND NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS DISTINCT FROM a.content_hash)) THEN 1" in sql
    assert sql.index("WHEN rec.index_status = 'blocked_embedding_capacity' AND") < sql.index("WHEN rec.index_status = 'blocked_embedding_capacity' THEN 4")
    assert "c.updated_at > rec.updated_at" not in sql
    assert params == ("Snowflake/snowflake-arctic-embed-l-v2.0", "docs", False, 1)


def test_fetch_corpus_search_index_rows_prioritises_explicit_capacity_retries():
    executed: list[tuple[str, tuple]] = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return []

    database._fetch_corpus_search_index_rows(
        FakeCursor(),
        root_name="docs",
        limit=1,
        embedding_model="Snowflake/snowflake-arctic-embed-l-v2.0",
        retry_capacity_blockers=True,
    )

    sql, params = executed[0]
    assert "WHEN rec.index_status = 'blocked_embedding_capacity' AND (%s OR (NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS NOT NULL AND NULLIF(a.content_hash, '') IS NOT NULL AND NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS DISTINCT FROM a.content_hash)) THEN 1" in sql
    assert params == ("Snowflake/snowflake-arctic-embed-l-v2.0", "docs", True, 1)


@pytest.mark.parametrize(
    ("fetch_rows", "current_content_hash"),
    [
        (database._fetch_episode_search_index_rows, "s.content_hash"),
        (database._fetch_claim_search_index_rows, "source.content_hash"),
    ],
)
def test_fetch_non_corpus_rows_reactivate_capacity_blockers_only_for_content_changes(fetch_rows, current_content_hash):
    executed: list[tuple[str, tuple]] = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return []

    fetch_rows(
        FakeCursor(),
        root_name="docs",
        limit=1,
        embedding_model="Snowflake/snowflake-arctic-embed-l-v2.0",
    )

    sql, params = executed[0]
    assert (
        "WHEN rec.index_status = 'blocked_embedding_capacity' AND "
        "(%s OR (NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', "
        "'^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS NOT NULL "
        f"AND NULLIF({current_content_hash}, '') IS NOT NULL "
        "AND NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', "
        f"'^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS DISTINCT FROM {current_content_hash})) THEN 1"
    ) in sql
    assert "updated_at > rec.updated_at" not in sql
    assert params == ("Snowflake/snowflake-arctic-embed-l-v2.0", "docs", False, 1)


def test_capacity_blocker_priority_normalises_only_persisted_outer_whitespace():
    later_pending_rank = 1
    whitespace_legacy_rank = database._search_index_capacity_blocker_priority(
        retry_capacity_blockers=False,
        recorded_content_hash="\t\n",
        current_content_hash="current-hash",
    )
    exact_mismatch_rank = database._search_index_capacity_blocker_priority(
        retry_capacity_blockers=False,
        recorded_content_hash="abc",
        current_content_hash=" abc ",
    )

    assert sorted([(whitespace_legacy_rank, "legacy"), (later_pending_rank, "pending")]) == [
        (1, "pending"),
        (4, "legacy"),
    ]
    assert exact_mismatch_rank == 1
    assert database._search_index_capacity_blocker_priority(
        retry_capacity_blockers=True,
        recorded_content_hash="\t\n",
        current_content_hash="current-hash",
    ) == 1


def test_search_index_embedding_batch_size_defaults_to_memory_safe_value(monkeypatch):
    monkeypatch.delenv("FLUX_KB_SEARCH_INDEX_EMBEDDING_BATCH_SIZE", raising=False)

    assert database._search_index_embedding_batch_size() == 16


def test_sync_search_index_processes_one_page_and_reports_continuation(monkeypatch):
    fetch_limits: list[int] = []
    batch_calls: list[list[str]] = []
    fed: list[dict] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    class FakeProvider:
        name = "model_runner"

        def embed_batch(self, inputs):
            batch_calls.append([item.owner_id for item in inputs])
            return [
                EmbeddingResult(
                    owner_table=item.owner_table,
                    owner_id=item.owner_id,
                    model="Snowflake/snowflake-arctic-embed-l-v2.0",
                    dimensions=1024,
                    vector=[1.0] + [0.0] * 1023,
                    metadata={"source_hash": f"hash-{item.owner_id}"},
                )
                for item in inputs
            ]

    class FakeAdapter:
        def feed(self, document):
            fed.append(document)
            return {"ok": True}

        def delete(self, _document_id):
            return {"ok": True}

    def candidate(index):
        return {
            "vespa_document_id": f"id:flux:flux_evidence::asset_chunks--chunk-{index}",
            "owner_table": "asset_chunks",
            "owner_id": f"chunk-{index}",
            "asset_id": f"asset-{index}",
            "root_id": "33333333-3333-3333-3333-333333333333",
            "root_name": "docs",
            "title": f"Chunk {index}",
            "body": f"body {index}",
            "source_path": f"docs/chunk-{index}.md",
            "symbols": [],
            "language": "",
            "file_kind": "text",
            "lifecycle_state": "active",
            "deleted": False,
            "canonical": True,
            "source_hash": f"old-hash-{index}",
            "index_text": f"Chunk {index}\nbody {index}",
            "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
            "embedding_dimensions": 1024,
            "existing_source_hash": None,
            "existing_index_status": None,
            "existing_embedding_model": None,
            "search_index_hydrated_body_chars": len(f"body {index}"),
            "search_index_truncated_chars": 0,
        }

    def fetch_rows(*_args, **kwargs):
        fetch_limits.append(kwargs["limit"])
        return [candidate(index) for index in range(kwargs["limit"])]

    monkeypatch.delenv("FLUX_KB_SEARCH_INDEX_PAGE_SIZE", raising=False)
    monkeypatch.setenv("FLUX_KB_SEARCH_INDEX_EMBEDDING_BATCH_SIZE", "200")
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_fetch_stale_search_index_records", lambda *_args, **_kwargs: [])
    monkeypatch.setattr(database, "_fetch_search_index_rows", fetch_rows)
    monkeypatch.setattr(database, "_upsert_search_index_record", lambda *_args, **_kwargs: None)

    result = database.sync_search_index(owner_class="corpus", root_name="docs", limit=250, embedding_provider=FakeProvider(), adapter=FakeAdapter())

    assert fetch_limits == [100]
    assert result["requested"] == 100
    assert result["indexed"] == 100
    assert result["rows_loaded"] == 100
    assert result["hydrated_body_chars"] == sum(len(f"body {index}") for index in range(100))
    assert result["truncated_body_chars"] == 0
    assert result["more_pending"] is True
    assert result["continuation_remaining"] == 150
    assert result["page_size"] == 100
    assert len(fed) == 100


def test_fetch_corpus_search_index_rows_truncates_indexed_body_text(monkeypatch):
    long_body = "abcdefghijklmnopqrstuvwxyz"
    executed: list[tuple[str, object]] = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return [
                (
                    "chunk-1",
                    "asset-1",
                    "root-1",
                    "docs",
                    "Chunk One",
                    long_body,
                    "docs/chunk.md",
                    "text",
                    {},
                    None,
                    None,
                    None,
                    None,
                    None,
                    None,
                )
            ]

    monkeypatch.setenv("FLUX_KB_SEARCH_INDEX_TEXT_MAX_CHARS", "12")

    rows = database._fetch_corpus_search_index_rows(
        FakeCursor(),
        root_name="docs",
        limit=1,
        embedding_model="Snowflake/snowflake-arctic-embed-l-v2.0",
    )

    assert rows[0]["body"] == "abcdefghijkl"
    assert "mnopqrstuvwxyz" not in rows[0]["index_text"]
    assert rows[0]["search_index_hydrated_body_chars"] == len(long_body)
    assert rows[0]["search_index_truncated_chars"] == len(long_body) - 12


def test_delete_search_index_records_for_root_requires_root_and_filters_statuses(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 29

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    with pytest.raises(ValueError, match="root_name is required"):
        database._delete_search_index_records_for_root(root_name=" ")

    deleted = database._delete_search_index_records_for_root(
        root_name="__retrieval_benchmark_deadbeef",
        statuses=["deleted", ""],
    )

    sql, params = executed[0]
    assert deleted == 29
    assert "DELETE FROM search_index_records" in sql
    assert "root_name = %s" in sql
    assert "index_status = ANY(%s::text[])" in sql
    assert params == ("__retrieval_benchmark_deadbeef", ["deleted"])


def test_delete_semantic_duplicate_clusters_for_root_requires_root(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 3

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    with pytest.raises(ValueError, match="root_name is required"):
        database._delete_semantic_duplicate_clusters_for_root(root_name="")

    deleted = database._delete_semantic_duplicate_clusters_for_root(root_name="__retrieval_benchmark_deadbeef")

    sql, params = executed[0]
    assert deleted == 3
    assert "DELETE FROM semantic_duplicate_clusters" in sql
    assert "root_name = %s" in sql
    assert params == ("__retrieval_benchmark_deadbeef",)


def test_delete_managed_mail_sidecars_for_root_deletes_distinct_refs(monkeypatch):
    executed = []
    refs = [
        {
            "source": "managed_mail",
            "storage": "disk_sidecar",
            "relative_path": "mail_content/aa/one.json",
        },
        {
            "source": "managed_mail",
            "storage": "disk_sidecar",
            "relative_path": "mail_content/aa/one.json",
        },
        {
            "source": "managed_mail",
            "storage": "disk_sidecar",
            "relative_path": "mail_content/bb/missing.json",
        },
    ]
    deleted_refs = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return [(ref,) for ref in refs]

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    def fake_delete_mail_content(ref):
        deleted_refs.append(ref["relative_path"])
        if ref["relative_path"].endswith("missing.json"):
            return {"status": "missing", "relative_path": ref["relative_path"]}
        return {"status": "deleted", "relative_path": ref["relative_path"]}

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database.mail_content_store, "delete_mail_content", fake_delete_mail_content)

    result = database.delete_managed_mail_sidecars_for_root(root_name="mail-gmail-capture")

    sql, params = executed[0]
    assert result["deleted"] == 1
    assert result["missing"] == 1
    assert result["blocked"] == 0
    assert deleted_refs == ["mail_content/aa/one.json", "mail_content/bb/missing.json"]
    assert "JOIN source_assets" in sql
    assert "JOIN monitored_roots" in sql
    assert "root_name = %s" in sql
    assert "sidecar_ref" in sql
    assert params == ("mail-gmail-capture",)


def test_delete_mail_profile_removes_profile_and_audits(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, name" in sql and "FROM mail_profiles" in sql:
                return ("profile-1", "gmail-capture")
            if "DELETE FROM mail_profiles" in sql:
                return ("profile-1", "gmail-capture")
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.delete_mail_profile(name="gmail-capture", actor="tester")

    sql = "\n".join(item[0] for item in executed)
    assert result == {"id": "profile-1", "name": "gmail-capture", "deleted": True}
    assert "DELETE FROM mail_profiles" in sql
    assert "mail_profile.deleted" in sql
    assert executed[0][1] == ("gmail-capture",)


def test_corpus_search_index_rows_hydrate_managed_mail_sidecar(monkeypatch):
    class FakeCursor:
        def execute(self, _sql, _params=()):
            return None

        def fetchall(self):
            return [
                (
                    "chunk-mail",
                    "asset-mail",
                    "33333333-3333-3333-3333-333333333333",
                    "docs",
                    "body.txt",
                    "",
                    "mail/body.txt",
                    "mail",
                    {
                        "sidecar_ref": {
                            "source": "managed_mail",
                            "storage": "disk_sidecar",
                            "sha256": "abc123",
                            "relative_path": "mail/chunks/abc123.json",
                            "redacted_from_db": True,
                        }
                    },
                    None,
                    None,
                    None,
                    None,
                    None,
                    None,
                ),
                (
                    "chunk-file",
                    "asset-file",
                    "33333333-3333-3333-3333-333333333333",
                    "docs",
                    "plan.txt",
                    "Plain file body",
                    "docs/plan.txt",
                    "text",
                    {},
                    None,
                    "old-hash",
                    "indexed",
                    "Snowflake/snowflake-arctic-embed-l-v2.0",
                    None,
                    None,
                ),
            ]

    monkeypatch.setattr(database.mail_content_store, "read_mail_content", lambda _ref: "Private mail body")

    rows = database._fetch_corpus_search_index_rows(
        FakeCursor(),
        root_name=None,
        limit=10,
        embedding_model="Snowflake/snowflake-arctic-embed-l-v2.0",
    )

    assert rows[0]["owner_id"] == "chunk-mail"
    assert rows[0]["index_text"] == "body.txt\nmail/body.txt\nPrivate mail body"
    assert rows[1]["owner_id"] == "chunk-file"
    assert rows[1]["index_text"] == "plan.txt\ndocs/plan.txt\nPlain file body"


def test_search_managed_mail_sidecar_rows_hydrates_private_text(monkeypatch):
    class FakeCursor:
        def execute(self, _sql, _params=()):
            return None

        def fetchall(self):
            return [
                (
                    "chunk-mail",
                    "asset-mail",
                    "body.txt",
                    "",
                    "export-1/body.txt",
                    0,
                    450,
                    "mail-gmail",
                    "text",
                    {
                        "sidecar_ref": {
                            "source": "managed_mail",
                            "storage": "disk_sidecar",
                            "sha256": "abc123",
                            "relative_path": "mail/chunks/abc123.json",
                            "redacted_from_db": True,
                        }
                    },
                )
            ]

    monkeypatch.setattr(database.mail_content_store, "read_mail_content", lambda _ref: "Customer RFP private body")

    rows = database._search_mail_sidecar_rows(FakeCursor(), query="customer rfp", root_name=None, filters=None, limit=5)

    assert rows[0][0] == "chunk-mail"
    assert rows[0][3] == "Customer RFP private body"
    assert rows[0][4] == "export-1/body.txt"
    assert rows[0][8] > 0


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
    assert "DELETE FROM embeddings" not in sql
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


def test_persist_crawl_plan_marks_unseen_assets_deleted_and_cancels_jobs(monkeypatch, tmp_path):
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
            sql = executed[-1][0]
            if "SELECT id::text FROM monitored_roots" in sql:
                return ("root-1",)
            if "INSERT INTO crawl_runs" in sql:
                return ("run-1",)
            if "SELECT count(*) FROM cancelled" in sql:
                return (2,)
            return None

        def fetchall(self):
            sql = executed[-1][0]
            if "RETURNING path" in sql:
                return [("old/secret.pdf",)]
            return []

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

    plan = CrawlPlan(root_path=tmp_path, assets=[], deferred_jobs=[], errors=[])
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.persist_crawl_plan(root_name="docs", plan=plan)

    sql = "\n".join(statement for statement, _params in executed)
    assert result["files_deleted"] == 1
    assert "extraction_status = 'deleted'" in sql
    assert "unseen_reason" in sql
    assert "unseen_since" in sql
    assert "purge_after" in sql
    assert "cancelled_unseen_asset" in sql
    assert "status IN ('pending', 'retrying_locked', 'retrying_vss_failed', 'running')" in sql
    assert "locked_at = NULL" in sql
    assert "locked_by = NULL" in sql
    assert "previous_status" in sql
    assert "RETURNING job.id::text" not in sql
    assert "RETURNING job.id, candidates.status AS previous_status" in sql
    assert any(params and "old/secret.pdf" in str(params) for _statement, params in executed)


def test_cancel_unseen_corpus_job_audit_target_id_keeps_uuid_type(monkeypatch):
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

    database.cancel_unseen_corpus_job(job_id="job-1", error="asset disappeared")

    sql = "\n".join(statement for statement, _params in executed)
    assert "INSERT INTO audit_events (event_type, target_table, target_id, details)" in sql
    assert "RETURNING id::text" not in sql
    assert "RETURNING id" in sql


def test_purge_unseen_corpus_assets_removes_index_rows_after_grace(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return (3,)

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

    result = database.purge_unseen_corpus_assets(root_name="docs", grace_seconds=86400, batch_size=500)

    sql = "\n".join(statement for statement, _params in executed)
    assert result == {"root_name": "docs", "assets_purged": 3}
    assert "metadata ? 'unseen_reason'" in sql
    assert "make_interval(secs => %s)" in sql
    assert "LIMIT %s" in sql
    assert "DELETE FROM embeddings" not in sql
    assert "DELETE FROM code_symbols" in sql
    assert "DELETE FROM code_references" in sql
    assert "DELETE FROM asset_chunks" in sql
    assert "DELETE FROM crawl_path_manifests" in sql
    assert "DELETE FROM source_assets" in sql
    assert "unlink" not in sql.lower()
    assert "remove(" not in sql.lower()


def test_repair_extracted_corpus_asset_statuses_marks_chunked_queued_assets_indexed(monkeypatch):
    executed = []

    class FakeCursor:
        count = 0

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            self.count += 1
            return ({1: 7, 2: 2, 3: 3, 4: 4}[self.count],)

        def fetchall(self):
            return []

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
    assert result == {
        "root_name": "watch-test",
        "repaired": 7,
        "internal_mail_artifacts_deleted": 2,
        "chunks_purged": 3,
        "mail_plaintext_chunks_repaired": 0,
    }
    assert "EXISTS" in sql
    assert "asset_chunks" in sql
    assert "extraction_status = 'queued'" in sql
    assert "a.extraction_status NOT IN ('indexed', 'processing_staged')" in sql
    assert "a.extraction_status <> 'indexed'" not in sql
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
    assert "AND status = 'running'" in sql


def test_cancel_orphaned_corpus_job_records_terminal_state(monkeypatch):
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

    database.cancel_orphaned_corpus_job(
        job_id="job-1",
        error="monitored root not found: smoke",
        duration_ms=12,
        telemetry={"result_status": "cancelled_orphaned_root"},
    )

    sql = "\n".join(item[0] for item in executed)
    assert "status = 'cancelled_orphaned_root'" in sql
    assert "completed_at = now()" in sql
    assert "locked_at = NULL" in sql
    assert "AND status = 'running'" in sql
    assert executed[0][1] == (
        "monitored root not found: smoke",
        12,
        '{"result_status": "cancelled_orphaned_root"}',
        "job-1",
    )


def test_cancel_missing_source_corpus_job_records_terminal_state_and_marks_asset_deleted(monkeypatch):
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

    database.cancel_missing_source_corpus_job(
        job_id="job-1",
        root_name="mail-outlook-mohesr",
        relative_path="missing/attachment.docx",
        error="source file not found: missing/attachment.docx",
        duration_ms=12,
        telemetry={"result_status": "cancelled_missing_source"},
    )

    sql = "\n".join(item[0] for item in executed)
    assert "status = 'cancelled_missing_source'" in sql
    assert "UPDATE source_assets" in sql
    assert "extraction_status = 'deleted'" in sql
    assert "AND status = 'running'" in sql
    assert executed[0][1] == (
        "source file not found: missing/attachment.docx",
        12,
        '{"result_status": "cancelled_missing_source"}',
        "job-1",
    )
    assert json.loads(executed[1][1][0])["missing_source_deleted"] is True


def test_block_corpus_job_only_updates_running_jobs(monkeypatch):
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

    database.block_corpus_job(
        job_id="job-1",
        error="extract failed",
        status="failed",
        telemetry={"result_status": "failed"},
    )

    sql = "\n".join(statement for statement, _params in executed)
    assert "status = %s" in sql
    assert "AND status = 'running'" in sql
    assert executed[0][1][0] == "failed"


def test_requeue_corpus_job_resets_terminal_state_for_operator_retry(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return ("job-1", "pending", 0)

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

    result = database.requeue_corpus_job(job_id="job-1", reason="operator retry")

    sql = "\n".join(item[0] for item in executed)
    assert result == {"job_id": "job-1", "status": "pending", "attempts": 0}
    assert "status = 'pending'" in sql
    assert "attempts = 0" in sql
    assert "next_attempt_at = now()" in sql
    assert "last_error = NULL" in sql
    assert "locked_at = NULL" in sql
    assert "broker_message_id = NULL" in sql
    assert "routing_key = NULL" in sql
    assert "correlation_id = NULL" in sql
    assert "causation_id = NULL" in sql
    assert "queued_at = NULL" in sql
    assert "broker_delivery_count = 0" in sql
    assert "jsonb_build_object('remediation_reason', %s::text)" in sql
    assert "- 'gpu_busy_first_seen_at'" in sql
    assert "- 'gpu_busy_retry_count'" in sql
    assert "- 'gpu_busy_next_cooldown_seconds'" in sql
    assert "- 'gpu_busy_block_after_seconds'" in sql
    assert "- 'gpu_busy_blocked_at'" in sql
    assert "status = 'failed'" in sql
    assert "status LIKE 'blocked_%%'" in sql
    assert "status = 'retrying_locked'" in sql
    assert "status = 'retrying_vss_failed'" in sql
    assert "status LIKE 'cancelled_%%'" in sql
    assert "AND (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')" in sql
    assert "delete_requested_at IS NULL" in sql
    assert "delete_requested_at = NULL" not in sql
    assert "delete_requested_by = NULL" not in sql
    assert "delete_reason = NULL" not in sql
    assert executed[0][1] == ("operator retry", "job-1")


def test_enqueue_capture_job_command_by_id_routes_host_agent_job(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT capture_jobs.id::text" in sql:
                return (
                    "job-host",
                    "corpus_extract_image",
                    {"root_name": "host-docs", "path": "logo.png"},
                    "image",
                    "gpu",
                    "pending",
                    True,
                )
            if "SELECT id::text, status, broker_message_id, routing_key" in sql:
                return ("job-host", "pending", None, None)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-1", "message-1", "pending")
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.enqueue_capture_job_command_by_id(job_id="job-host", force_new_message=True)

    outbox_params = next(params for statement, params in executed if "INSERT INTO message_outbox" in statement)
    payload = json.loads(outbox_params[9])
    assert result == {
        "job_id": "job-host",
        "status": "pending",
        "message_id": "message-1",
        "routing_key": "corpus.host_agent.process",
        "queued": True,
        "deduped": False,
    }
    assert outbox_params[1] == "flux.commands"
    assert outbox_params[2] == "corpus.host_agent.process"
    assert outbox_params[3] == "flux.corpus.host_agent.process"
    assert payload["payload"]["job_id"] == "job-host"
    assert payload["payload"]["resource_class"] == "gpu"


def test_enqueue_capture_job_command_ignores_stale_broker_marker_without_active_outbox():
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status, broker_message_id, routing_key" in sql:
                return ("job-host", "pending", "message-stale", "corpus.host_agent.process")
            if "SELECT EXISTS" in sql and "FROM message_outbox" in sql:
                return (False,)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-new", "message-new", "pending")
            return None

    outbox = database._enqueue_capture_job_command_with_cursor(
        FakeCursor(),
        job_id="job-host",
        job_type="corpus_extract_text",
        payload={"root_name": "host-docs", "path": "safe.md"},
        job_family="text",
        resource_class="cpu",
        host_agent_route=True,
    )

    assert outbox == {
        "id": "outbox-new",
        "message_id": "message-new",
        "status": "pending",
        "deduped": False,
        "routing_key": "corpus.host_agent.process",
    }
    assert any("INSERT INTO message_outbox" in statement for statement, _params in executed)


def test_retry_corpus_job_ignores_marked_jobs(monkeypatch):
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

    database.retry_corpus_job(job_id="job-1", error="temporary failure", telemetry={"stage": "extract"})

    sql, params = executed[0]
    assert "AND status = 'running'" in sql
    assert "AND delete_requested_at IS NULL" in sql
    assert params[5] == "job-1"


def test_list_capture_jobs_applies_filters_paging_and_updated_range(monkeypatch):
    executed = []
    timestamp = datetime(2026, 6, 25, 12, 30, tzinfo=timezone.utc)

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
                    "job-1",
                    "corpus_extract_pdf",
                    "office",
                    "cpu",
                    70,
                    300,
                    "failed",
                    {"root_name": "docs", "path": "docs/open.pdf"},
                    3,
                    "extract failed",
                    timestamp,
                    timestamp,
                    None,
                    None,
                    None,
                    {"stage": "extract"},
                    None,
                    None,
                    None,
                    timestamp,
                    "dashboard",
                    "operator_cleanup",
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

    jobs = database.list_capture_jobs(
        limit=25,
        offset=50,
        status=["failed", "retrying_locked"],
        root_name=["docs", "mail"],
        job_type=["corpus_extract_pdf", "corpus_sync_root"],
        updated_from="2026-06-25T00:00:00+00:00",
        updated_to="2026-06-26T00:00:00+00:00",
        sort_by="target",
        sort_dir="asc",
    )

    sql, params = executed[0]
    assert jobs[0]["id"] == "job-1"
    assert jobs[0]["delete_requested_at"] == timestamp.isoformat()
    assert jobs[0]["delete_requested_by"] == "dashboard"
    assert jobs[0]["delete_reason"] == "operator_cleanup"
    assert "status = ANY(%s::text[])" in sql
    assert "payload->>'root_name' = ANY(%s::text[])" in sql
    assert "job_type = ANY(%s::text[])" in sql
    assert "updated_at >= %s" in sql
    assert "updated_at <= %s" in sql
    assert "ORDER BY COALESCE(payload->>'path', payload->>'canonical_path', payload->>'file_path', payload->>'profile_name', '') ASC, id ASC" in sql
    assert "LIMIT %s" in sql
    assert "OFFSET %s" in sql
    assert params == (
        ["failed", "retrying_locked"],
        ["docs", "mail"],
        ["corpus_extract_pdf", "corpus_sync_root"],
        "2026-06-25T00:00:00+00:00",
        "2026-06-26T00:00:00+00:00",
        25,
        50,
    )


def test_list_capture_jobs_falls_back_to_default_sort_for_invalid_values(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return []

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

    database.list_capture_jobs(sort_by="updated_at; DROP TABLE capture_jobs", sort_dir="sideways")

    sql, params = executed[0]
    assert "DROP TABLE" not in sql
    assert "ORDER BY updated_at DESC, id DESC" in sql
    assert params == (50, 0)


def test_list_capture_jobs_progress_sort_prefers_terminal_result_over_stale_stage(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return []

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

    database.list_capture_jobs(sort_by="progress", sort_dir="asc")

    sql, params = executed[0]
    assert "WHEN status = 'completed'" in sql
    assert "WHEN status = 'obsolete'" in sql
    assert "telemetry->>'result_status'" in sql
    assert "telemetry->>'stage'" in sql
    assert "ORDER BY CASE" in sql
    assert params == (50, 0)


def test_count_capture_jobs_reuses_job_history_filters(monkeypatch):
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

    count = database.count_capture_jobs(
        status=["blocked_missing_dependency", "failed"],
        root_name=["docs"],
        job_type=["corpus_sync_root"],
        updated_from="2026-06-20T00:00:00+00:00",
        updated_to="2026-06-27T00:00:00+00:00",
    )

    sql, params = executed[0]
    assert count == 7
    assert "SELECT count(*)" in sql
    assert "status = ANY(%s::text[])" in sql
    assert "payload->>'root_name' = ANY(%s::text[])" in sql
    assert "job_type = ANY(%s::text[])" in sql
    assert "updated_at >= %s" in sql
    assert "updated_at <= %s" in sql
    assert params == (
        ["blocked_missing_dependency", "failed"],
        ["docs"],
        ["corpus_sync_root"],
        "2026-06-20T00:00:00+00:00",
        "2026-06-27T00:00:00+00:00",
    )


def test_list_capture_jobs_ignores_empty_multi_value_filters(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return []

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

    jobs = database.list_capture_jobs(status=[], root_name=[], job_type=[], limit=50, offset=0)

    sql, params = executed[0]
    assert jobs == []
    assert "status = ANY" not in sql
    assert "payload->>'root_name' = ANY" not in sql
    assert "job_type = ANY" not in sql
    assert params == (50, 0)


def test_capture_job_filter_options_lists_distinct_status_roots_and_types(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            sql = executed[-1][0]
            if "SELECT DISTINCT status" in sql:
                return [("failed",), ("retrying_locked",)]
            if "payload->>'root_name'" in sql:
                return [("docs",), ("mail",)]
            if "SELECT DISTINCT job_type" in sql:
                return [("corpus_extract_pdf",), ("corpus_sync_root",)]
            return []

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

    options = database.capture_job_filter_options()

    assert options == {
        "statuses": ["failed", "retrying_locked"],
        "roots": ["docs", "mail"],
        "job_types": ["corpus_extract_pdf", "corpus_sync_root"],
    }


def test_capture_job_tool_invocation_helpers_insert_update_list_and_complete(monkeypatch):
    executed = []
    timestamp = datetime(2026, 6, 30, 19, 45, tzinfo=timezone.utc)

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return ("inv-1", timestamp)

        def fetchall(self):
            return [
                (
                    "inv-1",
                    "job-1",
                    ["python", "-c", "print('hello')"],
                    "E:/LLM KB",
                    "completed",
                    0,
                    "hello\n",
                    "",
                    None,
                    None,
                    timestamp,
                    timestamp,
                    23,
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

    created = database.start_capture_job_tool_invocation(
        job_id="job-1",
        command=["python", "-c", "print('hello')"],
        cwd="E:/LLM KB",
    )
    database.update_capture_job_tool_invocation_output(invocation_id="inv-1", stdout="hello\n", stderr="")
    database.complete_capture_job_tool_invocation(
        invocation_id="inv-1",
        status="completed",
        return_code=0,
        stdout="hello\n",
        stderr="",
        duration_ms=23,
    )
    rows = database.list_capture_job_tool_invocations(job_id="job-1", limit=10)

    assert created == {"id": "inv-1", "started_at": timestamp.isoformat()}
    assert rows[0]["id"] == "inv-1"
    assert rows[0]["command"] == ["python", "-c", "print('hello')"]
    assert rows[0]["stdout"] == "hello\n"
    assert rows[0]["return_code"] == 0
    assert "INSERT INTO capture_job_tool_invocations" in executed[0][0]
    assert "RETURNING id::text, started_at" in executed[0][0]
    assert executed[0][1][0] == "job-1"
    assert executed[0][1][2] == "E:/LLM KB"
    assert "SET stdout = %s" in executed[1][0]
    assert executed[1][1] == ("hello\n", "", "inv-1")
    assert "completed_at = now()" in executed[2][0]
    assert "duration_ms = %s" in executed[2][0]
    assert executed[2][1] == ("completed", 0, "hello\n", "", None, None, 23, "inv-1")
    assert "ORDER BY started_at, id" in executed[3][0]
    assert executed[3][1] == ("job-1", 10)


def test_purge_expired_capture_job_tool_invocations_deletes_only_completed_jobs_after_retention(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 4

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

    result = database.purge_expired_capture_job_tool_invocations(retention_hours=24)

    sql, params = executed[0]
    assert result == {"purged": 4, "retention_hours": 24}
    assert "DELETE FROM capture_job_tool_invocations inv" in sql
    assert "USING capture_jobs job" in sql
    assert "job.status = 'completed'" in sql
    assert "job.completed_at < now() - make_interval(hours => %s)" in sql
    assert params == (24,)


def test_purge_expired_capture_jobs_deletes_completed_and_obsolete_marked_jobs(monkeypatch):
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

    result = database.purge_expired_capture_jobs(retention_days=7)

    sql, params = executed[0]
    assert result == {"purged": 5, "retention_days": 7}
    assert "DELETE FROM capture_jobs job" in sql
    assert "job.job_type LIKE 'corpus_%%'" in sql
    assert "job.status = 'completed'" in sql
    assert "job.delete_requested_at IS NOT NULL" in sql
    assert "job.status = 'obsolete'" in sql
    assert "COALESCE(job.completed_at, job.updated_at) < now() - make_interval(days => %s)" in sql
    assert "job.status IN ('pending', 'running', 'retrying_locked', 'retrying_vss_failed')" not in sql
    assert params == (7, 7)


def test_mark_capture_job_for_deletion_marks_terminal_jobs_and_audits(monkeypatch):
    executed = []
    rows = [
        ("job-1", "blocked_missing_dependency", None, None, None),
        ("job-1", "obsolete", "2026-07-01T09:00:00+00:00", "dashboard", "operator_cleanup"),
    ]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return rows.pop(0) if rows else None

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

    result = database.mark_capture_job_for_deletion(job_id="job-1", actor="dashboard", reason="operator_cleanup")

    sql = "\n".join(item[0] for item in executed)
    assert result == {
        "job_id": "job-1",
        "status": "obsolete",
        "delete_requested": True,
        "delete_requested_at": "2026-07-01T09:00:00+00:00",
        "delete_requested_by": "dashboard",
        "delete_reason": "operator_cleanup",
    }
    assert "SELECT id::text, status, delete_requested_at" in sql
    assert "UPDATE capture_jobs" in sql
    assert "status = 'obsolete'" in sql
    assert "delete_requested_at = COALESCE(delete_requested_at, now())" in sql
    assert "delete_requested_by = COALESCE(delete_requested_by, %s)" in sql
    assert "obsolete_previous_status" in sql
    assert "obsolete_previous_result_status" in sql
    assert "result_status" in sql
    assert "VALUES ('capture_job.deletion_requested', 'capture_jobs', %s, %s::jsonb)" in sql
    assert "updated_at = now()" in executed[1][0]
    assert executed[1][1] == ("dashboard", "operator_cleanup", "job-1")
    audit_details = json.loads(executed[2][1][1])
    assert audit_details["previous_status"] == "blocked_missing_dependency"
    assert audit_details["status"] == "obsolete"


def test_mark_capture_job_for_deletion_is_idempotent_for_already_marked_jobs(monkeypatch):
    executed = []
    rows = [("job-1", "obsolete", "2026-07-01T09:00:00+00:00", "dashboard", "operator_cleanup")]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return rows.pop(0) if rows else None

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

    result = database.mark_capture_job_for_deletion(job_id="job-1", actor="dashboard")

    assert result == {
        "job_id": "job-1",
        "status": "obsolete",
        "delete_requested": True,
        "delete_requested_at": "2026-07-01T09:00:00+00:00",
        "delete_requested_by": "dashboard",
        "delete_reason": "operator_cleanup",
    }
    assert len(executed) == 1
    assert "SELECT id::text, status, delete_requested_at" in executed[0][0]


def test_restore_capture_job_deletion_request_restores_previous_status_and_audits(monkeypatch):
    executed = []
    rows = [
        (
            "job-1",
            "obsolete",
            "2026-07-01T09:00:00+00:00",
            "dashboard",
            "operator_cleanup",
            {
                "obsolete_previous_status": "blocked_invalid_source",
                "obsolete_previous_result_status": "metadata_only",
                "result_status": "obsolete",
                "stage": "extract",
            },
        ),
        ("job-1", "blocked_invalid_source", None, None, None),
    ]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return rows.pop(0) if rows else None

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

    result = database.restore_capture_job_deletion_request(job_id="job-1", actor="dashboard")

    sql = "\n".join(item[0] for item in executed)
    assert result == {
        "job_id": "job-1",
        "status": "blocked_invalid_source",
        "delete_requested": False,
        "delete_requested_at": None,
        "delete_requested_by": None,
        "delete_reason": None,
    }
    assert "SELECT id::text, status, delete_requested_at, delete_requested_by, delete_reason, telemetry" in sql
    assert "status = %s" in sql
    assert "delete_requested_at = NULL" in sql
    assert "obsolete_previous_status" in sql
    assert "obsolete_previous_result_status" in sql
    assert "capture_job.deletion_restored" in sql
    assert executed[1][1] == ("blocked_invalid_source", "metadata_only", "metadata_only", "job-1")
    audit_details = json.loads(executed[2][1][1])
    assert audit_details["actor"] == "dashboard"
    assert audit_details["previous_status"] == "obsolete"
    assert audit_details["restored_status"] == "blocked_invalid_source"


def test_restore_capture_job_deletion_request_handles_missing_previous_result_status(monkeypatch):
    executed = []
    rows = [
        (
            "job-1",
            "obsolete",
            "2026-07-01T09:00:00+00:00",
            "dashboard",
            "operator_cleanup",
            {
                "obsolete_previous_status": "blocked_invalid_source",
                "result_status": "obsolete",
            },
        ),
        ("job-1", "blocked_invalid_source", None, None, None),
    ]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return rows.pop(0) if rows else None

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

    result = database.restore_capture_job_deletion_request(job_id="job-1", actor="dashboard")

    assert result["status"] == "blocked_invalid_source"
    assert result["delete_requested"] is False
    assert "WHEN %s::text IS NULL" in executed[1][0]
    assert executed[1][1] == ("blocked_invalid_source", None, None, "job-1")


def test_restore_capture_job_deletion_request_rejects_unmarked_or_missing_jobs(monkeypatch):
    executed = []
    rows = [("job-1", "failed", None, None, None, {"result_status": "failed"}), None]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return rows.pop(0) if rows else None

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

    conflict = database.restore_capture_job_deletion_request(job_id="job-1", actor="dashboard")
    missing = database.restore_capture_job_deletion_request(job_id="missing", actor="dashboard")

    assert conflict["job_id"] == "job-1"
    assert conflict["status"] == "failed"
    assert conflict["delete_requested"] is False
    assert "cannot be restored" in conflict["error"]
    assert missing == {
        "job_id": "missing",
        "status": "not_found",
        "delete_requested": False,
        "error": "corpus job not found: missing",
    }
    assert len(executed) == 2


def test_mark_capture_job_for_deletion_rejects_active_jobs(monkeypatch):
    executed = []
    rows = [("job-running", "running", None, None, None)]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return rows.pop(0) if rows else None

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

    result = database.mark_capture_job_for_deletion(job_id="job-running", actor="dashboard")

    assert result["job_id"] == "job-running"
    assert result["status"] == "running"
    assert result["delete_requested"] is False
    assert "cannot be marked for deletion" in result["error"]
    assert len(executed) == 1


def test_requeue_corpus_job_allows_cancelled_states_for_operator_retry(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return ("job-1", "pending", 0)

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

    result = database.requeue_corpus_job(job_id="job-1", reason="operator retry")

    sql = executed[0][0]
    assert result == {"job_id": "job-1", "status": "pending", "attempts": 0}
    assert "status = 'failed'" in sql
    assert "status LIKE 'blocked_%%'" in sql
    assert "status = 'retrying_locked'" in sql
    assert "status = 'retrying_vss_failed'" in sql
    assert "status LIKE 'cancelled_%%'" in sql
    assert "delete_requested_at IS NULL" in sql
    assert "delete_requested_at = NULL" not in sql
    assert "delete_requested_by = NULL" not in sql
    assert "delete_reason = NULL" not in sql


def test_cancel_corpus_job_marks_pending_jobs_cancelled(monkeypatch):
    executed = []
    rows = [("job-1", "pending")]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return rows.pop(0) if rows else None

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

    result = database.cancel_corpus_job(job_id="job-1", actor="dashboard")

    sql = "\n".join(item[0] for item in executed)
    assert result == {"job_id": "job-1", "status": "cancelled_operator", "cancelled": True}
    assert "status = 'cancelled_operator'" in sql
    assert "status IN ('pending', 'retrying_locked', 'retrying_vss_failed')" in sql
    assert executed[1][1] == ("cancelled by dashboard", "job-1")


def test_cancel_corpus_job_blocks_running_jobs_with_explicit_error(monkeypatch):
    executed = []
    rows = [("job-running", "running")]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return rows.pop(0) if rows else None

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

    result = database.cancel_corpus_job(job_id="job-running", actor="dashboard")

    assert result["job_id"] == "job-running"
    assert result["status"] == "running"
    assert result["cancelled"] is False
    assert "cannot be cancelled mid-execution" in result["error"]
    assert len(executed) == 1


def test_corpus_sync_root_uses_operator_priority_and_long_budget():
    schedule = database._job_schedule_metadata("corpus_sync_root")

    assert schedule["job_family"] == "general"
    assert schedule["resource_class"] == "cpu"
    assert schedule["priority"] > database.default_priority_for_family("text")
    assert schedule["time_budget_seconds"] >= 900


def test_enqueue_corpus_sync_job_uses_operator_schedule(monkeypatch):
    executed = []
    schedule = database._job_schedule_metadata("corpus_sync_root")

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status" in sql:
                return None
            if "INSERT INTO capture_jobs" in sql:
                return ("job-new", "pending")
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

    result = database.enqueue_corpus_sync_job(root_name="mail-outlook-mohesr", reason="manual_sync")

    insert_params = next(params for statement, params in executed if "INSERT INTO capture_jobs" in statement)
    assert result == {
        "job_id": "job-new",
        "status": "pending",
        "root_name": "mail-outlook-mohesr",
        "deduped": False,
        "reused": False,
    }
    assert insert_params[7] == schedule["priority"]
    assert insert_params[8] == schedule["time_budget_seconds"]
    assert any("INSERT INTO message_outbox" in statement for statement, _params in executed)
    assert any("routing_key = %s" in statement and "broker_message_id" in statement for statement, _params in executed)


def test_enqueue_unique_capture_job_writes_broker_command_to_outbox(monkeypatch):
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status" in sql:
                return None
            if "INSERT INTO capture_jobs" in sql:
                return ("job-new", "pending")
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-1", "message-1", "pending")
            return None

    result = database._enqueue_unique_capture_job_with_cursor(
        FakeCursor(),
        job_type="corpus_extract_text",
        payload={"root_name": "docs", "path": "safe.md", "reason": "test"},
        job_family="text",
        resource_class="cpu",
        telemetry={"stage": "queued"},
    )

    outbox_params = next(params for statement, params in executed if "INSERT INTO message_outbox" in statement)
    payload = json.loads(outbox_params[9])
    metadata_update = next(params for statement, params in executed if "routing_key = %s" in statement)

    assert result["job_id"] == "job-new"
    assert outbox_params[1] == "flux.commands"
    assert outbox_params[2] == "corpus.process"
    assert outbox_params[3] == "flux.corpus.process"
    assert payload["payload"] == {
        "job_id": "job-new",
        "job_type": "corpus_extract_text",
        "job_family": "text",
        "resource_class": "cpu",
        "root_name": "docs",
        "path": "safe.md",
        "reason": "test",
    }
    assert "raw_content" not in json.dumps(payload)
    assert metadata_update[0] == "corpus.process"
    assert metadata_update[2] == "message-1"


def test_reusing_terminal_capture_job_clears_stale_broker_state_and_forces_new_command():
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "identity_key = capture_job_identity" in sql:
                return ("job-old", "completed")
            if "UPDATE capture_jobs" in sql and "RETURNING id::text, status" in sql:
                return ("job-old", "pending")
            if "SELECT EXISTS" in sql and "FROM monitored_roots" in sql:
                return (False,)
            if "SELECT id::text, status, broker_message_id, routing_key" in sql:
                return ("job-old", "pending", "stale-message", "corpus.process")
            if "FROM message_outbox active_outbox" in sql:
                return (True,)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-new", "message-new", "pending")
            return None

    result = database._enqueue_unique_capture_job_with_cursor(
        FakeCursor(),
        job_type="corpus_extract_text",
        payload={"root_name": "docs", "path": "safe.md", "reason": "watch_event"},
        job_family="text",
        resource_class="cpu",
        telemetry={"stage": "queued"},
        audit_details={"root_name": "docs", "reason": "watch_event"},
    )

    sql = "\n".join(statement for statement, _params in executed)
    reset_sql = next(statement for statement, _params in executed if "UPDATE capture_jobs" in statement and "RETURNING id::text, status" in statement)
    audit_params = next(params for statement, params in executed if "capture_job.identity_reused" in statement)

    assert result == {
        "job_id": "job-old",
        "status": "pending",
        "created": False,
        "deduped": False,
        "reused": True,
    }
    assert "broker_message_id = NULL" in reset_sql
    assert "routing_key = NULL" in reset_sql
    assert "correlation_id = NULL" in reset_sql
    assert "causation_id = NULL" in reset_sql
    assert "queued_at = NULL" in reset_sql
    assert "broker_delivery_count = 0" in reset_sql
    assert "FROM message_outbox active_outbox" not in sql
    assert "INSERT INTO message_outbox" in sql
    assert "SET routing_key = %s" in sql
    audit_details = json.loads(audit_params[1])
    assert audit_details["previous_status"] == "completed"
    assert audit_details["message_id"] == "message-new"
    assert audit_details["routing_key"] == "corpus.process"


def test_enqueue_capture_job_command_routes_host_agent_jobs_to_host_queue():
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            if "INSERT INTO message_outbox" in executed[-1][0]:
                return ("outbox-1", "message-1", "pending")
            return None

    outbox = database._enqueue_capture_job_command_with_cursor(
        FakeCursor(),
        job_id="job-host",
        job_type="corpus_extract_text",
        payload={"root_name": "host-docs", "path": "safe.md"},
        job_family="text",
        resource_class="cpu",
        host_agent_route=True,
    )

    outbox_params = next(params for statement, params in executed if "INSERT INTO message_outbox" in statement)
    metadata_update = next(params for statement, params in executed if "routing_key = %s" in statement)

    assert outbox["message_id"] == "message-1"
    assert outbox_params[2] == "corpus.host_agent.process"
    assert outbox_params[3] == "flux.corpus.host_agent.process"
    assert metadata_update[0] == "corpus.host_agent.process"


def test_broker_claim_sql_casts_nullable_broker_message_parameters(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    database.claim_corpus_job_by_id(job_id="job-1", worker_id="worker-1", broker_message_id="message-1")
    database.claim_imap_sync_run_by_id(run_id="run-1", worker_id="worker-1", broker_message_id="message-2")
    database.claim_outlook_sync_request_by_id(request_id="request-1", host_id="host-1", broker_message_id="message-3")

    claim_sql = "\n".join(sql for sql, _params in executed)
    assert "broker_message_id = COALESCE(%s::text, broker_message_id)" in claim_sql
    assert "broker_message_id = COALESCE(%s::text, r.broker_message_id)" in claim_sql
    assert "%s::text IS NULL OR broker_message_id = %s::text" in claim_sql
    assert "%s::text IS NULL OR r.broker_message_id = %s::text" in claim_sql
    assert "%s::text IS NULL OR r.broker_message_id IS NULL OR r.broker_message_id = %s::text" in claim_sql


def test_enqueue_capture_job_command_reuses_active_broker_message_without_duplicate_outbox():
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status, broker_message_id, routing_key" in sql:
                return ("job-host", "pending", "message-existing", "corpus.host_agent.process")
            if "SELECT EXISTS" in sql and "FROM message_outbox" in sql:
                return (True,)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-new", "message-new", "pending")
            return None

    outbox = database._enqueue_capture_job_command_with_cursor(
        FakeCursor(),
        job_id="job-host",
        job_type="corpus_extract_text",
        payload={"root_name": "host-docs", "path": "safe.md"},
        job_family="text",
        resource_class="cpu",
        host_agent_route=True,
    )

    assert outbox == {
        "id": None,
        "message_id": "message-existing",
        "status": "deduped",
        "deduped": True,
        "routing_key": "corpus.host_agent.process",
    }
    assert not any("INSERT INTO message_outbox" in statement for statement, _params in executed)


def test_enqueue_pending_corpus_job_commands_routes_host_agent_root_from_metadata(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return [
                (
                    "job-host",
                    "corpus_extract_text",
                    {"root_name": "host-docs", "path": "safe.md"},
                    "text",
                    "cpu",
                    "pending",
                    True,
                )
            ]

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status, broker_message_id, routing_key" in sql:
                return ("job-host", "pending", None, None)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-1", "message-1", "pending")
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.enqueue_pending_corpus_job_commands(limit=10)

    outbox_params = next(params for statement, params in executed if "INSERT INTO message_outbox" in statement)
    assert result["queued"] == 1
    assert result["jobs"][0]["deduped"] is False
    assert outbox_params[2] == "corpus.host_agent.process"
    assert outbox_params[3] == "flux.corpus.host_agent.process"


def test_enqueue_capture_job_command_infers_host_agent_route_from_root_metadata():
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT EXISTS" in sql and "metadata->>'host_access' = 'host_agent'" in sql:
                return (True,)
            if "SELECT id::text, status, broker_message_id, routing_key" in sql:
                return ("job-host", "pending", None, None)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-1", "message-1", "pending")
            return None

    database._enqueue_capture_job_command_with_cursor(
        FakeCursor(),
        job_id="job-host",
        job_type="corpus_extract_text",
        payload={"root_name": "host-docs", "path": "safe.md"},
        job_family="text",
        resource_class="cpu",
    )

    outbox_params = next(params for statement, params in executed if "INSERT INTO message_outbox" in statement)
    assert outbox_params[2] == "corpus.host_agent.process"
    assert outbox_params[3] == "flux.corpus.host_agent.process"


def test_enqueue_search_index_command_uses_shared_broker_claim_route():
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status, broker_message_id, routing_key" in sql:
                return ("job-search", "pending", None, None)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-1", "message-1", "pending")
            return None

    outbox = database._enqueue_capture_job_command_with_cursor(
        FakeCursor(),
        job_id="job-search",
        job_type="search_index_sync",
        payload={"owner_class": "corpus", "root_name": "docs"},
        job_family="embedding",
        resource_class="gpu",
        host_agent_route=True,
    )

    outbox_params = next(params for statement, params in executed if "INSERT INTO message_outbox" in statement)
    assert outbox["message_id"] == "message-1"
    assert outbox_params[2] == "search_index.process"
    assert outbox_params[3] == "flux.search_index.process"


def test_repair_capture_command_storm_dry_run_does_not_mutate(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 0

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return [
                (
                    "job-host",
                    "corpus_extract_text",
                    {"root_name": "host-docs", "path": "safe.md"},
                    "text",
                    "cpu",
                    "pending",
                    True,
                    42,
                    11,
                )
            ]

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.repair_capture_command_storm()

    assert result["applied"] is False
    assert result["affected_jobs"] == 1
    assert result["jobs"][0]["duplicate_outbox_rows"] == 42
    assert not any("DELETE FROM message_outbox" in statement for statement, _params in executed)
    assert not any("UPDATE capture_jobs" in statement and "broker_message_id = NULL" in statement for statement, _params in executed)
    assert not any("INSERT INTO message_outbox" in statement for statement, _params in executed)


def test_repair_capture_command_storm_apply_resets_and_reenqueues_one_command(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 0

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))
            if "DELETE FROM message_outbox" in sql:
                self.rowcount = 11
            elif "UPDATE capture_jobs" in sql and "broker_message_id = NULL" in sql:
                self.rowcount = 1
            else:
                self.rowcount = 0

        def fetchall(self):
            return [
                (
                    "job-host",
                    "corpus_extract_text",
                    {"root_name": "host-docs", "path": "safe.md"},
                    "text",
                    "cpu",
                    "pending",
                    True,
                    42,
                    11,
                )
            ]

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status, broker_message_id, routing_key" in sql:
                return ("job-host", "pending", None, None)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-1", "message-1", "pending")
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.repair_capture_command_storm(apply=True, confirm="broker-claim-storm")

    assert result["applied"] is True
    assert result["deleted_unpublished_outbox"] == 11
    assert result["reset_jobs"] == 1
    assert result["enqueued"] == 1
    assert result["jobs"][0]["message_id"] == "message-1"
    delete_sql = next(statement for statement, _params in executed if "DELETE FROM message_outbox" in statement)
    assert "status IN ('pending', 'publishing', 'failed')" in delete_sql
    assert any("broker_message_id = NULL" in statement for statement, _params in executed)


def test_repair_capture_command_storm_apply_requires_exact_confirmation():
    with pytest.raises(ValueError, match="--confirm broker-claim-storm"):
        database.repair_capture_command_storm(apply=True, confirm="wrong")


def test_repair_stranded_capture_commands_dry_run_reports_without_mutating(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            return [
                (
                    "job-stranded",
                    "corpus_extract_image",
                    {"root_name": "host-docs", "path": "logo.png"},
                    "image",
                    "gpu",
                    "pending",
                    True,
                    "message-stale",
                    "corpus.host_agent.process",
                )
            ]

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.repair_stranded_capture_commands(job_id="job-stranded", root_name="host-docs", family="image")

    assert result["applied"] is False
    assert result["affected_jobs"] == 1
    assert result["reset_jobs"] == 0
    assert result["enqueued"] == 0
    assert result["jobs"][0]["job_id"] == "job-stranded"
    select_sql, select_params = executed[0]
    assert "NOT EXISTS" in select_sql
    assert "FROM message_outbox active_outbox" in select_sql
    assert "job-stranded" in select_params
    assert "host-docs" in select_params
    assert "image" in select_params
    assert not any("UPDATE capture_jobs" in statement for statement, _params in executed)
    assert not any("INSERT INTO message_outbox" in statement for statement, _params in executed)


def test_repair_stranded_capture_commands_apply_resets_and_enqueues(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 0

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))
            if "UPDATE capture_jobs" in sql and "broker_message_id = NULL" in sql:
                self.rowcount = 1
            else:
                self.rowcount = 0

        def fetchall(self):
            return [
                (
                    "job-stranded",
                    "corpus_extract_image",
                    {"root_name": "host-docs", "path": "logo.png"},
                    "image",
                    "gpu",
                    "pending",
                    True,
                    "message-stale",
                    "corpus.host_agent.process",
                )
            ]

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status, broker_message_id, routing_key" in sql:
                return ("job-stranded", "pending", None, None)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-1", "message-1", "pending")
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.repair_stranded_capture_commands(apply=True, confirm="stranded-capture-commands")

    assert result["applied"] is True
    assert result["affected_jobs"] == 1
    assert result["reset_jobs"] == 1
    assert result["enqueued"] == 1
    assert result["jobs"][0]["message_id"] == "message-1"
    reset_sql = next(statement for statement, _params in executed if "UPDATE capture_jobs" in statement and "broker_message_id = NULL" in statement)
    assert "routing_key = NULL" in reset_sql
    assert "broker_delivery_count = 0" in reset_sql


def test_repair_stranded_capture_commands_apply_requires_exact_confirmation():
    with pytest.raises(ValueError, match="--confirm stranded-capture-commands"):
        database.repair_stranded_capture_commands(apply=True, confirm="wrong")


@pytest.mark.parametrize(
    ("lease_id", "request_reason", "expected_lease_id"),
    [
        ("lease-1", "demand", "lease-1"),
        (None, "idle", None),
    ],
)
def test_enqueue_gpu_eviction_request_writes_state_and_broker_command(
    monkeypatch, lease_id, request_reason, expected_lease_id,
):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status, broker_message_id" in sql:
                return None
            if "INSERT INTO gpu_evictions" in sql:
                return ("42",)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-1", "message-1", "pending")
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def transaction(self):
            return self

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.enqueue_gpu_eviction_request(
        lease_id=lease_id,
        request_profile={"task_type": "ocr_document", "model_id": "PaddleOCR-VL", "estimated_vram_mb": 8_000},
        candidate={"task_type": "embedding", "model_id": "snowflake", "estimated_vram_mb": 2_500, "component": "model-runner"},
        request_reason=request_reason,
    )

    outbox_params = next(params for statement, params in executed if "INSERT INTO message_outbox" in statement)
    payload = json.loads(outbox_params[9])

    assert result == {"id": "42", "eviction_id": "42", "status": "queued", "message_id": "message-1", "deduped": False}
    assert outbox_params[1] == "flux.commands"
    assert outbox_params[2] == "gpu.eviction.request"
    assert outbox_params[3] == "flux.gpu.eviction.request"
    assert payload["payload"] == {
        "eviction_id": "42",
        "lease_id": expected_lease_id,
        "task_type": "embedding",
        "model_id": "snowflake",
        "component": "model-runner",
        "estimated_vram_mb": 2500,
    }
    assert "raw_content" not in json.dumps(payload)


def test_enqueue_gpu_eviction_request_requires_lease_for_demand():
    with pytest.raises(ValueError, match="lease_id is required for demand eviction"):
        database.enqueue_gpu_eviction_request(
            lease_id=None,
            request_profile={"task_type": "rerank", "model_id": "qwen"},
            candidate={"task_type": "embedding", "model_id": "snowflake", "component": "model-runner"},
        )


def test_enqueue_gpu_eviction_request_dedupes_active_candidate_across_leases(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status, broker_message_id" in sql:
                return ("42", "queued", "message-existing")
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def transaction(self):
            return self

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.enqueue_gpu_eviction_request(
        lease_id="lease-new",
        request_profile={
            "task_type": "ocr_image",
            "model_id": "PP-OCRv5",
            "estimated_vram_mb": 2_000,
        },
        candidate={
            "task_type": "embedding",
            "model_id": "snowflake",
            "estimated_vram_mb": 2_500,
            "component": "model-runner",
        },
    )

    select_sql, select_params = next(
        (statement, params)
        for statement, params in executed
        if "SELECT id::text, status, broker_message_id" in statement
    )
    assert "lease_id = %s" not in select_sql
    assert select_params == ("embedding", "snowflake", "model-runner", "")
    assert result == {
        "id": "42",
        "eviction_id": "42",
        "status": "queued",
        "message_id": "message-existing",
        "deduped": True,
    }
    assert not any("INSERT INTO gpu_evictions" in statement for statement, _params in executed)


def test_enqueue_gpu_eviction_request_locks_logical_key_before_absent_row_lookup(monkeypatch):
    """The absent-key path must serialise before it decides to publish."""
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status, broker_message_id" in sql:
                return None
            if "INSERT INTO gpu_evictions" in sql:
                return ("42",)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-1", "message-1", "pending")
            return None

        def fetchall(self):
            return []

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def transaction(self):
            return self

        def cursor(self):
            return FakeCursor()

    monkeypatch.setattr(
        database,
        "_load_psycopg",
        lambda: SimpleNamespace(connect=lambda *_args, **_kwargs: FakeConnection()),
    )

    database.enqueue_gpu_eviction_request(
        lease_id="lease-1",
        request_profile={"task_type": "rerank", "model_id": "qwen"},
        candidate={"task_type": "embedding", "model_id": "snowflake", "component": "model-runner"},
    )

    lock_index = next(
        index for index, (statement, _params) in enumerate(executed) if "pg_advisory_xact_lock" in statement
    )
    select_index = next(
        index
        for index, (statement, _params) in enumerate(executed)
        if "SELECT id::text, status, broker_message_id" in statement
    )
    lock_sql, lock_params = executed[lock_index]
    assert lock_index < select_index
    # PostgreSQL cannot resolve ``jsonb_build_array``'s polymorphic inputs
    # from untyped extended-query parameters.  Keep each element explicitly
    # text-typed so the logical-key advisory lock works against real psycopg.
    assert "jsonb_build_array(%s::text, %s::text, %s::text)::text" in lock_sql
    assert lock_params == ("embedding", "snowflake", "model-runner")


def test_enqueue_gpu_eviction_request_fences_dedupe_by_runtime_generation_and_upgrades_idle(monkeypatch):
    """An old generation cannot suppress a fresh demand eviction."""
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status, broker_message_id" in sql:
                return ("42", "queued", "message-existing", "idle")
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def transaction(self):
            return self

        def cursor(self):
            return FakeCursor()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_args, **_kwargs: FakeConnection()))

    result = database.enqueue_gpu_eviction_request(
        lease_id="lease-demand",
        request_profile={"task_type": "rerank", "model_id": "qwen"},
        candidate={
            "task_type": "embedding",
            "model_id": "snowflake",
            "component": "model-runner",
            "runtime_generation": "generation-2",
            "runtime_activity_sequence": 11,
        },
        request_reason="demand",
        reconciliation_observation_id="observation-2",
    )

    select_sql, select_params = next((sql, params) for sql, params in executed if "SELECT id::text, status, broker_message_id" in sql)
    assert "runtime_generation = %s" in select_sql
    assert select_params[-1] == "generation-2"
    assert result["deduped"] is True
    upgrade_sql, upgrade_params = next((sql, params) for sql, params in executed if "request_reason = 'demand'" in sql)
    assert "lease_id = %s" in upgrade_sql
    assert upgrade_params[0] == "lease-demand"


@pytest.mark.parametrize(
    ("request_reason", "lease_id"),
    [
        ("demand", "lease-demand"),
        ("idle", "idle:embedding:snowflake:model-runner:generation-1"),
    ],
)
def test_enqueue_gpu_eviction_request_replaces_published_expired_same_generation_command(
    monkeypatch, request_reason, lease_id,
):
    """A replacement must publish a command for its own eviction row."""
    evictions = []
    outbox = {}

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            self.sql = sql
            self.params = params
            if "INSERT INTO gpu_evictions" in sql:
                evictions.append({"id": str(42 + len(evictions)), "status": "queued"})
            elif "INSERT INTO message_outbox" in sql:
                message_id = params[0]
                outbox.setdefault(
                    message_id,
                    {
                        "id": f"outbox-{len(outbox) + 1}",
                        "message_id": message_id,
                        "aggregate_id": params[8],
                        "payload": json.loads(params[9]),
                        "status": "pending",
                    },
                )

        def fetchone(self):
            if "SELECT id::text, status, broker_message_id" in self.sql:
                active = next((row for row in evictions if row["status"] in {"queued", "running", "retrying"}), None)
                return None if active is None else (active["id"], active["status"], active.get("broker_message_id"), "demand")
            if "INSERT INTO gpu_evictions" in self.sql:
                return (evictions[-1]["id"],)
            if "INSERT INTO message_outbox" in self.sql:
                row = outbox[self.params[0]]
                return (row["id"], row["message_id"], row["status"])
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def transaction(self):
            return self

        def cursor(self):
            return FakeCursor()

    monkeypatch.setattr(
        database,
        "_load_psycopg",
        lambda: SimpleNamespace(connect=lambda *_args, **_kwargs: FakeConnection()),
    )
    monkeypatch.setattr(database, "_expire_stale_gpu_eviction_requests_with_cursor", lambda _cur: [])

    first = database.enqueue_gpu_eviction_request(
        lease_id=lease_id,
        request_profile={"task_type": "rerank", "model_id": "qwen"},
        candidate={"task_type": "embedding", "model_id": "snowflake", "component": "model-runner"},
        runtime_generation="generation-1",
        request_reason=request_reason,
    )
    evictions[0]["status"] = "expired"
    outbox[first["message_id"]]["status"] = "published"

    replacement = database.enqueue_gpu_eviction_request(
        lease_id=lease_id,
        request_profile={"task_type": "rerank", "model_id": "qwen"},
        candidate={"task_type": "embedding", "model_id": "snowflake", "component": "model-runner"},
        runtime_generation="generation-1",
        request_reason=request_reason,
    )

    assert replacement["deduped"] is False
    assert replacement["eviction_id"] != first["eviction_id"]
    assert replacement["message_id"] != first["message_id"]
    assert len(outbox) == 2
    replacement_outbox = outbox[replacement["message_id"]]
    assert replacement_outbox["status"] == "pending"
    assert replacement_outbox["aggregate_id"] == replacement["eviction_id"]
    assert replacement_outbox["payload"]["payload"]["eviction_id"] == replacement["eviction_id"]


def test_expire_stale_gpu_eviction_requests_preserves_rows_and_emits_terminal_audit(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            if "UPDATE gpu_evictions" in executed[-1][0]:
                return [(str(index), "lease-1", "embedding", "snowflake", "model-runner", "expired", 2500, "", {}, None, None, None, None, 1, "generation-1", 4, "", 7, None, None, None, None, "idle", "stale_request_expired", "obs-1") for index in range(1, 5)]
            return []

    class FakeConnection:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def transaction(self): return self
        def cursor(self): return FakeCursor()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_args, **_kwargs: FakeConnection()))
    monkeypatch.setattr(database, "_enqueue_gpu_eviction_event_with_cursor", lambda *_args, **_kwargs: {})

    expired = database.expire_stale_gpu_eviction_requests()

    assert [item["id"] for item in expired] == ["1", "2", "3", "4"]
    sql = next(statement for statement, _params in executed if "UPDATE gpu_evictions" in statement)
    assert "status = 'expired'" in sql
    assert "terminal_reason" in sql
    assert "DELETE FROM gpu_evictions" not in sql


def test_idle_eviction_candidate_query_requires_confirmed_idle_residency_without_protected_work(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def execute(self, sql, params=()): executed.append((sql, params))
        def fetchall(self):
            return [("embedding", "snowflake", 2500, "model-runner", "generation-1", 4)]

    class FakeConnection:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def cursor(self): return FakeCursor()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_args, **_kwargs: FakeConnection()))

    candidates = database.list_idle_gpu_eviction_candidates(idle_unload_seconds=120)

    assert candidates == [{
        "task_type": "embedding", "model_id": "snowflake", "estimated_vram_mb": 2500,
        "component": "model-runner", "runtime_generation": "generation-1", "runtime_activity_sequence": 4,
    }]
    sql, params = executed[0]
    assert "runtime_state = 'present'" in sql
    assert "last_operation_completed_at <= now() - (%s * interval '1 second')" in sql
    assert "runtime_in_flight = 0" in sql
    assert "last_operation_started_at <= r.last_operation_completed_at" in sql
    assert "status = 'running'" in sql
    assert "status = 'waiting'" in sql
    assert "status IN ('queued', 'running', 'retrying')" in sql
    assert params == (120.0,)


def test_gpu_eviction_maintenance_leader_lock_loser_is_a_noop(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def execute(self, sql, params=()): executed.append((sql, params))
        def fetchone(self): return (False,)

    class FakeConnection:
        autocommit = False
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def cursor(self): return FakeCursor()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_args, **_kwargs: FakeConnection()))

    with database.gpu_eviction_maintenance_leader_lock() as is_leader:
        assert is_leader is False

    assert len(executed) == 1
    assert "pg_try_advisory_lock(hashtextextended('flux.gpu.eviction.maintenance', 0))" in executed[0][0]


def test_complete_gpu_eviction_request_rejects_late_claim_token_without_mutation(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def execute(self, sql, params=()): executed.append((sql, params))
        def fetchone(self): return None

    class FakeConnection:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def transaction(self): return self
        def cursor(self): return FakeCursor()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_args, **_kwargs: FakeConnection()))

    result = database.complete_gpu_eviction_request(
        eviction_id="42", status="succeeded", claim_token="stale-token", row_version=4
    )

    assert result == {"eviction_id": "42", "cas_rejected": True}
    sql, params = executed[-1]
    assert "claim_token = %s" in sql
    assert "row_version = %s" in sql
    assert params[-2:] == ("stale-token", 4)


def test_gpu_eviction_cas_rejection_is_a_bounded_audit_event(monkeypatch):
    recorded: dict[str, object] = {}

    def record_audit_event(**kwargs):
        recorded.update(kwargs)
        return {"id": "audit-1", "event_type": "gpu_eviction.cas_rejected"}

    monkeypatch.setattr(database, "record_audit_event", record_audit_event)

    result = database.record_gpu_eviction_cas_rejection(
        eviction_id="eviction-1",
        stage="claim\n" + ("x" * 100),
        worker_id="worker\x00sk-live-secret" + ("x" * 200),
        broker_message_id="message\nBearer private-token" + ("x" * 200),
    )

    assert result == {"id": "audit-1", "event_type": "gpu_eviction.cas_rejected"}
    assert recorded["event_type"] == "gpu_eviction.cas_rejected"
    assert recorded["target_table"] == "gpu_evictions"
    assert recorded["target_id"] == "eviction-1"
    details = recorded["details"]
    assert details["stage"] == "claim_" + ("x" * 74)
    assert len(details["worker_id_hash"]) == 24
    assert len(details["broker_message_id_hash"]) == 24
    assert "sk-live-secret" not in json.dumps(details)
    assert "private-token" not in json.dumps(details)
    assert "\x00" not in json.dumps(details)
    assert "\n" not in json.dumps(details)


def test_gpu_eviction_deadlines_follow_current_rabbitmq_retry_policy(monkeypatch):
    monkeypatch.setattr(
        database.messaging,
        "RabbitMqConfig",
        SimpleNamespace(from_env=lambda: SimpleNamespace(retry_delay_ms=30_000, delivery_limit=8)),
    )
    monkeypatch.setenv("FLUX_KB_GPU_SCHEDULER_EVICTION_REQUEST_TIMEOUT_SECONDS", "40")

    deadlines = database._gpu_eviction_deadlines(delivery_count=3)

    assert deadlines == {
        "queued_seconds": 120,
        "running_seconds": 160,
        "retry_delay_seconds": 30,
        "retry_seconds": 340,
    }


def test_gpu_eviction_running_deadline_matches_settings_service_precedence(monkeypatch):
    reads = []

    def get_runtime_setting(key):
        reads.append(key)
        return {"key": key, "value": 47}

    monkeypatch.setenv("FLUX_KB_GPU_SCHEDULER_EVICTION_REQUEST_TIMEOUT_SECONDS", "5")
    monkeypatch.setattr(database, "get_runtime_setting", get_runtime_setting)
    monkeypatch.setattr(
        database.messaging,
        "RabbitMqConfig",
        SimpleNamespace(from_env=lambda: SimpleNamespace(retry_delay_ms=30_000, delivery_limit=8)),
    )

    deadlines = database._gpu_eviction_deadlines(delivery_count=1)

    assert settings.SettingsService().resolve("gpu.scheduler.eviction_request_timeout_seconds").raw_value == 5
    assert deadlines["running_seconds"] == 60
    assert reads == []

    monkeypatch.delenv("FLUX_KB_GPU_SCHEDULER_EVICTION_REQUEST_TIMEOUT_SECONDS", raising=False)
    reads.clear()

    deadlines = database._gpu_eviction_deadlines(delivery_count=1)

    assert settings.SettingsService().resolve("gpu.scheduler.eviction_request_timeout_seconds").raw_value == 47
    assert deadlines["running_seconds"] == 188
    assert reads == ["gpu.scheduler.eviction_request_timeout_seconds", "gpu.scheduler.eviction_request_timeout_seconds"]


def test_retry_gpu_eviction_request_uses_claim_fence_and_remaining_delivery_deadline(monkeypatch):
    executed = []
    row = (
        "42", "lease-1", "embedding", "snowflake", "model-runner", "retrying", 2500, "transient", {}, None,
        None, None, None, 3, "generation-1", 4, "token-1", 8, None, None, None, None, "demand", "", "obs-1",
    )

    class FakeCursor:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def execute(self, sql, params=()): executed.append((sql, params))
        def fetchone(self): return row

    class FakeConnection:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def transaction(self): return self
        def cursor(self): return FakeCursor()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_args, **_kwargs: FakeConnection()))
    monkeypatch.setattr(database, "_gpu_eviction_deadlines", lambda *, delivery_count=0: {"retry_delay_seconds": 30, "retry_seconds": 340})
    monkeypatch.setattr(database, "_enqueue_gpu_eviction_event_with_cursor", lambda *_args, **_kwargs: {})

    result = database.retry_gpu_eviction_request(
        eviction_id="42", error="transient", claim_token="token-1", row_version=7, broker_delivery_count=3
    )

    assert result["status"] == "retrying"
    sql, params = executed[-1]
    assert "claim_token = %s" in sql and "row_version = %s" in sql
    assert params[2:4] == (30, 340)
    assert params[-2:] == ("token-1", 7)


def test_update_gpu_residency_verification_retains_owner_and_records_unverified_state(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def execute(self, sql, params=()): executed.append((sql, params))

    class FakeConnection:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def transaction(self): return self
        def cursor(self): return FakeCursor()

    monkeypatch.setattr(database, "_load_psycopg", lambda: SimpleNamespace(connect=lambda *_args, **_kwargs: FakeConnection()))

    database.update_gpu_residency_verification(
        task_type="embedding", model_id="snowflake", runtime_state="memory_release_unverified",
        failure_reason="allocator did not drop", observation_id="obs-post", owner_component="model-runner",
        runtime_generation="generation-1", runtime_activity_sequence=4,
    )

    sql, params = executed[0]
    assert "runtime_state = %s" in sql
    assert "runtime_verification_capacity_state" in sql
    assert "owner_component = CASE WHEN %s THEN %s ELSE COALESCE(NULLIF(%s, ''), owner_component) END" in sql
    assert params[:5] == ("memory_release_unverified", "allocator did not drop", False, "model-runner", "model-runner")


def test_fenced_residency_verification_replaces_absent_post_identity_with_explicit_null_activity():
    executed = []

    class Cursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

    database._update_gpu_residency_verification_with_cursor(
        Cursor(),
        task_type="embedding", model_id="snowflake", runtime_state="unload_failed",
        owner_component="model-runner", runtime_generation="generation-2",
        runtime_activity_sequence=None, runtime_fingerprint="post-fingerprint",
        replace_runtime_identity=True,
    )

    sql, params = executed[0]
    assert "CASE WHEN %s THEN %s" in sql
    assert "runtime_activity_sequence = CASE WHEN %s THEN %s" in sql
    assert params[2:5] == (True, "model-runner", "model-runner")
    assert (True, None) in tuple(zip(params, params[1:]))


def test_fenced_residency_verification_replaces_ollama_fingerprint_without_generation_or_activity():
    executed = []

    class Cursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

    database._update_gpu_residency_verification_with_cursor(
        Cursor(),
        task_type="ollama_vision", model_id="qwen3-vl:8b", runtime_state="unload_failed",
        owner_component="ollama", runtime_generation="", runtime_activity_sequence=None,
        runtime_fingerprint="fresh-empty-inventory", replace_runtime_identity=True,
    )

    _sql, params = executed[0]
    assert "fresh-empty-inventory" in params
    assert (True, "") in tuple(zip(params, params[1:]))


def test_enqueue_corpus_sync_job_upgrades_existing_active_schedule(monkeypatch):
    executed = []
    schedule = database._job_schedule_metadata("corpus_sync_root")

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "status = 'running'" in sql:
                return None
            if "status IN ('pending', 'retrying_locked', 'retrying_vss_failed')" in sql:
                return ("job-existing", "pending", {"root_name": "mail-outlook-mohesr"})
            if "UPDATE capture_jobs" in sql:
                return ("job-existing", "pending")
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

    result = database.enqueue_corpus_sync_job(root_name="mail-outlook-mohesr", reason="manual_sync")

    sql = "\n".join(statement for statement, _params in executed)
    update_params = next(params for statement, params in executed if "UPDATE capture_jobs" in statement)
    assert result["job_id"] == "job-existing"
    assert result["status"] == "pending"
    assert result["root_name"] == "mail-outlook-mohesr"
    assert result["deduped"] is True
    assert result["reused"] is False
    assert result["message_id"]
    assert result["routing_key"] == "corpus.process"
    assert result["queued"] is True
    assert "priority = GREATEST(priority, %s)" in sql
    assert "time_budget_seconds = GREATEST(time_budget_seconds, %s)" in sql
    assert json.loads(update_params[0]) == {"root_name": "mail-outlook-mohesr", "reason": "manual_sync"}
    assert update_params[1] == schedule["priority"]
    assert update_params[2] == schedule["time_budget_seconds"]
    assert update_params[4] == "job-existing"


def test_enqueue_corpus_sync_job_batches_pending_path_job_for_different_watch_path(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "status = 'running'" in sql:
                return None
            if "status IN ('pending', 'retrying_locked', 'retrying_vss_failed')" in sql:
                return ("job-existing", "pending", {"root_name": "docs", "reason": "watch_event", "path": "a.md"})
            if "UPDATE capture_jobs" in sql:
                return ("job-existing", "pending")
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

    result = database.enqueue_corpus_sync_job(root_name="docs", path="b.md", reason="watch_event")

    sql = "\n".join(statement for statement, _params in executed)
    update_params = next(params for statement, params in executed if "UPDATE capture_jobs" in statement)
    assert result["deduped"] is True
    assert "(payload - 'path') || %s::jsonb" not in sql
    assert json.loads(update_params[0]) == {"root_name": "docs", "reason": "watch_event", "paths": ["a.md", "b.md"]}


def test_enqueue_corpus_sync_job_reenqueues_command_after_pending_merge_without_active_outbox(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "status = 'running'" in sql:
                return None
            if "status IN ('pending', 'retrying_locked', 'retrying_vss_failed')" in sql:
                return ("job-existing", "pending", {"root_name": "docs", "reason": "watch_event", "path": "a.md"})
            if "UPDATE capture_jobs" in sql and "RETURNING id::text, status" in sql:
                return ("job-existing", "pending")
            if "SELECT EXISTS" in sql and "FROM monitored_roots" in sql:
                return (True,)
            if "SELECT id::text, status, broker_message_id, routing_key" in sql:
                return ("job-existing", "pending", "old-message", "corpus.host_agent.process")
            if "FROM message_outbox active_outbox" in sql:
                return (False,)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-new", "message-new", "pending")
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

    result = database.enqueue_corpus_sync_job(root_name="docs", path="b.md", reason="watch_event")

    insert_params = next(params for statement, params in executed if "INSERT INTO message_outbox" in statement)
    command_payload = json.loads(insert_params[9])

    assert result == {
        "job_id": "job-existing",
        "status": "pending",
        "root_name": "docs",
        "deduped": True,
        "reused": False,
        "message_id": "message-new",
        "routing_key": "corpus.host_agent.process",
        "queued": True,
    }
    assert command_payload["payload"]["job_id"] == "job-existing"
    assert command_payload["payload"]["paths_count"] == 2
    assert command_payload["payload"]["reason"] == "watch_event"


def test_enqueue_corpus_sync_job_keeps_existing_active_command_after_pending_merge(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "status = 'running'" in sql:
                return None
            if "status IN ('pending', 'retrying_locked', 'retrying_vss_failed')" in sql:
                return ("job-existing", "pending", {"root_name": "docs", "reason": "watch_event", "path": "a.md"})
            if "UPDATE capture_jobs" in sql and "RETURNING id::text, status" in sql:
                return ("job-existing", "pending")
            if "SELECT EXISTS" in sql and "FROM monitored_roots" in sql:
                return (False,)
            if "SELECT id::text, status, broker_message_id, routing_key" in sql:
                return ("job-existing", "pending", "active-message", "corpus.process")
            if "FROM message_outbox active_outbox" in sql:
                return (True,)
            if "INSERT INTO message_outbox" in sql:
                return ("outbox-new", "message-new", "pending")
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

    result = database.enqueue_corpus_sync_job(root_name="docs", path="b.md", reason="watch_event")

    assert result == {
        "job_id": "job-existing",
        "status": "pending",
        "root_name": "docs",
        "deduped": True,
        "reused": False,
        "message_id": "active-message",
        "routing_key": "corpus.process",
        "queued": False,
    }
    assert not any("INSERT INTO message_outbox" in statement for statement, _params in executed)


def test_enqueue_corpus_sync_job_creates_pending_followup_for_running_path_job(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "status = 'running'" in sql:
                return ("job-running", "running", {"root_name": "docs", "reason": "watch_event", "path": "a.md"})
            if "status IN ('pending', 'retrying_locked', 'retrying_vss_failed')" in sql:
                return None
            if "INSERT INTO capture_jobs" in sql:
                return ("job-followup", "pending")
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

    result = database.enqueue_corpus_sync_job(root_name="docs", path="b.md", reason="watch_event")

    sql = "\n".join(statement for statement, _params in executed)
    insert_params = next(params for statement, params in executed if "INSERT INTO capture_jobs" in statement)
    assert result == {
        "job_id": "job-followup",
        "status": "pending",
        "root_name": "docs",
        "deduped": False,
        "reused": False,
        "followup": True,
    }
    assert "SET job_type = %s" not in sql
    assert "SET routing_key = %s" in sql
    assert json.loads(insert_params[1]) == {"root_name": "docs", "reason": "watch_event", "path": "b.md"}


def test_enqueue_corpus_sync_path_batch_jobs_splits_large_outlook_delta(monkeypatch):
    executed = []
    inserted = 0

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            nonlocal inserted
            if "INSERT INTO capture_jobs" in executed[-1][0]:
                inserted += 1
                return (f"job-{inserted}", "pending")
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

    paths = [f"/app/private/mail-spool/outlook/ready/export-{index}" for index in range(5001)]
    result = database.enqueue_corpus_sync_path_batch_jobs(
        root_name="mail-outlook",
        reason="outlook_spool_sync",
        paths=paths,
        payload={"profile_name": "outlook", "source_type": "outlook_com"},
    )

    assert database.MAX_CORPUS_SYNC_BATCH_PATHS == 5000
    assert result["count"] == 2
    assert [job["job_id"] for job in result["jobs"]] == ["job-1", "job-2"]
    insert_params = [params for statement, params in executed if "INSERT INTO capture_jobs" in statement]
    payloads = [json.loads(params[1]) for params in insert_params]
    assert len(payloads[0]["paths"]) == 5000
    assert len(payloads[1]["paths"]) == 1
    assert payloads[0]["paths_total"] == 5001
    assert payloads[0]["path_batch_index"] == 1
    assert payloads[0]["path_batch_total"] == 2
    assert payloads[1]["path_batch_index"] == 2
    assert payloads[1]["path_batch_total"] == 2
    assert payloads[1]["profile_name"] == "outlook"


def test_update_corpus_job_progress_records_progress_heartbeat(monkeypatch):
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

    database.update_corpus_job_progress(
        job_id="job-sync",
        telemetry={"stage": "hash\x00ing", "current_path": "a\x00/b.txt"},
        last_error="bad\x00error",
    )

    sql = "\n".join(statement for statement, _params in executed)
    params = executed[0][1]
    telemetry = json.loads(params[0])
    assert "progress_heartbeat_at = now()" in sql
    assert telemetry["stage"] == "hashing"
    assert telemetry["current_path"] == "a/b.txt"
    assert params[1] == "baderror"


def test_heartbeat_corpus_job_does_not_refresh_progress_heartbeat(monkeypatch):
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

    database.heartbeat_corpus_job(
        job_id="job-sync",
        telemetry={"stage": "run\x00ning", "current_path": "a\x00/b.txt"},
        last_error="heartbeat\x00error",
    )

    sql = "\n".join(statement for statement, _params in executed)
    params = executed[0][1]
    telemetry = json.loads(params[0])
    assert "updated_at = now()" in sql
    assert "progress_heartbeat_at" not in sql
    assert telemetry["stage"] == "running"
    assert telemetry["current_path"] == "a/b.txt"
    assert params[1] == "heartbeaterror"


def test_recover_stale_running_corpus_jobs_uses_progress_heartbeat_and_worker_liveness(monkeypatch):
    executed = []

    class FakeCursor:
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

    result = database.recover_stale_running_corpus_jobs(root_name="docs", stale_after_seconds=120)

    sql = "\n".join(statement for statement, _params in executed)
    assert result == {"root_name": "docs", "recovered": 2}
    assert "job.job_type LIKE 'corpus_%%'" in sql
    assert "job.job_type = 'corpus_sync_root'" not in sql
    assert "progress_heartbeat_at" in sql
    assert "GREATEST(%s, COALESCE(job.time_budget_seconds, 0))" in sql
    assert "runtime_components" in sql
    assert "component.metadata->>'worker_instance' = 'true'" in sql
    assert "status = 'running'" in sql
    assert "status = 'pending'" in sql


def test_recover_stale_model_activity_events_marks_only_stale_running_rows(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return (3,)

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

    monkeypatch.setenv(database.MODEL_ACTIVITY_TEST_WRITE_OPT_IN_ENV, "1")
    monkeypatch.setenv(database.MODEL_ACTIVITY_TEST_DATABASE_URL_ENV, "postgresql://activity-test")
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.recover_stale_model_activity_events(stale_after_seconds=360, url="postgresql://activity-test")

    sql = "\n".join(statement for statement, _params in executed)
    params = executed[0][1]
    assert result == {"recovered": 3}
    assert "status = 'stale_running'" in sql
    assert "completed_at = COALESCE(completed_at, now())" in sql
    assert "duration_ms = COALESCE(" in sql
    assert "error_class = COALESCE(error_class, 'ModelActivityStale')" in sql
    assert "stale_running_recovered" in sql
    assert "WHERE status = 'running'" in sql
    assert "completed_at IS NULL" in sql
    assert "started_at < now() - (%s * interval '1 second')" in sql
    assert params[-1] == 360
    assert database._sanitize_model_activity_metadata(
        {"stale_running_recovered": True, "unsafe_path": "E:/Private/file.txt"}
    ) == {"stale_running_recovered": True}


def test_persist_crawl_plan_reports_progress_in_dry_run(tmp_path):
    events = []
    plan = CrawlPlan(root_path=tmp_path, assets=[], deferred_jobs=[], errors=[])

    result = database.persist_crawl_plan(root_name="docs", plan=plan, dry_run=True, progress_callback=events.append)

    assert result["dry_run"] is True
    assert events == [
        {
            "stage": "persisted",
            "stage_index": 6,
            "stage_total": 6,
            "files_done": 0,
            "files_total": 0,
            "progress_percent": 100,
            "progress_label": "Persisted 0/0 files",
        }
    ]


def test_apply_extraction_result_persists_container_child_assets(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 0

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT a.id::text, a.canonical_asset_id::text" in sql and "a.root_id::text" in sql:
                return ("asset-parent", None, "root-1", "file:///docs/bundle.zip", 123)
            if "SELECT a.id::text, a.canonical_asset_id::text" in sql:
                return ("asset-parent", None)
            if "UPDATE source_assets" in sql:
                return ("asset-parent",)
            if "SELECT id::text FROM source_assets" in sql:
                return None
            if "INSERT INTO source_assets" in sql:
                return ("child-1",)
            if "INSERT INTO asset_chunks" in sql:
                return ("chunk-1",)
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

    child = SimpleNamespace(
        member_path="docs/readme.md",
        file_kind="text",
        mime_type="text/markdown",
        extension=".md",
        size_bytes=20,
        quick_hash="quick-child",
        content_hash="hash-child",
        extraction_tier="inline",
        extraction_status="indexed",
        chunks=(
            AssetChunk(
                chunk_index=0,
                title="docs/readme.md",
                body="Archive child body",
                modality="text",
                locator="char:0-18",
                token_estimate=3,
            ),
        ),
        metadata={"container_member_path": "docs/readme.md", "container_format": "zip"},
    )
    result = SimpleNamespace(status="metadata_only", metadata={"extractor": "container"}, chunks=(), child_assets=(child,))
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    database.apply_extraction_result(root_name="docs", relative_path="bundle.zip", result=result)

    sql = "\n".join(statement for statement, _params in executed)
    params_json = "\n".join(str(params) for _statement, params in executed)
    assert "metadata->>'container_asset_id' = %s" in sql
    assert "INSERT INTO source_assets" in sql
    assert "bundle.zip/docs/readme.md" in params_json
    assert "container_asset_id" in params_json
    assert "parent_asset_id" in params_json
    assert "container_member_path" in params_json
    assert "INSERT INTO asset_chunks" in sql


def test_apply_extraction_result_strips_stale_strict_blocked_metadata(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 0

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT a.id::text, a.canonical_asset_id::text" in sql:
                return ("asset-1", None, "root-1", "file:///docs/scan.png", 123)
            if "UPDATE source_assets" in sql:
                return ("asset-1",)
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

    result = SimpleNamespace(
        status="indexed",
        metadata={"strict_indexing": True, "readiness_status": "indexed", "readiness_reason": "content_extracted"},
        chunks=(),
        child_assets=(),
    )
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    database.apply_extraction_result(root_name="docs", relative_path="scan.png", result=result)

    sql = "\n".join(statement for statement, _params in executed)
    assert "metadata - 'metadata_only_blocked' - 'readiness_reason'" in sql
    assert "COALESCE(%s::jsonb->>'readiness_status', '') NOT LIKE 'blocked_%%'" in sql


def test_apply_extraction_result_for_job_locks_running_job_before_writing(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 0

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "FROM capture_jobs" in sql:
                return ("job-1", "running")
            if "SELECT a.id::text, a.canonical_asset_id::text" in sql:
                return ("asset-1", None, "root-1", "file:///docs/readme.md", 123)
            if "UPDATE source_assets" in sql:
                return ("asset-1",)
            if "INSERT INTO asset_chunks" in sql:
                return ("chunk-1",)
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

    result = SimpleNamespace(
        status="indexed",
        metadata={"extractor": "text"},
        chunks=(AssetChunk(chunk_index=0, title="readme", body="body", token_estimate=1),),
        child_assets=(),
    )
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    applied = database.apply_extraction_result_for_job(
        job_id="job-1",
        root_name="docs",
        relative_path="readme.md",
        result=result,
    )

    sql = "\n".join(statement for statement, _params in executed)
    assert applied is True
    assert "FOR UPDATE" in sql
    assert "status = 'running'" in sql
    assert "UPDATE source_assets" in sql
    assert "INSERT INTO asset_chunks" in sql


def test_apply_extraction_result_for_job_skips_when_job_was_cancelled(monkeypatch):
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

    result = SimpleNamespace(status="indexed", metadata={}, chunks=(), child_assets=())
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    applied = database.apply_extraction_result_for_job(
        job_id="job-1",
        root_name="docs",
        relative_path="readme.md",
        result=result,
    )

    sql = "\n".join(statement for statement, _params in executed)
    assert applied is False
    assert "FROM capture_jobs" in sql
    assert "FOR UPDATE" in sql
    assert "UPDATE source_assets" not in sql
    assert "INSERT INTO asset_chunks" not in sql


def test_apply_staged_extraction_piece_upserts_chunks_and_enqueues_next(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "FROM capture_jobs" in sql and "WHERE id = %s" in sql and "FOR UPDATE" in sql:
                return ("job-1", "running")
            if "SELECT a.id::text, a.canonical_asset_id::text" in sql:
                return ("asset-1", None)
            if "UPDATE source_assets" in sql:
                return ("asset-1",)
            if "SELECT id::text FROM asset_chunks" in sql:
                return ("old-chunk",)
            if "INSERT INTO asset_chunks" in sql:
                return ("new-chunk",)
            if "INSERT INTO capture_jobs" in sql:
                return ("job-next", "pending")
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

    result = SimpleNamespace(
        status="staged",
        metadata={
            "extractor": "media_segment",
            "staged_extraction": {
                "status": "piece_completed",
                "complete": False,
                "next_job": {
                    "job_type": "corpus_extract_media_segment",
                    "payload": {"segment_index": 1},
                },
            },
        },
        chunks=(AssetChunk(chunk_index=0, title="segment", body="body", modality="transcript"),),
        child_assets=(),
    )
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    applied = database.apply_staged_extraction_piece_for_job(
        job_id="job-1",
        root_name="media",
        relative_path="clip.mp4",
        result=result,
    )

    sql = "\n".join(statement for statement, _params in executed)
    params_json = "\n".join(str(params) for _statement, params in executed)
    assert applied is True
    assert "FOR UPDATE" in sql
    assert "extraction_status = %s" in sql
    assert "DELETE FROM embeddings" not in sql
    assert "DELETE FROM asset_chunks WHERE asset_id = %s AND chunk_index = %s" in sql
    assert "INSERT INTO asset_chunks" in sql
    assert "INSERT INTO capture_jobs" in sql
    assert "corpus_extract_media_segment" in params_json
    assert "segment_index" in params_json


def test_apply_staged_extraction_plan_persists_parent_chunks_before_child_jobs(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "FROM capture_jobs" in sql and "WHERE id = %s" in sql and "FOR UPDATE" in sql:
                return ("job-1", "running")
            if "SELECT a.id::text, a.canonical_asset_id::text" in sql:
                return ("asset-1", None)
            if "UPDATE source_assets" in sql:
                return ("asset-1",)
            if "SELECT id::text FROM asset_chunks" in sql:
                return None
            if "INSERT INTO asset_chunks" in sql:
                return ("embedded-chunk",)
            if "INSERT INTO capture_jobs" in sql:
                return ("job-next", "pending")
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

    result = SimpleNamespace(
        status="staged",
        metadata={
            "extractor": "pdf",
            "staged_jobs": [
                {
                    "job_type": "corpus_extract_pdf_ocr_pages",
                    "payload": {"pages": [2], "page_count": 2, "chunks_seen": 1},
                }
            ],
        },
        chunks=(AssetChunk(chunk_index=0, title="mixed.pdf", body="Embedded text", modality="text"),),
        child_assets=(),
    )
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    applied = database.apply_staged_extraction_plan_for_job(
        job_id="job-1",
        root_name="docs",
        relative_path="mixed.pdf",
        result=result,
    )

    sql = "\n".join(statement for statement, _params in executed)
    params_json = "\n".join(str(params) for _statement, params in executed)
    assert applied is True
    assert "DELETE FROM asset_chunks WHERE asset_id = %s" in sql
    assert "INSERT INTO asset_chunks" in sql
    assert "INSERT INTO capture_jobs" in sql
    assert "Embedded text" in params_json
    assert "corpus_extract_pdf_ocr_pages" in params_json


def test_requeue_metadata_only_source_assets_creates_scoped_standalone_extract_jobs(monkeypatch):
    executed = []
    rows = [
        ("asset-video", "media", "clip.mp4", "video"),
        ("asset-pdf", "docs", "scan.pdf", "document"),
    ]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            sql = executed[-1][0]
            if "FROM source_assets" in sql:
                return rows
            return []

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status" in sql:
                return None
            if "INSERT INTO capture_jobs" in sql:
                return (f"job-{len([item for item in executed if 'INSERT INTO capture_jobs' in item[0]])}", "pending")
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

    result = database.requeue_metadata_only_source_assets(
        root_name="docs",
        extensions=["DOCX", "pdf", ".docx"],
        paths=[r".\General\Pilot.docx", "General/Pilot.docx"],
        limit=10,
    )

    sql = "\n".join(statement for statement, _params in executed)
    params_json = "\n".join(str(params) for _statement, params in executed)
    assert result["queued"] == 2
    assert result["root_name"] == "docs"
    assert result["extensions"] == [".docx", ".pdf"]
    assert result["paths"] == ["General/Pilot.docx"]
    assert result["container_members_excluded"] is True
    assert "a.extraction_status = 'metadata_only'" in sql
    assert "a.deleted_at IS NULL" in sql
    assert "NOT (a.metadata ? 'container_asset_id')" in sql
    assert "lower(a.extension) = ANY(%s)" in sql
    assert "a.path = ANY(%s)" in sql
    assert ".docx" in params_json
    assert "General/Pilot.docx" in params_json
    assert "INSERT INTO capture_jobs" in sql
    assert "corpus_extract_video" in params_json
    assert "corpus_extract_document" in params_json


@pytest.mark.parametrize("path", ["../outside.docx", "/outside.docx", r"C:\outside.docx"])
def test_requeue_metadata_only_source_assets_rejects_non_relative_paths(path):
    with pytest.raises(ValueError, match="root-relative"):
        database.requeue_metadata_only_source_assets(root_name="docs", paths=[path])


def test_requeue_svg_source_assets_resets_svg_assets_and_creates_image_jobs(monkeypatch):
    executed = []
    rows = [
        ("asset-svg-1", "docs", "icons/logo.svg"),
        ("asset-svg-2", "docs", "fonts/glyphicons.svg"),
    ]

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            sql = executed[-1][0]
            if "FROM source_assets" in sql:
                return rows
            return []

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status" in sql:
                return None
            if "INSERT INTO capture_jobs" in sql:
                return (f"job-{len([item for item in executed if 'INSERT INTO capture_jobs' in item[0]])}", "pending")
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

    result = database.requeue_svg_source_assets(root_name="docs", limit=25)

    sql = "\n".join(statement for statement, _params in executed)
    params_json = "\n".join(str(params) for _statement, params in executed)
    assert result["queued"] == 2
    assert result["root_name"] == "docs"
    assert result["limit"] == 25
    assert "LOWER(a.extension) = '.svg'" in sql
    assert "COALESCE(a.metadata->>'svg_requeue_reason', '') <> 'svg_renderer_reparse'" in sql
    assert "r.name = %s" in sql
    assert "DELETE FROM asset_chunks WHERE asset_id = %s" in sql
    assert "extraction_status = 'queued'" in sql
    assert "indexed_at = NULL" in sql
    assert "metadata - 'svg' - 'svg_parse' - 'svg_raster' - 'ocr' - 'vision' - 'vision_escalation'" in sql
    assert "INSERT INTO capture_jobs" in sql
    assert "corpus_extract_image" in params_json
    assert "svg_renderer_reparse" in params_json
    assert "icons/logo.svg" in params_json
    assert "fonts/glyphicons.svg" in params_json


def test_invalidate_reprocess_derived_state_requeues_physical_assets_and_marks_index_pending(monkeypatch):
    executed = []
    rows = [
        ("asset-doc", "docs", "scan.pdf", "document"),
        ("asset-video", "docs", "clip.mp4", "video"),
    ]

    class FakeCursor:
        rowcount = 0

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            normalized = " ".join(str(sql).split())
            executed.append((sql, params))
            if "UPDATE capture_jobs" in normalized and "status = 'obsolete'" in normalized:
                self.rowcount = 2
            elif "UPDATE source_assets" in normalized and "metadata->>'container_asset_id'" in normalized:
                self.rowcount = 1
            elif "DELETE FROM asset_chunks" in normalized and "asset_id = ANY" in normalized:
                self.rowcount = 2
            elif "UPDATE source_assets" in normalized and "extraction_status = 'queued'" in normalized:
                self.rowcount = 2
            elif "UPDATE search_index_records" in normalized:
                self.rowcount = 3
            else:
                self.rowcount = 0

        def fetchall(self):
            sql = executed[-1][0]
            if "FROM monitored_roots" in sql:
                return [("root-1", "docs")]
            if "FROM source_assets" in sql and "metadata ? 'container_asset_id'" in sql:
                return rows
            return []

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, status" in sql:
                return None
            if "INSERT INTO capture_jobs" in sql:
                return (f"job-{len([item for item in executed if 'INSERT INTO capture_jobs' in item[0]])}", "pending")
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

    result = database.invalidate_reprocess_derived_state(
        root_name="docs",
        all_roots=False,
        force=True,
        limit=20,
        actor="test",
    )

    sql = "\n".join(statement for statement, _params in executed)
    params_json = "\n".join(str(params) for _statement, params in executed)
    assert result["assets_requeued"] == 2
    assert result["jobs_obsoleted"] == 2
    assert result["container_children_deleted"] == 1
    assert result["chunks_deleted"] == 2
    assert result["search_records_marked"] == 3
    assert "a.deleted_at IS NULL" in sql
    assert "a.canonical_asset_id IS NULL" in sql
    assert "NOT (a.metadata ? 'container_asset_id')" in sql
    assert "message.eml" in sql
    assert "body.html" in sql
    assert "status = 'obsolete'" in sql
    assert "'actor', %s::text" in sql
    assert "DELETE FROM code_references" in sql
    assert "DELETE FROM code_symbols" in sql
    assert "DELETE FROM asset_chunks" in sql
    assert "extraction_status = 'queued'" in sql
    assert "UPDATE search_index_records" in sql
    assert "index_status = 'pending'" in sql
    assert "INSERT INTO capture_jobs" in sql
    assert "corpus_extract_document" in params_json
    assert "corpus_extract_video" in params_json
    assert "maintenance_reprocess_derived_state" in params_json


def test_apply_extraction_result_persists_nested_container_child_metadata(monkeypatch):
    executed = []

    class FakeCursor:
        rowcount = 0

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT a.id::text, a.canonical_asset_id::text" in sql and "a.root_id::text" in sql:
                return ("asset-parent", None, "root-1", "file:///docs/bundle.zip", 123)
            if "SELECT a.id::text, a.canonical_asset_id::text" in sql:
                return ("asset-parent", None)
            if "UPDATE source_assets" in sql:
                return ("asset-parent",)
            if "SELECT id::text FROM source_assets" in sql:
                return None
            if "INSERT INTO source_assets" in sql:
                return ("child-1",)
            if "INSERT INTO asset_chunks" in sql:
                return ("chunk-1",)
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

    child = SimpleNamespace(
        member_path="nested/inner.zip/docs/readme.md",
        file_kind="text",
        mime_type="text/markdown",
        extension=".md",
        size_bytes=20,
        quick_hash="quick-child",
        content_hash="hash-child",
        extraction_tier="inline",
        extraction_status="indexed",
        chunks=(
            AssetChunk(
                chunk_index=0,
                title="docs/readme.md",
                body="Nested archive child body",
                modality="text",
                locator="char:0-25",
                token_estimate=4,
            ),
        ),
        metadata={
            "container_member_path": "nested/inner.zip/docs/readme.md",
            "container_parent_path": "nested/inner.zip",
            "container_depth": 2,
            "embedded_extractor": "text",
        },
    )
    result = SimpleNamespace(status="metadata_only", metadata={"extractor": "container"}, chunks=(), child_assets=(child,))
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    database.apply_extraction_result(root_name="docs", relative_path="bundle.zip", result=result)

    params_json = "\n".join(str(params) for _statement, params in executed)
    assert "bundle.zip/nested/inner.zip/docs/readme.md" in params_json
    assert "container_parent_path" in params_json
    assert "container_depth" in params_json
    assert "embedded_extractor" in params_json


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
    assert "source_assets.extraction_status LIKE 'blocked_%%'" in source


def test_persist_crawl_plan_does_not_requeue_completed_deferred_asset_when_content_hash_matches(
    monkeypatch,
    tmp_path,
):
    executed = []

    class FakeCursor:
        rowcount = 0

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text FROM monitored_roots" in sql:
                return ("root-1",)
            if "INSERT INTO crawl_runs" in sql:
                return ("run-1",)
            if "SELECT id::text, quick_hash, content_hash, extraction_status, extension" in sql:
                return ("asset-1", "quick-before", "sha256-same", "indexed", ".pdf")
            if "SELECT id::text" in sql and "WHERE content_hash" in sql:
                return None
            if "INSERT INTO source_assets" in sql:
                return ("asset-1",)
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

    file_path = tmp_path / "report.pdf"
    file_path.write_bytes(b"unchanged content")
    plan = CrawlPlan(
        root_path=tmp_path,
        assets=[
            DiscoveredAsset(
                path=file_path,
                relative_path="report.pdf",
                file_kind="document",
                mime_type="application/pdf",
                extension=".pdf",
                size_bytes=17,
                mtime_ns=200,
                quick_hash="quick-after-mtime-change",
                content_hash="sha256-same",
                extraction_tier="deferred",
            )
        ],
    )
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.persist_crawl_plan(root_name="docs", plan=plan)

    sql = "\n".join(statement for statement, _params in executed)
    assert result["files_changed"] == 0
    assert result["jobs_queued"] == 0
    assert "INSERT INTO capture_jobs" not in sql


def test_persist_crawl_plan_recovers_unchanged_blocked_asset_to_indexed(monkeypatch, tmp_path):
    executed = []

    class FakeCursor:
        rowcount = 0

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text FROM monitored_roots" in sql:
                return ("root-1",)
            if "INSERT INTO crawl_runs" in sql:
                return ("run-1",)
            if "SELECT id::text, quick_hash, content_hash, extraction_status, extension" in sql:
                return ("asset-1", "quick", "content", "blocked_missing_dependency", ".eml")
            if "SELECT id::text" in sql and "WHERE content_hash" in sql:
                return None
            if "INSERT INTO source_assets" in sql:
                return ("asset-1",)
            if "INSERT INTO asset_chunks" in sql:
                return ("chunk-1",)
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

    mail_path = tmp_path / "message.eml"
    mail_path.write_text("Subject: Ready\n\nBody", encoding="utf-8")
    plan = CrawlPlan(
        root_path=tmp_path,
        assets=[
            DiscoveredAsset(
                path=mail_path,
                relative_path="message.eml",
                file_kind="mail",
                mime_type="message/rfc822",
                extension=".eml",
                size_bytes=20,
                mtime_ns=100,
                quick_hash="quick",
                content_hash="content",
                extraction_tier="inline",
                extraction_status="indexed",
                chunks=(
                    AssetChunk(
                        chunk_index=0,
                        title="Ready",
                        body="Body",
                        token_estimate=1,
                    ),
                ),
            )
        ],
    )
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.persist_crawl_plan(root_name="mail-root", plan=plan)

    sql = "\n".join(statement for statement, _params in executed)
    assert result["chunks_indexed"] == 1
    assert result["files_changed"] == 0
    assert "EXCLUDED.extraction_status = 'indexed'" in sql
    assert "- 'metadata_only_blocked'" in sql
    assert "- 'readiness_status'" in sql
    assert "DELETE FROM asset_chunks WHERE asset_id = %s" in sql
    assert "INSERT INTO asset_chunks" in sql


def test_persist_crawl_plan_requeues_unchanged_blocked_deferred_asset(monkeypatch, tmp_path):
    executed = []

    class FakeCursor:
        rowcount = 0

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text FROM monitored_roots" in sql:
                return ("root-1",)
            if "INSERT INTO crawl_runs" in sql:
                return ("run-1",)
            if "SELECT id::text, quick_hash, content_hash, extraction_status, extension" in sql:
                return ("asset-1", "quick", "content", "blocked_missing_dependency", ".eml")
            if "SELECT id::text" in sql and "WHERE content_hash" in sql:
                return None
            if "INSERT INTO source_assets" in sql:
                return ("asset-1",)
            if "SELECT id::text, status" in sql:
                return None
            if "INSERT INTO capture_jobs" in sql:
                return ("job-mail", "pending")
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

    mail_path = tmp_path / "message.eml"
    mail_path.write_text("Subject: Ready\n\nBody", encoding="utf-8")
    plan = CrawlPlan(
        root_path=tmp_path,
        assets=[
            DiscoveredAsset(
                path=mail_path,
                relative_path="message.eml",
                file_kind="mail",
                mime_type="message/rfc822",
                extension=".eml",
                size_bytes=20,
                mtime_ns=100,
                quick_hash="quick",
                content_hash="content",
                extraction_tier="deferred",
            )
        ],
    )
    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())

    result = database.persist_crawl_plan(root_name="mail-root", plan=plan)

    sql = "\n".join(statement for statement, _params in executed)
    assert result["files_changed"] == 0
    assert result["jobs_queued"] == 1
    assert "EXCLUDED.extraction_status IN ('indexed', 'queued')" in sql
    assert "INSERT INTO capture_jobs" in sql


def test_persist_crawl_plan_requeues_legacy_metadata_only_documents():
    source = Path(database.__file__).read_text(encoding="utf-8")
    expected_extensions = {
        ".doc",
        ".rtf",
        ".dot",
        ".docm",
        ".dotx",
        ".dotm",
        ".xls",
        ".xlt",
        ".xlsb",
        ".xlsm",
        ".xltx",
        ".xltm",
        ".ppt",
        ".pot",
        ".pps",
        ".pptm",
        ".potx",
        ".potm",
        ".ppsx",
        ".ppsm",
        ".odt",
        ".ott",
        ".ods",
        ".ots",
        ".odp",
        ".otp",
    }

    assert "source_assets.extraction_status = 'metadata_only'" in source
    assert "EXCLUDED.extraction_status = 'queued'" in source
    assert database.REQUEUE_DOCUMENT_EXTENSIONS == expected_extensions


def test_corpus_search_filters_metadata_only_assets():
    source = Path(database.__file__).read_text(encoding="utf-8")
    search_function = source.split("def search_corpus_chunks", 1)[1].split("def insert_memory", 1)[0]

    assert "a.extraction_status = 'indexed'" in search_function
    assert "a.extraction_status <> 'metadata_only'" not in search_function


def test_replace_asset_chunks_enqueues_corpus_search_index_sync(monkeypatch):
    helper_calls: list[str] = []

    class FakeCursor:
        def __init__(self):
            self._last_sql = ""

        def execute(self, sql, params=()):
            self._last_sql = sql

        def fetchone(self):
            if "SELECT a.path, r.name, r.root_path, r.metadata" in self._last_sql:
                return None
            if "INSERT INTO asset_chunks" in self._last_sql:
                return ("chunk-1",)
            return None

    monkeypatch.setattr(
        database,
        "_enqueue_corpus_search_index_sync_for_asset",
        lambda cur, *, asset_id: helper_calls.append(asset_id),
        raising=False,
    )

    inserted = database._replace_asset_chunks(
        FakeCursor(),
        "asset-1",
        (
            AssetChunk(
                chunk_index=0,
                title="src/app.py::build_invoice",
                body="def build_invoice(): return 1",
                modality="text",
                locator=None,
                token_estimate=5,
                metadata={},
            ),
        ),
    )

    assert inserted == 1
    assert helper_calls == ["asset-1"]


def test_append_or_upsert_asset_chunks_enqueues_corpus_search_index_sync(monkeypatch):
    helper_calls: list[str] = []

    class FakeCursor:
        def __init__(self):
            self._last_sql = ""

        def execute(self, sql, params=()):
            self._last_sql = sql

        def fetchone(self):
            if "SELECT a.path, r.name, r.root_path, r.metadata" in self._last_sql:
                return None
            if "SELECT id::text FROM asset_chunks" in self._last_sql:
                return None
            if "INSERT INTO asset_chunks" in self._last_sql:
                return ("chunk-1",)
            return None

    monkeypatch.setattr(
        database,
        "_enqueue_corpus_search_index_sync_for_asset",
        lambda cur, *, asset_id: helper_calls.append(asset_id),
        raising=False,
    )

    inserted = database._append_or_upsert_asset_chunks(
        FakeCursor(),
        "asset-1",
        (
            AssetChunk(
                chunk_index=0,
                title="src/app.py::build_invoice",
                body="def build_invoice(): return 1",
                modality="text",
                locator=None,
                token_estimate=5,
                metadata={},
            ),
        ),
    )

    assert inserted == 1
    assert helper_calls == ["asset-1"]


def test_managed_mail_chunks_store_private_text_off_db(monkeypatch):
    from flux_llm_kb import mail_content_store

    executed = []
    writes = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT a.path" in sql and "JOIN monitored_roots" in sql:
                return ("export-1/body.txt", "mail-gmail", "E:/FluxMail/ready", {"mail_profile": "gmail"})
            if "INSERT INTO asset_chunks" in sql:
                return ("chunk-1",)
            return None

    def fake_write_mail_content(**kwargs):
        writes.append(kwargs)
        return {
            "storage": "disk_sidecar",
            "sha256": "abc123",
            "relative_path": "mail/chunks/abc123.json",
            "redacted_from_db": True,
        }

    monkeypatch.setattr(mail_content_store, "write_mail_content", fake_write_mail_content)

    inserted = database._replace_asset_chunks(
        FakeCursor(),
        "asset-1",
        (
            AssetChunk(
                chunk_index=0,
                title="body.txt",
                body="Please review the private mail body.",
                token_estimate=7,
            ),
        ),
    )

    insert_params = next(params for sql, params in executed if "INSERT INTO asset_chunks" in sql)
    metadata = json.loads(insert_params[7])
    assert inserted == 1
    assert insert_params[3] == ""
    assert writes[0]["text"] == "Please review the private mail body."
    assert metadata["sidecar_ref"]["source"] == "managed_mail"
    assert metadata["sidecar_ref"]["storage"] == "disk_sidecar"
    assert metadata["sidecar_ref"]["sha256"] == "abc123"
    assert metadata["sidecar_ref"]["redacted_from_db"] is True


def test_replace_asset_chunks_strips_postgres_nul_from_text_fields(monkeypatch):
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT a.path" in sql and "JOIN monitored_roots" in sql:
                return None
            if "INSERT INTO asset_chunks" in sql:
                return ("chunk-1",)
            return None

    inserted = database._replace_asset_chunks(
        FakeCursor(),
        "asset-1",
        (
            AssetChunk(
                chunk_index=0,
                title="nul\x00title",
                body="body\x00text\nArabic نص",
                modality="te\x00xt",
                locator="char:0\x00-9",
                token_estimate=3,
                metadata={"bad\x00key": "bad\x00value"},
            ),
        ),
    )

    insert_params = next(params for sql, params in executed if "INSERT INTO asset_chunks" in sql)
    metadata = json.loads(insert_params[7])
    assert inserted == 1
    assert insert_params[2] == "nultitle"
    assert insert_params[3] == "bodytext\nArabic نص"
    assert insert_params[4] == "text"
    assert insert_params[5] == "char:0-9"
    assert metadata == {"badkey": "badvalue"}
    assert "INSERT INTO embeddings" not in "\n".join(sql for sql, _params in executed)


def test_semantic_duplicate_candidates_require_indexed_assets_without_embedding_join():
    source = Path(database.__file__).read_text(encoding="utf-8")
    semantic_function = source.split("def _fetch_semantic_duplicate_candidates", 1)[1].split("elif memory_class == \"episode\"", 1)[0]

    assert "a.extraction_status = 'indexed'" in semantic_function
    assert "JOIN embeddings" not in semantic_function


def test_repair_extracted_corpus_asset_statuses_purges_stale_chunks_and_mail_internals():
    source = Path(database.__file__).read_text(encoding="utf-8")
    repair_function = source.split("def repair_extracted_corpus_asset_statuses", 1)[1].split("def worker_family_stats", 1)[0]

    assert "stale_chunks" in repair_function
    assert "DELETE FROM embeddings" not in repair_function
    assert "DELETE FROM asset_chunks" in repair_function
    assert "a.extraction_status NOT IN ('indexed', 'processing_staged')" in repair_function
    assert "a.extraction_status <> 'indexed'" not in repair_function
    assert "message.eml" in repair_function
    assert "message.msg" in repair_function
    assert "body.html" in repair_function
    assert "mail_plaintext_chunks_repaired" in repair_function
    assert "c.body <> ''" in repair_function
    assert "body = ''" in repair_function


def test_corpus_status_queries_include_lock_tolerant_states():
    source = Path(database.__file__).read_text(encoding="utf-8")

    assert "asset.extraction_status" in source
    assert "pending_stable" in source
    assert "retrying_locked" in source
    assert "blocked_locked" in source
    assert "retrying_vss_failed" in source
    assert "blocked_vss_failed" in source
    assert "status IN ('pending', 'retrying_locked', 'retrying_vss_failed')" in source


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


def test_imap_mail_claim_query_does_not_reference_update_alias_inside_from_join():
    source = Path(database.__file__).read_text(encoding="utf-8")
    claim_function = source.split("def claim_due_imap_sync_runs", 1)[1].split("def mark_mail_sync_run_running", 1)[0]

    assert "SELECT r.id, r.profile_id" in claim_function
    assert "JOIN mail_profiles p ON p.id = claimable.profile_id" in claim_function
    update_from = claim_function.split("FROM claimable", 1)[1]
    assert "JOIN mail_profiles p ON p.id = r.profile_id" not in update_from


def test_outlook_claim_query_does_not_reference_update_alias_inside_from_join():
    source = Path(database.__file__).read_text(encoding="utf-8")
    claim_function = source.split("def claim_outlook_sync_request", 1)[1].split("def enqueue_due_outlook_sync_commands", 1)[0]

    assert "SELECT r.id, p.name" in claim_function
    update_from = claim_function.split("FROM request", 1)[1]
    assert "JOIN mail_profiles p ON p.id = r.profile_id" not in update_from


def test_outlook_claim_query_recovers_stale_claims_from_dead_hosts():
    source = Path(database.__file__).read_text(encoding="utf-8")
    claim_function = source.split("def claim_outlook_sync_request", 1)[1].split("def enqueue_due_outlook_sync_commands", 1)[0]

    assert "stale_claimed" in claim_function
    assert "r.status = 'claimed'" in claim_function
    assert "r.claimed_at < now() - interval '15 minutes'" not in claim_function
    assert "r.claimed_by = %s" in claim_function
    assert "h.heartbeat_at >= now() - interval '120 seconds'" in claim_function
    assert "claimed_by = NULL" in claim_function


def test_dashboard_background_job_readers_cover_durable_sources():
    source = Path(database.__file__).read_text(encoding="utf-8")

    for function_name in [
        "list_message_outbox_jobs",
        "list_message_inbox_jobs",
        "list_callback_delivery_jobs",
        "list_runtime_control_requests",
        "list_gpu_lease_jobs",
        "list_gpu_eviction_jobs",
    ]:
        assert f"def {function_name}" in source

    outbox_function = source.split("def list_message_outbox_jobs", 1)[1].split("def list_message_inbox_jobs", 1)[0]
    assert "FROM message_outbox" in outbox_function
    assert "status IN ('pending', 'publishing', 'failed')" in outbox_function

    inbox_function = source.split("def list_message_inbox_jobs", 1)[1].split("def list_callback_delivery_jobs", 1)[0]
    assert "FROM message_inbox" in inbox_function
    assert "status IN ('processing', 'failed')" in inbox_function

    callback_function = source.split("def list_callback_delivery_jobs", 1)[1].split("def add_monitored_root", 1)[0]
    assert "FROM callback_deliveries" in callback_function
    assert "status IN ('pending', 'running', 'retrying', 'failed', 'blocked', 'delivered')" in callback_function

    runtime_function = source.split("def list_runtime_control_requests", 1)[1].split("def insert_mail_profile", 1)[0]
    assert "FROM runtime_control_requests" in runtime_function
    assert "broker_message_id" in runtime_function

    gpu_lease_function = source.split("def list_gpu_lease_jobs", 1)[1].split("def list_gpu_eviction_jobs", 1)[0]
    assert "FROM gpu_leases" in gpu_lease_function
    assert "broker_message_id" in gpu_lease_function

    gpu_eviction_function = source.split("def list_gpu_eviction_jobs", 1)[1].split("def _enqueue_gpu_eviction_event_with_cursor", 1)[0]
    assert "FROM gpu_evictions" in gpu_eviction_function
    assert "queued_at" in gpu_eviction_function


def test_outlook_claim_query_recovers_stale_claims_from_stale_heartbeat_without_claim_age_gate():
    source = Path(database.__file__).read_text(encoding="utf-8")
    claim_function = source.split("def claim_outlook_sync_request", 1)[1].split("def cancel_outlook_sync_request", 1)[0]
    stale_claimed = claim_function.split("WITH stale_claimed AS", 1)[1].split("UPDATE outlook_sync_requests r", 1)[0]

    assert "r.status = 'claimed'" in stale_claimed
    assert "r.claimed_at < now() - interval '15 minutes'" not in stale_claimed
    assert "h.heartbeat_at >= now() - interval '120 seconds'" in stale_claimed
    assert "claimed_by = NULL" in claim_function


def test_requeue_stale_pending_outlook_requests_refreshes_broker_message(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchall(self):
            sql, _params = executed[-1]
            if "FROM outlook_sync_requests r" in sql and "r.status = 'pending'" in sql:
                return [("req-1", "outlook-catchup", "dashboard")]
            return []

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

    enqueued = []

    def fake_enqueue(cur, **kwargs):
        enqueued.append(kwargs)
        return {"message_id": "msg-new"}

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(database, "_enqueue_message_outbox_with_cursor", fake_enqueue)

    result = database.requeue_stale_pending_outlook_sync_requests(
        min_age_seconds=300,
        limit=5,
        requested_by="repair-test",
    )

    assert result == {
        "requeued": 1,
        "requests": [{"id": "req-1", "profile_name": "outlook-catchup", "message_id": "msg-new"}],
    }
    assert enqueued[0]["routing_key"] == "mail.outlook.sync"
    assert enqueued[0]["payload"]["request_id"] == "req-1"
    update_sql = "\n".join(sql for sql, _params in executed if "UPDATE outlook_sync_requests" in sql)
    assert "broker_message_id = %s" in update_sql
    assert "requeued_from_stale_pending" in update_sql


def test_cancel_outlook_sync_request_blocks_claimed_mid_execution(monkeypatch):
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
            if "FOR UPDATE" in sql:
                return ("req-1", "outlook-catchup", "claimed")
            raise AssertionError("claimed request must not be updated")

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

    result = database.cancel_outlook_sync_request(request_id="req-1", actor="tester")

    assert result["cancelled"] is False
    assert result["status"] == "claimed"
    assert "mid-execution" in result["error"]
    assert len(executed) == 1


def test_cancel_outlook_sync_request_marks_pending_cancelled(monkeypatch):
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
            if "FOR UPDATE" in sql:
                return ("req-1", "outlook-catchup", "pending")
            if "UPDATE outlook_sync_requests" in sql:
                return ("req-1", "outlook-catchup", "cancelled")
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

    result = database.cancel_outlook_sync_request(request_id="req-1", actor="tester")

    sql = "\n".join(item[0] for item in executed)
    assert result == {"id": "req-1", "profile_name": "outlook-catchup", "status": "cancelled", "cancelled": True}
    assert "status = 'cancelled'" in sql
    assert "cancelled_by" in sql


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


def test_mail_message_exists_checks_profile_folder_and_source_message(monkeypatch):
    executed = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return (True,)

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

    assert database.mail_message_exists(
        profile_name="outlook-catchup",
        source_folder="Mailbox - Me\\Inbox\\MOHESR",
        source_message_id="outlook:entry-1",
    ) is True
    sql, params = executed[0]
    assert "mail_messages" in sql
    assert "mail_profiles" in sql
    assert params == ("outlook-catchup", "Mailbox - Me\\Inbox\\MOHESR", "outlook:entry-1")


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
    assert "re.relation_type = ANY" in traversal
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


def test_retention_policies_list_and_update_are_audited(monkeypatch):
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
                ("policy-claim", "claim", 120, 0.35, "review", "system", {}, timestamp, timestamp),
                ("policy-episode", "episode", 180, 0.25, "deprioritize", "system", {}, timestamp, timestamp),
            ]

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text, memory_class" in sql:
                return ("policy-claim", "claim", 120, 0.35, "review", "system", {}, timestamp, timestamp)
            if "INSERT INTO retention_policies" in sql:
                return ("policy-claim", "claim", 90, 0.45, "deprioritize", "tester", {"reason": "live review"}, timestamp, timestamp)
            if "INSERT INTO audit_events" in sql:
                return ("audit-1", "retention.policy_updated")
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

    policies = database.list_retention_policies()
    result = database.set_retention_policy(
        memory_class="claim",
        half_life_days=90,
        min_confidence=0.45,
        action="deprioritize",
        actor="tester",
        reason="live review",
    )

    assert policies[0]["memory_class"] == "claim"
    assert policies[0]["min_confidence"] == 0.35
    assert result["policy"]["half_life_days"] == 90
    assert result["audit_event"] == {"id": "audit-1", "event_type": "retention.policy_updated"}
    audit_sql, audit_params = next(item for item in executed if "INSERT INTO audit_events" in item[0])
    assert "retention.policy_updated" in audit_sql
    assert audit_params[0] == "tester"
    assert audit_params[1] == "policy-claim"
    assert json.loads(audit_params[2])["reason"] == "live review"


def test_retention_policy_update_rejects_invalid_inputs_without_database():
    with pytest.raises(ValueError, match="memory_class"):
        database.set_retention_policy(
            memory_class="mail",
            half_life_days=90,
            min_confidence=0.3,
            action="review",
            actor="tester",
            reason="invalid",
        )
    with pytest.raises(ValueError, match="half_life_days"):
        database.set_retention_policy(
            memory_class="claim",
            half_life_days=0,
            min_confidence=0.3,
            action="review",
            actor="tester",
            reason="invalid",
        )
    with pytest.raises(ValueError, match="min_confidence"):
        database.set_retention_policy(
            memory_class="claim",
            half_life_days=90,
            min_confidence=1.5,
            action="review",
            actor="tester",
            reason="invalid",
        )
    with pytest.raises(ValueError, match="action"):
        database.set_retention_policy(
            memory_class="claim",
            half_life_days=90,
            min_confidence=0.3,
            action="delete",
            actor="tester",
            reason="invalid",
        )
    with pytest.raises(ValueError, match="reason"):
        database.set_retention_policy(
            memory_class="claim",
            half_life_days=90,
            min_confidence=0.3,
            action="review",
            actor="tester",
            reason="   ",
        )


def test_retention_quality_report_aggregates_sanitized_candidates(monkeypatch):
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
            sql = executed[-1][0]
            if "FROM retention_policies" in sql:
                return [
                    ("policy-claim", "claim", 120, 0.5, "review", "system", {}, timestamp, timestamp),
                    ("policy-episode", "episode", 180, 0.7, "deprioritize", "system", {}, timestamp, timestamp),
                    ("policy-corpus", "corpus", 365, 0.2, "review", "system", {}, timestamp, timestamp),
                ]
            if "FROM claims c" in sql:
                return [
                    (
                        "claim-1",
                        "Flux",
                        "uses",
                        "PostgreSQL",
                        0.42,
                        "stale",
                        "deprioritize",
                        timestamp,
                        timestamp,
                        1,
                        None,
                    )
                ]
            if "FROM episodes" in sql:
                return [
                    (
                        "episode-1",
                        "Roadmap session",
                        0.4,
                        timestamp,
                        timestamp,
                        None,
                    )
                ]
            if "FROM source_assets" in sql:
                return [
                    (
                        "asset-1",
                        "docs/blocked.pdf",
                        0.8,
                        "blocked_missing_dependency",
                        "metadata_only",
                        True,
                        None,
                        timestamp,
                        timestamp,
                    )
                ]
            if "FROM semantic_duplicate_clusters" in sql:
                return [
                    (
                        "cluster-1",
                        "corpus",
                        "Architecture",
                        2,
                        "docs",
                        timestamp,
                    )
                ]
            return []

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

    report = database.retention_quality_report(limit=10)

    assert report["summary"]["total"] == 4
    assert report["summary"]["needs_review"] == 4
    assert report["summary"]["by_class"] == {"claim": 1, "episode": 1, "corpus": 2}
    assert report["summary"]["by_bucket"]["review"] == 1
    assert report["summary"]["by_bucket"]["deprioritize"] == 3
    assert report["candidates"][0]["memory_class"] == "claim"
    assert report["candidates"][0]["label"] == "Flux uses PostgreSQL"
    assert report["candidates"][1]["label"] == "Roadmap session"
    assert "summary" not in report["candidates"][1]
    assert "body" not in json.dumps(report)
    assert report["candidates"][2]["reason"] == "blocked_missing_dependency"
    semantic_candidates = [item for item in report["candidates"] if item["reason"] == "semantic_near_duplicate"]
    assert semantic_candidates[0]["label"] == "Semantic duplicates: Architecture"
    assert semantic_candidates[0]["metadata"] == {"root_name": "docs", "suppressed_count": 2}


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
    assert jobs[0]["payload"] == {"status": "pending_review", "path": "session.json"}


def test_list_capture_review_jobs_filters_explicit_status(monkeypatch):
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
                    "approved",
                    {"status": "approved", "path": "E:\\Private\\session.json", "content": "raw text"},
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

    jobs = database.list_capture_review_jobs(status="approved", limit=25)

    sql, params = executed[0]
    assert "payload->>'status' = %s" in sql
    assert "status = %s" in sql
    assert params == ("approved", "approved", 25)
    assert jobs[0]["payload"] == {"status": "approved", "path": "session.json"}


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
        "path": "session.json",
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


def test_service_ingests_approved_codex_backfill_with_personal_text_and_provenance(monkeypatch, tmp_path):
    from flux_llm_kb.service import KnowledgeService

    source = tmp_path / "turn.json"
    sensitive_assignment = "password" + "=sample"
    source.write_text(
        json.dumps(
            {
                "title": "Session decision",
                "body": f"Use PostgreSQL for durable storage. {sensitive_assignment}",
                "session_id": "session-1",
                "turn_id": "turn-1",
                "cwd": "E:\\LLM KB",
                "root_name": "llm-kb",
            }
        ),
        encoding="utf-8",
    )
    inserted = []
    updates = []
    audits = []

    monkeypatch.setattr(
        database,
        "list_capture_ingestion_jobs",
        lambda **kwargs: [
            {
                "id": "job-1",
                "job_type": "codex_backfill",
                "status": "approved",
                "payload": {
                    "status": "approved",
                    "path": str(source),
                    "review": {"audit_event_id": "audit-review-1"},
                },
            }
        ],
    )
    monkeypatch.setattr(database, "codex_backfill_source_hash_exists", lambda **_kwargs: False)
    monkeypatch.setattr(
        database,
        "insert_episode",
        lambda **kwargs: inserted.append(kwargs) or "episode-1",
    )
    monkeypatch.setattr(
        database,
        "update_capture_job_ingestion",
        lambda **kwargs: updates.append(kwargs)
        or {
            "id": kwargs["job_id"],
            "job_type": "codex_backfill",
            "status": kwargs["status"],
            "payload": {"status": kwargs["status"], "ingestion": kwargs["ingestion"]},
        },
    )
    monkeypatch.setattr(database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    result = KnowledgeService().ingest_capture_review_jobs(limit=5, actor="tester")

    assert result["processed"] == 1
    assert result["ingested"] == 1
    assert result["settings_mutated"] is False
    assert inserted[0]["title"] == "Session decision"
    assert inserted[0]["summary"] == f"Use PostgreSQL for durable storage. {sensitive_assignment}"
    metadata = inserted[0]["metadata"]
    assert metadata["source"] == "codex_backfill"
    assert metadata["capture_review_job_id"] == "job-1"
    assert metadata["review_audit_event_id"] == "audit-review-1"
    assert metadata["source_leaf"] == "turn.json"
    assert metadata["session_id"] == "session-1"
    assert metadata["turn_id"] == "turn-1"
    assert metadata["redactions"] == []
    assert updates[0]["status"] == "completed"
    assert updates[0]["ingestion"]["episode_ids"] == ["episode-1"]
    assert updates[0]["ingestion"]["status"] == "ingested"
    assert audits[0]["event_type"] == "capture.ingested"


def test_service_capture_ingestion_dry_run_and_duplicate_skip(monkeypatch, tmp_path):
    from flux_llm_kb.service import KnowledgeService

    source = tmp_path / "turn.md"
    source.write_text("# Session decision\n\nUse PostgreSQL for durable storage.", encoding="utf-8")
    inserted = []
    updates = []

    monkeypatch.setattr(
        database,
        "list_capture_ingestion_jobs",
        lambda **_kwargs: [
            {
                "id": "job-1",
                "job_type": "codex_backfill",
                "status": "approved",
                "payload": {"status": "approved", "path": str(source)},
            }
        ],
    )
    monkeypatch.setattr(database, "codex_backfill_source_hash_exists", lambda **_kwargs: True)
    monkeypatch.setattr(database, "insert_episode", lambda **kwargs: inserted.append(kwargs) or "episode-1")
    monkeypatch.setattr(database, "update_capture_job_ingestion", lambda **kwargs: updates.append(kwargs) or {})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    dry_run = KnowledgeService().ingest_capture_review_jobs(limit=5, dry_run=True)
    result = KnowledgeService().ingest_capture_review_jobs(limit=5)

    assert dry_run["dry_run"] is True
    assert dry_run["jobs"][0]["ingestion"]["status"] == "would_skip"
    assert result["skipped"] == 1
    assert inserted == []
    assert updates[0]["status"] == "completed"
    assert updates[0]["ingestion"]["status"] == "skipped"
    assert updates[0]["ingestion"]["skip_reasons"] == ["duplicate_source_hash"]


def test_service_capture_ingestion_blocks_missing_source(monkeypatch, tmp_path):
    from flux_llm_kb.service import KnowledgeService

    missing = tmp_path / "missing.json"
    updates = []
    audits = []

    monkeypatch.setattr(
        database,
        "list_capture_ingestion_jobs",
        lambda **_kwargs: [
            {
                "id": "job-1",
                "job_type": "codex_backfill",
                "status": "approved",
                "payload": {"status": "approved", "path": str(missing)},
            }
        ],
    )
    monkeypatch.setattr(database, "update_capture_job_ingestion", lambda **kwargs: updates.append(kwargs) or {})
    monkeypatch.setattr(database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    result = KnowledgeService().ingest_capture_review_jobs(limit=5)

    assert result["blocked"] == 1
    assert updates[0]["status"] == "blocked_missing_dependency"
    assert updates[0]["ingestion"]["status"] == "blocked_missing_dependency"
    assert updates[0]["ingestion"]["error"] == "source_missing"
    assert audits[0]["event_type"] == "capture.ingestion_failed"


@pytest.mark.parametrize(
    ("exception", "expected"),
    [
        (TimeoutError("Snowflake timed out"), ("embedding_timeout", None)),
        (ModelRunnerBusy("model runner busy"), ("model_runner_busy", None)),
        (ModelRunnerError("HTTP 503 model runner timeout"), ("model_runner_timeout", None)),
        (GpuLeaseTimeout("lease timed out"), ("gpu_lease_timeout", None)),
        (GpuLeaseDeferred("inventory incomplete", capacity_state="inventory_incomplete"), ("gpu_capacity", "inventory_incomplete")),
        (GpuLeaseDeferred("reconciliation required", capacity_state="reconciliation_required"), ("gpu_capacity", "reconciliation_required")),
    ],
)
def test_retryable_vespa_embedding_failure_classifier_accepts_only_temporary_embedding_failures(exception, expected):
    assert database._classify_retryable_vespa_embedding_failure(exception) == expected


def test_retryable_vespa_embedding_failure_classifier_leaves_unschedulable_visible():
    assert database._classify_retryable_vespa_embedding_failure(
        GpuLeaseRejected("cannot fit", capacity_state="unschedulable")
    ) is None


def test_retryable_vespa_embedding_failure_classifier_leaves_scheduler_configuration_errors_visible():
    assert database._classify_retryable_vespa_embedding_failure(
        ModelRunnerError("scheduler configuration is invalid")
    ) is None
