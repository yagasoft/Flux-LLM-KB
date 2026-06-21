from __future__ import annotations

from dataclasses import dataclass
import base64
from email import policy
from email.message import EmailMessage, Message
from email.parser import BytesParser
import hashlib
import json
from pathlib import Path
import re
import shutil
import time
from typing import Any, Iterable

from . import database


@dataclass(frozen=True)
class ParsedAttachment:
    filename: str
    content_type: str
    payload: bytes


@dataclass(frozen=True)
class ParsedEmail:
    subject: str
    sender: str
    recipients: tuple[str, ...]
    message_id: str | None
    received_at: str | None
    text_body: str
    html_body: str
    attachments: tuple[ParsedAttachment, ...]


@dataclass(frozen=True)
class MailExportResult:
    export_id: str
    ready_path: Path
    manifest: dict[str, Any]


@dataclass(frozen=True)
class OutlookFolderPath:
    mailbox: str
    parts: tuple[str, ...]


def build_xoauth2_string(user: str, access_token: str) -> str:
    payload = f"user={user}\x01auth=Bearer {access_token}\x01\x01"
    return base64.b64encode(payload.encode("utf-8")).decode("ascii")


def parse_email_bytes(raw_message: bytes) -> ParsedEmail:
    message = BytesParser(policy=policy.default).parsebytes(raw_message)
    text_parts: list[str] = []
    html_parts: list[str] = []
    attachments: list[ParsedAttachment] = []
    if message.is_multipart():
        for part in message.walk():
            if part.is_multipart():
                continue
            disposition = (part.get_content_disposition() or "").lower()
            content_type = part.get_content_type()
            filename = part.get_filename()
            payload = part.get_payload(decode=True) or b""
            if disposition == "attachment" or filename:
                attachments.append(
                    ParsedAttachment(
                        filename=_safe_filename(filename or "attachment.bin"),
                        content_type=content_type,
                        payload=payload,
                    )
                )
            elif content_type == "text/plain":
                text_parts.append(_part_text(part))
            elif content_type == "text/html":
                html_parts.append(_part_text(part))
    else:
        if message.get_content_type() == "text/html":
            html_parts.append(_part_text(message))
        else:
            text_parts.append(_part_text(message))

    return ParsedEmail(
        subject=str(message.get("Subject", "")),
        sender=str(message.get("From", "")),
        recipients=tuple(_split_addresses(str(message.get("To", "")))),
        message_id=str(message.get("Message-ID")) if message.get("Message-ID") else None,
        received_at=str(message.get("Date")) if message.get("Date") else None,
        text_body="\n".join(part.strip() for part in text_parts if part.strip()),
        html_body="\n".join(part.strip() for part in html_parts if part.strip()),
        attachments=tuple(attachments),
    )


def export_email_to_spool(
    *,
    raw_message: bytes,
    spool_path: str | Path,
    profile_name: str,
    source_type: str,
    source_folder: str,
    source_message_id: str,
    extra_metadata: dict[str, Any] | None = None,
) -> MailExportResult:
    spool = Path(spool_path).expanduser().resolve()
    parsed = parse_email_bytes(raw_message)
    export_id = _export_id(profile_name, source_folder, source_message_id, raw_message)
    inflight = spool / "_inflight" / export_id
    ready = spool / "ready" / export_id
    if ready.exists():
        manifest = json.loads((ready / "manifest.json").read_text(encoding="utf-8"))
        return MailExportResult(export_id=export_id, ready_path=ready, manifest=manifest)
    if inflight.exists():
        shutil.rmtree(inflight)
    inflight.mkdir(parents=True, exist_ok=True)
    (spool / "ready").mkdir(parents=True, exist_ok=True)
    (spool / "error").mkdir(parents=True, exist_ok=True)

    (inflight / "message.eml").write_bytes(raw_message)
    (inflight / "body.txt").write_text(parsed.text_body, encoding="utf-8")
    if parsed.html_body:
        (inflight / "body.html").write_text(parsed.html_body, encoding="utf-8")
    attachments_dir = inflight / "attachments"
    attachments_dir.mkdir()
    for attachment in parsed.attachments:
        (attachments_dir / attachment.filename).write_bytes(attachment.payload)

    manifest = {
        "export_id": export_id,
        "profile_name": profile_name,
        "source_type": source_type,
        "source_folder": source_folder,
        "source_message_id": source_message_id,
        "message_id": parsed.message_id,
        "subject": parsed.subject,
        "sender": parsed.sender,
        "recipients": list(parsed.recipients),
        "received_at": parsed.received_at,
        "attachment_count": len(parsed.attachments),
        "content_hash": hashlib.sha256(raw_message).hexdigest(),
        "exported_at_epoch": int(time.time()),
        "metadata": extra_metadata or {},
    }
    (inflight / "manifest.json").write_text(json.dumps(manifest, indent=2, sort_keys=True), encoding="utf-8")
    inflight.rename(ready)
    return MailExportResult(export_id=export_id, ready_path=ready, manifest=manifest)


