from flux_llm_kb import database
from flux_llm_kb.service import KnowledgeService


def test_backfill_blocks_missing_dependency_jobs_without_completing(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": []}
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
