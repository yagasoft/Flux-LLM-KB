from __future__ import annotations

from contextlib import asynccontextmanager
from dataclasses import dataclass
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import platform
import shutil
import sys
import threading
import time
from typing import Any
from urllib import error, request

from pydantic import BaseModel, ConfigDict

from . import database
from .processes import run_no_window
from .watcher import WatchEvent, WatchRoot, create_corpus_watcher


DEFAULT_HOST_AGENT_PORT = 8799
HOST_AGENT_REQUEST_TIMEOUT_SECONDS = 3
HOST_AGENT_BROWSE_TIMEOUT_SECONDS = 300
HOST_AGENT_BACKFILL_TIMEOUT_SECONDS = 600
HOST_AGENT_BENCHMARK_TIMEOUT_SECONDS = 600


class ValidateRequest(BaseModel):
    path: str
    require_directory: bool = True


class SyncRequest(BaseModel):
    root_name: str | None = None
    path: str | None = None
    dry_run: bool = False


class BackfillRequest(BaseModel):
    kind: str = "all"
    limit: int = 10
    workers: int = 1
    root_name: str | None = None
    family: str | None = None


class BenchmarkRequest(BaseModel):
    fixture: str = "all"
    files: int = 10
    mode: str = "scan"
    passes: int = 1
    label: str | None = None
    compare_label: str | None = None
    workers: int = 1
    family: str = "all"
    scope: str = "synthetic"
    root_name: str | None = None
    path: str | None = None
    max_files: int | None = None
    deployment_label: str | None = None
    scenario: str = "standard"
    include_model_probe: bool = False


class FileActionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    asset_id: str
    action: str


@dataclass(frozen=True)
class HostAgentClientError(RuntimeError):
    message: str

    def __str__(self) -> str:
        return self.message


def status_payload() -> dict[str, Any]:
    components = _runtime_components()
    return {
        "status": "running",
        "platform": platform.system() or "unknown",
        "process_id": os.getpid(),
        "browse_supported": _native_browse_supported(),
        "vss": _vss_status(),
        "codex": _host_codex_status(),
        "runtime": _host_runtime_checks(),
        "workers": [item for item in components if str(item.get("name", "")).startswith("corpus-worker:")],
        "time": time.time(),
    }


def validate_host_path(path: str, *, require_directory: bool = True) -> dict[str, Any]:
    raw_path = str(path).strip()
    path_style = _path_style(raw_path)
    absolute = path_style != "relative"
    if not raw_path or not absolute:
        return {
            "status": "invalid",
            "path": raw_path,
            "path_style": path_style,
            "absolute": False,
            "exists": False,
            "is_dir": False,
            "message": "path must be absolute",
        }

    local_path = Path(raw_path).expanduser()
    exists = local_path.exists()
    is_dir = local_path.is_dir() if exists else False
    if not exists:
        status = "missing"
        message = "directory does not exist" if require_directory else "path does not exist"
    elif require_directory and not is_dir:
        status = "invalid"
        message = "path must be a directory"
    else:
        status = "ok"
        message = "path is available"

    return {
        "status": status,
        "path": raw_path,
        "path_style": path_style,
        "absolute": absolute,
        "exists": exists,
        "is_dir": is_dir,
        "message": message,
    }


def browse_folder() -> dict[str, Any]:
    if not _native_browse_supported():
        return {
            "status": "unsupported",
            "path": None,
            "message": "native folder browsing is not available in this host session",
        }
    try:
        import tkinter as tk
        from tkinter import filedialog
    except Exception as exc:  # pragma: no cover - depends on host desktop support
        return {"status": "unsupported", "path": None, "message": str(exc)}

    root = tk.Tk()
    root.withdraw()
    root.attributes("-topmost", True)
    try:
        selected = filedialog.askdirectory(title="Choose Flux watched path")
    finally:
        root.destroy()
    if not selected:
        return {"status": "cancelled", "path": None}
    return {"status": "selected", "path": selected}


