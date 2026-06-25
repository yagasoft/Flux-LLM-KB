from __future__ import annotations

from datetime import UTC, datetime, timedelta
from typing import Any


def build_indexer_reliability_report(
    *,
    runs: list[dict[str, Any]],
    now: datetime | None = None,
    scope_type: str = "synthetic",
    scope_hash: str | None = None,
    label: str | None = None,
    deployment_label: str | None = None,
    worker_families: list[dict[str, Any]] | None = None,
    watcher_events: list[dict[str, Any]] | None = None,
    freshness_hours: int = 336,
) -> dict[str, Any]:
    observed_at = _as_aware(now or datetime.now(UTC))
    fresh_after = observed_at - timedelta(hours=max(1, int(freshness_hours or 336)))
    normalized_runs = [_normalize_run(run) for run in runs]
    fresh_runs = [run for run in normalized_runs if _run_created_at(run) and _run_created_at(run) >= fresh_after]
    reliability_runs = [run for run in fresh_runs if _scenario(run) == "reliability"]
    scoped_runs = [
        run
        for run in fresh_runs
        if _scenario(run) == "host_cloud"
        and run.get("scope_type") == scope_type
        and (not scope_hash or run.get("scope_hash") == scope_hash)
    ]
    tuning_runs = [run for run in fresh_runs if _scenario(run) == "tuning"]
    families = [_family_summary(row) for row in (worker_families or [])]
    blocked_workers = any((row.get("blocked_locked") or 0) > 0 or (row.get("failed") or 0) > 0 for row in families)

    checks = [
        _synthetic_reliability_check(reliability_runs, normalized_runs, fresh_after),
        _scoped_host_cloud_check(scoped_runs, scope_type=scope_type, scope_hash=scope_hash),
        _worker_tuning_check(tuning_runs, blocked_workers=blocked_workers),
    ]
    readiness = _overall_readiness(checks)
    latest_runs = [_run_ref(run) for run in sorted(fresh_runs, key=lambda item: item.get("created_at") or "", reverse=True)[:8]]
    watcher = _watcher_summary(reliability_runs, watcher_events or [])
    workers = {
        "families": families[:8],
        "blocked_family_count": sum(1 for row in families if (row.get("blocked_locked") or 0) > 0 or (row.get("failed") or 0) > 0),
        "pending_family_count": sum(1 for row in families if (row.get("pending") or 0) > 0),
    }
    candidate_runs = [run for run in fresh_runs if _run_candidates(run)]
    candidates = _evidence_scored_candidates(
        tuning_runs=candidate_runs,
        readiness=readiness,
        blocked_workers=blocked_workers,
        scope_type=scope_type,
        scope_hash=scope_hash,
    )
    return {
        "readiness": readiness,
        "settings_mutated": False,
        "scope": {
            "scope_type": scope_type,
            "scope_hash": scope_hash,
            "label": label,
            "deployment_label": deployment_label,
            "freshness_hours": max(1, int(freshness_hours or 336)),
        },
        "evidence_age_hours": _evidence_age_hours(fresh_runs, observed_at),
        "checks": checks,
        "latest_runs": latest_runs,
        "watcher": watcher,
        "workers": workers,
        "candidates": candidates,
    }


def build_root_reliability_card(
    *,
    root: dict[str, Any],
    asset_counts: dict[str, int] | None,
    job_counts: dict[str, int] | None,
    latest_crawl: dict[str, Any] | None,
    latest_benchmark: dict[str, Any] | None,
    scope_hash: str | None,
) -> dict[str, Any]:
    assets = {key: int(value or 0) for key, value in (asset_counts or {}).items()}
    jobs = {key: int(value or 0) for key, value in (job_counts or {}).items()}
    blockers = {
        "blocked_assets": int(assets.get("blocked", 0) + assets.get("blocked_locked", 0)),
        "failed_assets": int(assets.get("failed", 0)),
        "pending_jobs": int(jobs.get("pending", 0)),
        "retrying_locked_jobs": int(jobs.get("retrying_locked", 0)),
        "blocked_jobs": int(jobs.get("blocked", 0) + jobs.get("blocked_locked", 0)),
        "failed_jobs": int(jobs.get("failed", 0)),
    }
    if not root.get("enabled"):
        readiness = "blocked"
    elif any(value > 0 for key, value in blockers.items() if key.startswith("failed") or key.startswith("blocked")):
        readiness = "partial"
    elif blockers["pending_jobs"] > 0 or blockers["retrying_locked_jobs"] > 0:
        readiness = "partial"
    elif latest_benchmark:
        readiness = "ready"
    else:
        readiness = "not_run"
    return {
        "root_name": root.get("name"),
        "enabled": bool(root.get("enabled")),
        "watch_enabled": bool(root.get("watch_enabled")),
        "readiness": readiness,
        "scope_hash": scope_hash,
        "asset_counts": assets,
        "job_counts": jobs,
        "blockers": blockers,
        "latest_crawl": _safe_crawl(latest_crawl),
        "latest_benchmark": _run_ref(_normalize_run(latest_benchmark)) if latest_benchmark else None,
    }


