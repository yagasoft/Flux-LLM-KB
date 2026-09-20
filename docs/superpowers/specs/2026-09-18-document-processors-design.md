# Document-owned VSDX, PDF and OCR

Date: 2026-09-18
Status: approved for implementation; real-model provisioning remains separately gated
Baseline: `e62a55e` on `main`
Plan: [combined implementation plan](../plans/2026-09-18-document-processors.md)

> Historical baseline: the unfinished scope is superseded by the [2026-09-19 Visio/OCR design](2026-09-19-visio-ocr-delivery-design.md). It reconciles the user-selected interactive Visio route, accepted minor discrepancies, current cached models, model-switch pause and separate migration/generic-updater requirement. Do not treat the old no-Visio, empty-cache or deployment-specific updater statements below as current instructions.

## Delivery

Deliver all three capabilities together: meaningful VSDX extraction, native PDF extraction and measured GPU OCR. A physical document has one visible corpus identity and searchable subordinate chunks. ZIP/TAR collections continue to expose their actual files separately. Internal XML, relationships, fonts, previews and model artefacts never become independent corpus documents.

Implementation is authorised. Approval of this design does not approve model loading, adoption, copy, move, conversion or transfer. `J:\Models` must remain unchanged until the exact-file provisioning gate is separately approved.

## Evidence and choice

The native pipeline already supplies retained immutable inputs, capability fingerprints, fenced branches, Jobs/outbox, stages, source suppression, publication leases, GPU scheduling and result receipts. Its document processor currently emits derived child revisions; `OriginKind == 2` hides OOXML children in Corpus, but does not establish document-level search ownership. PDF is deferred; VSDX enters generic ZIP handling. The GPU registration still defaults to `NoGpuAdmissionGate`; the existing child worker has deterministic test frames, not an OCR payload/result protocol.

The offline model gate is delivered. Its actual manifest contains revision, filename, SHA-256 and byte length; its lease exposes held-file reads, not arbitrary provider paths. Reuse it unchanged. Read-only inspection found an RTX 4070 SUPER, 12,282 MiB VRAM. The canonical store currently contains inventory records, with no `artifacts` payload directory. Historical inventories identify Paddle OCR/orientation/unwarping candidates outside J:, not a verified deployable OCR bundle. Those records must be revalidated before reuse. The supplied Syncfusion licence file exists; its contents and runtime-key validity were not inspected.

Options considered:

1. **Selected:** native .NET VSDX/XML and Syncfusion PDF; one in-process ONNX/DirectML OCR adapter using the existing durable GPU scheduler. This keeps the Windows architecture and permits held-byte model loading.
2. Restore the legacy Python/Paddle services. Existing code offers useful parsing and model hints, but its provider-name loaders, implicit acquisition and CPU fallback conflict with the native/offline contract.
3. Start with a document VLM/service. Potentially useful for difficult pages, but adds runtime and grounding risk before ordinary document extraction is working. Do not add it to this delivery unless the explicit model-selection stop below is resolved by a revised approval.

The legacy `extractors.py` Visio routines are behavioural references only: do not copy replacement UTF-8 decoding or its weaker ZIP checks. Legacy `model_runner.py` disables orientation/unwarping in its normal OCR path and selects English; reusing that path would not satisfy this design's quality target.

## Document ownership and the smallest integration seam

Use the existing retained-branch and pipeline machinery. Do not introduce a workflow engine, another queue, another service or a new pipeline stage.

