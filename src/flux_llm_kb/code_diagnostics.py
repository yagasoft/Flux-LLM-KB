from __future__ import annotations

from pathlib import PurePosixPath, PureWindowsPath
from typing import Any


def build_code_status_report(*, roots: list[dict[str, Any]], totals: dict[str, Any] | None = None) -> dict[str, Any]:
    safe_roots = [_safe_root(row) for row in roots]
    computed_totals = {
        "asset_count": sum(int(row.get("asset_count") or 0) for row in safe_roots),
        "chunk_count": sum(int(row.get("chunk_count") or 0) for row in safe_roots),
        "symbol_count": sum(int(row.get("symbol_count") or 0) for row in safe_roots),
        "reference_count": sum(int(row.get("reference_count") or 0) for row in safe_roots),
        "fallback_count": sum(int(row.get("fallback_count") or 0) for row in safe_roots),
        "generated_count": sum(int(row.get("generated_count") or 0) for row in safe_roots),
    }
    if totals:
        computed_totals.update({key: int(value or 0) for key, value in totals.items() if isinstance(value, (int, float))})
    return {
        "settings_mutated": False,
        "totals": computed_totals,
        "roots": sorted(safe_roots, key=lambda row: str(row.get("root_name") or "")),
    }


def sanitize_code_result(row: dict[str, Any]) -> dict[str, Any]:
    result = {}
    for key in (
        "symbol",
        "name",
        "qualified_name",
        "target",
        "symbol_kind",
        "relationship",
        "relationship_kind",
        "language",
        "path",
        "line_start",
        "line_end",
        "parser_status",
        "confidence",
        "root_name",
        "is_generated",
        "target_symbol",
        "source_symbol",
        "route",
        "test_target",
        "excerpt",
        "snippet",
        "score",
        "streams",
    ):
        value = row.get(key)
        if value is None or value == "":
            continue
        result[key] = value
    if "qualified_name" in result and "symbol" not in result:
        result["symbol"] = result["qualified_name"]
    if "name" in result and "symbol" not in result:
        result["symbol"] = result["name"]
    if "relationship_kind" in result and "relationship" not in result:
        result["relationship"] = result["relationship_kind"]
    if "path" in result:
        result["path"] = _safe_path_leaf(str(result["path"]))
    return result


def sanitize_code_lookup(payload: dict[str, Any]) -> dict[str, Any]:
    return {
        "query": payload.get("query"),
        "matches": [sanitize_code_result(row) for row in payload.get("matches", []) if isinstance(row, dict)],
        "references": [sanitize_code_result(row) for row in payload.get("references", []) if isinstance(row, dict)],
        "settings_mutated": False,
    }


def _safe_root(row: dict[str, Any]) -> dict[str, Any]:
    parser_statuses = row.get("parser_statuses") if isinstance(row.get("parser_statuses"), dict) else {}
    fallback_count = int(row.get("fallback_count") or parser_statuses.get("fallback") or 0)
    asset_count = int(row.get("asset_count") or 0)
    health = "ready"
    if asset_count == 0:
        health = "not_run"
    elif fallback_count:
        health = "partial"
    safe = {
        "root_name": row.get("root_name") or "unknown",
        "asset_count": asset_count,
        "chunk_count": int(row.get("chunk_count") or 0),
        "symbol_count": int(row.get("symbol_count") or 0),
        "reference_count": int(row.get("reference_count") or 0),
        "generated_count": int(row.get("generated_count") or 0),
        "fallback_count": fallback_count,
        "languages": row.get("languages") if isinstance(row.get("languages"), dict) else {},
        "parser_statuses": parser_statuses,
        "health": health,
        "slow_files": [_safe_slow_file(item) for item in row.get("slow_files", []) if isinstance(item, dict)][:5],
    }
    return safe


def _safe_slow_file(row: dict[str, Any]) -> dict[str, Any]:
    return {
        "path": _safe_path_leaf(str(row.get("path") or "")),
        "duration_ms": int(row.get("duration_ms") or 0),
    }


def _safe_path_leaf(path: str) -> str:
    if not path:
        return ""
    normalized = path.replace("\\", "/")
    leaf = PurePosixPath(normalized).name
    if leaf == normalized:
        leaf = PureWindowsPath(path).name
    return leaf or "<path>"