def scan_ready_spool(spool_path: str | Path) -> list[dict[str, Any]]:
    ready = Path(spool_path).expanduser().resolve() / "ready"
    if not ready.exists():
        return []
    manifests: list[dict[str, Any]] = []
    for manifest_path in sorted(ready.glob("*/manifest.json")):
        try:
            manifests.append(json.loads(manifest_path.read_text(encoding="utf-8")))
        except json.JSONDecodeError:
            continue
    return manifests


def normalize_outlook_folder_path(path: str) -> OutlookFolderPath:
    parts = tuple(part.strip() for part in re.split(r"[\\/]+", path) if part.strip())
    if len(parts) < 2:
        raise ValueError("Outlook folder path must include mailbox and folder")
    return OutlookFolderPath(mailbox=parts[0], parts=parts[1:])


def add_mail_profile(
    *,
    name: str,
    source_type: str,
    spool_path: str | Path,
    folder_paths: list[str],
    account: str | None = None,
    server: str | None = None,
    post_process_policy: str = "move_to_processed",
    trust_rank: int = 450,
    sync_enabled: bool = False,
    sync_interval_seconds: int = 900,
    sync_window_days: int = 30,
    max_messages_per_run: int = 200,
) -> dict[str, Any]:
    spool = Path(spool_path).expanduser().resolve()
    profile = database.insert_mail_profile(
        name=name,
        source_type=source_type,
        account=account,
        server=server,
        folder_paths=folder_paths,
        spool_path=str(spool),
        post_process_policy=post_process_policy,
        trust_rank=trust_rank,
        sync_enabled=sync_enabled,
        sync_interval_seconds=sync_interval_seconds,
        sync_window_days=sync_window_days,
        max_messages_per_run=max_messages_per_run,
        metadata={},
    )
    ready_root = spool / "ready"
    ready_root.mkdir(parents=True, exist_ok=True)
    database.add_monitored_root(
        name=f"mail-{name}",
        root_path=ready_root,
        watch_enabled=False,
        enabled=True,
        trust_rank=trust_rank,
        metadata={"mail_profile": name, "source_type": source_type},
    )
    return profile


def mail_status() -> dict[str, Any]:
    payload = database.mail_status()
    try:
        from .mail_oauth import oauth_status

        payload["oauth"] = oauth_status()
    except Exception as exc:
        payload["oauth"] = {"status": "unavailable", "error": str(exc)}
    return payload


def sync_mail_profile(
    profile_name: str | None = None,
    *,
    access_token: str | None = None,
    imap_client_factory: Any | None = None,
    allow_outlook_com: bool = False,
) -> dict[str, Any]:
    profiles = database.list_mail_profiles(name=profile_name)
    results: list[dict[str, Any]] = []
    for profile in profiles:
        if not profile["enabled"]:
            continue
        if profile["source_type"] == "imap":
            result = _sync_imap_profile(profile, access_token=access_token, imap_client_factory=imap_client_factory)
        elif profile["source_type"] == "outlook_com":
            if allow_outlook_com:
                result = _sync_outlook_profile(profile)
            else:
                result = {
                    "profile": profile["name"],
                    "status": "outlook_host_required",
                    "command": "flux-kb outlook-host run",
                    "exported": 0,
                }
        else:
            result = {"profile": profile["name"], "status": "unsupported_source_type"}
        spool_result = sync_mail_spool(profile_name=profile["name"])
        result["spool_sync"] = spool_result
        results.append(result)
    return {"profiles": results, "count": len(results)}


