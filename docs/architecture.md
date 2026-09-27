# Architecture

## Application boundary

FluxKnowledge is a native Windows application for one trusted local user. One
IIS-hosted ASP.NET Core application provides Interactive Server Blazor, REST and
MCP at `http://127.0.0.1:5137`. A thin CLI calls the same native v1 facade. The
logged-in Windows companion handles approved Outlook and Visio operations that
require desktop COM. Runtime activation is explicit and capability-specific.

SQL Server is the authoritative store for knowledge, retained source revisions,
pipeline work, scheduler ownership, operation receipts, audit and publication
selection. SQL Full-Text and embedded USearch supply searchable projections.
USearch generations are immutable, fenced and rebuildable from canonical state.

## Project responsibilities

| Project | Responsibility |
| --- | --- |
| `FluxKnowledge.Domain` | Domain identities, value objects and invariants. |
| `FluxKnowledge.Application` | Use cases, source classification, retained processing, scheduling and port contracts. |
| `FluxKnowledge.Infrastructure.SqlServer` | EF Core schema/migrations, durable queues, leases, receipts and SQL retrieval. |
| `FluxKnowledge.Infrastructure.Usearch` | Generation construction, validation, publication and ANN reads. |
| `FluxKnowledge.Infrastructure.Inference` | Verified offline OCR execution and bounded result handling. |
| `FluxKnowledge.Integrations` | Windows storage/process boundaries, native installation and Codex integration. |
| `FluxKnowledge.Web` | Blazor operator UI, loopback endpoints, MCP and hosted pumps. |
| `FluxKnowledge.Cli` | Native clients and explicitly scoped diagnostics. |
| `FluxKnowledge.OutlookHost` | Logged-in desktop capture and interactive document operations. |
| `FluxKnowledge.NativeWorker.Protocol` | Native worker message contracts. |
| `FluxKnowledge.DeterministicWorker` | Deterministic worker used for protocol and supervision verification. |

## Storage

Production paths are fixed under `I:\FluxKnowledge`: `App`, `Config`, SQL data
and log files, `Data\Retained`, `Data\Index`, `Runtime\Spool`, `Runtime\Temp`,
`Runtime\Logs`, `CodexPlugin` and `Recovery`. Path guards reject traversal,
reparse points and ambiguous resolution. The independent model store is
`J:\Models`; application publication and rollback do not include model weights.

EF migrations and persisted capability/reason identifiers are part of the
native data contract. Preserve their identities when updating application code.
Already retained revisions and terminal receipts must remain interpretable.

## Durable processing

The observable path is source registration → discovery → retained revision →
durable work → extraction/normalisation → index generation → publication →
search and operator status. Source originals are read only by authorised ingress.
Downstream processors consume checksum-verified retained bytes bound to the
revision and processor capability.

SQL transactions establish work ownership before dispatch or presentation.
Leases, generations and operation receipts reject duplicate and stale completion.
Local wake signals and watcher events are hints; reconciliation rereads durable
state. Retained processor completion wakes source registration immediately, with
a periodic reconciliation fallback for missed signals.

ZIP and TAR processing enforce archive bounds and member path rules. Office Open
XML extraction produces one logical UTF-8 result for the original DOCX/XLSX/PPTX.
Document package members are not separately published corpus documents. Ordinary
retained UTF-8 is limited to 16 MiB; the bounded Office extracted-text result can
reach 200 MiB without changing package security limits.

The C# processor parses retained syntax without building or executing a project,
restoring packages or running source generators/analyzers. Its code facts,
relationships and diagnostics have durable completion and secret-disclosure
guards. Other code formats require their own supported capability.

## Source lifecycle

Local source roots must pass fixed-drive NTFS and root-overlap/path policies.
Registration uses preview/commit fencing and maintains stable revision identity.
Watcher-driven refresh is reconciled against retained state and configured
include/exclude rules.

