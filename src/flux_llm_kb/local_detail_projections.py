from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
import json
import re
from typing import Any, Callable

from .local_visibility import LocalDisclosureKind, evaluate_local_disclosure


_EXCERPT_LIMIT = 12_000
_FACT_LIMIT = 100
_DIAGNOSTIC_FIELD_LIMIT = 4_096
_LOCAL_DIAGNOSTIC_FIELDS = ("path", "hash", "runtime_detail", "parser_diagnostic", "retained_provenance")
_RAW_PUBLIC_DETAIL_KEYS = {
    "path",
    "source_path",
    "asset_path",
    "content_hash",
    "artifact_hash",
    "hash",
    "checksum",
    "runtime_detail",
    "parser_diagnostic",
    "parser_diagnostics",
    "retained_provenance",
    "retained_binding",
}
_PUBLIC_AUDIT_PRIVATE_KEYS = {
    "actor",
    "target_id",
    "output_dir",
    "target_path",
    "output_path",
    "source_root",
    "private_root",
    "root_path",
}
_PUBLIC_PATH_FRAGMENT = re.compile(r"(?<![A-Za-z0-9_])(?:[A-Za-z]:[\\/]|\\\\|/)[^\s\"'<>]+")
_PUBLIC_HASH_FRAGMENT = re.compile(
    r"(?ix)(?<![a-z0-9])(?:"
    r"(?:sha(?:-?(?:1|224|256|384|512))?|md5|blake2(?:b|s)?)\s*[:=]\s*[a-z0-9+/._=-]+"
    r"|[a-f0-9]{32,128}"
    r")(?![a-z0-9])"
)


@dataclass(frozen=True)
class LocalSourceDetailProjection:
    id: str
    source_path: str
    content_hash: str | None
    parser_diagnostics: list[dict[str, Any]]
    excerpt: str | None
    reason_code: str | None

    def as_dict(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "source_path": self.source_path,
            "content_hash": self.content_hash,
            "parser_diagnostics": self.parser_diagnostics,
            "excerpt": self.excerpt,
            "reason_code": self.reason_code,
        }


@dataclass(frozen=True)
class LocalCorpusDetailProjection:
    id: str
    source_path: str
    content_hash: str | None
    parser_diagnostics: list[dict[str, Any]]
    excerpt: str | None
    reason_code: str | None

    def as_dict(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "source_path": self.source_path,
            "content_hash": self.content_hash,
            "parser_diagnostics": self.parser_diagnostics,
            "excerpt": self.excerpt,
            "reason_code": self.reason_code,
        }


@dataclass(frozen=True)
class LocalCodeSearchProjection:
    query: str
    results: list[dict[str, Any]]
    settings_mutated: bool = False

    def as_dict(self) -> dict[str, Any]:
        return {"query": self.query, "results": self.results, "settings_mutated": self.settings_mutated}


def project_local_source_detail(row: dict[str, Any]) -> LocalSourceDetailProjection:
    return LocalSourceDetailProjection(**_detail_fields(row))


def project_local_corpus_detail(row: dict[str, Any]) -> LocalCorpusDetailProjection:
    return LocalCorpusDetailProjection(**_detail_fields(row))


def project_local_code_search(query: str, rows: list[dict[str, Any]]) -> LocalCodeSearchProjection:
    return LocalCodeSearchProjection(query=query, results=[_project_code_row(row) for row in rows[:_FACT_LIMIT]])


