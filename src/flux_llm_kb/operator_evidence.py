from __future__ import annotations

from typing import Any


def build_operator_evidence_report(
    *,
    reliability: dict[str, Any],
    roots: dict[str, Any],
    code_status: dict[str, Any],
    diagnostics: dict[str, Any],
) -> dict[str, Any]:
    checks = reliability.get("checks") if isinstance(reliability.get("checks"), list) else []
    check_status = {str(item.get("check")): str(item.get("status")) for item in checks if isinstance(item, dict)}
    root_totals = roots.get("totals") if isinstance(roots.get("totals"), dict) else {}
    root_rows = roots.get("roots") if isinstance(roots.get("roots"), list) else []
    diagnostic_items = diagnostics.get("items") if isinstance(diagnostics.get("items"), list) else []
    blockers = _top_blockers(root_rows=root_rows, diagnostic_items=diagnostic_items)
    manual_followups = _manual_followups(reliability)
    eligible = _eligible_for_design(reliability=reliability, checks=check_status, root_totals=root_totals, blockers=blockers)
    gate_state = "eligible_for_design" if eligible else ("blocked" if _has_blocked_evidence(check_status, root_totals, blockers) else "hold")
    gate_reasons = _gate_reasons(reliability=reliability, checks=check_status, root_totals=root_totals, blockers=blockers)
    return {
        "settings_mutated": False,
        "readiness": reliability.get("readiness") or "not_run",
        "evidence_age_hours": reliability.get("evidence_age_hours"),
        "root_readiness": {
            "ready": int(root_totals.get("ready") or 0),
            "partial": int(root_totals.get("partial") or 0),
            "blocked": int(root_totals.get("blocked") or 0),
            "not_run": int(root_totals.get("not_run") or 0),
            "total": int(root_totals.get("total") or 0),
        },
        "latest_runs": reliability.get("latest_runs") if isinstance(reliability.get("latest_runs"), list) else [],
        "top_blockers": blockers[:8],
        "manual_follow_ups": manual_followups[:8],
        "code_gaps": code_status.get("gaps") if isinstance(code_status.get("gaps"), list) else [],
        "diagnostic_items": diagnostic_items[:8],
        "gates": {
            "vss_snapshot": {
                "state": gate_state,
                "reason": "VSS snapshot extraction remains a design-only candidate until live reliability evidence requires it.",
                "requirements": gate_reasons,
                "settings_mutated": False,
            },
            "provider_acceleration": {
                "state": gate_state,
                "reason": "Provider acceleration remains blocked unless the same evidence gate is clean and tuning candidates are manual.",
                "requirements": gate_reasons,
                "settings_mutated": False,
            },
        },
    }


def _eligible_for_design(
    *,
    reliability: dict[str, Any],
    checks: dict[str, str],
    root_totals: dict[str, Any],
    blockers: list[dict[str, Any]],
) -> bool:
    return (
        reliability.get("readiness") == "ready"
        and checks.get("synthetic_reliability") == "ok"
        and checks.get("scoped_host_cloud") == "ok"
        and checks.get("worker_tuning") == "ok"
        and int(root_totals.get("total") or 0) > 0
        and int(root_totals.get("partial") or 0) == 0
        and int(root_totals.get("blocked") or 0) == 0
        and int(root_totals.get("not_run") or 0) == 0
        and not blockers
        and _has_watcher_evidence(reliability)
    )


def _has_watcher_evidence(reliability: dict[str, Any]) -> bool:
    watcher = reliability.get("watcher") if isinstance(reliability.get("watcher"), dict) else {}
    if int(watcher.get("run_count") or 0) > 0:
        return True
    return any((run.get("mode") == "watcher") for run in reliability.get("latest_runs", []) if isinstance(run, dict))


def _has_blocked_evidence(checks: dict[str, str], root_totals: dict[str, Any], blockers: list[dict[str, Any]]) -> bool:
    return (
        "blocked" in set(checks.values())
        or int(root_totals.get("blocked") or 0) > 0
        or any(item.get("severity") == "error" for item in blockers)
    )


def _gate_reasons(
    *,
    reliability: dict[str, Any],
    checks: dict[str, str],
    root_totals: dict[str, Any],
    blockers: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    return [
        {"requirement": "synthetic_reliability", "status": checks.get("synthetic_reliability", "missing")},
        {"requirement": "scoped_host_cloud", "status": checks.get("scoped_host_cloud", "missing")},
        {"requirement": "worker_tuning", "status": checks.get("worker_tuning", "missing")},
        {"requirement": "watcher_evidence", "status": "ok" if _has_watcher_evidence(reliability) else "missing"},
        {
            "requirement": "all_roots_ready",
            "status": "ok"
            if int(root_totals.get("total") or 0) > 0
            and int(root_totals.get("partial") or 0) == 0
            and int(root_totals.get("blocked") or 0) == 0
            and int(root_totals.get("not_run") or 0) == 0
            else "partial",
        },
        {"requirement": "unresolved_blockers", "status": "ok" if not blockers else "partial"},
    ]


def _top_blockers(*, root_rows: list[Any], diagnostic_items: list[Any]) -> list[dict[str, Any]]:
    blockers: list[dict[str, Any]] = []
    for row in root_rows:
        if not isinstance(row, dict):
            continue
        readiness = str(row.get("readiness") or "not_run")
        block_counts = row.get("blockers") if isinstance(row.get("blockers"), dict) else {}
        has_counts = any(int(value or 0) > 0 for value in block_counts.values())
        if readiness not in {"ready"} or has_counts:
            blockers.append(
                {
                    "section": "reliability",
                    "severity": "error" if readiness == "blocked" else "warning",
                    "root_name": row.get("root_name"),
                    "summary": row.get("required_action") or f"Root reliability is {readiness}.",
                    "evidence": block_counts,
                    "target": {"type": "root", "id": row.get("root_name")},
                }
            )
    for item in diagnostic_items:
        if isinstance(item, dict) and item.get("severity") in {"warning", "error"}:
            blockers.append(
                {
                    "section": item.get("section"),
                    "severity": item.get("severity"),
                    "root_name": item.get("root_name"),
                    "summary": item.get("summary"),
                    "evidence": item.get("evidence") if isinstance(item.get("evidence"), dict) else {},
                    "target": item.get("target") if isinstance(item.get("target"), dict) else {},
                }
            )
    return blockers


def _manual_followups(reliability: dict[str, Any]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for item in reliability.get("candidates", []) or []:
        if not isinstance(item, dict):
            continue
        command = item.get("follow_up_command")
        if command:
            rows.append(
                {
                    "setting": item.get("setting"),
                    "command": command,
                    "requires_manual_apply": True,
                    "settings_mutated": False,
                }
            )
    return rows
