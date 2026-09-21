# Logical-document publication and source completion

Status: implemented in the dedicated worktree; focused verification is complete and final Release/full-suite/deployment validation remains.

**Goal:** normal source creation must take supported files through extraction, publication and useful search under their original identities, with accurate terminal status. Finish the authorised image OCR path as part of validating the affected source.

**Architecture:** reuse retained processor branches, the existing Extract-to-Publish pipeline, `DocumentPublications`, and the frozen local PaddleOCR-VL execution path. A logical document has one selected pipeline record and may contain many ordinary search chunks. Archive members retain their existing independent identities.

**Execution:** one implementation owner, test-first changes in this worktree, one independent review of the consequential design and one final branch/pre-deployment review. Do not create a provider framework, new stage, migration or general output aggregation subsystem.

**Baseline:** inspected `main` at `82937357f60ece0f7cc545b653c8a116561ac118`. Worktree: `C:\Users\os008\.codex\worktrees\logical-document-publication\LLM KB`; branch: `codex/logical-document-publication`. Recheck its state before execution; preserve unrelated work.

## Evidence and corrections to the previous recommendation

The previous live investigation found completed OOXML/media branches and completed child pipelines while the original DOCX, XLSX and JPG remained Pending. Those database observations belong to the preceding investigation; no fresh production query or mutation is claimed by this planning turn.

Current code independently confirms the defect:

- `SqlStageTransitionStore.PublishDocumentIfApplicableAsync` admits only `DocumentProcessingInput.TryGetContract` inputs. That contract recognises PDF, legacy structural VSDX and current Visio VSDX; it does not recognise OOXML text or media metadata.
- `OoxmlStructuralTextProcessor.WriteChildrenAsync` emits OriginKind 2 UTF-8 text. `MediaMetadataRetainedProcessor` emits OriginKind 3 JSON. Their branch completion does not complete the owner's downstream publication.
- Corpus, lexical search and vector hydration hide unselected OriginKind 2 records. Source projections inspect owner activities/publications, leaving completed child work and downstream child failures invisible at the original file. OriginKind 3 currently escapes several of these filters, allowing synthetic metadata entries and misleading counts.
- The revised requirement is 200 MiB of extracted OOXML text. `SqlRetainedTextRegistrationStore` and `SqlRetainedSourceReader` currently cap ordinary UTF-8 inputs at 16 MiB. The larger allowance must remain exclusive to an exactly bound OOXML result.
- `ExtractDocumentStageWorker` only hands PDF pages to OCR. `PaddleOcrVlmLocalExecutor` invokes `IPdfPageRasterizer`; JPG processing currently extracts dimensions/container metadata, not visible text. Publishing that metadata alone would not fulfil the user's source OCR requirement.
- Publication currently orders replacements only by branch creation time and retires OriginKind 1 siblings. Generalising that method without guarding both behaviours would introduce equal-time races and potentially suppress archive members.

This plan supersedes the prior suggestions to lower the OOXML limit, treat metadata publication as completion of image OCR, or version existing processors merely to obtain replay. The user's instruction against version churn/backward-compatibility work remains applicable. It does not permit modifying terminal branches or bypassing source deletion.

Scope reconciliation: the practical amendment in `docs/superpowers/specs/2026-09-19-visio-ocr-delivery-design.md` restricts its delivered OCR increment to the PDF route. Standalone image OCR was not delivered by that increment. JPEG/PNG ingress below is an explicit, bounded extension needed by the subsequent request to make OCR work on files in the affected source; it is not a claim that the old PDF-only spec already delivered images. Keep its accepted accuracy limitations and diagnostics intact.

## Design decisions

### One logical owner, using existing durable fields

Use the existing persisted origins as output semantics: 0 physical source, 1 archive member, 2 internal document content/input, 3 internal metadata. Named constants or a small internal helper are sufficient; no public provider abstraction or schema change is required.

An OriginKind 2 or 3 child is publishable only with an exact completed processor-branch/member binding. Check all of the following in the existing transaction:

1. The branch owns the immediate parent revision and its input hash matches that owner. Parent, child and branch activity belong to the same source root; all relevant revisions are unsuppressed and the root permits publication.
2. The completed member names this child and its exact activity. That activity names the publishing pipeline record and revision, and its input hash, child revision and retained artefact agree in hash and byte length. Validate the expected registered producer descriptor and durable processor identity against the branch's owning activity, reusing existing capability identity checks. Do not introduce a second format allowlist in Publish. Origin alone never confers publication authority.
3. The logical branch contains exactly one emitted child and the durable manifest/count agrees. Reject ambiguous/mixed/multiple outputs at branch commit and again at publication. Never choose the first segment. Zero emitted children require the explicit no-content outcome below.
4. Retained binary inputs keep the existing stricter format contract and exact-parent-artefact checks. A generic UTF-8 child cannot impersonate a PDF, Visio or image input, or gain access to its binary loader.

Use `DocumentPublications` unchanged. Reuse the serializable source fence, publication selector lock and atomic final Publish transaction; failed publication must not leave a completed visible record. Preserve the previous selected result until a valid replacement commits. Repeated completion is idempotent.

Content publication has precedence over metadata. Metadata cannot replace a successful content result even if it completes later. Within the same semantic class, preserve newer-branch selection; use an explicit stable tie-break for equal creation times (ordinal canonical branch ID) rather than arrival order or mutable update time. Within the same branch/input, an older pipeline revision cannot replace a newer one. Apply this comparison under the selector lock. The tie-break defines a deterministic choice, not a claim about chronology.

Keep legacy archive-member retirement restricted to the existing document-container conversion cases. New OOXML text, image or metadata publication must not run blanket OriginKind 1 suppression. An archive member that independently receives a document result owns that result itself; publication never jumps to the outer archive. This is an ownership regression test, not authority to add new archive-format discovery/extraction features.

### Preserve the 200 MiB OOXML capacity without splitting publication

Emit one strict UTF-8 OOXML artefact up to the revised 200 MiB extracted-text limit. Preserve all existing package/element/relationship/path/expansion/security limits, including the 500,000-element limit. Do not truncate text.

Permit up to 200 MiB only for a verified, completed, single-child OOXML binding in registration and retained UTF-8 reading. Update the unlinked-activity offer predicate as well as actual registration/read validation; otherwise larger outputs would silently remain Pending. Ordinary text, archive-member text and unrelated processors retain their existing 16 MiB allowance. Reuse handle-bound/checksum-verified reads and the writer's existing per-call bound. This allowance applies to extracted UTF-8 text; it does not raise the existing 128 MiB compressed input, 256 MiB expanded-package, entry-count, compression-ratio, path, traversal or relationship limits.

For genuinely empty structural output, commit an explicit skipped member outcome with reason `office-document-no-extractable-text`, no pipeline child and an auditable receipt. Project it as completed with no extractable text, not Pending or Indexed. This is structural extraction only; embedded-image OCR in OOXML remains outside this repair.

### Truthful owner status and retrieval

Use the same logical-output eligibility across `SqlFullTextSearch`, `SqlSearchHydrator`, `SqlCorpusProjectionReader` and `SourceRootProjectionReader`. OriginKind 2/3 outputs are visible only through the selected owner publication, including list counts, details and previews. Ordinary files, archive members, Outlook and C# paths retain their contracts. Do not expose synthetic internal locators as source documents.

For owners without a publication, project progress and terminal reasons through the exact active/selected branch, child activity and pipeline binding. A downstream failure/defer is visible at the original file; no-content completion is terminal without an index claim. Use durable branch order/semantic precedence consistently; do not let an obsolete cancelled branch dominate newer progress. Existing parent activities remain immutable execution history.

For an owner with a last-good publication, keep that result searchable while showing an in-progress or failed replacement honestly in existing detail/status fields. Metadata-only publication must be labelled `metadata only`; if OCR is the selected route and is pending/failed, metadata must not mask its state or imply extracted image text. No dashboard redesign or new tiles are required.

### Image OCR through the existing complete implementation

Add a concrete JPEG/PNG document-input capability (`.jpg`, `.jpeg`, `.png`) and its one hidden OriginKind 2 input, using the same binary ownership checks as PDF. Select this route for supported images when the existing local OCR flags are enabled, before metadata promotion. Make that routing decision effective in candidate selection/claiming, not just the order of a loop, so concurrent activation cannot hand the same deferred work to metadata. When OCR is disabled, retain honestly labelled metadata-only behaviour. No automatic metadata fallback may conceal an OCR refusal.

Extend the existing Extract document operation with an image case: one logical page, zero-based page index 0, marked as requiring OCR. Reuse `DocumentOcrRequests`, the existing GPU handoff, completion coordinator, result merge and provenance. Preserve source hash, retained revision, job/revision, model/settings and admission-generation bindings through cancellation, retry and deletion. No separate image pipeline or SQL table is needed.

