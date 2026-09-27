# Hybrid passage retrieval implementation plan

Date: 25 September 2026. Baseline: `1f4aa16`.
Status: implementation in progress in `codex/hybrid-retrieval-design`.
Milestone 1 and the bounded scheduler-core slice of milestone 2 are implemented
and independently reviewed locally. The shared engine and opt-in runtime are now
implemented with bounded independent review. No production activation is claimed.
Authority: [design and acceptance contract](hybrid-passage-retrieval.md).

## Current implementation on 27 September 2026

The controlled reset SQL foundation has independent approval with 25 passing checks,
including an empty migration round trip and refusal to discard a committed rebuild
receipt. Exact dispatch-to-job and completion-to-output bindings preserve historical
replay across repeated Embed/Publish stages. Preparation now queues one captured
embedding job under concurrent/repeated requests. The existing workers rebuild and
publish real USearch files in disposable SQL without changing paused roots or captured
PDF/Visio document winners, branches or timestamps. Combined checks passed 153/153.
The bounded final gate checks complete items, canonical/source identity, model,
epoch/version, exact memberships, native files and current Full-Text population.
Independent review approved the final gate after adding the same zero-generation/
zero-membership proof used by empty-catalogue recovery. The operator CLI and
incremental updater are now implemented and independently reviewed locally.
Actual disposable SQL checks passed for the reviewed idempotent script and replay
(9/9), and real exited-process recovery settled an uncertain rebuild reservation
under deny-all admission without inference (1/1). Native updater checks cover
forward-held recovery, foreign holds and unjournalled schema, including failed
responses and resumed activation. No operational reset, real GPU inference or
production activation is included in this evidence.

The background adapter now joins the durable request, checkpoint, confirmed native
cleanup, existing lifecycle settlement and ordinary Embed requeue. Its bounded
independent review approved ownership/recovery and source-deletion safeguards.
The composed disposable-SQL test runs multiple synthetic embedding batches, seals
and publishes SQL/USearch, then runs foreground embedding/reranking through the
same shared admission gate and reads back complete cited passages. No real weights
or GPU are involved. The tokenizer disposal race is closed before late local-file
access. Focused composed/model checks passed 23/23.

The shared query engine captures matching SQL/native generation ownership before
embedding and retains it through final hydration/currentness. It uses complete
scope membership for dense root/workspace retrieval (10,000-vector capacity),
100 candidates per channel, same-ID fusion, 50 reranking inputs and checked complete
passages. The ten-second request budget includes scope resolution and all SQL/model/
assembly work; late execution keeps ownership until cleanup. Reciprocal result
positions supply the existing score field. Public corpus/search limits remain 20/50;
knowledge remains 100 with a source quota of 50.

The opt-in runtime requires the complete existing native OCR runtime and registers
both adapters into its existing slot/recovery loop. It opens models lazily from
pinned verified `J:\Models` paths and adds no model residency. Scoped lifecycle
forwarding avoids singleton-to-scoped persistence capture. Independent review
approved this bounded runtime/query slice after correcting degraded empty search
and knowledge-union signalling. Corpus retains explicit fallback status; useful
search fallback retains hit warnings; degraded empty search and any degraded source
retrieval at the knowledge-list boundary refuse explicitly. Corrected adapter
checks passed 53/53 and composition/HTTP checks 70/70, without failures or skips.

Remaining: canonical shared-GPU
inference, memory/full-search/OCR measurements; full transport and frozen English
acceptance; feature closeout, reviewed incremental deployment and live validation.
The admission maintenance fence passes a separate three-case disposable-SQL check:
it stops new admissions while an active page's ordinary receipt/release completes,
refuses uncertain/missing capacity and fails closed after actual session loss.
It is not a reset operation or authority to infer GPU release. Five-gate progress
remains 20%; no activation or operational approval is implied by bounded review.

The rebuild foundation now binds each dispatch to its exact job, including ordinary
worker claims, transition replay, checkpoint recovery and OCR/embedding continuation.
Migration binds only unambiguous existing jobs and refuses an unbound pending delivery;
downgrade refuses repeated stage jobs. A completed transition also records its exact
artefact ID, so historical completion cannot resolve a replacement projection.
The initial dispatch regression reproduced the old delivery claiming a newer job;
the output regression reproduced the old delivery returning a replacement artefact.
Corrected focused and recovery checks are recorded in the private evidence ledger.
No rebuild or production schema change has been performed.

