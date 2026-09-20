# Interactive Visio and English OCR implementation plan

## Visio continuation: 20 September 2026

The user has now authorised finishing the remaining interactive Visio slice in
`codex/visio-delivery`, based on the completed OCR release `6f27e69`. Preserve the
OCR implementation/settings and the separate proof worktree. The delivery is
one exact `documents run-visio` command, with its own retained-input fingerprint
and Extract operation, then the existing Normalise/index/Publish pipeline.
No migration, runtime installation, model access, new stage or resident Office
service is required. Existing terminal structural attempts and the last good
publication remain intact; repeated exact requests converge on one successor.

The independent ownership design review requires durable dispatch ownership,
expiry and source-state checks; exclusion from ordinary worker claims; a held
owned-process handle and kill-on-close job before document open; and a deletion
fence which survives an expired desktop lease until cleanup is proven. An exact
recovery command may settle a paused/deleting source only after proving that no
previous Visio execution remains. Unknown cleanup stays nonterminal. Retained
ZIP checks and strict UTF-8 are unchanged. No private text is written to logs.

Validate focused disposable-SQL and generated-document tests, then actual
interactive repeated extraction, the Release/full-suite gate and one final
independent review. Deploy through the unchanged generic updater's reviewed
PlanOnly/Apply flow, then process only the authorised retained VSDX revision and
verify original identity, provenance, search, idempotency and process cleanup.
Do not recreate sources, rescan a root or change watch-test. Rollback stops new
desktop invocations and proves drain before application restoration; there is no
schema rollback. Deployment failure uses the updater's automatic payload restore;
a later explicit restore requires its separately reviewed supported mechanism.

This section supersedes only the obsolete worktree/unimplemented Visio checkpoint
below. The OCR delivery and its recorded limitations remain unchanged.

## Delivery checkpoint: 20 September 2026

The user's later direction prioritised completing the frozen English PDF OCR
route. That increment is implemented, independently reviewed, deployed with all
three runtime flags enabled, and live-verified through GPU extraction, retained
page provenance, publication and useful search. The temporary single-file source
was separately authorised and deleted through the app after verification;
unrelated source fingerprints remained unchanged. The complete Release suite
passed 2,270 tests with 17 browser skips and a zero-warning build.

See the [practical assessment](../../operations/2026-09-20-english-ocr-practical-assessment.md)
and [delivery record](../../operations/2026-09-20-english-ocr-live-delivery.md).
The repetition diagnostic remains failed (one copy extracted from four identical
sentences). This is not a claim that the former CER/exact-match gates passed.
The interactive Visio batch below remains unimplemented/unverified; its checkboxes
must not be read as completed by the OCR release. Terminal failed OCR revisions
currently have no supported same-revision retry. The existing exact reprocess
command preserves completed branch ownership rather than resetting it.

The historical checklists below retain the broader authorised design; this
checkpoint records the narrower completed OCR increment without inventing a
Visio delivery or changing diagnostic expectations.

> **For agentic workers:** one higher-capability selection reviewer decides the OCR approach from evidence; one Terra owner implements the selected coherent batch. Luna handles bounded discovery if useful. Astra also covers material-risk decisions and the pre-deployment gate. Do not split serial edits among competing agents.

**Goal:** benchmark, select, integrate, deploy and verify accurate English OCR through document-owned indexing; retain the actual interactive Visio route. Arabic OCR and mixed-script OCR are excluded.

**Architecture:** retain existing PDF extraction, retained input, Jobs/outbox, GPU scheduler and publication. Add concrete OCR inference and result continuation; add an exact-revision desktop Visio command. No new workflow engine, service, queue or stage.

**Existing foundations:** .NET 10/SQL Server, installed Visio COM, Syncfusion PDF/raster 34.1.29 and ONNX Runtime DirectML 1.24.4. These dependencies and cached components do not preselect the winning OCR pipeline.

**Spec:** [design, constraints and acceptance](../specs/2026-09-19-visio-ocr-delivery-design.md). This is the active remaining-work plan; do not execute the old plan from Task 1 again.

