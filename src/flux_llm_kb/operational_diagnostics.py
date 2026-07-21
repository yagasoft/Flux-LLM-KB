from __future__ import annotations

from pathlib import PurePosixPath, PureWindowsPath
import time
from typing import Any


def summarize_operational_diagnostics(
    *,
    retrieval: dict[str, Any] | None = None,
    watcher: dict[str, Any] | None = None,
    workers: dict[str, Any] | None = None,
    jobs: dict[str, Any] | None = None,
    mail: dict[str, Any] | None = None,
    section: str = "all",
    root_name: str | None = None,
    status: str | None = None,
    family: str | None = None,
    since_hours: int | None = None,
    include_details: bool = False,
) -> dict[str, Any]:
    sections = {
        "retrieval": _sanitize_section(retrieval or {}),
        "watcher": _sanitize_section(watcher or {}),
        "workers": _sanitize_section(workers or {}),
        "jobs": _sanitize_section(jobs or {}),
        "mail": _sanitize_section(mail or {}),
    }
    counts = {
        "retrieval_explains": len(sections["retrieval"].get("recent_explains", []) or []),
        "watcher_events": len(sections["watcher"].get("events", []) or []),
        "worker_families": len(sections["workers"].get("families", []) or []),
        "retrying_gpu_evictions": len(_retrying_gpu_evictions(sections["workers"].get("gpu_evictions", {}))),
        "jobs": len(sections["jobs"].get("jobs", []) or []),
        "blocked_jobs": sum(1 for item in sections["jobs"].get("jobs", []) or [] if "blocked" in str(item.get("status") or "")),
        "mail_sync_runs": len(sections["mail"].get("sync_runs", []) or []),
        "mail_post_process_events": len(sections["mail"].get("post_process_events", []) or []),
    }
    filters = {
        "root_name": root_name,
        "status": status,
        "family": family,
        "since_hours": since_hours,
        "include_details": bool(include_details),
    }
    items = _diagnostic_items(sections, filters=filters)
    filtered_sections = _filter_sections(sections, filters=filters)
    selected_sections = filtered_sections if section == "all" else {section: filtered_sections.get(section, {})}
    return {
        "section": section,
        "settings_mutated": False,
        "counts": counts,
        "sections": selected_sections,
        "filters": filters,
        "items": items,
    }


def _sanitize_section(value: Any) -> Any:
    if isinstance(value, dict):
        return {str(key): _sanitize_section(item) for key, item in value.items()}
    if isinstance(value, list):
        return [_sanitize_section(item) for item in value]
    if isinstance(value, str):
        return _sanitize_text(value)
    return value


def _sanitize_text(value: str) -> str:
    normalized = value.replace("\\", "/")
    if ":/" in normalized or normalized.startswith("/"):
        leaf = PurePosixPath(normalized).name or PureWindowsPath(value).name
        return leaf or "<path>"
    return value


