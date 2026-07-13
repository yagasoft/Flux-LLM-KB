import base64
from email.message import EmailMessage
import gzip
import importlib.machinery
import importlib.util
import json
import sqlite3
import subprocess
import sys
import tarfile
from io import BytesIO
from pathlib import Path
from types import ModuleType, SimpleNamespace
from urllib.error import HTTPError
from urllib.parse import quote
from zipfile import ZipFile
import zlib

import pytest

from flux_llm_kb import extractors, model_activity
from flux_llm_kb.crawler import AssetChunk, CorpusPolicy
from flux_llm_kb.extractors import (
    MEDIA_SEGMENT_CHUNK_INDEX_STRIDE,
    PDF_OCR_CHUNK_INDEX_BASE,
    VISION_TIMEOUT_SECONDS,
    extract_file,
    extract_media_segment,
    extract_pdf_ocr_pages,
    extractor_availability,
    plan_staged_media_extraction,
    plan_staged_pdf_extraction,
)


PNG_BYTES = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAIAAADZrBkAAAAAD0lEQVR4nGP8z8AARLJAgAEACPwD"
    "Aaz3RyoAAAAASUVORK5CYII="
)
ONE_PIXEL_PNG_BYTES = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMB/axjfkUAAAAASUVORK5CYII="
)


def _synthetic_api_key() -> str:
    return "sk-" + ("b" * 24)


def _disable_configured_model_runner(monkeypatch):
    monkeypatch.delenv("FLUX_KB_MODEL_RUNNER_BASE_URL", raising=False)
    monkeypatch.setattr("flux_llm_kb.model_runner.configured_model_runner_base_url", lambda: "")


def _zip_payload(entries: dict[str, str | bytes]) -> bytes:
    buffer = BytesIO()
    with ZipFile(buffer, "w") as archive:
        for name, data in entries.items():
            archive.writestr(name, data)
    return buffer.getvalue()


def test_extractor_path_keeps_vss_shadow_paths_unresolved(monkeypatch, tmp_path):
    shadow = Path(r"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7\Docs\open.txt")

    def fail_resolve(self):
        raise AssertionError(f"unexpected resolve for {self}")

    monkeypatch.setattr(Path, "resolve", fail_resolve)

    assert extractors._extractor_path(shadow) == shadow


def test_extractor_path_resolves_normal_paths(tmp_path):
    path = tmp_path / "normal.txt"
    path.write_text("body", encoding="utf-8")

    assert extractors._extractor_path(path) == path.resolve()


def test_extract_code_uses_payload_relative_path_for_shadow_sources(tmp_path):
    path = tmp_path / "shadow.py"
    path.write_text("def run():\n    return 1\n", encoding="utf-8")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path / "unrelated"), relative_path="src/app.py")

    assert result.status == "indexed"
    assert any(chunk.title == "src/app.py::run" for chunk in result.chunks)


def test_extract_file_reads_text_chunks(tmp_path):
    path = tmp_path / "decision.md"
    path.write_text("# Decision\nUse the unified dashboard for watcher health.", encoding="utf-8")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].body == "# Decision\nUse the unified dashboard for watcher health."
    assert result.metadata["extractor"] == "text"


def test_extract_file_decodes_utf16_bom_without_nul_chunks(tmp_path):
    path = tmp_path / "decision.txt"
    path.write_bytes("# Decision\nPreserve Arabic نص and tabs\twithout NUL bytes.".encode("utf-16"))

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert "\x00" not in result.chunks[0].body
    assert "Preserve Arabic نص" in result.chunks[0].body
    assert "\t" in result.chunks[0].body


def test_extract_file_treats_empty_text_as_completed_no_content(tmp_path):
    path = tmp_path / "body.txt"
    path.write_text("", encoding="utf-8")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks == ()
    assert result.metadata["extractor"] == "text"
    assert result.metadata["empty"] is True


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


def test_extract_msg_uses_msgconvert_to_index_converted_eml(monkeypatch, tmp_path):
    path = tmp_path / "attached.msg"
    path.write_bytes(b"fake msg")

    def fake_run(command, **_kwargs):
        assert command[0] == "C:/tools/msgconvert.exe"
        assert command[1] == "--outfile"
        out_path = Path(command[2])
        assert out_path.name == "attached.eml"
        assert command[3] == str(path)
        out_path.write_text(
            "Subject: Shared Folder\nFrom: sender@example.com\n\nPlease review the shared folder.",
            encoding="utf-8",
        )
        return SimpleNamespace(returncode=0, stdout="", stderr="")

    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: "C:/tools/msgconvert.exe" if command == "msgconvert" else None,
    )
    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["extractor"] == "mail"
    assert result.metadata["mail_format"] == "msg"
    assert result.metadata["converted_format"] == "eml"
    assert result.metadata["subjects"] == ["Shared Folder"]
    assert "Please review the shared folder." in result.chunks[0].body


def test_extract_msg_blocks_when_msgconvert_is_missing(monkeypatch, tmp_path):
    path = tmp_path / "attached.msg"
    path.write_bytes(b"fake msg")
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.metadata["extractor"] == "mail"
    assert result.metadata["mail_format"] == "msg"
    assert result.metadata["dependency"] == "msgconvert"


@pytest.mark.parametrize(("filename", "extractor"), [("large.txt", "text"), ("large.py", "code")])
def test_extract_large_text_and_code_blocks_by_policy(tmp_path, filename, extractor):
    path = tmp_path / filename
    path.write_text("print('large')\n" * 10, encoding="utf-8")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=8))

    assert result.status == "blocked_by_policy"
    assert result.message == "text file exceeds inline extraction limit"
    assert result.metadata["extractor"] == extractor
    assert result.metadata["reason"] == "inline_extraction_limit"
    assert result.metadata["max_inline_bytes"] == 8
    assert result.metadata["size_bytes"] == path.stat().st_size


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
    path = tmp_path / "clip.mp3"
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
    path = tmp_path / "clip.mp3"
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


def test_extract_media_runs_asr_when_duration_exceeds_legacy_cap(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp3"
    path.write_bytes(b"fake media")
    _configure_asr_http(monkeypatch, tmp_path, max_duration=10)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    calls = {"ffprobe": 0, "ffmpeg": 0, "http": 0}

    def fake_run(command, **_kwargs):
        if command[0].endswith("ffprobe.exe"):
            calls["ffprobe"] += 1
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=75), stderr="")
        if command[0].endswith("ffmpeg.exe"):
            calls["ffmpeg"] += 1
            Path(command[-1]).write_bytes(b"wav")
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"unexpected command: {command}")

    class FakeResponse:
        def read(self, _limit=-1):
            return b'{"text":"Long meeting transcript","segments":[{"start":0.0,"end":1.0,"text":"Long"}]}'

    def fake_urlopen(_request, **_kwargs):
        calls["http"] += 1
        return FakeResponse()

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].body == "Long meeting transcript"
    assert result.metadata["asr"]["status"] == "completed"
    assert result.metadata["asr"]["duration_seconds"] == 75.0
    assert result.metadata["asr"]["max_duration_seconds"] == 10
    assert calls == {"ffprobe": 1, "ffmpeg": 1, "http": 1}


def test_plan_staged_video_extraction_queues_asr_then_frames(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    _configure_asr_http(monkeypatch, tmp_path)
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_SAMPLING_ENABLED", "true")
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_SAMPLE_COUNT", "3")
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: "C:/tools/ffprobe.exe" if command == "ffprobe" else None)

    def fake_run(command, **_kwargs):
        assert command[0].endswith("ffprobe.exe")
        return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=1200), stderr="")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    result = plan_staged_media_extraction(path, "video")

    assert result.status == "staged"
    assert result.metadata["asr"]["status"] == "planned"
    assert result.metadata["asr"]["duration_seconds"] == 1200.0
    assert result.metadata["frame_sampling"]["status"] == "planned"
    assert result.metadata["frame_sampling"]["timestamps"] == [300.0, 600.0, 900.0]
    first_job = result.metadata["staged_jobs"][0]
    assert first_job["job_type"] == "corpus_extract_media_segment"
    assert first_job["payload"]["segment_duration_seconds"] == 900.0
    assert first_job["payload"]["followup_jobs"][0]["job_type"] == "corpus_extract_video_frames"
    assert result.metadata["staged_extraction"]["pending_job_count"] == 3


def test_extract_media_segment_queues_next_audio_chunk(monkeypatch, tmp_path):
    path = tmp_path / "meeting.mp3"
    path.write_bytes(b"fake media")
    _configure_asr_http(monkeypatch, tmp_path)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    calls = {"ffprobe": 0, "ffmpeg": 0, "http": 0}

    def fake_run(command, **_kwargs):
        if command[0].endswith("ffprobe.exe"):
            calls["ffprobe"] += 1
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=75), stderr="")
        if command[0].endswith("ffmpeg.exe"):
            calls["ffmpeg"] += 1
            assert "-ss" not in command
            assert command[command.index("-t") + 1] == "30.000"
            Path(command[-1]).write_bytes(b"wav")
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(command)

    class FakeResponse:
        def read(self, _limit=-1):
            return b'{"text":"Chunk one transcript","segments":[{"text":"Chunk one"}]}'

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", lambda *_args, **_kwargs: calls.__setitem__("http", calls["http"] + 1) or FakeResponse(), raising=False)

    result = extract_media_segment(
        path,
        {
            "file_kind": "audio",
            "segment_index": 0,
            "segment_start_seconds": 0,
            "segment_duration_seconds": 30,
            "duration_seconds": 75,
            "chunks_seen": 0,
        },
    )

    assert result.status == "staged"
    assert result.chunks[0].chunk_index == 0
    assert result.chunks[0].body == "Chunk one transcript"
    next_job = result.metadata["staged_extraction"]["next_job"]
    assert next_job["job_type"] == "corpus_extract_media_segment"
    assert next_job["payload"]["segment_index"] == 1
    assert next_job["payload"]["segment_start_seconds"] == 30.0
    assert next_job["payload"]["chunks_seen"] == 1
    assert calls == {"ffprobe": 1, "ffmpeg": 1, "http": 1}


def test_extract_media_runs_local_asr_and_reuses_redacted_cache(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp3"
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
                        text=f"Project recap mentions {_synthetic_api_key()}",
                    )
                ],
                SimpleNamespace(language="en"),
            )

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setitem(sys.modules, "faster_whisper", SimpleNamespace(WhisperModel=FakeWhisperModel, __spec__=fake_spec))

    first = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert first.status == "indexed"
    assert first.chunks[0].modality == "transcript"
    assert first.chunks[0].body == f"Project recap mentions {_synthetic_api_key()}"
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


