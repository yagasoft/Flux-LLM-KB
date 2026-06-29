from __future__ import annotations

from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
import fnmatch
import hashlib
import mimetypes
from pathlib import Path
import time
from typing import Any, Callable, Iterable

from .code_index import CODE_LANGUAGE_EXTENSIONS, DEVELOPER_ARTIFACT_NAMES, is_code_like_path
from .redaction import redact_text


MARKER_FILES = (".gitignore", ".fluxignore", ".fluxkbignore", ".exclude.codex")
TEXT_EXTENSIONS = {
    ".adoc",
    ".cfg",
    ".conf",
    ".cs",
    ".css",
    ".csv",
    ".html",
    ".ini",
    ".java",
    ".js",
    ".json",
    ".jsonl",
    ".md",
    ".ps1",
    ".py",
    ".rs",
    ".sql",
    ".toml",
    ".ts",
    ".txt",
    ".xml",
    ".yaml",
    ".yml",
}
CODE_EXTENSIONS = set(CODE_LANGUAGE_EXTENSIONS)
SUBTITLE_EXTENSIONS = {".ass", ".dfxp", ".sbv", ".srt", ".ssa", ".ttml", ".vtt"}
MAIL_EXTENSIONS = {".eml", ".mbox", ".msg"}
CALENDAR_EXTENSIONS = {".ics", ".ical", ".ifb"}
CONTACT_EXTENSIONS = {".vcf", ".vcard"}
STRUCTURED_DATA_EXTENSIONS = {".jsonld", ".ndjson", ".psv", ".ssv"}
REPORT_EXTENSIONS = {
    ".burp",
    ".cyclonedx",
    ".har",
    ".lcov",
    ".nessus",
    ".nmap",
    ".sarif",
    ".spdx",
    ".tap",
    ".trx",
    ".zap",
}
REPORT_NAMES = {"cobertura.xml", "coverage.xml", "junit.xml", "results.xml"}
DATABASE_EXTENSIONS = {".accdb", ".db", ".dbf", ".duckdb", ".fdb", ".mdb", ".sqlite", ".sqlite3"}
GEOSPATIAL_EXTENSIONS = {".geojson", ".gpx", ".gpkg", ".kml", ".kmz", ".mbtiles", ".shp", ".topojson"}
CAD_EXTENSIONS = {".dae", ".dgn", ".dwg", ".dxf", ".fbx", ".gltf", ".glb", ".ifc", ".ifczip", ".obj", ".rfa", ".rvt", ".skp", ".stl", ".stp", ".step", ".usd", ".usda", ".usdc", ".usdz"}
SCIENTIFIC_EXTENSIONS = {".fits", ".h5", ".hdf5", ".mat", ".nc", ".netcdf", ".npy", ".npz"}
SENSITIVE_METADATA_EXTENSIONS = {".age", ".cer", ".crt", ".gpg", ".jks", ".kdbx", ".key", ".kstore", ".p12", ".pem", ".pfx", ".pgp"}
DEFERRED_LOCAL_PARSER_KINDS = {
    "calendar",
    "cad",
    "contact",
    "database",
    "geospatial",
    "mail",
    "report",
    "scientific",
    "sensitive_metadata",
    "structured_data",
    "subtitle",
}
DOCUMENT_EXTENSIONS = {
    ".azw",
    ".azw3",
    ".doc",
    ".docm",
    ".docx",
    ".dot",
    ".dotm",
    ".dotx",
    ".odp",
    ".ods",
    ".odt",
    ".epub",
    ".fb2",
    ".lit",
    ".mobi",
    ".otp",
    ".ots",
    ".ott",
    ".pdf",
    ".pot",
    ".potm",
    ".potx",
    ".pps",
    ".ppsm",
    ".ppsx",
    ".ppt",
    ".pptm",
    ".pptx",
    ".rtf",
    ".xls",
    ".xlsb",
    ".xlsm",
    ".xlsx",
    ".xlt",
    ".xltm",
    ".xltx",
}
DIAGRAM_EXTENSIONS = {".dio", ".drawio", ".vsdm", ".vsdx", ".vssm", ".vssx", ".vstm", ".vstx"}
DIAGRAM_COMPOUND_SUFFIXES = (".drawio.png", ".drawio.svg")
IMAGE_EXTENSIONS = {".bmp", ".gif", ".jpeg", ".jpg", ".png", ".svg", ".tif", ".tiff", ".webp"}
AUDIO_EXTENSIONS = {".aac", ".flac", ".m4a", ".mp3", ".ogg", ".wav", ".wma"}
VIDEO_EXTENSIONS = {".avi", ".m4v", ".mkv", ".mov", ".mp4", ".webm", ".wmv"}
ARCHIVE_EXTENSIONS = {
    ".7z",
    ".ar",
    ".bz2",
    ".cb7",
    ".cbr",
    ".cbt",
    ".cbz",
    ".cab",
    ".cpio",
    ".dmg",
    ".gz",
    ".iso",
    ".lz4",
    ".rar",
    ".tar",
    ".tgz",
    ".xz",
    ".zip",
    ".zst",
}
CONTAINER_EXTENSIONS = {
    ".apk",
    ".crate",
    ".crx",
    ".deb",
    ".ear",
    ".egg",
    ".gem",
    ".ipa",
    ".jar",
    ".nupkg",
    ".rpm",
    ".vsix",
    ".war",
    ".whl",
    ".xpi",
}
ARCHIVE_COMPOUND_SUFFIXES = (".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz4")
TRANSIENT_SUFFIXES = {".tmp", ".partial", ".crdownload", ".download", ".part"}
MAIL_SPOOL_INTERNAL_FILES = {"body.html", "message.eml", "message.msg"}


