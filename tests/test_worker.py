import json
import threading
import time
from contextlib import contextmanager
from types import SimpleNamespace

import pytest

from flux_llm_kb import database, service as service_module, worker
from flux_llm_kb.service import KnowledgeService


def test_process_claimed_corpus_job_wraps_subprocess_capture_context(monkeypatch):
    from flux_llm_kb import processes

    events = []

    @contextmanager
    def fake_capture(job_id, **kwargs):
        events.append(("enter", job_id, kwargs))
        try:
            yield
        finally:
            events.append(("exit", job_id, kwargs))

    monkeypatch.setattr(processes, "capture_job_tool_invocations", fake_capture)
    monkeypatch.setattr(
        worker,
        "process_corpus_job",
        lambda job: events.append(("process", job["id"], {})) or worker.JobProcessResult(status="indexed", telemetry={"parser_cache_hits": 1}),
    )

    job, duration_ms, result = KnowledgeService()._process_claimed_corpus_job(
        {"id": "job-1", "job_type": "corpus_extract_pdf", "payload": {"root_name": "docs", "path": "a.pdf"}}
    )

    assert job["id"] == "job-1"
    assert duration_ms >= 0
    assert result.status == "indexed"
    assert events[0] == ("enter", "job-1", {})
    assert events[1] == ("process", "job-1", {})
    assert events[2] == ("exit", "job-1", {})


def test_backfill_processes_corpus_sync_root_jobs_with_progress(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "progress": [], "recovered": [], "repaired": [], "cleared_errors": [], "purged": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-sync",
                "job_type": "corpus_sync_root",
                "job_family": "general",
                "resource_class": "cpu",
                "payload": {"root_name": "mail-outlook-mohesr", "reason": "outlook_spool_sync"},
                "attempts": 1,
            }
        ],
    )
    monkeypatch.setattr(database, "recover_stale_running_corpus_jobs", lambda **kwargs: calls["recovered"].append(kwargs) or {"recovered": 0})
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "update_corpus_job_progress", lambda **kwargs: calls["progress"].append(kwargs), raising=False)
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(
        database,
        "purge_expired_capture_jobs",
        lambda **kwargs: calls["purged"].append(kwargs) or {"purged": 3, "retention_days": 7},
    )
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    sync_calls = []

    def fake_sync(self, *, root_name=None, path=None, dry_run=False, reason="manual_sync", progress_callback=None):
        sync_calls.append(
            {
                "root_name": root_name,
                "path": path,
                "dry_run": dry_run,
                "reason": reason,
                "progress_callback": callable(progress_callback),
            }
        )
        progress_callback({"stage": "enumerated", "files_total": 35655, "files_seen": 0, "jobs_queued": 0})
        return {
            "root_name": root_name,
            "dry_run": dry_run,
            "reason": reason,
            "files_seen": 35655,
            "files_changed": 35371,
            "files_deleted": 0,
            "jobs_queued": 120,
            "chunks_indexed": 6500,
        }

    monkeypatch.setattr(KnowledgeService, "sync_corpus", fake_sync)

    result = KnowledgeService().run_corpus_backfill(kind="general", limit=1, workers=1)

    assert result["completed"] == 1
    assert calls["recovered"] == [{"root_name": None}]
    assert calls["blocked"] == []
    assert calls["retried"] == []
    assert sync_calls == [
        {
            "root_name": "mail-outlook-mohesr",
            "path": None,
            "dry_run": False,
            "reason": "outlook_spool_sync",
            "progress_callback": True,
        }
    ]
    assert calls["progress"][0]["job_id"] == "job-sync"
    assert calls["progress"][0]["telemetry"]["stage"] == "starting"
    assert any(call["telemetry"]["stage"] == "enumerated" for call in calls["progress"])
    assert calls["completed"][0]["job_id"] == "job-sync"
    assert calls["completed"][0]["telemetry"]["files_seen"] == 35655
    assert calls["completed"][0]["telemetry"]["jobs_queued"] == 120
    assert calls["purged"] == [{"retention_days": 7}]
    assert result["purged_capture_jobs"] == 3
    assert result["capture_job_retention_days"] == 7


def test_enqueue_corpus_backfill_returns_operation_metadata(monkeypatch):
    events = []
    monkeypatch.setattr(
        database,
        "enqueue_pending_corpus_job_commands",
        lambda **kwargs: events.append(("enqueue", kwargs))
        or {"queued": 1, "jobs": [{"job_id": "job-1", "message_id": "msg-1"}]},
    )
    monkeypatch.setattr(database, "record_audit_event", lambda **kwargs: events.append(("audit", kwargs)) or {"id": "audit-1"})

    result = KnowledgeService().enqueue_corpus_backfill(kind="text", limit=3, workers=2, root_name="docs")

    assert result["accepted"] is True
    assert result["operation_type"] == "corpus_backfill"
    assert result["queued"] == 1
    assert result["job_ids"] == ["job-1"]
    assert result["status_url"] == "/api/crawl/jobs"
    assert "corpus.job.completed" in result["event_topics"]
    assert events[0] == (
        "enqueue",
        {
            "limit": 3,
            "root_name": "docs",
            "job_families": ["text", "office"],
            "host_agent_roots": None,
        },
    )


def test_enqueue_corpus_backfill_attaches_valid_callback(monkeypatch):
    events = []
    monkeypatch.setattr(
        database,
        "enqueue_pending_corpus_job_commands",
        lambda **_kwargs: {"queued": 1, "jobs": [{"job_id": "11111111-1111-1111-1111-111111111111", "message_id": "msg-1"}]},
    )
    monkeypatch.setattr(
        database,
        "attach_callback_to_capture_jobs",
        lambda **kwargs: events.append(("attach", kwargs)) or {"attached": 1, "job_ids": kwargs["job_ids"]},
    )
    monkeypatch.setattr(database, "record_audit_event", lambda **kwargs: events.append(("audit", kwargs)) or {"id": "audit-1"})

    result = KnowledgeService().enqueue_corpus_backfill(
        kind="text",
        limit=1,
        callback_url="http://127.0.0.1:8765/callback",
    )

    assert result["callback"]["attached_jobs"] == 1
    assert events[0][0] == "attach"
    assert events[0][1]["job_ids"] == ["11111111-1111-1111-1111-111111111111"]
    assert events[0][1]["callback_url"] == "http://127.0.0.1:8765/callback"


def test_backfill_recovers_stale_jobs_globally(monkeypatch):
    calls = {"recovered": [], "cancelled": [], "repaired": [], "cleared_errors": []}

    monkeypatch.setattr(database, "recover_stale_running_corpus_jobs", lambda **kwargs: calls["recovered"].append(kwargs) or {"recovered": 2})
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **kwargs: calls["cancelled"].append(kwargs) or {"cancelled": 0})
    monkeypatch.setattr(database, "claim_corpus_jobs", lambda **_kwargs: [])
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    result = KnowledgeService().run_corpus_backfill(kind="general", limit=1, workers=1)

    assert calls["recovered"] == [{"root_name": None}]
    assert result["recovered_stale_running"] == 2
    assert calls["cancelled"] == [{"root_name": None}]


def test_backfill_purges_unseen_assets_before_claiming_jobs(monkeypatch):
    order = []
    calls = {"recovered": [], "purged": [], "cancelled": [], "repaired": [], "cleared_errors": []}

    monkeypatch.setattr(database, "recover_stale_running_corpus_jobs", lambda **kwargs: calls["recovered"].append(kwargs) or {"recovered": 0})
    monkeypatch.setattr(
        database,
        "purge_unseen_corpus_assets",
        lambda **kwargs: order.append("purge") or calls["purged"].append(kwargs) or {"assets_purged": 4},
        raising=False,
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **kwargs: calls["cancelled"].append(kwargs) or {"cancelled": 0})
    monkeypatch.setattr(database, "claim_corpus_jobs", lambda **_kwargs: order.append("claim") or [])
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)
    monkeypatch.setattr(service_module, "_configured_unseen_asset_purge_grace_seconds", lambda: 86400, raising=False)
    monkeypatch.setattr(service_module, "_configured_unseen_asset_purge_batch_size", lambda: 500, raising=False)

    result = KnowledgeService().run_corpus_backfill(kind="general", limit=1, workers=1)

    assert order == ["purge", "claim"]
    assert calls["purged"] == [{"root_name": None, "grace_seconds": 86400, "batch_size": 500}]
    assert result["purged_unseen_assets"] == 4


def test_backfill_records_unseen_asset_cancellation_without_retry(monkeypatch):
    calls = {"cancelled": [], "completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(database, "recover_stale_running_corpus_jobs", lambda **_kwargs: {"recovered": 0})
    monkeypatch.setattr(database, "purge_unseen_corpus_assets", lambda **_kwargs: {"assets_purged": 0}, raising=False)
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda **_kwargs: [
            {
                "id": "job-unseen",
                "job_type": "corpus_extract_document",
                "job_family": "office",
                "payload": {"path": "tmp/secret.docx", "root_name": "docs"},
                "attempts": 1,
            }
        ],
    )
    monkeypatch.setattr(database, "cancel_unseen_corpus_job", lambda **kwargs: calls["cancelled"].append(kwargs), raising=False)
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    monkeypatch.setattr(
        worker,
        "process_corpus_job",
        lambda _job: worker.JobProcessResult(
            status="cancelled_unseen_asset",
            message="source asset is no longer included by root policy: tmp/secret.docx",
            telemetry={"unseen_reason": "excluded_by_policy"},
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="office", limit=1, workers=1)

    assert result["cancelled_unseen_asset"] == 1
    assert calls["completed"] == []
    assert calls["blocked"] == []
    assert calls["retried"] == []
    assert calls["cancelled"][0]["job_id"] == "job-unseen"
    assert calls["cancelled"][0]["error"] == "source asset is no longer included by root policy: tmp/secret.docx"
    assert calls["cancelled"][0]["telemetry"]["result_status"] == "cancelled_unseen_asset"


def test_backfill_marks_failed_job_terminal_after_configured_attempts(monkeypatch):
    calls = {"blocked": [], "retried": [], "recovered": [], "repaired": [], "cleared_errors": []}

    monkeypatch.setattr(database, "recover_stale_running_corpus_jobs", lambda **kwargs: calls["recovered"].append(kwargs) or {"recovered": 0})
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda **_kwargs: [
            {
                "id": "job-image",
                "job_type": "corpus_extract_image",
                "job_family": "image",
                "resource_class": "gpu",
                "payload": {"root_name": "docs", "path": "bad.png"},
                "attempts": 3,
            }
        ],
    )
    monkeypatch.setattr(worker, "process_corpus_job", lambda _job: worker.JobProcessResult(status="failed", message="extract failed"))
    monkeypatch.setattr(service_module, "_configured_failure_max_attempts", lambda: 3, raising=False)
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    result = KnowledgeService().run_corpus_backfill(kind="image", limit=1, workers=1)

    assert result["failed"] == 1
    assert calls["retried"] == []
    assert calls["blocked"][0]["job_id"] == "job-image"
    assert calls["blocked"][0]["status"] == "failed"
    assert calls["blocked"][0]["error"] == "extract failed"


def test_backfill_retries_failed_job_below_configured_attempt_limit(monkeypatch):
    calls = {"blocked": [], "retried": [], "recovered": [], "repaired": [], "cleared_errors": []}

    monkeypatch.setattr(database, "recover_stale_running_corpus_jobs", lambda **kwargs: calls["recovered"].append(kwargs) or {"recovered": 0})
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda **_kwargs: [
            {
                "id": "job-image",
                "job_type": "corpus_extract_image",
                "job_family": "image",
                "resource_class": "gpu",
                "payload": {"root_name": "docs", "path": "bad.png"},
                "attempts": 2,
            }
        ],
    )
    monkeypatch.setattr(worker, "process_corpus_job", lambda _job: worker.JobProcessResult(status="failed", message="extract failed"))
    monkeypatch.setattr(service_module, "_configured_failure_max_attempts", lambda: 3, raising=False)
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    result = KnowledgeService().run_corpus_backfill(kind="image", limit=1, workers=1)

    assert result["failed"] == 0
    assert calls["blocked"] == []
    assert calls["retried"][0]["job_id"] == "job-image"
    assert calls["retried"][0]["error"] == "extract failed"


def test_process_corpus_sync_job_uses_worker_heartbeat_without_progress_renewal(monkeypatch):
    calls = {"progress": [], "heartbeats": []}

    monkeypatch.setattr(database, "update_corpus_job_progress", lambda **kwargs: calls["progress"].append(kwargs), raising=False)
    monkeypatch.setattr(database, "heartbeat_corpus_job", lambda **kwargs: calls["heartbeats"].append(kwargs), raising=False)

    class OneShotEvent:
        def __init__(self):
            self.calls = 0
            self.is_set = False

        def wait(self, _timeout):
            if self.calls == 0:
                self.calls += 1
                return False
            return True

        def set(self):
            self.is_set = True

    class ImmediateThread:
        def __init__(self, *, target, **_kwargs):
            self.target = target

        def start(self):
            self.target()

        def join(self, timeout=None):
            return None

    monkeypatch.setattr(service_module.threading, "Event", OneShotEvent)
    monkeypatch.setattr(service_module.threading, "Thread", ImmediateThread)

    def fake_sync(self, *, root_name=None, path=None, dry_run=False, reason="manual_sync", progress_callback=None):
        if progress_callback:
            progress_callback({"stage": "enumerated", "files_total": 1, "files_done": 1})
        return {
            "root_name": root_name,
            "files_seen": 1,
            "files_changed": 1,
            "files_deleted": 0,
            "jobs_queued": 0,
            "chunks_indexed": 0,
            "manifest_skipped_unchanged": 0,
        }

    monkeypatch.setattr(KnowledgeService, "sync_corpus", fake_sync)

    result = KnowledgeService()._process_corpus_sync_job(
        {
            "id": "job-sync",
            "job_type": "corpus_sync_root",
            "payload": {"root_name": "docs", "reason": "manual_sync"},
        }
    )

    assert result.status == "indexed"
    assert calls["progress"][0]["telemetry"]["stage"] == "starting"
    assert any(call["telemetry"]["stage"] == "enumerated" for call in calls["progress"])
    assert calls["heartbeats"]
    assert calls["heartbeats"][0]["job_id"] == "job-sync"
    assert calls["heartbeats"][0]["telemetry"]["stage"] == "running"


def test_backfill_processes_batched_corpus_sync_root_paths(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "progress": [], "recovered": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-sync",
                "job_type": "corpus_sync_root",
                "job_family": "general",
                "resource_class": "cpu",
                "payload": {"root_name": "docs", "reason": "watch_event", "paths": ["a.md", "b.md"]},
                "attempts": 1,
            }
        ],
    )
    monkeypatch.setattr(database, "recover_stale_running_corpus_jobs", lambda **kwargs: calls["recovered"].append(kwargs) or {"recovered": 0})
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "update_corpus_job_progress", lambda **kwargs: calls["progress"].append(kwargs), raising=False)
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "load_scan_manifest", lambda **_kwargs: {}, raising=False)
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [
            {
                "name": "docs",
                "root_path": "E:/docs",
                "recursive": True,
                "include_globs": [],
                "exclude_globs": [],
                "glob_mode": "extend",
                "max_inline_bytes": 1024,
                "heavy_threshold_bytes": 2048,
                "metadata": {},
            }
        ],
    )
    sync_calls = []

    def fake_sync(self, *, root, path=None, dry_run=False, reason="manual_sync", progress_callback=None, **_kwargs):
        sync_calls.append({"root_name": root["name"], "path": path, "reason": reason})
        progress_callback({"stage": "discovered", "files_total": 1, "files_seen": 1, "jobs_queued": 0})
        return {
            "root_name": root["name"],
            "files_seen": 1,
            "files_changed": 1 if path == "a.md" else 0,
            "files_deleted": 0,
            "jobs_queued": 0,
            "chunks_indexed": 2 if path == "a.md" else 0,
            "manifest_skipped_unchanged": 0 if path == "a.md" else 1,
        }

    monkeypatch.setattr(KnowledgeService, "_sync_corpus_selected_root", fake_sync)

    result = KnowledgeService().run_corpus_backfill(kind="general", limit=1, workers=1)

    assert result["completed"] == 1
    assert sync_calls == [
        {"root_name": "docs", "path": "a.md", "reason": "watch_event"},
        {"root_name": "docs", "path": "b.md", "reason": "watch_event"},
    ]
    assert calls["completed"][0]["telemetry"]["paths_total"] == 2
    assert calls["completed"][0]["telemetry"]["paths_done"] == 2
    assert calls["completed"][0]["telemetry"]["progress_percent"] == 100
    assert calls["completed"][0]["telemetry"]["progress_label"] == "Paths 2/2, stage 6/6 completed"
    assert calls["completed"][0]["telemetry"]["files_seen"] == 2
    assert calls["completed"][0]["telemetry"]["files_changed"] == 1
    assert calls["completed"][0]["telemetry"]["chunks_indexed"] == 2
    assert calls["completed"][0]["telemetry"]["manifest_skipped_unchanged"] == 1


