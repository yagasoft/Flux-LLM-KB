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