### Controlled rebuild implementation boundaries

Use one durable operation and an artefact worklist under a new corpus epoch. Keep
the original processing record, revision, canonical extraction, document metadata,
source activities, retained processor branches and document winner bindings.
The reset clears chunks, vectors, generation memberships and classified Embed/Publish
projection descriptors; its receipt records obsolete generation paths for later
fenced cleanup. Completed jobs, attempts, outbox deliveries and audits remain.
Settled embedding GPU request rows are checkpoint projections tied by foreign keys
to discarded generations. Inventory their IDs and clear them only when their state
and native cleanup prove settlement. Preserve mini-task, dispatch, receipt and native
lifecycle history; an unsettled embedding request refuses the reset.

The first supported reset requires projection-stage work to be settled. Plan/commit
refuses queued or processing CanonicalIndex/Embed/Publish work, uncertain native work,
active query leases, incompatible descriptors or changed capture identities. Earlier
ingress stages may remain queued and use the new passage builder after restart. A
refusal is a specific operational condition to resolve; it must not silently omit
an unsettled source or mark that work complete. Any need to supersede unfinished
projection work requires a separate concrete cancellation/recovery design.
Inventory excluded canonical records as well. Suppressed completed sources can be
restored and failed projection jobs can be retried without normal ingress. This first
reset must refuse those resumable records when they are outside its worklist; clearing
their projections and treating a later restore/retry as an empty source is forbidden.
The plan reports their counts and identities before destructive work. If the actual
catalogue contains them, resolve their inclusion or deferred rebuild obligations
before committing, rather than expanding the implementation speculatively.

Capture the currently published, permitted canonical inputs, including published
paused roots, with canonical and metadata hashes and exact document publication
identity. Commit validates the database, captured epoch/version and worklist again
under the publication fence. It advances the epoch, sets serving to rebuilding and
records the reset plus pending items atomically. Repeating the same operation reads
its receipt; a new or conflicting plan cannot reset an in-progress rebuild.

Prepare each item outside a long SQL transaction, using the pinned tokenizer and
coherent passage builder. Under the fence recheck its input and source/publication
identity, then atomically insert passages and one new, explicitly bound Embed job
and dispatch. A repeated preparation returns the same job. Embed checkpoints and
Publish use the existing workers; empty canonical inputs require no model work.
Restart resumes unfinished worklist preparation and queued jobs, never a second reset.

Paused roots stay paused. A maintenance claim exception must be restricted to the
active epoch's exact worklist job/dispatch, and only to a source previously published
in that capture. Document publication preserves the captured winning record/input/
branch/processor binding; it cannot use maintenance to select another branch or
expose a previously unpublished source. Suppression, deletion, changed ownership or
changed publication identity still wins over the maintenance claim.

Serving stays rebuilding across partial publication and restarts. Reopen only after
every worklist item has its expected coherent passages and completed replacement
work, the active generation/profile/stamp matches the complete permitted published
set (or a verified empty catalogue), and Full-Text population is complete. Old evidence
references remain invalid through the changed epoch. Query and pipeline admission,
GPU drain and application stop are joined by the incremental updater before reset;
the admission lease alone is insufficient proof of maintenance.

## Implementation evidence on 26 September 2026

Milestone 1 now follows real synthetic ingress, indexing and publication into
SQL/USearch, then REST search and zero-context evidence reading. The returned
complete body equals the persisted canonical span. It includes tokenizer-bound
passages, retained document boundaries, separately indexed context headers,
epoch-bound evidence v2, canonical context slicing and interrupted Full-Text
migration replay. It remains behind unreleased construction/search options;
the deployed composition still uses its existing path until the rebuild gate.

The scheduler-core slice adds exclusive source-job or instance-bound interactive
ownership, a two-request admission bound, cancellation/deadlines and the durable
three-batch OCR turn. Tests use the actual document-OCR handoff, receipt replay,
store restart, deferred OCR, background batches and uncertain capacity. Active
cancellation retains the reservation; expiry during admission rolls back it and
records a fenced, replayable refusal. Independent review approved both slices
after the overlap-progress, combined quota and admission-expiry corrections.

