from __future__ import annotations

import platform
import time
import traceback
from datetime import datetime, timezone
from typing import Any

from . import database
from .mail_ingestion import sync_outlook_profile


DEFAULT_HOST_ID = "default"
HOST_HEARTBEAT_STALE_AFTER_SECONDS = 120


def status(*, host_id: str = DEFAULT_HOST_ID) -> dict[str, Any]:
    host = database.get_outlook_host_state(host_id=host_id)
    if host is None:
        host = {
            "host_id": host_id,
            "status": "host_offline",
            "command": "flux-kb outlook-host run",
            "heartbeat_at": None,
            "last_error": None,
            "metadata": {},
        }
    else:
        host = _normalize_host_status(host)
    profiles = [profile for profile in database.list_mail_profiles() if profile["source_type"] == "outlook_com"]
    return {
        "host": host,
        "profiles": profiles,
        "pending_requests": database.list_outlook_sync_requests(limit=20),
    }


def request_sync(profile_name: str, *, actor: str = "cli") -> dict[str, Any]:
    return database.create_outlook_sync_request(profile_name=profile_name, actor=actor)


def cancel_request(request_id: str, *, actor: str = "cli") -> dict[str, Any]:
    return database.cancel_outlook_sync_request(request_id=request_id, actor=actor)


def set_profile_enabled(profile_name: str, *, enabled: bool) -> dict[str, Any]:
    return database.update_mail_profile_sync(name=profile_name, sync_enabled=enabled)


def run_forever(*, host_id: str = DEFAULT_HOST_ID, interval_seconds: int = 15, max_iterations: int | None = None) -> dict[str, Any]:
    iterations = 0
    error_count = 0
    last_result: dict[str, Any] | None = None
    last_error: str | None = None
    while max_iterations is None or iterations < max_iterations:
        try:
            last_result = run_once(host_id=host_id)
        except Exception as exc:  # Keep the host process alive after internal failures.
            error_count += 1
            last_error = str(exc)
            traceback.print_exc()
            _record_loop_error(host_id, exc)
        iterations += 1
        time.sleep(interval_seconds)
    return {
        "status": "stopped",
        "host_id": host_id,
        "iterations": iterations,
        "error_count": error_count,
        "last_error": last_error,
        "last_result": last_result or {},
    }


def run_once(*, host_id: str = DEFAULT_HOST_ID) -> dict[str, Any]:
    if platform.system() != "Windows":
        return _blocked(host_id, "blocked_not_windows", "Outlook COM host requires Windows")
    try:
        import win32com.client  # noqa: F401 # type: ignore[import-not-found]
    except ImportError:
        return _blocked(host_id, "blocked_missing_dependency", "pywin32 is required for Outlook COM")

    database.record_outlook_host_heartbeat(host_id=host_id, status="running", metadata={})
    request = database.claim_outlook_sync_request(host_id=host_id)
    if request is None:
        return {"status": "idle", "host_id": host_id}

    try:
        result = sync_outlook_profile(request["profile_name"])
        status_value = result.get("status", "completed")
        database.complete_outlook_sync_request(
            request_id=request["id"],
            profile_name=request["profile_name"],
            status=status_value,
            result=result,
            error=None if status_value in {"completed", "idle"} else str(result.get("errors") or result.get("error") or ""),
        )
        return {"host_id": host_id, "request_id": request["id"], **result}
    except Exception as exc:
        database.complete_outlook_sync_request(
            request_id=request["id"],
            profile_name=request["profile_name"],
            status="error",
            result={},
            error=str(exc),
        )
        database.record_outlook_host_heartbeat(
            host_id=host_id,
            status="blocked_outlook_unavailable",
            last_error=str(exc),
            metadata={},
        )
        return {
            "host_id": host_id,
            "request_id": request["id"],
            "profile": request["profile_name"],
            "status": "blocked_outlook_unavailable",
            "error": str(exc),
        }


def _blocked(host_id: str, status_value: str, message: str) -> dict[str, Any]:
    database.record_outlook_host_heartbeat(
        host_id=host_id,
        status=status_value,
        last_error=message,
        metadata={},
    )
    return {"host_id": host_id, "status": status_value, "error": message}


def _record_loop_error(host_id: str, exc: Exception) -> None:
    try:
        database.record_outlook_host_heartbeat(
            host_id=host_id,
            status="host_error",
            last_error=str(exc),
            metadata={"error_type": exc.__class__.__name__},
        )
    except Exception:
        traceback.print_exc()


def _normalize_host_status(host: dict[str, Any]) -> dict[str, Any]:
    normalized = dict(host)
    heartbeat_age = _heartbeat_age_seconds(host.get("heartbeat_at"))
    if heartbeat_age is not None:
        normalized["heartbeat_age_seconds"] = heartbeat_age
    if normalized.get("status") == "running" and (heartbeat_age is None or heartbeat_age > HOST_HEARTBEAT_STALE_AFTER_SECONDS):
        normalized["reported_status"] = "running"
        normalized["status"] = "host_stale"
        normalized["last_error"] = (
            f"Outlook host heartbeat is stale; last heartbeat was {heartbeat_age} seconds ago."
            if heartbeat_age is not None
            else "Outlook host heartbeat is missing."
        )
    return normalized


def _heartbeat_age_seconds(value: Any) -> int | None:
    if not value:
        return None
    if isinstance(value, datetime):
        heartbeat = value
    else:
        try:
            heartbeat = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        except ValueError:
            return None
    if heartbeat.tzinfo is None:
        heartbeat = heartbeat.replace(tzinfo=timezone.utc)
    return max(0, int((datetime.now(timezone.utc) - heartbeat.astimezone(timezone.utc)).total_seconds()))
