# Incremental index publication implementation plan

Date: 20 October 2026.
Status: implementation resumed with user authority on 20 October 2026; isolated format-2 publication is default-off pending the remaining gates.

**Goal:** Remove whole-corpus work from small publications and repeated warm global queries, while bounding obsolete derived storage and preserving source/query/deletion guarantees.

**Design:** [Incremental index publication and bounded retention](incremental-index-publication.md).

**Architecture:** SQL remains canonical. Version-2 generations are manifests of bounded immutable USearch segments, sharing segment membership. Transactional affected-scope changes, short fenced activation, durable native ownership and bounded reclamation preserve correctness.

**Technology:** Existing .NET 10, C#, EF Core, SQL Server/Full-Text, embedded USearch and Windows storage guards. No dependency or real-model acquisition is planned.

**Execution:** One implementation owner. Use relevant execution/testing guidance without per-file agent handoffs. Independent Astra review covers consequential design, final implementation and operational gates. The requested model-switch pause has ended following the user's approval.

## Preserved work and resumption

- Reuse the current dedicated task worktree. Source-access commit `428484c4`, reviewed Web fixture corrections `439ec891` and design boundary `56310e06` preserve unfinished delivery work; do not reset, overwrite, purge or silently mix their operational scopes into an index migration.
- Before implementation, verify the handoff, current Git/peer state and storage. Coordinate an isolated test window and later exclusive main closeout; no resource reservation is currently implied.
- Keep the source-access canonical closeout/deployment packet separate. Resolve a clean commit boundary before index runtime edits; if capacity prevents that closeout, preserve its exact changes and use reviewed local commits without invoking production or treating integration as complete.
- The approved existing-file ACL cohort and accepted pilot publications remain completed evidence. The additional folder grant/privileged broker activation, index migration and format-1 capacity cleanup remain separately scoped.
- The user subsequently approved this correction's implementation, production investigation, deployment and migration, and all read-only app reports. Keep reports bounded and retain independent operational gates. Missing SQL rows or age still do not establish deletion eligibility. New folder grants/broker installation remain separately scoped. Live performance monitoring during the fix is low priority by the user's latest direction.
- Use E: staging overflow while I: lacks capacity; honour configured isolated SQL/storage paths. Do not silently send builds to C:, borrow production bindings or download models.

## Global implementation constraints

All design invariants apply to every task. Keep both formats readable; new-format writing is default-off until migration. Preserve source eligibility/disclosure, current stamp and empty-catalogue semantics. Keep canonical vector draft ownership unchanged. No full-corpus fallback on ordinary publication, no tombstone shortcut, no unsupported format fallback and no deletion by age alone.

### Verified execution checkpoint

The isolated, model-free host now exercises supported REST/MCP publication and cited readback, normal restart, revision replacement and stale-citation refusal, forced process termination with two cached native owners, recovery, source deletion and automatic physical retirement. A separate control root remains searchable and readable. Combined checks passed 81 integration and 22 Web cases without skips. Successful candidate worklists are removed in the activation transaction; format-1 retention explicitly accepts only self-contained format-1 snapshots and refuses restoration after a format-2 cutover. Its full suite passed 45 cases with one existing opt-in scale test skipped. Production is unchanged.

Bounded maintenance now uses the existing cache sweep, keeps at most two inactive manifests for at most 24 hours, and admits terminal orphan producers only after a 30-minute grace. Rotating selection, full verification-read reservations, deferred-manifest status and observed queue drainage pass focused tests, including SQL retry configuration. Confirmed process exit releases abandoned candidates; temporary terminal worklists and consumed change records are pruned in bounded pages, preserving every pending candidate's base watermark. Publications can merge four small segments within one build budget while preserving unrelated files, membership and cached native handles. A genuinely stale merge is refused, and a tighter raw-byte budget prevents merging. Independent review accepted both slices; combined lifecycle/retirement/catalogue/merge/schema checks passed 52 cases without skips.

The eligible-vector query now seeds affected records through indexed source/parent/document-owner/root relationships before reading chunks or vectors. Actual-plan regressions measured one source plus its exhaustion query against 1,000 and 10,000 unrelated vectors/members: vector rows examined fell from 4,006/40,006 to 2/2, and current-ledger rows from 2,003/20,003 to 2/2. Current selection uses the three existing scope indexes and deliberately retains invalidated BEFORE members for removal. Shared final publication eligibility and coalesced scope predicates remain authoritative for eligible-vector selection. Independent review accepted both corrections; 55 combined checks passed without skips. This does not establish constant work for large affected roots/owners or complete the publication/cache scaling gate.

