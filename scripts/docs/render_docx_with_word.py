from __future__ import annotations

import argparse
import os
import shutil
import subprocess
from pathlib import Path


def _find_pdftoppm() -> Path | str:
    candidates: list[Path | str] = []
    poppler_bin = os.environ.get("POPPLER_BIN")
    if poppler_bin:
        candidates.extend((Path(poppler_bin) / "pdftoppm.exe", Path(poppler_bin) / "pdftoppm.cmd"))
    userprofile = Path(os.environ.get("USERPROFILE", ""))
    if userprofile:
        dependencies = userprofile / ".cache" / "codex-runtimes" / "codex-primary-runtime" / "dependencies"
        bundled_bin = dependencies / "bin"
        native_poppler = dependencies / "native" / "poppler" / "Library" / "bin"
        candidates.extend((native_poppler / "pdftoppm.exe", bundled_bin / "pdftoppm.exe", bundled_bin / "pdftoppm.cmd"))
    for name in ("pdftoppm.exe", "pdftoppm.cmd", "pdftoppm"):
        resolved = shutil.which(name)
        if resolved:
            candidates.append(resolved)
    for candidate in candidates:
        path = Path(candidate)
        if path.exists():
            return path
    raise FileNotFoundError("pdftoppm was not found. Install Poppler or use the bundled Codex runtime.")


def _word_to_pdf(input_path: Path, pdf_path: Path) -> None:
    try:
        import win32com.client  # type: ignore[import-not-found]
    except Exception as exc:  # pragma: no cover - platform dependency.
        raise RuntimeError("pywin32/win32com is required for the Word fallback renderer.") from exc

    word = win32com.client.DispatchEx("Word.Application")
    word.Visible = False
    word.DisplayAlerts = 0
    document = None
    try:
        document = word.Documents.Open(
            str(input_path),
            ConfirmConversions=False,
            ReadOnly=True,
            AddToRecentFiles=False,
        )
        document.ExportAsFixedFormat(str(pdf_path), 17)
    finally:
        if document is not None:
            document.Close(False)
        word.Quit()


def _rasterize_pdf(pdf_path: Path, output_dir: Path, dpi: int) -> list[Path]:
    pdftoppm = _find_pdftoppm()
    prefix = output_dir / pdf_path.stem
    for stale in output_dir.glob(f"{pdf_path.stem}-*.png"):
        stale.unlink()
    if isinstance(pdftoppm, Path) and pdftoppm.suffix.lower() == ".cmd":
        cmd = ["cmd.exe", "/d", "/c", str(pdftoppm), "-png", "-r", str(dpi), str(pdf_path), str(prefix)]
    else:
        cmd = [str(pdftoppm), "-png", "-r", str(dpi), str(pdf_path), str(prefix)]
    subprocess.run(cmd, check=True)
    pages = sorted(output_dir.glob(f"{pdf_path.stem}-*.png"))
    if not pages:
        raise RuntimeError("PDF rasterization completed without producing PNG pages.")
    return pages


def main() -> int:
    parser = argparse.ArgumentParser(description="Render DOCX to PNG pages through Microsoft Word and Poppler.")
    parser.add_argument("input_path", type=Path)
    parser.add_argument("--output_dir", type=Path, required=True)
    parser.add_argument("--dpi", type=int, default=144)
    parser.add_argument("--keep-pdf", action="store_true")
    args = parser.parse_args()

    input_path = args.input_path.resolve()
    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    pdf_path = output_dir / f"{input_path.stem}.pdf"
    pdf_path.unlink(missing_ok=True)
    _word_to_pdf(input_path, pdf_path)
    pages = _rasterize_pdf(pdf_path, output_dir, args.dpi)
    if not args.keep_pdf:
        pdf_path.unlink(missing_ok=True)
    for page in pages:
        print(page)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
