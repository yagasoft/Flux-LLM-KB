# Source pause, resume and delete

Status: implemented on `codex/source-delete-plan`; Release/full-suite verification,
incremental deployment and the authorised disposable-source live validation remain
before this design can be marked deployed.

## Outcome and scope

Add Pause/Resume and Delete controls to both the Sources list and source-detail
page. Deliver two working slices: pause/resume first, then complete deletion.
Use the existing native application, SQL Server, Blazor UI and command surfaces.
There is no separate administration service, generic workflow engine or new
pipeline stage.

The first delivery covers local filesystem sources, including their retained
archive children and C# facts. Outlook-bound roots and roots with external/GPU
execution ownership are refused with a specific explanation before deletion;
this feature must not terminate executors or infer their absence from a timeout.
Pause prevents new admission but does not claim to suspend an external process.

Original files and folders are never deleted or modified. `J:\Models`, provider
caches, application releases and deployment recovery payloads are outside the
deletion scope. No model acquisition, PDF/VSDX/OCR changes, backup creation,
recycle bin, undo mechanism, bulk deletion or dashboard-manual regeneration.

## User behaviour

| Control | Visible result | Content and work |
| --- | --- | --- |
| Pause | Paused, with an active-work count while draining | No new scans or processing claims; queued work is retained and existing content stays searchable. |
| Resume | Enabled | Release existing eligible work and request one coalesced reconciliation; completed and unsupported work is not replayed. |
| Delete | Deleting, then removal from Sources | Stop admission, drain owned execution, remove source-owned SQL and app-managed files, then report completion. |

Pause/Resume is a single UI action, with in-flight buttons disabled and an inline
result. Delete opens one accessible confirmation showing the name, canonical
path, affected record/file counts, shared files retained, and: "Original files
will remain. Indexed content and processing history will be permanently removed."
Cancel makes no changes. Confirm uses the preview's exact source/version and a
stable operation key. A changed source requires a fresh preview, not a blind retry.

During deletion, keep a status row with the phase and a useful reason if blocked.
Offer Retry cleanup for a failed cleanup pass. Disable Resume, Scan and other
source mutations while deleting. On success, remove the row and show a completion
message. Reloading the page must recover status from SQL. Deleted detail links
show "Source deleted" rather than a permanent loading indicator. Browser, REST,
MCP and CLI all call the same application command contract.

## Pause and admission

Reuse `SourceRootState.Paused`; add `Deleting` for deletion. Add native actions
`root_pause`, `root_resume`, `root_delete`. Preserve `root_disable` and
`watcher_set` compatibility, with the same lifecycle checks; neither may reopen
a deleting source. Reuse native preview, row-version fencing and idempotency.

Check source admission atomically at scan/watch claims, outbox and job claims,
retained processor/force claims, promotion/restart and GPU admission. Pause must
not consume attempts, mark jobs failed or erase held scan requests. An operation
already claimed before pause may complete its normal fenced boundary and enqueue
successors; successors remain unclaimed. Resume coalesces reconciliation and wakes
queued work immediately, including when no watcher batch exists.

Use one root-scoped execution lease around actual in-process scan, processor and
stage execution. A SQL application lock shared by active executions and exclusive
for deletion provides process-spanning drain evidence. Acquire the execution
lease, recheck admission, then claim; retain it through publication and final
filesystem use. Connection/lease loss aborts the worker and fences publication.
Pinned file handles continue protecting in-progress IO. Expired job/scan leases
alone are never evidence that execution has stopped. This is a small addition to
existing worker entry points, not a new scheduler or cancellation system.

## Small durable deletion operation

SQL and file deletion cannot share a transaction. Retain one deletion-operation
row and an operation-owned list of pending physical paths, so an interruption
does not lose cleanup responsibility. These are the only new control tables:

- Operation: ID, root ID, phase, owner/fence, retry time, bounded failure reason,
  timestamps and aggregate counts; unique active operation per root.
- Cleanup item: operation ID, storage kind, relative path, expected identity/hash
  where available, state and bounded failure reason. No source content copies.

Keep the source root in `Deleting` until cleanup completes; its canonical-path
uniqueness prevents re-adding it during deletion. Extend the existing
`SourceReconciliationService` to run a bounded deletion pass on wake and tick,
including after restart. A blocked root cannot starve unrelated scans. Repeated
commands and retries return/resume the same operation.

1. **Accept and drain.** In one fenced transaction, set Deleting, stop watcher
   admission and create the operation. Hide deleting-source content from new
   search/corpus/detail reads. Acquire exclusive execution ownership before purge.
   Queued work is removed with the source; running work is not forcibly completed.
2. **Prepare and commit removal.** Resolve descendants and exact ownership, build
   and validate a survivor index if required, and capture physical cleanup targets.
   Atomically activate the verified survivor index, remove the source's SQL graph
   and record cleanup responsibility. Keep the root/status row until step 3.
3. **Clean files and finish.** Dispose retired native index readers, delete only
   verified eligible app-owned paths, then remove the root and compact the
   operation to its completion receipt. Missing files count as already removed;
   access failures, sharing violations or unsafe paths remain incomplete.

The receipt retains only operation/root IDs, counts, timestamps and result. Purge
completed cleanup paths and source-specific detailed audit/history with the source.
Shared native command receipts, policies and capabilities remain. Failure before
the SQL cutover keeps the original records; failure after it leaves Deleting with
resumable cleanup. Deletion is irreversible once accepted, with no automatic
backup or compensating recreation.

## Ownership and physical cleanup

