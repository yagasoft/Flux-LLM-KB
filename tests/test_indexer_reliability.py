from __future__ import annotations

from datetime import UTC, datetime, timedelta
import json

from flux_llm_kb.indexer_reliability import (
    build_indexer_reliability_report,
    build_root_reliability_card,
    build_roots_reliability_report,
)


def _run(
    *,
    run_id: str,
    scenario: str,
    mode: str,
    created_at: datetime,
    scope_type: str = "synthetic",
    scope_hash: str | None = None,
    fixture: str = "text-heavy",
    warm_state: str = "warm",
    jobs_blocked: int = 0,
    manifest_skipped: int = 0,
    metadata: dict | None = None,
    recommendations: dict | None = None,
) -> dict:
    return {
        "id": run_id,
        "scenario": scenario,
        "mode": mode,
        "fixture": fixture,
        "status": "completed",
        "created_at": created_at.isoformat(),
        "scope_type": scope_type,
        "scope_hash": scope_hash,
        "warm_state": warm_state,
        "file_count": 12,
        "jobs_blocked": jobs_blocked,
        "manifest_skipped_unchanged": manifest_skipped,
        "metadata": metadata or {},
        "recommendation_metadata": recommendations or {"settings_mutated": False, "scenario": scenario},
    }


def test_reliability_report_marks_ready_with_recent_required_evidence():
    now = datetime(2026, 6, 25, 12, 0, tzinfo=UTC)
    scope_hash = "sha256:scope"
    runs = [
        _run(run_id="rel-scan-cold", scenario="reliability", mode="scan", warm_state="cold", created_at=now - timedelta(hours=1)),
        _run(run_id="rel-scan-warm", scenario="reliability", mode="scan", manifest_skipped=10, created_at=now - timedelta(hours=1)),
        _run(run_id="rel-soak", scenario="reliability", mode="soak", created_at=now - timedelta(hours=1)),
        _run(
            run_id="rel-watch",
            scenario="reliability",
            mode="watcher",
            created_at=now - timedelta(hours=1),
            metadata={"watcher_backend": {"selected_backend": "watchdog"}, "watcher_events": {"created": 1, "deleted": 1}},
        ),
        _run(
            run_id="host-root",
            scenario="host_cloud",
            mode="scan",
            created_at=now - timedelta(hours=2),
            scope_type="monitored_root",
            scope_hash=scope_hash,
            fixture="monitored-root",
            manifest_skipped=8,
            metadata={"host_access": "host_agent", "observed_files": 44},
        ),
        _run(
            run_id="tuning",
            scenario="tuning",
            mode="scan",
            created_at=now - timedelta(hours=3),
            recommendations={
                "settings_mutated": False,
                "scenario": "tuning",
                "candidates": [
                    {
                        "setting": "crawler.hash_parallelism",
                        "current": 1,
                        "candidate": 2,
                        "requires_manual_apply": True,
                    }
                ],
            },
        ),
    ]

    report = build_indexer_reliability_report(
        runs=runs,
        now=now,
        scope_type="monitored_root",
        scope_hash=scope_hash,
        label="nightly",
        deployment_label="desktop",
        worker_families=[{"family": "media", "pending": 2, "blocked_locked": 0, "backpressure": "pending"}],
        watcher_events=[{"action": "created"}, {"action": "deleted"}],
        freshness_hours=336,
    )

    assert report["readiness"] == "ready"
    assert report["settings_mutated"] is False
    assert {check["check"]: check["status"] for check in report["checks"]} == {
        "synthetic_reliability": "ok",
        "scoped_host_cloud": "ok",
        "worker_tuning": "ok",
    }
    assert report["scope"]["scope_hash"] == scope_hash
    assert report["scope"]["label"] == "nightly"
    assert report["watcher"]["backend"] == "watchdog"
    assert report["watcher"]["event_count"] == 2
    assert report["workers"]["families"][0]["family"] == "media"
    assert report["candidates"][0]["evidence_state"] == "ready_to_try"
    assert "flux-kb acceleration benchmark run" in report["candidates"][0]["follow_up_command"]
    assert "settings_mutated" in json.dumps(report)


