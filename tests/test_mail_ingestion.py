import json
from datetime import datetime, timezone
from email.message import EmailMessage
from pathlib import Path
import sys
from types import SimpleNamespace

from flux_llm_kb import database
from flux_llm_kb.mail_ingestion import (
    _resolve_host_spool_path,
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


def test_resolve_host_spool_path_maps_container_private_mount(monkeypatch, tmp_path):
    private_dir = tmp_path / "private"
    monkeypatch.setenv("FLUX_KB_PRIVATE_DIR", str(private_dir))

    path = _resolve_host_spool_path("/app/private/mail-spool/outlook-catchup")

    assert path == (private_dir / "mail-spool" / "outlook-catchup").resolve()


def test_resolve_host_spool_path_keeps_container_private_mount_when_env_is_container_native(monkeypatch):
    monkeypatch.setenv("FLUX_KB_PRIVATE_DIR", "/app/private")

    path = _resolve_host_spool_path("/app/private/mail-spool/outlook-catchup")

    assert path == Path("/app/private/mail-spool/outlook-catchup").expanduser().resolve()


def test_resolve_host_spool_path_ignores_windows_private_dir_outside_windows(monkeypatch):
    monkeypatch.setenv("FLUX_KB_PRIVATE_DIR", r"D:\FluxLLMKB\private")

    path = _resolve_host_spool_path("/app/private/mail-spool/outlook-catchup", platform_os_name="posix")

    assert path == Path("/app/private/mail-spool/outlook-catchup").expanduser().resolve()


def test_export_email_to_spool_uses_host_spool_path_resolver(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    calls = []
    mapped_spool = tmp_path / "mapped-spool"

    def fake_resolver(spool_path):
        calls.append(spool_path)
        return mapped_spool

    monkeypatch.setattr(mail_ingestion, "_resolve_host_spool_path", fake_resolver)

    result = mail_ingestion.export_email_to_spool(
        raw_message=_sample_message(),
        spool_path="/app/private/mail-spool/outlook-catchup",
        profile_name="outlook-catchup",
        source_type="outlook_com",
        source_folder="Mailbox/Inbox/Flux",
        source_message_id="entry-1",
    )

    assert calls == ["/app/private/mail-spool/outlook-catchup"]
    assert mapped_spool in result.ready_path.parents


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


def test_outlook_mail_profile_normalizes_imap_account_and_server(monkeypatch, tmp_path):
    calls = []
    monkeypatch.setattr(database, "insert_mail_profile", lambda **kwargs: calls.append(kwargs) or {"id": "profile-1", **kwargs})
    monkeypatch.setattr(database, "add_monitored_root", lambda **kwargs: {"name": kwargs["name"]})

    from flux_llm_kb.mail_ingestion import add_mail_profile

    profile = add_mail_profile(
        name="outlook-catchup",
        source_type="outlook_com",
        spool_path=tmp_path,
        folder_paths=["Mailbox - Me\\Inbox\\Flux Capture"],
        account="stale@example.com",
        server="imap.gmail.com",
        post_process_policy="none",
    )

    assert profile["source_type"] == "outlook_com"
    assert calls[0]["account"] is None
    assert calls[0]["server"] is None


def test_outlook_mail_profile_defaults_to_include_subfolders(monkeypatch, tmp_path):
    calls = []
    monkeypatch.setattr(database, "insert_mail_profile", lambda **kwargs: calls.append(kwargs) or {"id": "profile-1", **kwargs})
    monkeypatch.setattr(database, "add_monitored_root", lambda **kwargs: {"name": kwargs["name"]})

    from flux_llm_kb.mail_ingestion import add_mail_profile

    add_mail_profile(
        name="outlook-catchup",
        source_type="outlook_com",
        spool_path=tmp_path,
        folder_paths=["Mailbox - Me\\Inbox\\MOHESR"],
        post_process_policy="none",
    )

    assert calls[0]["metadata"]["include_subfolders"] is True


def test_outlook_mail_profile_can_disable_include_subfolders(monkeypatch, tmp_path):
    calls = []
    monkeypatch.setattr(database, "insert_mail_profile", lambda **kwargs: calls.append(kwargs) or {"id": "profile-1", **kwargs})
    monkeypatch.setattr(database, "add_monitored_root", lambda **kwargs: {"name": kwargs["name"]})

    from flux_llm_kb.mail_ingestion import add_mail_profile

    add_mail_profile(
        name="outlook-catchup",
        source_type="outlook_com",
        spool_path=tmp_path,
        folder_paths=["Mailbox - Me\\Inbox\\MOHESR"],
        post_process_policy="none",
        include_subfolders=False,
    )

    assert calls[0]["metadata"]["include_subfolders"] is False


def test_outlook_mail_profile_defaults_to_received_time_incremental_basis(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    calls = []
    monkeypatch.setattr(database, "insert_mail_profile", lambda **kwargs: calls.append(kwargs) or {"name": kwargs["name"]})
    monkeypatch.setattr(database, "add_monitored_root", lambda **_kwargs: {"name": "mail-outlook-catchup"})

    mail_ingestion.add_mail_profile(
        name="outlook-catchup",
        source_type="outlook_com",
        folder_paths=["Mailbox - Me\\Inbox\\Flux Capture"],
        spool_path=tmp_path,
    )

    assert calls[0]["metadata"]["outlook_incremental_basis"] == "received_time"


def test_outlook_mail_profile_accepts_last_modification_incremental_basis(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    calls = []
    monkeypatch.setattr(database, "insert_mail_profile", lambda **kwargs: calls.append(kwargs) or {"name": kwargs["name"]})
    monkeypatch.setattr(database, "add_monitored_root", lambda **_kwargs: {"name": "mail-outlook-catchup"})

    mail_ingestion.add_mail_profile(
        name="outlook-catchup",
        source_type="outlook_com",
        folder_paths=["Mailbox - Me\\Inbox\\Flux Capture"],
        spool_path=tmp_path,
        outlook_incremental_basis="last_modification_time",
    )

    assert calls[0]["metadata"]["outlook_incremental_basis"] == "last_modification_time"


def test_sync_outlook_profile_includes_child_folders_by_default(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    class FakeItem:
        Class = 43
        StoreID = "store-1"

        def __init__(self, entry_id: str):
            self.EntryID = entry_id

    class FakeFolder:
        def __init__(self, name: str, *, items=None, folders=None):
            self.Name = name
            self.Items = items or []
            self.Folders = folders or []

    moh = FakeFolder(
        "MOHESR",
        items=[FakeItem("parent-1")],
        folders=[
            FakeFolder("PM", items=[FakeItem("pm-1")]),
            FakeFolder("Business", items=[FakeItem("business-1")]),
        ],
    )
    namespace = SimpleNamespace(Folders=[FakeFolder("Mailbox - Me", folders=[FakeFolder("Inbox", folders=[moh])])])
    fake_client = SimpleNamespace(Dispatch=lambda _name: SimpleNamespace(GetNamespace=lambda _namespace: namespace))
    monkeypatch.setitem(sys.modules, "win32com", SimpleNamespace(client=fake_client))
    monkeypatch.setitem(sys.modules, "win32com.client", fake_client)

    exports = []

    def fake_export(*, item, folder_path, **_kwargs):
        exports.append((item.EntryID, folder_path))
        return SimpleNamespace(export_id=f"export-{item.EntryID}", manifest={"content_hash": f"hash-{item.EntryID}", "message_id": None})

    monkeypatch.setattr(mail_ingestion, "export_outlook_item_to_spool", fake_export)
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "record_mail_sync_run", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: kwargs)

    result = mail_ingestion._sync_outlook_profile(
        {
            "name": "outlook-catchup",
            "source_type": "outlook_com",
            "folder_paths": ["Mailbox - Me\\Inbox\\MOHESR"],
            "spool_path": str(tmp_path),
            "metadata": {},
            "max_messages_per_run": 200,
        }
    )

    assert result["exported"] == 3
    assert exports == [
        ("parent-1", "Mailbox - Me\\Inbox\\MOHESR"),
        ("pm-1", "Mailbox - Me\\Inbox\\MOHESR\\PM"),
        ("business-1", "Mailbox - Me\\Inbox\\MOHESR\\Business"),
    ]
    assert result["spool_paths"] == ["export-parent-1", "export-pm-1", "export-business-1"]


def test_sync_outlook_profile_queues_only_changed_spool_paths(monkeypatch):
    from flux_llm_kb import mail_ingestion

    queued = []
    monkeypatch.setattr(
        mail_ingestion.database,
        "list_mail_profiles",
        lambda name=None: [
            {
                "name": name or "outlook-catchup",
                "source_type": "outlook_com",
                "enabled": True,
            }
        ],
    )
    monkeypatch.setattr(
        mail_ingestion,
        "_sync_outlook_profile",
        lambda _profile: {
            "profile": "outlook-catchup",
            "status": "completed",
            "exported": 2,
            "seen": 2,
            "errors": [],
            "spool_paths": ["export-1", "export-2"],
        },
    )
    monkeypatch.setattr(
        mail_ingestion.database,
        "get_monitored_root",
        lambda _root_name: {"root_path": "/app/private/mail-spool/outlook-catchup/ready"},
    )
    monkeypatch.setattr(
        mail_ingestion.database,
        "enqueue_corpus_sync_job",
        lambda **_kwargs: (_ for _ in ()).throw(AssertionError("Outlook deltas must use path-batch enqueueing")),
    )
    monkeypatch.setattr(
        mail_ingestion.database,
        "enqueue_corpus_sync_path_batch_jobs",
        lambda **kwargs: queued.append(kwargs)
        or {
            "jobs": [{"job_id": f"job-{len(queued)}", "status": "pending", "deduped": False}],
            "count": 1,
            "path_count": len(kwargs["paths"]),
        },
        raising=False,
    )

    result = mail_ingestion.sync_outlook_profile("outlook-catchup")

    assert result["spool_sync"]["sync_mode"] == "background_path_jobs"
    assert result["spool_sync"]["profiles"][0]["path_count"] == 2
    assert queued == [
        {
            "root_name": "mail-outlook-catchup",
            "reason": "outlook_spool_sync",
            "paths": [
                "/app/private/mail-spool/outlook-catchup/ready/export-1",
                "/app/private/mail-spool/outlook-catchup/ready/export-2",
            ],
            "payload": {
                "profile_name": "outlook-catchup",
                "source_type": "outlook_com",
            },
        }
    ]


def test_sync_outlook_profile_skips_spool_job_when_no_messages_changed(monkeypatch):
    from flux_llm_kb import mail_ingestion

    monkeypatch.setattr(
        mail_ingestion.database,
        "list_mail_profiles",
        lambda name=None: [
            {
                "name": name or "outlook-catchup",
                "source_type": "outlook_com",
                "enabled": True,
            }
        ],
    )
    monkeypatch.setattr(
        mail_ingestion,
        "_sync_outlook_profile",
        lambda _profile: {
            "profile": "outlook-catchup",
            "status": "completed",
            "exported": 0,
            "seen": 10,
            "errors": [],
            "spool_paths": [],
        },
    )
    monkeypatch.setattr(
        mail_ingestion.database,
        "enqueue_corpus_sync_job",
        lambda **_kwargs: (_ for _ in ()).throw(AssertionError("unchanged Outlook runs must not queue a full-root sync")),
    )

    result = mail_ingestion.sync_outlook_profile("outlook-catchup")

    assert result["spool_sync"] == {
        "profiles": [
            {
                "profile": "outlook-catchup",
                "root_name": "mail-outlook-catchup",
                "status": "skipped_no_spool_changes",
                "path_count": 0,
            }
        ],
        "count": 1,
        "sync_mode": "skipped_no_spool_changes",
    }


def test_sync_outlook_profile_stops_after_item_export_error(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    class FakeItem:
        Class = 43
        StoreID = "store-1"

        def __init__(self, entry_id: str):
            self.EntryID = entry_id

    class FakeFolder:
        def __init__(self, name: str, *, items=None, folders=None):
            self.Name = name
            self.Items = items or []
            self.Folders = folders or []

    moh = FakeFolder("MOHESR", items=[FakeItem("bad-1"), FakeItem("good-1")])
    namespace = SimpleNamespace(Folders=[FakeFolder("Mailbox - Me", folders=[FakeFolder("Inbox", folders=[moh])])])
    fake_client = SimpleNamespace(Dispatch=lambda _name: SimpleNamespace(GetNamespace=lambda _namespace: namespace))
    monkeypatch.setitem(sys.modules, "win32com", SimpleNamespace(client=fake_client))
    monkeypatch.setitem(sys.modules, "win32com.client", fake_client)

    def fake_export(*, item, **_kwargs):
        if item.EntryID == "bad-1":
            raise RuntimeError("cannot save item")
        return SimpleNamespace(export_id=f"export-{item.EntryID}", manifest={"content_hash": f"hash-{item.EntryID}", "message_id": None})

    runs = []
    monkeypatch.setattr(mail_ingestion, "export_outlook_item_to_spool", fake_export)
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "record_mail_sync_run", lambda **kwargs: runs.append(kwargs) or kwargs)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: kwargs)

    result = mail_ingestion._sync_outlook_profile(
        {
            "name": "outlook-catchup",
            "source_type": "outlook_com",
            "folder_paths": ["Mailbox - Me\\Inbox\\MOHESR"],
            "spool_path": str(tmp_path),
            "metadata": {},
            "max_messages_per_run": 200,
        }
    )

    assert result["status"] == "partial"
    assert result["exported"] == 0
    assert "cannot save item" in result["errors"][0]["error"]
    assert runs[0]["messages_exported"] == 0


def test_sync_outlook_profile_first_run_writes_received_time_cursor(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    class FakeItem:
        Class = 43
        StoreID = "store-1"

        def __init__(self, entry_id: str, received_time: datetime):
            self.EntryID = entry_id
            self.ReceivedTime = received_time
            self.LastModificationTime = received_time

    class FakeItems(list):
        def __init__(self, items):
            super().__init__(items)
            self.sorts = []
            self.restricts = []

        def Sort(self, field, descending):
            self.sorts.append((field, descending))

        def Restrict(self, restriction):
            self.restricts.append(restriction)
            return self

    class FakeFolder:
        def __init__(self, name: str, *, items=None, folders=None):
            self.Name = name
            self.Items = items or FakeItems([])
            self.Folders = folders or []

    items = FakeItems(
        [
            FakeItem("newest", datetime(2026, 6, 20, 11, 0, tzinfo=timezone.utc)),
            FakeItem("older", datetime(2026, 6, 20, 10, 0, tzinfo=timezone.utc)),
        ]
    )
    moh = FakeFolder("MOHESR", items=items)
    namespace = SimpleNamespace(Folders=[FakeFolder("Mailbox - Me", folders=[FakeFolder("Inbox", folders=[moh])])])
    fake_client = SimpleNamespace(Dispatch=lambda _name: SimpleNamespace(GetNamespace=lambda _namespace: namespace))
    monkeypatch.setitem(sys.modules, "win32com", SimpleNamespace(client=fake_client))
    monkeypatch.setitem(sys.modules, "win32com.client", fake_client)

    exports = []
    metadata_updates = []
    runs = []

    def fake_export(*, item, **_kwargs):
        exports.append(item.EntryID)
        return SimpleNamespace(export_id=f"export-{item.EntryID}", manifest={"content_hash": f"hash-{item.EntryID}", "message_id": None})

    monkeypatch.setattr(mail_ingestion, "export_outlook_item_to_spool", fake_export)
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "record_mail_sync_run", lambda **kwargs: runs.append(kwargs) or kwargs)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: metadata_updates.append(kwargs) or kwargs)
    monkeypatch.setattr(database, "mail_message_exists", lambda **_kwargs: False, raising=False)

    result = mail_ingestion._sync_outlook_profile(
        {
            "name": "outlook-catchup",
            "source_type": "outlook_com",
            "folder_paths": ["Mailbox - Me\\Inbox\\MOHESR"],
            "spool_path": str(tmp_path),
            "metadata": {},
            "max_messages_per_run": 200,
        }
    )

    assert result["exported"] == 2
    assert exports == ["newest", "older"]
    assert items.sorts == [("[ReceivedTime]", False)]
    assert items.restricts == []
    assert metadata_updates[0]["metadata"]["outlook_cursors"]["Mailbox - Me\\Inbox\\MOHESR"] == {
        "basis": "received_time",
        "property": "ReceivedTime",
        "value": "2026-06-20T11:00:00+00:00",
    }
    assert runs[0]["last_cursor"]["Mailbox - Me\\Inbox\\MOHESR"]["value"] == "2026-06-20T11:00:00+00:00"


def test_sync_outlook_profile_without_cursor_skips_recorded_outlook_entry_ids(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    class FakeItem:
        Class = 43
        StoreID = "store-1"

        def __init__(self, entry_id: str, modified_time: datetime):
            self.EntryID = entry_id
            self.ReceivedTime = modified_time
            self.LastModificationTime = modified_time

    class FakeItems(list):
        def Sort(self, field, descending):
            attr = "LastModificationTime" if "LastModificationTime" in field else "ReceivedTime"
            self.sort(key=lambda item: getattr(item, attr), reverse=descending)

        def Restrict(self, *_args):
            return self

    class FakeFolder:
        def __init__(self, name: str, *, items=None, folders=None):
            self.Name = name
            self.Items = items or FakeItems([])
            self.Folders = folders or []

    items = FakeItems(
        [
            FakeItem("already-exported", datetime(2026, 6, 20, 10, 0, tzinfo=timezone.utc)),
            FakeItem("fresh", datetime(2026, 6, 20, 11, 0, tzinfo=timezone.utc)),
        ]
    )
    moh = FakeFolder("MOHESR", items=items)
    namespace = SimpleNamespace(Folders=[FakeFolder("Mailbox - Me", folders=[FakeFolder("Inbox", folders=[moh])])])
    fake_client = SimpleNamespace(Dispatch=lambda _name: SimpleNamespace(GetNamespace=lambda _namespace: namespace))
    monkeypatch.setitem(sys.modules, "win32com", SimpleNamespace(client=fake_client))
    monkeypatch.setitem(sys.modules, "win32com.client", fake_client)

    exports = []
    monkeypatch.setattr(
        mail_ingestion,
        "export_outlook_item_to_spool",
        lambda *, item, **_kwargs: exports.append(item.EntryID)
        or SimpleNamespace(export_id=f"export-{item.EntryID}", manifest={"content_hash": f"hash-{item.EntryID}", "message_id": None}),
    )
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "record_mail_sync_run", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: kwargs)
    monkeypatch.setattr(
        database,
        "mail_message_exists",
        lambda **kwargs: kwargs["source_message_id"] == "outlook:already-exported",
        raising=False,
    )

    result = mail_ingestion._sync_outlook_profile(
        {
            "name": "outlook-catchup",
            "source_type": "outlook_com",
            "folder_paths": ["Mailbox - Me\\Inbox\\MOHESR"],
            "spool_path": str(tmp_path),
            "metadata": {"outlook_incremental_basis": "last_modification_time"},
            "max_messages_per_run": 99999,
        }
    )

    assert result["exported"] == 1
    assert exports == ["fresh"]


def test_sync_outlook_profile_uses_ascending_order_and_checkpoints_safe_cursors(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    class FakeItem:
        Class = 43
        StoreID = "store-1"

        def __init__(self, entry_id: str, modified_time: datetime):
            self.EntryID = entry_id
            self.ReceivedTime = modified_time
            self.LastModificationTime = modified_time

    class FakeItems(list):
        def __init__(self, items):
            super().__init__(items)
            self.sorts = []

        def Sort(self, field, descending):
            self.sorts.append((field, descending))
            attr = "LastModificationTime" if "LastModificationTime" in field else "ReceivedTime"
            self.sort(key=lambda item: getattr(item, attr), reverse=descending)

        def Restrict(self, *_args):
            return self

    class FakeFolder:
        def __init__(self, name: str, *, items=None, folders=None):
            self.Name = name
            self.Items = items or FakeItems([])
            self.Folders = folders or []

    items = FakeItems(
        [
            FakeItem("latest", datetime(2026, 6, 20, 12, 0, tzinfo=timezone.utc)),
            FakeItem("oldest", datetime(2026, 6, 20, 10, 0, tzinfo=timezone.utc)),
            FakeItem("middle", datetime(2026, 6, 20, 11, 0, tzinfo=timezone.utc)),
        ]
    )
    moh = FakeFolder("MOHESR", items=items)
    namespace = SimpleNamespace(Folders=[FakeFolder("Mailbox - Me", folders=[FakeFolder("Inbox", folders=[moh])])])
    fake_client = SimpleNamespace(Dispatch=lambda _name: SimpleNamespace(GetNamespace=lambda _namespace: namespace))
    monkeypatch.setitem(sys.modules, "win32com", SimpleNamespace(client=fake_client))
    monkeypatch.setitem(sys.modules, "win32com.client", fake_client)

    exports = []
    metadata_updates = []
    monkeypatch.setattr(
        mail_ingestion,
        "export_outlook_item_to_spool",
        lambda *, item, **_kwargs: exports.append(item.EntryID)
        or SimpleNamespace(export_id=f"export-{item.EntryID}", manifest={"content_hash": f"hash-{item.EntryID}", "message_id": None}),
    )
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "record_mail_sync_run", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: metadata_updates.append(kwargs["metadata"]) or kwargs)
    monkeypatch.setattr(database, "mail_message_exists", lambda **_kwargs: False, raising=False)

    mail_ingestion._sync_outlook_profile(
        {
            "name": "outlook-catchup",
            "source_type": "outlook_com",
            "folder_paths": ["Mailbox - Me\\Inbox\\MOHESR"],
            "spool_path": str(tmp_path),
            "metadata": {"outlook_incremental_basis": "last_modification_time"},
            "max_messages_per_run": 99999,
        }
    )

    cursor_values = [
        update["outlook_cursors"]["Mailbox - Me\\Inbox\\MOHESR"]["value"]
        for update in metadata_updates
    ]
    assert items.sorts == [("[LastModificationTime]", False)]
    assert exports == ["oldest", "middle", "latest"]
    assert cursor_values == [
        "2026-06-20T10:00:00+00:00",
        "2026-06-20T11:00:00+00:00",
        "2026-06-20T12:00:00+00:00",
    ]


def test_sync_outlook_profile_stops_folder_after_failed_item_and_preserves_safe_cursor(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    class FakeItem:
        Class = 43
        StoreID = "store-1"

        def __init__(self, entry_id: str, modified_time: datetime):
            self.EntryID = entry_id
            self.ReceivedTime = modified_time
            self.LastModificationTime = modified_time

    class FakeItems(list):
        def Sort(self, field, descending):
            attr = "LastModificationTime" if "LastModificationTime" in field else "ReceivedTime"
            self.sort(key=lambda item: getattr(item, attr), reverse=descending)

        def Restrict(self, *_args):
            return self

    class FakeFolder:
        def __init__(self, name: str, *, items=None, folders=None):
            self.Name = name
            self.Items = items or FakeItems([])
            self.Folders = folders or []

    items = FakeItems(
        [
            FakeItem("later", datetime(2026, 6, 20, 12, 0, tzinfo=timezone.utc)),
            FakeItem("bad", datetime(2026, 6, 20, 11, 0, tzinfo=timezone.utc)),
            FakeItem("good", datetime(2026, 6, 20, 10, 0, tzinfo=timezone.utc)),
        ]
    )
    moh = FakeFolder("MOHESR", items=items)
    namespace = SimpleNamespace(Folders=[FakeFolder("Mailbox - Me", folders=[FakeFolder("Inbox", folders=[moh])])])
    fake_client = SimpleNamespace(Dispatch=lambda _name: SimpleNamespace(GetNamespace=lambda _namespace: namespace))
    monkeypatch.setitem(sys.modules, "win32com", SimpleNamespace(client=fake_client))
    monkeypatch.setitem(sys.modules, "win32com.client", fake_client)

    exports = []
    runs = []
    metadata_updates = []

    def fake_export(*, item, **_kwargs):
        exports.append(item.EntryID)
        if item.EntryID == "bad":
            raise RuntimeError("cannot save item")
        return SimpleNamespace(export_id=f"export-{item.EntryID}", manifest={"content_hash": f"hash-{item.EntryID}", "message_id": None})

    monkeypatch.setattr(mail_ingestion, "export_outlook_item_to_spool", fake_export)
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "record_mail_sync_run", lambda **kwargs: runs.append(kwargs) or kwargs)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: metadata_updates.append(kwargs["metadata"]) or kwargs)
    monkeypatch.setattr(database, "mail_message_exists", lambda **_kwargs: False, raising=False)

    result = mail_ingestion._sync_outlook_profile(
        {
            "name": "outlook-catchup",
            "source_type": "outlook_com",
            "folder_paths": ["Mailbox - Me\\Inbox\\MOHESR"],
            "spool_path": str(tmp_path),
            "metadata": {"outlook_incremental_basis": "last_modification_time"},
            "max_messages_per_run": 99999,
        }
    )

    assert result["status"] == "partial"
    assert exports == ["good", "bad"]
    assert metadata_updates[-1]["outlook_cursors"]["Mailbox - Me\\Inbox\\MOHESR"]["value"] == "2026-06-20T10:00:00+00:00"
    assert runs[0]["last_cursor"]["Mailbox - Me\\Inbox\\MOHESR"]["value"] == "2026-06-20T10:00:00+00:00"


def test_sync_outlook_profile_restricts_received_time_from_existing_cursor(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    class FakeItem:
        Class = 43
        StoreID = "store-1"

        def __init__(self, entry_id: str, received_time: datetime):
            self.EntryID = entry_id
            self.ReceivedTime = received_time
            self.LastModificationTime = received_time

    class FakeItems(list):
        def __init__(self, items):
            super().__init__(items)
            self.sorts = []
            self.restricts = []

        def Sort(self, field, descending):
            self.sorts.append((field, descending))

        def Restrict(self, restriction):
            self.restricts.append(restriction)
            return FakeItems([item for item in self if item.EntryID == "new"])

    class FakeFolder:
        def __init__(self, name: str, *, items=None, folders=None):
            self.Name = name
            self.Items = items or FakeItems([])
            self.Folders = folders or []

    items = FakeItems(
        [
            FakeItem("new", datetime(2026, 6, 20, 11, 30, tzinfo=timezone.utc)),
            FakeItem("old", datetime(2026, 6, 20, 9, 0, tzinfo=timezone.utc)),
        ]
    )
    moh = FakeFolder("MOHESR", items=items)
    namespace = SimpleNamespace(Folders=[FakeFolder("Mailbox - Me", folders=[FakeFolder("Inbox", folders=[moh])])])
    fake_client = SimpleNamespace(Dispatch=lambda _name: SimpleNamespace(GetNamespace=lambda _namespace: namespace))
    monkeypatch.setitem(sys.modules, "win32com", SimpleNamespace(client=fake_client))
    monkeypatch.setitem(sys.modules, "win32com.client", fake_client)

    exports = []
    monkeypatch.setattr(
        mail_ingestion,
        "export_outlook_item_to_spool",
        lambda *, item, **_kwargs: exports.append(item.EntryID)
        or SimpleNamespace(export_id=f"export-{item.EntryID}", manifest={"content_hash": f"hash-{item.EntryID}", "message_id": None}),
    )
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "record_mail_sync_run", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "mail_message_exists", lambda **_kwargs: False, raising=False)

    result = mail_ingestion._sync_outlook_profile(
        {
            "name": "outlook-catchup",
            "source_type": "outlook_com",
            "folder_paths": ["Mailbox - Me\\Inbox\\MOHESR"],
            "spool_path": str(tmp_path),
            "metadata": {
                "outlook_cursors": {
                    "Mailbox - Me\\Inbox\\MOHESR": {
                        "basis": "received_time",
                        "property": "ReceivedTime",
                        "value": "2026-06-20T10:00:00+00:00",
                    }
                }
            },
            "max_messages_per_run": 200,
        }
    )

    assert result["exported"] == 1
    assert exports == ["new"]
    assert items.sorts == [("[ReceivedTime]", False)]
    assert items.restricts == ["[ReceivedTime] >= '06/20/2026 09:45 AM'"]


def test_sync_outlook_profile_can_use_last_modification_time_for_moved_mail(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    class FakeItem:
        Class = 43
        StoreID = "store-1"
        ReceivedTime = datetime(2026, 1, 15, 9, 0, tzinfo=timezone.utc)

        def __init__(self, entry_id: str, modified_time: datetime):
            self.EntryID = entry_id
            self.LastModificationTime = modified_time

    class FakeItems(list):
        def __init__(self, items):
            super().__init__(items)
            self.sorts = []
            self.restricts = []

        def Sort(self, field, descending):
            self.sorts.append((field, descending))

        def Restrict(self, restriction):
            self.restricts.append(restriction)
            return FakeItems([item for item in self if item.EntryID == "moved-old"])

    class FakeFolder:
        def __init__(self, name: str, *, items=None, folders=None):
            self.Name = name
            self.Items = items or FakeItems([])
            self.Folders = folders or []

    items = FakeItems([FakeItem("moved-old", datetime(2026, 6, 20, 12, 0, tzinfo=timezone.utc))])
    moh = FakeFolder("MOHESR", items=items)
    namespace = SimpleNamespace(Folders=[FakeFolder("Mailbox - Me", folders=[FakeFolder("Inbox", folders=[moh])])])
    fake_client = SimpleNamespace(Dispatch=lambda _name: SimpleNamespace(GetNamespace=lambda _namespace: namespace))
    monkeypatch.setitem(sys.modules, "win32com", SimpleNamespace(client=fake_client))
    monkeypatch.setitem(sys.modules, "win32com.client", fake_client)

    exports = []
    runs = []
    monkeypatch.setattr(
        mail_ingestion,
        "export_outlook_item_to_spool",
        lambda *, item, **_kwargs: exports.append(item.EntryID)
        or SimpleNamespace(export_id=f"export-{item.EntryID}", manifest={"content_hash": f"hash-{item.EntryID}", "message_id": None}),
    )
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "record_mail_sync_run", lambda **kwargs: runs.append(kwargs) or kwargs)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "mail_message_exists", lambda **_kwargs: False, raising=False)

    mail_ingestion._sync_outlook_profile(
        {
            "name": "outlook-catchup",
            "source_type": "outlook_com",
            "folder_paths": ["Mailbox - Me\\Inbox\\MOHESR"],
            "spool_path": str(tmp_path),
            "metadata": {
                "outlook_incremental_basis": "last_modification_time",
                "outlook_cursors": {
                    "Mailbox - Me\\Inbox\\MOHESR": {
                        "basis": "last_modification_time",
                        "property": "LastModificationTime",
                        "value": "2026-06-20T10:00:00+00:00",
                    }
                },
            },
            "max_messages_per_run": 200,
        }
    )

    assert exports == ["moved-old"]
    assert items.sorts == [("[LastModificationTime]", False)]
    assert items.restricts == ["[LastModificationTime] >= '06/20/2026 09:45 AM'"]
    assert runs[0]["last_cursor"]["Mailbox - Me\\Inbox\\MOHESR"]["property"] == "LastModificationTime"


