from __future__ import annotations

import base64
import binascii
from dataclasses import dataclass, field
from html import unescape
import importlib.util
import json
import os
from pathlib import Path
from pathlib import PurePosixPath
import re
import shutil
import struct
import tempfile
from typing import Any
from urllib.parse import unquote
from xml.etree import ElementTree
from zipfile import BadZipFile, ZipFile
import zlib

from .crawler import AssetChunk, CorpusPolicy, classify_file
from .processes import run_no_window
from .redaction import redact_text


DIAGRAM_MAX_ZIP_MEMBERS = 200
DIAGRAM_MAX_TOTAL_BYTES = 25 * 1024 * 1024
DIAGRAM_MAX_MEMBER_BYTES = 5 * 1024 * 1024


@dataclass(frozen=True)
class ExtractionResult:
    status: str
    chunks: tuple[AssetChunk, ...] = ()
    metadata: dict[str, Any] = field(default_factory=dict)
    message: str | None = None


def extract_file(path: str | Path, policy: CorpusPolicy) -> ExtractionResult:
    file_path = Path(path).expanduser().resolve()
    classification = classify_file(file_path, policy)
    if classification.file_kind in {"text", "code"}:
        return _extract_text(file_path, policy, extractor=classification.file_kind)
    if classification.file_kind == "document":
        return _extract_document(file_path, policy)
    if classification.file_kind == "diagram":
        return _extract_diagram(file_path)
    if classification.file_kind == "image":
        return _extract_image(file_path)
    if classification.file_kind in {"audio", "video"}:
        return _extract_media(file_path, classification.file_kind)
    return ExtractionResult(
        status="metadata_only",
        metadata={"extractor": classification.file_kind, "mime_type": classification.mime_type},
    )


def extractor_availability() -> dict[str, dict[str, Any]]:
    return {
        "pypdf": _module_check("pypdf"),
        "python_docx": _module_check("docx"),
        "python_pptx": _module_check("pptx"),
        "openpyxl": _module_check("openpyxl"),
        "pillow": _module_check("PIL"),
        "watchdog": _module_check("watchdog"),
        "libreoffice": _first_tool_check("LibreOffice", ("soffice", "libreoffice")),
        "antiword": _tool_check("antiword"),
        "catdoc": _tool_check("catdoc"),
        "wvText": _tool_check("wvText"),
        "word_com": _word_com_check(),
        "ffprobe": _tool_check("ffprobe"),
        "ffmpeg": _tool_check("ffmpeg"),
        "tesseract": _tool_check("tesseract"),
        "faster_whisper": _module_check("faster_whisper"),
    }


def image_metadata(path: str | Path) -> dict[str, Any]:
    metadata: dict[str, Any] = {"extractor": "image"}
    dimensions = _image_dimensions(Path(path))
    if dimensions:
        metadata.update({"width": dimensions[0], "height": dimensions[1]})
    return metadata


def _extract_text(path: Path, policy: CorpusPolicy, *, extractor: str) -> ExtractionResult:
    if path.stat().st_size > policy.max_inline_bytes:
        return ExtractionResult(
            status="blocked_missing_dependency",
            metadata={"extractor": extractor},
            message="text file exceeds inline extraction limit",
        )
    text = path.read_text(encoding="utf-8", errors="replace").strip()
    chunks = _chunks_from_text(text, path.name)
    return ExtractionResult(status="indexed" if chunks else "metadata_only", chunks=chunks, metadata={"extractor": extractor})


def _extract_document(path: Path, policy: CorpusPolicy) -> ExtractionResult:
    ext = path.suffix.lower()
    if ext == ".pdf":
        return _extract_pdf(path)
    if ext == ".docx":
        return _extract_docx(path)
    if ext == ".pptx":
        return _extract_pptx(path)
    if ext == ".xlsx":
        return _extract_xlsx(path)
    if ext in {".doc", ".rtf"}:
        return _extract_legacy_word_document(path)
    return ExtractionResult(status="metadata_only", metadata={"extractor": "document", "extension": ext})


def _extract_pdf(path: Path) -> ExtractionResult:
    try:
        from pypdf import PdfReader
    except ImportError:
        return ExtractionResult(status="blocked_missing_dependency", metadata={"extractor": "pdf"}, message="pypdf not installed")
    reader = PdfReader(str(path))
    text = "\n".join(page.extract_text() or "" for page in reader.pages).strip()
    chunks = _chunks_from_text(text, path.name)
    return ExtractionResult(
        status="indexed" if chunks else "metadata_only",
        chunks=chunks,
        metadata={"extractor": "pdf", "page_count": len(reader.pages)},
    )


