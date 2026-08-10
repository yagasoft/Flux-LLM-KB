# Native Windows replacement design

## Status and decision

Status: approved for specification, not yet approved for implementation.

This records the target architecture for replacing the legacy
Python/Docker/PostgreSQL/RabbitMQ/Vespa runtime with a native Windows
application. It applies to codex/flux-native-windows. The legacy runtime is a
read-only behavioural and compatibility reference until the replacement passes
local verification and its SQL-to-USearch rebuild test.

The target is one ASP.NET Core server application hosted in IIS. It uses a
Blazor Web App with Interactive Server, SQL Server as canonical durable state,
and an in-process USearch index as a derived, rebuildable ANN projection.
Classic Outlook is the sole acquisition-adapter exception: a signed VSTO/COM
add-in runs in OUTLOOK.EXE, not IIS, and is not a worker service.

This is a full target design and traceability baseline. Phases 0, 1 and 2 have
approved implementation evidence; Phase 3A now has focused source-level and
component-level code evidence recorded in [its validation record](../../operations/native-windows-phase-3a-source-management-validation.md).
Its schema is unapplied and no Phase 3A deployment, IIS action, local SQL/IIS
vertical-slice validation or processor activation has been authorised or run.

## Goals, non-goals and guardrails

### Goals

1. Preserve crawling, monitoring, Outlook, Gmail, indexing, search, code
   understanding, OCR, image/video/audio, archive processing, MCP, Codex
   plugin, REST, CLI and operator visibility.
2. Use only native Windows runtime components: IIS, ASP.NET Core, SQL Server,
   embedded USearch and explicitly authorised local inference adapters.
3. Make SQL canonical and every index, cache, queue and projection recoverable
   from SQL-held durable state.
4. Preserve source revision and parent/child provenance through all branches.
5. Deliver a small local vertical slice without losing the complete feature set.

### Non-goals and hard constraints

- Do not migrate PostgreSQL data.
- Do not alter legacy code, Docker assets, model caches or private spools until
  local replacement and rebuild paths are verified.
- The target runtime contains no Docker, WSL, RabbitMQ, Vespa, Elasticsearch,
  external worker service or separately deployed microservice.
- Do not download, convert, copy, activate or parity-test a GPU model without
  explicit current-conversation approval.
- Do not deploy to IIS, restart services, cut over, run a production migration
  or process dead-letter messages without separate approval.
- Do not update dashboard manuals, screenshots or DOCX assets unless requested.

## Architectural shape

The process is a modular monolith. Class libraries protect boundaries and
testability, but only FluxKnowledge.Web is deployed and runs in IIS.

    IIS / FluxKnowledge.Web
        Blazor Interactive Server + SignalR + REST + hosted MCP
        Application orchestration and read models
        In-process durable workers and GPU scheduler
        SQL Server persistence and transactional outbox
        Embedded USearch generation manager
        Native inference adapters after approval

    Classic Outlook VSTO / COM add-in
        STA COM acquisition only
        ACL-protected spool and authenticated IIS ingress

| Component | Responsibility | Must not depend on |
| --- | --- | --- |
| FluxKnowledge.Domain | Pipeline contracts, public states, provenance, errors and values | SQL Server, IIS, USearch, GPU SDKs |
| FluxKnowledge.Application | Stage orchestration, query use cases, policy and ports | Provider-specific SDKs |
| FluxKnowledge.Infrastructure.SqlServer | Transactions, claims, outbox, projections and schema migrations | Blazor, MCP transport |
| FluxKnowledge.Infrastructure.Usearch | Immutable snapshot build, validate, switch, query and rebuild | Pipeline orchestration internals |
| FluxKnowledge.Infrastructure.Inference | Deterministic provider and approved ONNX Runtime CUDA/DirectML providers | UI and SQL types |
| FluxKnowledge.Integrations | File, Gmail, Outlook ingress, extraction and local-tool adapters | Presentation concerns |
| FluxKnowledge.Web | Blazor, SignalR, REST, Streamable HTTP MCP, health and authentication | Provider implementation details |
| FluxKnowledge.CodexBridge | Optional local hook and stdio-to-HTTP bridge without durable state or business logic | Pipeline and persistence logic |
| FluxKnowledge.Tests | Unit, SQL integration, contract, restart and end-to-end tests | Production configuration |

The bridge is included only when a lifecycle hook or a demonstrated Codex client
limitation cannot call hosted MCP directly. It is an HTTP client, never a
worker, queue, durable store or alternate business-logic host.

## Permanent domain contracts

### Source and provenance

PipelineRecord is exactly one source item revision. It owns a stable record
identifier, source identity, revision, content hash, root lineage, optional
parent record, current stage, required completion criteria and derived status.
An updated source creates a linked revision; it does not overwrite the meaning
of an indexed revision.

