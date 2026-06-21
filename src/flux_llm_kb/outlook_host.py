from __future__ import annotations

import platform
import time
from typing import Any

from . import database
from .mail_ingestion import sync_outlook_profile


DEFAULT_HOST_ID = "default"


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
    profiles = [profile for profile in database.list_mail_profiles() if profile["source_type"] == "outlook_com"]
    return {
        "host": host,
        "profiles": profiles,
        "pending_requests": database.list_outlook_sync_requests(limit=20),
    }


def request_sync(profile_name: str, *, actor: str = "cli") -> dict[str, Any]:
    return database.create_outlook_sync_request(profile_name=profile_name, actor=actor)


def set_profile_enabled(profile_name: str, *, enabled: bool) -> dict[str, Any]:
    return database.update_mail_profile_sync(name=profile_name, sync_enabled=enabled)


def run_forever(*, host_id: str = DEFAULT_HOST_ID, interval_seconds: int = 15) -> dict[str, Any]:
    while True:
        run_once(host_id=host_id)
        time.sleep(interval_seconds)


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