Six isolated pipeline/cache measurements now compare format-1, fitting-cache segmented and partial-cache segmented operation at 38 and 377 base vectors. A small segmented addition reads one builder vector/1,024 payload bytes and creates one 1,280-byte native file at both sizes, with zero full-generation member rows. Prior files and segment memberships remain unchanged. The format-1 addition reads 39/378 builder vectors and inserts 39/378 full-generation member rows. Total newly created file sizes are 5,825/33,518 bytes for the segmented cases, including native and metadata files; this is not measured disk-write traffic. Fitting warm ANN queries open no native segments; a 16 KiB partial cache, which can hold a full segment, reopens all 6/49 segments per query. Instrumented query-store payload reads are zero on the segmented path. These tiny deterministic fixtures establish reuse and scaling mechanisms, not total SQL I/O, production RSS or a general latency improvement. Independent review accepted the evidence with those limits; production cache values remain unfrozen.

The actual build-vector query also reads two vector rows at both 1,000 and 10,000 unrelated vectors, including its final empty page; no query behaviour correction was needed. The four selection checks pass without skips. Non-empty recovery storage still refuses retirement until the migration-boundary audit and enforced format-aware writer/consumer contracts are implemented; this temporary restriction must not be shipped as production retention support.

Explicit conversion, repair and current-SQL format-1 rollback now use purpose-bound durable candidates. The operation binds source/target format, base generation, corpus stamp and model profile. Full worklists are captured in bounded metadata pages; activation verifies current canonical eligibility and changes the pointer, membership and required format atomically. A replay of an already committed operation returns its receipt without reverting later publications. A fresh repair creates a new physical incarnation; resuming the same operation verifies and reuses its placed files. Explicit candidates cannot be abandoned through the ordinary publication API, and takeover requires proved owner exit. Streamed format-1 rollback refuses an empty eligible set and leaves the current format intact.

The combined migration, native-builder, format-1-reader and physical-path suite passes 82 cases without skips or build warnings. It covers in-process placement/seal interruptions, corrupt/missing derived objects, bounded metric-probe allocations, tied ANN results and unchanged format-1 descriptor-read counts. This does not replace a separate-process placement-before-SQL crash rehearsal. CLI/updater wiring, every-start format admission, the non-empty Recovery audit, partial-cache/resource defaults and complete independent release review remain. The new index format has not been deployed.

Add meaningful tests first for behaviour/invariant changes. All ordinary tests are synthetic/model-free; dedicated staging tests must positively prove ANN use. Release builds retain zero warnings. Existing production evidence is context, not permission or proof of the new implementation.

## Task 1 — Publish and search a segmented generation

Deliverable: an isolated ordinary publication creates a version-2 manifest, reuses an unaffected segment on a second publication, and searches it through the real supported query path.

**Existing files to extend**

- `src/FluxKnowledge.Application/Ports/IIndexGenerationStore.cs`, `IIndexGenerationPublisher.cs`, `IAnnIndex.cs` and `ICorpusGenerationLeaseStore.cs`: versioned descriptors, immutable candidate proof and lease access.
- `src/FluxKnowledge.Application/Indexing/PublishStageWorker.cs` and `Pipeline/StageTransitionRequest.cs`: carry the sealed candidate identity instead of a full-corpus `ActivateMembership` array on the version-2 path.
- `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlPipelineStore.cs`, `SqlStageTransitionStore.cs`, `SqlPublishedPassageSelection.cs`, `SqlCorpusGenerationLeaseStore.cs` and `FluxKnowledgeDbContext.cs`.
- `src/FluxKnowledge.Infrastructure.Usearch/UsearchGenerationBuilder.cs`, `UsearchGenerationValidator.cs`, `AtomicGenerationPlacement.cs` and `UsearchCorpusAnnLeaseFactory.cs`: retain format-1 routing.
- `src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlCorpusRetrievalReader.Dense.cs`.
- `tools/FluxKnowledge.StagingHost/Program.cs` and existing staging composition/settings: deterministic ANN query fixture only.

**Focused new units**

