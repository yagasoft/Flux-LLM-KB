from __future__ import annotations

import asyncio
from collections.abc import AsyncIterator
from datetime import UTC, datetime
from itertools import count
import json
from typing import Any


DASHBOARD_SECTIONS = ("health", "crawl", "jobs", "retrieval", "modelActivity", "mail", "outlook", "settings")

_SEQUENCE = count(1)


def collect_dashboard_snapshot(
    *,
    jobs: dict[str, Any] | None = None,
    model_activity: dict[str, Any] | None = None,
) -> dict[str, Any]:
    from .health import (
        collect_crawl_payload,
        collect_dashboard_jobs_payload,
        collect_dashboard_payload,
        collect_retrieval_payload,
    )
    from .model_activity import collect_model_activity_payload

    jobs_options = dict(jobs or {})
    model_options = dict(model_activity or {})
    return {
        "generated_at": _utc_now_iso(),
        "health": _safe_call(collect_dashboard_payload, {}),
        "crawl": _safe_call(collect_crawl_payload, {"roots": [], "root_summaries": [], "status": {}, "watchers": [], "recent_errors": []}),
        "jobs": _safe_call(lambda: collect_dashboard_jobs_payload(**jobs_options), {"jobs": [], "count": 0, "limit": jobs_options.get("limit", 50), "offset": jobs_options.get("offset", 0), "has_next": False}),
        "retrieval": _safe_call(collect_retrieval_payload, {}),
        "modelActivity": _safe_call(lambda: collect_model_activity_payload(**model_options), {"events": [], "active_count": 0, "recent_count": 0}),
        "mail": _safe_call(_mail_status, {"profiles": []}),
        "outlook": _safe_call(_outlook_status, {"profiles": [], "pending_requests": []}),
        "settings": _safe_call(_settings_list, []),
    }


def collect_dashboard_section(section: str, *, subscription: dict[str, Any] | None = None) -> Any:
    if section not in DASHBOARD_SECTIONS:
        raise ValueError(f"unsupported dashboard section: {section}")
    options = subscription or {}
    snapshot = collect_dashboard_snapshot(
        jobs=options.get("jobs") if isinstance(options.get("jobs"), dict) else None,
        model_activity=options.get("model_activity") if isinstance(options.get("model_activity"), dict) else None,
    )
    return snapshot[section]


def subscription_from_client(message: dict[str, Any]) -> dict[str, Any]:
    sections = message.get("sections")
    if isinstance(sections, list):
        safe_sections = [str(section) for section in sections if str(section) in DASHBOARD_SECTIONS]
    else:
        safe_sections = list(DASHBOARD_SECTIONS)
    if not safe_sections:
        safe_sections = list(DASHBOARD_SECTIONS)
    return {
        "sections": safe_sections,
        "active_tab": str(message.get("activeTab") or message.get("active_tab") or ""),
        "jobs": _jobs_options(message.get("jobs") if isinstance(message.get("jobs"), dict) else {}),
        "model_activity": _model_activity_options(message.get("modelActivity") if isinstance(message.get("modelActivity"), dict) else {}),
    }


def connected_message(stream: dict[str, Any] | None = None) -> dict[str, Any]:
    return {
        "type": "dashboard.connected",
        "sequence": next_sequence(),
        "generated_at": _utc_now_iso(),
        "stream": stream or stream_broker_status(),
    }


def snapshot_message(snapshot: dict[str, Any]) -> dict[str, Any]:
    return {
        "type": "dashboard.snapshot",
        "sequence": next_sequence(),
        "generated_at": snapshot.get("generated_at") or _utc_now_iso(),
        "payload": snapshot,
    }


def section_message(section: str, payload: Any, *, reason: str = "subscribe") -> dict[str, Any]:
    return {
        "type": "dashboard.section",
        "section": section,
        "payload": payload,
        "reason": reason,
        "sequence": next_sequence(),
        "generated_at": _utc_now_iso(),
    }


def event_message(*, section: str, reason: str, event: dict[str, Any] | None = None) -> dict[str, Any]:
    safe_event = _dashboard_event_payload(event or {})
    return {
        "type": "dashboard.event",
        "section": section,
        "reason": str(reason or "changed"),
        "message": _dashboard_event_summary(safe_event, fallback=str(reason or "changed")),
        "payload": safe_event,
        "sequence": next_sequence(),
        "generated_at": _utc_now_iso(),
    }


def error_message(message: str, *, code: str = "dashboard.stream_error", retryable: bool = True) -> dict[str, Any]:
    return {
        "type": "dashboard.error",
        "code": code,
        "message": message,
        "retryable": retryable,
        "sequence": next_sequence(),
        "generated_at": _utc_now_iso(),
    }


def stream_broker_status() -> dict[str, Any]:
    try:
        from . import messaging

        status = messaging.management_queue_status(timeout_seconds=0.2)
        if isinstance(status, dict) and status.get("available"):
            return {"status": "ok", "rabbitmq": True}
        return {"status": "degraded", "rabbitmq": False, "reason": "rabbitmq unavailable"}
    except Exception:
        return {"status": "degraded", "rabbitmq": False, "reason": "rabbitmq unavailable"}