def _extract_docx(path: Path) -> ExtractionResult:
    try:
        from docx import Document
    except ImportError:
        return ExtractionResult(status="blocked_missing_dependency", metadata={"extractor": "docx"}, message="python-docx not installed")
    document = Document(str(path))
    text = "\n".join(paragraph.text for paragraph in document.paragraphs).strip()
    chunks = _chunks_from_text(text, path.name)
    return ExtractionResult(status="indexed" if chunks else "metadata_only", chunks=chunks, metadata={"extractor": "docx"})


def _extract_pptx(path: Path) -> ExtractionResult:
    try:
        from pptx import Presentation
    except ImportError:
        return ExtractionResult(status="blocked_missing_dependency", metadata={"extractor": "pptx"}, message="python-pptx not installed")
    presentation = Presentation(str(path))
    parts: list[str] = []
    for slide in presentation.slides:
        for shape in slide.shapes:
            if hasattr(shape, "text") and shape.text:
                parts.append(shape.text)
    chunks = _chunks_from_text("\n".join(parts), path.name)
    return ExtractionResult(
        status="indexed" if chunks else "metadata_only",
        chunks=chunks,
        metadata={"extractor": "pptx", "slide_count": len(presentation.slides)},
    )


def _extract_xlsx(path: Path) -> ExtractionResult:
    try:
        import openpyxl
    except ImportError:
        return ExtractionResult(status="blocked_missing_dependency", metadata={"extractor": "xlsx"}, message="openpyxl not installed")
    workbook = openpyxl.load_workbook(str(path), read_only=True, data_only=True)
    parts: list[str] = []
    for worksheet in workbook.worksheets[:10]:
        parts.append(f"Sheet: {worksheet.title}")
        for row in worksheet.iter_rows(max_row=200, max_col=30, values_only=True):
            values = [str(value) for value in row if value is not None]
            if values:
                parts.append(" | ".join(values))
    chunks = _chunks_from_text("\n".join(parts), path.name)
    return ExtractionResult(
        status="indexed" if chunks else "metadata_only",
        chunks=chunks,
        metadata={"extractor": "xlsx", "sheet_count": len(workbook.worksheets)},
    )


def _extract_legacy_word_document(path: Path) -> ExtractionResult:
    ext = path.suffix.lower()
    metadata = {"extractor": "legacy_document", "extension": ext}
    attempts: list[str] = []

    for extractor_name, extract in (
        ("libreoffice", _extract_with_libreoffice),
        ("antiword", _extract_with_antiword),
        ("catdoc", _extract_with_catdoc),
        ("wvText", _extract_with_wvtext),
        ("word_com", _extract_with_word_com),
    ):
        attempts.append(extractor_name)
        text = extract(path)
        if not text:
            continue
        chunks = _chunks_from_text(text, path.name)
        return ExtractionResult(
            status="indexed" if chunks else "metadata_only",
            chunks=chunks,
            metadata={"extractor": extractor_name, "extension": ext},
        )

    return ExtractionResult(
        status="blocked_missing_dependency",
        metadata={**metadata, "attempted": attempts},
        message="Legacy Word extraction requires LibreOffice, antiword, catdoc, wvText, or Windows Word COM.",
    )


def _extract_with_libreoffice(path: Path) -> str | None:
    command = shutil.which("soffice") or shutil.which("libreoffice")
    if command is None:
        return None
    try:
        with tempfile.TemporaryDirectory(prefix="flux-kb-lo-") as temp_dir:
            result = run_no_window(
                [
                    command,
                    "--headless",
                    "--convert-to",
                    "txt:Text",
                    "--outdir",
                    temp_dir,
                    str(path),
                ],
                text=True,
                capture_output=True,
                timeout=90,
                check=False,
            )
            if result.returncode != 0:
                return None
            output_path = Path(temp_dir) / f"{path.stem}.txt"
            candidates = [output_path] if output_path.exists() else sorted(Path(temp_dir).glob("*.txt"))
            for candidate in candidates:
                text = candidate.read_text(encoding="utf-8", errors="replace").strip()
                if text:
                    return text
    except Exception:
        return None
    return None


def _extract_with_antiword(path: Path) -> str | None:
    return _extract_with_stdout_tool(path, "antiword")