- `src/FluxKnowledge.Application/Indexing/SegmentedIndexContracts.cs`: `IndexSegmentDescriptor`, `IndexManifestDescriptor`, `IndexPublicationDelta` and `SegmentedIndexCandidate`. Descriptors carry format/profile/digest/count; a delta carries expected/result stamps, base generation, affected lineage and added/removed membership identity. Use paged payload readers and sealed candidate IDs, not unbounded vector lists.
- `src/FluxKnowledge.Application/Ports/ISegmentedIndexStore.cs`: `ReadPublicationDeltaAsync(Guid draftGenerationId, CancellationToken)`, paged segment-member/payload access and `ReadManifestAsync(Guid generationId, CancellationToken)`. Activation stays inside the existing stage transaction, not an independently committing service.
- `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSegmentedIndexStore.cs`, `SqlIndexProjectionChanges.cs`; matching entities/configuration and one additive migration.
- `src/FluxKnowledge.Infrastructure.Usearch/UsearchSegmentBuilder.cs`, `UsearchManifestValidator.cs`: build/seal/validate bounded units and a manifest, reusing existing storage helpers.

**Test-first sequence**

- [ ] Add `tests/FluxKnowledge.Integration.Tests/Indexing/SegmentedPublicationIntegrationTests.cs`: two new sources, multiple segments, correct SQL/member uniqueness, immutable reuse, exact selection oracle, same-delivery replay, stale stamp rejection and format-1/default-off compatibility. Verify failure against the current behaviour.
- [ ] Add format/profile/digest/path/collision tests alongside existing `UsearchGenerationTests` and `WindowsIndexGenerationReadLeaseTests`. Cover differing native byte encodings for an identical semantic identity without overwriting sealed data.
- [ ] Implement the additive model, new-source delta preview and atomic stage activation; remove whole-vector reads/counts from this version-2 route. Reuse shared publication predicates and existing retry semantics.
- [ ] Integrate manifest ANN fan-out/merge and exact scoped SQL membership joins. Initially uncached segment reads are allowed only inside this development batch; warm reuse is required by Task 2.
- [ ] Extend `CorpusRetrievalEndToEndTests` and staging composition with deterministic query encoding. Assert actual native ANN execution, global/workspace eligibility and REST/MCP/CLI parity, not merely HTTP success or lexical fallback.
- [ ] Run the focused tests and a zero-warning Release build in the coordinated isolated window; retain bytes/rows/native-open counters from this slice. Commit a coherent local development boundary.

## Task 2 — Complete delta closure, updates/deletions and native ownership

Deliverable: the first complete publish/search/update/delete/restart/concurrent-reader slice. This is the **two-batch effort checkpoint**, not production readiness.

**Files**

- Extend Task 1 units plus `SqlSourceScanStore.cs`, `SqlNativeOperationStore.cs`, `SqlSourceDeletionStore.cs`, `SqlRetainedPublicationBinding.cs`, the `SqlCorpusRebuildStore` partials and `SqlDerivedIndexRecoveryStore.cs`. Document winner selection is `SqlStageTransitionStore.PublishDocumentIfApplicableAsync`, not a separate store.
- Add `src/FluxKnowledge.Infrastructure.Usearch/UsearchSegmentCache.cs` and `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlIndexSegmentOwnerStore.cs`; adapt `SqlCorpusQueryLeaseRecovery.cs` and existing host lifecycle composition.
- Extend `SqlCorpusRetrievalReader.Dense.cs`, relevant query lease configuration and source deletion/recovery tests.

**Test-first sequence**

- [ ] Extend the SQL oracle fixture with old/new revisions, branch winner replacement, derived children with different physical owners, root suppression/restoration, overlapping deletions and last-good-publication preservation. Assert every relevant version advance records complete affected scopes and that missing log intervals fail closed.
- [ ] Implement copy-on-write affected-segment replacement and the current-membership ledger. Stream root-wide changes. Ordinary derived refresh must consume the same scope log instead of full rebuilding.
- [ ] Add `tests/FluxKnowledge.Integration.Tests/Indexing/SegmentedPublicationRecoveryTests.cs`: cancellation/low disk, placement-before-SQL, SQL-before-ack, concurrent publication, duplicate recovery, zero eligible vectors and explicit epoch/profile changes.
- [ ] Add `tests/FluxKnowledge.Integration.Tests/Indexing/SegmentOwnerLifetimeTests.cs`: register before native open, concurrent borrow/dispose, lost SQL session with live process, unknown owner, verified process exit, failed native disposal, retirement/open race and repeated retirement. Prove no permanent idle shared lock and no cleanup before disposal.
- [ ] Implement bounded verified-handle reuse and retirement observation using the existing process-incarnation proof, lock order and physical read guards. Keep uncertain ownership visible and actionable.
- [ ] Implement the minimal exact retirement operation needed by this slice: mark/commit under exclusive recovery then publication fence; release both locks while owners dispose; reacquire in the same order and revalidate intent/closure/owners before guarded physical removal. Test a borrower release/owner acknowledgement requiring SQL access, and a new reference attempted between phases. No waiting for native disposal under the publication fence. Task 3 adds scheduled budgets to this same operation.
- [ ] Complete the synthetic source lifecycle through REST/MCP, including current citation reads and physical retirement after a held reader releases. Prove unaffected warm handles, files and SQL membership are reused.
- [ ] Review this executable milestone independently. If the slice is absent, report the blocker and revise the smallest safe delivery path before broadening. Do not proceed with a large unfinished foundation.

