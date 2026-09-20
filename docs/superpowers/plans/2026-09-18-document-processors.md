# VSDX, PDF and OCR implementation plan

> Historical plan. Continue from the [2026-09-19 remaining-work plan](2026-09-19-visio-ocr-delivery.md), based on the already-implemented document/PDF path. Do not rerun these old unchecked tasks blindly, download the cached model bundle, or add deployment-specific updater changes. The new plan ends at the user's requested model-switch pause.

> **For agentic workers:** use `superpowers:executing-plans` after approval. One Terra implementation owner handles the coherent batches below; do not split serial shared-file edits across agents. Use Luna only for bounded discovery and Astra for material ownership/GPU/migration decisions and the required pre-production gate.

**Goal:** deliver VSDX, PDF and accurate local GPU OCR together, with one visible document identity and useful cited search results.

**Architecture:** reuse retained input/branch ownership and the existing Extract → Normalise → CanonicalIndex → Embed → Publish pipeline. A single internal document-processing revision references the physical owner's retained binary; a document Extract worker produces text and provenance. OCR uses one in-process ONNX/DirectML executor through the existing GPU scheduler and a narrow durable result continuation.

**Tech stack:** .NET 10, SQL Server/EF Core, existing Blazor UI and USearch, Syncfusion major-34 PDF packages, ONNX Runtime DirectML. Pin exact compatible package versions centrally and retain lockfiles during implementation; no package/model installation in this planning turn.

**Spec:** [document processors design](../specs/2026-09-18-document-processors-design.md). Read it with this plan; it defines limits, failure semantics, model choices and acceptance thresholds.

## Global constraints

- This is one combined delivery; do not call it complete with OCR deferred for missing models.
- `J:\Models` is the sole production model root. A cache miss stops with an auditable refusal; no downloader or another-drive fallback.
- Implementation is authorised after the user's model switch. Model loading/adoption/copy/move/conversion/download remains separately prohibited until exact-file approval.
- Model acquisition/adoption/conversion needs a separate exact-file approval; design approval is not transfer permission.
- Preserve all existing ZIP container defences and precise security reasons, strict UTF-8 output, terminal ownership and source isolation.
- Keep the deterministic child worker/protocol unchanged. Add no stage, service, generic provider platform or model-distribution subsystem.
- Use the existing UI for document status/provenance. Do not regenerate manuals or redesign screens.
- Keep private golden documents, inventories and licence contents outside Git; the watch-test input remains read-only.
- Only the incremental updater may deploy. No manual IIS restart, full GoLive, broad replay or model-store cleanup.

## Start and prerequisites

Worktree: `C:\Users\os008\.codex\worktrees\document-processors-plan\LLM KB`, branch `codex/document-processors-plan`, based on `e62a55e`. Preserve this planning commit for the model switch. Check fresh `main` and dirty state before executing; integrate a newer main in this worktree if required. Do not run closeout or purge this worktree at the planning pause.

- [x] The written design is approved after the switch. English/Arabic quality thresholds and the additive metadata migration remain unmeasured acceptance criteria, not claims of capability.
- [ ] Confirm the supplied external Syncfusion file is a usable runtime key without printing it. Use offline registration and matching packages; request the correct runtime key if it is an unlock licence. Do not put the secret into appsettings, test fixtures, logs, Git or release payloads.
- [x] Read-only architecture selection chose separate official ONNX components for detection, English, Arabic, orientation, layout and tables. PP-OCRv6 does not cover Arabic; layout detection does not establish RTL reading order. The exact component revisions are in the design. Verify hashes/companions/DirectML/quality only after separately approved provisioning; no automated model experiment matrix or speculative downloads.
- [ ] Plan-only model evidence is not a compatibility test. After separately approved provisioning, run the existing gate and one bounded real-GPU smoke check. If graphs need an unapproved conversion, external-data loader, new service or fail the native provider, stop that dependency and present the smallest revised choice. VSDX/PDF work can proceed; OCR completion cannot be claimed.