def test_sync_outlook_profile_skips_overlap_duplicates_before_export(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    class FakeItem:
        Class = 43
        StoreID = "store-1"

        def __init__(self, entry_id: str, received_time: datetime):
            self.EntryID = entry_id
            self.ReceivedTime = received_time
            self.LastModificationTime = received_time

    class FakeItems(list):
        def Sort(self, field, descending):
            attr = "LastModificationTime" if "LastModificationTime" in field else "ReceivedTime"
            self.sort(key=lambda item: getattr(item, attr), reverse=descending)

        def Restrict(self, *_args):
            return self

    class FakeFolder:
        def __init__(self, name: str, *, items=None, folders=None):
            self.Name = name
            self.Items = items or FakeItems()
            self.Folders = folders or []

    items = FakeItems(
        [
            FakeItem("duplicate", datetime(2026, 6, 20, 10, 5, tzinfo=timezone.utc)),
            FakeItem("fresh", datetime(2026, 6, 20, 10, 30, tzinfo=timezone.utc)),
        ]
    )
    moh = FakeFolder("MOHESR", items=items)
    namespace = SimpleNamespace(Folders=[FakeFolder("Mailbox - Me", folders=[FakeFolder("Inbox", folders=[moh])])])
    fake_client = SimpleNamespace(Dispatch=lambda _name: SimpleNamespace(GetNamespace=lambda _namespace: namespace))
    monkeypatch.setitem(sys.modules, "win32com", SimpleNamespace(client=fake_client))
    monkeypatch.setitem(sys.modules, "win32com.client", fake_client)

    exports = []
    monkeypatch.setattr(
        mail_ingestion,
        "export_outlook_item_to_spool",
        lambda *, item, **_kwargs: exports.append(item.EntryID)
        or SimpleNamespace(export_id=f"export-{item.EntryID}", manifest={"content_hash": f"hash-{item.EntryID}", "message_id": None}),
    )
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "record_mail_sync_run", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: kwargs)
    monkeypatch.setattr(
        database,
        "mail_message_exists",
        lambda **kwargs: kwargs["source_message_id"] == "outlook:duplicate",
        raising=False,
    )

    result = mail_ingestion._sync_outlook_profile(
        {
            "name": "outlook-catchup",
            "source_type": "outlook_com",
            "folder_paths": ["Mailbox - Me\\Inbox\\MOHESR"],
            "spool_path": str(tmp_path),
            "metadata": {
                "outlook_cursors": {
                    "Mailbox - Me\\Inbox\\MOHESR": {
                        "basis": "received_time",
                        "property": "ReceivedTime",
                        "value": "2026-06-20T10:10:00+00:00",
                    }
                }
            },
            "max_messages_per_run": 200,
        }
    )

    assert result["exported"] == 1
    assert exports == ["fresh"]


