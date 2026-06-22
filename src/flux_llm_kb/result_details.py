from __future__ import annotations

from collections import OrderedDict
from html import escape
from html.parser import HTMLParser
import json
from pathlib import PurePosixPath, PureWindowsPath
from typing import Any
from urllib.parse import urlparse

from . import database


MAIL_IMPLEMENTATION_FILES = {"manifest.json", "body.txt", "body.html", "message.eml", "message.msg"}
MAIL_RAW_EXTENSIONS = {".eml", ".msg"}
DETAIL_PREVIEW_LIMIT = 12000


def result_detail(kind: str, result_id: str) -> dict[str, Any]:
    if kind == "corpus_chunk":
        chunk = database.get_asset_chunk_detail(result_id)
        if chunk is None:
            raise LookupError("asset chunk not found")
        asset = database.get_source_asset_detail(chunk["asset_id"])
        if asset is None:
            raise LookupError("source asset not found")
        if mail_export_id_from_path(str(asset.get("path") or "")):
            return _mail_detail(asset=asset, selected_chunk=chunk)
        return _file_detail(asset=asset, selected_chunk=chunk)
    if kind == "asset":
        asset = database.get_source_asset_detail(result_id)
        if asset is None:
            raise LookupError("source asset not found")
        if mail_export_id_from_path(str(asset.get("path") or "")):
            return _mail_detail(asset=asset, selected_chunk=None)
        return _file_detail(asset=asset, selected_chunk=None)
    if kind == "mail":
        message = database.get_mail_message(result_id)
        if message is None:
            raise LookupError("mail message not found")
        export_id = str(message.get("export_id") or "")
        if not export_id:
            raise LookupError("mail export not found")
        assets = database.list_mail_export_assets(export_id, root_name=None)
        if not assets:
            raise LookupError("mail export assets not found")
        return _mail_detail(asset=assets[0], selected_chunk=None, assets=assets, message=message)
    if kind == "episode":
        episode = database.get_episode_detail(result_id)
        if episode is None:
            raise LookupError("episode not found")
        return {
            "logical_kind": "episode",
            "detail_ref": {"kind": "episode", "id": result_id},
            "id": episode["id"],
            "title": episode["title"],
            "summary": episode["summary"],
            "metadata": episode.get("metadata") or {},
            "related_evidence": [],
            "provenance": [{"type": "episode", "id": episode["id"], "source_kind": episode.get("source_kind")}],
            "actions": {},
        }
    raise ValueError(f"unsupported result kind: {kind}")


def decorate_corpus_search_item(item: dict[str, Any]) -> dict[str, Any]:
    result = dict(item)
    result["logical_kind"] = "mail" if mail_export_id_from_path(str(result.get("source_path") or "")) else "file"
    result["chunk_id"] = result.get("id")
    result["detail_ref"] = {"kind": "corpus_chunk", "id": result.get("id")}
    if "asset_id" in result:
        result["asset_id"] = result.get("asset_id")
    result.setdefault("related_evidence_count", 0)
    return result


def collapse_mail_spool_search_results(items: list[dict[str, Any]]) -> list[dict[str, Any]]:
    groups: "OrderedDict[str, list[dict[str, Any]]]" = OrderedDict()
    passthrough: list[dict[str, Any]] = []
    for item in items:
        export_id = mail_export_id_from_path(str(item.get("source_path") or ""))
        if not export_id:
            passthrough.append(item)
            continue
        groups.setdefault(export_id, []).append(item)

    collapsed = list(passthrough)
    for export_id, grouped in groups.items():
        collapsed.append(_collapse_one_mail_search_group(export_id, grouped))
    return collapsed


def mail_export_id_from_path(path: str) -> str | None:
    parts = _mail_path_parts(path)
    if len(parts) < 2:
        return None
    leaf = parts[-1].lower()
    if parts[1].lower() == "attachments":
        return parts[0]
    if leaf in MAIL_IMPLEMENTATION_FILES:
        return parts[0]
    if PurePosixPath(leaf).suffix.lower() in MAIL_RAW_EXTENSIONS:
        return parts[0]
    return None


