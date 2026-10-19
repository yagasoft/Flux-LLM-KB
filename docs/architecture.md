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
retained UTF-8 is limited to 32 MiB; the bounded Office extracted-text result can
reach 200 MiB. OOXML v4 permits 128 MiB XML parts and streams XML without an
element-count ceiling; the separately versioned Visio parser keeps its 64 MiB
part and two-million-element budgets. Total XML expansion remains 512 MiB with a
finite depth limit; compression ratios apply to XML opened for extraction, not unopened
binary media. ZIP expansion permits two nested ZIP levels with one cumulative
entry and expansion budget. Media metadata uses a checksum-verified retained
stream up to 1 GiB, with a separate 96 MiB parser-read budget; it does not decode
or publish the media payload.

OOXML v4 resolves extension-default content types, package-rooted internal
relationships and valid empty directory markers without following external
resources or accepting package escapes. Worksheet inline strings and DrawingML
slide text contribute to the retained text. Versions 1–3 remain recognised for
existing child registration, publication and exact policy lineage; registration
retires their runnable capabilities. Terminal predecessors require explicit,
exactly bound policy recovery rather than automatic replay.

The C# processor parses retained syntax without building or executing a project,
restoring packages or running source generators/analyzers. Its code facts,
relationships and diagnostics have durable completion and secret-disclosure
guards. Opted-in Git sources also publish strict UTF-8 source text through a
separate, fingerprinted text activity; only that admitted route can coexist
with the C# branch. Other languages have searchable source text for the
supported extensions, without implied symbol/reference analysis.
Long C# display signatures and references are shortened with the full-value hash
after secret scanning. Input, syntax, qualified-name and cumulative fact-text
bounds remain enforced.

## Source lifecycle

Sources explicitly added by the local user are trusted content. The delivery
priority is to process legitimate files by default, replacing unnecessary format,
encoding, size, count, nesting and classification refusals with suitable streaming,
batching or resumable processing. Current processor limits below describe the
implemented runtime, rather than permanent product requirements. Physical scope,
protected paths, integrity and truthful outcomes remain necessary.

Repository sources use `CrawlMode=1` (`git-tracked`) and one configured root.
Local, helper-disabled `git ls-files` supplies membership; retained bytes come
from no-follow reads of tracked working files. Private untracked content,
private/runtime/build paths and model payload locations remain excluded.
Authored `src/**/Models/*.cs` is code, not a model payload directory. Watcher
hints include validated linked-worktree control paths; periodic rescans handle
missed hints and changes in tracking status. Complete, stable inventory evidence
and the current serialised root scan lease are required for unseen suppression.
Every Git revision convergence and scan completion also fences configuration
and the owned lease. Active scans renew that lease; renewal loss cancels work.
Older binaries ignore this mode: quiesce workers and pause/disable Git roots,
drain/fence their claims, then roll back; keep those roots paused until compatible
code returns. Existing filesystem sources retain mode zero.

Local source roots must pass fixed-drive NTFS and root-overlap/path policies.
Registration uses preview/commit fencing and maintains stable revision identity.
Watcher-driven refresh is reconciled against retained state and configured
include/exclude rules.

New roots default to 32 MiB strict UTF-8 text and inert source-text indexing.
Explicit root restrictions remain authoritative. The additional text extensions
are RDL, INI, HTML, NDJSON, VTT and URL; content is indexed as text and never
executed or followed. Applying revised defaults to an existing root is explicit.
Previously retained policy refusals are re-evaluated one exact item at a time
through `policy_reprocess`, with a current root revision, unique unsuppressed
physical binding and checksum proof. Immutable source classification, original
terminal attempts and the last good publication stay intact. Current status
follows the versioned successor while history remains available.
Configuration drift blocks unexecuted successors visibly; only never-executed,
unlinked work can receive a newly verified admission proof. Changed processors
use new capability identities and retain exact previous read compatibility. During
the ordinary incremental deployment hold, readiness rejects outstanding previous
runnable work before activation. See [the policy delivery record](operations/2026-10-16-default-source-policy.md).

Filesystem sources admit the Windows Cloud Files reparse-tag family alongside
ordinary local folders. Parent-relative, no-follow/no-recall handles preserve the
root identity and physical scope through discovery and retention. Symbolic links,
junctions and unknown reparse types remain rejected for source-root scans; retained
storage and Git control paths retain their stricter policies. Direct UTF-8 ingress
still permits aliases whose resolved physical targets remain authorised.