def test_extract_media_passes_configured_gpu_settings_to_faster_whisper(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp3"
    path.write_bytes(b"fake media")
    model_path = _configure_asr(monkeypatch, tmp_path, device="cuda", compute_type="float16")
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    fake_spec = importlib.machinery.ModuleSpec("faster_whisper", loader=None)
    monkeypatch.setattr(
        "flux_llm_kb.extractors.importlib.util.find_spec",
        lambda name: fake_spec if name == "faster_whisper" else importlib.util.find_spec(name),
    )
    model_kwargs = {}

    def fake_run(command, **_kwargs):
        if command[0].endswith("ffprobe.exe"):
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=12), stderr="")
        if command[0].endswith("ffmpeg.exe"):
            Path(command[-1]).write_bytes(b"wav")
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"unexpected command: {command}")

    class FakeWhisperModel:
        def __init__(self, model_size_or_path, **kwargs):
            assert model_size_or_path == str(model_path)
            model_kwargs.update(kwargs)

        def transcribe(self, audio_path, **_kwargs):
            assert Path(audio_path).exists()
            return ([SimpleNamespace(text="GPU ASR text")], SimpleNamespace(language="en"))

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setitem(sys.modules, "faster_whisper", SimpleNamespace(WhisperModel=FakeWhisperModel, __spec__=fake_spec))

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["asr"]["device"] == "cuda"
    assert result.metadata["asr"]["compute_type"] == "float16"
    assert model_kwargs == {"local_files_only": True, "device": "cuda", "compute_type": "float16"}


def test_extract_media_uses_openai_compatible_asr_provider(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp3"
    path.write_bytes(b"fake media")
    _configure_asr_http(monkeypatch, tmp_path)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    calls = {"ffprobe": 0, "ffmpeg": 0, "http": 0}

    def fake_run(command, **_kwargs):
        if command[0].endswith("ffprobe.exe"):
            calls["ffprobe"] += 1
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=12), stderr="")
        if command[0].endswith("ffmpeg.exe"):
            calls["ffmpeg"] += 1
            Path(command[-1]).write_bytes(b"wav")
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"unexpected command: {command}")

    class FakeResponse:
        def read(self, _limit=-1):
            return (
                '{"text":"Meeting mentions '
                + _synthetic_api_key()
                + '","segments":[{"start":0.0,"end":1.0,"text":"Meeting"}]}'
            ).encode("utf-8")

    def fake_urlopen(request, **_kwargs):
        calls["http"] += 1
        assert request.full_url == "http://127.0.0.1:8788/v1/audio/transcriptions"
        assert request.headers["Content-type"].startswith("multipart/form-data; boundary=")
        body = request.data
        assert b'name="model"' in body
        assert b"large-v3-turbo" in body
        assert b'name="file"; filename="audio.wav"' in body
        return FakeResponse()

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].modality == "transcript"
    assert result.chunks[0].body == f"Meeting mentions {_synthetic_api_key()}"
    assert result.metadata["asr"]["engine"] == "openai_compatible_asr"
    assert result.metadata["asr"]["provider"] == "openai_compatible"
    assert result.metadata["asr"]["model"] == "large-v3-turbo"
    assert result.metadata["asr"]["base_url"] == "http://127.0.0.1:8788"
    assert result.metadata["asr"]["status"] == "completed"
    assert result.metadata["asr"]["cache_hits"] == 0
    assert result.metadata["asr"]["cache_misses"] == 1
    assert result.metadata["asr"]["segments"] == 1
    assert calls == {"ffprobe": 1, "ffmpeg": 1, "http": 1}


def test_extract_media_openai_compatible_asr_blocks_when_service_unavailable(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp3"
    path.write_bytes(b"fake media")
    _configure_asr_http(monkeypatch, tmp_path)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")

    def fake_run(command, **_kwargs):
        if command[0].endswith("ffprobe.exe"):
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=12), stderr="")
        if command[0].endswith("ffmpeg.exe"):
            Path(command[-1]).write_bytes(b"wav")
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"unexpected command: {command}")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", lambda *_args, **_kwargs: (_ for _ in ()).throw(OSError("connection refused")), raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "ASR service unavailable: connection refused"
    assert result.metadata["asr"]["status"] == "blocked_missing_dependency"


def test_extract_media_openai_compatible_asr_raises_model_runner_busy_for_scheduler_busy(monkeypatch, tmp_path):
    from flux_llm_kb.model_runner import ModelRunnerBusy

    path = tmp_path / "clip.mp3"
    path.write_bytes(b"fake media")
    _configure_asr_http(monkeypatch, tmp_path)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    _install_fake_openai_asr_media_commands(monkeypatch)

    def fake_urlopen(_request, **_kwargs):
        raise _http_error(
            429,
            {
                "detail": {
                    "code": "gpu.scheduler_busy",
                    "message": "GPU scheduler busy",
                    "retry_after_seconds": 7,
                }
            },
        )

    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    with pytest.raises(ModelRunnerBusy, match="GPU scheduler busy") as exc_info:
        extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert exc_info.value.retry_after_seconds == 7


def test_extract_media_openai_compatible_asr_503_without_scheduler_busy_blocks(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp3"
    path.write_bytes(b"fake media")
    _configure_asr_http(monkeypatch, tmp_path)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    _install_fake_openai_asr_media_commands(monkeypatch)

    def fake_urlopen(_request, **_kwargs):
        raise _http_error(503, {"detail": "ASR warming up"})

    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "ASR service unavailable: ASR warming up"
    assert result.metadata["asr"]["status"] == "blocked_missing_dependency"


def test_extract_media_openai_compatible_asr_generic_http_error_fails(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp3"
    path.write_bytes(b"fake media")
    _configure_asr_http(monkeypatch, tmp_path)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    _install_fake_openai_asr_media_commands(monkeypatch)

    def fake_urlopen(_request, **_kwargs):
        raise _http_error(500, {"detail": "decoder crashed"})

    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "failed"
    assert result.message == "decoder crashed"
    assert result.metadata["asr"]["status"] == "failed"


def test_extract_media_treats_video_without_audio_as_metadata_only(monkeypatch, tmp_path):
    path = tmp_path / "silent.mp4"
    path.write_bytes(b"fake media")
    _configure_asr_http(monkeypatch, tmp_path)
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_SAMPLING_ENABLED", "false")
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    calls = {"ffprobe": 0, "ffmpeg": 0, "http": 0}

    def fake_run(command, **_kwargs):
        if command[0].endswith("ffprobe.exe"):
            calls["ffprobe"] += 1
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=60, has_audio=False), stderr="")
        if command[0].endswith("ffmpeg.exe"):
            calls["ffmpeg"] += 1
            return SimpleNamespace(
                returncode=1,
                stdout="",
                stderr=(
                    "Output #0, wav, to 'audio.wav':\n"
                    "[out#0/wav @ 000001] Output file does not contain any stream\n"
                    "Error opening output files: Invalid argument"
                ),
            )
        raise AssertionError(f"unexpected command: {command}")

    def fake_urlopen(*_args, **_kwargs):
        calls["http"] += 1
        raise AssertionError("ASR service should not be called for video without audio")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.message == "media has no audio stream for ASR"
    assert result.metadata["asr"]["status"] == "skipped_no_audio_stream"
    assert result.metadata["asr"]["has_audio_stream"] is False
    assert result.metadata["asr"]["cache_hits"] == 0
    assert result.metadata["asr"]["cache_misses"] == 0
    assert calls == {"ffprobe": 1, "ffmpeg": 0, "http": 0}


def test_extract_media_asr_cache_key_changes_with_provider_model(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp3"
    path.write_bytes(b"fake media")
    _configure_asr_http(monkeypatch, tmp_path, model="large-v3-turbo")
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    calls = {"ffprobe": 0, "ffmpeg": 0, "http": 0}

    def fake_run(command, **_kwargs):
        if command[0].endswith("ffprobe.exe"):
            calls["ffprobe"] += 1
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=12), stderr="")
        if command[0].endswith("ffmpeg.exe"):
            calls["ffmpeg"] += 1
            Path(command[-1]).write_bytes(b"wav")
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"unexpected command: {command}")

    class FakeResponse:
        def __init__(self, index: int):
            self.index = index

        def read(self, _limit=-1):
            return json.dumps({"text": f"Transcript {self.index}", "segments": [{"text": f"Transcript {self.index}"}]}).encode("utf-8")

    def fake_urlopen(_request, **_kwargs):
        calls["http"] += 1
        return FakeResponse(calls["http"])

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    first = extract_file(path, CorpusPolicy(root_path=tmp_path))
    second = extract_file(path, CorpusPolicy(root_path=tmp_path))
    monkeypatch.setenv("FLUX_KB_ASR_MODEL", "large-v3-turbo-alt")
    third = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert first.chunks[0].body == "Transcript 1"
    assert second.chunks[0].body == "Transcript 1"
    assert second.metadata["asr"]["status"] == "cache_hit"
    assert third.chunks[0].body == "Transcript 2"
    assert third.metadata["asr"]["status"] == "completed"
    assert calls == {"ffprobe": 3, "ffmpeg": 2, "http": 2}


def _configure_asr(
    monkeypatch,
    tmp_path: Path,
    *,
    enabled: bool = True,
    model_path: str | None = None,
    max_duration: int = 3600,
    device: str = "auto",
    compute_type: str = "default",
) -> Path:
    model_dir = tmp_path / "models" / "faster-whisper-tiny"
    if model_path is None:
        model_dir.mkdir(parents=True)
        model_path = str(model_dir)
    monkeypatch.setenv("FLUX_KB_ASR_ENABLED", "true" if enabled else "false")
    monkeypatch.setenv("FLUX_KB_ASR_PROVIDER", "local_faster_whisper")
    monkeypatch.setenv("FLUX_KB_ASR_MODEL", "")
    monkeypatch.setenv("FLUX_KB_ASR_BASE_URL", "")
    monkeypatch.setenv("FLUX_KB_ASR_MODEL_PATH", model_path)
    monkeypatch.setenv("FLUX_KB_ASR_MAX_DURATION_SECONDS", str(max_duration))
    monkeypatch.setenv("FLUX_KB_ASR_DEVICE", device)
    monkeypatch.setenv("FLUX_KB_ASR_COMPUTE_TYPE", compute_type)
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    return model_dir


def _configure_asr_http(
    monkeypatch,
    tmp_path: Path,
    *,
    enabled: bool = True,
    model: str = "large-v3-turbo",
    base_url: str = "http://127.0.0.1:8788",
    max_duration: int = 3600,
    device: str = "cuda",
    compute_type: str = "float16",
) -> None:
    monkeypatch.setenv("FLUX_KB_ASR_ENABLED", "true" if enabled else "false")
    monkeypatch.setenv("FLUX_KB_ASR_PROVIDER", "openai_compatible")
    monkeypatch.setenv("FLUX_KB_ASR_MODEL", model)
    monkeypatch.setenv("FLUX_KB_ASR_BASE_URL", base_url)
    monkeypatch.setenv("FLUX_KB_ASR_MODEL_PATH", "")
    monkeypatch.setenv("FLUX_KB_ASR_MAX_DURATION_SECONDS", str(max_duration))
    monkeypatch.setenv("FLUX_KB_ASR_DEVICE", device)
    monkeypatch.setenv("FLUX_KB_ASR_COMPUTE_TYPE", compute_type)
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))


