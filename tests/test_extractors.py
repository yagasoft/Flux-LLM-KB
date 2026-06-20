import base64

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
    assert "ffprobe" in availability
    assert all("ok" in item and "message" in item for item in availability.values())
