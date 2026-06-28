import base64
from email.message import EmailMessage
import gzip
import importlib.machinery
import importlib.util
import json
import sqlite3
import sys
import tarfile
from io import BytesIO
from pathlib import Path
from types import SimpleNamespace
from urllib.parse import quote
from zipfile import ZipFile
import zlib

from flux_llm_kb.crawler import AssetChunk, CorpusPolicy
from flux_llm_kb.extractors import extract_file, extractor_availability


PNG_BYTES = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAIAAADZrBkAAAAAD0lEQVR4nGP8z8AARLJAgAEACPwD"
    "Aaz3RyoAAAAASUVORK5CYII="
)
ONE_PIXEL_PNG_BYTES = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMB/axjfkUAAAAASUVORK5CYII="
)


def _zip_payload(entries: dict[str, str | bytes]) -> bytes:
    buffer = BytesIO()
    with ZipFile(buffer, "w") as archive:
        for name, data in entries.items():
            archive.writestr(name, data)
    return buffer.getvalue()


def test_extract_file_reads_text_chunks(tmp_path):
    path = tmp_path / "decision.md"
    path.write_text("# Decision\nUse the unified dashboard for watcher health.", encoding="utf-8")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].body == "# Decision\nUse the unified dashboard for watcher health."
    assert result.metadata["extractor"] == "text"


def test_extract_file_records_png_dimensions_without_cloud_calls(tmp_path):
    path = tmp_path / "pixel.png"
    path.write_bytes(PNG_BYTES)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status in {"metadata_only", "indexed"}
    assert result.metadata["width"] == 2
    assert result.metadata["height"] == 3
    assert result.metadata["extractor"] == "image"


def test_extract_subtitle_transcript_formats_strip_cues_and_timing(tmp_path):
    path = tmp_path / "standup.srt"
    path.write_text(
        "1\n00:00:00,000 --> 00:00:02,000\nCoverage bundle begins.\n\n"
        "2\n00:00:02,000 --> 00:00:04,000\nOperators see safer metadata.",
        encoding="utf-8",
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "subtitle"
    assert result.metadata["cue_count"] == 2
    assert result.chunks[0].body == "Coverage bundle begins.\nOperators see safer metadata."
    assert "00:00" not in result.chunks[0].body


def test_extract_eml_indexes_headers_and_plain_body_without_attachment_content(tmp_path):
    message = EmailMessage()
    message["Subject"] = "Roadmap Review"
    message["From"] = "sender@example.com"
    message["To"] = "operator@example.com"
    message.set_content("Coverage completion should stay metadata first.")
    message.add_attachment(b"attachment raw bytes", maintype="application", subtype="octet-stream", filename="secret.bin")
    path = tmp_path / "message.eml"
    path.write_bytes(message.as_bytes())

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "mail"
    assert result.metadata["message_count"] == 1
    assert result.metadata["attachment_count"] == 1
    assert result.metadata["subjects"] == ["Roadmap Review"]
    assert "Roadmap Review" in result.chunks[0].body
    assert "Coverage completion should stay metadata first." in result.chunks[0].body
    assert "attachment raw bytes" not in result.chunks[0].body
    assert "sender@example.com" not in result.chunks[0].body


def test_extract_mbox_summarizes_multiple_messages(tmp_path):
    path = tmp_path / "archive.mbox"
    path.write_text(
        "From sender@example.com Fri Jan 01 00:00:00 2026\n"
        "Subject: First Decision\n"
        "From: sender@example.com\n"
        "\n"
        "First body line.\n"
        "From sender@example.com Fri Jan 02 00:00:00 2026\n"
        "Subject: Second Decision\n"
        "From: sender@example.com\n"
        "\n"
        "Second body line.\n",
        encoding="utf-8",
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "mail"
    assert result.metadata["mail_format"] == "mbox"
    assert result.metadata["message_count"] == 2
    assert result.metadata["subjects"] == ["First Decision", "Second Decision"]
    assert "First body line." in result.chunks[0].body
    assert "Second body line." in result.chunks[0].body


def test_extract_calendar_and_contact_files_use_conservative_text_summaries(tmp_path):
    ics = tmp_path / "meeting.ics"
    ics.write_text(
        "BEGIN:VCALENDAR\nBEGIN:VEVENT\nSUMMARY:Coverage Review\nDTSTART:20260626T090000Z\n"
        "DESCRIPTION:Review parser coverage\nEND:VEVENT\nEND:VCALENDAR\n",
        encoding="utf-8",
    )
    vcf = tmp_path / "person.vcf"
    vcf.write_text(
        "BEGIN:VCARD\nFN:Flux Operator\nORG:Local KB\nEMAIL:operator@example.com\nEND:VCARD\n",
        encoding="utf-8",
    )

    calendar = extract_file(ics, CorpusPolicy(root_path=tmp_path))
    contact = extract_file(vcf, CorpusPolicy(root_path=tmp_path))

    assert calendar.status == "indexed"
    assert calendar.metadata["extractor"] == "calendar"
    assert calendar.metadata["event_count"] == 1
    assert "Coverage Review" in calendar.chunks[0].body
    assert "Review parser coverage" in calendar.chunks[0].body
    assert contact.status == "indexed"
    assert contact.metadata["extractor"] == "contact"
    assert contact.metadata["contact_count"] == 1
    assert "Flux Operator" in contact.chunks[0].body
    assert "operator@example.com" not in contact.chunks[0].body


def test_extractor_availability_reports_optional_tools():
    availability = extractor_availability()

    assert "python_docx" in availability
    assert "libreoffice" in availability
    assert "antiword" in availability
    assert "catdoc" in availability
    assert "wvText" in availability
    assert "word_com" in availability
    assert "pdftoppm" in availability
    assert "ffprobe" in availability
    assert "ffmpeg" in availability
    assert "faster_whisper" in availability
    assert "ebook_convert" in availability
    for name in (
        "readpst",
        "msgconvert",
        "duckdb",
        "pyarrow",
        "ogrinfo",
        "gdalinfo",
        "ifcopenshell",
        "assimp",
        "blender",
        "exiftool",
        "pandoc",
    ):
        assert name in availability
    assert all("ok" in item and "message" in item for item in availability.values())


def test_extract_epub_indexes_xhtml_content_and_metadata(tmp_path):
    path = tmp_path / "guide.epub"
    path.write_bytes(
        _zip_payload(
            {
                "OEBPS/content.opf": """<?xml version="1.0" encoding="utf-8"?>
<package xmlns:dc="http://purl.org/dc/elements/1.1/">
  <metadata>
    <dc:title>Slow Knowledge</dc:title>
    <dc:creator>Ada Architect</dc:creator>
  </metadata>
  <manifest>
    <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" />
  </manifest>
</package>""",
                "OEBPS/chapter.xhtml": """<?xml version="1.0" encoding="utf-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
  <body>
    <h1>Chapter One</h1>
    <p>Owl roadmap decisions stay local and reviewable.</p>
  </body>
</html>""",
            }
        )
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "publication"
    assert result.metadata["publication_type"] == "ebook"
    assert result.metadata["publication_format"] == "epub"
    assert result.metadata["publication_title"] == "Slow Knowledge"
    assert result.metadata["publication_author"] == "Ada Architect"
    assert result.metadata["content_file_count"] == 1
    assert "Chapter One" in result.chunks[0].body
    assert "Owl roadmap decisions stay local and reviewable." in result.chunks[0].body


def test_extract_fb2_indexes_body_content_and_metadata(tmp_path):
    path = tmp_path / "brief.fb2"
    path.write_text(
        """<?xml version="1.0" encoding="utf-8"?>
<FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0">
  <description>
    <title-info>
      <author><first-name>Ada</first-name><last-name>Architect</last-name></author>
      <book-title>Flux Field Notes</book-title>
    </title-info>
  </description>
  <body>
    <section>
      <title><p>Extractor Notes</p></title>
      <p>FB2 paragraphs become searchable local chunks.</p>
      <p>Metadata stays sanitized and bounded.</p>
    </section>
  </body>
</FictionBook>""",
        encoding="utf-8",
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "publication"
    assert result.metadata["publication_type"] == "ebook"
    assert result.metadata["publication_format"] == "fb2"
    assert result.metadata["publication_title"] == "Flux Field Notes"
    assert result.metadata["publication_author"] == "Ada Architect"
    assert result.metadata["paragraph_count"] == 3
    assert "Extractor Notes" in result.chunks[0].body
    assert "FB2 paragraphs become searchable local chunks." in result.chunks[0].body


def test_extract_mobi_blocks_when_ebook_convert_is_missing(monkeypatch, tmp_path):
    path = tmp_path / "legacy.mobi"
    path.write_bytes(b"mobi placeholder")
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "MOBI/AZW/LIT extraction requires Calibre ebook-convert."
    assert result.metadata["extractor"] == "publication"
    assert result.metadata["publication_format"] == "mobi"
    assert result.metadata["attempted"] == ["ebook-convert"]


def test_extract_azw3_uses_calibre_conversion(monkeypatch, tmp_path):
    path = tmp_path / "manual.azw3"
    path.write_bytes(b"azw3 placeholder")
    commands = []
    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: "C:/tools/ebook-convert.exe" if command == "ebook-convert" else None,
    )

    def fake_run(command, **_kwargs):
        commands.append(command)
        Path(command[2]).write_text("Converted chapter text from Calibre.", encoding="utf-8")
        return SimpleNamespace(returncode=0, stdout="", stderr="")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].body == "Converted chapter text from Calibre."
    assert result.metadata["extractor"] == "ebook_convert"
    assert result.metadata["publication_type"] == "ebook"
    assert result.metadata["publication_format"] == "azw3"
    assert result.metadata["source_extension"] == ".azw3"
    assert result.metadata["converted_extension"] == ".txt"
    assert commands and commands[0][0].endswith("ebook-convert.exe")


def test_extract_cbz_reuses_container_extraction_with_publication_metadata(tmp_path):
    path = tmp_path / "comic.cbz"
    path.write_bytes(_zip_payload({"page-notes.txt": "Panel text from a comic archive."}))

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.metadata["extractor"] == "container"
    assert result.metadata["publication_type"] == "comic_archive"
    assert result.metadata["publication_format"] == "cbz"
    assert result.metadata["member_count"] == 1
    assert result.metadata["parsed_child_count"] == 1
    assert result.child_assets[0].member_path == "page-notes.txt"
    assert result.child_assets[0].chunks[0].body == "Panel text from a comic archive."


def test_extract_archive_uses_embedded_media_sidecar_without_asr_tools(monkeypatch, tmp_path):
    path = tmp_path / "media-bundle.zip"
    path.write_bytes(
        _zip_payload(
            {
                "clip.mp4": b"fake media bytes",
                "clip.mp4.srt": "1\n00:00:00,000 --> 00:00:01,000\nPrepared archive transcript",
            }
        )
    )
    tool_lookups = []
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: tool_lookups.append(command) or None)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    video_child = next(child for child in result.child_assets if child.member_path == "clip.mp4")
    sidecar_child = next(child for child in result.child_assets if child.member_path == "clip.mp4.srt")
    assert video_child.extraction_status == "indexed"
    assert video_child.chunks[0].modality == "transcript"
    assert "Prepared archive transcript" in video_child.chunks[0].body
    assert video_child.metadata["transcript_source"] == "embedded_sidecar"
    assert video_child.metadata["embedded_sidecar_path"] == "clip.mp4.srt"
    assert sidecar_child.member_path == "clip.mp4.srt"
    assert "ffprobe" not in tool_lookups


def test_extract_media_prefers_sidecar_transcript_before_probe_or_asr(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    path.with_suffix(".mp4.txt").write_text("Prepared transcript from sidecar", encoding="utf-8")
    monkeypatch.setattr(
        "flux_llm_kb.extractors.run_no_window",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("sidecar should bypass tools")),
    )
    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda _command: (_ for _ in ()).throw(AssertionError("sidecar should bypass tool lookup")),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].modality == "transcript"
    assert result.chunks[0].body == "Prepared transcript from sidecar"
    assert result.metadata["transcript_source"] == "sidecar"
    assert "asr" not in result.metadata


