from __future__ import annotations

import contextlib
import os
import platform
import re
import tempfile
import threading
import time
import traceback
from datetime import datetime, timezone
from typing import Any, Iterator

from . import database
from .mail_ingestion import sync_outlook_profile


DEFAULT_HOST_ID = "default"
HOST_HEARTBEAT_STALE_AFTER_SECONDS = 120
_LOCKED_HOST_PATHS: set[str] = set()
_LOCKED_HOST_PATHS_GUARD = threading.Lock()
_ACTIVE_REQUEST_HEARTBEAT_HOSTS: set[str] = set()
_ACTIVE_REQUEST_HEARTBEAT_GUARD = threading.Lock()


class OutlookHostAlreadyRunning(RuntimeError):
    pass


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


def run_forever(
    *,
    host_id: str = DEFAULT_HOST_ID,
    interval_seconds: int = 15,
    max_iterations: int | None = None,
    heartbeat_interval_seconds: float = 30.0,
) -> dict[str, Any]:
    try:
        with _outlook_host_lock(host_id):
            return _run_forever_locked(
                host_id=host_id,
                interval_seconds=interval_seconds,
                max_iterations=max_iterations,
                heartbeat_interval_seconds=heartbeat_interval_seconds,
            )
    except OutlookHostAlreadyRunning as exc:
        return {"status": "already_running", "host_id": host_id, "error": str(exc)}


def _run_forever_locked(
    *,
    host_id: str = DEFAULT_HOST_ID,
    interval_seconds: int = 15,
    max_iterations: int | None = None,
    heartbeat_interval_seconds: float = 30.0,
) -> dict[str, Any]:
    iterations = 0
    error_count = 0
    last_result: dict[str, Any] | None = None
    last_error: str | None = None
    stop_heartbeat, heartbeat_thread = _start_loop_heartbeat(host_id=host_id, interval_seconds=heartbeat_interval_seconds)
    try:
        while max_iterations is None or iterations < max_iterations:
            try:
                last_result = run_once(host_id=host_id, heartbeat_interval_seconds=heartbeat_interval_seconds)
            except Exception as exc:  # Keep the host process alive after internal failures.
                error_count += 1
                last_error = str(exc)
                traceback.print_exc()
                _record_loop_error(host_id, exc)
            iterations += 1
            time.sleep(interval_seconds)
    finally:
        stop_heartbeat.set()
        heartbeat_thread.join(timeout=1.0)
    return {
        "status": "stopped",
        "host_id": host_id,
        "iterations": iterations,
        "error_count": error_count,
        "last_error": last_error,
        "last_result": last_result or {},
    }


def run_once(*, host_id: str = DEFAULT_HOST_ID, heartbeat_interval_seconds: float = 30.0) -> dict[str, Any]:
    if platform.system() != "Windows":
        return _blocked(host_id, "blocked_not_windows", "Outlook COM host requires Windows")
    try:
        import win32com.client  # noqa: F401 # type: ignore[import-not-found]
    except ImportError:
        return _blocked(host_id, "blocked_missing_dependency", "pywin32 is required for Outlook COM")

    database.record_outlook_host_heartbeat(host_id=host_id, status="running", process_id=os.getpid(), metadata={})
    request = database.claim_outlook_sync_request(host_id=host_id)
    if request is None:
        return {"status": "idle", "host_id": host_id}

    try:
        result = _run_with_active_heartbeat(
            host_id=host_id,
            metadata={"active_request_id": request["id"], "profile_name": request["profile_name"]},
            interval_seconds=heartbeat_interval_seconds,
            action=lambda: sync_outlook_profile(request["profile_name"]),
        )
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
            process_id=os.getpid(),
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


def process_request_by_id(
    *,
    request_id: str,
    host_id: str = DEFAULT_HOST_ID,
    broker_message_id: str | None = None,
    heartbeat_interval_seconds: float = 30.0,
) -> dict[str, Any]:
    if platform.system() != "Windows":
        return _blocked(host_id, "blocked_not_windows", "Outlook COM host requires Windows")
    try:
        import win32com.client  # noqa: F401 # type: ignore[import-not-found]
    except ImportError:
        return _blocked(host_id, "blocked_missing_dependency", "pywin32 is required for Outlook COM")

    database.record_outlook_host_heartbeat(host_id=host_id, status="running", process_id=os.getpid(), metadata={})
    request = database.claim_outlook_sync_request_by_id(
        request_id=request_id,
        host_id=host_id,
        broker_message_id=broker_message_id,
    )
    if request is None:
        return {"status": "not_claimable", "host_id": host_id, "request_id": request_id, "retryable": False}
    try:
        result = _run_with_active_heartbeat(
            host_id=host_id,
            metadata={"active_request_id": request["id"], "profile_name": request["profile_name"]},
            interval_seconds=heartbeat_interval_seconds,
            action=lambda: sync_outlook_profile(request["profile_name"]),
        )
        status_value = result.get("status", "completed")
        database.complete_outlook_sync_request(
            request_id=request["id"],
            profile_name=request["profile_name"],
            status=status_value,
            result=result,
            error=None if status_value in {"completed", "idle"} else str(result.get("errors") or result.get("error") or ""),
        )
        return {"host_id": host_id, "request_id": request["id"], **result, "retryable": False}
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
            process_id=os.getpid(),
            last_error=str(exc),
            metadata={},
        )
        return {
            "host_id": host_id,
            "request_id": request["id"],
            "profile": request["profile_name"],
            "status": "blocked_outlook_unavailable",
            "error": str(exc),
            "retryable": True,
        }


