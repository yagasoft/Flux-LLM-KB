from flux_llm_kb import database
from flux_llm_kb import result_details


def test_sanitize_mail_html_removes_active_content():
    html = """
    <div onclick="alert(1)">
      <script>alert("x")</script>
      <img src="https://tracking.example/pixel.png">
      <a href="javascript:alert(1)" style="color:red">bad</a>
      <a href="https://example.com/rfp">good</a>
      <p>Readable <strong>body</strong></p>
    </div>
    """

    sanitized = result_details.sanitize_mail_html(html)

    assert "script" not in sanitized.lower()
    assert "onclick" not in sanitized.lower()
    assert "javascript:" not in sanitized.lower()
    assert "<img" not in sanitized.lower()
    assert "style=" not in sanitized.lower()
    assert 'href="https://example.com/rfp"' in sanitized
    assert "Readable" in sanitized
    assert "<strong>body</strong>" in sanitized


def test_file_chunk_detail_exposes_preview_and_actions(monkeypatch):
    monkeypatch.setattr(
        database,
        "get_asset_chunk_detail",
        lambda chunk_id: {
            "id": chunk_id,
            "asset_id": "asset-1",
            "chunk_index": 0,
            "title": "Project Plan",
            "body": "The extracted project plan body.",
            "asset_path": "plans/project-plan.md",
            "root_name": "docs",
        },
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "get_source_asset_detail",
        lambda asset_id: {
            "id": asset_id,
            "path": "plans/project-plan.md",
            "root_path": "E:/Flux Docs",
            "root_name": "docs",
            "file_kind": "text",
            "mime_type": "text/markdown",
            "extension": ".md",
            "size_bytes": 42,
            "status": "indexed",
            "deleted_at": None,
            "metadata": {},
            "chunks": [
                {
                    "id": "chunk-1",
                    "chunk_index": 0,
                    "title": "Project Plan",
                    "body": "The extracted project plan body.",
                }
            ],
        },
        raising=False,
    )
    monkeypatch.setattr(database, "list_related_source_assets", lambda **_kwargs: [], raising=False)

    detail = result_details.result_detail("corpus_chunk", "chunk-1")

    assert detail["logical_kind"] == "file"
    assert detail["asset_id"] == "asset-1"
    assert detail["preview"]["text"] == "The extracted project plan body."
    assert detail["actions"]["copy_path"]["path"].endswith("plans/project-plan.md")
    assert detail["actions"]["open"]["available"] is True
    assert detail["actions"]["reveal"]["available"] is True


def test_mail_chunk_detail_groups_spool_files_and_sanitizes_body(monkeypatch):
    assets = [
        {
            "id": "asset-manifest",
            "path": "export-1/manifest.json",
            "root_path": "E:/FluxMail/ready",
            "root_name": "mail-gmail",
            "file_kind": "text",
            "mime_type": "application/json",
            "extension": ".json",
            "size_bytes": 100,
            "status": "indexed",
            "deleted_at": None,
            "metadata": {},
            "chunks": [
                {
                    "id": "chunk-manifest",
                    "chunk_index": 0,
                    "title": "manifest.json",
                    "body": '{"export_id":"export-1","profile_name":"gmail","source_folder":"FluxCapture","subject":"Customer RFP","sender":"Sender <sender@example.com>","recipients":["me@example.com"],"received_at":"Tue, 23 Jun 2026 10:00:00 +0000","attachment_count":1}',
                }
            ],
        },
        {
            "id": "asset-html",
            "path": "export-1/body.html",
            "root_path": "E:/FluxMail/ready",
            "root_name": "mail-gmail",
            "file_kind": "code",
            "mime_type": "text/html",
            "extension": ".html",
            "size_bytes": 40,
            "status": "indexed",
            "deleted_at": None,
            "metadata": {},
            "chunks": [
                {
                    "id": "chunk-html",
                    "chunk_index": 0,
                    "title": "body.html",
                    "body": '<p onclick="bad()">Please review</p><script>alert(1)</script>',
                }
            ],
        },
        {
            "id": "asset-attachment",
            "path": "export-1/attachments/rfp.pdf",
            "root_path": "E:/FluxMail/ready",
            "root_name": "mail-gmail",
            "file_kind": "document",
            "mime_type": "application/pdf",
            "extension": ".pdf",
            "size_bytes": 200,
            "status": "metadata_only",
            "deleted_at": None,
            "metadata": {},
            "chunks": [],
        },
    ]
    monkeypatch.setattr(
        database,
        "get_asset_chunk_detail",
        lambda chunk_id: {
            "id": chunk_id,
            "asset_id": "asset-html",
            "chunk_index": 0,
            "title": "body.html",
            "body": '<p onclick="bad()">Please review</p><script>alert(1)</script>',
            "asset_path": "export-1/body.html",
            "root_name": "mail-gmail",
        },
        raising=False,
    )
    monkeypatch.setattr(database, "get_source_asset_detail", lambda asset_id: next(item for item in assets if item["id"] == asset_id), raising=False)
    monkeypatch.setattr(database, "list_mail_export_assets", lambda export_id, root_name=None: assets, raising=False)
    monkeypatch.setattr(
        database,
        "get_mail_message_by_export_id",
        lambda export_id, profile_name=None: {
            "id": "mail-1",
            "profile_name": "gmail",
            "source_folder": "FluxCapture",
            "export_id": export_id,
            "export_state": "exported",
        },
        raising=False,
    )

    detail = result_details.result_detail("corpus_chunk", "chunk-html")

    assert detail["logical_kind"] == "mail"
    assert detail["mail"]["subject"] == "Customer RFP"
    assert detail["mail"]["profile_name"] == "gmail"
    assert "onclick" not in detail["body"]["html_sanitized"]
    assert "script" not in detail["body"]["html_sanitized"].lower()
    assert detail["attachments"][0]["path"] == "export-1/attachments/rfp.pdf"
    assert {item["path"] for item in detail["related_evidence"]} >= {
        "export-1/body.html",
        "export-1/attachments/rfp.pdf",
    }