def test_extract_media_blocks_when_ffprobe_is_missing(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "ffprobe command not found"
    assert result.metadata["extractor"] == "video"


def test_extract_media_skips_asr_when_disabled(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    monkeypatch.setenv("FLUX_KB_ASR_ENABLED", "false")
    monkeypatch.setenv("FLUX_KB_ASR_MAX_DURATION_SECONDS", "3600")
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    calls = []
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")

    def fake_run(command, **_kwargs):
        calls.append(command[0])
        assert command[0].endswith("ffprobe.exe")
        return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=12), stderr="")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.metadata["ffprobe"]["format"]["duration"] == "12"
    assert result.metadata["asr"]["status"] == "disabled"
    assert result.metadata["asr"]["cache_hits"] == 0
    assert result.metadata["asr"]["cache_misses"] == 0
    assert calls == ["C:/tools/ffprobe.exe"]


def test_extract_media_blocks_when_asr_model_path_is_missing(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    _configure_asr(monkeypatch, tmp_path, model_path="")
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    monkeypatch.setattr(
        "flux_llm_kb.extractors.run_no_window",
        lambda command, **_kwargs: SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=12), stderr=""),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "ASR model path is not configured"
    assert result.metadata["asr"]["status"] == "blocked_missing_dependency"
    assert result.metadata["asr"]["cache_hits"] == 0
    assert result.metadata["asr"]["cache_misses"] == 0


def test_extract_media_blocks_when_ffmpeg_is_missing(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    _configure_asr(monkeypatch, tmp_path)

    def fake_which(command):
        if command == "ffmpeg":
            return None
        return f"C:/tools/{command}.exe"

    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", fake_which)
    monkeypatch.setattr(
        "flux_llm_kb.extractors.run_no_window",
        lambda command, **_kwargs: SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=12), stderr=""),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "ffmpeg command not found"
    assert result.metadata["asr"]["status"] == "blocked_missing_dependency"


def test_extract_media_blocks_when_faster_whisper_is_missing(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    _configure_asr(monkeypatch, tmp_path)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    monkeypatch.setattr(
        "flux_llm_kb.extractors.importlib.util.find_spec",
        lambda name: None if name == "faster_whisper" else importlib.util.find_spec(name),
    )
    run_calls = []

    def fake_run(command, **_kwargs):
        run_calls.append(command[0])
        assert command[0].endswith("ffprobe.exe")
        return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=12), stderr="")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "faster_whisper not installed"
    assert result.metadata["asr"]["status"] == "blocked_missing_dependency"
    assert run_calls == ["C:/tools/ffprobe.exe"]


def test_extract_media_skips_asr_when_duration_exceeds_cap(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    _configure_asr(monkeypatch, tmp_path, max_duration=10)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    calls = []

    def fake_run(command, **_kwargs):
        calls.append(command[0])
        assert command[0].endswith("ffprobe.exe")
        return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=75), stderr="")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.metadata["asr"]["status"] == "skipped_duration_cap"
    assert result.metadata["asr"]["duration_seconds"] == 75.0
    assert result.metadata["asr"]["max_duration_seconds"] == 10
    assert calls == ["C:/tools/ffprobe.exe"]


def test_extract_media_runs_local_asr_and_reuses_redacted_cache(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    model_path = _configure_asr(monkeypatch, tmp_path)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    fake_spec = importlib.machinery.ModuleSpec("faster_whisper", loader=None)
    monkeypatch.setattr(
        "flux_llm_kb.extractors.importlib.util.find_spec",
        lambda name: fake_spec if name == "faster_whisper" else importlib.util.find_spec(name),
    )
    calls = {"ffprobe": 0, "ffmpeg": 0, "model": 0}

    def fake_run(command, **_kwargs):
        if command[0].endswith("ffprobe.exe"):
            calls["ffprobe"] += 1
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=12), stderr="")
        if command[0].endswith("ffmpeg.exe"):
            calls["ffmpeg"] += 1
            Path(command[-1]).write_bytes(b"wav")
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"unexpected command: {command}")

    class FakeWhisperModel:
        def __init__(self, model_size_or_path, **kwargs):
            calls["model"] += 1
            assert model_size_or_path == str(model_path)
            assert kwargs["local_files_only"] is True

        def transcribe(self, audio_path, **_kwargs):
            assert Path(audio_path).exists()
            return (
                [
                    SimpleNamespace(
                        start=0.0,
                        end=1.25,
                        text="Project recap mentions sk-12345678901234567890",
                    )
                ],
                SimpleNamespace(language="en"),
            )

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setitem(sys.modules, "faster_whisper", SimpleNamespace(WhisperModel=FakeWhisperModel, __spec__=fake_spec))

    first = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert first.status == "indexed"
    assert first.chunks[0].modality == "transcript"
    assert first.chunks[0].body == "Project recap mentions [REDACTED:openai_api_key]"
    assert first.metadata["asr"]["status"] == "completed"
    assert first.metadata["asr"]["engine"] == "faster_whisper"
    assert first.metadata["asr"]["cache_hits"] == 0
    assert first.metadata["asr"]["cache_misses"] == 1
    assert first.metadata["asr"]["segments"] == 1

    second = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert second.status == "indexed"
    assert second.chunks[0].body == first.chunks[0].body
    assert second.metadata["asr"]["status"] == "cache_hit"
    assert second.metadata["asr"]["cache_hits"] == 1
    assert second.metadata["asr"]["cache_misses"] == 0
    assert second.metadata["asr"]["segments"] == 1
    assert calls == {"ffprobe": 2, "ffmpeg": 1, "model": 1}