An owned filesystem scan can resolve a positively observed file's older physical
bindings even when unrelated entries make its inventory incomplete. The hashing
read handle stays open through convergence; root admission, scope, scan ownership,
physical identity and metadata are checked again before commit. Only a verified
single-link capture under a no-follow root can suppress conflicting older
physical observations. Unavailable siblings are never treated as deleted, and
retained bytes or persisted discovery JSON alone cannot authorise suppression.
Ordinary files under an existing `FollowLinks` setting still ingest through the
same strict no-follow reader, without this additional suppression authority.
Same-content replacements with a different physical identity remain unresolved
rather than relabelling immutable history. The handle prevents writes and renames;
hard-link and access-control changes are checked but are not atomically excluded
with the SQL commit.

Physical revisions may retain a history parent. Retained policy admission and
exact settled-document recovery use origin and unique current binding to identify
the physical owner, rather than requiring its history parent to be empty. Derived
inputs remain ineligible as physical owners. Later physical revisions require the
marked, fenced document-recovery contract at exact claim; the earlier reprocess
contract is unchanged.

Discovery requests only materialised directory entries. Offline, recall-required
or partial content is reported unavailable without requesting hydration; an
incomplete cloud namespace or a retention failure prevents unseen-source
suppression. Read-only Preview samples at most 256 entries, 16 candidate files and
16 MiB of content, with a five-second cooperative deadline. Counts describe that
sample when a limit, unavailable item or incomplete namespace is reported. These
controls do not promise zero provider metadata/network activity or a hard deadline
for Windows filesystem calls. Source originals and sync-client settings are not
modified.

Pause prevents new admission and publication without discarding completed work.
Resume makes eligible work available again. Delete is a durable operation with
visible progress: admission stops, in-flight work drains, retained projections
are excluded, owned records/files are removed and shared blobs or surviving
index members are preserved. Cleanup uses bounded, no-follow application paths.
Source originals and model-store files are never deletion targets.

Deletion commits survivor activation and exact cleanup targets before removing
historical memberships. `cleanup-memberships` removes at most 1,000 rows for one
owned scalar vector ID per transaction; the cursor advances only after that
vector is empty. Committed row counts, the last committed lease token and the
resume phase survive cancellation, ambiguous commit acknowledgement and restart.
`purge-records` refuses late memberships before removing the owned canonical graph.
The existing 15-minute claim expiry remains the upper wait for recovery of an
uncommitted claim whose process disappeared; expiry never proves native cleanup.

In `cleanup-files`, pending-target reads are read-only. The coordinator acquires
the existing generation-query or content-hash publication gate, rereads authority
and immutable target metadata, then holds the gate through native IO. An already
started old worker can finish that exact idempotent IO after takeover, but its
stale SQL receipt cannot change the new owner's operation. Shared blobs remain
captured targets until a gated preservation/removal receipt decides their outcome.
Attention retains its exact resume phase and safe reason; the UI shows committed
membership counts and last progress without estimating a completion percentage.

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
The installed hybrid passage runtime combines SQL Full-Text, learned BGE
embeddings, USearch and a cross-encoder reranker for source retrieval. The
deterministic token-hash provider remains the non-hybrid baseline. SQL
publication eligibility controls which revision can appear.

The eleven native tools, their REST routes, envelopes, cursors and CLI verbs are
specified in [integrations](integrations.md). Mutations require a preview-bound
confirmation and an idempotency key. Direct-loopback checks refuse forwarding,
proxies and redirects; public responses are bounded and secret-filtered.

[Scoped corpus search and cited passage reading](design/corpus-retrieval.md)
provide published-text search and bounded cited reads through MCP, REST and CLI.
SQL Full-Text remains the lexical index. The learned BGE passage path is active
in the installed app and has passed the scoped English staging acceptance
recorded below. The earlier BGE-M3 ONNX pilot evaluated different windows and
failed its own relevance gates; it is not the delivered passage pipeline's result.

The deployed initial [repository retrieval corrections](design/repository-retrieval-reliability.md)
add optional, versioned canonical C# disclosure proofs in two derived SQL tables.
New indexing writes proofs atomically; a model-free worker backfills retained
artifacts under the deployment hold. Credential, parser and transport protections
remain. Root/workspace semantic search scans exact SQL membership in 256-vector
keyset pages under its generation lease and retains at most 100 candidates,
without a total-vector cap or opening global ANN. All-corpus search still uses
USearch. Release `a248811a` applied the additive migration and automatic proof
backfill. Correction release `4f9393ef` deployed the whole-file disclosure and
initial scoped query-plan changes; exact current code/docs reads pass. Serial-page
release `404a38b5` passed all 40 frozen semantic searches with p95 16.2 seconds.
Scoped SQL now uses constant serial ordered membership seek/loop hints without changing
eligibility or deadlines. Full guards retain credential checks and proof spans;
Protected refusal applies to returned text, and completed bounded code candidates
are not enlarged by unrelated trailing guard text.

