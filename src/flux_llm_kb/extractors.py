from __future__ import annotations

import base64
import bz2
import binascii
from dataclasses import dataclass, field
import gzip
import hashlib
from html import unescape
import importlib.util
import json
import lzma
import mimetypes
import os
from pathlib import Path
from pathlib import PurePosixPath
import re
import shutil
import struct
import tarfile
import tempfile
from typing import Any, Callable
from urllib.parse import quote, unquote
from xml.etree import ElementTree
from zipfile import BadZipFile, ZipFile
import zlib

from .acceleration import resolve_cache_layout
from .crawler import (
    ARCHIVE_COMPOUND_SUFFIXES,
    ARCHIVE_EXTENSIONS,
    AUDIO_EXTENSIONS,
    AssetChunk,
    CODE_EXTENSIONS,
    CONTAINER_EXTENSIONS,
    DIAGRAM_COMPOUND_SUFFIXES,
    DIAGRAM_EXTENSIONS,
    DOCUMENT_EXTENSIONS,
    IMAGE_EXTENSIONS,
    TEXT_EXTENSIONS,
    VIDEO_EXTENSIONS,
    CorpusPolicy,
    classify_file,
)
from .processes import run_no_window
from .redaction import redact_text


DIAGRAM_MAX_ZIP_MEMBERS = 200
DIAGRAM_MAX_TOTAL_BYTES = 25 * 1024 * 1024
DIAGRAM_MAX_MEMBER_BYTES = 5 * 1024 * 1024

LEGACY_WORD_EXTENSIONS = {".doc", ".dot", ".rtf"}
CONVERTED_WORD_EXTENSIONS = {".docm", ".dotm", ".dotx", ".odt", ".ott"}
OPENPYXL_EXTENSIONS = {".xlsx", ".xlsm", ".xltm", ".xltx"}
LEGACY_SPREADSHEET_EXTENSIONS = {".xls", ".xlsb", ".xlt"}
OPENDOCUMENT_SPREADSHEET_EXTENSIONS = {".ods", ".ots"}
PPTX_PACKAGE_EXTENSIONS = {".pptx", ".pptm", ".potm", ".potx", ".ppsm", ".ppsx"}
LEGACY_PRESENTATION_EXTENSIONS = {".ppt", ".pot", ".pps"}
OPENDOCUMENT_PRESENTATION_EXTENSIONS = {".odp", ".otp"}
OCR_CACHE_SCHEMA = "flux-ocr-cache-v1"
ASR_CACHE_SCHEMA = "flux-asr-cache-v1"
OCR_MAX_PDF_PAGES = 25
OCR_PDF_DPI = 200
OCR_TIMEOUT_SECONDS = 30
ASR_AUDIO_SAMPLE_RATE = 16000
ASR_FFMPEG_TIMEOUT_SECONDS = 300


@dataclass(frozen=True)
class ContainerChildAsset:
    member_path: str
    file_kind: str
    mime_type: str | None
    extension: str
    size_bytes: int
    quick_hash: str
    content_hash: str | None
    extraction_tier: str
    extraction_status: str
    chunks: tuple[AssetChunk, ...] = ()
    metadata: dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class ExtractionResult:
    status: str
    chunks: tuple[AssetChunk, ...] = ()
    child_assets: tuple[ContainerChildAsset, ...] = ()
    metadata: dict[str, Any] = field(default_factory=dict)
    message: str | None = None


@dataclass(frozen=True)
class OcrResult:
    status: str
    text: str = ""
    metadata: dict[str, Any] = field(default_factory=dict)
    message: str | None = None


@dataclass(frozen=True)
class AsrResult:
    status: str
    text: str = ""
    metadata: dict[str, Any] = field(default_factory=dict)
    message: str | None = None


def extract_file(path: str | Path, policy: CorpusPolicy) -> ExtractionResult:
    file_path = Path(path).expanduser().resolve()
    classification = classify_file(file_path, policy)
    if classification.file_kind in {"text", "code"}:
        return _extract_text(file_path, policy, extractor=classification.file_kind)
    if classification.file_kind == "document":
        return _extract_document(file_path, policy)
    if classification.file_kind in {"archive", "container"}:
        return _extract_container(file_path, policy, container_kind=classification.file_kind)
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
        "excel_com": _excel_com_check(),
        "powerpoint_com": _powerpoint_com_check(),
        "pdftoppm": _tool_check("pdftoppm"),
        "seven_zip": _first_tool_check("7-Zip", ("7z", "7zz")),
        "bsdtar": _tool_check("bsdtar"),
        "unar": _tool_check("unar"),
        "unrar": _tool_check("unrar"),
        "zstd": _tool_check("zstd"),
        "lz4": _tool_check("lz4"),
        "ar": _tool_check("ar"),
        "rpm2cpio": _tool_check("rpm2cpio"),
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
    if ext in LEGACY_WORD_EXTENSIONS:
        return _extract_legacy_word_document(path)
    if ext in CONVERTED_WORD_EXTENSIONS:
        return _extract_converted_word_document(path)
    if ext in OPENPYXL_EXTENSIONS:
        return _extract_xlsx(path, extractor=ext.lstrip("."))
    if ext in LEGACY_SPREADSHEET_EXTENSIONS:
        return _extract_legacy_spreadsheet(path)
    if ext in OPENDOCUMENT_SPREADSHEET_EXTENSIONS:
        return _extract_opendocument_spreadsheet(path)
    if ext in PPTX_PACKAGE_EXTENSIONS:
        return _extract_pptx(path, extractor=ext.lstrip("."))
    if ext in LEGACY_PRESENTATION_EXTENSIONS:
        return _extract_legacy_presentation(path)
    if ext in OPENDOCUMENT_PRESENTATION_EXTENSIONS:
        return _extract_opendocument_presentation(path)
    return ExtractionResult(status="metadata_only", metadata={"extractor": "document", "extension": ext})


