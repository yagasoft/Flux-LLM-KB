# Source deletion historical cleanup implementation plan

**Status:** Implementation and representative staging acceptance passed independent Astra final review. Canonical whole-branch verification and closeout remain pending; production authority is absent.

**Goal:** Complete source deletion at substantial historical membership fan-out, with durable progress, safe restart and useful failure information.

**Architecture:** Preserve the existing source fence, native ownership checks and survivor-generation proof. Commit cutover and exact physical cleanup targets before draining historical memberships in small transactions, then purge the owned graph and perform the existing guarded file cleanup.

**Stack and baseline:** .NET 10, EF Core SQL Server 10.0.10, SQL Server, embedded USearch and the existing Windows no-follow file store. Inspected commit `1e81e9bb8bc60bc1e20cbe805abafea0928faada`; branch `codex/source-deletion-history-fix` in the dedicated `source-deletion-history-fix` worktree. Parallel Release 1 and hook work stay separate.

**Specification:** The acceptance criteria and invariants below are the specification. One implementation owner continues this chat after the model switch. Independent Astra review covers the consequential design and final implementation; no per-task implementation handoffs are required.

## Authority and scope

- Implementation, isolated staging, tests and review are authorised. No production queries, investigation, retry, mutation, deployment, restart, migration or cleanup are authorised.
- Use unique staging database, configuration, runtime, storage and loopback endpoints. Do not copy production mutable data, load its configuration or reuse its bindings/tasks.
- No model acquisition. Synthetic vectors and real USearch/file operations suffice for this defect. Any later real-model use must satisfy the complete `J:\Models` rules first.
- Preserve originals, unrelated sources, shared blobs, model files, operational evidence and other worktrees. Do not execute the queued 17-generation retention operation or change retention policy.
- Do not regenerate the user manual. Update architecture and the source-lifecycle roadmap entry to reflect verified results at implementation closeout.

## Findings and working diagnosis

The supplied 14 October investigation is historical evidence, not current production status: 3,434 source vectors had 4,847,721 membership rows across 1,443 generations, in a table of approximately 22.8 million rows. Two aborted deletes had grossly underestimated cardinality and high CPU/logical reads. Blocking was not captured and is not established.

The inspected pre-change baseline confirmed:

- `SqlSourceDeletionStore.PurgeAsync` holds cleanup ownership and the publication fence in a serializable transaction around target capture, survivor activation and the entire graph purge.
- `DeleteOwnedGraphAsync` deletes all memberships using a parameter collection, then deletes vectors. The statement dates to `e62a55ef` (18 September); later changes preserve this shape.
- `IndexGenerationVectors` already has `(GenerationId, VectorId)` as its key and a `VectorId` index. Adding the same index is not a correction.
- The transaction failure discards both cutover and newly captured file targets. The earlier tombstoning phase remains committed.
- `SourceDeletionCoordinator` discards the caught exception. `SqlNativeOperationStore` retries non-file attention operations from `accepted`, not the failed durable phase.
- File targets already have a durable ledger, publication/query gates and identity-checked native deletion. Reuse these protections.
- Ordinary startup loads canonical production configuration unless the internal isolated-test composition has been established. Environment overrides alone do not establish staging isolation.

The correction addresses underestimated, unbounded work and its transaction/recovery boundary. It does not claim to prove the cause of every contemporaneous read timeout.

## Acceptance criteria

