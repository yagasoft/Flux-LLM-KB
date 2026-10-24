# GPU OCR lifecycle recovery

Status: architecture approved with the corrections below, 24 October 2026; implementation and release verification in progress. The design and core correction received independent Astra review. This document does not assert that deployment or incident recovery has occurred.

The independent architecture gate requires these binding corrections: persist supervisor instance/PID/start time as well as machine and child identity; atomically claim and acknowledge PendingDelivery work; make Claimed-to-permitted start mutually exclusive with verified-unstarted cleanup; treat a missing/inaccessible named job unconditionally as CleanupUnproven requiring stopped maintenance; guard both ordinary OCR callbacks and capacity reconciliation with exact durable OCR cleanup proof; fence new result writes to the same execution lease; and persist RecoveryStartedAtUtc separately from mutable reason-update time. A bare generic capacity-evidence row grants no OCR release authority. Replayed accepted operations must not touch a subsequently reassigned slot. New job creation rejects an existing name; recovery opens and never recreates it. Ownership checks and proof-kind validation belong in SQL as well as the trusted local observer.

## Intended result and limits

Healthy OCR taking longer than ten minutes continues to completion. A hung or interrupted OCR attempt reaches verified cleanup and durable settlement, or an explicit actionable blocked state within a bounded period. Once cleanup is proved, another queued GPU task can start. A saved successful OCR result continues once through the original Extract job; a saved failed result follows the existing document-failure path. The existing uncertain reservation must be recovered as part of delivery, rather than left behind by a prevention-only change.

Keep the SQL-authoritative scheduler, one physical capacity slot, existing OCR model/runtime/settings identity, retained-source checks, normal publication pipeline and offline model policy. Do not introduce automatic repeated inference: for this first repair, a terminated attempt without a valid stored result becomes a bounded, sanitised OCR failure after cleanup. This meets the retry-or-failure requirement without adding retry ownership or replay policy. Do not reset uncertain slots on age, infer cleanup from an OCR result or a process-name search, add capacity to bypass ownership, or modify historical unrelated uncertain tasks.

No model downloads, cache moves, private evidence in Git, manual regeneration, scheduler rewrite or broad production cleanup are in scope. Model-free tests use synthetic results and disposable processes. Real-model staging/live checks use only verified existing local artefacts under J:\Models and existing offline gates.

## Confirmed mechanism

`GpuSchedulerOptions.Default` diagnoses reservations without liveness for ten minutes. `PaddleOcrVlmExecutorAdapter` acknowledges, then waits for execution without updating scheduler liveness. `PaddleOcrVlmProcessRunner` allows twenty minutes. In the investigated incident, a valid result was stored after approximately fourteen minutes, after the watchdog had changed capacity to uncertain. The normal completion methods correctly rejected the now-ineligible reservation, but `PaddleOcrVlmCompletionCoordinator` ignored all three dispositions and retried each minute with new operation IDs.

The process timeout also does not bound the full execution: `TryTerminate` does not await termination, and `DrainAsync` can wait indefinitely for inherited output pipes. Cancellation currently asserts release without a durable cleanup proof. Fixing the heartbeat alone would leave these failure modes.

`ReconcileCapacityAsync` only releases capacity; `ReconcileTaskOutcomeAsync` only marks an outcome uncertain. Neither makes the task Completed and dispatch Terminal, which `RequeueCompletedAsync` requires. Known-result reconciliation therefore needs a narrow new settlement operation.

## Decisions and minimal persistence

Use a Windows job object around the one OCR child and every descendant, plus a startup gate before third-party imports/model execution. Reuse the native API patterns from `OwnedVisioLifetime.cs`; do not change Visio semantics or route Python through the unrelated native-worker pipe protocol. The latter has closed lifecycle enums, connection attestations and no machine identity; repurposing it would enlarge this change.

Extend the existing `DocumentOcrRequests` row with the following additive fields. It already owns the one source-bound mini-task and has a rowversion. No new retry queue, lease service or table is needed:

| Field | Type and purpose |
| --- | --- |
| ExecutionInstanceId | nullable GUID; immutable one-attempt claim |
| ExecutionMachineFingerprint | nullable canonical SHA-256; reuse the existing interactive-owner machine-fingerprint provider |
| SupervisorInstanceId / SupervisorProcessId / SupervisorProcessStartedAtUtc | nullable GUID/PID/UTC; stable per application process, separate from the child incarnation |
| ExecutionPhase | integer default 0; Unclaimed, Claimed, NativeStartPermitted, CleanupConfirmed, Settled, Blocked; closed enum/check constraint |
| ExecutionClaimedAtUtc | nullable datetimeoffset(7); immutable claim time |
| ExecutionDeadlineAtUtc | nullable datetimeoffset(7); immutable claim time plus twenty minutes |
| NativeProcessId / NativeProcessStartedAtUtc | nullable pair; exact child incarnation, positive PID |
| NativeCleanupConfirmedAtUtc | nullable datetimeoffset(7); only written following verified proof |
| RecoveryReason | nullable bounded opaque reason code, no exception/private text |
| RecoveryUpdatedAtUtc | nullable datetimeoffset(7); last phase/reason change, not every poll |
| RecoveryStartedAtUtc | nullable datetimeoffset(7); immutable recovery-budget origin |
| PhysicalCleanupEvidenceSha256 | nullable canonical SHA-256; external stopped-maintenance receipt for earlier recovery only |

Use the existing scheduler operation receipts and executor evidence for idempotent settlement and capacity-release evidence. Derive the private job-object name from the execution-instance GUID and fixed application prefix; do not persist process paths, command lines, OCR text or secrets. Do not use a PID alone. A null new owner on an old request means earlier/unproven ownership, never unstarted execution.

This migration is necessary because the current OCR row cannot distinguish unstarted work, a particular child incarnation, an execution permitted to start, or independently confirmed cleanup across restart. Reusing a free-text result field or encoding ownership inside evidence-class names would obscure those invariants. Generate one additive EF migration and update the model snapshot and schema baseline tests. Preserve existing result JSON/digests and initialise existing rows as Unclaimed with no implied cleanup. Normal launch claims must refuse a earlier acknowledged/uncertain dispatch lacking new ownership; only a genuinely PendingDelivery new dispatch can obtain a new claim.

## Interfaces and files

Keep orchestration in `PaddleOcrVlmExecutorAdapter`, durable OCR state in `SqlDocumentOcrStore`, and process containment in the Integrations document implementation. Add application-level records to `Application/Documents/DocumentOcrExecutionLifecycle.cs`:

- `DocumentOcrExecutionLease`: exact `GpuExecutorBatchHandle`, mini-task ID, execution-instance ID, machine fingerprint, exact supervisor instance/PID/start and immutable deadline.
- `DocumentOcrProcessIdentity`: machine fingerprint, PID and process start UTC.
- `DocumentOcrCleanupProof`: execution-instance identity, exact child identity when launched, proof kind (VerifiedUnstarted / OwnedJobEmpty / StoppedMaintenance), observation UTC and optional external receipt digest. Only the trusted local supervisor/maintenance verifier constructs accepted proofs.
- `DocumentOcrLifecycleResult`: explicit disposition (Applied, AlreadyApplied, StaleOwner, SourceWithdrawn, CleanupUnproven, LegacyOwnerUnproven, InvalidResult, Blocked, ResultAvailable) and bounded reason code. Do not equate a stored rejected operation receipt with a transient failure.
- `IDocumentOcrExecutionObserver`: `PermitNativeStartAsync(lease, identity, cancellationToken)` and `ConfirmCleanupAsync(lease, proof, cancellationToken)`; implemented by the scoped SQL-facing adapter. Return dispositions, not fire-and-forget notifications.

Extend `IDocumentOcrExecutor.ExecuteAsync` with an explicit execution context containing the lease and observer, and carry it into `IPaddleOcrVlmProcessRunner.RunAsync`. The scope prevents production executor use without an ownership context. Update existing synthetic implementations mechanically. Avoid ambient process-global or AsyncLocal ownership.

