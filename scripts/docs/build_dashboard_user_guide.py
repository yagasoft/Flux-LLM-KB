from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

from docx import Document
from docx.enum.section import WD_SECTION
from docx.enum.table import WD_TABLE_ALIGNMENT, WD_CELL_VERTICAL_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Inches, Pt, RGBColor


REPO_ROOT = Path(__file__).resolve().parents[2]
GUIDE_DIR = REPO_ROOT / "docs" / "user-guide"
SCREENS_DIR = GUIDE_DIR / "screens"
MANUAL_MD = GUIDE_DIR / "dashboard-user-manual.md"
DOCX_PATH = GUIDE_DIR / "Flux-LLM-KB-Dashboard-User-Manual.docx"

BODY_FONT = "Calibri"
HEADING_BLUE = "2E74B5"
HEADING_DARK = "1F4D78"
BODY_COLOR = "172033"
MUTED_COLOR = "52627A"
TABLE_HEADER_FILL = "E8EEF5"
CALLOUT_FILL = "F4F6F9"
TABLE_WIDTH_DXA = 9360
TABLE_INDENT_DXA = 120


def capture_screens() -> None:
    node = _resolve_node()
    subprocess.run(
        [node, str(REPO_ROOT / "scripts" / "docs" / "capture_dashboard_user_guide_screens.mjs")],
        cwd=REPO_ROOT,
        check=True,
    )


def _resolve_node() -> str:
    bundled = Path.home() / ".cache" / "codex-runtimes" / "codex-primary-runtime" / "dependencies" / "node" / "bin" / "node.exe"
    if bundled.exists():
        return str(bundled)
    return "node"


def _set_doc_styles(document: Document) -> None:
    section = document.sections[0]
    section.top_margin = Inches(1)
    section.bottom_margin = Inches(1)
    section.left_margin = Inches(1)
    section.right_margin = Inches(1)
    section.header_distance = Inches(0.492)
    section.footer_distance = Inches(0.492)

    normal = document.styles["Normal"]
    normal.font.name = BODY_FONT
    normal.font.size = Pt(11)
    normal.font.color.rgb = RGBColor.from_string(BODY_COLOR)
    normal.paragraph_format.space_after = Pt(6)
    normal.paragraph_format.line_spacing = 1.25

    for style_name, size, color, before, after in (
        ("Heading 1", 16, HEADING_BLUE, 18, 10),
        ("Heading 2", 13, HEADING_BLUE, 14, 7),
        ("Heading 3", 12, HEADING_DARK, 10, 5),
    ):
        style = document.styles[style_name]
        style.font.name = BODY_FONT
        style.font.size = Pt(size)
        style.font.bold = True
        style.font.color.rgb = RGBColor.from_string(color)
        style.paragraph_format.space_before = Pt(before)
        style.paragraph_format.space_after = Pt(after)
        style.paragraph_format.keep_with_next = True

    title = document.styles["Title"]
    title.font.name = BODY_FONT
    title.font.size = Pt(20)
    title.font.bold = True
    title.font.color.rgb = RGBColor.from_string(BODY_COLOR)
    title.paragraph_format.space_after = Pt(8)

    for list_style_name in ("List Bullet", "List Number"):
        style = document.styles[list_style_name]
        style.font.name = BODY_FONT
        style.font.size = Pt(11)
        style.paragraph_format.space_after = Pt(4)
        style.paragraph_format.line_spacing = 1.25
        style.paragraph_format.left_indent = Inches(0.375)
        style.paragraph_format.first_line_indent = Inches(-0.188)


def _set_core_properties(document: Document) -> None:
    document.core_properties.title = "Flux LLM KB Dashboard User Manual"
    document.core_properties.subject = "Dashboard, guarded automation, diagnostics, performance, and operator workflows"
    document.core_properties.comments = "Generated from docs/user-guide/dashboard-user-manual.md using compact_reference_guide layout and real dashboard screenshots."
    document.core_properties.author = "Flux LLM KB"
    document.core_properties.keywords = "Flux LLM KB, dashboard, user manual, guarded automation, diagnostics"


