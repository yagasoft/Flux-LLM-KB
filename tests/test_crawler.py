import base64
from pathlib import Path
from types import SimpleNamespace
import threading
import time

from flux_llm_kb import crawler
from flux_llm_kb.crawler import AssetChunk, CorpusPolicy, classify_file, scan_path


def test_policy_honors_marker_ignores(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    (root / ".gitignore").write_text("*.log\n", encoding="utf-8")
    (root / ".fluxignore").write_text("private/**\n", encoding="utf-8")
    (root / ".exclude.codex").write_text("legacy/**\n", encoding="utf-8")
    (root / "keep.md").write_text("durable project decision", encoding="utf-8")
    (root / "debug.log").write_text("ignore me", encoding="utf-8")
    (root / "private").mkdir()
    (root / "private" / "secret.md").write_text("ignore me", encoding="utf-8")
    (root / "legacy").mkdir()
    (root / "legacy" / "old.md").write_text("ignore me", encoding="utf-8")

    plan = scan_path(root, CorpusPolicy(root_path=root))

    assert [asset.relative_path for asset in plan.assets] == ["keep.md"]
    assert plan.assets[0].chunks[0].body == "durable project decision"


def test_scan_path_indexes_python_code_with_semantic_chunks_and_metadata(tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    (root / "service.py").write_text(
        "\n".join(
            [
                "class BillingService:",
                "    def issue_invoice(self, customer_id):",
                "        return customer_id",
                "",
                "def create_invoice(customer_id):",
                "    return BillingService().issue_invoice(customer_id)",
            ]
        ),
        encoding="utf-8",
    )

    plan = scan_path(root, CorpusPolicy(root_path=root))

    assert len(plan.assets) == 1
    asset = plan.assets[0]
    assert asset.file_kind == "code"
    assert asset.extraction_tier == "inline"
    assert asset.metadata["code"]["language"] == "python"
    assert asset.metadata["code"]["parser_status"] == "parsed"
    assert {chunk.title for chunk in asset.chunks} == {
        "service.py::module",
        "service.py::BillingService",
        "service.py::BillingService.issue_invoice",
        "service.py::create_invoice",
    }
    assert all(chunk.metadata["language"] == "python" for chunk in asset.chunks)
    assert any(symbol["qualified_name"] == "BillingService.issue_invoice" for symbol in asset.metadata["code"]["symbols"])


def test_scan_path_classifies_heavy_media_as_deferred(tmp_path):
    root = tmp_path / "media"
    root.mkdir()
    (root / "clip.mp4").write_bytes(b"not a real video")

    plan = scan_path(root, CorpusPolicy(root_path=root, heavy_threshold_bytes=1))

    assert plan.assets[0].file_kind == "video"
    assert plan.assets[0].extraction_tier == "deferred"
    assert plan.deferred_jobs == [
        {
            "job_type": "corpus_extract_video",
            "relative_path": "clip.mp4",
            "reason": "heavy_file",
        }
    ]


def test_scan_path_blocks_metadata_only_files_when_strict_indexing_enabled(tmp_path):
    root = tmp_path / "strict"
    root.mkdir()
    (root / "unknown.bin").write_bytes(b"\x00\x01\x02")

    plan = scan_path(root, CorpusPolicy(root_path=root, strict_indexing=True, mail_spool=True))

    assert len(plan.assets) == 1
    asset = plan.assets[0]
    assert asset.file_kind == "binary"
    assert asset.extraction_tier == "metadata_only"
    assert asset.extraction_status == "blocked_missing_dependency"
    assert asset.chunks == ()
    assert asset.metadata["strict_indexing"] is True
    assert asset.metadata["metadata_only_blocked"] is True
    assert "Strict indexing" in asset.metadata["readiness_reason"]
    assert plan.deferred_jobs == []


def test_scan_path_skips_mail_spool_internal_raw_artifacts(tmp_path):
    root = tmp_path / "ready"
    export = root / "export-1"
    attachments = export / "attachments"
    attachments.mkdir(parents=True)
    (export / "manifest.json").write_text('{"subject":"Customer RFP"}', encoding="utf-8")
    (export / "body.txt").write_text("Please review the customer RFP.", encoding="utf-8")
    (export / "body.html").write_text("<p>Please review the customer RFP.</p>", encoding="utf-8")
    (export / "message.eml").write_text("Subject: Customer RFP\n\nPlease review", encoding="utf-8")
    (attachments / "rfp.txt").write_text("Attachment requirements", encoding="utf-8")

    plan = scan_path(root, CorpusPolicy(root_path=root, strict_indexing=True, mail_spool=True))

    assert [asset.relative_path for asset in plan.assets] == [
        "export-1/attachments/rfp.txt",
        "export-1/body.txt",
        "export-1/manifest.json",
    ]
    assert {asset.relative_path: asset.extraction_status for asset in plan.assets} == {
        "export-1/attachments/rfp.txt": None,
        "export-1/body.txt": None,
        "export-1/manifest.json": None,
    }
    assert all(asset.chunks for asset in plan.assets)
    assert plan.deferred_jobs == []


def test_scan_path_indexes_two_level_eml_outside_managed_mail_spool(tmp_path):
    root = tmp_path / "docs"
    export = root / "export-1"
    export.mkdir(parents=True)
    (export / "message.eml").write_text("Subject: Customer RFP\n\nPlease review", encoding="utf-8")

    plan = scan_path(root, CorpusPolicy(root_path=root))

    assert [asset.relative_path for asset in plan.assets] == ["export-1/message.eml"]
    assert plan.assets[0].file_kind == "mail"
    assert plan.deferred_jobs == [
        {
            "job_type": "corpus_extract_mail",
            "relative_path": "export-1/message.eml",
            "reason": "deferred_extractor",
        }
    ]


def test_scan_path_records_image_metadata_without_ocr(monkeypatch, tmp_path):
    root = tmp_path / "images"
    root.mkdir()
    image = root / "diagram.png"
    image.write_bytes(
        base64.b64decode(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAIAAADZrBkAAAAAD0lEQVR4nGP8z8AARLJAgAEACPwD"
            "Aaz3RyoAAAAASUVORK5CYII="
        )
    )

    def fail_ocr(_path):
        raise AssertionError("OCR must run only in deferred extraction jobs")

    monkeypatch.setattr("flux_llm_kb.extractors._ocr_image", fail_ocr)

    plan = scan_path(root, CorpusPolicy(root_path=root))

    assert plan.assets[0].file_kind == "image"
    assert plan.assets[0].extraction_tier == "deferred"
    assert plan.assets[0].metadata["width"] == 2
    assert plan.assets[0].metadata["height"] == 3
    assert plan.deferred_jobs == [
        {
            "job_type": "corpus_extract_image",
            "relative_path": "diagram.png",
            "reason": "deferred_extractor",
        }
    ]


def test_classify_archive_and_container_extensions_as_deferred(tmp_path):
    archive_extensions = {
        ".zip",
        ".7z",
        ".rar",
        ".tar",
        ".tgz",
        ".gz",
        ".bz2",
        ".xz",
        ".zst",
        ".lz4",
        ".cab",
        ".ar",
        ".cpio",
        ".iso",
        ".dmg",
    }
    container_extensions = {
        ".jar",
        ".war",
        ".ear",
        ".apk",
        ".ipa",
        ".nupkg",
        ".whl",
        ".egg",
        ".gem",
        ".crate",
        ".deb",
        ".rpm",
        ".vsix",
        ".xpi",
        ".crx",
    }

    for extension in sorted(archive_extensions | container_extensions):
        path = tmp_path / f"bundle{extension}"
        path.write_bytes(b"container placeholder")

        classification = classify_file(path, CorpusPolicy(root_path=tmp_path))

        assert classification.file_kind == ("container" if extension in container_extensions else "archive"), extension
        assert classification.extraction_tier == "deferred", extension


def test_scan_path_queues_archives_and_package_containers(tmp_path):
    root = tmp_path / "containers"
    root.mkdir()
    (root / "bundle.zip").write_bytes(b"PK")
    (root / "package.whl").write_bytes(b"PK")

    plan = scan_path(root, CorpusPolicy(root_path=root))

    assert {asset.relative_path: asset.file_kind for asset in plan.assets} == {
        "bundle.zip": "archive",
        "package.whl": "container",
    }
    assert {(job["job_type"], job["relative_path"], job["reason"]) for job in plan.deferred_jobs} == {
        ("corpus_extract_archive", "bundle.zip", "deferred_extractor"),
        ("corpus_extract_container", "package.whl", "deferred_extractor"),
    }


def test_classify_business_document_extensions_as_deferred_documents(tmp_path):
    extensions = {
        ".dot",
        ".docm",
        ".dotx",
        ".dotm",
        ".xls",
        ".xlt",
        ".xlsb",
        ".xlsm",
        ".xltx",
        ".xltm",
        ".ppt",
        ".pot",
        ".pps",
        ".pptm",
        ".potx",
        ".potm",
        ".ppsx",
        ".ppsm",
        ".odt",
        ".ott",
        ".ods",
        ".ots",
        ".odp",
        ".otp",
    }

    for extension in sorted(extensions):
        path = tmp_path / f"sample{extension}"
        path.write_bytes(b"business document placeholder")

        classification = classify_file(path, CorpusPolicy(root_path=tmp_path))

        assert classification.file_kind == "document", extension
        assert classification.extraction_tier == "deferred", extension


def test_classify_publication_extensions_as_deferred_documents(tmp_path):
    extensions = {".epub", ".fb2", ".mobi", ".azw", ".azw3", ".lit"}

    for extension in sorted(extensions):
        path = tmp_path / f"sample{extension}"
        path.write_bytes(b"publication placeholder")

        classification = classify_file(path, CorpusPolicy(root_path=tmp_path))

        assert classification.file_kind == "document", extension
        assert classification.extraction_tier == "deferred", extension


def test_classify_practical_corpus_coverage_extensions(tmp_path):
    expected = {
        ".srt": "subtitle",
        ".vtt": "subtitle",
        ".eml": "mail",
        ".mbox": "mail",
        ".ics": "calendar",
        ".vcf": "contact",
        ".psv": "structured_data",
        ".ssv": "structured_data",
        ".ndjson": "structured_data",
        ".jsonld": "structured_data",
        ".sarif": "report",
        ".spdx": "report",
        ".har": "report",
        ".sqlite": "database",
        ".duckdb": "database",
        ".geojson": "geospatial",
        ".ifc": "cad",
        ".h5": "scientific",
        ".pem": "sensitive_metadata",
        ".key": "sensitive_metadata",
    }

    for extension, file_kind in expected.items():
        path = tmp_path / f"sample{extension}"
        path.write_text("placeholder", encoding="utf-8")

        classification = classify_file(path, CorpusPolicy(root_path=tmp_path))

        assert classification.file_kind == file_kind, extension
        assert classification.extraction_tier == "deferred", extension


def test_scan_path_queues_comic_archives_as_deferred_archives(tmp_path):
    root = tmp_path / "publications"
    root.mkdir()
    for name in ("comic.cbz", "comic.cbr", "comic.cb7", "comic.cbt"):
        (root / name).write_bytes(b"comic archive placeholder")

    plan = scan_path(root, CorpusPolicy(root_path=root))

    assert {asset.relative_path: asset.file_kind for asset in plan.assets} == {
        "comic.cb7": "archive",
        "comic.cbr": "archive",
        "comic.cbt": "archive",
        "comic.cbz": "archive",
    }
    assert {(job["job_type"], job["relative_path"], job["reason"]) for job in plan.deferred_jobs} == {
        ("corpus_extract_archive", "comic.cb7", "deferred_extractor"),
        ("corpus_extract_archive", "comic.cbr", "deferred_extractor"),
        ("corpus_extract_archive", "comic.cbt", "deferred_extractor"),
        ("corpus_extract_archive", "comic.cbz", "deferred_extractor"),
    }


def test_scan_path_classifies_structured_diagrams_as_deferred(tmp_path):
    root = tmp_path / "diagrams"
    root.mkdir()
    (root / "architecture.drawio").write_text("<mxfile></mxfile>", encoding="utf-8")
    (root / "workflow.drawio.svg").write_text("<svg></svg>", encoding="utf-8")
    (root / "network.drawio.png").write_bytes(b"not a real png")
    (root / "process.vsdx").write_bytes(b"PK")
    (root / "icon.svg").write_text("<svg></svg>", encoding="utf-8")
    (root / "pixel.png").write_bytes(
        base64.b64decode(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAIAAADZrBkAAAAAD0lEQVR4nGP8z8AARLJAgAEACPwD"
            "Aaz3RyoAAAAASUVORK5CYII="
        )
    )

    plan = scan_path(root, CorpusPolicy(root_path=root))

    kinds = {asset.relative_path: asset.file_kind for asset in plan.assets}
    tiers = {asset.relative_path: asset.extraction_tier for asset in plan.assets}
    assert kinds["architecture.drawio"] == "diagram"
    assert kinds["workflow.drawio.svg"] == "diagram"
    assert kinds["network.drawio.png"] == "diagram"
    assert kinds["process.vsdx"] == "diagram"
    assert tiers["architecture.drawio"] == "deferred"
    assert tiers["process.vsdx"] == "deferred"
    assert kinds["icon.svg"] == "image"
    assert kinds["pixel.png"] == "image"
    assert {(job["job_type"], job["relative_path"], job["reason"]) for job in plan.deferred_jobs} == {
        ("corpus_extract_diagram", "architecture.drawio", "deferred_extractor"),
        ("corpus_extract_diagram", "network.drawio.png", "deferred_extractor"),
        ("corpus_extract_image", "icon.svg", "deferred_extractor"),
        ("corpus_extract_image", "pixel.png", "deferred_extractor"),
        ("corpus_extract_diagram", "process.vsdx", "deferred_extractor"),
        ("corpus_extract_diagram", "workflow.drawio.svg", "deferred_extractor"),
    }


def test_scan_path_records_locked_files_as_retrying_locked(monkeypatch, tmp_path):
    root = tmp_path / "locked"
    root.mkdir()
    target = root / "open.docx"
    target.write_bytes(b"locked")
    original_discover = crawler.discover_asset

    def fake_discover(path, root_path, policy):
        if path.name == "open.docx":
            raise PermissionError("file is being used by another process")
        return original_discover(path, root_path, policy)

    monkeypatch.setattr(crawler, "discover_asset", fake_discover)

    plan = scan_path(root, CorpusPolicy(root_path=root))

    assert plan.errors == []
    assert len(plan.assets) == 1
    assert plan.assets[0].relative_path == "open.docx"
    assert plan.assets[0].extraction_status == "retrying_locked"
    assert plan.assets[0].metadata["readiness_reason"] == "file_locked"
    assert "being used" in plan.assets[0].metadata["error"]


def test_scan_path_marks_recent_files_pending_stable(tmp_path):
    root = tmp_path / "unstable"
    root.mkdir()
    target = root / "draft.md"
    target.write_text("still changing", encoding="utf-8")

    plan = scan_path(
        root,
        CorpusPolicy(
            root_path=root,
            stability_quiet_seconds=5.0,
            clock=lambda: target.stat().st_mtime + 1.0,
        ),
    )

    assert plan.assets[0].relative_path == "draft.md"
    assert plan.assets[0].extraction_status == "pending_stable"
    assert plan.assets[0].chunks == ()
    assert plan.assets[0].metadata["readiness_reason"] == "mtime_not_stable"


def test_scan_path_skips_transient_editor_and_cloud_artifacts(tmp_path):
    root = tmp_path / "cloud"
    root.mkdir()
    (root / "~$budget.xlsx").write_bytes(b"office temp")
    (root / "report.tmp").write_text("partial", encoding="utf-8")
    (root / "keep.md").write_text("stable", encoding="utf-8")

    plan = scan_path(root, CorpusPolicy(root_path=root))

    assert [asset.relative_path for asset in plan.assets] == ["keep.md"]


def test_scan_path_reuses_manifest_hash_for_unchanged_file(monkeypatch, tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    target = root / "keep.md"
    target.write_text("stable", encoding="utf-8")
    stat = target.stat()
    quick_hash = crawler._quick_hash(target.resolve(), stat.st_size, stat.st_mtime_ns)

    monkeypatch.setattr(crawler, "_sha256_file", lambda _path: (_ for _ in ()).throw(AssertionError("unchanged files should skip expensive hashing")))

    plan = scan_path(
        root,
        CorpusPolicy(
            root_path=root,
            manifest_lookup=lambda relative_path: {
                "path": relative_path,
                "size_bytes": stat.st_size,
                "mtime_ns": stat.st_mtime_ns,
                "quick_hash": quick_hash,
                "content_hash": "previous-content-hash",
            },
        ),
    )

    assert plan.assets[0].content_hash == "previous-content-hash"
    assert plan.assets[0].metadata["manifest_skipped_unchanged"] is True


def test_scan_path_repairs_unchanged_indexed_file_when_chunks_are_missing(monkeypatch, tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    target = root / "keep.md"
    target.write_text("stable content", encoding="utf-8")
    stat = target.stat()
    quick_hash = crawler._quick_hash(target.resolve(), stat.st_size, stat.st_mtime_ns)

    monkeypatch.setattr(crawler, "_sha256_file", lambda _path: (_ for _ in ()).throw(AssertionError("unchanged repair should reuse manifest hash")))

    def fake_extract_file(path, _policy):
        return SimpleNamespace(
            status="indexed",
            metadata={"extractor": "text"},
            chunks=(
                AssetChunk(
                    chunk_index=0,
                    title=path.name,
                    body="stable content",
                    modality="text",
                    locator="char:0-14",
                    token_estimate=2,
                ),
            ),
        )

    monkeypatch.setattr("flux_llm_kb.extractors.extract_file", fake_extract_file)

    plan = scan_path(
        root,
        CorpusPolicy(
            root_path=root,
            manifest_lookup=lambda relative_path: {
                "path": relative_path,
                "size_bytes": stat.st_size,
                "mtime_ns": stat.st_mtime_ns,
                "quick_hash": quick_hash,
                "content_hash": "previous-content-hash",
                "source_asset_status": "indexed",
                "chunk_count": 0,
            },
        ),
    )

    assert plan.assets[0].content_hash == "previous-content-hash"
    assert plan.assets[0].metadata["manifest_skipped_unchanged"] is True
    assert plan.assets[0].metadata["manifest_repaired_missing_chunks"] is True
    assert plan.assets[0].chunks[0].body == "stable content"


def test_scan_path_hashes_files_concurrently_without_reordering(monkeypatch, tmp_path):
    root = tmp_path / "repo"
    root.mkdir()
    for name in ("b.bin", "a.bin", "c.bin"):
        (root / name).write_bytes(f"payload-{name}".encode("utf-8"))

    active = 0
    max_active = 0
    lock = threading.Lock()
    release = threading.Event()
    entered = threading.Event()
    hashes: list[str] = []

    def fake_hash(path):
        nonlocal active, max_active
        with lock:
            active += 1
            max_active = max(max_active, active)
            if active >= 2:
                entered.set()
        if not release.is_set():
            entered.wait(timeout=1)
            release.set()
        time.sleep(0.01)
        with lock:
            active -= 1
        digest = f"hash:{path.name}"
        hashes.append(digest)
        return digest

    monkeypatch.setattr(crawler, "_sha256_file", fake_hash)

    plan = scan_path(root, CorpusPolicy(root_path=root, hash_parallelism=2))

    assert [asset.relative_path for asset in plan.assets] == ["a.bin", "b.bin", "c.bin"]
    assert [asset.content_hash for asset in plan.assets] == ["hash:a.bin", "hash:b.bin", "hash:c.bin"]
    assert sorted(hashes) == ["hash:a.bin", "hash:b.bin", "hash:c.bin"]
    assert max_active >= 2
