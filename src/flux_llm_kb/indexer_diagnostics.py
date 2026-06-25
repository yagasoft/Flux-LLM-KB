from __future__ import annotations

import os
import re
from typing import Any


BENCHMARK_SCENARIOS: tuple[str, ...] = (
    "standard",
    "reliability",
    "host_cloud",
    "cache_readiness",
    "tuning",
)

_PATH_FRAGMENT_RE = re.compile(r"([A-Za-z]:[\\/][^\s,;]+|/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)+)")


def normalize_benchmark_scenario(value: str | None) -> str:
    scenario = str(value or "standard").strip().lower().replace("-", "_")
    if scenario not in BENCHMARK_SCENARIOS:
        raise ValueError(f"benchmark scenario must be one of: {', '.join(BENCHMARK_SCENARIOS)}")
    return scenario


def scenario_recommendation_metadata(scenario: str) -> dict[str, Any]:
    return {"settings_mutated": False, "scenario": normalize_benchmark_scenario(scenario)}


def build_indexer_diagnostics(
    *,
    scenario: str,
    runs: list[dict[str, Any]],
    scope_descriptor: dict[str, Any],
    settings_snapshot: dict[str, Any],
    acceleration_status: dict[str, Any] | None,
    model_telemetry: dict[str, Any] | None,
    lock_retry_cooldown_seconds: int,
    lock_max_attempts: int,
    scenario_evidence: dict[str, Any] | None = None,
) -> list[dict[str, Any]]:
    normalized = normalize_benchmark_scenario(scenario)
    if normalized == "standard":
        return []

    diagnostics: list[dict[str, Any]] = []
    if normalized == "reliability":
        evidence = scenario_evidence or {}
        diagnostics.extend(
            [
                _file_churn_diagnostic(runs, evidence.get("file_churn") if isinstance(evidence.get("file_churn"), dict) else {}),
                _lock_recovery_diagnostic(runs, lock_retry_cooldown_seconds, lock_max_attempts),
                _watch_reconcile_diagnostic(runs),
            ]
        )
    elif normalized == "host_cloud":
        diagnostics.append(_host_cloud_diagnostic(runs, scope_descriptor))
    elif normalized == "cache_readiness":
        diagnostics.append(_cache_readiness_diagnostic(acceleration_status or {}, model_telemetry or {}))
    elif normalized == "tuning":
        candidates = recommendation_candidates(scenario=normalized, runs=runs, settings_snapshot=settings_snapshot)
        diagnostics.append(
            {
                "scenario": normalized,
                "check": "tuning",
                "status": "ok" if candidates else "observed",
                "summary": "Bounded benchmark comparison completed; candidates require manual settings changes.",
                "evidence": {
                    "candidate_count": len(candidates),
                    "scan_runs": sum(1 for run in runs if run.get("mode") == "scan"),
                    "soak_runs": sum(1 for run in runs if run.get("mode") == "soak"),
                    "settings_mutated": False,
                },
            }
        )
    return diagnostics


def build_benchmark_recommendations(
    *,
    scenario: str,
    runs: list[dict[str, Any]],
    settings_snapshot: dict[str, Any],
) -> dict[str, Any]:
    normalized = normalize_benchmark_scenario(scenario)
    best_scan = max(
        (run for run in runs if run.get("mode") == "scan"),
        key=lambda run: float(run.get("throughput_files_per_second") or 0.0),
        default=None,
    )
    best_soak = max(
        (run for run in runs if run.get("mode") == "soak"),
        key=lambda run: int(run.get("jobs_completed") or 0),
        default=None,
    )
    return {
        "settings_mutated": False,
        "scenario": normalized,
        "observed_hash_parallelism": best_scan.get("hash_parallelism") if best_scan else None,
        "observed_worker_count": best_soak.get("worker_count") if best_soak else None,
        "basis": "diagnostic_observation_only",
        "candidates": recommendation_candidates(scenario=normalized, runs=runs, settings_snapshot=settings_snapshot),
    }