def perform_file_action(*, asset_id: str, action: str) -> dict[str, Any]:
    if action not in {"open", "reveal"}:
        return _file_action_result(asset_id=asset_id, action=action, state="not_allowed")

    asset = database.get_source_asset_for_file_action(asset_id)
    if asset is None:
        return _file_action_result(asset_id=asset_id, action=action, state="not_allowed")
    if asset.get("deleted_at") or asset.get("status") == "deleted":
        return _file_action_result(asset_id=asset_id, action=action, state="deleted", asset=asset)

    target = _resolve_known_asset_path(asset)
    if target is None:
        return _file_action_result(asset_id=asset_id, action=action, state="not_allowed", asset=asset)
    if not target.exists():
        return _file_action_result(asset_id=asset_id, action=action, state="missing", asset=asset, target=target)

    try:
        if action == "open":
            _launch_default_app(target)
        else:
            _reveal_in_folder(target)
    except FileNotFoundError:
        return _file_action_result(asset_id=asset_id, action=action, state="missing", asset=asset, target=target)
    except (PermissionError, OSError) as exc:
        state = "locked" if _is_locked_error(exc) else "not_allowed"
        return _file_action_result(asset_id=asset_id, action=action, state=state, asset=asset, target=target, error=str(exc))
    return _file_action_result(asset_id=asset_id, action=action, state="opened", asset=asset, target=target)


class HostAgentWatcherLoop:
    def __init__(
        self,
        *,
        root_name: str | None = None,
        interval_seconds: float = 2.0,
        service_factory=None,
        watcher_factory=None,
    ) -> None:
        self.root_name = root_name
        self.interval_seconds = interval_seconds
        self.service_factory = service_factory
        self.watcher_factory = watcher_factory or create_corpus_watcher
        self._last_reconcile_at = 0.0
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._watcher = self.watcher_factory(
            lambda: _load_host_watch_roots(self.root_name),
            on_change=None,
            interval_seconds=interval_seconds,
            debounce_seconds=_configured_watcher_debounce_seconds(),
            stability_quiet_seconds=_configured_stability_quiet_seconds(),
            max_queue_size=_configured_watcher_max_queue_size(),
        )

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="flux-host-agent-watcher", daemon=True)
        self._thread.start()

    def stop(self, *, timeout: float = 5.0) -> None:
        self._stop.set()
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=timeout)

    def run_once(self, *, seed: bool = False) -> dict[str, Any]:
        roots = _load_host_watch_roots(self.root_name)
        if not roots:
            return {"status": "no_enabled_host_roots", "roots": 0, "events": 0}
        for root in roots:
            database.record_watcher_heartbeat(root_name=root.name)
        self._watcher.poll_once(seed=seed)
        events = self._watcher.drain_events() if hasattr(self._watcher, "drain_events") else []
        for event in events:
            self._handle_event(event)
        return {"status": "running", "roots": len(roots), "events": len(events)}

    def _run(self) -> None:
        if _configured_reconcile_on_start():
            self.reconcile_once(reason="startup_reconcile")
            self._last_reconcile_at = time.monotonic()
        self.run_once(seed=True)
        while not self._stop.wait(self.interval_seconds):
            try:
                self.run_once(seed=False)
                reconcile_interval = _configured_reconcile_interval_seconds()
                if reconcile_interval > 0 and time.monotonic() - self._last_reconcile_at >= reconcile_interval:
                    self.reconcile_once(reason="periodic_reconcile")
                    self._last_reconcile_at = time.monotonic()
            except Exception as exc:  # pragma: no cover - defensive long-running loop
                for root in _load_host_watch_roots(self.root_name):
                    database.record_watch_error(root_name=root.name, error=str(exc))

    def _handle_event(self, event: WatchEvent) -> None:
        try:
            database.record_watch_event(root_name=event.root_name)
            service = self.service_factory() if self.service_factory else _service()
            service.sync_corpus(root_name=event.root_name, path=str(event.path), reason="watch_event")
        except Exception as exc:  # pragma: no cover - environment-specific watcher loop
            database.record_watch_error(root_name=event.root_name, error=str(exc))

    def reconcile_once(self, *, reason: str) -> dict[str, Any]:
        service = self.service_factory() if self.service_factory else _service()
        return service.reconcile_watch_roots(
            root_name=self.root_name,
            reason=reason,
            host_agent_roots=True,
            component_name="watch-reconciler:host-agent",
        )