Pause prevents new admission and publication without discarding completed work.
Resume makes eligible work available again. Delete is a durable operation with
visible progress: admission stops, in-flight work drains, retained projections
are excluded, owned records/files are removed and shared blobs or surviving
index members are preserved. Cleanup uses bounded, no-follow application paths.
Source originals and model-store files are never deletion targets.

An expired lease alone does not prove that a Visio process has stopped. Unknown
process cleanup retains the deletion fence. Failed terminal work is not silently
reset by replaying an idempotent command.

## Scheduling and native processes

The GPU scheduler is SQL-authoritative and uses strict priority, FIFO and
compatible-batch ordering. Capacity ownership, executor dispatch, acknowledgement,
result acceptance and settlement are fenced by durable identities. A result
cannot publish against a superseded revision, paused/deleting source or stale
ownership generation.

Native process supervision records lifecycle evidence. A process outcome that
cannot be proved does not create permission to repeat inference or release an
unsettled reservation. Shutdown cancels owned children; restart reconciliation
recovers from committed state. Model-free deterministic executors support tests.

## Offline model boundary and English OCR

The model gate resolves a bounded local manifest only against `J:\Models`, pins
ancestors and files with no-follow handles, verifies immutable identities,
SHA-256 and byte lengths, and holds read-only handles for the lease. Missing,
corrupt, offline, recall-required or redirected artifacts refuse execution.
Verification receipts remain in the model store. There is no downloader or
fallback cache in normal model loading.

Provisioned English OCR uses PaddleOCR-VL 1.6 with layout and page-orientation
companions in an offline Python worker. Model, GPU and OCR activation flags must
agree. This Python worker is an inference adapter within the native pipeline.
PDF pages with visible content and no native text, plus supported single-frame
JPEG/PNG inputs, pass through the existing durable GPU scheduler. Images are
bounded to 64 MiB, 25 megapixels and a 6,000-pixel edge.

The source-bound OCR result continues through normalisation, indexing and
publication with retained page/block provenance. Native-text pages preserve
their text. A page containing both native text and scanned regions is not
currently region-OCRed. Repeated-text fidelity remains imperfect; Arabic OCR is
not supported. The scoped corpus operations project retained page, image and
Visio locations; the older mixed `knowledge.search` response has no page fields.
Reprocessing a terminal failed revision does not provide a same-revision retry.
See the [practical assessment](operations/2026-09-20-english-ocr-practical-assessment.md)
and [scoped delivery evidence](operations/2026-09-20-english-ocr-live-delivery.md).

## Interactive Visio and Outlook

The logged-in companion selects prepared retained VSDX work and invokes Visio
outside IIS. It records ordered pages, shapes/groups, expanded text, shape data
and connections under the original document identity. It refuses an existing
user Visio session, opens retained bytes read-only with macros/events disabled
and refresh declined, and requires proven process cleanup before acceptance.
It does not claim OS-level network isolation or unattended Office support.

Capability changes create successor records; they preserve terminal predecessors
and the last good publication until a replacement publishes. Interactive Visio
[delivery evidence](operations/2026-09-20-interactive-visio-delivery.md) records
the current scope and limitations.

Outlook capture uses a separately provisioned logged-in COM host. Its schedules,
leases, spool manifests and captured revisions use the native durable stores.
Normal application startup does not activate Outlook or change desktop tasks.
Credentials, mail contents and spool data are private runtime material.

## Retrieval and integration surfaces

Current search combines eligible retained corpus text with knowledge records.
SQL Full-Text and USearch serve the native retrieval path; the registered
embedding provider is a deterministic token-hash baseline. It is not a learned
semantic model. SQL publication eligibility controls which revision can appear.

The eleven native tools, their REST routes, envelopes, cursors and CLI verbs are
specified in [integrations](integrations.md). Mutations require a preview-bound
confirmation and an idempotency key. Direct-loopback checks refuse forwarding,
proxies and redirects; public responses are bounded and secret-filtered.