Fresh focused checks passed with no failures or skips: 181 domain tests, 162
GPU/OCR/migration integration tests, 207 search/indexing/source-lifecycle/model
integration tests and two REST tests. The full solution Release build passed
with zero warnings/errors after locked dependency restore. The repository
contract and `git diff --check` also passed.
These counts describe selected suites, not whole-repository or model-quality
acceptance. Local TRX files are retained under
`artifacts/verification/hybrid-passage`, outside Git.

Native GPU inference adapters and runtime composition are still required to
complete milestone 2. The offline native CPU adapters and independently reviewed
process-incarnation recovery are implemented locally. The bounded private request
executor now follows existing admission, acknowledgement, receipt and release
protocols, including duplicate delivery, cancellation, pre-start uncertainty and
lost committed responses; its bounded independent review approved the corrected
26-case suite. Combined scheduler/OCR/model/migration checks passed 225 tests with
zero failures or skips. Milestone 3 now has the local publication/version/query
ownership slices described below; reset and integrated runtime work remain.
At that earlier checkpoint, milestones 4-5 remained unimplemented. No weights were acquired, no real-model inference was run and no
production schema or index was changed in this implementation session.
The [exact reranker source acquisition](bge-reranker-acquisition-request.md)
was separately approved and completed on 27 September, transferring exactly
2,288,188,149 bytes and reusing three verified dependencies. All conversion
packages were subsequently found in existing caches. Their
[offline adoption/export](bge-reranker-offline-conversion.md) added zero downloads
and produced a float32 ONNX model with CPU reference parity. Native runtime/GPU
and integrated quality/latency acceptance still remain.

Process recovery evidence on 27 September: 15 focused SQL/Windows tests passed
without failures or skips. They prove live/exit/PID-reuse/foreign-machine
observation, recovery across four persisted interruption states and guarded
migration roundtrips. Queries and candidate text remain private in-memory work;
only process and dispatch ownership metadata are durable. Model residency and
production activation are excluded from these results.

Publication and query ownership on 27 September: SQL preview and commit share the
exact published-selection rule, capture epoch/version stamps and roll back all
publication effects on conflict. Newly activated generations require both stamps;
Earlier recovery metadata remains readable. Immutable generation IDs and file
metadata include the stamp. Suppression/restoration and initial root deletion
advance the singleton version. Downgrade refuses loss of versioned/stamped data.

The local query lease captures a matching stamped profile/generation under the
publication fence. A shared SQL lock and authoritative process-incarnation record
protect SQL membership/vector rows and generation files across instances. Native
ANN disposal precedes explicit release through a fresh connection, including after
query-session loss. Exclusive cleanup retains live/unknown owners and retires only
verified exited incarnations. Purge and physical generation cleanup defer without
changing their durable phase while queries remain owned. The final engine still
needs to hold this lease through hydration and final currentness/eligibility checks.
Connection-loss recovery checks passed 19 tests; four focused incarnation cases
include an actual disposable SQL session kill with a live native ANN handle.
These are integrity checks, not neural relevance/latency or production acceptance.

## Delivery contract

Deliver one English passage-retrieval pipeline: coherent source spans, SQL
Full-Text plus learned dense candidates, fusion by passage identity, a trained
cross-encoder and checked complete passages/citations. Share it across corpus,
knowledge-source and existing search adapters. Keep the notes/claims merge.
Use BGE-M3 embeddings and BGE-reranker-v2-m3 as the first proposed model pair,
with GPU admission through the existing scheduler and an optional validated CPU
placement. The model pair remains subject to runtime and local relevance gates.

Discard current search projections in a controlled clean rebuild. Preserve
originals, unrelated knowledge records, model files and source registrations.
Do not implement concurrent embedding profiles or old evidence compatibility.
Preserve the source lifecycle, disclosure restrictions, public limits and all
three integration surfaces. No OCR/extractor upgrades, answer generator, new
storage platform, dashboard redesign or manuals belong to this delivery.

