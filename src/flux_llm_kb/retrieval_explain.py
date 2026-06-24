from __future__ import annotations

import re
from typing import Any

from .redaction import redact_text


TERM_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]*")
DEFAULT_SNIPPET_CHARS = 240


def query_terms(query: str) -> list[str]:
    seen: set[str] = set()
    terms: list[str] = []
    for match in TERM_RE.finditer(str(query or "").lower()):
        term = match.group(0).strip("._-")
        if len(term) < 2 or term in seen:
            continue
        seen.add(term)
        terms.append(term)
    return terms


def build_query_snippet(
    query: str,
    text: Any,
    *,
    source: str,
    source_path: str | None = None,
    max_chars: int = DEFAULT_SNIPPET_CHARS,
) -> dict[str, Any]:
    redacted, _ = redact_text(str(text or ""))
    cleaned = " ".join(redacted.split())
    terms = query_terms(query)
    max_chars = max(40, int(max_chars or DEFAULT_SNIPPET_CHARS))
    if not cleaned:
        snippet_text = ""
    else:
        snippet_text = _snippet_window(cleaned, terms, max_chars=max_chars)

    highlights = _highlights(snippet_text, terms)
    matched_terms = [highlight["term"] for highlight in highlights]
    result: dict[str, Any] = {
        "text": snippet_text,
        "matched_terms": list(dict.fromkeys(matched_terms)),
        "highlights": highlights,
        "source": source,
    }
    if source_path:
        result["source_path"] = source_path
    return result


def explain_search_result(query: str, item: dict[str, Any]) -> dict[str, Any]:
    explanation: dict[str, Any] = {
        "score": float(item.get("score") or 0.0),
        "streams": [str(stream) for stream in item.get("streams", [])],
        "raw_scores": _float_mapping(item.get("raw_scores")),
        "scope": _scope_explanation(item),
    }
    lifecycle = item.get("lifecycle")
    if isinstance(lifecycle, dict):
        explanation["lifecycle"] = lifecycle
    graph = item.get("graph")
    if isinstance(graph, dict):
        explanation["graph"] = graph
    corpus = _corpus_explanation(item)
    if corpus:
        explanation["corpus"] = corpus
    adjustments = _adjustments(item)
    if adjustments:
        explanation["adjustments"] = adjustments
    filters = _filter_explanation(item)
    if filters:
        explanation["filters"] = filters
    suppression = _suppression_explanation(item)
    if suppression:
        explanation["suppression"] = suppression
    return explanation


def enrich_search_result(query: str, item: dict[str, Any]) -> dict[str, Any]:
    result = dict(item)
    source_text = str(result.get("summary") or result.get("excerpt") or "")
    source_path = str(result.get("source_path") or "") or None
    snippet = build_query_snippet(query, source_text, source="summary", source_path=source_path)
    result["snippet"] = snippet
    result["excerpt"] = snippet["text"]
    result["retrieval_explanation"] = explain_search_result(query, result)
    return result