[Scoped corpus search and cited passage reading](design/corpus-retrieval.md)
provide deployed published-text lexical search and bounded cited reads through
MCP, REST and CLI. The one-time SQL chunk Full-Text index is active. No learned
provider is active; the BGE-M3 ONNX pilot verified offline execution but failed
its frozen relevance gates.

The proposed [hybrid passage architecture](design/hybrid-passage-retrieval.md)
and [implementation plan](design/hybrid-passage-retrieval-plan.md), dated
25 September 2026, supersede the earlier semantic transition plan. They retain
SQL Server, Full-Text and USearch, use coherent shared passages, learned
embeddings and a cross-encoder reranker, and share source ranking across the
search adapters. English is the initial acceptance scope. Existing indexed text
is disposable: adoption uses a controlled clean rebuild with a new evidence
epoch, without an old-passage or concurrent-model migration system.

The proposed first pair is BGE-M3 and BGE-reranker-v2-m3, with GPU execution
through the existing scheduler and optional validated CPU placement. Extend
interactive ownership and runtime admission while retaining batch ownership,
trusted release and recovery. The proposed baseline unloads models after each
request/batch, and guarantees an OCR turn after at most three new non-OCR
retrieval/embedding admissions while OCR waits, without interrupting an active
page. Identify OCR by validated runtime identity and preserve existing lanes;
document OCR currently shares `DocumentIndexing` with other work. Keeping models
loaded between requests is a separate enhancement requiring measured loading,
memory and full-search benefit before retention-specific scheduler changes.
The existing OCR-only gate does not already provide search admission.
GPU parity, local relevance, concurrency/recovery and latency gates
remain implementation work. Current source publication, disclosure and citation
safeguards are retained. No OCR or extractor upgrade is implied.

The implementation branch now has a locally verified complete-passage path from
synthetic ingress through SQL/USearch publication to REST search and citation
reading, including separate context headers and evidence v2 corpus epochs.
The shared scheduler core also supports exclusive interactive ownership,
bounded admission and the durable OCR turn exception. Both slices passed
independent review. They are unreleased: production composition has not enabled
the new passage options, request-owner adapter or workload policy. GPU adapter and
runtime wiring, coherent generation/reset leases and the unified hybrid
engine remain required before activation. See the implementation plan for the
focused verification evidence and remaining milestone gates.

The pinned reranker source and a float32 ONNX export are now verified in
`J:\Models`. An isolated CPU conversion runtime reused cached packages with no
additional downloads; its numeric reference checks passed. This is model
preparation, not native provider activation or a semantic-search accuracy claim.
The [conversion record](design/bge-reranker-offline-conversion.md) states its scope
and remaining native/GPU checks.

The [native CPU adapter record](design/bge-native-cpu-adapters.md) now establishes
offline .NET tokenizer and numeric parity for both fixed models, including the
512-token boundary and verified-file/native-library lifetimes. Models are loaded
from explicit protected bundle paths in the canonical store, without acquisition.
The branch also has an unreleased shared admission gate using the existing OCR
physical slot and an exact-owner/dispatch/slot execution read. These do not yet
constitute GPU execution, full-search accuracy or latency
acceptance, and are not registered in production.

Interactive ownership now records an opaque Windows machine fingerprint, PID and
process start time. Independently reviewed recovery proves the exact local process
incarnation has exited before reconciling capacity; it never replays private search
work. Live, inaccessible and foreign-machine owners retain ownership. The local
request executor keeps at most two private callbacks in memory, uses existing
dispatch/receipt/callback primitives, and separates caller cancellation from native
cleanup. Before native execution starts, cancellation can reconcile an undelivered
uncertain admission using its single-owner no-start proof. Confirmed cleanup can
also reconcile watchdog uncertainty. Failed cleanup retains capacity. This adapter
passed independent review with 26 focused ownership/executor tests. It remains
unregistered pending native GPU integration and shared-slot runtime validation.

