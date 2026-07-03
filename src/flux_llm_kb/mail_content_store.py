from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
from typing import Any

from .acceleration import resolve_cache_layout


MAIL_CONTENT_SCHEMA = "flux-mail-content-v1"
_TOKEN_RE = re.compile(r"[A-Za-z0-9_]{2,}")


def managed_mail_content_kind(asset_path: str) -> str | None:
    parts = _mail_path_parts(asset_path)
    if len(parts) == 2 and parts[1].lower() == "body.txt":
        return "body"
    if len(parts) >= 3 and parts[1].lower() == "attachments":
        return "attachment"
    return None


def is_managed_mail_content(asset_path: str, root_metadata: dict[str, Any] | None) -> bool:
    metadata = root_metadata if isinstance(root_metadata, dict) else {}
    return bool(metadata.get("mail_profile")) and managed_mail_content_kind(asset_path) is not None


def write_mail_content(
    *,
    root_name: str,
    asset_path: str,
    chunk_index: int,
    title: str,
    text: str,
    kind: str | None = None,
) -> dict[str, Any]:
    clean_text = str(text or "")
    text_hash = hashlib.sha256(clean_text.encode("utf-8")).hexdigest()
    key = hashlib.sha256(
        f"{MAIL_CONTENT_SCHEMA}\0{root_name}\0{asset_path}\0{chunk_index}\0{text_hash}".encode("utf-8")
    ).hexdigest()
    relative_path = f"mail_content/{key[:2]}/{key}.json"
    path = _cache_root() / relative_path
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "schema": MAIL_CONTENT_SCHEMA,
        "sha256": text_hash,
        "root_name": root_name,
        "asset_path": asset_path,
        "chunk_index": int(chunk_index),
        "title": title,
        "kind": kind or managed_mail_content_kind(asset_path) or "mail",
        "text": clean_text,
    }
    path.write_text(json.dumps(payload, sort_keys=True), encoding="utf-8")
    return {
        "storage": "disk_sidecar",
        "schema": MAIL_CONTENT_SCHEMA,
        "sha256": text_hash,
        "relative_path": relative_path,
        "kind": payload["kind"],
        "token_count": len(_tokens(clean_text)),
        "redacted_from_db": True,
    }


def read_mail_content(ref: dict[str, Any] | None) -> str:
    if not isinstance(ref, dict):
        return ""
    relative_path = str(ref.get("relative_path") or "").replace("\\", "/").strip("/")
    if not relative_path:
        return ""
    path = _cache_root() / relative_path
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return ""
    if payload.get("schema") != MAIL_CONTENT_SCHEMA:
        return ""
    text = str(payload.get("text") or "")
    expected_hash = str(ref.get("sha256") or "")
    if expected_hash and hashlib.sha256(text.encode("utf-8")).hexdigest() != expected_hash:
        return ""
    return text


def delete_mail_content(ref: dict[str, Any] | None) -> dict[str, Any]:
    if not isinstance(ref, dict):
        return {"status": "skipped", "deleted": False, "blocked_reason": "missing sidecar reference"}
    if ref.get("source") != "managed_mail" or ref.get("storage") != "disk_sidecar":
        return {
            "status": "blocked",
            "deleted": False,
            "blocked_reason": "sidecar reference is not a managed mail disk sidecar",
        }
    relative_path = str(ref.get("relative_path") or "").replace("\\", "/").strip("/")
    if not relative_path:
        return {"status": "skipped", "deleted": False, "blocked_reason": "missing sidecar relative path"}
    if not relative_path.startswith("mail_content/"):
        return {
            "status": "blocked",
            "deleted": False,
            "relative_path": relative_path,
            "blocked_reason": "mail sidecar path is outside the managed mail_content cache",
        }

    root = _cache_root().resolve()
    path = (root / relative_path).resolve()
    try:
        path.relative_to(root)
    except ValueError:
        return {
            "status": "blocked",
            "deleted": False,
            "relative_path": relative_path,
            "path": str(path),
            "blocked_reason": "mail sidecar path escapes the cache root",
        }
    if not path.exists():
        return {"status": "missing", "deleted": False, "relative_path": relative_path, "path": str(path)}
    if not path.is_file():
        return {
            "status": "blocked",
            "deleted": False,
            "relative_path": relative_path,
            "path": str(path),
            "blocked_reason": "mail sidecar path is not a file",
        }
    path.unlink()
    return {"status": "deleted", "deleted": True, "relative_path": relative_path, "path": str(path)}


def hydrate_chunk_body(chunk: dict[str, Any]) -> str:
    body = str(chunk.get("body") or "")
    if body:
        return body
    metadata = chunk.get("metadata") if isinstance(chunk.get("metadata"), dict) else {}
    ref = metadata.get("sidecar_ref") or metadata.get("mail_content")
    return read_mail_content(ref)


def score_mail_text(query: str, title: str, text: str) -> float:
    query_tokens = _tokens(query)
    if not query_tokens:
        return 0.0
    haystack = f"{title}\n{text}".lower()
    text_tokens = _tokens(haystack)
    overlap = len(query_tokens & text_tokens)
    score = overlap / max(len(query_tokens), 1)
    normalized_query = " ".join(sorted(query_tokens))
    if normalized_query and normalized_query in haystack:
        score += 0.5
    if str(query or "").strip().lower() in haystack:
        score += 0.25
    return score


def _cache_root() -> Path:
    return Path(resolve_cache_layout()["root"])


def _mail_path_parts(path: str) -> list[str]:
    return [part for part in str(path or "").replace("\\", "/").split("/") if part]


def _tokens(text: str) -> set[str]:
    return {token.lower() for token in _TOKEN_RE.findall(text)}
