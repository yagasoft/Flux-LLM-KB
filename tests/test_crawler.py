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


def test_classify_file_uses_metadata_only_for_archives(tmp_path):
    archive = tmp_path / "bundle.zip"
    archive.write_bytes(b"PK")

    classification = classify_file(archive, CorpusPolicy(root_path=tmp_path))

    assert classification.file_kind == "archive"
    assert classification.extraction_tier == "metadata_only"


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
