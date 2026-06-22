from __future__ import annotations

from pathlib import PurePosixPath, PureWindowsPath
import re
from typing import Any


_VERSION_TOKENS = re.compile(
    r"""
    (?:
      \b(?:v|ver|version|rev|revision)\s*[\._-]?\s*\d+[a-z]?\b
      |\b(?:draft|final|latest|current|copy)\b
      |\(\s*\d+\s*\)
      |\b\d{4}[\._-]\d{1,2}[\._-]\d{1,2}\b
      |\b\d{1,2}[\._-]\d{1,2}[\._-]\d{2,4}\b
    )
    """,
    re.IGNORECASE | re.VERBOSE,
)
_NON_WORD = re.compile(r"[^a-z0-9]+")


def document_family_key(path: str, *, title: str | None = None) -> str:
    candidate = title or _stem(path)
    normalized = candidate.lower()
    normalized = _VERSION_TOKENS.sub(" ", normalized)
    normalized = _NON_WORD.sub(" ", normalized)
    normalized = " ".join(token for token in normalized.split() if token)
    return normalized or _stem(path).lower()


def collapse_version_families(items: list[dict[str, Any]], *, limit: int) -> list[dict[str, Any]]:
    families: dict[str, list[dict[str, Any]]] = {}
    passthrough: list[dict[str, Any]] = []
    for item in items:
        if item.get("logical_kind") == "mail" or item.get("kind") != "corpus_chunk" or not item.get("source_path"):
            passthrough.append(item)
            continue
        key = document_family_key(str(item["source_path"]), title=str(item.get("title") or ""))
        families.setdefault(key, []).append(item)

    collapsed = list(passthrough)
    for key, family_items in families.items():
        ordered = sorted(family_items, key=_canonical_sort_key)
        canonical = dict(ordered[0])
        suppressed = ordered[1:]
        canonical["version_family"] = {
            "key": key,
            "canonical_source_path": canonical.get("source_path"),
            "suppressed_count": len(suppressed),
            "suppressed_source_paths": [item.get("source_path") for item in suppressed],
        }
        collapsed.append(canonical)

    return sorted(collapsed, key=lambda item: float(item.get("score") or 0.0), reverse=True)[:limit]


def _canonical_sort_key(item: dict[str, Any]) -> tuple[float, float, int, str]:
    trust_rank = float(item.get("trust_rank") or 0)
    score = float(item.get("score") or 0)
    path = str(item.get("source_path") or "")
    quality = len(str(item.get("summary") or ""))
    return (-trust_rank, -score, -quality, path.lower())


def _stem(path: str) -> str:
    raw = str(path).replace("\\", "/")
    posix_name = PurePosixPath(raw).name
    windows_name = PureWindowsPath(path).name
    name = windows_name if len(windows_name) < len(posix_name) else posix_name
    if "." in name:
        return ".".join(name.split(".")[:-1]) or name
    return name