def _extract_pdf(path: Path) -> ExtractionResult:
    try:
        from pypdf import PdfReader
    except ImportError:
        return ExtractionResult(status="blocked_missing_dependency", metadata={"extractor": "pdf"}, message="pypdf not installed")
    reader = PdfReader(str(path))
    page_count = len(reader.pages)
    text = "\n".join(page.extract_text() or "" for page in reader.pages).strip()
    chunks = _chunks_from_text(text, path.name)
    if chunks:
        return ExtractionResult(
            status="indexed",
            chunks=chunks,
            metadata={
                "extractor": "pdf",
                "page_count": page_count,
                "ocr": {
                    "status": "skipped_embedded_text",
                    "page_count": page_count,
                    "pages_attempted": 0,
                    "cache_hits": 0,
                    "cache_misses": 0,
                },
            },
        )
    ocr = _ocr_pdf(path, page_count=page_count)
    metadata = {"extractor": "pdf", "page_count": page_count, "ocr": ocr.metadata}
    if ocr.status == "blocked_missing_dependency":
        return ExtractionResult(status="blocked_missing_dependency", metadata=metadata, message=ocr.message)
    if ocr.status == "failed":
        return ExtractionResult(status="failed", metadata=metadata, message=ocr.message)
    chunks = _chunks_from_text(ocr.text, path.name, modality="ocr")
    return ExtractionResult(status="indexed" if chunks else "metadata_only", chunks=chunks, metadata=metadata, message=ocr.message)


def _extract_docx(path: Path, *, extractor: str = "docx") -> ExtractionResult:
    try:
        from docx import Document
    except ImportError:
        return ExtractionResult(status="blocked_missing_dependency", metadata={"extractor": extractor}, message="python-docx not installed")
    document = Document(str(path))
    text = "\n".join(paragraph.text for paragraph in document.paragraphs).strip()
    chunks = _chunks_from_text(text, path.name)
    return ExtractionResult(status="indexed" if chunks else "metadata_only", chunks=chunks, metadata={"extractor": extractor})


def _extract_pptx(path: Path, *, extractor: str = "pptx") -> ExtractionResult:
    try:
        from pptx import Presentation
    except ImportError:
        return ExtractionResult(status="blocked_missing_dependency", metadata={"extractor": extractor}, message="python-pptx not installed")
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
        metadata={"extractor": extractor, "slide_count": len(presentation.slides)},
    )


def _extract_xlsx(path: Path, *, extractor: str = "xlsx") -> ExtractionResult:
    try:
        import openpyxl
    except ImportError:
        return ExtractionResult(status="blocked_missing_dependency", metadata={"extractor": extractor}, message="openpyxl not installed")
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
        metadata={"extractor": extractor, "sheet_count": len(workbook.worksheets)},
    )


def _extract_converted_word_document(path: Path) -> ExtractionResult:
    result = _extract_via_libreoffice_conversion(
        path,
        target_format="docx",
        target_suffix=".docx",
        read_converted=lambda converted_path: _extract_docx(converted_path),
    )
    if result is not None:
        return result
    ext = path.suffix.lower()
    return ExtractionResult(
        status="blocked_missing_dependency",
        metadata={"extractor": "word_document", "extension": ext, "attempted": ["libreoffice"]},
        message="Word-like document extraction requires LibreOffice for this format.",
    )


def _extract_legacy_spreadsheet(path: Path) -> ExtractionResult:
    ext = path.suffix.lower()
    result = _extract_via_libreoffice_conversion(
        path,
        target_format="xlsx",
        target_suffix=".xlsx",
        read_converted=lambda converted_path: _extract_xlsx(converted_path),
    )
    if result is not None and result.status != "blocked_missing_dependency":
        return result
    text = _extract_with_excel_com(path)
    if text:
        chunks = _chunks_from_text(text, path.name)
        return ExtractionResult(
            status="indexed" if chunks else "metadata_only",
            chunks=chunks,
            metadata={"extractor": "excel_com", "source_extension": ext},
        )
    if result is not None:
        return result
    return ExtractionResult(
        status="blocked_missing_dependency",
        metadata={"extractor": "legacy_spreadsheet", "extension": ext, "attempted": ["libreoffice", "excel_com"]},
        message="Legacy spreadsheet extraction requires LibreOffice or Windows Excel COM.",
    )


def _extract_opendocument_spreadsheet(path: Path) -> ExtractionResult:
    ext = path.suffix.lower()
    result = _extract_via_libreoffice_conversion(
        path,
        target_format="xlsx",
        target_suffix=".xlsx",
        read_converted=lambda converted_path: _extract_xlsx(converted_path),
    )
    if result is not None:
        return result
    return ExtractionResult(
        status="blocked_missing_dependency",
        metadata={"extractor": "opendocument_spreadsheet", "extension": ext, "attempted": ["libreoffice"]},
        message="OpenDocument spreadsheet extraction requires LibreOffice.",
    )


def _extract_legacy_presentation(path: Path) -> ExtractionResult:
    ext = path.suffix.lower()
    result = _extract_via_libreoffice_conversion(
        path,
        target_format="pptx",
        target_suffix=".pptx",
        read_converted=lambda converted_path: _extract_pptx(converted_path),
    )
    if result is not None and result.status != "blocked_missing_dependency":
        return result
    text = _extract_with_powerpoint_com(path)
    if text:
        chunks = _chunks_from_text(text, path.name)
        return ExtractionResult(
            status="indexed" if chunks else "metadata_only",
            chunks=chunks,
            metadata={"extractor": "powerpoint_com", "source_extension": ext},
        )
    if result is not None:
        return result
    return ExtractionResult(
        status="blocked_missing_dependency",
        metadata={"extractor": "legacy_presentation", "extension": ext, "attempted": ["libreoffice", "powerpoint_com"]},
        message="Legacy presentation extraction requires LibreOffice or Windows PowerPoint COM.",
    )


def _extract_opendocument_presentation(path: Path) -> ExtractionResult:
    ext = path.suffix.lower()
    result = _extract_via_libreoffice_conversion(
        path,
        target_format="pptx",
        target_suffix=".pptx",
        read_converted=lambda converted_path: _extract_pptx(converted_path),
    )
    if result is not None:
        return result
    return ExtractionResult(
        status="blocked_missing_dependency",
        metadata={"extractor": "opendocument_presentation", "extension": ext, "attempted": ["libreoffice"]},
        message="OpenDocument presentation extraction requires LibreOffice.",
    )


