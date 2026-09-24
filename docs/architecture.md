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
MCP, REST and CLI. The one-time SQL chunk Full-Text index is active. The follow-on
design evaluates local embeddings with explicit model/index binding and
recoverable generation transitions; BGE-M3 ONNX is being evaluated offline
under separate acquisition approval, with no learned provider active. The
design was aligned with main `52742d9` on 2026-09-23:
it consumes selected internal document/metadata publications, current
PDF/image/Visio provenance and bounded reads of large Office results. It does
not require another OCR merge or add same-page region OCR, typed table-cell
extraction or an OCR retry facility. The planned embedding transition must
distinguish published search eligibility from broader stored vector membership.

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