def test_sync_outlook_profile_does_not_advance_cursor_past_failed_item(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    class FakeItem:
        Class = 43
        StoreID = "store-1"

        def __init__(self, entry_id: str, received_time: datetime):
            self.EntryID = entry_id
            self.ReceivedTime = received_time
            self.LastModificationTime = received_time

    class FakeItems(list):
        def Sort(self, field, descending):
            attr = "LastModificationTime" if "LastModificationTime" in field else "ReceivedTime"
            self.sort(key=lambda item: getattr(item, attr), reverse=descending)

        def Restrict(self, *_args):
            return self

    class FakeFolder:
        def __init__(self, name: str, *, items=None, folders=None):
            self.Name = name
            self.Items = items or FakeItems()
            self.Folders = folders or []

    items = FakeItems(
        [
            FakeItem("bad", datetime(2026, 6, 20, 11, 0, tzinfo=timezone.utc)),
            FakeItem("good", datetime(2026, 6, 20, 10, 30, tzinfo=timezone.utc)),
        ]
    )
    moh = FakeFolder("MOHESR", items=items)
    namespace = SimpleNamespace(Folders=[FakeFolder("Mailbox - Me", folders=[FakeFolder("Inbox", folders=[moh])])])
    fake_client = SimpleNamespace(Dispatch=lambda _name: SimpleNamespace(GetNamespace=lambda _namespace: namespace))
    monkeypatch.setitem(sys.modules, "win32com", SimpleNamespace(client=fake_client))
    monkeypatch.setitem(sys.modules, "win32com.client", fake_client)

    runs = []

    def fake_export(*, item, **_kwargs):
        if item.EntryID == "bad":
            raise RuntimeError("cannot save item")
        return SimpleNamespace(export_id=f"export-{item.EntryID}", manifest={"content_hash": f"hash-{item.EntryID}", "message_id": None})

    monkeypatch.setattr(mail_ingestion, "export_outlook_item_to_spool", fake_export)
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "record_mail_sync_run", lambda **kwargs: runs.append(kwargs) or kwargs)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "mail_message_exists", lambda **_kwargs: False, raising=False)

    result = mail_ingestion._sync_outlook_profile(
        {
            "name": "outlook-catchup",
            "source_type": "outlook_com",
            "folder_paths": ["Mailbox - Me\\Inbox\\MOHESR"],
            "spool_path": str(tmp_path),
            "metadata": {
                "outlook_cursors": {
                    "Mailbox - Me\\Inbox\\MOHESR": {
                        "basis": "received_time",
                        "property": "ReceivedTime",
                        "value": "2026-06-20T10:00:00+00:00",
                    }
                }
            },
            "max_messages_per_run": 200,
        }
    )

    assert result["status"] == "partial"
    assert runs[0]["last_cursor"]["Mailbox - Me\\Inbox\\MOHESR"]["value"] == "2026-06-20T10:30:00+00:00"


