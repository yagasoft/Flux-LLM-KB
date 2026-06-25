from __future__ import annotations

from pathlib import PurePosixPath, PureWindowsPath
from typing import Any


def summarize_operational_diagnostics(
    *,
    retrieval: dict[str, Any] | None = None,
    watcher: dict[str, Any] | None = None,
    workers: dict[str, Any] | None = None,
    jobs: dict[str, Any] | None = None,
    mail: dict[str, Any] | None = None,
    section: str = "all",
) -> dict[str, Any]:
    sections = {
        "retrieval": _sanitize_section(retrieval or {}),
        "watcher": _sanitize_section(watcher or {}),
        "workers": _sanitize_section(workers or {}),
        "jobs": _sanitize_section(jobs or {}),
        "mail": _sanitize_section(mail or {}),
    }
    counts = {
        "retrieval_explains": len(sections["retrieval"].get("recent_explains", []) or []),
        "watcher_events": len(sections["watcher"].get("events", []) or []),
        "worker_families": len(sections["workers"].get("families", []) or []),
        "jobs": len(sections["jobs"].get("jobs", []) or []),
        "blocked_jobs": sum(1 for item in sections["jobs"].get("jobs", []) or [] if "blocked" in str(item.get("status") or "")),
        "mail_sync_runs": len(sections["mail"].get("sync_runs", []) or []),
        "mail_post_process_events": len(sections["mail"].get("post_process_events", []) or []),
    }
    selected_sections = sections if section == "all" else {section: sections.get(section, {})}
    return {
        "section": section,
        "settings_mutated": False,
        "counts": counts,
        "sections": selected_sections,
    }


def _sanitize_section(value: Any) -> Any:
    if isinstance(value, dict):
        return {str(key): _sanitize_section(item) for key, item in value.items()}
    if isinstance(value, list):
        return [_sanitize_section(item) for item in value]
    if isinstance(value, str):
        return _sanitize_text(value)
    return value


def _sanitize_text(value: str) -> str:
    normalized = value.replace("\\", "/")
    if ":/" in normalized or normalized.startswith("/"):
        leaf = PurePosixPath(normalized).name or PureWindowsPath(value).name
        return leaf or "<path>"
    return value