def sync_outlook_profile(profile_name: str) -> dict[str, Any]:
    profiles = database.list_mail_profiles(name=profile_name)
    if not profiles:
        raise ValueError(f"mail profile not found: {profile_name}")
    profile = profiles[0]
    if profile["source_type"] != "outlook_com":
        raise ValueError(f"mail profile is not Outlook COM: {profile_name}")
    result = _sync_outlook_profile(profile)
    result["spool_sync"] = sync_mail_spool(profile_name=profile_name)
    return result


def sync_mail_spool(profile_name: str | None = None) -> dict[str, Any]:
    from .service import KnowledgeService

    profiles = database.list_mail_profiles(name=profile_name)
    synced: list[dict[str, Any]] = []
    for profile in profiles:
        root_name = f"mail-{profile['name']}"
        result = KnowledgeService().sync_corpus(root_name=root_name)
        synced.append({"profile": profile["name"], **result})
    return {"profiles": synced, "count": len(synced)}


def render_outlook_config(profile_name: str, *, spool_path: str | Path, folder_paths: Iterable[str]) -> str:
    lines = [
        "[flux-kb-outlook]",
        f"profile={profile_name}",
        f"spool_path={Path(spool_path).expanduser().resolve()}",
    ]
    for index, folder in enumerate(folder_paths, start=1):
        lines.append(f"folder_{index}={folder}")
    return "\n".join(lines) + "\n"


def sync_imap_folder(
    *,
    client: Any,
    profile_name: str,
    account: str,
    folder: str,
    spool_path: str | Path,
    previous_uid: int = 0,
    previous_uidvalidity: int | None = None,
    post_process_policy: str = "move_to_processed",
    processed_folder: str | None = None,
) -> dict[str, Any]:
    client.select(folder)
    uidvalidity = _imap_uidvalidity(client)
    uidvalidity_changed = previous_uidvalidity is not None and uidvalidity != previous_uidvalidity
    start_uid = 1 if uidvalidity_changed else max(previous_uid + 1, 1)
    status, data = client.uid("SEARCH", None, f"UID {start_uid}:*")
    if status != "OK":
        raise RuntimeError(f"IMAP search failed for {folder}")
    uids = _parse_uids(data)
    exported = 0
    last_uid = previous_uid if not uidvalidity_changed else 0
    for uid in uids:
        fetch_status, fetch_data = client.uid("FETCH", str(uid), "(RFC822)")
        if fetch_status != "OK":
            database.record_mail_message(
                profile_name=profile_name,
                source_message_id=f"imap:{folder}:{uid}",
                source_folder=folder,
                export_state="error",
                error=f"IMAP fetch failed for UID {uid}",
            )
            continue
        raw_message = _extract_fetch_rfc822(fetch_data)
        result = export_email_to_spool(
            raw_message=raw_message,
            spool_path=spool_path,
            profile_name=profile_name,
            source_type="imap",
            source_folder=folder,
            source_message_id=f"uid:{uid}",
            extra_metadata={"account": account, "uid": uid, "uidvalidity": uidvalidity},
        )
        database.record_mail_message(
            profile_name=profile_name,
            source_message_id=f"imap:{folder}:{uid}",
            source_folder=folder,
            export_state="exported",
            export_id=result.export_id,
            content_hash=result.manifest["content_hash"],
            internet_message_id=result.manifest.get("message_id"),
            metadata={"account": account, "uid": uid, "uidvalidity": uidvalidity},
        )
        _post_process_imap_message(
            client,
            uid=uid,
            post_process_policy=post_process_policy,
            processed_folder=processed_folder,
        )
        exported += 1
        last_uid = max(last_uid, uid)
    return {
        "profile_name": profile_name,
        "folder": folder,
        "uidvalidity": uidvalidity,
        "uidvalidity_changed": uidvalidity_changed,
        "last_uid": last_uid,
        "seen": len(uids),
        "exported": exported,
    }


