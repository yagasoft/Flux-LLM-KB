# OCR quality assessment harness

This is a **synthetic screening benchmark, not a release gate**. It creates 30 public, English-only pages covering clean/high-resolution and low-resolution images, JPEG compression, blur/low contrast, skew/90/180 degree rotation, serif/sans/monospace/bold/italic/small text, two columns, tables and exact identifiers/amounts. It does not acquire or load models, and it does not change any application OCR code.

```powershell
dotnet run --project probes/ocr-quality-assessment/OcrQualityAssessment.csproj -- generate --output E:/Temp/ocr-screening
dotnet run --project probes/ocr-quality-assessment/OcrQualityAssessment.csproj -- evaluate --truth E:/Temp/ocr-screening/truth.json --results E:/Temp/candidate-results.json --report E:/Temp/report.json
```

The imported candidate-results JSON must have this shape:

```json
{
  "candidateName": "candidate label",
  "provenance": { "engine": "engine name", "version": "immutable version", "runId": "run receipt" },
  "samples": [
    { "id": "en-01", "pageText": "...", "blockOrder": ["heading"], "tables": [] }
  ]
}
```

CER is a literal Unicode-scalar Levenshtein character error rate; it deliberately performs no whitespace, punctuation, digit or script normalisation. Reading order, every table cell and each critical identifier/number are reported separately. Truth and the evaluation report include quality/writing-style/layout/orientation strata plus dimension summaries, so aggregate accuracy cannot hide a failed stratum. An evaluation with missing engine, version or run ID cannot pass.