The native GPU factories now require an active executor-owned context bound to
the pinned BGE runtime/settings profile. The context expires before capacity
settlement; model sessions retain verified local-file leases and dispose before
those leases are released. DirectML uses sequential execution with memory patterns
disabled. Guard checks and the cached CPU reference regression pass; actual GPU
execution, memory, parity and full-search latency remain unverified.

Publication preview, activation, lexical search/read and deletion survivor
selection now share one SQL publication rule in the unreleased branch. Pending
unrooted revisions preserve the last completed publication. Preview applies the
existing document winner selection inside a rolled-back transaction, including
SQL retry support. A changed snapshot rolls back publication and pointer updates;
the worker rebuilds at most three times, then durably requeues with five-second
backoff. A deleting root is immediately excluded, while another deletion's owned
canonical rows remain intact. An absent search projection does not establish an
empty canonical catalogue. Local publication now captures the corpus epoch and
membership version in immutable SQL/file metadata; new activations require both
expected and resulting stamps. Suppression, restoration and the initial deleting
transition invalidate older candidates. Committed transition replay remains
idempotent after a later publication.

The unreleased query path captures one stamped profile/generation before embedding.
A shared SQL session lock and durable process-incarnation record protect its
membership, vectors and derived files. Native ANN disposal precedes explicit SQL
lease release. Losing a SQL session refuses results but retains ownership: cleanup
requires explicit release or verified owner-process exit. Live or unknown owners
block purge, recovery and generation-file deletion. The shared hybrid engine
retains this lease through final hydration and rechecks eligibility/currentness.
Recovery now reports a stale stamped projection or a recognised pending draft as
`IndexUpdating`, without attempting to repair superseded files or declaring the
canonical catalogue empty. An unknown absent pointer still fails validation.

The unreleased embedding path accepts batches of at most four passages. SQL stores
an unplaced draft owned by the existing Embed job and saves exact search-input and
payload hashes. Each delivery performs one batch and requeues through the existing
fenced retry transition; reclaim reuses completed vectors. Final sealing rechecks
the epoch, ownership, complete membership and payload integrity before the atomic
Embed-to-Publish transition. Checksum validation reads bounded keyset pages, including
when SQL retry buffering is enabled. Deletion withdraws its own pending draft in the
first durable phase, then captures cleanup and releases its job reference in the
second phase. BGE batch and synthetic disposable-SQL pipeline tests cover this path;
the opt-in model/scheduler composition is implemented locally. Rebuild maintenance and
reset/drain remain milestone 3 work before semantic activation.

The unreleased foreground BGE wrapper now loads, runs and unloads embedding before
opening the reranker. Native allocations register against the acknowledged owner;
closing that owner atomically prevents later allocations. A constructor or disposal
failure retains capacity until independent release evidence exists. Model-free
tests run this wrapper through actual disposable-SQL dispatch and settlement.

Opt-in `FluxKnowledge-HybridSearch` EventPipe diagnostics report actual candidate,
shortlist and result IDs, search status/time, HTTP trace/span-to-native-batch identity,
and model load/inference/unload duration and outcome. They emit no queries,
passages, paths or vectors and retain the existing ownership and release rules.
These measurements support the English acceptance gates and any later decision
about model residency; native memory still requires an independent measurement.

Background embedding can now persist one request containing at most four passage
IDs and input hashes, then hand it to the existing parent-job GPU lifecycle. The
ordinary Embed worker uses this profile without running inference under its worker
lease. GPU checkpoint writes use a separately acknowledged, process-bound dispatch
and atomically save vectors and a completion digest. A stable claim-operation ID
recovers a lost claim response; native execution still requires adapter single-flight
delivery. The selector admits one such request at a time. Queued and processing
drafts remain recognised as updating projections. Completion alone does not prove
GPU release. The background adapter now joins checkpoint, confirmed cleanup,
existing lifecycle settlement and ordinary Embed requeue. Response-loss, disposal
uncertainty, process recovery and source withdrawal checks cover this path.