def _synthetic_reliability_check(runs: list[dict[str, Any]], all_runs: list[dict[str, Any]], fresh_after: datetime) -> dict[str, Any]:
    if not runs:
        stale = [run for run in all_runs if _scenario(run) == "reliability" and _run_created_at(run) and _run_created_at(run) < fresh_after]
        return _check("synthetic_reliability", "stale" if stale else "missing", "Recent synthetic reliability benchmark evidence is required.", {"stale_runs": len(stale)})
    scan_runs = [run for run in runs if run.get("mode") == "scan"]
    has_cold = any(run.get("warm_state") == "cold" for run in scan_runs)
    has_warm = any(run.get("warm_state") == "warm" for run in scan_runs)
    warm_skips = sum(int(run.get("manifest_skipped_unchanged") or 0) for run in scan_runs if run.get("warm_state") == "warm")
    soak_runs = [run for run in runs if run.get("mode") == "soak"]
    watcher_runs = [run for run in runs if run.get("mode") == "watcher"]
    blocked = sum(int(run.get("jobs_blocked") or 0) for run in runs)
    if blocked:
        status = "blocked"
    elif has_cold and has_warm and warm_skips > 0 and soak_runs and watcher_runs:
        status = "ok"
    else:
        status = "partial"
    return _check(
        "synthetic_reliability",
        status,
        "Synthetic reliability evidence covers scan, soak, watcher, and warm manifest behavior.",
        {
            "run_count": len(runs),
            "cold_scan": has_cold,
            "warm_scan": has_warm,
            "warm_manifest_skips": warm_skips,
            "soak_runs": len(soak_runs),
            "watcher_runs": len(watcher_runs),
            "jobs_blocked": blocked,
        },
    )


def _scoped_host_cloud_check(runs: list[dict[str, Any]], *, scope_type: str, scope_hash: str | None) -> dict[str, Any]:
    if scope_type == "synthetic" or not scope_hash:
        return _check("scoped_host_cloud", "missing", "Run scoped host/cloud calibration for an opted-in root or path.", {"scope_type": scope_type})
    if not runs:
        return _check("scoped_host_cloud", "missing", "Run scoped host/cloud calibration for the selected root or path.", {"scope_type": scope_type, "scope_hash": scope_hash})
    blocked = sum(int(run.get("jobs_blocked") or 0) for run in runs)
    files = sum(int(run.get("file_count") or 0) for run in runs)
    return _check(
        "scoped_host_cloud",
        "blocked" if blocked else "ok",
        "Scoped host/cloud evidence is present and stored as aggregate metadata only.",
        {"run_count": len(runs), "file_count": files, "jobs_blocked": blocked, "scope_hash": scope_hash},
    )


def _worker_tuning_check(runs: list[dict[str, Any]], *, blocked_workers: bool) -> dict[str, Any]:
    if blocked_workers:
        return _check("worker_tuning", "blocked", "Worker failures or blocked locks must be resolved before tuning candidates are trusted.", {"run_count": len(runs)})
    if not runs:
        return _check("worker_tuning", "missing", "Run tuning diagnostics before changing worker caps or hash parallelism.", {"run_count": 0})
    blocked = sum(int(run.get("jobs_blocked") or 0) for run in runs)
    return _check(
        "worker_tuning",
        "blocked" if blocked else "ok",
        "Tuning diagnostics are present; recommendations remain manual.",
        {"run_count": len(runs), "jobs_blocked": blocked},
    )


def _overall_readiness(checks: list[dict[str, Any]]) -> str:
    statuses = {str(item.get("status")) for item in checks}
    if "blocked" in statuses or "stale" in statuses:
        return "blocked"
    if statuses == {"ok"}:
        return "ready"
    if "ok" in statuses:
        return "partial"
    return "not_run"


def _evidence_scored_candidates(
    *,
    tuning_runs: list[dict[str, Any]],
    readiness: str,
    blocked_workers: bool,
    scope_type: str,
    scope_hash: str | None,
) -> list[dict[str, Any]]:
    candidates: list[dict[str, Any]] = []
    for run in tuning_runs:
        for item in _run_candidates(run):
            if not isinstance(item, dict):
                continue
            if blocked_workers or readiness == "blocked":
                state = "blocked_by_failures"
            elif readiness == "ready":
                state = "ready_to_try"
            else:
                state = "needs_comparison"
            setting = str(item.get("setting") or "")
            candidates.append(
                {
                    "setting": setting,
                    "current": item.get("current"),
                    "candidate": item.get("candidate"),
                    "evidence_state": state,
                    "reason": _candidate_reason(state),
                    "requires_manual_apply": True,
                    "settings_mutated": False,
                    "source_run_id": run.get("id"),
                    "follow_up_command": _follow_up_command(setting=setting, scope_type=scope_type, scope_hash=scope_hash),
                }
            )
    return candidates[:8]


