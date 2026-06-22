import base64
from types import SimpleNamespace

from flux_llm_kb.crawler import CorpusPolicy
from flux_llm_kb.extractors import extract_file, extractor_availability


def test_extract_file_reads_text_chunks(tmp_path):
    path = tmp_path / "decision.md"
    path.write_text("# Decision\nUse the unified dashboard for watcher health.", encoding="utf-8")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].body == "# Decision\nUse the unified dashboard for watcher health."
    assert result.metadata["extractor"] == "text"


def test_extract_file_records_png_dimensions_without_cloud_calls(tmp_path):
    path = tmp_path / "pixel.png"
    path.write_bytes(
        base64.b64decode(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAIAAADZrBkAAAAAD0lEQVR4nGP8z8AARLJAgAEACPwD"
            "Aaz3RyoAAAAASUVORK5CYII="
        )
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status in {"metadata_only", "indexed"}
    assert result.metadata["width"] == 2
    assert result.metadata["height"] == 3
    assert result.metadata["extractor"] == "image"


def test_extractor_availability_reports_optional_tools():
    availability = extractor_availability()

    assert "python_docx" in availability
    assert "libreoffice" in availability
    assert "antiword" in availability
    assert "catdoc" in availability
    assert "wvText" in availability
    assert "word_com" in availability
    assert "ffprobe" in availability
    assert all("ok" in item and "message" in item for item in availability.values())


def test_extract_legacy_doc_uses_local_converter(monkeypatch, tmp_path):
    path = tmp_path / "proposal_v2.doc"
    path.write_bytes(b"legacy binary placeholder")

    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: "C:/tools/antiword.exe" if command == "antiword" else None,
    )

    def fake_run(command, **_kwargs):
        assert command[0] == "C:/tools/antiword.exe"
        assert command[-1] == str(path)
        return SimpleNamespace(returncode=0, stdout="Changed legacy Word body", stderr="")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors._extract_with_word_com", lambda _path: None)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].body == "Changed legacy Word body"
    assert result.metadata["extractor"] == "antiword"


def test_extract_legacy_doc_uses_word_com_fallback(monkeypatch, tmp_path):
    path = tmp_path / "resume.doc"
    path.write_bytes(b"legacy binary placeholder")

    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)
    monkeypatch.setattr("flux_llm_kb.extractors._extract_with_word_com", lambda _path: "Word COM extracted body")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].body == "Word COM extracted body"
    assert result.metadata["extractor"] == "word_com"


def test_extract_legacy_doc_blocks_when_no_local_extractor(monkeypatch, tmp_path):
    path = tmp_path / "resume.doc"
    path.write_bytes(b"legacy binary placeholder")

    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)
    monkeypatch.setattr("flux_llm_kb.extractors._extract_with_word_com", lambda _path: None)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.metadata["extractor"] == "legacy_document"
    assert "LibreOffice" in (result.message or "")