def _configure_asr(
    monkeypatch,
    tmp_path: Path,
    *,
    enabled: bool = True,
    model_path: str | None = None,
    max_duration: int = 3600,
) -> Path:
    model_dir = tmp_path / "models" / "faster-whisper-tiny"
    if model_path is None:
        model_dir.mkdir(parents=True)
        model_path = str(model_dir)
    monkeypatch.setenv("FLUX_KB_ASR_ENABLED", "true" if enabled else "false")
    monkeypatch.setenv("FLUX_KB_ASR_MODEL_PATH", model_path)
    monkeypatch.setenv("FLUX_KB_ASR_MAX_DURATION_SECONDS", str(max_duration))
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    return model_dir


def _configure_vision(
    monkeypatch,
    tmp_path: Path,
    *,
    enabled: bool = True,
    model: str = "llava:latest",
    provider: str = "ollama",
    max_pixels: int = 4_096_000,
) -> None:
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "true" if enabled else "false")
    monkeypatch.setenv("FLUX_KB_VISION_MODEL", model)
    monkeypatch.setenv("FLUX_KB_VISION_MAX_IMAGE_PIXELS", str(max_pixels))
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_ENABLED", "true")
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_PROVIDER", provider)
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_BASE_URL", "http://127.0.0.1:11434")
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_PROBE_TIMEOUT_SECONDS", "1")


def _configure_frame_sampling(
    monkeypatch,
    tmp_path: Path,
    *,
    enabled: bool = True,
    count: int = 3,
    threshold: float = 0.35,
    max_duration: int = 1800,
) -> None:
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setenv("FLUX_KB_ASR_ENABLED", "false")
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_SAMPLING_ENABLED", "true" if enabled else "false")
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_SAMPLE_COUNT", str(count))
    monkeypatch.setenv("FLUX_KB_VIDEO_SCENE_THRESHOLD", str(threshold))
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_MAX_DURATION_SECONDS", str(max_duration))


def _media_probe_json(*, duration: int | float) -> str:
    return f'{{"format":{{"duration":"{duration}"}},"streams":[{{"codec_type":"audio","codec_name":"aac"}}]}}'


def test_extract_image_blocks_when_tesseract_is_missing(monkeypatch, tmp_path):
    path = tmp_path / "scan.png"
    path.write_bytes(PNG_BYTES)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "tesseract command not found"
    assert result.metadata["ocr"]["status"] == "blocked_missing_dependency"
    assert result.metadata["ocr"]["cache_hits"] == 0
    assert result.metadata["ocr"]["cache_misses"] == 0


def test_extract_image_skips_decorative_one_pixel_assets_before_ocr_or_vision(monkeypatch, tmp_path):
    path = tmp_path / "spacer.png"
    path.write_bytes(ONE_PIXEL_PNG_BYTES)
    _configure_vision(monkeypatch, tmp_path)
    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda _command: (_ for _ in ()).throw(AssertionError("decorative image should not probe OCR tools")),
    )
    monkeypatch.setattr(
        "flux_llm_kb.extractors.run_no_window",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("decorative image should not run tools")),
    )
    monkeypatch.setattr(
        "flux_llm_kb.extractors.urlopen",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("decorative image should not call vision")),
        raising=False,
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.chunks == ()
    assert result.metadata["decorative"] == {"status": "skipped", "reason": "tiny_spacer"}
    assert "ocr" not in result.metadata
    assert "vision" not in result.metadata


def test_extract_image_uses_local_vision_when_ocr_is_missing_and_reuses_cache(monkeypatch, tmp_path):
    path = tmp_path / "diagram.png"
    path.write_bytes(PNG_BYTES)
    _configure_vision(monkeypatch, tmp_path)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)
    calls = {"vision": 0}

    class FakeResponse:
        def read(self, _limit=-1):
            return b'{"response":"Diagram mentions sk-12345678901234567890 and system context"}'

    def fake_urlopen(request, **_kwargs):
        calls["vision"] += 1
        assert request.full_url == "http://127.0.0.1:11434/api/generate"
        return FakeResponse()

    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    first = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert first.status == "indexed"
    assert first.chunks[0].modality == "vision"
    assert first.chunks[0].body == "Diagram mentions [REDACTED:openai_api_key] and system context"
    assert first.metadata["ocr"]["status"] == "blocked_missing_dependency"
    assert first.metadata["vision"]["status"] == "completed"
    assert first.metadata["vision"]["cache_hits"] == 0
    assert first.metadata["vision"]["cache_misses"] == 1

    monkeypatch.setattr(
        "flux_llm_kb.extractors.urlopen",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("second extraction should use vision cache")),
        raising=False,
    )

    second = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert second.status == "indexed"
    assert second.chunks[0].body == first.chunks[0].body
    assert second.metadata["vision"]["status"] == "cache_hit"
    assert second.metadata["vision"]["cache_hits"] == 1
    assert second.metadata["vision"]["cache_misses"] == 0
    assert calls == {"vision": 1}


def test_extract_image_blocks_unsupported_vision_provider_without_remote_call(monkeypatch, tmp_path):
    path = tmp_path / "diagram.png"
    path.write_bytes(PNG_BYTES)
    _configure_vision(monkeypatch, tmp_path, provider="openai_compatible")
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)
    monkeypatch.setattr(
        "flux_llm_kb.extractors.urlopen",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("unsupported provider must not call network")),
        raising=False,
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.metadata["vision"]["status"] == "blocked_config"
    assert result.metadata["vision"]["provider"] == "openai_compatible"
    assert result.message == "vision provider openai_compatible is not implemented for vision enrichment in this build"


def test_extract_image_keeps_ocr_indexing_when_vision_provider_is_blocked(monkeypatch, tmp_path):
    path = tmp_path / "scan.png"
    path.write_bytes(PNG_BYTES)
    _configure_vision(monkeypatch, tmp_path, provider="openai_compatible")
    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: "C:/tools/tesseract.exe" if command == "tesseract" else None,
    )
    monkeypatch.setattr(
        "flux_llm_kb.extractors.urlopen",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("blocked provider must not call network")),
        raising=False,
    )

    def fake_run(command, **_kwargs):
        assert command[0] == "C:/tools/tesseract.exe"
        return SimpleNamespace(returncode=0, stdout="OCR text survives blocked vision", stderr="")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].modality == "ocr"
    assert result.chunks[0].body == "OCR text survives blocked vision"
    assert result.metadata["ocr"]["status"] == "completed"
    assert result.metadata["vision"]["status"] == "blocked_config"
    assert result.metadata["vision"]["provider"] == "openai_compatible"


def test_extract_image_writes_and_reuses_redacted_ocr_cache(monkeypatch, tmp_path):
    path = tmp_path / "scan.png"
    path.write_bytes(PNG_BYTES)
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: "C:/tools/tesseract.exe" if command == "tesseract" else None,
    )
    calls = []

    def fake_run(command, **_kwargs):
        calls.append(command)
        assert command[0] == "C:/tools/tesseract.exe"
        return SimpleNamespace(returncode=0, stdout="Scanned image text", stderr="")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    first = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert first.status == "indexed"
    assert first.chunks[0].body == "Scanned image text"
    assert first.metadata["ocr"]["cache_hits"] == 0
    assert first.metadata["ocr"]["cache_misses"] == 1
    assert len(calls) == 1

    def fail_run(_command, **_kwargs):
        raise AssertionError("second extraction should use the OCR cache")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fail_run)

    second = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert second.status == "indexed"
    assert second.chunks[0].body == "Scanned image text"
    assert second.metadata["ocr"]["cache_hits"] == 1
    assert second.metadata["ocr"]["cache_misses"] == 0