Automatic repository discovery also requires the IIS Web process to stay available
without user requests. A normal idle shutdown exposed the OnDemand/20-minute/
preload-disabled gap. The incremental `-EnableUnattendedDiscovery` path
scopes AlwaysRunning, zero idle timeout and preload to FluxKnowledge, with atomic
settings, exact rollback and existing admission/drain controls. Release `29b420ba`
activated this correction. On 12 October the same worker survived 1,833.76 seconds
of idle time and converged tracked additions, edits, renames and deletions during a
2,313.30-second window with no HTTP requests, including the log-flush interval.
Current citations, stale-citation refusal and fixture withdrawal passed.

The delivered [hybrid passage architecture](design/hybrid-passage-retrieval.md)
and [implementation plan](design/hybrid-passage-retrieval-plan.md), dated
25 September 2026, supersede the earlier semantic transition plan. They retain
SQL Server, Full-Text and USearch, use coherent shared passages, learned
embeddings and a cross-encoder reranker, and share source ranking across the
search adapters. English is the initial acceptance scope. Existing indexed text
is disposable: adoption uses a controlled clean rebuild with a new evidence
epoch, without an old-passage or concurrent-model migration system.

The selected pair is BGE-M3 and BGE-reranker-v2-m3, with GPU execution
through the existing scheduler and optional validated CPU placement. Interactive
ownership and runtime admission retain batch ownership, trusted release and
recovery. The GPU path unloads models after each request/batch, and the
scheduler guarantees an OCR turn after at most three new non-OCR
retrieval/embedding admissions while OCR waits, without interrupting an active
page. Identify OCR by validated runtime identity and preserve existing lanes;
document OCR currently shares `DocumentIndexing` with other work. Keeping models
loaded on the GPU between requests is a separate enhancement requiring measured
loading, memory and full-search benefit before retention-specific scheduler
changes. The CPU fallback keeps verified model sessions resident in RAM.
The shared gate provides search admission. GPU numeric parity, English
relevance, live OCR fairness and the measured two-caller latency gate passed
staging acceptance. Source publication, disclosure and citation safeguards
are retained. No OCR or extractor upgrade is implied.

The GPU-first search runtime makes its placement decision inside the
existing serialised scheduler admission transaction. An idle GPU receives the
usual durable, owner-bound request. If queued work or reserved/uncertain GPU
capacity makes it busy, the transaction proves that no search request was
created before the caller enters CPU inference. Ambiguous SQL or GPU delivery
failures never trigger CPU. CPU inference uses two resident lanes, each with
one embedding session and four parallel reranking sessions, loaded only from
verified local model files. A lane stays owned until the complete search and
all native shards settle, including after caller cancellation. A protected,
stable Runtime file grants just one IIS worker ownership of this pool across
overlapping recycles; a waiting worker remains GPU-capable. The outer search
deadline is 25 seconds for either placement, with a 20-second BGE GPU
execution limit. The scheduler serialises state-changing SQL transactions with
a transaction-scoped mutation lock, acquired after the admission lock where
that lock is required. This prevents wake acknowledgements and interactive
handoffs from deadlocking on the scheduler state row. GPU model residency
remains a separate uncommitted enhancement. The final two-caller 96-question
staging run returned 96/96 trained, ready searches at 16.46-second full REST
p95, with 49 GPU tasks and 47 CPU fallbacks. An OCR page arriving during a
GPU search waited for that active batch, then ran without interruption before
new search admissions.

Strict IIS startup shares one cancellable, read-only probe of the configured SQL
database before background database work begins. Only classified availability
failures retry with backoff; worker operations and transactions are not replayed
by this gate. The listener can start while readiness remains 503. Initial index
validation runs in the existing recovery service and must still succeed before
readiness becomes healthy. The incremental updater waits for readiness HTTP 200
within its existing deadline, retrying only readiness HTTP 503 during startup.
Capacity bootstrap preserves existing reservations
and accepts a concurrent insert only after a fresh read proves the exact slot;
scheduler admission retains its transactional capacity and ownership checks.
The desktop companion returns a failed exit code for classified SQL unavailability
at its read-only Visio discovery boundary, before COM or durable work, leaving
the existing scheduled task responsible for its next activation.

CPU shutdown observes native cleanup after the host's waiting deadline. A failed
close during warmup or partial session construction retains ownership and verified
file leases, and cannot be reported as completed cleanup. Cancellation callback
failures do not bypass draining. Process and warmup identifiers distinguish
overlapping workers in these logs. These safeguards do not establish the origin
or prevention of the separately observed native IIS heap failure.

