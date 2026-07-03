from __future__ import annotations

from contextlib import contextmanager
from contextvars import ContextVar
from datetime import UTC, datetime, timedelta
import re
import time
from typing import Any, Iterator

from . import database
from .error_diagnostics import redact_secrets
from .gpu_scheduler import get_gpu_scheduler


ALLOWED_STATUSES = {"running", "completed", "failed", "busy", "stale_running"}
ALLOWED_ACTIVITY_CLASSES = {"retrieval", "vision_ocr", "sidecar", "health", "control_plane", "model_loading"}
CONTROL_PLANE_ACTIVITY_CLASSES = {"health", "control_plane"}
ALLOWED_METADATA_KEYS = {
    "batch_size",
    "component",
    "dimensions",
    "document",
    "duration_hint_ms",
    "input_count",
    "keep_alive",
    "passage_count",
    "quantization",
    "resident",
    "route",
    "task_type",
}
DEFAULT_WINDOW_MINUTES = 60
MIN_WINDOW_MINUTES = 5
MAX_WINDOW_MINUTES = 360
DEFAULT_LIMIT = 50
MAX_LIMIT = 200
RETENTION_HOURS = 24
STALE_RUNNING_AFTER_MINUTES = 60

_CALLER_SURFACE: ContextVar[str] = ContextVar("flux_model_activity_caller_surface", default="")
_PATH_RE = re.compile(r"(?<!\w)(?:[A-Za-z]:[\\/]|/)[^\s,;]+")


@contextmanager
def caller_surface(surface: str) -> Iterator[None]:
    token = _CALLER_SURFACE.set(_safe_label(surface, max_length=40))
    try:
        yield
    finally:
        _CALLER_SURFACE.reset(token)


@contextmanager
def record_model_activity(
    *,
    service: str,
    endpoint: str,
    action: str,
    activity_class: str,
    caller_surface: str | None = None,
    model: str | None = None,
    metadata: dict[str, Any] | None = None,
) -> Iterator[str | None]:
    started = time.monotonic()
    event_id = _safe_start_event(
        service=service,
        endpoint=endpoint,
        action=action,
        activity_class=activity_class,
        caller_surface=caller_surface if caller_surface is not None else _CALLER_SURFACE.get(),
        model=model or "",
        metadata=metadata or {},
    )
    try:
        yield event_id
    except Exception as exc:
        _safe_finish_event(
            event_id,
            status=_status_for_exception(exc),
            duration_ms=_duration_ms(started),
            error_class=exc.__class__.__name__,
            error_message=_sanitize_error_message(str(exc)),
        )
        raise
    else:
        _safe_finish_event(
            event_id,
            status="completed",
            duration_ms=_duration_ms(started),
            error_class=None,
            error_message=None,
        )


def collect_model_activity_payload(
    window_minutes: int | str | None = DEFAULT_WINDOW_MINUTES,
    limit: int | str | None = DEFAULT_LIMIT,
    offset: int | str | None = 0,
    *,
    include_control_plane: bool = False,
) -> dict[str, Any]:
    safe_window = bounded_window_minutes(window_minutes)
    safe_limit = bounded_limit(limit)
    safe_offset = bounded_offset(offset)
    try:
        database.prune_model_activity_events(retention_hours=RETENTION_HOURS)
    except Exception:
        pass
    try:
        rows = database.list_model_activity_events(
            window_minutes=safe_window,
            limit=safe_limit,
            offset=safe_offset,
            include_control_plane=include_control_plane,
        )
    except Exception:
        rows = []
    try:
        total_count = int(
            database.count_model_activity_events(
                window_minutes=safe_window,
                include_control_plane=include_control_plane,
            )
        )
    except Exception:
        total_count = safe_offset + len(rows)
    now = _utc_now()
    events = sorted([_event_payload(row, now=now) for row in rows], key=_event_sort_key, reverse=True)
    if not include_control_plane:
        events = [
            item
            for item in events
            if str(item.get("activity_class") or "").lower() not in CONTROL_PLANE_ACTIVITY_CLASSES
        ]
        total_count = max(total_count, len(events))
    active_count = sum(1 for item in events if item["status"] == "running")
    last_event_at = _latest_iso(
        [
            item.get("completed_at") or item.get("started_at")
            for item in events
            if item.get("completed_at") or item.get("started_at")
        ]
    )
    page_count = (total_count + safe_limit - 1) // safe_limit if total_count > 0 else 0
    return {
        "window_minutes": safe_window,
        "limit": safe_limit,
        "offset": safe_offset,
        "total_count": total_count,
        "has_next": safe_offset + len(events) < total_count,
        "page_count": page_count,
        "active_count": active_count,
        "recent_count": len(events),
        "last_event_at": last_event_at,
        "service_breakdown": _service_breakdown(events),
        "class_breakdown": _class_breakdown(events),
        "events": events,
        "scheduler": _scheduler_summary(now=now, event_last_at=last_event_at),
    }