def test_sync_outlook_profile_tracks_recursive_folder_cursors_independently(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion

    class FakeItem:
        Class = 43
        StoreID = "store-1"

        def __init__(self, entry_id: str, received_time: datetime):
            self.EntryID = entry_id
            self.ReceivedTime = received_time
            self.LastModificationTime = received_time

    class FakeItems(list):
        def Sort(self, *_args):
            return None

        def Restrict(self, *_args):
            return self

    class FakeFolder:
        def __init__(self, name: str, *, items=None, folders=None):
            self.Name = name
            self.Items = items or FakeItems()
            self.Folders = folders or []

    moh = FakeFolder(
        "MOHESR",
        items=FakeItems([FakeItem("parent", datetime(2026, 6, 20, 11, 0, tzinfo=timezone.utc))]),
        folders=[
            FakeFolder("PM", items=FakeItems([FakeItem("pm", datetime(2026, 6, 20, 12, 0, tzinfo=timezone.utc))])),
        ],
    )
    namespace = SimpleNamespace(Folders=[FakeFolder("Mailbox - Me", folders=[FakeFolder("Inbox", folders=[moh])])])
    fake_client = SimpleNamespace(Dispatch=lambda _name: SimpleNamespace(GetNamespace=lambda _namespace: namespace))
    monkeypatch.setitem(sys.modules, "win32com", SimpleNamespace(client=fake_client))
    monkeypatch.setitem(sys.modules, "win32com.client", fake_client)

    runs = []
    monkeypatch.setattr(
        mail_ingestion,
        "export_outlook_item_to_spool",
        lambda *, item, **_kwargs: SimpleNamespace(export_id=f"export-{item.EntryID}", manifest={"content_hash": f"hash-{item.EntryID}", "message_id": None}),
    )
    monkeypatch.setattr(database, "record_mail_message", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "record_mail_sync_run", lambda **kwargs: runs.append(kwargs) or kwargs)
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: kwargs)
    monkeypatch.setattr(database, "mail_message_exists", lambda **_kwargs: False, raising=False)

    mail_ingestion._sync_outlook_profile(
        {
            "name": "outlook-catchup",
            "source_type": "outlook_com",
            "folder_paths": ["Mailbox - Me\\Inbox\\MOHESR"],
            "spool_path": str(tmp_path),
            "metadata": {},
            "max_messages_per_run": 200,
        }
    )

    assert runs[0]["last_cursor"]["Mailbox - Me\\Inbox\\MOHESR"]["value"] == "2026-06-20T11:00:00+00:00"
    assert runs[0]["last_cursor"]["Mailbox - Me\\Inbox\\MOHESR\\PM"]["value"] == "2026-06-20T12:00:00+00:00"


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


