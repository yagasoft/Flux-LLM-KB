import base64
import sys
from pathlib import Path
from types import SimpleNamespace
from urllib.parse import quote
from zipfile import ZipFile
import zlib

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
