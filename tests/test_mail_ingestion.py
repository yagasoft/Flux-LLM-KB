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

    assert value == "user=user@gmail.com\x01auth=Bearer access-token\x01\x01"
    assert "\x01" in value


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


def test_export_email_to_spool_writes_text_body_from_html_fallback(tmp_path):
    message = EmailMessage()
    message["Subject"] = "HTML only"
    message["From"] = "sender@example.com"
    message["To"] = "receiver@example.com"
    message.set_content("<p>Please <strong>review</strong> this update.</p>", subtype="html")

    result = export_email_to_spool(
        raw_message=message.as_bytes(),
        spool_path=tmp_path,
        profile_name="gmail-capture",
        source_type="imap",
        source_folder="FluxCapture",
        source_message_id="uid:43",
    )

    assert (result.ready_path / "body.txt").read_text(encoding="utf-8") == "Please review this update."
    assert (result.ready_path / "body.html").exists()


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
    assert calls[0]["metadata"]["strict_indexing"] is True


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
    monkeypatch.setattr(database, "record_mail_post_process_event", lambda **kwargs: kwargs)

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


def test_sync_imap_folder_trash_policy_executes_delete_and_expunge(monkeypatch, tmp_path):
    calls = []

    class FakeClient:
        def select(self, folder):
            return "OK", [b"1"]

        def response(self, key):
            return "OK", [b"1"]

        def uid(self, command, *args):
            calls.append((command, args))
            if command == "SEARCH":
                return "OK", [b"7"]
            if command == "FETCH":
                return "OK", [(b"7 (RFC822)", _sample_message())]
            if command == "STORE":
                return "OK", []
            raise AssertionError(command)

        def expunge(self):
            calls.append(("EXPUNGE", ()))
            return "OK", []

    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: {"id": "mail-1"})
    monkeypatch.setattr(database, "record_mail_post_process_event", lambda **kwargs: kwargs)

    result = sync_imap_folder(
        client=FakeClient(),
        profile_name="gmail-capture",
        account="me@gmail.com",
        folder="FluxCapture",
        spool_path=tmp_path,
        post_process_policy="trash",
        profile_metadata={"provider": "imap", "destructive_post_process_confirmed": True},
    )

    assert result["exported"] == 1
    assert ("STORE", ("7", "+FLAGS", r"(\Deleted)")) in calls
    assert ("EXPUNGE", ()) in calls


def test_sync_imap_folder_records_post_process_failure_without_advancing_cursor(monkeypatch, tmp_path):
    class FakeClient:
        def select(self, folder):
            return "OK", [b"1"]

        def response(self, key):
            return "OK", [b"1"]

        def uid(self, command, *args):
            if command == "SEARCH":
                return "OK", [b"7"]
            if command == "FETCH":
                return "OK", [(b"7 (RFC822)", _sample_message())]
            if command == "COPY":
                raise RuntimeError("COPY denied")
            raise AssertionError(command)

    events = []
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: {"id": "mail-1", "export_state": kwargs["export_state"]})
    monkeypatch.setattr(database, "record_mail_post_process_event", lambda **kwargs: events.append(kwargs) or kwargs)

    result = sync_imap_folder(
        client=FakeClient(),
        profile_name="gmail-capture",
        account="me@gmail.com",
        folder="FluxCapture",
        spool_path=tmp_path,
        previous_uid=6,
        post_process_policy="move_to_processed",
        processed_folder="FluxProcessed",
        profile_metadata={"provider": "imap"},
        sync_run_id="run-1",
    )

    assert result["exported"] == 1
    assert result["last_uid"] == 6
    assert result["post_process_errors"][0]["status"] == "failed"
    assert "COPY denied" in result["post_process_errors"][0]["error"]
    assert events[0]["mail_message_id"] == "mail-1"
    assert events[0]["sync_run_id"] == "run-1"
    assert events[0]["status"] == "failed"


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