def _install_fake_openai_asr_media_commands(monkeypatch, *, duration: float = 12.0) -> None:
    def fake_run(command, **_kwargs):
        if command[0].endswith("ffprobe.exe"):
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=duration), stderr="")
        if command[0].endswith("ffmpeg.exe"):
            Path(command[-1]).write_bytes(b"wav")
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"unexpected command: {command}")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)


def _http_error(status_code: int, payload: dict[str, object] | str) -> HTTPError:
    raw = json.dumps(payload).encode("utf-8") if isinstance(payload, dict) else str(payload).encode("utf-8")
    return HTTPError("http://127.0.0.1:8788/v1/audio/transcriptions", status_code, "error", {}, BytesIO(raw))


def _configure_vision(
    monkeypatch,
    tmp_path: Path,
    *,
    enabled: bool = True,
    model: str = "llava:latest",
    provider: str = "ollama",
    max_pixels: int = 4_096_000,
    base_url: str = "http://127.0.0.1:11434",
    keep_alive: str = "",
) -> None:
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "true" if enabled else "false")
    monkeypatch.setenv("FLUX_KB_VISION_MODEL", model)
    monkeypatch.setenv("FLUX_KB_VISION_MAX_IMAGE_PIXELS", str(max_pixels))
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_ENABLED", "true")
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_PROVIDER", provider)
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_BASE_URL", base_url)
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_KEEP_ALIVE", keep_alive)
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_PROBE_TIMEOUT_SECONDS", "1")


def _configure_frame_sampling(
    monkeypatch,
    tmp_path: Path,
    *,
    enabled: bool = True,
    count: int = 3,
    threshold: float = 0.35,
    max_duration: int = 1800,
    vision_enabled: bool = False,
) -> None:
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setenv("FLUX_KB_ASR_ENABLED", "false")
    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "true" if vision_enabled else "false")
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_ENABLED", "true" if vision_enabled else "false")
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_PROVIDER", "ollama")
    monkeypatch.setenv("FLUX_KB_LOCAL_INFERENCE_BASE_URL", "http://127.0.0.1:11434")
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_SAMPLING_ENABLED", "true" if enabled else "false")
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_SAMPLE_COUNT", str(count))
    monkeypatch.setenv("FLUX_KB_VIDEO_SCENE_THRESHOLD", str(threshold))
    monkeypatch.setenv("FLUX_KB_VIDEO_FRAME_MAX_DURATION_SECONDS", str(max_duration))


def _media_probe_json(*, duration: int | float, has_audio: bool = True) -> str:
    streams = '[{"codec_type":"audio","codec_name":"aac"}]' if has_audio else '[{"codec_type":"video","codec_name":"h264"}]'
    return f'{{"format":{{"duration":"{duration}"}},"streams":{streams}}}'


def test_extract_image_blocks_when_paddleocr_is_missing(monkeypatch, tmp_path):
    path = tmp_path / "scan.png"
    path.write_bytes(PNG_BYTES)
    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "false")
    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_image",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(ModuleNotFoundError("paddleocr")),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "paddleocr module not installed"
    assert result.metadata["ocr"]["status"] == "blocked_missing_dependency"
    assert result.metadata["ocr"]["engine"] == "paddleocr"
    assert result.metadata["ocr"]["cache_hits"] == 0
    assert result.metadata["ocr"]["cache_misses"] == 1


def test_extract_paddleocr_vl_dependency_error_blocks_missing_dependency(monkeypatch, tmp_path):
    path = tmp_path / "scan-document.png"
    path.write_bytes(PNG_BYTES)
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "false")

    class DependencyError(Exception):
        pass

    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_document",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(
            DependencyError(
                "`PaddleOCR-VL` requires additional dependencies for E:/Docs/report.pdf. "
                'Run pip install "paddlex[ocr]".'
            )
        ),
    )

    result = extractors._ocr_image_with_paddleocr(path, model="PaddleOCR-VL")

    assert result.status == "blocked_missing_dependency"
    assert result.metadata["status"] == "blocked_missing_dependency"
    assert result.metadata["model"] == "PaddleOCR-VL"
    assert "PaddleOCR-VL" in str(result.message)
    assert "E:/Docs/report.pdf" in str(result.message)


def test_run_paddleocr_image_configures_onnxruntime_before_importing_paddleocr(monkeypatch, tmp_path):
    path = tmp_path / "scan.png"
    path.write_bytes(PNG_BYTES)
    events: list[str] = []

    class FakePaddleOCR:
        def __init__(self, **_kwargs):
            events.append("paddleocr-init")

        def predict(self, _path):
            return [{"text": "legacy OCR text"}]

    class FakePaddleOCRModule(ModuleType):
        def __getattr__(self, name):
            if name == "PaddleOCR":
                events.append("import-paddleocr")
                return FakePaddleOCR
            raise AttributeError(name)

    _disable_configured_model_runner(monkeypatch)
    monkeypatch.setattr(extractors, "configure_onnxruntime_logging", lambda: events.append("configure-ort"), raising=False)
    monkeypatch.setitem(sys.modules, "paddleocr", FakePaddleOCRModule("paddleocr"))

    text = extractors._run_paddleocr_image(path, model="PP-OCRv5")

    assert text == "legacy OCR text"
    assert events[:2] == ["configure-ort", "import-paddleocr"]


def test_direct_worker_paddleocr_image_records_safe_activity(monkeypatch, tmp_path):
    path = tmp_path / "private-screenshot.png"
    path.write_bytes(PNG_BYTES)
    records: list[dict[str, object]] = []

    class FakeRecorder:
        def __init__(self, **kwargs):
            records.append(kwargs)

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeLease:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeScheduler:
        def acquire(self, profile):
            assert profile.task_type == "ocr_image"
            assert profile.component == "worker"
            return FakeLease()

    class FakePaddleOCR:
        def __init__(self, **_kwargs):
            pass

        def predict(self, _path):
            return [{"text": "worker OCR text"}]

    _disable_configured_model_runner(monkeypatch)
    monkeypatch.setattr(extractors, "record_model_activity", lambda **kwargs: FakeRecorder(**kwargs), raising=False)
    monkeypatch.setattr("flux_llm_kb.gpu_scheduler.get_gpu_scheduler", lambda: FakeScheduler())
    monkeypatch.setitem(sys.modules, "paddleocr", SimpleNamespace(PaddleOCR=FakePaddleOCR))

    text = extractors._run_paddleocr_image(path, model="PP-OCRv5")

    assert text == "worker OCR text"
    assert records == [
        {
            "service": "worker",
            "endpoint": "/v1/ocr/image",
            "action": "ocr_image",
            "activity_class": "vision_ocr",
            "caller_surface": "worker",
            "model": "PP-OCRv5",
            "metadata": {"document": False},
        }
    ]
    assert str(path) not in str(records)


def test_direct_worker_paddleocr_vl_document_records_safe_activity(monkeypatch, tmp_path):
    path = tmp_path / "private-document.png"
    path.write_bytes(PNG_BYTES)
    records: list[dict[str, object]] = []

    class FakeRecorder:
        def __init__(self, **kwargs):
            records.append(kwargs)

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    _disable_configured_model_runner(monkeypatch)
    monkeypatch.setattr("flux_llm_kb.model_runner._ocr_document_with_paddle", lambda _path, **_kwargs: "document OCR text")
    monkeypatch.setattr(extractors, "record_model_activity", lambda **kwargs: FakeRecorder(**kwargs), raising=False)

    text = extractors._run_paddleocr_document(path, model="PaddleOCR-VL")

    assert text == "document OCR text"
    assert records == [
        {
            "service": "worker",
            "endpoint": "/v1/ocr/document",
            "action": "ocr_document",
            "activity_class": "vision_ocr",
            "caller_surface": "worker",
            "model": "PaddleOCR-VL",
            "metadata": {"document": True},
        }
    ]
    assert str(path) not in str(records)


def test_worker_paddleocr_vl_document_uses_configured_model_runner_url(monkeypatch, tmp_path):
    from flux_llm_kb import settings

    path = tmp_path / "private-document.png"
    path.write_bytes(PNG_BYTES)
    calls: list[dict[str, object]] = []

    class FakeSettingsService:
        def resolve(self, key):
            assert key == "model_runner.base_url"
            return SimpleNamespace(raw_value="http://configured-model-runner:8790", source="db")

    class FakeModelRunnerClient:
        def __init__(self, base_url=None, **_kwargs):
            calls.append({"base_url": base_url})

        def ocr_file(self, input_path, *, model, document=False, timeout_seconds=None):
            calls.append({"path": Path(input_path).name, "model": model, "document": document, "timeout_seconds": timeout_seconds})
            return {"ok": True, "text": "remote document OCR"}

    monkeypatch.delenv("FLUX_KB_MODEL_RUNNER_BASE_URL", raising=False)
    monkeypatch.setattr(settings, "SettingsService", FakeSettingsService)
    monkeypatch.setattr("flux_llm_kb.model_runner.ModelRunnerClient", FakeModelRunnerClient)
    monkeypatch.setattr(
        "flux_llm_kb.model_runner._ocr_document_with_paddle",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("local PaddleOCR-VL must not run")),
    )

    text = extractors._run_paddleocr_document(path, model="PaddleOCR-VL")

    assert text == "remote document OCR"
    assert calls == [
        {"base_url": "http://configured-model-runner:8790"},
        {"path": "private-document.png", "model": "PaddleOCR-VL", "document": True, "timeout_seconds": 1.0},
    ]


def test_worker_paddleocr_vl_document_ignores_unconfigured_default_model_runner_url(monkeypatch, tmp_path):
    from flux_llm_kb import settings

    path = tmp_path / "private-document.png"
    path.write_bytes(PNG_BYTES)

    class FakeSettingsService:
        def resolve(self, key):
            assert key == "model_runner.base_url"
            return SimpleNamespace(raw_value="http://127.0.0.1:8790", source="default")

    class FakeModelRunnerClient:
        def __init__(self, *_args, **_kwargs):
            raise AssertionError("unconfigured default model-runner URL must not be used")

    monkeypatch.delenv("FLUX_KB_MODEL_RUNNER_BASE_URL", raising=False)
    monkeypatch.setattr(settings, "SettingsService", FakeSettingsService)
    monkeypatch.setattr("flux_llm_kb.model_runner.ModelRunnerClient", FakeModelRunnerClient)
    monkeypatch.setattr(
        "flux_llm_kb.model_runner._ocr_document_with_paddle",
        lambda input_path, *, model, **_kwargs: f"local OCR {Path(input_path).name} {model}",
    )

    text = extractors._run_paddleocr_document(path, model="PaddleOCR-VL")

    assert text == "local OCR private-document.png PaddleOCR-VL"