def test_extract_video_samples_transition_frames_and_reuses_thumbnail_cache(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    _configure_frame_sampling(monkeypatch, tmp_path, count=2, threshold=0.35)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    calls: list[str] = []

    def fake_run(command, **_kwargs):
        joined = " ".join(str(part) for part in command)
        if command[0].endswith("ffprobe.exe"):
            calls.append("ffprobe")
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=60), stderr="")
        if "select='gt(scene,0.35)'" in joined:
            calls.append("scene")
            return SimpleNamespace(
                returncode=0,
                stdout="",
                stderr=(
                    "frame:1 pts_time:12.0\n"
                    "lavfi.scene_score=0.82\n"
                    "frame:2 pts_time:4.0\n"
                    "lavfi.scene_score=0.95\n"
                    "frame:3 pts_time:24.0 lavfi.scene_score=0.40\n"
                ),
            )
        if "-frames:v" in command:
            calls.append(f"thumb:{command[command.index('-ss') + 1]}")
            Path(command[-1]).write_bytes(PNG_BYTES)
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"unexpected command: {command}")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    first = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert first.status == "metadata_only"
    assert first.metadata["frame_sampling"]["status"] == "completed"
    assert first.metadata["frame_sampling"]["timestamps"] == [4.0, 12.0]
    assert first.metadata["frame_sampling"]["scene_scores"] == [0.95, 0.82]
    assert first.metadata["frame_sampling"]["thumbnail_cache_hits"] == 0
    assert first.metadata["frame_sampling"]["thumbnail_cache_misses"] == 2
    assert calls == ["ffprobe", "scene", "thumb:4.000", "thumb:12.000"]

    calls.clear()
    second = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert second.metadata["frame_sampling"]["status"] == "completed"
    assert second.metadata["frame_sampling"]["thumbnail_cache_hits"] == 2
    assert second.metadata["frame_sampling"]["thumbnail_cache_misses"] == 0
    assert calls == ["ffprobe", "scene"]


def test_extract_video_uses_midpoint_frame_when_no_transition_is_detected(monkeypatch, tmp_path):
    path = tmp_path / "static.mp4"
    path.write_bytes(b"fake media")
    _configure_frame_sampling(monkeypatch, tmp_path, count=3, threshold=0.35)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")

    def fake_run(command, **_kwargs):
        joined = " ".join(str(part) for part in command)
        if command[0].endswith("ffprobe.exe"):
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=80), stderr="")
        if "select='gt(scene,0.35)'" in joined:
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        if "-frames:v" in command:
            assert command[command.index("-ss") + 1] == "40.000"
            Path(command[-1]).write_bytes(PNG_BYTES)
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"unexpected command: {command}")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.metadata["frame_sampling"]["status"] == "fallback_no_transition"
    assert result.metadata["frame_sampling"]["timestamps"] == [40.0]
    assert result.metadata["frame_sampling"]["scene_scores"] == []
    assert result.metadata["frame_sampling"]["frame_count"] == 1


def test_extract_pdf_with_embedded_text_skips_ocr(monkeypatch, tmp_path):
    path = tmp_path / "embedded.pdf"
    path.write_bytes(b"%PDF embedded")

    class FakePage:
        def extract_text(self):
            return "Embedded PDF text"

    class FakePdfReader:
        def __init__(self, _path):
            self.pages = [FakePage()]

    monkeypatch.setitem(sys.modules, "pypdf", SimpleNamespace(PdfReader=FakePdfReader))
    monkeypatch.setattr(
        "flux_llm_kb.extractors.run_no_window",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("OCR tools must not run")),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].body == "Embedded PDF text"
    assert result.metadata["ocr"]["status"] == "skipped_embedded_text"
    assert result.metadata["ocr"]["pages_attempted"] == 0


def test_extract_pdf_blocks_when_pypdf_needs_crypto_dependency(monkeypatch, tmp_path):
    path = tmp_path / "encrypted.pdf"
    path.write_bytes(b"%PDF encrypted")

    class DependencyError(Exception):
        pass

    class FakePdfReader:
        def __init__(self, _path):
            raise DependencyError("cryptography>=3.1 is required for AES algorithm")

    monkeypatch.setitem(
        sys.modules,
        "pypdf",
        SimpleNamespace(PdfReader=FakePdfReader, errors=SimpleNamespace(DependencyError=DependencyError)),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert "cryptography" in result.message
    assert result.metadata["extractor"] == "pdf"
    assert result.metadata["dependency"] == "cryptography"


def test_extract_image_only_pdf_uses_pdftoppm_and_tesseract(monkeypatch, tmp_path):
    path = tmp_path / "scan.pdf"
    path.write_bytes(b"%PDF scanned")
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))

    class EmptyPage:
        def extract_text(self):
            return ""

    class FakePdfReader:
        def __init__(self, _path):
            self.pages = [EmptyPage(), EmptyPage()]

    monkeypatch.setitem(sys.modules, "pypdf", SimpleNamespace(PdfReader=FakePdfReader))
    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: {
            "pdftoppm": "C:/tools/pdftoppm.exe",
            "tesseract": "C:/tools/tesseract.exe",
        }.get(command),
    )
    calls = []

    def fake_run(command, **_kwargs):
        calls.append(command)
        if command[0] == "C:/tools/pdftoppm.exe":
            page = command[command.index("-f") + 1]
            output_prefix = Path(command[-1])
            output_prefix.with_name(f"{output_prefix.name}-{page}.png").write_bytes(PNG_BYTES + page.encode("ascii"))
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        if command[0] == "C:/tools/tesseract.exe":
            page_name = Path(command[1]).stem
            return SimpleNamespace(returncode=0, stdout=f"OCR text from {page_name}", stderr="")
        raise AssertionError(command)

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert "OCR text from page-1" in result.chunks[0].body
    assert "OCR text from page-2" in result.chunks[0].body
    assert result.metadata["ocr"]["renderer"] == "pdftoppm"
    assert result.metadata["ocr"]["page_count"] == 2
    assert result.metadata["ocr"]["pages_attempted"] == 2
    assert result.metadata["ocr"]["cache_hits"] == 0
    assert result.metadata["ocr"]["cache_misses"] == 2
    assert [Path(command[0]).name for command in calls].count("pdftoppm.exe") == 2
    assert [Path(command[0]).name for command in calls].count("tesseract.exe") == 2


def test_extract_image_only_pdf_blocks_when_renderer_is_missing(monkeypatch, tmp_path):
    path = tmp_path / "scan.pdf"
    path.write_bytes(b"%PDF scanned")

    class EmptyPage:
        def extract_text(self):
            return ""

    class FakePdfReader:
        def __init__(self, _path):
            self.pages = [EmptyPage()]

    monkeypatch.setitem(sys.modules, "pypdf", SimpleNamespace(PdfReader=FakePdfReader))
    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: "C:/tools/tesseract.exe" if command == "tesseract" else None,
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "pdftoppm command not found"
    assert result.metadata["ocr"]["status"] == "blocked_missing_dependency"
    assert result.metadata["ocr"]["pages_attempted"] == 0


def test_extract_large_scanned_pdf_skips_ocr_by_page_cap(monkeypatch, tmp_path):
    path = tmp_path / "large-scan.pdf"
    path.write_bytes(b"%PDF large")

    class EmptyPage:
        def extract_text(self):
            return ""

    class FakePdfReader:
        def __init__(self, _path):
            self.pages = [EmptyPage() for _ in range(26)]

    monkeypatch.setitem(sys.modules, "pypdf", SimpleNamespace(PdfReader=FakePdfReader))
    monkeypatch.setattr(
        "flux_llm_kb.extractors.run_no_window",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("page-capped PDF should not render OCR pages")),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.metadata["ocr"]["status"] == "skipped_page_cap"
    assert result.metadata["ocr"]["page_count"] == 26
    assert result.metadata["ocr"]["pages_attempted"] == 0


def test_extractor_availability_reports_container_tools():
    availability = extractor_availability()

    assert "seven_zip" in availability
    assert "bsdtar" in availability
    assert "unrar" in availability
    assert "zstd" in availability
    assert "lz4" in availability
    assert "rpm2cpio" in availability
    assert all("ok" in availability[name] and "message" in availability[name] for name in ("seven_zip", "bsdtar", "unrar"))


def test_extract_zip_archive_indexes_inline_child_assets(tmp_path):
    path = tmp_path / "bundle.zip"
    with ZipFile(path, "w") as archive:
        archive.writestr("docs/readme.md", "# Readme\nArchive body")
        archive.writestr("nested/inner.zip", b"PK")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.metadata["extractor"] == "container"
    assert result.metadata["format"] == "zip"
    assert result.metadata["member_count"] == 2
    assert [child.member_path for child in result.child_assets] == ["docs/readme.md", "nested/inner.zip"]
    readme = result.child_assets[0]
    nested = result.child_assets[1]
    assert readme.file_kind == "text"
    assert readme.extraction_status == "indexed"
    assert readme.chunks[0].body == "# Readme\nArchive body"
    assert readme.metadata["container_member_path"] == "docs/readme.md"
    assert nested.file_kind == "archive"
    assert nested.extraction_status == "metadata_only"
    assert nested.chunks == ()


