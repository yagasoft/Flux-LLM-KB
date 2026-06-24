from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

from . import database
from .crawler import CorpusPolicy
from .extractors import extract_file
from .settings import SettingsService


@dataclass(frozen=True)
class JobProcessResult:
    status: str
    message: str | None = None
    telemetry: dict[str, Any] | None = None


def process_corpus_job(job: dict) -> JobProcessResult:
    payload = job.get("payload") or {}
    root_name = payload.get("root_name")
    relative_path = payload.get("path")
    if not root_name or not relative_path:
        return JobProcessResult(status="failed", message="job payload requires root_name and path")

    root = database.get_monitored_root(root_name)
    if root is None:
        return JobProcessResult(status="failed", message=f"monitored root not found: {root_name}")

    path = Path(root["root_path"]) / relative_path
    if not path.exists():
        return JobProcessResult(status="failed", message=f"file not found: {relative_path}")

    policy = CorpusPolicy(
        root_path=Path(root["root_path"]),
        recursive=root["recursive"],
        include_globs=tuple(root["include_globs"]),
        exclude_globs=tuple(root["exclude_globs"]),
        max_inline_bytes=root["max_inline_bytes"],
        heavy_threshold_bytes=root["heavy_threshold_bytes"],
        **_configured_container_limits(),
    )
    try:
        result = extract_file(path, policy)
    except OSError as exc:
        if _is_locked_error(exc):
            return JobProcessResult(status="retrying_locked", message=str(exc))
        raise
    if result.status in {"indexed", "metadata_only", "blocked_missing_dependency"}:
        database.apply_extraction_result(root_name=root_name, relative_path=relative_path, result=result)
    return JobProcessResult(status=result.status, message=result.message, telemetry=_telemetry_from_extraction_result(result))


def _is_locked_error(exc: OSError) -> bool:
    text = str(exc).lower()
    return isinstance(exc, PermissionError) or "locked" in text or "being used by another process" in text


def _telemetry_from_extraction_result(result: object) -> dict[str, Any]:
    metadata = getattr(result, "metadata", None)
    if not isinstance(metadata, dict):
        return {}
    telemetry: dict[str, Any] = {}
    ocr = metadata.get("ocr")
    if isinstance(ocr, dict):
        if "cache_hits" in ocr:
            telemetry["ocr_cache_hits"] = int(ocr.get("cache_hits") or 0)
        if "cache_misses" in ocr:
            telemetry["ocr_cache_misses"] = int(ocr.get("cache_misses") or 0)
        if "pages_attempted" in ocr:
            telemetry["ocr_pages_attempted"] = int(ocr.get("pages_attempted") or 0)
    asr = metadata.get("asr")
    if isinstance(asr, dict):
        if "cache_hits" in asr:
            telemetry["asr_cache_hits"] = int(asr.get("cache_hits") or 0)
        if "cache_misses" in asr:
            telemetry["asr_cache_misses"] = int(asr.get("cache_misses") or 0)
        if "segments" in asr:
            telemetry["asr_segments"] = int(asr.get("segments") or 0)
    if metadata.get("extractor") == "container":
        for source_key, telemetry_key in {
            "member_count": "container_member_count",
            "parsed_child_count": "container_parsed_child_count",
            "skipped_child_count": "container_skipped_child_count",
            "blocked_dependency_count": "container_blocked_dependency_count",
            "max_depth": "container_max_depth",
        }.items():
            if source_key in metadata:
                telemetry[telemetry_key] = int(metadata.get(source_key) or 0)
    return telemetry


def _configured_container_limits() -> dict[str, int]:
    settings = SettingsService()
    defaults = CorpusPolicy(root_path=Path("."))
    keys = {
        "container_max_depth": "crawler.container_max_depth",
        "container_max_members": "crawler.container_max_members",
        "container_max_total_bytes": "crawler.container_max_total_bytes",
        "container_max_member_bytes": "crawler.container_max_member_bytes",
    }
    resolved: dict[str, int] = {}
    for field_name, setting_key in keys.items():
        try:
            resolved[field_name] = int(settings.resolve(setting_key).raw_value)
        except Exception:
            resolved[field_name] = int(getattr(defaults, field_name))
    return resolved