def emit_dashboard_change(*, section: str, reason: str, event: dict[str, Any] | None = None) -> None:
    if section not in DASHBOARD_SECTIONS:
        return
    payload = {
        "section": section,
        "reason": str(reason or "changed"),
        "event": _json_safe(event or {}),
        "generated_at": _utc_now_iso(),
    }
    try:
        from . import database, messaging

        database.enqueue_message_outbox(
            exchange=messaging.EVENTS_EXCHANGE,
            routing_key=f"dashboard.{section}.changed",
            message_type="dashboard.section.changed",
            payload=payload,
            aggregate_type="dashboard",
            aggregate_id=section,
        )
    except Exception:
        pass


async def rabbitmq_dashboard_messages(
    subscription: dict[str, Any],
    *,
    debounce_seconds: float = 0.2,
) -> AsyncIterator[dict[str, Any]]:
    from . import messaging

    aio_pika = messaging._load_aio_pika()
    connection = await aio_pika.connect_robust(messaging.RabbitMqConfig.from_env().url)
    try:
        channel = await connection.channel()
        await channel.set_qos(prefetch_count=16)
        exchange_type = getattr(aio_pika.ExchangeType, "TOPIC", None)
        exchange = await channel.declare_exchange(messaging.EVENTS_EXCHANGE, exchange_type, durable=True)
        queue = await channel.declare_queue("", durable=False, exclusive=True, auto_delete=True)
        await queue.bind(exchange, routing_key="#")

        async with queue.iterator() as iterator:
            while True:
                incoming = await iterator.__anext__()
                pending_reasons: dict[str, str] = {}
                pending_events: list[dict[str, Any]] = []
                await _record_incoming_dashboard_event(
                    incoming,
                    subscription=subscription,
                    pending_reasons=pending_reasons,
                    pending_events=pending_events,
                )

                deadline = asyncio.get_running_loop().time() + max(0.0, debounce_seconds)
                while True:
                    remaining = deadline - asyncio.get_running_loop().time()
                    if remaining <= 0:
                        break
                    try:
                        next_incoming = await asyncio.wait_for(iterator.__anext__(), timeout=remaining)
                    except asyncio.TimeoutError:
                        break
                    await _record_incoming_dashboard_event(
                        next_incoming,
                        subscription=subscription,
                        pending_reasons=pending_reasons,
                        pending_events=pending_events,
                    )

                dashboard_payload = collect_dashboard_snapshot(
                    jobs=subscription.get("jobs") if isinstance(subscription.get("jobs"), dict) else None,
                    model_activity=subscription.get("model_activity") if isinstance(subscription.get("model_activity"), dict) else None,
                )
                for section, reason in pending_reasons.items():
                    if section in dashboard_payload:
                        yield section_message(section, dashboard_payload[section], reason=reason)
                for event in pending_events[:8]:
                    yield event_message(
                        section=str(event.get("section") or "health"),
                        reason=str(event.get("reason") or "changed"),
                        event=event,
                    )
    finally:
        await connection.close()


def dashboard_sections_for_event(message: Any) -> tuple[tuple[str, str], ...]:
    payload = getattr(message, "payload", {}) if message is not None else {}
    if not isinstance(payload, dict):
        payload = {}
    routing_key = str(getattr(message, "routing_key", "") or "")
    message_type = str(getattr(message, "message_type", "") or "")
    reason = str(payload.get("reason") or routing_key or message_type or "changed")
    explicit_section = _section_name(payload.get("section"))
    if routing_key.startswith("dashboard.") and explicit_section:
        return ((explicit_section, reason),)

    key_text = f"{routing_key} {message_type}".lower()
    sections: list[tuple[str, str]] = []
    if "model_activity" in key_text or "modelactivity" in key_text or "gpu.eviction" in key_text:
        sections.append(("modelActivity", reason))
    if "outlook" in key_text:
        sections.extend([("outlook", reason), ("jobs", reason), ("health", reason)])
    elif "mail" in key_text or "imap" in key_text:
        sections.extend([("mail", reason), ("jobs", reason), ("health", reason)])
    if "setting" in key_text or "runtime_control" in key_text or "runtime.control" in key_text:
        sections.extend([("settings", reason), ("jobs", reason), ("health", reason)])
    if "search_index" in key_text or "search-index" in key_text or "retrieval" in key_text:
        sections.extend([("retrieval", reason), ("jobs", reason), ("health", reason)])
    if "corpus" in key_text or "crawl" in key_text or "capture_job" in key_text or ".job." in key_text:
        sections.extend([("crawl", reason), ("jobs", reason), ("health", reason)])
    if "automation" in key_text or "governance" in key_text or "callback" in key_text:
        sections.extend([("jobs", reason), ("health", reason)])
    return _dedupe_section_reasons(sections)


def next_sequence() -> int:
    return next(_SEQUENCE)