def test_extract_zip_archive_recursively_indexes_nested_container_members(tmp_path):
    path = tmp_path / "bundle.zip"
    inner_zip = _zip_payload({"notes/decision.md": "# Nested\nUse recursive extraction"})
    with ZipFile(path, "w") as archive:
        archive.writestr("nested/inner.zip", inner_zip)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, container_max_depth=2))

    assert result.status == "metadata_only"
    assert result.metadata["max_depth"] == 2
    assert result.metadata["member_count"] == 1
    assert result.metadata["parsed_child_count"] == 1
    assert result.metadata["skipped_child_count"] == 1
    assert result.metadata["blocked_dependency_count"] == 0
    assert [child.member_path for child in result.child_assets] == [
        "nested/inner.zip",
        "nested/inner.zip/notes/decision.md",
    ]
    nested_container, nested_note = result.child_assets
    assert nested_container.file_kind == "archive"
    assert nested_container.extraction_status == "metadata_only"
    assert nested_container.metadata["nested_container"] is True
    assert nested_container.metadata["container_depth"] == 1
    assert nested_note.file_kind == "text"
    assert nested_note.extraction_status == "indexed"
    assert nested_note.metadata["container_depth"] == 2
    assert nested_note.metadata["container_parent_path"] == "nested/inner.zip"
    assert nested_note.chunks[0].body == "# Nested\nUse recursive extraction"


def test_extract_zip_archive_respects_nested_depth_cap(tmp_path):
    path = tmp_path / "bundle.zip"
    inner_zip = _zip_payload({"notes/decision.md": "# Nested\nShould stay deferred"})
    with ZipFile(path, "w") as archive:
        archive.writestr("nested/inner.zip", inner_zip)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, container_max_depth=1))

    assert [child.member_path for child in result.child_assets] == ["nested/inner.zip"]
    assert result.child_assets[0].metadata["recursive_skipped_reason"] == "max_depth"
    assert result.metadata["parsed_child_count"] == 0
    assert result.metadata["skipped_child_count"] == 1


def test_extract_zip_archive_records_nested_parse_failure_without_losing_parent(tmp_path):
    path = tmp_path / "bundle.zip"
    inner_zip = _zip_payload({"../evil.txt": "escape"})
    with ZipFile(path, "w") as archive:
        archive.writestr("nested/inner.zip", inner_zip)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, container_max_depth=2))

    assert result.status == "metadata_only"
    assert [child.member_path for child in result.child_assets] == ["nested/inner.zip"]
    nested_container = result.child_assets[0]
    assert nested_container.extraction_status == "failed"
    assert "unsafe" in " ".join(nested_container.metadata["warnings"]).lower()
    assert result.metadata["parsed_child_count"] == 0
    assert result.metadata["skipped_child_count"] == 1


def test_extract_archive_parses_embedded_document_member(monkeypatch, tmp_path):
    path = tmp_path / "bundle.zip"
    with ZipFile(path, "w") as archive:
        archive.writestr("docs/report.docx", b"docx placeholder")

    def fake_extract_document(member_path, _policy):
        assert member_path.exists()
        assert member_path.suffix == ".docx"
        assert member_path.read_bytes() == b"docx placeholder"
        return SimpleNamespace(
            status="indexed",
            chunks=(
                AssetChunk(
                    chunk_index=0,
                    title="report.docx",
                    body="Embedded document body",
                    token_estimate=3,
                ),
            ),
            metadata={"extractor": "docx"},
            message=None,
        )

    monkeypatch.setattr("flux_llm_kb.extractors._extract_document", fake_extract_document)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, container_max_depth=2))

    assert result.child_assets[0].member_path == "docs/report.docx"
    assert result.child_assets[0].file_kind == "document"
    assert result.child_assets[0].extraction_status == "indexed"
    assert result.child_assets[0].metadata["embedded_extractor"] == "docx"
    assert result.child_assets[0].chunks[0].body == "Embedded document body"


def test_extract_archive_parses_embedded_diagram_member(tmp_path):
    path = tmp_path / "bundle.zip"
    with ZipFile(path, "w") as archive:
        archive.writestr(
            "diagrams/flow.drawio",
            """
            <mxfile>
              <diagram name="Embedded">
                <mxGraphModel>
                  <root><mxCell id="shape" value="Embedded Diagram Label" /></root>
                </mxGraphModel>
              </diagram>
            </mxfile>
            """,
        )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, container_max_depth=2))

    assert result.child_assets[0].member_path == "diagrams/flow.drawio"
    assert result.child_assets[0].file_kind == "diagram"
    assert result.child_assets[0].extraction_status == "indexed"
    assert result.child_assets[0].metadata["embedded_extractor"] == "diagram"
    assert "Embedded Diagram Label" in result.child_assets[0].chunks[0].body


def test_extract_tgz_archive_indexes_text_member(tmp_path):
    path = tmp_path / "bundle.tgz"
    payload = b"Meeting notes from archive"
    with tarfile.open(path, "w:gz") as archive:
        info = tarfile.TarInfo("notes/meeting.txt")
        info.size = len(payload)
        archive.addfile(info, BytesIO(payload))

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.metadata["format"] == "tar"
    assert result.child_assets[0].member_path == "notes/meeting.txt"
    assert result.child_assets[0].extraction_status == "indexed"
    assert "Meeting notes" in result.child_assets[0].chunks[0].body


def test_extract_gzip_stream_indexes_single_child(tmp_path):
    path = tmp_path / "server.log.gz"
    with gzip.open(path, "wb") as handle:
        handle.write(b"error line from compressed log")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.metadata["format"] == "gzip"
    assert len(result.child_assets) == 1
    assert result.child_assets[0].member_path == "server.log"
    assert result.child_assets[0].file_kind == "text"
    assert "compressed log" in result.child_assets[0].chunks[0].body


def test_extract_package_container_uses_zip_adapter(tmp_path):
    path = tmp_path / "library.whl"
    with ZipFile(path, "w") as archive:
        archive.writestr("package/METADATA", "Name: library\nVersion: 1.0")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.metadata["format"] == "zip"
    assert result.metadata["container_kind"] == "container"
    assert result.child_assets[0].member_path == "package/METADATA"
    assert result.child_assets[0].extraction_status == "indexed"
    assert "Version: 1.0" in result.child_assets[0].chunks[0].body


def test_extract_archive_parses_embedded_practical_corpus_member(tmp_path):
    path = tmp_path / "exports.zip"
    with ZipFile(path, "w") as archive:
        archive.writestr(
            "meeting.vtt",
            "WEBVTT\n\n00:00:00.000 --> 00:00:03.000\nRoadmap owners approved coverage completion.",
        )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, container_max_depth=2))

    child = result.child_assets[0]
    assert child.member_path == "meeting.vtt"
    assert child.file_kind == "subtitle"
    assert child.extraction_status == "indexed"
    assert child.metadata["embedded_extractor"] == "subtitle"
    assert child.chunks[0].body == "Roadmap owners approved coverage completion."


def test_extract_archive_rejects_unsafe_member_path(tmp_path):
    path = tmp_path / "unsafe.zip"
    with ZipFile(path, "w") as archive:
        archive.writestr("../evil.txt", "escape")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "failed"
    assert result.metadata["extractor"] == "container"
    assert "unsafe" in (result.message or "").lower()
    assert result.child_assets == ()


def test_extract_archive_respects_member_size_cap(tmp_path):
    path = tmp_path / "large.zip"
    with ZipFile(path, "w") as archive:
        archive.writestr("large.txt", "too large")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, container_max_member_bytes=4))

    assert result.status == "metadata_only"
    assert result.child_assets == ()
    assert "member exceeds size limit" in (result.message or "")


def test_extract_unsupported_archive_blocks_when_tool_missing(monkeypatch, tmp_path):
    path = tmp_path / "bundle.7z"
    path.write_bytes(b"7z placeholder")
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.metadata["extractor"] == "container"
    assert result.metadata["format"] == "7z"
    assert "attempted" in result.metadata
    assert "7z" in (result.message or "")


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