def test_process_batched_corpus_sync_root_paths_reuses_manifest_and_prefers_batch_progress(monkeypatch, tmp_path):
    root = tmp_path / "docs"
    root.mkdir()
    progress_calls = []
    manifest_loads = []
    scan_calls = []
    persist_calls = []
    monkeypatch.setattr(database, "update_corpus_job_progress", lambda **kwargs: progress_calls.append(kwargs), raising=False)
    monkeypatch.setattr(database, "heartbeat_corpus_job", lambda **_kwargs: None, raising=False)
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [
            {
                "name": "docs",
                "root_path": str(root),
                "recursive": True,
                "include_globs": [],
                "exclude_globs": [],
                "glob_mode": "extend",
                "max_inline_bytes": 1024,
                "heavy_threshold_bytes": 2048,
                "metadata": {},
            }
        ],
    )

    def fake_load_scan_manifest(**kwargs):
        manifest_loads.append(kwargs)
        return {
            "a.md": {
                "content_hash": "cached",
                "source_asset_status": "indexed",
                "chunk_count": 1,
            }
        }

    def fake_scan(_root_path, policy, target_path=None, progress_callback=None):
        target = str(target_path)
        scan_calls.append(
            {
                "target_path": target,
                "manifest": policy.manifest_lookup("a.md"),
                "container_max_depth": policy.container_max_depth,
                "hash_parallelism": policy.hash_parallelism,
            }
        )
        if progress_callback:
            progress_callback(
                {
                    "stage": "discovered",
                    "stage_index": 5,
                    "stage_total": 6,
                    "files_total": 1,
                    "files_seen": 1,
                    "files_done": 1,
                    "progress_percent": 83,
                    "progress_label": "Discovered 1/1 files",
                }
            )
        return SimpleNamespace(
            root_path=root,
            scope_relative_path=target,
            scope_is_file=target.endswith(".md"),
            assets=[],
            deferred_jobs=[],
            errors=[],
        )

    def fake_persist(**kwargs):
        plan = kwargs["plan"]
        persist_calls.append(
            {
                "root_name": kwargs["root_name"],
                "scope_relative_path": plan.scope_relative_path,
                "scope_is_file": plan.scope_is_file,
            }
        )
        return {
            "root_name": kwargs["root_name"],
            "files_seen": 1,
            "files_changed": 1 if plan.scope_relative_path == "a.md" else 0,
            "files_deleted": 0,
            "jobs_queued": 0,
            "chunks_indexed": 1 if plan.scope_relative_path == "a.md" else 0,
            "manifest_skipped_unchanged": 0 if plan.scope_relative_path == "a.md" else 1,
        }

    monkeypatch.setattr(database, "load_scan_manifest", fake_load_scan_manifest, raising=False)
    monkeypatch.setattr(service_module, "scan_path", fake_scan)
    monkeypatch.setattr(database, "persist_crawl_plan", fake_persist)

    result = KnowledgeService()._process_corpus_sync_job(
        {
            "id": "job-sync",
            "job_type": "corpus_sync_root",
            "payload": {"root_name": "docs", "reason": "watch_event", "paths": ["a.md", "folder"]},
        }
    )

    assert result.status == "indexed"
    assert len(manifest_loads) == 1
    assert [call["target_path"] for call in scan_calls] == ["a.md", "folder"]
    assert all(call["manifest"]["content_hash"] == "cached" for call in scan_calls)
    assert persist_calls == [
        {"root_name": "docs", "scope_relative_path": "a.md", "scope_is_file": True},
        {"root_name": "docs", "scope_relative_path": "folder", "scope_is_file": False},
    ]
    discovered_progress = [call["telemetry"] for call in progress_calls if call["telemetry"].get("stage") == "discovered"]
    assert discovered_progress[0]["progress_label"] == "Paths 1/2, stage 5/6 discovered, files 1/1"
    assert result.telemetry["batch_paths_total"] == 2
    assert result.telemetry["batch_paths_done"] == 2
    assert result.telemetry["batch_manifest_loaded_once"] is True
    assert result.telemetry["manifest_skipped_unchanged"] == 1


