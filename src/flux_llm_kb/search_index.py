from __future__ import annotations

from dataclasses import dataclass
import json
from typing import Any, Iterable
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin
from urllib.request import Request, urlopen


SNOWFLAKE_EMBEDDING_MODEL = "Snowflake/snowflake-arctic-embed-l-v2.0"
SNOWFLAKE_EMBEDDING_DIMENSIONS = 1024
DEFAULT_VESPA_BASE_URL = "http://127.0.0.1:8080"
VESPA_SCHEMA = "flux_evidence"
VESPA_NAMESPACE = "flux"


def vespa_document_id(owner_table: str, owner_id: str) -> str:
    """Return a stable Vespa document id that cannot collide across owner tables."""
    table = str(owner_table or "").strip().replace(" ", "_")
    item_id = str(owner_id or "").strip()
    if not table or not item_id:
        raise ValueError("owner_table and owner_id are required for a Vespa document id")
    safe_table = "".join(char if char.isalnum() or char in {"_", "-"} else "_" for char in table)
    safe_id = "".join(char if char.isalnum() or char in {"_", "-"} else "_" for char in item_id)
    return f"id:{VESPA_NAMESPACE}:{VESPA_SCHEMA}::{safe_table}--{safe_id}"


def _vespa_text(value: Any) -> str:
    text = str(value or "")
    return "".join(
        char if char in {"\t", "\n", "\r"} or ord(char) >= 0x20 and ord(char) != 0x7F else " "
        for char in text
    )


def _vespa_filter_values(values: Iterable[str] | None) -> list[str]:
    normalized: list[str] = []
    seen: set[str] = set()
    for value in values or []:
        text = _vespa_text(value).strip()
        if not text or text in seen:
            continue
        normalized.append(text)
        seen.add(text)
    return normalized


def _add_string_attribute_filter(
    yql_filters: list[str],
    query_params: dict[str, str],
    *,
    field: str,
    param_prefix: str,
    values: Iterable[str] | None,
) -> None:
    filtered_values = _vespa_filter_values(values)
    if not filtered_values:
        return
    predicates: list[str] = []
    for index, value in enumerate(filtered_values):
        param_name = f"{param_prefix}_{index}"
        query_params[param_name] = value
        predicates.append(f"{field} contains @{param_name}")
    yql_filters.append(predicates[0] if len(predicates) == 1 else f"({' or '.join(predicates)})")


class SearchIndexError(RuntimeError):
    pass


def _vespa_http_error_message(exc: HTTPError) -> str:
    detail = str(exc)
    try:
        body = exc.read().decode("utf-8", errors="replace").strip()
    except Exception:  # pragma: no cover - defensive fallback
        body = ""
    if body:
        try:
            decoded = json.loads(body)
        except json.JSONDecodeError:
            rendered = body
        else:
            if isinstance(decoded, dict):
                rendered = str(decoded.get("message") or decoded.get("error") or decoded)
            else:
                rendered = str(decoded)
        detail = f"{detail}: {rendered[:2000]}"
    return detail


@dataclass(frozen=True)
class VespaSearchResult:
    owner_table: str
    owner_id: str
    score: float
    title: str = ""
    root_name: str | None = None
    source_path: str | None = None
    match_features: dict[str, Any] | None = None

    def as_candidate(self) -> dict[str, Any]:
        return {
            "owner_table": self.owner_table,
            "owner_id": self.owner_id,
            "score": self.score,
            "title": self.title,
            "root_name": self.root_name,
            "source_path": self.source_path,
            "match_features": self.match_features or {},
        }


