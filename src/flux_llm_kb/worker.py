from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

from . import database
from .crawler import CorpusPolicy, strict_indexing_enabled, strict_metadata_only_message
from .extractors import ExtractionResult, extract_file
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
        return JobProcessResult(status="cancelled_orphaned_root", message=f"monitored root not found: {root_name}")

    path = Path(root["root_path"]) / relative_path
    if not path.exists():
        return JobProcessResult(
            status="cancelled_missing_source",
            message=f"source file not found: {relative_path}",
            telemetry={"missing_source": True, "missing_source_deleted": True},
        )

    root_metadata = root.get("metadata") if isinstance(root.get("metadata"), dict) else {}
    strict_indexing = strict_indexing_enabled(root_metadata)
    policy = CorpusPolicy(
        root_path=Path(root["root_path"]),
        recursive=root["recursive"],
        include_globs=tuple(root["include_globs"]),
        exclude_globs=tuple(root["exclude_globs"]),
        strict_indexing=strict_indexing,
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
    except Exception as exc:
        return JobProcessResult(
            status="failed",
            message=str(exc),
            telemetry={"error_type": exc.__class__.__name__},
        )
    result = _enforce_strict_indexing_result(result, strict_indexing=strict_indexing)
    if result.status in {"indexed", "metadata_only", "blocked_missing_dependency"}:
        database.apply_extraction_result(root_name=root_name, relative_path=relative_path, result=result)
    return JobProcessResult(status=result.status, message=result.message, telemetry=_telemetry_from_extraction_result(result))


def process_embedding_job(job: dict) -> JobProcessResult:
    payload = job.get("payload") or {}
    try:
        result = database.refresh_embeddings(
            owner_class=str(payload.get("owner_class") or "all"),
            root_name=payload.get("root_name"),
            stale_only=bool(payload.get("stale_only", True)),
            limit=int(payload.get("limit") or 100),
        )
    except ValueError as exc:
        return JobProcessResult(status="failed", message=str(exc))
    telemetry = {
        "embedding_vectors": int(result.get("vectors") or 0),
        "embedding_skipped_unchanged": int(result.get("skipped_unchanged") or 0),
        "embedding_batches": int(result.get("batches") or 0),
        "embedding_cache_hits": int(result.get("cache_hits") or 0),
        "embedding_cache_misses": int(result.get("cache_misses") or 0),
        "embedding_provider": result.get("provider"),
        "embedding_model": result.get("model"),
        "embedding_dimensions": int(result.get("dimensions") or 0),
    }
    return JobProcessResult(status="indexed", telemetry=telemetry)


def _is_locked_error(exc: OSError) -> bool:
    text = str(exc).lower()
    return isinstance(exc, PermissionError) or "locked" in text or "being used by another process" in text


def _enforce_strict_indexing_result(result: object, *, strict_indexing: bool) -> object:
    if not strict_indexing:
        return result
    if getattr(result, "status", None) == "indexed":
        metadata = _strict_indexed_metadata(dict(getattr(result, "metadata", {}) or {}))
        return ExtractionResult(
            status="indexed",
            chunks=tuple(getattr(result, "chunks", ()) or ()),
            child_assets=tuple(getattr(result, "child_assets", ()) or ()),
            metadata=metadata,
            message=getattr(result, "message", None),
        )
    if getattr(result, "status", None) != "metadata_only":
        return result
    metadata = dict(getattr(result, "metadata", {}) or {})
    decorative = metadata.get("decorative")
    if isinstance(decorative, dict) and decorative.get("status") == "skipped":
        metadata.update(
            {
                "strict_indexing": True,
                "decorative_indexed": True,
                "readiness_status": "completed_no_content",
                "no_content_reason": "decorative_image",
                "original_status": "metadata_only",
            }
        )
        return ExtractionResult(status="indexed", chunks=(), child_assets=(), metadata=metadata, message=getattr(result, "message", None))
    child_assets = tuple(getattr(result, "child_assets", ()) or ())
    if _strict_metadata_only_container_has_extracted_children(metadata, child_assets):
        metadata.update({"strict_indexing": True, "container_children_indexed": True})
        if _metadata_int(metadata, "skipped_child_count") > 0 or metadata.get("warnings"):
            metadata.update({"partial_extraction": True, "readiness_status": "completed_partial"})
        return ExtractionResult(
            status="indexed",
            chunks=tuple(getattr(result, "chunks", ()) or ()),
            child_assets=child_assets,
            metadata=metadata,
            message=getattr(result, "message", None),
        )
    no_content_reason = _strict_completed_no_content_reason(metadata)
    if no_content_reason is not None:
        metadata.update(
            {
                "strict_indexing": True,
                "readiness_status": "completed_no_content",
                "no_content_reason": no_content_reason,
                "original_status": "metadata_only",
            }
        )
        return ExtractionResult(status="indexed", chunks=(), child_assets=(), metadata=metadata, message=getattr(result, "message", None))
    message = strict_metadata_only_message(getattr(result, "message", None))
    metadata.update(
        {
            "strict_indexing": True,
            "metadata_only_blocked": True,
            "readiness_status": "blocked_missing_dependency",
            "readiness_reason": message,
            "original_status": "metadata_only",
        }
    )
    return ExtractionResult(status="blocked_missing_dependency", chunks=(), child_assets=(), metadata=metadata, message=message)


def _strict_metadata_only_container_has_extracted_children(metadata: dict[str, Any], child_assets: tuple[Any, ...]) -> bool:
    if metadata.get("extractor") != "container":
        return False
    if _metadata_int(metadata, "blocked_dependency_count") > 0:
        return False
    if _metadata_int(metadata, "parsed_child_count") > 0:
        return True
    return any(str(getattr(child, "extraction_status", "")) == "indexed" for child in child_assets)


def _strict_indexed_metadata(metadata: dict[str, Any]) -> dict[str, Any]:
    metadata.pop("metadata_only_blocked", None)
    if metadata.get("readiness_status") == "blocked_missing_dependency":
        metadata["readiness_status"] = "indexed"
        metadata["readiness_reason"] = "content_extracted"
    metadata["strict_indexing"] = True
    metadata.setdefault("readiness_status", "indexed")
    metadata.setdefault("readiness_reason", "content_extracted")
    return metadata


def _strict_completed_no_content_reason(metadata: dict[str, Any]) -> str | None:
    extractor = str(metadata.get("extractor") or "")
    if extractor == "container":
        if (
            _metadata_int(metadata, "blocked_dependency_count") == 0
            and _metadata_int(metadata, "parsed_child_count") == 0
            and _metadata_int(metadata, "skipped_member_size_limit_count") > 0
        ):
            return "archive_members_exceeded_size_limit"
        return None
    if extractor == "image":
        ocr = metadata.get("ocr")
        vision = metadata.get("vision")
        vision_escalation = str(metadata.get("vision_escalation") or "")
        ocr_completed = isinstance(ocr, dict) and str(ocr.get("status") or "") in {"completed", "cache_hit"}
        vision_finished = vision_escalation in {"no_content", "unavailable", "ineligible"}
        if ocr_completed and vision_finished and not _metadata_has_vision_description(vision):
            return "image_ocr_and_vision_empty"
        return None
    if extractor == "pdf":
        ocr = metadata.get("ocr")
        if isinstance(ocr, dict) and str(ocr.get("status") or "") in {"completed", "cache_hit"}:
            return "pdf_text_and_ocr_empty"
        return None
    if extractor in {
        "docx",
        "pptx",
        "xlsx",
        "xlsm",
        "xltx",
        "xltm",
        "libreoffice",
        "excel_com",
        "powerpoint_com",
    } and not metadata.get("warnings"):
        return f"{extractor}_empty"
    return None


def _metadata_has_vision_description(vision: object) -> bool:
    return isinstance(vision, dict) and _metadata_int(vision, "descriptions") > 0


def _metadata_int(metadata: dict[str, Any], key: str) -> int:
    try:
        return int(metadata.get(key) or 0)
    except (TypeError, ValueError):
        return 0


def _telemetry_from_extraction_result(result: object) -> dict[str, Any]:
    metadata = getattr(result, "metadata", None)
    if not isinstance(metadata, dict):
        return {}
    telemetry: dict[str, Any] = {}
    parser_cache = metadata.get("parser_cache")
    if isinstance(parser_cache, dict):
        if "hits" in parser_cache:
            telemetry["parser_cache_hits"] = int(parser_cache.get("hits") or 0)
        if "misses" in parser_cache:
            telemetry["parser_cache_misses"] = int(parser_cache.get("misses") or 0)
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
        if "duration_seconds" in asr:
            telemetry["asr_duration_seconds"] = int(float(asr.get("duration_seconds") or 0))
        if "sidecar_used" in asr:
            telemetry["asr_sidecar_used"] = bool(asr.get("sidecar_used"))
        if "source" in asr:
            telemetry["asr_source"] = str(asr.get("source") or "")[:80]
    decorative = metadata.get("decorative")
    if isinstance(decorative, dict) and decorative.get("status") == "skipped":
        telemetry["decorative_image_skips"] = 1
    vision = metadata.get("vision")
    if isinstance(vision, dict):
        if "cache_hits" in vision:
            telemetry["vision_cache_hits"] = int(vision.get("cache_hits") or 0)
        if "cache_misses" in vision:
            telemetry["vision_cache_misses"] = int(vision.get("cache_misses") or 0)
        if "descriptions" in vision:
            telemetry["vision_descriptions"] = int(vision.get("descriptions") or 0)
        if "blocked_dependency_count" in vision:
            telemetry["vision_blocked_dependency_count"] = int(vision.get("blocked_dependency_count") or 0)
    frame_sampling = metadata.get("frame_sampling")
    if isinstance(frame_sampling, dict):
        if "frame_count" in frame_sampling:
            telemetry["frame_sample_count"] = int(frame_sampling.get("frame_count") or 0)
        if "thumbnail_cache_hits" in frame_sampling:
            telemetry["thumbnail_cache_hits"] = int(frame_sampling.get("thumbnail_cache_hits") or 0)
        if "thumbnail_cache_misses" in frame_sampling:
            telemetry["thumbnail_cache_misses"] = int(frame_sampling.get("thumbnail_cache_misses") or 0)
        if isinstance(frame_sampling.get("timestamps"), list):
            telemetry["frame_sample_timestamps"] = [float(value) for value in frame_sampling.get("timestamps", [])[:20]]
    if metadata.get("blocked_dependency_reason"):
        telemetry["blocked_dependency_reason"] = str(metadata.get("blocked_dependency_reason"))[:120]
    for source_key, telemetry_key in {
        "message_count": "mail_message_count",
        "event_count": "calendar_event_count",
        "contact_count": "contact_count",
        "finding_count": "report_finding_count",
        "test_count": "report_test_count",
        "entry_count": "har_entry_count",
        "table_count": "database_table_count",
        "component_count": "report_component_count",
        "package_count": "report_package_count",
        "covered_line_count": "coverage_covered_line_count",
        "line_count": "coverage_line_count",
    }.items():
        if source_key in metadata:
            telemetry[telemetry_key] = int(metadata.get(source_key) or 0)
    if metadata.get("sensitive") is True:
        telemetry["sensitive_metadata"] = True
    if metadata.get("extractor") == "container":
        for source_key, telemetry_key in {
            "member_count": "container_member_count",
            "parsed_child_count": "container_parsed_child_count",
            "skipped_child_count": "container_skipped_child_count",
            "blocked_dependency_count": "container_blocked_dependency_count",
            "max_depth": "container_max_depth",
            "vision_cache_hits": "vision_cache_hits",
            "vision_cache_misses": "vision_cache_misses",
            "vision_descriptions": "vision_descriptions",
            "vision_blocked_dependency_count": "vision_blocked_dependency_count",
            "decorative_image_skips": "decorative_image_skips",
            "frame_sample_count": "frame_sample_count",
            "thumbnail_cache_hits": "thumbnail_cache_hits",
            "thumbnail_cache_misses": "thumbnail_cache_misses",
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