def export_outlook_item_to_spool(
    *,
    item: Any,
    spool_path: str | Path,
    profile_name: str,
    folder_path: str,
) -> MailExportResult:
    raw_message = _outlook_item_to_email(item)
    result = export_email_to_spool(
        raw_message=raw_message,
        spool_path=spool_path,
        profile_name=profile_name,
        source_type="outlook_com",
        source_folder=folder_path,
        source_message_id=f"entry:{getattr(item, 'EntryID', '')}",
        extra_metadata={
            "outlook_entry_id": getattr(item, "EntryID", None),
            "outlook_store_id": getattr(item, "StoreID", None),
        },
    )
    _save_outlook_msg_backup(item, result.ready_path / "message.msg")
    _save_outlook_attachments(item, result.ready_path / "attachments")
    manifest = dict(result.manifest)
    manifest["outlook_entry_id"] = getattr(item, "EntryID", None)
    manifest["outlook_store_id"] = getattr(item, "StoreID", None)
    (result.ready_path / "manifest.json").write_text(json.dumps(manifest, indent=2, sort_keys=True), encoding="utf-8")
    return MailExportResult(export_id=result.export_id, ready_path=result.ready_path, manifest=manifest)


class ImapSyncClient:
    """Small adapter over imaplib; tests can pass fakes with the same methods."""

    def __init__(self, host: str, *, port: int = 993) -> None:
        import imaplib

        self._client = imaplib.IMAP4_SSL(host, port)

    def authenticate_xoauth2(self, user: str, access_token: str) -> None:
        auth_string = build_xoauth2_string(user, access_token)
        self._client.authenticate("XOAUTH2", lambda _challenge: auth_string.encode("ascii"))

    def select(self, folder: str):
        return self._client.select(folder)

    def uid(self, *args):
        return self._client.uid(*args)

    def close(self) -> None:
        self._client.close()

    def logout(self) -> None:
        self._client.logout()


def _sync_imap_profile(
    profile: dict[str, Any],
    *,
    access_token: str | None,
    imap_client_factory: Any | None,
) -> dict[str, Any]:
    token = access_token or _oauth_access_token(profile["name"])
    if not token:
        token = _legacy_oauth_token()
    if not token:
        database.record_mail_sync_run(
            profile_name=profile["name"],
            status="blocked_auth_required",
            errors=[{"error": "Gmail OAuth is not configured for this mail profile"}],
        )
        return {"profile": profile["name"], "status": "blocked_auth_required", "exported": 0}
    if token == "__auth_expired__":
        database.record_mail_sync_run(
            profile_name=profile["name"],
            status="auth_expired",
            errors=[{"error": "Gmail OAuth refresh failed; re-run OAuth setup"}],
        )
        return {"profile": profile["name"], "status": "auth_expired", "exported": 0}
    factory = imap_client_factory or ImapSyncClient
    cursors = dict((profile.get("metadata") or {}).get("cursors") or {})
    folder_results: list[dict[str, Any]] = []
    total_seen = 0
    total_exported = 0
    for folder in profile["folder_paths"]:
        client = factory(profile.get("server") or "imap.gmail.com")
        try:
            if hasattr(client, "authenticate_xoauth2"):
                client.authenticate_xoauth2(profile.get("account") or "", token)
            previous = cursors.get(folder, {})
            result = sync_imap_folder(
                client=client,
                profile_name=profile["name"],
                account=profile.get("account") or "",
                folder=folder,
                spool_path=profile["spool_path"],
                previous_uid=int(previous.get("last_uid", 0) or 0),
                previous_uidvalidity=previous.get("uidvalidity"),
                post_process_policy=profile["post_process_policy"],
                processed_folder=(profile.get("metadata") or {}).get("processed_folder"),
            )
            cursors[folder] = {"last_uid": result["last_uid"], "uidvalidity": result["uidvalidity"]}
            folder_results.append(result)
            total_seen += result["seen"]
            total_exported += result["exported"]
        finally:
            _close_imap_client(client)
    metadata = dict(profile.get("metadata") or {})
    metadata["cursors"] = cursors
    database.update_mail_profile_metadata(name=profile["name"], metadata=metadata)
    database.record_mail_sync_run(
        profile_name=profile["name"],
        status="completed",
        messages_seen=total_seen,
        messages_exported=total_exported,
        last_cursor=cursors,
    )
    return {"profile": profile["name"], "status": "completed", "folders": folder_results, "exported": total_exported}