def _extract_via_libreoffice_conversion(
    path: Path,
    *,
    target_format: str,
    target_suffix: str,
    read_converted: Callable[[Path], ExtractionResult],
) -> ExtractionResult | None:
    with tempfile.TemporaryDirectory(prefix="flux-kb-lo-") as temp_dir:
        converted_path = _convert_with_libreoffice(
            path,
            target_format=target_format,
            target_suffix=target_suffix,
            output_dir=Path(temp_dir),
        )
        if converted_path is None:
            return None
        result = read_converted(converted_path)
        metadata = {
            **result.metadata,
            "extractor": "libreoffice",
            "source_extension": path.suffix.lower(),
            "converted_extension": target_suffix,
        }
        return ExtractionResult(
            status=result.status,
            chunks=result.chunks,
            metadata=metadata,
            message=result.message,
        )


def _convert_with_libreoffice(
    path: Path,
    *,
    target_format: str,
    target_suffix: str,
    output_dir: Path,
) -> Path | None:
    command = shutil.which("soffice") or shutil.which("libreoffice")
    if command is None:
        return None
    try:
        result = run_no_window(
            [
                command,
                "--headless",
                "--convert-to",
                target_format,
                "--outdir",
                str(output_dir),
                str(path),
            ],
            text=True,
            capture_output=True,
            timeout=120,
            check=False,
        )
        if result.returncode != 0:
            return None
        output_path = output_dir / f"{path.stem}{target_suffix}"
        candidates = [output_path] if output_path.exists() else sorted(output_dir.glob(f"*{target_suffix}"))
        return candidates[0] if candidates else None
    except Exception:
        return None


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


def _extract_with_excel_com(path: Path) -> str | None:
    if os.name != "nt":
        return None
    try:
        import pythoncom
        import win32com.client
    except Exception:
        return None

    excel = None
    workbook = None
    initialized = False
    try:
        pythoncom.CoInitialize()
        initialized = True
        excel = win32com.client.DispatchEx("Excel.Application")
        excel.Visible = False
        excel.DisplayAlerts = False
        workbook = excel.Workbooks.Open(
            Filename=str(path),
            ReadOnly=True,
            UpdateLinks=0,
            AddToMru=False,
        )
        parts: list[str] = []
        sheet_count = min(int(workbook.Worksheets.Count), 10)
        for sheet_index in range(1, sheet_count + 1):
            worksheet = workbook.Worksheets(sheet_index)
            parts.append(f"Sheet: {worksheet.Name}")
            for row in _com_table_rows(worksheet.UsedRange.Value, max_rows=200, max_cols=30):
                if row:
                    parts.append(" | ".join(row))
        return "\n".join(parts).strip() or None
    except Exception:
        return None
    finally:
        if workbook is not None:
            try:
                workbook.Close(False)
            except Exception:
                pass
        if excel is not None:
            try:
                excel.Quit()
            except Exception:
                pass
        if initialized:
            try:
                pythoncom.CoUninitialize()
            except Exception:
                pass


def _extract_with_powerpoint_com(path: Path) -> str | None:
    if os.name != "nt":
        return None
    try:
        import pythoncom
        import win32com.client
    except Exception:
        return None

    powerpoint = None
    presentation = None
    initialized = False
    try:
        pythoncom.CoInitialize()
        initialized = True
        powerpoint = win32com.client.DispatchEx("PowerPoint.Application")
        presentation = powerpoint.Presentations.Open(
            FileName=str(path),
            ReadOnly=True,
            Untitled=False,
            WithWindow=False,
        )
        parts: list[str] = []
        slide_count = min(int(presentation.Slides.Count), 200)
        for slide_index in range(1, slide_count + 1):
            slide = presentation.Slides(slide_index)
            slide_parts: list[str] = []
            for shape_index in range(1, int(slide.Shapes.Count) + 1):
                shape = slide.Shapes(shape_index)
                if bool(getattr(shape, "HasTextFrame", False)) and bool(shape.TextFrame.HasText):
                    text = str(shape.TextFrame.TextRange.Text or "").strip()
                    if text:
                        slide_parts.append(" ".join(text.split()))
            if slide_parts:
                parts.append(f"Slide {slide_index}: {' '.join(slide_parts)}")
        return "\n".join(parts).strip() or None
    except Exception:
        return None
    finally:
        if presentation is not None:
            try:
                presentation.Close()
            except Exception:
                pass
        if powerpoint is not None:
            try:
                powerpoint.Quit()
            except Exception:
                pass
        if initialized:
            try:
                pythoncom.CoUninitialize()
            except Exception:
                pass


def _com_table_rows(value: Any, *, max_rows: int, max_cols: int) -> list[list[str]]:
    if value is None:
        return []
    if not isinstance(value, tuple):
        return [[str(value)]]
    if not value:
        return []
    if not isinstance(value[0], tuple):
        rows = (value,)
    else:
        rows = value
    normalized: list[list[str]] = []
    for row in rows[:max_rows]:
        cells = row if isinstance(row, tuple) else (row,)
        values = [str(cell) for cell in cells[:max_cols] if cell is not None]
        if values:
            normalized.append(values)
    return normalized


ZIP_CONTAINER_EXTENSIONS = {
    ".apk",
    ".ear",
    ".egg",
    ".ipa",
    ".jar",
    ".nupkg",
    ".vsix",
    ".war",
    ".whl",
    ".xpi",
    ".zip",
}
TAR_CONTAINER_EXTENSIONS = {".crate", ".gem", ".tar", ".tgz"}
STREAM_CONTAINER_FORMATS = {".bz2": "bzip2", ".gz": "gzip", ".xz": "xz"}
OPTIONAL_CONTAINER_TOOLS = {
    "7z": ("7z", "7zz", "bsdtar", "unar"),
    "rar": ("unrar", "7z", "7zz", "bsdtar", "unar"),
    "cab": ("7z", "7zz", "bsdtar", "unar"),
    "iso": ("7z", "7zz", "bsdtar", "unar"),
    "dmg": ("7z", "7zz", "bsdtar", "unar"),
    "zst": ("zstd", "bsdtar"),
    "lz4": ("lz4", "bsdtar"),
    "ar": ("ar", "bsdtar"),
    "cpio": ("bsdtar",),
    "deb": ("ar", "bsdtar"),
    "rpm": ("rpm2cpio", "bsdtar"),
    "crx": ("7z", "7zz", "bsdtar", "unar"),
}
TEXT_MEMBER_NAMES = {"changelog", "copying", "license", "metadata", "notice", "readme"}
TEXT_MEMBER_EXTENSIONS = TEXT_EXTENSIONS | {".err", ".log", ".out", ".trace"}


