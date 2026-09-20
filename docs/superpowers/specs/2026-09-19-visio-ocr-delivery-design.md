# Interactive Visio and English OCR delivery

Date: 2026-09-19
Status: approved delivery in progress; OCR selection and quality must precede further integration
Baseline: native PDF at `8654140`; reusable local-model/raster foundations at `6ca595b`, in `codex/document-processors-plan`
Plan: [implementation and live verification](../plans/2026-09-19-visio-ocr-delivery.md)

## Outcome and authority

Deliver useful, searchable English text from scanned/mixed PDFs and VSDX documents, with one visible document identity and page/shape/table provenance. Preserve the deployed native PDF text path. OCR is English-only and must handle rotation, columns, tables, critical numbers, varied image quality and writing styles. Arabic OCR, mixed-script OCR, language routing and Arabic model activation/updates are excluded. This does not remove Unicode support from native PDF or Visio text extraction. Small Visio extraction discrepancies remain acceptable.

The user has authorised one complete outcome: benchmark, select, integrate, deploy and verify live. Continue through those steps without routine approval pauses. Stop only for required acquisition/adoption approval, a material unapproved architecture/runtime change or a demonstrated blocker. Check the latest stable OCR engines and immutable model revisions before accuracy testing. No firewall/network-isolation project is required for Visio. Model acquisition remains separately governed by `AGENTS.md`.

## 2026-09-20 practical English OCR delivery amendment

This amendment is authoritative for the remaining English-only OCR work. It supersedes only contrary OCR selection and release-gate language below; retained CER/order/table-F1 reports, failed assertions and exact-match results remain unchanged diagnostic evidence and must not be rewritten or described as passed.

Freeze the complete local PaddleOCR-VL candidate provisionally: PaddleOCR-VL-1.6 revision `c5630abae1d940eafe0697512a0325494b02ab42`, the existing local layout and page-orientation components, the non-zero orientation confidence threshold of `0.80`, and the existing adaptive provider-order rule. Do not acquire a model, add a runtime, activate Arabic, alter the frozen settings or tune against the same synthetic templates unless an app-relevant failure demonstrates a need and the user separately authorises the change.

Replace the mandatory 30-page numerical release gate with one private, bounded practical assessment of approximately 6–10 distinct representative English documents. It must cover real scans, mixed native/scanned PDFs, rotation, columns, tables and varied image quality. Before predictions are reviewed, retain locally (outside Git) independently checked retrieval questions, expected passages and critical numbers/identifiers/table relationships from the originals. Reuse retained predictions whenever they answer the question; measure only consequential omissions, duplication, ordering or context errors rather than formatting differences.

The practical gate passes only when useful searches retrieve the expected passages; no material omission, duplication or order error changes meaning; checked critical values and table relationships are correct in context; original document identity and page provenance are preserved; and unreadable or uncertain material is explicitly surfaced rather than invented or counted as correct. Minor punctuation or formatting differences are acceptable only when they do not change meaning or retrieval. The practical gate is evidence for this private knowledge base, not a claim that the earlier numerical thresholds passed.

The GPU candidate must use the existing admission/result-continuation route, rather than a synchronous call inside the two-minute Extract worker claim. The adapter must retain the exact local J-store files actually consumed by the Python runtime, prevent every downloader/fallback path, bind result payload, model/settings fingerprint, source revision and admission generation durably, and prove cancellation/deletion drains the owned child before publication. Existing direct-ML/seven-role prototype code is diagnostic only and must not become an enabled alternate OCR path.

Delivery remains deliberately small: extend only the existing PDF document route, result continuation, document provenance/search projection and narrowly identified local-OCR deletion path needed for this candidate. Do not add another pipeline stage, provider platform, model distribution layer, OCR algorithm or broad source replay. Deploy only after the practical gate, focused tests, Release build/full suite and one final review, using the unchanged generic incremental updater's reviewed `-PlanOnly` then `-Apply` flow. Live verification is restricted to eligible existing authorised source revisions; never recreate a deleted source, broaden a rescan or alter watch inputs without separate authority.

## Verified starting point

- Native PDF text extraction and the document-owned pipeline exist at `8654140`. The deployed Integrations assembly identifies that commit. Native PDF currently returns `pdf-ocr-required` for uncovered non-blank pages; it does not perform OCR.
- `DocumentExtractionResult` currently contains only `Text`, `IsComplete` and `Warnings`. `ArtifactEntity` has no document metadata field. These are real implementation gaps, not completed work from the old plan.
- `NativeGoLiveRuntimeOptions.ValidateEffective` rejects enabled model/GPU/OCR flags. `GpuSchedulerServiceCollectionExtensions` registers `NoGpuAdmissionGate`. Turning flags on alone will not deliver OCR.
- Read-only checks found all seven selected OCR role manifests and all fourteen files under the canonical store. SHA-256 and length checks passed for 297,834,120 bytes. No download is needed for that bundle. This is file integrity evidence, not a provider-load, GPU or accuracy result.
- Hardware: RTX 4070 SUPER, 12,282 MiB VRAM, driver 610.88. Installed Visio: 16.0.20228.20190. Neither fact proves compatibility under the intended execution identity.