Add methods on `SqlDocumentOcrStore` (a partial lifecycle file is appropriate):

1. `TryClaimExecutionAsync(handle, miniTaskId, executionInstanceId, machine, ct)` -> lease/typed refusal. The claim is unique; a replay can read it but does not grant permission to start a second process.
2. `PermitNativeStartAsync(lease, identity, ct)` -> typed result; persists the child identity and NativeStartPermitted before the gate is opened.
3. `HeartbeatAsync(lease, ct)` -> typed result; updates the exact Reserved slot and active batch only, without changing task/batch phase.
4. `ConfirmCleanupAsync(lease, proof, ct)` -> typed result; immutable proof plus capacity evidence, independently of source-enabled status.
5. `SettleCompletionAsync(handle, miniTaskId, expectedDigest, ct)` -> typed result; handles both normal and uncertain OCR states with validated stored output and cleanup.
6. `ReadRecoveryCandidatesAsync(ct)` and `RecordBlockedAsync(handle, reason, ct)`; include requests without a stored result, not only current pending completions.

Use the existing scheduler serialisable transaction and global mutation/admission lock ordering for heartbeat, claim/start permission, cleanup and settlement. Reuse/expose the narrowly needed lock helpers from `SqlGpuSchedulerStore`; do not introduce a second conflicting lock hierarchy. Operations checking source state must use the existing source/deletion fences in the same order as current handoff/publication paths. SQL commands and lock waits are bounded.

Update `PaddleOcrVlmCompletionCoordinator`, `PaddleOcrVlmExecutionRegistry` cancellation handling and `PaddleOcrVlmCompletionRecoveryService` to consume these typed outcomes. Register the observer and process supervisor in `WebHostComposition.cs`. The `_deliveries` dictionary should use `Lazy<Task>` as the existing embedding executor does: `ConcurrentDictionary.GetOrAdd` may invoke a task-producing value factory more than once. SQL ownership remains authoritative across processes.

## Process start, liveness and cleanup

1. Claim durably before opening retained content. Read back an ambiguous claim commit. Only its original local claimant may launch; a duplicate delivery recovers existing state. A crash after claim cannot lead to blind relaunch.
2. Create a private named Windows job with kill-on-close and no breakaway. Start the fixed Python executable/script with stdin redirected and a new required startup-gate switch. The script waits for a fixed go message before importing Paddle/transformers, loading models or executing OCR; EOF, invalid input or a bounded startup wait fails closed. Existing offline environment configuration remains unchanged.
3. Assign the waiting child to the job. Assignment failure kills/awaits that child; it never receives go. Job handles are non-inheritable and have an application-restricted ACL. Persist its exact PID/start/machine with the lease. Only after accepted, read-back-confirmed NativeStartPermitted may the parent send go. A crash after permission but before go is treated as possibly started and cleaned up, never as safe-to-replay.
4. A 30-second supervisor heartbeat refreshes slot and batch while it owns this attempt and before the immutable deadline. A heartbeat neither declares progress nor proves cleanup. It must not use SafeBoundary callbacks, refresh an uncertain slot, extend the deadline or continue after supervisor completion. Rejected ownership stops local work and triggers exact cleanup. A transient SQL failure does not release capacity; subsequent uncertainty is resolved through settlement after cleanup.
5. The twenty-minute deadline begins at the durable claim and includes retained reading, verification/rasterisation, process execution, result handling and bounded cancellation. Pass the remaining budget through each async stage. If an in-process preparation stage ignores cancellation, do not launch subsequently: start permission checks the expired deadline. Report blocked preparation rather than pretending it completed.
6. On completion, timeout or cancellation, stop any remaining owned descendants, await the root exit and query the exact job until its active-process count is zero. Use a 30-second cleanup budget. Drain/dispose redirected streams within five seconds, within that same budget. Never wait indefinitely for pipe EOF. A successful root exit with surviving children still requires cleanup. Dispose the job only after recording/retaining the relevant proof; kill-on-close is a fallback, not a proof by itself.
7. An OwnedJobEmpty proof is valid only for this contained attempt after verified assignment and no-breakaway enforcement. On restart, open/inspect the exact named job and child incarnation on the recorded machine; never kill by executable name. A missing job is not independently enough: require exact child exit plus the established containment contract, and fail closed for inaccessible/foreign/ambiguous ownership. If the implementation cannot establish that the vanished job proves all descendants gone, require the stopped-maintenance proof instead. Test this Windows behaviour explicitly; do not claim automatic crash recovery on an untested inference.

