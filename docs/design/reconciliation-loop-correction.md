# Reconciliation loop correction

## Incident and demonstrated cause

On 2 November 2026, two production sources were paused through the supported
`root_pause` preview/commit operation. The active filesystem scan lost root
authority and returned to pending through ordinary completion. Subsequent
snapshots showed no running scans or new claims. Pending work, recovery state,
retained sources and published data were not cleared. A deployment-validation
hold alone does not stop post-startup reconciliation claims.

The journal query succeeded, but reading the saved, in-range cursor returned
Win32 error 87 (`ERROR_INVALID_PARAMETER`). Reading the same journal at its
current head succeeded. An isolated NTFS fixture reproduced the error by using
an aligned position inside a journal record. This establishes rejection of the
saved position; it does not establish why the original position became unreadable
or prove journal corruption. The helper process, pipe availability, configured
indexer SID and protected storage checks did not explain the persistent failure.

The reader classified the rejection as ordinary unavailability. The helper then
returned generic `unavailable`, so intake retried the same cursor indefinitely.
The saved baseline recovery obligation remained required in fallback mode.

Later degraded watcher hints advance a coalesced root obligation's generation.
Completion correctly cannot acknowledge a newer generation. However, automatic
pending-work dispatch did not honour the reconciliation cadence after a completed
fallback sweep, so it could immediately queue another whole-root request. SQL
regressions reproduced this with both recovery-required and ordinary root-wide
debt, including deferred content. The exact origin of the production watcher
hints was not captured; scan/provider feedback remains a hypothesis.

## Behaviour and invariants

- Only a journal-read error 87 at a validated historical cursor receives a
  control probe. It makes at most one read on the same held volume handle with
  the same journal ID, buffer bound and fields, changing only `StartUsn` to the
  previously queried head. The control must parse successfully. Failed or
  malformed controls retain unavailability; cancellation propagates before and
  after the probe. General error-87 handling remains unchanged.
- The closed pipe value `cursor-rejected` preserves the diagnosis and native
  error. Existing generic gaps and availability failures retain their contracts.
  Old clients treat the new value as unavailable, so a paired helper/application
  release is required for the correction to take effect.
- Recovery uses the existing owned claim, registration-generation fence and
  ordered root locks. A reset atomically records the old/replacement cursor,
  bounded reason and affected root IDs with the recovery debt. Stale claims
  cannot advance the cursor or emit a success receipt.
- A shared-volume reset refuses to proceed while any registered participant is
  not enabled. It retains the shared cursor and every root's exact coverage,
  pending generations and retry metadata, settling only its own journal claim
  with `journal-recovery-paused`. A foreground proof barrier yields promptly
  to conservative fallback. One paused participant therefore defers volume-wide
  recovery; it is never enabled automatically.
- Creation of new automatic fallback whole-root work respects
  `LastReconciliationAtUtc + ReconciliationCadence`. Existing owned requests
  remain recoverable. Pacing applies only while current journal availability is
  degraded; a successful same-epoch page admits due work immediately. Healthy
  scoped work is unaffected. No pending obligation is consumed to achieve quiet.

Source contents, public APIs, schema, native cleanup rules, model configuration,
search and the user manual are outside this correction.

## Verification and release boundary

Verification covers real NTFS rejection and the actual pipe, failed/malformed
control responses, cancellation, mixed and fully paused participants, stale
claims/registration, atomic recovery evidence, newer watcher generations,
deferred debt, cadence expiry and continuous-history restoration.

The separate model-free staging executable uses its own loopback endpoint,
restricted database, runtime, storage and synthetic sources. Its exercise injects
an invalid cursor only into that owned database, proves paused metadata remains
unchanged, explicitly resumes only the synthetic sources, observes recovery and
a quiet interval, then pauses those sources again. This qualifies reconciliation;
it is not an OCR, semantic-search or production-publication acceptance claim.

Use `scripts/dev/complete-feature.ps1` for canonical source closeout. The concrete
release packet must include the incremental updater's reviewed `-PlanOnly`
output, exact application/helper payloads, unchanged journal option hashes,
database identity, predecessor payload/configuration receipts and a bounded live
plan. No model acquisition, schema migration, journal recreation or retention
cleanup is needed for this change.

Deployment, helper restart, shared-cursor recovery and resumption require fresh
user approval. Keep both production sources paused during release validation.
Validate the held helper transport, live/readiness/index health, preserved root
revisions and pending/cursor evidence, and absence of scan claims. Stop on any
preservation failure. Do not automatically resume sources after deployment.

A later, separately approved recovery/resumption can permit the shared reset and
one required baseline. Bound observations and pause again on renewed whole-root
churn, unconfirmed native ownership or failed health. Record the rejected cursor
and recovery receipt rather than clearing debt. Preserve source data and the
predecessor release throughout.

There is no schema downgrade. The incremental journal updater refuses automatic
predecessor restoration once protected feature/configuration evidence exists.
Failure containment therefore retains the deployment hold, the matching payload
set and paused sources until a separately reviewed forward-recovery action is
approved. Preserve predecessor application/helper/configuration receipts for
assessment. Reverting binaries must use a supported, specifically reviewed
compatible recovery path; do not substitute manual copies or an unapproved
feature-off operation. Preserve current SQL, index and recovery evidence, and
never restore an old database or cursor to make rollback appear complete. If
recovery has begun, its durable debt remains required.
