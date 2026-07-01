from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

from . import database, host_vss
from .crawler import AssetChunk, CorpusPolicy, _is_included, strict_indexing_enabled, strict_metadata_only_message
from .extractors import (
    ExtractionResult,
    extract_file,
    extract_media_segment,
    extract_pdf_ocr_pages,
    extract_video_frames,
    plan_staged_media_extraction,
    plan_staged_pdf_extraction,
)
from .glob_policy import effective_glob_policy
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

    job_id = str(job.get("id") or "").strip()
    if job_id:
        try:
            job_running = database.corpus_job_is_running(job_id)
        except Exception:
            job_running = True
        if not job_running:
            return _cancelled_unseen_result(
                "corpus job was cancelled before extraction started",
                reason="cancelled_before_extraction",
            )

    root = database.get_monitored_root(root_name)
    if root is None:
        return JobProcessResult(status="cancelled_orphaned_root", message=f"monitored root not found: {root_name}")

    root_metadata = root.get("metadata") if isinstance(root.get("metadata"), dict) else {}
    strict_indexing = strict_indexing_enabled(root_metadata)
    glob_policy = _configured_glob_policy(root)
    policy = CorpusPolicy(
        root_path=Path(root["root_path"]),
        recursive=root["recursive"],
        include_globs=tuple(glob_policy["include_globs"]),
        exclude_globs=tuple(glob_policy["exclude_globs"]),
        strict_indexing=strict_indexing,
        max_inline_bytes=root["max_inline_bytes"],
        heavy_threshold_bytes=root["heavy_threshold_bytes"],
        **_configured_container_limits(),
    )
    if not _is_included(str(relative_path), policy, []):
        return _cancelled_unseen_result(
            f"source asset is no longer included by root policy: {relative_path}",
            reason="excluded_by_policy",
        )

    path = Path(root["root_path"]) / relative_path
    if not path.exists():
        return JobProcessResult(
            status="cancelled_missing_source",
            message=f"source file not found: {relative_path}",
            telemetry={"missing_source": True, "missing_source_deleted": True},
        )

    job_type = str(job.get("job_type") or "")
    try:
        result = _extract_for_corpus_job(job_type, path, policy, payload)
    except OSError as exc:
        if _is_locked_error(exc):
            vss_result = _extract_locked_file_with_vss(
                job_type=job_type,
                path=path,
                policy=policy,
                payload=payload,
                root_metadata=root_metadata,
            )
            if isinstance(vss_result, JobProcessResult):
                return vss_result
            if vss_result is not None:
                result = vss_result
            else:
                return JobProcessResult(status="retrying_locked", message=str(exc))
        else:
            raise
    except host_vss.VssSnapshotError as exc:
        return JobProcessResult(status="retrying_vss_failed", message=str(exc), telemetry=_telemetry_from_vss_error(exc))
    except Exception as exc:
        return JobProcessResult(
            status="failed",
            message=str(exc),
            telemetry={"error_type": exc.__class__.__name__},
        )
    result = _enforce_strict_indexing_result(result, strict_indexing=strict_indexing)
    staged_child = _is_staged_child_job(job_type)
    if result.status == "staged":
        if job_id:
            apply = database.apply_staged_extraction_piece_for_job if staged_child else database.apply_staged_extraction_plan_for_job
            applied = apply(
                job_id=job_id,
                root_name=root_name,
                relative_path=relative_path,
                result=result,
            )
            if not applied:
                return _cancelled_unseen_result(
                    "corpus job was cancelled before staged extraction results were applied",
                    reason="cancelled_during_extraction",
                )
    elif staged_child and result.status in {"indexed", "metadata_only", "blocked_missing_dependency"}:
        if job_id:
            applied = database.apply_staged_extraction_piece_for_job(
                job_id=job_id,
                root_name=root_name,
                relative_path=relative_path,
                result=result,
            )
            if not applied:
                return _cancelled_unseen_result(
                    "corpus job was cancelled before staged extraction results were applied",
                    reason="cancelled_during_extraction",
                )
    elif result.status in {"indexed", "metadata_only", "blocked_missing_dependency"}:
        if job_id:
            applied = database.apply_extraction_result_for_job(
                job_id=job_id,
                root_name=root_name,
                relative_path=relative_path,
                result=result,
            )
            if not applied:
                return _cancelled_unseen_result(
                    "corpus job was cancelled before extraction results were applied",
                    reason="cancelled_during_extraction",
                )
        else:
            database.apply_extraction_result(root_name=root_name, relative_path=relative_path, result=result)
    return JobProcessResult(status=result.status, message=getattr(result, "message", None), telemetry=_telemetry_from_extraction_result(result))


