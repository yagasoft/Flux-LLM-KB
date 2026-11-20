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
Downstream processors consume protected retained bytes bound to the
revision and processor capability.

SQL transactions establish work ownership before dispatch or presentation.
Leases, generations and operation receipts reject duplicate and stale completion.
Local wake signals and watcher events are hints; reconciliation rereads durable
state. Retained processor completion wakes source registration immediately, with
a periodic reconciliation fallback for missed signals.

Normal source recovery retains pipeline jobs for at least seven days from the
committed suppression time in a verified file-deletion witness. It checks the
original namespace for absence again, checks for a recorded return, and
revalidates ownership under the publication and scheduler fences. A sweep
considers at most 32 jobs per maintenance tick and commits each physical
owner's changes together. A restart rediscovers remaining jobs from SQL.

Generic legacy suppression, edits and policy exclusions provide insufficient
deletion evidence. Leased work, uncertain outcomes, native associations,
recoverable incomplete records and checkpoints with no independent cleanup
anchor retain their jobs. Git publication recovery keeps its original stage
chain for the existing revision retention window. Completed attempts cascade
with their job, dispatched outbox history keeps its artifact reference, and
terminal publication candidates retain detached provenance. Settled GPU
batches remain while another owner or dispatch reference needs them. Retained
source revisions, content and whole-source deletion have their own lifecycles.

Source deletion discovers GPU task ownership through the request's pipeline
record, including mismatched foreign parents. It subtracts already proven local
tasks in memory and checks remaining task IDs in batches against the unchanged
supersession and native-cleanup proof. Its artifact inventory computes sharing
in one SQL query and preserves exact path/hash/length identities in memory.
Both checks retain the existing serializable transaction and publication fence.
After a manifest detaches, shared-segment reference checks use batches of at most
256 supplied IDs. SQL preserves its comparison semantics and returns the supplied
IDs; unshared segments still pass the same target, pin, state and native-owner checks.
Source-owned manifest retirement handles at most 16 exact targets per page. It
commits retirement intents before a fresh, non-retrying file-reclamation phase.
Each phase checks unconfirmed artifact references once for the page, using a
bounded comparison tree ordered by the actual text-column collation. This
preserves exact SQL equality without repeatedly comparing every document body
against every target or hashing whole bodies. Every physical deletion rechecks
the source lease, target and original SQL-session ownership. Cleanup receipts
commit together; a lost acknowledgement reuses the committed reclamation result.
An already committed retirement with no memberships skips repeated marking.
The reclaim phase handles at most 16 dependent segments before finishing ready
manifests, selecting segments through both their retirement operations and exact
source targets. It keeps generation pin checks setwise for the page, including
embedding requests and recovery references. The proof belongs to its original
context and transaction; native owners and source/session authority are still
checked separately before every physical deletion. A blocked owner never grants
an extra seventeenth segment attempt.
Classified SQL timeout, deadlock or lock-timeout failures while acquiring the
publication fence defer retirement before file authority is granted. Caller
cancellation and other SQL failures still propagate; no file operation is retried.
Source status joins the unique earlier-policy eligibility IDs within its original
SQL statement. Summary and detail retain the same complete proof and categories,
including Deleting roots, without repeating a correlated assessment for each file.

ZIP and TAR processing enforce archive bounds and member path rules. Office Open
XML extraction produces one logical UTF-8 result for the original DOCX/XLSX/PPTX.
Document package members are not separately published corpus documents. Ordinary
retained physical UTF-8 and the bounded Office extracted-text result can reach
200 MiB. Other derived text retains its existing 32 MiB read and registration
limit. OOXML v4 permits 128 MiB XML parts and streams XML without an
element-count ceiling; the separately versioned Visio parser keeps its 64 MiB
part and two-million-element budgets. Total XML expansion remains 512 MiB with a
finite depth limit; compression ratios apply to XML opened for extraction, not unopened
binary media. ZIP expansion permits two nested ZIP levels with one cumulative
entry and expansion budget. Media metadata uses a protected retained
stream up to 1 GiB, with a separate 96 MiB parser-read budget; it does not decode
or publish the media payload.

Native transcription uses a separate Extract job and the ordinary SQL outbox.
The same bounded registration checks apply to new observations and to expressly
named catch-up revision IDs. Source roots opt in through
`MediaTranscription:SourceRootIds` with a UTC `AfterUtc` and up to 16
`CatchUpRevisionIds`. The shared GPU and source-worker configuration is
required. The default configuration has no opt-in roots. Discovery verifies
the current physical and retained file and holds ambiguous adjacent transcript
candidates for review.

The GPU adapter admits one task in the existing background video lane. The
native child starts only after durable permission and Windows job containment.
Settlement requires exact process cleanup evidence. The worker applies the
Egyptian Arabic and English code-switching profile when language sampling
supports Arabic or Arabic and English. Teams recordings can attribute the spoken
words to active nameplates in supported gallery and screen-sharing layouts.
The supervised worker uses offline OCR and runs visual attribution after ASR
within the remaining lease. It leaves speech unlabelled when visual analysis is
unavailable and renders unresolved concurrent candidates with `or`. The
same versioned module ships with the shared local scripts. Audio-only cues
retain their existing format. The transcript is flushed to a private
pending file before the GPU job settles. The original extraction job then saves
a UTF-8 file named `<recording name>.transcript.txt` beside the recording.
The normal source scanner classifies and indexes it as text. SQL retains a
receipt and the ordinary search index. The recording and transcript each have
their own status in the file drawer.
Corpus retrieval preserves bracketed cue timestamps as plain text. The disclosure
guard still checks structured JSON and embedded credentials before returning
transcript passages.