@dataclass(frozen=True)
class CorpusPolicy:
    root_path: Path
    recursive: bool = True
    include_globs: tuple[str, ...] = ()
    exclude_globs: tuple[str, ...] = ()
    strict_indexing: bool = False
    max_inline_bytes: int = 256 * 1024
    heavy_threshold_bytes: int = 10 * 1024 * 1024
    hash_max_bytes: int = 512 * 1024 * 1024
    container_max_depth: int = 1
    container_max_members: int = 200
    container_max_total_bytes: int = 50 * 1024 * 1024
    container_max_member_bytes: int = 10 * 1024 * 1024
    stability_quiet_seconds: float = 0.0
    large_file_stability_quiet_seconds: float = 0.0
    hash_parallelism: int = 1
    manifest_lookup: Callable[[str], dict[str, Any] | None] | None = None
    clock: Callable[[], float] | None = None
    mail_spool: bool = False


@dataclass(frozen=True)
class FileClassification:
    file_kind: str
    extraction_tier: str
    mime_type: str | None
    reason: str | None = None


@dataclass(frozen=True)
class AssetChunk:
    chunk_index: int
    title: str
    body: str
    modality: str = "text"
    locator: str | None = None
    token_estimate: int = 0
    metadata: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class DiscoveredAsset:
    path: Path
    relative_path: str
    file_kind: str
    mime_type: str | None
    extension: str
    size_bytes: int
    mtime_ns: int
    quick_hash: str
    content_hash: str | None
    extraction_tier: str
    extraction_status: str | None = None
    chunks: tuple[AssetChunk, ...] = ()
    metadata: dict[str, object] = field(default_factory=dict)


@dataclass(frozen=True)
class CrawlPlan:
    root_path: Path
    scope_relative_path: str | None = None
    scope_is_file: bool = False
    assets: list[DiscoveredAsset] = field(default_factory=list)
    deferred_jobs: list[dict[str, str]] = field(default_factory=list)
    errors: list[dict[str, str]] = field(default_factory=list)