The old native-model-store spec describes broader upstream/conversion metadata and future path consumers than its delivered manifest/held-read API. Keep that historical scope intact. This plan consumes the actual four-field manifest and held reads; it does not silently expand the gate. The original ZIP-only non-goals are superseded only where the later PDF/VSDX/OCR request explicitly requires it.

## Task 1: searchable, document-owned VSDX

**Files:**

- Add `src/FluxKnowledge.Application/Documents/DocumentExtractionResult.cs`, `DocumentProcessingInputHandler.cs`, `VsdxDocumentExtractor.cs`, `DocumentProvenance.cs` and `src/FluxKnowledge.Application/Workers/ExtractDocumentStageWorker.cs`.
- Modify `Sources/SourceClassifier.cs`, `Sources/RetainedProcessorActivationService.cs`, `Sources/ZipArchiveRetainedProcessor.cs`, `Ports/IRetainedProcessorBranchStore.cs`, `Sources/RetainedTextActivityPlanner.cs`, `Workers/IStageWorker.cs`, `Pipeline/StageTransitionRequest.cs`, `Workers/NormaliseTextStageWorker.cs`, `Indexing/CanonicalIndexStageWorker.cs` within `src/FluxKnowledge.Application`.
- Modify `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedProcessorBranchStore.cs`, `SqlRetainedTextRegistrationStore.cs`, `SqlStageTransitionStore.cs`, `SqlPipelineStore.cs`, `SqlCorpusProjectionReader.cs`, `Entities/ArtifactEntity.cs`, `Configurations/CanonicalSchemaConfigurations.cs`, `Search/SqlSearchHydrator.cs` and `Workers/OutboxWorkerRegistration.cs` (Search/Workers paths are relative to the SQL Server project).
- Add `src/FluxKnowledge.Cli/Commands/LocalDocumentProcessingCommand.cs`, one `documents` dispatch in `src/FluxKnowledge.Cli/Program.cs`, and `tests/FluxKnowledge.Integration.Tests/Cli/LocalDocumentProcessingCommandTests.cs`, following the existing local C# command's composition and output style.
- Add one EF migration `AddDocumentArtifactMetadata` plus its generated snapshot/designer. Change `src/FluxKnowledge.Web` Corpus/source-detail rendering and existing contract DTOs only to expose owner/provenance/quality evidence.
- Add `tests/FluxKnowledge.Domain.Tests/Documents/VsdxDocumentExtractorTests.cs`, `DocumentProvenanceTests.cs`, `tests/FluxKnowledge.Integration.Tests/Documents/DocumentOwnershipIntegrationTests.cs`, `DocumentMigrationIntegrationTests.cs`, and `tests/FluxKnowledge.Web.Tests/Components/DocumentCorpusTests.cs`. Extend existing ZIP, OOXML, source deletion and search tests.

**Interfaces:** `DocumentExtractionResult` contains `Text`, `Blocks`, `Warnings`, `IsComplete` and `OcrRegions`; `DocumentBlock` contains page number, stable block ID, text span, kind, bounds, shape/connector or table-cell evidence and extraction method. Keep these concrete immutable records. `VsdxDocumentExtractor.Extract(RetainedSourceBytes, CancellationToken)` returns that result. New `PipelineOperations.ExtractDocument` stays in `PipelineStage.Extract`. Extend `StageArtifact` with nullable `DocumentMetadataJson`; ordinary text callers remain unchanged.

Use a version-1 closed structure schema with integer page/shape IDs, UTF-16 span offsets matching .NET chunk offsets, finite page-relative bounds, and bounded arrays. `DocumentOcrRegion` identifies a PDF page rectangle or a validated VSDX page/shape/raster-part reference, never a filesystem path. `DocumentRaster` owns a disposable pixel buffer with width, height, stride and that provenance. The OCR contract returns the same `DocumentBlock` records; do not invent a second text/provenance format. Enforce current source-root byte caps as well as parser caps; don't increase a root's limit to fit a sample.