def sanitize_mail_html(value: str) -> str:
    parser = _MailHTMLSanitizer()
    parser.feed(value or "")
    parser.close()
    return parser.html


def _collapse_one_mail_search_group(export_id: str, grouped: list[dict[str, Any]]) -> dict[str, Any]:
    manifest_item = next((item for item in grouped if _mail_path_parts(str(item.get("source_path") or ""))[-1:] == ["manifest.json"]), None)
    canonical = dict(manifest_item or max(grouped, key=lambda item: float(item.get("score") or 0.0)))
    manifest = _parse_json_object(canonical.get("summary"))
    if not manifest and manifest_item:
        manifest = _parse_json_object(manifest_item.get("summary"))

    subject = str(manifest.get("subject") or "").strip()
    if subject:
        canonical["title"] = f"Mail: {subject}"
    else:
        canonical["title"] = canonical.get("title") or f"Mail: {export_id}"

    summary = _mail_manifest_summary(manifest)
    if summary:
        canonical["summary"] = summary
        canonical["excerpt"] = summary
    canonical["logical_kind"] = "mail"
    canonical["mail_export_id"] = export_id
    canonical["chunk_id"] = canonical.get("id")
    canonical["detail_ref"] = {"kind": "corpus_chunk", "id": canonical.get("id")}
    canonical["asset_id"] = canonical.get("asset_id")
    canonical["source_path"] = str(canonical.get("source_path") or f"{export_id}/manifest.json")
    canonical["score"] = max(float(item.get("score") or 0.0) for item in grouped)
    canonical["streams"] = sorted({stream for item in grouped for stream in item.get("streams", [])})
    canonical["raw_scores"] = _merge_raw_scores(grouped)
    canonical["related_evidence_count"] = max(0, len(grouped) - 1)
    canonical["related_evidence"] = [
        _evidence_from_search_item(item)
        for item in grouped
        if item.get("id") != canonical.get("id")
    ]
    return canonical


def _file_detail(*, asset: dict[str, Any], selected_chunk: dict[str, Any] | None) -> dict[str, Any]:
    chunks = list(asset.get("chunks") or [])
    if selected_chunk and not any(chunk.get("id") == selected_chunk.get("id") for chunk in chunks):
        chunks.insert(0, selected_chunk)
    preview_text = _preview_text(chunks)
    canonical_path = _canonical_display_path(str(asset.get("root_path") or ""), str(asset.get("path") or ""))
    related = database.list_related_source_assets(asset_id=asset["id"])
    return {
        "logical_kind": "file",
        "detail_ref": {"kind": "asset", "id": asset["id"]},
        "id": asset["id"],
        "asset_id": asset["id"],
        "title": PurePosixPath(str(asset.get("path") or "")).name or str(asset.get("path") or "Untitled file"),
        "metadata": _asset_metadata(asset, canonical_path=canonical_path),
        "preview": {
            "available": bool(preview_text),
            "text": preview_text,
            "chunks": [_chunk_summary(chunk) for chunk in chunks],
        },
        "actions": _file_actions(asset, canonical_path),
        "related_evidence": [_related_evidence(item) for item in related],
        "attachments": [],
        "provenance": [_asset_provenance(asset, canonical_path=canonical_path)],
    }