SourceIdentity holds stable external identity: local file, mail item,
archive member, embedded document image, sampled video frame or Outlook
profile/store/entry item. It is separate from PipelineRecord so the system can
retain lineage across revisions.

Artifact is an immutable stage output. SQL-resident metadata, text, chunks,
symbols, vectors and projections commit within the stage transition. A large
binary/file artefact is staged, hash-validated and atomically placed into an
immutable content-addressed store before the SQL transaction makes it visible.
A transaction failure leaves only unreferenced staged data for bounded cleanup,
never a partially visible result.

Archive members, embedded images, frames and mail attachments become child
PipelineRecords. Parent/child provenance is preserved for search, explanation,
retry and operator display.

### Jobs and dispatch

Job is one stage action. The only public Job states are:

1. worker queued
2. worker processing
3. GPU queued
4. GPU processing
5. completed
6. failed

Pending always means worker queued. Retry count, error details, due time,
lease evidence and blocked-dependency information are attributes or attempt
history, never additional public status values. Capacity loss returns a Job to
the applicable queued state with a due time; it does not create retrying,
blocked, parked or terminal work.

DispatchMessage is the durable SQL outbox record. It contains the pipeline
record identifier, source revision, stage, operation, dispatch generation and
idempotency key. It may carry safe scheduling metadata, but no SQL Server,
IIS, USearch, RabbitMQ or GPU-runtime type leaks into the contract.

Every successful stage transition is one SQL transaction:

1. Validate revision and idempotency key.
2. Persist the stage artefact and audit data.
3. Complete the current Job.
4. Create next Job or Jobs.
5. Persist their DispatchMessages.
6. Commit.

Only after commit may a worker acknowledge delivery or send an in-memory
wake-up. A duplicate delivery returns the durable original outcome.

## SQL Server and durable dispatch

- SQL Server is authoritative for records, jobs, artefacts, vectors, index
  metadata, ingress receipts, audit data and read projections.
- SQL Server creates and owns
  I:\FluxKnowledge\Sql\Data\FluxKnowledge.mdf and
  I:\FluxKnowledge\Sql\Log\FluxKnowledge_log.ldf.
- The application uses a standard server connection string and never
  AttachDbFilename or a user-owned local database file.
- A privileged provisioning command verifies I:, creates the approved
  directories, grants the SQL Server service SID the required ACL, creates the
  database through SQL Server and verifies an off-I backup target. Normal
  application startup only validates this state; it never silently attaches,
  moves or creates an arbitrary database.
- The IIS identity has only necessary application-store and SQL permissions;
  the SQL Server service SID owns database-file access.
- Claims are atomic SQL mutations with bounded lease and lease generation.
  Expired leases return to normal due-work claiming after a crash or IIS
  recycle. There is no message-loss reconciliation service.
- Workers drain due outbox rows after a wake-up and at startup. The 60-second
  poll is only missed-notification and publisher-recovery fallback.

## Routing and acquisition adapters

Stage is separate from Job state and is a versioned application contract.
Routes are chosen from normalised source metadata:

| Family | Required route |
| --- | --- |
| Text/document | identify, extract, normalise, canonical index, embed, publish |
| Metadata-only | identify, canonical metadata index, complete |
| Image | OCR, LLM enrichment, re-index derived text |
| Video/audio | transcript, re-index transcript, sample frames into child image records |
| Code | parse, symbols/references, derived artefacts, re-index |
| Archive | bounded member extraction and child record creation |
| Mail | normalise body, create attachment children, then reuse normal routes |

Every source adapter provides discovery or receipt validation, stable identity,
revision detection, content acquisition, metadata normalisation and restart-safe
resume. It creates PipelineRecords and never bypasses the durable pipeline.

- Filesystem crawling/monitoring runs inside the IIS application and persists
  root, revision and watcher evidence in SQL.
- Gmail remains an in-application acquisition adapter with durable cursors and
  attachment hand-off.
- Outlook COM never runs in IIS. The signed VSTO/COM add-in owns the STA thread,
  writes an ACL-protected spool using temporary write then atomic ready rename,
  and posts authenticated ingress to IIS.
- Outlook ingress includes stable GUID, profile/store/entry identifiers,
  revision, content hash and attachment hashes. IIS atomically creates or
  recognises the receipt, record, initial Job and DispatchMessage. Repeats
  return the same receipt.
- Spool entries are deleted/archived only after acknowledgement. Bounded
  startup catch-up runs on Outlook's COM thread.
- Use Windows Authentication or mutual TLS. DPAPI is the only permitted local
  secret protection when unavoidable; Outlook credentials are never stored.

## GPU and native inference

GPU work becomes a durable mini-task after worker hand-off. A mini-task holds
parent Job/revision, model/runtime key, one priority lane, compatible
dimension/settings fingerprint, bounded memory estimate, admission generation
and idempotency key. The scheduler owns it independently of the source worker.