def _extract_container(path: Path, policy: CorpusPolicy, *, container_kind: str) -> ExtractionResult:
    format_name = _container_format(path)
    if format_name == "zip":
        return _extract_zip_container(path, policy, container_kind=container_kind)
    if format_name == "tar":
        return _extract_tar_container(path, policy, container_kind=container_kind)
    if format_name in STREAM_CONTAINER_FORMATS.values():
        return _extract_stream_container(path, policy, container_kind=container_kind, format_name=format_name)
    if format_name in {"zst", "lz4"}:
        stream_result = _extract_tool_stream_container(path, policy, container_kind=container_kind, format_name=format_name)
        if stream_result is not None:
            return stream_result
    optional_result = _extract_optional_tool_container(path, policy, container_kind=container_kind, format_name=format_name)
    if optional_result is not None:
        return optional_result
    attempted = list(OPTIONAL_CONTAINER_TOOLS.get(format_name, ()))
    return ExtractionResult(
        status="blocked_missing_dependency",
        metadata=_container_metadata(format_name, container_kind, attempted=attempted),
        message=f"{format_name} extraction requires a local tool: {', '.join(attempted) or format_name}.",
    )


def _container_format(path: Path) -> str:
    name = path.name.lower()
    ext = path.suffix.lower()
    if ext in ZIP_CONTAINER_EXTENSIONS:
        return "zip"
    if name.endswith((".tar.zst", ".tar.lz4")):
        return ext.lstrip(".")
    if ext in TAR_CONTAINER_EXTENSIONS or name.endswith((".tar.gz", ".tar.bz2", ".tar.xz")):
        return "tar"
    if ext in STREAM_CONTAINER_FORMATS:
        return STREAM_CONTAINER_FORMATS[ext]
    return ext.lstrip(".") or "unknown"


def _extract_zip_container(path: Path, policy: CorpusPolicy, *, container_kind: str) -> ExtractionResult:
    format_name = "zip"
    metadata = _container_metadata(format_name, container_kind)
    try:
        with ZipFile(path) as archive:
            infos = [info for info in archive.infolist() if not info.is_dir()]
            children: list[ContainerChildAsset] = []
            total_bytes = 0
            for index, info in enumerate(infos):
                member_path = _safe_container_member_name(info.filename)
                if member_path is None:
                    return _failed_container_result(metadata, f"unsafe container member: {info.filename}")
                if info.flag_bits & 0x1:
                    return _failed_container_result(metadata, f"encrypted container member is not supported: {member_path}")
                size = int(info.file_size or 0)
                cap_message = _container_cap_message(policy, member_count=index + 1, member_size=size, total_bytes=total_bytes + size)
                if cap_message:
                    metadata["member_count"] = len(infos)
                    return _metadata_only_container_result(metadata, cap_message)
                total_bytes += size
                data = archive.read(info)
                children.append(
                    _container_child_from_bytes(
                        member_path,
                        data,
                        policy,
                        container_format=format_name,
                        container_kind=container_kind,
                        member_index=index,
                        compressed_size=int(info.compress_size or 0),
                    )
                )
    except BadZipFile as exc:
        return _failed_container_result(metadata, f"ZIP container parse failed: {exc}")
    except ValueError as exc:
        return _failed_container_result(metadata, str(exc))

    metadata["member_count"] = len(children)
    metadata["total_uncompressed_bytes"] = sum(child.size_bytes for child in children)
    return ExtractionResult(status="metadata_only", child_assets=tuple(children), metadata=metadata)


def _extract_tar_container(path: Path, policy: CorpusPolicy, *, container_kind: str) -> ExtractionResult:
    format_name = "tar"
    metadata = _container_metadata(format_name, container_kind)
    try:
        with tarfile.open(path) as archive:
            members = [member for member in archive.getmembers() if not member.isdir()]
            children: list[ContainerChildAsset] = []
            total_bytes = 0
            for index, member in enumerate(members):
                member_path = _safe_container_member_name(member.name)
                if member_path is None:
                    return _failed_container_result(metadata, f"unsafe container member: {member.name}")
                if not member.isfile():
                    return _failed_container_result(metadata, f"unsafe non-file container member: {member_path}")
                size = int(member.size or 0)
                cap_message = _container_cap_message(policy, member_count=index + 1, member_size=size, total_bytes=total_bytes + size)
                if cap_message:
                    metadata["member_count"] = len(members)
                    return _metadata_only_container_result(metadata, cap_message)
                extracted = archive.extractfile(member)
                data = extracted.read() if extracted is not None else b""
                total_bytes += len(data)
                children.append(
                    _container_child_from_bytes(
                        member_path,
                        data,
                        policy,
                        container_format=format_name,
                        container_kind=container_kind,
                        member_index=index,
                        compressed_size=None,
                    )
                )
    except tarfile.TarError as exc:
        return _failed_container_result(metadata, f"TAR container parse failed: {exc}")
    except ValueError as exc:
        return _failed_container_result(metadata, str(exc))

    metadata["member_count"] = len(children)
    metadata["total_uncompressed_bytes"] = sum(child.size_bytes for child in children)
    return ExtractionResult(status="metadata_only", child_assets=tuple(children), metadata=metadata)


def _extract_stream_container(
    path: Path,
    policy: CorpusPolicy,
    *,
    container_kind: str,
    format_name: str,
) -> ExtractionResult:
    metadata = _container_metadata(format_name, container_kind)
    opener: Callable[..., Any]
    if format_name == "gzip":
        opener = gzip.open
    elif format_name == "bzip2":
        opener = bz2.open
    else:
        opener = lzma.open
    try:
        with opener(path, "rb") as handle:
            data = handle.read(policy.container_max_member_bytes + 1)
    except OSError as exc:
        return _failed_container_result(metadata, f"{format_name} stream parse failed: {exc}")
    if len(data) > policy.container_max_member_bytes:
        return _metadata_only_container_result(metadata, "member exceeds size limit")
    member_path = _stream_member_name(path, format_name)
    child = _container_child_from_bytes(
        member_path,
        data,
        policy,
        container_format=format_name,
        container_kind=container_kind,
        member_index=0,
        compressed_size=path.stat().st_size,
    )
    metadata["member_count"] = 1
    metadata["total_uncompressed_bytes"] = child.size_bytes
    return ExtractionResult(status="metadata_only", child_assets=(child,), metadata=metadata)


