# English OCR screening, 19 September 2026

Status: two updated Tesseract text baselines measured; no complete OCR approach
has passed the release gate. No production integration or deployment occurred.

## Method and limits

Both runs used Tesseract `5.5.3.20260724`, CPU, `--oem 1 --psm 3`, on the same
30 frozen synthetic English pages. Models were resolved by the native J-only
model gate; the verified read lease remained held through every owned process.
No model download occurred during inference. The original global installation
and source documents were untouched.

The fixtures cover five quality conditions, six font/style conditions, single
blocks, columns, tables and 90/180-degree rotation. Representative column, table
and small-text rendering was inspected for clipping. They are sparse synthetic
pages, not real scans or handwriting and not the >=30-page release corpus.

CER below is the arithmetic mean of **literal per-page character error rates**,
including whitespace, punctuation, order and digits. Only CRLF and terminal
newlines are serialised consistently; no OCR text is corrected to improve scores.
Quality/style groups overlap layout and orientation: their differences do not
establish causal effects of a font or degradation. A critical-token check requires
all declared exact strings to occur, but does not prove correct cell assignment.

The plain-text baseline does not emit block identities or table cells, and does
not enable Tesseract orientation detection. Missing structure is unsupported,
not credited as passing. This is not an assessment of every supported Tesseract
or Syncfusion configuration.

## Fresh measurements

| Measure | `tessdata_fast` English | `tessdata_best` English |
| --- | ---: | ---: |
| Successful page processes | 30/30 | 30/30 |
| Mean page CER | 14.5284% | 14.5377% |
| Clean-high-resolution group, 6 pages | 7.7731% | 7.6720% |
| Degraded groups, 24 pages | 16.2172% | 16.2541% |
| Upright single-block pages, 13 pages | 1.1424% | 1.1043% |
| Pages retaining every critical token | 27/30 | 26/30 |
| Sum of page process elapsed time | 7.1035 s | 10.2471 s |
| Maximum sampled child working set | 61.7813 MiB | 66.6914 MiB |

Memory is sampled every 10 ms, not an exact OS lifetime peak or total pipeline
memory. Timing includes each owned OCR process but excludes the initial version
probe and model verification; it is not a throughput guarantee.

| Stratum | Pages | Fast CER | Best CER |
| --- | ---: | ---: | ---: |
| Low resolution | 6 | 21.5685% | 21.7985% |
| JPEG compression | 6 | 7.4596% | 7.3771% |
| Blur/low contrast | 6 | 26.7115% | 26.7115% |
| Skew/rotation quality group | 6 | 9.1294% | 9.1294% |
| Sans | 5 | 8.5554% | 8.5554% |
| Serif | 5 | 9.0864% | 9.0864% |
| Monospace | 5 | 9.4267% | 9.3277% |
| Bold | 5 | 25.9391% | 26.0381% |
| Italic | 5 | 8.6767% | 8.7324% |
| Small text | 5 | 25.4861% | 25.4861% |
| Columns | 6 | 32.9293% | 32.8283% |
| Table text, without cell structure | 6 | 8.1121% | 8.2596% |
| Rotated orientation | 5 | 34.9505% | 35.0495% |

Both variants lost all declared critical tokens on the two upside-down pages,
and the date on the skewed small-text page. Best also lost a date on one column
page. Column order and table whitespace account for part of the literal CER;
extra paragraph separators also prevent exact page-text matches. Assertions and
expected text were not weakened. The larger model is not a demonstrated upgrade
on this screening set, and neither baseline meets the complete release contract.

## Reproduction and retained evidence

The public harness and model-free runner checks:

```powershell
dotnet build probes/ocr-quality-assessment/OcrQualityAssessment.csproj -c Release --no-restore -warnaserror
dotnet run --project probes/ocr-quality-assessment/OcrQualityAssessment.csproj -c Release --no-build -- self-test
dotnet build probes/tesseract-text-baseline/TesseractTextBaseline.csproj -c Release --no-restore -warnaserror
dotnet run --project probes/tesseract-text-baseline/TesseractTextBaseline.csproj -c Release --no-build -- self-test
```

Builds and self-tests passed. Runner tests cover refusal before any process,
model lease lifetime, process errors/timeouts, model-derived candidate identity
and a short-lived real child. An initial Windows post-exit memory query failure
was reproduced and fixed; the measured runs above use refreshed live sampling.

The following commands were run separately for `fast` and `best`; result
directories for the reported runs are `tesseract-fast-5.5.3-run2` and
`tesseract-best-5.5.3` beneath the local assessment directory:

```powershell
$bench = 'E:\Temp\flux-ocr-assessment-20260919'
$variant = 'best' # Repeat with fast, using its distinct result directory.
$output = Join-Path $bench 'tesseract-best-5.5.3'
dotnet run --project probes/tesseract-text-baseline/TesseractTextBaseline.csproj -c Release --no-build -- run --runtime 'J:\Models\runtimes\tesseract\5.5.3.20260724\tesseract.exe' --manifest "J:\Models\manifests\english-ocr-update-20260919\tessdata-$variant-eng.native-gate.json" --truth "$bench\synthetic-screening\truth.json" --output $output
dotnet run --project probes/ocr-quality-assessment/OcrQualityAssessment.csproj -c Release --no-build -- evaluate --truth "$bench\synthetic-screening\truth.json" --results "$output\candidate-results.json" --report "$output\evaluation.json"
```

Each runner exited 0; each evaluator exited 1 for the reported non-passing
screening result, not an execution failure. Detailed per-page JSON remains local.
Truth SHA-256:
`284d8890f86b81a7d73ccaf46656704aceb1b79e0e8cf9aa1c65095670361cfb`.
Exact model revisions/hashes and acquisition evidence are in the
[update record](english-ocr-update-proposal.md).

## Next complete comparison

Use a supported complete document pipeline and independently checked public
English scans, with reading order, cell assignments and critical numbers scored
separately. The [active plan](../superpowers/plans/2026-09-19-visio-ocr-delivery.md)
retains clean CER <=2%, degraded CER <=5%, order >=95%, table-cell F1 >=95% and
exact critical identifiers. No winner, integration, deployment or live-quality
claim is permitted before that gate passes. Arabic OCR remains excluded.