def test_extract_legacy_xls_uses_libreoffice_conversion(monkeypatch, tmp_path):
    path = tmp_path / "budget.xls"
    path.write_bytes(b"legacy spreadsheet placeholder")

    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: "C:/tools/soffice.exe" if command == "soffice" else None,
    )

    def fake_run(command, **_kwargs):
        assert command[0] == "C:/tools/soffice.exe"
        assert "--convert-to" in command
        assert "xlsx" in command
        out_dir = Path(command[command.index("--outdir") + 1])
        (out_dir / f"{path.stem}.xlsx").write_bytes(b"converted spreadsheet")
        return SimpleNamespace(returncode=0, stdout="", stderr="")

    class FakeWorksheet:
        title = "Budget"

        def iter_rows(self, max_row, max_col, values_only):
            assert (max_row, max_col, values_only) == (200, 30, True)
            return iter([("Quarter", "Amount"), ("Q1", 1200)])

    fake_openpyxl = SimpleNamespace(
        load_workbook=lambda _path, read_only, data_only: SimpleNamespace(worksheets=[FakeWorksheet()])
    )
    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setitem(sys.modules, "openpyxl", fake_openpyxl)
    monkeypatch.setattr("flux_llm_kb.extractors._extract_with_excel_com", lambda _path: None, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "libreoffice"
    assert result.metadata["source_extension"] == ".xls"
    assert result.metadata["converted_extension"] == ".xlsx"
    assert result.metadata["sheet_count"] == 1
    assert "Q1 | 1200" in result.chunks[0].body


def test_extract_large_legacy_xls_uses_converted_sample_first(monkeypatch, tmp_path):
    path = tmp_path / "large-budget.xls"
    path.write_bytes(b"legacy spreadsheet placeholder large enough")

    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: "C:/tools/soffice.exe" if command == "soffice" else None,
    )

    def fake_run(command, **_kwargs):
        out_dir = Path(command[command.index("--outdir") + 1])
        (out_dir / f"{path.stem}.xlsx").write_bytes(b"converted spreadsheet large enough")
        return SimpleNamespace(returncode=0, stdout="", stderr="")

    class FakeWorksheet:
        title = "Budget"

        def iter_rows(self, max_row=None, max_col=None, values_only=True):
            rows = [("id", "name", "amount")]
            rows.extend((index, f"Line {index}", index * 100) for index in range(18))
            return iter(rows)

    fake_openpyxl = SimpleNamespace(
        load_workbook=lambda _path, read_only, data_only: SimpleNamespace(worksheets=[FakeWorksheet()])
    )
    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setitem(sys.modules, "openpyxl", fake_openpyxl)
    monkeypatch.setattr("flux_llm_kb.extractors._extract_with_excel_com", lambda _path: None, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=8))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "libreoffice"
    assert result.metadata["source_extension"] == ".xls"
    assert result.metadata["converted_extension"] == ".xlsx"
    assert result.metadata["sample_first"]["source_extension"] == ".xlsx"
    assert result.metadata["sample_first"]["truncated"] is True
    assert result.chunks[0].metadata["sample_first"] is True
    assert "Line 17" not in result.chunks[0].body


def test_extract_legacy_xls_uses_excel_com_fallback(monkeypatch, tmp_path):
    path = tmp_path / "forecast.xls"
    path.write_bytes(b"legacy spreadsheet placeholder")

    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)
    monkeypatch.setattr("flux_llm_kb.extractors._extract_with_excel_com", lambda _path: "Sheet: Forecast\nA | B", raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "excel_com"
    assert result.metadata["source_extension"] == ".xls"
    assert "Sheet: Forecast" in result.chunks[0].body


def test_extract_legacy_xls_blocks_when_no_local_extractor(monkeypatch, tmp_path):
    path = tmp_path / "forecast.xls"
    path.write_bytes(b"legacy spreadsheet placeholder")

    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)
    monkeypatch.setattr("flux_llm_kb.extractors._extract_with_excel_com", lambda _path: None, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.metadata["extractor"] == "legacy_spreadsheet"
    assert result.metadata["attempted"] == ["libreoffice", "excel_com"]
    assert "Excel COM" in (result.message or "")


def test_extract_legacy_ppt_uses_libreoffice_conversion(monkeypatch, tmp_path):
    path = tmp_path / "briefing.ppt"
    path.write_bytes(b"legacy presentation placeholder")

    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: "C:/tools/soffice.exe" if command == "soffice" else None,
    )

    def fake_run(command, **_kwargs):
        assert command[0] == "C:/tools/soffice.exe"
        assert "--convert-to" in command
        assert "pptx" in command
        out_dir = Path(command[command.index("--outdir") + 1])
        (out_dir / f"{path.stem}.pptx").write_bytes(b"converted presentation")
        return SimpleNamespace(returncode=0, stdout="", stderr="")

    class FakeShape:
        text = "Launch Plan"

    fake_pptx = SimpleNamespace(Presentation=lambda _path: SimpleNamespace(slides=[SimpleNamespace(shapes=[FakeShape()])]))
    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setitem(sys.modules, "pptx", fake_pptx)
    monkeypatch.setattr("flux_llm_kb.extractors._extract_with_powerpoint_com", lambda _path: None, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "libreoffice"
    assert result.metadata["source_extension"] == ".ppt"
    assert result.metadata["converted_extension"] == ".pptx"
    assert result.metadata["slide_count"] == 1
    assert "Launch Plan" in result.chunks[0].body


def test_extract_legacy_ppt_uses_powerpoint_com_fallback(monkeypatch, tmp_path):
    path = tmp_path / "briefing.ppt"
    path.write_bytes(b"legacy presentation placeholder")

    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)
    monkeypatch.setattr(
        "flux_llm_kb.extractors._extract_with_powerpoint_com",
        lambda _path: "Slide 1: Launch Plan",
        raising=False,
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "powerpoint_com"
    assert result.metadata["source_extension"] == ".ppt"
    assert "Launch Plan" in result.chunks[0].body


def test_extract_legacy_ppt_blocks_when_no_local_extractor(monkeypatch, tmp_path):
    path = tmp_path / "briefing.ppt"
    path.write_bytes(b"legacy presentation placeholder")

    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)
    monkeypatch.setattr("flux_llm_kb.extractors._extract_with_powerpoint_com", lambda _path: None, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.metadata["extractor"] == "legacy_presentation"
    assert result.metadata["attempted"] == ["libreoffice", "powerpoint_com"]
    assert "PowerPoint COM" in (result.message or "")


def test_extract_large_csv_uses_sample_first_profile(tmp_path):
    csv_path = tmp_path / "large.csv"
    csv_path.write_text(
        "id,name,amount\n"
        + "\n".join(f"{index},Customer {index},{index * 10}" for index in range(25)),
        encoding="utf-8",
    )

    result = extract_file(csv_path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=32))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "sample_first_tabular"
    assert result.metadata["sample_first"]["row_count_estimate"] == 25
    assert result.metadata["sample_first"]["sample_row_count"] < 25
    assert result.metadata["sample_first"]["truncated"] is True
    assert result.metadata["sample_first"]["columns"] == ["id", "name", "amount"]
    assert result.chunks[0].metadata["sample_first"] is True
    assert "Customer 24" not in result.chunks[0].body


def test_extract_pipe_and_space_delimited_files_use_sample_first_profile(tmp_path):
    pipe_path = tmp_path / "large.psv"
    pipe_path.write_text(
        "id|name|amount\n" + "\n".join(f"{index}|Customer {index}|{index * 10}" for index in range(14)),
        encoding="utf-8",
    )
    space_path = tmp_path / "large.ssv"
    space_path.write_text(
        "id name amount\n" + "\n".join(f"{index} Customer{index} {index * 10}" for index in range(14)),
        encoding="utf-8",
    )

    pipe_result = extract_file(pipe_path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=32))
    space_result = extract_file(space_path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=32))

    assert pipe_result.metadata["extractor"] == "sample_first_tabular"
    assert pipe_result.metadata["sample_first"]["format"] == "psv"
    assert pipe_result.metadata["sample_first"]["columns"] == ["id", "name", "amount"]
    assert "Customer 13" not in pipe_result.chunks[0].body
    assert space_result.metadata["sample_first"]["format"] == "ssv"
    assert space_result.metadata["sample_first"]["columns"] == ["id", "name", "amount"]
    assert "Customer13" not in space_result.chunks[0].body


def test_extract_large_jsonl_uses_sample_first_profile(tmp_path):
    jsonl_path = tmp_path / "events.jsonl"
    jsonl_path.write_text(
        "\n".join(f'{{"id": {index}, "kind": "event", "amount": {index * 10}}}' for index in range(20)),
        encoding="utf-8",
    )

    result = extract_file(jsonl_path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=64))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "sample_first_jsonl"
    assert result.metadata["sample_first"]["row_count_estimate"] == 20
    assert result.metadata["sample_first"]["sample_row_count"] < 20
    assert result.metadata["sample_first"]["columns"] == ["amount", "id", "kind"]
    assert result.metadata["sample_first"]["truncated"] is True
    assert result.chunks[0].metadata["sample_first"] is True
    assert '"id": 19' not in result.chunks[0].body


