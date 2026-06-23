import base64
from pathlib import Path

from flux_llm_kb import crawler
from flux_llm_kb.crawler import CorpusPolicy, classify_file, scan_path


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