class HostAgentWorkerLoop:
    def __init__(
        self,
        *,
        root_name: str | None = None,
        interval_seconds: float = 5.0,
        limit: int | None = None,
        workers: int = 1,
        service_factory=None,
    ) -> None:
        self.root_name = root_name
        self.interval_seconds = interval_seconds
        self.limit = limit
        self.workers = workers
        self.service_factory = service_factory
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="flux-host-agent-worker", daemon=True)
        self._thread.start()

    def stop(self, *, timeout: float = 5.0) -> None:
        self._stop.set()
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=timeout)

    def run_once(self) -> dict[str, Any]:
        roots = _load_host_roots(self.root_name)
        batch_size = self.limit if self.limit is not None else _configured_worker_batch_size()
        metadata = {"root_count": len(roots), "roots": [root["name"] for root in roots]}
        database.record_runtime_component_heartbeat(
            name="corpus-worker:host-agent",
            status="running" if roots else "idle",
            metadata=metadata,
        )
        if not roots:
            return {"status": "no_enabled_host_roots", "roots": 0, "completed": 0, "blocked": 0, "retried": 0}

        service = self.service_factory() if self.service_factory else _service()
        totals = {"completed": 0, "blocked": 0, "retried": 0, "claimed": 0}
        results: list[dict[str, Any]] = []
        for root in roots:
            result = service.run_corpus_backfill(
                kind="all",
                limit=batch_size,
                workers=self.workers,
                root_name=root["name"],
                host_agent_roots=True,
            )
            results.append(result)
            for key in totals:
                totals[key] += int(result.get(key) or 0)
        payload = {"status": "running", "roots": len(roots), **totals, "results": results}
        database.record_runtime_component_heartbeat(
            name="corpus-worker:host-agent",
            status="running",
            metadata={"last_result": payload},
        )
        return payload

    def _run(self) -> None:
        while not self._stop.wait(0):
            try:
                self.run_once()
            except Exception as exc:  # pragma: no cover - defensive long-running loop
                database.record_runtime_component_heartbeat(
                    name="corpus-worker:host-agent",
                    status="error",
                    metadata={"last_error": str(exc)},
                )
            if self._stop.wait(self.interval_seconds):
                return