Strict lane order:

1. interactive retrieval: MCP, Blazor, REST, CLI query embedding and Qwen rerank
2. document indexing/embedding: file, mail, attachment, extracted text and code chunks
3. image OCR
4. image LLM enrichment
5. video extraction, ASR, GPU frame work and unknown/untrusted GPU work

CPU-only classification, routing, archive expansion, metadata, chunking and
ordinary code parsing do not enter the GPU scheduler. Batches never mix model
runtime, lane, dimensions or incompatible settings. Admission is lane order,
then FIFO within lane; there is no ageing or automatic promotion.
Model-specific durable queues are implementation lanes only: model identity
never overrides the global lane order.

A higher-priority waiter creates a drain barrier. A lower-priority operation can
finish its bounded batch, but cannot start another before the higher-priority
task receives an admission decision. Running GPU work is never forcibly
interrupted. GPU pressure leaves work GPU queued with a due time, not failed.

The first slice uses deterministic local embedding and no GPU admission. The
permanent mini-task/lane contract is retained now so later adapters do not
require a schema rewrite. ONNX Runtime CUDA or DirectML is added only after
explicit approval of the model and runtime. Approved models use a versioned,
hashed app-owned cache such as %ProgramData%\FluxKnowledge\Models; Docker,
Hugging Face, Ollama and Paddle caches are compatibility references only.

## Retrieval and index safety

SQL stores canonical vectors with stable numeric vector ID, model fingerprint,
dimensions, content hash, entity revision, deletion state and index generation.
USearch contains no authoritative-only data.

Generation publication:

1. Select eligible SQL vectors for a candidate generation.
2. Build USearch in staging.
3. Save, reopen and validate dimensions, IDs, counts and metadata.
4. Atomically place the immutable candidate generation.
5. In a short SQL transaction, switch the active generation pointer and retire
   the prior pointer.

A failure before the SQL switch leaves the previous generation live. Hydration
reads a stable generation then rejects deleted or revision-mismatched SQL rows.
A rebuild enumerates SQL vectors and repeats publication; it never relies on a
USearch directory as recovery data.

Phase 2 adds continuous derived-index recovery. If the active USearch
generation is missing or invalid after startup, readiness becomes unready while
an in-process hosted recovery service rebuilds from the immutable SQL membership
and validates a safely placed replacement. Recoverable derived-index failures
use bounded backoff; invalid SQL membership/checksum, schema, configuration and
permissions failures are operator-actionable and are not retried indefinitely.
Recovery never changes the SQL active pointer on failure and never deletes an
active or SQL-referenced generation. Only aged, unreferenced staging or
quarantine candidates may be cleaned up.

The detailed approved recovery contract and acceptance criteria are recorded in
[the Phase 2 derived-index recovery design](2026-07-27-native-windows-phase-2-recovery-rebuild-design.md).

    SQL Full-Text + exact code/symbol retrieval + USearch ANN
        -> C# reciprocal-rank fusion
        -> optional authorised Qwen rerank
        -> canonical SQL hydration and explanation

Phase 1 implements SQL text search, deterministic vectors, ANN, C# fusion and
SQL hydration. Code/symbol ranking and reranking use the same query-plan
interface and arrive with their canonical artefacts/approved models.

## MCP, plugin, REST and CLI compatibility

The existing public MCP surface is a compatibility contract. Before a legacy
surface is retired, fixtures must capture tool discovery metadata, input
schemas, output envelopes, error classes, read-only/mutating semantics,
retries, readiness and hook behaviour. IIS-hosted MCP, REST and CLI adapters
use the same application commands.

The current baseline contains all 54 names:

- kb.search, kb.explain, kb.brief, kb.remember
- kb.claim_upsert, kb.claim_transition, kb.graph_traverse
- kb.capture_review, kb.capture_review_decide, kb.capture_review_ingest
- kb.retention_policies, kb.retention_quality
- kb.semantic_duplicates_refresh, kb.semantic_duplicates_list
- kb.acceleration_status, kb.watch_probe, kb.worker_status
- kb.crawl_backfill, kb.benchmark_run, kb.benchmark_history
- kb.indexer_reliability_status, kb.indexer_reliability_run
- kb.operator_evidence, kb.indexer_root_reliability, kb.indexer_reliability_roots
- kb.code_status, kb.code_search, kb.code_symbol_lookup, kb.code_feedback_record, kb.code_feedback_summary
- kb.operational_diagnostics, kb.diagnostics_remediate
- kb.retrieval_benchmark_run, kb.retrieval_benchmark_history
- kb.automation_status, kb.automation_run, kb.automation_actions
- kb.governance_run, kb.governance_actions, kb.governance_apply, kb.governance_recover, kb.governance_digest, kb.governance_policy
- kb.finalize_turn, kb.audit, kb.forget, kb.status
- kb.crawl_status, kb.crawl_sync, kb.crawl_watch_status, kb.crawl_watch_enable, kb.crawl_watch_disable, kb.crawl_jobs
- kb.mail_status

