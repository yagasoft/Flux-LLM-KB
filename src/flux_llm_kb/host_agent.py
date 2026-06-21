from __future__ import annotations

from dataclasses import dataclass
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import platform
import shutil
import subprocess
import sys
import time
from typing import Any
from urllib import error, request

from pydantic import BaseModel


DEFAULT_HOST_AGENT_PORT = 8799
HOST_AGENT_REQUEST_TIMEOUT_SECONDS = 3
HOST_AGENT_BROWSE_TIMEOUT_SECONDS = 300


class ValidateRequest(BaseModel):
    path: str
    require_directory: bool = True


class SyncRequest(BaseModel):
    root_name: str | None = None
    path: str | None = None
    dry_run: bool = False


@dataclass(frozen=True)
class HostAgentClientError(RuntimeError):
    message: str

    def __str__(self) -> str:
        return self.message


def status_payload() -> dict[str, Any]:
    return {
        "status": "running",
        "platform": platform.system() or "unknown",
        "process_id": os.getpid(),
        "browse_supported": _native_browse_supported(),
        "codex": _host_codex_status(),
        "runtime": _host_runtime_checks(),
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


def create_app():
    try:
        from fastapi import Body, FastAPI
    except ImportError as exc:  # pragma: no cover - optional dependency
        raise RuntimeError("Install host agent REST support with `pip install -e .[api]`") from exc

    app = FastAPI(title="Flux Host Agent")

    @app.get("/status")
    def status():
        return status_payload()

    @app.post("/validate-path")
    def validate(req: ValidateRequest = Body(...)):
        return validate_host_path(req.path, require_directory=req.require_directory)

    @app.post("/browse-folder")
    def browse():
        return browse_folder()

    @app.post("/crawl/sync")
    def crawl_sync(req: SyncRequest = Body(...)):
        from .service import KnowledgeService

        return KnowledgeService().sync_corpus(root_name=req.root_name, path=req.path, dry_run=req.dry_run)

    return app


def run_server(*, host: str = "127.0.0.1", port: int = DEFAULT_HOST_AGENT_PORT) -> dict[str, Any]:
    try:
        import uvicorn
    except ImportError as exc:  # pragma: no cover - optional dependency
        raise RuntimeError("Install host agent REST support with `pip install -e .[api]`") from exc

    app = create_app()
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
        result = subprocess.run(
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