## Global constraints and execution authority

- The user has authorised continuous delivery through benchmark, selection, integration, deployment and scoped live verification. Stop only for required acquisition approval, a material unapproved architecture/runtime change or a demonstrated blocker; there is no routine plan/model-switch pause.
- Continue in `codex/document-processors-plan`, foundation commit `6ca595b`; preserve the uncommitted prototype and separate Syncfusion/Visio proof. Recheck main/worktree status and preserve unrelated edits.
- `J:\Models` only. The historical seven-component bundle is intact, but only six components are relevant to English. Do not load/update/delete the cached Arabic component. Check current stable engine releases and exact latest upstream model revisions against J and relevant legacy caches before OCR testing; present changed/missing artifact identities, bytes and destinations before acquisition/adoption. Never redownload an unchanged artifact.
- No private document text, fixtures, licence contents or raw inventory in Git. The authorised watch folder remains read-only.
- No new compatibility framework, broad source replay, updater special casing or manual IIS restart. Keep terminal ownership immutable and all original ZIP defences.

## 2026-09-20 authoritative practical OCR amendment

This section supersedes contrary OCR-selection, seven-role/DirectML, 30-page numerical-gate and repeated-acquisition work below. Historical benchmark reports, assertions and failed threshold results stay intact as diagnostics; do not edit expectations or call those thresholds passed.

- [x] Freeze the current complete candidate: PaddleOCR-VL-1.6 revision `c5630abae1d940eafe0697512a0325494b02ab42`, its existing local layout/page-orientation components, non-zero orientation confidence threshold `0.80`, and adaptive provider-order rule. No further model/runtime survey, acquisition, Arabic activation or synthetic-template tuning is in scope.
- [x] Create one private assessment record outside Git for approximately 6–10 representative English documents: independently record retrieval questions, expected passages and important values from originals before reviewing predictions. Cover real scans, mixed native/scanned PDFs, rotation, columns, tables and varied image quality; reuse retained predictions where they already answer the question.
- [x] Apply the revised practical gate: expected passages retrieve usefully; no material omission, duplication or ordering defect changed meaning; checked numbers, identifiers and table relationships were correct in context; identity and page provenance survive; and provider refusal remains explicit. Minor punctuation/formatting differences remain accepted only when retrieval and meaning are unaffected.
- [x] Integrate the frozen GPU candidate through the existing `GpuTaskHandoffAsync`/`IGpuExecutorAdapter` and durable result-continuation design. It does not synchronously invoke OCR from `ExtractDocumentStageWorker`; source/admission/model/settings bindings, idempotent recovery, pending-result invisibility and source-deletion drain have focused coverage.
- [x] Verify and hold the exact J-store files consumed by the Python candidate, not merely a different CAS copy. The adapter fails closed for a missing, corrupt, unsafe or reparse-point bundle/runtime file, J: unavailability, receipt failure, provider/downloader attempt or another-drive path. No model miss may acquire or fall back.
- [x] Extend the current PDF document result/provenance and publication-aware search path required to make accepted OCR text retrievable under its original document and page identity. The focused search check preserves accepted document OCR contributions without a general evaluation or search framework.
- [ ] Run focused model-free/SQL/composition tests; execute the practical assessment; then run the Release build, full suite and one independent final review. Deploy with the unchanged incremental updater using reviewed `-PlanOnly` then `-Apply`; use only eligible existing authorised source revisions for the scoped live extraction, provenance, indexing and search proof.

## Accuracy selection before integration