MCP is hosted in IIS using Streamable HTTP. Plugin manifest, installation,
discovery, readiness and status checks remain and cease to depend on Docker.
Codex uses hosted transport directly unless a demonstrated client limitation
requires the narrow local stdio-to-HTTP bridge. Lifecycle hook scripts call a
local hook client to reach the host and preserve current redaction, readiness
and error-envelope behaviour.

REST and CLI share the same use cases. A contract ledger identifies each
existing public route/command before its legacy counterpart is retired. No
transport creates divergent business rules.

## Operator experience, installation and recovery

The new Blazor UI is built from scratch rather than ported from React. It is
modern, responsive and accessible, and provides Overview; Pipeline Records;
Jobs; Job timeline and detail; Search and explanation; Index health; GPU
queue/status; and useful MCP/plugin/integration health. SignalR delivers
presentation notifications only. Clients reload SQL projections on
connect/reconnect, then subscribe for deltas. Counts are exact through 999 and
show 999+ only when the real count exceeds 999. Pending is worker queued,
never an inferred broader class.

Readiness verifies SQL reachability, schema version, outbox draining, active
USearch generation validity, rebuild capability, approved writable stores,
integration configuration and model authorisation. It never downloads/starts a
model, mutates settings, publishes to IIS or changes mail state.

Deployment guidance requires ASP.NET Core Hosting Bundle, x64 app pool,
WebSockets, least-privilege app-pool ACLs, no shared writable deployment
directory and application initialisation/always-running configuration where
background work is enabled. Restore verification rebuilds USearch from SQL
vectors. Backup must be off I:.

## Delivery sequence

| Phase | Deliverable | Exit evidence |
| --- | --- | --- |
| 0 | Target design, requirements traceability, contract inventory and roadmap | Reviewed specification; no runtime claim |
| 1 | Local UTF-8 file vertical slice: SQL record/job/outbox, deterministic embedding, USearch, hydrated search, live Blazor, kb.search/kb.brief | Focused SQL, snapshot, MCP and browser tests |
| 2 | Pipeline durability, continuous derived-index recovery, scheduler, rebuild, full job/read projections and the durable executor/result boundary | Atomic claim, duplicate, lease, runtime recovery, snapshot, rebuild, strict-priority and fenced adapter/receipt evidence |
| 3A | Local source management and searchable-content usefulness slice: durable roots, retained source revisions, classification, declarative activities, UTF-8 search and Sources/Indexing UI | Safe root validation, transactional scan request/outbox, preview, indexed UTF-8 result, deferred unsupported activity, provenance, rescan/restart/rebuild and truthful UI evidence |
| 3B | Broader local content and capability expansion | Processor capability registration, document/archive/image/OCR/video/audio/code activity evidence, parent/child provenance and exact-once deferred replay |
| 4 | Gmail, Outlook and other source ingress | Restart-safe ingress, receipts, spool, provenance and operator evidence |
| 5 | Remaining document, archive, image, video/audio and code branches | Parent/child provenance and branch-completion evidence |
| 6 | Explicitly authorised native model adapters/cache | Per-model approval, native-runtime and scheduler evidence |
| 3C | Full MCP/plugin, REST and CLI parity in bounded contract groups against a useful local corpus; starts only after Phases 1–6 are complete | 54-tool/route/command ledger, schemas, envelopes, errors, retries, hooks, readiness and executable compatibility fixtures |
| 7 | Local replacement readiness and legacy retirement decision | SQL rebuild, backup/restore, end-to-end surface evidence and explicit cutover approval |

The order is deliberately usefulness-first: a searchable local corpus must
exist before full MCP/plugin/REST/CLI parity is judged useful, and Phase 3C
starts only after Phases 1–6 are complete. It does not delete, downgrade or
waive any original Phase 3–7 requirement. No legacy capability is removed until
its replacement passes local verification.

## Phase 3A usefulness-first local corpus design

### Scope and safety boundary

Phase 3A is a local usefulness slice, not a general extractor platform. It adds
the durable source/root contract, source-byte preservation, safe classification,
declarative activity planning, a useful UTF-8 text path and a dedicated local
Sources/Indexing operator experience. It does not implement Gmail, Outlook,
advanced document/media/code processors, full MCP/plugin/REST/CLI parity, real
executor process management, model/GPU execution, external access or legacy
actions. SQL Server remains authoritative and the app remains loopback-only.

### Implemented now, designed extensions and future gates

The revised checkpoint distinguishes three statuses:

- **Implemented now:** Phases 0–2 and the Phase 3A source-management code path:
  source roots and scan controls, retained source revisions/artifacts,
  in-process UTF-8 planning/indexing, deferred replay and Sources/Indexing
  projections. The Phase 3A schema is unapplied and the local SQL/IIS
  vertical-slice validation remains outstanding. No real executor adapter is
  active in production.
- **Designed extension points:** Phase 3A may add only an execution-class or
  capability descriptor with one of these values:

  | Descriptor | Phase 3A meaning |
  | --- | --- |
  | `InProcess` | Safe text/metadata extraction, chunking, deterministic embedding and index publication run inside the application. |
  | `DeferredCapability` | OCR, media, GPU-dependent, advanced or otherwise unavailable work is retained as a durable deferred activity with a reason. |
  | `NativeExecutorLater` | A non-runnable design marker for a later adapter; it never starts, supervises, releases or activates an executor in Phase 3A. |

  The existing opaque executor/result boundary is the future seam for that
  marker. Phase 3A does not change GPU admission, callback contracts or
  executor activation.
- **Future approval-gated implementation:** Native process start/stop,
  supervision, PIDs, termination evidence, runtime/driver probes, GPU
  admission changes, executor activation, model/cache activation, external
  access and legacy work remain outside this checkpoint.

The process-management design is deliberately a separate future specification
and approval gate. It must not be folded into source ingestion merely because a
source activity may eventually need an executor.

The design uses these conceptual SQL records; exact table names and migration
shape are implementation-plan decisions, not an authorisation to change schema
in this checkpoint:

- `SourceRootConfiguration`: one durable local root and its revisioned policy.
- `SourceScanRequest`: an explicit operator or reconciliation request.
- `SourceRevision`: immutable source identity, revision and provenance.
- `SourceArtifact`: checksum-verified retained bytes and storage metadata.
- `SourceActivity`: one planned processing operation for one source revision.
- existing durable `Job` and `DispatchMessage`: execution and wake delivery,
  never an unrelated in-memory or broker-only queue.

### Source/root configuration contract

Each root records:

- display name and canonical absolute local path;
- enabled or paused state;
- recursive scan policy;
- include and exclude patterns, with the effective policy recorded;
- follow-links policy, off by default;
- maximum file size and allowed file-type/content-type policy;
- crawl/watch mode and reconciliation cadence;
- last scan start/end, matched/indexed/deferred/blocked counts;
- permission and health evidence, including the checked path identity;
- monotonically increasing configuration revision and audit evidence.

The recommended default is a local NTFS directory on a fixed drive, with
periodic reconciliation every 15 minutes and an optional watcher that only
coalesces wake hints. Manual scan remains available. A watcher event is never
the source of truth; reconciliation rereads the directory and SQL state.

Adding a root validates the path, permissions, reparse-point policy and target
store exclusions before the save. Both actions persist a `SourceScanRequest`,
the initial durable Job and its outbox record in one SQL transaction. **Save**
leaves that initial request durably held for an explicit later scan; **Save and
scan** releases it as runnable in the same committed operation. A restart
therefore resumes from committed state and cannot lose an in-memory setting or
scan request.

### Source preservation and classification

For every discovered revision, retain:

- immutable source bytes in an app-owned, checksum-verified,
  content-addressed source store outside the IIS deployment and SQL data roots;
- canonical path and source-root identity as provenance;
- stable source identity, revision identity and content hash;
- detected content type, extension, signature/classification and discovery
  metadata, including size and timestamps.

The original path is retained for reconciliation evidence but is not the only
copy on which later processing depends. If bytes cannot be retained or safely
reopened, the revision is recorded with an operator-visible blocked reason and
is not presented as searchable text. Classification uses magic/signature data
first, extension as a secondary hint and an explicit `unknown` result when the
signals disagree. Unknown binaries remain metadata-only/deferred; they are
never silently coerced into plain text.

The first usefulness slice supports UTF-8 text with or without BOM in `.txt`,
`.md`, `.markdown`, `.log`, `.csv`, `.tsv`, `.json`, `.xml`, `.yaml` and `.yml`
files up to a recommended 16 MiB. Code extensions are classified as `code`,
not as generic text, until a code processor is registered. PDF, image, audio,
video, archive and unsupported code files encountered before their processor is
available become explicit deferred activities with a reason.

Unseen files are suppressed from active retrieval after reconciliation while
their source identity, last revision, evidence and retention deadline remain
durable. Physical source/artifact cleanup is a separate aged-retention action;
it never deletes bytes still referenced by an active projection or an SQL
rebuild path.

### Durable activity planning and deferred capabilities

Discovery creates or reuses a source revision by stable identity and content
hash. An unchanged rescan creates no new revision or duplicate activity. A
changed file creates a new linked revision and a new plan; the prior revision
remains immutable and searchable until the policy explicitly suppresses it.