def _add_header_footer(document: Document) -> None:
    section = document.sections[0]
    header = section.header.paragraphs[0]
    header.text = "Flux LLM KB Dashboard User Manual"
    header.alignment = WD_ALIGN_PARAGRAPH.RIGHT
    header.runs[0].font.size = Pt(8)
    header.runs[0].font.color.rgb = RGBColor.from_string(MUTED_COLOR)
    footer = section.footer.paragraphs[0]
    footer.text = "Public-safe guide. Screenshots use deterministic fixture data."
    footer.alignment = WD_ALIGN_PARAGRAPH.CENTER
    footer.runs[0].font.size = Pt(8)
    footer.runs[0].font.color.rgb = RGBColor.from_string(MUTED_COLOR)


def _clean_inline(text: str) -> str:
    text = re.sub(r"`([^`]+)`", r"\1", text)
    text = text.replace(r"\|", "|")
    return text


def _add_rich_paragraph(document: Document, text: str, style: str | None = None):
    paragraph = document.add_paragraph(style=style)
    add_rich_text(paragraph, text)
    return paragraph


def add_rich_text(paragraph, text: str) -> None:
    cursor = 0
    for match in re.finditer(r"(\*\*([^*]+)\*\*)|(`([^`]+)`)|(_([^_]+)_)", text):
        if match.start() > cursor:
            paragraph.add_run(_clean_inline(text[cursor:match.start()]))
        token = match.group(2) or match.group(4) or match.group(6) or ""
        run = paragraph.add_run(token)
        if match.group(2):
            run.bold = True
        if match.group(4):
            run.font.name = "Consolas"
            run.font.size = Pt(9)
        if match.group(6):
            run.italic = True
        cursor = match.end()
    if cursor < len(text):
        paragraph.add_run(_clean_inline(text[cursor:]))


def add_callout(document: Document, text: str) -> None:
    table = document.add_table(rows=1, cols=1)
    _set_table_geometry(table, [TABLE_WIDTH_DXA])
    cell = table.cell(0, 0)
    _set_cell_fill(cell, CALLOUT_FILL)
    _set_cell_margins(cell, top=120, bottom=120, start=160, end=160)
    paragraph = cell.paragraphs[0]
    paragraph.paragraph_format.space_after = Pt(0)
    add_rich_text(paragraph, _clean_inline(text))
    document.add_paragraph()


def _add_image(document: Document, alt: str, rel_path: str) -> None:
    image_path = (GUIDE_DIR / rel_path).resolve()
    if not image_path.exists():
        raise FileNotFoundError(f"Referenced screenshot not found: {image_path}")
    paragraph = document.add_paragraph()
    paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = paragraph.add_run()
    run.add_picture(str(image_path), width=Inches(6.3))
    paragraph.paragraph_format.keep_with_next = True


def add_caption(document: Document, caption: str) -> None:
    paragraph = document.add_paragraph()
    paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
    paragraph.paragraph_format.space_before = Pt(0)
    paragraph.paragraph_format.space_after = Pt(8)
    run = paragraph.add_run(_clean_inline(caption))
    run.font.size = Pt(8.5)
    run.font.italic = True
    run.font.color.rgb = RGBColor.from_string(MUTED_COLOR)


def parse_markdown_table(lines: list[str], start: int) -> tuple[list[list[str]], int]:
    table_lines: list[str] = []
    index = start
    while index < len(lines) and lines[index].strip().startswith("|") and lines[index].strip().endswith("|"):
        table_lines.append(lines[index].strip())
        index += 1
    rows: list[list[str]] = []
    for line_index, line in enumerate(table_lines):
        cells = [cell.strip() for cell in line.strip("|").split("|")]
        if line_index == 1 and all(re.fullmatch(r":?-{3,}:?", cell.replace(" ", "")) for cell in cells):
            continue
        rows.append(cells)
    return rows, index