def test_export_outlook_item_to_spool_writes_rich_outlook_artifacts(tmp_path):
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

    parsed = parse_email_bytes((result.ready_path / "message.eml").read_bytes())
    assert parsed.sender == "sender@example.com"
    assert parsed.recipients == ("me@example.com",)
    assert parsed.text_body == "Outlook body text"
    assert (result.ready_path / "message.msg").read_bytes() == b"msg"
    assert (result.ready_path / "attachments" / "notes.txt").read_text(encoding="utf-8") == "attachment"
    assert result.manifest["source_type"] == "outlook_com"
    assert result.manifest["outlook_entry_id"] == "entry-1"
    assert result.manifest["outlook_export_mode"] == "rich"
    assert result.manifest["outlook_attachment_count"] == 1


def test_outlook_item_to_email_is_deterministic_for_same_html_item():
    from flux_llm_kb import mail_ingestion

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

    first = mail_ingestion._outlook_item_to_email(FakeItem())
    second = mail_ingestion._outlook_item_to_email(FakeItem())

    assert first == second


def test_export_outlook_item_to_spool_reuses_ready_folder_for_same_html_item(tmp_path):
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

        def SaveAs(self, path, *_args):
            Path(path).write_bytes(b"msg")

    first = export_outlook_item_to_spool(
        item=FakeItem(),
        spool_path=tmp_path,
        profile_name="outlook-catchup",
        folder_path="Mailbox/Inbox/Flux",
    )
    second = export_outlook_item_to_spool(
        item=FakeItem(),
        spool_path=tmp_path,
        profile_name="outlook-catchup",
        folder_path="Mailbox/Inbox/Flux",
    )

    assert second.export_id == first.export_id
    assert second.ready_path == first.ready_path
    assert [path.name for path in (tmp_path / "ready").iterdir()] == [first.export_id]