def _extract_locked_file_with_vss(
    *,
    job_type: str,
    path: Path,
    policy: CorpusPolicy,
    payload: dict[str, Any],
    root_metadata: dict[str, Any],
) -> ExtractionResult | JobProcessResult | None:
    if not _configured_host_vss_enabled() or not _root_uses_host_agent(root_metadata):
        return None
    snapshot_telemetry: dict[str, Any] = {}
    try:
        with host_vss.snapshot_path(
            path,
            max_file_bytes=_configured_host_vss_max_file_bytes(),
            timeout_seconds=_configured_host_vss_timeout_seconds(),
        ) as snapshot:
            snapshot_telemetry = snapshot.telemetry
            result = _extract_for_corpus_job(job_type, snapshot.path, policy, payload)
            if _extraction_result_rejected_vss_tool_path(result):
                return _vss_tool_rejection_result(getattr(result, "message", None), snapshot_telemetry)
    except host_vss.VssSnapshotError as exc:
        telemetry = _telemetry_from_vss_error(exc)
        if exc.reason in {"not_windows", "not_local_volume"}:
            return None
        return JobProcessResult(status="retrying_vss_failed", message=str(exc), telemetry=telemetry)
    except OSError as exc:
        if _is_locked_error(exc):
            return JobProcessResult(
                status="retrying_vss_failed",
                message=str(exc),
                telemetry={"vss_status": "failed", "vss_reason": "shadow_locked"},
            )
        if _is_vss_tool_path_rejection(exc):
            return _vss_tool_rejection_result(str(exc), snapshot_telemetry)
        return JobProcessResult(
            status="failed",
            message=str(exc),
            telemetry={"error_type": exc.__class__.__name__, "vss_status": "completed", "vss_reason": "snapshot_created"},
        )
    except Exception as exc:
        if _is_vss_tool_path_rejection(exc):
            return _vss_tool_rejection_result(str(exc), snapshot_telemetry)
        return JobProcessResult(
            status="failed",
            message=str(exc),
            telemetry={"error_type": exc.__class__.__name__},
        )
    return _with_vss_fallback_metadata(result, snapshot.telemetry)


def _extract_for_corpus_job(job_type: str, path: Path, policy: CorpusPolicy, payload: dict[str, Any]) -> ExtractionResult:
    if job_type == "corpus_extract_media_segment":
        return extract_media_segment(path, payload)
    if job_type == "corpus_extract_video_frames":
        return extract_video_frames(path, payload)
    if job_type == "corpus_extract_pdf_ocr_pages":
        return extract_pdf_ocr_pages(path, payload)
    if job_type == "corpus_extract_audio":
        return plan_staged_media_extraction(path, "audio")
    if job_type == "corpus_extract_video":
        return plan_staged_media_extraction(path, "video")
    if job_type == "corpus_extract_pdf" or (job_type == "corpus_extract_document" and path.suffix.lower() == ".pdf"):
        return plan_staged_pdf_extraction(path, policy)
    relative_path = _payload_relative_path_for_extraction(path, policy, payload)
    if relative_path is not None:
        return extract_file(path, policy, relative_path=relative_path)
    return extract_file(path, policy)