class VespaHttpClient:
    def __init__(self, base_url: str = DEFAULT_VESPA_BASE_URL, *, timeout_seconds: int = 30) -> None:
        self.base_url = base_url.rstrip("/")
        self.timeout_seconds = max(1, int(timeout_seconds or 30))

    def post_json(self, path: str, payload: dict[str, Any]) -> dict[str, Any]:
        request = Request(
            urljoin(f"{self.base_url}/", path.lstrip("/")),
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urlopen(request, timeout=self.timeout_seconds) as response:
                raw = response.read().decode("utf-8")
        except HTTPError as exc:  # pragma: no cover - network-specific
            raise SearchIndexError(_vespa_http_error_message(exc)) from exc
        except URLError as exc:  # pragma: no cover - network-specific
            raise SearchIndexError(str(exc)) from exc
        except Exception as exc:  # pragma: no cover - network-specific
            raise SearchIndexError(str(exc)) from exc
        try:
            decoded = json.loads(raw)
        except json.JSONDecodeError as exc:
            raise SearchIndexError("Vespa returned invalid JSON") from exc
        if not isinstance(decoded, dict):
            raise SearchIndexError("Vespa returned a non-object payload")
        return decoded

    def put_json(self, path: str, payload: dict[str, Any]) -> dict[str, Any]:
        request = Request(
            urljoin(f"{self.base_url}/", path.lstrip("/")),
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="PUT",
        )
        try:
            with urlopen(request, timeout=self.timeout_seconds) as response:
                raw = response.read().decode("utf-8")
        except HTTPError as exc:  # pragma: no cover - network-specific
            raise SearchIndexError(_vespa_http_error_message(exc)) from exc
        except Exception as exc:  # pragma: no cover - network-specific
            raise SearchIndexError(str(exc)) from exc
        return json.loads(raw or "{}")

    def delete(self, path: str) -> dict[str, Any]:
        request = Request(urljoin(f"{self.base_url}/", path.lstrip("/")), method="DELETE")
        try:
            with urlopen(request, timeout=self.timeout_seconds) as response:
                raw = response.read().decode("utf-8")
        except HTTPError as exc:  # pragma: no cover - network-specific
            raise SearchIndexError(_vespa_http_error_message(exc)) from exc
        except Exception as exc:  # pragma: no cover - network-specific
            raise SearchIndexError(str(exc)) from exc
        return json.loads(raw or "{}")


class VespaSearchAdapter:
    def __init__(self, base_url: str = DEFAULT_VESPA_BASE_URL, *, http: Any | None = None) -> None:
        self.base_url = base_url.rstrip("/")
        self.http = http or VespaHttpClient(self.base_url)

    def feed(self, document: dict[str, Any]) -> dict[str, Any]:
        document_id = str(document["id"])
        path = f"/document/v1/{VESPA_NAMESPACE}/{VESPA_SCHEMA}/docid/{document_id.rsplit('::', 1)[-1]}"
        return self.http.post_json(path, {"put": document_id, "fields": document["fields"]})

    def delete(self, document_id: str) -> dict[str, Any]:
        path = f"/document/v1/{VESPA_NAMESPACE}/{VESPA_SCHEMA}/docid/{document_id.rsplit('::', 1)[-1]}"
        return self.http.delete(path)

    def count_by_root_name(self, root_name: str, *, owner_table: str | None = None) -> int:
        filters = ["root_name contains @root_name"]
        payload: dict[str, Any] = {
            "yql": "",
            "root_name": _vespa_text(root_name),
            "hits": 0,
            "timeout": "5s",
        }
        if owner_table:
            filters.append("owner_table contains @owner_table")
            payload["owner_table"] = _vespa_text(owner_table)
        payload["yql"] = f"select * from sources * where {' and '.join(filters)}"
        response = self.http.post_json("/search/", payload)
        fields = response.get("root", {}).get("fields", {})
        try:
            return int(fields.get("totalCount") or 0)
        except (TypeError, ValueError):
            return 0

    def query(
        self,
        query: str,
        *,
        embedding: list[float],
        root_name: str | None = None,
        file_kinds: Iterable[str] | None = None,
        languages: Iterable[str] | None = None,
        limit: int = 20,
        rank_profile: str = "hybrid",
    ) -> list[dict[str, Any]]:
        yql_filters = [
            "lifecycle_state contains \"active\"",
            "deleted = false",
            "canonical = true",
        ]
        query_params: dict[str, str] = {}
        if root_name:
            yql_filters.append("root_name contains @root_name")
        _add_string_attribute_filter(yql_filters, query_params, field="file_kind", param_prefix="file_kind", values=file_kinds)
        _add_string_attribute_filter(yql_filters, query_params, field="language", param_prefix="language", values=languages)
        where = " and ".join(yql_filters)
        payload: dict[str, Any] = {
            "yql": (
                "select * from sources * where "
                f"({where}) and ({{targetHits:200}}nearestNeighbor(embedding, query_embedding) or userQuery())"
            ),
            "query": query,
            "type": "all",
            "hits": max(1, min(int(limit or 20), 200)),
            "ranking.profile": rank_profile,
            "ranking.listFeatures": "true",
            "input.query(query_embedding)": embedding,
        }
        if root_name:
            payload["root_name"] = root_name
        payload.update(query_params)
        response = self.http.post_json("/search/", payload)
        children = response.get("root", {}).get("children", [])
        results: list[dict[str, Any]] = []
        for child in children if isinstance(children, list) else []:
            if not isinstance(child, dict):
                continue
            fields = child.get("fields") if isinstance(child.get("fields"), dict) else {}
            match_features = child.get("matchfeatures") if isinstance(child.get("matchfeatures"), dict) else fields.get("matchfeatures")
            result = VespaSearchResult(
                owner_table=str(fields.get("owner_table") or ""),
                owner_id=str(fields.get("owner_id") or ""),
                title=str(fields.get("title") or ""),
                root_name=fields.get("root_name"),
                source_path=fields.get("source_path"),
                score=float(child.get("relevance") or 0.0),
                match_features=match_features if isinstance(match_features, dict) else {},
            )
            results.append(result.as_candidate())
        return results


def build_vespa_document(row: dict[str, Any]) -> dict[str, Any]:
    document_id = str(row.get("vespa_document_id") or vespa_document_id(str(row.get("owner_table") or ""), str(row.get("owner_id") or "")))
    vector = [float(value) for value in list(row.get("embedding") or [])]
    if len(vector) != int(row.get("embedding_dimensions") or SNOWFLAKE_EMBEDDING_DIMENSIONS):
        raise ValueError("Vespa document embedding does not match embedding_dimensions")
    fields = {
        "owner_table": _vespa_text(row.get("owner_table")),
        "owner_id": _vespa_text(row.get("owner_id")),
        "root_id": _vespa_text(row.get("root_id")),
        "root_name": _vespa_text(row.get("root_name")),
        "title": _vespa_text(row.get("title")),
        "body": _vespa_text(row.get("body")),
        "source_path": _vespa_text(row.get("source_path")),
        "symbols": [_vespa_text(symbol) for symbol in list(row.get("symbols") or [])],
        "language": _vespa_text(row.get("language")),
        "file_kind": _vespa_text(row.get("file_kind")),
        "lifecycle_state": _vespa_text(row.get("lifecycle_state") or "active"),
        "deleted": bool(row.get("deleted", False)),
        "canonical": bool(row.get("canonical", True)),
        "source_hash": _vespa_text(row.get("source_hash")),
        "model_generation": _vespa_text(row.get("model_generation") or "snowflake-qwen-paddleocr-v1"),
        "embedding_model": _vespa_text(row.get("embedding_model") or SNOWFLAKE_EMBEDDING_MODEL),
        "embedding_dimensions": int(row.get("embedding_dimensions") or SNOWFLAKE_EMBEDDING_DIMENSIONS),
        "embedding": {"values": vector},
    }
    return {"id": document_id, "fields": fields}