def test_mail_detail_hydrates_body_from_private_content_ref(monkeypatch):
    assets = [
        {
            "id": "asset-manifest",
            "path": "export-1/manifest.json",
            "root_path": "E:/FluxMail/ready",
            "root_name": "mail-gmail",
            "file_kind": "text",
            "mime_type": "application/json",
            "extension": ".json",
            "size_bytes": 100,
            "status": "indexed",
            "deleted_at": None,
            "metadata": {},
            "chunks": [
                {
                    "id": "chunk-manifest",
                    "chunk_index": 0,
                    "title": "manifest.json",
                    "body": '{"export_id":"export-1","profile_name":"gmail","subject":"Customer RFP","sender":"Sender <sender@example.com>","recipients":["me@example.com"],"attachment_count":0}',
                }
            ],
        },
        {
            "id": "asset-body",
            "path": "export-1/body.txt",
            "root_path": "E:/FluxMail/ready",
            "root_name": "mail-gmail",
            "file_kind": "text",
            "mime_type": "text/plain",
            "extension": ".txt",
            "size_bytes": 40,
            "status": "indexed",
            "deleted_at": None,
            "metadata": {},
            "chunks": [
                {
                    "id": "chunk-body",
                    "chunk_index": 0,
                    "title": "body.txt",
                    "body": "",
                    "metadata": {
                        "mail_content": {
                            "storage": "disk_sidecar",
                            "sha256": "abc123",
                            "relative_path": "mail/chunks/abc123.json",
                            "redacted_from_db": True,
                        }
                    },
                }
            ],
        },
    ]

    monkeypatch.setattr(
        database,
        "get_asset_chunk_detail",
        lambda chunk_id: {
            "id": chunk_id,
            "asset_id": "asset-body",
            "chunk_index": 0,
            "title": "body.txt",
            "body": "",
            "asset_path": "export-1/body.txt",
            "root_name": "mail-gmail",
            "metadata": {
                "mail_content": {
                    "storage": "disk_sidecar",
                    "sha256": "abc123",
                    "relative_path": "mail/chunks/abc123.json",
                    "redacted_from_db": True,
                }
            },
        },
        raising=False,
    )
    monkeypatch.setattr(database, "get_source_asset_detail", lambda asset_id: next(item for item in assets if item["id"] == asset_id), raising=False)
    monkeypatch.setattr(database, "list_mail_export_assets", lambda export_id, root_name=None: assets, raising=False)
    monkeypatch.setattr(database, "get_mail_message_by_export_id", lambda export_id, profile_name=None: None, raising=False)
    monkeypatch.setattr(
        result_details.mail_content_store,
        "read_mail_content",
        lambda ref: "Please review the private mail body.",
    )

    detail = result_details.result_detail("corpus_chunk", "chunk-body")

    assert detail["logical_kind"] == "mail"
    assert detail["body"]["text"] == "Please review the private mail body."
    assert detail["provenance"][1]["asset_id"] == "asset-body"


def test_related_evidence_uses_parent_metadata(monkeypatch):
    monkeypatch.setattr(
        database,
        "get_source_asset_detail",
        lambda asset_id: {
            "id": asset_id,
            "path": "archive/bundle.zip",
            "root_path": "E:/Flux Docs",
            "root_name": "docs",
            "file_kind": "archive",
            "mime_type": "application/zip",
            "extension": ".zip",
            "size_bytes": 1000,
            "status": "metadata_only",
            "deleted_at": None,
            "metadata": {},
            "chunks": [],
        },
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "list_related_source_assets",
        lambda **_kwargs: [
            {
                "id": "child-1",
                "path": "archive/bundle.zip/member.txt",
                "file_kind": "text",
                "status": "indexed",
                "metadata": {"parent_asset_id": "asset-parent"},
            }
        ],
        raising=False,
    )

    detail = result_details.result_detail("asset", "asset-parent")

    assert detail["logical_kind"] == "file"
    assert detail["related_evidence"][0]["relationship"] == "related"
    assert detail["related_evidence"][0]["path"] == "archive/bundle.zip/member.txt"