def create_app(*, start_watcher: bool = False):
    try:
        from fastapi import Body, FastAPI
    except ImportError as exc:  # pragma: no cover - optional dependency
        raise RuntimeError("Install host agent REST support with `pip install -e .[api]`") from exc

    watcher_loop = HostAgentWatcherLoop() if start_watcher else None
    worker_loop = HostAgentWorkerLoop() if start_watcher else None

    if watcher_loop is not None or worker_loop is not None:
        @asynccontextmanager
        async def lifespan(_app):
            if watcher_loop is not None:
                watcher_loop.start()
            if worker_loop is not None:
                worker_loop.start()
            try:
                yield
            finally:
                if worker_loop is not None:
                    worker_loop.stop()
                if watcher_loop is not None:
                    watcher_loop.stop()
    else:
        lifespan = None

    app = FastAPI(title="Flux Host Agent", lifespan=lifespan)

    @app.get("/status")
    def status():
        return status_payload()

    @app.post("/validate-path")
    def validate(req: ValidateRequest = Body(...)):
        return validate_host_path(req.path, require_directory=req.require_directory)

    @app.post("/browse-folder")
    def browse():
        return browse_folder()

    @app.post("/file-actions")
    def file_action(req: FileActionRequest = Body(...)):
        return perform_file_action(asset_id=req.asset_id, action=req.action)

    @app.post("/crawl/sync")
    def crawl_sync(req: SyncRequest = Body(...)):
        return _service().sync_corpus(root_name=req.root_name, path=req.path, dry_run=req.dry_run)

    @app.post("/crawl/backfill")
    def crawl_backfill(req: BackfillRequest = Body(...)):
        kwargs: dict[str, Any] = {
            "kind": req.kind,
            "limit": req.limit,
            "workers": req.workers,
            "root_name": req.root_name,
        }
        if req.family is not None:
            kwargs["family"] = req.family
        return _service().run_corpus_backfill(**kwargs)

    @app.post("/acceleration/benchmarks/run")
    def benchmark_run(req: BenchmarkRequest = Body(...)):
        return _service().run_benchmark(
            fixture=req.fixture,
            files=req.files,
            mode=req.mode,
            passes=req.passes,
            label=req.label,
            compare_label=req.compare_label,
            workers=req.workers,
            family=req.family,
            scope=req.scope,
            root_name=req.root_name,
            path=req.path,
            max_files=req.max_files,
            deployment_label=req.deployment_label,
            scenario=req.scenario,
            include_model_probe=req.include_model_probe,
        )

    return app


def run_server(*, host: str = "127.0.0.1", port: int = DEFAULT_HOST_AGENT_PORT) -> dict[str, Any]:
    agent_url = f"http://{host}:{port}"
    existing = remote_status(agent_url=agent_url)
    if existing.get("status") == "running":
        return {
            "status": "already_running",
            "host": host,
            "port": port,
            "process_id": existing.get("process_id"),
        }
    try:
        import uvicorn
    except ImportError as exc:  # pragma: no cover - optional dependency
        raise RuntimeError("Install host agent REST support with `pip install -e .[api]`") from exc

    app = create_app(start_watcher=True)
    uvicorn.run(app, host=host, port=port, log_level="info")
    return {"status": "stopped", "host": host, "port": port}


def remote_status(agent_url: str | None = None) -> dict[str, Any]:
    try:
        return _request_json("GET", f"{_agent_url(agent_url)}/status")
    except HostAgentClientError as exc:
        return {"status": "host_agent_offline", "message": str(exc), "browse_supported": False}


def remote_validate_path(path: str, *, agent_url: str | None = None) -> dict[str, Any]:
    try:
        return _request_json(
            "POST",
            f"{_agent_url(agent_url)}/validate-path",
            {"path": path, "require_directory": True},
        )
    except HostAgentClientError as exc:
        return {
            "status": "host_agent_offline",
            "path": path,
            "absolute": _path_style(path) != "relative",
            "path_style": _path_style(path),
            "exists": False,
            "is_dir": False,
            "message": str(exc),
        }


def remote_browse_folder(agent_url: str | None = None) -> dict[str, Any]:
    try:
        return _request_json(
            "POST",
            f"{_agent_url(agent_url)}/browse-folder",
            {},
            timeout=HOST_AGENT_BROWSE_TIMEOUT_SECONDS,
        )
    except HostAgentClientError as exc:
        return {"status": "host_agent_offline", "path": None, "message": str(exc)}


def remote_sync(
    *,
    root_name: str | None = None,
    path: str | None = None,
    dry_run: bool = False,
    agent_url: str | None = None,
) -> dict[str, Any]:
    try:
        return _request_json(
            "POST",
            f"{_agent_url(agent_url)}/crawl/sync",
            {"root_name": root_name, "path": path, "dry_run": dry_run},
        )
    except HostAgentClientError as exc:
        return {"status": "host_agent_offline", "message": str(exc), "root_name": root_name, "path": path}