def test_pdf_ocr_pages_routes_paddleocr_vl_through_document_pipeline(monkeypatch, tmp_path):
    path = tmp_path / "scan.pdf"
    path.write_bytes(b"%PDF scanned")
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    _disable_configured_model_runner(monkeypatch)
    monkeypatch.delenv("FLUX_KB_PADDLE_RUNNER_BASE_URL", raising=False)
    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: {"pdftoppm": "C:/tools/pdftoppm.exe"}.get(command),
    )

    def fake_run(command, **_kwargs):
        assert command[0] == "C:/tools/pdftoppm.exe"
        output_prefix = Path(command[-1])
        output_prefix.with_name(f"{output_prefix.name}-1.png").write_bytes(PNG_BYTES)
        return SimpleNamespace(returncode=0, stdout="", stderr="")

    simple_ocr_kwargs: list[dict[str, object]] = []
    document_pipeline_kwargs: list[dict[str, object]] = []

    class FakePaddleOCR:
        def __init__(self, **kwargs):
            simple_ocr_kwargs.append(kwargs)
            if kwargs.get("ocr_version") == "PaddleOCR-VL":
                raise AssertionError("PaddleOCR-VL must not be passed as a PaddleOCR ocr_version")

    class FakeDocumentPipeline:
        def predict(self, image_path):
            assert Path(image_path).name == "page-1.png"
            return [{"content": "PaddleOCR VL page text"}]

    def fake_create_pipeline(**kwargs):
        document_pipeline_kwargs.append(kwargs)
        return FakeDocumentPipeline()

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setitem(sys.modules, "paddleocr", SimpleNamespace(PaddleOCR=FakePaddleOCR))
    monkeypatch.setitem(sys.modules, "paddlex", SimpleNamespace(create_pipeline=fake_create_pipeline))

    result = extractors._ocr_pdf_pages(path, page_start=1, page_end=1, page_count=1, page_numbers=[1])

    assert result.status == "completed"
    assert result.text == "PaddleOCR VL page text"
    assert simple_ocr_kwargs == []
    assert document_pipeline_kwargs[0]["pipeline"] == "PaddleOCR-VL"


def test_extract_svg_embedded_text_without_ocr_or_vision(monkeypatch, tmp_path):
    path = tmp_path / "workflow.svg"
    path.write_text(
        """<svg xmlns="http://www.w3.org/2000/svg" width="400" height="120" viewBox="0 0 400 120">
  <title>Admissions workflow</title>
  <desc>Applicant request lifecycle</desc>
  <text x="10" y="20">Submit application</text>
  <text x="10" y="50"><tspan>Review documents</tspan></text>
</svg>""",
        encoding="utf-8",
    )
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)
    monkeypatch.setattr(
        "flux_llm_kb.extractors.run_no_window",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("embedded SVG text should not run OCR tools")),
    )
    monkeypatch.setattr(
        "flux_llm_kb.extractors.urlopen",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("embedded SVG text should not call vision")),
        raising=False,
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert [chunk.modality for chunk in result.chunks] == ["svg_text"]
    assert "Admissions workflow" in result.chunks[0].body
    assert "Applicant request lifecycle" in result.chunks[0].body
    assert "Submit application" in result.chunks[0].body
    assert "Review documents" in result.chunks[0].body
    assert result.metadata["extractor"] == "image"
    assert result.metadata["svg"]["kind"] == "text"
    assert result.metadata["svg_parse"]["status"] == "completed"
    assert "ocr" not in result.metadata
    assert "vision" not in result.metadata


def test_extract_svg_font_completes_metadata_only_without_ocr_or_vision(monkeypatch, tmp_path):
    path = tmp_path / "glyphicons-halflings-regular.svg"
    path.write_text(
        """<?xml version="1.0" standalone="no"?>
<svg xmlns="http://www.w3.org/2000/svg">
  <defs>
    <font id="Glyphicons" horiz-adv-x="1200">
      <font-face font-family="Glyphicons Halflings" units-per-em="1200"/>
      <glyph unicode="&#xe001;" glyph-name="glass" d="M10 20"/>
      <glyph unicode="&#xe002;" glyph-name="music" d="M30 40"/>
    </font>
  </defs>
</svg>""",
        encoding="utf-8",
    )
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)
    monkeypatch.setattr(
        "flux_llm_kb.extractors.run_no_window",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("SVG font should not run OCR tools")),
    )
    monkeypatch.setattr(
        "flux_llm_kb.extractors.urlopen",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("SVG font should not call vision")),
        raising=False,
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.chunks == ()
    assert result.metadata["extractor"] == "image"
    assert result.metadata["svg"]["kind"] == "font"
    assert result.metadata["svg"]["glyph_count"] == 2
    assert result.metadata["svg"]["font_families"] == ["Glyphicons Halflings"]
    assert result.metadata["svg_parse"]["status"] == "completed"
    assert "ocr" not in result.metadata
    assert "vision" not in result.metadata


def test_extract_visual_svg_renders_png_before_ocr_and_vision(monkeypatch, tmp_path):
    path = tmp_path / "logo.svg"
    path.write_text(
        """<svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
  <path d="M0 0h120v80H0z"/>
</svg>""",
        encoding="utf-8",
    )
    _configure_vision(monkeypatch, tmp_path)
    commands = []

    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: {
            "rsvg-convert": "C:/tools/rsvg-convert.exe",
        }.get(command),
    )

    def fake_run(command, **_kwargs):
        commands.append(command)
        if command[0] == "C:/tools/rsvg-convert.exe":
            output_path = Path(command[command.index("--output") + 1])
            output_path.write_bytes(PNG_BYTES)
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"unexpected command: {command}")

    class FakeResponse:
        def read(self, _limit=-1):
            return b'{"response":"Rendered SVG logo with a dark rectangular mark."}'

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_image", lambda image_path, **_kwargs: "")
    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", lambda *_args, **_kwargs: FakeResponse(), raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].modality == "vision"
    assert result.chunks[0].body == "Rendered SVG logo with a dark rectangular mark."
    assert result.metadata["svg"]["kind"] == "visual"
    assert result.metadata["svg_raster"]["status"] == "completed"
    assert result.metadata["svg_raster"]["renderer"] == "rsvg-convert"
    assert result.metadata["ocr"]["status"] == "completed"
    assert result.metadata["ocr"]["engine"] == "paddleocr"
    assert result.metadata["vision"]["status"] == "completed"
    assert [Path(command[0]).name for command in commands] == ["rsvg-convert.exe"]


def test_extract_visual_svg_blocks_when_renderer_is_missing(monkeypatch, tmp_path):
    path = tmp_path / "shape.svg"
    path.write_text(
        """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
  <circle cx="50" cy="50" r="40"/>
</svg>""",
        encoding="utf-8",
    )
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda _command: None)
    monkeypatch.setattr(
        "flux_llm_kb.extractors.run_no_window",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("missing SVG renderer must block before OCR")),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "SVG renderer command not found"
    assert result.metadata["svg"]["kind"] == "visual"
    assert result.metadata["svg_parse"]["status"] == "completed"
    assert result.metadata["svg_raster"]["status"] == "blocked_missing_dependency"
    assert "ocr" not in result.metadata


def test_extract_svg_size_limit_blocks_by_policy(monkeypatch, tmp_path):
    path = tmp_path / "oversized.svg"
    path.write_text('<svg xmlns="http://www.w3.org/2000/svg"><text>Too large</text></svg>', encoding="utf-8")
    monkeypatch.setattr(extractors, "SVG_MAX_BYTES", 12)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_by_policy"
    assert result.message == "SVG file exceeds readable size limit"
    assert result.metadata["svg_parse"]["status"] == "blocked_by_policy"
    assert result.metadata["svg_parse"]["reason"] == "svg_size_limit"
    assert "ocr" not in result.metadata


def test_extract_malformed_svg_blocks_invalid_source(monkeypatch, tmp_path):
    path = tmp_path / "broken.svg"
    path.write_bytes(b"\x00not svg xml")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_invalid_source"
    assert result.metadata["svg_parse"]["status"] == "blocked_invalid_source"
    assert result.metadata["svg_parse"]["reason"] == "invalid_svg_xml"
    assert "SVG XML parse failed" in (result.message or "")
    assert "ocr" not in result.metadata


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

    assert result.status == "indexed"
    assert result.chunks == ()
    assert result.metadata["decorative"] == {"status": "skipped", "reason": "tiny_spacer"}
    assert "ocr" not in result.metadata
    assert "vision" not in result.metadata


def test_extract_image_skips_small_inline_icons_before_ocr_or_vision(monkeypatch, tmp_path):
    from PIL import Image

    path = tmp_path / "inline-icon.png"
    Image.new("RGBA", (20, 20), (0, 0, 0, 0)).save(path)
    assert path.stat().st_size <= 4096
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

    assert result.status == "indexed"
    assert result.chunks == ()
    assert result.metadata["decorative"] == {"status": "skipped", "reason": "small_icon"}
    assert "ocr" not in result.metadata
    assert "vision" not in result.metadata


def test_extract_image_uses_local_vision_when_ocr_is_missing_and_reuses_cache(monkeypatch, tmp_path):
    path = tmp_path / "diagram.png"
    path.write_bytes(PNG_BYTES)
    _configure_vision(monkeypatch, tmp_path)
    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_image",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(ModuleNotFoundError("paddleocr")),
    )
    calls = {"vision": 0}

    class FakeResponse:
        def read(self, _limit=-1):
            return ('{"response":"Diagram mentions ' + _synthetic_api_key() + ' and system context"}').encode("utf-8")

    def fake_urlopen(request, **_kwargs):
        calls["vision"] += 1
        assert request.full_url == "http://127.0.0.1:11434/api/generate"
        payload = json.loads(request.data.decode("utf-8"))
        assert "keep_alive" not in payload
        return FakeResponse()

    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    first = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert first.status == "indexed"
    assert first.chunks[0].modality == "vision"
    assert first.chunks[0].body == f"Diagram mentions {_synthetic_api_key()} and system context"
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