1. A real staging REST `root_delete` preview/commit completes through the background coordinator and actual Windows cleanup against at least 3,434 target vectors, 1,443 generations and 4,847,721 target memberships, with approximately 22.8 million total memberships. The seed is wholly synthetic and includes mixed-source generations and untouched survivor-only generations.
2. Each membership transaction removes at most 1,000 rows. Initial acceptance targets are batch p95 below one second, no membership command reaching the existing 30-second timeout, and completion within ten minutes on the documented staging hardware when no deliberate ownership hold is present. Measure first; a missed gate triggers one bounded adjustment or an explicit limitation, not a timeout increase.
3. Concurrent overview, source-list/detail and unrelated corpus reads have no failures/timeouts; read p95 is at most the greater of two seconds and twice the measured idle baseline. Record maxima and raw samples. Exercise shared semantic/query ownership separately with deterministic inputs and a real native index.
4. Cancellation/crash before and after a batch commit preserves an exact cursor/count; a new process resumes without duplicate effects. Ambiguous commit replay cannot double-count progress or advance twice. Expired/stolen SQL leases cannot change progress, fail another owner or record file completion. Physical cleanup retains its existing exact-target, idempotent replay semantics under the independent query/blob execution gates; it does not promise to revoke already-started native IO at the instant a SQL lease expires.
5. Duplicate API requests retain the same deletion operation and idempotent command receipts. A fresh retry after attention resumes the exact durable phase. pre-migration attention operations without new metadata remain recoverable.
6. Survivor publication stays correct during source deletion and concurrent publication. Cross-root ownership and native-cleanup uncertainty remain refused/draining. Unrelated canonical rows, survivor-only generations, shared blobs and original workspace bytes remain unchanged.
7. UI distinguishes running, waiting, attention and completed work; shows committed membership removals and last progress time without invented percentages. Failure diagnostics identify phase and safe cause. Completion requires physical target receipts as well as SQL completion.

## Design

### Durable phase boundary

Keep `accepted`/`draining` and `rebuild-index`. After validating the candidate against current published vectors and corpus stamp, atomically capture exact cleanup targets, retire affected historical generations, activate the survivor (or safely clear an obsolete active pointer when there are no eligible survivors), and commit `cleanup-memberships`.

Do not delete canonical vectors/records in that transaction. Do not mark the catalogue empty while canonical vectors or another owner's drafts remain. Preserve enough canonical ownership until membership drainage and graph purge are complete. Only a proven cutover may enter `cleanup-memberships`.

Then run `cleanup-memberships` -> `purge-records` -> `cleanup-files` -> `completed`. After cutover, a newer legitimate survivor publication may proceed; cleanup must not reactivate an old candidate. The final graph transaction rechecks the fence, ownership, dependencies, native cleanup and absence of owned memberships before deleting principals. Existing strict empty-catalogue handling runs only when its proof holds.

Capture affected generations before removing any memberships; otherwise the association needed for physical cleanup is lost. Make capture phase-idempotent. Do not recapture file targets or rebuild survivors on ordinary batch resumption. Replace the current broad `retireAllGenerations: survivorGeneration is null` rule: no eligible survivor does not establish ownership of another pending/deleting source's generations. Capture only generations intersecting this root's vectors or its owned drafts/checkpoints, unless a strict global-empty proof permits the existing empty-catalogue cleanup.

### Small scalar membership units

Use an explicit parameterised SQL delete for one exactly owned vector at a time:

`DELETE TOP (@batchSize) FROM IndexGenerationVectors WHERE VectorId = @vectorId`

The initial batch size is 1,000. A scalar equality avoids the large collection/OPENJSON estimate at the failing statement and can use the existing vector index. Inspect actual staging plans and reads; do not assume a row cap guarantees a good access path. If the scalar plan is inadequate, evaluate only a statement-local recompile or keyed temporary target set, using measured evidence. Do not change global EF translation, SQL isolation or add speculative indexes.

Persist a nullable `MembershipVectorCursor` (last fully drained owned vector) and `MembershipRowsDeleted` (`bigint`) on the existing operation. Select the next owned vector above the cursor through the canonical root relationship. A vector remains selected until no memberships remain. Commit row removal, count, cursor and progress timestamp together. Bound empty-vector advancement to one vector per unit as well. The final no-membership check must detect any late membership below the cursor; never silently skip it.

Each unit checks the operation/root IDs, active lease token, expiry, root fence and generation/cutover eligibility. Preserve lock order: exclusive query/recovery cleanup ownership, publication fence, operation/root authority. Release every database and application lock between units; no outer transaction spans the loop. Retain canonical vectors until drainage completes. Preserve the existing native-owner proof; SQL absence/expiry is insufficient.

Each claim authorises exactly one durable mutating unit, which atomically releases that lease. Add one nullable `LastCommittedLeaseId` on the operation and use the claim's unique lease token as the unit receipt. Commit it with phase, count, cursor and any target/receipt changes. Check it before ordinary lease rejection: a matching replay returns the already-committed outcome; superseded authority returns a benign no-write result, even if a newer unit has replaced the receipt. Phase/cursor alone is insufficient while several batches drain the same vector. No permanent per-batch history table is needed.