def recommendation_candidates(
    *,
    scenario: str,
    runs: list[dict[str, Any]],
    settings_snapshot: dict[str, Any],
) -> list[dict[str, Any]]:
    if normalize_benchmark_scenario(scenario) != "tuning":
        return []

    candidates: list[dict[str, Any]] = []
    current_hash = _positive_int(settings_snapshot.get("hash_parallelism"), default=1)
    observed_hash = max(
        (_positive_int(run.get("hash_parallelism"), default=current_hash) for run in runs if run.get("mode") == "scan"),
        default=current_hash,
    )
    max_hash_candidate = max(2, min(8, os.cpu_count() or 2))
    hash_candidate = min(max_hash_candidate, max(current_hash + 1, observed_hash))
    if hash_candidate > current_hash:
        candidates.append(
            {
                "setting": "crawler.hash_parallelism",
                "current": current_hash,
                "candidate": hash_candidate,
                "reason": "Scan benchmarks produced reusable cold/warm evidence; increase hash fan-out manually and compare a follow-up run.",
                "requires_manual_apply": True,
                "settings_mutated": False,
            }
        )

    worker_caps = settings_snapshot.get("worker_caps") if isinstance(settings_snapshot.get("worker_caps"), dict) else {}
    for family in ("general", "text", "office", "media"):
        current_cap = _positive_int(worker_caps.get(family), default=1)
        if current_cap >= 4:
            continue
        candidate_cap = min(4, current_cap + 1)
        candidates.append(
            {
                "setting": f"acceleration.worker_cap.{family}",
                "current": current_cap,
                "candidate": candidate_cap,
                "reason": f"Worker-family cap candidate for {family}; apply manually only after reviewing queue pressure and host capacity.",
                "requires_manual_apply": True,
                "settings_mutated": False,
            }
        )
        if family == "general":
            break
    return candidates


def _file_churn_diagnostic(runs: list[dict[str, Any]], probe_evidence: dict[str, Any]) -> dict[str, Any]:
    scan_runs = [run for run in runs if run.get("mode") == "scan"]
    warm_runs = [run for run in scan_runs if run.get("warm_state") == "warm"]
    warm_manifest_skips = sum(int(run.get("manifest_skipped_unchanged") or 0) for run in warm_runs)
    evidence = {
        "cold_runs": sum(1 for run in scan_runs if run.get("warm_state") == "cold"),
        "warm_runs": len(warm_runs),
        "warm_manifest_skips": warm_manifest_skips,
        "cache_hits": sum(int(run.get("cache_hits") or 0) for run in scan_runs),
        "cache_misses": sum(int(run.get("cache_misses") or 0) for run in scan_runs),
        "pending_stable_probe": "metadata_only",
        "settings_mutated": False,
    }
    evidence.update({key: value for key, value in probe_evidence.items() if key not in evidence})
    return {
        "scenario": "reliability",
        "check": "file_churn",
        "status": "ok" if scan_runs else "not_run",
        "summary": "Synthetic file churn checks cover cold scan, warm manifest skip evidence, and pending-stable style no-op handling.",
        "evidence": evidence,
    }


def _lock_recovery_diagnostic(runs: list[dict[str, Any]], retry_cooldown_seconds: int, max_attempts: int) -> dict[str, Any]:
    soak_runs = [run for run in runs if run.get("mode") == "soak"]
    blocked_locked = sum(int(run.get("jobs_blocked") or 0) for run in soak_runs)
    return {
        "scenario": "reliability",
        "check": "lock_recovery",
        "status": "ok" if soak_runs else "not_run",
        "summary": "Benchmark-tagged worker jobs exercised retry-to-block evidence without mutating settings.",
        "evidence": {
            "retrying_locked": 1 if soak_runs and max_attempts > 1 else 0,
            "blocked_locked": blocked_locked,
            "retry_cooldown_seconds": max(1, int(retry_cooldown_seconds)),
            "max_attempts": max(1, int(max_attempts)),
            "settings_mutated": False,
        },
    }