def test_extract_image_sends_configured_local_inference_keep_alive(monkeypatch, tmp_path):
    path = tmp_path / "diagram.png"
    path.write_bytes(PNG_BYTES)
    _configure_vision(monkeypatch, tmp_path, keep_alive="2m")
    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_image",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(ModuleNotFoundError("paddleocr")),
    )

    class FakeResponse:
        def read(self, _limit=-1):
            return b'{"response":"Diagram shows a workflow."}'

    def fake_urlopen(request, **_kwargs):
        payload = json.loads(request.data.decode("utf-8"))
        assert payload["keep_alive"] == "2m"
        assert _kwargs["timeout"] == VISION_TIMEOUT_SECONDS
        return FakeResponse()

    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["vision"]["status"] == "completed"


def test_extract_image_uses_qwen_thinking_when_ollama_response_is_empty(monkeypatch, tmp_path):
    path = tmp_path / "diagram.png"
    path.write_bytes(PNG_BYTES)
    _configure_vision(monkeypatch, tmp_path, keep_alive="2m")
    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_image",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(ModuleNotFoundError("paddleocr")),
    )

    class FakeResponse:
        def read(self, _limit=-1):
            return json.dumps(
                {
                    "response": "",
                    "thinking": (
                        "<think>Got it, let's break down the user's request. "
                        "They want a description for a private local knowledge index. "
                        "The image is an ER diagram titled Project Data. "
                        "Need to be concise and factual.</think>"
                    ),
                    "done_reason": "length",
                }
            ).encode("utf-8")

    def fake_urlopen(request, **_kwargs):
        payload = json.loads(request.data.decode("utf-8"))
        assert payload["options"]["num_predict"] >= 1024
        return FakeResponse()

    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].modality == "vision"
    assert result.chunks[0].body == "The image is an ER diagram titled Project Data."
    assert result.metadata["vision"]["status"] == "completed"
    assert result.metadata["vision"]["descriptions"] == 1
    assert result.metadata["vision"]["fallback_field"] == "thinking"


def test_ollama_vision_records_safe_model_activity(monkeypatch, tmp_path):
    from flux_llm_kb import extractors

    image = tmp_path / "vision.png"
    image.write_bytes(PNG_BYTES)
    records: list[dict[str, object]] = []

    class FakeRecorder:
        def __init__(self, **kwargs):
            records.append(kwargs)

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeLease:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeScheduler:
        def acquire(self, _profile):
            return FakeLease()

        def record_model_residency(self, _residency):
            return None

    class FakeResponse:
        def read(self, _limit=-1):
            return b'{"response": "diagram summary"}'

    monkeypatch.setattr(extractors, "_vision_request_image_bytes", lambda _path: (b"image-bytes", {"original_size_bytes": 10}))
    monkeypatch.setattr(extractors, "urlopen", lambda *_args, **_kwargs: FakeResponse())
    monkeypatch.setattr("flux_llm_kb.gpu_scheduler.get_gpu_scheduler", lambda: FakeScheduler())
    monkeypatch.setattr(extractors, "record_model_activity", lambda **kwargs: FakeRecorder(**kwargs), raising=False)

    result = extractors._vision_with_ollama_compatible(
        image,
        source_label="private-image.png",
        provider="ollama",
        base_url="http://ollama:11434",
        model="qwen3-vl:8b",
        keep_alive="10m",
        timeout_seconds=1,
        metadata={"provider": "ollama", "model": "qwen3-vl:8b"},
    )

    assert result.status == "completed"
    assert records == [
        {
            "service": "ollama",
            "endpoint": "/api/generate",
            "action": "vision_generate",
            "activity_class": "vision_ocr",
            "caller_surface": "worker",
            "model": "qwen3-vl:8b",
            "metadata": {"keep_alive": True},
        }
    ]
    assert "private-image.png" not in str(records)
    assert "image-bytes" not in str(records)


def test_ollama_vision_records_http_error_body_without_payload_leak(monkeypatch, tmp_path):
    image = tmp_path / "vision.png"
    image.write_bytes(PNG_BYTES)
    finished: list[dict[str, object]] = []

    class FakeLease:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeScheduler:
        def acquire(self, _profile):
            return FakeLease()

        def record_model_residency(self, _residency):
            return None

    body = b'{"error":"failed to decode image/media: ffprobe failed on buffer"}'

    def fake_urlopen(_request, **_kwargs):
        raise HTTPError(
            url="http://ollama:11434/api/generate",
            code=400,
            msg="Bad Request",
            hdrs={},
            fp=BytesIO(body),
        )

    monkeypatch.setattr(extractors, "_vision_request_image_bytes", lambda _path: (b"image-bytes", {"submitted_bytes": 10}))
    monkeypatch.setattr(extractors, "urlopen", fake_urlopen, raising=False)
    monkeypatch.setattr("flux_llm_kb.gpu_scheduler.get_gpu_scheduler", lambda: FakeScheduler())
    monkeypatch.setattr(model_activity.database, "start_model_activity_event", lambda **_kwargs: "event-vision", raising=False)
    monkeypatch.setattr(model_activity.database, "finish_model_activity_event", lambda **kwargs: finished.append(kwargs), raising=False)

    result = extractors._vision_with_ollama_compatible(
        image,
        source_label="private-image.png",
        provider="ollama",
        base_url="http://ollama:11434",
        model="qwen3-vl:8b",
        keep_alive="2m",
        timeout_seconds=1,
        metadata={"provider": "ollama", "model": "qwen3-vl:8b"},
    )

    assert result.status == "failed"
    assert "failed to decode image/media" in (result.message or "")
    assert result.metadata["error"].startswith("Ollama vision request failed")
    assert "ffprobe failed on buffer" in result.metadata["error"]
    assert finished[0]["status"] == "failed"
    assert finished[0]["error_class"] == "RuntimeError"
    assert "failed to decode image/media" in str(finished[0]["error_message"])
    assert "image-bytes" not in str(finished)
    assert "private-image.png" not in str(finished)


def test_ollama_vision_gpu_lease_rejection_is_retryable(monkeypatch, tmp_path):
    from flux_llm_kb.gpu_scheduler import GpuLeaseRejected

    image = tmp_path / "vision.png"
    image.write_bytes(PNG_BYTES)

    class FakeScheduler:
        def acquire(self, _profile):
            raise GpuLeaseRejected("vram_budget_exceeded")

    monkeypatch.setattr(extractors, "_vision_request_image_bytes", lambda _path: (b"image-bytes", {"submitted_bytes": 10}))
    monkeypatch.setattr("flux_llm_kb.gpu_scheduler.get_gpu_scheduler", lambda: FakeScheduler())

    with pytest.raises(GpuLeaseRejected, match="vram_budget_exceeded"):
        extractors._vision_with_ollama_compatible(
            image,
            source_label="private-image.png",
            provider="ollama",
            base_url="http://ollama:11434",
            model="qwen3-vl:8b",
            keep_alive="2m",
            timeout_seconds=1,
            metadata={"provider": "ollama", "model": "qwen3-vl:8b"},
        )


def test_ollama_vision_http_503_is_retryable_gpu_busy(monkeypatch, tmp_path):
    from flux_llm_kb.model_runner import ModelRunnerBusy

    image = tmp_path / "vision.png"
    image.write_bytes(PNG_BYTES)

    class FakeLease:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    class FakeScheduler:
        def acquire(self, _profile):
            return FakeLease()

        def record_model_residency(self, _residency):
            return None

    body = b'{"detail":{"code":"gpu.scheduler_busy","message":"GPU scheduler busy","retry_after_seconds":7}}'

    def fake_urlopen(_request, **_kwargs):
        raise HTTPError(
            url="http://ollama:11434/api/generate",
            code=503,
            msg="Service Unavailable",
            hdrs={},
            fp=BytesIO(body),
        )

    monkeypatch.setattr(extractors, "_vision_request_image_bytes", lambda _path: (b"image-bytes", {"submitted_bytes": 10}))
    monkeypatch.setattr(extractors, "urlopen", fake_urlopen, raising=False)
    monkeypatch.setattr("flux_llm_kb.gpu_scheduler.get_gpu_scheduler", lambda: FakeScheduler())

    with pytest.raises(ModelRunnerBusy, match="GPU scheduler busy") as exc_info:
        extractors._vision_with_ollama_compatible(
            image,
            source_label="private-image.png",
            provider="ollama",
            base_url="http://ollama:11434",
            model="qwen3-vl:8b",
            keep_alive="2m",
            timeout_seconds=1,
            metadata={"provider": "ollama", "model": "qwen3-vl:8b"},
        )

    assert exc_info.value.retry_after_seconds == 7.0


def test_extract_image_resizes_large_payload_for_local_vision(monkeypatch, tmp_path):
    from PIL import Image

    path = tmp_path / "large-diagram.jpg"
    Image.new("RGB", (3446, 2086), "white").save(path, quality=95)
    _configure_vision(monkeypatch, tmp_path, max_pixels=80_000_000)
    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_image",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(ModuleNotFoundError("paddleocr")),
    )

    class FakeResponse:
        def read(self, _limit=-1):
            return b'{"response":"Resized diagram caption."}'

    def fake_urlopen(request, **_kwargs):
        payload = json.loads(request.data.decode("utf-8"))
        image_bytes = base64.b64decode(payload["images"][0])
        with Image.open(BytesIO(image_bytes)) as submitted:
            assert max(submitted.size) <= 1280
        return FakeResponse()

    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].body == "Resized diagram caption."
    assert result.metadata["vision"]["input_width"] == 3446
    assert result.metadata["vision"]["submitted_max_edge"] == 1280
    assert result.metadata["vision"]["submitted_width"] <= 1280


def test_extract_image_reindexes_combined_ocr_and_vision_chunks(monkeypatch, tmp_path):
    path = tmp_path / "diagram.png"
    path.write_bytes(PNG_BYTES)
    _configure_vision(monkeypatch, tmp_path)
    class FakeResponse:
        def read(self, _limit=-1):
            return b'{"response":"Vision text"}'

    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_image", lambda *_args, **_kwargs: "OCR text")
    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", lambda *_args, **_kwargs: FakeResponse(), raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert [chunk.chunk_index for chunk in result.chunks] == [0, 1]
    assert [chunk.modality for chunk in result.chunks] == ["ocr", "vision"]
    assert [chunk.body for chunk in result.chunks] == ["OCR text", "Vision text"]


def test_extract_screenshot_image_uses_simple_paddleocr_route(monkeypatch, tmp_path):
    from PIL import Image

    path = tmp_path / "homepage-screenshot.png"
    Image.new("RGB", (1600, 1000), "white").save(path)
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "false")
    calls: list[dict[str, object]] = []

    def fake_simple(image_path, *, model):
        calls.append({"path": Path(image_path).name, "model": model})
        return "Screenshot OCR text"

    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_image", fake_simple)
    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_document",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("ordinary screenshots must not use PaddleOCR-VL")),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["ocr"]["model"] == "PP-OCRv5"
    assert result.metadata["ocr"]["route"] == "simple_image"
    assert result.metadata["ocr"]["route_reason"] == "ordinary_image"
    assert calls == [{"path": "homepage-screenshot.png", "model": "PP-OCRv5"}]


