from __future__ import annotations

from datetime import datetime
import json
from typing import Any, Callable, Iterable

from . import database


DASHBOARD_JOB_SOURCES = (
    "capture_jobs",
    "stranded_capture_commands",
    "mail_sync_runs",
    "outlook_sync_requests",
    "runtime_control_requests",
    "operator_automation_runs",
    "memory_governance_runs",
    "message_outbox",
    "message_inbox",
    "callback_deliveries",
    "gpu_leases",
    "gpu_evictions",
    "model_activity_events",
)

_DOMAIN_AGGREGATE_SOURCES = {
    "capture_jobs": "capture_jobs",
    "mail_sync_runs": "mail_sync_runs",
    "outlook_sync_requests": "outlook_sync_requests",
    "runtime_control_requests": "runtime_control_requests",
    "operator_automation_runs": "operator_automation_runs",
    "memory_governance_runs": "memory_governance_runs",
    "callback_deliveries": "callback_deliveries",
    "gpu_evictions": "gpu_evictions",
}
_QUERY_MODEL_SURFACES = {"api", "codex", "dashboard", "mcp"}
_QUERY_MODEL_CLASSES = {"control_plane", "health", "retrieval"}


def collect_dashboard_jobs_payload(
    *,
    limit: int = 50,
    offset: int = 0,
    status: str | list[str] | tuple[str, ...] | None = None,
    root_name: str | list[str] | tuple[str, ...] | None = None,
    job_type: str | list[str] | tuple[str, ...] | None = None,
    job_source: str | list[str] | tuple[str, ...] | None = None,
    updated_from: str | None = None,
    updated_to: str | None = None,
    sort_by: str | None = "updated",
    sort_dir: str | None = "desc",
) -> dict[str, Any]:
    safe_limit = _bounded_limit(limit)
    safe_offset = _bounded_offset(offset)
    source_limit = max(200, min(1000, safe_limit + safe_offset + 200))
    rows = _collect_all_rows(source_limit=source_limit)
    filter_options = _filter_options(
        rows,
        status=status,
        root_name=root_name,
        job_type=job_type,
        job_source=job_source,
        updated_from=updated_from,
        updated_to=updated_to,
    )
    filtered = [
        row
        for row in rows
        if _matches_filters(
            row,
            status=status,
            root_name=root_name,
            job_type=job_type,
            job_source=job_source,
            updated_from=updated_from,
            updated_to=updated_to,
        )
    ]
    sorted_rows = sorted(filtered, key=lambda row: _sort_key(row, sort_by), reverse=_sort_desc(sort_dir))
    page = sorted_rows[safe_offset : safe_offset + safe_limit]
    return {
        "jobs": page,
        "count": len(filtered),
        "limit": safe_limit,
        "offset": safe_offset,
        "has_next": safe_offset + len(page) < len(filtered),
        "filter_options": filter_options,
    }


def collect_dashboard_job_counts() -> dict[str, int]:
    counts = {"pending": 0, "running": 0, "failed": 0, "blocked": 0}
    for row in _collect_all_rows(source_limit=1000):
        group = str(row.get("status_group") or "")
        if group in counts:
            counts[group] += 1
    return counts