def add_markdown_table(document: Document, rows: list[list[str]]) -> None:
    if not rows:
        return
    max_cols = max(len(row) for row in rows)
    table = document.add_table(rows=len(rows), cols=max_cols)
    table.alignment = WD_TABLE_ALIGNMENT.LEFT
    table.style = "Table Grid"
    column_widths = _column_widths(max_cols)
    _set_table_geometry(table, column_widths)
    for row_index, row in enumerate(rows):
        for col_index in range(max_cols):
            cell = table.cell(row_index, col_index)
            cell.vertical_alignment = WD_CELL_VERTICAL_ALIGNMENT.CENTER
            _set_cell_margins(cell, top=80, bottom=80, start=120, end=120)
            if row_index == 0:
                _set_cell_fill(cell, TABLE_HEADER_FILL)
            paragraph = cell.paragraphs[0]
            paragraph.paragraph_format.space_after = Pt(0)
            paragraph.paragraph_format.line_spacing = 1.15
            text = row[col_index] if col_index < len(row) else ""
            add_rich_text(paragraph, _clean_inline(text))
            for run in paragraph.runs:
                run.font.size = Pt(9.25)
                if row_index == 0:
                    run.bold = True
    document.add_paragraph()


def _column_widths(col_count: int) -> list[int]:
    if col_count == 2:
        return [2700, TABLE_WIDTH_DXA - 2700]
    if col_count == 3:
        return [2100, 2700, TABLE_WIDTH_DXA - 4800]
    if col_count == 4:
        return [1700, 2100, 2600, TABLE_WIDTH_DXA - 6400]
    width = TABLE_WIDTH_DXA // col_count
    widths = [width] * col_count
    widths[-1] += TABLE_WIDTH_DXA - sum(widths)
    return widths


def _set_table_geometry(table, widths: list[int]) -> None:
    table.autofit = False
    tbl_pr = table._tbl.tblPr
    tbl_w = tbl_pr.first_child_found_in("w:tblW")
    if tbl_w is None:
        tbl_w = OxmlElement("w:tblW")
        tbl_pr.insert(0, tbl_w)
    tbl_w.set(qn("w:type"), "dxa")
    tbl_w.set(qn("w:w"), str(TABLE_WIDTH_DXA))
    tbl_ind = tbl_pr.first_child_found_in("w:tblInd")
    if tbl_ind is None:
        tbl_ind = OxmlElement("w:tblInd")
        tbl_pr.append(tbl_ind)
    tbl_ind.set(qn("w:w"), str(TABLE_INDENT_DXA))
    tbl_ind.set(qn("w:type"), "dxa")
    grid = table._tbl.tblGrid
    for child in list(grid):
        grid.remove(child)
    for width in widths:
        grid_col = OxmlElement("w:gridCol")
        grid_col.set(qn("w:w"), str(width))
        grid.append(grid_col)
    for row in table.rows:
        for cell, width in zip(row.cells, widths):
            tc_pr = cell._tc.get_or_add_tcPr()
            tc_w = tc_pr.first_child_found_in("w:tcW")
            if tc_w is None:
                tc_w = OxmlElement("w:tcW")
                tc_pr.insert(0, tc_w)
            tc_w.set(qn("w:type"), "dxa")
            tc_w.set(qn("w:w"), str(width))


def _set_cell_fill(cell, fill: str) -> None:
    tc_pr = cell._tc.get_or_add_tcPr()
    shading = tc_pr.first_child_found_in("w:shd")
    if shading is None:
        shading = OxmlElement("w:shd")
        tc_pr.append(shading)
    shading.set(qn("w:fill"), fill)


def _set_cell_margins(cell, *, top: int, bottom: int, start: int, end: int) -> None:
    tc_pr = cell._tc.get_or_add_tcPr()
    margins = tc_pr.first_child_found_in("w:tcMar")
    if margins is None:
        margins = OxmlElement("w:tcMar")
        tc_pr.append(margins)
    for key, value in {"top": top, "bottom": bottom, "start": start, "end": end}.items():
        node = margins.find(qn(f"w:{key}"))
        if node is None:
            node = OxmlElement(f"w:{key}")
            margins.append(node)
        node.set(qn("w:w"), str(value))
        node.set(qn("w:type"), "dxa")