def _run_candidates(run: dict[str, Any]) -> list[Any]:
    metadata = run.get("recommendation_metadata") if isinstance(run.get("recommendation_metadata"), dict) else {}
    candidates = metadata.get("candidates")
    return candidates if isinstance(candidates, list) else []


def _watcher_summary(runs: list[dict[str, Any]], events: list[dict[str, Any]]) -> dict[str, Any]:
    watcher_runs = [run for run in runs if run.get("mode") == "watcher"]
    latest = max(watcher_runs, key=lambda run: run.get("created_at") or "", default={})
    metadata = latest.get("metadata") if isinstance(latest.get("metadata"), dict) else {}
    backend = metadata.get("watcher_backend") if isinstance(metadata.get("watcher_backend"), dict) else {}
    event_counts = metadata.get("watcher_events") if isinstance(metadata.get("watcher_events"), dict) else {}
    return {
        "backend": backend.get("selected_backend") or backend.get("provider") or backend.get("policy") or "unknown",
        "run_count": len(watcher_runs),
        "event_count": len(events),
        "probe_event_count": sum(int(value or 0) for value in event_counts.values()) if event_counts else 0,
        "recent_actions": [str(event.get("action") or event.get("status") or "event") for event in events[:8] if isinstance(event, dict)],
    }


def _family_summary(row: dict[str, Any]) -> dict[str, Any]:
    return {
        key: row.get(key)
        for key in (
            "family",
            "resource_class",
            "configured_cap",
            "running",
            "pending",
            "blocked",
            "failed",
            "retrying_locked",
            "blocked_locked",
            "backpressure",
            "p95_duration_ms",
        )
        if row.get(key) not in {None, ""}
    }


def _check(check: str, status: str, summary: str, evidence: dict[str, Any]) -> dict[str, Any]:
    evidence = dict(evidence)
    evidence["settings_mutated"] = False
    return {"check": check, "status": status, "summary": summary, "evidence": evidence}


def _normalize_run(run: dict[str, Any] | None) -> dict[str, Any]:
    payload = dict(run or {})
    metadata = payload.get("metadata") if isinstance(payload.get("metadata"), dict) else {}
    recommendations = payload.get("recommendation_metadata") if isinstance(payload.get("recommendation_metadata"), dict) else {}
    payload["metadata"] = metadata
    payload["recommendation_metadata"] = recommendations
    payload["scenario"] = payload.get("scenario") or recommendations.get("scenario") or metadata.get("scenario")
    return payload


def _scenario(run: dict[str, Any]) -> str:
    return str(run.get("scenario") or "standard")


def _run_created_at(run: dict[str, Any]) -> datetime | None:
    return _parse_datetime(run.get("created_at"))


def _parse_datetime(value: Any) -> datetime | None:
    if isinstance(value, datetime):
        return _as_aware(value)
    if not value:
        return None
    try:
        return _as_aware(datetime.fromisoformat(str(value).replace("Z", "+00:00")))
    except ValueError:
        return None


def _as_aware(value: datetime) -> datetime:
    return value if value.tzinfo else value.replace(tzinfo=UTC)


def _evidence_age_hours(runs: list[dict[str, Any]], now: datetime) -> int | None:
    created = [_run_created_at(run) for run in runs]
    latest = max((item for item in created if item), default=None)
    if not latest:
        return None
    return max(0, int((now - latest).total_seconds() // 3600))


def _run_ref(run: dict[str, Any]) -> dict[str, Any]:
    keys = (
        "id",
        "scenario",
        "fixture",
        "mode",
        "status",
        "scope_type",
        "scope_hash",
        "label",
        "deployment_label",
        "warm_state",
        "file_count",
        "jobs_blocked",
        "created_at",
    )
    return {key: run.get(key) for key in keys if run.get(key) not in {None, ""}}


def _safe_crawl(crawl: dict[str, Any] | None) -> dict[str, Any] | None:
    if not crawl:
        return None
    return {
        key: crawl.get(key)
        for key in ("id", "status", "reason", "started_at", "finished_at", "files_seen", "files_changed", "files_deleted", "jobs_queued")
        if crawl.get(key) not in {None, ""}
    }


def _candidate_reason(state: str) -> str:
    if state == "ready_to_try":
        return "Recent reliability evidence is ready; apply manually only after reviewing the candidate."
    if state == "blocked_by_failures":
        return "Resolve blocked or failed worker evidence before applying this candidate."
    return "Collect a comparable before/after benchmark before applying this candidate."


def _follow_up_command(*, setting: str, scope_type: str, scope_hash: str | None) -> str:
    base = "flux-kb acceleration benchmark run --scenario tuning --mode scan --passes 2"
    if scope_type != "synthetic" and scope_hash:
        base += " --scope root"
    if setting:
        base += f" --label after-{setting.replace('.', '-')}"
    return base
