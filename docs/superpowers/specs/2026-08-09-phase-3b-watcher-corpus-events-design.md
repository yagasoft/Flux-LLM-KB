# Phase 3B watcher, corpus and events design

## Status and decision

Status: approved design; implementation begins only after review of this written
specification and an approved implementation plan.

Phase 3A established a durable local UTF-8 corpus. Phase 3B makes that corpus
practical to operate: a safe local watcher reduces change-to-index latency, a
PipelineRecord-led Corpus view exposes actual indexed entries, and a durable
Events view explains work in progress. It does not activate a new processor,
executor, GPU runtime, external connector or MCP write surface.

The 2026-08-09 loopback validation proved the Phase 3A baseline: held and
released scans, retained UTF-8 indexing, an explicitly deferred PDF, search
provenance and restart-stable source rows. See the [Phase 3A validation
record](../../operations/native-windows-phase-3a-source-management-validation.md).

## Goals

1. Notice changes beneath enabled Phase 3A source roots promptly without making
   watcher notifications authoritative.
2. Let an operator browse every application-owned `PipelineRecord`, including
   direct registrations and source-backed revisions, with stable paging and
   SQL-authoritative status.
3. Show indexed text, provenance, lineage, pipeline state and deferred or
   failed activity evidence without re-opening the original file.
4. Provide a durable, filterable event timeline that explains watcher, scan,
   source-activity and pipeline work across IIS restarts.

## Boundaries and non-goals

- SQL remains authoritative. USearch remains derived and is never the catalogue
  or event-history authority.
- `FileSystemWatcher` is a best-effort wake hint. A persisted scan request and
  periodic reconciliation verify every reported change.
- Phase 3A root policy, physical identity checks, no-reparse traversal,
  retention, checksum verification and source-activity idempotency remain
  unchanged.
- The Corpus lists only application-owned `PipelineRecord` rows. MCP, Gmail,
  Outlook and other external text appear only after a later adapter creates an
  immutable, provenance-preserving application record.
- No watcher event directly indexes, deletes, reads an original source file or
  changes a public pipeline Job state. A missing source uses the existing
  suppression model, not physical deletion.
- No real executor activation, process supervision, GPU admission, model work,
  external access, public mutation route, deployment or IIS operation belongs
  to this design.

## Authoritative data model

### PipelineRecord catalogue

`PipelineRecord` is the sole entry identity. The Corpus read model is a
parameterised SQL projection, not a new `CorpusEntry` table.

Each row joins a `PipelineRecord` to its `SourceIdentity`, latest Job and
pipeline artefact summary. When `SourceRevisionId` is present it also joins the
source revision, root, root-relative folder/path, classification, linked source
activities and retained-artifact evidence. Direct registrations remain rows in
the same list, marked with their source kind and without a fictitious folder.

The default view includes current, non-deleted records and unsuppressed source
revisions. Historical, deleted and suppressed records are an explicit filter.
The default list sort is most recent durable activity, then `PipelineRecordId`.
Records without an audit event use `RegisteredAtUtc` as that durable-activity
fallback. This avoids hiding a newly registered direct record before its first
stage transition.
Cursor tokens carry those two values and the exact filter fingerprint; they do
not expose SQL offsets or mutable row counts. The default page size is 50 and
the server bounds it to 200.

Search combines normalised source identity/path matching with existing SQL
Full-Text search over indexed text. Results are hydrated from the same SQL
projection, so search cannot return a record excluded by current/deleted or
source-suppression rules. Filters cover source kind, root, folder, source
classification, pipeline status, source-activity status and time range.

### Durable events

The existing `AuditEvents` table becomes the common durable operator-event
ledger; Phase 3B does not create a competing event store. A migration adds
nullable source correlations (`SourceRootId`, `SourceScanRequestId`,
`SourceRevisionId` and `SourceActivityId`), a bounded correlation identifier,
event family and severity. `PipelineRecordId`, `EventType`, actor, timestamp
and sanitised details remain the existing pipeline-audit contract.

Events are appended in the transaction that makes the corresponding
authoritative state change visible. Details are canonical, redacted and
bounded; they contain identifiers, counts, reason codes and safe path
provenance, never retained text, raw bytes, credentials or opaque
lease/process data. Indexes support descending `(OccurredAtUtc, Id)` paging and
correlated PipelineRecord, root and revision timelines.

Stable event types are:

- `watch.batch_detected` and `watch.overflow_detected`;
- `scan.released`, `scan.claimed`, `scan.completed` and `scan.failed`;
- `source.added`, `source.updated`, `source.removed` and
  `source.retention_blocked`;
- `activity.planned`, `activity.deferred`, `activity.claimed`,
  `activity.completed` and `activity.failed`;
- existing `pipeline.*` and derived-index audit events.

Idempotency follows the durable transition that emits an event. A watcher batch
is unique for its root and persisted debounce generation; a source change is
unique for its revision and transition; pipeline events retain their existing
receipt or stage fence. Replays return the original event rather than creating
a duplicate timeline entry.

## Watcher and reconciliation flow