def build_docx() -> None:
    if not MANUAL_MD.exists():
        raise FileNotFoundError(MANUAL_MD)
    document = Document()
    _set_doc_styles(document)
    _set_core_properties(document)
    _add_header_footer(document)

    lines = MANUAL_MD.read_text(encoding="utf-8").splitlines()
    first_h2 = True
    in_code = False
    ordered_index = 1
    index = 0
    while index < len(lines):
        raw_line = lines[index]
        line = raw_line.rstrip()
        stripped = line.strip()
        if stripped.startswith("```"):
            in_code = not in_code
            index += 1
            continue
        if in_code:
            paragraph = document.add_paragraph(line)
            for run in paragraph.runs:
                run.font.name = "Consolas"
                run.font.size = Pt(8.5)
            index += 1
            continue
        if not stripped:
            ordered_index = 1
            index += 1
            continue
        if stripped.startswith("|") and stripped.endswith("|"):
            ordered_index = 1
            rows, index = parse_markdown_table(lines, index)
            add_markdown_table(document, rows)
            continue
        image_match = re.match(r"!\[(?P<alt>[^\]]+)\]\((?P<path>[^)]+)\)", stripped)
        if image_match:
            ordered_index = 1
            _add_image(document, image_match.group("alt"), image_match.group("path"))
            if index + 1 < len(lines) and lines[index + 1].strip().startswith("_Figure:"):
                add_caption(document, lines[index + 1].strip().strip("_"))
                index += 2
            else:
                add_caption(document, image_match.group("alt"))
                index += 1
            continue
        if stripped.startswith("> "):
            ordered_index = 1
            add_callout(document, stripped[2:])
            index += 1
            continue
        if stripped.startswith("# "):
            ordered_index = 1
            title = document.add_paragraph(style="Title")
            title.alignment = WD_ALIGN_PARAGRAPH.CENTER
            add_rich_text(title, stripped[2:])
            subtitle = document.add_paragraph("Comprehensive operator guide with public-safe real dashboard screenshots")
            subtitle.alignment = WD_ALIGN_PARAGRAPH.CENTER
            subtitle.runs[0].font.size = Pt(10)
            subtitle.runs[0].font.color.rgb = RGBColor.from_string(MUTED_COLOR)
            index += 1
            continue
        if stripped.startswith("## "):
            ordered_index = 1
            if first_h2:
                first_h2 = False
            else:
                document.add_section(WD_SECTION.NEW_PAGE)
            document.add_heading(stripped[3:], level=1)
            index += 1
            continue
        if stripped.startswith("### "):
            ordered_index = 1
            document.add_heading(stripped[4:], level=2)
            index += 1
            continue
        if stripped.startswith("#### "):
            ordered_index = 1
            document.add_heading(stripped[5:], level=3)
            index += 1
            continue
        if stripped.startswith("- "):
            ordered_index = 1
            _add_rich_paragraph(document, stripped[2:], style="List Bullet")
            index += 1
            continue
        numbered = re.match(r"\d+\.\s+(?P<text>.+)", stripped)
        if numbered:
            paragraph = _add_rich_paragraph(document, f"{ordered_index}. {numbered.group('text')}")
            paragraph.paragraph_format.left_indent = Inches(0.32)
            paragraph.paragraph_format.first_line_indent = Inches(-0.32)
            paragraph.paragraph_format.space_after = Pt(4)
            ordered_index += 1
            index += 1
            continue
        ordered_index = 1
        paragraph = _add_rich_paragraph(document, stripped)
        paragraph.paragraph_format.space_after = Pt(6)
        index += 1

    DOCX_PATH.parent.mkdir(parents=True, exist_ok=True)
    document.save(DOCX_PATH)


def main() -> int:
    parser = argparse.ArgumentParser(description="Build the Flux dashboard user guide screenshots and DOCX.")
    parser.add_argument("--capture-screens", action="store_true", help="Capture public-safe screenshots from the real dashboard UI.")
    parser.add_argument("--build-docx", action="store_true", help="Build the DOCX from markdown and screenshots.")
    parser.add_argument("--all", action="store_true", help="Capture screenshots and build the DOCX.")
    args = parser.parse_args()

    if not (args.capture_screens or args.build_docx or args.all):
        args.all = True
    if args.capture_screens or args.all:
        capture_screens()
    if args.build_docx or args.all:
        build_docx()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
