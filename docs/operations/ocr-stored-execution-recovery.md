# Recover a stored OCR result on the current schema

Version 3 of the canonical OCR maintenance plan handles a modern execution whose
result is stored but whose native cleanup cannot be proved by normal recovery.
An uncertain reservation blocks ordinary deployment. Its age, an exited PID or a
scheduler receipt alone does not establish native cleanup.

This mode retains the exact migration history of the matching updater and CLI.
It preserves the original execution, supervisor and
native process identities, claim/deadline, result, parent job and delivery. Fresh
physical verification records cleanup for that execution; the existing settlement
operation then releases exactly its capacity reservation. The CLI does not requeue
the parent. Normal recovery consumes the saved result after startup if the source
remains eligible. No model execution, acquisition or source backfill is involved.

## Prepare and review

Use `scripts/deploy/update-native-iis-incremental.ps1` with `-PlanOnly`,
`-ApplyOcrLifecycleRecovery` and `-OcrStoredExecution`. Supply the exact batch through
`-OcrRecoveryBatchId`, a new private `-OcrRecoveryOperationRoot`, and the reviewed
desktop classifications through `-OcrDesktopClassificationsPath`.

The plan binds the original execution lease, native identity, single-batch owner,
request/task/job versions, current source/root configuration and stored-result
digest. It also pins database identity/history, configuration, task/launcher,
candidate payloads, preserved helper, physical verifier and implementation.
Preparation must pass before the plan is approved for its production target.

If an earlier deployment owns the existing hold, supply a separately reviewed
`-OcrPredecessorBindingPath` and `-ExpectedOcrPredecessorBindingSha256`. This private
version-1 receipt uses purpose `ocr-stored-execution-forward-hold` and contains:

- The old release ID, hold path/hash and failed manifest path/hash.
- The old updater's exact process ID and native start time.
- The installed Web, Outlook and helper roots/hashes, configuration paths/hashes
  and database identity.

The original updater must have exited and its recovery writer must already have
been safely resolved. Parent exit alone does not authorise writer deletion.
Under the updater mutex and admission fence, a zero-writer guard precedes the
new release's own writer. A durable transfer intent and same-directory atomic
replacement transfer the old hold to the new plan without an admission gap.
The old recovery packet remains intact.

## Apply and resume

Apply through the same updater using `-Apply -ApplyOcrLifecycleRecovery`,
`-OcrRecoveryPlanPath` and `-ExpectedOcrRecoveryPlanSha256`. The plan controls its
mode; preparation-only switches are refused during Apply. Production actions
still require explicit authority for that target and independent technical review.

The existing stopped-maintenance verifier must prove exact owner exit and native
cleanup. Proof and settlement are separate, idempotent steps. Their receipts bind
the full original maintenance request and physical evidence. A stale binding,
changed result, foreign proof or conflicting cleanup receipt is refused.

After an interrupted or uncertain response, inspect the saved deployment state.
Use `-ResumeOcrLifecycleRecovery` with the same plan and hash for an authorised
continuation. A committed proof reads back after settlement changes row versions;
it does not replace the original identities or execute OCR again. A lost hold
release response is recoverable only from this plan's durable release intent and
matching candidate/helper bindings. Containment restores the new hold before
stopping owners. Early absence and foreign hold ownership remain refusals.

Ordinary updates now check definite uncertain or missing capacity before creating
their recovery writer or publishing candidates. Reserved work keeps its normal
drain opportunity. An unproved initial stop retains the observed Web worker,
payloads and hold for explicit recovery instead of repeating the drain window.
Scheduler admission defers without holding the publication fence while waiting
for maintenance, allowing existing cleanup to finish.

Version-1 recovery and version-2 cleanup-only maintenance retain their
existing contracts. Model payloads remain exclusively under `J:\Models`.
