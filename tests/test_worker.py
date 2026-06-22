from flux_llm_kb import database
from flux_llm_kb.service import KnowledgeService


def test_backfill_blocks_missing_dependency_jobs_without_completing(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None: [
            {
                "id": "job-1",
                "job_type": "corpus_extract_video",
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
            status="blocked_missing_dependency",
            message="ffprobe command not found",
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="media", limit=1, workers=1)

    assert result["blocked"] == 1
    assert calls["completed"] == []
    assert calls["retried"] == []
    assert calls["blocked"][0]["job_id"] == "job-1"
    assert calls["repaired"] == [{"root_name": None}]
    assert calls["cleared_errors"] == [{"root_name": None}]


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
        lambda limit=10: mail_sync_limits.append(limit) or {"count": 1, "profiles": [{"profile": "gmail", "status": "completed"}]},
    )

    result = KnowledgeService().run_corpus_worker(once=True, limit=7, host_agent_roots=False)

    assert mail_sync_limits == [7]
    assert result["last_result"]["mail_sync"]["count"] == 1
    assert heartbeats[-1]["metadata"]["last_result"]["mail_sync"]["profiles"][0]["profile"] == "gmail"


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