def bounded_window_minutes(value: int | str | None) -> int:
    try:
        numeric = int(value if value is not None else DEFAULT_WINDOW_MINUTES)
    except (TypeError, ValueError):
        numeric = DEFAULT_WINDOW_MINUTES
    return max(MIN_WINDOW_MINUTES, min(numeric, MAX_WINDOW_MINUTES))


def bounded_limit(value: int | str | None) -> int:
    try:
        numeric = int(value if value is not None else DEFAULT_LIMIT)
    except (TypeError, ValueError):
        numeric = DEFAULT_LIMIT
    return max(1, min(numeric, MAX_LIMIT))


def bounded_offset(value: int | str | None) -> int:
    try:
        numeric = int(value if value is not None else 0)
    except (TypeError, ValueError):
        numeric = 0
    return max(0, numeric)


def sanitize_metadata(value: dict[str, Any]) -> dict[str, Any]:
    sanitized: dict[str, Any] = {}
    for key, item in dict(value or {}).items():
        key_text = str(key)
        if key_text not in ALLOWED_METADATA_KEYS:
            continue
        safe = _safe_metadata_value(item)
        if safe is not None:
            sanitized[key_text] = safe
    return sanitized


def _safe_start_event(**kwargs: Any) -> str | None:
    try:
        return database.start_model_activity_event(
            service=_safe_label(kwargs.get("service"), max_length=80) or "unknown",
            endpoint=_safe_endpoint(kwargs.get("endpoint")),
            action=_safe_label(kwargs.get("action"), max_length=80),
            activity_class=_safe_activity_class(kwargs.get("activity_class")),
            caller_surface=_safe_label(kwargs.get("caller_surface"), max_length=40),
            model=_safe_label(kwargs.get("model"), max_length=200),
            metadata=sanitize_metadata(kwargs.get("metadata") if isinstance(kwargs.get("metadata"), dict) else {}),
        )
    except Exception:
        return None


def _safe_finish_event(
    event_id: str | None,
    *,
    status: str,
    duration_ms: int | None,
    error_class: str | None,
    error_message: str | None,
) -> None:
    if not event_id:
        return
    try:
        database.finish_model_activity_event(
            event_id=event_id,
            status=_safe_status(status),
            duration_ms=duration_ms,
            error_class=_safe_label(error_class, max_length=120) or None,
            error_message=_sanitize_error_message(error_message) if error_message else None,
        )
    except Exception:
        pass


def _event_payload(row: dict[str, Any], *, now: datetime) -> dict[str, Any]:
    started = _coerce_datetime(row.get("started_at"))
    completed = _coerce_datetime(row.get("completed_at"))
    status = _safe_status(row.get("status"))
    if status == "running" and started and started < now - timedelta(minutes=STALE_RUNNING_AFTER_MINUTES):
        status = "stale_running"
    return {
        "id": str(row.get("id") or ""),
        "service": _safe_label(row.get("service"), max_length=80) or "unknown",
        "endpoint": _safe_endpoint(row.get("endpoint")),
        "action": _safe_label(row.get("action"), max_length=80),
        "activity_class": _safe_activity_class(row.get("activity_class")),
        "caller_surface": _safe_label(row.get("caller_surface"), max_length=40),
        "model": _safe_label(row.get("model"), max_length=200),
        "status": status,
        "started_at": _iso_or_none(started),
        "completed_at": _iso_or_none(completed),
        "duration_ms": _non_negative_int(row.get("duration_ms")),
        "error_class": _safe_label(row.get("error_class"), max_length=120) or None,
        "error_message": _sanitize_error_message(row.get("error_message")) if row.get("error_message") else None,
        "metadata": sanitize_metadata(row.get("metadata") if isinstance(row.get("metadata"), dict) else {}),
    }