Each plan activity has a kind, processor version, input fingerprint, required
capability, source revision, parent activity, attempt evidence and one of these
explicit states:

Activity kinds include text extraction, metadata extraction, document parsing,
OCR, archive expansion, code parsing/symbol indexing, media transcription and
embedding/index publication. Phase 3A registers only the safe local text and
metadata activities; later phases add the other kinds without changing the
source-revision or idempotency contract.

| Activity state | Meaning and transition rule |
| --- | --- |
| `Pending` | Durable work is planned but not claimed. |
| `Running` | A matching Job claim owns the attempt; lease evidence is separate. |
| `Completed` | The processor committed its immutable projection and receipt. |
| `DeferredUnsupported` | No registered processor/capability can safely handle the input; no automatic retry loop. |
| `DeferredPolicy` | Root policy, size or permission policy intentionally prevents processing; the reason is operator-visible. |
| `FailedRetryable` | A bounded transient failure may be retried under normal durable Job rules. |
| `FailedTerminal` | Validation, corruption or a non-retryable processor failure requires operator action. |
| `CancelledSuperseded` | A newer source revision or explicit cancellation superseded the activity. |

The held/not-started state used by Save-only belongs to the scan request and
outbox delivery metadata; it is not an additional public Job or activity state.

The idempotency key is exactly equivalent to:

```text
(source_revision_id, activity_kind, processor_version, input_fingerprint)
```

Unsupported capability is deferred, never treated as completed text and never
retried forever. A capability registration records processor kind, version,
input classifications, output contract, processor fingerprint and readiness.
When a capability becomes available, reconciliation finds matching
`DeferredUnsupported` activities and enqueues each key exactly once. The new
projection is additive: it does not replace a valid canonical projection or
change the source revision. A local operator may request the same replay
explicitly; the idempotency key still fences duplicates.

### Phase 3A UI flow and wireframe-level design

The current Overview screenshot is evidence for two corrections: the Overview
must not expose full opaque generation IDs in summary cards, and card content
must not overflow. Full identifiers move to a detail/copy diagnostic field;
summary cards show **Healthy**, **Recovering** or **Blocked**, with responsive
wrapping and a short explanation.

Add a dedicated **Sources / Indexing** navigation item rather than placing root
configuration on Overview:

```text
Flux Knowledge   Overview   Pipeline records   Search   Sources / Indexing

Sources / Indexing                         [Add folder]
------------------------------------------------------------------------
Name        Path             State       Last scan       Indexed Deferred Errors
Knowledge   D:\Knowledge      Healthy     2026-08-06      142     3        0

When no roots exist:  No local folders are indexed yet.  [Add folder]
```

The Add folder flow is:

1. Choose a validated local path, display name, recursion and effective
   include/exclude rules.
2. Select the processing profile and review the permission/path validation.
3. Run a read-only preview showing matched files, unsupported types and
   planned activities.
4. Choose **Save** or **Save and scan** explicitly; Save holds the durable
   initial request without starting work, while Save and scan releases it.
5. On the root detail page, show scan progress, queued/indexed/deferred/
   blocked counts, reasons, last reconciliation and a local **Reprocess
   deferred content** action when a capability is registered.

Source configuration is an intentional local operator mutation surface. It
uses the existing antiforgery, validation, audit, cancellation and loopback
safeguards. It is separate from internal executor callback contracts and adds
no public executor mutation route.

### Phase 3A acceptance criteria

The implementation may proceed only when its focused tests and local evidence
prove all of the following:

1. A valid local root can be added through the Sources/Indexing UI.
2. Missing, inaccessible, non-local, unsafe or excluded-store paths are
   rejected before save, with a truthful reason.
3. Save-only creates the durable scan request, initial Job and outbox record in
   a held/not-started state; Save and scan releases that same request as
   runnable transactionally and starts no duplicate request.
4. Recursive scanning and include/exclude policy produce preview counts and
   match the persisted effective policy.
5. Permission evidence is captured in the preview, root health and audit data.
6. A UTF-8 text file becomes an immutable source revision, searchable chunk,
   vector/projection and hydrated result with root/path provenance.
7. PDF, image, media, archive, unknown and unsupported code inputs encountered
   before a processor exists become deferred/blocked activities, not bogus text.
8. Source bytes, revision identity, content hash and parent/root provenance are
   retained and reopenable for later processing.
9. An unchanged rescan is idempotent and creates neither a new revision nor
   duplicate activities.
10. A changed file creates a new revision without overwriting the old one.
11. An unseen/deleted file is suppressed from active retrieval according to a
   durable retention policy, with evidence preserved.
12. Restart during discovery, planning or processing reconciles from SQL and
   does not lose or duplicate scan work.
13. SQL-to-USearch rebuild uses durable SQL/source state and does not require
   the original watcher event or an in-memory plan.
14. A failed or partial candidate never becomes the active generation and does
   not expose partial search results.