def scan_path(
    root_path: str | Path,
    policy: CorpusPolicy | None = None,
    *,
    target_path: str | Path | None = None,
    progress_callback: Callable[[dict[str, Any]], None] | None = None,
) -> CrawlPlan:
    root = Path(root_path).expanduser().resolve()
    active_policy = policy or CorpusPolicy(root_path=root)
    marker_patterns = _load_marker_patterns(root)
    assets: list[DiscoveredAsset] = []
    deferred_jobs: list[dict[str, str]] = []
    errors: list[dict[str, str]] = []
    target = Path(target_path).expanduser().resolve() if target_path else None
    scope_relative_path = _scope_relative_path(root, target) if target else None
    scope_is_file = bool(target and target.is_file())
    entries: list[tuple[str, Path | DiscoveredAsset]] = []
    paths = sorted(
        _iter_files(root, recursive=active_policy.recursive, target=target, policy=active_policy, marker_patterns=marker_patterns),
        key=lambda item: item.relative_to(root).as_posix().lower(),
    )
    _emit_progress(progress_callback, stage="enumerated", files_total=len(paths))

    files_skipped = 0
    skipped_top_dirs: Counter[str] = Counter()
    for path in paths:
        try:
            relative_path = path.relative_to(root).as_posix()
            if not _is_included(relative_path, active_policy, marker_patterns):
                files_skipped += 1
                skipped_top_dirs[relative_path.split("/", 1)[0]] += 1
                continue
            if active_policy.mail_spool and _is_mail_spool_internal_artifact(relative_path):
                files_skipped += 1
                continue
            if _should_wait_for_stability(path, root, active_policy):
                entries.append(("asset", _status_asset(path, root, "pending_stable", "mtime_not_stable", active_policy)))
                continue
            entries.append(("path", path))
        except Exception as exc:
            if _is_locked_error(exc):
                entries.append(("asset", _status_asset(path, root, "retrying_locked", "file_locked", active_policy, error=str(exc))))
                continue
            errors.append({"path": str(path), "error": str(exc)})

    _emit_progress(
        progress_callback,
        stage="filtered",
        files_total=len(paths),
        files_seen=0,
        files_candidate=len(entries),
        files_skipped=files_skipped,
        top_skipped_dirs=[{"path": path, "count": count} for path, count in skipped_top_dirs.most_common(10)],
        errors=len(errors),
    )
    stable_paths = [entry[1] for entry in entries if entry[0] == "path"]
    _emit_progress(progress_callback, stage="hashing", files_total=len(stable_paths), files_seen=0)
    precomputed_hashes = _precompute_content_hashes(stable_paths, root, active_policy)

    for entry_type, entry in entries:
        try:
            if entry_type == "asset":
                assets.append(entry)  # type: ignore[arg-type]
                continue
            path = entry  # type: ignore[assignment]
            hash_precomputed = path in precomputed_hashes
            if hash_precomputed:
                asset = discover_asset(
                    path,
                    root,
                    active_policy,
                    content_hash_override=precomputed_hashes.get(path),
                    content_hash_precomputed=True,
                )
            else:
                asset = discover_asset(path, root, active_policy)
            assets.append(asset)
            if asset.extraction_tier == "deferred":
                deferred_jobs.append(
                    {
                        "job_type": f"corpus_extract_{asset.file_kind}",
                        "relative_path": asset.relative_path,
                        "reason": "heavy_file" if asset.size_bytes > active_policy.heavy_threshold_bytes else "deferred_extractor",
                    }
                )
        except Exception as exc:
            if _is_locked_error(exc):
                assets.append(_status_asset(path, root, "retrying_locked", "file_locked", active_policy, error=str(exc)))
                continue
            errors.append({"path": str(path), "error": str(exc)})

    _emit_progress(
        progress_callback,
        stage="discovered",
        files_total=len(entries),
        files_seen=len(assets),
        jobs_queued=len(deferred_jobs),
        errors=len(errors),
    )
    return CrawlPlan(
        root_path=root,
        scope_relative_path=scope_relative_path,
        scope_is_file=scope_is_file,
        assets=assets,
        deferred_jobs=deferred_jobs,
        errors=errors,
    )


def _emit_progress(callback: Callable[[dict[str, Any]], None] | None, **payload: Any) -> None:
    if callback is None:
        return
    try:
        callback(dict(payload))
    except Exception:
        return