The application recovery loop runs every 30 seconds. Before the deadline, a live exact child is allowed to finish; after it, recovery may terminate that exact owned job. Recovery budget is two minutes from deadline/first post-restart recovery observation, including 30 seconds cleanup and bounded durable-write retries. Budget exhaustion sets a durable Blocked reason and stops repeated rejected lifecycle writes. A periodic read may detect newly available proof, but it must not silently reset the recovery budget. Derive the start of recovery from the immutable deadline or the first persisted RecoveryUpdatedAtUtc for its recovery phase; preserve that start separately in code if subsequent reason updates would otherwise move it (add a nullable RecoveryStartedAtUtc column in that case).

## Atomic settlement and commit loss

Use one OCR-specific settlement transaction, for normal and late completion, rather than a best-effort acknowledgement/receipt/callback sequence. It must validate exact dispatch, batch, slot, executor, generation, owner, one mini-task, OCR runtime/settings and execution claim/proof. The slot must still belong to this batch and be Reserved or Uncertain. An already-settled replay is accepted from its operation receipt even if the slot now belongs to a newer batch; it never touches that newer slot.

Recompute SHA-256 over the persisted result JSON, compare with the stored/expected digest, and run the existing bounded shape validation. A valid `Succeeded=false` result is a known completed execution; it is not an uncertain GPU outcome. Record its result receipt and mark the GPU task Completed, batch Completed, dispatch Terminal, request execution Settled, and capacity Available in one transaction. Record the capacity-release/reconciliation wake in the same transaction. A bad/mismatched digest leaves the stored result untouched and blocks publication; independently proved cleanup can still release capacity via the existing trusted capacity reconciliation and terminalise the affected document as a corrupt-result failure through a separately tested path.

After settlement, call the existing `RequeueCompletedAsync`; that operation already fences parent identity, current source/revision, pending outbox and idempotent requeue. Keep a settled-but-not-requeued request in recovery selection so a crash between settlement and continuation resumes without another native attempt. Paused sources can remain deferred; deleted/superseded sources do not publish or prevent capacity cleanup. Do not requeue into a deleted source merely to report an error.

When no valid result exists after confirmed termination, store one sanitised failure result (for example document-ocr-runtime-timeout or document-ocr-owner-interrupted), then settle as a known failed execution and use the same normal continuation/failure path. If source withdrawal prevents StoreResultAsync, release capacity with proof and finish/cancel the source-bound task using existing deletion/cancellation semantics; do not weaken SourceIsEnabledAsync or recreate withdrawn work. Never overwrite an existing successful result with a recovery failure. If an original late writer races recovery, serialise the result decision with the request row and execution phase: first valid committed result wins, conflicting writes are refused and reread.

Operation IDs are deterministic from dispatch, generation, execution-instance, immutable phase purpose and (where needed) result/proof digest. Timestamps inside replay fingerprints are captured once and reused from durable state. A new heartbeat is a new observation; retries of that observation retain its ID, rather than reusing one operation forever. Prefer a fenced monotonic heartbeat update without an operation receipt per tick, since duplicate writes of the same observed timestamp are harmless. Stable settlement IDs must not reuse a previous rejected normal callback ID. Authoritative refusal enters a different reconciliation/blocked state, not a fresh-ID version of the same rejected request. After commit-response loss, read the matching receipt/state before deciding whether work remains.

## Visibility