## Smallest approach

Keep the existing .NET/SQL retained-document pipeline. Select one complete OCR approach from a small evidence-based benchmark before building more integration. Use a higher-capability model for selection and an efficient implementation owner afterwards. Prefer supported processing implementations; the cached components and custom DirectML prototype have no presumption of winning. Preserve useful verification/raster work. Do not introduce a provider-selection platform or replace the scheduler. Any material new runtime boundary must be identified and approved before its installation/integration; absence of an installed runtime is not evidence against its accuracy.

The complete OCR outcome is a retained scanned PDF through the selected processor, durable completion and existing Corpus/Search, followed by authorised deployment and scoped live verification. The Visio route retains the same ownership and publication rules. Benchmark preparation, tensor smoke tests and infrastructure alone do not complete delivery.

### OCR and PDF

Retain the existing Syncfusion PDF/raster foundation and its external licence without printing or copying the licence into a release. ONNX Runtime DirectML 1.24.4 is a working local dependency, not the selected OCR approach. Pin the eventual candidate's supported packages and retain lockfiles.

The [existing acquisition specification](../../operations/document-ocr-acquisition-proposal.md) describes historical cached components, not seven competing OCR systems. Six roles remain relevant to an English conventional pipeline: detection, English recognition, page/line orientation, layout and tables. Preserve the cached Arabic files without loading, updating or deleting them. Resolve the latest published revisions of relevant installed identities, compare hashes with central and legacy caches, and acquire only specifically approved missing files. A different model generation/accuracy variant is a named candidate change, not a transparent file update.

### Accuracy is a release requirement

The user requires the highest demonstrated English extraction accuracy. The authoritative practical amendment above is the release gate for this private knowledge base; the retained numerical thresholds, exact-match reports and failed assertions remain diagnostic evidence and are not rewritten or described as passed. Successful inference and weak substring assertions do not establish practical usefulness. Do not extend custom geometry, decoder or OCR algorithms until an app-relevant failure demonstrates the need.

The bounded shortlist is supported Tesseract/Syncfusion structured extraction where the required component and licence are available, official PP-StructureV3, and full PaddleOCR-VL only if the first candidates fail or evidence warrants it. Plain Tesseract text/bounds is a baseline, not table-cell reconstruction. Compare supported full pipelines, not individual detector/recogniser/layout roles. Preserve text, page/block bounds, orientation, confidence and table/reading-order provenance in one document result. Retain headers, footnotes and numbers even where vendor Markdown defaults omit them. Do not choose a pipeline by vendor aggregate scores or a model's release date alone.

A missing Paddle/PaddleOCR/PaddleX installation is a runtime prerequisite to assess, not a rejection of that approach. The old Python service's non-`J:` caches and downloader/proxy defaults cannot be reused. Before acquiring a supported runtime or any model variant, identify the exact runtime change and all missing artifacts, revisions, bytes, checked caches and J-only destinations. If no available complete candidate passes, leave admission disabled and report measured gaps plus the smallest approval needed to continue. Do not manufacture an accuracy winner.

Use `ILocalModelStore.ResolveAsync` and hold every `VerifiedLocalModelLease` through session disposal. Read graphs and companions through the held-read API; no provider-name constructors or arbitrary path loaders. Reject external tensor data, custom operators or unresolved decoder dependencies before inference. Disable optimised-model serialisation and provider-generated model exports; loading must not create another model cache. A missing/corrupt/unavailable bundle refuses with zero download and no other-drive fallback. Do not modify the model gate or add distribution/conversion machinery.

If DirectML is selected, use sequential execution, disabled memory patterns and one `Run` at a time. Identify the actual GPU adapter rather than assuming device zero. For any GPU candidate, begin with one document/raster and measure provider placement and peak memory against the current 8 GiB budget. A candidate requiring a different budget/runtime needs an explicit design decision before activation. Report CPU and GPU work honestly; a deliberately CPU-based Tesseract comparison is valid, while silent fallback is not.

Extract native PDF word/line bounds first. Detect missing/broken text and scanned regions even on pages which also contain valid text. Rasterise only required pages/regions, process them through the selected supported English pipeline, then merge by native-text priority and geometric overlap. Preserve original OCR evidence and visible uncertainty; do not use generated language to repair names or numbers.