def _extract_with_catdoc(path: Path) -> str | None:
    return _extract_with_stdout_tool(path, "catdoc")


def _extract_with_stdout_tool(path: Path, command_name: str) -> str | None:
    command = shutil.which(command_name)
    if command is None:
        return None
    try:
        result = run_no_window(
            [command, str(path)],
            text=True,
            capture_output=True,
            timeout=60,
            check=False,
        )
    except Exception:
        return None
    if result.returncode != 0:
        return None
    return result.stdout.strip() or None


def _extract_with_wvtext(path: Path) -> str | None:
    command = shutil.which("wvText")
    if command is None:
        return None
    try:
        with tempfile.TemporaryDirectory(prefix="flux-kb-wv-") as temp_dir:
            output_path = Path(temp_dir) / f"{path.stem}.txt"
            result = run_no_window(
                [command, str(path), str(output_path)],
                text=True,
                capture_output=True,
                timeout=60,
                check=False,
            )
            if result.returncode != 0 or not output_path.exists():
                return None
            return output_path.read_text(encoding="utf-8", errors="replace").strip() or None
    except Exception:
        return None


def _extract_with_word_com(path: Path) -> str | None:
    if os.name != "nt":
        return None
    try:
        import pythoncom
        import win32com.client
    except Exception:
        return None

    word = None
    document = None
    initialized = False
    try:
        pythoncom.CoInitialize()
        initialized = True
        word = win32com.client.DispatchEx("Word.Application")
        word.Visible = False
        word.DisplayAlerts = 0
        document = word.Documents.Open(
            FileName=str(path),
            ConfirmConversions=False,
            ReadOnly=True,
            AddToRecentFiles=False,
            Visible=False,
            OpenAndRepair=True,
        )
        return str(document.Content.Text or "").strip() or None
    except Exception:
        return None
    finally:
        if document is not None:
            try:
                document.Close(False)
            except Exception:
                pass
        if word is not None:
            try:
                word.Quit()
            except Exception:
                pass
        if initialized:
            try:
                pythoncom.CoUninitialize()
            except Exception:
                pass


def _extract_diagram(path: Path) -> ExtractionResult:
    name = path.name.lower()
    if path.suffix.lower() in {".vsdm", ".vsdx", ".vssm", ".vssx", ".vstm", ".vstx"}:
        return _extract_vsdx_diagram(path)
    if path.suffix.lower() in {".dio", ".drawio"} or name.endswith((".drawio.svg", ".drawio.png")):
        return _extract_drawio_diagram(path)
    return ExtractionResult(status="metadata_only", metadata={"extractor": "diagram", "format": "unknown"})


def _extract_drawio_diagram(path: Path) -> ExtractionResult:
    metadata = _diagram_metadata("drawio")
    try:
        raw = _read_limited_file(path, DIAGRAM_MAX_TOTAL_BYTES)
        mxfile_xml = _embedded_mxfile_xml(raw)
        if not mxfile_xml:
            metadata["warnings"].append("drawio mxfile payload not found")
            return ExtractionResult(status="metadata_only", metadata=metadata)
        root = ElementTree.fromstring(mxfile_xml)
    except ValueError as exc:
        return ExtractionResult(status="failed", metadata=metadata, message=str(exc))
    except ElementTree.ParseError as exc:
        return ExtractionResult(status="failed", metadata=metadata, message=f"drawio XML parse failed: {exc}")

    page_blocks: list[str] = []
    try:
        pages = _drawio_pages(root)
    except ValueError as exc:
        return ExtractionResult(status="failed", metadata=metadata, message=str(exc))
    metadata["page_count"] = len(pages)
    for page_name, page_root in pages:
        if page_root is None:
            metadata["warnings"].append(f"page payload could not be decoded: {page_name}")
            continue
        page_lines, page_stats = _summarize_drawio_page(page_name, page_root)
        _merge_diagram_stats(metadata, page_stats)
        if page_lines:
            page_blocks.append("\n".join([f"Page: {page_name}", *page_lines]))

    text = "\n\n".join(page_blocks)
    chunks = _chunks_from_text(text, path.name, modality="diagram")
    return ExtractionResult(
        status="indexed" if chunks else "metadata_only",
        chunks=chunks,
        metadata=metadata,
    )


