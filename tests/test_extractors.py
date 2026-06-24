import base64
import gzip
import sys
import tarfile
from io import BytesIO
from pathlib import Path
from types import SimpleNamespace
from urllib.parse import quote
from zipfile import ZipFile
import zlib

from flux_llm_kb.crawler import CorpusPolicy
from flux_llm_kb.extractors import extract_file, extractor_availability


PNG_BYTES = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAIAAADZrBkAAAAAD0lEQVR4nGP8z8AARLJAgAEACPwD"
    "Aaz3RyoAAAAASUVORK5CYII="
)


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
    assert all("ok" in item and "message" in item for item in availability.values())


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