def discover_asset(
    path: Path,
    root: Path,
    policy: CorpusPolicy,
    *,
    content_hash_override: str | None = None,
    content_hash_precomputed: bool = False,
) -> DiscoveredAsset:
    resolved = path.resolve()
    stat = resolved.stat()
    classification = classify_file(resolved, policy)
    relative_path = resolved.relative_to(root.resolve()).as_posix()
    quick_hash = _quick_hash(resolved, stat.st_size, stat.st_mtime_ns)
    metadata: dict[str, object] = {}
    extraction_status: str | None = None
    manifest = policy.manifest_lookup(relative_path) if policy.manifest_lookup else None
    manifest_unchanged = _manifest_matches(manifest, size_bytes=stat.st_size, mtime_ns=stat.st_mtime_ns, quick_hash=quick_hash)
    repair_missing_chunks = False
    if manifest_unchanged:
        content_hash = str(manifest.get("content_hash") or "") or None
        metadata["manifest_skipped_unchanged"] = True
        repair_missing_chunks = _manifest_reports_missing_chunks(manifest, extraction_tier=classification.extraction_tier)
        if repair_missing_chunks:
            metadata["manifest_repaired_missing_chunks"] = True
    elif content_hash_precomputed:
        content_hash = content_hash_override
    else:
        content_hash = _sha256_file(resolved) if stat.st_size <= policy.hash_max_bytes else None
    chunks: tuple[AssetChunk, ...] = ()
    if classification.file_kind == "image":
        from .extractors import image_metadata

        metadata.update(image_metadata(resolved))
    elif classification.extraction_tier == "inline" and (not manifest_unchanged or repair_missing_chunks):
        from .extractors import extract_file

        extraction = extract_file(resolved, policy)
        metadata.update(extraction.metadata)
        chunks = extraction.chunks
        if extraction.status != "indexed":
            extraction_status = extraction.status
            if policy.strict_indexing and extraction.status == "metadata_only":
                extraction_status = "blocked_missing_dependency"
                metadata.update(_strict_metadata_only_metadata(extraction.message))
    if policy.strict_indexing and classification.extraction_tier == "metadata_only":
        extraction_status = "blocked_missing_dependency"
        metadata.update(_strict_metadata_only_metadata())
    return DiscoveredAsset(
        path=resolved,
        relative_path=relative_path,
        file_kind=classification.file_kind,
        mime_type=classification.mime_type,
        extension=resolved.suffix.lower(),
        size_bytes=stat.st_size,
        mtime_ns=stat.st_mtime_ns,
        quick_hash=quick_hash,
        content_hash=content_hash,
        extraction_tier=classification.extraction_tier,
        extraction_status=extraction_status,
        chunks=chunks,
        metadata=metadata,
    )


def _manifest_reports_missing_chunks(manifest: dict[str, object] | None, *, extraction_tier: str) -> bool:
    if extraction_tier != "inline" or not manifest:
        return False
    if str(manifest.get("source_asset_status") or "") != "indexed":
        return False
    if "chunk_count" not in manifest:
        return False
    try:
        return int(manifest.get("chunk_count") or 0) <= 0
    except (TypeError, ValueError):
        return False


def classify_file(path: str | Path, policy: CorpusPolicy) -> FileClassification:
    file_path = Path(path)
    ext = file_path.suffix.lower()
    size = file_path.stat().st_size
    mime_type, _ = mimetypes.guess_type(file_path.name)
    file_kind = _file_kind(file_path, mime_type)

    if file_kind in {"archive", "container", "diagram", "image", "audio", "video"} | DEFERRED_LOCAL_PARSER_KINDS:
        return FileClassification(file_kind=file_kind, extraction_tier="deferred", mime_type=mime_type)
    if size > policy.heavy_threshold_bytes:
        return FileClassification(
            file_kind=file_kind,
            extraction_tier="deferred",
            mime_type=mime_type,
            reason="heavy_file",
        )
    if file_kind in {"text", "code"} and size <= policy.max_inline_bytes:
        return FileClassification(file_kind=file_kind, extraction_tier="inline", mime_type=mime_type)
    if ext in DOCUMENT_EXTENSIONS:
        return FileClassification(file_kind=file_kind, extraction_tier="deferred", mime_type=mime_type)
    return FileClassification(file_kind=file_kind, extraction_tier="metadata_only", mime_type=mime_type)


def _precompute_content_hashes(paths: list[Path], root: Path, policy: CorpusPolicy) -> dict[Path, str | None]:
    parallelism = max(1, int(policy.hash_parallelism or 1))
    if parallelism <= 1 or len(paths) <= 1:
        return {}

    targets: list[Path] = []
    hashes: dict[Path, str | None] = {}
    for path in paths:
        try:
            resolved = path.resolve()
            stat = resolved.stat()
            relative_path = resolved.relative_to(root.resolve()).as_posix()
            quick_hash = _quick_hash(resolved, stat.st_size, stat.st_mtime_ns)
            manifest = policy.manifest_lookup(relative_path) if policy.manifest_lookup else None
            manifest_unchanged = _manifest_matches(
                manifest,
                size_bytes=stat.st_size,
                mtime_ns=stat.st_mtime_ns,
                quick_hash=quick_hash,
            )
            if manifest_unchanged:
                hashes[path] = str(manifest.get("content_hash") or "") or None
            elif stat.st_size <= policy.hash_max_bytes:
                targets.append(path)
            else:
                hashes[path] = None
        except OSError:
            continue

    if not targets:
        return hashes

    with ThreadPoolExecutor(max_workers=min(parallelism, len(targets))) as executor:
        for path, content_hash in zip(targets, executor.map(lambda item: _sha256_file(item.resolve()), targets)):
            hashes[path] = content_hash
    return hashes


