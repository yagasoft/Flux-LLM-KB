import json
from email.message import EmailMessage
from pathlib import Path

from flux_llm_kb import database
from flux_llm_kb.mail_ingestion import (
    build_xoauth2_string,
    export_email_to_spool,
    export_outlook_item_to_spool,
    normalize_outlook_folder_path,
    parse_email_bytes,
    scan_ready_spool,
    sync_imap_folder,
)


def _sample_message() -> bytes:
    message = EmailMessage()
    message["Subject"] = "Customer RFP"
    message["From"] = "Sender <sender@example.com>"
    message["To"] = "receiver@example.com"
    message["Message-ID"] = "<msg-1@example.com>"
    message["Date"] = "Fri, 20 Jun 2026 10:00:00 +0000"
    message.set_content("Please review the attached RFP.")
    message.add_attachment(
        b"attachment-content",
        maintype="application",
        subtype="pdf",
        filename="rfp.pdf",
    )
    return message.as_bytes()


def test_build_xoauth2_string_uses_gmail_imap_shape():
    value = build_xoauth2_string("user@gmail.com", "access-token")

    assert value.endswith("=")
    assert " " not in value


def test_parse_email_bytes_extracts_body_metadata_and_attachments():
    parsed = parse_email_bytes(_sample_message())

    assert parsed.subject == "Customer RFP"
    assert parsed.message_id == "<msg-1@example.com>"
    assert "attached RFP" in parsed.text_body
    assert parsed.attachments[0].filename == "rfp.pdf"


def test_export_email_to_spool_writes_ready_manifest_and_ignores_inflight(tmp_path):
    result = export_email_to_spool(
        raw_message=_sample_message(),
        spool_path=tmp_path,
        profile_name="gmail-capture",
        source_type="imap",
        source_folder="FluxCapture",
        source_message_id="uid:42",
    )

    assert result.ready_path.parent.name == "ready"
    assert not (tmp_path / "_inflight" / result.export_id).exists()
    assert (result.ready_path / "manifest.json").exists()
    assert (result.ready_path / "message.eml").exists()
    assert (result.ready_path / "body.txt").read_text(encoding="utf-8").startswith("Please review")
    assert (result.ready_path / "attachments" / "rfp.pdf").exists()

    manifests = scan_ready_spool(tmp_path)

    assert [manifest["export_id"] for manifest in manifests] == [result.export_id]
    assert json.loads((result.ready_path / "manifest.json").read_text(encoding="utf-8"))["attachment_count"] == 1


def test_normalize_outlook_folder_path_splits_mailbox_and_nested_folders():
    path = normalize_outlook_folder_path("Mailbox - Me/Inbox/Flux Capture")

    assert path.mailbox == "Mailbox - Me"
    assert path.parts == ("Inbox", "Flux Capture")


def test_mail_profile_registers_ready_spool_root(monkeypatch, tmp_path):
    calls = []
    monkeypatch.setattr(database, "insert_mail_profile", lambda **kwargs: {"id": "profile-1", **kwargs})
    monkeypatch.setattr(database, "add_monitored_root", lambda **kwargs: calls.append(kwargs) or {"name": kwargs["name"]})

    from flux_llm_kb.mail_ingestion import add_mail_profile

    profile = add_mail_profile(
        name="gmail-capture",
        source_type="imap",
        spool_path=tmp_path,
        folder_paths=["FluxCapture"],
        account="user@gmail.com",
    )

    assert profile["name"] == "gmail-capture"
    assert calls[0]["name"] == "mail-gmail-capture"
    assert calls[0]["root_path"] == tmp_path / "ready"
    assert calls[0]["metadata"]["mail_profile"] == "gmail-capture"


def test_sync_imap_folder_resets_cursor_when_uidvalidity_changes(monkeypatch, tmp_path):
    class FakeClient:
        def __init__(self):
            self.searches = []
            self.post_processed = []

        def select(self, folder):
            return "OK", [b"1"]

        def response(self, key):
            assert key == "UIDVALIDITY"
            return "OK", [b"9"]

        def uid(self, command, *args):
            if command == "SEARCH":
                self.searches.append(args[-1])
                return "OK", [b"3"]
            if command == "FETCH":
                return "OK", [(b"3 (RFC822)", _sample_message())]
            if command == "COPY":
                self.post_processed.append(("COPY", args))
                return "OK", []
            if command == "STORE":
                self.post_processed.append(("STORE", args))
                return "OK", []
            raise AssertionError(command)

    records = []
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: records.append(kwargs) or {"id": "mail-1"})

    result = sync_imap_folder(
        client=FakeClient(),
        profile_name="gmail-capture",
        account="me@gmail.com",
        folder="FluxCapture",
        spool_path=tmp_path,
        previous_uid=50,
        previous_uidvalidity=8,
        processed_folder="FluxProcessed",
    )

    assert result["uidvalidity_changed"] is True
    assert result["last_uid"] == 3
    assert result["exported"] == 1
    assert records[0]["source_message_id"] == "imap:FluxCapture:3"


def test_export_outlook_item_to_spool_writes_msg_backup_and_manifest(tmp_path):
    class FakeAttachment:
        FileName = "notes.txt"

        def SaveAsFile(self, path):
            Path(path).write_text("attachment", encoding="utf-8")

    class FakeAttachments:
        Count = 1

        def Item(self, index):
            assert index == 1
            return FakeAttachment()

    class FakeItem:
        Subject = "Outlook catch-up"
        SenderEmailAddress = "sender@example.com"
        To = "me@example.com"
        EntryID = "entry-1"
        StoreID = "store-1"
        InternetMessageID = "<outlook-1@example.com>"
        ReceivedTime = "2026-06-20 10:00:00"
        Body = "Outlook body text"
        HTMLBody = "<p>Outlook body text</p>"
        Attachments = FakeAttachments()

        def SaveAs(self, path, *_args):
            Path(path).write_bytes(b"msg")

    result = export_outlook_item_to_spool(
        item=FakeItem(),
        spool_path=tmp_path,
        profile_name="outlook-catchup",
        folder_path="Mailbox/Inbox/Flux",
    )

    assert (result.ready_path / "message.msg").read_bytes() == b"msg"
    assert (result.ready_path / "attachments" / "notes.txt").read_text(encoding="utf-8") == "attachment"
    assert result.manifest["source_type"] == "outlook_com"
    assert result.manifest["outlook_entry_id"] == "entry-1"
