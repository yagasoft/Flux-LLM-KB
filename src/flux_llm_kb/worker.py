from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from . import database
from .crawler import CorpusPolicy
from .extractors import extract_file


@dataclass(frozen=True)
class JobProcessResult:
    status: str
    message: str | None = None


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
    )
    result = extract_file(path, policy)
    if result.status in {"indexed", "metadata_only", "blocked_missing_dependency"}:
        database.apply_extraction_result(root_name=root_name, relative_path=relative_path, result=result)
    return JobProcessResult(status=result.status, message=result.message)