The final English holdout covered 24 sources, with strict top-five answer
support for 80/84 answerable questions and exact reads for all 480 cited
passages. Acceptance used a full REST p95 target of at most 20 seconds under
two callers, within the 25-second request deadline. The 12-search OCR-overlap
probe passed at 19.30-second p95; OCR waited 7.06 seconds for an active GPU
batch, then completed without interruption. These are measured staging results,
not guarantees for sustained OCR, cold CPU warmup, higher concurrency or other
languages. Negative controls returned related passages; answer abstention is
not an accepted capability. The
[live acceptance record](operations/2026-09-28-hybrid-search-live-acceptance.md)
retains the failed intermediate runs and final evidence.

The implementation has a locally verified complete-passage path from
synthetic ingress through SQL/USearch publication to REST search and citation
reading, including separate context headers and evidence v2 corpus epochs.
The shared scheduler core also supports exclusive interactive ownership,
bounded admission and the durable OCR turn exception. Both slices passed
independent review. The combined engine, native adapter and rebuild flow are
deployed. The corrected 35-input rebuild completed with 3,596 passages and the
validation hold was released after healthy startup recovery. Background GPU
embeddings executed with confirmed cleanup. Earlier separator-only passages
caused a terminal embedding failure; the corrected replacement is documented in
the [recovery record](operations/2026-09-27-hybrid-rebuild-recovery.md). Live
search was intermittently hybrid and sometimes reached its former ten-second
deadline. The corrected GPU-first/CPU-fallback path and its subsequent
acceptance evidence are recorded in the
[live acceptance record](operations/2026-09-28-hybrid-search-live-acceptance.md).

The pinned reranker source and a float32 ONNX export are now verified in
`J:\Models`. An isolated CPU conversion runtime reused cached packages with no
additional downloads; its numeric reference checks passed. This is model
preparation, not a semantic-search accuracy claim.
The [conversion record](design/bge-reranker-offline-conversion.md) states the
original conversion scope; the later integrated staging results are recorded
in the live acceptance record above.

The [native CPU adapter record](design/bge-native-cpu-adapters.md) now establishes
offline .NET tokenizer and numeric parity for both fixed models, including the
512-token boundary and verified-file/native-library lifetimes. Models are loaded
from explicit protected bundle paths in the canonical store, without acquisition.
The shared admission gate uses the existing OCR physical slot and an exact-owner/
dispatch/slot execution read. It is registered in the installed app, where
background GPU execution and the scoped full-search quality and latency gates
have passed staging validation.

A held 35-input rebuild reached 28 completed inputs before native process memory
growth required a pause. Isolated tokenizer-only cycles identified repeated native
tokenizer DLL unload as the main source of that growth. The local correction pins
only the verified tokenizer runtime DLL and its protected file lease for the IIS
process lifetime; each tokenizer and ONNX model session retains per-request
disposal. The corrected payload finished the held rebuild and passed startup
readiness after an exact-release restart.

Further tokenizer-only isolation reproduced native arena retention when complete
tokenizer lifecycles ran on departing OS threads, despite that process pin,
successful disposal and managed garbage collection. The native tokenizer now has
one permanent background thread for initialisation, encoding, cleanup and native
library calls. A bounded queue applies backpressure; operation failures return to
their caller without replacing the owner. Per-tokenizer locking and verified file
leases remain in force until accepted native work finishes, including during
cancellation and shutdown. The [memory incident record](operations/2026-10-17-tokenizer-native-memory.md)
distinguishes the reproduced mechanism from other unresolved native crash risks.

Interactive ownership now records an opaque Windows machine fingerprint, PID and
process start time. Independently reviewed recovery proves the exact local process
incarnation has exited before reconciling capacity; it never replays private search
work. Live, inaccessible and foreign-machine owners retain ownership. The local
request executor keeps at most two private callbacks in memory, uses existing
dispatch/receipt/callback primitives, and separates caller cancellation from native
cleanup. Before native execution starts, cancellation can reconcile an undelivered
uncertain admission using its single-owner no-start proof. Confirmed cleanup can
also reconcile watchdog uncertainty. Failed cleanup retains capacity. This adapter
passed independent review with 26 focused ownership/executor tests. It is registered
in the installed app; live foreground search and the bounded two-caller/OCR
handover probe have passed staging validation. Sustained OCR workloads remain
outside that measured acceptance envelope.

The native GPU factories now require an active executor-owned context bound to
the pinned BGE runtime/settings profile. The context expires before capacity
settlement; model sessions retain verified local-file leases and dispose before
those leases are released. DirectML uses sequential execution with memory patterns
disabled. Guard checks, the cached CPU reference regression and GPU numeric
parity passed. An earlier 50-passage trace took 9.954 seconds, including 6.650
seconds of model loading; it is historical diagnostic evidence, not the final
p95. The deployed process-held verified-file cache retains protected,
hash-verified model-file handles to avoid repeated hashing. It does not retain
native GPU sessions between requests. The corrected GPU-first/CPU-fallback
runtime passed the full-search staging latency gate described above.