`ClaimNextAsync` must create its attempted token outside the SQL retry delegate and recover that same committed claim after ambiguous acknowledgement, never claim a second operation for that retry. Cover claim acknowledgement loss separately from unit acknowledgement loss. Target reads remain read-only; preserving a newly shared blob is a single receipt unit, not an incidental write followed by another mutation under the same claim.

Use the existing bounded coordinator quantum with a short cancellation-aware yield outside transactions. Ensure pending deletion ownership waits get a bounded deletion-only wake (target two seconds), rather than waiting for the unrelated 15-minute scan cadence. Do not turn this into a general scheduler rewrite or tight polling loop.

### Retry and observability

Add nullable `ResumePhase` and `LastProgressAtUtc` alongside the cursor/count and committed-lease receipt in one additive migration. Existing operations begin with null metadata and zero count. Retain `State=4`/`Phase=attention` compatibility while recording the actual resumable phase. A failed unit must persist the authoritative database phase, not blindly trust a stale work item's phase.

Update explicit `root_delete` retry to restore that phase and retain counts/targets; use the existing pre-migration inference only when resume metadata is absent. Keep the existing rowversion concurrency protection. Make `FailAsync`, `RecordCleanupResultAsync` and shared-artifact preservation receipts conditional on current operation/root/lease/expiry/phase authority at the write, including expiry between a read and its write. A stale worker cannot record attention over a newer owner. Cancellation propagates and remains restartable, without being labelled a SQL failure.

For native cleanup, acquire the existing query-generation or content-hash publication gate before rereading current operation authority and the exact pending target, then hold that gate through native IO. A failed final check prevents starting IO. This check is not a claim that takeover is impossible after it: an already-started old worker may finish the same immutable target idempotently, but cannot record a result under a superseded SQL lease. The next owner serialises through the same execution gate and verifies the target/absence before recording its receipt. Test that overlap explicitly, including a new shared-blob reference. Do not introduce a new global cleanup-lock protocol merely to promise instantaneous IO revocation.

Keep SQL-specific classification in infrastructure and carry a safe failure code/phase through the application boundary. Preserve timeout, deadlock, ownership refusal, invariant violation, file IO and unexpected-failure distinctions. Log the underlying exception with operation/lease/phase correlation; expose only safe categories and a diagnostic reference to the UI, not SQL text, credentials or private paths. Failure-recording failure must not erase the original diagnostic.

Project deletion state, resume phase, committed count and last-progress time into the existing source list/detail UI. Offer Retry cleanup when attention requires explicit retry, not as the normal running-state action. No estimated completion percentage or expensive repeated total count is required.

## Implementation and checks

### 1. Deliver restartable historical membership cleanup

Affected files: `SqlSourceDeletionStore.cs` (a focused partial/helper file is acceptable), `SourceDeletionOperationEntity.cs`, `CanonicalSchemaConfigurations.cs`, the new EF migration/snapshot, `SqlNativeOperationStore.cs`, `ISourceDeletionStore.cs`, `SourceDeletionCoordinator.cs` and the narrow deletion scheduling portion of `SourceReconciliationService.cs`.

- [x] Add focused failing SQL tests for multi-batch fan-out (including one vector with over 2,000 memberships), transactional progress, interruption/resume, separate claim/unit acknowledgement loss, stale worker/lease expiry, duplicate request and exact-phase retry, including a pre-migration attention operation with no targets.
- [x] Implement the phase split and scalar bounded delete. Prove the first end-to-end result on a small synthetic catalogue through REST preview/commit, the real coordinator and real Windows cleanup before scaling the seed.
- [x] Add/retain meaningful cases for concurrent source deletions and survivor publication, stale survivor candidates, final-source deletion, late membership detection, shared/cross-root dependencies, native ownership uncertainty and physical cleanup interruption. In the no-eligible-survivor case, retain another root's exclusively owned placed generation and unplaced draft. In the native takeover case, prove exact-target idempotence and rejection of the stale worker's SQL receipt.
- [x] Run focused integration groups: `SourceDeletion`, `NativeCorpusActionMatrix`, `EmbeddingCheckpoint`, `EmbeddingGpuRequest` and relevant `SqlToUsearchRebuild` cases. The combined group passes 232/232 with no skips against the restricted disposable SQL binding.