def _collect_all_rows(*, source_limit: int) -> list[dict[str, Any]]:
    domain_rows: list[dict[str, Any]] = []
    domain_rows.extend(_capture_job_rows(_safe_source(lambda: database.list_capture_jobs(limit=source_limit, offset=0), [])))
    domain_rows.extend(
        _stranded_capture_rows(
            _safe_source(
                lambda: _call_if_available(
                    "list_stranded_capture_commands",
                    root_name=None,
                    family=None,
                    min_age_seconds=60,
                    limit=source_limit,
                ),
                [],
            )
        )
    )
    domain_rows.extend(_mail_sync_rows(_safe_source(lambda: database.list_mail_sync_runs(limit=source_limit), [])))
    domain_rows.extend(_outlook_request_rows(_safe_source(lambda: database.list_outlook_sync_requests(limit=source_limit), [])))
    domain_rows.extend(_runtime_control_rows(_safe_source(lambda: _call_if_available("list_runtime_control_requests", limit=source_limit), [])))
    domain_rows.extend(_operator_automation_rows(_safe_source(lambda: database.list_operator_automation_runs(limit=source_limit), [])))
    domain_rows.extend(_governance_rows(_safe_source(lambda: database.list_memory_governance_runs(limit=source_limit), [])))
    domain_rows.extend(_callback_rows(_safe_source(lambda: _call_if_available("list_callback_delivery_jobs", limit=source_limit), [])))
    domain_rows.extend(_gpu_lease_rows(_safe_source(lambda: _call_if_available("list_gpu_lease_jobs", limit=source_limit), [])))
    domain_rows.extend(_gpu_eviction_rows(_safe_source(lambda: _call_if_available("list_gpu_eviction_jobs", limit=source_limit), [])))
    domain_rows.extend(
        _model_activity_rows(
            _safe_source(
                lambda: database.list_model_activity_events(
                    window_minutes=24 * 60,
                    limit=source_limit,
                    offset=0,
                    include_control_plane=True,
                ),
                [],
            )
        )
    )

    domain_keys = _domain_keys(domain_rows)
    broker_message_ids = {
        str(row.get("details", {}).get("broker_message_id") or "").strip()
        for row in domain_rows
        if str(row.get("details", {}).get("broker_message_id") or "").strip()
    }
    broker_rows = _outbox_rows(
        _safe_source(lambda: _call_if_available("list_message_outbox_jobs", limit=source_limit), []),
        domain_keys=domain_keys,
        broker_message_ids=broker_message_ids,
    )
    broker_rows.extend(_inbox_rows(_safe_source(lambda: _call_if_available("list_message_inbox_jobs", limit=source_limit), [])))
    return [*domain_rows, *broker_rows]


def _capture_job_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    output = []
    for row in rows:
        payload = _dict(row.get("payload"))
        telemetry = _dict(row.get("telemetry"))
        source_id = _text(row.get("id"))
        target = _first_text(payload.get("path"), payload.get("canonical_path"), payload.get("file_path"), payload.get("profile_name"))
        if not target and row.get("job_type") == "corpus_sync_root":
            target = "Root sync"
        output.append(
            _job_row(
                source="capture_jobs",
                source_id=source_id,
                job_type=_text(row.get("job_type")) or "capture_job",
                status=_text(row.get("status")) or "unknown",
                target=target or "No path",
                root_name=_first_text(payload.get("root_name"), row.get("root_name")) or "corpus",
                attempts=_int(row.get("attempts")),
                last_error=_text(row.get("last_error")),
                created_at=_text(row.get("created_at")),
                updated_at=_text(row.get("updated_at")),
                started_at=_text(row.get("started_at")),
                completed_at=_text(row.get("completed_at")),
                progress=_progress(row, telemetry),
                details={"payload": payload, "telemetry": telemetry, "broker_message_id": _text(row.get("broker_message_id"))},
            )
        )
    return output


def _stranded_capture_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    output = []
    for row in rows:
        payload = _dict(row.get("payload"))
        telemetry = _dict(row.get("telemetry"))
        source_id = _first_text(row.get("job_id"), row.get("id"))
        output.append(
            _job_row(
                source="stranded_capture_commands",
                source_id=source_id,
                job_type=_text(row.get("job_type")) or "stranded_capture_command",
                status=_text(row.get("status")) or "stranded_command",
                target=_first_text(payload.get("path"), row.get("path")) or "No path",
                root_name=_first_text(payload.get("root_name"), row.get("root_name")) or "corpus",
                attempts=_int(row.get("attempts")),
                last_error=_text(row.get("last_error")),
                created_at=_text(row.get("created_at")),
                updated_at=_text(row.get("updated_at")),
                progress=_progress(row, telemetry),
                details={"payload": payload, "telemetry": telemetry, "capture_job_id": source_id},
            )
        )
    return output