def _event_sort_key(event: dict[str, Any]) -> tuple[str, str, str]:
    completed_or_started = str(event.get("completed_at") or event.get("started_at") or "")
    started = str(event.get("started_at") or "")
    event_id = str(event.get("id") or "")
    return (completed_or_started, started, event_id)


def _service_breakdown(events: list[dict[str, Any]]) -> list[dict[str, Any]]:
    rows: dict[str, dict[str, int]] = {}
    for item in events:
        service = str(item.get("service") or "unknown")
        row = rows.setdefault(service, {"count": 0, "active": 0, "failures": 0})
        row["count"] += 1
        if item.get("status") == "running":
            row["active"] += 1
        if item.get("status") == "failed":
            row["failures"] += 1
    return [
        {"service": service, **values}
        for service, values in sorted(rows.items(), key=lambda item: (-item[1]["count"], item[0]))
    ]


def _class_breakdown(events: list[dict[str, Any]]) -> list[dict[str, Any]]:
    counts: dict[str, int] = {}
    for item in events:
        activity_class = str(item.get("activity_class") or "sidecar")
        counts[activity_class] = counts.get(activity_class, 0) + 1
    return [
        {"activity_class": key, "count": value}
        for key, value in sorted(counts.items(), key=lambda item: (-item[1], item[0]))
    ]


def _scheduler_summary(*, now: datetime, event_last_at: str | None) -> dict[str, Any]:
    try:
        status = get_gpu_scheduler().status()
    except Exception:
        return _unavailable_scheduler_summary()
    running = _list_of_dicts(status.get("running"))
    waiting = _list_of_dicts(status.get("waiting"))
    recent = _list_of_dicts(status.get("recent"))
    evictions = status.get("evictions") if isinstance(status.get("evictions"), dict) else {}
    eviction_recent = _list_of_dicts(evictions.get("recent") if isinstance(evictions, dict) else None)
    resident_models = _resident_models(status.get("model_residency"))
    last_eviction_at = _latest_iso([_time_value(item.get("completed_at") or item.get("created_at")) for item in eviction_recent])
    last_activity_at = _latest_iso(
        [
            event_last_at,
            *[_time_value(item.get("released_at") or item.get("heartbeat_at") or item.get("granted_at") or item.get("created_at")) for item in running + waiting + recent],
            *[_time_value(item.get("last_used_at")) for item in _list_of_dicts(status.get("model_residency"))],
            last_eviction_at,
        ]
    )
    return {
        "mode": str(status.get("mode") or "unknown"),
        "running_count": len(running),
        "waiting_count": len(waiting),
        "recent_count": len(recent),
        "rejections": _non_negative_int(status.get("rejections")) or 0,
        "timeouts": _non_negative_int(status.get("timeouts")) or 0,
        "evictions_recent_count": len(eviction_recent),
        "last_eviction_at": last_eviction_at,
        "oldest_wait_age_ms": _oldest_wait_age_ms(waiting, now=now),
        "last_activity_at": last_activity_at,
        "resident_models": resident_models,
        "live_gpu_memory": _live_gpu_memory_summary(status.get("live_gpu_memory")),
    }


def _unavailable_scheduler_summary() -> dict[str, Any]:
    return {
        "mode": "unavailable",
        "running_count": 0,
        "waiting_count": 0,
        "recent_count": 0,
        "rejections": 0,
        "timeouts": 0,
        "evictions_recent_count": 0,
        "last_eviction_at": None,
        "oldest_wait_age_ms": None,
        "last_activity_at": None,
        "resident_models": [],
        "live_gpu_memory": {"available": False, "used_mb": None, "total_mb": None},
    }


def _resident_models(value: Any) -> list[dict[str, Any]]:
    rows = []
    for item in _list_of_dicts(value):
        if item.get("resident") is False:
            continue
        rows.append(
            {
                "service": _safe_label(item.get("component") or _service_for_task_type(str(item.get("task_type") or "")), max_length=80),
                "model": _safe_label(item.get("model_id"), max_length=200),
                "task_type": _safe_label(item.get("task_type"), max_length=80),
                "last_used_at": _iso_or_none(_coerce_datetime(item.get("last_used_at"))),
            }
        )
    return rows[:20]