def test_sync_imap_profile_refreshes_oauth_token_before_login(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion, mail_oauth

    auth_calls = []

    class FakeClient:
        def __init__(self, host):
            self.host = host

        def authenticate_xoauth2(self, user, token):
            auth_calls.append((user, token))

        def select(self, folder):
            return "OK", [b"1"]

        def response(self, key):
            return "OK", [b"1"]

        def uid(self, command, *args):
            if command == "SEARCH":
                return "OK", [b""]
            raise AssertionError(command)

        def close(self):
            return None

        def logout(self):
            return None

    profile = {
        "name": "gmail",
        "source_type": "imap",
        "enabled": True,
        "account": "me@gmail.com",
        "server": "imap.gmail.com",
        "folder_paths": ["FluxCapture"],
        "spool_path": str(tmp_path),
        "post_process_policy": "none",
        "metadata": {},
    }
    runs = []
    monkeypatch.setattr(database, "list_mail_profiles", lambda name=None: [profile])
    monkeypatch.setattr(database, "create_imap_sync_run", lambda **kwargs: {"id": "run-manual", "status": "queued", "attempt_count": 0})
    monkeypatch.setattr(database, "mark_mail_sync_run_running", lambda **kwargs: None)
    monkeypatch.setattr(database, "complete_mail_sync_run", lambda **kwargs: runs.append(kwargs) or {"id": kwargs["run_id"], "status": kwargs["status"]})
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: profile | {"metadata": kwargs["metadata"]})
    monkeypatch.setattr(mail_ingestion, "sync_mail_spool", lambda profile_name=None: {"profiles": [], "count": 0})
    monkeypatch.setattr(mail_oauth, "access_token_for_profile", lambda profile_name: "fresh-access-token")

    result = mail_ingestion.sync_mail_profile(profile_name="gmail", imap_client_factory=FakeClient)

    assert result["profiles"][0]["status"] == "completed"
    assert result["profiles"][0]["run_id"] == "run-manual"
    assert auth_calls == [("me@gmail.com", "fresh-access-token")]
    assert runs[0]["status"] == "completed"