def test_extract_large_ndjson_and_jsonld_use_sample_first_profiles(tmp_path):
    ndjson_path = tmp_path / "events.ndjson"
    ndjson_path.write_text(
        "\n".join(f'{{"id": {index}, "kind": "event", "amount": {index * 10}}}' for index in range(18)),
        encoding="utf-8",
    )
    jsonld_path = tmp_path / "graph.jsonld"
    jsonld_path.write_text(
        json.dumps(
            {
                "@context": {"name": "https://schema.org/name"},
                "@graph": [{"id": index, "name": f"Node {index}"} for index in range(16)],
            }
        ),
        encoding="utf-8",
    )

    ndjson_result = extract_file(ndjson_path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=64))
    jsonld_result = extract_file(jsonld_path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=64))

    assert ndjson_result.metadata["extractor"] == "sample_first_jsonl"
    assert ndjson_result.metadata["sample_first"]["format"] == "ndjson"
    assert ndjson_result.metadata["sample_first"]["row_count_estimate"] == 18
    assert '"id": 17' not in ndjson_result.chunks[0].body
    assert jsonld_result.metadata["extractor"] == "sample_first_json"
    assert jsonld_result.metadata["sample_first"]["format"] == "jsonld"
    assert jsonld_result.metadata["sample_first"]["source_key"] == "@graph"
    assert "Node 15" not in jsonld_result.chunks[0].body


def test_extract_large_json_array_uses_sample_first_profile(tmp_path):
    json_path = tmp_path / "events.json"
    json_path.write_text(
        json.dumps([{"id": index, "kind": "event", "amount": index * 10} for index in range(18)]),
        encoding="utf-8",
    )

    result = extract_file(json_path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=64))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "sample_first_json"
    assert result.metadata["sample_first"]["row_count_estimate"] == 18
    assert result.metadata["sample_first"]["sample_row_count"] < 18
    assert result.metadata["sample_first"]["columns"] == ["amount", "id", "kind"]
    assert result.metadata["sample_first"]["truncated"] is True
    assert result.chunks[0].metadata["sample_first"] is True
    assert '"id": 17' not in result.chunks[0].body


def test_extract_security_and_test_reports_use_bounded_summaries(tmp_path):
    sarif = tmp_path / "scan.sarif"
    sarif.write_text(
        json.dumps(
            {
                "runs": [
                    {
                        "tool": {"driver": {"name": "StaticScan"}},
                        "results": [
                            {"ruleId": "SEC001", "message": {"text": "Use parameterized SQL"}},
                            {"ruleId": "SEC002", "message": {"text": "Rotate test key"}},
                        ],
                    }
                ]
            }
        ),
        encoding="utf-8",
    )
    junit = tmp_path / "junit.xml"
    junit.write_text(
        """<testsuite name="unit" tests="3" failures="1" skipped="1">
  <testcase classname="A" name="passes" />
  <testcase classname="A" name="fails"><failure message="expected true" /></testcase>
</testsuite>""",
        encoding="utf-8",
    )
    lcov = tmp_path / "coverage.lcov"
    lcov.write_text("TN:\nSF:src/app.py\nDA:1,1\nDA:2,0\nend_of_record\n", encoding="utf-8")
    har = tmp_path / "session.har"
    har.write_text(
        json.dumps(
            {
                "log": {
                    "entries": [
                        {"request": {"method": "GET", "url": "https://example.test/api/items"}, "response": {"status": 200}},
                        {"request": {"method": "POST", "url": "https://example.test/api/items"}, "response": {"status": 500}},
                    ]
                }
            }
        ),
        encoding="utf-8",
    )

    sarif_result = extract_file(sarif, CorpusPolicy(root_path=tmp_path))
    junit_result = extract_file(junit, CorpusPolicy(root_path=tmp_path))
    lcov_result = extract_file(lcov, CorpusPolicy(root_path=tmp_path))
    har_result = extract_file(har, CorpusPolicy(root_path=tmp_path))

    assert sarif_result.metadata["extractor"] == "report"
    assert sarif_result.metadata["report_format"] == "sarif"
    assert sarif_result.metadata["finding_count"] == 2
    assert "SEC001: Use parameterized SQL" in sarif_result.chunks[0].body
    assert junit_result.metadata["report_format"] == "junit"
    assert junit_result.metadata["test_count"] == 3
    assert "Failures: 1" in junit_result.chunks[0].body
    assert lcov_result.metadata["report_format"] == "lcov"
    assert lcov_result.metadata["line_coverage_percent"] == 50.0
    assert har_result.metadata["report_format"] == "har"
    assert har_result.metadata["entry_count"] == 2
    assert "POST https://example.test/api/items -> 500" in har_result.chunks[0].body


def test_extract_additional_bom_test_and_coverage_reports_use_bounded_summaries(tmp_path):
    cyclonedx = tmp_path / "bom.cyclonedx"
    cyclonedx.write_text(
        json.dumps(
            {
                "bomFormat": "CycloneDX",
                "components": [
                    {"name": "app", "version": "1.0.0", "purl": "pkg:npm/app@1.0.0"},
                    {"name": "lib", "version": "2.0.0"},
                ],
            }
        ),
        encoding="utf-8",
    )
    spdx = tmp_path / "notice.spdx"
    spdx.write_text("SPDXVersion: SPDX-2.3\nPackageName: sample\nPackageVersion: 1.0.0\n", encoding="utf-8")
    trx = tmp_path / "results.trx"
    trx.write_text(
        """<TestRun><Results>
  <UnitTestResult testName="passes" outcome="Passed" />
  <UnitTestResult testName="fails" outcome="Failed" />
</Results></TestRun>""",
        encoding="utf-8",
    )
    tap = tmp_path / "smoke.tap"
    tap.write_text("TAP version 13\n1..2\nok 1 starts\nnot ok 2 fails\n", encoding="utf-8")
    coverage = tmp_path / "coverage.xml"
    coverage.write_text('<coverage lines-covered="5" lines-valid="10" />', encoding="utf-8")

    cyclonedx_result = extract_file(cyclonedx, CorpusPolicy(root_path=tmp_path))
    spdx_result = extract_file(spdx, CorpusPolicy(root_path=tmp_path))
    trx_result = extract_file(trx, CorpusPolicy(root_path=tmp_path))
    tap_result = extract_file(tap, CorpusPolicy(root_path=tmp_path))
    coverage_result = extract_file(coverage, CorpusPolicy(root_path=tmp_path))

    assert cyclonedx_result.metadata["report_format"] == "cyclonedx"
    assert cyclonedx_result.metadata["component_count"] == 2
    assert "pkg:npm/app@1.0.0" in cyclonedx_result.chunks[0].body
    assert spdx_result.metadata["report_format"] == "spdx"
    assert spdx_result.metadata["package_count"] == 1
    assert "sample 1.0.0" in spdx_result.chunks[0].body
    assert trx_result.metadata["report_format"] == "trx"
    assert trx_result.metadata["test_count"] == 2
    assert trx_result.metadata["failure_count"] == 1
    assert "Failed: 1" in trx_result.chunks[0].body
    assert tap_result.metadata["report_format"] == "tap"
    assert tap_result.metadata["test_count"] == 2
    assert tap_result.metadata["failure_count"] == 1
    assert "not ok 2 fails" in tap_result.chunks[0].body
    assert coverage_result.metadata["report_format"] == "coverage_xml"
    assert coverage_result.metadata["line_coverage_percent"] == 50.0


def test_extract_sqlite_records_schema_metadata_without_rows(tmp_path):
    path = tmp_path / "runtime.sqlite"
    with sqlite3.connect(path) as conn:
        conn.execute("CREATE TABLE secrets (id integer primary key, token text)")
        conn.execute("INSERT INTO secrets (token) VALUES ('raw-secret-token')")
        conn.commit()

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "database"
    assert result.metadata["database_format"] == "sqlite"
    assert result.metadata["table_count"] == 1
    assert result.metadata["tables"][0]["name"] == "secrets"
    assert result.metadata["tables"][0]["columns"] == ["id", "token"]
    assert "secrets (id, token)" in result.chunks[0].body
    assert "raw-secret-token" not in result.chunks[0].body


def test_extract_sensitive_metadata_formats_never_index_raw_content(tmp_path):
    path = tmp_path / "private.pem"
    path.write_text("-----BEGIN PRIVATE KEY-----\nraw secret material\n-----END PRIVATE KEY-----", encoding="utf-8")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.chunks == ()
    assert result.metadata["extractor"] == "sensitive_metadata"
    assert result.metadata["sensitive"] is True