@pytest.mark.parametrize(
    ("blocked_status", "message"),
    [
        ("blocked_missing_dependency", "ffprobe command not found"),
        ("blocked_by_policy", "text file exceeds inline extraction limit"),
        ("blocked_invalid_source", "Package not found"),
    ],
)
def test_backfill_blocks_terminal_blocked_jobs_without_completing(monkeypatch, blocked_status, message):
    calls = {"completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-1",
                "job_type": "corpus_extract_video",
                "job_family": "media",
                "payload": {"path": "clip.mp4", "root_name": "media"},
                "attempts": 1,
            }
        ],
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(
        database,
        "repair_extracted_corpus_asset_statuses",
        lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0},
    )
    monkeypatch.setattr(
        database,
        "clear_completed_corpus_job_errors",
        lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0},
    )
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    monkeypatch.setattr(
        worker,
        "process_corpus_job",
        lambda job: worker.JobProcessResult(
            status=blocked_status,
            message=message,
            telemetry={"ocr_cache_hits": 0, "ocr_cache_misses": 0},
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="media", limit=1, workers=1)

    assert result["blocked"] == 1
    assert calls["completed"] == []
    assert calls["retried"] == []
    assert calls["blocked"][0]["job_id"] == "job-1"
    assert calls["blocked"][0]["error"] == message
    if blocked_status == "blocked_missing_dependency":
        assert "status" not in calls["blocked"][0]
    else:
        assert calls["blocked"][0]["status"] == blocked_status
    assert calls["blocked"][0]["telemetry"]["ocr_cache_hits"] == 0
    assert calls["blocked"][0]["telemetry"]["ocr_cache_misses"] == 0
    assert calls["repaired"] == [{"root_name": None}]
    assert calls["cleared_errors"] == [{"root_name": None}]


def test_backfill_blocks_paddle_dependency_result_without_retry(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda **_kwargs: [
            {
                "id": "job-ocr",
                "job_type": "corpus_extract_pdf_ocr_pages",
                "job_family": "documents",
                "resource_class": "gpu",
                "payload": {"path": "scan.pdf", "root_name": "docs"},
                "attempts": 1,
            }
        ],
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    monkeypatch.setattr(
        worker,
        "process_corpus_job",
        lambda _job: worker.JobProcessResult(
            status="blocked_missing_dependency",
            message="PaddleOCR-VL requires additional dependencies",
            telemetry={"error_type": "DependencyError", "ocr_cache_hits": 0, "ocr_cache_misses": 1},
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="documents", limit=1, workers=1)

    assert result["blocked"] == 1
    assert result["retried"] == 0
    assert result["failed"] == 0
    assert calls["completed"] == []
    assert calls["retried"] == []
    assert calls["blocked"][0]["job_id"] == "job-ocr"
    assert calls["blocked"][0]["error"] == "PaddleOCR-VL requires additional dependencies"
    assert "status" not in calls["blocked"][0]
    assert calls["blocked"][0]["telemetry"]["error_type"] == "DependencyError"


def test_backfill_processes_claimed_jobs_in_parallel_when_workers_gt_one(monkeypatch):
    active = 0
    max_active = 0
    lock = threading.Lock()
    saw_parallel = threading.Event()
    completed: list[dict] = []

    jobs = [
        {
            "id": f"job-{index}",
            "job_type": "corpus_extract_text",
            "job_family": "text",
            "resource_class": "cpu",
            "payload": {"root_name": "docs", "path": f"file-{index}.md"},
            "attempts": 1,
        }
        for index in range(4)
    ]

    monkeypatch.setattr(database, "claim_corpus_jobs", lambda **_kwargs: list(jobs))
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: completed.append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **_kwargs: None)
    monkeypatch.setattr(database, "retry_corpus_job", lambda **_kwargs: None)
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **_kwargs: {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **_kwargs: {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    def slow_process(job):
        nonlocal active, max_active
        with lock:
            active += 1
            max_active = max(max_active, active)
            if active >= 2:
                saw_parallel.set()
        time.sleep(0.05)
        with lock:
            active -= 1
        return worker.JobProcessResult(status="indexed", telemetry={"path": job["payload"]["path"]})

    monkeypatch.setattr(worker, "process_corpus_job", slow_process)

    result = KnowledgeService().run_corpus_backfill(kind="text", limit=4, workers=4)

    assert result["claimed"] == 4
    assert result["completed"] == 4
    assert len(completed) == 4
    assert saw_parallel.is_set()
    assert max_active >= 2


def test_backfill_handles_parallel_job_exceptions_independently(monkeypatch):
    completed: list[dict] = []
    retried: list[dict] = []
    jobs = [
        {
            "id": "job-fail",
            "job_type": "corpus_extract_text",
            "job_family": "text",
            "resource_class": "cpu",
            "payload": {"root_name": "docs", "path": "fail.md"},
            "attempts": 1,
        },
        {
            "id": "job-ok",
            "job_type": "corpus_extract_text",
            "job_family": "text",
            "resource_class": "cpu",
            "payload": {"root_name": "docs", "path": "ok.md"},
            "attempts": 1,
        },
    ]

    monkeypatch.setattr(database, "claim_corpus_jobs", lambda **_kwargs: list(jobs))
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: completed.append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **_kwargs: None)
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: retried.append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **_kwargs: {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **_kwargs: {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    def process(job):
        if job["id"] == "job-fail":
            raise RuntimeError("boom")
        return worker.JobProcessResult(status="indexed", telemetry={"path": job["payload"]["path"]})

    monkeypatch.setattr(worker, "process_corpus_job", process)

    result = KnowledgeService().run_corpus_backfill(kind="text", limit=2, workers=2)

    assert result["claimed"] == 2
    assert result["completed"] == 1
    assert result["retried"] == 1
    assert completed[0]["job_id"] == "job-ok"
    assert retried[0]["job_id"] == "job-fail"
    assert retried[0]["error"] == "boom"


def test_search_index_sync_gpu_lease_timeout_is_retryable(monkeypatch):
    from flux_llm_kb.gpu_scheduler import GpuLeaseTimeout

    monkeypatch.setattr(
        database,
        "sync_search_index",
        lambda **_kwargs: (_ for _ in ()).throw(GpuLeaseTimeout("GPU busy", retry_after_seconds=5.0)),
    )

    result = worker.process_search_index_sync_job({"payload": {"owner_class": "all", "limit": 10}})

    assert result.status == "retrying_gpu_busy"
    assert result.message == "GPU busy"
    assert result.telemetry == {
        "error_type": "GpuLeaseTimeout",
        "retry_after_seconds": 5.0,
        "gpu_scheduler_status": "busy",
    }


def test_search_index_sync_gpu_lease_rejection_is_retryable(monkeypatch):
    from flux_llm_kb.gpu_scheduler import GpuLeaseRejected

    monkeypatch.setattr(
        database,
        "sync_search_index",
        lambda **_kwargs: (_ for _ in ()).throw(GpuLeaseRejected("vram_budget_exceeded")),
    )

    result = worker.process_search_index_sync_job({"payload": {"owner_class": "all", "limit": 10}})

    assert result.status == "retrying_gpu_busy"
    assert result.message == "vram_budget_exceeded"
    assert result.telemetry == {
        "error_type": "GpuLeaseRejected",
        "retry_after_seconds": 1.0,
        "gpu_scheduler_status": "busy",
    }


def test_search_index_sync_worker_enqueues_continuation_after_success(monkeypatch):
    continuation_calls: list[dict] = []

    monkeypatch.setattr(
        database,
        "sync_search_index",
        lambda **kwargs: {
            "search_engine": "vespa",
            "requested": 100,
            "indexed": 100,
            "deleted": 0,
            "skipped_unchanged": 0,
            "failed": 0,
            "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
            "embedding_dimensions": 1024,
            "embedding_batch_size": 16,
            "embedding_batches": 7,
            "model_generation": "snowflake-qwen-paddleocr-v1",
            "more_pending": True,
            "continuation_remaining": 150,
            "page_size": 100,
            "page_sequence": 0,
            "rows_loaded": 100,
            "hydrated_body_chars": 2048,
            "truncated_body_chars": 128,
            **kwargs,
        },
    )
    monkeypatch.setattr(
        database,
        "enqueue_search_index_sync_continuation",
        lambda **kwargs: continuation_calls.append(kwargs)
        or {"queued": 1, "job_id": "job-search-next", "deduped": False, "reused": False, **kwargs},
        raising=False,
    )

    result = worker.process_search_index_sync_job(
        {
            "id": "job-search-current",
            "payload": {
                "owner_class": "corpus",
                "root_name": "docs",
                "limit": 250,
                "page_size": 100,
                "page_sequence": 0,
            },
        }
    )

    assert result.status == "indexed"
    assert continuation_calls == [
        {
            "owner_class": "corpus",
            "root_name": "docs",
            "limit": 150,
            "page_size": 100,
            "continuation_of": "job-search-current",
            "page_sequence": 1,
        }
    ]
    assert result.telemetry["search_index_continuation_queued"] == 1
    assert result.telemetry["search_index_continuation_remaining"] == 150
    assert result.telemetry["search_index_rows_loaded"] == 100
    assert result.telemetry["search_index_truncated_body_chars"] == 128


def test_backfill_retries_gpu_busy_result_without_terminal_failure(monkeypatch):
    calls = {"retried": [], "blocked": [], "completed": [], "repaired": [], "cleared_errors": []}

    monkeypatch.setattr(database, "recover_stale_running_corpus_jobs", lambda **_kwargs: {"recovered": 0})
    monkeypatch.setattr(database, "purge_unseen_corpus_assets", lambda **_kwargs: {"assets_purged": 0}, raising=False)
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(service_module, "_configured_failure_max_attempts", lambda: 1, raising=False)
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda **_kwargs: [
            {
                "id": "job-search",
                "job_type": "search_index_sync",
                "job_family": "embedding",
                "resource_class": "gpu",
                "payload": {"owner_class": "all", "limit": 10},
                "attempts": 99,
            }
        ],
    )
    monkeypatch.setattr(worker, "process_search_index_sync_job", lambda _job: worker.JobProcessResult(status="retrying_gpu_busy", message="GPU busy", telemetry={"retry_after_seconds": 5.0}))
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    result = KnowledgeService().run_corpus_backfill(kind="embedding", limit=1, workers=1)

    assert result["retried"] == 1
    assert result["failed"] == 0
    assert calls["completed"] == []
    assert calls["blocked"] == []
    assert calls["retried"][0]["job_id"] == "job-search"
    assert calls["retried"][0]["status"] == "retrying_gpu_busy"
    assert calls["retried"][0]["cooldown_seconds"] == 60
    assert calls["retried"][0]["telemetry"]["gpu_busy_retry_count"] == 1


def test_finalize_gpu_busy_result_uses_bounded_cooldown(monkeypatch):
    calls = {"retried": [], "blocked": []}
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(service_module, "_configured_failure_max_attempts", lambda: 1)
    monkeypatch.setattr(service_module, "_configured_gpu_busy_retry_base_cooldown_seconds", lambda: 60, raising=False)
    monkeypatch.setattr(service_module, "_configured_gpu_busy_retry_max_cooldown_seconds", lambda: 120, raising=False)
    monkeypatch.setattr(service_module, "_configured_gpu_busy_retry_block_after_seconds", lambda: 86400, raising=False)

    outcome = KnowledgeService()._finalize_corpus_job_process_result(
        {
            "id": "job-gpu",
            "job_type": "corpus_extract_image",
            "job_family": "image",
            "resource_class": "gpu",
            "attempts": 99,
            "telemetry": {},
        },
        duration_ms=25,
        process_result=worker.JobProcessResult(
            status="retrying_gpu_busy",
            message="vram_budget_exceeded",
            telemetry={"retry_after_seconds": 9999.0},
        ),
    )

    assert outcome["status"] == "retrying_gpu_busy"
    assert outcome["retryable"] is True
    assert calls["blocked"] == []
    assert calls["retried"][0]["status"] == "retrying_gpu_busy"
    assert calls["retried"][0]["cooldown_seconds"] == 120
    telemetry = calls["retried"][0]["telemetry"]
    assert telemetry["gpu_busy_retry_count"] == 1
    assert telemetry["gpu_busy_next_cooldown_seconds"] == 120
    assert telemetry["gpu_busy_block_after_seconds"] == 86400
    assert isinstance(telemetry["gpu_busy_first_seen_at"], str)


def test_finalize_gpu_busy_result_blocks_after_retry_age(monkeypatch):
    calls = {"retried": [], "blocked": []}
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(service_module, "_configured_gpu_busy_retry_base_cooldown_seconds", lambda: 60, raising=False)
    monkeypatch.setattr(service_module, "_configured_gpu_busy_retry_max_cooldown_seconds", lambda: 900, raising=False)
    monkeypatch.setattr(service_module, "_configured_gpu_busy_retry_block_after_seconds", lambda: 60, raising=False)

    outcome = KnowledgeService()._finalize_corpus_job_process_result(
        {
            "id": "job-gpu-old",
            "job_type": "corpus_extract_image",
            "job_family": "image",
            "resource_class": "gpu",
            "attempts": 99,
            "telemetry": {
                "gpu_busy_first_seen_at": "2000-01-01T00:00:00+00:00",
                "gpu_busy_retry_count": 12,
            },
        },
        duration_ms=25,
        process_result=worker.JobProcessResult(
            status="retrying_gpu_busy",
            message="vram_budget_exceeded",
            telemetry={"retry_after_seconds": 1.0},
        ),
    )

    assert outcome["status"] == "blocked_gpu_busy"
    assert outcome["category"] == "blocked"
    assert outcome["retryable"] is False
    assert calls["retried"] == []
    assert calls["blocked"][0]["status"] == "blocked_gpu_busy"
    assert calls["blocked"][0]["telemetry"]["gpu_busy_retry_count"] == 13
    assert isinstance(calls["blocked"][0]["telemetry"]["gpu_busy_blocked_at"], str)


def test_backfill_cancels_orphaned_root_jobs_without_retrying(monkeypatch):
    calls = {"cancelled": [], "completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-orphan",
                "job_type": "corpus_extract_video",
                "job_family": "media",
                "payload": {"path": "clip.mp4", "root_name": "smoke-deleted"},
                "attempts": 1,
            }
        ],
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "cancel_orphaned_corpus_job", lambda **kwargs: calls["cancelled"].append(kwargs))
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    monkeypatch.setattr(
        worker,
        "process_corpus_job",
        lambda job: worker.JobProcessResult(
            status="cancelled_orphaned_root",
            message="monitored root not found: smoke-deleted",
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="media", limit=1, workers=1)

    assert result["cancelled_orphaned"] == 1
    assert calls["completed"] == []
    assert calls["blocked"] == []
    assert calls["retried"] == []
    assert calls["cancelled"][0]["job_id"] == "job-orphan"
    assert calls["cancelled"][0]["error"] == "monitored root not found: smoke-deleted"
    assert calls["cancelled"][0]["telemetry"]["result_status"] == "cancelled_orphaned_root"


def test_backfill_cancels_missing_source_jobs_without_retrying(monkeypatch):
    calls = {"cancelled": [], "completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-missing",
                "job_type": "corpus_extract_document",
                "job_family": "office",
                "payload": {"path": "missing/attachment.docx", "root_name": "mail-outlook-mohesr"},
                "attempts": 1,
            }
        ],
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "cancel_missing_source_corpus_job", lambda **kwargs: calls["cancelled"].append(kwargs))
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    monkeypatch.setattr(
        worker,
        "process_corpus_job",
        lambda job: worker.JobProcessResult(
            status="cancelled_missing_source",
            message="source file not found: missing/attachment.docx",
            telemetry={"missing_source": True, "missing_source_deleted": True},
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="office", limit=1, workers=1)

    assert result["cancelled_missing_source"] == 1
    assert calls["completed"] == []
    assert calls["blocked"] == []
    assert calls["retried"] == []
    assert calls["cancelled"][0]["job_id"] == "job-missing"
    assert calls["cancelled"][0]["root_name"] == "mail-outlook-mohesr"
    assert calls["cancelled"][0]["relative_path"] == "missing/attachment.docx"
    assert calls["cancelled"][0]["telemetry"]["result_status"] == "cancelled_missing_source"


def test_corpus_job_cancels_missing_source_file_instead_of_blocking(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda root_name: {
            "name": root_name,
            "root_path": str(tmp_path),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 262144,
            "heavy_threshold_bytes": 10485760,
            "metadata": {"strict_indexing": True},
        },
    )

    result = worker.process_corpus_job(
        {
            "id": "job-missing",
            "job_type": "corpus_extract_document",
            "payload": {"root_name": "mail-outlook-mohesr", "path": "missing/attachment.docx"},
        }
    )

    assert result.status == "cancelled_missing_source"
    assert result.message == "source file not found: missing/attachment.docx"
    assert result.telemetry == {"missing_source": True, "missing_source_deleted": True}


def test_corpus_job_cancels_excluded_path_without_extraction(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    excluded_dir = root / "tmp"
    excluded_dir.mkdir(parents=True)
    (excluded_dir / "secret.pdf").write_bytes(b"private")
    extracted = []
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda root_name: {
            "name": root_name,
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": ["tmp/**"],
            "max_inline_bytes": 262144,
            "heavy_threshold_bytes": 10485760,
            "metadata": {},
        },
    )
    monkeypatch.setattr(worker, "extract_file", lambda *_args: extracted.append(True) or SimpleNamespace(status="indexed"))
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))

    result = worker.process_corpus_job(
        {
            "id": "job-excluded",
            "job_type": "corpus_extract_document",
            "payload": {"root_name": "docs", "path": "tmp/secret.pdf"},
        }
    )

    assert result.status == "cancelled_unseen_asset"
    assert result.message == "source asset is no longer included by root policy: tmp/secret.pdf"
    assert result.telemetry == {"unseen_reason": "excluded_by_policy", "cancelled_unseen_asset": True}
    assert extracted == []
    assert applied == []


def test_corpus_job_discards_extraction_result_when_running_job_was_cancelled(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "readme.md").write_text("body", encoding="utf-8")
    legacy_applied = []
    guarded_applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda root_name: {
            "name": root_name,
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 262144,
            "heavy_threshold_bytes": 10485760,
            "metadata": {},
        },
    )
    monkeypatch.setattr(database, "corpus_job_is_running", lambda _job_id: True, raising=False)
    monkeypatch.setattr(
        database,
        "apply_extraction_result_for_job",
        lambda **kwargs: guarded_applied.append(kwargs) or False,
        raising=False,
    )
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: legacy_applied.append(kwargs))
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda *_args: SimpleNamespace(status="indexed", message=None, metadata={"extractor": "text"}, chunks=(), child_assets=()),
    )

    result = worker.process_corpus_job(
        {
            "id": "job-running",
            "job_type": "corpus_extract_text",
            "payload": {"root_name": "docs", "path": "readme.md"},
        }
    )

    assert result.status == "cancelled_unseen_asset"
    assert result.message == "corpus job was cancelled before extraction results were applied"
    assert result.telemetry == {"unseen_reason": "cancelled_during_extraction", "cancelled_unseen_asset": True}
    assert guarded_applied[0]["job_id"] == "job-running"
    assert legacy_applied == []


def test_backfill_merges_ocr_telemetry_into_completed_jobs(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-ocr",
                "job_type": "corpus_extract_image",
                "job_family": "image",
                "resource_class": "gpu",
                "payload": {"path": "scan.png", "root_name": "images"},
                "attempts": 1,
            }
        ],
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    monkeypatch.setattr(
        worker,
        "process_corpus_job",
        lambda job: worker.JobProcessResult(
            status="indexed",
            telemetry={"ocr_cache_hits": 1, "ocr_cache_misses": 0},
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="images", limit=1, workers=1)

    assert result["completed"] == 1
    telemetry = calls["completed"][0]["telemetry"]
    assert telemetry["job_family"] == "image"
    assert telemetry["resource_class"] == "gpu"
    assert telemetry["ocr_cache_hits"] == 1
    assert telemetry["ocr_cache_misses"] == 0


def test_process_corpus_job_marks_missing_root_as_orphaned(monkeypatch):
    from flux_llm_kb import worker

    monkeypatch.setattr(database, "get_monitored_root", lambda _root_name: None)

    result = worker.process_corpus_job(
        {
            "id": "job-orphan",
            "job_type": "corpus_extract_video",
            "payload": {"path": "clip.mp4", "root_name": "smoke-deleted"},
        }
    )

    assert result.status == "cancelled_orphaned_root"
    assert result.message == "monitored root not found: smoke-deleted"


def test_process_corpus_job_merges_asr_telemetry(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "media"
    root.mkdir()
    (root / "clip.mp4").write_bytes(b"fake media")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "media",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
        },
    )
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda *_args: type(
            "Extraction",
            (),
            {
                "status": "indexed",
                "message": None,
                "metadata": {"asr": {"cache_hits": 2, "cache_misses": 1, "segments": 4}},
            },
        )(),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "media", "path": "clip.mp4"}})

    assert result.status == "indexed"
    assert result.telemetry == {"asr_cache_hits": 2, "asr_cache_misses": 1, "asr_segments": 4}
    assert applied[0]["root_name"] == "media"


def test_process_corpus_job_plans_staged_media_extraction(monkeypatch, tmp_path):
    root = tmp_path / "media"
    root.mkdir()
    (root / "clip.mp4").write_bytes(b"fake media")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "media",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
        },
    )
    monkeypatch.setattr(database, "corpus_job_is_running", lambda _job_id: True)
    monkeypatch.setattr(database, "apply_staged_extraction_plan_for_job", lambda **kwargs: applied.append(kwargs) or True)
    monkeypatch.setattr(worker, "extract_file", lambda *_args: (_ for _ in ()).throw(AssertionError("parent media job should plan staged work")))
    monkeypatch.setattr(
        worker,
        "plan_staged_media_extraction",
        lambda *_args, **_kwargs: worker.ExtractionResult(
            status="staged",
            metadata={
                "extractor": "video",
                "staged_extraction": {
                    "status": "planned",
                    "pending_job_count": 1,
                    "next_job_type": "corpus_extract_media_segment",
                },
                "staged_jobs": [
                    {
                        "job_type": "corpus_extract_media_segment",
                        "payload": {"segment_index": 0},
                    }
                ],
            },
        ),
    )

    result = worker.process_corpus_job(
        {
            "id": "job-video",
            "job_type": "corpus_extract_video",
            "payload": {"root_name": "media", "path": "clip.mp4"},
        }
    )

    assert result.status == "staged"
    assert result.telemetry["staged_job_count"] == 1
    assert result.telemetry["next_job_type"] == "corpus_extract_media_segment"
    assert applied[0]["job_id"] == "job-video"
    assert applied[0]["result"].status == "staged"


def test_process_corpus_segment_job_appends_piece_and_queues_next(monkeypatch, tmp_path):
    root = tmp_path / "media"
    root.mkdir()
    (root / "clip.mp4").write_bytes(b"fake media")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "media",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
        },
    )
    monkeypatch.setattr(database, "corpus_job_is_running", lambda _job_id: True)
    monkeypatch.setattr(database, "apply_staged_extraction_piece_for_job", lambda **kwargs: applied.append(kwargs) or True)
    monkeypatch.setattr(worker, "extract_file", lambda *_args: (_ for _ in ()).throw(AssertionError("segment job should use staged extractor")))
    monkeypatch.setattr(
        worker,
        "extract_media_segment",
        lambda *_args, **_kwargs: worker.ExtractionResult(
            status="staged",
            chunks=(
                worker.AssetChunk(
                    chunk_index=0,
                    title="clip.mp4 segment 1",
                    body="segment transcript",
                    modality="transcript",
                ),
            ),
            metadata={
                "extractor": "media_segment",
                "asr": {"status": "completed", "segments": 1},
                "staged_extraction": {
                    "status": "piece_completed",
                    "complete": False,
                    "next_job": {
                        "job_type": "corpus_extract_media_segment",
                        "payload": {"segment_index": 1},
                    },
                },
            },
        ),
    )

    result = worker.process_corpus_job(
        {
            "id": "job-segment",
            "job_type": "corpus_extract_media_segment",
            "payload": {"root_name": "media", "path": "clip.mp4", "segment_index": 0},
        }
    )

    assert result.status == "staged"
    assert result.telemetry["asr_segments"] == 1
    assert result.telemetry["next_job_type"] == "corpus_extract_media_segment"
    assert applied[0]["job_id"] == "job-segment"
    assert applied[0]["result"].chunks[0].body == "segment transcript"


def test_process_corpus_job_plans_staged_pdf_ocr(monkeypatch, tmp_path):
    root = tmp_path / "docs"
    root.mkdir()
    (root / "scan.pdf").write_bytes(b"%PDF")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
        },
    )
    monkeypatch.setattr(database, "corpus_job_is_running", lambda _job_id: True)
    monkeypatch.setattr(database, "apply_staged_extraction_plan_for_job", lambda **kwargs: applied.append(kwargs) or True)
    monkeypatch.setattr(worker, "extract_file", lambda *_args: (_ for _ in ()).throw(AssertionError("PDF parent job should plan OCR pages")))
    monkeypatch.setattr(
        worker,
        "plan_staged_pdf_extraction",
        lambda *_args, **_kwargs: worker.ExtractionResult(
            status="staged",
            metadata={
                "extractor": "pdf",
                "page_count": 42,
                "staged_extraction": {
                    "status": "planned",
                    "pending_job_count": 1,
                    "next_job_type": "corpus_extract_pdf_ocr_pages",
                },
                "staged_jobs": [
                    {
                        "job_type": "corpus_extract_pdf_ocr_pages",
                        "payload": {"page_start": 1, "page_end": 5},
                    }
                ],
            },
        ),
    )

    result = worker.process_corpus_job(
        {
            "id": "job-pdf",
            "job_type": "corpus_extract_document",
            "payload": {"root_name": "docs", "path": "scan.pdf"},
        }
    )

    assert result.status == "staged"
    assert result.telemetry["staged_job_count"] == 1
    assert result.telemetry["next_job_type"] == "corpus_extract_pdf_ocr_pages"
    assert applied[0]["result"].metadata["page_count"] == 42


def test_process_corpus_job_merges_parser_and_media_diagnostics(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "media"
    root.mkdir()
    (root / "clip.mp4").write_bytes(b"fake media")
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "media",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
        },
    )
    monkeypatch.setattr(database, "apply_extraction_result", lambda **_kwargs: None)
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda *_args: SimpleNamespace(
            status="indexed",
            message=None,
            metadata={
                "parser_cache": {"hits": 3, "misses": 1},
                "asr": {"segments": 4, "duration_seconds": 12.5, "sidecar_used": True, "source": "sidecar"},
                "frame_sampling": {"frame_count": 2, "timestamps": [0.0, 5.0]},
                "blocked_dependency_reason": "ffprobe_missing",
            },
        ),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "media", "path": "clip.mp4"}})

    assert result.telemetry["parser_cache_hits"] == 3
    assert result.telemetry["parser_cache_misses"] == 1
    assert result.telemetry["asr_segments"] == 4
    assert result.telemetry["asr_duration_seconds"] == 12
    assert result.telemetry["asr_sidecar_used"] is True
    assert result.telemetry["asr_source"] == "sidecar"
    assert result.telemetry["frame_sample_count"] == 2
    assert result.telemetry["frame_sample_timestamps"] == [0.0, 5.0]
    assert result.telemetry["blocked_dependency_reason"] == "ffprobe_missing"


def test_worker_telemetry_includes_practical_corpus_parser_counts():
    from flux_llm_kb import worker

    telemetry = worker._telemetry_from_extraction_result(
        SimpleNamespace(
            metadata={
                "extractor": "report",
                "message_count": 3,
                "event_count": 2,
                "contact_count": 4,
                "finding_count": 5,
                "test_count": 6,
                "entry_count": 7,
                "table_count": 8,
                "component_count": 9,
                "package_count": 10,
                "covered_line_count": 11,
                "line_count": 12,
                "sensitive": True,
            }
        )
    )

    assert telemetry["mail_message_count"] == 3
    assert telemetry["calendar_event_count"] == 2
    assert telemetry["contact_count"] == 4
    assert telemetry["report_finding_count"] == 5
    assert telemetry["report_test_count"] == 6
    assert telemetry["har_entry_count"] == 7
    assert telemetry["database_table_count"] == 8
    assert telemetry["report_component_count"] == 9
    assert telemetry["report_package_count"] == 10
    assert telemetry["coverage_covered_line_count"] == 11
    assert telemetry["coverage_line_count"] == 12
    assert telemetry["sensitive_metadata"] is True


def test_worker_telemetry_includes_ocr_status_and_error():
    telemetry = worker._telemetry_from_extraction_result(
        SimpleNamespace(
            metadata={
                "ocr": {
                    "status": "blocked_invalid_source",
                    "error_code": "ocr.invalid_image_input",
                    "error": "OCR image payload is not a readable image",
                    "cache_hits": 0,
                    "cache_misses": 1,
                }
            }
        )
    )

    assert telemetry["ocr_status"] == "blocked_invalid_source"
    assert telemetry["ocr_error_code"] == "ocr.invalid_image_input"
    assert telemetry["ocr_error"] == "OCR image payload is not a readable image"
    assert telemetry["ocr_cache_hits"] == 0
    assert telemetry["ocr_cache_misses"] == 1


def test_process_corpus_job_merges_visual_enrichment_telemetry(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "media"
    root.mkdir()
    (root / "clip.mp4").write_bytes(b"fake media")
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "media",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
        },
    )
    monkeypatch.setattr(database, "apply_extraction_result", lambda **_kwargs: None)
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda *_args: type(
            "Extraction",
            (),
            {
                "status": "indexed",
                "message": None,
                "metadata": {
                    "decorative": {"status": "skipped"},
                    "vision": {
                        "status": "failed",
                        "error": "HTTP Error 500 from qwen",
                        "cache_hits": 1,
                        "cache_misses": 2,
                        "descriptions": 3,
                        "blocked_dependency_count": 1,
                    },
                    "frame_sampling": {
                        "frame_count": 2,
                        "thumbnail_cache_hits": 4,
                        "thumbnail_cache_misses": 5,
                    },
                },
            },
        )(),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "media", "path": "clip.mp4"}})

    assert result.status == "indexed"
    assert result.telemetry == {
        "decorative_image_skips": 1,
        "vision_status": "failed",
        "vision_error": "HTTP Error 500 from qwen",
        "vision_cache_hits": 1,
        "vision_cache_misses": 2,
        "vision_descriptions": 3,
        "vision_blocked_dependency_count": 1,
        "frame_sample_count": 2,
        "thumbnail_cache_hits": 4,
        "thumbnail_cache_misses": 5,
    }


def test_process_corpus_job_uses_container_policy_and_merges_container_telemetry(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "bundle.zip").write_bytes(b"fake archive")
    captured = {}
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
        },
    )
    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_DEPTH", "4")
    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_MEMBERS", "17")
    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_TOTAL_BYTES", "4096")
    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_MEMBER_BYTES", "512")
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))

    def fake_extract(path, policy):
        captured["path"] = path
        captured["policy"] = policy
        return SimpleNamespace(
            status="metadata_only",
            message=None,
            chunks=(),
            child_assets=(),
            metadata={
                "extractor": "container",
                "member_count": 5,
                "parsed_child_count": 2,
                "skipped_child_count": 3,
                "blocked_dependency_count": 1,
                "max_depth": 4,
            },
        )

    monkeypatch.setattr(worker, "extract_file", fake_extract)

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "bundle.zip"}})

    assert captured["path"] == root / "bundle.zip"
    assert captured["policy"].container_max_depth == 4
    assert captured["policy"].container_max_members == 17
    assert captured["policy"].container_max_total_bytes == 4096
    assert captured["policy"].container_max_member_bytes == 512
    assert result.status == "metadata_only"
    assert result.telemetry == {
        "container_member_count": 5,
        "container_parsed_child_count": 2,
        "container_skipped_child_count": 3,
        "container_blocked_dependency_count": 1,
        "container_max_depth": 4,
    }
    assert applied[0]["relative_path"] == "bundle.zip"


def test_process_corpus_job_blocks_metadata_only_for_strict_roots(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "unknown.bin").write_bytes(b"\x00\x01\x02")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
            "metadata": {"strict_indexing": True},
        },
    )
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda _path, _policy: SimpleNamespace(
            status="metadata_only",
            message="no local extractor",
            chunks=(),
            child_assets=(),
            metadata={"extractor": "binary"},
        ),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "unknown.bin"}})

    assert result.status == "blocked_by_policy"
    assert "Strict indexing" in result.message
    applied_result = applied[0]["result"]
    assert applied_result.status == "blocked_by_policy"
    assert applied_result.chunks == ()
    assert applied_result.metadata["strict_indexing"] is True
    assert applied_result.metadata["metadata_only_blocked"] is True
    assert applied_result.metadata["readiness_status"] == "blocked_by_policy"
    assert applied_result.metadata["original_status"] == "metadata_only"


def test_process_corpus_job_allows_decorative_metadata_only_for_strict_roots(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "icon.png").write_bytes(b"fake image")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
            "metadata": {"strict_indexing": True},
        },
    )
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda _path, _policy: SimpleNamespace(
            status="metadata_only",
            message=None,
            chunks=(),
            child_assets=(),
            metadata={"extractor": "image", "decorative": {"status": "skipped", "reason": "small_icon"}},
        ),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "icon.png"}})

    assert result.status == "indexed"
    assert result.telemetry["decorative_image_skips"] == 1
    applied_result = applied[0]["result"]
    assert applied_result.status == "indexed"
    assert applied_result.chunks == ()
    assert applied_result.metadata["strict_indexing"] is True
    assert applied_result.metadata["decorative_indexed"] is True
    assert applied_result.metadata["readiness_status"] == "completed_no_content"
    assert applied_result.metadata["no_content_reason"] == "decorative_image"
    assert "metadata_only_blocked" not in applied_result.metadata


def test_process_corpus_job_allows_image_no_content_after_vision_attempt_for_strict_roots(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "scan.png").write_bytes(b"fake image")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
            "metadata": {"strict_indexing": True},
        },
    )
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda _path, _policy: SimpleNamespace(
            status="metadata_only",
            message=None,
            chunks=(),
            child_assets=(),
            metadata={
                "extractor": "image",
                "ocr": {"status": "completed", "text_length": 0},
                "vision": {"status": "completed", "descriptions": 0},
                "vision_escalation": "no_content",
            },
        ),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "scan.png"}})

    assert result.status == "indexed"
    applied_result = applied[0]["result"]
    assert applied_result.status == "indexed"
    assert applied_result.chunks == ()
    assert applied_result.metadata["strict_indexing"] is True
    assert applied_result.metadata["readiness_status"] == "completed_no_content"
    assert applied_result.metadata["no_content_reason"] == "image_ocr_and_vision_empty"
    assert applied_result.metadata["vision_escalation"] == "no_content"
    assert "metadata_only_blocked" not in applied_result.metadata


def test_process_corpus_job_marks_strict_indexed_vision_result_ready(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "scan.png").write_bytes(b"fake image")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
            "metadata": {"strict_indexing": True},
        },
    )
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda _path, _policy: SimpleNamespace(
            status="indexed",
            message=None,
            chunks=(SimpleNamespace(body="vision caption"),),
            child_assets=(),
            metadata={
                "extractor": "image",
                "ocr": {"status": "completed", "text_length": 0},
                "vision": {"status": "completed", "descriptions": 1},
                "vision_escalation": "completed",
                "readiness_status": "blocked_by_policy",
                "metadata_only_blocked": True,
            },
        ),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "scan.png"}})

    assert result.status == "indexed"
    applied_result = applied[0]["result"]
    assert applied_result.status == "indexed"
    assert applied_result.metadata["strict_indexing"] is True
    assert applied_result.metadata["readiness_status"] == "indexed"
    assert applied_result.metadata["readiness_reason"] == "content_extracted"
    assert applied_result.metadata["vision_escalation"] == "completed"
    assert "metadata_only_blocked" not in applied_result.metadata


@pytest.mark.parametrize(
    ("extractor", "extra_metadata", "expected_reason"),
    [
        ("docx", {}, "docx_empty"),
        ("pptx", {"slide_count": 3}, "pptx_empty"),
        ("pdf", {"ocr": {"status": "completed", "pages_attempted": 2}}, "pdf_text_and_ocr_empty"),
    ],
)
def test_process_corpus_job_allows_office_no_content_after_successful_extraction_for_strict_roots(
    monkeypatch,
    tmp_path,
    extractor,
    extra_metadata,
    expected_reason,
):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "empty.docx").write_bytes(b"fake office")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
            "metadata": {"strict_indexing": True},
        },
    )
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda _path, _policy: SimpleNamespace(
            status="metadata_only",
            message=None,
            chunks=(),
            child_assets=(),
            metadata={"extractor": extractor, **extra_metadata},
        ),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "empty.docx"}})

    assert result.status == "indexed"
    applied_result = applied[0]["result"]
    assert applied_result.metadata["readiness_status"] == "completed_no_content"
    assert applied_result.metadata["no_content_reason"] == expected_reason
    assert "metadata_only_blocked" not in applied_result.metadata


def test_process_corpus_job_allows_container_parent_when_children_are_extracted_for_strict_roots(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "bundle.zip").write_bytes(b"fake archive")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
            "metadata": {"strict_indexing": True},
        },
    )
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda _path, _policy: SimpleNamespace(
            status="metadata_only",
            message=None,
            chunks=(),
            child_assets=(SimpleNamespace(path="child.txt"),),
            metadata={
                "extractor": "container",
                "child_asset_count": 1,
                "parsed_child_count": 1,
                "skipped_child_count": 0,
                "blocked_dependency_count": 0,
            },
        ),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "bundle.zip"}})

    assert result.status == "indexed"
    assert result.telemetry["container_parsed_child_count"] == 1
    applied_result = applied[0]["result"]
    assert applied_result.status == "indexed"
    assert applied_result.child_assets == (SimpleNamespace(path="child.txt"),)
    assert applied_result.metadata["container_children_indexed"] is True
    assert "metadata_only_blocked" not in applied_result.metadata


def test_process_corpus_job_allows_partial_container_parent_when_safe_children_are_extracted(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "bundle.zip").write_bytes(b"fake archive")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
            "metadata": {"strict_indexing": True},
        },
    )
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda _path, _policy: SimpleNamespace(
            status="metadata_only",
            message="member exceeds size limit",
            chunks=(),
            child_assets=(SimpleNamespace(path="child.txt"),),
            metadata={
                "extractor": "container",
                "child_asset_count": 2,
                "parsed_child_count": 1,
                "skipped_child_count": 1,
                "blocked_dependency_count": 0,
                "warnings": ["member exceeds size limit"],
            },
        ),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "bundle.zip"}})

    assert result.status == "indexed"
    applied_result = applied[0]["result"]
    assert applied_result.status == "indexed"
    assert applied_result.metadata["container_children_indexed"] is True
    assert applied_result.metadata["partial_extraction"] is True
    assert applied_result.metadata["readiness_status"] == "completed_partial"
    assert "metadata_only_blocked" not in applied_result.metadata


def test_process_corpus_job_allows_archive_no_content_when_members_are_policy_skipped(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "bundle.zip").write_bytes(b"fake archive")
    applied = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
            "metadata": {"strict_indexing": True},
        },
    )
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda _path, _policy: SimpleNamespace(
            status="metadata_only",
            message=None,
            chunks=(),
            child_assets=(SimpleNamespace(extraction_status="metadata_only"),),
            metadata={
                "extractor": "container",
                "child_asset_count": 1,
                "parsed_child_count": 0,
                "skipped_child_count": 1,
                "blocked_dependency_count": 0,
                "skipped_member_size_limit_count": 1,
                "warnings": ["member exceeds size limit"],
            },
        ),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "bundle.zip"}})

    assert result.status == "indexed"
    applied_result = applied[0]["result"]
    assert applied_result.status == "indexed"
    assert applied_result.metadata["readiness_status"] == "completed_no_content"
    assert applied_result.metadata["no_content_reason"] == "archive_members_exceeded_size_limit"
    assert applied_result.metadata["skipped_member_size_limit_count"] == 1
    assert "metadata_only_blocked" not in applied_result.metadata


def test_sync_corpus_uses_container_cap_settings(monkeypatch, tmp_path):
    from flux_llm_kb import service as service_module

    root = tmp_path / "docs"
    root.mkdir()
    captured = {}
    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_DEPTH", "3")
    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_MEMBERS", "19")
    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_TOTAL_BYTES", "8192")
    monkeypatch.setenv("FLUX_KB_CRAWLER_CONTAINER_MAX_MEMBER_BYTES", "1024")
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [
            {
                "name": "docs",
                "root_path": str(root),
                "recursive": True,
                "include_globs": [],
                "exclude_globs": [],
                "glob_mode": "extend",
                "max_inline_bytes": 1024,
                "heavy_threshold_bytes": 2048,
            }
        ],
    )

    def fake_scan(_root_path, policy, target_path=None, progress_callback=None):
        captured["policy"] = policy
        captured["target_path"] = target_path
        captured["manifest"] = policy.manifest_lookup("docs/readme.md")
        if progress_callback:
            progress_callback({"stage": "enumerated", "files_total": 1})
        return SimpleNamespace(assets=[], deferred_jobs=[], errors=[], root_path=root)

    monkeypatch.setattr(service_module, "scan_path", fake_scan)
    monkeypatch.setattr(
        database,
        "load_scan_manifest",
        lambda **_kwargs: {
            "docs/readme.md": {
                "content_hash": "cached",
                "source_asset_status": "indexed",
                "chunk_count": 1,
            }
        },
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "lookup_scan_manifest",
        lambda **_kwargs: (_ for _ in ()).throw(AssertionError("per-file manifest lookup should not be used")),
    )
    def fake_persist(**kwargs):
        captured["persist_progress_callback"] = kwargs.get("progress_callback")
        if kwargs.get("progress_callback"):
            kwargs["progress_callback"]({"stage": "persisted", "files_total": 1})
        return {"root_name": kwargs["root_name"]}

    monkeypatch.setattr(database, "persist_crawl_plan", fake_persist)

    progress = []
    result = KnowledgeService().sync_corpus(root_name="docs", progress_callback=progress.append)

    assert result == {"root_name": "docs"}
    assert captured["policy"].container_max_depth == 3
    assert captured["policy"].container_max_members == 19
    assert captured["policy"].container_max_total_bytes == 8192
    assert captured["policy"].container_max_member_bytes == 1024
    assert callable(captured["policy"].manifest_lookup)
    assert captured["manifest"]["content_hash"] == "cached"
    assert callable(captured["persist_progress_callback"])
    assert progress == [{"stage": "enumerated", "files_total": 1}, {"stage": "persisted", "files_total": 1}]


def test_sync_corpus_uses_configured_content_hash_mode(monkeypatch, tmp_path):
    from flux_llm_kb import service as service_module

    root = tmp_path / "docs"
    root.mkdir()
    captured = {}
    monkeypatch.setattr(service_module, "_configured_content_hash_mode", lambda: "all_eligible")
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [
            {
                "name": "docs",
                "root_path": str(root),
                "recursive": True,
                "include_globs": [],
                "exclude_globs": [],
                "glob_mode": "extend",
                "max_inline_bytes": 1024,
                "heavy_threshold_bytes": 2048,
            }
        ],
    )

    def fake_scan(_root_path, policy, target_path=None, progress_callback=None):
        captured["policy"] = policy
        return SimpleNamespace(assets=[], deferred_jobs=[], errors=[], root_path=root)

    monkeypatch.setattr(service_module, "scan_path", fake_scan)
    monkeypatch.setattr(database, "load_scan_manifest", lambda **_kwargs: {}, raising=False)
    monkeypatch.setattr(database, "persist_crawl_plan", lambda **kwargs: {"root_name": kwargs["root_name"]})

    result = KnowledgeService().sync_corpus(root_name="docs")

    assert result == {"root_name": "docs"}
    assert captured["policy"].content_hash_mode == "all_eligible"


def test_process_corpus_job_uses_configured_content_hash_mode(monkeypatch, tmp_path):
    root = tmp_path / "docs"
    root.mkdir()
    target = root / "brief.docx"
    target.write_bytes(b"PK\x03\x04")
    captured = {}
    monkeypatch.setattr(worker, "_configured_content_hash_mode", lambda: "all_eligible")
    monkeypatch.setattr(database, "corpus_job_is_running", lambda _job_id: True)
    monkeypatch.setattr(database, "apply_extraction_result_for_job", lambda **_kwargs: True)
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _root_name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": [],
            "glob_mode": "extend",
            "max_inline_bytes": 1024,
            "heavy_threshold_bytes": 2048,
            "metadata": {},
        },
    )

    def fake_extract_file(path, policy):
        captured["path"] = path
        captured["policy"] = policy
        return worker.ExtractionResult(status="metadata_only", metadata={"extractor": "fake"}, chunks=())

    monkeypatch.setattr(worker, "extract_file", fake_extract_file)

    result = worker.process_corpus_job(
        {
            "id": "job-1",
            "job_type": "corpus_extract_document",
            "payload": {"root_name": "docs", "path": "brief.docx"},
        }
    )

    assert result.status == "metadata_only"
    assert captured["path"] == target
    assert captured["policy"].content_hash_mode == "all_eligible"


def test_reconcile_unseen_assets_for_root_marks_paths_excluded_by_effective_policy(monkeypatch, tmp_path):
    root = tmp_path / "docs"
    root.mkdir()
    calls = []
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda _root_name: {
            "name": "docs",
            "root_path": str(root),
            "recursive": True,
            "include_globs": [],
            "exclude_globs": ["tmp/**"],
            "glob_mode": "extend",
            "max_inline_bytes": 262144,
            "heavy_threshold_bytes": 10485760,
            "metadata": {},
        },
    )
    monkeypatch.setattr(database, "list_active_source_asset_paths", lambda **_kwargs: ["keep/readme.md", "tmp/secret.pdf"], raising=False)
    monkeypatch.setattr(
        database,
        "mark_unseen_source_assets",
        lambda **kwargs: calls.append(kwargs) or {"assets_marked": len(kwargs["paths"]), "jobs_cancelled": len(kwargs["paths"])},
        raising=False,
    )
    monkeypatch.setattr(service_module, "_configured_unseen_asset_purge_grace_seconds", lambda: 86400, raising=False)

    result = KnowledgeService().reconcile_unseen_assets_for_root(root_name="docs", reason="root_policy_update")

    assert result == {"root_name": "docs", "reason": "root_policy_update", "assets_marked": 1, "jobs_cancelled": 1}
    assert calls == [{"root_name": "docs", "paths": ["tmp/secret.pdf"], "reason": "root_policy_update", "grace_seconds": 86400}]


def test_backfill_passes_configured_worker_family_caps(monkeypatch):
    claim_calls = []
    monkeypatch.setenv("FLUX_KB_WORKER_CAP_MEDIA", "3")
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)

    def fake_claim_corpus_jobs(*, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None):
        claim_calls.append({"family_caps": family_caps, "job_families": job_families})
        return []

    monkeypatch.setattr(database, "claim_corpus_jobs", fake_claim_corpus_jobs)
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **_kwargs: {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **_kwargs: {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    result = KnowledgeService().run_corpus_backfill(kind="media", limit=2, workers=1)

    assert result["claimed"] == 0
    assert claim_calls[0]["job_families"] == ["media"]
    assert claim_calls[0]["family_caps"]["media"] == 3


def test_backfill_uses_configured_batch_and_worker_defaults(monkeypatch):
    claim_calls = []
    process_calls = []
    monkeypatch.setenv("FLUX_KB_WORKER_BATCH_SIZE", "24")
    monkeypatch.setenv("FLUX_KB_WORKER_DEFAULT_WORKERS", "8")
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)

    def fake_claim_corpus_jobs(*, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None):
        claim_calls.append({"limit": limit, "worker_id": worker_id})
        return []

    monkeypatch.setattr(database, "claim_corpus_jobs", fake_claim_corpus_jobs)
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **_kwargs: {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **_kwargs: {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)
    monkeypatch.setattr(KnowledgeService, "_process_claimed_corpus_jobs", lambda self, claimed, *, workers: process_calls.append(workers) or [])

    result = KnowledgeService().run_corpus_backfill(kind="all", limit=None, workers=None)

    assert result["claimed"] == 0
    assert claim_calls[0]["limit"] == 24
    assert process_calls == [8]


def test_backfill_preserves_explicit_serial_worker_values(monkeypatch):
    claim_calls = []
    process_calls = []
    monkeypatch.setenv("FLUX_KB_WORKER_BATCH_SIZE", "24")
    monkeypatch.setenv("FLUX_KB_WORKER_DEFAULT_WORKERS", "8")
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "claim_corpus_jobs", lambda **kwargs: claim_calls.append(kwargs) or [])
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **_kwargs: {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **_kwargs: {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)
    monkeypatch.setattr(KnowledgeService, "_process_claimed_corpus_jobs", lambda self, claimed, *, workers: process_calls.append(workers) or [])

    KnowledgeService().run_corpus_backfill(kind="all", limit=3, workers=1)

    assert claim_calls[0]["limit"] == 3
    assert process_calls == [1]


def test_backfill_accepts_exact_worker_family(monkeypatch):
    claim_calls = []
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: claim_calls.append(
            {"root_name": root_name, "job_families": job_families}
        )
        or [],
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **_kwargs: {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **_kwargs: {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    result = KnowledgeService().run_corpus_backfill(kind="office", limit=2, workers=1, root_name="docs")

    assert result["root_name"] == "docs"
    assert result["job_families"] == ["office"]
    assert claim_calls == [{"root_name": "docs", "job_families": ["office"]}]


def test_service_remediate_diagnostic_dispatches_safe_actions(monkeypatch):
    calls = []
    monkeypatch.setattr(database, "requeue_corpus_job", lambda **kwargs: calls.append(("retry", kwargs)) or {"job_id": kwargs["job_id"], "status": "pending"})
    monkeypatch.setattr(
        database,
        "enqueue_capture_job_command_by_id",
        lambda **kwargs: calls.append(("enqueue", kwargs))
        or {
            "job_id": kwargs["job_id"],
            "status": "pending",
            "message_id": "message-1",
            "routing_key": "corpus.process",
            "queued": True,
            "deduped": False,
        },
    )
    monkeypatch.setattr(database, "record_audit_event", lambda **kwargs: calls.append(("audit", kwargs)) or {"id": "audit-1"})

    retry = KnowledgeService().remediate_diagnostic(
        action="retry_corpus_job",
        target_type="job",
        target_id="job-1",
        root_name="docs",
        family="office",
        reason="operator retry",
        actor="cli",
    )

    assert retry["settings_mutated"] is False
    assert retry["action"] == "retry_corpus_job"
    assert retry["result"] == {
        "job_id": "job-1",
        "status": "pending",
        "command": {
            "job_id": "job-1",
            "status": "pending",
            "message_id": "message-1",
            "routing_key": "corpus.process",
            "queued": True,
            "deduped": False,
        },
        "queued": True,
        "message_id": "message-1",
        "routing_key": "corpus.process",
        "deduped": False,
    }
    assert calls[0] == (
        "retry",
        {"job_id": "job-1", "reason": "operator retry"},
    )
    assert calls[1] == (
        "enqueue",
        {"job_id": "job-1", "force_new_message": True},
    )
    assert calls[2][0] == "audit"
    assert calls[2][1]["event_type"] == "diagnostics.remediation"
    assert calls[2][1]["target_id"] is None
    assert calls[2][1]["details"]["action"] == "retry_corpus_job"
    assert calls[2][1]["details"]["target_id"] == "job-1"

    monkeypatch.setattr(KnowledgeService, "enqueue_corpus_backfill", lambda self, **kwargs: {"accepted": True, "backfill": kwargs})
    backfill = KnowledgeService().remediate_diagnostic(
        action="run_backfill",
        target_type="family",
        target_id="office",
        root_name="docs",
        family="office",
        reason="operator backfill",
    )

    assert backfill["result"] == {"accepted": True, "backfill": {"kind": "office", "limit": 10, "workers": 1, "root_name": "docs"}}
    assert calls[3][0] == "audit"
    assert calls[3][1]["target_id"] is None
    assert calls[3][1]["details"]["target_id"] == "office"

    monkeypatch.setattr(
        database,
        "repair_stranded_capture_commands",
        lambda **kwargs: calls.append(("repair_stranded", kwargs)) or {"applied": True, "affected_jobs": 1, "enqueued": 1},
        raising=False,
    )
    repair = KnowledgeService().remediate_diagnostic(
        action="repair_stranded_capture_command",
        target_type="job",
        target_id="11111111-1111-1111-1111-111111111111",
        root_name="docs",
        family="general",
        reason="repair stranded command",
        actor="cli",
    )

    assert repair["result"] == {"applied": True, "affected_jobs": 1, "enqueued": 1}
    assert calls[4] == (
        "repair_stranded",
        {
            "apply": True,
            "confirm": "stranded-capture-commands",
            "job_id": "11111111-1111-1111-1111-111111111111",
            "root_name": "docs",
            "family": "general",
            "min_age_seconds": 0,
            "limit": 1,
        },
    )
    assert calls[5][0] == "audit"
    assert calls[5][1]["target_id"] == "11111111-1111-1111-1111-111111111111"
    assert calls[5][1]["details"]["action"] == "repair_stranded_capture_command"


def test_service_remediate_diagnostic_preserves_obsolete_retry_conflict(monkeypatch):
    def fake_requeue(**_kwargs):
        raise LookupError("retryable corpus job not found: job-obsolete")

    monkeypatch.setattr(database, "requeue_corpus_job", fake_requeue)
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: pytest.fail("retry conflict should not be audited as success"))

    with pytest.raises(LookupError, match="retryable corpus job not found: job-obsolete"):
        KnowledgeService().remediate_diagnostic(
            action="retry_corpus_job",
            target_type="job",
            target_id="job-obsolete",
            reason="operator retry",
            actor="dashboard",
        )


def test_service_remediate_diagnostic_requires_scoped_backfill_and_cleanup():
    with pytest.raises(ValueError, match="root_name and exact worker family"):
        KnowledgeService().remediate_diagnostic(
            action="run_backfill",
            target_type="family",
            target_id="office",
            family="office",
        )

    with pytest.raises(ValueError, match="requires root_name"):
        KnowledgeService().remediate_diagnostic(action="repair_asset_statuses", target_type="root")


def test_benchmark_run_uses_synthetic_fixtures_and_records_history(monkeypatch):
    recorded = []
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: recorded.append(kwargs) or {"id": "run-1", "fixture": kwargs["fixture"]})

    result = KnowledgeService().run_benchmark(fixture="text-heavy", files=3)

    assert result["fixture"] == "text-heavy"
    assert result["runs"][0]["fixture"] == "text-heavy"
    assert recorded[0]["fixture"] == "text-heavy"
    assert recorded[0]["file_count"] == 3
    assert "root_path" not in recorded[0]["metadata"]


def test_benchmark_scan_mode_records_cold_and_warm_passes(monkeypatch):
    recorded = []
    monkeypatch.setattr(service_module, "_configured_hash_parallelism", lambda: 3)
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: recorded.append(kwargs) or {"id": f"run-{len(recorded)}", "fixture": kwargs["fixture"]})

    result = KnowledgeService().run_benchmark(fixture="text-heavy", files=2, mode="scan", passes=2, label="nightly", compare_label="baseline")

    assert result["mode"] == "scan"
    assert result["recommendations"]["settings_mutated"] is False
    assert [run["warm_state"] for run in result["runs"]] == ["cold", "warm"]
    assert [run["pass_index"] for run in result["runs"]] == [1, 2]
    assert recorded[0]["mode"] == "scan"
    assert recorded[0]["label"] == "nightly"
    assert recorded[0]["compare_label"] == "baseline"
    assert recorded[0]["hash_parallelism"] == 3
    assert recorded[1]["warm_state"] == "warm"
    assert recorded[1]["manifest_skipped_unchanged"] == 2
    assert recorded[1]["cache_hits"] == 2


def test_benchmark_history_forwards_reliability_filters(monkeypatch):
    captured = {}
    monkeypatch.setattr(database, "list_benchmark_runs", lambda **kwargs: captured.update(kwargs) or [{"id": "run-1"}])

    payload = KnowledgeService().benchmark_history(
        fixture="monitored-root",
        mode="scan",
        label="nightly",
        warm_state="warm",
        scope_type="monitored_root",
        deployment_label="desktop",
        scenario="host_cloud",
        scope_hash="sha256:root",
        freshness_hours=24,
        limit=7,
    )

    assert payload["runs"] == [{"id": "run-1"}]
    assert captured == {
        "fixture": "monitored-root",
        "mode": "scan",
        "label": "nightly",
        "warm_state": "warm",
        "scope_type": "monitored_root",
        "deployment_label": "desktop",
        "scenario": "host_cloud",
        "scope_hash": "sha256:root",
        "freshness_hours": 24,
        "limit": 7,
    }


def test_benchmark_reliability_scenario_returns_diagnostics_and_persists_metadata(monkeypatch):
    recorded = []
    calls = {"created": [], "claimed": [], "completed": [], "blocked": [], "purged": []}
    monkeypatch.setattr(service_module, "_configured_hash_parallelism", lambda: 2)
    monkeypatch.setattr(service_module, "_configured_lock_max_attempts", lambda: 3)
    monkeypatch.setattr(service_module, "_configured_lock_retry_cooldown_seconds", lambda: 45)
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: recorded.append(kwargs) or {"id": f"run-{len(recorded)}", "fixture": kwargs["fixture"]})
    monkeypatch.setattr(service_module, "probe_watcher_backend", lambda **kwargs: {"backend": {"selected_backend": "polling"}, "events": {"created": 1, "modified": 1}, "latency_ms": 9})
    monkeypatch.setattr(database, "create_benchmark_soak_jobs", lambda **kwargs: calls["created"].append(kwargs) or {"tag": kwargs["tag"], "created": kwargs["file_count"]})

    def fake_claim(**kwargs):
        calls["claimed"].append(kwargs)
        return [
            {"id": "job-1", "job_family": "office", "resource_class": "cpu", "payload": {"benchmark_outcome": "completed"}, "attempts": 1},
            {"id": "job-2", "job_family": "office", "resource_class": "cpu", "payload": {"benchmark_outcome": "blocked"}, "attempts": 3},
        ]

    monkeypatch.setattr(database, "claim_corpus_jobs", fake_claim)
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "purge_benchmark_soak_jobs", lambda **kwargs: calls["purged"].append(kwargs) or {"purged": 2})

    result = KnowledgeService().run_benchmark(fixture="text-heavy", files=2, mode="all", passes=2, scenario="reliability", workers=2)

    assert result["scenario"] == "reliability"
    diagnostics = {item["check"]: item for item in result["diagnostics"]}
    assert {"file_churn", "lock_recovery", "watch_reconcile"} <= set(diagnostics)
    assert diagnostics["file_churn"]["evidence"]["warm_manifest_skips"] == 2
    assert diagnostics["file_churn"]["evidence"]["large_write_bytes"] >= 1024 * 1024
    assert diagnostics["file_churn"]["evidence"]["rename_save_detected"] is True
    assert diagnostics["file_churn"]["evidence"]["transient_skipped"] == 2
    assert diagnostics["file_churn"]["evidence"]["pending_stable_count"] >= 1
    assert diagnostics["file_churn"]["evidence"]["probe_warm_manifest_skips"] >= 1
    assert diagnostics["lock_recovery"]["evidence"]["blocked_locked"] == 1
    assert diagnostics["lock_recovery"]["evidence"]["retry_cooldown_seconds"] == 45
    assert diagnostics["watch_reconcile"]["evidence"]["watcher_backend"] == "polling"
    assert result["recommendations"]["settings_mutated"] is False
    assert result["recommendations"]["scenario"] == "reliability"
    assert recorded
    assert {row["recommendation_metadata"]["scenario"] for row in recorded} == {"reliability"}
    assert all(row["recommendation_metadata"]["settings_mutated"] is False for row in recorded)


def test_benchmark_tuning_scenario_returns_manual_recommendation_candidates(monkeypatch):
    recorded = []
    monkeypatch.setattr(service_module, "_configured_hash_parallelism", lambda: 1)
    monkeypatch.setattr(service_module, "_configured_worker_caps", lambda: {"general": 1, "office": 1, "media": 1})
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: recorded.append(kwargs) or {"id": "run-tuning", "fixture": kwargs["fixture"]})

    result = KnowledgeService().run_benchmark(fixture="text-heavy", files=2, mode="scan", passes=2, scenario="tuning", workers=1)

    assert result["scenario"] == "tuning"
    assert result["recommendations"]["settings_mutated"] is False
    assert result["recommendations"]["candidates"]
    assert all(candidate["requires_manual_apply"] is True for candidate in result["recommendations"]["candidates"])
    assert {candidate["setting"] for candidate in result["recommendations"]["candidates"]} >= {
        "crawler.hash_parallelism",
        "acceleration.worker_cap.general",
    }
    assert "tuning" in {item["check"] for item in result["diagnostics"]}
    assert recorded[0]["recommendation_metadata"]["scenario"] == "tuning"


def test_benchmark_real_root_scope_records_only_sanitized_aggregate_metadata(monkeypatch, tmp_path):
    root = tmp_path / "docs"
    root.mkdir()
    (root / "one.md").write_text("one", encoding="utf-8")
    recorded = []
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [
            {
                "name": "docs",
                "root_path": str(root),
                "recursive": True,
                "include_globs": [],
                "exclude_globs": [],
                "glob_mode": "extend",
                "max_inline_bytes": 256 * 1024,
                "heavy_threshold_bytes": 10 * 1024 * 1024,
                "metadata": {"host_access": "direct"},
            }
        ],
    )
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: recorded.append(kwargs) or {"id": "run-root", "fixture": kwargs["fixture"]})

    result = KnowledgeService().run_benchmark(scope="root", root_name="docs", mode="scan", max_files=5, deployment_label="after-update")

    assert result["scope_type"] == "monitored_root"
    assert result["runs"][0]["scope_type"] == "monitored_root"
    assert result["runs"][0]["deployment_label"] == "after-update"
    assert recorded[0]["scope_type"] == "monitored_root"
    assert recorded[0]["scope_hash"].startswith("sha256:")
    assert recorded[0]["deployment_label"] == "after-update"
    assert recorded[0]["settings_snapshot"]["hash_parallelism"] >= 1
    assert recorded[0]["recommendation_metadata"]["settings_mutated"] is False
    serialized = json.dumps(recorded[0], default=str)
    assert str(root) not in serialized
    assert "root_path" not in serialized


def test_benchmark_host_cloud_scenario_records_only_aggregate_scope_hash(monkeypatch, tmp_path):
    root = tmp_path / "cloud-docs"
    root.mkdir()
    (root / "one.md").write_text("one", encoding="utf-8")
    recorded = []
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [
            {
                "name": "cloud-docs",
                "root_path": str(root),
                "recursive": True,
                "include_globs": [],
                "exclude_globs": [],
                "glob_mode": "extend",
                "max_inline_bytes": 256 * 1024,
                "heavy_threshold_bytes": 10 * 1024 * 1024,
                "metadata": {"host_access": "host_agent"},
            }
        ],
    )
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: recorded.append(kwargs) or {"id": "run-host", "fixture": kwargs["fixture"]})

    result = KnowledgeService().run_benchmark(scope="root", root_name="cloud-docs", mode="scan", max_files=5, scenario="host_cloud")

    assert result["scenario"] == "host_cloud"
    assert result["scope_type"] == "monitored_root"
    diagnostics = {item["check"]: item for item in result["diagnostics"]}
    assert diagnostics["host_cloud"]["evidence"]["host_access"] == "host_agent"
    assert diagnostics["host_cloud"]["evidence"]["scope_hash"].startswith("sha256:")
    assert recorded[0]["recommendation_metadata"]["scenario"] == "host_cloud"
    serialized = json.dumps(result, default=str)
    assert str(root) not in serialized
    assert "root_path" not in serialized


def test_benchmark_host_cloud_scenario_rejects_synthetic_scope():
    with pytest.raises(ValueError, match="host_cloud"):
        KnowledgeService().run_benchmark(scenario="host_cloud")


def test_benchmark_model_mode_records_local_readiness_without_private_content(monkeypatch):
    recorded = []
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: recorded.append(kwargs) or {"id": "run-model", "fixture": kwargs["fixture"]})
    monkeypatch.setattr(
        service_module,
        "collect_acceleration_status",
        lambda: {
            "capabilities": {"local_model": {"ok": False, "state": "disabled", "provider": "ollama"}},
            "cache": {"root": "E:/private/cache"},
            "worker_families": [],
            "benchmarks": {},
        },
    )
    monkeypatch.setattr(
        service_module,
        "extractor_availability",
        lambda: {
            "paddleocr": {"ok": True, "message": "available"},
            "ffmpeg": {"ok": False, "message": "ffmpeg command not found"},
            "faster_whisper": {"ok": False, "message": "module not installed"},
        },
    )

    result = KnowledgeService().run_benchmark(fixture="image-heavy", mode="model", passes=2, deployment_label="after-update")

    assert result["mode"] == "model"
    assert [run["warm_state"] for run in result["runs"]] == ["cold", "warm"]
    assert result["runs"][0]["model_telemetry"]["local_model"]["state"] == "disabled"
    assert result["runs"][0]["model_telemetry"]["tools"]["ffmpeg"]["ok"] is False
    assert recorded[0]["model_telemetry"]["blocked_dependency_count"] == 2
    assert recorded[0]["deployment_label"] == "after-update"
    assert "private" not in json.dumps(recorded[0]["model_telemetry"]).lower()


def test_benchmark_cache_readiness_scenario_reports_tool_blocks_without_private_paths(monkeypatch):
    recorded = []
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: recorded.append(kwargs) or {"id": "run-cache", "fixture": kwargs["fixture"]})
    monkeypatch.setattr(
        service_module,
        "collect_acceleration_status",
        lambda: {
            "capabilities": {"local_model": {"ok": False, "state": "disabled", "provider": "ollama"}},
            "cache": {"root": "E:/private/cache", "source": "env", "directories": {"ocr": "E:/private/cache/ocr"}},
            "worker_families": [{"family": "media", "pending": 2, "retrying_locked": 1, "blocked_locked": 1}],
            "benchmarks": {},
        },
    )
    monkeypatch.setattr(
        service_module,
        "extractor_availability",
        lambda: {
            "paddleocr": {"ok": True, "message": "available"},
            "pdftoppm": {"ok": True, "message": "available"},
            "ffmpeg": {"ok": False, "message": "ffmpeg command not found"},
            "faster_whisper": {"ok": False, "message": "module not installed"},
        },
    )

    result = KnowledgeService().run_benchmark(fixture="image-heavy", mode="model", scenario="cache_readiness", deployment_label="after-update")

    assert result["scenario"] == "cache_readiness"
    diagnostics = {item["check"]: item for item in result["diagnostics"]}
    assert diagnostics["cache_readiness"]["evidence"]["cache_root_configured"] is True
    assert diagnostics["cache_readiness"]["evidence"]["blocked_dependency_count"] == 2
    assert diagnostics["cache_readiness"]["evidence"]["cache_directory_count"] == 1
    assert recorded[0]["recommendation_metadata"]["scenario"] == "cache_readiness"
    serialized = json.dumps({"diagnostics": result["diagnostics"], "recommendations": result["recommendations"]}, default=str).lower()
    assert "private" not in serialized
    assert "e:/private/cache" not in serialized


def test_indexer_reliability_run_orchestrates_scenarios_without_mutating_settings(monkeypatch):
    calls = []

    def fake_run_benchmark(self, **kwargs):
        calls.append(kwargs)
        return {
            "scenario": kwargs["scenario"],
            "scope_type": "monitored_root" if kwargs.get("scope") == "root" else "synthetic",
            "runs": [
                {
                    "id": f"run-{len(calls)}",
                    "scenario": kwargs["scenario"],
                    "mode": kwargs["mode"],
                    "scope_type": "monitored_root" if kwargs.get("scope") == "root" else "synthetic",
                    "recommendation_metadata": {"settings_mutated": False, "scenario": kwargs["scenario"]},
                }
            ],
            "recommendations": {"settings_mutated": False, "scenario": kwargs["scenario"], "candidates": []},
        }

    monkeypatch.setattr(KnowledgeService, "run_benchmark", fake_run_benchmark)
    monkeypatch.setattr(KnowledgeService, "indexer_reliability_status", lambda self, **kwargs: {"readiness": "ready", "settings_mutated": False, "status_args": kwargs})

    result = KnowledgeService().run_indexer_reliability(
        scope="root",
        root_name="docs",
        label="nightly",
        deployment_label="desktop",
        max_files=100,
        include_cache_readiness=True,
        include_tuning=True,
    )

    assert result["readiness"] == "ready"
    assert result["settings_mutated"] is False
    assert [call["scenario"] for call in calls] == ["reliability", "host_cloud", "cache_readiness", "tuning"]
    assert calls[0]["label"] == "nightly"
    assert calls[1]["scope"] == "root"
    assert calls[1]["root_name"] == "docs"
    assert calls[1]["max_files"] == 100
    assert calls[2]["mode"] == "model"
    assert calls[3]["mode"] == "scan"
    assert calls[3]["scope"] == "root"
    assert calls[3]["root_name"] == "docs"
    assert calls[3]["max_files"] == 100


def test_indexer_reliability_all_roots_runs_scoped_evidence_per_enabled_root(monkeypatch):
    calls = []

    monkeypatch.setattr(
        database,
        "crawl_root_summaries",
        lambda **kwargs: [
            {"name": "docs", "enabled": True, "root_path": "E:/private/docs"},
            {"name": "code", "enabled": True, "root_path": "E:/private/code"},
            {"name": "disabled", "enabled": False, "root_path": "E:/private/disabled"},
        ],
    )

    def fake_run_benchmark(self, **kwargs):
        calls.append(kwargs)
        return {
            "scenario": kwargs["scenario"],
            "scope_type": "monitored_root" if kwargs.get("scope") == "root" else "synthetic",
            "runs": [
                {
                    "id": f"run-{len(calls)}",
                    "scenario": kwargs["scenario"],
                    "scope_type": "monitored_root" if kwargs.get("scope") == "root" else "synthetic",
                    "recommendation_metadata": {"settings_mutated": False, "scenario": kwargs["scenario"]},
                }
            ],
            "recommendations": {"settings_mutated": False, "scenario": kwargs["scenario"], "candidates": []},
        }

    monkeypatch.setattr(KnowledgeService, "run_benchmark", fake_run_benchmark)
    monkeypatch.setattr(
        KnowledgeService,
        "indexer_reliability_roots",
        lambda self, **kwargs: {"settings_mutated": False, "roots": [], "status_args": kwargs},
    )

    result = KnowledgeService().run_indexer_reliability(
        scope="all_roots",
        label="nightly",
        deployment_label="desktop",
        max_files=250,
        include_cache_readiness=True,
        include_tuning=True,
    )

    assert result["settings_mutated"] is False
    assert [call["scenario"] for call in calls] == [
        "reliability",
        "host_cloud",
        "host_cloud",
        "cache_readiness",
        "tuning",
        "tuning",
    ]
    assert [call.get("root_name") for call in calls if call["scenario"] == "host_cloud"] == ["docs", "code"]
    assert all(call.get("max_files") == 250 for call in calls if call["scenario"] in {"host_cloud", "tuning"})
    assert "disabled" not in {call.get("root_name") for call in calls}


def test_benchmark_soak_mode_claims_worker_family_jobs_and_purges(monkeypatch):
    calls = {"created": [], "claimed": [], "completed": [], "blocked": [], "purged": []}
    monkeypatch.setattr(service_module, "_configured_worker_caps", lambda: {"media": 1, "office": 2})
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: {"id": "run-soak", "fixture": kwargs["fixture"]})
    monkeypatch.setattr(database, "create_benchmark_soak_jobs", lambda **kwargs: calls["created"].append(kwargs) or {"tag": kwargs["tag"], "created": kwargs["file_count"]})

    def fake_claim(**kwargs):
        calls["claimed"].append(kwargs)
        return [
            {"id": "job-1", "job_family": "media", "resource_class": "gpu", "payload": {"benchmark_outcome": "completed"}, "attempts": 0},
            {"id": "job-2", "job_family": "media", "resource_class": "gpu", "payload": {"benchmark_outcome": "blocked"}, "attempts": 0},
        ]

    monkeypatch.setattr(database, "claim_corpus_jobs", fake_claim)
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "purge_benchmark_soak_jobs", lambda **kwargs: calls["purged"].append(kwargs) or {"purged": 2})

    result = KnowledgeService().run_benchmark(fixture="image-heavy", files=4, mode="soak", workers=2, family="media", label="soak")

    assert result["mode"] == "soak"
    assert result["runs"][0]["jobs_queued"] == 4
    assert result["runs"][0]["jobs_completed"] == 1
    assert result["runs"][0]["jobs_blocked"] == 1
    assert calls["created"][0]["family"] == "media"
    assert calls["claimed"][0]["job_families"] == ["media"]
    assert calls["claimed"][0]["family_caps"] == {"media": 1, "office": 2}
    assert calls["completed"][0]["telemetry"]["benchmark_mode"] == "soak"
    assert calls["blocked"][0]["status"] == "blocked_benchmark"
    assert calls["purged"][0]["tag"] == calls["created"][0]["tag"]


def test_benchmark_soak_mode_rejects_unknown_worker_family():
    with pytest.raises(ValueError, match="benchmark family"):
        KnowledgeService().run_benchmark(fixture="image-heavy", files=4, mode="soak", family="unknown")


def test_benchmark_watcher_and_all_modes_record_metadata(monkeypatch):
    recorded = []
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: recorded.append(kwargs) or {"id": f"run-{len(recorded)}", "fixture": kwargs["fixture"]})
    monkeypatch.setattr(service_module, "probe_watcher_backend", lambda **kwargs: {"backend": {"policy": "polling", "selected_backend": "polling"}, "events": {"created": 1, "modified": 1}, "latency_ms": 12})
    monkeypatch.setattr(database, "create_benchmark_soak_jobs", lambda **kwargs: {"tag": kwargs["tag"], "created": kwargs["file_count"]})
    monkeypatch.setattr(database, "claim_corpus_jobs", lambda **kwargs: [])
    monkeypatch.setattr(database, "purge_benchmark_soak_jobs", lambda **kwargs: {"purged": 0})

    watcher = KnowledgeService().run_benchmark(fixture="text-heavy", mode="watcher", files=1)
    all_modes = KnowledgeService().run_benchmark(fixture="text-heavy", mode="all", files=1)

    assert watcher["runs"][0]["mode"] == "watcher"
    assert watcher["runs"][0]["metadata"]["watcher_backend"]["selected_backend"] == "polling"
    assert {run["mode"] for run in all_modes["runs"]} == {"scan", "soak", "watcher"}
    assert {row["mode"] for row in recorded} >= {"scan", "soak", "watcher"}


def test_backfill_retries_locked_jobs_with_lock_state(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-1",
                "job_type": "corpus_extract_document",
                "job_family": "office",
                "payload": {"path": "open.docx", "root_name": "docs"},
                "attempts": 1,
            }
        ],
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    monkeypatch.setattr(worker, "process_corpus_job", lambda job: worker.JobProcessResult(status="retrying_locked", message="file is locked"))

    result = KnowledgeService().run_corpus_backfill(kind="all", limit=1, workers=1)

    assert result["retried"] == 1
    assert calls["completed"] == []
    assert calls["blocked"] == []
    assert calls["retried"][0]["job_id"] == "job-1"
    assert calls["retried"][0]["status"] == "retrying_locked"
    assert calls["retried"][0]["cooldown_seconds"] > 0


def test_backfill_retries_vss_failed_jobs_with_vss_state(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-1",
                "job_type": "corpus_extract_document",
                "job_family": "office",
                "payload": {"path": "open.docx", "root_name": "docs"},
                "attempts": 1,
            }
        ],
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    monkeypatch.setattr(
        worker,
        "process_corpus_job",
        lambda job: worker.JobProcessResult(
            status="retrying_vss_failed",
            message="VSS access denied",
            telemetry={"vss_status": "failed", "vss_reason": "access_denied"},
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="all", limit=1, workers=1)

    assert result["retried"] == 1
    assert calls["completed"] == []
    assert calls["blocked"] == []
    assert calls["retried"][0]["job_id"] == "job-1"
    assert calls["retried"][0]["status"] == "retrying_vss_failed"
    assert calls["retried"][0]["cooldown_seconds"] > 0
    assert calls["retried"][0]["telemetry"]["vss_reason"] == "access_denied"


def test_backfill_blocks_persistently_locked_jobs(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-1",
                "job_type": "corpus_extract_document",
                "job_family": "office",
                "payload": {"path": "open.docx", "root_name": "docs"},
                "attempts": 5,
            }
        ],
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    monkeypatch.setattr(worker, "process_corpus_job", lambda job: worker.JobProcessResult(status="retrying_locked", message="file is locked"))

    result = KnowledgeService().run_corpus_backfill(kind="all", limit=1, workers=1)

    assert result["blocked"] == 1
    assert calls["retried"] == []
    assert calls["blocked"][0]["job_id"] == "job-1"
    assert calls["blocked"][0]["status"] == "blocked_locked"


def test_backfill_blocks_persistently_vss_failed_jobs(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-1",
                "job_type": "corpus_extract_document",
                "job_family": "office",
                "payload": {"path": "open.docx", "root_name": "docs"},
                "attempts": 5,
            }
        ],
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    monkeypatch.setattr(
        worker,
        "process_corpus_job",
        lambda job: worker.JobProcessResult(
            status="retrying_vss_failed",
            message="VSS provider failed",
            telemetry={"vss_status": "failed", "vss_reason": "provider_failure"},
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="all", limit=1, workers=1)

    assert result["blocked"] == 1
    assert calls["retried"] == []
    assert calls["blocked"][0]["job_id"] == "job-1"
    assert calls["blocked"][0]["status"] == "blocked_vss_failed"
    assert calls["blocked"][0]["telemetry"]["vss_reason"] == "provider_failure"


def test_backfill_diagrams_kind_processes_only_diagram_jobs(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "processed": [], "repaired": [], "cleared_errors": []}
    claim_calls = []

    def fake_claim_corpus_jobs(*, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None):
        claim_calls.append(
            {
                "limit": limit,
                "worker_id": worker_id,
                "root_name": root_name,
                "job_families": job_families,
                "family_caps": family_caps,
                "host_agent_roots": host_agent_roots,
            }
        )
        return [
            {
                "id": "job-diagram",
                "job_type": "corpus_extract_diagram",
                "job_family": "diagram",
                "payload": {"path": "flow.drawio", "root_name": "docs"},
                "attempts": 1,
            }
        ]

    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        fake_claim_corpus_jobs,
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    def fake_process(job):
        calls["processed"].append(job["id"])
        return worker.JobProcessResult(status="indexed")

    monkeypatch.setattr(worker, "process_corpus_job", fake_process)

    result = KnowledgeService().run_corpus_backfill(kind="diagrams", limit=2, workers=1)

    assert result["completed"] == 1
    assert claim_calls[0]["job_families"] == ["diagram"]
    assert calls["processed"] == ["job-diagram"]
    assert calls["completed"][0]["job_id"] == "job-diagram"
    assert calls["completed"][0]["telemetry"]["job_family"] == "diagram"
    assert calls["retried"] == []


def test_backfill_archive_kinds_process_archive_and_container_jobs(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "processed": [], "repaired": [], "cleared_errors": []}
    claim_calls = []

    def fake_claim_corpus_jobs(*, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None):
        claim_calls.append({"job_families": job_families, "family_caps": family_caps, "limit": limit})
        return [
            {
                "id": "job-archive",
                "job_type": "corpus_extract_archive",
                "job_family": "archive",
                "payload": {"path": "bundle.zip", "root_name": "docs"},
                "attempts": 1,
            },
            {
                "id": "job-container",
                "job_type": "corpus_extract_container",
                "job_family": "archive",
                "payload": {"path": "package.whl", "root_name": "docs"},
                "attempts": 1,
            },
        ]

    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        fake_claim_corpus_jobs,
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    def fake_process(job):
        calls["processed"].append(job["id"])
        return worker.JobProcessResult(status="metadata_only")

    monkeypatch.setattr(worker, "process_corpus_job", fake_process)

    result = KnowledgeService().run_corpus_backfill(kind="archives", limit=3, workers=1)

    assert result["completed"] == 2
    assert claim_calls[0]["job_families"] == ["archive"]
    assert calls["processed"] == ["job-archive", "job-container"]
    assert [item["job_id"] for item in calls["completed"]] == ["job-archive", "job-container"]
    assert [item["telemetry"]["job_family"] for item in calls["completed"]] == ["archive", "archive"]
    assert calls["retried"] == []


def test_backfill_search_index_jobs_uses_search_index_processor(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-search-index",
                "job_type": "search_index_sync",
                "job_family": "embedding",
                "resource_class": "gpu",
                "payload": {"owner_class": "all", "root_name": "docs", "limit": 25},
                "attempts": 1,
            }
        ],
    )
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: calls["completed"].append(kwargs))
    monkeypatch.setattr(database, "block_corpus_job", lambda **kwargs: calls["blocked"].append(kwargs))
    monkeypatch.setattr(database, "retry_corpus_job", lambda **kwargs: calls["retried"].append(kwargs))
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **kwargs: calls["repaired"].append(kwargs) or {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **kwargs: calls["cleared_errors"].append(kwargs) or {"cleared": 0})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: None)

    from flux_llm_kb import worker

    monkeypatch.setattr(
        worker,
        "process_corpus_job",
        lambda _job: (_ for _ in ()).throw(AssertionError("search-index jobs must not use file extraction")),
    )
    monkeypatch.setattr(
        worker,
        "process_search_index_sync_job",
        lambda job: worker.JobProcessResult(
            status="indexed",
            telemetry={
                "search_index_indexed": 3,
                "search_index_skipped_unchanged": 2,
                "search_index_engine": "vespa",
                "search_index_embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
                "search_index_embedding_dimensions": 1024,
            },
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="search-index", limit=1, workers=1)

    assert result["completed"] == 1
    telemetry = calls["completed"][0]["telemetry"]
    assert telemetry["job_family"] == "embedding"
    assert telemetry["resource_class"] == "gpu"
    assert telemetry["search_index_indexed"] == 3
    assert telemetry["search_index_skipped_unchanged"] == 2
    assert telemetry["search_index_engine"] == "vespa"


def test_process_corpus_job_reports_locked_file(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "open.docx").write_bytes(b"locked")
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "docs",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
    })
    monkeypatch.setattr(worker, "extract_file", lambda *_args: (_ for _ in ()).throw(PermissionError("being used by another process")))

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "open.docx"}})

    assert result.status == "retrying_locked"
    assert "being used" in result.message


def test_process_corpus_job_uses_vss_for_locked_text_file(monkeypatch, tmp_path):
    from flux_llm_kb import host_vss, worker

    root = tmp_path / "docs"
    root.mkdir()
    source = root / "open.txt"
    source.write_text("locked", encoding="utf-8")
    shadow = tmp_path / "shadow" / "open.txt"
    shadow.parent.mkdir()
    shadow.write_text("shadow body", encoding="utf-8")
    applied = []
    extract_paths = []
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "docs",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
        "metadata": {"host_access": "host_agent"},
    })
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(worker, "_configured_host_vss_enabled", lambda: True, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_max_file_bytes", lambda: 4096, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_timeout_seconds", lambda: 5, raising=False)

    @contextmanager
    def fake_snapshot(path, *, max_file_bytes, timeout_seconds):
        assert path == source
        assert max_file_bytes == 4096
        assert timeout_seconds == 5
        yield host_vss.VssSnapshot(path=shadow, telemetry={"status": "completed", "reason": "snapshot_created"})

    def fake_extract(path, _policy, *, relative_path=None):
        extract_paths.append(path)
        if path == source:
            assert relative_path is None
            raise PermissionError("being used by another process")
        assert path == shadow
        assert relative_path == "open.txt"
        return worker.ExtractionResult(
            status="indexed",
            chunks=(worker.AssetChunk(title="open.txt", body="shadow body", chunk_index=0),),
            metadata={"extractor": "text"},
        )

    monkeypatch.setattr(host_vss, "snapshot_path", fake_snapshot)
    monkeypatch.setattr(worker, "extract_file", fake_extract)

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "open.txt"}})

    assert result.status == "indexed"
    assert extract_paths == [source, shadow]
    applied_result = applied[0]["result"]
    assert applied_result.chunks[0].body == "shadow body"
    assert applied_result.metadata["vss_fallback"]["status"] == "completed"
    assert result.telemetry["vss_status"] == "completed"


def test_process_corpus_job_uses_same_vss_path_for_locked_docx(monkeypatch, tmp_path):
    from flux_llm_kb import host_vss, worker

    root = tmp_path / "docs"
    root.mkdir()
    source = root / "open.docx"
    source.write_bytes(b"locked docx")
    shadow = tmp_path / "shadow" / "open.docx"
    shadow.parent.mkdir()
    shadow.write_bytes(b"shadow docx")
    applied = []
    extract_paths = []
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "docs",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
        "metadata": {"host_access": "host_agent"},
    })
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(worker, "_configured_host_vss_enabled", lambda: True, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_max_file_bytes", lambda: 4096, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_timeout_seconds", lambda: 5, raising=False)

    @contextmanager
    def fake_snapshot(path, *, max_file_bytes, timeout_seconds):
        yield host_vss.VssSnapshot(path=shadow, telemetry={"status": "completed", "reason": "snapshot_created"})

    def fake_extract_for_job(job_type, path, policy, payload):
        extract_paths.append((job_type, path))
        if path == source:
            raise PermissionError("being used by another process")
        assert path == shadow
        return worker.ExtractionResult(
            status="indexed",
            chunks=(worker.AssetChunk(title="open.docx", body="docx body", chunk_index=0),),
            metadata={"extractor": "docx"},
        )

    monkeypatch.setattr(host_vss, "snapshot_path", fake_snapshot)
    monkeypatch.setattr(worker, "_extract_for_corpus_job", fake_extract_for_job)

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "open.docx"}})

    assert result.status == "indexed"
    assert extract_paths == [("", source), ("", shadow)]
    assert applied[0]["result"].metadata["vss_fallback"]["status"] == "completed"


def test_process_corpus_job_does_not_persist_shadow_path_in_staged_jobs(monkeypatch, tmp_path):
    from flux_llm_kb import host_vss, worker

    root = tmp_path / "media"
    root.mkdir()
    source = root / "clip.mp4"
    source.write_bytes(b"locked video")
    shadow = tmp_path / "shadow" / "clip.mp4"
    shadow.parent.mkdir()
    shadow.write_bytes(b"shadow video")
    staged_results = []
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "media",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
        "metadata": {"host_access": "host_agent"},
    })
    monkeypatch.setattr(database, "corpus_job_is_running", lambda _job_id: True)
    monkeypatch.setattr(database, "apply_staged_extraction_plan_for_job", lambda **kwargs: staged_results.append(kwargs["result"]) or True)
    monkeypatch.setattr(worker, "_configured_host_vss_enabled", lambda: True, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_max_file_bytes", lambda: 4096, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_timeout_seconds", lambda: 5, raising=False)

    @contextmanager
    def fake_snapshot(path, *, max_file_bytes, timeout_seconds):
        yield host_vss.VssSnapshot(path=shadow, telemetry={"status": "completed", "reason": "snapshot_created"})

    def fake_plan(path, file_kind):
        if path == source:
            raise PermissionError("being used by another process")
        assert path == shadow
        return worker.ExtractionResult(
            status="staged",
            chunks=(),
            metadata={
                "extractor": "video",
                "staged_jobs": [{"job_type": "corpus_extract_media_segment", "payload": {"file_kind": "video"}}],
                "staged_extraction": {"status": "planned", "next_job_type": "corpus_extract_media_segment"},
            },
        )

    monkeypatch.setattr(host_vss, "snapshot_path", fake_snapshot)
    monkeypatch.setattr(worker, "plan_staged_media_extraction", fake_plan)

    result = worker.process_corpus_job(
        {"id": "job-video", "job_type": "corpus_extract_video", "payload": {"root_name": "media", "path": "clip.mp4"}}
    )

    assert result.status == "staged"
    serialized = json.dumps(staged_results[0].metadata)
    assert str(shadow) not in serialized
    assert staged_results[0].metadata["vss_fallback"]["status"] == "completed"


def test_process_corpus_job_retries_locked_when_tool_rejects_vss_path(monkeypatch, tmp_path):
    from flux_llm_kb import host_vss, worker

    root = tmp_path / "docs"
    root.mkdir()
    source = root / "open.pdf"
    source.write_bytes(b"locked pdf")
    shadow = tmp_path / "shadow" / "open.pdf"
    shadow.parent.mkdir()
    shadow.write_bytes(b"shadow pdf")
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "docs",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
        "metadata": {"host_access": "host_agent"},
    })
    monkeypatch.setattr(worker, "_configured_host_vss_enabled", lambda: True, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_max_file_bytes", lambda: 4096, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_timeout_seconds", lambda: 5, raising=False)

    @contextmanager
    def fake_snapshot(path, *, max_file_bytes, timeout_seconds):
        assert path == source
        yield host_vss.VssSnapshot(path=shadow, telemetry={"status": "completed", "reason": "snapshot_created", "return_value": 0})

    def fake_extract_for_job(job_type, path, policy, payload):
        if path == source:
            raise PermissionError("being used by another process")
        assert path == shadow
        raise OSError(r"tool rejected \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7")

    monkeypatch.setattr(host_vss, "snapshot_path", fake_snapshot)
    monkeypatch.setattr(worker, "_extract_for_corpus_job", fake_extract_for_job)

    result = worker.process_corpus_job({"job_type": "corpus_extract_document", "payload": {"root_name": "docs", "path": "open.pdf"}})

    assert result.status == "retrying_locked"
    assert result.telemetry["vss_status"] == "completed"
    assert result.telemetry["vss_reason"] == "snapshot_created"
    assert result.telemetry["vss_tool_path_rejected"] is True


def test_process_corpus_job_retries_locked_when_vss_extractor_returns_tool_failure(monkeypatch, tmp_path):
    from flux_llm_kb import host_vss, worker

    root = tmp_path / "docs"
    root.mkdir()
    source = root / "scan.pdf"
    source.write_bytes(b"locked pdf")
    shadow = tmp_path / "shadow" / "scan.pdf"
    shadow.parent.mkdir()
    shadow.write_bytes(b"shadow pdf")
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "docs",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
        "metadata": {"host_access": "host_agent"},
    })
    monkeypatch.setattr(worker, "_configured_host_vss_enabled", lambda: True, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_max_file_bytes", lambda: 4096, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_timeout_seconds", lambda: 5, raising=False)

    @contextmanager
    def fake_snapshot(path, *, max_file_bytes, timeout_seconds):
        assert path == source
        yield host_vss.VssSnapshot(path=shadow, telemetry={"status": "completed", "reason": "snapshot_created"})

    def fake_extract_for_job(job_type, path, policy, payload):
        if path == source:
            raise PermissionError("being used by another process")
        assert path == shadow
        return worker.ExtractionResult(status="failed", message=r"pdftoppm rejected \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7")

    monkeypatch.setattr(host_vss, "snapshot_path", fake_snapshot)
    monkeypatch.setattr(worker, "_extract_for_corpus_job", fake_extract_for_job)

    result = worker.process_corpus_job({"job_type": "corpus_extract_pdf", "payload": {"root_name": "docs", "path": "scan.pdf"}})

    assert result.status == "retrying_locked"
    assert result.telemetry["vss_status"] == "completed"
    assert result.telemetry["vss_tool_path_rejected"] is True


def test_process_corpus_job_reports_vss_failure_for_locked_file(monkeypatch, tmp_path):
    from flux_llm_kb import host_vss, worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "open.txt").write_text("locked", encoding="utf-8")
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "docs",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
        "metadata": {"host_access": "host_agent"},
    })
    monkeypatch.setattr(worker, "_configured_host_vss_enabled", lambda: True, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_max_file_bytes", lambda: 4096, raising=False)
    monkeypatch.setattr(worker, "_configured_host_vss_timeout_seconds", lambda: 5, raising=False)
    monkeypatch.setattr(worker, "extract_file", lambda *_args: (_ for _ in ()).throw(PermissionError("being used by another process")))

    @contextmanager
    def failing_snapshot(path, *, max_file_bytes, timeout_seconds):
        raise host_vss.VssSnapshotError(
            "VSS access denied",
            reason="access_denied",
            telemetry={"status": "failed", "reason": "access_denied", "return_value": 1},
        )
        yield

    monkeypatch.setattr(host_vss, "snapshot_path", failing_snapshot)

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "open.txt"}})

    assert result.status == "retrying_vss_failed"
    assert result.message == "VSS access denied"
    assert result.telemetry["vss_reason"] == "access_denied"


def test_process_corpus_job_blocks_invalid_xlsx_package(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "bad.xlsx").write_bytes(b"not a zip file")
    applied = []
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "docs",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
        "metadata": {},
    })
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "bad.xlsx"}})

    assert result.status == "blocked_invalid_source"
    assert "File is not a zip file" in (result.message or "")
    assert applied[0]["root_name"] == "docs"
    assert applied[0]["relative_path"] == "bad.xlsx"
    applied_result = applied[0]["result"]
    assert applied_result.status == "blocked_invalid_source"
    assert applied_result.metadata["extractor"] == "xlsx"
    assert applied_result.metadata["reason"] == "invalid_package"


def test_process_corpus_job_persists_invalid_svg_source(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "broken.svg").write_bytes(b"\x00not svg xml")
    applied = []
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "docs",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
        "metadata": {},
    })
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))

    result = worker.process_corpus_job({"job_type": "corpus_extract_image", "payload": {"root_name": "docs", "path": "broken.svg"}})

    assert result.status == "blocked_invalid_source"
    assert "SVG XML parse failed" in (result.message or "")
    assert applied[0]["root_name"] == "docs"
    assert applied[0]["relative_path"] == "broken.svg"
    applied_result = applied[0]["result"]
    assert applied_result.status == "blocked_invalid_source"
    assert applied_result.metadata["extractor"] == "image"
    assert applied_result.metadata["svg_parse"]["reason"] == "invalid_svg_xml"


def test_process_corpus_job_persists_policy_block(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "large.txt").write_text("large text\n", encoding="utf-8")
    applied = []
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "docs",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
        "metadata": {},
    })
    monkeypatch.setattr(database, "apply_extraction_result", lambda **kwargs: applied.append(kwargs))
    monkeypatch.setattr(
        worker,
        "extract_file",
        lambda _path, _policy: SimpleNamespace(
            status="blocked_by_policy",
            message="text file exceeds inline extraction limit",
            chunks=(),
            child_assets=(),
            metadata={"extractor": "text", "reason": "inline_extraction_limit"},
        ),
    )

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "large.txt"}})

    assert result.status == "blocked_by_policy"
    assert applied[0]["root_name"] == "docs"
    assert applied[0]["relative_path"] == "large.txt"
    applied_result = applied[0]["result"]
    assert applied_result.status == "blocked_by_policy"
    assert applied_result.metadata["reason"] == "inline_extraction_limit"


def test_process_corpus_job_returns_failed_for_unexpected_extractor_error(monkeypatch, tmp_path):
    from flux_llm_kb import worker

    root = tmp_path / "docs"
    root.mkdir()
    (root / "broken.pdf").write_bytes(b"broken")
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "docs",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
    })
    monkeypatch.setattr(worker, "extract_file", lambda *_args: (_ for _ in ()).throw(RuntimeError("extractor crashed")))

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "broken.pdf"}})

    assert result.status == "failed"
    assert "extractor crashed" in result.message
    assert result.telemetry == {"error_type": "RuntimeError"}


def test_process_corpus_job_gpu_lease_rejection_is_retryable(monkeypatch, tmp_path):
    from flux_llm_kb import worker
    from flux_llm_kb.gpu_scheduler import GpuLeaseRejected

    root = tmp_path / "docs"
    root.mkdir()
    (root / "scan.png").write_bytes(b"image")
    monkeypatch.setattr(database, "get_monitored_root", lambda _name: {
        "name": "docs",
        "root_path": str(root),
        "recursive": True,
        "include_globs": [],
        "exclude_globs": [],
        "max_inline_bytes": 1024,
        "heavy_threshold_bytes": 2048,
    })
    monkeypatch.setattr(worker, "extract_file", lambda *_args: (_ for _ in ()).throw(GpuLeaseRejected("vram_budget_exceeded")))

    result = worker.process_corpus_job({"payload": {"root_name": "docs", "path": "scan.png"}})

    assert result.status == "retrying_gpu_busy"
    assert result.message == "vram_budget_exceeded"
    assert result.telemetry == {
        "error_type": "GpuLeaseRejected",
        "retry_after_seconds": 1.0,
        "gpu_scheduler_status": "busy",
    }


def test_docker_corpus_worker_processes_due_imap_mail_profiles(monkeypatch):
    from flux_llm_kb import mail_ingestion

    heartbeats = []
    mail_sync_limits = []

    monkeypatch.setattr(database, "record_runtime_component_heartbeat", lambda **kwargs: heartbeats.append(kwargs))
    monkeypatch.setattr(
        KnowledgeService,
        "run_corpus_backfill",
        lambda self, **kwargs: {"claimed": 0, "completed": 0, "jobs": []},
    )
    monkeypatch.setattr(
        mail_ingestion,
        "sync_due_mail_profiles",
        lambda limit=10, worker_id="flux-kb-mail-worker": mail_sync_limits.append((limit, worker_id))
        or {"count": 1, "profiles": [{"profile": "gmail", "status": "completed"}]},
    )

    result = KnowledgeService().run_corpus_worker(once=True, limit=7, host_agent_roots=False)

    assert mail_sync_limits == [(7, "corpus-worker:docker")]
    assert result["last_result"]["mail_sync"]["count"] == 1
    assert heartbeats[-1]["metadata"]["last_result"]["mail_sync"]["profiles"][0]["profile"] == "gmail"


def test_corpus_worker_recovers_interrupted_imap_runs_on_start(monkeypatch):
    from flux_llm_kb import mail_ingestion

    events = []

    monkeypatch.setattr(database, "record_runtime_component_heartbeat", lambda **kwargs: None)
    monkeypatch.setattr(
        database,
        "recover_interrupted_imap_sync_runs",
        lambda **kwargs: events.append(("recover", kwargs)) or {"recovered": 1},
    )
    monkeypatch.setattr(
        KnowledgeService,
        "run_corpus_backfill",
        lambda self, **kwargs: events.append(("backfill", kwargs)) or {"claimed": 0, "completed": 0, "jobs": []},
    )
    monkeypatch.setattr(
        mail_ingestion,
        "sync_due_mail_profiles",
        lambda **kwargs: events.append(("mail_sync", kwargs)) or {"count": 0, "profiles": []},
    )

    result = KnowledgeService().run_corpus_worker(once=True, limit=3, component_name="corpus-worker:test", host_agent_roots=False)

    assert events[0][0] == "recover"
    assert events[0][1]["worker_id"] == "corpus-worker:test"
    assert "worker_started_at" in events[0][1]
    assert [event[0] for event in events] == ["recover", "backfill", "mail_sync"]
    assert result["last_result"]["mail_orphan_recovery"] == {"recovered": 1}


def test_reprocess_derived_state_blocks_confirmed_runs_when_scoped_jobs_are_running(monkeypatch, tmp_path):
    events = []
    cache_root = tmp_path / "cache"
    ocr_cache = cache_root / "ocr"
    ocr_cache.mkdir(parents=True)
    (ocr_cache / "cached.txt").write_text("cached", encoding="utf-8")

    monkeypatch.setattr(
        database,
        "inventory_reprocess_derived_state",
        lambda **kwargs: events.append(("inventory", kwargs))
        or {
            "scope": {"all_roots": True, "root_name": None, "root_names": ["docs"]},
            "counts": {"running_jobs": 2, "candidate_assets": 5},
            "running_jobs": [{"id": "job-1"}, {"id": "job-2"}],
        },
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "invalidate_reprocess_derived_state",
        lambda **kwargs: events.append(("invalidate", kwargs)),
        raising=False,
    )
    monkeypatch.setattr(
        service_module.acceleration,
        "resolve_cache_layout",
        lambda: {"root": str(cache_root), "source": "test", "directories": {"ocr": str(ocr_cache)}},
        raising=False,
    )

    result = KnowledgeService().reprocess_derived_state(
        all_roots=True,
        confirm=True,
        force=True,
        clear_caches="ocr",
        process=True,
        limit=10,
        workers=1,
        max_passes=1,
    )

    assert result["settings_mutated"] is False
    assert result["dry_run"] is False
    assert result["blocked_reasons"] == ["2 scoped corpus/search-index job(s) are already running"]
    assert [event[0] for event in events] == ["inventory"]
    assert result["cache_actions"]["dry_run"] is True
    assert (ocr_cache / "cached.txt").exists()


def test_reprocess_derived_state_clears_only_derived_caches_and_runs_backfills(monkeypatch, tmp_path):
    cache_root = tmp_path / "cache"
    cache_dirs = {
        name: cache_root / name
        for name in ("models", "ocr", "asr", "vision", "thumbnails", "parser", "embeddings", "mail_content", "temp")
    }
    for name, path in cache_dirs.items():
        path.mkdir(parents=True)
        (path / f"{name}.txt").write_text(name, encoding="utf-8")

    events = []
    inventory_calls = {"count": 0}

    def fake_inventory(**kwargs):
        inventory_calls["count"] += 1
        events.append(("inventory", kwargs))
        return {
            "scope": {"all_roots": True, "root_name": None, "root_names": ["docs"]},
            "counts": {
                "candidate_assets": 4 if inventory_calls["count"] == 1 else 0,
                "running_jobs": 0,
                "search_index_records": 3,
            },
            "running_jobs": [],
        }

    monkeypatch.setattr(database, "inventory_reprocess_derived_state", fake_inventory, raising=False)
    monkeypatch.setattr(
        database,
        "invalidate_reprocess_derived_state",
        lambda **kwargs: events.append(("invalidate", kwargs))
        or {
            "jobs_obsoleted": 2,
            "assets_requeued": 4,
            "container_children_deleted": 1,
            "chunks_deleted": 4,
            "search_records_marked": 3,
            "jobs": [{"job_id": "job-1"}],
        },
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "enqueue_search_index_sync",
        lambda **kwargs: events.append(("search_sync", kwargs)) or {"queued": 1, "job_id": "job-search"},
    )
    monkeypatch.setattr(
        service_module.acceleration,
        "resolve_cache_layout",
        lambda: {"root": str(cache_root), "source": "test", "directories": {name: str(path) for name, path in cache_dirs.items()}},
        raising=False,
    )
    monkeypatch.setattr(
        KnowledgeService,
        "enqueue_corpus_backfill",
        lambda self, **kwargs: events.append(("backfill_enqueue", kwargs)) or {"accepted": True, "queued": 1, "request": kwargs},
    )

    result = KnowledgeService().reprocess_derived_state(
        all_roots=True,
        confirm=True,
        force=True,
        clear_caches="all",
        process=True,
        limit=7,
        workers=2,
        max_passes=2,
    )

    assert result["settings_mutated"] is False
    assert result["jobs_obsoleted"] == 2
    assert result["assets_requeued"] == 4
    assert result["search_records_marked"] == 3
    assert result["cache_actions"]["cleared"] == ["asr", "embeddings", "ocr", "parser", "thumbnails", "vision"]
    assert all(not any(cache_dirs[name].iterdir()) for name in ("ocr", "asr", "vision", "thumbnails", "parser", "embeddings"))
    assert (cache_dirs["models"] / "models.txt").exists()
    assert (cache_dirs["mail_content"] / "mail_content.txt").exists()
    assert (cache_dirs["temp"] / "temp.txt").exists()
    assert [event[0] for event in events] == [
        "inventory",
        "invalidate",
        "backfill_enqueue",
        "backfill_enqueue",
        "search_sync",
        "backfill_enqueue",
        "backfill_enqueue",
        "inventory",
    ]
    assert events[2][1] == {"kind": "all", "limit": 7, "workers": 2}
    assert events[3][1] == {"kind": "all", "limit": 7, "workers": 2}
    assert events[5][1] == {"kind": "search-index", "limit": 7, "workers": 2}
    assert result["verification"]["status"] == "queued"


def test_corpus_worker_uses_unique_instance_lease_for_backfill_and_heartbeat(monkeypatch):
    heartbeats = []
    backfill_calls = []

    monkeypatch.setattr(database, "record_runtime_component_heartbeat", lambda **kwargs: heartbeats.append(kwargs))
    monkeypatch.setattr(database, "recover_interrupted_imap_sync_runs", lambda **_kwargs: {"recovered": 0})
    monkeypatch.setattr(
        KnowledgeService,
        "run_corpus_backfill",
        lambda self, **kwargs: backfill_calls.append(kwargs) or {"claimed": 0, "completed": 0, "jobs": []},
    )
    monkeypatch.setattr(service_module, "uuid", SimpleNamespace(uuid4=lambda: SimpleNamespace(hex="abc123")), raising=False)

    result = KnowledgeService().run_corpus_worker(
        once=True,
        limit=3,
        component_name="corpus-worker:test",
        host_agent_roots=True,
    )

    worker_id = "corpus-worker:test:abc123"
    assert backfill_calls[0]["worker_id"] == worker_id
    assert result["worker_id"] == worker_id
    assert any(
        heartbeat["name"] == worker_id
        and heartbeat["metadata"]["worker_instance"] is True
        and heartbeat["metadata"]["parent_component"] == "corpus-worker:test"
        for heartbeat in heartbeats
    )


def test_host_agent_corpus_worker_does_not_process_imap_mail_profiles(monkeypatch):
    from flux_llm_kb import mail_ingestion

    mail_sync_limits = []

    monkeypatch.setattr(database, "record_runtime_component_heartbeat", lambda **kwargs: None)
    monkeypatch.setattr(
        KnowledgeService,
        "run_corpus_backfill",
        lambda self, **kwargs: {"claimed": 0, "completed": 0, "jobs": []},
    )
    monkeypatch.setattr(
        mail_ingestion,
        "sync_due_mail_profiles",
        lambda limit=10: mail_sync_limits.append(limit) or {"count": 1},
    )

    result = KnowledgeService().run_corpus_worker(once=True, limit=7, host_agent_roots=True)

    assert mail_sync_limits == []
    assert "mail_sync" not in result["last_result"]


def test_corpus_backfill_reports_expired_job_purge(monkeypatch):
    monkeypatch.setattr(database, "recover_stale_running_corpus_jobs", lambda **_kwargs: {"recovered": 0})
    monkeypatch.setattr(database, "purge_unseen_corpus_assets", lambda **_kwargs: {"assets_purged": 0})
    monkeypatch.setattr(database, "cancel_duplicate_corpus_jobs", lambda **_kwargs: {"cancelled": 0})
    monkeypatch.setattr(database, "claim_corpus_jobs", lambda **_kwargs: [])
    monkeypatch.setattr(database, "repair_extracted_corpus_asset_statuses", lambda **_kwargs: {"repaired": 0})
    monkeypatch.setattr(database, "clear_completed_corpus_job_errors", lambda **_kwargs: {"cleared": 0})
    monkeypatch.setattr(database, "purge_expired_capture_jobs", lambda **_kwargs: {"purged": 3, "retention_days": 7})
    monkeypatch.setattr(database, "record_audit_event", lambda **_kwargs: {"id": "audit-1"})

    result = KnowledgeService().run_corpus_backfill(limit=2, worker_id="worker-test")

    assert result["purged_capture_jobs"] == 3
    assert result["capture_job_retention_days"] == 7
    assert "purged_tool_invocations" not in result


def test_corpus_worker_governance_librarian_is_disabled_by_default(monkeypatch):
    governance_calls = []

    monkeypatch.setattr(database, "record_runtime_component_heartbeat", lambda **kwargs: None)
    monkeypatch.setattr(KnowledgeService, "run_corpus_backfill", lambda self, **kwargs: {"claimed": 0, "completed": 0, "jobs": []})
    monkeypatch.setattr(service_module, "_governance_policy_from_settings", lambda: {"librarian_enabled": False, "interval_seconds": 1, "mode": "auto", "auto_apply_enabled": True, "max_actions_per_run": 3})
    monkeypatch.setattr(KnowledgeService, "run_governance", lambda self, **kwargs: governance_calls.append(kwargs) or {"run": {"id": "run-1"}})

    result = KnowledgeService().run_corpus_worker(once=True, limit=2)

    assert governance_calls == []
    assert "governance" not in result["last_result"]


def test_corpus_worker_governance_librarian_runs_shadow_without_auto_apply(monkeypatch):
    governance_calls = []

    monkeypatch.setattr(database, "record_runtime_component_heartbeat", lambda **kwargs: None)
    monkeypatch.setattr(KnowledgeService, "run_corpus_backfill", lambda self, **kwargs: {"claimed": 0, "completed": 0, "jobs": []})
    monkeypatch.setattr(service_module, "_governance_policy_from_settings", lambda: {"librarian_enabled": True, "interval_seconds": 1, "mode": "auto", "auto_apply_enabled": False, "max_actions_per_run": 3})
    monkeypatch.setattr(KnowledgeService, "run_governance", lambda self, **kwargs: governance_calls.append(kwargs) or {"run": {"id": "run-1"}, "memory_mutated": False})

    result = KnowledgeService().run_corpus_worker(once=True, limit=2, component_name="corpus-worker:test")

    assert governance_calls == [{"mode": "shadow", "actor": "corpus-worker:test", "limit": 3}]
    assert result["last_result"]["governance"]["run"]["id"] == "run-1"