def _run_with_active_heartbeat(
    *,
    host_id: str,
    metadata: dict[str, Any],
    interval_seconds: float,
    action,
):
    stop_heartbeat = threading.Event()
    interval = max(0.1, float(interval_seconds or 30.0))

    def heartbeat() -> None:
        while not stop_heartbeat.is_set():
            try:
                database.record_outlook_host_heartbeat(host_id=host_id, status="running", process_id=os.getpid(), metadata=metadata)
            except Exception:
                traceback.print_exc()
            stop_heartbeat.wait(interval)

    thread = threading.Thread(target=heartbeat, name=f"outlook-host-heartbeat:{host_id}", daemon=True)
    with _active_request_heartbeat(host_id):
        thread.start()
        try:
            return action()
        finally:
            stop_heartbeat.set()
            thread.join(timeout=1.0)


def _start_loop_heartbeat(*, host_id: str, interval_seconds: float) -> tuple[threading.Event, threading.Thread]:
    stop_heartbeat = threading.Event()
    interval = max(0.1, float(interval_seconds or 30.0))

    def heartbeat() -> None:
        while not stop_heartbeat.is_set():
            if not _active_request_heartbeat_running(host_id):
                try:
                    database.record_outlook_host_heartbeat(
                        host_id=host_id,
                        status="running",
                        process_id=os.getpid(),
                        metadata={"mode": "host_loop"},
                    )
                except Exception:
                    traceback.print_exc()
            stop_heartbeat.wait(interval)

    thread = threading.Thread(target=heartbeat, name=f"outlook-host-loop-heartbeat:{host_id}", daemon=True)
    thread.start()
    return stop_heartbeat, thread


@contextlib.contextmanager
def _active_request_heartbeat(host_id: str) -> Iterator[None]:
    with _ACTIVE_REQUEST_HEARTBEAT_GUARD:
        _ACTIVE_REQUEST_HEARTBEAT_HOSTS.add(host_id)
    try:
        yield
    finally:
        with _ACTIVE_REQUEST_HEARTBEAT_GUARD:
            _ACTIVE_REQUEST_HEARTBEAT_HOSTS.discard(host_id)


def _active_request_heartbeat_running(host_id: str) -> bool:
    with _ACTIVE_REQUEST_HEARTBEAT_GUARD:
        return host_id in _ACTIVE_REQUEST_HEARTBEAT_HOSTS


def _blocked(host_id: str, status_value: str, message: str) -> dict[str, Any]:
    database.record_outlook_host_heartbeat(
        host_id=host_id,
        status=status_value,
        process_id=os.getpid(),
        last_error=message,
        metadata={},
    )
    return {"host_id": host_id, "status": status_value, "error": message}


def _record_loop_error(host_id: str, exc: Exception) -> None:
    try:
        database.record_outlook_host_heartbeat(
            host_id=host_id,
            status="host_error",
            process_id=os.getpid(),
            last_error=str(exc),
            metadata={"error_type": exc.__class__.__name__},
        )
    except Exception:
        traceback.print_exc()


@contextlib.contextmanager
def _outlook_host_lock(host_id: str = DEFAULT_HOST_ID) -> Iterator[str]:
    lock_path = _outlook_host_lock_path(host_id)
    with _LOCKED_HOST_PATHS_GUARD:
        if lock_path in _LOCKED_HOST_PATHS:
            raise OutlookHostAlreadyRunning(f"Outlook host {host_id!r} is already running")
        _LOCKED_HOST_PATHS.add(lock_path)
    handle = None
    try:
        handle = open(lock_path, "a+b")
        _lock_file(handle, host_id)
        handle.seek(0)
        handle.truncate()
        handle.write(str(os.getpid()).encode("ascii"))
        handle.flush()
        yield lock_path
    finally:
        if handle is not None:
            try:
                _unlock_file(handle)
            finally:
                handle.close()
        with _LOCKED_HOST_PATHS_GUARD:
            _LOCKED_HOST_PATHS.discard(lock_path)


def _outlook_host_lock_path(host_id: str) -> str:
    lock_dir = os.environ.get("FLUX_KB_LOG_DIR") or os.path.join(tempfile.gettempdir(), "flux-llm-kb")
    os.makedirs(lock_dir, exist_ok=True)
    safe_host_id = re.sub(r"[^A-Za-z0-9_.-]+", "_", host_id or DEFAULT_HOST_ID)
    return os.path.abspath(os.path.join(lock_dir, f"outlook-host-{safe_host_id}.lock"))


def _lock_file(handle: Any, host_id: str) -> None:
    if os.name == "nt":
        import msvcrt

        handle.seek(0)
        try:
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as exc:
            raise OutlookHostAlreadyRunning(f"Outlook host {host_id!r} is already running") from exc
        return
    import fcntl

    try:
        fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError as exc:
        raise OutlookHostAlreadyRunning(f"Outlook host {host_id!r} is already running") from exc


def _unlock_file(handle: Any) -> None:
    if os.name == "nt":
        import msvcrt

        handle.seek(0)
        try:
            msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
        except OSError:
            pass
        return
    import fcntl

    fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


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