def test_extract_large_xlsx_uses_sample_first_profile(monkeypatch, tmp_path):
    path = tmp_path / "large.xlsx"
    path.write_bytes(b"xlsx placeholder large enough")

    class FakeWorksheet:
        title = "Pipeline"

        def iter_rows(self, values_only=True):
            rows = [("id", "name", "amount")]
            rows.extend((index, f"Deal {index}", index * 100) for index in range(16))
            return iter(rows)

    fake_openpyxl = SimpleNamespace(
        load_workbook=lambda _path, read_only, data_only: SimpleNamespace(worksheets=[FakeWorksheet()])
    )
    monkeypatch.setitem(sys.modules, "openpyxl", fake_openpyxl)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=8))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "sample_first_workbook"
    assert result.metadata["sample_first"]["row_count_estimate"] == 16
    assert result.metadata["sample_first"]["sample_row_count"] < 16
    assert result.metadata["sample_first"]["columns"] == ["id", "name", "amount"]
    assert result.metadata["sample_first"]["sheet_count"] == 1
    assert result.metadata["sample_first"]["truncated"] is True
    assert result.chunks[0].metadata["sample_first"] is True
    assert "Deal 15" not in result.chunks[0].body


def test_extract_opendocument_spreadsheet_uses_libreoffice_conversion(monkeypatch, tmp_path):
    path = tmp_path / "budget.ods"
    path.write_bytes(b"opendocument spreadsheet placeholder")

    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: "C:/tools/soffice.exe" if command == "soffice" else None,
    )

    def fake_run(command, **_kwargs):
        assert command[0] == "C:/tools/soffice.exe"
        assert "--convert-to" in command
        assert "xlsx" in command
        out_dir = Path(command[command.index("--outdir") + 1])
        (out_dir / f"{path.stem}.xlsx").write_bytes(b"converted spreadsheet")
        return SimpleNamespace(returncode=0, stdout="", stderr="")

    class FakeWorksheet:
        title = "ODS Budget"

        def iter_rows(self, max_row, max_col, values_only):
            return iter([("Line", "Amount"), ("Travel", 500)])

    fake_openpyxl = SimpleNamespace(
        load_workbook=lambda _path, read_only, data_only: SimpleNamespace(worksheets=[FakeWorksheet()])
    )
    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setitem(sys.modules, "openpyxl", fake_openpyxl)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "libreoffice"
    assert result.metadata["source_extension"] == ".ods"
    assert result.metadata["converted_extension"] == ".xlsx"
    assert "Travel | 500" in result.chunks[0].body


def test_extract_large_opendocument_spreadsheet_uses_converted_sample_first(monkeypatch, tmp_path):
    path = tmp_path / "large-budget.ods"
    path.write_bytes(b"opendocument spreadsheet placeholder large enough")

    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: "C:/tools/soffice.exe" if command == "soffice" else None,
    )

    def fake_run(command, **_kwargs):
        out_dir = Path(command[command.index("--outdir") + 1])
        (out_dir / f"{path.stem}.xlsx").write_bytes(b"converted spreadsheet large enough")
        return SimpleNamespace(returncode=0, stdout="", stderr="")

    class FakeWorksheet:
        title = "ODS Budget"

        def iter_rows(self, max_row=None, max_col=None, values_only=True):
            rows = [("id", "name", "amount")]
            rows.extend((index, f"ODS Line {index}", index * 100) for index in range(16))
            return iter(rows)

    fake_openpyxl = SimpleNamespace(
        load_workbook=lambda _path, read_only, data_only: SimpleNamespace(worksheets=[FakeWorksheet()])
    )
    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setitem(sys.modules, "openpyxl", fake_openpyxl)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=8))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "libreoffice"
    assert result.metadata["source_extension"] == ".ods"
    assert result.metadata["converted_extension"] == ".xlsx"
    assert result.metadata["sample_first"]["truncated"] is True
    assert result.chunks[0].metadata["sample_first"] is True
    assert "ODS Line 15" not in result.chunks[0].body


def test_extractor_availability_reports_office_com_extractors():
    availability = extractor_availability()

    assert "excel_com" in availability
    assert "powerpoint_com" in availability
    assert all("ok" in availability[name] and "message" in availability[name] for name in ("excel_com", "powerpoint_com"))


def test_extract_drawio_indexes_plain_xml_structure(tmp_path):
    path = tmp_path / "architecture.drawio"
    path.write_text(
        """
        <mxfile>
          <diagram name="Architecture">
            <mxGraphModel>
              <root>
                <mxCell id="api" value="API Gateway" link="https://example.test/api" />
                <mxCell id="db" value="Database" />
                <mxCell id="edge" value="syncs to" edge="1" source="api" target="db" />
              </root>
            </mxGraphModel>
          </diagram>
        </mxfile>
        """,
        encoding="utf-8",
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "diagram"
    assert result.metadata["format"] == "drawio"
    assert result.metadata["page_count"] == 1
    assert result.metadata["shape_count"] == 2
    assert result.metadata["connector_count"] == 1
    assert result.metadata["text_count"] >= 3
    assert result.chunks[0].modality == "diagram"
    assert "Page: Architecture" in result.chunks[0].body
    assert "API Gateway" in result.chunks[0].body
    assert "syncs to" in result.chunks[0].body
    assert "https://example.test/api" in result.chunks[0].body


def test_extract_drawio_decodes_compressed_page_payload(tmp_path):
    path = tmp_path / "workflow.drawio"
    inner_xml = (
        '<mxGraphModel><root><mxCell id="start" value="Start Intake" />'
        '<mxCell id="finish" value="Approve Request" /></root></mxGraphModel>'
    )
    compressor = zlib.compressobj(level=9, wbits=-15)
    compressed = compressor.compress(quote(inner_xml).encode("utf-8")) + compressor.flush()
    encoded = base64.b64encode(compressed).decode("ascii")
    path.write_text(f'<mxfile><diagram name="Compressed">{encoded}</diagram></mxfile>', encoding="utf-8")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["format"] == "drawio"
    assert result.metadata["page_count"] == 1
    assert "Start Intake" in result.chunks[0].body
    assert "Approve Request" in result.chunks[0].body


def test_extract_embedded_drawio_svg_and_png_payloads(tmp_path):
    svg = tmp_path / "diagram.drawio.svg"
    svg.write_text(
        '<svg><metadata><mxfile><diagram name="SVG"><mxGraphModel><root>'
        '<mxCell id="shape" value="SVG Embedded Label" />'
        "</root></mxGraphModel></diagram></mxfile></metadata></svg>",
        encoding="utf-8",
    )
    png = tmp_path / "diagram.drawio.png"
    png.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + b'<mxfile><diagram name="PNG"><mxGraphModel><root>'
        + b'<mxCell id="shape" value="PNG Embedded Label" />'
        + b"</root></mxGraphModel></diagram></mxfile>"
    )

    svg_result = extract_file(svg, CorpusPolicy(root_path=tmp_path))
    png_result = extract_file(png, CorpusPolicy(root_path=tmp_path))

    assert svg_result.status == "indexed"
    assert png_result.status == "indexed"
    assert "SVG Embedded Label" in svg_result.chunks[0].body
    assert "PNG Embedded Label" in png_result.chunks[0].body


def test_extract_vsdx_indexes_page_text_from_zipped_xml(tmp_path):
    path = tmp_path / "process.vsdx"
    with ZipFile(path, "w") as archive:
        archive.writestr(
            "visio/pages/page1.xml",
            """
            <PageContents xmlns="http://schemas.microsoft.com/office/visio/2012/main">
              <Shapes>
                <Shape ID="1" NameU="Process">
                  <Text>Start Process</Text>
                </Shape>
                <Shape ID="2" NameU="Decision">
                  <Text>Approve Request</Text>
                </Shape>
              </Shapes>
              <Connects>
                <Connect FromSheet="1" ToSheet="2" />
              </Connects>
            </PageContents>
            """,
        )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "diagram"
    assert result.metadata["format"] == "vsdx"
    assert result.metadata["page_count"] == 1
    assert result.metadata["shape_count"] == 2
    assert result.metadata["connector_count"] == 1
    assert "Start Process" in result.chunks[0].body
    assert "Approve Request" in result.chunks[0].body


def test_extract_diagram_returns_metadata_only_when_no_text_exists(tmp_path):
    path = tmp_path / "empty.drawio"
    path.write_text('<mxfile><diagram name="Blank"><mxGraphModel><root /></mxGraphModel></diagram></mxfile>', encoding="utf-8")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.chunks == ()
    assert result.metadata["format"] == "drawio"
    assert result.metadata["page_count"] == 1


def test_extract_vsdx_rejects_unsafe_container_member(tmp_path):
    path = tmp_path / "unsafe.vsdx"
    with ZipFile(path, "w") as archive:
        archive.writestr("../evil.xml", "<xml />")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "failed"
    assert result.metadata["extractor"] == "diagram"
    assert result.metadata["format"] == "vsdx"
    assert "unsafe" in (result.message or "").lower()