- [x] Resolve the exact [English update/acquisition request](../../operations/english-ocr-update-proposal.md) before inference. Approved Tesseract 5.5.3 and best-English payloads are verified under J:, with 41,973,825 payload bytes transferred. Existing fast-English/orientation data were reused non-destructively; current Paddle component revisions need no replacement download. This does not select a production winner or authorise acquisition of the other complete pipelines.
- [ ] Compare a small set of complete supported approaches: licensed Syncfusion/Tesseract structured extraction if the required component exists, official PP-StructureV3, and full PaddleOCR-VL only if justified by gaps/evidence. Verify runtime and model identities separately. A missing installation is a prerequisite, not an accuracy failure. Do not extend the custom detector/cropper/prototype to create a preferred winner.
- Current next comparison: official PP-StructureV3 with its documented direct ONNX Runtime engine on CPU, formulas/charts/seals disabled, reusing compatible cached English v6/orientation/layout/table artifacts. First confirm the complete structural ONNX bundle and exact runtime dependencies; do not acquire equivalent Paddle-format models or the provisional GPU bundle alongside them. CPU accuracy results do not establish RTX throughput or numerical consistency: resolve the exact GPU route after quality selection, before production enablement.
- The user approved the [exact next acquisition proposal](../../operations/english-ocr-onnx-acquisition-proposal.md) on 2026-09-20: 946,394,247 missing payload bytes, with five structural ONNX bundles and 97 pinned runtime wheels, plus the specified non-destructive local reuse and isolated offline setup. No part of the superseded native-Paddle/GPU proposal is authorised. Recheck caches under artifact locks before each transfer; preserve all existing cache content and stop on any unlisted dependency.
- [ ] Inspect benchmark rendering and independently check expected English text, order, table cells and exact numbers. Use the same frozen samples for each candidate, covering real scans, 300/150/low resolution, compression, blur, contrast, skew/rotation, columns, tables, small type and serif/sans-serif/monospace/bold/italic styles. Genuine handwriting requires genuine independently labelled examples; disclose absent coverage. Generated pages are screening only.
- Use a small, reusable subset of public, annotated English scans to supplement synthetic screening. Check licence/access, annotation accuracy and rendered content before freezing expectations; record source revisions/hashes and keep document payloads outside Git. Do not equate a dataset's published annotation with an independently checked expectation or acquire a multi-gigabyte corpus unnecessarily.
- [x] Preserve the >=30-page CER/order/table-F1 corpus and its failed assertions as diagnostic evidence only. The authoritative practical amendment replaces it as the release gate; do not rewrite expectations or report it as passed.
- [ ] Have the higher-capability reviewer nominate the best demonstrated complete approach. If none passes, report no winner, exact measured gaps and the smallest supported alternative/acquisition needed. Obtain only the approvals the cache/runtime rules require, then resume this sequence.
- [ ] After the gate passes, hand the selected adapter/runtime contract and benchmark evidence to the efficient implementation owner. Preserve useful existing model-store/raster work, and execute the integration, release and live steps below in the same authorised delivery. Do not stop at a benchmark or infrastructure milestone.

## Batch 1: working OCR document, including reliable publication

**Existing files to change:**

- `src/FluxKnowledge.Application/Documents/DocumentExtractionResult.cs`, `Workers/ExtractDocumentStageWorker.cs`, `Pipeline/StageTransitionRequest.cs`, `Workers/NormaliseTextStageWorker.cs` and `Indexing/CanonicalIndexStageWorker.cs`.
- `src/FluxKnowledge.Integrations/Documents/SyncfusionPdfDocumentExtractor.cs`; add adjacent `SyncfusionPdfRasteriser.cs`.
- Add the smallest concrete adapter for the selected supported pipeline and bounded provenance contracts. Reuse supported pre/postprocessing; do not add a provider platform or rebuild OCR algorithms speculatively.
- Add `Workers/DocumentOcrExecutorAdapter.cs`, `DocumentOcrAdmissionGate.cs` and `Persistence/SqlDocumentOcrResultStore.cs` in Infrastructure.SqlServer. Modify `GpuExecutorDispatchRecoveryService.cs`, `GpuSchedulerServiceCollectionExtensions.cs`, `OutboxWorkerRegistration.cs`, `SqlStageTransitionStore.cs`, `SqlSourceDeletionStore.cs` and existing Corpus/search projections only for this route.
- Add `Entities/DocumentOcrResultEntity.cs`, its composite mini-task/admission key and non-searchable payload mapping, plus the nullable metadata mapping in existing `ArtifactEntity`/`CanonicalSchemaConfigurations`. Generate one `AddDocumentOcrResultsAndMetadata` EF migration and snapshot/designer. Existing receipt digests do not store payloads; current Extract text is searchable. These concrete gaps require this table and column, not a generic result framework.
- `Directory.Packages.props`, affected project/lockfiles; `src/FluxKnowledge.Web/Configuration/NativeGoLiveRuntimeOptions.cs` and runtime composition. Preserve all unrelated disabled-provider checks.