Add a small image raster helper using the already referenced SkiaSharp dependency. Decode only checksum-verified retained JPEG/PNG bytes, check format/frame/dimensions before full allocation, enforce the existing 64 MiB input, 25 MP/6,000-pixel raster, 16 MiB PNG output and execution limits, and refuse unsupported/corrupt/oversized inputs with precise reasons. Normalise raster orientation consistently and retain the original-image to OCR-raster orientation mapping so provenance is correct; test EXIF orientation as well as pixel rotation. Do not silently resize away important text. Treat ambiguous/unsupported multi-frame input as a refusal. Pass the bounded PNG into the same frozen Paddle process; no PDF wrapper or new runtime is necessary.

Keep all frozen model revisions, orientation/order settings, scheduler admission and J-only cache checks. A cache miss refuses; it never downloads or selects another drive. Source images and licence contents stay out of logs/Git. Empty or uncertain OCR is reported honestly and is not counted as successful text retrieval.

### Existing data and operational scope

No historical backfill, terminal-job reset, processor-version bump or automatic successor replay is part of this fix. Completed old pipelines will not spontaneously republish. Existing old multi-child OOXML branches must never publish a partial segment under the revised selector.

The user authorised deleting/re-adding the test source through the application to exercise ordinary creation. After verified deployment, use that lifecycle for this source only, preserve its root/settings, wait for deletion to complete, then create it once. Do not touch watched files or trigger scan/replay/activation/worker endpoints manually. Preserve unrelated sources and terminal records. If read-only pre-deployment inspection shows the new image capability would process unrelated retained work, resolve that scope issue before Apply; do not hard-code a production source ID into routing or silently mutate other roots.

## Implementation and verification plan

### Task 1: complete logical-document publication

First observable result: a disposable source containing a public DOCX goes through the real scan, retained processor, ordinary pipeline and search, resulting in one original-document corpus entry and an Indexed owner. Do not satisfy this test by seeding a publication row or manually marking work completed.

Files to change:

- `src/FluxKnowledge.Application/Sources/OoxmlStructuralTextProcessor.cs`: one bounded artefact and explicit no-content outcome.
- `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedProcessorBranchStore.cs`: durable logical cardinality/binding validation; leave archive behaviour intact.
- `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedTextRegistrationStore.cs`: OOXML-specific 200 MiB registration/offer/read allowance with exact binding.
- `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlStageTransitionStore.cs`: common owner publication, metadata precedence, deterministic ties and guarded legacy retirement.
- `src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlFullTextSearch.cs`, `Search/SqlSearchHydrator.cs`, `Persistence/SqlCorpusProjectionReader.cs`: consistent selected-output visibility and owner identity.
- `src/FluxKnowledge.Web/Components/Sources/SourceRootProjectionReader.cs`: owner progress/terminal outcomes, truthful classification and consistent counts. Keep view changes minimal if an existing reason field needs rendering.

- [ ] Retain failing tests in `OoxmlReplayIntegrationTests`, `DocumentPublicationIntegrationTests`, `RetainedTextPipelineIntegrationTests`, `CorpusProjectionIntegrationTests`, `HybridSearchIntegrationTests`, and `SourceRootProjectionReaderIntegrationTests` proving completed children currently leave owners unpublished/Pending.
- [ ] Cover DOCX/XLSX/PPTX through normal registration and workers; verify one original identity, useful search, and zero exposed internal segments. Exercise a same-content file in another root to prove separate ownership despite shared artefacts.
- [ ] Change the explicit two-segment test in `OoxmlStructuralTextProcessorTests` to require one artefact. Add registration/read checks above 16 MiB, at 200 MiB and over the limit, including multibyte UTF-8 and normalisation/chunking completion; ordinary UTF-8 over 16 MiB and forged OriginKind 2 must remain refused. Avoid duplicating several simultaneous 200 MiB allocations in a single test process. This intentionally changes segmentation and raises the supported extracted-text limit without changing the other container limits.
- [ ] Exercise missing/wrong-root/wrong-activity/wrong-hash bindings, mixed/multiple-child rejection, explicit zero-content outcome, downstream failed/deferred state, repeat Publish, equal-time competition in both completion orders, older branch/pipeline-revision completion after replacement, and metadata after content.
- [ ] Retain PDF/Visio, archive-member identity, legacy-retirement isolation, pause-before-Publish, delete-during-Publish, source suppression and shared-file deletion checks. Use actual interleavings/transactions for affected concurrency invariants.
- [ ] Implement the focused changes and make these tests pass. Keep a small internal binding helper only if needed to share the exact same authority check; no broad query or provider abstraction.