def _oauth_access_token(profile_name: str) -> str | None:
    try:
        from . import mail_oauth

        return mail_oauth.access_token_for_profile(profile_name)
    except ImportError:
        return None
    except Exception as exc:
        if exc.__class__.__name__ == "OAuthAuthExpired":
            return "__auth_expired__"
        return None


def _legacy_oauth_token() -> str:
    try:
        from .settings import SettingsService

        return str(SettingsService().resolve("mail.imap.oauth_refresh_token").raw_value or "")
    except Exception:
        return ""


def _sync_outlook_profile(profile: dict[str, Any]) -> dict[str, Any]:
    try:
        import win32com.client  # type: ignore[import-not-found]
    except ImportError:
        database.record_mail_sync_run(
            profile_name=profile["name"],
            status="blocked_missing_dependency",
            errors=[{"error": "pywin32 is required for Outlook COM catch-up"}],
        )
        return {"profile": profile["name"], "status": "blocked_missing_dependency", "exported": 0}

    outlook = win32com.client.Dispatch("Outlook.Application")
    namespace = outlook.GetNamespace("MAPI")
    exported = 0
    errors: list[dict[str, Any]] = []
    for folder_path in profile["folder_paths"]:
        try:
            folder = _resolve_outlook_folder(namespace, normalize_outlook_folder_path(folder_path))
            for item in folder.Items:
                result = export_outlook_item_to_spool(
                    item=item,
                    spool_path=profile["spool_path"],
                    profile_name=profile["name"],
                    folder_path=folder_path,
                )
                database.record_mail_message(
                    profile_name=profile["name"],
                    source_message_id=f"outlook:{getattr(item, 'EntryID', result.export_id)}",
                    source_folder=folder_path,
                    export_state="exported",
                    export_id=result.export_id,
                    content_hash=result.manifest["content_hash"],
                    internet_message_id=result.manifest.get("message_id"),
                    metadata={"outlook_store_id": getattr(item, "StoreID", None)},
                )
                exported += 1
        except Exception as exc:
            errors.append({"folder": folder_path, "error": str(exc)})
    status = "completed" if not errors else "partial"
    database.record_mail_sync_run(
        profile_name=profile["name"],
        status=status,
        messages_exported=exported,
        errors=errors,
    )
    return {"profile": profile["name"], "status": status, "exported": exported, "errors": errors}


def _part_text(part: Message) -> str:
    try:
        value = part.get_content()
    except Exception:
        payload = part.get_payload(decode=True) or b""
        value = payload.decode(part.get_content_charset() or "utf-8", errors="replace")
    return str(value)


def _imap_uidvalidity(client: Any) -> int:
    if hasattr(client, "response"):
        status, data = client.response("UIDVALIDITY")
        if status == "OK" and data:
            try:
                return int(data[0])
            except (TypeError, ValueError):
                pass
    return 0


def _parse_uids(data: Any) -> list[int]:
    if not data:
        return []
    first = data[0]
    if isinstance(first, bytes):
        first = first.decode("ascii", errors="ignore")
    return [int(value) for value in str(first).split() if value.isdigit()]


def _extract_fetch_rfc822(fetch_data: Any) -> bytes:
    for item in fetch_data or []:
        if isinstance(item, tuple) and len(item) >= 2 and isinstance(item[1], (bytes, bytearray)):
            return bytes(item[1])
    raise RuntimeError("IMAP FETCH response did not include RFC822 bytes")