def _extract_tool_stream_container(
    path: Path,
    policy: CorpusPolicy,
    *,
    container_kind: str,
    format_name: str,
) -> ExtractionResult | None:
    tool_name = "zstd" if format_name == "zst" else "lz4"
    command = shutil.which(tool_name)
    if command is None:
        return None
    metadata = _container_metadata(format_name, container_kind, attempted=[tool_name])
    try:
        result = run_no_window(
            [command, "-dc", str(path)],
            capture_output=True,
            timeout=120,
            check=False,
        )
    except Exception as exc:
        return _failed_container_result(metadata, str(exc))
    if result.returncode != 0:
        return _failed_container_result(metadata, result.stderr.decode("utf-8", errors="replace") if isinstance(result.stderr, bytes) else str(result.stderr or "stream decompression failed"))
    data = bytes(result.stdout or b"")
    if len(data) > policy.container_max_member_bytes:
        return _metadata_only_container_result(metadata, "member exceeds size limit")
    child = _container_child_from_bytes(
        _stream_member_name(path, format_name),
        data,
        policy,
        container_format=format_name,
        container_kind=container_kind,
        member_index=0,
        compressed_size=path.stat().st_size,
    )
    metadata["member_count"] = 1
    metadata["total_uncompressed_bytes"] = child.size_bytes
    return ExtractionResult(status="metadata_only", child_assets=(child,), metadata=metadata)


def _extract_optional_tool_container(
    path: Path,
    policy: CorpusPolicy,
    *,
    container_kind: str,
    format_name: str,
) -> ExtractionResult | None:
    tools = OPTIONAL_CONTAINER_TOOLS.get(format_name, ())
    for tool in tools:
        command = shutil.which(tool)
        if command is None:
            continue
        result = _extract_with_directory_tool(
            path,
            policy,
            container_kind=container_kind,
            format_name=format_name,
            command=command,
            command_name=tool,
            attempted=list(tools),
        )
        if result is not None:
            return result
    return None


def _extract_with_directory_tool(
    path: Path,
    policy: CorpusPolicy,
    *,
    container_kind: str,
    format_name: str,
    command: str,
    command_name: str,
    attempted: list[str],
) -> ExtractionResult | None:
    metadata = _container_metadata(format_name, container_kind, attempted=attempted)
    with tempfile.TemporaryDirectory(prefix="flux-kb-container-") as temp_dir:
        temp_path = Path(temp_dir)
        command_line: list[str]
        kwargs: dict[str, Any] = {"text": True, "capture_output": True, "timeout": 180, "check": False}
        if command_name in {"7z", "7zz"}:
            command_line = [command, "x", "-y", f"-o{temp_dir}", str(path)]
        elif command_name == "bsdtar":
            command_line = [command, "-xf", str(path), "-C", temp_dir]
        elif command_name == "unar":
            command_line = [command, "-quiet", "-force-overwrite", "-output-directory", temp_dir, str(path)]
        elif command_name == "unrar":
            command_line = [command, "x", "-o+", "-inul", str(path), temp_dir]
        elif command_name == "ar":
            command_line = [command, "x", str(path)]
            kwargs["cwd"] = temp_dir
        else:
            return None
        try:
            result = run_no_window(command_line, **kwargs)
        except Exception as exc:
            return _failed_container_result(metadata, str(exc))
        if result.returncode != 0:
            return None
        return _children_from_extracted_directory(temp_path, policy, container_kind=container_kind, format_name=format_name, metadata=metadata)
    return None


def _children_from_extracted_directory(
    temp_path: Path,
    policy: CorpusPolicy,
    *,
    container_kind: str,
    format_name: str,
    metadata: dict[str, Any],
) -> ExtractionResult:
    children: list[ContainerChildAsset] = []
    total_bytes = 0
    files = [path for path in temp_path.rglob("*") if path.is_file() and not path.is_symlink()]
    for index, member in enumerate(files):
        member_path = _safe_container_member_name(member.relative_to(temp_path).as_posix())
        if member_path is None:
            return _failed_container_result(metadata, f"unsafe container member: {member}")
        data = member.read_bytes()
        cap_message = _container_cap_message(policy, member_count=index + 1, member_size=len(data), total_bytes=total_bytes + len(data))
        if cap_message:
            metadata["member_count"] = len(files)
            return _metadata_only_container_result(metadata, cap_message)
        total_bytes += len(data)
        children.append(
            _container_child_from_bytes(
                member_path,
                data,
                policy,
                container_format=format_name,
                container_kind=container_kind,
                member_index=index,
                compressed_size=None,
            )
        )
    metadata["member_count"] = len(children)
    metadata["total_uncompressed_bytes"] = total_bytes
    return ExtractionResult(status="metadata_only", child_assets=tuple(children), metadata=metadata)


def _container_child_from_bytes(
    member_path: str,
    data: bytes,
    policy: CorpusPolicy,
    *,
    container_format: str,
    container_kind: str,
    member_index: int,
    compressed_size: int | None,
) -> ContainerChildAsset:
    size = len(data)
    content_hash = hashlib.sha256(data).hexdigest()
    file_kind = _member_file_kind(member_path)
    extension = PurePosixPath(member_path).suffix.lower()
    mime_type, _ = mimetypes.guess_type(member_path)
    metadata: dict[str, Any] = {
        "extractor": "container_member",
        "container_format": container_format,
        "container_kind": container_kind,
        "container_member_path": member_path,
        "container_member_index": member_index,
    }
    if compressed_size is not None:
        metadata["compressed_size_bytes"] = compressed_size
    chunks: tuple[AssetChunk, ...] = ()
    extraction_tier = "metadata_only"
    extraction_status = "metadata_only"
    if file_kind in {"text", "code"} and size <= policy.max_inline_bytes:
        text = data.decode("utf-8", errors="replace").strip()
        chunks = _chunks_from_text(text, member_path)
        extraction_tier = "inline"
        extraction_status = "indexed" if chunks else "metadata_only"
    elif file_kind in {"archive", "container"}:
        metadata["nested_container"] = True
    return ContainerChildAsset(
        member_path=member_path,
        file_kind=file_kind,
        mime_type=mime_type,
        extension=extension,
        size_bytes=size,
        quick_hash=_container_child_quick_hash(member_path, size, content_hash),
        content_hash=content_hash,
        extraction_tier=extraction_tier,
        extraction_status=extraction_status,
        chunks=chunks,
        metadata=metadata,
    )