def test_extract_scanned_tiff_uses_paddleocr_vl_route(monkeypatch, tmp_path):
    from PIL import Image

    path = tmp_path / "scanned-page.tiff"
    Image.new("RGB", (1200, 1600), "white").save(path)
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "false")
    calls: list[dict[str, object]] = []

    def fake_document(image_path, *, model):
        calls.append({"path": Path(image_path).name, "model": model})
        return "Scanned document OCR text"

    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_image",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("scanned TIFF pages must use PaddleOCR-VL")),
    )
    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_document", fake_document)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["ocr"]["model"] == "PaddleOCR-VL"
    assert result.metadata["ocr"]["route"] == "document_image"
    assert result.metadata["ocr"]["route_reason"] == "tiff_scan"
    assert calls == [{"path": "scanned-page.tiff", "model": "PaddleOCR-VL"}]


def test_extract_image_records_failed_local_vision_attempt_when_ocr_succeeds(monkeypatch, tmp_path):
    path = tmp_path / "diagram.png"
    path.write_bytes(PNG_BYTES)
    _configure_vision(monkeypatch, tmp_path, keep_alive="2m")
    def fake_urlopen(_request, **_kwargs):
        raise TimeoutError("vision request exceeded cold-load timeout")

    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_image", lambda *_args, **_kwargs: "OCR text still indexes")
    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].modality == "ocr"
    assert result.metadata["vision"]["status"] == "failed"
    assert result.metadata["vision"]["cache_misses"] == 1
    assert result.metadata["vision"]["descriptions"] == 0
    assert "cold-load timeout" in result.metadata["vision"]["error"]
    assert result.metadata["vision_escalation"] == "unavailable"


def test_extract_image_uses_local_vision_after_empty_ocr_via_docker_host_gateway(monkeypatch, tmp_path):
    path = tmp_path / "diagram.png"
    path.write_bytes(PNG_BYTES)
    _configure_vision(
        monkeypatch,
        tmp_path,
        model="qwen2.5vl:7b",
        base_url="http://host.docker.internal:11434",
    )
    class FakeResponse:
        def read(self, _limit=-1):
            return b'{"response":"Diagram shows an approvals workflow and inbox attachments."}'

    def fake_urlopen(request, **_kwargs):
        assert request.full_url == "http://host.docker.internal:11434/api/generate"
        return FakeResponse()

    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_image", lambda *_args, **_kwargs: "")
    monkeypatch.setattr("flux_llm_kb.extractors.urlopen", fake_urlopen, raising=False)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].modality == "vision"
    assert result.chunks[0].body == "Diagram shows an approvals workflow and inbox attachments."
    assert result.metadata["ocr"]["status"] == "completed"
    assert result.metadata["vision"]["status"] == "completed"
    assert result.metadata["vision"]["model"] == "qwen2.5vl:7b"
    assert result.metadata["vision_escalation"] == "completed"


def test_extract_image_blocks_unsupported_vision_provider_without_remote_call(monkeypatch, tmp_path):
    path = tmp_path / "diagram.png"
    path.write_bytes(PNG_BYTES)
    _configure_vision(monkeypatch, tmp_path, provider="openai_compatible")
    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_image",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(ModuleNotFoundError("paddleocr")),
    )
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
        "flux_llm_kb.extractors.urlopen",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("blocked provider must not call network")),
        raising=False,
    )

    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_image", lambda *_args, **_kwargs: "OCR text survives blocked vision")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].modality == "ocr"
    assert result.chunks[0].body == "OCR text survives blocked vision"
    assert result.metadata["ocr"]["status"] == "completed"
    assert result.metadata["vision"]["status"] == "blocked_config"
    assert result.metadata["vision"]["provider"] == "openai_compatible"


def test_extract_image_blocks_paddleocr_timeout_without_retryable_failure(monkeypatch, tmp_path):
    path = tmp_path / "scan.png"
    path.write_bytes(PNG_BYTES)
    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "false")
    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_image",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(subprocess.TimeoutExpired("paddleocr", 30)),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert "timed out" in result.message
    assert result.metadata["ocr"]["status"] == "blocked_timeout"
    assert result.metadata["ocr"]["engine"] == "paddleocr"


def test_extract_image_blocks_invalid_ocr_input_as_invalid_source(monkeypatch, tmp_path):
    from flux_llm_kb import model_runner

    path = tmp_path / "scan.png"
    path.write_bytes(PNG_BYTES)
    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "false")
    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_image",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(
            model_runner.OcrInvalidInputError(
                "OCR image payload is not a readable image",
                metadata={"suffix": ".png", "byte_count": 16},
            )
        ),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_invalid_source"
    assert result.message == "OCR image payload is not a readable image"
    assert result.metadata["ocr"]["status"] == "blocked_invalid_source"
    assert result.metadata["ocr"]["error_code"] == "ocr.invalid_image_input"
    assert result.metadata["ocr"]["error"] == "OCR image payload is not a readable image"
    assert result.metadata["ocr"]["engine"] == "paddleocr"


def test_paddle_ocr_missing_dependency_message_keeps_path_when_redactions_disabled(monkeypatch):
    monkeypatch.delenv("FLUX_KB_REDACTIONS_ENABLED", raising=False)
    path = "E:/Docs/scan.png"

    message = extractors._paddle_ocr_missing_dependency_message(ImportError(f"paddlex[ocr] failed for {path}"))

    assert path in message
    assert "[REDACTED:path]" not in message


def test_paddle_ocr_missing_dependency_message_redacts_path_when_enabled(monkeypatch):
    monkeypatch.setenv("FLUX_KB_REDACTIONS_ENABLED", "true")
    path = "E:/Docs/scan.png"

    message = extractors._paddle_ocr_missing_dependency_message(ImportError(f"paddlex[ocr] failed for {path}"))

    assert path not in message
    assert "[REDACTED:path]" in message


def test_extract_image_writes_and_reuses_redacted_ocr_cache(monkeypatch, tmp_path):
    path = tmp_path / "scan.png"
    path.write_bytes(PNG_BYTES)
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "false")
    calls = []

    def fake_paddle(image_path, **_kwargs):
        calls.append(Path(image_path))
        return "Scanned image text"

    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_image", fake_paddle)

    first = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert first.status == "indexed"
    assert first.chunks[0].body == "Scanned image text"
    assert first.metadata["ocr"]["cache_hits"] == 0
    assert first.metadata["ocr"]["cache_misses"] == 1
    assert len(calls) == 1

    def fail_run(_image_path, **_kwargs):
        raise AssertionError("second extraction should use the OCR cache")

    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_image", fail_run)

    second = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert second.status == "indexed"
    assert second.chunks[0].body == "Scanned image text"
    assert second.metadata["ocr"]["cache_hits"] == 1
    assert second.metadata["ocr"]["cache_misses"] == 0


def test_extract_image_ocr_cache_hit_does_not_record_model_activity(monkeypatch, tmp_path):
    path = tmp_path / "scan.png"
    path.write_bytes(PNG_BYTES)
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))

    extractors._write_ocr_cache(path, "Cached OCR text", model="PP-OCRv5")
    monkeypatch.setattr(
        "flux_llm_kb.extractors._run_paddleocr_image",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("cache hit must not run OCR")),
    )
    monkeypatch.setattr(
        extractors,
        "record_model_activity",
        lambda **_kwargs: (_ for _ in ()).throw(AssertionError("cache hit must not record model activity")),
        raising=False,
    )

    result = extractors._ocr_image_with_paddleocr(path, model="PP-OCRv5")

    assert result.status == "completed"
    assert result.text == "Cached OCR text"
    assert result.metadata["status"] == "cache_hit"
    assert result.metadata["cache_hits"] == 1
    assert result.metadata["cache_misses"] == 0


def test_extract_large_image_downscales_paddleocr_input_and_reuses_original_cache(monkeypatch, tmp_path):
    from PIL import Image

    path = tmp_path / "tall-scan.png"
    Image.new("RGB", (16, 7000), "white").save(path)
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setenv("FLUX_KB_VISION_ENABLED", "false")
    calls = []

    def fake_paddle(image_path, **_kwargs):
        calls.append(Path(image_path))
        ocr_input = Path(image_path)
        assert ocr_input != path
        with Image.open(ocr_input) as image:
            assert max(image.size) <= 6000
        return "Large scan text"

    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_image", fake_paddle)

    first = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert first.status == "indexed"
    assert first.chunks[0].body == "Large scan text"
    assert first.metadata["ocr"]["preprocess"]["status"] == "scaled"
    assert first.metadata["ocr"]["preprocess"]["input_width"] == 16
    assert first.metadata["ocr"]["preprocess"]["input_height"] == 7000
    assert first.metadata["ocr"]["preprocess"]["max_edge"] == 6000
    assert len(calls) == 1

    def fail_run(_image_path, **_kwargs):
        raise AssertionError("second extraction should use original-source OCR cache")

    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_image", fail_run)

    second = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert second.status == "indexed"
    assert second.chunks[0].body == first.chunks[0].body
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


def test_thumbnail_for_frame_retries_with_accurate_seek_after_empty_output(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    calls: list[list[object]] = []

    def fake_run(command, **_kwargs):
        calls.append(command)
        output_path = Path(command[-1])
        if len(calls) == 1:
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        output_path.write_bytes(PNG_BYTES)
        return SimpleNamespace(returncode=0, stdout="", stderr="")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)

    result = extractors._thumbnail_for_frame(path, ffmpeg="C:/tools/ffmpeg.exe", timestamp=12.345)

    assert result["status"] == "completed"
    assert result["cache_hit"] is False
    assert result["attempts"] == 2
    assert len(calls) == 2
    assert calls[0].index("-ss") < calls[0].index("-i")
    assert calls[1].index("-ss") > calls[1].index("-i")


def test_extract_video_frames_completes_metadata_when_thumbnails_are_unavailable(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    _configure_frame_sampling(monkeypatch, tmp_path, count=2, threshold=0.35)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")

    def fake_run(command, **_kwargs):
        if command[0].endswith("ffprobe.exe"):
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=60), stderr="")
        raise AssertionError(f"unexpected command: {command}")

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr(
        "flux_llm_kb.extractors._thumbnail_for_frame",
        lambda *_args, **_kwargs: {
            "status": "failed",
            "path": str(tmp_path / "missing.png"),
            "cache_hit": False,
            "message": "thumbnail file was not created",
            "attempts": 2,
        },
    )

    result = extractors.extract_video_frames(
        path,
        {"timestamps": [10.0, 20.0], "duration_seconds": 60.0, "chunks_seen": 0},
    )

    assert result.status == "metadata_only"
    assert result.message == "thumbnail file was not created"
    frame_sampling = result.metadata["frame_sampling"]
    assert frame_sampling["status"] == "skipped_thumbnail_unavailable"
    assert frame_sampling["frame_count"] == 0
    assert frame_sampling["thumbnail_failure_count"] == 2


