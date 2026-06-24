from types import SimpleNamespace

from flux_llm_kb import database
from flux_llm_kb.service import KnowledgeService


def test_backfill_blocks_missing_dependency_jobs_without_completing(monkeypatch):
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
            status="blocked_missing_dependency",
            message="ffprobe command not found",
            telemetry={"ocr_cache_hits": 0, "ocr_cache_misses": 0},
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="media", limit=1, workers=1)

    assert result["blocked"] == 1
    assert calls["completed"] == []
    assert calls["retried"] == []
    assert calls["blocked"][0]["job_id"] == "job-1"
    assert calls["blocked"][0]["telemetry"]["ocr_cache_hits"] == 0
    assert calls["blocked"][0]["telemetry"]["ocr_cache_misses"] == 0
    assert calls["repaired"] == [{"root_name": None}]
    assert calls["cleared_errors"] == [{"root_name": None}]


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

    def fake_scan(_root_path, policy, target_path=None):
        captured["policy"] = policy
        captured["target_path"] = target_path
        return SimpleNamespace(assets=[], deferred_jobs=[], errors=[], root_path=root)

    monkeypatch.setattr(service_module, "scan_path", fake_scan)
    monkeypatch.setattr(database, "lookup_scan_manifest", lambda **_kwargs: None)
    monkeypatch.setattr(database, "persist_crawl_plan", lambda **kwargs: {"root_name": kwargs["root_name"]})

    result = KnowledgeService().sync_corpus(root_name="docs")

    assert result == {"root_name": "docs"}
    assert captured["policy"].container_max_depth == 3
    assert captured["policy"].container_max_members == 19
    assert captured["policy"].container_max_total_bytes == 8192
    assert captured["policy"].container_max_member_bytes == 1024
    assert callable(captured["policy"].manifest_lookup)


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


def test_benchmark_run_uses_synthetic_fixtures_and_records_history(monkeypatch):
    recorded = []
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: recorded.append(kwargs) or {"id": "run-1", "fixture": kwargs["fixture"]})

    result = KnowledgeService().run_benchmark(fixture="text-heavy", files=3)

    assert result["fixture"] == "text-heavy"
    assert result["runs"][0]["fixture"] == "text-heavy"
    assert recorded[0]["fixture"] == "text-heavy"
    assert recorded[0]["file_count"] == 3
    assert "root_path" not in recorded[0]["metadata"]


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


def test_backfill_embedding_jobs_uses_embedding_processor(monkeypatch):
    calls = {"completed": [], "blocked": [], "retried": [], "repaired": [], "cleared_errors": []}
    monkeypatch.setattr(
        database,
        "claim_corpus_jobs",
        lambda *, limit, worker_id, root_name=None, job_families=None, family_caps=None, host_agent_roots=None: [
            {
                "id": "job-embed",
                "job_type": "corpus_embed",
                "job_family": "embedding",
                "resource_class": "gpu",
                "payload": {"owner_class": "corpus", "root_name": "docs", "stale_only": True, "limit": 25},
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
        lambda _job: (_ for _ in ()).throw(AssertionError("embedding jobs must not use file extraction")),
    )
    monkeypatch.setattr(
        worker,
        "process_embedding_job",
        lambda job: worker.JobProcessResult(
            status="indexed",
            telemetry={
                "embedding_vectors": 3,
                "embedding_skipped_unchanged": 2,
                "embedding_batches": 1,
                "embedding_cache_hits": 2,
                "embedding_cache_misses": 3,
                "embedding_provider": "hash",
                "embedding_model": "flux-hash-v1",
                "embedding_dimensions": 1536,
            },
        ),
    )

    result = KnowledgeService().run_corpus_backfill(kind="embeddings", limit=1, workers=1)

    assert result["completed"] == 1
    telemetry = calls["completed"][0]["telemetry"]
    assert telemetry["job_family"] == "embedding"
    assert telemetry["resource_class"] == "gpu"
    assert telemetry["embedding_vectors"] == 3
    assert telemetry["embedding_skipped_unchanged"] == 2
    assert telemetry["embedding_batches"] == 1
    assert telemetry["embedding_cache_hits"] == 2
    assert telemetry["embedding_cache_misses"] == 3
    assert telemetry["embedding_provider"] == "hash"


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