- Register versioned VSDX and PDF capabilities before generic ZIP promotion. Confirm both extension and package/signature; a malformed `.vsdx` must not fall through to raw ZIP indexing. Recognised document packages without a runnable processor stay deferred as documents.
- Each accepted document branch creates exactly **one internal document-processing revision**, linked to the physical source revision through `ParentSourceRevisionId`, using `OriginKind == 2`. It references the already retained binary through the existing content-addressed artefact store; no per-part child files are emitted. This internal revision is an execution record, not an additional user-visible document.
- Use a distinct `DocumentProcessingInput` classification and exact document capability/output contract. Register its existing Extract-stage Job with a new `extract document` operation. Never pass binary input to the UTF-8 worker or relax `AcceptedUtf8Text` checks. The extractor reads the parent's retained bytes through the existing reader.
- The document-processing identity includes the physical owner and processing fingerprint. New parser/model/settings fingerprints create successor branches and internal revisions; old terminal branches, receipts and pipeline state stay immutable. Replays of the same identity are idempotent. A dedicated owner projection selects the one accepted execution for Corpus, preview, lexical and semantic search. Existing generic OOXML `OriginKind == 2` children remain hidden; removing that filter globally is prohibited.
- The Extract output is strict UTF-8 text plus a bounded structure map: pages, blocks, text spans, shape/group/connector IDs, table cells, coordinates, extraction method and quality flags. Add one nullable `DocumentMetadataJson` column to `Artifacts` for this map; no new metadata framework or table-per-block model. Validate its closed schema and limits. Retain it through normalisation, remapping offsets when FormKC or line-ending changes alter text length. Never apply raw offsets to normalised chunks.
- Reuse existing chunks, embeddings and index publication. Chunk on page/block boundaries where possible. Hydrate corpus/search results with the physical owner's name/path and page/shape citations. Group internal extraction revisions under that owner; a parent awaiting extraction must still be visible with an honest status. Only the current successfully published document revision is searchable.
- Retire historical raw package-member projections only for the explicitly selected physical document: traverse ownership links, suppress those revisions, tombstone their pipeline projections and publish the corrected index membership. Preserve terminal execution records and shared content-addressed files. Do not delete/recreate the source or clear the corpus. Prove that a racing old member job cannot republish suppressed output.
- Apply the same publication, root-state and ownership fences to VSDX, PDF and OCR. Pause stops new claims; deletion prevents late result publication. This release does not change DOCX/XLSX/PPTX extraction; retain their regression tests and prevent new generic-ZIP fan-out for recognised document types.

Current `SqlSourceDeletionStore` refuses any source with a GPU mini-task using `source-delete-external-execution-owned`, including completed tasks. Extend it only for the new locally owned OCR task type: cancel never-admitted work under the scheduler/root locks, drain admitted work to proven completion/release, and remove source-owned terminal task/result references in the existing deletion transaction. Unknown executors and uncertain execution retain the existing refusal. Preserve shared batches/receipts still referenced by another source; prove deletion of one source cannot change their outcome or release their capacity. This compatibility correction is part of enabling OCR, not a redesign of source lifecycle.

This is an intentional, small schema/configuration extension beyond the earlier ZIP-only request. The later request for PDF/VSDX/OCR supersedes its no-processor/no-OCR scope. It does not supersede security, ownership, cost controls or source-isolation requirements.

## VSDX behaviour

Validate the entire ZIP before publishing output. Reuse the current validator, factoring its private checks into a small internal helper only where necessary. Keep the same limits and precise reasons for encryption, invalid central/local directories, unsafe/absolute/drive/backslash/traversal paths, aliases/collisions, link/reparse risks, compressed input, per-entry/total expansion, entry count, nested archives and compression ratios. Validate skipped entries too. The explicitly approved package-wide XML-element bound is 500,000 for all supported OOXML document types; do not widen it further to accommodate the sample.

Resolve only package-internal OPC relationships, normalising relationship-relative `..` segments within the package; this is distinct from accepting `..` in a ZIP entry name. Reject escaping/ambiguous internal targets and cycles. Record external relationships without following them. Verify content types and Visio namespaces; parse with DTDs/entities/resolvers disabled, strict UTF-8 and bounded depth/elements/text.

Extract page names/order, visible shape text including mixed text runs and cached field values, groups, shape data, instantiated master text with local overrides, and explicit connector endpoints. Resolve `Master`/`MasterShape` inheritance with bounded cycle detection. Do not index uninstantiated master libraries. Do not evaluate ShapeSheet formulas or infer a connection's business meaning/direction when the stored data does not establish it. Report unresolved references or unavailable cached values.

Unused binary/non-text parts are skipped with member-level reasons and no emitted artefact/chunk. A selected malformed/non-UTF-8 semantic XML part cannot be decoded with replacement or another encoding; retain its skipped reason and mark extraction incomplete, never advertise a complete document. A structurally unsafe container is wholly rejected. Structurally safe documents with unrelated binary thumbnails still complete normally.