## Task 3 — Bound ongoing storage and prove scaling

Deliverable: sustained small publications/deletions do not cause unbounded obsolete storage or repeated full-corpus work.

**Files**

- Extend `SqlIndexRetentionOperations.cs`, `SqlDerivedIndexRecoveryStore` and existing native operation/receipt contracts for version-2 reference closure and unknown-outcome recovery.
- Add only focused segment-lifecycle logic where the existing retention class would otherwise mix incompatible formats, for example `SqlSegmentRetentionOperations.cs`.
- Extend `DerivedIndexRecoveryCoordinator.cs` and `UsearchIndexOptions.cs` for bounded low-priority work/admission. Do not add another independent scheduler.
- Extend existing status/event projections only for actionable capacity, retirement and owner blockers.

**Test-first sequence**

- [ ] Add `tests/FluxKnowledge.Integration.Tests/Indexing/SegmentRetentionTests.cs`: active manifests, query/cache owners, canonical draft references, rebuild/recovery/source-deletion pins, unknown references, protected paths, hard links/reparse paths, lock loss and crash between file and SQL phases.
- [ ] Inventory consumers of Publish artefact generation IDs. Prove that version-2 completed artefacts retain historical proof without an unintended perpetual physical pin; preserve all format-1 semantics and operation/recovery pins.
- [ ] Implement at-most-four-input bounded merging and the proposed manifest/orphan/reclaim budgets from the design. Exercise repeated invocations and source-deletion override of optional history; neither payload copies nor SQL/member/change-log rows may accumulate without a current reason.
- [ ] Verify a reclaim intent pins its exact targets, blocks reacquisition, reconciles missing files/acknowledgement loss and retains an unknown outcome. Garbage-collect consumed change rows only past all pinned base/candidate/recovery watermarks; a gap must be detected.
- [ ] Test disk admission and recovery-copy accounting. Automatic ordinary reclamation of rebuildable version-2 bytes creates compact receipts, not an unbounded backup tree. A configured required backup blocks reclaim if its destination or approved lifetime is unavailable.
- [ ] Add `SegmentedPublicationScalingTests.cs` using deterministic 1x/10x corpora and an independent full-selection oracle. Count writes, payload reads, membership inserts, fence work, native opens, peak storage/memory and include merge/cleanup overhead. Verify unchanged objects incur zero payload/member rewrite.
- [ ] Run repeated global/scoped queries with warm and cold caches, both fitting and exceeding the cache budget, against the format-1 path on the same isolated inputs. Check candidate validity, deterministic ties, exact-fixture top-k and approximate recall comparison; do not equate a synthetic query with real-model quality. Record latency distributions, native reopen counts and peak memory. Fitting-cache success alone is insufficient for the production query-performance gate; fix material regressions before freezing defaults.
- [ ] Freeze segment/cache/maintenance defaults from the evidence. Current issue views must clear a resolved capacity/segment error, while unknown owners and unresolved pins retain their exact reason. Obtain review of the completed lifecycle and scaling evidence.

## Task 4 — Verify format transition and rollback in staging

Deliverable: a resumable migration and a rollback which preserve the latest canonical state.

**Files**

- Extend generation/version configuration, `SqlCorpusRebuildStore` and `SqlDerivedIndexRecoveryStore`, and the new format migration from Task 1.
- Extend `scripts/deploy/update-native-iis-incremental.ps1` only if its supported migration/hold contract can safely represent the sequence. Otherwise document the exact gap; do not switch to clean-slate deployment.
- Add `tests/FluxKnowledge.Integration.Tests/Indexing/SegmentedIndexMigrationTests.cs` and relevant existing deployment contract tests.

**Sequence**