Each enabled, policy-valid local root has one hosted `FileSystemWatcher` with
subdirectory monitoring. It observes create, change, delete, rename and error
signals only. Before opening a watch, the application revalidates the persisted
root identity and reparse policy; paused, invalid or inaccessible roots are not
watched and emit bounded diagnostic evidence.

Signals feed one `SourceRootWatchState` row per root: first/last signal times,
a bounded signal count, debounce generation and next due time. A two-second
quiet period, bounded by a 30-second maximum delay, produces one released
source scan request and one `watch.batch_detected` event. A
scheduler/reconciliation instance may claim only that durable request; it then
performs existing authoritative filesystem enumeration and SQL convergence.

Watcher error, overflow, an invalidated handle or uncertain rename schedules
the same durable full reconciliation and emits `watch.overflow_detected`. IIS
restart between a hint and reconciliation is safe because the watch state and
request are in SQL. If the application was stopped while the filesystem changed,
the existing 15-minute periodic reconciliation finds the difference. The
watcher improves freshness but never supplies truth.

After reconciliation, newly discovered, changed and newly unseen source rows
append `source.added`, `source.updated` and `source.removed` respectively.
Only then may the normal retained-text planner and in-process pipeline create
their linked records and activity events.

## Operator experience

### Corpus

`/corpus` replaces the current basic pipeline-record table as the primary
catalogue view; `/pipeline-records` remains a compatibility redirect or clear
link. The page provides a filter drawer and root/folder browser beside a
server-paged table:

| Field | Meaning |
| --- | --- |
| Entry | Source display name or source-backed relative file path, linked to detail |
| Category | Source kind and, when present, source classification |
| Location | Root and folder for source-backed entries; `Direct` for other records |
| Pipeline | Exact current stage and derived public state |
| Source activity | Indexed, pending, deferred, blocked or terminal failure, with no offer treated as indexed |
| Updated | Most recent durable event time |
| Actions | Open entry, source root or relevant event timeline |

Folder browsing is derived from canonical persisted paths relative to the root.
It never enumerates the live filesystem, follows a link, or grants the browser
file access. Each folder projection includes direct-child folders and aggregate
current/deferred/blocked/failed counts. Selecting a folder constrains the same
cursor-paged PipelineRecord query.

`/corpus/{pipelineRecordId}` shows source and revision provenance, lineage,
checksums, current and historic pipeline activity, jobs, and related events. A
bounded preview reads canonical indexed text artefact/chunks from SQL rather
than the original path or retained binary store. A deferred or non-text record
shows its exact reason and available evidence, never invented text.

### Events

`/events` is a descending, cursor-paged timeline with filters for time, event
family, severity, root, PipelineRecord, source revision and correlation ID. It
supports live tail with pause, but initial load and every SignalR reconnect
re-read SQL. The live feed is a refresh hint only; the durable event ledger is
what the operator sees after a restart.

Each row exposes time, event type, concise message, related root/entry and
correlation. Event detail renders sanitised structured evidence and links back
to the Corpus entry, Sources root and any related scan request. Corpus and
Sources detail pages show their latest related events so an operator need not
manually correlate identifiers.

## Failure handling and safety

- A watcher startup failure, root replacement, overflow or persistent access
  error is surfaced as an event and leaves the root available for periodic
  reconciliation; it never disables source safety checks.
- The watch coordinator coalesces a noisy save sequence but does not drop the
  resulting durable scan request. Repeated requests converge through existing
  source revision/activity idempotency.
- Read projections use no filesystem handles and do not expose retained bytes
  other than the bounded, already indexed text preview.
- Every event write shares the state-change transaction or is not shown as
  completed. Event query failures do not alter pipeline capacity or source work.

## Acceptance criteria and verification

Implementation starts with focused failing Domain, native SQL integration and
Web tests. It must prove:

1. A synthetic create, change, rename and delete hint becomes one bounded watch
   batch and an authoritative scan; eventual file events match SQL revisions
   rather than raw watcher guesses.
2. Burst coalescing, overflow and IIS restart preserve a due full scan and emit
   deterministic, non-duplicated event evidence.
3. The Corpus lists direct and source-backed PipelineRecords in one stable
   cursor order; filters and cursor fingerprints cannot leak rows across scope.
4. Folder counts and source status are SQL-derived and remain correct when the
   original filesystem path is unavailable.
5. A Corpus detail preview matches indexed artefact text, remains bounded and
   never re-opens the original source or non-text retained artefact.
6. Source/pipeline transitions append one correlated event in the same durable
   transaction; duplicate delivery, fenced terminal transition and restart do
   not duplicate it.
7. Events, Corpus and Sources projections reload from SQL on reconnect and do
   not overflow opaque identifiers.
8. Existing Phase 1/2 pipeline and Phase 3A root, recovery, deferred-activity,
   source-suppression and SQL-to-USearch rebuild invariants remain valid.

The implementation plan will specify exact focused test commands and a broader
Release build. No migration, deployment, IIS restart or live validation is
authorised by this design record.

## Deferred follow-up

Phase 3B later processor/capability expansion remains separate from this
watcher-and-observability slice. MCP/external text needs an immutable snapshot
and provenance contract before it can become a PipelineRecord; it is not a live
query into another corpus. Event retention, export and operator mutation actions
also require separate policy and approval.