def _mail_sync_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        _job_row(
            source="mail_sync_runs",
            source_id=_text(row.get("id")),
            job_type="mail_sync",
            status=_text(row.get("status")) or "unknown",
            target=_text(row.get("profile_name")) or "mail profile",
            root_name="mail",
            attempts=_int(row.get("attempt_count")),
            last_error=_text(row.get("last_error")),
            created_at=_text(row.get("started_at")),
            updated_at=_first_text(row.get("updated_at"), row.get("finished_at"), row.get("started_at")),
            started_at=_text(row.get("started_at")),
            completed_at=_text(row.get("finished_at")),
            progress=_mail_progress(row),
            details=_compact_details(row, "profile_name", "trigger", "requested_by", "claimed_by", "worker_id", "messages_seen", "messages_exported"),
        )
        for row in rows
    ]


def _outlook_request_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        _job_row(
            source="outlook_sync_requests",
            source_id=_text(row.get("id")),
            job_type="outlook_sync_request",
            status=_text(row.get("status")) or "unknown",
            target=_text(row.get("profile_name")) or "Outlook request",
            root_name="Outlook COM",
            attempts=0,
            last_error=_text(row.get("error")),
            created_at=_text(row.get("created_at")),
            updated_at=_first_text(row.get("updated_at"), row.get("created_at")),
            completed_at=_text(row.get("completed_at")),
            details=_compact_details(row, "profile_name", "requested_by", "claimed_by", "result", "broker_message_id"),
        )
        for row in rows
    ]


def _runtime_control_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        _job_row(
            source="runtime_control_requests",
            source_id=_text(row.get("id")),
            job_type="runtime_control",
            status=_text(row.get("status")) or "unknown",
            target=_first_text(row.get("setting_key"), row.get("action")) or "runtime control",
            root_name="settings",
            attempts=0,
            last_error=_text(row.get("error")),
            created_at=_first_text(row.get("requested_at"), row.get("created_at")),
            updated_at=_first_text(row.get("updated_at"), row.get("acknowledged_at"), row.get("requested_at")),
            completed_at=_text(row.get("acknowledged_at")),
            details=_compact_details(row, "setting_key", "action", "affected_components", "actor", "metadata", "broker_message_id"),
        )
        for row in rows
    ]


def _operator_automation_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        _job_row(
            source="operator_automation_runs",
            source_id=_text(row.get("id")),
            job_type="operator_automation_run",
            status=_text(row.get("status")) or "unknown",
            target=_first_text(row.get("mode"), row.get("trigger")) or "operator automation",
            root_name="automation",
            attempts=0,
            last_error=_text(row.get("error")),
            created_at=_first_text(row.get("created_at"), row.get("started_at")),
            updated_at=_first_text(row.get("updated_at"), row.get("completed_at"), row.get("started_at")),
            started_at=_text(row.get("started_at")),
            completed_at=_text(row.get("completed_at")),
            details=_compact_details(row, "mode", "trigger", "actor", "summary", "broker_message_id"),
        )
        for row in rows
    ]


def _governance_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        _job_row(
            source="memory_governance_runs",
            source_id=_text(row.get("id")),
            job_type="governance_run",
            status=_text(row.get("status")) or "unknown",
            target=_first_text(row.get("mode"), row.get("trigger")) or "governance",
            root_name="governance",
            attempts=0,
            last_error=_text(row.get("error")),
            created_at=_text(row.get("created_at")),
            updated_at=_first_text(row.get("updated_at"), row.get("created_at")),
            completed_at=_text(row.get("updated_at")) if _status_group(_text(row.get("status"))) in {"blocked", "completed", "failed"} else None,
            details=_compact_details(row, "mode", "trigger", "actor", "summary", "gate", "broker_message_id"),
        )
        for row in rows
    ]


def _callback_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        _job_row(
            source="callback_deliveries",
            source_id=_text(row.get("id")),
            job_type="callback_delivery",
            status=_text(row.get("status")) or "unknown",
            target=_first_text(row.get("job_id"), row.get("message_id"), row.get("callback_url")) or "callback",
            root_name="callbacks",
            attempts=_int(row.get("attempts")),
            last_error=_text(row.get("last_error")),
            created_at=_text(row.get("created_at")),
            updated_at=_first_text(row.get("updated_at"), row.get("completed_at"), row.get("created_at")),
            completed_at=_text(row.get("completed_at")),
            details=_compact_details(row, "message_id", "job_id", "status_code", "last_status_code", "payload"),
        )
        for row in rows
    ]