One owner implements the coherent change in a dedicated worktree. Use focused
test-first changes for new behaviour and invariants, review complete milestones,
then the complete branch. Independent review is required for publication/reset
and GPU ownership changes; reviewer approval is not production authority.

## Stable boundaries

Define these contracts before connecting model-specific code. Extend existing
ports when their meaning fits; add a new port only for a distinct responsibility.

| Boundary | Required meaning |
| --- | --- |
| Passage builder | Canonical input and retained provenance -> deterministic contiguous spans plus separate bounded context header. Policy identity includes tokenizer and limits. |
| Embedding provider | Verified profile + query or passage input + cancellation -> finite normalised vector and the exact embedding-space identity. Device is separately recorded. |
| Candidate reader | Resolved scope + captured epoch/generation -> bounded eligible passage IDs, independent channel ranks and hashes. |
| Generation lease | Immutable epoch/version/profile + membership + open ANN handle, acquired before query embedding; disposed after final hydration. |
| Reranker | Exact candidate IDs + complete query/header/body pairs -> one finite scalar for every input, or one explicit refusal. No new text or substitutions. |
| GPU owner | One pipeline job or one expiring interactive request, fenced to executor instance; load/run/unload within existing batch ownership and confirm release before reusing capacity. |
| Result assembler | Same ranked passage ID -> current scoped/disclosable canonical body, location and epoch-bound reference. |

Model changes do not change the candidate/ranking/citation interfaces. A new
embedding space requires reindexing; a compatible provider/device change does
not. Reranker replacement changes scoring configuration, not stored embeddings.
Do not create a universal model framework beyond these concrete boundaries.

## Milestone 1: complete passages through the real search/read interfaces

First observable result: ingest public synthetic text in a disposable SQL
database, follow the existing pipeline into durable passage rows, query it
through REST, and resolve the returned complete passage using its evidence
reference. Test scope exclusion and successor publication through the same path.
Use synthetic model providers; real-model acquisition is not a prerequisite.
Keep this unreleased until the publication/reset gate in milestone 3.

Affected areas:

- [TextChunker](../../src/FluxKnowledge.Application/Indexing/TextChunker.cs),
  new `PassageBuilder`, `CanonicalIndexStageWorker` and bounded canonical readers.
- `TextChunkEntity`, `IndexStateEntity`, the DbContext mappings and a new explicit
  migration for passage/profile/epoch fields. Retain SQL integer Full-Text keys.
- [SqlCorpusRetrievalReader](../../src/FluxKnowledge.Infrastructure.SqlServer/Search/SqlCorpusRetrievalReader.cs),
  [CorpusRetrievalService](../../src/FluxKnowledge.Application/Search/CorpusRetrievalService.cs),
  `CorpusEvidenceCodec` and `CorpusCitationMapper`.

Implement the design's 192-token target, dual hard body bounds, real document
boundaries and bounded sentence overlap. Store/index the shared header/body
search input. Return the whole selected body. Replace ordinal concatenation
with bounded canonical slicing; Unicode/UTF-16 semantics and provenance remain
checked. Introduce evidence v2 with epoch binding and a named old-reference
refusal. No decoder or translation for old passage IDs.

Meaningful checks: determinism, strict progress on long tokens, emoji/combining
characters/CRLF, overlap without duplicated read context, boundary answers,
metadata-only discovery, exact-body priority, valid multi-page citations,
large-document bounded reads and complete-line disclosure failures. Source
readback must equal the search passage when requested context is zero.

Extend `TextChunkerTests` into passage-policy coverage, the existing evidence and
citation tests, `ScopedCorpusRetrievalTests` and `CorpusRetrievalEndToEndTests`.
Do not change pipeline enum ordinals or invent Office table structures.

## Milestone 2: quality models and shared GPU execution

Local implementation status, 27 September: the scheduler core and
[native CPU adapter slice](bge-native-cpu-adapters.md) passed focused independent
review. Tokenisation and CPU outputs match the pinned offline reference; complete
512-token inputs and native/file lifetimes are verified. The shared admission gate
and exact-owner execution read are implemented but unregistered. GPU ownership,
native request payloads, owner-loss recovery and measured GPU/full-search behaviour
remain required; this milestone is not complete.