def _is_mail_spool_internal_artifact(relative_path: str) -> bool:
    parts = [part for part in relative_path.replace("\\", "/").split("/") if part]
    return len(parts) == 2 and parts[1].lower() in MAIL_SPOOL_INTERNAL_FILES


def _iter_files(
    root: Path,
    *,
    recursive: bool,
    target: Path | None = None,
    policy: CorpusPolicy | None = None,
    marker_patterns: list[str] | None = None,
) -> Iterable[Path]:
    active_policy = policy or CorpusPolicy(root_path=root)
    active_marker_patterns = marker_patterns or []

    def walk(directory: Path) -> Iterable[Path]:
        try:
            children = sorted(directory.iterdir(), key=lambda item: item.name.lower())
        except OSError:
            return
        for child in children:
            if child.is_dir():
                if recursive and not _should_prune_directory(child, root, active_policy, active_marker_patterns):
                    yield from walk(child)
                continue
            if child.is_file() and child.name not in MARKER_FILES and not _is_transient_artifact(child):
                yield child

    if target is not None:
        if not _is_relative_to(target, root):
            raise ValueError(f"target path is outside monitored root: {target}")
        if target.is_file() and not _is_transient_artifact(target):
            return iter([target])
        if not target.exists():
            return iter(())
        if not target.is_dir() or _should_prune_directory(target, root, active_policy, active_marker_patterns):
            return iter(())
        return walk(target) if recursive else (
            path for path in target.iterdir() if path.is_file() and path.name not in MARKER_FILES and not _is_transient_artifact(path)
        )
    return walk(root) if recursive else (
        path for path in root.iterdir() if path.is_file() and path.name not in MARKER_FILES and not _is_transient_artifact(path)
    )


def _should_prune_directory(
    directory: Path,
    root: Path,
    policy: CorpusPolicy,
    marker_patterns: list[str],
) -> bool:
    try:
        relative_path = directory.relative_to(root).as_posix()
    except ValueError:
        return False
    if not relative_path or relative_path == ".":
        return False
    patterns = [*policy.exclude_globs, *marker_patterns]
    for pattern in patterns:
        if pattern.startswith("!"):
            continue
        if _matches_directory(relative_path, pattern) and not _has_negated_descendant(relative_path, patterns):
            return True
    return False


def _matches_directory(relative_path: str, pattern: str) -> bool:
    normalized = pattern.replace("\\", "/").strip()
    if not normalized:
        return False
    if normalized.endswith("/"):
        base = normalized.rstrip("/")
        return relative_path == base or relative_path.startswith(f"{base}/")
    if normalized.endswith("/**"):
        base = normalized[:-3].rstrip("/")
        return relative_path == base or relative_path.startswith(f"{base}/")
    return _matches(relative_path, normalized) or _matches(f"{relative_path}/", normalized)


def _has_negated_descendant(relative_path: str, patterns: list[str]) -> bool:
    prefix = f"{relative_path}/"
    for pattern in patterns:
        if not pattern.startswith("!"):
            continue
        candidate = pattern[1:].replace("\\", "/").strip()
        if not candidate:
            continue
        if "/" not in candidate:
            return True
        if candidate == relative_path or candidate.startswith(prefix):
            return True
    return False