def _is_staged_child_job(job_type: str) -> bool:
    return job_type in {"corpus_extract_media_segment", "corpus_extract_video_frames", "corpus_extract_pdf_ocr_pages"}


def _payload_relative_path_for_extraction(path: Path, policy: CorpusPolicy, payload: dict[str, Any]) -> str | None:
    relative_path = str(payload.get("path") or "").strip()
    if not relative_path:
        return None
    original_path = Path(policy.root_path) / relative_path
    if path == original_path:
        return None
    try:
        if path.resolve() == original_path.resolve():
            return None
    except OSError:
        pass
    return relative_path


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


def _root_uses_host_agent(root_metadata: dict[str, Any]) -> bool:
    return str(root_metadata.get("host_access") or "") == "host_agent"


def _with_vss_fallback_metadata(result: ExtractionResult, telemetry: dict[str, Any]) -> ExtractionResult:
    metadata = dict(getattr(result, "metadata", {}) or {})
    metadata["vss_fallback"] = _sanitize_vss_telemetry(telemetry)
    return ExtractionResult(
        status=result.status,
        chunks=tuple(getattr(result, "chunks", ()) or ()),
        child_assets=tuple(getattr(result, "child_assets", ()) or ()),
        metadata=metadata,
        message=getattr(result, "message", None),
    )


def _sanitize_vss_telemetry(telemetry: dict[str, Any] | None) -> dict[str, Any]:
    source = telemetry if isinstance(telemetry, dict) else {}
    sanitized: dict[str, Any] = {}
    for key in (
        "status",
        "reason",
        "shadow_id",
        "volume",
        "return_value",
        "return_code",
        "size_bytes",
        "max_file_bytes",
        "error_type",
    ):
        if key not in source:
            continue
        value = source[key]
        if key in {"return_value", "return_code", "size_bytes", "max_file_bytes"}:
            try:
                sanitized[key] = int(value)
            except (TypeError, ValueError):
                continue
        else:
            sanitized[key] = str(value or "")[:120]
    return sanitized


def _telemetry_from_vss_error(exc: host_vss.VssSnapshotError) -> dict[str, Any]:
    vss = _sanitize_vss_telemetry(exc.telemetry)
    vss.setdefault("status", "failed")
    vss.setdefault("reason", exc.reason)
    telemetry: dict[str, Any] = {
        "vss_status": str(vss.get("status") or "failed")[:80],
        "vss_reason": str(vss.get("reason") or exc.reason)[:120],
    }
    if "return_value" in vss:
        telemetry["vss_return_value"] = int(vss["return_value"])
    if "return_code" in vss:
        telemetry["vss_return_code"] = int(vss["return_code"])
    if "size_bytes" in vss:
        telemetry["vss_size_bytes"] = int(vss["size_bytes"])
    if "max_file_bytes" in vss:
        telemetry["vss_max_file_bytes"] = int(vss["max_file_bytes"])
    return telemetry


def _telemetry_from_vss_snapshot(telemetry: dict[str, Any]) -> dict[str, Any]:
    vss = _sanitize_vss_telemetry(telemetry)
    result: dict[str, Any] = {}
    if "status" in vss:
        result["vss_status"] = str(vss.get("status") or "")[:80]
    if "reason" in vss:
        result["vss_reason"] = str(vss.get("reason") or "")[:120]
    if "return_value" in vss:
        result["vss_return_value"] = int(vss["return_value"])
    return result


def _vss_tool_rejection_result(message: object, telemetry: dict[str, Any]) -> JobProcessResult:
    return JobProcessResult(
        status="retrying_locked",
        message=str(message or "tool rejected VSS shadow path"),
        telemetry={**_telemetry_from_vss_snapshot(telemetry), "vss_tool_path_rejected": True},
    )


def _extraction_result_rejected_vss_tool_path(result: ExtractionResult) -> bool:
    return str(getattr(result, "status", "") or "") == "failed" and _is_vss_tool_path_rejection_text(getattr(result, "message", None))