def _jobs_options(options: dict[str, Any]) -> dict[str, Any]:
    return {
        "limit": options.get("limit", 50),
        "offset": options.get("offset", 0),
        "status": options.get("status"),
        "root_name": options.get("root_name") or options.get("rootName"),
        "job_type": options.get("job_type") or options.get("jobType"),
        "job_source": options.get("job_source") or options.get("jobSource"),
        "updated_from": options.get("updated_from") or options.get("updatedFrom"),
        "updated_to": options.get("updated_to") or options.get("updatedTo"),
        "sort_by": options.get("sort_by") or options.get("sortBy") or "updated",
        "sort_dir": options.get("sort_dir") or options.get("sortDir") or "desc",
    }


def _model_activity_options(options: dict[str, Any]) -> dict[str, Any]:
    return {
        "window_minutes": options.get("window_minutes") or options.get("windowMinutes") or 60,
        "limit": options.get("limit", 50),
        "offset": options.get("offset", 0),
        "include_control_plane": bool(options.get("include_control_plane") or options.get("includeControlPlane") or False),
    }


def _mail_status() -> dict[str, Any]:
    from .mail_ingestion import mail_status

    return mail_status()


def _outlook_status() -> dict[str, Any]:
    from .outlook_host import status

    return status()


def _settings_list() -> list[dict[str, Any]]:
    from .settings import SettingsService

    return SettingsService().public_list()


def _safe_call(callable_obj, fallback):
    try:
        return callable_obj()
    except Exception:
        return fallback


def _json_safe(value: Any) -> Any:
    try:
        return json.loads(json.dumps(value, default=str))
    except Exception:
        return {}


def _utc_now_iso() -> str:
    return datetime.now(UTC).isoformat()


async def _record_incoming_dashboard_event(
    incoming: Any,
    *,
    subscription: dict[str, Any],
    pending_reasons: dict[str, str],
    pending_events: list[dict[str, Any]],
) -> None:
    try:
        payload = json.loads(incoming.body.decode("utf-8"))
        from . import messaging

        message = messaging.FluxMessage.model_validate(payload)
        sections = dashboard_sections_for_event(message)
        subscribed = set(subscription.get("sections") or DASHBOARD_SECTIONS)
        for section, reason in sections:
            if section in subscribed:
                pending_reasons[section] = reason
        if sections:
            event_payload = _flux_event_payload(message, sections[0][0], sections[0][1])
            if event_payload:
                pending_events.append(event_payload)
    except Exception:
        return
    finally:
        await _ack_incoming(incoming)


async def _ack_incoming(incoming: Any) -> None:
    ack = getattr(incoming, "ack", None)
    if ack is None:
        return
    try:
        result = ack()
        if asyncio.iscoroutine(result):
            await result
    except Exception:
        return


def _flux_event_payload(message: Any, section: str, reason: str) -> dict[str, Any]:
    payload = getattr(message, "payload", {}) if message is not None else {}
    if not isinstance(payload, dict):
        payload = {}
    explicit_event = payload.get("event") if isinstance(payload.get("event"), dict) else {}
    source = explicit_event or payload
    safe_event = _dashboard_event_payload(source)
    safe_event.update(
        {
            "section": section,
            "reason": reason,
            "message_id": str(getattr(message, "message_id", "") or ""),
            "message_type": str(getattr(message, "message_type", "") or ""),
            "routing_key": str(getattr(message, "routing_key", "") or ""),
        }
    )
    return safe_event


def _dashboard_event_payload(event: dict[str, Any]) -> dict[str, Any]:
    safe = _json_safe(event)
    if not isinstance(safe, dict):
        return {}
    allowed_keys = {
        "activity_class",
        "action",
        "component",
        "endpoint",
        "event_id",
        "job_id",
        "key",
        "message_id",
        "message_type",
        "model",
        "profile_name",
        "reason",
        "recovered",
        "request_id",
        "routing_key",
        "run_id",
        "section",
        "service",
        "status",
    }
    result: dict[str, Any] = {}
    for key, value in safe.items():
        if str(key) not in allowed_keys:
            continue
        if isinstance(value, str):
            result[str(key)] = value[:240]
        elif isinstance(value, bool) or isinstance(value, int | float) or value is None:
            result[str(key)] = value
        else:
            result[str(key)] = str(value)[:240]
    return result


def _dashboard_event_summary(event: dict[str, Any], *, fallback: str) -> str:
    for key in ("job_id", "event_id", "profile_name", "request_id", "key", "service", "component"):
        value = str(event.get(key) or "").strip()
        if value:
            status = str(event.get("status") or event.get("reason") or fallback).strip()
            return f"{value}: {status}" if status else value
    return str(event.get("reason") or fallback)


def _section_name(value: Any) -> str | None:
    text = str(value or "").strip()
    return text if text in DASHBOARD_SECTIONS else None


def _dedupe_section_reasons(sections: list[tuple[str, str]]) -> tuple[tuple[str, str], ...]:
    seen: set[str] = set()
    result: list[tuple[str, str]] = []
    for section, reason in sections:
        if section in seen or section not in DASHBOARD_SECTIONS:
            continue
        seen.add(section)
        result.append((section, reason))
    return tuple(result)