def remote_backfill(
    *,
    kind: str = "all",
    limit: int = 10,
    workers: int = 1,
    root_name: str | None = None,
    family: str | None = None,
    agent_url: str | None = None,
) -> dict[str, Any]:
    payload: dict[str, Any] = {"kind": kind, "limit": limit, "workers": workers, "root_name": root_name}
    if family is not None:
        payload["family"] = family
    try:
        return _request_json(
            "POST",
            f"{_agent_url(agent_url)}/crawl/backfill",
            payload,
            timeout=HOST_AGENT_BACKFILL_TIMEOUT_SECONDS,
        )
    except HostAgentClientError as exc:
        return {"status": "host_agent_offline", "message": str(exc), "root_name": root_name}


def remote_benchmark(
    *,
    fixture: str = "all",
    files: int = 10,
    mode: str = "scan",
    passes: int = 1,
    label: str | None = None,
    compare_label: str | None = None,
    workers: int = 1,
    family: str = "all",
    scope: str = "synthetic",
    root_name: str | None = None,
    path: str | None = None,
    max_files: int | None = None,
    deployment_label: str | None = None,
    scenario: str = "standard",
    include_model_probe: bool = False,
    agent_url: str | None = None,
) -> dict[str, Any]:
    payload = {
        "fixture": fixture,
        "files": files,
        "mode": mode,
        "passes": passes,
        "label": label,
        "compare_label": compare_label,
        "workers": workers,
        "family": family,
        "scope": scope,
        "root_name": root_name,
        "path": path,
        "max_files": max_files,
        "deployment_label": deployment_label,
        "scenario": scenario,
        "include_model_probe": include_model_probe,
    }
    try:
        return _request_json(
            "POST",
            f"{_agent_url(agent_url)}/acceleration/benchmarks/run",
            payload,
            timeout=HOST_AGENT_BENCHMARK_TIMEOUT_SECONDS,
        )
    except HostAgentClientError as exc:
        return {"status": "host_agent_offline", "message": str(exc), "root_name": root_name, "path": path}


def remote_file_action(
    *,
    asset_id: str,
    action: str,
    agent_url: str | None = None,
) -> dict[str, Any]:
    try:
        return _request_json(
            "POST",
            f"{_agent_url(agent_url)}/file-actions",
            {"asset_id": asset_id, "action": action},
        )
    except HostAgentClientError as exc:
        return {"state": "host_agent_offline", "message": str(exc), "asset_id": asset_id, "action": action}


def path_requires_host_agent(path: str) -> bool:
    style = _path_style(path)
    if style in {"windows_drive", "windows_unc"} and platform.system() != "Windows":
        return True
    return False


def _agent_url(agent_url: str | None = None) -> str:
    if agent_url:
        return agent_url.rstrip("/")
    configured = os.environ.get("FLUX_KB_HOST_AGENT_URL")
    if configured:
        return configured.rstrip("/")
    host = "host.docker.internal" if Path("/.dockerenv").exists() else "127.0.0.1"
    return f"http://{host}:{DEFAULT_HOST_AGENT_PORT}"


def _service():
    from .service import KnowledgeService

    return KnowledgeService()


def _load_host_watch_roots(root_name: str | None = None) -> list[WatchRoot]:
    roots = _load_host_roots(root_name=root_name, watch_enabled=True)
    return [
        WatchRoot(
            name=root["name"],
            root_path=Path(root["root_path"]),
            watch_enabled=root["watch_enabled"],
            recursive=root["recursive"],
        )
        for root in roots
    ]


def _load_host_roots(root_name: str | None = None, *, watch_enabled: bool | None = None) -> list[dict[str, Any]]:
    roots = database.list_monitored_roots(watch_enabled=watch_enabled) if watch_enabled is not None else database.list_monitored_roots()
    return [
        root
        for root in roots
        if root.get("enabled")
        and (root_name is None or root.get("name") == root_name)
        and _is_host_agent_root(root)
    ]


def _configured_worker_batch_size() -> int:
    try:
        from .settings import SettingsService

        return int(SettingsService().resolve("worker.batch_size").raw_value)
    except Exception:
        return 10