Published catch-up results are eligible for conversion only for the configured
exact revision IDs. The conversion keeps the previous publication until it
verifies ordinary extraction and passage coverage through the last cue. It
then withdraws the former transcript publication and preserves the native
cleanup receipt, language evidence and cue timing.

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
and the current serialised root scan lease are required for tracking/policy exclusion.
Physical deletion can also converge independently under current root authority
and live no-follow absence leases; it does not depend on a successful content scan.
Every Git revision convergence and scan completion also fences configuration
and the owned lease. Active scans renew that lease; renewal loss cancels work.
Older binaries ignore this mode: quiesce workers and pause/disable Git roots,
drain/fence their claims, then roll back; keep those roots paused until compatible
code returns. Existing filesystem sources retain mode zero.

Local source roots must pass fixed-drive NTFS and root-overlap/path policies.
Registration uses preview/commit fencing and maintains stable revision identity.
Watcher-driven refresh is reconciled against retained state and configured
include/exclude rules.

Durable removal scopes hold new work for affected originals and their finite
archive/document descendants. Guarded reconciliation suppresses absent originals,
withdraws their published descendants and records a versioned absence witness.
New dispatch, native inference and result/publication commits share the same
source-eligibility rule and publication fence. Running owners receive cooperative
cancellation; native capacity stays occupied until existing cleanup proofs settle.
Unclaimed or expired retained processor work reaches an explicit terminal state.

Each process and root configuration starts with retained-work admission closed.
Startup drains a fixed journal high-water mark, or checks retained original paths
when coverage is unavailable, before admitting unaffected paths. Uncertain paths
remain durable holds. Standalone document commands validate their exact physical
owner and honour existing pending scopes; their local gate is confined to exact
request/branch/dispatch selectors and never starts a sibling sweep.
Cleanup remains available while source admission is closed. Ordinary publication
reads follow durable owner suppression and continue to expose paused roots.

Opening source admission publishes a durable GPU scheduler wake after the exact
root configuration is admitted. Recovery shares acknowledgement state across
scopes and retries failed wake writes, including when admission is already open.
Clearing a pending removal scope commits its scheduler wake in the same transaction.
These triggers resume an existing queue after a no-candidate startup decision;
unchanged recovery passes do not repeatedly trigger admission.
If source admission changes after an outbox claim, the exact live job and dispatch
can be deferred together without waiting for their leases to expire. This route
requires a source-linked record and never resets GPU-owned work.

Temporary database availability failures defer source reconciliation and retained
processor hosting with a cancellable backoff. Their failed scopes are disposed;
the next iteration rechecks the deployment hold and durable claims. SQL timeouts
or pre-login failures therefore do not restart the whole app and reload its models.
Unrecognised database failures and unrelated programming errors remain faults. This
hosted-loop policy does not expand permission to retry individual stage mutations.

Exact same-path/hash/length filesystem replacements can reuse one immutable logical
revision only under owned, single-link, no-follow physical capture. Inventory and
capture evidence record the current physical binding; immutable history and retained
artifacts keep their identities. Pending-path pressure becomes one full-recapture
obligation rather than repeatedly invalidating active scan epochs. Later harmless
journal CLOSE traffic does not advance its forced-capture boundary.

Opt-in journal-backed roots converge watcher hints and bounded NTFS journal
pages on one durable pending-work pipeline. Cursor advancement and pending
generations commit together; healthy qualified coverage uses watcher signals
as advisory intake wakeups. Root/environment-bound guarded seals allow unchanged
files to reuse retained observations. Baseline and journal-gap recovery,
independent paced namespace/Git reconciliation and scoped degraded-mode work
preserve discovery and deletion authority. See the [incremental scanning design](design/incremental-source-scanning.md).

The rejected-cursor correction distinguishes a historical native read rejection
from unavailability after a valid control read on the same volume handle and
one retry at the original historical position. Transient success returns that
original history; control bytes never advance coverage. Healthy reads add no I/O.
Its paired helper protocol preserves that diagnosis. A shared reset records the
previous/replacement cursor atomically with recovery debt and refuses to alter
paused participants' coverage or pending metadata. New automatic whole-root work
honours the normal reconciliation cadence even after journal recovery; journal
retries and scoped changes continue independently. The overview shows checkpoint
availability, retry timing, recovery debt and paused scan admission in its committed
status sample. Production release and resumption remain separate
gates in the [correction record](design/reconciliation-loop-correction.md).

An ancestor authority event preserves a baseline only when an ordered same-parent,
case-only rename in the accepted NTFS page agrees with the complete guarded directory
chain and continuous journal coverage. Cloud reparse changes, access failures and
ambiguous events keep coverage in recovery. Their first due time commits with the
cursor, survives restart and is at least 30 seconds after detection and 15 minutes
from the last reconciliation. The recurring scheduler coalesces requests under the
same source lease and retains the weekly metadata backstop. Only a current,
namespace-complete baseline clears the early reconciliation obligation.

