from datetime import datetime, timezone
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from flux_llm_kb import database
from flux_llm_kb.crawler import AssetChunk
from flux_llm_kb.database import forget_episode
from flux_llm_kb.embeddings import EmbeddingInput


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


def test_code_index_status_filters_selected_root_without_join_leakage(monkeypatch):
    executed: list[tuple[str, tuple]] = []
    result_sets = [
        [("app", 3, 5, 7, 11, 1, 2)],
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

    main_sql, main_params = executed[0]
    assert "LEFT JOIN source_assets a ON a.root_id = r.id AND a.deleted_at IS NULL" in main_sql
    assert "WHERE r.name = %s" in main_sql
    assert "LEFT JOIN source_assets a ON a.root_id = r.id AND a.deleted_at IS NULL AND r.name" not in main_sql
    assert main_params == ("app",)
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
    assert "telemetry = telemetry || %s::jsonb" in sql
    assert params == (42, json.dumps({"family": "media"}, sort_keys=True), "job-1")


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
    assert params[0] == json.dumps({"archive": 2, "media": 1}, sort_keys=True)


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
            return ("docs/readme.md", 12, 42, "quick", "content", {"fixture": "text-heavy"})

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
    assert "SELECT path, size_bytes, mtime_ns, quick_hash, content_hash, metadata" in sql
    manifest_params = next(params for statement, params in executed if "INSERT INTO crawl_path_manifests" in statement)
    assert json.loads(manifest_params[-1]) == {"fixture": "text-heavy"}
    assert row["content_hash"] == "content"


def test_search_corpus_chunks_includes_freshness_stream():
    source = Path(database.__file__).read_text(encoding="utf-8")
    function = source.split("def search_corpus_chunks", 1)[1].split("\ndef ", 1)[0]

    assert "corpus_lexical" in function
    assert "corpus_fuzzy" in function
    assert "corpus_vector" in function
    assert "corpus_trust" in function
    assert "corpus_freshness" in function
    assert "EXTRACT(EPOCH FROM (now() - c.updated_at))" in function


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
                    "flux-hash-v1:cosine",
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


def test_upsert_claim_records_claim_embedding_for_semantic_duplicate_refresh():
    source = Path(database.__file__).read_text(encoding="utf-8")
    function = source.split("def upsert_claim", 1)[1].split("\ndef ", 1)[0]

    assert "_embedding_result_for_text" in function
    assert 'owner_table="claims"' in function
    assert 'text=f"{subject_name}\\n{predicate}\\n{object_text}"' in function


def test_enqueue_embedding_jobs_creates_corpus_embed_job(monkeypatch):
    executed: list[tuple[str, object]] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return ("job-embed",)

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

    result = database.enqueue_embedding_jobs(owner_class="corpus", root_name="docs", stale_only=True, limit=25)

    sql, params = executed[0]
    payload = json.loads(params[1])
    assert "INSERT INTO capture_jobs" in sql
    assert "corpus_embed" in params
    assert payload == {"owner_class": "corpus", "root_name": "docs", "stale_only": True, "limit": 25}
    assert params[2:6] == ("embedding", "gpu", 35, 300)
    assert result == {
        "queued": 1,
        "job_id": "job-embed",
        "owner_class": "corpus",
        "root_name": "docs",
        "stale_only": True,
        "limit": 25,
    }


def test_refresh_embeddings_batches_candidates_and_replaces_vectors(monkeypatch):
    replaced = []

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

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(
        database,
        "_fetch_embedding_inputs",
        lambda _cur, *, owner_class, root_name, stale_only, limit: [
            EmbeddingInput("asset_chunks", "chunk-1", "Chunk One\nbody", "flux-hash-v1", 1536),
            EmbeddingInput("claims", "claim-1", "subject predicate object", "flux-hash-v1", 1536),
        ],
    )
    monkeypatch.setattr(database, "_replace_embeddings", lambda _cur, results: replaced.extend(results) or len(results))

    result = database.refresh_embeddings(owner_class="all", root_name="docs", stale_only=True, limit=20)

    assert result["owner_class"] == "all"
    assert result["root_name"] == "docs"
    assert result["stale_only"] is True
    assert result["requested"] == 2
    assert result["vectors"] == 2
    assert result["skipped_unchanged"] == 0
    assert result["batches"] == 1
    assert result["cache_misses"] == 2
    assert result["cache_hits"] == 0
    assert result["provider"] == "hash"
    assert result["model"] == "flux-hash-v1"
    assert result["dimensions"] == 1536
    assert [item.owner_id for item in replaced] == ["chunk-1", "claim-1"]


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
    assert "status IN ('failed', 'blocked_missing_dependency', 'blocked_locked', 'retrying_locked')" in sql
    assert executed[0][1] == ("operator retry", "job-1")


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
    monkeypatch.setattr(database, "embed_text", lambda _text: [0.0, 0.0, 0.0])
    monkeypatch.setattr(database, "to_pgvector_literal", lambda _embedding: "[0,0,0]")

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
    monkeypatch.setattr(database, "embed_text", lambda _text: [0.0, 0.0, 0.0])
    monkeypatch.setattr(database, "to_pgvector_literal", lambda _embedding: "[0,0,0]")

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
    assert "source_assets.extraction_status IN ('indexed', 'metadata_only', 'blocked_missing_dependency')" in source


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


def test_service_ingests_approved_codex_backfill_with_redaction_and_provenance(monkeypatch, tmp_path):
    from flux_llm_kb.service import KnowledgeService

    source = tmp_path / "turn.json"
    source.write_text(
        json.dumps(
            {
                "title": "Session decision",
                "body": "Use PostgreSQL for durable storage. password=secret",
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
    assert inserted[0]["summary"] == "Use PostgreSQL for durable storage. password=[REDACTED:password_assignment]"
    metadata = inserted[0]["metadata"]
    assert metadata["source"] == "codex_backfill"
    assert metadata["capture_review_job_id"] == "job-1"
    assert metadata["review_audit_event_id"] == "audit-review-1"
    assert metadata["source_leaf"] == "turn.json"
    assert metadata["session_id"] == "session-1"
    assert metadata["turn_id"] == "turn-1"
    assert metadata["redactions"] == ["password_assignment"]
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