def _configured_reconcile_on_start() -> bool:
    try:
        from .settings import SettingsService

        return bool(SettingsService().resolve("watcher.reconcile_on_start").raw_value)
    except Exception:
        return True


def _configured_reconcile_interval_seconds() -> int:
    try:
        from .settings import SettingsService

        return int(SettingsService().resolve("watcher.reconcile_interval_seconds").raw_value)
    except Exception:
        return 3600


def _configured_watcher_debounce_seconds() -> float:
    try:
        from .settings import SettingsService

        return float(SettingsService().resolve("watcher.debounce_seconds").raw_value)
    except Exception:
        return 0.75


def _configured_watcher_max_queue_size() -> int:
    try:
        from .settings import SettingsService

        return int(SettingsService().resolve("watcher.max_queue_size").raw_value)
    except Exception:
        return 1000


def _configured_stability_quiet_seconds() -> float:
    try:
        from .settings import SettingsService

        return float(SettingsService().resolve("watcher.stability_quiet_seconds").raw_value)
    except Exception:
        return 2.0


def _vss_status() -> dict[str, Any]:
    try:
        from .settings import SettingsService

        settings = SettingsService()
        enabled = bool(settings.resolve("host_agent.vss_enabled").raw_value)
        max_file_bytes = int(settings.resolve("host_agent.vss_max_file_bytes").raw_value)
        timeout_seconds = int(settings.resolve("host_agent.vss_timeout_seconds").raw_value)
    except Exception:
        enabled = False
        max_file_bytes = 512 * 1024 * 1024
        timeout_seconds = 30
    if not enabled:
        return {
            "enabled": False,
            "status": "disabled",
            "max_file_bytes": max_file_bytes,
            "timeout_seconds": timeout_seconds,
            "message": "VSS fallback is disabled; locked files use retry/cooldown states.",
        }
    if platform.system() != "Windows":
        return {
            "enabled": True,
            "status": "unavailable",
            "max_file_bytes": max_file_bytes,
            "timeout_seconds": timeout_seconds,
            "message": "VSS fallback is only available on Windows host-agent roots.",
        }
    return {
        "enabled": True,
        "status": "unavailable",
        "max_file_bytes": max_file_bytes,
        "timeout_seconds": timeout_seconds,
        "message": "VSS fallback is not implemented yet; locked files use retry/cooldown states.",
    }


def _is_host_agent_root(root: dict[str, Any]) -> bool:
    metadata = root.get("metadata") or {}
    if metadata.get("host_access") == "host_agent":
        return True
    return _path_style(str(root.get("root_path") or "")) in {"windows_drive", "windows_unc"}


def _resolve_known_asset_path(asset: dict[str, Any]) -> Path | None:
    root_text = str(asset.get("root_path") or "").strip()
    relative_text = str(asset.get("path") or "").strip()
    if not root_text or not relative_text or _path_style(relative_text) != "relative":
        return None
    try:
        root = Path(root_text).expanduser().resolve()
        target = (root / PurePosixPath(relative_text).as_posix()).resolve()
    except Exception:
        return None
    try:
        if target != root and not target.is_relative_to(root):
            return None
    except ValueError:
        return None
    return target


def _file_action_result(
    *,
    asset_id: str,
    action: str,
    state: str,
    asset: dict[str, Any] | None = None,
    target: Path | None = None,
    error: str | None = None,
) -> dict[str, Any]:
    details = {
        "asset_id": asset_id,
        "action": action,
        "state": state,
        "path": str(asset.get("path")) if asset else None,
        "target_path": str(target) if target else None,
        "error": error,
    }
    database.record_audit_event(
        event_type="host.file_action",
        target_table="source_assets",
        target_id=asset_id,
        details={key: value for key, value in details.items() if value is not None},
    )
    return {
        "state": state,
        "asset_id": asset_id,
        "action": action,
        "path": str(target) if target else None,
        "message": error,
    }