def _gpu_lease_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        _job_row(
            source="gpu_leases",
            source_id=_text(row.get("id")),
            job_type="gpu_lease",
            status=_text(row.get("status")) or "unknown",
            target=" / ".join(item for item in [_text(row.get("task_type")), _text(row.get("model_id"))] if item) or "GPU lease",
            root_name="gpu",
            attempts=0,
            last_error=_text(row.get("error")),
            created_at=_text(row.get("created_at")),
            updated_at=_first_text(row.get("released_at"), row.get("heartbeat_at"), row.get("granted_at"), row.get("created_at")),
            started_at=_text(row.get("granted_at")),
            completed_at=_text(row.get("released_at")),
            details=_compact_details(row, "task_type", "model_id", "component", "request_id", "metadata"),
        )
        for row in rows
    ]


def _gpu_eviction_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    return [
        _job_row(
            source="gpu_evictions",
            source_id=_text(row.get("id")),
            job_type="gpu_eviction",
            status=_text(row.get("status")) or "unknown",
            target=" / ".join(item for item in [_text(row.get("task_type")), _text(row.get("model_id"))] if item) or "GPU eviction",
            root_name="gpu",
            attempts=_int(row.get("broker_delivery_count")),
            last_error=_text(row.get("error")),
            created_at=_text(row.get("created_at")),
            updated_at=_first_text(row.get("completed_at"), row.get("started_at"), row.get("queued_at"), row.get("created_at")),
            started_at=_text(row.get("started_at")),
            completed_at=_text(row.get("completed_at")),
            details=_compact_details(row, "lease_id", "task_type", "model_id", "component", "metadata", "broker_message_id"),
        )
        for row in rows
    ]


def _model_activity_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    output = []
    for row in rows:
        if not _is_internal_background_model_activity(row):
            continue
        output.append(
            _job_row(
                source="model_activity_events",
                source_id=_text(row.get("id")),
                job_type="model_activity",
                status=_text(row.get("status")) or "unknown",
                target=" / ".join(item for item in [_text(row.get("service")), _text(row.get("model"))] if item) or "model activity",
                root_name="models",
                attempts=0,
                last_error=_first_text(row.get("error_message"), row.get("error_class")),
                created_at=_text(row.get("started_at")),
                updated_at=_first_text(row.get("completed_at"), row.get("started_at")),
                started_at=_text(row.get("started_at")),
                completed_at=_text(row.get("completed_at")),
                progress=_first_text(row.get("activity_class"), row.get("action")),
                details=_compact_details(row, "service", "endpoint", "action", "activity_class", "caller_surface", "model", "duration_ms", "metadata"),
            )
        )
    return output


def _outbox_rows(
    rows: Iterable[dict[str, Any]],
    *,
    domain_keys: set[tuple[str, str]],
    broker_message_ids: set[str],
) -> list[dict[str, Any]]:
    output = []
    for row in rows:
        message_id = _text(row.get("message_id"))
        aggregate_type = _text(row.get("aggregate_type"))
        aggregate_id = _text(row.get("aggregate_id"))
        mapped_source = _DOMAIN_AGGREGATE_SOURCES.get(aggregate_type)
        if message_id and message_id in broker_message_ids:
            continue
        if mapped_source and aggregate_id and (mapped_source, aggregate_id) in domain_keys:
            continue
        source_id = _text(row.get("id")) or message_id or aggregate_id
        output.append(
            _job_row(
                source="message_outbox",
                source_id=source_id,
                job_type=_first_text(row.get("message_type"), row.get("routing_key")) or "message_outbox",
                status=_text(row.get("status")) or "unknown",
                target=_first_text(aggregate_id, row.get("routing_key"), message_id) or "queued command",
                root_name="messaging",
                attempts=_int(row.get("attempts")),
                last_error=_text(row.get("last_error")),
                created_at=_text(row.get("created_at")),
                updated_at=_first_text(row.get("updated_at"), row.get("created_at")),
                details=_compact_details(row, "message_id", "exchange", "routing_key", "message_type", "aggregate_type", "aggregate_id", "payload"),
            )
        )
    return output