15. Adding a processor later replays each matching deferred activity exactly
   once or idempotently and adds its projection without replacing valid data.
16. Activity state, scan counts, progress, deferred reasons and blocked reasons
   are truthful after refresh/reconnect.
17. The Overview no longer overflows opaque generation identifiers and keeps
   diagnostic identifiers out of summary cards.
18. No test, setup or operator action downloads/activates a model, admits GPU
   work, starts/stops or supervises a process, records or trusts a PID or
   termination signal, runs a runtime/driver probe, opens external access or
   touches legacy, RabbitMQ, Docker or Vespa components.
19. Only `InProcess`, `DeferredCapability` and non-runnable
    `NativeExecutorLater` descriptors can be written by the Phase 3A design;
    no descriptor can start a process, change GPU admission or activate an
    executor.

### Requirements-traceability impact

The resequencing changes delivery order, not requirements:

| Original requirement | Revised coverage |
| --- | --- |
| Local filesystem crawling, monitoring, source identity and provenance | Phase 3A source-root, revision and activity contract |
| Searchable local corpus and operator visibility | Phase 3A UTF-8 slice, Sources/Indexing UI and Overview correction |
| Documents, archives, image/OCR, video/audio and code | Phase 3B activity/capability expansion and Phase 5 branch completion |
| Full MCP and Codex plugin surface | Phase 3C 54-tool ledger, hosted MCP, hooks and readiness fixtures, scheduled after Phase 6 |
| REST and CLI compatibility | Phase 3C shared use cases and route/command ledger, scheduled after Phase 6 |
| Gmail and Outlook VSTO ingress | Phase 4 receipts, spool, provenance and restart evidence |
| Opaque executor/result boundary and execution class | Phase 2 boundary is implemented; Phase 3A writes descriptors only; a separate process-management checkpoint is required before any native adapter or admission change |
| Native model/inference adapters and caches | Phase 6, still explicitly approval-gated |
| SQL rebuild, backup/restore, local readiness and operator evidence | Phase 7 readiness gate, with Phase 1/2 rebuild invariants retained throughout |
| Legacy retention and retirement/cutover decision | Phase 7 only; no legacy capability is removed by Phase 3A |

### Unresolved decisions and recommended defaults

| Decision | Recommended default for Phase 3A | Reason / boundary |
| --- | --- | --- |
| App-owned source bytes or re-openable paths | Retain app-owned immutable bytes plus the original canonical path as provenance. | Later processors and SQL rebuilds remain possible after a path changes; failure to retain is visibly blocked. |
| Local path and symlink policy | Existing local NTFS directory, canonical absolute path, no UNC roots, no deployment/SQL/cache/secret roots, no reparse/link traversal by default. | Prevents escape from the operator-selected corpus and avoids ambiguous identity. |
| Type detection and unknown files | Magic/signature first, extension second, explicit `unknown` on disagreement. | Prevents binary data being indexed as invented text. |
| Watcher versus crawl | Periodic 15-minute reconciliation is authoritative; optional watcher events are coalesced hints; manual scan is always available. | Watcher loss cannot lose durable work or corrupt source truth. |
| Activity-plan versioning and capabilities | Immutable processor version/fingerprint in every activity key; SQL-visible capability registration; explicit local replay. | Enables exact-once deferred replay and reproducible provenance. |
| Local operator authorisation | Loopback plus Windows-authenticated/local operator policy, antiforgery, path/permission checks and append-only audit evidence. | Source configuration is a deliberate mutation, unlike executor callbacks. |
| First-slice types, size and quality | UTF-8 `.txt`, Markdown, logs and common structured text up to 16 MiB; code/PDF/image/media/archive are deferred until capable processors exist. A sentinel phrase must be retrievable in the top 10 with correct provenance; no broad relevance claim is made yet. | Establishes useful, deterministic evidence without activating advanced processors or models. |

This is the revised design checkpoint. The approved Phase 3A plan has produced
the documented code checkpoint, but it does not authorise or evidence schema
migration, deployment, IIS restart, local SQL/IIS validation, external access,
process management, model/GPU execution, advanced processor activation,
legacy/RabbitMQ/Docker/Vespa action or Phase 3C parity work. The root contract,
source preservation/activity engine and Sources/Indexing UI remain separately
testable batches; the outstanding vertical-slice proof is recorded in the
validation record.

Process-management design remains a separate future checkpoint. That checkpoint
must be approved independently before any process start/stop or supervision,
PID or termination evidence, runtime/driver probe, GPU-admission change or
executor activation is designed for implementation.

## Requirements traceability