OCR may process supported raster images actually referenced by visible shapes, under the same document and shape/page provenance. Ignore package thumbnails, previews, unreferenced media, OLE objects and unsupported vector images. No Visio automation, page rendering engine, legacy VSD/VSDM support, formula execution or diagram generation.

## PDF behaviour

Use `Syncfusion.Pdf.Net.Core` for native text/line/word bounds and `Syncfusion.PdfToImageConverter.Net.Core` for bounded page rasterisation. Register the runtime licence offline before first use, reading only the supplied external secret. Verify that the file contains the correct runtime key for the pinned major-34 packages; an installer unlock licence is not assumed to be that key. Never log, embed or commit it. If unavailable/invalid, expose a precise deferred capability reason and keep unrelated processors running.

Preserve page order, coordinates, headings/paragraphs, table cell associations and source references. Use native PDF text when usable; do not OCR every page indiscriminately. Route pages/regions with missing text, broken character mapping or uncovered scanned content to OCR, including mixed native-text/scan PDFs. Merge by region overlap and native-text priority so text is not duplicated. Detect genuinely blank pages separately from OCR failures. Preserve original text evidence; do not repair names/numbers with a language model.

Reject encrypted/password-protected or malformed PDFs without password guessing. Do not execute JavaScript, actions or attachments, follow external links, fetch fonts or process embedded files as standalone sources. Keep extraction read-only. Proposed hard limits are 64 MiB retained input, 500 pages, 16 MiB output UTF-8, 4 MiB structure metadata, 25 megapixels per raster, 6,000-pixel longest edge, one raster resident at a time and a ten-minute document budget. Exceeding a limit yields a named incomplete/limit result, not silent truncation or completion.

## OCR runtime and model choice

Use `Microsoft.ML.OnnxRuntime.DirectML` from .NET on the existing Windows host. DirectML is the proposed deployment fit, not a claim that every model graph is already compatible. Pin one package version and one explicit model bundle after compatibility validation. Load self-contained ONNX graphs from bytes read through `VerifiedLocalModelLease`; hold the lease throughout session lifetime. Reject external-data/custom-operator dependencies unless a separately reviewed implementation resolves every dependency through the same held lease. Do not add pathname escape hatches to the gate.

Set sequential session execution, disable memory-pattern optimisation and allow only one `Run` per session at once. Admit one OCR document at a time in the existing Image OCR lane. Bound page and recognition batches, observe cancellation at page/block boundaries, and record actual provider/latency/peak-memory evidence. No silent CPU fallback: any intentional CPU graph partition must be disclosed and validated; an unavailable GPU defers OCR. Target no more than 8 GiB peak GPU allocation for this adapter on the 12 GiB device, verified rather than inferred from parameter counts.

Use one fixed OCR stack, not automatic provider/model selection:

| Purpose | Proposed selection and reuse decision |
| --- | --- |
| Detection | `PP-OCRv6_medium_det_onnx`, revision `61323801669c338b7891481ec7bac61ce31b576a`. |
| English recognition | `PP-OCRv6_medium_rec_onnx`, revision `50c7eacafc52fa7bcf4194e8cd08e46f8558504b`. Its published language list does not cover Arabic. |
| Arabic recognition | `arabic_PP-OCRv5_mobile_rec_onnx`, revision `14aaedcd75825982689ecf5cd64ab33ee083215a`. Route and decoder quality require held-out evidence; do not compare incompatible recogniser confidence values. |
| Orientation/layout/tables | `PP-LCNet_x1_0_doc_ori_onnx` (`7330ab7039123e46af2dc03154b9969aa412c61d`), `PP-LCNet_x1_0_textline_ori_onnx` (`7fdcf3cf7061163eda7183b224aa334bd33068f7`), `PP-DocLayout_plus-L_onnx` (`feb74619326f634e0e883218598096a3733ad9f7`) and `SLANet_plus_onnx` (`7dbe640e127602bf506815e822c09758de73c482`). Layout is not a reading-order model: apply the bounded column/block, RTL/LTR span and ambiguous-order contract below. |
| Alternatives considered | Exclude PaddleOCR-VL and Surya from this delivery: their documented paths require a provider/serving runtime not established for held-byte .NET/DirectML execution. Do not infer that they cannot fit 12 GiB; they are excluded by architecture and evidence scope. |