Cloud seal profiles additionally bind the provider name/version read from Cloud
Files metadata at the root. A mismatch or unavailable provider evidence revokes
reuse and advisory-only watcher trust. Local qualification remains separate.
The tested hydrated OneDrive workflow has independently reviewed guarded-CLOSE
compatibility evidence on provider 26.189.0928.0009: eight exact native capture,
seal and later-change cycles, with preserved bytes/identity and a passing matched
no-edit capture control. The composite still failed at final synchronisation;
older edited controls provide context rather than current-version post-edit sync
equivalence. This does not establish general sync reliability. The 26 October
release activated exact root/provider/environment bindings after independent
review. Actual IIS-identity transport and progressing sealed captures passed
bounded live acceptance; initial baseline and namespace establishment were still
ongoing, without a claim of production steady-state byte savings or change latency.

Weekly integrity checks use the existing durable source request/outbox/root
lease with a separate verification request kind. Frozen cycle membership,
per-file successful receipts and a protected physical reader slot survive
ordinary job retries without claiming ordinary scan completeness. Source and
retained reads share one paced budget; ordinary work, current policy/pause and
ownership are checked between chunks. Qualified native read intervals and a
closing journal barrier bind historical source hashes. Successors require a
fresh authoritative hash, and deletions/exclusions require guarded suppression
witnesses. Deferred or incomplete coverage cannot advance the clean verification
time. The existing source page and native APIs read SQL-only progress; settings
use the normal confirmed, idempotent and audited native mutation contract.

Feature-off rollback uses an explicit stopped-updater retirement operation, with
no standalone CLI route or background retirement service. It pins the deployment
hold/configuration and stopped-writer evidence to the database incarnation and
physical runtime directory. The existing physical verification slot remains held
through SQL retirement. Active cycles become invalidated, queued verification
controls become terminal to retry/release, and unfinished measurement attempts
remain explicit lower bounds. Existing receipts, clean verification timestamps,
content-change obligations and outboxes survive. This operation alone does not
establish older-binary compatibility; the updater must retain the hold and refuse
unproved predecessor startup.
Actual read deltas commit atomically with successful receipts. Abandoned unfinished
attempts leave a sticky lower-bound measurement flag, independently of whether
the cycle's integrity receipts eventually complete successfully.

All roots use the 200 MiB strict UTF-8 text limit. Existing stored file-size values
are retained for historical configuration evidence. Native creation accepts the
legacy file-size field for compatibility and applies the app's limit.
Explicit root restrictions remain authoritative. The additional text extensions
are RDL, INI, HTML, NDJSON, VTT and URL; content is indexed as text and never
executed or followed. Applying revised defaults to an existing root is explicit.
Previously retained policy refusals are re-evaluated one exact item at a time
through `policy_reprocess`, with a current root revision, unique unsuppressed
physical binding and retained-file length/path checks. Immutable source classification, original
terminal attempts and the last good publication stay intact. Current status
follows the versioned successor while history remains available.
An unexecuted earlier-format text activity against an older `Unknown` or `DeferredPolicy`
classification is not a queued job. Current views show its retained-policy
assessment requirement. Exact retained admission may use that idle activity only
when no older policy owner, processor branch, recognised successor or pipeline
record exists. Mixed histories use the older policy owner and supersede its idle
text sibling. A cancelled primary activity proves only its exact recorded
successor; cancellation alone never authorises recovery. Once execution starts,
the original native receipt may be replayed, but a new reprocess does not replay
completed work. The text-size limit and unsupported-format outcomes retain their
specific reasons.
Native policy finalization registers only its exact pending text successor
through the established planner and wakes the outbox, independently of long
source scans. Exact receipt replay recovers interruption before or after job
registration without creating a second record or delivery.
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
Same-size/date replacements may reuse their existing logical revision while
inventory records the current physical identity and clears file-specific journal
and seal facts. Immutable ingestion history remains unchanged. The handle prevents writes and renames;
hard-link and access-control changes are checked but are not atomically excluded
with the SQL commit.

The source detection policy is `events-weekly-metadata-v1`. Journal and watcher
file events select paths for size/date checks. The weekly fallback compares
membership, size and modification time. Quiet unchanged files are reused without source or retained assurance reads,
including missing seals, pause/resume and journal recovery. Same-size edits with
preserved timestamps can be missed even after events; silent corruption need not be
automatically detected. These are accepted freshness limits. Normal ingestion,
deduplication and publication retain their hashes and ownership/access safeguards.

Automatic checksum verification and forced unsealed-file rereads are retired.
The stopped updater archives prior policy, strict root obligations and frozen
manifests, invalidates verification-only work under its physical native-reader
slot, and adopts the weekly interval for enabled and paused non-deleting roots.
Specific pending file events and historical receipts remain intact. Journal
retry and warnings continue independently of scan scheduling. Only explicit
`source_sync` with the compatibility `fullContent: true` field requests a full metadata
reconciliation; it no longer forces unchanged content reads. See
[the source update policy](design/low-disk-source-updates.md).