- [ ] Test old schema/data with version-aware code still publishing format-1 snapshots; migrations are additive and do not bootstrap at startup.
- [ ] Implement explicit bounded bootstrap, resumable sealed progress and final stamp validation. Test changes during conversion, stale candidate rejection, epoch reset, corrupt/missing segment and retries without duplicate writes.
- [ ] Prove exact vector/member/profile equality and supported retrieval before/after activation. Verify a claimed empty catalogue uses the existing canonical proof.
- [ ] Implement and test external deployment/start admission in the updater/operator launcher: compare SQL required format with the exact payload capability before **every** candidate, predecessor, restart/recovery or rollback start. An unsupported/unknown old binary is refused before the Start callback and hold release; neither an application flag nor a check only in new code is sufficient. Test the predecessor rollback callback explicitly.
- [ ] Test production-style cutover in staging with old writers/native owners drained. A flag change cannot bypass SQL's format requirement; unchanged supported version-aware payloads still start. If the current updater cannot cover the sequence, preserve the hold and document the specific gap for direction.
- [ ] Test rollback after an update and a deletion: rebuild a format-1 projection from current SQL, validate it, switch atomically, then permit an old payload. Old deleted content must never reappear. Low capacity or an unknown owner must stop the procedure before cutover.
- [ ] Rehearse interrupted bootstrap/cutover/recovery without production data or bindings. Retain exact migration hashes, candidate identity, resource requirements and rollback evidence for the approval packet.

## Task 5 — Closeout and separately authorised delivery

- [ ] Self-review the complete change; obtain independent Astra review of security/eligibility, concurrency, migration/recovery, performance and resource evidence. Correct blocking findings without adding unrelated work.
- [ ] Update `docs/architecture.md`, `docs/roadmap.md` and targeted operator/developer configuration notes to match implemented behaviour. Keep manuals untouched; do not mark planned capability delivered.
- [ ] Coordinate exclusive main use. Run `scripts/dev/complete-feature.ps1` with the task worktree, verified isolated test bindings and `-KeepWorktree`, without `-GoLive`. Preserve the separate source-access delivery boundary. On failure report `failed_step` and `log_path`, fix and rerun the prescribed script.
- [ ] Prepare the exact incremental updater `-PlanOnly`, migration/format switch, maintenance limits, format-1-generation disposition, bounded live checks and rollback. Include combined peak disk/RAM and explicit preservation of other threads' release work.
- [ ] Obtain fresh specific human approval and independent pre-operation review before production investigation, capacity relief, retention-policy activation, migration, restart, deployment or canary. Technical design review grants none of these.
- [ ] Following approval, deploy only the reviewed scope and perform bounded representative publication/update/readback and current-status checks. Observe disk/SQL/native work against the staged expectation. No full-workspace ingestion for validation, blanket replay, source-content mutation or new ACL reset.
- [ ] Clear only resolved current issues, report remaining current causes/actionable items, deployment identity and limits. Complete independent live assessment and any still-required source-access acceptance before the previously requested squash/purge closeout; preserve unfinished work and recovery evidence.

## Verification commands and evidence

After resumption and resource coordination, use the existing disposable SQL setup with credentials loaded privately. Build first, then narrow test filters such as:

```powershell
dotnet build FluxKnowledge.slnx -m:1 -c Release
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SegmentedPublication|FullyQualifiedName~SegmentOwnerLifetime|FullyQualifiedName~SegmentRetention|FullyQualifiedName~SegmentedIndexMigration" --logger "console;verbosity=minimal"
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~CorpusRetrievalEndToEnd|FullyQualifiedName~StagingHostComposition" --logger "console;verbosity=minimal"
```

If storage requires an approved task-owned `ArtifactsPath` override, pass the same global property to build and test; keep the canonical closeout script authoritative. The existing scoped retrieval, publication/version, rebuild, source-deletion, physical-path, native-operation and deployment contract suites are also required when their behaviour is affected. Run the full required checks at final integration, not repeatedly between unchanged messages.

Record test counts/skips, zero-warning build output, exact commit/tree/configuration identities and the operation counters above. The independent full SQL oracle is for isolated tests/bootstrap, never ordinary publication. The first two tasks must yield the observable slice; the final gates add lifecycle, migration, scale and deployment evidence.

## Review focus

- A mutation outside ordinary Publish advances the stamp but fails to record the old owner: Task 2 closure/oracle tests.
- A lost SQL connection releases a lock while a cached native handle remains alive: Task 2 owner-loss/disposal tests and Task 3 cleanup tests.
- A historical artefact or recovery copy silently pins every old segment: Task 3 reference and sustained-storage tests.
- An old binary or old monolith returns deleted content after cutover/rollback: Task 4 format admission and current-SQL rollback tests.
- A staging endpoint returns lexical fallback and falsely passes ANN acceptance: Task 1 positive native-path assertions, repeated in Task 2.

The model-switch handoff must retain these two documents, the active private mini-roadmap, the generic source-access code/evidence and the pending approval scopes. Planning completion is not implementation or production completion.