Publication preview, activation, lexical search/read and deletion survivor
selection now share one SQL publication rule in the shared implementation. Pending
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

The implemented query path captures one stamped profile/generation before embedding.
A shared SQL session lock and durable process-incarnation record protect its
membership, vectors and derived files. Native ANN disposal precedes explicit SQL
lease release. Losing a SQL session refuses results but retains ownership: cleanup
requires explicit release or verified owner-process exit. Live or unknown owners
block purge, recovery and generation-file deletion. The shared hybrid engine
retains this lease through final hydration and rechecks eligibility/currentness.
Recovery reports a recognised pending draft or active corpus rebuild as
`IndexUpdating`. When a source change leaves the active generation behind the
corpus version, the periodic recovery probe builds a new immutable USearch
generation from already eligible SQL vectors. A publication-fenced transaction
rechecks the exact stamp, complete vector membership and model profile before
activating it. It preserves the old generation and all canonical rows; a
concurrent publication retries from a fresh snapshot. Deployment holds defer
this refresh. Zero eligible vectors remain `IndexUpdating` without claiming an
empty canonical catalogue. An unknown absent pointer still fails validation.

Routine Healthy probes read the active projection under the same serializable
publication fence, preserving membership, payload, stamp, path and empty-catalogue
checks. They do not audit unrelated inactive checkpoint payloads or authorise
cleanup. Startup, fault recovery and exclusive recovery retain the full snapshot
and checkpoint validation; a projection read without an active generation also
uses that full proof. Checkpoint append, seal, publication and resume checks are
unchanged.

The prepared maintenance correction keeps complete healthy projection checks
under Shared ownership, allowing concurrent query leases. Detected faults leave
Healthy, release Shared and reread under Exclusive before mutation. Rebuild
admission drains only proven-exited query owners inside its Exclusive transaction;
failed admission rolls back drainage. Production activation remains pending.

Explicit `publication_retry` recovery uses the shared native confirmation and
idempotency boundary. It reopens one failed current retained Publish job and its
same completed delivery under the publication fence, locks dispatch before job,
preserves the old failure in an atomic audit and advances both lease generations.
It verifies immutable Embed inputs and current lifecycle state before restoring
the exact text activity. Normal publication still rebuilds and validates the
current corpus stamp; a receipt replay cannot reopen a later failed attempt.
Explicit `embedding_retry` uses the same boundary for terminal Embed work. It
validates current retained lifecycle, canonical/checkpoint integrity, epoch/profile
and exact settled GPU input/result/cleanup/slot provenance. The atomic audit,
lease-generation increments and same-delivery requeue preserve saved vectors;
normal workers resume missing batches and publication. Independent recovery review
and separate human terminal-processing approval precede production use.