Owned filesystem and Git scans carry the discovery hashing handle directly into retained
publication and SQL convergence. The final snapshot already contains scan
ownership and keeps its object identity through the receipt. Existing retained
blobs receive length/path checks without a content pass. Missing blobs reuse the
capture digest and bounded discovery buffer, or copy the held stream without a
second hash. Captures are consumed once,
processed sequentially and released before activity planning, with cancellation
and iterator-disposal backstops. Git opening/closing inventory authority remains
mandatory. Preview and unowned snapshot callers retain source revalidation.

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
historical memberships. `cleanup-memberships` checks at most 1,000 owned vectors
and removes at most 16,000 membership rows per transaction. The separate row
ceiling amortises deletion over historical generation fan-out without enlarging
the vector inspection page. It checks withdrawal
and retirement across the entire page before mutation. The cursor advances only
through the fully cleared prefix, including gaps and vectors with no memberships.
The generation-retirement guard uses one typed JSON vector page and a query-local
row-goal hint. Within the same SQL batch and transaction it collects unique
referenced generation IDs before checking active/retired state. Every page
membership remains examined; generation status need not be looked up again for
every historical membership. The transient ID set is rebuilt for each unit.
High-history qualification covers 32,768 unretired control generations, over a
million owned and foreign memberships, both cached-plan cardinality orders and
protected memberships at the end of a full page.
Retired membership pruning uses fresh scalar active/uncertain-execution and
native-cleanup checks in both request/task ownership directions. It retains the
query/publication fences, exact lease and Deleting-root checks, whole-page
withdrawal/current/segment/retirement proof and behind-cursor refusal. Full
historical ownership, cross-root and shared-batch checks remain mandatory before
every vector/chunk/graph purge and native-file reclamation. A phase transition
cannot fall through to payload deletion in the same unit. Qualification also
covers 217,685 historical tasks across 20,001 jobs for both OCR and embedding.
Committed row counts, the last committed lease token and the
resume phase survive cancellation, ambiguous commit acknowledgement and restart.
Initial withdrawal updates at most 4,096 vectors per leased transaction and
persists its cursor with the receipt; a live vector behind that cursor prevents
cutover. `purge-records` refuses late memberships, unretired checkpoints, shared
batches, active capacity and unconfirmed native cleanup before deleting any
payload page. It removes at most 4,096 vectors or chunks per transaction while
retaining record/request/generation ownership anchors until final graph removal.
Chunk deletion cascades to its derived window decision.
The existing 15-minute claim expiry remains the upper wait for recovery of an
uncommitted claim whose process disappeared; expiry never proves native cleanup.

The stopped OCR maintenance path has a separate version-2 cleanup-only mode for
an exact uncertain OCR task with no recorded execution identity on a source
marked Deleting. It pins the matching updater and CLI's reviewed migration
history, schema, root and configuration, request/task/job row versions,
single-member batch, dispatch, admission, owner and capacity-slot state. Fresh
OS/driver verification under publication/scheduler locks records physical cleanup
evidence while preserving task/job state and stored results. It performs no DDL,
completion or requeue. Replay binds the complete input and committed proof.
Its incremental release uses candidate Web/Outlook and preserved helper pins for
start/hold-release admission. Private maintenance evidence remains outside the
canonical recovery tree; the rollback packet uses the existing ordinary format.

In `cleanup-files`, pending-target reads are read-only. The coordinator acquires
the existing generation-query or content-hash publication gate, rereads authority
and immutable target metadata, then holds the gate through native IO. An already
started old worker can finish that exact idempotent IO after takeover, but its
stale SQL receipt cannot change the new owner's operation. Shared blobs remain
captured targets until a gated preservation/removal receipt decides their outcome.
Attention retains its exact resume phase and safe reason; the UI shows committed
membership counts and last progress without estimating a completion percentage.

Root ownership queries keep revision, record and job sets on SQL Server; exact
embedding/OCR bindings and archived cleanup proofs remain required. Generation
metadata referenced by any embedding request survives empty-index cutover until
ordinary graph cleanup removes that request. Pending file receipts use the
operational `IX_SourceDeletionCleanupItems_PendingQueue` index on operation,
state, storage kind and ID. Completed receipts therefore do not accumulate in
each pending lookup. The unique path index and per-file gated authority reread
remain. Fresh explicit bootstrap installs the same declaration as the typed
`-EnsureSourceDeletionPendingQueueIndex` incremental deployment option. This
performance index changes neither EF migration history nor application readiness.
Deployment verifies the exact database/history/index state, records a durable
attempt and reconciles uncertain outcomes without replay; failure retains the
stopped hosts and deployment hold for reviewed forward recovery.

An expired lease alone does not prove that a Visio process has stopped. Unknown
process cleanup retains the deletion fence. Failed terminal work is not silently
reset by replaying an idempotent command.

## Scheduling and native processes

The GPU scheduler is SQL-authoritative and uses strict priority, FIFO and
compatible-batch ordering. Capacity ownership, executor dispatch, acknowledgement,
result acceptance and settlement are fenced by durable identities. A result
cannot publish against a superseded revision, paused/deleting source or stale
ownership generation.

The shared runtime can retain one GPU model for two minutes after its last
completed job. The scheduler chooses the highest eligible lane first, then
prefers the oldest exact match for the loaded model within that lane. A different
model can run after the resident allocation has been released. Active jobs keep
their existing deadlines and continue to own the device until they settle.

Each reused model has a durable owner distinct from the job, and each successful
job records its detachment from that owner. OCR and transcription use one
contained helper with an independently authorised command per job. BGE revokes
each request's access to the ONNX session before returning it to the scheduler.
The next incompatible BGE phase disposes the current session before loading its
replacement. Capacity becomes available after confirmed release of the precise
allocation. An unverifiable owner or helper keeps the slot fenced.