### 2. Expose truthful status and failure causes

Affected files: `SourceRootProjectionReader.cs`, `Application/Contracts/SourceRootViewContracts.cs`, `SourceLifecycleControls.razor`, `Sources.razor`, `SourceRootDetail.razor` and focused Web/domain tests.

- [x] Add failing tests for running/waiting/attention distinction, durable counts after restart, safe timeout classification, cancellation, and failed diagnostic persistence.
- [x] Implement the focused projection/UI changes and inspect rendered running, waiting and attention states. Keep original workspace-path assurances and existing command confirmation/idempotency behaviour.
- [x] Verify one injected failure followed by explicit retry through the real staging interface preserves the same operation and completes physical cleanup.

### 3. Validate representative scale and review the complete change

- [x] Use a dedicated test host/bootstrap based on `ConfigureIsolatedTestLayout`/existing test composition, bound to a unique loopback port, with its own generated database, config and storage. Exercise the real REST endpoints, reconciliation service, SQL store, survivor publisher and Windows file store. Do not simply launch the ordinary production executable with environment overrides.
- [x] Explicitly select a verified staging SQL connection. The existing helper defaults to `localhost`; loopback alone does not prove isolation. The user's subsequent direct answer in the coordinated Release 1 chat approves a separate database and restricted staging login on the existing SQL Server. Use that approved arrangement with this task's own catalogue/login/files and app runtime/port; never inherit integrated/admin credentials or access the production catalogue. Release 1 owns shared provisioning/fixture support. Separate scale work from Release 1 latency acceptance.
- [x] Capture synthetic baseline checksums, execute the scale/read-load test, then verify terminal SQL receipt, actual app-owned file removal, intact originals and unchanged unrelated data. Capture actual plan, elapsed time, CPU/logical reads, batch counts/durations and concurrent read samples. No production replay is involved. Final acceptance: 4,851,155 removals in 575.11 seconds; 533/534 usable Overview reads, one explicit unavailable sample, zero failures; all 1,602 other reads usable and all physical/canonical/survivor checks passed. Earlier failed evidence remains retained.
- [x] Run interruption/restart at post-cutover, mid-membership, post-final-batch, graph/file boundary and interrupted-file-cleanup points. Use deterministic failpoints only in the test harness and at least one actual staging-process restart.
- [x] Obtain independent Astra implementation review of the final diff, invariant tests and scale evidence. The final branch against `ff9f14c4` was approved with no blocking code correction; the review also confirmed all six runtime/harness hashes against the passing scale run.
- [ ] Run the relevant combined SQL, Web and native file tests, then zero-warning Release build and required repository checks through canonical closeout.

Checkpoint: after the first two coherent implementation batches, require the small real-interface result above. If it is missing, identify the concrete blocker and reduce the delivery path. Allow one measured query/batch adjustment after the initial scale run; do not start a general retention or SQL tuning project.

## Closeout and production proposal

After implementation and review, update `docs/architecture.md` and the source-lifecycle row in `docs/roadmap.md` with measured evidence and remaining gates. Use `scripts/dev/complete-feature.ps1 -FeatureWorktree <this worktree> -MainRoot <main> -KeepWorktree`, without `-GoLive` or any clean-slate acknowledgement. The script commits, pulls main, squash-merges, verifies and pushes; it is not merely a test runner. Coordinate exclusive use of main with parallel work before running it; a dirty/occupied main is a pending integration gate, not permission to disturb another thread. Inspect all invoked tests/environment first for isolation. Report `failed_step` and the failed step's `log_path` on failure; do not substitute a manual sequence.

Two failed canonical attempts are retained. The first exposed an unobserved read-task outcome in a concurrency test; its independently reviewed test-only correction preserves and observes that outcome. The second exposed two real two-second Overview budget expirations and an existing retrieval `resource-pressure` refusal during overlapping test projects. The approved narrow workflow change runs both solution-wide test commands with `-m:1`, preserving coverage, deadlines and deliberate concurrency inside each project. The native dry-run contract and actual sequential project order are verified. The bounded run passed all domain/relevant SQL/Web cases but failed on an existing Outlook test's process-wide console race; a non-parallel collection now protects that redirected console, preserving the privacy assertion, and all 85 Outlook tests pass. Fresh canonical success remains required. No failed run is reclassified as passing.