def project_local_operational_diagnostics(
    report: dict[str, Any],
    *,
    retrieval: dict[str, Any] | None = None,
    watcher: dict[str, Any] | None = None,
    workers: dict[str, Any] | None = None,
    jobs: dict[str, Any] | None = None,
    mail: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Enrich an already-sanitised diagnostic summary through the named local reader only."""
    result = _bounded_local_report(report)
    rows = _diagnostic_rows(retrieval=retrieval, watcher=watcher, workers=workers, jobs=jobs, mail=mail)
    for item in result.get("items") if isinstance(result.get("items"), list) else []:
        if not isinstance(item, dict):
            continue
        raw = rows.get(_diagnostic_identity(item))
        if raw is None:
            continue
        item.update(_local_diagnostic_fields(raw))
    return result


def project_local_audit_events(events: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Return bounded, secret-scanned local audit evidence without mutating stored events."""
    result: list[dict[str, Any]] = []
    for event in events[:_FACT_LIMIT]:
        if not isinstance(event, dict):
            continue
        projected = _local_audit_value(event)
        if not isinstance(projected, dict):
            continue
        details = event.get("details") if isinstance(event.get("details"), dict) else {}
        projected.update(_local_diagnostic_fields(details))
        result.append(projected)
    return result


def project_public_audit_events(events: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Preserve the aggregate audit shape while excluding raw local-only evidence."""
    result: list[dict[str, Any]] = []
    for event in events[:_FACT_LIMIT]:
        if not isinstance(event, dict):
            continue
        projected = _public_audit_value(event)
        if isinstance(projected, dict):
            result.append(projected)
    return result


def _diagnostic_rows(
    *,
    retrieval: dict[str, Any] | None,
    watcher: dict[str, Any] | None,
    workers: dict[str, Any] | None,
    jobs: dict[str, Any] | None,
    mail: dict[str, Any] | None,
) -> dict[tuple[str, str], dict[str, Any]]:
    result: dict[tuple[str, str], dict[str, Any]] = {}
    for section, rows in (
        ("retrieval", (retrieval or {}).get("recent_explains", [])),
        ("watcher", (watcher or {}).get("events", [])),
        ("workers", (workers or {}).get("families", [])),
        ("workers", _retrying_gpu_eviction_rows(workers or {})),
        ("jobs", (jobs or {}).get("jobs", [])),
        ("mail", (mail or {}).get("sync_runs", [])),
        ("mail", (mail or {}).get("post_process_events", [])),
    ):
        for row in rows if isinstance(rows, list) else []:
            if not isinstance(row, dict):
                continue
            target = _diagnostic_row_id(section, row)
            result[(section, str(target))] = row
    return result


def _diagnostic_identity(item: dict[str, Any]) -> tuple[str, str]:
    target = item.get("target") if isinstance(item.get("target"), dict) else {}
    section = str(item.get("section") or "")
    value = target.get("id") or item.get("root_name") or item.get("family") or section
    return section, str(value)


def _diagnostic_row_id(section: str, row: dict[str, Any]) -> Any:
    return row.get("id") or row.get("root_name") or row.get("family") or row.get("profile_name") or row.get("query_hash") or section


def _retrying_gpu_eviction_rows(workers: dict[str, Any]) -> list[dict[str, Any]]:
    evictions = workers.get("gpu_evictions") if isinstance(workers.get("gpu_evictions"), dict) else {}
    recent = evictions.get("recent") if isinstance(evictions.get("recent"), list) else []
    return [row for row in recent if isinstance(row, dict) and str(row.get("status") or "") == "retrying"]


def _local_diagnostic_fields(row: dict[str, Any]) -> dict[str, Any]:
    nested = row.get("details") if isinstance(row.get("details"), dict) else {}
    metadata = row.get("metadata") if isinstance(row.get("metadata"), dict) else {}
    nested_metadata = nested.get("metadata") if isinstance(nested.get("metadata"), dict) else {}
    # Public producer rows put extensible diagnostic evidence in metadata; audit
    # rows use details and may in turn place their evidence in details.metadata.
    # Direct row/detail fields win when both forms are present.
    candidates = {**nested_metadata, **metadata, **nested, **row}
    explicit_runtime_detail = _first(candidates, "runtime_detail")
    values = {
        "path": _first(candidates, "path", "source_path", "asset_path", "target_path", "output_path") or _mail_folder(row, metadata),
        "hash": _first(candidates, "content_hash", "hash", "checksum", "artifact_hash", "query_hash", "runtime_fingerprint"),
        "runtime_detail": explicit_runtime_detail or _gpu_runtime_evidence(row, metadata) or _first(candidates, "last_error", "error"),
        "parser_diagnostic": _first(candidates, "parser_diagnostic", "parser_diagnostics"),
        "retained_provenance": _first(candidates, "retained_provenance", "retained_binding", "provenance") or _mail_provenance(row, metadata),
    }
    result: dict[str, Any] = {}
    for field in _LOCAL_DIAGNOSTIC_FIELDS:
        value, reason = _bounded_local_value(values[field], LocalDisclosureKind.DIAGNOSTIC)
        result[field] = value
        if reason:
            result[f"{field}_reason_code"] = reason
    return result


def _first(values: dict[str, Any], *keys: str) -> Any:
    for key in keys:
        if values.get(key) not in (None, ""):
            return values[key]
    return None


def _mail_folder(row: dict[str, Any], metadata: dict[str, Any]) -> str | None:
    """Return only the durable IMAP folder field from a mail event producer."""
    if not (row.get("mail_message_id") or row.get("provider") or row.get("profile_name")):
        return None
    value = metadata.get("folder")
    return str(value) if value not in (None, "") else None


def _mail_provenance(row: dict[str, Any], metadata: dict[str, Any]) -> dict[str, Any] | None:
    if _mail_folder(row, metadata) is None:
        return None
    value = {
        key: metadata[key]
        for key in ("folder", "uid", "uidvalidity")
        if metadata.get(key) not in (None, "")
    }
    return value or None


def _gpu_runtime_evidence(row: dict[str, Any], metadata: dict[str, Any]) -> dict[str, Any] | None:
    """Map the GPU scheduler's own fenced runtime evidence without aliases."""
    values = {
        "owner_component": row.get("owner_component") or metadata.get("owner_component") or row.get("component"),
        "runtime_generation": row.get("runtime_generation") or metadata.get("runtime_generation"),
        "runtime_activity_sequence": row.get("runtime_activity_sequence", metadata.get("runtime_activity_sequence")),
        "runtime_fingerprint": row.get("runtime_fingerprint") or metadata.get("runtime_fingerprint"),
        "reconciliation_observation_id": row.get("reconciliation_observation_id") or metadata.get("reconciliation_observation_id"),
        "terminal_reason": row.get("terminal_reason") or metadata.get("terminal_reason"),
    }
    sequence = values["runtime_activity_sequence"]
    substantive_sequence = isinstance(sequence, int) and not isinstance(sequence, bool) and sequence > 0
    # A component or the row decoder's default sequence of zero is ordinary
    # scheduler status, not fenced runtime evidence.  Preserve the useful error
    # until an identity, observation, terminal claim, or positive sequence exists.
    substantive = substantive_sequence or any(
        values[key] not in (None, "")
        for key in (
            "runtime_generation",
            "runtime_fingerprint",
            "reconciliation_observation_id",
            "terminal_reason",
        )
    )
    if not substantive:
        return None
    return {key: value for key, value in values.items() if value not in (None, "")}


def _bounded_local_value(value: Any, kind: LocalDisclosureKind) -> tuple[Any, str | None]:
    if value in (None, ""):
        return None, None
    serialised = value if isinstance(value, str) else json.dumps(value, sort_keys=True, separators=(",", ":"), default=str)
    disclosure = evaluate_local_disclosure(serialised, kind)
    if disclosure.withheld:
        return None, disclosure.reason_code
    if isinstance(value, str):
        return value[:_DIAGNOSTIC_FIELD_LIMIT], None
    if len(serialised) > _DIAGNOSTIC_FIELD_LIMIT:
        return serialised[:_DIAGNOSTIC_FIELD_LIMIT], None
    return deepcopy(value), None


def _public_audit_value(value: Any) -> Any:
    if isinstance(value, dict):
        return {
            str(key): _public_audit_value(item)
            for key, item in value.items()
            if not _is_public_audit_private_key(str(key), item)
        }
    if isinstance(value, list):
        return [_public_audit_value(item) for item in value[:_FACT_LIMIT]]
    if isinstance(value, str):
        disclosure = evaluate_local_disclosure(value, LocalDisclosureKind.AUDIT_EVIDENCE)
        if disclosure.withheld:
            return "secret-content-withheld"
        return _PUBLIC_HASH_FRAGMENT.sub("<hash>", _PUBLIC_PATH_FRAGMENT.sub("<path>", value))[:_DIAGNOSTIC_FIELD_LIMIT]
    return value


def _local_audit_value(value: Any) -> Any:
    if isinstance(value, dict):
        return {
            str(key): _local_audit_value(item)
            for key, item in value.items()
            if not _is_secret_key(str(key))
        }
    if isinstance(value, list):
        return [_local_audit_value(item) for item in value[:_FACT_LIMIT]]
    if isinstance(value, str):
        disclosure = evaluate_local_disclosure(value, LocalDisclosureKind.AUDIT_EVIDENCE)
        return "secret-content-withheld" if disclosure.withheld else value[:_DIAGNOSTIC_FIELD_LIMIT]
    return deepcopy(value)


def _is_public_audit_private_key(key: str, value: Any = None) -> bool:
    normalised = key.lower().replace("-", "_")
    compact = normalised.replace("_", "")
    hash_key = "hash" in compact or "digest" in compact or "checksum" in compact
    aggregate = (
        (normalised.endswith("_count") or normalised.endswith("_counts") or normalised.endswith("_present"))
        and (isinstance(value, bool) or type(value) in {int, float})
    )
    return (
        normalised in _RAW_PUBLIC_DETAIL_KEYS
        or normalised in _PUBLIC_AUDIT_PRIVATE_KEYS
        or _is_secret_key(key)
        or "path" in compact
        or (hash_key and not aggregate)
        or compact in {"md5", "sha1", "sha224", "sha256", "sha384", "sha512", "blake2b", "blake2s"}
        or "directory" in compact
        or compact.endswith("dir")
        or compact in {"actor", "actorid", "targetid", "sourceid", "privateid"}
    )


def _bounded_local_report(value: Any) -> Any:
    if isinstance(value, dict):
        return {str(key): _bounded_local_report(item) for key, item in value.items()}
    if isinstance(value, list):
        return [_bounded_local_report(item) for item in value[:_FACT_LIMIT]]
    if isinstance(value, str):
        disclosure = evaluate_local_disclosure(value, LocalDisclosureKind.DIAGNOSTIC)
        return disclosure.value[:_DIAGNOSTIC_FIELD_LIMIT] if disclosure.value is not None else disclosure.reason_code
    return deepcopy(value)


def _is_secret_key(key: str) -> bool:
    normalised = key.lower().replace("-", "_")
    return any(
        token in normalised
        for token in (
            "password",
            "token",
            "secret",
            "credential",
            "cookie",
            "authorization",
            "connection_string",
            "api_key",
            "access_key",
            "private_key",
            "oauth",
        )
    )


def _detail_fields(row: dict[str, Any]) -> dict[str, Any]:
    excerpt, reason_code = _excerpt(row.get("excerpt"))
    return {
        "id": str(row.get("id") or ""),
        "source_path": str(row.get("source_path") or ""),
        "content_hash": _optional_text(row.get("content_hash")),
        "parser_diagnostics": _project_diagnostics(row.get("parser_diagnostics")),
        "excerpt": excerpt,
        "reason_code": reason_code,
    }


def _project_code_row(row: dict[str, Any]) -> dict[str, Any]:
    excerpt, reason_code = _excerpt(row.get("excerpt"))
    return {
        "source_path": str(row.get("source_path") or ""),
        "content_hash": _optional_text(row.get("content_hash")),
        "symbols": _project_facts(row.get("symbols"), _symbol_fields, LocalDisclosureKind.SYMBOL),
        "signatures": _project_signatures(row.get("symbols")),
        "relationships": _project_facts(row.get("relationships"), _relationship_fields, LocalDisclosureKind.REFERENCE),
        "parser_diagnostics": _project_diagnostics(row.get("parser_diagnostics")),
        "excerpt": excerpt,
        "reason_code": reason_code,
    }


def _project_signatures(value: Any) -> list[str]:
    signatures: list[str] = []
    for item in value if isinstance(value, list) else []:
        if not isinstance(item, dict) or not isinstance(item.get("signature"), str):
            continue
        disclosure = evaluate_local_disclosure(item["signature"], LocalDisclosureKind.SYMBOL)
        if not disclosure.withheld:
            signatures.append(item["signature"])
    return signatures[:_FACT_LIMIT]


def _project_facts(value: Any, fields: Callable[[dict[str, Any]], dict[str, Any]], kind: LocalDisclosureKind) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for item in value if isinstance(value, list) else []:
        if not isinstance(item, dict):
            continue
        projected = fields(item)
        scanned_values = [field for field in projected.values() if isinstance(field, str)]
        if any(evaluate_local_disclosure(field, kind).withheld for field in scanned_values):
            continue
        result.append(projected)
    return result[:_FACT_LIMIT]


def _symbol_fields(item: dict[str, Any]) -> dict[str, Any]:
    return _compact(item, "name", "qualified_name", "signature", "symbol_kind", "language", "parent_symbol", "line_start", "line_end")


def _relationship_fields(item: dict[str, Any]) -> dict[str, Any]:
    return _compact(item, "source_symbol", "target", "relationship", "language", "line_start", "line_end")


def _project_diagnostics(value: Any) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for item in value if isinstance(value, list) else []:
        if not isinstance(item, dict):
            continue
        code = str(item.get("code") or item.get("error_type") or "parser-diagnostic")[:128]
        message = _optional_text(item.get("message") or item.get("detail") or item.get("text"))
        if message is None:
            reason_code = _optional_text(item.get("reason_code"))
            result.append({"code": code, "reason_code": reason_code} if reason_code else {"code": code})
            continue
        disclosure = evaluate_local_disclosure(message, LocalDisclosureKind.DIAGNOSTIC)
        result.append({"code": code, "reason_code": disclosure.reason_code} if disclosure.withheld else {"code": code, "message": message})
    return result[:_FACT_LIMIT]


def _excerpt(value: Any) -> tuple[str | None, str | None]:
    text = _optional_text(value)
    if text is None:
        return None, None
    disclosure = evaluate_local_disclosure(text[:_EXCERPT_LIMIT], LocalDisclosureKind.CODE_EXCERPT)
    return disclosure.value, disclosure.reason_code


def _compact(item: dict[str, Any], *keys: str) -> dict[str, Any]:
    return {key: item[key] for key in keys if item.get(key) not in (None, "")}


def _optional_text(value: Any) -> str | None:
    return str(value) if value not in (None, "") else None