def _member_file_kind(member_path: str) -> str:
    path = PurePosixPath(member_path)
    name = path.name.lower()
    ext = path.suffix.lower()
    if name in TEXT_MEMBER_NAMES:
        return "text"
    if ext in DIAGRAM_EXTENSIONS or any(name.endswith(suffix) for suffix in DIAGRAM_COMPOUND_SUFFIXES):
        return "diagram"
    if ext in CODE_EXTENSIONS:
        return "code"
    if ext in TEXT_MEMBER_EXTENSIONS:
        return "text"
    if ext in DOCUMENT_EXTENSIONS:
        return "document"
    if ext in IMAGE_EXTENSIONS:
        return "image"
    if ext in AUDIO_EXTENSIONS:
        return "audio"
    if ext in VIDEO_EXTENSIONS:
        return "video"
    if ext in CONTAINER_EXTENSIONS:
        return "container"
    if ext in ARCHIVE_EXTENSIONS or any(name.endswith(suffix) for suffix in ARCHIVE_COMPOUND_SUFFIXES):
        return "archive"
    mime_type, _ = mimetypes.guess_type(member_path)
    if mime_type and mime_type.startswith("text/"):
        return "text"
    return "binary"


def _container_metadata(format_name: str, container_kind: str, *, attempted: list[str] | None = None) -> dict[str, Any]:
    metadata: dict[str, Any] = {
        "extractor": "container",
        "format": format_name,
        "container_kind": container_kind,
        "member_count": 0,
        "max_depth": 1,
    }
    if attempted is not None:
        metadata["attempted"] = attempted
    return metadata


def _container_cap_message(policy: CorpusPolicy, *, member_count: int, member_size: int, total_bytes: int) -> str | None:
    if policy.container_max_depth < 1:
        return "container expansion depth must be at least 1"
    if member_count > policy.container_max_members:
        return "container exceeds member count limit"
    if member_size > policy.container_max_member_bytes:
        return "member exceeds size limit"
    if total_bytes > policy.container_max_total_bytes:
        return "container exceeds total size limit"
    return None


def _metadata_only_container_result(metadata: dict[str, Any], message: str) -> ExtractionResult:
    metadata = {**metadata, "warnings": [message]}
    return ExtractionResult(status="metadata_only", metadata=metadata, message=message)


def _failed_container_result(metadata: dict[str, Any], message: str) -> ExtractionResult:
    metadata = {**metadata, "warnings": [message]}
    return ExtractionResult(status="failed", metadata=metadata, message=message)


def _safe_container_member_name(name: str) -> str | None:
    normalized = str(name or "").replace("\\", "/")
    if not normalized or normalized.startswith("/"):
        return None
    path = PurePosixPath(normalized)
    if path.is_absolute():
        return None
    if any(part in {"", ".", ".."} or ":" in part for part in path.parts):
        return None
    return path.as_posix()


def _stream_member_name(path: Path, format_name: str) -> str:
    name = path.name
    suffix_by_format = {"gzip": ".gz", "bzip2": ".bz2", "xz": ".xz", "zst": ".zst", "lz4": ".lz4"}
    suffix = suffix_by_format.get(format_name, path.suffix)
    if name.lower().endswith(suffix):
        candidate = name[: -len(suffix)]
        if candidate:
            return candidate
    return path.stem or f"{path.name}.contents"


def _container_child_quick_hash(member_path: str, size_bytes: int, content_hash: str) -> str:
    value = f"{member_path}:{size_bytes}:{content_hash}".encode("utf-8", errors="ignore")
    return hashlib.sha256(value).hexdigest()


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
    ocr = _ocr_image(path)
    metadata["ocr"] = ocr.metadata
    if ocr.status == "blocked_missing_dependency":
        return ExtractionResult(status="blocked_missing_dependency", metadata=metadata, message=ocr.message)
    if ocr.status == "failed":
        return ExtractionResult(status="failed", metadata=metadata, message=ocr.message)
    chunks = _chunks_from_text(ocr.text, path.name, modality="ocr")
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
    asr = _asr_media(path, probed)
    metadata["asr"] = asr.metadata
    if asr.status == "blocked_missing_dependency":
        return ExtractionResult(status="blocked_missing_dependency", metadata=metadata, message=asr.message)
    if asr.status == "failed":
        return ExtractionResult(status="failed", metadata=metadata, message=asr.message)
    chunks = _chunks_from_text(asr.text, path.name, modality="transcript")
    return ExtractionResult(
        status="indexed" if chunks else "metadata_only",
        chunks=chunks,
        metadata=metadata,
        message=asr.message,
    )


def _asr_media(path: Path, probed: dict[str, Any]) -> AsrResult:
    settings = _asr_settings()
    duration = _media_duration_seconds(probed)
    metadata = _asr_metadata(
        status="pending",
        duration_seconds=duration,
        max_duration_seconds=settings["max_duration_seconds"],
    )
    if not settings["enabled"]:
        return AsrResult(status="completed", metadata={**metadata, "status": "disabled"})
    if duration is not None and duration > settings["max_duration_seconds"]:
        return AsrResult(status="completed", metadata={**metadata, "status": "skipped_duration_cap"})
    configured_model = str(settings["model_path"] or "").strip()
    if not configured_model:
        return AsrResult(
            status="blocked_missing_dependency",
            metadata={**metadata, "status": "blocked_missing_dependency"},
            message="ASR model path is not configured",
        )
    model_path = Path(configured_model).expanduser()
    if not model_path.exists():
        return AsrResult(
            status="blocked_missing_dependency",
            metadata={**metadata, "status": "blocked_missing_dependency"},
            message="ASR model path not found",
        )
    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg is None:
        return AsrResult(
            status="blocked_missing_dependency",
            metadata={**metadata, "status": "blocked_missing_dependency"},
            message="ffmpeg command not found",
        )
    if importlib.util.find_spec("faster_whisper") is None:
        return AsrResult(
            status="blocked_missing_dependency",
            metadata={**metadata, "status": "blocked_missing_dependency"},
            message="faster_whisper not installed",
        )
    cached = _read_asr_cache(path, model_path)
    if cached is not None:
        text, segments = cached
        return AsrResult(
            status="completed",
            text=text,
            metadata={**metadata, "status": "cache_hit", "cache_hits": 1, "cache_misses": 0, "segments": segments},
        )
    return _asr_with_faster_whisper(path, ffmpeg=ffmpeg, model_path=model_path, metadata=metadata)


