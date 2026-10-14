# Pipeline visibility acceptance and production gate

The dashboard implementation is reviewed, verified and merged into Main as
`e7ed80bbcc6a9315026d983bae80a5db0d63f134`. It has not been deployed to
production. Production deployment and live dashboard validation remain open.

## User-visible behaviour

The overview puts current activity and named source progress first. Files,
pipeline jobs, GPU tasks and source scans have separate labelled counts. Exact
totals replace `999+`. Source scope and the successful status-read time are visible.
Current queues, retries, blocked work, confirmed execution and unresolved historical
records are distinguished using revision, publication, lease and scheduler evidence.
Age alone never establishes history. Ambiguous file bindings remain provisional.

Historical failures and uncertain GPU outcomes remain accessible through read-only
paged detail. Search-index health describes the derived projection separately.
Detailed scheduler and lifetime totals are expandable. The existing Blazor shell,
components, native controls and styling are retained. No estimated completion
percentage or undiscovered remaining total is presented.

## Verification

- Independent Astra review approved the complete feature for canonical closeout.
- `complete-feature.ps1` passed all 21 steps, including feature/Main integration
  and push. Both checkouts passed 3,340 tests with 25 opt-in skips. Release builds
  had zero warnings and errors. The dedicated worktree was retained.
- Focused SQL checks passed 24 cases. State/render checks passed 53 dashboard
  cases; the combined source-preview regression run passed 62 cases.
- The separate 1,000-file backlog check accepted 10 of exactly 12 reads during
  four heartbeat writes: 40.5 ms median and 45.8 ms maximum. Every accepted read
  retained exactly 1,000 pending files. Rejected reads remain unavailable/stale.
- A cold diagnostic read observed stable fence values advancing from 3029 to
  3031 and correctly rejected the sample. The allocator remains unproved. The
  single compilation warm-up is recorded/discarded; measured thresholds, runtime
  acceptance and pending/intervening transaction tests were not relaxed.
- The opt-in browser scenario passed desktop, 390 px and 200% zoom, including
  source selection, paging, keyboard operation, stale/error recovery and scope races.
- Final Main was published into the reused, isolated Release 1 functional stage.
  Actual SQL-backed checks passed fresh idle status, one known/published file,
  scope, refresh, job/GPU detail and keyboard diagnostic expansion at desktop and
  390 px. No page-wide overflow or console warnings/errors were observed. Source
  and configuration hashes were unchanged; models, GPU, OCR and mail remained off.

Private receipts, TRX files, earlier controlled browser images and original failed
diagnostics are retained outside Git. Staging evidence is not production acceptance.

## Production gate and unresolved work

The routine incremental updater's `-PlanOnly` succeeded with no migration,
clean-slate, stopped-pool recovery, retention or replay flags. It preserves the
scheduled task and hidden launcher, retained state and predecessor payload rollback.
`-Apply` was not run. No production restart, hold, recovery or job mutation was
performed by this feature task.

Independent exact-target production review requires a hold. Current operational
evidence includes readiness changing from Starting/503 to Healthy/200, a worker
replacement, increasing uncertain GPU outcomes and a separately documented
recurring managed shutdown/native IIS crash sequence. A healthy response alone
does not establish a stable deployment baseline. The UI change does not repair
that incident. Release 1 owns the isolated ZIP collision reproduction and narrowly
scoped correction/containment; native causation remains unproved.

Reassess the exact candidate and production action after independently reviewed
staging evidence. Forced termination, ownership reconciliation, replay and
stopped-pool repair are not substitutes for that gate. Material operational scope
changes require explicit user direction. A recurrence during subsequent live
validation is a failed gate, not authority for recovery retries.

Historical jobs, uncertain outcomes and source-deletion attention remain unresolved.
No record was reset, replayed, deleted or marked successful to tidy counts. Any
separate recovery still needs exact eligible manifests, compatible bindings, known
prior outcomes, confirmed native cleanup and idempotency. Source content, canonical
embeddings, checkpoints, the model store and recovery evidence are preserved.
Manuals were not changed.