Retain the existing design limits: 64 MiB input, 500 pages, 16 MiB UTF-8 text, 4 MiB metadata, 25 megapixels per raster, 6,000-pixel longest edge and a ten-minute document budget. Exceeding a limit yields a named incomplete outcome, never silent truncation. Native-only documents must make zero OCR model calls.

### One document and reliable completion

Extend the existing extraction result with concrete bounded blocks and OCR regions. A block carries page, stable block/shape/table-cell identity, bounds, text span, extraction method and quality flags. Add only the previously planned nullable `Artifacts.DocumentMetadataJson` field for accepted metadata; remap spans during normalisation. Keep private text and intermediate OCR results off public logs and out of Git.

For a selected asynchronous GPU approach, use the existing Image OCR lane, `GpuTaskHandoffAsync`, `IGpuExecutorAdapter`, lifecycle receipts and callbacks. Its approved pending-result design is one concrete `DocumentOcrResults` table, keyed by mini-task ID and admission generation, with exact dispatch/batch, parent job and retained-source binding, input/settings/model fingerprints, result digest, bounded text and metadata. Payload and binding are immutable after insertion; equal repeated results are idempotent and conflicting digests refuse. No full-text index or retrieval query reads this table. The existing executor receipt deliberately contains a digest only, while Extract artefacts are searchable, so neither is a safe pending-payload slot. Promote only after a matching durable receipt/callback, with task/source/generation/digest equality in the continuation transaction. Finalise the selected adapter's ownership route after the quality decision; do not build speculative GPU machinery for a synchronous processor which can safely use the existing retained Extract path.

`DeliverAsync` must promptly acknowledge/start exactly one admission-owned run. The dispatch recovery service cancels delivery at `FallbackInterval`; that token is not the inference lifetime. Deduplicate redelivery. Timeout alone never proves execution ended or releases capacity. Recovery must find accepted results even after a dispatch stops being pending, then create the Normalise job/outbox once without forging or reviving an expired worker lease.

Apply source pause/deletion and suppression fences at admission, result acceptance, continuation and final publication. Extend deletion only for positively identified local OCR tasks: cancel never-admitted work, drain running work to proven release, remove source-owned terminal references, and preserve shared files/batches and other sources. Unknown/uncertain execution retains the existing refusal. No compatibility framework or terminal-row rewrites: use the existing successor/idempotency mechanism when extraction identity changes, and keep the last good document searchable until replacement publication succeeds.

### Interactive Visio

Visio interprets VSDX through its supported COM object model. Our code traverses ordered pages, shapes/groups, expanded text, shape data and defined connections, then produces the same document result. No new VSDX content XML parser and no package-member corpus entries. Keep existing ZIP security checks and strict UTF-8 output; structural validation is not a second extraction engine.

The first delivery is an on-demand STA desktop command, `documents run-visio`, bound to one source revision, expected input hash and processor fingerprint. Add an exact dispatch claim using existing outbox/job lease rules; the current `documents reprocess` command only prepares branches and does not already do this. Give interactive Visio work a distinct operation within the existing Extract stage so IIS cannot claim it. No scheduled-task installation, resident desktop service or new pipeline stage in this delivery. Missing desktop/Visio leaves an honest waiting/deferred state.

Open a checksum-verified retained input read-only in a dedicated Visio instance. Preserve the original watch file. Inspect external relationships, data connections and embedded/active content before opening; refuse auto-refresh or active embedded cases that the supported settings cannot suppress. This is a bounded package safety check, not another content parser. Disable macros and events before opening, read back the settings, decline refresh, never activate OLE/attachments or deliberately follow links, and close without saving. Do not treat the decline-refresh flag alone as proof of no external action. Refuse interference with an existing user session. Track the exact owned Windows PID; never kill processes by name. Bound execution, confirm owned-process cleanup before publication, and refuse late/stale results. Visio may interpret fields/formulas; our code does not run arbitrary formula commands.

No OS network isolation or zero-Office-network-traffic claim. The user's waiver does not authorise deliberately fetching document links, uploading content, enabling macros or weakening model-download controls. Small private-file count differences are reported but do not fail the Visio gate; failures to open, missing whole pages, hangs, invalid output and unsafe publication still do.

OCR for raster content referenced by a VSDX shape uses the same model route and provenance when required. Do not index thumbnails, unreferenced package media or OLE content. The initial integration need not add Visio page rendering or OCR to native shape text.

## Enablement, deployment and verification