def _mail_detail(
    *,
    asset: dict[str, Any],
    selected_chunk: dict[str, Any] | None,
    assets: list[dict[str, Any]] | None = None,
    message: dict[str, Any] | None = None,
) -> dict[str, Any]:
    export_id = mail_export_id_from_path(str(asset.get("path") or ""))
    if not export_id:
        raise LookupError("mail export not found")
    all_assets = assets if assets is not None else database.list_mail_export_assets(export_id, root_name=asset.get("root_name"))
    if not all_assets:
        all_assets = [asset]
    if not any(item.get("id") == asset.get("id") for item in all_assets):
        all_assets.insert(0, asset)

    manifest_asset = _asset_by_leaf(all_assets, "manifest.json")
    manifest = _parse_json_object(_asset_text(manifest_asset) if manifest_asset else "")
    if selected_chunk and selected_chunk.get("title") == "manifest.json":
        manifest = _parse_json_object(selected_chunk.get("body")) or manifest
    profile_name = str(manifest.get("profile_name") or "")
    mail_message = message or database.get_mail_message_by_export_id(export_id, profile_name=profile_name or None)

    text_body = _asset_text(_asset_by_leaf(all_assets, "body.txt"))
    html_body = _asset_text(_asset_by_leaf(all_assets, "body.html"))
    html_sanitized = sanitize_mail_html(html_body) if html_body else ""
    attachments = [_related_evidence(item, relationship="attachment") for item in all_assets if _is_mail_attachment(item)]
    related = [_related_evidence(item, relationship=_mail_relationship(item)) for item in all_assets if item.get("id") != (manifest_asset or {}).get("id")]
    subject = str(manifest.get("subject") or "Untitled mail")
    return {
        "logical_kind": "mail",
        "detail_ref": {"kind": "mail", "id": (mail_message or {}).get("id") or export_id},
        "id": (mail_message or {}).get("id") or export_id,
        "asset_id": asset.get("id"),
        "mail_message_id": (mail_message or {}).get("id"),
        "title": f"Mail: {subject}" if subject else f"Mail: {export_id}",
        "mail": {
            "export_id": export_id,
            "profile_name": profile_name or (mail_message or {}).get("profile_name"),
            "source_type": manifest.get("source_type") or (mail_message or {}).get("source_type"),
            "source_folder": manifest.get("source_folder") or (mail_message or {}).get("source_folder"),
            "post_process_state": (mail_message or {}).get("export_state"),
            "subject": subject,
            "sender": manifest.get("sender"),
            "recipients": manifest.get("recipients") or [],
            "received_at": manifest.get("received_at"),
            "message_id": manifest.get("message_id"),
            "attachment_count": manifest.get("attachment_count", len(attachments)),
        },
        "body": {
            "text": text_body,
            "html_sanitized": html_sanitized,
            "format": "html" if html_sanitized else "text",
        },
        "attachments": attachments,
        "related_evidence": related,
        "actions": {},
        "provenance": [_asset_provenance(item, canonical_path=_canonical_display_path(str(item.get("root_path") or ""), str(item.get("path") or ""))) for item in all_assets],
    }


def _file_actions(asset: dict[str, Any], canonical_path: str) -> dict[str, Any]:
    deleted = bool(asset.get("deleted_at")) or asset.get("status") == "deleted"
    unavailable_reason = "Asset is deleted from the index." if deleted else ""
    if not canonical_path and not unavailable_reason:
        unavailable_reason = "No canonical indexed path is available."
    available = bool(canonical_path) and not deleted
    return {
        "preview": {"available": bool(asset.get("chunks")), "disabled_reason": "" if asset.get("chunks") else "No extracted text is available."},
        "copy_path": {"available": bool(canonical_path), "path": canonical_path, "disabled_reason": "" if canonical_path else "No canonical indexed path is available."},
        "open": {"available": available, "disabled_reason": "" if available else unavailable_reason},
        "reveal": {"available": available, "disabled_reason": "" if available else unavailable_reason},
    }


def _preview_text(chunks: list[dict[str, Any]]) -> str:
    text = "\n\n".join(str(chunk.get("body") or "") for chunk in sorted(chunks, key=lambda item: int(item.get("chunk_index") or 0))).strip()
    return text[:DETAIL_PREVIEW_LIMIT]


def _asset_text(asset: dict[str, Any] | None) -> str:
    if not asset:
        return ""
    return _preview_text(list(asset.get("chunks") or []))


def _asset_by_leaf(assets: list[dict[str, Any]], leaf: str) -> dict[str, Any] | None:
    for asset in assets:
        parts = _mail_path_parts(str(asset.get("path") or ""))
        if parts and parts[-1].lower() == leaf:
            return asset
    return None