- [ ] Write failing synthetic fixtures first: page names/order, mixed text/fields, nested groups, inherited master text/overrides, explicit connectors, unrelated invalid-UTF-8 thumbnail, external relationship, missing part, corrupt/unsafe ZIP. Reuse existing ZIP fixture builders and precise-reason assertions. Do not copy the private VSDX into Git.
- [ ] Add a disposable-SQL failing test through classification → retained branch → one internal document revision → real pipeline → corpus/search. Repeat with the same fingerprint, then a successor fingerprint. Assert old terminal rows/receipts remain identical and only the current document is visible. Include genuine ZIP members as the counterexample.

```csharp
// Assertions in DocumentOwnershipIntegrationTests, after using its real SQL fixture.
Assert.Single(corpus.Items.Where(item => item.FileName == "diagram.vsdx"));
Assert.DoesNotContain(corpus.Items, item => item.Entry.Contains("retained-archive-members"));
Assert.Contains(search.Results, hit => hit.Snippet.Contains("Gateway"));
Assert.DoesNotContain(search.Results, hit => hit.Snippet.Contains("MasterContents"));
Assert.Equal(beforeTerminalRows, afterTerminalRows);
```

The fixture variables represent real `CorpusPage` and `SearchResponse` query results, not mocked Corpus/Search responses.

- [ ] Introduce only the exact document-input route. Reuse retained storage by reference under a publication lease, verify input hash/length, and use a capability-specific child manifest branch in `CommitAsync`. Do not reinterpret arbitrary derived children as runnable binary documents. Existing UTF-8 and ZIP callers must retain their current contract.
- [ ] Label branch completion as document-input acceptance, not successful extraction/indexing. Source/Corpus status must follow the resulting Extract/OCR/publish state. Keep any last good document visible until successor publication succeeds.
- [ ] Factor the ZIP security validator into an internal reusable helper if needed, keeping byte-for-byte semantics and reasons under regression tests. Parse VSDX relationships and selected semantic parts as specified; create no raw XML/member output artefacts. Record skipped dispositions in existing member/quality evidence.
- [ ] Persist the structure map in the nullable artefact column. Normalise text and remap spans together; canonical chunks carry page/block boundaries. Owner hydration must work for list/detail/lexical/semantic results and for pending or incomplete documents, not only a UI list filter.
- [ ] Add a revision-scoped document successor/reconciliation operation to the existing branch store, exposed only by the small local command `documents reprocess --source-revision <guid> --expected-input-sha256 <sha256> --expected-processor-fingerprint <fingerprint>`. Require all three values; support no root-wide/all-source switch. Resolve and validate the owner/hash/fingerprint transactionally; repeats return the same successor. It refuses unrelated roots and terminal mutation. On successful replacement publication, suppress only old descendants belonging to that physical document and rebuild affected index membership through existing publication. Fence old concurrent jobs against resurrection; preserve shared files and other sources. Do not clear historical data to make the test pass.
- [ ] Retain up/down/up migration and rollback-readable old text tests on disposable SQL. Extend source deletion tests for document metadata, pending extraction and shared retained binary references.
- [ ] Run focused tests, inspect their fresh output, then commit the VSDX/ownership batch. The observable result must be a useful document search hit, not only new types or a hidden member list.

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~VsdxDocument|FullyQualifiedName~DocumentProvenance|FullyQualifiedName~ZipArchive|FullyQualifiedName~Ooxml"
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~DocumentOwnership|FullyQualifiedName~DocumentMigration|FullyQualifiedName~SourceDeletion"
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter "FullyQualifiedName~DocumentCorpus"
```

## Task 2: native PDF through the same document path

**Files:** add `src/FluxKnowledge.Integrations/Documents/SyncfusionPdfExtractor.cs`, `SyncfusionPdfRasteriser.cs`, `SyncfusionLicenceRegistration.cs`, and `src/FluxKnowledge.Application/Documents/PdfTextQuality.cs`. Modify the Task 1 worker/registration, central package versions, affected `.csproj`/lockfiles and existing private configuration binding. Add `tests/FluxKnowledge.Integration.Tests/Documents/PdfDocumentProcessingTests.cs` and `tests/FluxKnowledge.Domain.Tests/Documents/PdfTextQualityTests.cs`.

**Interfaces:** `SyncfusionPdfExtractor.Extract(RetainedSourceBytes, CancellationToken)` returns the same `DocumentExtractionResult`. `SyncfusionPdfRasteriser.RenderPage(...)` accepts retained bytes plus an integer page and bounded render settings, returning a disposable raster; never accepts a remote URL. `PdfTextQuality` selects OCR page/region work using extracted text, word bounds and raster/image coverage, including mixed pages.

- [ ] Write failing public/synthetic fixtures for native paragraphs, columns, tables, blank pages, image-only pages, mixed content, rotation, broken character maps, encrypted and malformed files, licence unavailable and every hard limit. Establish expected text/page/region output before implementation.
- [ ] Pin the minimum Syncfusion packages and register the external licence before library use. Load from streams, apply the specified limits, preserve line/word bounds and mark OCR-required regions; no attachments/JavaScript/network/Office executors.
- [ ] Route the PDF result through Task 1 registration, structure mapping and real indexing. Native-only PDF completes without any model call. Image-only PDF must remain visibly OCR-required rather than produce an empty successful document.

```csharp
Assert.Equal("native", result.Blocks[0].ExtractionMethod);
Assert.Empty(nativeOnly.OcrRegions);
Assert.NotEmpty(scanned.OcrRegions);
Assert.NotEmpty(mixed.OcrRegions);
Assert.Equal(0, modelProbe.LoadCalls);
Assert.Equal(0, networkProbe.Requests);
```

- [ ] Run focused tests and commit. After these two implementation batches, demonstrate actual VSDX and native PDF corpus/search results. If both batches produced only infrastructure/tests, stop and correct the path before adding more machinery.

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~PdfTextQuality|FullyQualifiedName~DocumentProvenance"
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~PdfDocumentProcessing|FullyQualifiedName~DocumentOwnership"
```

