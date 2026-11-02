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
  after the probe. A valid control permits exactly one retry at the original
  position on that same handle. Successful retry returns the original history,
  never control bytes; only another error 87 becomes cursor-rejected. Other
  retry errors retain their existing availability or explicit gap semantics.
  Cancellation is checked before retry and after success or failure. Healthy
  reads remain one read IOCTL; the exceptional path makes at most three read
  IOCTLs, with no general retry loop or successful-path overhead.
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
- Creation of new automatic whole-root work respects
  `LastReconciliationAtUtc + ReconciliationCadence`, falling back to the last
  completed scan when there is no reconciliation timestamp. This applies to
  degraded work, healthy root-wide work and recovery baselines after journal
  restoration. An initial baseline has no previous completion to pace. Existing
  owned requests remain recoverable, and explicit user requests retain their
  contract. Journal retries and scoped work continue independently. No pending
  obligation is consumed to achieve quiet.
- The pipeline overview warns prominently when a checkpoint is unavailable or a
  baseline is required. It shows the unavailable-since and next-retry timestamps,
  normal full-scan interval, recovery debt and paused admission, with a link to
  source details. Health comes from the checkpoint in the same committed sample,
  so a stale fallback mode alone cannot misreport a successfully restored journal.
- Helper replacement retains operation-local exit proof through rollback,
  restart and held IIS checks. A stale CIM row may be ignored only when its
  helper name, PID and creation time match an incarnation whose previously
  captured native handle proves exit. Immutable identity receipts survive
  handle disposal until the update closes, then are cleared. Unknown or live
  processes, reused PIDs and missing identities still block the update. No
  persistent process allowlist or time-based grace period is introduced.

Source contents, public APIs, schema, native cleanup invariants, model configuration,
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

The resilience follow-up also exercises real NTFS create, content edit, file
rename, directory move and deletion in separate functional staging with
deterministic embeddings and no real models or GPU. Validate current publication
and public retained readback as well as file hashes. A scoped directory capture
alone does not certify the whole namespace. If metadata debt remains, preserve
it until the ordinary reconciliation sweep, and retain any newer uncertainty. Do not
require immediate zero debt, infer publication from pipeline completion alone,
or use an unqualified fixture to claim unchanged-content byte savings.

The qualified functional run on 2 November passed all five file operations,
current REST search and retained readback, and deletion withdrawal while all
20 unchanged documents remained published. Their revision IDs, hashes and paths
were unchanged. Across the interval, enumeration read 18,008 source bytes
(four changed-file lengths), retention read 9,004 bytes (two changed versions),
and verification read zero bytes. One root-wide namespace sweep respected the
fixture's normal 60-second cadence; the production 900-second cadence was not
changed. No requests appeared in the final 20-second quiet interval. Namespace
coverage was complete and pending work empty before the fixture was paused.
Deterministic embeddings qualify the functional publication/readback path,
not semantic quality, real-model latency or OneDrive provider behaviour. The
earlier capture-only and harness failures remain preserved as separate evidence.

Journal positions are offsets in a volume's journal stream; records are
appended and old history can be trimmed. A file rename adds change records; it
does not relocate an earlier USN. The original error87 position was inside the
advertised retained range with the same journal ID. This excludes neither every
possible cursor/protocol defect nor every filesystem fault; the exact origin
remains unproved. The successful head control returned no historical records.
See Microsoft's [journal record description](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journal-records)
and [read cursor contract](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-read_usn_journal_data_v0).

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

## Interim production result, 2 November

Canonical release `ba9de046` passed 5,018 .NET tests and nine native contract
steps with zero build warnings. The paired update refused a missing-path native
process observation while stopping the helper. It restored the old helper and
retained the Web deployment hold. The failing CIM row was not preserved, so the
exact original process race remains unproved. A stale row for an exited,
previously captured process reproduces the same refusal in isolation.

Separately approved Web-only recovery started the installed new Web app using
the existing native, schema, index, source and transport guards, then released
only that failed release's hold. The companion stayed disabled and both sources
stayed paused. Ten-minute validation confirmed live/readiness health, unchanged
cursor, exact pending work and published index generation, with no new scan
attempts. Desktop and mobile dashboard warnings passed inspection. A bounded
file trace observed ordinary index-manifest reads and no E: source-file reads.

The paired helper correction remains pending. Its release needs canonical
closeout, independent operational review and fresh deployment approval. Neither
the interim recovery nor the next helper release authorises source resumption.