def test_extract_video_reindexes_transcript_and_frame_vision_chunks(monkeypatch, tmp_path):
    path = tmp_path / "clip.mp4"
    path.write_bytes(b"fake media")
    _configure_frame_sampling(monkeypatch, tmp_path, count=2, threshold=0.35, vision_enabled=True)
    monkeypatch.setattr("flux_llm_kb.extractors.shutil.which", lambda command: f"C:/tools/{command}.exe")
    monkeypatch.setattr(
        "flux_llm_kb.extractors._asr_media",
        lambda *_args, **_kwargs: SimpleNamespace(
            status="completed",
            text="Transcript text",
            metadata={"status": "completed"},
            message=None,
        ),
    )

    def fake_run(command, **_kwargs):
        joined = " ".join(str(part) for part in command)
        if command[0].endswith("ffprobe.exe"):
            return SimpleNamespace(returncode=0, stdout=_media_probe_json(duration=60), stderr="")
        if "select='gt(scene,0.35)'" in joined:
            return SimpleNamespace(
                returncode=0,
                stdout="",
                stderr=(
                    "frame:1 pts_time:10.0\n"
                    "lavfi.scene_score=0.95\n"
                    "frame:2 pts_time:20.0\n"
                    "lavfi.scene_score=0.90\n"
                ),
            )
        if "-frames:v" in command:
            Path(command[-1]).write_bytes(PNG_BYTES)
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(f"unexpected command: {command}")

    def fake_vision(_path, *, source_label):
        return SimpleNamespace(
            status="completed",
            text=f"Vision text for {source_label}",
            metadata={"status": "completed", "cache_hits": 0, "cache_misses": 1, "descriptions": 1},
            message=None,
        )

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors._vision_image", fake_vision)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert [chunk.chunk_index for chunk in result.chunks] == [0, 1, 2]
    assert [chunk.modality for chunk in result.chunks] == ["transcript", "vision", "vision"]
    assert result.chunks[0].body == "Transcript text"


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


def test_plan_staged_mixed_pdf_keeps_embedded_text_and_queues_scanned_pages(monkeypatch, tmp_path):
    path = tmp_path / "mixed.pdf"
    path.write_bytes(b"%PDF mixed")

    class FakePage:
        def __init__(self, text):
            self.text = text

        def extract_text(self):
            return self.text

    class FakePdfReader:
        def __init__(self, _path):
            self.pages = [FakePage("Embedded first page"), FakePage(""), FakePage("Embedded third page"), FakePage("")]

    monkeypatch.setitem(sys.modules, "pypdf", SimpleNamespace(PdfReader=FakePdfReader))

    result = plan_staged_pdf_extraction(path)

    assert result.status == "staged"
    assert [chunk.body for chunk in result.chunks] == ["Embedded first page\nEmbedded third page"]
    assert result.metadata["ocr"]["pages_planned"] == 2
    assert result.metadata["ocr"]["pages_with_embedded_text"] == 2
    first_job = result.metadata["staged_jobs"][0]
    assert first_job["job_type"] == "corpus_extract_pdf_ocr_pages"
    assert first_job["payload"]["pages"] == [2, 4]
    assert first_job["payload"]["chunks_seen"] == 1
    assert result.metadata["staged_extraction"]["pending_job_count"] == 1


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


def test_extract_docx_blocks_invalid_package_without_retryable_failure(monkeypatch, tmp_path):
    path = tmp_path / "broken.docx"
    path.write_bytes(b"not a docx package")

    class PackageNotFoundError(Exception):
        pass

    def fake_document(_path):
        raise PackageNotFoundError(f"Package not found at '{path}'")

    monkeypatch.setitem(sys.modules, "docx", SimpleNamespace(Document=fake_document))

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_invalid_source"
    assert "Package not found" in result.message
    assert result.metadata["extractor"] == "docx"
    assert result.metadata["reason"] == "invalid_package"


def test_extract_docx_salvages_text_when_embedded_part_missing(monkeypatch, tmp_path):
    path = tmp_path / "dangling-ole.docx"
    path.write_bytes(
        _zip_payload(
            {
                "[Content_Types].xml": """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>""",
                "_rels/.rels": """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>""",
                "word/document.xml": """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    <w:p><w:r><w:t>System blueprint</w:t><w:tab/><w:t>Recognition guide</w:t></w:r></w:p>
    <w:p><w:r><w:t>Equivalency notes</w:t><w:br/><w:t>Second line</w:t></w:r></w:p>
  </w:body>
</w:document>""",
                "word/_rels/document.xml.rels": """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rIdOle" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject" Target="embeddings/oleObject1.bin"/>
</Relationships>""",
            }
        )
    )

    def fake_document(_path):
        raise KeyError("There is no item named 'word/embeddings/oleObject1.bin' in the archive")

    monkeypatch.setitem(sys.modules, "docx", SimpleNamespace(Document=fake_document))

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert "System blueprint\tRecognition guide" in result.chunks[0].body
    assert "Equivalency notes\nSecond line" in result.chunks[0].body
    assert result.metadata["extractor"] == "docx"
    assert result.metadata["fallback"] == "package_xml"
    assert result.metadata["missing_package_part"] == "word/embeddings/oleObject1.bin"
    assert result.metadata["warnings"] == [
        "\"There is no item named 'word/embeddings/oleObject1.bin' in the archive\""
    ]


def test_extract_docx_preserves_top_level_paragraph_text(tmp_path):
    docx = pytest.importorskip("docx")
    path = tmp_path / "paragraphs.docx"
    document = docx.Document()
    document.add_paragraph("Top-level paragraph text")
    document.save(path)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.chunks[0].body == "Top-level paragraph text"


def test_extract_docx_indexes_text_from_table_only_document(tmp_path):
    docx = pytest.importorskip("docx")
    path = tmp_path / "table-only.docx"
    document = docx.Document()
    table = document.add_table(rows=1, cols=1)
    table.cell(0, 0).text = "Accreditation evidence in a table cell"
    document.save(path)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert "Accreditation evidence in a table cell" in result.chunks[0].body


def test_extract_docx_preserves_body_order_across_paragraphs_and_tables(tmp_path):
    docx = pytest.importorskip("docx")
    path = tmp_path / "mixed-order.docx"
    document = docx.Document()
    document.add_paragraph("Before table")
    table = document.add_table(rows=1, cols=1)
    table.cell(0, 0).text = "Within table"
    document.add_paragraph("After table")
    document.save(path)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))
    body = result.chunks[0].body

    assert result.status == "indexed"
    assert body.index("Before table") < body.index("Within table") < body.index("After table")


def test_extract_docx_indexes_nested_table_text_in_cell_order(tmp_path):
    docx = pytest.importorskip("docx")
    path = tmp_path / "nested-table.docx"
    document = docx.Document()
    outer_table = document.add_table(rows=1, cols=1)
    cell = outer_table.cell(0, 0)
    cell.text = "Outer text before nested table"
    nested_table = cell.add_table(rows=1, cols=1)
    nested_table.cell(0, 0).text = "Nested table text"
    cell.add_paragraph("Outer text after nested table")
    document.save(path)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))
    body = result.chunks[0].body

    assert result.status == "indexed"
    assert body.index("Outer text before nested table") < body.index("Nested table text") < body.index("Outer text after nested table")


def test_extract_docx_keeps_empty_tables_metadata_only(tmp_path):
    docx = pytest.importorskip("docx")
    path = tmp_path / "empty-table.docx"
    document = docx.Document()
    document.add_table(rows=1, cols=1)
    document.save(path)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "metadata_only"
    assert result.chunks == ()


def test_extract_docx_does_not_duplicate_merged_table_cell_text(tmp_path):
    docx = pytest.importorskip("docx")
    path = tmp_path / "merged-table.docx"
    document = docx.Document()
    document.add_paragraph("Existing paragraph")
    table = document.add_table(rows=1, cols=2)
    table.cell(0, 0).text = "Merged table cell text"
    table.cell(0, 0).merge(table.cell(0, 1))
    document.save(path)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))
    body = result.chunks[0].body

    assert result.status == "indexed"
    assert body.count("Merged table cell text") == 1


def test_extract_docx_keeps_distinct_cells_when_vertical_merge_reuses_the_top_cell(tmp_path):
    docx = pytest.importorskip("docx")
    path = tmp_path / "vertical-merged-table.docx"
    document = docx.Document()
    table = document.add_table(rows=3, cols=2)
    for row_index, row in enumerate(table.rows):
        for column_index, cell in enumerate(row.cells):
            cell.text = f"cell-{row_index}-{column_index}"
    table.cell(0, 0).merge(table.cell(1, 0))
    document.save(path)

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))
    body = result.chunks[0].body

    assert result.status == "indexed"
    for value in ("cell-0-0", "cell-1-0", "cell-0-1", "cell-1-1", "cell-2-0", "cell-2-1"):
        assert body.count(value) == 1


def test_extract_image_only_pdf_uses_pdftoppm_and_paddleocr_vl(monkeypatch, tmp_path):
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
        }.get(command),
    )
    calls = []

    def fake_run(command, **_kwargs):
        calls.append(command)
        if command[0] == "C:/tools/pdftoppm.exe":
            page = command[command.index("-f") + 1]
            assert command[command.index("-scale-to") + 1] == "6000"
            output_prefix = Path(command[-1])
            output_prefix.with_name(f"{output_prefix.name}-{page}.png").write_bytes(PNG_BYTES + page.encode("ascii"))
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(command)

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_document", lambda image_path, **_kwargs: f"OCR text from {Path(image_path).stem}")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert "OCR text from page-1" in result.chunks[0].body
    assert "OCR text from page-2" in result.chunks[0].body
    assert result.metadata["ocr"]["renderer"] == "pdftoppm"
    assert result.metadata["ocr"]["engine"] == "paddleocr"
    assert result.metadata["ocr"]["model"] == "PaddleOCR-VL"
    assert result.metadata["ocr"]["page_count"] == 2
    assert result.metadata["ocr"]["pages_attempted"] == 2
    assert result.metadata["ocr"]["cache_hits"] == 0
    assert result.metadata["ocr"]["cache_misses"] == 2
    assert [Path(command[0]).name for command in calls].count("pdftoppm.exe") == 2


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
        lambda _command: None,
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert result.message == "pdftoppm command not found"
    assert result.metadata["ocr"]["status"] == "blocked_missing_dependency"
    assert result.metadata["ocr"]["pages_attempted"] == 0