Each role is a separate local manifest/lease because every candidate uses `inference.onnx`; the current manifest correctly rejects duplicate filenames. Fingerprint the ordered role/component identities after every required companion is verified, and hold all leases through session disposal. No family name above approves a download. Before **any** adoption, conversion or transfer, freshly check `J:\Models`/inventory, legacy Paddle/Hugging Face caches, `E:\Temp\pip-cache`, `E:\FluxPackageCache` and `E:\Codex Workspaces`. Present exact repositories/revisions/files/hashes/bytes, verified reusable bytes, missing companions, total transfer bytes and J-only destinations. Unknown identity or size stops the operation. Public documentation may name models; private inventory and licence material stay outside Git.

Do not run old provider constructors to inspect caches. Cache misses stop with an auditable refusal. There is no downloader in the application, setup, tests, deployment or retry path, and no another-drive fallback. One-time, separately authorised provisioning remains an operator action, not a new distribution subsystem.

### Durable OCR completion

The existing scheduler owns admission and capacity; it does not yet publish document results. Add only the concrete adapter and a narrow SQL document-result bridge:

1. The claimed Extract Job hands off one idempotent document OCR mini-task using the existing `GpuTaskHandoffAsync`. Its source and render plan are reconstructed from the retained revision and pinned settings, not an arbitrary path/URL in a callback.
2. The adapter resolves the exact dispatch/task/root fences, acknowledges the dispatch, resolves its local model lease and performs bounded inference. Native text extraction remains available without a model.
3. Persist the bounded Extract result and its digest idempotently under the exact task/admission/source fingerprint. The result is not searchable yet. Then use the existing result receipt and completion callback. Release capacity only after inference is finished and sessions/resources for that admission are released; timeout alone is insufficient evidence.
4. An idempotent continuation in existing dispatch recovery advances the parent Job and creates the Normalise Job/outbox only when the matching completed receipt/callback and result digest exist. It must not forge a `ClaimedJob`, use an expired worker lease or rerun inference after a result has been durably accepted.
5. Crash gaps between result/receipt/callback/continuation resume from durable evidence. Uncertain GPU execution stays uncertain under existing policy; it cannot be marked successful or cause unrestricted replay. Check suppression/deletion again at result acceptance and continuation.

Keep the deterministic child worker/protocol unchanged. No CPU/GPU scheduler rewrite, new daemon, new stage or provider HTTP surface is needed. This bridge and old-member retirement are the two high-risk invariant families requiring focused SQL integration tests and independent review.

The existing Corpus full-text candidate query reads Extract artefacts too. Explicitly exclude unaccepted GPU results from Corpus preview/search and retrieval hydration until the durable completion conditions above hold; absence of a Normalise Job alone is not a visibility fence.

## Quality and acceptance

Agree the following proposed release thresholds before execution; they are targets, not measured results. Retain synthetic/public fixtures in Git and private golden material outside Git. Pin the corpus before comparing models; include held-out examples, not only training/tuning pages.

- VSDX: exact expected pages, labels, group/master overrides and connector endpoints in representative fixtures; safe non-UTF-8 thumbnail skipped; unsafe ZIP reasons unchanged; actual authorised sample searchable under its document identity.
- PDF: born-digital, two-column, native tables, scanned, mixed native/scanned, rotated, encrypted and malformed cases; correct page references; no duplicate text or external access.
- OCR: at least 30 labelled pages, including English, Arabic/mixed script, columns, tables, rotation and degraded scans. Clean-page character error rate at most 2% per language; degraded-page CER at most 5%; block reading-order accuracy at least 95%; table cell-assignment F1 at least 95%. Report each stratum and worst pages, not only an average.
- A fixed list of critical names, identifiers and numbers must match exactly on accepted golden pages. Low-confidence/failed/unresolved regions remain visibly flagged with coordinates and reason; don't quietly drop them, invent corrections or label the document fully complete. Confidence is not itself proof of accuracy.
- One visible document per physical owner, with search snippets and page/shape/table provenance. Package XML, previews, skipped parts and model artefacts yield no search hits or independent corpus entries. Actual ZIP files remain independently searchable members.
- Repeated processing, restart, changed fingerprint, pause and concurrent deletion preserve ownership and idempotency. Publication cannot resurrect retired members; another source's rows/index membership/shared files are unchanged.
- Missing/corrupt/unwritable/unavailable/reparse model store: durable refusal where possible, zero acquisition/provider-load calls on refusal, zero fallback files. Real-model load/benchmark is an explicit test, never part of ordinary build/test.

