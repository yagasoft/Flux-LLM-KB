# Isolated Tesseract text baseline

This probe is not a production processor. It runs the separately approved,
centrally cached Tesseract 5.5.3 Windows runtime against local image paths from an
external benchmark catalogue. It does not initialise SQL, indexing, IIS, workers
or application providers.

```powershell
dotnet build probes/tesseract-text-baseline/TesseractTextBaseline.csproj -c Release --no-restore -warnaserror
dotnet run --project probes/tesseract-text-baseline/TesseractTextBaseline.csproj -c Release --no-build -- self-test
dotnet run --project probes/tesseract-text-baseline/TesseractTextBaseline.csproj -c Release --no-build -- help
```

The existing native model gate must verify the one-file English traineddata
manifest and persist its receipt before any child process starts. The read lease
is held through the version probe and every bounded OCR process. Model paths
derive only from verified hashes beneath `J:\Models`; there is no downloader,
provider fallback or other-drive model path. No installer is executed.

The catalogue supplies only sample IDs and relative image paths, never expected
text to the OCR engine. Results include engine version, model revision/hash,
per-page errors/timing and nullable live-sampled memory. Page text is emitted with
only CRLF/terminal-newline serialisation; the probe supplies no answer correction,
orientation preprocessor, table cells or block-order predictions. These missing
capabilities are explicit and cannot count as passing.

Keep source images, expected text and detailed results outside Git. Use a distinct
output directory per run. The model-free self-test uses fake model files/processes
plus one short-lived Windows command child; it never loads Tesseract or a real
model. See the [screening record](../../docs/operations/2026-09-19-english-ocr-screening.md)
for exact measured commands and limitations.