### Task 2: finish the affected image path

Files to change/add:

- Add `src/FluxKnowledge.Application/Sources/ImageDocumentInputProcessor.cs`; extend `Documents/DocumentProcessingInput.cs` with the concrete image contract.
- Update `Sources/SourceClassifier.cs`, `Sources/RetainedProcessorActivationService.cs`, `SqlRetainedProcessorBranchStore.cs` and `SqlRetainedTextRegistrationStore.cs` for image candidate routing, registration and ordinary dispatch. Audit all existing contract switches; only proven format dispatch belongs here, never in the publication selector.
- Update `src/FluxKnowledge.Application/Workers/ExtractDocumentStageWorker.cs`, `Documents/IDocumentOcrExecutor.cs` and relevant handoff comments to describe both approved inputs. Preserve the current execution interface where possible.
- Add `src/FluxKnowledge.Integrations/Documents/ImageDocumentRasterizer.cs`; update `PaddleOcrVlmLocalExecutor.cs` to use it for the exact image signature and keep the PDF raster route unchanged.
- Update `src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs` and `src/FluxKnowledge.Web/WebHostComposition.cs` for the existing flag-controlled composition. Change `SqlDocumentOcrStore`/completion code only if an actual PDF-specific predicate prevents the unchanged binding from working.

- [ ] Add focused image input/raster tests and extend `PaddleOcrVlmLocalExecutorTests`, `SqlDocumentOcrHandoffTests` and normal source-flow tests: JPEG/PNG, EXIF/pixel rotation, corrupt/oversized/multi-frame refusal, OCR-disabled metadata-only state, enabled OCR winning routing, and no metadata downgrade. Use existing public/synthetic fixtures with independently checked text/numbers.
- [ ] Prove failed/missing-model OCR produces an honest owner reason with no fallback/download; tests use doubles and must never acquire or load real models. Retain real-model tests as explicit opt-in checks.
- [ ] Prove ordinary source add reaches GPU handoff, result continuation, owner publication, provenance and useful search. Prove repeated worker/activation delivery creates one logical result; pause/deletion cancels or fences both image and PDF results and preserves unrelated/shared data.
- [ ] Run one bounded real GPU image check with the unchanged verified local bundle and independently inspected fixture, plus existing native/scanned/mixed PDF and Visio regressions. Preserve all previous accuracy failures and limitations. Do not start another model survey or synthetic tuning loop.

Effort checkpoint: after these two coherent tasks, the disposable end-to-end outcomes must work. A need for a new runtime, model, schema, output aggregator or broad lifecycle redesign is a reason to stop and present the concrete blocker; it is not permission to extend this plan.

### Required commands and evidence

Run narrow tests first; use the existing disposable `NativeSqlServerFixture`, never production SQL. The executor must verify the exact test filters discovered the intended tests and report skipped/unexecuted real-runtime checks honestly.

```powershell
dotnet restore FluxKnowledge.slnx --locked-mode --ignore-failed-sources
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~OoxmlStructuralTextProcessorTests|FullyQualifiedName~ImageDocument|FullyQualifiedName~PdfDocumentProcessingTests|FullyQualifiedName~DocumentOcrProvenanceTests'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~OoxmlReplayIntegrationTests|FullyQualifiedName~DocumentPublicationIntegrationTests|FullyQualifiedName~RetainedTextPipelineIntegrationTests|FullyQualifiedName~MediaMetadataReplayIntegrationTests|FullyQualifiedName~ImageDocument|FullyQualifiedName~SqlDocumentOcrHandoffTests|FullyQualifiedName~SourceDeletionIntegrationTests|FullyQualifiedName~CorpusProjectionIntegrationTests|FullyQualifiedName~HybridSearchIntegrationTests|FullyQualifiedName~ZipArchiveReplayIntegrationTests'
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SourceRootProjection|FullyQualifiedName~CorpusProjection|FullyQualifiedName~OoxmlStructuralTextPrivacy|FullyQualifiedName~MediaMetadataProcessorPrivacy'
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build --logger 'console;verbosity=minimal'
git diff --check
```