The third attempt passed 3,401 tests with one failure and 26 skips before Main integration. Its sole failure was the unrelated-write test's forced closing-fence pause. A bounded trace measured 235 ms opening its writer connection versus 3.9 ms executing the UPDATE; the earlier failed wait remains unclassified. The independently assessed fixture correction prepares only the separate writer connection and typed command before the cold Overview read, then executes one autocommit update between the actual fences. No query warm-up, deadline or assertion relaxation is introduced. Fresh Web verification passes 441 tests with 21 browser opt-in skips and zero build warnings/errors; canonical closeout remains pending.

Prepare, but do not execute, a production packet containing the reviewed commit/payload, additive migration SQL, staged measurements, source/operation identity supplied by the user, expected phase/counter changes, abort criteria and rollback prerequisites. The incremental updater's `-PlanOnly` path reads live configuration/SQL, so even that production preflight needs specific approval. It is not authorised by this design task.

Known rollout gap: the updater's current `-ApplyMigrations` contract is pinned to `20260826160702_AddEmptyCatalogueReadiness` -> `20260918121829_AddSourceDeletionOperations` and its exact SQL hash. It cannot be reused for the new progress migration. Prepare a minimal, separately reviewed opt-in migration contract in the same incremental updater, with exact baseline/target/hash and disposable contract tests, if needed to make the rollout packet executable. Do not repurpose the old switch, run a direct production migration or use another deployment path. Production readiness remains pending until this contract and the new-phase rollback fence are verified.

Proposed separately approved sequence:

1. Read-only preflight for the named production target and incremental updater `-PlanOnly`; compare current identity, ownership, storage and schema with the prepared packet.
2. Independent operational review, then specific approval to apply the exact incremental release and additive migration. Use `update-native-iis-incremental.ps1`; no full GoLive path. If its migration options cannot safely apply this addition, report the gap and request direction instead of bypassing it.
3. Specific approval to resume only the named source deletion through the existing preview/commit retry, with bounded observation and explicit stop criteria. This does not authorise general retention or any dead-letter processing.
4. Verify terminal SQL and physical cleanup independently, intact originals, survivor reads and native ownership evidence. Keep the historical failure evidence.

Rollback: preserve the predecessor payload/configuration and updater receipt. Keep the additive columns and durable targets/cursor; do not down-migrate away recovery evidence. Before returning to an older binary, stop/hold deletion workers and establish a reviewed fence that prevents that binary claiming new intermediate phases. If that cannot be proved, remain held and roll forward with a correction. A code rollback does not restore deleted memberships or app-owned files. Any data restoration needs a separately approved consistent database/storage recovery procedure; never blindly re-enable the source or replay the monolithic purge.

## Review focus and reference basis

Independent review must resolve: cutover recovery while tombstoned canonical vectors remain; durable target capture before associations disappear; a commit acknowledged ambiguously; late writes behind the cursor; native query/file ownership across process and lease changes; and safe old-binary rollback.

Microsoft documents both the query-planning sensitivity of parameterised collections and using smaller delete transactions to reduce lock footprint. These support the design choices, not a claim that blocking caused this incident: [EF collection translation](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew), [bounded deletes and lock escalation](https://learn.microsoft.com/en-us/troubleshoot/sql/performance/resolve-blocking-problems-caused-lock-escalation).

Independent Astra review of the initial design required the concrete claim/unit replay receipt, narrower no-survivor target capture, and precise native IO ownership semantics above. After those corrections, the reviewer approved the amended approach with no remaining blocking design issues. At that gate, approval covered design only; implementation, scale evidence, final independent review and migration/rollback verification were outstanding. The reviewer also confirmed the existing null-pointer/deleting-root recovery path and operation rowversion guard, which the implementation exercises rather than redesigns.

At design-phase closeout, no implementation, migration, runtime tests, staging deployment or production action had been performed. Subsequent progress is recorded in the task worktree and verification evidence; production authority remains absent.