def _asr_settings() -> dict[str, Any]:
    defaults = {"enabled": True, "model_path": "", "max_duration_seconds": 3600}
    try:
        from .settings import SettingsService

        service = SettingsService()
        return {
            "enabled": bool(service.resolve("acceleration.asr.enabled").raw_value),
            "model_path": str(service.resolve("acceleration.asr.model_path").raw_value or ""),
            "max_duration_seconds": int(service.resolve("acceleration.asr.max_duration_seconds").raw_value or 3600),
        }
    except Exception:
        return defaults


def _asr_with_faster_whisper(path: Path, *, ffmpeg: str, model_path: Path, metadata: dict[str, Any]) -> AsrResult:
    with tempfile.TemporaryDirectory(prefix="flux-kb-asr-") as temp_dir:
        audio_path = Path(temp_dir) / "audio.wav"
        command = [
            ffmpeg,
            "-y",
            "-i",
            str(path),
            "-vn",
            "-ac",
            "1",
            "-ar",
            str(ASR_AUDIO_SAMPLE_RATE),
            "-f",
            "wav",
            str(audio_path),
        ]
        try:
            extract = run_no_window(
                command,
                text=True,
                capture_output=True,
                timeout=ASR_FFMPEG_TIMEOUT_SECONDS,
                check=False,
            )
        except Exception as exc:  # pragma: no cover - environment-specific
            return AsrResult(status="failed", metadata={**metadata, "status": "failed"}, message=str(exc))
        if extract.returncode != 0:
            return AsrResult(
                status="failed",
                metadata={**metadata, "status": "failed"},
                message=extract.stderr.strip() or "ffmpeg failed",
            )
        try:
            faster_whisper = importlib.import_module("faster_whisper")
            model = faster_whisper.WhisperModel(str(model_path), local_files_only=True)
            segments_iter, info = model.transcribe(str(audio_path))
            parts: list[str] = []
            segment_count = 0
            for segment in segments_iter:
                text = str(getattr(segment, "text", "") or "").strip()
                if text:
                    parts.append(text)
                segment_count += 1
            redacted, _ = redact_text("\n".join(parts).strip())
            text = redacted.strip()
            _write_asr_cache(path, model_path, text, segment_count)
            result_metadata = {
                **metadata,
                "status": "completed",
                "cache_hits": 0,
                "cache_misses": 1,
                "segments": segment_count,
            }
            language = getattr(info, "language", None)
            if language:
                result_metadata["language"] = language
            return AsrResult(status="completed", text=text, metadata=result_metadata)
        except Exception as exc:
            return AsrResult(status="failed", metadata={**metadata, "status": "failed"}, message=str(exc))


def _asr_metadata(
    *,
    status: str,
    duration_seconds: float | None,
    max_duration_seconds: int,
) -> dict[str, Any]:
    metadata: dict[str, Any] = {
        "engine": "faster_whisper",
        "status": status,
        "max_duration_seconds": max_duration_seconds,
        "cache_hits": 0,
        "cache_misses": 0,
        "segments": 0,
    }
    if duration_seconds is not None:
        metadata["duration_seconds"] = duration_seconds
    return metadata


def _media_duration_seconds(probed: dict[str, Any]) -> float | None:
    candidates: list[Any] = []
    if isinstance(probed.get("format"), dict):
        candidates.append(probed["format"].get("duration"))
    streams = probed.get("streams")
    if isinstance(streams, list):
        candidates.extend(stream.get("duration") for stream in streams if isinstance(stream, dict))
    for value in candidates:
        try:
            parsed = float(str(value).strip())
        except (TypeError, ValueError):
            continue
        if parsed >= 0:
            return parsed
    return None


def _read_asr_cache(path: Path, model_path: Path) -> tuple[str, int] | None:
    try:
        cache_file = _asr_cache_file(path, model_path)
        payload = json.loads(cache_file.read_text(encoding="utf-8"))
    except Exception:
        return None
    if payload.get("schema") != ASR_CACHE_SCHEMA:
        return None
    if payload.get("source_hash") != _sha256_file(path):
        return None
    if payload.get("model_key") != _asr_model_key(model_path):
        return None
    text = payload.get("text")
    segments = payload.get("segments")
    if not isinstance(text, str):
        return None
    try:
        segment_count = int(segments or 0)
    except (TypeError, ValueError):
        segment_count = 0
    return text, segment_count


def _write_asr_cache(path: Path, model_path: Path, text: str, segments: int) -> None:
    try:
        cache_file = _asr_cache_file(path, model_path)
        cache_file.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "schema": ASR_CACHE_SCHEMA,
            "source_hash": _sha256_file(path),
            "model_key": _asr_model_key(model_path),
            "engine": "faster_whisper",
            "segments": int(segments),
            "text": text,
        }
        cache_file.write_text(json.dumps(payload, sort_keys=True), encoding="utf-8")
    except Exception:
        return


def _asr_cache_file(path: Path, model_path: Path) -> Path:
    source_hash = _sha256_file(path)
    model_key = _asr_model_key(model_path)
    key = hashlib.sha256(f"{ASR_CACHE_SCHEMA}:faster_whisper:{model_key}:{source_hash}".encode("utf-8")).hexdigest()
    return Path(resolve_cache_layout()["directories"]["asr"]) / f"{key}.json"


def _asr_model_key(model_path: Path) -> str:
    try:
        return str(model_path.resolve())
    except Exception:
        return str(model_path)


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


def _ocr_image(path: Path) -> OcrResult:
    cached = _read_ocr_cache(path)
    if cached is not None:
        return OcrResult(
            status="completed",
            text=cached,
            metadata={
                "engine": "tesseract",
                "status": "cache_hit",
                "cache_hits": 1,
                "cache_misses": 0,
            },
        )
    tesseract = shutil.which("tesseract")
    if tesseract is None:
        return OcrResult(
            status="blocked_missing_dependency",
            metadata={
                "engine": "tesseract",
                "status": "blocked_missing_dependency",
                "cache_hits": 0,
                "cache_misses": 0,
            },
            message="tesseract command not found",
        )
    return _ocr_image_with_tesseract(path, tesseract)