def _is_vss_tool_path_rejection(exc: BaseException) -> bool:
    return _is_vss_tool_path_rejection_text(str(exc))


def _is_vss_tool_path_rejection_text(value: object) -> bool:
    text = str(value or "").lower().replace("/", "\\")
    return "globalroot\\device" in text or "harddiskvolumeshadowcopy" in text


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
        svg = metadata.get("svg")
        if isinstance(svg, dict) and str(svg.get("kind") or "") == "font":
            return "svg_font_metadata_only"
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
    vss = metadata.get("vss_fallback")
    if isinstance(vss, dict):
        if "status" in vss:
            telemetry["vss_status"] = str(vss.get("status") or "")[:80]
        if "reason" in vss:
            telemetry["vss_reason"] = str(vss.get("reason") or "")[:120]
        if "return_value" in vss:
            telemetry["vss_return_value"] = int(vss.get("return_value") or 0)
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
        if "status" in vision:
            telemetry["vision_status"] = str(vision.get("status") or "")[:80]
        if "error" in vision:
            telemetry["vision_error"] = str(vision.get("error") or "")[:240]
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
    staged = metadata.get("staged_extraction")
    if isinstance(staged, dict):
        telemetry["stage"] = str(staged.get("status") or "staged")[:80]
        telemetry["staged_complete"] = bool(staged.get("complete")) if "complete" in staged else False
        if "pending_job_count" in staged:
            telemetry["staged_job_count"] = int(staged.get("pending_job_count") or 0)
        if "chunks_written" in staged:
            telemetry["chunks_written"] = int(staged.get("chunks_written") or 0)
        if "chunks_seen" in staged:
            telemetry["chunks_seen"] = int(staged.get("chunks_seen") or 0)
        if "page_start" in staged:
            telemetry["page_start"] = int(staged.get("page_start") or 0)
        if "page_end" in staged:
            telemetry["page_end"] = int(staged.get("page_end") or 0)
        if "page_count" in staged:
            telemetry["page_count"] = int(staged.get("page_count") or 0)
        if "segment_index" in staged:
            telemetry["segment_index"] = int(staged.get("segment_index") or 0)
        if "duration_seconds" in staged and staged.get("duration_seconds") is not None:
            telemetry["duration_seconds"] = int(float(staged.get("duration_seconds") or 0))
        next_job = staged.get("next_job")
        if isinstance(next_job, dict):
            telemetry["next_job_type"] = str(next_job.get("job_type") or "")[:120]
        elif "next_job_type" in staged:
            telemetry["next_job_type"] = str(staged.get("next_job_type") or "")[:120]
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


def _cancelled_unseen_result(message: str, *, reason: str) -> JobProcessResult:
    return JobProcessResult(
        status=database.UNSEEN_ASSET_CANCELLED_STATUS,
        message=message,
        telemetry={"unseen_reason": reason, "cancelled_unseen_asset": True},
    )


def _configured_glob_policy(root: dict[str, Any]) -> dict[str, Any]:
    settings = SettingsService()
    try:
        global_include = settings.resolve("crawler.global_include_globs").raw_value
    except Exception:
        global_include = []
    try:
        global_exclude = settings.resolve("crawler.global_exclude_globs").raw_value
    except Exception:
        global_exclude = []
    return effective_glob_policy(root, global_include=global_include, global_exclude=global_exclude)


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


def _configured_host_vss_enabled() -> bool:
    try:
        return bool(SettingsService().resolve("host_agent.vss_enabled").raw_value)
    except Exception:
        return True


def _configured_host_vss_max_file_bytes() -> int:
    try:
        return int(SettingsService().resolve("host_agent.vss_max_file_bytes").raw_value)
    except Exception:
        return 512 * 1024 * 1024


def _configured_host_vss_timeout_seconds() -> int:
    try:
        return int(SettingsService().resolve("host_agent.vss_timeout_seconds").raw_value)
    except Exception:
        return 30