Permit only the selected and implemented local OCR configuration: `Runtime:ModelRuntimeEnabled` and `Runtime:OcrEnabled`, plus `Runtime:GpuEnabled` only for an actual GPU route. Require valid J-only manifest bindings and the selected route's ownership/admission path; reject inconsistent combinations. Preserve refusal of Vision, ASR, FFmpeg, network parsing and unknown providers. Expose capability readiness/refusal in existing status surfaces; do not let an OCR failure take down native PDF text or unrelated indexing.

The canary must be source/revision scoped before enabling admission. Resolve the current live source state read-only; historical Test IDs may have been deleted. Do not silently recreate Test, scan the whole watch root or infer authority over unrelated documents. If the authorised files no longer have eligible retained revisions, report that exact prerequisite and request the smallest registration scope rather than writing SQL ownership rows directly.

Use separate, reviewed command-line SQL migration execution, then the unchanged generic incremental updater, `-PlanOnly` before `-Apply`. No deployment-specific updater edits, full GoLive or manual IIS restart. Enable flags only after local model/quality and composition tests pass, with effective-identity J-store access and verification of the selected CPU/GPU execution. Follow the updater's actual validation-hold protocol; do not assume it remains active after return.

Live success requires actual OCR and Visio text reaching Publish and useful Corpus/Search results under the original document identity, with page/shape/table provenance. Check unchanged original hashes, repeat-run idempotency, no raw member/thumbnail output, no duplicate text and unchanged unrelated-source membership. Test English native-only, scanned and mixed native/scanned documents. Use private fixtures outside Git and keep the watch folder read-only.

Use the authoritative practical gate above: a bounded 6–10-input English assessment covering the stated layouts and qualities, independently checked retrieval questions and critical values, useful retrieval, no meaning-changing omission/duplication/order error, and correct document/page provenance. Retain the former 30-page CER/order/table-F1 reports as diagnostics, including their failures; they are no longer an admission threshold. Synthetic pages remain useful screening/regression evidence and cannot by themselves establish the practical gate. The Visio tolerance does not lower the practical acceptance requirements.

Rollback disables new admission and drains owned execution before restoring the last good application/configuration through the reviewed deployment mechanism. Preserve model files/receipts, input bytes and last good projections. Keep additive schema compatible with old code; destructive production down-migrations are not routine rollback. Rehearse up/down/up and code rollback on disposable SQL.

## Reconciliation and non-goals

This design supersedes the unfinished parts of the 2026-09-18 design/plan, not their implemented baseline:

- Their no-Visio/formula-interpretation rule and exact Visio-count gate are replaced by the user-selected interactive processor and minor-discrepancy tolerance.
- The prior model-switch pause has been lifted. Latest-version/cache checks and any required explicit acquisition approvals precede benchmark inference. Their empty-central-store statement is historical.
- Earlier Arabic/mixed-script OCR requirements are superseded by the user's English-only instruction. Historical inventory/acquisition records remain intact; cached language assets are not deleted. Fixed seven-role loading and custom DirectML selection are superseded by the complete-approach benchmark.
- Their deployment-specific updater/migration changes are explicitly rejected; migrations run separately.
- Their proposed `DocumentExtractionResult.Blocks` and metadata field must not be mistaken for existing code. Native PDF and document ownership must not be rebuilt from scratch. Their proposal to stage OCR in Extract artefacts is replaced by one non-searchable pending-results table because current retrieval reads Extract text; the same additive migration carries the metadata field.

No new PDF/VSD/DOCX/Office family, OCR provider platform, downloader, auto-model selection, VLM enrichment, broad UI redesign, background Visio daemon, network sandbox or unrelated replay. Retain required OCR accuracy, source isolation, recovery and cache safeguards; these are not optional embellishments.

Architecture gate: Astra approved this direction with concrete corrections for mixed-page coverage, dispatch timeout ownership, post-receipt recovery, runtime flags and deleted-source canary scope; all are included above. The specific non-searchable pending-results table was also approved against the actual receipt/artefact contracts. This is not release approval. One implementation owner and one completed-delivery review are sufficient unless a demonstrated invariant fails.

## Primary references

- [DirectML restrictions and lifecycle status](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html): applies if the benchmark selects that runtime; graph compatibility alone does not prove accuracy.
- [DirectML 1.24.4 package](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.DirectML/1.24.4) and [Syncfusion rasteriser packages](https://help.syncfusion.com/document-processing/pdf/conversions/pdf-to-image/net/nuget-packages-required).
- [Visio event suppression](https://learn.microsoft.com/en-us/office/vba/api/visio.application.eventsenabled), [read-only open options](https://learn.microsoft.com/en-us/office/vba/api/visio.documents.openex) and [Office unattended-automation limitations](https://support.microsoft.com/en-us/visio/considerations-for-server-side-automation-of-office).