def _is_mail_attachment(asset: dict[str, Any]) -> bool:
    parts = _mail_path_parts(str(asset.get("path") or ""))
    return len(parts) >= 3 and parts[1].lower() == "attachments"


def _mail_relationship(asset: dict[str, Any]) -> str:
    if _is_mail_attachment(asset):
        return "attachment"
    leaf = PurePosixPath(str(asset.get("path") or "")).name.lower()
    if leaf in {"body.txt", "body.html"}:
        return "body"
    if leaf in {"message.eml", "message.msg"}:
        return "raw_message"
    return "related"


def _asset_metadata(asset: dict[str, Any], *, canonical_path: str) -> dict[str, Any]:
    return {
        "path": asset.get("path"),
        "canonical_path": canonical_path,
        "root_name": asset.get("root_name"),
        "root_path": asset.get("root_path"),
        "file_kind": asset.get("file_kind"),
        "mime_type": asset.get("mime_type"),
        "extension": asset.get("extension"),
        "size_bytes": asset.get("size_bytes"),
        "status": "deleted" if asset.get("deleted_at") else asset.get("status"),
        "deleted_at": asset.get("deleted_at"),
        "metadata": asset.get("metadata") or {},
    }


def _chunk_summary(chunk: dict[str, Any]) -> dict[str, Any]:
    return {
        "id": chunk.get("id"),
        "chunk_index": chunk.get("chunk_index"),
        "title": chunk.get("title"),
        "modality": chunk.get("modality"),
        "locator": chunk.get("locator"),
        "token_estimate": chunk.get("token_estimate"),
    }


def _related_evidence(item: dict[str, Any], *, relationship: str = "related") -> dict[str, Any]:
    return {
        "id": item.get("id"),
        "asset_id": item.get("id"),
        "relationship": relationship,
        "path": item.get("path"),
        "title": PurePosixPath(str(item.get("path") or "")).name,
        "file_kind": item.get("file_kind"),
        "mime_type": item.get("mime_type"),
        "status": "deleted" if item.get("deleted_at") else item.get("status"),
        "size_bytes": item.get("size_bytes"),
        "metadata": item.get("metadata") or {},
    }


def _asset_provenance(asset: dict[str, Any], *, canonical_path: str) -> dict[str, Any]:
    return {
        "type": "source_asset",
        "asset_id": asset.get("id"),
        "root_name": asset.get("root_name"),
        "path": asset.get("path"),
        "canonical_path": canonical_path,
        "status": "deleted" if asset.get("deleted_at") else asset.get("status"),
    }


def _evidence_from_search_item(item: dict[str, Any]) -> dict[str, Any]:
    return {
        "chunk_id": item.get("id"),
        "asset_id": item.get("asset_id"),
        "path": item.get("source_path"),
        "title": item.get("title"),
        "score": item.get("score"),
    }


def _mail_path_parts(path: str) -> list[str]:
    raw_parts = [part for part in str(path or "").replace("\\", "/").split("/") if part]
    lowered = [part.lower() for part in raw_parts]
    if "ready" in lowered:
        return raw_parts[lowered.index("ready") + 1 :]
    return raw_parts


def _canonical_display_path(root_path: str, relative_path: str) -> str:
    root = str(root_path or "").strip()
    relative = str(relative_path or "").strip().replace("\\", "/")
    if not root:
        return relative
    separator = "\\" if root.startswith("\\\\") or "\\" in root else "/"
    return root.rstrip("\\/") + separator + relative.replace("/", separator)


def _looks_windows(path: str) -> bool:
    return bool(PureWindowsPath(path).drive) or str(path).startswith("\\\\")


def _parse_json_object(value: Any) -> dict[str, Any]:
    if isinstance(value, dict):
        return value
    if not isinstance(value, str) or not value.strip():
        return {}
    try:
        payload = json.loads(value)
    except json.JSONDecodeError:
        return {}
    return payload if isinstance(payload, dict) else {}