Work in the existing `Infrastructure.Inference`, `Application/Gpu`, SQL scheduler
store, local OCR admission/dispatch wiring and model-store boundaries. Add the
embedding/reranker adapters and the minimum interactive request-owner records.
The handoff contract currently requires a parent job; make owner exclusivity a
database and application invariant, rather than using fake source jobs.

Extend the one physical GPU slot to admit allowlisted OCR and retrieval runtimes.
Persist the executor/instance binding and deadline for interactive requests;
keep bounded request payloads in memory. Never replay a lost query after restart.
Preserve receipt/admission-generation fencing, trusted release and recovery.
Load/run/unload within one admitted foreground request or bounded background
batch; unload the embedding session before loading the reranker. Confirm native
execution and allocations have ended before releasing capacity. Cancellation
cannot release live GPU work. Baseline scope excludes keeping models loaded
between requests, idle-residency states and resident-owner/reuse extensions.

Add only the design's OCR turn exception to normal selection: at most three newly
admitted non-OCR retrieval/embedding batches while valid OCR is waiting, then one
oldest eligible OCR page. Recognise OCR by validated allowlisted runtime/workload
identity and preserve existing lanes: actual document OCR uses `DocumentIndexing`,
so a lane-only predicate is incorrect. Count interactive and document-embedding
batches together, explicitly excluding OCR; select the oldest valid OCR across
its applicable lanes when owed and preserve FIFO among OCR waiters.
Store the counter in existing durable scheduler state, atomically with admission
and its receipt; preserve it across busy/deferred decisions, request timeout and
restart. An owed but capacity-deferred OCR turn cannot be bypassed. Remove invalid
waiters without reserving capacity. Reset on OCR admission or no remaining valid
OCR waiter. Never interrupt an active OCR page. Background batches yield after
at most four passages; foreground batches contain exactly one bounded request.

First execute this scheduler path with synthetic executors in a disposable
database, including an OCR-shaped task and a retrieval-shaped task sharing one
slot. Then implement real offline adapters:

1. Recheck exact BGE inventory and tokenizer receipts in `J:\Models`. Verify
   tokenizer IDs/masks/pooling/normalisation and .NET outputs against reference
   fixtures, including pair encoding and full input-length detection.
2. Prepare an exact reranker acquisition/export specification using the pinned
   upstream identity in [model candidates](semantic-model-candidates.md). Inspect
   all relevant caches. Missing weights or conversion dependencies require exact
   acquisition approval; this plan supplies none. Existing artifacts are reused.
3. Produce a reproducible ONNX export only from verified approved inputs, record
   tool/input/output hashes in the central store, and prove reference logits and
   DirectML execution. Configure sequential sessions and actual provider reporting.
4. Measure each model's load/run/unload time and peak/released GPU memory before
   setting the admission resource bound. Measure full-search latency with those
   loads/unloads included. Do not use file size as peak memory or preload sessions
   outside admission. Across-request retention remains a conditional decision.
5. Measure same-model CPU as an execution option. Do not trade down model quality
   or launch CPU fallback concurrently with an abandoned GPU request to meet a
   latency chart. Any numeric/quantisation differences must pass relevance checks.

Checks: cache hits make zero downloads; misses fail closed; unavailable/reparsed
J: never falls back; authorised concurrent acquisition transfers only missing
content once. Ordinary tests use synthetic fixtures, not real weights. Test
exclusive owner constraints, cancellation before/after admission, owner loss,
late/duplicate callbacks, process death, unload-before-release, OCR handover,
instance recycle and forged/stale release evidence. Under continuous retrieval
and embedding traffic prove each owed OCR turn occurs before a fourth new
non-OCR retrieval/embedding admission. Include the actual document-OCR handoff
in `DocumentIndexing`, proving it resets rather than increments the counter.
Also cover restart/replayed receipts, cancelled OCR,
capacity deferral and an active page completing without interruption. Verify
search deadlines/fallback while OCR runs, and no early release on timeout.
Required independent concurrency review precedes enabling this adapter, even
in a release candidate.