def _extract_vsdx_diagram(path: Path) -> ExtractionResult:
    metadata = _diagram_metadata("vsdx")
    try:
        with ZipFile(path) as archive:
            infos = archive.infolist()
            if len(infos) > DIAGRAM_MAX_ZIP_MEMBERS:
                raise ValueError(f"diagram container has too many members: {len(infos)}")
            total_bytes = 0
            page_members = []
            for info in infos:
                member_name = _safe_zip_member_name(info.filename)
                if member_name is None:
                    raise ValueError(f"unsafe diagram container member: {info.filename}")
                if info.file_size > DIAGRAM_MAX_MEMBER_BYTES:
                    raise ValueError(f"diagram member exceeds size limit: {member_name}")
                total_bytes += int(info.file_size or 0)
                if total_bytes > DIAGRAM_MAX_TOTAL_BYTES:
                    raise ValueError("diagram container exceeds readable XML limit")
                normalized = member_name.lower()
                if normalized.startswith("visio/pages/") and normalized.endswith(".xml"):
                    page_members.append((member_name, info))

            page_blocks: list[str] = []
            metadata["page_count"] = len(page_members)
            for member_name, info in page_members:
                xml_text = archive.read(info).decode("utf-8", errors="replace")
                page_name = PurePosixPath(member_name).stem
                try:
                    page_root = ElementTree.fromstring(xml_text)
                except ElementTree.ParseError as exc:
                    metadata["warnings"].append(f"{member_name}: XML parse failed: {exc}")
                    continue
                page_lines, page_stats = _summarize_vsdx_page(page_name, page_root)
                _merge_diagram_stats(metadata, page_stats)
                if page_lines:
                    page_blocks.append("\n".join([f"Page: {page_name}", *page_lines]))
    except BadZipFile as exc:
        return ExtractionResult(status="failed", metadata=metadata, message=f"vsdx ZIP parse failed: {exc}")
    except ValueError as exc:
        return ExtractionResult(status="failed", metadata=metadata, message=str(exc))

    if metadata["page_count"] == 0:
        metadata["warnings"].append("no Visio page XML members found")
    text = "\n\n".join(page_blocks)
    chunks = _chunks_from_text(text, path.name, modality="diagram")
    return ExtractionResult(
        status="indexed" if chunks else "metadata_only",
        chunks=chunks,
        metadata=metadata,
    )


def _diagram_metadata(format_name: str) -> dict[str, Any]:
    return {
        "extractor": "diagram",
        "format": format_name,
        "page_count": 0,
        "shape_count": 0,
        "connector_count": 0,
        "text_count": 0,
        "warnings": [],
    }


def _read_limited_file(path: Path, limit: int) -> bytes:
    size = path.stat().st_size
    if size > limit:
        raise ValueError("diagram file exceeds readable size limit")
    return path.read_bytes()


def _embedded_mxfile_xml(raw: bytes) -> str:
    text = raw.decode("utf-8", errors="ignore")
    for candidate in (text, unescape(text)):
        start = candidate.find("<mxfile")
        end = candidate.find("</mxfile>")
        if start >= 0 and end >= start:
            return candidate[start : end + len("</mxfile>")]
        if candidate.lstrip().startswith("<mxfile"):
            return candidate.strip()
    return ""


def _drawio_pages(root: ElementTree.Element) -> list[tuple[str, ElementTree.Element | None]]:
    root_name = _local_name(root.tag)
    if root_name == "mxGraphModel":
        return [("Page 1", root)]
    diagrams = [element for element in root.iter() if _local_name(element.tag) == "diagram"]
    pages: list[tuple[str, ElementTree.Element | None]] = []
    for index, diagram in enumerate(diagrams, start=1):
        page_name = _clean_diagram_text(diagram.attrib.get("name") or f"Page {index}") or f"Page {index}"
        child_models = [child for child in list(diagram) if _local_name(child.tag) == "mxGraphModel"]
        if child_models:
            pages.append((page_name, child_models[0]))
            continue
        decoded = _decode_drawio_payload("".join(diagram.itertext()).strip())
        if decoded:
            try:
                pages.append((page_name, ElementTree.fromstring(decoded)))
            except ElementTree.ParseError:
                pages.append((page_name, None))
        else:
            pages.append((page_name, None))
    return pages