def _mail_manifest_summary(manifest: dict[str, Any]) -> str:
    if not manifest:
        return ""
    parts: list[str] = []
    sender = _display_address(manifest.get("sender"))
    if sender:
        parts.append(f"From {sender}")
    recipients = manifest.get("recipients")
    if isinstance(recipients, list) and recipients:
        display_recipients = ", ".join(_display_address(item) for item in recipients[:3] if _display_address(item))
        if display_recipients:
            parts.append(f"to {display_recipients}")
    received_at = str(manifest.get("received_at") or "").strip()
    if received_at:
        parts.append(f"received {received_at}")
    source_folder = str(manifest.get("source_folder") or "").strip()
    if source_folder:
        parts.append(f"folder {source_folder}")
    if manifest.get("attachment_count") is not None:
        count = int(manifest.get("attachment_count") or 0)
        parts.append(f"{count} attachment{'s' if count != 1 else ''}")
    return "; ".join(parts) + "." if parts else ""


def _display_address(value: Any) -> str:
    text = str(value or "").strip()
    if "<" in text:
        text = text.split("<", 1)[0].strip()
    return text


def _merge_raw_scores(items: list[dict[str, Any]]) -> dict[str, float]:
    merged: dict[str, float] = {}
    for item in items:
        for key, value in (item.get("raw_scores") or {}).items():
            merged[key] = max(float(value or 0.0), merged.get(key, 0.0))
    return merged


class _MailHTMLSanitizer(HTMLParser):
    allowed_tags = {
        "a",
        "b",
        "blockquote",
        "br",
        "code",
        "div",
        "em",
        "h1",
        "h2",
        "h3",
        "h4",
        "h5",
        "h6",
        "i",
        "li",
        "ol",
        "p",
        "pre",
        "span",
        "strong",
        "table",
        "tbody",
        "td",
        "th",
        "thead",
        "tr",
        "u",
        "ul",
    }
    dropped_tags = {"script", "style", "iframe", "object", "embed", "form", "svg", "math"}
    dropped_void_tags = {"input", "button", "select", "textarea", "link", "meta", "img"}
    void_tags = {"br"}

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.parts: list[str] = []
        self.drop_depth = 0

    @property
    def html(self) -> str:
        return "".join(self.parts)

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        tag = tag.lower()
        if tag in self.dropped_void_tags:
            return
        if tag in self.dropped_tags:
            self.drop_depth += 1
            return
        if self.drop_depth or tag not in self.allowed_tags:
            return
        clean_attrs = self._clean_attrs(tag, attrs)
        attr_text = "".join(f' {name}="{escape(value, quote=True)}"' for name, value in clean_attrs)
        self.parts.append(f"<{tag}{attr_text}>")

    def handle_endtag(self, tag: str) -> None:
        tag = tag.lower()
        if tag in self.dropped_tags and self.drop_depth:
            self.drop_depth -= 1
            return
        if self.drop_depth or tag not in self.allowed_tags or tag in self.void_tags:
            return
        self.parts.append(f"</{tag}>")

    def handle_data(self, data: str) -> None:
        if not self.drop_depth:
            self.parts.append(escape(data, quote=False))

    def handle_entityref(self, name: str) -> None:
        if not self.drop_depth:
            self.parts.append(f"&{name};")

    def handle_charref(self, name: str) -> None:
        if not self.drop_depth:
            self.parts.append(f"&#{name};")

    def _clean_attrs(self, tag: str, attrs: list[tuple[str, str | None]]) -> list[tuple[str, str]]:
        clean: list[tuple[str, str]] = []
        for name, value in attrs:
            attr = name.lower()
            text = str(value or "")
            if attr.startswith("on") or attr == "style":
                continue
            if tag == "a" and attr in {"href", "title"}:
                if attr == "href" and not _safe_href(text):
                    continue
                clean.append((attr, text))
            elif tag in {"td", "th"} and attr in {"colspan", "rowspan"} and text.isdigit():
                clean.append((attr, text))
        return clean


def _safe_href(value: str) -> bool:
    parsed = urlparse(value.strip())
    return parsed.scheme in {"http", "https", "mailto"}