Extend `LocalModelStoreTests`, `GpuSchedulerContractTests`,
`GpuExecutorContractTests`, `GpuSchedulerCoordinatorTests`, existing SQL GPU
admission/dispatch/handoff tests and worker recovery tests. Add targeted inference
parity fixtures and an opt-in offline model probe outside ordinary CI.

### Conditional decision after baseline measurements

Do not add retention-specific scheduler code in this milestone. Record baseline
model loading, inference, cleanup and memory, full-search p50/p95, and OCR wait
p50/p95/max and throughput at one/two callers plus sustained traffic. Establish
whether loading materially causes an unmet target. Only then propose a bounded
retention experiment using the same model pair/precision/inputs; it is not a
production scheduler activation. Any model acquisition retains its separate gate.

If measured full-search benefit and memory headroom justify the added state,
write a separate retention design with ownership, unload, uncertain release and
recovery tests, and obtain independent review before committing to those changes.
Compare OCR wait and degraded rates as well as search latency. An owed OCR turn
must survive compatible-model reuse. Otherwise keep the baseline; no speculative
resident-owner fields, idle timers or retained-session recovery machinery.

## Milestone 3: coherent publication, deletion and rebuild

This integrity milestone is required before exposing new semantic results.
Update `EmbedStageWorker`, `PublishStageWorker`, `SqlPipelineStore`,
`SqlStageTransitionStore`, `SqlSourceScanStore`, `SqlSourceDeletionStore`,
`SqlNativeOperationStore`, `SqlDerivedIndexRecoveryStore`, generation entities
and the existing USearch builder/validator/recovery components together.

Read/write embeddings in bounded resumable batches. Persist one vector for
each passage/revision/profile, with exact input and payload hashes. Retries reuse
verified vectors; generation memberships reference them. Introduce the singleton
epoch/version fence first in the consistent lock order for all affected mutation
paths. Share the exact published-selection helper between preview, commit,
search eligibility and rebuild. Include a pending branch only if it would win;
unfinished unrooted revisions must not displace published ones.

The current unreleased checkpoint implementation uses the existing unplaced
`IndexGenerations` draft, with a unique nullable Embed-job owner and corpus stamp.
Each job delivery embeds at most four complete `SearchText` inputs and commits
their exact input/payload hashes atomically. Continuations use the existing fenced
retry transition. A reclaimed delivery finds the same draft and skips saved inputs;
a profile/epoch mismatch refuses reuse. Final sealing checks the complete membership
and validates payloads in keyset pages before the normal Embed-to-Publish transition.
Deletion retires an owned unplaced checkpoint in its first committed phase and
captures cleanup before releasing the job reference. Downgrade refuses to erase
nonempty checkpoint ownership or input hashes. The worker path requires an explicit
batched provider; real BGE background scheduler composition is still an activation gate.

Build immutable candidate files outside transactions. Recheck epoch/version,
membership and ownership under the publication fence, then atomically publish
the winning document selection, job completion and index pointer. Retry snapshot
conflicts with the design's bounded policy. Deletion/suppression immediately
changes SQL eligibility; stale semantic generations refuse until caught up.
Generation leases pin the model/index before embedding and keep files plus SQL
membership/vector rows alive until release or proven owner death. Register the
lease and owning instance in SQL for cross-instance cleanup. Reset drains
those leases. Logical deletion remains immediate through final eligibility.

Add a targeted plan/commit rebuild operation, not a startup reset. Plan output
names the database/epoch, precise search projections, sources, maintenance
window, model readiness and restart/downgrade path. Prove admission has stopped
and search/read admission has stopped, with owned work and query leases drained
before reset. Advance epoch and clear only authorised derived search state.
Distinguish search projections from retained canonical artifacts and lifecycle
history in the manifest; create explicit rebuild work so old completed stages
cannot suppress reindexing. Preserve source/model/knowledge data. Persist progress so
an interruption leaves explicit rebuilding status and resumes safely.

Focused integration matrix:

| Race/failure | Required result |
| --- | --- |
| Two publishers and selected/unselected branches | Correct public winner, complete membership and monotonically advancing pointer |
| Publish against pause/delete/suppression/new revision | No withdrawn source resurrection; last good permitted publication retained |
| Crash before file placement, before commit or after commit | Either old coherent state or new coherent state; orphan candidates recoverable |
| Query against pointer change/reset | One captured profile/generation, final eligibility enforced; old epoch refs rejected |
| Delete/cleanup from another instance during a scoped query | No deleted final result; active file handle and SQL membership/vector snapshot remain valid |
| Reset interrupted/replayed | Bounded resumable reset, no unrelated data/model loss, explicit unavailable status |

Extend `SqlToUsearchRebuildTests`, `UsearchGenerationTests`, source deletion/scan
and native operation tests. Include real SQL transactions and two independent
connections for races; mocks do not prove these invariants. Obtain independent
review of this complete change and reconcile blocking findings.

## Milestone 4: one hybrid candidate and ranking engine

Connect the verified components behind the existing search contracts. Refactor
`CorpusRetrievalService`, `HybridSearchService`, `ReciprocalRankFusion`,
`KnowledgeQueryService`, SQL readers and `UsearchAnnIndex` to use one passage
engine. Preserve public shapes and limits; adapt the knowledge total limit to
a bounded source quota instead of passing invalid limits to the inner search.

Implement lexical 100 + dense 100, union by passage ID, RRF 60, exact-body tier,
rerank 50, per-document diversity, span deduplication and final revalidation.
Implement full scoped cosine up to the configured 10,000-member bound and explicit
refusal above it. Never global-top-k/filter as the scoped algorithm. Validate
ANN recall against exhaustive cosine on a bounded test corpus as well as source
answer recall. Expose the same complete selected passages on every adapter.

Run lexical work alongside admission. Observe one overall deadline, all-or-nothing
reranker scores, explicit busy/unavailable/timeout/index-updating/refusal codes
and bounded shortlist top-up. A fallback is available service, not proof of
successful semantic execution. Do not silently truncate pair inputs or interpret
model logits as probability that a passage is true.

Tests cover exact identifier versus similar wording, a paraphrase with no shared
keyword, identical passage IDs across channels, header-only matches, duplicate
spans, empty/no-answer queries, partial/bad/late reranker outputs, changing scope,
wrong-model vectors, corrupted generation and FTS population delay. Verify
MCP/REST/CLI parity, query/public limit validation and note/claim merge behaviour.
Add these to the existing search, knowledge and endpoint suites.

## Milestone 5: bounded acceptance and operational readiness

Prepare the frozen baseline/cohorts early while the first slice is built. Run
the complete pipeline once components are joined; the design defines cohort
independence, stage metrics, acceptance targets and a bounded correction policy.
Use real source questions and report failures at extraction, passage construction,
candidate retrieval, shortlist, reranking or evidence assembly. Keep private
corpus material, labels, vectors and receipts outside Git.

Record model/profile/ranking fingerprints, runtime/device, corpus size and scope,
source coverage, per-class counts, latency distributions including model load and
unload, OCR wait p50/p95/max, OCR throughput, turn counts and degraded rates.
Measure two callers, sustained search, background indexing and OCR handover.
The baseline is measured with no model retained between requests; report the
observed seconds corresponding to the admission bound, not a fictional deadline
guarantee. Warm-hit rate applies only to a separately justified retention trial.
Agree any necessary quality/latency trade-off explicitly; a fast lexical fallback
is not a successful neural latency measurement. No deployment follows automatically.

The release package contains reviewed schema/reset plans, build/test evidence,
exact model manifests already satisfied locally, feature flags, maintenance
status and restart-safe resume. The incremental path uses forward-only held recovery after its first
schema attempt, because non-transactional Full-Text changes may precede a recorded
migration receipt. A failure must never automatically restore old binaries or
downgrade that schema. Original payload/configuration remain available for inspection.
Use the existing incremental IIS updater for supported steps; extend its reviewed
targeted capability if necessary. Prepare its `-PlanOnly` output for the actual
target before requesting operational approval. No full clean-slate path is implied.

After explicit deployment authority and the required independent operational
review, perform the named deployment/reset, live search/read/scope checks and
restore admission. An unreconciled failure leaves rebuild/degraded state visible;
it does not initiate repeated resets, model downloads or source deletion.