Native process supervision records lifecycle evidence. A process outcome that
cannot be proved does not create permission to repeat inference or release an
unsettled reservation. Shutdown cancels owned children; restart reconciliation
recovers from committed state. Model-free deterministic executors support tests.

PaddleOCR owns one durable execution identity and supervisor incarnation per
dispatch. Its 30-second liveness heartbeat keeps healthy long work active without
extending the twenty-minute deadline. Python starts with `-I -S` behind a stdin
gate, enters a restricted Windows job, and receives permission only after SQL
binds its exact child incarnation. Job termination and pipe drainage are bounded;
native cleanup is recorded independently of the result. A missing or inaccessible
job requires operator verification rather than an age-based capacity reset.

Known OCR output and physical cleanup settle atomically, including a reservation
already diagnosed as uncertain. Stable receipts fence replay after slot reuse.
Recovery preserves a live supervisor's result-write window through its deadline;
an interrupted attempt with proved cleanup becomes an explicit failure without
repeating inference. Invalid output is retained without publication and cannot
monopolise independently proved-clean capacity. The Overview and read-only GPU
status expose the current OCR owner, deadline, actual heartbeat age and action.

Earlier OCR reservations without native ownership require the incremental updater's
explicit OCR stopped-maintenance mode. It pins database incarnation, migration,
payloads, configuration, reservation and result; stops exact application owners;
and verifies the runtime boundary against bracketed OS/driver observations.
Unrelated desktop GPU processes require positive executable classification.
The additive schema and result/cleanup receipts survive rollback; after candidate
start intent recovery proceeds forward under a retained admission hold.

## Offline model boundary and English OCR

The model gate resolves a bounded local manifest only against `J:\Models`, pins
ancestors and files with no-follow handles, checks declared identities and byte
lengths, and holds read-only handles for the lease. Normal startup/load does not
hash model payloads; same-length corruption may reach the native loader. Missing,
unavailable, recall-required or redirected artifacts refuse execution. The
existing metadata receipt records zero checksum-verified bytes and does not claim
integrity verification. `models verify` retains its command name for compatibility
and performs these metadata checks. Acquisition integrity remains a separate,
explicitly authorised activity under `J:\Models`. There is no downloader or
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
The dashboard's **Verify and enable** action saves the profile disabled and queues
a targeted folder lookup in the same SQL transaction. The existing desktop host
can complete the lookup after the dashboard closes. A successful, current
verification selects the folder and enables capture atomically. Pausing, removing
or resaving the profile invalidates older verification requests. The requested
path stays in private SQL state and is cleared when the request terminates.
Normal application startup does not activate Outlook or change desktop tasks.
Credentials, mail contents and spool data are private runtime material.
An explicitly bound Outlook profile publishes committed mail bundles into a
dedicated ordinary filesystem source. It stages searchable Markdown and original
attachments outside the watched tree, records the exact staged files and previous
withdrawal in SQL, and atomically promotes the bundle. The normal source pipeline
owns discovery, extraction, OCR and search. The private spool keeps the capture
receipts, original artifacts and cursor evidence. See
[Outlook as a watched source](design/outlook-watched-source.md) for cutover and rollback.

## Retrieval and integration surfaces

Current search combines eligible retained corpus text with knowledge records.
The installed hybrid passage runtime combines SQL Full-Text, learned BGE
embeddings, USearch and a cross-encoder reranker for source retrieval. The
deterministic token-hash provider remains the non-hybrid baseline. SQL
publication eligibility controls which revision can appear.

Passage validation reuses computed body/search-input digests only for exactly
unchanged text and context, in a bounded process cache; disclosure and current
source ownership are checked separately on every request. Deduplication compares
header/body strings directly. Protected pagination tokens contain the canonical
query and scope for direct comparison rather than custom hashes. Previously
issued version 1 tokens retain read-only compatibility verification; mutation
confirmations, idempotency and framework token protection are unchanged.

Rebuildable ANN projection files are checked by contained path, length, native
format and structural metadata rather than full-file checksums. New segment
descriptors retain the schema's 64-character checksum field with an all-zero
unchecked marker; membership identities remain functional metadata hashes. Older
projections remain readable. A rollback binary may refuse a newly written
unchecked projection and need a derived-index rebuild from canonical SQL; keep
the reviewed admission hold until that recovery is healthy.

Retirement records a bounded file-identity intent before unlinking app-owned
projection children. A fresh-process retry keeps those original identities and
absences, so it can finish interrupted deletion without adopting replacement
files. Incomplete intents and unexpected children refuse deletion. This is
target ownership metadata, not a payload checksum or background integrity scan.

Source list pages fetch only the latest finished nonverification scan's error
count per root. Detail reads fetch the current state and latest finished counts
and audit evidence, without namespace manifests. Status queries evaluate earlier-format
text eligibility once per activity rather than repeating its correlated proof
in each projected field.
Both source views correlate that assessment to the activity's primary key.
The deferred-owner lookup uses revision and state: a pending activity cannot
also be its own deferred owner. This avoids the repeated table-scan plan seen
on mixed retained history without changing assessment or recovery authority.

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
remain. The installed root/workspace semantic path scans exact SQL membership in 256-vector
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