def _post_process_imap_message(
    client: Any,
    *,
    uid: int,
    post_process_policy: str,
    processed_folder: str | None,
) -> None:
    if post_process_policy == "none":
        return
    if post_process_policy == "move_to_processed" and processed_folder:
        client.uid("COPY", str(uid), processed_folder)
        client.uid("STORE", str(uid), "+FLAGS", r"(\Deleted)")
        return
    if post_process_policy == "trash":
        client.uid("STORE", str(uid), "+FLAGS", r"(\Deleted)")


def _close_imap_client(client: Any) -> None:
    for method_name in ("close", "logout"):
        method = getattr(client, method_name, None)
        if not method:
            continue
        try:
            method()
        except Exception:
            pass


def _resolve_outlook_folder(namespace: Any, folder_path: OutlookFolderPath) -> Any:
    root = None
    for folder in namespace.Folders:
        if str(folder.Name) == folder_path.mailbox:
            root = folder
            break
    if root is None:
        raise ValueError(f"Outlook mailbox not found: {folder_path.mailbox}")
    current = root
    for part in folder_path.parts:
        next_folder = None
        for child in current.Folders:
            if str(child.Name) == part:
                next_folder = child
                break
        if next_folder is None:
            raise ValueError(f"Outlook folder not found: {part}")
        current = next_folder
    return current


def _outlook_item_to_email(item: Any) -> bytes:
    message = EmailMessage()
    message["Subject"] = str(getattr(item, "Subject", ""))
    message["From"] = str(getattr(item, "SenderEmailAddress", ""))
    message["To"] = str(getattr(item, "To", ""))
    internet_message_id = getattr(item, "InternetMessageID", None)
    if internet_message_id:
        message["Message-ID"] = str(internet_message_id)
    received = getattr(item, "ReceivedTime", None)
    if received:
        message["Date"] = str(received)
    body = str(getattr(item, "Body", "") or "")
    html_body = str(getattr(item, "HTMLBody", "") or "")
    if html_body:
        message.set_content(body or _strip_html(html_body))
        message.add_alternative(html_body, subtype="html")
    else:
        message.set_content(body)
    return message.as_bytes()


def _save_outlook_msg_backup(item: Any, path: Path) -> None:
    if not hasattr(item, "SaveAs"):
        return
    try:
        item.SaveAs(str(path), 3)
    except TypeError:
        item.SaveAs(str(path))


def _save_outlook_attachments(item: Any, attachments_dir: Path) -> None:
    attachments = getattr(item, "Attachments", None)
    count = int(getattr(attachments, "Count", 0) or 0)
    if not attachments or count < 1:
        return
    attachments_dir.mkdir(exist_ok=True)
    for index in range(1, count + 1):
        attachment = attachments.Item(index)
        filename = _safe_filename(str(getattr(attachment, "FileName", f"attachment-{index}.bin")))
        attachment.SaveAsFile(str(attachments_dir / filename))


def _strip_html(value: str) -> str:
    return re.sub(r"<[^>]+>", " ", value)


def _split_addresses(value: str) -> list[str]:
    return [part.strip() for part in value.split(",") if part.strip()]


def _safe_filename(value: str) -> str:
    sanitized = re.sub(r"[^A-Za-z0-9._ -]+", "_", value).strip(" .")
    return sanitized or "attachment.bin"


def _export_id(profile_name: str, source_folder: str, source_message_id: str, raw_message: bytes) -> str:
    digest = hashlib.sha256()
    digest.update(profile_name.encode("utf-8", errors="ignore"))
    digest.update(b"\0")
    digest.update(source_folder.encode("utf-8", errors="ignore"))
    digest.update(b"\0")
    digest.update(source_message_id.encode("utf-8", errors="ignore"))
    digest.update(b"\0")
    digest.update(hashlib.sha256(raw_message).hexdigest().encode("ascii"))
    return digest.hexdigest()[:32]
