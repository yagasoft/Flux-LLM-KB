# English OCR benchmark execution

Status: approved CPU/ONNX acquisition verified and isolated runtime installed offline. The additional official ONNX schema-parser dependency was acquired once into `J:\Models`, hash-verified and installed offline on 2026-09-19. The isolated probe now performs the required parse-only ONNX schema preflight before provider import. A fresh guarded screening run completed, but the configured PP-Structure candidate missed the retained accuracy thresholds and is not selected. No production OCR selection, integration, deployment or live-verification claim.

## Authority and scope

On 2026-09-20 the user approved the exact [946,394,247-byte request](english-ocr-onnx-acquisition-proposal.md), including the specified local reuse and isolated offline setup. This does not approve unlisted dependencies, GPU packages, replacement downloads or Arabic OCR. Preserve the useful existing prototype without treating it as the selected approach.

The independent architecture review approved a probe-local consumer mapping: source-link the existing native Windows filesystem helper, verify the actual paired model/configuration files beneath the fixed J runtime directory, and hold their read-only file and ancestor handles until the owned Python process exits. Use the existing verification receipt contract. Do not expand the production model gate or patch PaddleX's processing algorithms. Acquisition refusal must be established before provider imports; a Python socket guard is not an operating-system sandbox.

## Fresh baseline checks

Both commands completed with exit code 0 on 2026-09-20, without loading a real OCR model:

```powershell
dotnet run --project probes/ocr-quality-assessment/OcrQualityAssessment.csproj -c Release --no-build -- self-test
dotnet run --project probes/tesseract-text-baseline/TesseractTextBaseline.csproj -c Release --no-build -- self-test
```

The existing [Tesseract scores](2026-09-19-english-ocr-screening.md) remain historical measured screening results. They are not fresh scores for the new candidate or a release-corpus result.

## Public supplementary inputs

The following small public test inputs were fetched once outside Git, with each downloaded file checked against the pinned Git blob identity and byte length. These are separate from the approved **model** transfer budget. No private source content was uploaded or modified.

