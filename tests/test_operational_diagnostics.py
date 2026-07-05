from __future__ import annotations

import json

from flux_llm_kb import database
from flux_llm_kb.operational_diagnostics import summarize_operational_diagnostics
from flux_llm_kb.service import KnowledgeService


def test_operational_diagnostics_summary_groups_signals_without_raw_private_paths():
    report = summarize_operational_diagnostics(
        retrieval={"recent_explains": [{"query_hash": "sha256:abc", "result_count": 3, "confidence": "medium"}]},
        watcher={"events": [{"root_name": "docs", "action": "modified", "path": "E:/private/docs/file.docx"}]},
        workers={
            "families": [{"family": "office", "pending": 2, "blocked_locked": 1, "slowest_recent_jobs": [{"path": "E:/private/docs/file.docx", "duration_ms": 2400}]}]
        },
        jobs={"jobs": [{"id": "job-1", "job_family": "office", "status": "blocked_missing_dependency", "last_error": "missing local tool"}]},
        mail={
            "sync_runs": [{"id": "sync-1", "profile_name": "work", "status": "failed", "last_error": "auth"}],
            "post_process_events": [{"id": "event-1", "profile_name": "work", "status": "failed", "action": "move"}],
        },
    )

    assert report["settings_mutated"] is False
    assert report["counts"]["watcher_events"] == 1
    assert report["counts"]["blocked_jobs"] == 1
    assert report["sections"]["mail"]["sync_runs"][0]["status"] == "failed"
    serialized = json.dumps(report).lower()
    assert "e:/private" not in serialized
    assert "file.docx" in serialized


def test_service_operational_diagnostics_reads_existing_status_helpers(monkeypatch):
    monkeypatch.setattr(database, "list_watch_events", lambda **kwargs: [{"root_name": "docs", "action": "created"}])
    monkeypatch.setattr(database, "worker_family_stats", lambda **kwargs: [{"family": "office", "pending": 1, "blocked_locked": 0}])
    monkeypatch.setattr(database, "list_capture_jobs", lambda **kwargs: [{"id": "job-1", "job_family": "office", "status": "pending"}])
    monkeypatch.setattr(database, "list_mail_sync_runs", lambda **kwargs: [{"id": "sync-1", "profile_name": "work", "status": "completed"}])
    monkeypatch.setattr(database, "list_mail_post_process_events", lambda **kwargs: [{"id": "event-1", "profile_name": "work", "status": "applied"}])
    monkeypatch.setattr(database, "recent_retrieval_explain_diagnostics", lambda **kwargs: [{"query_hash": "sha256:abc", "result_count": 2}])

    payload = KnowledgeService().operational_diagnostics(section="all", limit=10)

    assert payload["counts"]["watcher_events"] == 1
    assert payload["sections"]["workers"]["families"][0]["family"] == "office"
    assert payload["sections"]["retrieval"]["recent_explains"][0]["query_hash"] == "sha256:abc"


def test_operational_diagnostics_filters_and_standardizes_drilldown_items():
    report = summarize_operational_diagnostics(
        watcher={
            "events": [
                {"root_name": "docs", "action": "modified", "status": "ok", "path": "E:/private/docs/file.docx"},
                {"root_name": "code", "action": "deleted", "status": "ok", "path": "E:/private/code/app.py"},
            ]
        },
        workers={
            "families": [
                {"family": "office", "pending": 2, "blocked_locked": 1, "root_name": "docs"},
                {"family": "media", "pending": 1, "blocked_locked": 0, "root_name": "videos"},
            ]
        },
        jobs={
            "jobs": [
                {"id": "job-1", "job_family": "office", "status": "blocked_missing_dependency", "root_name": "docs", "path": "E:/private/docs/file.docx"},
                {"id": "job-2", "job_family": "media", "status": "pending", "root_name": "videos", "path": "E:/private/videos/clip.mp4"},
            ]
        },
        root_name="docs",
        status="blocked_missing_dependency",
        family="office",
        include_details=True,
    )

    assert report["settings_mutated"] is False
    assert report["filters"] == {"root_name": "docs", "status": "blocked_missing_dependency", "family": "office", "since_hours": None, "include_details": True}
    assert report["items"][0]["section"] == "jobs"
    assert report["items"][0]["severity"] == "warning"
    assert report["items"][0]["root_name"] == "docs"
    assert report["items"][0]["follow_up_command"].startswith("flux-kb")
    assert report["items"][0]["target"]["type"] == "job"
    serialized = json.dumps(report).lower()
    assert "e:/private" not in serialized
    assert "clip.mp4" not in serialized