def _inbox_rows(rows: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    output = []
    for row in rows:
        source_id = ":".join(item for item in [_text(row.get("consumer_name")), _text(row.get("message_id"))] if item)
        raw_status = _text(row.get("status")) or "unknown"
        metadata = _dict(row.get("metadata"))
        projected_status = _inbox_projected_status(raw_status, metadata)
        details = _compact_details(row, "consumer_name", "message_id", "message_type", "metadata")
        if _inbox_result_is_retryable(metadata):
            details["inbox_status"] = raw_status
        output.append(
            _job_row(
                source="message_inbox",
                source_id=source_id,
                job_type=_text(row.get("message_type")) or "message_inbox",
                status=projected_status,
                target=_first_text(row.get("consumer_name"), row.get("message_id")) or "message consumer",
                root_name="messaging",
                attempts=_int(row.get("attempts")),
                last_error=_text(row.get("last_error")),
                created_at=_text(row.get("first_seen_at")),
                updated_at=_first_text(row.get("last_seen_at"), row.get("handled_at"), row.get("first_seen_at")),
                completed_at=_text(row.get("handled_at")),
                details=details,
            )
        )
    return output


def _job_row(
    *,
    source: str,
    source_id: str,
    job_type: str,
    status: str,
    target: str,
    root_name: str,
    attempts: int,
    last_error: str | None,
    created_at: str | None,
    updated_at: str | None,
    started_at: str | None = None,
    completed_at: str | None = None,
    progress: str | None = None,
    details: dict[str, Any] | None = None,
) -> dict[str, Any]:
    clean_source_id = source_id or "unknown"
    clean_status = status or "unknown"
    clean_details = {key: value for key, value in (details or {}).items() if _is_present(value)}
    return {
        "id": f"{source}:{clean_source_id}",
        "source_id": clean_source_id,
        "job_source": source,
        "status": clean_status,
        "status_group": _status_group(clean_status),
        "job_type": job_type or source,
        "target": target or "-",
        "path": target or "-",
        "root_name": root_name or "-",
        "attempts": attempts,
        "last_error": last_error,
        "created_at": created_at,
        "updated_at": updated_at or completed_at or started_at or created_at,
        "started_at": started_at,
        "completed_at": completed_at,
        "progress": progress or "-",
        "details": clean_details,
    }


def _status_group(status: str | None) -> str:
    value = str(status or "").strip().lower()
    if value.startswith("blocked") or value in {"stranded_command"}:
        return "blocked"
    if value in {"failed", "stale_running", "timed_out", "rejected"}:
        return "failed"
    if value in {"running", "processing", "claimed", "publishing"}:
        return "running"
    if value in {"pending", "queued", "retrying", "waiting", "due", "retrying_locked", "retrying_vss_failed", "retrying_gpu_busy"}:
        return "pending"
    if value.startswith("retrying"):
        return "pending"
    return "completed"


def _inbox_projected_status(raw_status: str, metadata: dict[str, Any]) -> str:
    result = _inbox_metadata_result(metadata)
    if result.get("retryable") is True:
        return _first_text(result.get("status"), result.get("process_status")) or "retrying"
    return raw_status


def _inbox_result_is_retryable(metadata: dict[str, Any]) -> bool:
    return _inbox_metadata_result(metadata).get("retryable") is True


def _inbox_metadata_result(metadata: dict[str, Any]) -> dict[str, Any]:
    result = metadata.get("result")
    return result if isinstance(result, dict) else {}


def _filter_options(
    rows: list[dict[str, Any]],
    *,
    status: str | list[str] | tuple[str, ...] | None = None,
    root_name: str | list[str] | tuple[str, ...] | None = None,
    job_type: str | list[str] | tuple[str, ...] | None = None,
    job_source: str | list[str] | tuple[str, ...] | None = None,
    updated_from: str | None = None,
    updated_to: str | None = None,
) -> dict[str, list[str]]:
    return {
        "statuses": _facet_values(
            rows,
            "status",
            selected=status,
            status=None,
            root_name=root_name,
            job_type=job_type,
            job_source=job_source,
            updated_from=updated_from,
            updated_to=updated_to,
        ),
        "roots": _facet_values(
            rows,
            "root_name",
            selected=root_name,
            status=status,
            root_name=None,
            job_type=job_type,
            job_source=job_source,
            updated_from=updated_from,
            updated_to=updated_to,
            exclude_values={"-"},
        ),
        "job_types": _facet_values(
            rows,
            "job_type",
            selected=job_type,
            status=status,
            root_name=root_name,
            job_type=None,
            job_source=job_source,
            updated_from=updated_from,
            updated_to=updated_to,
        ),
        "sources": _facet_values(
            rows,
            "job_source",
            selected=job_source,
            status=status,
            root_name=root_name,
            job_type=job_type,
            job_source=None,
            updated_from=updated_from,
            updated_to=updated_to,
        ),
    }


def _facet_values(
    rows: list[dict[str, Any]],
    field: str,
    *,
    selected: str | list[str] | tuple[str, ...] | None,
    status: str | list[str] | tuple[str, ...] | None,
    root_name: str | list[str] | tuple[str, ...] | None,
    job_type: str | list[str] | tuple[str, ...] | None,
    job_source: str | list[str] | tuple[str, ...] | None,
    updated_from: str | None,
    updated_to: str | None,
    exclude_values: set[str] | None = None,
) -> list[str]:
    excluded = exclude_values or set()
    values = {
        str(row.get(field))
        for row in rows
        if _matches_filters(
            row,
            status=status,
            root_name=root_name,
            job_type=job_type,
            job_source=job_source,
            updated_from=updated_from,
            updated_to=updated_to,
        )
        and row.get(field)
        and str(row.get(field)) not in excluded
    }
    values.update(value for value in _filter_values(selected) if value not in excluded)
    return sorted(values)


def _matches_filters(
    row: dict[str, Any],
    *,
    status: str | list[str] | tuple[str, ...] | None,
    root_name: str | list[str] | tuple[str, ...] | None,
    job_type: str | list[str] | tuple[str, ...] | None,
    job_source: str | list[str] | tuple[str, ...] | None,
    updated_from: str | None,
    updated_to: str | None,
) -> bool:
    if not _matches_value(row.get("status"), status):
        return False
    if not _matches_value(row.get("root_name"), root_name):
        return False
    if not _matches_value(row.get("job_type"), job_type):
        return False
    if not _matches_value(row.get("job_source"), job_source):
        return False
    return _matches_time_window(row.get("updated_at"), updated_from=updated_from, updated_to=updated_to)


def _matches_value(value: Any, filters: str | list[str] | tuple[str, ...] | None) -> bool:
    values = _filter_values(filters)
    return not values or str(value or "") in values


def _filter_values(value: str | list[str] | tuple[str, ...] | None) -> set[str]:
    if value is None:
        return set()
    if isinstance(value, str):
        values = [value]
    else:
        values = list(value)
    return {str(item).strip() for item in values if str(item).strip()}


def _matches_time_window(value: Any, *, updated_from: str | None, updated_to: str | None) -> bool:
    current = _parse_time(value)
    start = _parse_time(updated_from)
    end = _parse_time(updated_to)
    if start and (not current or current < start):
        return False
    if end and (not current or current > end):
        return False
    return True


def _sort_key(row: dict[str, Any], sort_by: str | None) -> tuple[Any, str]:
    key = str(sort_by or "updated")
    if key == "status":
        return (str(row.get("status") or ""), str(row.get("id") or ""))
    if key == "job_type":
        return (str(row.get("job_type") or ""), str(row.get("id") or ""))
    if key == "target":
        return (str(row.get("target") or ""), str(row.get("id") or ""))
    if key == "root":
        return (str(row.get("root_name") or ""), str(row.get("id") or ""))
    if key == "attempts":
        return (_int(row.get("attempts")), str(row.get("id") or ""))
    if key == "progress":
        return (str(row.get("progress") or ""), str(row.get("id") or ""))
    if key == "last_error":
        return (str(row.get("last_error") or ""), str(row.get("id") or ""))
    return (_parse_time(row.get("updated_at")) or datetime.min, str(row.get("id") or ""))


def _sort_desc(sort_dir: str | None) -> bool:
    return str(sort_dir or "desc").strip().lower() != "asc"


def _domain_keys(rows: Iterable[dict[str, Any]]) -> set[tuple[str, str]]:
    keys: set[tuple[str, str]] = set()
    for row in rows:
        source = _text(row.get("job_source"))
        source_id = _text(row.get("source_id"))
        if source and source_id:
            keys.add((source, source_id))
        if source == "stranded_capture_commands" and source_id:
            keys.add(("capture_jobs", source_id))
    return keys


def _is_internal_background_model_activity(row: dict[str, Any]) -> bool:
    caller = str(row.get("caller_surface") or "").strip().lower()
    activity_class = str(row.get("activity_class") or "").strip().lower()
    if caller in _QUERY_MODEL_SURFACES:
        return False
    if activity_class in _QUERY_MODEL_CLASSES:
        return False
    return True


def _progress(row: dict[str, Any], telemetry: dict[str, Any]) -> str | None:
    blocked = _blocked_progress(row, telemetry)
    if blocked:
        return blocked
    return _first_text(
        telemetry.get("progress_label"),
        row.get("progress"),
        telemetry.get("stage"),
        telemetry.get("progress_percent"),
    )


def _blocked_progress(row: dict[str, Any], telemetry: dict[str, Any]) -> str | None:
    status = _text(row.get("status"))
    if not status.startswith("blocked_"):
        return None
    if _is_asr_gpu_capacity_block(row, telemetry):
        return "Blocked: ASR GPU capacity"
    if status == "blocked_missing_dependency":
        return "Missing dependency"
    return status.replace("_", " ").title()


def _is_asr_gpu_capacity_block(row: dict[str, Any], telemetry: dict[str, Any]) -> bool:
    if _text(row.get("job_type")) != "corpus_extract_media_segment":
        return False
    haystack = " ".join(
        [
            _text(row.get("last_error")),
            _text(telemetry.get("error")),
            _text(telemetry.get("error_type")),
            _text(telemetry.get("result_status")),
            json.dumps(telemetry, default=str),
        ]
    ).lower()
    return "vram_budget_exceeded" in haystack or ("asr" in haystack and "vram" in haystack)


def _mail_progress(row: dict[str, Any]) -> str | None:
    seen = row.get("messages_seen")
    exported = row.get("messages_exported")
    if seen is not None or exported is not None:
        return f"{_int(exported)} exported / {_int(seen)} seen"
    return None


def _compact_details(row: dict[str, Any], *keys: str) -> dict[str, Any]:
    return {key: row.get(key) for key in keys if _is_present(row.get(key))}


def _is_present(value: Any) -> bool:
    if value is None:
        return False
    if isinstance(value, str):
        return bool(value)
    if isinstance(value, (dict, list, tuple, set)):
        return bool(value)
    return True


def _safe_source(fetch: Callable[[], Any], fallback: list[dict[str, Any]]) -> list[dict[str, Any]]:
    try:
        value = fetch()
    except Exception:
        return fallback
    if not isinstance(value, list):
        return fallback
    return [item for item in value if isinstance(item, dict)]


def _call_if_available(name: str, **kwargs: Any) -> Any:
    func = getattr(database, name, None)
    if not callable(func):
        return []
    return func(**kwargs)


def _bounded_limit(value: int | str | None) -> int:
    try:
        numeric = int(value if value is not None else 50)
    except (TypeError, ValueError):
        numeric = 50
    return max(1, min(numeric, 200))


def _bounded_offset(value: int | str | None) -> int:
    try:
        numeric = int(value if value is not None else 0)
    except (TypeError, ValueError):
        numeric = 0
    return max(0, numeric)


def _parse_time(value: Any) -> datetime | None:
    if isinstance(value, datetime):
        return value.replace(tzinfo=None)
    text = _text(value)
    if not text:
        return None
    try:
        return datetime.fromisoformat(text.replace("Z", "+00:00")).replace(tzinfo=None)
    except ValueError:
        return None


def _dict(value: Any) -> dict[str, Any]:
    return value if isinstance(value, dict) else {}


def _int(value: Any) -> int:
    try:
        return int(value or 0)
    except (TypeError, ValueError):
        return 0


def _text(value: Any) -> str:
    if value is None:
        return ""
    return str(value)


def _first_text(*values: Any) -> str:
    for value in values:
        text = _text(value).strip()
        if text:
            return text
    return ""