def test_reliability_report_blocks_on_stale_and_failed_evidence():
    now = datetime(2026, 6, 25, 12, 0, tzinfo=UTC)
    runs = [
        _run(
            run_id="old-rel",
            scenario="reliability",
            mode="scan",
            created_at=now - timedelta(days=30),
            jobs_blocked=3,
        ),
        _run(
            run_id="fresh-tuning",
            scenario="tuning",
            mode="scan",
            created_at=now - timedelta(hours=1),
            recommendations={
                "settings_mutated": False,
                "scenario": "tuning",
                "candidates": [{"setting": "acceleration.worker_cap.media", "current": 1, "candidate": 2}],
            },
        )
    ]

    report = build_indexer_reliability_report(
        runs=runs,
        now=now,
        scope_type="monitored_root",
        scope_hash="sha256:scope",
        worker_families=[{"family": "media", "blocked_locked": 2, "failed": 1}],
        watcher_events=[],
        freshness_hours=24,
    )

    assert report["readiness"] == "blocked"
    statuses = {check["check"]: check["status"] for check in report["checks"]}
    assert statuses["synthetic_reliability"] == "stale"
    assert statuses["scoped_host_cloud"] == "missing"
    assert statuses["worker_tuning"] == "blocked"
    assert report["candidates"][0]["evidence_state"] == "blocked_by_failures"
    serialized = json.dumps(report).lower()
    assert "e:/private" not in serialized
    assert "root_path" not in serialized


def test_root_reliability_card_combines_counts_and_latest_scoped_run():
    now = datetime(2026, 6, 25, 12, 0, tzinfo=UTC)
    latest_run = _run(
        run_id="root-run",
        scenario="host_cloud",
        mode="scan",
        created_at=now,
        scope_type="monitored_root",
        scope_hash="sha256:root",
        fixture="monitored-root",
        metadata={"observed_files": 25, "host_access": "direct"},
    )

    card = build_root_reliability_card(
        root={
            "name": "docs",
            "enabled": True,
            "watch_enabled": True,
            "root_path": "E:/private/docs",
        },
        asset_counts={"total": 50, "indexed": 40, "pending_stable": 1, "blocked": 2, "failed": 0},
        job_counts={"pending": 3, "retrying_locked": 1, "blocked": 2, "failed": 0},
        latest_crawl={"id": "crawl-1", "status": "completed", "reason": "periodic_reconcile"},
        latest_benchmark=latest_run,
        scope_hash="sha256:root",
    )

    assert card["root_name"] == "docs"
    assert card["readiness"] == "partial"
    assert card["scope_hash"] == "sha256:root"
    assert card["blockers"]["blocked_assets"] == 2
    assert card["blockers"]["pending_jobs"] == 3
    assert card["latest_benchmark"]["id"] == "root-run"
    assert "root_path" not in card


def test_roots_reliability_report_summarizes_readiness_and_remaining_actions():
    roots = [
        {
            "root_name": "docs",
            "enabled": True,
            "readiness": "ready",
            "scope_hash": "sha256:docs",
            "blockers": {"blocked_assets": 0, "failed_jobs": 0, "pending_jobs": 0},
            "latest_benchmark": {"id": "bench-docs", "created_at": "2026-06-25T10:00:00+00:00"},
        },
        {
            "root_name": "cloud",
            "enabled": True,
            "readiness": "partial",
            "scope_hash": "sha256:cloud",
            "blockers": {"blocked_assets": 1, "failed_jobs": 0, "pending_jobs": 2},
            "latest_benchmark": None,
        },
        {
            "root_name": "disabled",
            "enabled": False,
            "readiness": "blocked",
            "scope_hash": "sha256:disabled",
            "blockers": {},
            "latest_benchmark": None,
        },
    ]

    report = build_roots_reliability_report(roots=roots, include_disabled=False, freshness_hours=24)

    assert report["settings_mutated"] is False
    assert report["totals"] == {"ready": 1, "partial": 1, "blocked": 0, "not_run": 0, "total": 2}
    assert [item["root_name"] for item in report["roots"]] == ["cloud", "docs"]
    assert report["roots"][0]["required_action"] == "Run scoped host/cloud reliability evidence and clear blocked or pending work."
    assert report["roots"][1]["required_action"] == "No action required."
    assert "disabled" not in {item["root_name"] for item in report["roots"]}
    serialized = json.dumps(report).lower()
    assert "e:/private" not in serialized
    assert "root_path" not in serialized