Delete the source root's entire retained descendant graph, not just current
revisions: scan jobs/requests/outboxes, watch state, source revisions/artifacts,
activities/relations, branches/attempts/member dispositions, deferred and force
requests, source-specific action history, C# facts/receipts/diagnostics, pipeline
records, jobs/attempts/outboxes, artifacts/chunks/vectors and owned audit events.
Remove source identities only when no surviving record references them. Independent
knowledge notes/claims have no source-ownership relation and are preserved; never
delete them by matching text. Cross-root relations are checked, not cascaded blindly.

Retained blobs are content-addressed and may be shared. Check surviving references
by storage-root identity plus relative blob path, not `ReferenceCount` alone.
Coordinate cleanup with writers across the whole file-publication-to-SQL-reference
interval. A shared SQL storage lease held through registration and an exclusive
cleanup lease are sufficient; a lock inside `PutAsync` alone is not sufficient.
Recheck references while holding the cleanup lease. Preserve legitimately shared
files and report their count. Capture known output/staging paths before deleting
their SQL owner; do not sweep unattributed files or an entire storage directory.

USearch generations are immutable and can contain several sources. Reuse the
existing recovery lock for shared normal generation publication, held from
build/placement through SQL commit or registered disposal. Deletion takes exclusive
ownership before the final target capture, survivor build and cutover. This also
prevents a late, rejected publisher from leaving deleted vectors in a new file.
Keep snapshot validation at activation; a lock is not a substitute for it.

Create a new generation from surviving eligible vectors. Retire every generation
containing the removed vectors, including historical generations. Add nullable
`RetiredAtUtc` to generation metadata: remove retired memberships and files, clear
its physical path, and prohibit activation/rebuild. Keep the original ID and
content-free descriptor only while surviving vector FKs or pipeline provenance
need it; do not rewrite those vectors or terminal artifacts. This is explicit
retirement, not conversion to an unplaced Embed draft. Draft validation stays
strict. Delete unreferenced retired descriptors. Wait if a surviving active job
still owns a generation being retired. Never rerun another source's pipeline.

When no eligible vectors remain but canonical rows still exist, introduce a
validated zero-vector generation: builder, validator, recovery and readiness must
explicitly support it. Validate zero physical entries and metadata; do not run the
non-empty cosine probe. This is new behaviour with a retained test for suppressed
survivor vectors. Preserve the existing validated-empty-catalogue rule: that marker
is allowed only when vectors, generations and memberships are all empty. Explicitly
release `UsearchAnnIndex` handles before physical removal, including the null-active
case. Current/in-flight readers must finish before their file can be deleted.

All deletes use pinned app-storage roots and the existing handle-relative Windows
filesystem helper. Validate exact child identity and no-follow containment at the
mutation; reject reparse points, traversal and substituted directories. Do not
remove a file solely because its SQL path looks plausible. Shared surviving data,
source originals and models are protected even when persisted paths are corrupt.

Six retained-C# triggers currently reject DELETE as well as UPDATE. A reviewed
migration must permit DELETE only for rows belonging to the exact active deletion
operation in its SQL purge phase, while continuing to reject every UPDATE and
mixed-root or unauthorised DELETE. Do not disable triggers or foreign keys.
Update the readiness verifier's expected trigger hashes in the same migration
delivery. This explicit deletion exception does not permit rewriting terminal
processor ownership or receipts during ordinary processing.

## Verification and decisions

Prefer retained disposable-SQL and native filesystem integration tests. Cover
pause/claim races and draining, restart-stable pause/resume without duplicate
processing, full deletion of a populated root, a surviving second root with a
shared blob and mixed current/historical indexes, final-source empty readiness,
C# trigger permissions, interruption around SQL/file cutover, locked-file retry,
no-follow path replacement, repeated confirmation/commit, and actual UI controls.

The alternatives were a direct cascade (cannot safely finish shared file cleanup)
and a separate generic deletion subsystem (unnecessary). The selected approach is
one source lifecycle guard plus a small durable cleanup operation in existing loops.
The only architecture expansion is the storage needed to resume irreversible IO
and the coordination needed to preserve shared data. No new product processors.

## Deployment and practical verification

The current incremental updater explicitly reports `migrations = false`; its
application-only rollback also cannot restore compatibility with the old pinned
C# trigger hashes after this migration. Include a small, explicit `-ApplyMigrations`
opt-in in that same updater. Keep ordinary application-only deployment unchanged.
PlanOnly must report exact allowed migration IDs/hashes and the prior schema;
unknown drift or insufficient existing SQL permissions stops deployment. No SQL
bootstrap, permission grants, clean-slate GoLive or manual IIS restart.

Apply only the reviewed feature migration while the pool is stopped under the
existing validation hold, before starting the candidate. The hold must also reject
lifecycle commits on every surface and hold deletion cleanup. Candidate probes
must prove readiness and unchanged retained data plus no deletion operations,
retired generations or zero-vector feature state created during validation.
On held validation failure, stop the candidate, prove those conditions again,
reverse only this unused migration, and verify exact prior migration history and
trigger hashes before starting the previous payload. If reversal cannot be proved,
leave the pool stopped and hold intact; do not release it in unconditional cleanup.
After hold release, schema downgrade and restoration of deleted data are forbidden;
failures require forward repair. Existing application-payload recovery is retained,
but no source-data backup is created.

Live verification uses only the selected test source and its original read-only
folder: exercise Pause/Resume on both UI locations, delete and verify complete
owned SQL/file/search removal, then re-add and scan that folder once. Capture
source-file hashes before and after without copying contents. Never replay another
source or alter model stores. Report actual parser outcomes rather than treating
this lifecycle feature as a PDF/VSDX/OCR improvement.

Design review is required before implementation. This turn ends after documentation
and plan review; no deletion, rescan, migration or deployment belongs to this turn.