The [scoped ANN correction](design/scoped-ann-retrieval-plan.md) replaces this
serving scan in the staging branch with native filtered searches over immutable
USearch segments. SQL returns eligible identifiers and selected passages, preserving
publication and path guards. Read leases survive unrelated publication; final
citation validation remains fenced. The native view cache retains at most 8 GiB
of segment file lengths, with pressure, retirement and shutdown eviction; mapped
length is not a RAM measurement. Model-free background preparation owns its own
lease. It verifies every segment member before excluding IDs whose complete
context-zero disclosure window cannot be constructed. Canonical indexing and
corpus rebuild preparation persist that decision once per chunk, including false
results, bound to canonical revision/hash/length, chunk revision/hash/boundaries
and the exact disclosure-window rule version. Restart preparation reads these
metadata bindings and flags without copying canonical windows to tempdb.
Missing or stale decisions report warming. With a registered writer they request
bounded, resumable backfill; read-only staging neither registers that worker nor
writes revisit state from retrieval.
Deleting sources are excluded at capture and commit and never require flags to
prepare a surviving projection. Backfill commits at most 128 chunks per unit,
retains at most four bounded canonical bodies within 16 MiB, and uses bounded
windows for larger documents. Progress and revisit hints live in SQL.
Range discovery and exact ID validation use distinct SQL shapes; the commit
recheck reads only its bounded ID set while holding the publication fence.
Replacing canonical text or chunks invalidates proofs and the artifact cache stamp in
the same publication-fenced transaction; new chunks receive new decisions.
Preparation captures flags/stamps consistently, includes the rule version in
cache identity and checks captured artifact versions at each ANN key selection.
The flag grants no disclosure permission. Fresh publication checks, credential/proof checks and
final citation reads remain mandatory. A request allows at most 250 ms for incremental preparation on its original
captured generation, then reports explicit warming degradation; it never loads
the full projection inline. Exact duplicate search inputs are grouped before
fusion and the 50-input rerank budget, with each returned equivalent retaining
its own current citation. Lexical selection materialises scoped keys
before body inspection, preserving ordinal and Full-Text ordering while excluding
historical bodies. Full-size exact recall passed at staging width 512 on both the
original scope population and the current window-available population. Full API
latency, answer relevance and indexing-overlap acceptance remain pending; no
production activation is included.

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
recovery. The GPU path unloads models after each request or batch. An eligible
interactive search takes the next free slot. Active OCR pages finish, and the
existing background lane order and OCR FIFO apply when search has no eligible
request. Document OCR currently shares `DocumentIndexing` with other work. Keeping models
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
bounded admission and interactive search priority. The preceding baseline passed
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
p95. The process-held local-file cache retains protected,
metadata-checked model-file handles. It does not retain
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
Recognised retained document inputs use the same read-only lineage proof as
publication. Recovery additionally binds the unique physical owner, completed
unleased one-child branch, member, activities, retained artifacts and four
completed preceding stages. Changed bindings, competing publications or native
ownership/uncertainty refuse recovery. Publish reuses existing extraction and
Embed results; it does not start another GPU task. Native versions are hashed in
stable order so larger documents retain the existing confirmation target bound.
Explicit `embedding_retry` uses the same boundary for terminal Embed work. It
validates current retained lifecycle, canonical/checkpoint integrity, epoch/profile
and exact settled GPU input/result/cleanup/slot provenance. The atomic audit,
lease-generation increments and same-delivery requeue preserve saved vectors;
normal workers resume missing batches and publication. Independent recovery review
and separate human terminal-processing approval precede production use.

Ordinary Git text edits select the current revision from its exact path inventory,
retained artifact and completed authoritative scan proof, including the current
configuration, repository identity and policy. A revision number alone grants no
authority. Pending same-path captures and outstanding peer observations hold
publication; an older terminal observation cannot indefinitely block a later
completed capture. The last successful publication remains visible until its
qualified successor atomically suppresses the predecessors and activates the new
index membership. Segmented candidates cover every predecessor owner and recheck
that scope at activation, including replacements that change physical identity.
An empty successor retires the old text and vectors through the same path.

Recovery may revisit qualified current Extract replacement holds and settled Embed
group holds, preserving delivery leases and unrelated blocked states. An Embed
hold needs exact native cleanup, released capacity, draft/profile/input bindings,
no saved vectors for the missing group and fewer than three failed attempts.
It retries the original ordered group through the existing GPU handoff and keeps
all attempt history and successful sibling vectors. Every failed attempt retains
its own cleanup/release proof. Publication requires a strictly later, fully verified
success for each failed group, rechecking result payloads at preflight and inside
the activation transaction. Missing results never authorise publication. A
clean file captured alongside a transient sibling error retains a targeted proof
retry. A revert to an already completed, unexpired revision can restore its verified
publication without embedding again, after source admission and removal checks.
This advances the corpus version and uses the normal derived-index refresh.
Recovery pages advance across hosted service scopes, so ineligible retained
history cannot starve later reverts. No schema migration is required.