## Task 3: offline GPU OCR and durable document completion

**Files:** add `src/FluxKnowledge.Infrastructure.Inference/Documents/OnnxDocumentOcr.cs`, `DocumentOcrModels.cs`, `DocumentOcrPostProcessor.cs`; add `src/FluxKnowledge.Application/Documents/DocumentOcrContracts.cs`; add `src/FluxKnowledge.Infrastructure.SqlServer/Workers/DocumentOcrExecutorAdapter.cs`, `DocumentOcrAdmissionGate.cs` and `Persistence/SqlDocumentOcrResultStore.cs`. Modify the existing Extract worker, `GpuSchedulerServiceCollectionExtensions.cs`, `GpuExecutorDispatchRecoveryService.cs`, `Persistence/SqlSourceDeletionStore.cs` and stage-result composition only where required. Update package pins/lockfiles. Add focused `DocumentOcrTests`, `DocumentOcrIntegrationTests` and `DocumentOcrQualityTests` under the existing Domain/Integration test projects.

**Interfaces:** `OnnxDocumentOcr.RecogniseAsync(VerifiedLocalModelLease, DocumentRaster, CancellationToken)` returns bounded document blocks and quality evidence. `DocumentOcrExecutorAdapter` implements existing `IGpuExecutorAdapter.DeliverAsync(GpuExecutorBatchHandle, CancellationToken)`; the admission gate implements `IGpuAdmissionGate`. The concrete SQL bridge resolves a handle to its exact mini-task/owner, persists the Extract result idempotently, and offers `ContinueCompletedAsync` for recovery of accepted results. No source path, model repository name or text supplied by a callback becomes execution authority.

- [ ] Start with synthetic tiny models/fake inference, not PC model caches. Write failing tests for zero acquisition on hit/miss, no provider construction on refusal, held lease for session lifetime, corrupt/missing companion/unavailable J:, duplicate delivery, admission contention, cancellation, stale fences, pause, concurrent deletion and restart after each of result/receipt/callback/continuation. Reuse the existing model-gate filesystem and GPU SQL fixtures.

```csharp
Assert.Equal(0, acquisitionProbe.Calls);
Assert.Equal(0, providerProbe.LoadCalls); // refused bundle
Assert.Equal(1, acceptedExtractArtifacts.Count);
Assert.Equal(1, normaliseOutboxMessages.Count);
Assert.Equal(0, deletedSourceSearchHits.Count);
Assert.Equal(otherSourceBefore, otherSourceAfter);
```