def test_extract_image_only_pdf_blocks_pdftoppm_timeout_without_retryable_failure(monkeypatch, tmp_path):
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
        lambda command: {
            "pdftoppm": "C:/tools/pdftoppm.exe",
        }.get(command),
    )
    monkeypatch.setattr(
        "flux_llm_kb.extractors.run_no_window",
        lambda command, **_kwargs: (_ for _ in ()).throw(subprocess.TimeoutExpired(command, 30)),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_missing_dependency"
    assert "timed out" in result.message
    assert result.metadata["ocr"]["status"] == "blocked_timeout"
    assert result.metadata["ocr"]["renderer"] == "pdftoppm"
    assert result.metadata["ocr"]["pages_attempted"] == 0


def test_extract_large_scanned_pdf_ocr_all_pages_without_page_cap(monkeypatch, tmp_path):
    path = tmp_path / "large-scan.pdf"
    path.write_bytes(b"%PDF large")
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))

    class EmptyPage:
        def extract_text(self):
            return ""

    class FakePdfReader:
        def __init__(self, _path):
            self.pages = [EmptyPage() for _ in range(26)]

    monkeypatch.setitem(sys.modules, "pypdf", SimpleNamespace(PdfReader=FakePdfReader))
    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: {
            "pdftoppm": "C:/tools/pdftoppm.exe",
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
        raise AssertionError(command)

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_document", lambda image_path, **_kwargs: f"OCR text from {Path(image_path).stem}")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert "OCR text from page-1" in result.chunks[0].body
    assert "OCR text from page-26" in result.chunks[0].body
    assert result.metadata["ocr"]["status"] == "completed"
    assert result.metadata["ocr"]["page_count"] == 26
    assert result.metadata["ocr"]["pages_attempted"] == 26
    assert [Path(command[0]).name for command in calls].count("pdftoppm.exe") == 26


def test_extract_pdf_ocr_pages_queues_next_batch(monkeypatch, tmp_path):
    path = tmp_path / "scan.pdf"
    path.write_bytes(b"%PDF staged")
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: {
            "pdftoppm": "C:/tools/pdftoppm.exe",
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
        raise AssertionError(command)

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_document", lambda image_path, **_kwargs: f"OCR text from {Path(image_path).stem}")

    result = extract_pdf_ocr_pages(
        path,
        {
            "page_start": 1,
            "page_end": 2,
            "page_count": 3,
            "page_batch_size": 2,
            "chunks_seen": 0,
        },
    )

    assert result.status == "staged"
    assert result.chunks[0].chunk_index == PDF_OCR_CHUNK_INDEX_BASE
    assert "OCR text from page-1" in result.chunks[0].body
    assert "OCR text from page-2" in result.chunks[0].body
    assert result.metadata["ocr"]["pages_attempted"] == 2
    next_job = result.metadata["staged_extraction"]["next_job"]
    assert next_job["job_type"] == "corpus_extract_pdf_ocr_pages"
    assert next_job["payload"]["page_start"] == 3
    assert next_job["payload"]["page_end"] == 3
    assert next_job["payload"]["chunks_seen"] == 1
    assert [Path(command[0]).name for command in calls].count("pdftoppm.exe") == 2


def test_extract_pdf_ocr_pages_accepts_explicit_mixed_page_batches(monkeypatch, tmp_path):
    path = tmp_path / "mixed.pdf"
    path.write_bytes(b"%PDF staged mixed")
    monkeypatch.setenv("FLUX_KB_CACHE_ROOT", str(tmp_path / "cache"))
    monkeypatch.setattr(
        "flux_llm_kb.extractors.shutil.which",
        lambda command: {
            "pdftoppm": "C:/tools/pdftoppm.exe",
        }.get(command),
    )
    rendered_pages = []

    def fake_run(command, **_kwargs):
        if command[0] == "C:/tools/pdftoppm.exe":
            page = command[command.index("-f") + 1]
            rendered_pages.append(int(page))
            output_prefix = Path(command[-1])
            output_prefix.with_name(f"{output_prefix.name}-{page}.png").write_bytes(PNG_BYTES + page.encode("ascii"))
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        raise AssertionError(command)

    monkeypatch.setattr("flux_llm_kb.extractors.run_no_window", fake_run)
    monkeypatch.setattr("flux_llm_kb.extractors._run_paddleocr_document", lambda image_path, **_kwargs: f"OCR text from {Path(image_path).stem}")

    result = extract_pdf_ocr_pages(
        path,
        {
            "pages": [2],
            "remaining_pages": [4],
            "page_count": 4,
            "page_batch_size": 1,
            "chunks_seen": 1,
            "embedded_chunk_count": 1,
        },
    )

    assert result.status == "staged"
    assert rendered_pages == [2]
    assert result.chunks[0].chunk_index == PDF_OCR_CHUNK_INDEX_BASE + MEDIA_SEGMENT_CHUNK_INDEX_STRIDE
    assert result.metadata["ocr"]["pages"] == [2]
    assert result.metadata["staged_extraction"]["chunks_seen"] == 2
    next_job = result.metadata["staged_extraction"]["next_job"]
    assert next_job["payload"]["pages"] == [4]
    assert next_job["payload"]["chunks_seen"] == 2


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


def test_extract_archive_indexes_embedded_xlsx_member(tmp_path):
    openpyxl = pytest.importorskip("openpyxl")
    member_bytes = BytesIO()
    workbook = openpyxl.Workbook()
    worksheet = workbook.active
    worksheet.title = "Inventory"
    worksheet.append(("Item", "Count"))
    worksheet.append(("Evidence", 3))
    workbook.save(member_bytes)
    workbook.close()

    path = tmp_path / "bundle.zip"
    with ZipFile(path, "w") as archive:
        archive.writestr("data/inventory.xlsx", member_bytes.getvalue())

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, container_max_depth=2))
    child = result.child_assets[0]

    assert child.member_path == "data/inventory.xlsx"
    assert child.extraction_status == "indexed"
    assert "Item | Count" in child.chunks[0].body


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
    assert result.message is None
    assert result.metadata["skipped_member_size_limit_count"] == 1
    assert result.metadata["parsed_child_count"] == 0
    assert result.metadata["skipped_child_count"] == 1
    assert "member exceeds size limit" in result.metadata["warnings"]
    assert len(result.child_assets) == 1
    child = result.child_assets[0]
    assert child.member_path == "large.txt"
    assert child.extraction_status == "metadata_only"
    assert child.content_hash is None
    assert child.metadata["skipped_reason"] == "member_size_limit"


def test_extract_archive_skips_oversized_member_and_indexes_safe_member(tmp_path):
    path = tmp_path / "mixed.zip"
    with ZipFile(path, "w") as archive:
        archive.writestr("large.txt", "too large")
        archive.writestr("notes.txt", "ok")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, container_max_member_bytes=4))

    assert result.status == "metadata_only"
    assert result.metadata["skipped_member_size_limit_count"] == 1
    assert result.metadata["parsed_child_count"] == 1
    assert result.metadata["skipped_child_count"] == 1
    assert [child.member_path for child in result.child_assets] == ["large.txt", "notes.txt"]
    assert result.child_assets[0].metadata["skipped_reason"] == "member_size_limit"
    assert result.child_assets[1].extraction_status == "indexed"
    assert result.child_assets[1].chunks[0].body == "ok"


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


def test_extract_invalid_xlsx_blocks_as_invalid_package(tmp_path):
    path = tmp_path / "bad.xlsx"
    path.write_bytes(b"not a zip file")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "blocked_invalid_source"
    assert result.chunks == ()
    assert result.metadata["extractor"] == "xlsx"
    assert result.metadata["reason"] == "invalid_package"
    assert "File is not a zip file" in (result.message or "")


def test_extract_xlsx_closes_workbook_after_reading(monkeypatch, tmp_path):
    path = tmp_path / "budget.xlsx"
    path.write_bytes(b"xlsx placeholder")

    class FakeWorksheet:
        title = "Budget"

        def iter_rows(self, max_row, max_col, values_only):
            assert (max_row, max_col, values_only) == (200, 30, True)
            return iter([("Quarter", "Amount"), ("Q1", 1200)])

    class FakeWorkbook:
        def __init__(self):
            self.worksheets = [FakeWorksheet()]
            self.close_calls = 0

        def close(self):
            self.close_calls += 1

    workbook = FakeWorkbook()
    monkeypatch.setitem(
        sys.modules,
        "openpyxl",
        SimpleNamespace(load_workbook=lambda _path, read_only, data_only: workbook),
    )

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert "Q1 | 1200" in result.chunks[0].body
    assert workbook.close_calls == 1


def test_extract_large_invalid_xlsx_sample_first_blocks_as_invalid_package(tmp_path):
    path = tmp_path / "bad.xlsx"
    path.write_bytes(b"not a zip file")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path, max_inline_bytes=8))

    assert result.status == "blocked_invalid_source"
    assert result.chunks == ()
    assert result.metadata["extractor"] == "xlsx"
    assert result.metadata["reason"] == "invalid_package"
    assert "File is not a zip file" in (result.message or "")


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


def test_extract_vsdx_indexes_page_text_when_package_has_many_non_page_members(tmp_path):
    path = tmp_path / "many-members.vsdx"
    with ZipFile(path, "w") as archive:
        archive.writestr(
            "visio/pages/page1.xml",
            """
            <PageContents xmlns="http://schemas.microsoft.com/office/visio/2012/main">
              <Shapes>
                <Shape ID="1" NameU="Process">
                  <Text>Large package page text</Text>
                </Shape>
              </Shapes>
            </PageContents>
            """,
        )
        for index in range(225):
            archive.writestr(f"visio/masters/master{index}.xml", "<Master />")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["page_count"] == 1
    assert "Large package page text" in result.chunks[0].body


def test_extract_vsdx_ignores_oversized_non_page_media_members(tmp_path):
    path = tmp_path / "large-media.vsdx"
    with ZipFile(path, "w") as archive:
        archive.writestr(
            "visio/pages/page1.xml",
            """
            <PageContents xmlns="http://schemas.microsoft.com/office/visio/2012/main">
              <Shapes>
                <Shape ID="1" NameU="Process">
                  <Text>Readable page beside large media</Text>
                </Shape>
              </Shapes>
            </PageContents>
            """,
        )
        archive.writestr("visio/media/image1.bmp", b"0" * (6 * 1024 * 1024))

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "indexed"
    assert result.metadata["page_count"] == 1
    assert "Readable page beside large media" in result.chunks[0].body


def test_extract_vsdx_rejects_too_many_page_xml_members(tmp_path):
    path = tmp_path / "many-pages.vsdx"
    with ZipFile(path, "w") as archive:
        for index in range(201):
            archive.writestr(f"visio/pages/page{index}.xml", "<PageContents />")

    result = extract_file(path, CorpusPolicy(root_path=tmp_path))

    assert result.status == "failed"
    assert "page XML" in (result.message or "")


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