def _live_gpu_memory_summary(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        return {"available": False, "used_mb": None, "total_mb": None}
    if {"available", "used_mb", "total_mb"}.issubset(value):
        return {
            "available": bool(value.get("available")),
            "used_mb": _non_negative_int(value.get("used_mb")),
            "total_mb": _non_negative_int(value.get("total_mb")),
        }
    gpus = value.get("gpus")
    if not isinstance(gpus, list) or not gpus or not value.get("ok", False):
        return {"available": False, "used_mb": None, "total_mb": None}
    used = 0
    total = 0
    for gpu in gpus:
        if not isinstance(gpu, dict):
            continue
        used += _non_negative_int(gpu.get("memory_used_mb")) or 0
        total += _non_negative_int(gpu.get("memory_total_mb")) or 0
    return {"available": total > 0, "used_mb": used if total > 0 else None, "total_mb": total if total > 0 else None}


def _oldest_wait_age_ms(waiting: list[dict[str, Any]], *, now: datetime) -> int | None:
    created = [_coerce_datetime(item.get("created_at")) for item in waiting]
    values = [item for item in created if item is not None]
    if not values:
        return None
    return max(0, int((now - min(values)).total_seconds() * 1000))


def _status_for_exception(exc: Exception) -> str:
    name = exc.__class__.__name__
    if name in {"ModelRunnerBusy", "GpuLeaseRejected", "GpuLeaseTimeout"}:
        return "busy"
    return "failed"


def _duration_ms(started: float) -> int:
    return max(0, int((time.monotonic() - started) * 1000))


def _sanitize_error_message(value: Any) -> str:
    redacted = redact_secrets(str(value or "")) or ""
    redacted = _PATH_RE.sub("[REDACTED:path]", redacted)
    redacted = re.sub(r"(?i)\b(password|secret|token|credential|authorization)\b", "[REDACTED]", redacted)
    return redacted[:300]


def _safe_metadata_value(value: Any) -> Any:
    if value is None or isinstance(value, bool):
        return value
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        return round(value, 6)
    if isinstance(value, str):
        return _safe_label(value, max_length=120)
    return None


def _safe_status(value: Any) -> str:
    normalized = str(value or "running").strip().lower()
    return normalized if normalized in ALLOWED_STATUSES else "failed"


def _safe_activity_class(value: Any) -> str:
    normalized = str(value or "sidecar").strip().lower()
    return normalized if normalized in ALLOWED_ACTIVITY_CLASSES else "sidecar"


def _safe_endpoint(value: Any) -> str:
    text = str(value or "").strip()
    if not text:
        return ""
    if "?" in text:
        text = text.split("?", 1)[0]
    return text[:120]


def _safe_label(value: Any, *, max_length: int) -> str:
    return str(value or "").strip()[:max_length]


def _service_for_task_type(task_type: str) -> str:
    if task_type in {"embedding", "rerank"}:
        return "model-runner"
    if task_type in {"ocr_image", "ocr_document"}:
        return "paddle-runner"
    if task_type == "ollama_vision":
        return "ollama"
    if task_type == "asr":
        return "asr"
    return ""


def _non_negative_int(value: Any) -> int | None:
    try:
        if value is None:
            return None
        return max(0, int(float(value)))
    except (TypeError, ValueError):
        return None


def _list_of_dicts(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        return []
    return [dict(item) for item in value if isinstance(item, dict)]


def _time_value(value: Any) -> str | None:
    return _iso_or_none(_coerce_datetime(value))


def _latest_iso(values: list[Any]) -> str | None:
    parsed = [_coerce_datetime(value) for value in values if value]
    parsed = [value for value in parsed if value is not None]
    if not parsed:
        return None
    return max(parsed).isoformat()


def _iso_or_none(value: datetime | None) -> str | None:
    return value.isoformat() if value else None


def _coerce_datetime(value: Any) -> datetime | None:
    if value is None:
        return None
    if isinstance(value, datetime):
        return value if value.tzinfo else value.replace(tzinfo=UTC)
    if isinstance(value, (int, float)):
        return datetime.fromtimestamp(float(value), tz=UTC)
    if isinstance(value, str):
        text = value.strip()
        if not text:
            return None
        try:
            parsed = datetime.fromisoformat(text.replace("Z", "+00:00"))
            return parsed if parsed.tzinfo else parsed.replace(tzinfo=UTC)
        except ValueError:
            return None
    return None


def _utc_now() -> datetime:
    return datetime.now(UTC)
