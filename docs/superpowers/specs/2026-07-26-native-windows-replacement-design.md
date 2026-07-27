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

This is a full target design and traceability baseline. Only the first vertical
slice will receive an implementation-level plan after this specification is
reviewed.

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
| 2 | Pipeline durability, continuous derived-index recovery, scheduler, rebuild and full job/read projections | Atomic claim, duplicate, lease, runtime recovery, snapshot, rebuild and strict-priority evidence |
| 3 | Full MCP/plugin, REST and CLI parity in bounded contract groups | Tool/schema/error/hook/readiness contract evidence |
| 4 | Filesystem, Gmail and Outlook ingestion | Restart-safe ingress, receipts, spool, provenance and operator evidence |
| 5 | Document, archive, image, video/audio and code branches | Parent/child provenance and branch-completion evidence |
| 6 | Explicitly authorised native model adapters/cache | Per-model approval, native-runtime and scheduler evidence |
| 7 | Local replacement readiness and legacy retirement decision | SQL rebuild, backup/restore, end-to-end surface evidence and explicit cutover approval |

The order does not waive requirements. No legacy capability is removed until its
replacement passes local verification.

## Requirements traceability

| ID | Requirement family | Permanent design response | Delivery evidence |
| --- | --- | --- | --- |
| NW-01 | IIS-hosted ASP.NET Core and new Blazor UI | Modular monolith; one deployed Web host; Interactive Server | Phases 1 and 7 |
| NW-02 | Fixed SQL Server canonical store | Provisioning, SQL-owned files, no AttachDbFilename, backup-off-I health | Phases 1 and 7 |
| NW-03 | Derived/rebuildable USearch | Canonical SQL vectors, immutable generations, SQL pointer switch and bounded runtime recovery from SQL | Phases 1 and 2 |
| NW-04 | No forbidden target runtime components | In-process workers, SQL outbox and hosted integrations | Every phase |
| NW-05 | Preserve sources, extraction, code/search and visibility | Adapter and route-family model with staged delivery | Phases 3 through 6 |
| NW-06 | Preserve MCP and Codex plugin | 54-tool ledger, hosted MCP, hooks and installation/readiness parity | Phases 1 and 3 |
| NW-07 | Platform-neutral pipeline contract | Domain-level PipelineRecord, Job and DispatchMessage | Phase 1 |
| NW-08 | Exact Job-state semantics | Six states only; stage/due/lease separate | Phase 1 and UI tests |
| NW-09 | Durable atomic dispatch | SQL stage/outbox transaction, post-commit wake-up and leases | Phases 1 and 2 |
| NW-10 | GPU priority and batching | Durable mini-tasks, lanes, barriers and no forced interruption | Phase 2; adapters in 6 |
| NW-11 | Safe retrieval/index generation | Stable IDs, validation, hydration filtering and rebuild | Phases 1 and 2 |
| NW-12 | Model approval restriction | Deterministic first provider and explicit hashed-cache approval gate | Phases 1 and 6 |
| NW-13 | Outlook COM boundary | Signed STA add-in, atomic spool and idempotent authenticated ingress | Phase 4 |
| NW-14 | Blazor/SignalR operations | SQL projections, reconnect rehydration and truthful counts | Phases 1, 2 and 7 |
| NW-15 | REST/CLI compatibility | Shared commands plus route/command ledger | Phase 3 |
| NW-16 | Required invariant proof | SQL integration, restart, snapshot, scheduler and UI matrix | Phases 1, 2 and 7 |
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

## Review request

This document fixes durable boundaries now and deliberately defers extractor,
model and screen implementation detail that needs later evidence. After review,
the next document is a detailed Phase 1 implementation plan only.