def _file_kind(path: str | Path, mime_type: str | None) -> str:
    file_path = Path(path)
    ext = file_path.suffix.lower()
    name = file_path.name.lower()
    if ext in SENSITIVE_METADATA_EXTENSIONS:
        return "sensitive_metadata"
    if ext in SUBTITLE_EXTENSIONS:
        return "subtitle"
    if ext in MAIL_EXTENSIONS:
        return "mail"
    if ext in CALENDAR_EXTENSIONS:
        return "calendar"
    if ext in CONTACT_EXTENSIONS:
        return "contact"
    if ext in STRUCTURED_DATA_EXTENSIONS:
        return "structured_data"
    if ext in REPORT_EXTENSIONS or name in REPORT_NAMES:
        return "report"
    if ext in DATABASE_EXTENSIONS:
        return "database"
    if ext in GEOSPATIAL_EXTENSIONS:
        return "geospatial"
    if ext in CAD_EXTENSIONS:
        return "cad"
    if ext in SCIENTIFIC_EXTENSIONS:
        return "scientific"
    if ext in DIAGRAM_EXTENSIONS or any(name.endswith(suffix) for suffix in DIAGRAM_COMPOUND_SUFFIXES):
        return "diagram"
    if is_code_like_path(file_path):
        return "code"
    if ext in TEXT_EXTENSIONS or name in DEVELOPER_ARTIFACT_NAMES:
        return "text"
    if ext in DOCUMENT_EXTENSIONS:
        return "document"
    if ext in IMAGE_EXTENSIONS:
        return "image"
    if ext in AUDIO_EXTENSIONS:
        return "audio"
    if ext in VIDEO_EXTENSIONS:
        return "video"
    if ext in CONTAINER_EXTENSIONS:
        return "container"
    if ext in ARCHIVE_EXTENSIONS or any(name.endswith(suffix) for suffix in ARCHIVE_COMPOUND_SUFFIXES):
        return "archive"
    if mime_type and mime_type.startswith("text/"):
        return "text"
    return "binary"


def _should_wait_for_stability(path: Path, root: Path, policy: CorpusPolicy) -> bool:
    quiet_seconds = _stability_quiet_seconds(path, policy)
    if quiet_seconds <= 0:
        return False
    try:
        stat = path.stat()
    except OSError:
        return False
    now = policy.clock() if policy.clock else time.time()
    return now - stat.st_mtime < quiet_seconds


def _stability_quiet_seconds(path: Path, policy: CorpusPolicy) -> float:
    if policy.large_file_stability_quiet_seconds <= 0:
        return policy.stability_quiet_seconds
    try:
        if path.stat().st_size > policy.heavy_threshold_bytes:
            return max(policy.stability_quiet_seconds, policy.large_file_stability_quiet_seconds)
    except OSError:
        return policy.stability_quiet_seconds
    return policy.stability_quiet_seconds


def _status_asset(
    path: Path,
    root: Path,
    status: str,
    reason: str,
    policy: CorpusPolicy,
    *,
    error: str | None = None,
) -> DiscoveredAsset:
    resolved = path.expanduser().resolve()
    try:
        stat = resolved.stat()
        size_bytes = stat.st_size
        mtime_ns = stat.st_mtime_ns
    except OSError:
        size_bytes = 0
        mtime_ns = 0
    mime_type, _ = mimetypes.guess_type(resolved.name)
    ext = resolved.suffix.lower()
    metadata: dict[str, object] = {
        "readiness_status": status,
        "readiness_reason": reason,
        "stability_quiet_seconds": policy.stability_quiet_seconds,
    }
    if error:
        metadata["error"] = error
    return DiscoveredAsset(
        path=resolved,
        relative_path=resolved.relative_to(root.resolve()).as_posix(),
        file_kind=_file_kind(resolved, mime_type),
        mime_type=mime_type,
        extension=ext,
        size_bytes=size_bytes,
        mtime_ns=mtime_ns,
        quick_hash=_quick_hash(resolved, size_bytes, mtime_ns),
        content_hash=None,
        extraction_tier="metadata_only",
        extraction_status=status,
        chunks=(),
        metadata=metadata,
    )


def strict_indexing_enabled(metadata: dict[str, Any] | None) -> bool:
    if not isinstance(metadata, dict):
        return False
    direct = metadata.get("strict_indexing")
    if _truthy_policy_value(direct):
        return True
    policy = metadata.get("metadata_only_policy")
    if _blocking_policy_value(policy):
        return True
    indexing = metadata.get("indexing")
    if isinstance(indexing, dict):
        return _blocking_policy_value(indexing.get("metadata_only"))
    return False


def strict_metadata_only_message(message: str | None = None) -> str:
    suffix = f" Original extractor message: {message}" if message else ""
    return f"Strict indexing requires full content extraction; metadata-only result blocked.{suffix}"


