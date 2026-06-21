from __future__ import annotations

import html
import platform
from pathlib import Path
import shutil
import subprocess
import sys
from typing import Any

from . import database
from .extractors import extractor_availability
from .watcher import summarize_watcher_staleness

DASHBOARD_INDEX = Path(__file__).resolve().parent / "dashboard_static" / "index.html"


def doctor_payload() -> dict[str, Any]:
    checks = {
        "python": {
            "ok": sys.version_info >= (3, 11),
            "message": platform.python_version(),
            "required": True,
        },
        "docker": _docker_check(required=False),
        "git": _command_check("git", "Git source control", required=True),
        "gh": _command_check("gh", "GitHub CLI", required=False),
    }
    db_status = database.check_database()
    checks["postgresql"] = {"ok": db_status.ok, "message": db_status.message, "required": True}
    return {
        "summary": {"ok": all(check["ok"] for check in checks.values() if check.get("required", True))},
        "checks": checks,
    }


def collect_dashboard_payload() -> dict[str, Any]:
    db_status = database.check_database()
    roots = _safe(database.list_monitored_roots, [])
    crawl = _safe(
        database.crawl_status,
        {
            "active_watch_roots": 0,
            "disabled_watch_roots": 0,
            "pending_jobs": 0,
            "failed_jobs": 0,
            "recent_errors": [],
            "watchers": [],
        },
    )
    retrieval = _safe(
        database.retrieval_stats,
        {"episodes": 0, "sources": 0, "source_assets": 0, "asset_chunks": 0, "embeddings": 0},
    )
    checks = doctor_payload()["checks"]
    watcher_summary = summarize_watcher_staleness(crawl.get("watchers", []))
    return {
        "database": {"ok": db_status.ok, "message": db_status.message},
        "runtime": {
            "python": checks["python"],
            "docker": checks["docker"],
            "git": checks["git"],
            "postgresql": checks["postgresql"],
        },
        "watcher": {
            "active_roots": crawl["active_watch_roots"],
            "disabled_roots": crawl["disabled_watch_roots"],
            "roots": roots,
            "states": watcher_summary["states"],
            "stale_count": watcher_summary["stale_count"],
        },
        "jobs": {
            "pending": crawl["pending_jobs"],
            "failed": crawl["failed_jobs"],
            "blocked": crawl.get("blocked_jobs", 0),
        },
        "retrieval": retrieval,
        "extractors": extractor_availability(),
        "duplicates": {"assets": crawl.get("duplicate_assets", retrieval.get("duplicate_assets", 0))},
        "recent_errors": crawl["recent_errors"],
        "settings": _safe(lambda: __import__("flux_llm_kb.settings", fromlist=["SettingsService"]).SettingsService().public_list(), []),
        "mail": _safe(lambda: __import__("flux_llm_kb.mail_ingestion", fromlist=["mail_status"]).mail_status(), {"enabled_profiles": 0, "profiles": []}),
    }


def collect_crawl_payload() -> dict[str, Any]:
    return {"roots": _safe(database.list_monitored_roots, []), "status": _safe(database.crawl_status, {})}


def collect_jobs_payload(limit: int = 50) -> dict[str, Any]:
    return {"jobs": _safe(lambda: database.list_capture_jobs(limit=limit), [])}


def collect_retrieval_payload() -> dict[str, Any]:
    return database.retrieval_stats()


def build_dashboard_html() -> str:
    if DASHBOARD_INDEX.exists():
        return DASHBOARD_INDEX.read_text(encoding="utf-8")
    title = "Flux-LLM-KB Dashboard"
    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{html.escape(title)}</title>
  <style>
    body {{ margin: 0; min-height: 100vh; display: grid; place-items: center; font-family: Arial, sans-serif; background: #f6f7f9; color: #17181c; }}
    main {{ width: min(560px, calc(100vw - 32px)); border: 1px solid #d9dde5; border-radius: 8px; background: #fff; padding: 24px; box-shadow: 0 12px 32px rgba(20, 28, 40, 0.08); }}
    h1 {{ font-size: 22px; margin: 0 0 10px; }}
    p {{ color: #596170; line-height: 1.5; }}
    code {{ background: #f1f3f6; border: 1px solid #d9dde5; border-radius: 6px; padding: 2px 5px; }}
  </style>
</head>
<body>
  <main>
    <div id="root"></div>
    <h1>{html.escape(title)}</h1>
    <p>This dashboard build is missing. Run <code>npm --prefix dashboard run build</code>, then refresh <code>/dashboard</code>.</p>
    <p class="dashboard-static">Expected bundled assets under <code>flux_llm_kb/dashboard_static</code>.</p>
  </main>
</body>
</html>"""


def _safe(callable_obj, fallback):
    try:
        return callable_obj()
    except Exception:
        return fallback


def _command_check(command: str, description: str, *, required: bool = True) -> dict[str, Any]:
    path = shutil.which(command)
    return {
        "ok": path is not None,
        "message": path or f"{description} command not found",
        "required": required,
    }


def _docker_check(*, required: bool = True) -> dict[str, Any]:
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