The locally implemented [automatic repository continuation](design/repository-retrieval-reliability.md#automatic-continuation-after-repository-withdrawal)
preserves nonterminal jobs, deliveries, checkpoints and vectors during withdrawal.
An immutable original processing/admission binding and mutable authoritative Git
discovery proof gate resumption; queued waiting work consumes no processing attempts.
Fresh compatible discovery, canonical/vector integrity and known completed GPU
results with confirmed cleanup are required. Publish rechecks source authority
inside its snapshot fence and reuses only fully validated safe immutable collision
winners. Recovery-feature closeout is integrated as Main `09945ae1`. The locally
implemented canonical-updater extension binds the exact additive migration and
coordinated payloads, supports migration-only reconciliation and preserves evidence,
stopped predecessor and hold on failure. Its retained scan uses one ordered sequential
SQL row stream per table and native bounded UTF-8 hashing, preserving the exact V1
projection/hash contract and checks. The three authority tables retain their
append allowlists, protecting
existing scheduler/worker authority rows; only identified new no-work receipts and
verified no-work startup telemetry may be additional. Local updater implementation,
checks and independent review are complete; the authorised continuation and bounded
live validation subsequently completed as recorded below. The explicit
`-ResumeRepositoryRecoveryPreparedRelease` accepts only untouched Prepared receipts
without a baseline, failure or prior intent, preserving the original operation,
application candidate, hold and bindings. It records the corrected operator commit
immutably in separate `PreparedContinuation`; read-only reconciliation accepts the
original candidate or recorded operator. It cannot implicitly retry, rebind or republish.
The subsequent activation failure exposed an IIS configuration read while the real
swap temporarily removes `App`: full environment checks now bracket the swap and
candidate placement before IIS start, with gap-safe identity/location/hold/task
checks between them. Explicit `-ResumeRepositoryRecoveryActivationRelease` is limited to
`FailedHeld` at `PayloadActivationIntent`, exact target schema/history, the unchanged
saved retained baseline and verified restored originals/candidates/companion backup.
It requires no prior activation continuation, saved result or reconciliation and
leaves the receipt unchanged on precondition refusal. Immutable `ActivationContinuation`
preserves the original failure and binds the corrected operator/prior receipt before
mutation, without changing `PreparedContinuation`. It runs only payload/validation/release;
no migration or baseline recapture is allowed. Focused checks and the native
incremental updater contract pass; independent implementation/operational review
and fresh approval for the exact amended production packet subsequently completed.

An explicit single-task acknowledgement freezes the cancelled, unreserved
interactive row's identity and full canonical SHA-256 in `ActivationContinuation`,
preserves that row, and compares the original retained baseline excluding only
that exact unchanged row. Only this path may accept a known Started original-payload
pool at preflight, then use the canonical GPU/IIS drain and repeat stopped-owner,
retained and location checks before activation intent or placement. Default
continuation remains strict; each production use requires independent review and
current authority for its concrete amended packet.

The authorised continuation completed at receipt revision 11 with candidate
`e1d18352` and operator `dc58eb91`, releasing the hold while preserving the original
baseline, receipt/rollback evidence, Disabled task and hidden launcher. Six bounded
MCP/REST/operator-CLI code/document cases passed hybrid-ready cited readback;
the initial MCP index-updating fallback and CLI document timeout remain recorded,
with only the failed CLI case repeated once after reviewed cleanup and settled
publication, on a later generation. This verifies each current binding/source,
not a common-generation or full frozen run. Final independent closeout review
passed for this bounded deployment and live-validation scope; frozen semantic and
unattended-hosting acceptance remain open.

The [operational-readiness design](design/repository-operational-readiness.md)
and [plan](design/repository-operational-readiness-plan.md) scope the remaining
unattended, semantic, terminal-recovery, companion-host and historical-retention
work. Local checkpoint-probe batching, phase diagnostics, strict frozen-workload
verification, explicit task restoration and manifest-bound retention are implemented
and independently reviewed. Release `29b420ba` passed unattended publication and
normal scheduled-task activation with actual hidden companion processing. The five
approved terminal jobs published with their original checkpoints and 86 saved
vectors preserved; only 449 missing positions were generated, and every recovered
source passed current cited readback. All 1,098 eligible current repository paths
were published at the 12 October observation. Frozen semantic acceptance then
stopped with two ready searches, one timeout and 37 unstarted cases. The timed-out
task has a durable uncertain outcome despite committed native-capacity release;
the separately authorised two-request diagnostic then completed hybrid-ready in
19.55 and 13.58 seconds, with a known GPU result, confirmed cleanup and no new
uncertainty. Its root dense phase took 7.74 seconds and model loads totalled
6.43 seconds; the original timeout was not reproduced. A disposable 50,000-vector
comparison did not justify a streaming rewrite. Further inference remains paused
for focused diagnosis. A second authorised diagnostic pair returned ready in
13.96 and 17.64 seconds with confirmed native cleanup. Its runtime trace lost no
events, but final SQL metadata handling rejected a valid plan handle, leaving the
capture incomplete. The private observer correction passed focused checks;
no matching cached plan remained. A disposable test confirmed that an acquired
query can read while checkpoint validation holds the fence and source convergence
waits. Neither result establishes the original timeout's cause. Full interface
acceptance remains open, and the approved
historical-retention set has not been staged or deleted. Existing evidence and
failed runs are preserved.

A local watcher correction suppresses Git repository hints wholly inside the
existing excluded trees, including diagnostic and build output. Git control
paths, uncertain hints and either relevant rename endpoint still signal;
directory notifications cover ancestor moves. Disposable watcher tests prove
these transitions while periodic discovery remains authoritative. This removes
avoidable rescan load; deployment and final semantic acceptance remain pending.

SQL vector selection resolves chunk identity before record revision predicates
to exclude the observed pair expansion, retaining all eligibility checks. Its
LOOP join fixes join order and trades additional point lookups on mostly
ineligible corpora for predictable input-proportional work.

Background GPU embedding groups at most sixteen passages per owned request, using
sequential tensor runs of at most four under one model load and disposal. Direct
non-GPU checkpoints remain limited to four. The tensor/token limits, model profile,
native runtime/settings identity and capacity estimate are unchanged. Ownership,
cancellation and every output are checked between runs; a later failure returns
no partial result. SQL stores an unplaced draft owned by the existing Embed job
and saves exact search-input and payload hashes. Each delivery performs one bounded
request and requeues through the existing
fenced retry transition; reclaim reuses completed vectors. Final sealing rechecks
the epoch, ownership, complete membership and payload integrity before the atomic
Embed-to-Publish transition. Checksum validation reads bounded keyset pages, including
when SQL retry buffering is enabled. Deletion withdraws its own pending draft in the
first durable phase, then captures cleanup and releases its job reference in the
second phase. BGE and disposable-SQL pipeline tests cover this path. The earlier
four-input composition passed staging rebuild recovery, two-caller retrieval,
OCR handover and the frozen English holdout. The larger background grouping
requires its own bounded operational acceptance.

Unfinished requests replay their exact ordered persisted inputs and digest,
including a four-input request saved before a lost handoff. Failed settled groups
retain their digest-based retry budget until a successful checkpoint; regrouping
cannot reset it. Both checkpoint selection and queue admission recheck exclusivity
under the publication fence. Conflicting groups or unresolved native ownership
refuse. Sixteen maximum-width chunk IDs and hashes fit the existing SQL input
contract, so no schema change is needed. Once larger requests exist, an older
four-input binary is not a compatible downgrade without explicit reconciliation.

The implemented foreground BGE wrapper now loads, runs and unloads embedding before
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

`Search:HybridPassagesEnabled` selects the shared runtime only when the
complete existing local OCR/GPU runtime is enabled. It retains the existing physical
slot, durable recovery loop and OCR turn policy. Singleton adapters call scoped
lifecycle services through fresh scopes. Model stores use pinned verified paths
under `J:\Models`; DI construction loads no weights. Each GPU batch/request
unloads its embedding session before any reranker session opens; the separate
CPU fallback pool keeps its verified sessions resident. The flag is enabled in
the installed app that passed the scoped staging acceptance above.

The deployed controlled rebuild captures canonical inputs and existing publication
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
Worker exit proof uses a validated complete IIS worker inventory and selects the
exact application pool; unrelated workers do not prevent completion. The updater
records its owned stop before requesting it, so a failure during exit proof can
restore the original pool before any schema change. A pool already stopped by
another operation is not claimed as an owned stop.

The updater records its forward recovery boundary before the first schema command.
After that boundary, failure retains the hold, schema, payload and configuration;
resume verifies the same release, database, operation and file hashes. A stopped
compatible candidate can run existing dead-owner recovery under deny-all admission
before draining again. Foreign holds and journal/schema disagreement refuse without
service changes. Disposable SQL checks exercise actual idempotent migration/replay,
session loss and exited-owner recovery. The recorded incremental releases,
rebuild recovery, GPU measurements and scoped English staging acceptance have
completed. Future deployment or rebuild operations still require their own
explicit operational authority.

An interrupted schema-54 worklist can be replaced explicitly through that updater.
The successor binds the immutable predecessor packet and the same canonical inputs,
then atomically commits a new epoch/worklist and permanent old-job supersession.
Historical states, attempts, failures and native receipts remain; proven unstarted
GPU tasks are cancelled under the existing admission fence. Claim, retry,
checkpoint and publication paths reject superseded jobs after the successor finishes.
SQL resolves an ambiguous commit response before hold ownership changes. Recovery
inherits the predecessor's original scheduled intake preference and stays forward
with the successor's compatible schema/payload. No model residency change is part
of this recovery.

The shared query engine retrieves at most 100 lexical and 100 dense passage IDs,
fuses by identity, reranks at most 50 and revalidates complete bodies/citations.
Root/workspace dense retrieval scores the entire eligible captured scope up to
10,000 vectors; larger scopes explicitly refuse semantic retrieval. The current
25-second budget includes scope resolution, SQL, models and final hydration. Timeout stops
caller waiting while late native work retains its leases until confirmed cleanup.
Search scores are reciprocal returned positions, not calibrated probabilities.
Corpus and useful existing-search fallback results expose degradation; an empty
degraded search response refuses explicitly. The knowledge list cannot carry response
metadata, so degraded source retrieval produces the same explicit refusal instead
of an apparently complete notes/source union. Healthy notes/claims interleaving is
unchanged; its source shortlist remains bounded at 50 even for a knowledge limit of 100.

The search page shows a waiting state and prevents duplicate submissions while a
request is running. It explains index updates, busy admission, deadlines and
unavailable semantic retrieval when showing available results. Refused searches
clear previous results and leave the form usable; they do not display a successful
empty search. These messages use the existing retrieval status and do not change
inference routing, deadlines or citation checks.

Automatic Codex `UserPromptSubmit` context bypasses that general knowledge
union. It calls the same corpus service through an explicit lexical-only
interface, resolves only the supplied registered `cwd` subtree and validates
each selected hit with a zero-context retained read before rendering a cited
JSON record. The `workspace-lexical-v1` policy admits body matches only,
deduplicates documents and equal normalised passages, and emits no context on
missing scope or weak queries. The hook's cooperative retrieval deadline is
1,750 ms, with a two-second overall deadline and metadata-only audit.
Manual corpus, knowledge, MCP, REST and CLI hybrid dispatch is unchanged.
The [context design](design/workspace-codex-context.md) records the policy
and operational boundary.

## Operations and verification

The operator UI projects committed source, job, scheduler, corpus and audit
state. Live events trigger refresh; they do not replace durable evidence.
The overview separates confirmed execution from current queued/waiting work,
unresolved historical jobs and uncertain GPU outcomes. Worker leases and compatible
GPU task/batch/slot/dispatch fences establish activity; processing labels and age
alone do not. Current source revisions, selected logical branches, record bindings,
root lifecycle and rebuild admission establish applicability. A live claim on a
previous revision remains visible as running historical work.
Source scans are counted separately from pipeline jobs and files. Due released
scans follow root, lease, same-root Git and full deployment-hold admission.
Held initial scans and recorded retries are waiting; a future ordinary periodic
check alone is not backlog. Paused intake without outstanding work may be idle.
Unconfirmed scan/processor leases remain attributed to their source.

Source-root `ErrorCount` (labelled `Failed files`) counts unresolved processing
failures for current files. A failed activity with a verified completed,
non-deleted pipeline record bound to the same source is recovered and retains its
corpus link; independent unresolved failures remain visible. `LastScanIssueCount`
(`Last scan issues`) separately reports access, availability and read issues from
the last completed or failed crawl. Queued/running crawls do not reset that total;
a later finished crawl replaces it. Recovery changes the current projection,
while activity and scan audit history remain intact.
Corpus lists, details, indexed/failed filters and folder counts use the same
verified recovery rule. A recovered record can preview its bounded persisted
indexed text without retaining a current failure reason. Recovery requires its
own activity binding and current, unsuppressed source scope, including the
physical owner of a selected document input; unrelated failures stay unresolved.

File counts use distinct current file owners, including retained archive members;
logical text/metadata children roll up to their original file. Successful publication
is distinct from extraction registration, metadata-only and no-text outcomes.
Conflicting unsuppressed bindings count once per retained file path as provisional
inventory. They are excluded from published/pending/progress totals until authoritative
reconciliation resolves them; neither discovery time nor the highest revision number
selects a winner. Their jobs remain visible under the existing execution evidence.
`Catching up` requires pending files and successful current publication receipts
after discovery in the same actively processing source. The UI reports no inferred
percentage, completion time or undiscovered remaining total. Search-index health
describes the rebuildable projection separately from pipeline health.

The read-only projection records its read window and uses a conservative database
rowversion fence: intervening versioned writes or pending versioned transactions
reject the sample. Immutable evidence and graph deletion rely on the existing
atomic versioned record/branch/lifecycle/index companion writes. This is not snapshot
isolation for arbitrary direct SQL. Rejected/failed refreshes retain the last good
sample with an explicit stale warning; initial failure remains unknown. During
source deletion, overview reads acquire the publication fence before deletion
operation locks, then retain the same DBTS acceptance guard. One two-second
budget bounds coordination and its retry delays. An exhausted internal budget
releases its locks and reports busy/unavailable without advancing the successful
read timestamp; caller cancellation and genuine SQL failures remain distinct.
Source events and a non-overlapping ten-second timer refresh the overview. Attention detail
is read-only and paged in batches of 25, with its own loaded time and source scope.
An initially pending transaction rejects before reading potentially locked rows.
Only an end-fence mismatch permits one complete fresh attention read; names,
scope bindings, time, totals and rows are recalculated together. A second mismatch
fails visibly, and other exceptions are not retried.

The [operator guide](user-guide/dashboard-user-manual.md) records the broader operator
workflow; the [safety policy](safety.md) defines disclosure and storage limits.

Routine IIS changes use the incremental updater with an inspected plan, explicit
approval, retained-state validation and payload rollback.
For explicit stopped-pool recovery, the same updater starts only the candidate;
failed held validation restores exact prior bytes without predecessor startup and
retains the hold. It refuses migration/rebuild/deferral combinations and preserves
the existing boundary against automatic rollback after hold release.
Clean-slate installation uses a separate guarded one-shot GoLive path with independent clean-slate, VSS,
SQL destruction and native Codex registration acknowledgements. The GoLive path
does not offer automatic recovery or resume after an interrupted destructive run.
See [setup](setup.md) for the supported workflow and verification commands.
