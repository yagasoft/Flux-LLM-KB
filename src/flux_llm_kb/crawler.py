from __future__ import annotations

from dataclasses import dataclass, field
import fnmatch
import hashlib
import mimetypes
from pathlib import Path
import time
from typing import Callable, Iterable

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
CODE_EXTENSIONS = {
    ".cs",
    ".css",
    ".html",
    ".java",
    ".js",
    ".ps1",
    ".py",
    ".rs",
    ".sql",
    ".ts",
}
DOCUMENT_EXTENSIONS = {
    ".doc",
    ".docm",
    ".docx",
    ".dot",
    ".dotm",
    ".dotx",
    ".odp",
    ".ods",
    ".odt",
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
ARCHIVE_EXTENSIONS = {".7z", ".gz", ".rar", ".tar", ".tgz", ".zip"}
TRANSIENT_SUFFIXES = {".tmp", ".partial", ".crdownload", ".download", ".part"}


@dataclass(frozen=True)
class CorpusPolicy:
    root_path: Path
    recursive: bool = True
    include_globs: tuple[str, ...] = ()
    exclude_globs: tuple[str, ...] = ()
    max_inline_bytes: int = 256 * 1024
    heavy_threshold_bytes: int = 10 * 1024 * 1024
    hash_max_bytes: int = 512 * 1024 * 1024
    stability_quiet_seconds: float = 0.0
    large_file_stability_quiet_seconds: float = 0.0
    clock: Callable[[], float] | None = None


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

    for path in _iter_files(root, recursive=active_policy.recursive, target=target):
        try:
            relative_path = path.relative_to(root).as_posix()
            if not _is_included(relative_path, active_policy, marker_patterns):
                continue
            if _should_wait_for_stability(path, root, active_policy):
                assets.append(_status_asset(path, root, "pending_stable", "mtime_not_stable", active_policy))
                continue
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

    return CrawlPlan(
        root_path=root,
        scope_relative_path=scope_relative_path,
        scope_is_file=scope_is_file,
        assets=assets,
        deferred_jobs=deferred_jobs,
        errors=errors,
    )


def discover_asset(path: Path, root: Path, policy: CorpusPolicy) -> DiscoveredAsset:
    resolved = path.resolve()
    stat = resolved.stat()
    classification = classify_file(resolved, policy)
    content_hash = _sha256_file(resolved) if stat.st_size <= policy.hash_max_bytes else None
    metadata: dict[str, object] = {}
    chunks: tuple[AssetChunk, ...] = ()
    if classification.file_kind == "image":
        from .extractors import image_metadata

        metadata = image_metadata(resolved)
    elif classification.extraction_tier == "inline":
        from .extractors import extract_file

        extraction = extract_file(resolved, policy)
        metadata = extraction.metadata
        chunks = extraction.chunks
    return DiscoveredAsset(
        path=resolved,
        relative_path=resolved.relative_to(root.resolve()).as_posix(),
        file_kind=classification.file_kind,
        mime_type=classification.mime_type,
        extension=resolved.suffix.lower(),
        size_bytes=stat.st_size,
        mtime_ns=stat.st_mtime_ns,
        quick_hash=_quick_hash(resolved, stat.st_size, stat.st_mtime_ns),
        content_hash=content_hash,
        extraction_tier=classification.extraction_tier,
        chunks=chunks,
        metadata=metadata,
    )


def classify_file(path: str | Path, policy: CorpusPolicy) -> FileClassification:
    file_path = Path(path)
    ext = file_path.suffix.lower()
    size = file_path.stat().st_size
    mime_type, _ = mimetypes.guess_type(file_path.name)
    file_kind = _file_kind(file_path, mime_type)

    if file_kind == "archive":
        return FileClassification(file_kind=file_kind, extraction_tier="metadata_only", mime_type=mime_type)
    if file_kind in {"diagram", "image", "audio", "video"}:
        return FileClassification(file_kind=file_kind, extraction_tier="deferred", mime_type=mime_type)
    if size > policy.heavy_threshold_bytes:
        return FileClassification(
            file_kind=file_kind,
            extraction_tier="deferred",
            mime_type=mime_type,
            reason="heavy_file",
        )
    if ext in TEXT_EXTENSIONS and size <= policy.max_inline_bytes:
        return FileClassification(file_kind=file_kind, extraction_tier="inline", mime_type=mime_type)
    if ext in DOCUMENT_EXTENSIONS:
        return FileClassification(file_kind=file_kind, extraction_tier="deferred", mime_type=mime_type)
    return FileClassification(file_kind=file_kind, extraction_tier="metadata_only", mime_type=mime_type)


def _iter_files(root: Path, *, recursive: bool, target: Path | None = None) -> Iterable[Path]:
    if target is not None:
        if not _is_relative_to(target, root):
            raise ValueError(f"target path is outside monitored root: {target}")
        if target.is_file() and not _is_transient_artifact(target):
            return iter([target])
        if not target.exists():
            return iter(())
        iterator = target.rglob("*") if recursive else target.iterdir()
        return (path for path in iterator if path.is_file() and path.name not in MARKER_FILES and not _is_transient_artifact(path))
    iterator = root.rglob("*") if recursive else root.iterdir()
    return (path for path in iterator if path.is_file() and path.name not in MARKER_FILES and not _is_transient_artifact(path))


def _file_kind(path: str | Path, mime_type: str | None) -> str:
    file_path = Path(path)
    ext = file_path.suffix.lower()
    name = file_path.name.lower()
    if ext in DIAGRAM_EXTENSIONS or any(name.endswith(suffix) for suffix in DIAGRAM_COMPOUND_SUFFIXES):
        return "diagram"
    if ext in CODE_EXTENSIONS:
        return "code"
    if ext in TEXT_EXTENSIONS:
        return "text"
    if ext in DOCUMENT_EXTENSIONS:
        return "document"
    if ext in IMAGE_EXTENSIONS:
        return "image"
    if ext in AUDIO_EXTENSIONS:
        return "audio"
    if ext in VIDEO_EXTENSIONS:
        return "video"
    if ext in ARCHIVE_EXTENSIONS:
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


def _is_locked_error(exc: BaseException) -> bool:
    text = str(exc).lower()
    return isinstance(exc, PermissionError) or "locked" in text or "being used by another process" in text


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