def test_sync_imap_profile_reports_auth_required_without_crashing(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion, mail_oauth

    profile = {
        "name": "gmail",
        "source_type": "imap",
        "enabled": True,
        "account": "me@gmail.com",
        "server": "imap.gmail.com",
        "folder_paths": ["FluxCapture"],
        "spool_path": str(tmp_path),
        "post_process_policy": "none",
        "metadata": {},
    }
    runs = []
    monkeypatch.setattr(database, "list_mail_profiles", lambda name=None: [profile])
    monkeypatch.setattr(database, "create_imap_sync_run", lambda **kwargs: {"id": "run-manual", "status": "queued", "attempt_count": 0})
    monkeypatch.setattr(database, "mark_mail_sync_run_running", lambda **kwargs: None)
    monkeypatch.setattr(database, "complete_mail_sync_run", lambda **kwargs: runs.append(kwargs) or {"id": kwargs["run_id"], "status": kwargs["status"]})
    monkeypatch.setattr(mail_ingestion, "sync_mail_spool", lambda profile_name=None: {"profiles": [], "count": 0})
    monkeypatch.setattr(mail_oauth, "access_token_for_profile", lambda profile_name: None)

    result = mail_ingestion.sync_mail_profile(profile_name="gmail")

    assert result["profiles"][0]["status"] == "blocked_auth_required"
    assert runs[0]["status"] == "blocked_auth_required"


def test_sync_imap_profile_reports_expired_oauth_without_crashing(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion, mail_oauth

    profile = {
        "name": "gmail",
        "source_type": "imap",
        "enabled": True,
        "account": "me@gmail.com",
        "server": "imap.gmail.com",
        "folder_paths": ["FluxCapture"],
        "spool_path": str(tmp_path),
        "post_process_policy": "none",
        "metadata": {},
    }
    runs = []
    monkeypatch.setattr(database, "list_mail_profiles", lambda name=None: [profile])
    monkeypatch.setattr(database, "create_imap_sync_run", lambda **kwargs: {"id": "run-manual", "status": "queued", "attempt_count": 0})
    monkeypatch.setattr(database, "mark_mail_sync_run_running", lambda **kwargs: None)
    monkeypatch.setattr(database, "complete_mail_sync_run", lambda **kwargs: runs.append(kwargs) or {"id": kwargs["run_id"], "status": kwargs["status"]})
    monkeypatch.setattr(mail_ingestion, "sync_mail_spool", lambda profile_name=None: {"profiles": [], "count": 0})
    monkeypatch.setattr(mail_oauth, "access_token_for_profile", lambda profile_name: (_ for _ in ()).throw(mail_oauth.OAuthAuthExpired("invalid_grant")))

    result = mail_ingestion.sync_mail_profile(profile_name="gmail")

    assert result["profiles"][0]["status"] == "auth_expired"
    assert runs[0]["status"] == "auth_expired"


def test_sync_imap_profile_reports_auth_failure_without_500(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion, mail_oauth

    class FakeClient:
        def __init__(self, host):
            self.host = host
            self.closed = False

        def authenticate_xoauth2(self, user, token):
            raise RuntimeError("AUTHENTICATE command error: BAD Invalid SASL argument")

        def close(self):
            self.closed = True

        def logout(self):
            self.closed = True

    profile = {
        "name": "gmail",
        "source_type": "imap",
        "enabled": True,
        "account": "me@gmail.com",
        "server": "imap.gmail.com",
        "folder_paths": ["FluxCapture"],
        "spool_path": str(tmp_path),
        "post_process_policy": "none",
        "metadata": {},
    }
    runs = []
    monkeypatch.setattr(database, "list_mail_profiles", lambda name=None: [profile])
    monkeypatch.setattr(database, "create_imap_sync_run", lambda **kwargs: {"id": "run-manual", "status": "queued", "attempt_count": 0})
    monkeypatch.setattr(database, "mark_mail_sync_run_running", lambda **kwargs: None)
    monkeypatch.setattr(database, "complete_mail_sync_run", lambda **kwargs: runs.append(kwargs) or {"id": kwargs["run_id"], "status": kwargs["status"]})
    monkeypatch.setattr(mail_ingestion, "sync_mail_spool", lambda profile_name=None: {"profiles": [], "count": 0})
    monkeypatch.setattr(mail_oauth, "access_token_for_profile", lambda profile_name: "fresh-access-token")

    result = mail_ingestion.sync_mail_profile(profile_name="gmail", imap_client_factory=FakeClient)

    assert result["profiles"][0]["status"] == "auth_failed"
    assert "Invalid SASL argument" in result["profiles"][0]["errors"][0]["error"]
    assert runs[0]["status"] == "auth_failed"


def test_sync_due_mail_profiles_runs_due_imap_profiles(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion, mail_oauth

    auth_calls = []
    listed_limits = []

    class FakeClient:
        def __init__(self, host):
            self.host = host

        def authenticate_xoauth2(self, user, token):
            auth_calls.append((user, token))

        def select(self, folder):
            return "OK", [b"1"]

        def response(self, key):
            return "OK", [b"1"]

        def uid(self, command, *args):
            if command == "SEARCH":
                return "OK", [b""]
            raise AssertionError(command)

        def close(self):
            return None

        def logout(self):
            return None

    profile = {
        "name": "gmail",
        "source_type": "imap",
        "enabled": True,
        "account": "me@gmail.com",
        "server": "imap.gmail.com",
        "folder_paths": ["FluxCapture"],
        "spool_path": str(tmp_path),
        "post_process_policy": "none",
        "metadata": {},
    }
    runs = []
    monkeypatch.setattr(
        database,
        "claim_due_imap_sync_runs",
        lambda *, limit=10, worker_id="flux-kb-mail-worker": listed_limits.append((limit, worker_id)) or [{"id": "run-1", "attempt_count": 1, **profile}],
    )
    monkeypatch.setattr(database, "mark_mail_sync_run_running", lambda **kwargs: None)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: profile | {"metadata": kwargs["metadata"]})
    monkeypatch.setattr(database, "complete_mail_sync_run", lambda **kwargs: runs.append(kwargs) or {"id": kwargs["run_id"], "status": kwargs["status"]})
    monkeypatch.setattr(mail_ingestion, "sync_mail_spool", lambda profile_name=None: {"profiles": [], "count": 0})
    monkeypatch.setattr(mail_oauth, "access_token_for_profile", lambda profile_name: "fresh-access-token")

    result = mail_ingestion.sync_due_mail_profiles(limit=3, imap_client_factory=FakeClient)

    assert listed_limits == [(3, "flux-kb-mail-worker")]
    assert result["count"] == 1
    assert result["profiles"][0]["profile"] == "gmail"
    assert result["profiles"][0]["status"] == "completed"
    assert result["profiles"][0]["run_id"] == "run-1"
    assert auth_calls == [("me@gmail.com", "fresh-access-token")]
    assert runs[0]["status"] == "completed"


def test_sync_mail_spool_blocks_when_mail_root_unavailable(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion
    from flux_llm_kb.service import KnowledgeService

    missing_root = tmp_path / "missing-spool"
    calls = []
    monkeypatch.setattr(
        database,
        "list_mail_profiles",
        lambda name=None: [
            {
                "name": "gmail",
                "source_type": "imap",
                "enabled": True,
                "spool_path": str(missing_root.parent),
                "metadata": {},
            }
        ],
    )
    monkeypatch.setattr(
        database,
        "get_monitored_root",
        lambda root_name: {
            "name": root_name,
            "root_path": str(missing_root),
            "metadata": {"mail_profile": "gmail", "strict_indexing": True},
        },
    )
    monkeypatch.setattr(
        KnowledgeService,
        "sync_corpus",
        lambda self, **kwargs: calls.append(kwargs) or {"files_deleted": 99},
    )

    result = mail_ingestion.sync_mail_spool(profile_name="gmail")

    assert calls == []
    assert result["count"] == 1
    assert result["profiles"][0]["profile"] == "gmail"
    assert result["profiles"][0]["status"] == "blocked_spool_unavailable"
    assert result["profiles"][0]["files_deleted"] == 0
    assert "not accessible" in result["profiles"][0]["error"]