def _decode_drawio_payload(value: str) -> str:
    text = value.strip()
    if not text:
        return ""
    candidates = [unescape(text), unquote(unescape(text))]
    for encoded in {text, unquote(unescape(text))}:
        try:
            compressed = base64.b64decode("".join(encoded.split()), validate=True)
        except (binascii.Error, ValueError):
            continue
        if len(compressed) > DIAGRAM_MAX_MEMBER_BYTES:
            raise ValueError("embedded drawio payload exceeds size limit")
        for wbits in (-15, zlib.MAX_WBITS):
            try:
                inflated = zlib.decompress(compressed, wbits)
            except zlib.error:
                continue
            if len(inflated) > DIAGRAM_MAX_MEMBER_BYTES:
                raise ValueError("inflated drawio payload exceeds size limit")
            decoded = inflated.decode("utf-8", errors="replace")
            candidates.extend([decoded, unquote(decoded)])
    for candidate in candidates:
        cleaned = candidate.strip()
        if cleaned.startswith("<"):
            return cleaned
    return ""


def _summarize_drawio_page(page_name: str, root: ElementTree.Element) -> tuple[list[str], dict[str, int]]:
    lines: list[str] = []
    stats = {"shape_count": 0, "connector_count": 0, "text_count": 0}
    for cell in root.iter():
        if _local_name(cell.tag) != "mxCell":
            continue
        value = _clean_diagram_text(cell.attrib.get("value"))
        link = _clean_diagram_text(cell.attrib.get("link"))
        source = _clean_diagram_text(cell.attrib.get("source"))
        target = _clean_diagram_text(cell.attrib.get("target"))
        is_connector = cell.attrib.get("edge") == "1" or bool(source or target)
        if is_connector:
            stats["connector_count"] += 1
            parts = []
            if value:
                parts.append(value)
                stats["text_count"] += 1
            if source or target:
                parts.append(f"{source or '?'} -> {target or '?'}")
            if link:
                parts.append(f"link {link}")
                stats["text_count"] += 1
            if parts:
                lines.append(f"Connector: {'; '.join(parts)}")
            continue
        if value or link:
            stats["shape_count"] += 1
            parts = []
            if value:
                parts.append(value)
                stats["text_count"] += 1
            if link:
                parts.append(f"link {link}")
                stats["text_count"] += 1
            lines.append(f"Shape: {'; '.join(parts)}")
    return lines, stats


def _summarize_vsdx_page(page_name: str, root: ElementTree.Element) -> tuple[list[str], dict[str, int]]:
    lines: list[str] = []
    stats = {"shape_count": 0, "connector_count": 0, "text_count": 0}
    for shape in root.iter():
        if _local_name(shape.tag) != "Shape":
            continue
        label = _shape_label(shape)
        text = _shape_text(shape)
        if not text:
            continue
        stats["shape_count"] += 1
        stats["text_count"] += 1
        lines.append(f"Shape {label}: {text}" if label else f"Shape: {text}")
    for connect in root.iter():
        if _local_name(connect.tag) != "Connect":
            continue
        stats["connector_count"] += 1
        source = _clean_diagram_text(connect.attrib.get("FromSheet"))
        target = _clean_diagram_text(connect.attrib.get("ToSheet"))
        if source or target:
            lines.append(f"Connector: {source or '?'} -> {target or '?'}")
    return lines, stats


def _shape_label(shape: ElementTree.Element) -> str:
    return _clean_diagram_text(
        shape.attrib.get("NameU")
        or shape.attrib.get("Name")
        or shape.attrib.get("ID")
        or shape.attrib.get("id")
    )


def _shape_text(shape: ElementTree.Element) -> str:
    parts: list[str] = []
    for element in shape.iter():
        if _local_name(element.tag) == "Text":
            text = _clean_diagram_text(" ".join(element.itertext()))
            if text:
                parts.append(text)
    return " ".join(dict.fromkeys(parts))


def _merge_diagram_stats(metadata: dict[str, Any], stats: dict[str, int]) -> None:
    for key in ("shape_count", "connector_count", "text_count"):
        metadata[key] = int(metadata.get(key) or 0) + int(stats.get(key) or 0)


def _safe_zip_member_name(name: str) -> str | None:
    normalized = str(name or "").replace("\\", "/")
    if not normalized or normalized.startswith("/"):
        return None
    path = PurePosixPath(normalized)
    if path.is_absolute() or any(part in {"", ".", ".."} for part in path.parts):
        return None
    return path.as_posix()


def _clean_diagram_text(value: Any) -> str:
    text = unquote(unescape(str(value or "")))
    text = re.sub(r"<[^>]+>", " ", text)
    return " ".join(text.split())


def _local_name(tag: str) -> str:
    return str(tag).rsplit("}", 1)[-1]