def _strict_metadata_only_metadata(message: str | None = None) -> dict[str, object]:
    return {
        "strict_indexing": True,
        "metadata_only_blocked": True,
        "readiness_status": "blocked_missing_dependency",
        "readiness_reason": strict_metadata_only_message(message),
        "original_status": "metadata_only",
    }


def _truthy_policy_value(value: object) -> bool:
    if isinstance(value, bool):
        return value
    return str(value or "").strip().lower() in {"1", "true", "yes", "on", "strict", "block", "blocked"}


def _blocking_policy_value(value: object) -> bool:
    return str(value or "").strip().lower() in {
        "block",
        "blocked",
        "strict",
        "error",
        "fail",
        "fail_closed",
        "blocked_missing_dependency",
    }


def _is_locked_error(exc: BaseException) -> bool:
    text = str(exc).lower()
    return isinstance(exc, PermissionError) or "locked" in text or "being used by another process" in text


def _manifest_matches(
    manifest: dict[str, Any] | None,
    *,
    size_bytes: int,
    mtime_ns: int,
    quick_hash: str,
) -> bool:
    if not isinstance(manifest, dict) or not manifest.get("content_hash"):
        return False
    try:
        return (
            int(manifest.get("size_bytes") or -1) == int(size_bytes)
            and int(manifest.get("mtime_ns") or -1) == int(mtime_ns)
            and str(manifest.get("quick_hash") or "") == quick_hash
        )
    except (TypeError, ValueError):
        return False


def _is_transient_artifact(path: Path) -> bool:
    name = path.name.lower()
    return name.startswith("~$") or path.suffix.lower() in TRANSIENT_SUFFIXES


def _load_marker_patterns(root: Path) -> list[str]:
    patterns: list[str] = []
    for marker in MARKER_FILES:
        marker_path = root / marker
        if not marker_path.exists():
            continue
        for raw_line in marker_path.read_text(encoding="utf-8", errors="ignore").splitlines():
            line = raw_line.strip()
            if line and not line.startswith("#"):
                patterns.append(line)
    return patterns


def _is_included(relative_path: str, policy: CorpusPolicy, marker_patterns: list[str]) -> bool:
    included = not policy.include_globs
    for pattern in policy.include_globs:
        if _matches(relative_path, pattern):
            included = True
    for pattern in (*policy.exclude_globs, *marker_patterns):
        negated = pattern.startswith("!")
        candidate = pattern[1:] if negated else pattern
        if _matches(relative_path, candidate):
            included = bool(negated)
    return included


def _matches(relative_path: str, pattern: str) -> bool:
    normalized = pattern.replace("\\", "/").strip()
    if not normalized:
        return False
    if normalized.endswith("/"):
        return relative_path.startswith(normalized)
    if normalized.endswith("/**"):
        return relative_path == normalized[:-3] or relative_path.startswith(normalized[:-2])
    name = Path(relative_path).name
    return fnmatch.fnmatch(relative_path, normalized) or fnmatch.fnmatch(name, normalized)


def _extract_text_chunks(path: Path, root: Path) -> list[AssetChunk]:
    text = path.read_text(encoding="utf-8", errors="replace").strip()
    if not text:
        return []
    redacted, _ = redact_text(text)
    relative_path = path.relative_to(root.resolve()).as_posix()
    chunks: list[AssetChunk] = []
    for index, start in enumerate(range(0, len(redacted), 4000)):
        body = redacted[start : start + 4000].strip()
        if body:
            chunks.append(
                AssetChunk(
                    chunk_index=index,
                    title=relative_path,
                    body=body,
                    locator=f"char:{start}-{start + len(body)}",
                    token_estimate=max(1, len(body.split())),
                )
            )
    return chunks


def _scope_relative_path(root: Path, target: Path | None) -> str | None:
    if target is None:
        return None
    if not _is_relative_to(target, root):
        raise ValueError(f"target path is outside monitored root: {target}")
    return target.relative_to(root).as_posix()


def _is_relative_to(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _quick_hash(path: Path, size_bytes: int, mtime_ns: int) -> str:
    value = f"{path.name}:{size_bytes}:{mtime_ns}".encode("utf-8", errors="ignore")
    return hashlib.sha256(value).hexdigest()