def test_operational_diagnostics_items_include_safe_remediation_actions():
    report = summarize_operational_diagnostics(
        jobs={
            "jobs": [
                {
                    "id": "job-1",
                    "job_family": "office",
                    "status": "blocked_missing_dependency",
                    "root_name": "docs",
                    "payload": {"path": "E:/private/docs/budget.xls", "root_name": "docs"},
                    "last_error": "LibreOffice missing",
                }
            ]
        },
        root_name="docs",
        status="blocked_missing_dependency",
        family="office",
        include_details=True,
    )

    item = report["items"][0]
    action_ids = {action["id"] for action in item["remediation_actions"]}

    assert {"retry_corpus_job", "run_backfill", "repair_asset_statuses", "clear_completed_errors"} <= action_ids
    retry_action = next(action for action in item["remediation_actions"] if action["id"] == "retry_corpus_job")
    assert retry_action["method"] == "POST"
    assert retry_action["endpoint"] == "/api/diagnostics/actions"
    assert retry_action["target"] == {"type": "job", "id": "job-1"}
    assert retry_action["payload"] == {
        "action": "retry_corpus_job",
        "target_type": "job",
        "target_id": "job-1",
        "root_name": "docs",
        "family": "office",
        "reason": "operator diagnostic remediation",
    }
    assert retry_action["requires_confirmation"] is True
    assert retry_action["destructive"] is False
    assert retry_action["settings_mutated"] is False
    serialized = json.dumps(report).lower()
    assert "e:/private" not in serialized
    assert "budget.xls" in serialized


def test_operational_diagnostics_surfaces_retrying_gpu_evictions():
    report = summarize_operational_diagnostics(
        workers={
            "families": [],
            "gpu_evictions": {
                "retrying": 1,
                "recent": [
                    {
                        "id": "eviction-1",
                        "status": "retrying",
                        "model_id": "snowflake",
                        "component": "model-runner",
                        "error": "VRAM did not recover after eviction",
                        "created_at": 1000,
                        "broker_delivery_count": 3,
                    }
                ],
            },
        },
    )

    assert report["counts"]["retrying_gpu_evictions"] == 1
    item = report["items"][0]
    assert item["status"] == "gpu_eviction_retrying"
    assert item["severity"] == "warning"
    assert item["family"] == "gpu_eviction"
    assert item["target"] == {"type": "worker", "id": "eviction-1"}
    assert item["evidence"]["broker_delivery_count"] == 3
    assert item["evidence"]["last_error"] == "VRAM did not recover after eviction"


def test_operational_diagnostics_distinguishes_blocked_status_guidance():
    report = summarize_operational_diagnostics(
        jobs={
            "jobs": [
                {"id": "job-policy", "job_family": "code", "status": "blocked_by_policy", "root_name": "docs", "last_error": "inline limit"},
                {"id": "job-invalid", "job_family": "office", "status": "blocked_invalid_source", "root_name": "docs", "last_error": "Package not found"},
                {"id": "job-dep", "job_family": "media", "status": "blocked_missing_dependency", "root_name": "docs", "last_error": "ffprobe missing"},
            ]
        },
        root_name="docs",
        include_details=True,
    )

    by_id = {item["target"]["id"]: item for item in report["items"]}

    assert "include/exclude globs" in by_id["job-policy"]["user_action"]
    assert "size limits" in by_id["job-policy"]["user_action"]
    assert "repair, resave, rehydrate, or exclude" in by_id["job-invalid"]["user_action"]
    assert "Install or configure" in by_id["job-dep"]["user_action"]
    assert "retry_corpus_job" in {action["id"] for action in by_id["job-policy"]["remediation_actions"]}
    assert "retry_corpus_job" in {action["id"] for action in by_id["job-invalid"]["remediation_actions"]}