**Interfaces:** extend the current extraction result with bounded document blocks and OCR regions, as defined in the design. OCR consumes retained-derived rasters and verified local artifacts, never a source URL or implicit model-name acquisition. For an asynchronous GPU winner, the listed scheduler/result-store changes remain the approved design: `DocumentOcrExecutorAdapter` implements existing `IGpuExecutorAdapter.DeliverAsync(GpuExecutorBatchHandle, CancellationToken)`, and exact task/source/admission bindings fence continuation. For a synchronous CPU winner, assess whether existing retained Extract ownership already suffices before adding those GPU-specific files or schema. Finalise that bounded integration decision after selection.

- [ ] Start with failing model-free tests for missing/corrupt companions, unavailable J:, zero acquisition, no provider construction after refusal, lease lifetime, native-only bypass, mixed-page coverage, strict UTF-8 and limits. Reuse the existing model-store and PDF test fixtures.
- [ ] Pin only the selected implementation's required packages/models. Reuse the held-file gate and bounded raster foundation. Inspect graph/decoder dependencies and reject unknown ones without downloading. Load only selected English-pipeline dependencies, with actual CPU/GPU placement and peak memory recorded.
- [ ] Integrate native-word bounds, scanned-region selection, the selected supported English OCR/structure pipeline and native-priority merge. Consume real orientation/order/table outputs. Preserve original confidence/uncertainty and critical numbers. Native Unicode text remains supported; Arabic OCR, language routing and Arabic OCR acceptance fixtures are outside this delivery.
- [ ] Persist accepted metadata and normalised span mappings. Build the result bridge with real SQL tests first: duplicate delivery, crash after result/receipt/callback, recovery when dispatch is no longer pending, exactly one Normalise transition, and invisible unaccepted results.
- [ ] Make delivery acknowledgement prompt and inference lifetime admission-owned; deduplicate redelivery. Never release capacity merely because delivery cancellation or a deadline fired. Check root/suppression at each publication boundary. Extend source deletion only for identified local OCR and prove other-source/shared-file preservation.
- [ ] Admit only the provisioned model/OCR configuration and enable GPU only for a selected GPU route. Test supported flag aliases, inconsistent combinations, missing manifests, and continued rejection of other providers. Validate production composition without loading models at unrelated startup/import paths.
- [ ] Exercise the full real retained scanned/mixed English PDF path in disposable SQL through Publish and useful search, then confirm the same retained quality matrix through the integrated path. Do not enable admission if a required quality/style stratum is missing or fails. Continue to deployment and live verification after the required review; this test result is not delivery completion.

Focused test names below include planned new test classes; add them to existing projects. Assert real results, not counts of mock callbacks:

```csharp
Assert.Equal(0, acquisitionProbe.Calls);
Assert.Equal(0, refusedProviderProbe.LoadCalls);
Assert.Single(acceptedExtractArtifacts);
Assert.Single(normaliseOutboxMessages);
Assert.Empty(unacceptedResultSearchHits);
Assert.Equal(otherSourceBefore, otherSourceAfter);
```

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter 'FullyQualifiedName~DocumentOcr|FullyQualifiedName~PdfDocumentProcessing|FullyQualifiedName~DocumentProvenance|FullyQualifiedName~LocalModelStore'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~DocumentOcr|FullyQualifiedName~DocumentPublication|FullyQualifiedName~SourceDeletion|FullyQualifiedName~GpuExecutor'
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter 'FullyQualifiedName~WebHostComposition|FullyQualifiedName~DocumentOcr|FullyQualifiedName~Corpus'
# Explicit opt-in only, after model-update checks, required acquisition approval and fixture review:
$env:FLUXKNOWLEDGE_RUN_DOCUMENT_OCR_QUALITY = '1'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~DocumentOcrQualityTests'
Remove-Item Env:\FLUXKNOWLEDGE_RUN_DOCUMENT_OCR_QUALITY
```

An enabled real-model test fails, rather than skips, if its resources are missing. Ordinary tests do not load PC models. Record per-stratum CER/order/table/critical-value results, not only an average or successful inference call.

## Batch 2: actual Visio through the same document route

**Files:** add `src/FluxKnowledge.Integrations/Documents/VisioDocumentExtractor.cs` and `src/FluxKnowledge.Cli/Commands/VisioDocumentCommand.cs`. Extend CLI dispatch, `DocumentReprocessCommand.cs`, `Documents/DocumentProcessingInput.cs`, `Workers/IStageWorker.cs`, the existing retained document registration, `IOutboxStore`/`SqlOutboxStore` exact claim and the existing stage-transition route. Add focused Visio extractor/CLI/SQL tests; keep production XML extraction unchanged until explicit route selection.

- [ ] Write failing tests for one document, ordered pages/shape provenance, grouped/master-derived text, Unicode/expanded fields, defined connectors, repeated run, malformed ZIP, stale input, expired claim, concurrent deletion, desktop unavailable and an existing user Visio instance. Native Unicode preservation is distinct from excluded non-English OCR. Use generated public fixtures, never the private source in Git.
- [ ] Add `documents run-visio --source-revision <guid> --expected-input-sha256 <hash> --expected-processor-fingerprint <fingerprint>`. Resolve one eligible retained binding and claim its exact outbox/job using existing SQL fences. A distinct operation within Extract is registered only in the interactive runner, not IIS. Do not manufacture a `ClaimedJob`/dispatch or directly edit terminal state.
- [ ] Run COM synchronously on an STA thread in the logged-in desktop. Reuse the proof only as API evidence, not production-ready code. Apply ZIP/UTF-8 checks, read-only input, macros/events disabled, no deliberate refresh or OLE activation, dedicated instance/PID and bounded cleanup. No firewall work. Translate results into the shared contract; publish only after confirmed completion and ownership checks.
- [ ] Keep result spans/quality visible in existing document detail/search. Minor shape-count differences are reported, not blockers. Whole-page loss, silent COM failure, private text in diagnostics, hangs and publication races fail.
- [ ] Prove CLI → actual Visio → accepted result → existing pipeline → one corpus identity in disposable SQL and then the authorised live canary. On-demand desktop execution is the supported first delivery; automatic scheduling and a resident host are not included.

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter 'FullyQualifiedName~VisioDocument|FullyQualifiedName~ZipArchive|FullyQualifiedName~Ooxml'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~VisioDocument|FullyQualifiedName~DocumentReprocess|FullyQualifiedName~DocumentPublication|FullyQualifiedName~StageTransitionAtomicity'
```

**Budget checkpoint:** after these two batches, show executable OCR and Visio documents through real interfaces. If either batch produces only infrastructure/tests, stop and name the concrete blocker; do not grow another foundation milestone.

## Release, enablement and live verification

- [ ] Resolve current live state read-only and record selected source/revision IDs, expected hashes and processor fingerprints in a private canary manifest. Historical Test IDs are not authority. If Test was deleted and no eligible revision exists, stop that canary and request exact re-registration scope; do not silently recreate or scan the whole root. Select individually named scanned/mixed English PDFs, plus the already-authorised VSDX. Never write fixtures into the watch folder.
- [ ] Run focused green, then one combined Release/full-suite gate and one independent whole-diff/pre-deployment review. Correct demonstrated blockers only. Include up/down/up migration tests, old-code readability, source isolation, shared files, pending-result invisibility and rollback. Do not lower OCR thresholds to pass.