- [ ] Compose the one selected bundle via the existing resolver. Read models/vocabularies/configs from the lease into bounded memory; reject unmanaged external dependencies. Set DirectML sequential execution and memory-pattern settings. Do not invoke Paddle/Hugging Face convenience loaders. Preprocessing/decoding is specific to the pinned selected model contracts; retain reference tensor/crop/decoder tests.
- [ ] Implement orientation, detection, recognition, layout ordering and table cell association, including Arabic/English ordering. Merge native/OCR regions without duplication. Retain low-confidence evidence and incomplete coverage, suppress hallucinated corrections, and limit all tensor/raster/text sizes. Native VSDX text and native PDF text bypass inference.
- [ ] Hand the real Extract Job to the existing Image OCR lane. Resolve durable dispatch context before inference. Persist result text/metadata/digest in the existing Extract artefact storage, then existing fenced result receipts/callback; create the Normalise Job/outbox once through recovery only after matching completion evidence. Parent Job progression must use a narrow validated transaction, never a manufactured worker lease. Retain GPU uncertainty on native hangs; do not infer release from a timer.
- [ ] Prove result publication cannot race source deletion or historical-member suppression. An ordinary process semaphore is only an in-process guard; the SQL capacity slot remains cross-process admission authority.
- [ ] Extend deletion only for the new, positively identified local OCR tasks. Retain `source-delete-external-execution-owned` for unknown/uncertain ownership. Under existing root/scheduler locks cancel never-admitted OCR tasks, drain admitted tasks, then remove only terminal source-owned task/result references. Test queued, admitted, completed and uncertain cases plus a batch shared with another source. Preserve the other source's receipts, batch outcome and capacity; require no new source lifecycle state. Exclude persisted-but-unaccepted OCR artefacts from Corpus preview, full-text candidate queries and retrieval hydration during crash recovery.
- [ ] After exact model provisioning approval, run real-model quality tests explicitly on the fixed golden corpus. Verify actual GPU placement and peak allocation; compare the selected route with the reusable baseline only when those baseline assets were separately approved for loading/conversion. A failed quality gate requires an explicit model/scope decision, not lower assertions or a silent CPU/VLM fallback.
- [ ] Run focused tests and commit. Keep real-model test enablement process-local; ordinary suites must skip those explicit tests and make zero model acquisition calls.

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~DocumentOcr|FullyQualifiedName~DocumentProvenance|FullyQualifiedName~Gpu"
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~DocumentOcrIntegration|FullyQualifiedName~LocalModelStore|FullyQualifiedName~Gpu|FullyQualifiedName~SourceDeletion"
# Only after separate provisioning/loading authority and fixture selection:
$env:FLUXKNOWLEDGE_RUN_DOCUMENT_OCR_QUALITY = '1'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~DocumentOcrQualityTests"
Remove-Item Env:\FLUXKNOWLEDGE_RUN_DOCUMENT_OCR_QUALITY
```

The explicit quality test reads fixed J-only manifests and a separately selected private/synthetic corpus; no environment variable can override the production model root. It records corpus/model/settings fingerprints, per-language/per-layout accuracy, critical-value matches, reading order, table scores, timings and memory. It must fail when enabled without its required resources, not turn a missing bundle into a passing skip.

## Task 4: combined verification, deployment and scoped live proof

**Files:** extend `scripts/deploy/update-native-iis-incremental.ps1`, `tests/native/native-deployment-plan.ps1` and `tests/native/phase-5-deployment-safety.ps1` only for the exact metadata migration/configuration needs. Update `docs/architecture.md`, the affected `docs/roadmap.md` entry and a concise operations validation record with non-private evidence. Use Task 1's local exact-revision command for the canary; do not add a general replay platform.

- [ ] Finish the focused matrix: one document identity for each format; actual ZIP member behaviour; skipped binaries never indexed/emitted; precise unsafe-container rejection; strict selected-text decoding; provenance across normalisation; successor/replay isolation; GPU result recovery; shared-file preservation; pause/deletion; migration up/down/up and old-code rollback compatibility. Include PDF/VSDX nested as real archive members in fixtures without automatically enabling broader live replay.
- [ ] Run the Release build with warnings as errors and the full suite once after focused green. Review the final diff for unrelated edits, private data, model payloads, automatic replay and download paths. Record the individual test-project totals/skips from fresh output.

```powershell
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build --logger 'console;verbosity=minimal'
git diff --check
```

- [ ] Have one independent Astra reviewer check the completed branch's ownership, old-member retirement, GPU publication/concurrency, local-model cost boundary, migration and release plan. Fix demonstrated blockers only. Do not add repeated review cycles or abstractions. A substantial redesign stops deployment for the smallest safe revised option.
- [ ] Confirm current deployment authority and exact validation revision IDs. Keep new capability activation limited to those IDs during the canary; automatic discovery/promotion must not replay other deferred documents. Extend the existing incremental migration preflight for the exact generated range/hash with rollback evidence; if the updater cannot safely cover it, stop and request direction.
- [ ] Read the incremental deployment plan before applying. Check that it contains no model transfer, broad SQL/data recreation, full GoLive or unrelated source operation.

```powershell
./scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot (Get-Location).Path -PlanOnly -ApplyMigrations
# After reviewing the exact plan under current deployment authority:
./scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot (Get-Location).Path -Apply -ApplyMigrations
```

- [ ] While the updater's validation hold is active, verify liveness/readiness/index health, runtime licence availability and local model verification. Select the previously named VSDX plus individual PDFs in the authorised test source, record their SHA-256 values and unrelated-source baseline, then invoke only the exact scoped successor/reprocess operation. Request a read-only scan sample if none exists; do not manufacture files in the watch folder.
- [ ] Prove the VSDX and PDFs have useful indexed text under their original names; verify page/shape/table citations and scanned-page OCR. Check SQL, Corpus, lexical/semantic retrieval and retained artefact references for absence of skipped-member output and old raw XML hits. Check unchanged input hashes, other sources and shared files. Pending/deferred/no-block status alone is not success. Release the hold only after the accepted probe policy succeeds.
- [ ] Rollback on failed health, ownership or quality validation: disable new OCR/document admission through the same controlled deployment path; retain source/model bytes and receipts, leave the additive nullable column for old-code compatibility, and preserve the last good projection. Never run a destructive down migration against accepted document data as routine rollback.
- [ ] Update evidence/roadmap only with proven outcomes. Commit, then use the mandatory closeout script; do not substitute manual squash/push/purge or use `-GoLive`.

```powershell
./scripts/dev/complete-feature.ps1 -FeatureWorktree (Get-Location).Path -MainRoot 'E:\LLM KB' -CommitMessage 'Add document-owned VSDX PDF and offline GPU OCR'
```

If closeout fails, report its JSON `failed_step` and `log_path`, correct that failure and rerun it. Do not purge until merge, push, deployment and probes have all succeeded. Report exact changed files, commands/results, deployment output, model-transfer bytes (including zero), quality measurements, live source isolation and remaining limitations.

## Plan self-review and handoff

Coverage: Task 1 delivers VSDX and document ownership; Task 2 delivers native PDF; Task 3 delivers OCR and durable GPU completion; Task 4 verifies/releases the combined result. Security, cost, provenance, idempotency, source isolation and rollback have explicit tests. No step treats synthetic inference or a model inventory as measured OCR success. Model identities/bytes are resolved by an explicit prerequisite with a stop, not invented placeholders or implicit acquisition permission.

The implementation changes include one metadata column and a concrete GPU result continuation because those capabilities do not exist today. No larger schema, provider-loader, child-host or distribution design is authorised. At the model switch, begin with the written approval and prerequisites, then follow the batches; do not restart design discovery or repeat completed cache inventories without a change or pre-acquisition recheck.

**Planning stop:** save and self-review these documents, run `git diff --check`, commit documentation only and wait for the user's model switch. Do not execute any unchecked implementation or release step in this turn.