def _write_outlook_spool_export(
    spool_path: Path,
    export_id: str,
    *,
    source_message_id: str = "entry:entry-1",
    body_text: str = "Outlook body text",
    attachment_payloads: tuple[bytes, ...] = (b"attachment",),
    exported_at_epoch: int = 1,
) -> Path:
    ready_path = spool_path / "ready" / export_id
    attachments_path = ready_path / "attachments"
    attachments_path.mkdir(parents=True)
    (ready_path / "body.txt").write_text(body_text, encoding="utf-8")
    (ready_path / "body.html").write_text(f"<p>{body_text}</p>", encoding="utf-8")
    (ready_path / "message.eml").write_text(
        f"Subject: Outlook catch-up\n\n{body_text}\n",
        encoding="utf-8",
    )
    (ready_path / "message.msg").write_bytes(f"msg-{export_id}".encode("utf-8"))
    for index, payload in enumerate(attachment_payloads, start=1):
        (attachments_path / f"attachment-{index}.bin").write_bytes(payload)
    manifest = {
        "export_id": export_id,
        "profile_name": "outlook-catchup",
        "source_type": "outlook_com",
        "source_folder": "Mailbox/Inbox/Flux",
        "source_message_id": source_message_id,
        "subject": "Outlook catch-up",
        "sender": "sender@example.com",
        "recipients": ["me@example.com"],
        "message_id": "<outlook-1@example.com>",
        "received_at": "2026-06-20 10:00:00",
        "attachment_count": 0,
        "content_hash": f"hash-{export_id}",
        "exported_at_epoch": exported_at_epoch,
        "outlook_entry_id": source_message_id.removeprefix("entry:"),
        "outlook_attachment_count": len(attachment_payloads),
    }
    (ready_path / "manifest.json").write_text(json.dumps(manifest, indent=2, sort_keys=True), encoding="utf-8")
    return ready_path