def _launch_default_app(path: Path) -> None:
    system = platform.system()
    if system == "Windows":
        os.startfile(str(path))  # type: ignore[attr-defined]
        return
    if system == "Darwin":
        result = run_no_window(["open", str(path)], capture_output=True, text=True, check=False)
    else:
        result = run_no_window(["xdg-open", str(path)], capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise OSError(result.stderr.strip() or result.stdout.strip() or "open failed")


def _reveal_in_folder(path: Path) -> None:
    system = platform.system()
    if system == "Windows":
        result = run_no_window(["explorer", f"/select,{path}"], capture_output=True, text=True, check=False)
    elif system == "Darwin":
        result = run_no_window(["open", "-R", str(path)], capture_output=True, text=True, check=False)
    else:
        result = run_no_window(["xdg-open", str(path.parent)], capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise OSError(result.stderr.strip() or result.stdout.strip() or "reveal failed")


def _is_locked_error(exc: OSError) -> bool:
    if isinstance(exc, PermissionError):
        return True
    winerror = getattr(exc, "winerror", None)
    if winerror in {32, 33}:
        return True
    text = str(exc).lower()
    return "locked" in text or "being used by another process" in text or "permission" in text


def _request_json(
    method: str,
    url: str,
    payload: dict[str, Any] | None = None,
    *,
    timeout: float = HOST_AGENT_REQUEST_TIMEOUT_SECONDS,
) -> dict[str, Any]:
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    req = request.Request(
        url,
        data=body,
        method=method,
        headers={"Content-Type": "application/json"},
    )
    try:
        with request.urlopen(req, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except (OSError, error.URLError, TimeoutError, json.JSONDecodeError) as exc:
        raise HostAgentClientError(str(exc)) from exc


def _native_browse_supported() -> bool:
    try:
        import tkinter  # noqa: F401
    except Exception:
        return False
    return platform.system() in {"Windows", "Darwin", "Linux"}


def _host_codex_status() -> dict[str, Any]:
    try:
        from .codex_integration import codex_status

        return codex_status()
    except Exception as exc:  # pragma: no cover - defensive status payload
        return {"status": "unknown", "message": str(exc)}


def _host_runtime_checks() -> dict[str, Any]:
    return {
        "python": {
            "ok": sys.version_info >= (3, 11),
            "message": platform.python_version(),
            "required": True,
        },
        "docker": _host_docker_check(required=False),
        "git": _host_command_check("git", "Git source control", required=True),
        "gh": _host_command_check("gh", "GitHub CLI", required=False),
    }


def _runtime_components() -> list[dict[str, Any]]:
    try:
        return database.list_runtime_components()
    except Exception:
        return []


def _host_command_check(command: str, description: str, *, required: bool = True) -> dict[str, Any]:
    path = shutil.which(command)
    return {
        "ok": path is not None,
        "message": path or f"{description} command not found",
        "required": required,
    }


def _host_docker_check(*, required: bool = True) -> dict[str, Any]:
    path = shutil.which("docker")
    if path is None:
        return {"ok": False, "message": "Docker command not found", "required": required}
    try:
        result = run_no_window(
            ["docker", "compose", "version"],
            text=True,
            capture_output=True,
            timeout=8,
            check=False,
        )
    except Exception as exc:  # pragma: no cover - environment-specific
        return {"ok": False, "message": str(exc), "required": required}
    if result.returncode != 0:
        message = result.stderr.strip() or result.stdout.strip() or "Docker Compose unavailable"
        return {"ok": False, "message": message, "required": required}
    return {"ok": True, "message": result.stdout.strip() or path, "required": required}


def _path_style(path: str) -> str:
    raw_path = str(path).strip()
    if not raw_path:
        return "relative"
    windows_path = PureWindowsPath(raw_path)
    if raw_path.startswith("\\\\") and windows_path.is_absolute():
        return "windows_unc"
    if windows_path.drive and windows_path.is_absolute():
        return "windows_drive"
    if PurePosixPath(raw_path).is_absolute():
        return "posix"
    return "relative"