A scheduler mutation-lock timeout before Embed handoff leaves the original
request and worker delivery recoverable by lease expiry. Only the typed lock
timeout at that pre-mutation boundary receives this treatment. A confirmed
terminal retry may also reuse exactly one demonstrably unstarted request:
all execution/cleanup/result fields are absent, no task or idempotency-key
collision exists, its exact canonical group has no vectors, and every saved
sibling remains fully verified. Git recovery records an append-only audit fence
requiring current capture authority after claim, at handoff and before native
allocation. Claims and subsequent scan updates cannot erase it. Once such a
requeue commits, recovery must use a binary enforcing this fence; hold workers
and preserve receipts for reviewed forward recovery rather than admitting an
older payload. Native history checks batch metadata and bounded group payloads
without dropping ordered input or result-digest verification.

Physical source revisions may link to their immediately preceding revision after
an edit or rename. Terminal Embed and Publish recovery distinguish that history
from derived inputs by origin kind, root, stable identity and revision sequence.
A terminal Embed failure before its first draft can resume only with the exact
completed Canonical job, delivery and verified chunks, and no draft, GPU task,
request or vector evidence anywhere for that record revision. Confirmation binds
those inputs and the absence; the ordinary worker creates the first draft. Git
work retains its enrolled current-capture fence through claim and native handoff.

Explicit recovery of cleanup-confirmed failed GPU groups records a separate
append-only audit requirement on both filesystem and Git roots. Current inventory,
retained input, attempt limits and native ownership remain mandatory. The shared
checkpoint verifier and both publication fences require a strictly later verified
matching success, including previously superseded groups. A lost result or changed
input keeps the publication held; ordinary unrelated filesystem work is unchanged.
Recovery after these marked actions requires a binary enforcing the audit fences.

Repeated recovery proofs read canonical artifact text and optional metadata with
SQL sequential access on the existing connection and transaction. The reader
preserves every artifact field and the configured transient-read retry outside
transaction-owned execution. Successful native group payloads are fetched in
pages of at most 128 chunks, then hashed in each request's original input order.
Confirmation binding retains SQL request ordering. These I/O changes retain the
complete source, sibling-result, ownership, cleanup and publication proofs on
every required gate. Ordinary partial embedding recovery now reuses persisted
validation markers in the existing GPU operation receipt table. Markers bind the
exact source/draft/runtime, ordered inputs and vector/request/task/batch/dispatch
rowversions and stored digests. An unchanged group skips vector-body reads; a miss
validates payload checksum, shape, norm and result digest before atomically saving
its marker. Ownership, cleanup, unresolved-result and sibling guards run every
time. Earlier-format/CPU work, final publication and external confirmation proofs retain
their full checks. No schema migration or process-local cache is required.

Text normalisation preserves well-formed Unicode noncharacters. Windows FormKC
can reject these valid UTF-8 values; the shared normaliser retains them unchanged
and normalises surrounding spans. Plain text, document provenance and the local
model-free provider use the same rule. Unpaired UTF-16 surrogates still fail, and
the retained Extract text and source bytes are preserved.

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

The hosted GPU scheduler waits through an ordinary deployment validation hold
before consuming wake state or attempting admission. Any observed hold, including
one permitting the exact corpus rebuild, leaves a full-release recovery pending.
Permitted rebuild admission remains subject to the existing SQL gate. After full
release the scheduler first reconciles any in-flight or unknown admission using
its original operation identity and reasons, then performs a fresh startup recovery
round for the ordinary queue. This prevents an empty held-startup decision from
stranding existing ready work. Cancellation leaves the hold and native ownership
unchanged. This covers one validation hold and full release for the worker started
by the ordinary updater. Repeated in-place live holds, or an entire arbitrary hold
cycle hidden inside a single call, require separate recovery design and review.

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
slot, durable recovery loop and workload priority. Singleton adapters call scoped
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
its bound embedding work under the same native ownership rules.

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

Held deployment validation can catch up a segmented derived projection before
releasing canonical work. Only a sealed purpose-0 candidate without a draft may
activate through this path. Same-epoch/version/base-pointer, complete change-log,
membership, maintenance and native ownership checks remain mandatory; the refresh
does not advance the canonical corpus stamp or run pipeline jobs. This avoids a
cycle in which held readiness waits for a refresh that itself waits for hold
release. Stopped incremental recovery can atomically adopt the exact failed
release's hold using a reviewed payload/configuration/database/owner binding;
foreign holds, drift and interrupted transfer replay remain refused.

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
The former 10,000-vector scoped refusal was superseded by exact SQL paging and
is now being replaced by filtered segment ANN in staging. The
25-second budget includes scope resolution, SQL, models and final hydration. A
separate semantic cutoff reserves five seconds for lexical fallback. Overall
deadline exhaustion raises a retryable refusal. Timeout stops
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

The Overview puts current issues next to the affected source and an action that
opens that scope. Previous attempts and ownership diagnostics use separate paged
reads. A GPU result and the scheduler's evidence of released capacity are shown
separately. Interactive attempts do not initiate indexing recovery. Existing
workers retain responsibility for their bounded automatic continuations.

Guided recovery reuses the native preview and scoped commit. The control keeps
an ambiguous submission available under its original confirmation and operation
identity as the Overview changes. Acceptance is shown as an accepted request.
The exact source, retained branch, catch-up or pipeline evidence establishes the
downstream progress. A source scan retry rechecks the latest ordinary scan,
root configuration, durable delivery, owner and hold. The existing worker wakes
after a successful queue receipt. Guided document recovery preserves prior
native evidence and verifies the retained input and any required physical source
authority. A prepared successor must still satisfy normal worker admission.