def _watch_reconcile_diagnostic(runs: list[dict[str, Any]]) -> dict[str, Any]:
    watcher_runs = [run for run in runs if run.get("mode") == "watcher"]
    watcher_metadata = watcher_runs[-1].get("metadata", {}) if watcher_runs else {}
    watcher_backend = watcher_metadata.get("watcher_backend") if isinstance(watcher_metadata, dict) else {}
    backend_name = None
    if isinstance(watcher_backend, dict):
        backend_name = watcher_backend.get("selected_backend") or watcher_backend.get("provider") or watcher_backend.get("policy")
    return {
        "scenario": "reliability",
        "check": "watch_reconcile",
        "status": "ok" if watcher_runs else "not_run",
        "summary": "Watcher reconciliation evidence uses startup/periodic metadata and temporary-root no-op probes.",
        "evidence": {
            "watcher_runs": len(watcher_runs),
            "watcher_backend": backend_name or "unknown",
            "changed_probe": bool(watcher_runs),
            "deleted_probe": bool(watcher_runs),
            "noop_temp_root_probe": bool(watcher_runs),
            "settings_mutated": False,
        },
    }


def _host_cloud_diagnostic(runs: list[dict[str, Any]], scope_descriptor: dict[str, Any]) -> dict[str, Any]:
    return {
        "scenario": "host_cloud",
        "check": "host_cloud",
        "status": "ok" if runs else "not_run",
        "summary": "Host/cloud calibration stores only aggregate scope hashes, host access mode, and counts.",
        "evidence": {
            "scope_type": scope_descriptor.get("scope_type"),
            "scope_hash": scope_descriptor.get("scope_hash"),
            "host_access": scope_descriptor.get("host_access", "direct"),
            "run_count": len(runs),
            "file_count": sum(int(run.get("file_count") or 0) for run in runs),
            "manifest_skips": sum(int(run.get("manifest_skipped_unchanged") or 0) for run in runs),
            "cache_hits": sum(int(run.get("cache_hits") or 0) for run in runs),
            "cache_misses": sum(int(run.get("cache_misses") or 0) for run in runs),
            "delayed_availability_probe": "aggregate_only",
            "settings_mutated": False,
        },
    }


def _cache_readiness_diagnostic(acceleration_status: dict[str, Any], model_telemetry: dict[str, Any]) -> dict[str, Any]:
    cache = acceleration_status.get("cache") if isinstance(acceleration_status.get("cache"), dict) else {}
    directories = cache.get("directories") if isinstance(cache.get("directories"), dict) else {}
    tools = model_telemetry.get("tools") if isinstance(model_telemetry.get("tools"), dict) else {}
    missing_tools = [
        str(name)
        for name, item in sorted(tools.items())
        if isinstance(item, dict) and not bool(item.get("ok"))
    ]
    local_model = model_telemetry.get("local_model") if isinstance(model_telemetry.get("local_model"), dict) else {}
    evidence = {
        "cache_root_configured": bool(cache.get("root")),
        "cache_source": _safe_text(cache.get("source") or "default"),
        "cache_directory_count": len(directories),
        "blocked_dependency_count": int(model_telemetry.get("blocked_dependency_count") or len(missing_tools)),
        "missing_tools": missing_tools,
        "local_model_state": _safe_text(local_model.get("state") or local_model.get("provider") or "unknown"),
        "settings_mutated": False,
    }
    return {
        "scenario": "cache_readiness",
        "check": "cache_readiness",
        "status": "warning" if evidence["blocked_dependency_count"] else "ok",
        "summary": "Cache, extractor, and local-model readiness summarized without storing local paths.",
        "evidence": evidence,
    }


def _positive_int(value: Any, *, default: int) -> int:
    try:
        parsed = int(value)
    except Exception:
        return default
    return parsed if parsed > 0 else default


def _safe_text(value: Any) -> str:
    return _PATH_FRAGMENT_RE.sub("<path>", str(value or ""))[:200]