## Verification and effort checkpoints

After the first two substantial implementation batches, demonstrate milestone
1's real-interface result and synthetic GPU handover. If unavailable, identify
the concrete blocker and revise the smallest delivery slice before adding more
foundation work. Complete a coherent milestone before broadening tests.

Example focused commands, run from the implementation worktree against explicitly
configured disposable SQL fixtures, never a production connection:

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~Indexing|FullyQualifiedName~CorpusEvidence|FullyQualifiedName~CorpusCitation|FullyQualifiedName~Gpu|FullyQualifiedName~KnowledgeQuery"
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~Search|FullyQualifiedName~Indexing|FullyQualifiedName~Gpu|FullyQualifiedName~SourceDeletion|FullyQualifiedName~LocalModelStore"
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~CorpusRetrieval"
pwsh -NoProfile -File tests/native/repository-contract.ps1 -SourceRoot .
git diff --check
```

These are starting filters, not a substitute for added reset/scan/root/transport
coverage and full required closeout checks. Inspect fixture prerequisites and
observed skip counts. Build with the repository's zero-warning requirements.
Use `scripts/dev/complete-feature.ps1` for implementation feature closeout;
do not manually substitute its sequence or infer deployment authority from it.
Update architecture, roadmap and integration contracts as behaviour becomes real.

## Design review record

An independent Astra review on 25 September 2026 approved the initial planning approach
with no blocking architecture or concurrency findings. It inspected the current
publication, scope, source-deletion, ANN and GPU ownership implementations.
Its clarifications were incorporated: leases protect SQL membership/vector data
across instances as well as files; the reset barrier covers search/read requests
and query leases; the reset manifest separates projections from retained artifacts
and lifecycle records. This review supplies no model-acquisition or production
authority. Model adapter parity, reranker acquisition/export, integrated accuracy
and shared-GPU latency remain future implementation evidence.

The subsequent scheduler-scope revision removes mandatory retention between
requests and adds a bounded OCR turn policy. Focused independent Astra review
identified one required correction: actual document OCR uses `DocumentIndexing`,
so fairness must classify by validated workload/runtime identity, not lane alone.
The design and tests now explicitly preserve lanes, exclude OCR from the search
counter and cover its actual handoff. The reviewer confirmed the correction and
approved the revised design with no remaining blocking findings. No implementation
or runtime measurement was performed during this documentation revision.

## Bounded checkpoint implementation evidence

On 27 September 2026, independent review approved the unreleased batching,
checkpoint, publication and unavailable-projection recovery slice after correcting
recovery between deletion phases. Normal batch continuation also preserves source
activity state. Fresh verification passed 481 integration, 83 domain and 2 REST
tests, with no failures or skips; the Release solution had no warnings or errors.
The disposable pipeline test covers saved batches, reclaim, sealing, publication
and a native ANN result using model-free embeddings. A draft exceeding 128 vectors
also verifies paged checksum order. This evidence does not complete milestone 3
or establish real BGE GPU, full-search latency, OCR waiting or English relevance
acceptance. Runtime composition, reset/drain and the unified hybrid engine remain
required before activation.

On 27 September, the foreground inference session passed 45 focused tests and
independent bounded review. Tests cover actual disposable-SQL dispatch, sequential
model lifetimes, cancellation and unconfirmed native allocations. No real GPU
model was loaded. Background handoff/checkpoint tests subsequently passed 35
focused checks: one request stores at most four passage references; the ordinary
Embed worker queues it; an exact acknowledged process/claim identity authorises
atomic vectors and completion digest. Claim replay, contradictory ownership fences,
GPU-state draft recovery and guarded empty-schema round trips are covered.

The subsequent observable result is that queued request passing through the native
adapter, durable checkpoint, confirmed cleanup, existing lifecycle settlement and
ordinary Embed requeue, then sealing/publication. Required joining checks include
response loss, native disposal uncertainty, process exit versus alive/unknown owners
and source withdrawal. These checks and opt-in registration are now implemented,
as recorded above. Activation still requires the maintenance/reset capability and
real measurements, not a new scheduler or model residency. Real model measurements require the canonical shared
physical slot and the reviewed deployment path for its ownership schema.