def test_dedupe_outlook_spool_reports_safe_duplicates_without_mutating(tmp_path):
    from flux_llm_kb import mail_ingestion

    _write_outlook_spool_export(tmp_path, "older", exported_at_epoch=1)
    _write_outlook_spool_export(tmp_path, "newer", exported_at_epoch=2)

    payload = mail_ingestion.dedupe_outlook_spool(tmp_path)

    assert payload["settings_mutated"] is False
    assert payload["applied"] is False
    assert payload["duplicate_group_count"] == 1
    assert payload["duplicate_export_count"] == 1
    assert payload["kept_export_ids"] == ["newer"]
    assert payload["candidate_duplicate_export_ids"] == ["older"]
    assert payload["reclaimable_bytes"] > 0
    assert (tmp_path / "ready" / "older").exists()
    assert (tmp_path / "ready" / "newer").exists()


def test_dedupe_outlook_spool_purges_only_safe_duplicates(tmp_path):
    from flux_llm_kb import mail_ingestion

    _write_outlook_spool_export(tmp_path, "older", exported_at_epoch=1)
    _write_outlook_spool_export(tmp_path, "newer", exported_at_epoch=2)

    payload = mail_ingestion.dedupe_outlook_spool(tmp_path, apply=True, purge=True)

    assert payload["applied"] is True
    assert payload["purged_export_ids"] == ["older"]
    assert payload["kept_export_ids"] == ["newer"]
    assert not (tmp_path / "ready" / "older").exists()
    assert (tmp_path / "ready" / "newer").exists()