```powershell
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build --logger 'console;verbosity=minimal'
git diff --check
```

- [ ] Apply any new migration separately using the reviewed exact EF migration range and command-line SQL connection held outside logs. Inspect generated SQL/checksum, record current migration head, test backward compatibility, and verify the new head after applying. Do not pass the updater's source-deletion-only `-ApplyMigrations` switch for this new range. Do not modify the generic updater.
- [ ] Configure only the selected English pipeline's J-only manifests, effective execution-identity access and canary-only admission. Grant only required model reads and verification-receipt writes; no model-payload write/delete permission. Preserve the exact old configuration in private recovery evidence before enabling only the selected runtime's required flags; GPU must reflect actual execution. Keep unrelated providers disabled. Confirm the generic updater preserves the intended configuration and rollback restores old flags. A general flag is not permission to drain unrelated deferred documents.
- [ ] Validate the candidate's effective configuration and actual CPU/GPU execution under the intended runtime identity, not just the developer desktop. Deploy only through reviewed incremental PlanOnly/Apply output. Use an interactive CLI for Visio after the app deployment; do not install or restart an Office service.

```powershell
./scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot (Get-Location).Path -PlanOnly
# Only after the actual plan and migration/flag/canary scope have been reviewed:
./scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot (Get-Location).Path -Apply
```

- [ ] Follow the updater's actual hold/probe behaviour and separately preserve the revision-scoped admission restriction through the canary. Verify `/health/live`, `/health/ready`, `/api/index-health` and unchanged effective flags. Inspect the deployed assembly commit, registered executor and local model-verification receipts; flags or HTTP 200 alone do not prove OCR.
- [ ] Invoke existing `documents reprocess` for each exact approved binding, then the new interactive `documents run-visio` for the VSDX. Verify actual OCR execution evidence (including GPU receipts for a GPU route) and Visio completion, pipeline Publish, one original document identity, useful search snippets and page/shape/table provenance. Check native text was not duplicated, skipped parts emitted/indexed nothing, original hashes unchanged and unrelated-source rows/index membership unchanged. Repeat the same binding and prove idempotency. Keep extracted private text in local evidence only.
- [ ] On failed live health/ownership/quality, stop new admission, drain owned work, restore the last good app/config through the updater's supported recovery path, and verify old health/projections. If explicit restore is not supported by that path, stop for the exact recovery action rather than improvising a clean-slate deployment. Preserve J-store content and additive schema; no production data-erasing down migration.
- [ ] After successful live verification, update concise evidence/roadmap and close out with the mandatory script. No worktree purge before merge/push/deployment/probes succeed; no `-GoLive`.

```powershell
./scripts/dev/complete-feature.ps1 -FeatureWorktree (Get-Location).Path -MainRoot 'E:\LLM KB' -CommitMessage 'Enable native document OCR and interactive Visio extraction'
```

If closeout fails, retain and report its JSON `failed_step` and `log_path`, fix that failure and rerun. Report exact changed files, fresh commands/results, deployment output, effective flags/provider, zero model-transfer bytes, quality and source-isolation evidence, and any remaining limitation.

## Plan self-review and handoff

Self-review: English-only scope supersedes prior Arabic/language-routing requirements. Complete supported approaches are compared before further prototype integration; latest model revisions, image quality and writing styles are explicit. Cache reuse, mixed-page coverage, accuracy, dispatch ownership/recovery where applicable, deletion, runtime flags, generic deployment and exact live scope remain. No private fixtures/model payloads enter Git, no synthetic-only score is a release result, and missing runtime is not an accuracy verdict.

Known execution dependencies: the selected supported runtime and complete model bundle, latest-version checks/acquisition approval, the labelled English quality corpus, rendering under the runtime identity, any required migration range and eligible current live revisions. Check each at its first affected step. A failed dependency means a concrete correction/decision, never an automatic download, weakened assertion or broad replay.

**Continue through the authorised delivery; pause only at the user's acquisition/runtime/blocker boundaries.**