def _snippet_window(text: str, terms: list[str], *, max_chars: int) -> str:
    lower = text.lower()
    positions = [lower.find(term) for term in terms if lower.find(term) >= 0]
    if not positions:
        return _truncate(text, max_chars)
    first = min(positions)
    start = max(0, first - (max_chars // 4))
    end = min(len(text), start + max_chars)
    if end - start < max_chars and start > 0:
        start = max(0, end - max_chars)
    window = text[start:end].strip()
    if start > 0:
        window = f"...{window}"
    if end < len(text):
        window = f"{window}..."
    return window


def _truncate(text: str, max_chars: int) -> str:
    if len(text) <= max_chars:
        return text
    return text[: max_chars - 3].rstrip() + "..."


def _highlights(text: str, terms: list[str]) -> list[dict[str, Any]]:
    lower = text.lower()
    highlights: list[dict[str, Any]] = []
    for term in terms:
        start = lower.find(term)
        if start < 0:
            continue
        highlights.append({"term": term, "start": start, "end": start + len(term)})
    return sorted(highlights, key=lambda item: (int(item["start"]), str(item["term"])))


def _float_mapping(value: Any) -> dict[str, float]:
    if not isinstance(value, dict):
        return {}
    result: dict[str, float] = {}
    for key, item in value.items():
        try:
            result[str(key)] = float(item)
        except (TypeError, ValueError):
            continue
    return result


def _scope_explanation(item: dict[str, Any]) -> dict[str, Any]:
    scope: dict[str, Any] = {"label": str(item.get("retrieval_scope") or "global")}
    for source_key, output_key in (
        ("retrieval_cwd", "cwd"),
        ("retrieval_root_name", "root_name"),
        ("retrieval_root_path", "root_path"),
        ("retrieval_workspace_root", "workspace_root"),
        ("retrieval_workspace_key", "workspace_key"),
    ):
        value = item.get(source_key)
        if value:
            scope[output_key] = value
    return scope


def _corpus_explanation(item: dict[str, Any]) -> dict[str, Any]:
    corpus: dict[str, Any] = {}
    for key in ("source_path", "root_name", "trust_rank", "duplicate_count", "related_evidence_count"):
        if key in item and item.get(key) is not None:
            corpus[key] = item.get(key)
    if "related_evidence_count" not in corpus and ("source_path" in corpus or item.get("logical_kind") == "mail"):
        corpus["related_evidence_count"] = 0
    return corpus


def _adjustments(item: dict[str, Any]) -> dict[str, Any]:
    adjustments: dict[str, Any] = {}
    for key in ("base_score", "scope_score_boost", "suppressed_versions", "canonical_path"):
        if key in item and item.get(key) is not None:
            adjustments[key] = item.get(key)
    return adjustments


def _filter_explanation(item: dict[str, Any]) -> dict[str, Any]:
    filters = item.get("retrieval_filters")
    if not isinstance(filters, dict):
        return {}
    return {"active": filters}


def _suppression_explanation(item: dict[str, Any]) -> dict[str, Any]:
    filters = item.get("retrieval_filters")
    if not isinstance(filters, dict) or not filters.get("include_suppressed"):
        return {}

    suppression: dict[str, Any] = {}
    duplicate_count = _positive_int(item.get("duplicate_count"))
    if duplicate_count:
        exact_duplicates: dict[str, Any] = {
            "suppressed_count": duplicate_count,
            "reason": "exact_content_duplicate",
        }
        source_path = item.get("source_path")
        if source_path:
            exact_duplicates["canonical_source_path"] = source_path
        asset_id = item.get("asset_id")
        if asset_id:
            exact_duplicates["canonical_asset_id"] = asset_id
        suppression["exact_duplicates"] = exact_duplicates

    version_family = item.get("version_family")
    if isinstance(version_family, dict) and _positive_int(version_family.get("suppressed_count")):
        family: dict[str, Any] = {
            "suppressed_count": _positive_int(version_family.get("suppressed_count")),
            "reason": "same_document_version_family",
        }
        for key in ("key", "canonical_source_path", "suppressed_source_paths"):
            if key in version_family and version_family.get(key) is not None:
                family[key] = version_family.get(key)
        suppression["version_family"] = family
    semantic_cluster = item.get("semantic_duplicate_cluster")
    if isinstance(semantic_cluster, dict) and _positive_int(semantic_cluster.get("suppressed_count")):
        semantic: dict[str, Any] = {
            "cluster_id": semantic_cluster.get("cluster_id"),
            "suppressed_count": _positive_int(semantic_cluster.get("suppressed_count")),
            "reason": "semantic_near_duplicate",
        }
        for key in ("threshold", "max_similarity", "suppressed"):
            if semantic_cluster.get(key) is not None:
                semantic[key] = semantic_cluster.get(key)
        suppression["semantic_duplicates"] = semantic
    return suppression


def _positive_int(value: Any) -> int:
    try:
        number = int(value or 0)
    except (TypeError, ValueError):
        return 0
    return max(0, number)