def _extract_image(path: Path) -> ExtractionResult:
    metadata = image_metadata(path)
    text = _ocr_image(path)
    chunks = _chunks_from_text(text or "", path.name, modality="ocr")
    return ExtractionResult(status="indexed" if chunks else "metadata_only", chunks=chunks, metadata=metadata)


def _extract_media(path: Path, file_kind: str) -> ExtractionResult:
    metadata: dict[str, Any] = {"extractor": file_kind}
    sidecar = _read_sidecar_transcript(path)
    if sidecar:
        chunks = _chunks_from_text(sidecar, path.name, modality="transcript")
        return ExtractionResult(status="indexed", chunks=chunks, metadata={**metadata, "transcript_source": "sidecar"})
    ffprobe = shutil.which("ffprobe")
    if ffprobe is None:
        return ExtractionResult(status="blocked_missing_dependency", metadata=metadata, message="ffprobe command not found")
    try:
        result = run_no_window(
            [
                ffprobe,
                "-v",
                "error",
                "-show_format",
                "-show_streams",
                "-of",
                "json",
                str(path),
            ],
            text=True,
            capture_output=True,
            timeout=30,
            check=False,
        )
    except Exception as exc:  # pragma: no cover - environment-specific
        return ExtractionResult(status="failed", metadata=metadata, message=str(exc))
    if result.returncode != 0:
        return ExtractionResult(status="failed", metadata=metadata, message=result.stderr.strip() or "ffprobe failed")
    try:
        probed = json.loads(result.stdout or "{}")
    except json.JSONDecodeError:
        probed = {}
    metadata["ffprobe"] = probed
    return ExtractionResult(status="metadata_only", metadata=metadata)


def _chunks_from_text(text: str, title: str, *, modality: str = "text") -> tuple[AssetChunk, ...]:
    cleaned = text.strip()
    if not cleaned:
        return ()
    redacted, _ = redact_text(cleaned)
    chunks: list[AssetChunk] = []
    for index, start in enumerate(range(0, len(redacted), 4000)):
        body = redacted[start : start + 4000].strip()
        if body:
            chunks.append(
                AssetChunk(
                    chunk_index=index,
                    title=title,
                    body=body,
                    modality=modality,
                    locator=f"char:{start}-{start + len(body)}",
                    token_estimate=max(1, len(body.split())),
                )
            )
    return tuple(chunks)


def _image_dimensions(path: Path) -> tuple[int, int] | None:
    try:
        from PIL import Image

        with Image.open(path) as image:
            return image.size
    except Exception:
        return _png_dimensions(path)


def _png_dimensions(path: Path) -> tuple[int, int] | None:
    try:
        with path.open("rb") as handle:
            header = handle.read(24)
        if header[:8] != b"\x89PNG\r\n\x1a\n":
            return None
        return struct.unpack(">II", header[16:24])
    except Exception:
        return None


def _ocr_image(path: Path) -> str | None:
    tesseract = shutil.which("tesseract")
    if tesseract is None:
        return None
    try:
        result = run_no_window(
            [tesseract, str(path), "stdout"],
            text=True,
            capture_output=True,
            timeout=30,
            check=False,
        )
    except Exception:
        return None
    if result.returncode != 0:
        return None
    return result.stdout.strip() or None


def _read_sidecar_transcript(path: Path) -> str | None:
    for suffix in (".txt", ".md", ".vtt", ".srt"):
        sidecar = path.with_suffix(path.suffix + suffix)
        if sidecar.exists():
            return sidecar.read_text(encoding="utf-8", errors="replace").strip()
    return None


def _module_check(module_name: str) -> dict[str, Any]:
    ok = importlib.util.find_spec(module_name) is not None
    return {"ok": ok, "message": "available" if ok else f"{module_name} not installed"}


def _tool_check(command: str) -> dict[str, Any]:
    path = shutil.which(command)
    return {"ok": path is not None, "message": path or f"{command} command not found"}


def _first_tool_check(label: str, commands: tuple[str, ...]) -> dict[str, Any]:
    for command in commands:
        path = shutil.which(command)
        if path is not None:
            return {"ok": True, "message": path}
    return {"ok": False, "message": f"{label} command not found"}


def _word_com_check() -> dict[str, Any]:
    if os.name != "nt":
        return {"ok": False, "message": "Windows Word COM is only available on Windows"}
    ok = importlib.util.find_spec("win32com") is not None
    return {"ok": ok, "message": "pywin32 available" if ok else "pywin32 not installed"}