Extend `GpuSchedulerStatusSnapshot` with one optional OCR owner summary for the application's single physical slot: batch/execution identity, executor, state, actual age, absolute deadline, sanitised reason and next action. Read this in the existing projection path and expose it through `/api/gpu-status`. Show the global owner in `Components/Pages/Overview.razor` independently of source filtering, distinguishing active OCR, automatic cleanup and operator evidence required. Link the existing operator surface. Do not expose machine fingerprint, process command line, retained path or OCR text in the dashboard. No unsafe force-clear button is introduced; supported operator recovery is the bounded maintenance command below.

## Existing incident and incremental deployment

Keep private incident identities and receipts in the external operational-evidence directory. The existing row has a saved successful result but no execution identity. The new runtime must display LegacyOwnerUnproven and must not manufacture an unstarted/native-cleanup receipt.

The ordinary incremental updater calls `Invoke-WithHybridGpuDrain`, which waits for all capacity to be Available before stopping IIS. It therefore cannot pass this incident's uncertain slot. Existing `-RecoverStoppedPool` only verifies an already-stopped IIS pool; it does not prove earlier child/driver cleanup. Existing migration modes bind named migration contracts and hashes; there is no generic safe application of this new migration. These are material gaps, not reasons to bypass the updater.

Implement a narrow OCR lifecycle mode/module within `update-native-iis-incremental.ps1`, modelled on its existing pinned migration and maintenance-receipt modes. Its PlanOnly contract must name exact database identity, current/target migration history, generated SQL hash, old/new payload fingerprints, site/pool and companion target, exact affected reservation/generation, expected saved result digest and requested maintenance action. Reject combination with unrelated migration/rebuild modes. Expose two explicit stages: prepare stopped maintenance and apply/reconcile the exact saved receipt; neither stage clears capacity by SQL assignment.

Preparation acquires/retains the deployment admission hold, disables the identified application's launch task, records exact IIS/companion owner incarnations and bindings, stops only those application owners, verifies their exit and checks native GPU driver ownership. This earlier case requires the repository's independently reviewed stopped-owner/driver proof, including descendant ambiguity: an unavailable or incomplete driver inventory, unknown potentially relevant process or changed binding fails closed. No broad process-name killing, device reset, model cache cleanup or service-wide restart. Save the exact observation, tool/version/output hashes and authorisation identity externally. A receipt hash alone is not authority: apply rereads live stopped-owner/driver facts under the held maintenance boundary and validates every binding before accepting it.

Apply the pinned additive migration while the application remains held/stopped. Invoke a narrow maintenance entry point in the candidate application code that validates the externally bound proof and exact current reservation/result identity, records a StoppedMaintenance cleanup proof and calls the same tested OCR settlement operation. This is the only earlier exception; it cannot be called by routine recovery, cannot target arbitrary executors and cannot run if a new owner/generation appeared. No manual table edits. Verify affected-row/receipt identities, known successful task settlement, preserved digest and safe continuation eligibility. Then use the updater's existing payload-swap/start/validation-hold conventions and release the hold only after candidate health and ownership checks.

AGENTS.md requires explicit operational authority and independent review for production deployment and recovery. Prepare the exact extended mode/PlanOnly packet before the operational gate. Explain any advanced updater gap and obtain direction if existing authority does not cover the specific stopped-maintenance action. Continue authorised implementation and staging work independently. Use the incremental updater for this release.

Rollback preserves additive schema, saved results, ownership/cleanup/operation receipts and original payloads. Before activation, restore the original matched payload pair while held/stopped. After new ownership state has been written, do not run the old OCR implementation against unsettled new attempts; retain the hold and use reviewed forward recovery. No Down migration after state exists and no reversal of a committed capacity settlement.

## Implementation sequence and checks

One owner implements the coherent change. Write failing invariant tests first, then implement the corresponding batch; mechanical fixture signature updates need no artificial red/green cycle. Use the existing disposable SQL fixture and supported runner. Baseline supplied by the owner: 133 selected OCR/shared-capacity checks passed; this is contextual evidence, not validation of the changes.

### Batch 1: durable lifecycle and next-task vertical result