Neither additional terminal GPU replay policy qualified. The established Embed
continuation keeps its input-group budget and preserves executor uncertainty
when the result is missing. Current OCR settlement does not prove the same
physical input was restored after withdrawal. The eligible native actions for
those cases remain available through guided review.

Source-root `ErrorCount` (labelled `Failed files`) counts unresolved processing
failures for current files. A failed activity with a verified completed,
non-deleted pipeline record bound to the same source is recovered and retains its
corpus link; independent unresolved failures remain visible. `LastScanIssueCount`
(`Last scan issues`) separately reports access, availability and read issues from
the last completed or failed crawl. Queued/running crawls do not reset that total;
a later finished crawl replaces it. Activity, scan and native execution records
remain intact. Current operator events exclude an earlier diagnostic only when
its canonical identifiers match an exact selected successful document publication.
Recognised Office policy history is limited to increasing shipped v1–v4 processor
pairs on the same immutable physical owner; image/PDF recovery uses its explicit
child relationship. Derived text may change across Office versions. The proof
requires the selected branch, child/activity/artifacts and five completed stages;
pausing a source does not reverse a proved success. Unrelated access errors,
unknown relationships, malformed correlations and later failures remain visible.

After an authorised successful scan completes, a separate short transaction can
remove at most 100 such obsolete diagnostic rows from 1,000 root-scoped candidates.
A wrapping advisory cursor advances with the delete transaction, preserving other
health evidence and revisiting unresolved candidates on later bounded passes.
It rechecks fresh completion authority under publication, root and scan locks.
An expired or changed claim skips cleanup without changing filesystem completion
semantics. Cleanup has a five-second overall deadline, two-second SQL commands
and a 250 ms lock timeout. It never retries an ambiguous cleanup commit; failure
does not reopen the completed scan. Jobs, attempts, policy relationships, native
receipts and unresolved diagnostics are preserved.
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

With explicit `Dashboard:ReadConsistency=Snapshot` activation, one read-only SQL
snapshot transaction supplies the whole overview, including scheduler ownership,
activity and counts. One comparison time applies throughout. The displayed time
identifies the start of that committed sample; it is not a promise of current
ownership or permission to mutate. A writer may commit while the earlier image is
read, and an uncommitted ownership change leaves the previous committed image
visible. Mutations retain their current-state validation. Local deployment
admission and recovery observations have separate metadata; admission changes
mark the sample stale and queue a refresh.

Selected branches and independent per-job GPU facts are materialised once on the
same connection and transaction. GPU facts retain exact task/batch/generation
proof and revision identity; active, ready, waiting and uncertain facts can
coexist. Both temporary tables are dropped under the read deadline, with bounded
failure cleanup and fresh reconstruction after retries. No permanent dashboard
index or schema migration is required.

One process-wide coordinator publishes immutable samples for the trusted local
operator scope. Scoped presentation retains source filters and attention state.
It coalesces simultaneous reads and gives an Overview refresh five seconds for
scope setup, all SQL attempts and their retries, and snapshot preparation.
The SQL reader enforces the same budget when called directly. Caller
cancellation only stops that caller's wait. The coordinator waits for
cancellation cleanup before releasing its in-flight slot and refreshes every
ten seconds while subscribers exist. Data events invalidate the displayed sample and preserve
events received during a read for one follow-up; connection hints reuse a fresh
sample to avoid duplicate hydration reads. Manual refresh joins existing work.
Prerendering streams the loading shell and reuses a warm sample immediately.
Failed reads keep the original successful timestamp and an explicit stale notice;
initial failure stays unknown. No subscriber means no automatic SQL refresh.

`LegacyFenced`, the unconfigured default, retains the conservative database
rowversion fence and deletion publication/operation locks. Intervening versioned
writes or pending transactions reject that sample. Immutable evidence and graph
deletion rely on their existing atomic versioned companion writes; this mode is
not snapshot isolation for arbitrary direct SQL. The shared cache does not make
its rejected samples usable. Both modes use the same Overview budget.
The bulk current-activity query applies the exact eligibility proof. The
projection uses the shared text limit for each matching file. File drilldowns
retain their independent five-second deadline.

Attention detail remains independently read-only and paged in batches of 25,
with its own loaded time, source scope and existing conservative fence.
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
The separately reviewed physical-stop receipt keeps historical GPU uncertainty
intact. Version 1 binds code-only maintenance, version 2 additionally binds source
access, and version 3 admits only the dashboard snapshot option/configuration.
Version 3 pins the database incarnation, initial snapshot/RCSI/ADR settings,
configuration before/after hashes, commit and paired payload hashes, alongside
unchanged schema/history/current projection and exact writer-exit evidence.
The updater records durable intent before enabling snapshot isolation and inspects
the resulting state even after interruption. Startup requires fully ON. Before
startup, recovery restores the original configuration and payload pair while
leaving the option ON; transition/unknown state requires reconciliation. After
start intent or hold-release uncertainty, matching candidate bytes and the hold
remain for a fresh reviewed stop and forward recovery. No receipt settles native
work or permits automatic option reversal, RCSI/ADR changes or session killing.
Clean-slate installation uses a separate guarded one-shot GoLive path with independent clean-slate, VSS,
SQL destruction and native Codex registration acknowledgements. The GoLive path
does not offer automatic recovery or resume after an interrupted destructive run.
See [setup](setup.md) for the supported workflow and verification commands.