`Search:HybridPassagesEnabled` selects the unreleased shared runtime only when the
complete existing local OCR/GPU runtime is enabled. It retains the existing physical
slot, durable recovery loop and OCR turn policy. Singleton adapters call scoped
lifecycle services through fresh scopes. Model stores use pinned verified paths
under `J:\Models`; DI construction loads no weights. Each batch/request unloads its
embedding session before any reranker session opens. The flag remains disabled
pending maintenance, real GPU measurements and relevance acceptance.

The unreleased controlled rebuild captures canonical inputs and existing publication
bindings in an immutable operation manifest. SQL reset preserves extraction and
completion history, advances the corpus epoch and records a durable worklist. Each
item prepares coherent passages outside its commit transaction, then queues one
exactly bound Embed job. Claims admit only that worklist's Embed and subsequent
Publish while ordinary intake waits. A paused source remains paused and retains its
published document winner, branch and timestamp. The existing scheduler can admit
its bound embedding work without changing OCR ownership or turn limits.

Search and evidence reads report rebuilding until worklist completion, matching
model and corpus stamp, exact vector membership, native USearch validation and
current Full-Text population are proved. Empty validation requires no vectors,
generations or memberships. Reset and finalisation use the same unpooled SQL session
for admission, recovery and publication fences; loss of that session cannot continue
through another connection.

The trusted-local `corpus-rebuild` CLI exposes plan, commit, prepare, status and
finish. The incremental IIS updater joins these operations with an owner-bound
deployment hold, the existing scheduler's admission fence and proof of worker
exit. While a rebuild is active, outbox, job and GPU admissions require the exact
operation permit, including when the hold file is absent. Source intake remains
held. An active OCR page finishes normally before the application stops.

The updater records its forward recovery boundary before the first schema command.
After that boundary, failure retains the hold, schema, payload and configuration;
resume verifies the same release, database, operation and file hashes. A stopped
compatible candidate can run existing dead-owner recovery under deny-all admission
before draining again. Foreign holds and journal/schema disagreement refuse without
service changes. Disposable SQL checks exercise actual idempotent migration/replay,
session loss and exited-owner recovery. Canonical GPU measurements, English quality,
deployment and live validation remain pending.

The shared query engine retrieves at most 100 lexical and 100 dense passage IDs,
fuses by identity, reranks at most 50 and revalidates complete bodies/citations.
Root/workspace dense retrieval scores the entire eligible captured scope up to
10,000 vectors; larger scopes explicitly refuse semantic retrieval. The ten-second
budget includes scope resolution, SQL, models and final hydration. Timeout stops
caller waiting while late native work retains its leases until confirmed cleanup.
Search scores are reciprocal returned positions, not calibrated probabilities.
Corpus and useful existing-search fallback results expose degradation; an empty
degraded search response refuses explicitly. The knowledge list cannot carry response
metadata, so degraded source retrieval produces the same explicit refusal instead
of an apparently complete notes/source union. Healthy notes/claims interleaving is
unchanged; its source shortlist remains bounded at 50 even for a knowledge limit of 100.

## Operations and verification

The operator UI projects committed source, job, scheduler, corpus and audit
state. Live events trigger refresh; they do not replace durable evidence.
The [operator guide](user-guide/dashboard-user-manual.md) describes the current
pages and [safety policy](safety.md) defines disclosure and storage limits.

Routine IIS changes use the incremental updater with an inspected plan, explicit
approval, retained-state validation and payload rollback. Clean-slate installation
uses a separate guarded one-shot GoLive path with independent clean-slate, VSS,
SQL destruction and native Codex registration acknowledgements. The GoLive path
does not offer automatic recovery or resume after an interrupted destructive run.
See [setup](setup.md) for the supported workflow and verification commands.
