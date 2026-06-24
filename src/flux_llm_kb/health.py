from __future__ import annotations

import html
import platform
import os
from pathlib import Path
import shutil
import sys
from typing import Any

from . import database
from .acceleration import collect_acceleration_status
from .codex_integration import codex_status
from .error_diagnostics import coerce_error_detail, error_envelope
from .extractors import extractor_availability
from .glob_policy import effective_glob_policy
from .hook_policy import codex_hook_policy_status
from .host_agent import remote_status
from .processes import run_no_window
from .watcher import summarize_watcher_staleness

DASHBOARD_INDEX = Path(__file__).resolve().parent / "dashboard_static" / "index.html"


def doctor_payload() -> dict[str, Any]:
    production_mode = bool(os.environ.get("FLUX_KB_INSTALL_ROOT"))
    checks = {
        "python": {
            "ok": sys.version_info >= (3, 11),
            "message": platform.python_version(),
            "required": True,
        },
        "docker": _docker_check(required=False, production_mode=production_mode),
        "git": _command_check(
            "git",
            "Git source control",
            required=not production_mode,
            production_mode=production_mode,
        ),
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
    components = _safe(database.list_runtime_components, [])
    workers = [item for item in components if str(item.get("name", "")).startswith("corpus-worker:")]
    checks = doctor_payload()["checks"]
    host_agent_status = remote_status()
    crawl = _overlay_host_agent_crawl_status(crawl, roots, host_agent_status)
    watcher_summary = summarize_watcher_staleness(crawl.get("watchers", []))
    host_runtime = host_agent_status.get("runtime") if isinstance(host_agent_status, dict) else {}
    runtime_checks = {
        "python": checks["python"],
        "docker": checks["docker"],
        "git": checks["git"],
        "postgresql": checks["postgresql"],
    }
    if isinstance(host_runtime, dict):
        for key in ("python", "docker", "git"):
            if key in host_runtime:
                runtime_checks[key] = host_runtime[key]
    local_codex = _safe(codex_status, {"status": "unknown"})
    host_codex = host_agent_status.get("codex") or {}
    codex = {**local_codex, **host_codex}
    codex = {
        **codex,
        "hook_policy": _safe(
            codex_hook_policy_status,
            {
                "status": "unknown",
                "enabled": False,
                "preflight_enabled": False,
                "capture_enabled": False,
                "recent_events": [],
            },
        ),
    }
    extractors = extractor_availability()
    mail_payload = _safe(
        lambda: __import__("flux_llm_kb.mail_ingestion", fromlist=["mail_status"]).mail_status(),
        {"enabled_profiles": 0, "profiles": []},
    )
    recent_error_details = _dashboard_error_details(
        crawl=crawl,
        roots=roots,
        host_agent_status=host_agent_status,
        extractors=extractors,
        mail=mail_payload,
    )
    acceleration = _safe(collect_acceleration_status, {"capabilities": {}, "cache": {}, "worker_families": []})
    return {
        "database": {"ok": db_status.ok, "message": db_status.message},
        "runtime": runtime_checks,
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
        "extractors": extractors,
        "acceleration": acceleration,
        "host_agent": host_agent_status,
        "codex": codex,
        "workers": {
            "active": sum(1 for item in workers if item.get("status") == "running"),
            "components": workers,
        },
        "deployment": _deployment_status(),
        "duplicates": {"assets": crawl.get("duplicate_assets", retrieval.get("duplicate_assets", 0))},
        "recent_errors": crawl["recent_errors"],
        "recent_error_details": recent_error_details,
        "settings": _safe(lambda: __import__("flux_llm_kb.settings", fromlist=["SettingsService"]).SettingsService().public_list(), []),
        "mail": mail_payload,
    }


def collect_crawl_payload() -> dict[str, Any]:
    status = _safe(database.crawl_status, {})
    roots = _safe(database.list_monitored_roots, [])
    summaries = _safe(database.crawl_root_summaries, [])
    host_agent_status = remote_status()
    status = _overlay_host_agent_crawl_status(status, roots, host_agent_status)
    summaries = [_overlay_host_agent_root_summary(root, host_agent_status) for root in summaries]
    return {
        "roots": [_with_effective_globs(root) for root in roots],
        "root_summaries": [_with_effective_globs(root) for root in summaries],
        "status": status,
        "watchers": status.get("watchers", []),
        "recent_errors": status.get("recent_errors", []),
    }


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


def _dashboard_error_details(
    *,
    crawl: dict[str, Any],
    roots: list[dict[str, Any]],
    host_agent_status: dict[str, Any],
    extractors: dict[str, dict[str, Any]],
    mail: dict[str, Any],
) -> list[dict[str, Any]]:
    details: list[dict[str, Any]] = []
    for item in crawl.get("recent_error_details", []) or []:
        details.append(coerce_error_detail(item))
    for message in crawl.get("recent_errors", []) or []:
        details.append(
            error_envelope(
                code="runtime.error",
                message=str(message),
                severity="error",
                component="runtime",
                retryable=True,
                user_action="Open the related dashboard panel and review the failing component.",
            )
        )
    for watcher in crawl.get("watchers", []) or []:
        message = watcher.get("last_error")
        if not message:
            continue
        root_name = str(watcher.get("root_name") or "")
        details.append(
            error_envelope(
                code="watcher.error",
                message=str(message),
                severity="error",
                component="watcher",
                stage=str(watcher.get("status") or "watch"),
                retryable=True,
                user_action="Open the Corpus tab, inspect this watched path, then restart watch or sync after fixing the issue.",
                technical_detail=str(message),
                target={"type": "root", "id": root_name or "unknown"},
                links=[{"label": "Corpus", "tab": "corpus", "root": root_name}],
            )
        )
    for job in _safe(lambda: database.list_capture_jobs(limit=20), []):
        message = job.get("last_error")
        if not message:
            continue
        status = str(job.get("status") or "")
        severity = "warning" if status.startswith("blocked") else "error"
        details.append(
            error_envelope(
                code="corpus.job_blocked" if severity == "warning" else "corpus.job_failed",
                message=str(message),
                severity=severity,
                component="worker",
                stage=str(job.get("job_type") or "corpus_job"),
                retryable=True,
                user_action="Open Jobs to inspect the queued extraction task and retry after fixing the dependency or file state.",
                technical_detail=f"{job.get('job_type')} {status}: {message}",
                target={"type": "job", "id": str(job.get("id") or "")},
                links=[{"label": "Jobs", "tab": "jobs"}],
            )
        )
    if not _host_agent_is_running(host_agent_status) and any(_root_uses_host_agent(root) for root in roots):
        message = str(host_agent_status.get("message") or host_agent_status.get("status") or "host agent offline")
        details.append(
            error_envelope(
                code="host_agent.offline",
                message=message,
                severity="error",
                component="host-agent",
                stage="status",
                retryable=True,
                user_action="Start the Flux host agent, then refresh the dashboard.",
                technical_detail=message,
                target={"type": "component", "id": "host-agent"},
                links=[{"label": "Corpus", "tab": "corpus"}],
            )
        )
    for name, status in extractors.items():
        if status.get("ok") is not False:
            continue
        message = str(status.get("message") or f"{name} unavailable")
        details.append(
            error_envelope(
                code="extractor.missing_dependency",
                message=message,
                severity="warning",
                component="extractor",
                stage=name,
                retryable=True,
                user_action="Install the optional local tool only if you need this extractor family.",
                technical_detail=message,
                target={"type": "extractor", "id": name},
                links=[{"label": "Health", "tab": "health"}],
            )
        )
    oauth = mail.get("oauth")
    if isinstance(oauth, dict) and oauth.get("status") == "unavailable":
        message = str(oauth.get("error") or oauth.get("message") or "mail OAuth status unavailable")
        details.append(
            error_envelope(
                code="mail.oauth_unavailable",
                message=message,
                severity="error",
                component="mail",
                stage="oauth",
                retryable=True,
                user_action="Open Mail and recheck OAuth configuration for the affected profile.",
                technical_detail=message,
                target={"type": "mail", "id": "oauth"},
                links=[{"label": "Mail", "tab": "mail"}],
            )
        )
    scheduler = mail.get("scheduler") if isinstance(mail, dict) else {}
    if isinstance(scheduler, dict):
        for item in scheduler.get("diagnostics", []) or []:
            details.append(coerce_error_detail(item))
    return _dedupe_error_details(details)


def _dedupe_error_details(details: list[dict[str, Any]]) -> list[dict[str, Any]]:
    seen: set[tuple[str, str, str]] = set()
    unique: list[dict[str, Any]] = []
    for item in details:
        target = item.get("target") if isinstance(item.get("target"), dict) else {}
        target_key = f"{target.get('type', '')}:{target.get('id', '')}"
        key = (str(item.get("code") or ""), str(item.get("message") or ""), target_key)
        if key in seen:
            continue
        seen.add(key)
        unique.append(item)
    return unique[:12]


def _with_effective_globs(root: dict[str, Any]) -> dict[str, Any]:
    return {**root, "effective_globs": effective_glob_policy(root, **_global_glob_defaults())}


def _overlay_host_agent_crawl_status(
    status: dict[str, Any],
    roots: list[dict[str, Any]],
    host_agent_status: dict[str, Any],
) -> dict[str, Any]:
    if _host_agent_is_running(host_agent_status):
        return status
    host_root_names = {
        str(root.get("name"))
        for root in roots
        if root.get("watch_enabled") and _root_uses_host_agent(root)
    }
    if not host_root_names:
        return status
    message = str(host_agent_status.get("message") or host_agent_status.get("status") or "host agent offline")
    watchers = []
    for watcher in status.get("watchers", []):
        item = dict(watcher)
        if item.get("root_name") in host_root_names:
            item["status"] = "host_offline"
            item["last_error"] = item.get("last_error") or message
        watchers.append(item)
    return {**status, "watchers": watchers}


def _overlay_host_agent_root_summary(root: dict[str, Any], host_agent_status: dict[str, Any]) -> dict[str, Any]:
    if _host_agent_is_running(host_agent_status) or not root.get("watch_enabled") or not _root_uses_host_agent(root):
        return root
    message = str(host_agent_status.get("message") or host_agent_status.get("status") or "host agent offline")
    watcher = dict(root.get("watcher") or {})
    watcher["status"] = "host_offline"
    watcher["last_error"] = watcher.get("last_error") or message
    return {**root, "state": "host_offline", "watcher": watcher}


def _host_agent_is_running(host_agent_status: dict[str, Any]) -> bool:
    return isinstance(host_agent_status, dict) and host_agent_status.get("status") == "running"


def _root_uses_host_agent(root: dict[str, Any]) -> bool:
    metadata = root.get("metadata") or {}
    return metadata.get("host_access") == "host_agent"


def _global_glob_defaults() -> dict[str, list[str]]:
    try:
        from .settings import SettingsService

        settings = SettingsService()
        return {
            "global_include": list(settings.resolve("crawler.global_include_globs").raw_value or []),
            "global_exclude": list(settings.resolve("crawler.global_exclude_globs").raw_value or []),
        }
    except Exception:
        return {"global_include": [], "global_exclude": []}


def _command_check(
    command: str,
    description: str,
    *,
    required: bool = True,
    production_mode: bool = False,
) -> dict[str, Any]:
    path = shutil.which(command)
    if production_mode and path is None and command in {"docker", "git"}:
        return {
            "ok": True,
            "message": f"{description} is host-owned in production; not required inside the API container",
            "required": False,
        }
    return {
        "ok": path is not None,
        "message": path or f"{description} command not found",
        "required": required,
    }


def _docker_check(*, required: bool = True, production_mode: bool = False) -> dict[str, Any]:
    path = shutil.which("docker")
    if path is None:
        if production_mode:
            return {
                "ok": True,
                "message": "Docker is host-owned in production; not required inside the API container",
                "required": False,
            }
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


def _deployment_status() -> dict[str, Any]:
    source_root = Path(__file__).resolve().parents[2]
    install_root = os.environ.get("FLUX_KB_INSTALL_ROOT")
    app_root = os.environ.get("FLUX_KB_APP_ROOT")
    private_dir = os.environ.get("FLUX_KB_PRIVATE_DIR")
    data_dir = os.environ.get("FLUX_KB_DATA_DIR")
    logs_dir = os.environ.get("FLUX_KB_LOG_DIR")
    image_tag = os.environ.get("FLUX_KB_IMAGE_TAG")
    cwd = Path.cwd()
    try:
        running_from_repo = (cwd == source_root or source_root in cwd.parents) and (source_root / ".git").exists()
    except Exception:
        running_from_repo = False
    return {
        "install_root": install_root,
        "app_root": app_root,
        "private_dir": private_dir,
        "data_dir": data_dir,
        "logs_dir": logs_dir,
        "image_tag": image_tag,
        "source_root": str(source_root),
        "running_from_repo": running_from_repo,
        "repo_coupled": running_from_repo or not install_root,
        "mode": "production" if install_root and not running_from_repo else "development",
    }