def test_dedupe_outlook_spool_skips_same_source_with_changed_body(tmp_path):
    from flux_llm_kb import mail_ingestion

    _write_outlook_spool_export(tmp_path, "older", body_text="Original body", exported_at_epoch=1)
    _write_outlook_spool_export(tmp_path, "newer", body_text="Changed body", exported_at_epoch=2)

    payload = mail_ingestion.dedupe_outlook_spool(tmp_path, apply=True, purge=True)

    assert payload["duplicate_group_count"] == 0
    assert payload["skipped_group_count"] == 1
    assert payload["purged_export_ids"] == []
    assert (tmp_path / "ready" / "older").exists()
    assert (tmp_path / "ready" / "newer").exists()


def test_dedupe_outlook_spool_skips_same_source_with_changed_attachments(tmp_path):
    from flux_llm_kb import mail_ingestion

    _write_outlook_spool_export(tmp_path, "older", attachment_payloads=(b"one",), exported_at_epoch=1)
    _write_outlook_spool_export(tmp_path, "newer", attachment_payloads=(b"two",), exported_at_epoch=2)

    payload = mail_ingestion.dedupe_outlook_spool(tmp_path, apply=True, purge=True)

    assert payload["duplicate_group_count"] == 0
    assert payload["skipped_group_count"] == 1
    assert payload["purged_export_ids"] == []
    assert (tmp_path / "ready" / "older").exists()
    assert (tmp_path / "ready" / "newer").exists()


def test_sync_mail_spool_for_profile_queues_outlook_spool_sync_job(monkeypatch):
    from flux_llm_kb import mail_ingestion

    profile = {"name": "outlook-catchup", "source_type": "outlook_com"}
    queued = []
    monkeypatch.setattr(
        mail_ingestion,
        "sync_mail_spool",
        lambda profile_name=None: (_ for _ in ()).throw(AssertionError("Outlook spool sync must be queued, not run inline")),
    )
    monkeypatch.setattr(
        mail_ingestion,
        "_sync_mail_spool_via_api",
        lambda root_name: (_ for _ in ()).throw(AssertionError("Outlook spool sync must not wait on HTTP")),
    )
    monkeypatch.setattr(
        mail_ingestion.database,
        "enqueue_corpus_sync_job",
        lambda **kwargs: queued.append(kwargs) or {"job_id": "job-1", "status": "pending", "deduped": False},
    )

    result = mail_ingestion._sync_mail_spool_for_profile(profile)

    assert result["sync_mode"] == "background_job"
    assert result["profiles"][0]["status"] == "queued_background_sync"
    assert result["profiles"][0]["profile"] == "outlook-catchup"
    assert result["profiles"][0]["root_name"] == "mail-outlook-catchup"
    assert result["profiles"][0]["job_id"] == "job-1"
    assert result["profiles"][0]["job_status"] == "pending"
    assert queued == [
        {
            "root_name": "mail-outlook-catchup",
            "reason": "outlook_spool_sync",
            "payload": {"profile_name": "outlook-catchup", "source_type": "outlook_com"},
        }
    ]


def test_sync_mail_spool_via_api_posts_root_sync(monkeypatch):
    from flux_llm_kb import mail_ingestion

    calls = []

    class FakeResponse:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def read(self):
            return json.dumps({"files_seen": 16, "jobs_queued": 16}).encode("utf-8")

    def fake_urlopen(request, timeout):
        calls.append((request, timeout))
        return FakeResponse()

    monkeypatch.setattr(mail_ingestion.urllib.request, "urlopen", fake_urlopen)

    result = mail_ingestion._sync_mail_spool_via_api("mail-outlook-catchup", api_url="http://127.0.0.1:8765/")

    request, timeout = calls[0]
    assert result == {"files_seen": 16, "jobs_queued": 16}
    assert request.full_url == "http://127.0.0.1:8765/api/crawl/sync"
    assert request.get_method() == "POST"
    assert timeout == 60
    assert json.loads(request.data.decode("utf-8")) == {
        "root_name": "mail-outlook-catchup",
        "dry_run": False,
    }


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