Use process-local `FLUX_KB_RUN_LOCAL_PADDLE_OCR=1` only for the explicitly selected existing local-runtime tests, after the J-only gate/receipts are checked. Never print the licence value. Record actual commands, test counts, failures and final fresh results; do not reuse historical success claims for changed code.

Update affected native entries in `docs/architecture.md` and `docs/roadmap.md` when implementation evidence exists. Do not claim progress percentages from this plan, rewrite historical diagnostics, or regenerate manuals/screenshots. One independent Astra final review covers the full diff, lifecycle races, tests, source scope and the concrete deployment/rollback plan. Fix demonstrated blockers only.

### Deployment, ordinary source flow and closeout

After the model switch and implementation, use the existing deployment authority in this conversation. Resolve current main changes without losing unrelated work. Freeze a reviewed commit and record the current known-good deployed commit for rollback.

```powershell
pwsh -NoProfile -File scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot 'C:\Users\os008\.codex\worktrees\logical-document-publication\LLM KB' -PlanOnly
# Review exact targets, payload, readiness and recovery output; independent pre-deployment gate.
pwsh -NoProfile -File scripts/deploy/update-native-iis-incremental.ps1 -SourceRoot 'C:\Users\os008\.codex\worktrees\logical-document-publication\LLM KB' -Apply
```

Do not change the updater, use GoLive, manually restart IIS, or run a migration. Capture the updater's actual output and release identity.

Live validation, restricted to the existing authorised test source:

1. Read its current identity/root/settings and unrelated-source baselines. Inspect the read-only originals and select expected passages/numbers before looking at the new predictions; keep private expectations/results outside Git. Record input hashes without modifying the watch folder.
2. Delete that source through the supported application operation, wait for its owned jobs/files/corpus cleanup to finish, and re-add it once with identical settings. The user's no-backup instruction applies to this test source; retain only redacted operational evidence. If it is already absent, perform only the authorised re-add. No raw SQL deletion or forced scan/reprocess calls.
3. Observe the normal scan, activation, worker dispatch and completion. Verify each original DOCX/XLSX/PDF/VSDX has one selected corpus identity, appropriate terminal status and useful search results; verify the JPG has actual OCR text and correct image/page provenance. Metadata alone cannot pass the JPG check. Check expected numbers/identifiers and report consequential errors separately from acceptable formatting differences.
4. Check there are no exposed document/metadata child entries, no duplicate publication on an ordinary subsequent reconciliation, no indefinite Pending after terminal work, and no change to unrelated membership or input hashes. Do not trigger reconciliation manually. Check Visio remains on its interactive host and current capability.
5. If rollout fails, use the generic updater's supported recovery. For a post-deployment regression, pause only the affected source through the application and redeploy the recorded known-good checkout via reviewed PlanOnly/Apply, subject to the same independent production gate. Preserve SQL ownership history and shared artefacts; do not roll back by resetting jobs or deleting other sources. Report that the known-good build retains the pre-existing publication limitation.

After successful deployment and live verification, use the repository closeout script rather than manually substituting its sequence:

```powershell
pwsh -NoProfile -File scripts/dev/complete-feature.ps1 -FeatureWorktree 'C:\Users\os008\.codex\worktrees\logical-document-publication\LLM KB' -MainRoot 'E:\LLM KB' -CommitMessage 'Complete logical document publication and image OCR'
```

Inspect the current script's dry-run when preparing closeout; do not supply `-GoLive`. If it fails, report `failed_step` and `log_path`, correct only the authorised failure and rerun. If merging changes the deployed code, rerun affected checks/review and the generic updater flow before declaring deployment current. Never purge unmerged/unverified work or touch `J:\Models` during cleanup.

## Planning review and handoff

Self-review criteria: every current symptom is traced to a concrete binding/status/routing change; the 200 MiB extracted-text capacity and all other archive security limits are preserved; zero/multiple outputs and downstream failures have explicit outcomes; image OCR is tested as text extraction; ownership/deletion/last-good invariants are covered; prior no-version/no-manual-trigger instructions are honoured; implementation, deployment and data operations have not begun.

Independent design review: Astra reviewed the current code and approved the direction subject to concrete binding/cardinality, guarded member-retirement, deterministic selection, capacity, terminal-status and image-routing requirements. The user subsequently raised extracted OOXML text capacity to 200 MiB; the same exact-binding rule contains that larger allowance. Those requirements are incorporated above, including pipeline-revision ordering and multibyte/normalisation tests. This is design review, not implementation or live verification.