## Release, rollback and non-goals

One combined release after focused tests, zero-warning Release build, full native suite, one independent whole-branch review and real-GPU quality evidence. Do not call the combined delivery complete with OCR deferred for missing models. Model-free progress may continue, but report the unmet dependency explicitly.

The additive metadata migration needs an up/down/up test on disposable SQL. Code rollback should normally leave the nullable column in place. The incremental updater currently pins a source-deletion migration range; this new migration cannot be silently passed under that approval. Extend its existing reviewed migration handling narrowly for the exact new range/checksum and test it, or stop before deployment if that requires a broader change.

Deploy only through reviewed `scripts/deploy/update-native-iis-incremental.ps1 -PlanOnly`, then `-Apply` under current explicit authority. Reuse its validation hold and probes. No clean-slate path or manual IIS restart. Prove activation cannot broadly promote deferred sources: use an exact revision allowlist for the canary, then remove it only with wider authority.

Live verification uses the previously authorised test source read-only, selecting the named VSDX and individually enumerated PDFs only. If it contains no suitable scanned PDF, request one read-only sample; never write a fixture into the watch folder. Record input hashes, exact selected revision/branch IDs and unrelated-source baselines. Reconcile only those revisions, prove useful indexed search and absence of internal-member hits, then release the deployment hold. Preserve model inventory and payloads on rollback. Do not suppress historical members until the replacement output can be published safely; failed new processing must leave the last good searchable document intact.

Out of scope: new DOCX/XLSX/PPTX processors, legacy VSD/Office support, generic standalone-image rollout, handwriting/mathematics/chart understanding guarantees, VLM enrichment, conversion/acquisition services, UI redesign, new public management APIs and unrelated-source replay. Existing Sources/Corpus/Search/detail screens gain only document status and provenance necessary to inspect this result.

## References and reconciliation

- [Existing gate design](2026-09-09-native-model-store-design.md) remains the cost boundary; its delivery scope defers runtime activation to this work. It describes broader provenance/lock requirements than the implemented small manifest. Do not implement those deferred features here. The current held-read API is the integration authority; the old spec's reference to future provider paths does not authorise an unverified path loader.
- [Microsoft Visio package structure](https://learn.microsoft.com/en-us/office/client-developer/visio/introduction-to-the-visio-file-formatvsdx) and [master identification](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-vsdx/f04b271b-5aec-48d1-b156-b632d0172fe0) inform bounded relationship/inheritance extraction.
- [Syncfusion text extraction](https://help.syncfusion.com/document-processing/pdf/pdf-library/net/working-with-text-extraction), [rasteriser packages](https://help.syncfusion.com/document-processing/pdf/conversions/pdf-to-image/net/nuget-packages-required), and [offline licence registration](https://help.syncfusion.com/aspnet-core/licensing/how-to-register-in-an-application) establish the proposed PDF route, not proof of this application's licence or runtime compatibility.
- [DirectML restrictions](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html), [official PP-OCRv6 ONNX](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx), [Arabic recognisers](https://www.paddleocr.ai/main/en/version3.x/module_usage/text_recognition.html), [layout ONNX](https://huggingface.co/PaddlePaddle/PP-DocLayout_plus-L_onnx), [table ONNX repository](https://huggingface.co/PaddlePaddle/SLANet_plus_onnx) and [Surya runtime](https://github.com/datalab-to/surya) inform selection; local accuracy/compatibility are unmeasured.

Self-review: all three requested capabilities included; no implied acquisition; no invented existing OCR executor; immutable terminal ownership preserved; necessary migration and continuation work disclosed; quality and deployment require evidence. Stop after writing the plan for the user's model switch.