- Add the minimal columns/migration, typed lifecycle records, claim/heartbeat/proof/settlement store methods and stable identities.
- Replace completion's ignored dispositions with the typed settlement path and recover settled-but-not-requeued requests.
- Add focused tests to `SqlDocumentOcrHandoffTests` partial files and scheduler lifecycle tests: a synthetic fourteen-minute execution, uncertainty before late success, known failure, duplicate/reordered settlement, lost commit response, source pause/delete/revision supersession, old generation/ABA rejection, and new queued task admission after proved cleanup.
- Test source withdrawal still releases independently proved capacity; result corruption cannot publish. No proof means neither normal nor reconciliation release succeeds.
- The first observable capability is synthetic durable input -> exact claim -> result and proof -> settlement -> parent continuation -> next GPU task. It must run through real store/admission interfaces, not only mocks.

### Batch 2: owned process and bounded recovery

- Add the gated job supervisor, pass the explicit execution context through OCR, enforce twenty-minute deadline, 30-second heartbeat and cleanup bound, and five-second stream drainage.
- Extend `PaddleOcrVlmLocalExecutorTests`, add a synthetic child-process helper/test collection, and test gate failure, attach failure, timeout, child/descendant hangs, inherited stdout, cancellation, root success with surviving child, app-host crash, PID reuse, inaccessible/foreign owner and exact job recovery.
- Add concurrent delivery/recovery and all crash windows: claim commit, process creation before bind, bind commit before go, result commit, cleanup commit, settlement commit and requeue commit. Assert at most one native start and one document continuation. Preserve existing cache-hit/offline/miss-fails-closed tests; do not download real models.
- Add a fake-clock 10+minute healthy path and a short real-time synthetic process test; a production-sized twenty-minute wait is unnecessary for every test.

**Effort checkpoint after these two batches:** require the model-free end-to-end path and next-task progress to execute in disposable SQL, plus demonstrated bounded termination or explicit blocked escalation. If it does not, identify the actual failed invariant and revise the smallest path; do not proceed into extensive UI/deployment scaffolding while the core is unproven.

### Batch 3: visibility, migration/recovery rehearsal and release

- Add the owner/status projection, dashboard message and focused rendering/API checks. Update architecture and affected roadmap Progress %/Remaining Work; leave user-manual assets untouched.
- Add pinned updater migration/earlier-recovery mode and model-free script tests for wrong DB, history/hash drift, stale receipt, live/unverifiable process, driver-check failure, changed generation, lost mutation response, duplicate apply and failure before/after start intent. Verify failure retains hold/stopped posture and no unrelated targets change.
- Rehearse the incident shape in a separate staging database/runtime/endpoints with an intentionally earlier uncertain slot, stored result and next queued task. Use synthetic external proof fixtures only in tests; production mode must perform actual proof validation. Verify migration leaves historical payload/digests unchanged and replay is idempotent.
- Independent review covers the complete ownership/cleanup/concurrency design and final diff. Resolve blocking findings; run focused suites, required combined checks and zero-new-warning build. Then review the concrete production PlanOnly target and recovery proof immediately before the authorised action.
- Deploy through the incremental updater only; validate installed commit, exact recovered reservation/result continuation, available/shared-slot turnover, a subsequent queued real task and absence of new false watchdog transitions. Observe the global queue making progress rather than using an aggregate uncertainty decrease as success. Use only existing offline models.
- Run `scripts/dev/complete-feature.ps1 -KeepWorktree` for canonical qualification and the authorised squash merge before preparing the production release. Do not manually substitute its merge sequence or enable its clean-slate GoLive path. On failure, report failed_step and log_path, fix in scope and rerun. Preserve the worktree and operational evidence through the reviewed incremental deployment and required live checks; purge the task worktree only after those succeed.

## Acceptance evidence

Record pass/fail/not-run for healthy long OCR; bounded timeout/cancellation/pipe cleanup; restart and start-gate races; exact ownership/ABA; commit-loss/idempotency; source/privacy fences; no release without proof; earlier result-preserving settlement; and next-task execution. Staging success does not itself prove production recovery. A blocked/unverifiable cleanup case is an acceptable fail-closed behaviour test, but the current production incident is not resolved until its authorised exact recovery and subsequent queue progress are observed.