| Source | Revision | Payload fetched | Purpose and limitation |
| --- | --- | ---: | --- |
| [PubTabNet examples](https://github.com/ibm-aur-nlp/PubTabNet/tree/8ffde9024bd331f5a61c3c549fdf30dd3c3e43a5/examples) | `8ffde9024bd331f5a61c3c549fdf30dd3c3e43a5` | 543,538 bytes: 20 PNGs and JSONL | Low-resolution scientific table images, cell annotations, merged rows/columns and critical decimal values. The selected examples are training examples, not evidence of unseen-data performance or real scanning. |
| [Tesseract public tests](https://github.com/tesseract-ocr/test/tree/232ff181c66516116ec0e84c4963f70de15050fd/testing) | `232ff181c66516116ec0e84c4963f70de15050fd` | 275,545 bytes: two magazine TIFF/text pairs, phototest TIFF/text, three rotated PNGs and repository licence | Two genuine multi-column magazine scans with text wrapping around pictures, plus a synthetic orientation test. These longstanding public tests are not guaranteed held out from model training. |

PubTabNet annotations are CDLA-Permissive-1.0; its images retain their individual PMC open-access terms. Keep source attribution and [the dataset's licence distinction](https://github.com/ibm-aur-nlp/PubTabNet/blob/8ffde9024bd331f5a61c3c549fdf30dd3c3e43a5/LICENSE.md). The Tesseract test repository includes Apache-2.0, but embedded magazine credits remain visible; these fixtures are local evaluation material, not application distribution assets.

Visual inspection covered PubTabNet `PMC2753619_002_00`, `PMC4003957_018_00` and `PMC5332562_005_00`, plus both magazine scans and upright phototest. Observed challenges include four columns, drop capitals, image-wrapped reading order, very small type, decimal signs, merged headers and row spans. The exercise-table's repeated row number is present in the image and must not be silently corrected. Published magazine references contain `~` placeholders for punctuation/diacritics; they are not independently checked ground truth until reconciled against the images. The table annotations also require a declared treatment of line breaks and markup before scoring.

Local lossless TIFF-to-PNG previews were created solely to inspect the scans. Original inputs remain unchanged. The LOC public scan API returned a browser challenge; it was not bypassed and no LOC payload was acquired.

These supplementary inputs do not yet establish the 30-page release gate. Freeze independently checked expectations before a held-out score, retain the existing thresholds, and report unverified coverage rather than treating successful inference as accuracy.

## Acquisition recovery

Fresh independent verification found all 100 runtime wheels complete and correct (183,994,780 bytes, including three reused wheels). The first model payload, PP-DocBlockLayout ONNX, was also complete and matched its approved SHA-256 at 129,353,461 bytes. Its upstream Git SHA-1 describes an LFS pointer rather than the large payload; the helper was corrected to use the supplied payload SHA-256. The retained complete partial was published without a replacement transfer.

A subsequent PowerShell automatic-variable collision failed before the YAML body-copy operation, leaving a zero-byte staging file. That file was preserved under a unique failed-attempt name, not deleted or overwritten. Its receipt distinguishes zero retained bytes from unknown historical HTTP wire traffic. The corrected helper passed injected Receive-path cache-hit and refusal tests before further approved acquisition was permitted. No additional model identity or download budget was approved by this recovery.

Independent verification subsequently passed for all ten newly acquired model/configuration files, totalling 762,589,283 bytes. Together with 97 new wheels (183,804,964 bytes), the verified new payload equals the approved 946,394,247 bytes; three existing wheels contributed another 189,816 bytes without reacquisition. This is verified payload accounting, not an exact historical HTTP transfer total. The known resumed model-body reads total 633,235,822 bytes; earlier wire traffic was not measured reliably.

The first aggregate receipt contained incorrect file-count and wheel-byte fields. An immutable correction, `english-ocr-paddle-onnx-cpu-20260919-batch-reconciled-correction.json`, preserves that history and supplies the verified totals under `J:\Models\inventory`. The fourteen files in the structural availability manifest describe seven structural pairs, not fourteen newly downloaded files. The complete runtime also needs the four existing detector, English recogniser and orientation pairs: eleven model/configuration pairs in total.

The isolated environment is `J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312`, with PaddleOCR 3.7.0, PaddleX 3.7.2 and CPU ONNX Runtime 1.30.0 installed from the approved local wheels. Installation is not evidence of inference compatibility or accuracy. No GPU runtime or extra dependency was acquired.

## Scoring contract

Retain literal Unicode-scalar Levenshtein CER and mean per-page aggregation against the approved clean/degraded limits. Pooled and formatting-normalised CER may be reported as diagnostics, but cannot reverse a failed release gate. Critical values are exact values with location and occurrence counts, not substring matches.

The release assessment must geometrically match independently annotated blocks and tables without exposing truth to the provider. It preserves emitted order and penalises missing, extra and duplicated blocks. Cell-assignment precision/recall/F1 includes row, column, row span, column span and empty cells; text accuracy is separately visible. Existing exact-match screening assertions are not weakened.

A mixed synthetic/real corpus is permitted, but must contain at least 30 distinct independently checked page originals, genuine scans and every required stratum. Augmented copies do not increase the page-original count; table crops supply supplementary structure evidence. Freeze provider settings before the held-out assessment and do not describe these public training examples as unseen-data evidence.

## Fresh supplementary Tesseract diagnostics

Tesseract 5.5.3.20260724 with the verified best-English model and unchanged PSM 3 settings completed all six original scan/orientation inputs, all four lower-resolution scan variants and all twenty public table crops. The original TIFF metadata reports 300 DPI. The derived variants use deterministic bicubic resampling at 150 and 75 DPI; all four were visually inspected for clipping. They remain augmentations of two page originals.

| Input | Literal CER |
| --- | ---: |
| Magazine 8071, 300 DPI | 9.6194% |
| Magazine 8087, 300 DPI | 7.8077% |
| Magazine 8071, 150 DPI | 4.8749% |
| Magazine 8087, 150 DPI | 7.6172% |
| Magazine 8071, 75 DPI | 57.0386% |
| Magazine 8087, 75 DPI | 10.7831% |
| Phototest upright / right rotation | 0.3497% each |
| Phototest 180-degree / left rotation | 82.8671% each |

These CER values include publisher line breaks, indentation, punctuation and text order. They are not glyph-only error rates. Reference corrections were made from the original pixels, including source-resolution crops of printed accents, without consulting candidate predictions. The inherited evaluator hard-codes a synthetic-screening classification even for these public diagnostic inputs; its exit 1 records failed exact-text screening, not a failed process or a release-gate result. No block/table/critical-value accuracy is claimed from empty structural truth arrays.

The twenty table-image processes exited successfully, but eight returned empty text. Their total recorded execution time was 5,986.052 ms. This is evidence that the plain PSM 3 baseline is incomplete on this set, not a verdict on every supported Tesseract/Syncfusion configuration.

The runs used the existing isolated runner, with no provider acquisition:

```powershell
dotnet run --project probes/tesseract-text-baseline/TesseractTextBaseline.csproj -c Release --no-build -- run --runtime J:\Models\runtimes\tesseract\5.5.3.20260724\tesseract.exe --manifest J:\Models\manifests\english-ocr-update-20260919\tessdata-best-eng.native-gate.json --truth E:\Temp\flux-ocr-assessment-20260919\public-scan-supplement\input-catalogue.json --output E:\Temp\flux-ocr-assessment-20260919\tesseract-best-public-scans-20260920
dotnet run --project probes/tesseract-text-baseline/TesseractTextBaseline.csproj -c Release --no-build -- run --runtime J:\Models\runtimes\tesseract\5.5.3.20260724\tesseract.exe --manifest J:\Models\manifests\english-ocr-update-20260919\tessdata-best-eng.native-gate.json --truth E:\Temp\flux-ocr-assessment-20260919\pubtabnet-examples\provider-inputs.json --output E:\Temp\flux-ocr-assessment-20260919\tesseract-best-public-tables-20260920
dotnet run --project probes/tesseract-text-baseline/TesseractTextBaseline.csproj -c Release --no-build -- run --runtime J:\Models\runtimes\tesseract\5.5.3.20260724\tesseract.exe --manifest J:\Models\manifests\english-ocr-update-20260919\tessdata-best-eng.native-gate.json --truth E:\Temp\flux-ocr-assessment-20260919\public-scan-supplement\derived-quality-variants-20260920\provider-inputs.json --output E:\Temp\flux-ocr-assessment-20260919\tesseract-best-derived-scans-20260920
```

All three commands exited 0. Inputs, reference corrections, raw results and diagnostic reports remain outside Git. There is no production extraction/search claim.

## Supported alternative not yet measured

Current primary documentation describes [Syncfusion Smart Data Extractor](https://help.syncfusion.com/document-processing/data-extraction/net/overview) with document/table structure, and its [OCR integration](https://help.syncfusion.com/document-processing/data-extraction/net/working-with-data-extraction) supports a configured Tesseract processor. It is not equivalent to the plain CLI baseline. The local NuGet cache contains PDF/raster components at 34.1.29, but not Smart Data Extractor or the PDF OCR package; neither usual Syncfusion installation directory exists. This bounded inventory is a prerequisite finding, not evidence that the complete approach is unsuitable. No extra package or model was acquired for it.

## Corrected PP-Structure execution evidence and blocker

The first smoke stopped during import because replacing `socket.socket` with a function broke Python SSL class construction. The guard was corrected to use an SSL-compatible subclass. The later output directory `E:\Temp\flux-ocr-assessment-20260919\ppstructurev3-onnx-screening-20260920-guardfix` contains a completed public synthetic run: exit 0, 81,276.6506 ms, 30 samples and no reported per-sample failures. Its raw JSON is 391,344 bytes. Model-construction messages are present; sampled peak memory is unavailable. ORT also logged unused-initialiser warnings. This is successful inference, not an accuracy or safety pass.

The implementer's initial handoff incorrectly reported that no model construction had occurred and no raw results existed. The coordinator inspected the process and raw-result files, corrected that statement, and confirmed no matching Python child remained. All original outputs are preserved. This run preceded the required external-tensor preflight and must not be used as a compliant release run.

The independent review established from [pinned ORT 1.30.0 source](https://github.com/microsoft/onnxruntime/blob/v1.30.0/onnxruntime/core/framework/tensorprotoutils.cc#L441) that loading bytes alone does not prevent relative external tensor reads. A malformed synthetic graph returned `InvalidProtobuf`; that experiment is explicitly insufficient, not a passed safety test. The required correction is official schema-based, parse-only recursive rejection before ORT construction, including tensor attributes and sparse tensors. Do not replace this with string scanning or a custom parser.

The approved acquisition omitted that parser dependency. The bounded central/legacy cache inventory found no available official ONNX schema/parser. The user then approved the [exact additional request](english-ocr-onnx-preflight-acquisition.md): ONNX 1.23.0 (7,872,197 bytes) and `ml_dtypes` 0.6.0 (439,333 bytes). Both were transferred once to `J:\Models\artifacts\sha256`, independently hash/length verified, recorded in immutable receipts under `J:\Models\inventory`, and installed into the isolated runtime using pip with `--no-index --no-deps`. Existing models remain intact. The probe now uses the official ONNX schema to reject every populated external tensor declaration, including nested graphs, tensor attributes, sparse tensors, functions and training graphs, before importing PaddleOCR, PaddleX or ONNX Runtime.

Fresh coordinator verification:

```powershell
dotnet build probes/ppstructure-onnx-benchmark/PpStructureOnnxBenchmark.csproj -c Release --no-restore -warnaserror
dotnet run --project probes/ppstructure-onnx-benchmark/PpStructureOnnxBenchmark.csproj -c Release --no-build -- self-test
& 'J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312\Scripts\python.exe' -B probes/ppstructure-onnx-benchmark/ppstructure_driver_tests.py
& 'J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312\Scripts\python.exe' -B probes/ppstructure-onnx-benchmark/ppstructure_driver.py --preflight-refusal-test
git diff --check
```

All five commands exited 0; the build reported zero warnings/errors. The Python suite ran 20 tests, including valid inline controls; normal/nested graph, tensor-attribute, sparse-tensor, function and training-graph external-data refusals; invalid role-map refusal; and proof that the refusal and guard paths do not import a provider or create an ORT session. The .NET self-test also validates its local model-bundle guard. The probe remains isolated and is not a production model gate. `git diff --check` emitted line-ending normalisation notices for four existing lockfiles, not build warnings.

The public table supplement now includes independently inspected structural references for three images: 178 source cells, 17 span cells and verified image hashes. These are published table-structure annotations, not release text ground truth; punctuation, superscripts and line-wrap ambiguities are preserved and disclosed.

New probe files are `BundleManifest.cs`, `ProbeVerificationFiles.cs`, `Program.cs`, `ppstructure_driver.py`, `PpStructureOnnxBenchmark.csproj` and `packages.lock.json` under `probes/ppstructure-onnx-benchmark`. They do not replace the production OCR implementation. Existing dirty prototype changes were preserved. No full application suite, commit, deployment, source mutation, live verification or closeout is claimed at this blocked checkpoint.

## Exploratory PP-Structure accuracy only

The preserved smoke was converted locally without re-running inference. The converter retained emitted block order and raw text, rendered HTML table cells as tab-separated rows, and inserted two newline characters between blocks. The latter differs from the frozen truth's one-line block separators; the literal CER includes that formatting difference. No normalisation was used to turn a failed score into a pass. Table spans and empty cells had a tiny converter check, but the diagnostic candidate deliberately supplies no invented truth block/table IDs, so the legacy evaluator's structural booleans are not release metrics.

| Frozen synthetic cohort | Samples | Mean per-page literal CER |
| --- | ---: | ---: |
| All | 30 | 10.8539% |
| Clean | 6 | 6.9872% |
| Degraded | 24 | 11.8206% |

All 30 pages differ literally from the reference; spacing contributes to that count. Concrete non-formatting gaps also exist: two originally upright pages were processed in reversed page order, the two-column examples were interleaved row-wise, and `en-02`, `en-08` and `en-26` substituted the letter `o` for digit zero in dates or amounts. The coordinator viewed those three rendered serif samples and checked the original raw blocks. Do not repair numbers from expected truth or infer release quality from these exploratory results. The frozen 2% clean / 5% degraded thresholds are unchanged and no complete candidate is selected.

The unchanged evaluator was freshly run by the coordinator:

```powershell
dotnet run --project probes/ocr-quality-assessment/OcrQualityAssessment.csproj -c Release --no-build -- evaluate --truth E:\Temp\flux-ocr-assessment-20260919\synthetic-screening\truth.json --results E:\Temp\flux-ocr-assessment-20260919\ppstructurev3-onnx-screening-20260920-guardfix\diagnostic-candidate-results.json --report E:\Temp\flux-ocr-assessment-20260919\ppstructurev3-onnx-screening-20260920-guardfix\coordinator-diagnostic-evaluation.json
```

It exited 1 with `overallPass=false`. This is an accuracy-screening failure, not a failed inference process. PP-Structure has not yet been run against the real-scan/public-table supplements, and there is no held-out release-corpus score.

## Fresh guarded PP-Structure screening and selection result

After the schema preflight passed, the isolated runner processed all 30 frozen synthetic English screening images from `E:\Temp\flux-ocr-assessment-20260919\synthetic-screening\truth.json` into `E:\Temp\flux-ocr-assessment-20260919\ppstructurev3-onnx-screening-20260920-post-preflight`. It exited 0 with no per-sample failures in 76,834.6756 ms. The raw result is retained outside Git; the input, process and raw-result receipts are respectively 5,778, 37,244 and 391,374 bytes. The output contains normal ONNX Runtime unused-initialiser warnings, not an acquisition attempt.

The unchanged diagnostic evaluator scored the raw output at 10.8044% mean English CER and 6.9872% mean clean/high-resolution CER. All 30 samples differed literally, only 27 of 30 critical-value checks passed and no block-order check passed. The retained release limits remain 2% clean and 5% degraded. The converter did not emit table objects and carries an old exploratory candidate label, so its 24 of 30 table booleans are explicitly unmeasured diagnostics, not a table-F1 claim. The raw and process receipts, rather than that stale converter label, establish this fresh run's provenance.

This configured PP-Structure candidate is therefore not a selection candidate. It will not be tuned against truth or advanced to real-scan scoring merely to prolong a failing screen. No threshold, model file or provider setting was changed.