def _diagnostic_items(sections: dict[str, Any], *, filters: dict[str, Any]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for event in sections["watcher"].get("events", []) or []:
        if not isinstance(event, dict):
            continue
        rows.append(_item("watcher", event, status=str(event.get("status") or event.get("action") or "event"), family=None))
    for family in sections["workers"].get("families", []) or []:
        if not isinstance(family, dict):
            continue
        severity = "warning" if int(family.get("blocked_locked") or 0) or int(family.get("failed") or 0) else "info"
        rows.append(
            _item(
                "workers",
                family,
                status=str(family.get("backpressure") or "observed"),
                family=str(family.get("family") or ""),
                severity=severity,
            )
        )
    for eviction in _retrying_gpu_evictions(sections["workers"].get("gpu_evictions", {})):
        rows.append(
            _item(
                "workers",
                _gpu_eviction_diagnostic_row(eviction),
                status="gpu_eviction_retrying",
                family="gpu_eviction",
                severity="warning",
            )
        )
    for job in sections["jobs"].get("jobs", []) or []:
        if not isinstance(job, dict):
            continue
        job_status = str(job.get("status") or "unknown")
        severity = "warning" if job_status == "stranded_command" or "blocked" in job_status or "failed" in job_status else "info"
        rows.append(_item("jobs", job, status=job_status, family=str(job.get("job_family") or ""), severity=severity))
    for run in sections["mail"].get("sync_runs", []) or []:
        if not isinstance(run, dict):
            continue
        run_status = str(run.get("status") or "unknown")
        severity = "warning" if run_status not in {"completed", "queued", "claimed", "running"} else "info"
        rows.append(_item("mail", run, status=run_status, family=None, severity=severity))
    return [row for row in rows if _matches_filters(row, filters)][:25]


def _filter_sections(sections: dict[str, Any], *, filters: dict[str, Any]) -> dict[str, Any]:
    result = {
        "retrieval": sections.get("retrieval", {}),
        "watcher": {"events": []},
        "workers": {
            "families": [],
            "gpu_evictions": sections.get("workers", {}).get("gpu_evictions", {}),
            "gpu_runtime_reconciliation": sections.get("workers", {}).get("gpu_runtime_reconciliation"),
        },
        "jobs": {"jobs": []},
        "mail": sections.get("mail", {}),
    }
    for event in sections.get("watcher", {}).get("events", []) or []:
        item = _item("watcher", event, status=str(event.get("status") or event.get("action") or "event"), family=None) if isinstance(event, dict) else {}
        if item and _matches_filters(item, filters):
            result["watcher"]["events"].append(event)
    for family in sections.get("workers", {}).get("families", []) or []:
        if not isinstance(family, dict):
            continue
        item = _item("workers", family, status=str(family.get("backpressure") or "observed"), family=str(family.get("family") or ""))
        if _matches_filters(item, filters):
            result["workers"]["families"].append(family)
    for job in sections.get("jobs", {}).get("jobs", []) or []:
        if not isinstance(job, dict):
            continue
        item = _item("jobs", job, status=str(job.get("status") or "unknown"), family=str(job.get("job_family") or ""))
        if _matches_filters(item, filters):
            result["jobs"]["jobs"].append(job)
    return result


def _item(
    section: str,
    row: dict[str, Any],
    *,
    status: str,
    family: str | None,
    severity: str = "info",
) -> dict[str, Any]:
    root_name = row.get("root_name")
    target_id = row.get("id") or row.get("root_name") or row.get("family") or row.get("profile_name") or section
    summary = _summary(section=section, status=status, row=row)
    evidence = {
        key: value
        for key, value in row.items()
        if key in {
            "pending",
            "running",
            "blocked",
            "failed",
            "retrying_locked",
            "blocked_locked",
            "action",
            "status",
            "duration_ms",
            "age_seconds",
            "broker_delivery_count",
            "broker_message_id",
            "routing_key",
            "last_error",
            "model_id",
            "component",
        }
    }
    return {
        "section": section,
        "severity": severity,
        "status": status,
        "family": family,
        "root_name": root_name,
        "summary": summary,
        "user_action": _user_action(section=section, status=status),
        "evidence": evidence,
        "follow_up_command": _follow_up_command(section, row),
        "target": {"type": "job" if section == "jobs" else section.rstrip("s"), "id": target_id},
        "remediation_actions": _remediation_actions(
            section=section,
            status=status,
            family=family,
            root_name=root_name if isinstance(root_name, str) else None,
            target_id=str(target_id) if target_id is not None else None,
        ),
    }


def _summary(*, section: str, status: str, row: dict[str, Any]) -> str:
    if section == "jobs":
        return f"Job {row.get('id') or ''} is {status}.".strip()
    if section == "workers" and status == "gpu_eviction_retrying":
        delivery_count = row.get("broker_delivery_count") or 0
        return f"GPU eviction {row.get('id') or 'request'} is retrying after {delivery_count} deliveries."
    if section == "workers":
        return f"Worker family {row.get('family') or 'unknown'} reports {status}."
    if section == "watcher":
        return f"Watcher event {status} observed."
    if section == "mail":
        return f"Mail sync {row.get('profile_name') or 'profile'} is {status}."
    return f"{section} diagnostic is {status}."


def _user_action(*, section: str, status: str) -> str:
    if section == "workers" and status == "gpu_eviction_retrying":
        return "Review the retry age, delivery count, and last error; no-op evictions should become terminal under the brokered handler."
    if section == "jobs" and status == "blocked_by_policy":
        return "Review include/exclude globs, strict indexing rules, and size limits before retrying or excluding the file."
    if section == "jobs" and status == "blocked_invalid_source":
        return "Try to repair, resave, rehydrate, or exclude the source file before retrying."
    if section == "jobs" and status == "blocked_missing_dependency":
        return "Install or configure the missing extractor dependency before retrying."
    if section == "jobs" and status.startswith("blocked_"):
        return "Inspect the blocker details and retry only after the underlying condition is resolved."
    if section == "jobs" and status == "failed":
        return "Inspect the last error and retry after correcting the failed extraction condition."
    if section == "jobs" and status == "stranded_command":
        return "Repair the stranded capture command to publish a fresh command for the pending job."
    return "Review the diagnostic evidence and run only the scoped remediation actions that match the issue."


def _follow_up_command(section: str, row: dict[str, Any]) -> str:
    if section == "workers" and row.get("family") == "gpu_eviction":
        return "flux-kb diagnostics workers --family gpu_eviction"
    if section == "jobs":
        family = row.get("job_family") or "all"
        return f"flux-kb crawl worker status --family {family}"
    if section == "workers":
        family = row.get("family") or "all"
        return f"flux-kb crawl worker status --family {family}"
    if section == "watcher":
        return "flux-kb crawl watch probe --timeout 2"
    if section == "mail":
        return "flux-kb mail status"
    return "flux-kb diagnostics all"


def _retrying_gpu_evictions(evictions: Any) -> list[dict[str, Any]]:
    if not isinstance(evictions, dict):
        return []
    recent = evictions.get("recent") if isinstance(evictions.get("recent"), list) else []
    return [
        item
        for item in recent
        if isinstance(item, dict) and str(item.get("status") or "") == "retrying"
    ]


def _gpu_eviction_diagnostic_row(eviction: dict[str, Any]) -> dict[str, Any]:
    created_at = _float_or_none(eviction.get("created_at"))
    age_seconds = max(0, int(time.time() - created_at)) if created_at is not None else None
    return {
        "id": eviction.get("id"),
        "family": "gpu_eviction",
        "status": "gpu_eviction_retrying",
        "age_seconds": age_seconds,
        "broker_delivery_count": eviction.get("broker_delivery_count"),
        "last_error": eviction.get("error"),
        "model_id": eviction.get("model_id"),
        "component": eviction.get("component"),
    }


def _float_or_none(value: Any) -> float | None:
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _remediation_actions(
    *,
    section: str,
    status: str,
    family: str | None,
    root_name: str | None,
    target_id: str | None,
) -> list[dict[str, Any]]:
    actions: list[dict[str, Any]] = []
    normalized_family = family or None
    if section == "jobs" and target_id and status == "stranded_command":
        actions.append(
            _action(
                action_id="repair_stranded_capture_command",
                label="Repair stranded command",
                target_type="job",
                target_id=target_id,
                root_name=root_name,
                family=normalized_family,
            )
        )
    if section == "jobs" and target_id and status in {
        "failed",
        "blocked_missing_dependency",
        "blocked_by_policy",
        "blocked_invalid_source",
        "blocked_locked",
        "retrying_locked",
    }:
        actions.append(
            _action(
                action_id="retry_corpus_job",
                label="Retry corpus job",
                target_type="job",
                target_id=target_id,
                root_name=root_name,
                family=normalized_family,
            )
        )
    if section in {"jobs", "workers"} and root_name and normalized_family:
        actions.append(
            _action(
                action_id="run_backfill",
                label="Run scoped backfill",
                target_type="family",
                target_id=normalized_family,
                root_name=root_name,
                family=normalized_family,
            )
        )
    if root_name:
        actions.append(
            _action(
                action_id="repair_asset_statuses",
                label="Repair asset statuses",
                target_type="root",
                target_id=root_name,
                root_name=root_name,
                family=normalized_family,
            )
        )
        actions.append(
            _action(
                action_id="clear_completed_errors",
                label="Clear completed errors",
                target_type="root",
                target_id=root_name,
                root_name=root_name,
                family=normalized_family,
            )
        )
    return actions


def _action(
    *,
    action_id: str,
    label: str,
    target_type: str,
    target_id: str | None,
    root_name: str | None,
    family: str | None,
) -> dict[str, Any]:
    payload = {
        "action": action_id,
        "target_type": target_type,
        "target_id": target_id,
        "root_name": root_name,
        "family": family,
        "reason": "operator diagnostic remediation",
    }
    return {
        "id": action_id,
        "label": label,
        "target": {"type": target_type, "id": target_id},
        "method": "POST",
        "endpoint": "/api/diagnostics/actions",
        "payload": payload,
        "requires_confirmation": True,
        "destructive": False,
        "settings_mutated": False,
    }


def _matches_filters(row: dict[str, Any], filters: dict[str, Any]) -> bool:
    if filters.get("root_name") and row.get("root_name") != filters.get("root_name"):
        return False
    if filters.get("status") and row.get("status") != filters.get("status"):
        return False
    if filters.get("family") and row.get("family") != filters.get("family"):
        return False
    return True