def _ocr_pdf(path: Path, *, page_count: int) -> OcrResult:
    base_metadata: dict[str, Any] = {
        "engine": "tesseract",
        "renderer": "pdftoppm",
        "page_count": page_count,
        "pages_attempted": 0,
        "cache_hits": 0,
        "cache_misses": 0,
    }
    if page_count > OCR_MAX_PDF_PAGES:
        return OcrResult(
            status="completed",
            metadata={**base_metadata, "status": "skipped_page_cap"},
            message=f"PDF has {page_count} pages; OCR cap is {OCR_MAX_PDF_PAGES}",
        )
    renderer = shutil.which("pdftoppm")
    if renderer is None:
        return OcrResult(
            status="blocked_missing_dependency",
            metadata={**base_metadata, "status": "blocked_missing_dependency"},
            message="pdftoppm command not found",
        )
    tesseract = shutil.which("tesseract")
    if tesseract is None:
        return OcrResult(
            status="blocked_missing_dependency",
            metadata={**base_metadata, "status": "blocked_missing_dependency"},
            message="tesseract command not found",
        )
    parts: list[str] = []
    with tempfile.TemporaryDirectory(prefix="flux-kb-pdf-ocr-") as temp_dir:
        temp_root = Path(temp_dir)
        for page_number in range(1, page_count + 1):
            output_prefix = temp_root / "page"
            try:
                render = run_no_window(
                    [
                        renderer,
                        "-r",
                        str(OCR_PDF_DPI),
                        "-png",
                        "-f",
                        str(page_number),
                        "-l",
                        str(page_number),
                        str(path),
                        str(output_prefix),
                    ],
                    text=True,
                    capture_output=True,
                    timeout=OCR_TIMEOUT_SECONDS,
                    check=False,
                )
            except Exception as exc:  # pragma: no cover - environment-specific
                return OcrResult(
                    status="failed",
                    metadata={**base_metadata, "pages_attempted": page_number - 1, "status": "failed"},
                    message=str(exc),
                )
            if render.returncode != 0:
                return OcrResult(
                    status="failed",
                    metadata={**base_metadata, "pages_attempted": page_number - 1, "status": "failed"},
                    message=render.stderr.strip() or "pdftoppm failed",
                )
            rendered_path = output_prefix.with_name(f"{output_prefix.name}-{page_number}.png")
            page_ocr = _ocr_image_with_tesseract(rendered_path, tesseract)
            base_metadata["pages_attempted"] = page_number
            base_metadata["cache_hits"] += int(page_ocr.metadata.get("cache_hits") or 0)
            base_metadata["cache_misses"] += int(page_ocr.metadata.get("cache_misses") or 0)
            if page_ocr.status == "failed":
                return OcrResult(
                    status="failed",
                    metadata={**base_metadata, "status": "failed"},
                    message=page_ocr.message,
                )
            if page_ocr.text:
                parts.append(page_ocr.text)
    return OcrResult(status="completed", text="\n".join(parts), metadata={**base_metadata, "status": "completed"})


def _ocr_image_with_tesseract(path: Path, tesseract: str) -> OcrResult:
    cached = _read_ocr_cache(path)
    if cached is not None:
        return OcrResult(
            status="completed",
            text=cached,
            metadata={
                "engine": "tesseract",
                "status": "cache_hit",
                "cache_hits": 1,
                "cache_misses": 0,
            },
        )
    try:
        result = run_no_window(
            [tesseract, str(path), "stdout"],
            text=True,
            capture_output=True,
            timeout=OCR_TIMEOUT_SECONDS,
            check=False,
        )
    except Exception as exc:
        return OcrResult(
            status="failed",
            metadata={
                "engine": "tesseract",
                "status": "failed",
                "cache_hits": 0,
                "cache_misses": 1,
            },
            message=str(exc),
        )
    if result.returncode != 0:
        return OcrResult(
            status="completed",
            metadata={
                "engine": "tesseract",
                "status": "failed",
                "cache_hits": 0,
                "cache_misses": 1,
            },
            message=result.stderr.strip() or "tesseract failed",
        )
    redacted, _ = redact_text(result.stdout.strip())
    text = redacted.strip()
    _write_ocr_cache(path, text)
    return OcrResult(
        status="completed",
        text=text,
        metadata={
            "engine": "tesseract",
            "status": "completed",
            "cache_hits": 0,
            "cache_misses": 1,
        },
    )


def _read_ocr_cache(path: Path) -> str | None:
    try:
        cache_file = _ocr_cache_file(path)
        payload = json.loads(cache_file.read_text(encoding="utf-8"))
    except Exception:
        return None
    if payload.get("schema") != OCR_CACHE_SCHEMA:
        return None
    if payload.get("source_hash") != _sha256_file(path):
        return None
    text = payload.get("text")
    return text if isinstance(text, str) else None


def _write_ocr_cache(path: Path, text: str) -> None:
    try:
        cache_file = _ocr_cache_file(path)
        cache_file.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "schema": OCR_CACHE_SCHEMA,
            "source_hash": _sha256_file(path),
            "engine": "tesseract",
            "text": text,
        }
        cache_file.write_text(json.dumps(payload, sort_keys=True), encoding="utf-8")
    except Exception:
        return


def _ocr_cache_file(path: Path) -> Path:
    source_hash = _sha256_file(path)
    key = hashlib.sha256(f"{OCR_CACHE_SCHEMA}:tesseract:{source_hash}".encode("utf-8")).hexdigest()
    return Path(resolve_cache_layout()["directories"]["ocr"]) / f"{key}.json"


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


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


def _excel_com_check() -> dict[str, Any]:
    if os.name != "nt":
        return {"ok": False, "message": "Windows Excel COM is only available on Windows"}
    ok = importlib.util.find_spec("win32com") is not None
    return {"ok": ok, "message": "pywin32 available" if ok else "pywin32 not installed"}


def _powerpoint_com_check() -> dict[str, Any]:
    if os.name != "nt":
        return {"ok": False, "message": "Windows PowerPoint COM is only available on Windows"}
    ok = importlib.util.find_spec("win32com") is not None
    return {"ok": ok, "message": "pywin32 available" if ok else "pywin32 not installed"}
