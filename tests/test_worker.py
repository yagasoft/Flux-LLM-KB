import json
from types import SimpleNamespace

import pytest

from flux_llm_kb import database, service as service_module
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
    assert retry["result"] == {"job_id": "job-1", "status": "pending"}
    assert calls[0] == (
        "retry",
        {"job_id": "job-1", "reason": "operator retry"},
    )
    assert calls[1][0] == "audit"
    assert calls[1][1]["event_type"] == "diagnostics.remediation"
    assert calls[1][1]["details"]["action"] == "retry_corpus_job"

    monkeypatch.setattr(KnowledgeService, "run_corpus_backfill", lambda self, **kwargs: {"backfill": kwargs})
    backfill = KnowledgeService().remediate_diagnostic(
        action="run_backfill",
        target_type="family",
        target_id="office",
        root_name="docs",
        family="office",
        reason="operator backfill",
    )

    assert backfill["result"] == {"backfill": {"kind": "office", "limit": 10, "workers": 1, "root_name": "docs"}}


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
            "tesseract": {"ok": True, "message": "available"},
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
            "tesseract": {"ok": True, "message": "available"},
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