| ID | Requirement family | Permanent design response | Delivery evidence |
| --- | --- | --- | --- |
| NW-01 | IIS-hosted ASP.NET Core and new Blazor UI | Modular monolith; one deployed Web host; Interactive Server | Phases 1 and 7 |
| NW-02 | Fixed SQL Server canonical store | Provisioning, SQL-owned files, no AttachDbFilename, backup-off-I health | Phases 1 and 7 |
| NW-03 | Derived/rebuildable USearch | Canonical SQL vectors, immutable generations, SQL pointer switch and bounded runtime recovery from SQL | Phases 1 and 2 |
| NW-04 | No forbidden target runtime components | In-process workers, SQL outbox and hosted integrations | Every phase |
| NW-05 | Preserve sources, extraction, code/search and visibility | Adapter and route-family model with staged delivery | Phases 3A through 6 |
| NW-06 | Preserve MCP and Codex plugin | 54-tool ledger, hosted MCP, hooks and installation/readiness parity | Phases 1 and 3C |
| NW-07 | Platform-neutral pipeline contract | Domain-level PipelineRecord, Job and DispatchMessage | Phase 1 |
| NW-08 | Exact Job-state semantics | Six states only; stage/due/lease separate | Phase 1 and UI tests |
| NW-09 | Durable atomic dispatch | SQL stage/outbox transaction, post-commit wake-up and leases | Phases 1 and 2 |
| NW-10 | GPU priority and batching | Durable mini-tasks, lanes, barriers and no forced interruption | Phase 2; adapters in 6 |
| NW-11 | Safe retrieval/index generation | Stable IDs, validation, hydration filtering and rebuild | Phases 1 and 2 |
| NW-12 | Model approval restriction | Deterministic first provider and explicit hashed-cache approval gate | Phases 1 and 6 |
| NW-13 | Outlook COM boundary | Signed STA add-in, atomic spool and idempotent authenticated ingress | Phase 4 |
| NW-14 | Blazor/SignalR operations | SQL projections, reconnect rehydration and truthful counts | Phases 1, 2, 3A and 7 |
| NW-15 | REST/CLI compatibility | Shared commands plus route/command ledger | Phase 3C |
| NW-16 | Required invariant proof | SQL integration, restart, snapshot, scheduler and UI matrix | Phases 1, 2, 3A and 7 |
| NW-17 | Legacy retention/no data migration | Legacy read-only until explicit retirement decision | Through Phase 7 |
| NW-18 | Approval-gated operations | Operational guardrails and final cutover gate | Through Phase 7 |

## Acceptance matrix

Local replacement readiness requires proof that:

1. A UTF-8 file creates a PipelineRecord, Job, outbox delivery, deterministic
   vectors, active USearch generation, hydrated search result and live indexed
   UI state.
2. Two workers cannot claim the same eligible Job or DispatchMessage.
3. Duplicate delivery does not duplicate artefacts or next Jobs.
4. A stale source revision cannot replace a newer record or hydrate in search.
5. An expired lease recovers by normal claim after crash/IIS recycle.
6. A failed candidate snapshot leaves the active snapshot live; SQL vectors
   rebuild an index from no snapshot. A missing or invalid active derived index
   after startup makes readiness unready until a validated SQL-based recovery
   succeeds, without requiring an IIS restart.
7. GPU mini-tasks obey lane order, compatible batching, FIFO and drain barriers
   without interrupting active work.
8. MCP kb.search/kb.brief preserve current temporary-unavailable and retry
   behaviour; later contract groups preserve their captured behaviours.
9. SignalR loss/reconnect cannot hide durable state because projections reload.
10. No test or setup activates/downloads a GPU model without explicit approval.

## Risks and decisions to retain

| Risk or decision | Treatment |
| --- | --- |
| Missing SQL service-SID ACL | Provisioning fails; app reports not-ready, never falls back to a user-owned database file |
| IIS idle shutdown | Deploy only with proper always-running/preload settings; startup outbox drain remains safety net |
| USearch native ABI/package mismatch | Isolate adapter; prove build/save/reopen in Phase 1 |
| Undocumented current MCP behaviour | Capture executable contract fixture before replacement of that tool group |
| Outlook VSTO signing/topology unavailable | Do not emulate COM in IIS; block the adapter phase until sanctioned path exists |
| Model/runtime choice | Explicit approval gate; deterministic provider keeps Phase 1 independent |
| Legacy data retention | No migration is designed; retention/retirement needs a later explicit decision |

## Review and approval gate

This documentation-only checkpoint fixes the usefulness-first sequence and the
Phase 3A durable boundaries while deliberately deferring code, schema,
extractor, model, process and deployment implementation. The Phase 2
executor/result boundary is recorded as complete; the stale roadmap wording has
been corrected; and all original Phase 3–7 requirements remain traceable.

Please review and approve or amend the revised phase table, Phase 3A source/root
contract, activity/deferred-work state model, operator UI flow, acceptance
criteria and recommended defaults above. No implementation plan or code change
should begin until this written design is explicitly approved.
