from __future__ import annotations

from dataclasses import dataclass, field
import importlib.util
import json
from pathlib import Path
import shutil
import struct
from typing import Any

from .crawler import AssetChunk, CorpusPolicy, classify_file
from .processes import run_no_window
from .redaction import redact_text


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
