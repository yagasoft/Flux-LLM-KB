# Events and weekly metadata reconciliation

The personal knowledge app prioritises low disk activity over exhaustive change
detection. The user accepts stale or missed same-size edits with preserved
modification times and undetected silent corruption. This supersedes automatic
source and retained-content assurance. Access, path, ownership, publication and
native cleanup protections remain required.

## One operating policy

Journal and watcher events drive normal updates. An identified file content
event forces normal ingestion even when identity, size and modification time
match. The fallback is a weekly metadata inventory: membership, physical file
identity, size and modification time. Existing unchanged content is reused;
the inventory does not hash source or retained bytes for assurance. Hashing
during actual ingestion, deduplication and retained-content consumption stays.

There is no automatic checksum schedule, unsealed-file timer or restoration
recapture of otherwise unchanged content. Seals and old capture receipts remain
historical evidence; missing seals do not prevent metadata reuse. The new policy
does not manufacture a seal, checksum time or continuous journal coverage.

Epochs, generations, root/configuration fences and safe observation leases still
protect concurrent ownership. An initial or recovery baseline is a metadata
inventory, with content reads only for new/changed files or actual pending file
events. A journal gap retains its explicit old/replacement cursor receipt;
bounded journal retry and a dashboard warning remain. Pause/resume may require
metadata reconciliation but must not force unchanged-content recapture.

An initial query failure can leave a registered root without a volume
checkpoint. Background intake retries that query once per volume per service
poll, including during multi-volume bursts. When it recovers, an enabled root
and an absent checkpoint are rechecked under the normal locks before recording
the actual journal ID and current head. The first-registration audit explicitly
claims no historical coverage. Existing or concurrently registered checkpoints,
root recovery evidence, pending changes and metadata deadlines are preserved;
normal journal reads continue from the next poll.

The weekly interval is 604,800 seconds. Existing enabled and paused roots,
excluding roots undergoing deletion, move from their prior interval through the
audited transition before new worker admission. New roots use the weekly default.
The compatibility field `reconciliationSeconds` defaults to 604,800 and rejects
other supplied values; UI and mutations describe the effective policy clearly.
Explicit `source_sync` with `fullContent: true` provides a manual full
rescan/reindex through normal ingestion. Ordinary sync uses metadata. Manual
full work has a distinct request/manifest marker and is never scheduled by
journal uncertainty or missing proof. Retired verification commands cannot
reopen checksum work.

## Reuse, events and membership

Metadata reuse checks the current admitted source path, physical identity,
identity/size/modification time, current revision/artifact binding and retained
metadata under owned root/configuration/lease fences. It does not require the
old observation's seal or recovery epoch to match. The retained hash keeps its
original ingestion provenance. Newer relevant dirty generations prevent reuse
and remain pending; only an actual capture may satisfy content-event debt.

An absent inventory metadata receipt does not itself require a content read.
The current unique unsuppressed physical revision's original ingestion identity,
size and nonnull modification time may initialise explicitly `MetadataOnly`
inventory facts when all match current safe source and retained leases. This
keeps the original artifact/hash authority and current root/plan fences, records
no capture generation or verification time, and cannot acknowledge a file event.
Missing or conflicting ingestion facts still require ordinary ingestion. Existing
malformed or conflicting receipt bindings are not silently repaired.

Git reuse also establishes current complete tracked membership and physical
repository identity. A metadata observation can reauthorise discovery after a
pause or configuration-revision change only through the existing
observe/complete/authorise flow. It must not copy old authorisation or weaken
the global visibility predicate, revive suppressed data or authorise excluded
paths. Authoritative absence checks and publication fences remain unchanged.

Known file-event paths remain durable. Capacity pressure must not collapse them
into an assurance-driven full recapture. A journal page that cannot admit its
new unique paths rolls back all page effects and retries with bounded backoff;
updates to existing paths remain admissible at capacity. An indivisible record
whose alias expansion cannot fit is visibly blocked, never cursor-skipped.
Pending paths can drain without successful continuous-journal barriers.
Watcher capacity refusal retains the batch and later generations. Already
retained hints survive overflow alongside metadata uncertainty; events beyond
the watcher's bounded hint buffer may be missed under the accepted policy.

## Explicit transition and recovery

The supported incremental updater performs a stopped, bound policy transition
before starting the new Web worker. Reuse the existing deployment mutex,
database incarnation, held runtime/configuration/writer bindings and physical
verification-reader slot. Cancellation is only a request: native terminal
completion and handle/buffer disposal must precede slot release and retirement.
Journal intake remains enabled; this is not the journal feature-off path.

The policy transition also runs when the operator explicitly disables journal
intake. That mode preserves its disabled configuration guards and can use a
separately published current CLI operator bound by
`SourceJournalPolicyOperatorRoot` and
`ExpectedSourceJournalPolicyOperatorSha256`; it does not load or restart an old
installed helper merely to adopt the policy. Plan-only validation checks the
operator digest and fixed policy contract before stopping the application.

Retire outstanding verification-only runs as invalidated with a policy-change
reason and terminalise only verification-kind requests/jobs. Preserve items,
receipts, counters, incomplete-read evidence and prior verification timestamps.
Gate scheduling, claims, manual/retry controls, prepare, read permission and late
finish so no retired work can restart or certify success.

In the same auditable policy operation, record old/new policy and root interval,
affected roots, verification work and strict root obligations. Convert strict
whole-root assurance into metadata inventory debt, preserving its original
reason/generation evidence. Keep actual file-level pending changes. Their old
reason bits mix assurance with real events, so ambiguous file-level work is
retained for one normal capture, not deleted or labelled completed. No journal
cursor, ingestion hash, publication or unrelated work is cleared. Existing
claims are fenced and their actual obligations remain recoverable.

The audit marker makes replay idempotent. No destructive schema cleanup is
needed. A failed transition retains the deployment hold and paused roots.
Rollback must keep roots paused and use a reviewed compatible payload/forward
recovery path; it must not silently restart the prior assurance policy.

## Verification and release gates

The first observable result is a real staging file event reaching durable work,
normal ingestion, publication and public retained readback. Byte counters must
also prove zero periodic source/retained assurance reads for quiet files across
the former 15-minute deadline, a weekly metadata pass, missing seals and journal
recovery. Metadata-only adoption must preserve old hashes and checksum times.

Focused SQL/native checks cover transition replay and rollback, active-reader
refusal, cancellation/late finish, retained history, newer events, exact
generation acknowledgement, queue capacity and Git/current-path ownership.
Isolated functional staging covers additions, edits, renames, moves and deletes
through indexed readback, including event-triggered metadata-equal edits and
the explicitly accepted missed edit without an event.

After focused tests, review the complete change and run the prescribed canonical
closeout. Prepare exact release bindings, rollback and bounded live validation
for independent operational review. Both production roots stay paused while
preparing. The user's continuing deployment/validation authority applies to
this change; it does not authorise unrelated retention or ownership cleanup.
