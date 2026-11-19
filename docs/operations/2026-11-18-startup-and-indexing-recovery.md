# Startup and indexing recovery, 18 November 2026

Normal file reconciliation now uses size and last-write date. Source content is
hashed on first ingestion and after a detected metadata change for duplicate
identity. Command-confirmation hashing remains unchanged.

## Causes and corrections

The investigation found repeatable code costs, alongside stale records from
failed development deployments:

- Normal reconciliation and loading repeated source, retained, ANN and model
  payload verification. Normal readers now use metadata, bounded format/length
  checks and offline local model paths. Recovery reuses validated checkpoints.
- Buffered retention also reused the publicly mutable classification array after
  discovery had completed its digest. A private bounded copy now preserves the
  captured bytes without another file read or hash. Prefix-only captures still
  stream the held handle. Metadata-only retained reuse, length refusal and
  cancellation have explicit coverage; 30 capture and 213 affected domain checks
  pass.
- Startup reconstructed passage-window decisions from retained canonical text.
  Schema 80 stores exact per-chunk eligibility bindings, including unavailable
  results, and invalidates affected caches when inputs change. Bounded resumable
  backfill excludes Deleting sources; migration itself performs no backfill.
- Source status and admission revisited historical data or left queued work
  asleep. Current-activity lookups and durable wake signals correct those paths.
- Dashboard file status repeated the full earlier-policy eligibility proof for
  every activity. Summary and detail now left-join its unique eligible IDs in
  the same SQL statement, preserving exact reasons and Deleting-root counts.
- Large-source removal issued repeated history and reference scans. Withdrawal,
  vector/chunk payloads, memberships, manifest references and dependent segments
  now use bounded work. Committed retirement intents survive interrupted file
  cleanup; fresh source, pin, native-owner and original-session checks still
  precede deletion.
- A publication-lock timeout during retirement admission incorrectly moved the
  operation to attention. Classified contention now defers before file authority
  is granted. Cancellation and unexpected errors still propagate.
- Each membership page also reclassified the source's entire execution history.
  At the observed 217,685 tasks, that made small metadata batches repeat costly
  historical work. Membership pruning now uses fresh scalar execution/cleanup
  guards and exact page protection. Full ownership and shared/cross-root checks
  remain mandatory before every payload/graph unit and native-file operation.
- Even after that correction, a 1,000-row delete repeatedly paid the same page
  proof and transaction costs across extensive generation fan-out. Qualification
  separates the unchanged 1,000-vector inspection page from a 16,000-row deletion
  ceiling, preserving complete-page checks, prefix progress and rollback.
- Embedding and publication workers treated classified transient SQL failures
  as permanent stage failures. The deployed correction preserves their original
  delivery leases and checkpoints. Known unwound publication candidates are
  abandoned under exact ownership; an already committed activation is retained.
  Unknown SQL and file/native errors keep their existing refusal behaviour.
- The sequential canonical reader omitted the persisted window-version field.
  Its entity initializer supplied a new GUID on every read, making embedding
  retry confirmations unstable. The reader now materialises the stored field
  before its large text columns. Repeated reads, transaction rollback, real
  preview/commit and stale-version refusal have focused SQL coverage.
- A newer stored OCR execution lost reopenable native cleanup evidence. Its
  exact original failure trigger remains unproved. Model-free current-schema
  [stored-execution maintenance](ocr-stored-execution-recovery.md) preserves its
  execution/result while requiring fresh physical proof before settlement.
  Scheduler admission no longer holds the publication fence while waiting for
  the maintenance gate. Ordinary deployment checks definite uncertain capacity
  early and does not repeat an unproved initial stop during journal containment.
- The final graph guard lacked an OCR cleanup check. It now refuses unresolved
  execution before deleting vectors or text. A previous uncertain OCR request needs
  fresh physical cleanup proof; its old scheduler release receipt is insufficient.
  Version-2 cleanup-only stopped maintenance records that proof without completion,
  requeue, DDL, model acquisition or source backfill. Its current-schema start and
  hold-release admission include the preserved helper, and its rollback packet
  uses the existing recovery classifier format.
- Once native retirement completed, a membership safety query timed out over
  extensive generation history. Its row-goal plan walked generations with
  repeated membership seeks. A reproduced 32,768-generation case now uses a
  typed vector page and a query-local row-goal hint, preserving the complete-page
  retirement proof. Microsoft documents this option for
  [queries using `IN` or `EXISTS`](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-query?view=sql-server-ver17).
  The batch also collects unique referenced generation IDs before checking their
  state, avoiding repeated generation lookups under high membership fan-out.
- Three exited failed-deployment writer records prevented recovery auditing.
  An independently reviewed, exact-incarnation operation removed only those
  records. The normal native audit subsequently sealed the recovery inventory.
  The classifier also recognises the current typed deployment receipts.

## Retained hashing decisions

| Item | Purpose and effect on frequent work |
| --- | --- |
| 5: text, chunk and passage identity | Binds stored text, disclosure and search inputs to the correct revision. Unchanged passage validation and stored window decisions are reused. |
| 6: embedding and recovery digests | Binds inputs, vector results and native receipts. Completed checkpoints are reused without rereading full history. |
| 7: index identity | Optional whole-file corruption scans are removed from normal loading. Small membership, publication and ownership identities remain. |
| 9: path/configuration/runtime keys | SQL Server does not require these hashes. The application uses compact keys in computed columns, indexes and compatibility references. They process bounded strings rather than file bodies. |
| 10: command confirmations | Kept unchanged by explicit choice. Their small request/token values have negligible startup disk cost. |
| 11: search/cursor/knowledge keys | Search deduplication uses direct comparisons; pagination uses protected bindings. Cached passage integrity, saved-knowledge identities and framework token protection remain. |
| 13: token-hash embeddings | An embedding algorithm for model-free configurations and tests, rather than file verification. It runs when producing embeddings; configured BGE can replace it. |
| 14: verification tooling | Test/probe/benchmark hashing remains outside normal reconciliation. Full feature closeout runs after implementation and live acceptance. |

The sole model store remains `J:\Models`. Acquisition still requires explicit
approval and integrity verification; normal loading performs no weight rehash
or implicit download. Deployment fingerprints remain separate release evidence.

## Qualification and release

The combined `bc44ff03` runtime deployed the transient SQL, larger bounded-delete,
Overview and source-editor corrections. A subsequent `8bf741d2` update reached an
uncertain GPU reservation after copying its Outlook payload. Its two drain windows
expired with Web still on `bc44ff03`, the Outlook task disabled and the deployment
hold retained. This mixed installed state is preserved as the next plan's exact
baseline; the failed Apply was not replayed. The reviewed `14b5ed32` forward
deployment then recorded fresh native proof, settled that original execution and
activated matching Web/Outlook payloads. Schema 80 and helper policies are
unchanged. The hold is released, the original disabled Outlook policy is preserved,
and the separate preview was restored. The original OCR parent consumed the saved
result and completed extraction, normalisation and chunking; embedding continues
through its ordinary pipeline.

Focused qualification passed 43 cases. Combined retirement/source checks passed
170 cases, with three existing explicit scale opt-ins skipped. The Release build
reported zero warnings and errors. Earlier unchanged window, migration and
1.3-million-vector deletion qualification remains applicable; the latter completed
in 16m26s. New native-file tests prove 16/17 boundaries, owner blocking, foreign
operation/target exclusion, partial deletion, session/lease loss, fresh retries,
lost acknowledgements and references inserted between phases.

Equivalent 16-manifest reference workloads reduced logical reads from 480 to 54
for a giant-body fixture and from 192,384 to 24,024Ã¢â‚¬â€œ28,024 for typical bodies.
Embedding-reference reads fell from 26,912 to 1,682. These are isolated SQL
measurements, rather than cold-start wall-clock benchmarks.

The new membership fixture reproduces the timeout with a 1,000-vector page,
32,768 unretired control generations and 1,048,576 foreign memberships. A
hint-only experiment isolates the row-goal effect; the final parameterised SQL
passes both full-page protection cases and drains through vector, chunk and graph
cleanup in 36 units. Membership reads stay at or below 9,165 per unit and all
50,000 control vectors and foreign memberships survive.
The affected source-deletion suite passed 73 cases and the web status/concurrency
suite passed 144, each with two existing explicit scale opt-ins skipped.
The complete Release build has zero warnings or errors.

An additional owned-history case covers 1,024 retired generations per vector.
The prior cached small-to-large plan performed 1,024,000 generation lookups and
3,147,014 logical reads. The final batch performs 1,025 generation lookups and
16,297 total proof reads in that order; the reverse order chooses one catalogue
scan and consumes 14,532 reads. It still examines every page membership. Both
full-page protection cases pass. The 30,000-file status fixture drops from
205,340 to 39,703 logical reads with identical categories and reasons; this read
reduction alone does not establish the two-second live overview criterion.
The explicit 30,000-file/20,000-job public-overview qualification passed 24/24
reads with 682 overlapping writes, 1,159.7 ms p95 and 1,456.2 ms maximum. The
30,000-file eligibility summary took 960 ms warm. Final production acceptance
remains separate from these disposable-environment measurements.

## Live acceptance

Release `87b68ae1` and one independently reviewed targeted statistics refresh
restored successful dashboard sampling and refresh. The root primary-key
histogram described one long-deleted root and none of the three current roots.
The causal disposable comparison reduced status reads from 399,032 to 9,815
without changing the 30,000 selected file categories. A fresh rendered HTTP
sample and browser refresh both succeeded. Hidden reserved notices and streamed
initial placeholders are excluded from the HTTP observer's final status check.

The additional membership/cleanup-only release is deployed. Its 281 combined
source/OCR/deployment checks passed. With 217,685 tasks across 20,001 jobs, the
previous full OCR classification used 1,705 reader commands; the new complete
membership unit uses 22 and 10,017Ã¢â‚¬â€œ10,021 logical reads. Both OCR and embedding
history variants passed. Exact CLI, lost-acknowledgement, physical-proof and
composed deployment guards also passed independent review.

The first cleanup-only activation attempt stopped at preflight, before creating
a hold or deployment state, stopping FluxKnowledge, recording SQL cleanup or
swapping a payload. PowerShell dynamic-module closures hid script-local helpers.
An isolated nested-scope regression reproduced both the binding and state-writer
failures; the correction keeps those synchronous callbacks in their existing
scope. The separate authorised preview pause was restored successfully. The
failed plan is retained as evidence and will not be replayed.

Live removal completion, the final original-file metadata comparison and fresh
semantic acceptance remain open. No completion is claimed for those checks.
All 11,608 manifests and 13,102 segments have been reclaimed and acknowledged.
Cleanup-only stopped maintenance recorded fresh physical proof without changing
the previous OCR task, parent job or results. The new ordinary recovery packet
passed the native audit, sealing 23 packets with zero index pins. One fresh
native command resumed the existing deletion operation without replay or manual
reset. All 118,709,880 historical memberships have now drained. Final record purge
stopped on a SQL timeout, which recurred after the admission correction. A bounded
capture identifies its shared-batch ownership query: two large JSON ID sets
consumed 35,388,732 logical reads and 20,195 ms CPU in the observed statement;
other callers waited behind the purge transaction. The typed-set correction is deployed as `12f2d406`. Materialised primary-keyed GUID sets preserve that full
foreign-task check. Full OCR classification now uses one equivalent candidate-set
query instead of approximately 1,700 small queries. The final execution graph
commits at most 1,000 task/request identities per unit, retaining parent/source
anchors and batch-wide evidence until the last task leaves. Every unit obtains
fresh ownership, cleanup and capacity proof. A result bound to a foreign batch is
refused before mutation; no phase or schema change is required.

The complete synthetic 217,685-task/20,000-parent workflow includes dispatches,
results, evidence and scheduler receipts. Its earlier final transaction took
55.7 seconds. The revised complete removal passed in 14m33s, with a 10.14-second
maximum purge call against the unchanged 30-second bound, while preserving the
control record. Recorded command timings peaked at 2.03 seconds; reader timings
cover opening, while the complete purge-call measurement includes consumption
and bounds the transaction. Both 32,768-batch cached-plan orders also passed.
Focused checks cover a batch spanning pages, cancellation rollback, lost
acknowledgements, fresh-process continuation, mixed OCR/embedding history and
foreign ownership inserted between commits. Release `12f2d406` activated that graph correction on schema 80. Its native
recovery audit sealed 27 packets with zero pins. The same removal operation is
committing payload pages; final removal remains open. The original files have
not been touched.

The next combined correction separates a 16,000-row vector/chunk purge ceiling
from the unchanged 4,096-row initial withdrawal and 1,000-task execution pages.
Every unit retains the full fresh ownership proof. A combined fixture has
217,685 tasks across 20,000 parents and 32,017 vector/chunk pairs, including
4,096-byte vectors, 2,048-character chunks and eligibility rows. Full, partial,
replayed, cancelled and fresh-process pages pass. The maximum complete call was
12.035 seconds against the unchanged 30-second bound; a concurrent publication
waited 12.024 seconds, and recorded SQL statements peaked at 2.047 seconds.

The enlarged regression also exposed a history-dependent initial-withdrawal
UPDATE timeout. Running its preceding mixed-history case reproduced it. The
update and its fresh postcheck now use the IDs already selected under the same
serializable transaction instead of repeating five ownership joins. The 4,096
limit and final root-wide cursor refusals remain. The reproduced sequence and cancellation/ownership/replay checks
pass; the measured UPDATE maximum is 50.3 ms. A competing source-ownership write
remains blocked until the unit commits. No timeout was increased.

Physical cleanup now handles at most 16 targets of one storage kind per batch,
with fresh shared-reference reads, per-target gate/lease checks and a single
attempted-prefix receipt transaction. A matched disposable real-file comparison
removed 120 unshared retained blobs and 24 previous directories while preserving
eight shared blobs and the original-file control. SQL round trips fell from
3,064 to 347 and elapsed worker time from 4.832 to 1.093 seconds. These are
staging measurements for physical cleanup; combined release acceptance remains
pending.

Combined qualification now passes 213 affected domain checks, 157 SQL
integration checks and 105 web checks, with six existing opt-in skips and no
failures. The complete Release build has zero warnings and errors. The exact
combined deployment plan and live acceptance remain release gates.

The five current MoE failures were traced to three SQL pre-login timeouts, one
SQL execution timeout and one historical native-file sharing error. All 104
focused SQL, GPU handoff and candidate-ownership checks passed for the deployed
SQL correction. Four normal publication retries completed. The fifth Embed retry
exposed the unstable canonical-reader binding described above. After its corrected
reader was activated, one fresh confirmed retry preserved its checkpoint and all
five selected records are now published. Arbitrary I/O errors are not automatically
retried. The fresh native recovery audit sealed 26 recognised packets with zero
pins after the forward deployment.

Observed recovery evidence already includes a normal 1,777-file reconciliation
with zero newly indexed, deferred, blocked or failed files, and 32 passage flag
bindings/calculation times preserved across the latest restart. All currently
active vectors have flags. The excluded source has received no backfill flags.

Runtime `12f2d406` passed the required held loopback probes within 13.05
seconds of the observed IIS worker birth; the combined `a669a51d` release
passed within 23.33 seconds. These bound the respective warm restart
observations; they do not establish trained semantic or operating-system cold
readiness. Live MCP, REST and CLI literal search/cited readback all matched their
selected passages. During purge, keyword retrieval truthfully reported the
`index-updating` lexical fallback. Final post-removal semantic acceptance remains
open.

No controlled operating-system cold-start benchmark or per-application physical
disk attribution was performed. The older native heap-corruption cause remains
unproved. Necessary model/ANN opening can still read local files. These limits do
not justify restoring repeated verification scans.

Post-release monitoring found a further recovery defect after an initially
successful segmented refresh. Code and regression tests showed that temporary
refusal of shared maintenance ownership was reported as a permanent configuration
error, stopping future probes. A
separate state/generation read could also observe an old generation after its
concurrent retirement. The corrections use the existing snapshot-conflict path
for the specific ownership refusal and capture projection metadata inside one
short transaction with the established recovery/publication lock order. Genuine
invalid-generation and native failures retain their previous classifications.
The original exception is now logged with process, attempt and generation
identifiers; public status remains sanitised. The historical live exception was
not retained, so valid current SQL metadata does not prove which path caused it.

CPU search had an independent lifetime latch: initial memory-pressure refusal
was cached until restart. Only refusal before model allocation, followed by
confirmed owner release, now permits a shared retry after 60 seconds. The
32/24/16 GiB checks remain. Later loading errors, unknown native failures and
uncertain disposal do not retry. Stopping the service cancels its retry delay
while retaining the existing model-drain and native-cleanup ownership checks.
Synthetic tests do not load models or acquire dependencies. This startup
correction passes 115 SQL recovery/retirement checks, 276 web checks and 19
CPU/domain checks; eight existing opt-in web checks are skipped. The Release
build has zero warnings and errors. Release `4004ae4c` passed its required held
loopback probes within 21.89 seconds of worker birth, with 32 sampled passage
bindings and calculation times unchanged. Recovery subsequently remained healthy
while normal publication advanced. Nine additional current publication failures
from the same maintenance-refusal defect completed through confirmed native
retries, preserving their existing embedding artifacts.

The shared SQL projection-read fence needed a further narrow correction:
`sp_getapplock` timeout (`-1`) now takes the existing snapshot-conflict path,
without consuming the generic I/O retry budget. Cancellation and other negative
lock outcomes remain distinct. Regression coverage holds maintenance ownership
for six probe cycles, then verifies recovery after release.

After vectors, chunks and execution requests drained, final source graph cleanup
exposed an unbounded five-way audit deletion. The replacement selects at most
16,000 distinct audit IDs through all five ownership scopes and deletes only
that frozen set. Source anchors remain until a fresh empty audit check and final
principal deletion can share a transaction. Overlapping and mixed references,
late audit events, cancellation and acknowledgement loss have SQL coverage.

Retained artifacts also require a byte bound: counting rows alone does not bound
large text deletion. Each page selects at most 4,096 IDs and 64 MiB of SQL-reported
text plus metadata lengths, without loading those bodies into the worker. One
oversized first artifact is allowed to guarantee progress. Source revisions,
records, jobs and retained-source references survive intermediate pages. Final
artifact emptiness and principal deletion share the same transaction. Tests
cover 48/48/60 MiB separation, metadata-only budget consumption, an oversized
artifact, replay, cancellation and unrelated records. This is a selected-payload
bound, not a transaction-log bound; the existing statement and whole-unit timing
limits still apply. Deployment and completion of the existing removal operation
remain open.

Combined qualification passes 274 affected SQL checks without skips. The final
graph fixture includes 35,000 additional revisions, 20,000 records, 60,000
activities, 100,000 artifacts and 654,575 audit events. After both page bounds,
its maximum complete cleanup unit is 5.324 seconds and maximum SQL statement is
1.804 seconds, within the unchanged 30-second and 15-second qualification limits.
The fixture's artifact pages never exceed 4,096 rows. These are staging bounds,
not a production throughput estimate. The full Release build has zero warnings
and errors.

Local main's journal diagnostics changes were merged while preserving size/date
reconciliation and both helper policy controls. Focused UI and native helper
contracts pass. The retained protocol-v1 helper is compatible with the new app;
some older helper failures retain the generic `unavailable` classification.
The routine update does not refresh that helper or alter its volume allowlist.

Release `5e9af73e` preserved schema 80, helper policy and the sampled passage
bindings. Its warm held-readiness bound is 14.54 seconds from worker birth.
The native recovery audit sealed 30 packets with zero pins. Normal MCP hybrid
search returned dense results with trained reranking and matching cited readback,
without warnings. The Overview refreshed successfully and reported a current,
healthy derived index.

Live removal drained the audit pages and committed one artifact page. Artifact
selection and deletion took 279 ms and 772 ms in the captured successful SQL
statistics. The next unit instead timed out at a repeated publication-metadata
delete. Its plan scanned 562 unrelated publications and reparsed a 34,891-GUID
JSON list through two nested-loop ownership alternatives. The earlier fixture
had no publications, so missed that query family.

Final graph ownership now stays in server-side root/revision/record/branch,
activity, action and job queries until those principals are removed. Every
existing OR arm and deletion order remains. Distinct source identity IDs are
frozen before record deletion and removed in 1,000-ID chunks only when no record
still references them. Artifact page IDs remain frozen and bounded. The expanded
graph fixture includes 600 unrelated publications; it completes within a
4.969-second maximum unit and 1.359-second maximum statement. Both cache orders,
shared identities and force/action/relation ownership alternatives pass.

The final file review also found a document Extract job failed permanently after
a SQL timeout while consuming an already settled OCR result. The outbox's
classified SQL catch now also covers the exact ExtractDocument operation,
preserving delivery leases and durable outcomes for normal recovery. All 23
focused checks pass, including transition rollback/commit ambiguity, actual OCR
request/handoff acknowledgement loss, old-generation refusal and cancellation.
Unclassified SQL, file/native errors and other extraction operations retain their
existing policy. Recovery of the one historical terminal job is a separate,
explicitly reviewed operational action; it must reuse its stored result and
preserve original native execution evidence.

Broader qualification twice exposed a plan-sensitive timeout while capturing
references to older generations. Capture now first selects distinct generations referenced
by owned vectors, unions generations owned through drafts/checkpoints, then
joins Format1 generation rows. Shared generations, overlapping ownership,
empty inputs, retirement timestamps and Format2 handling retain their existing
semantics. The measured capture queries take 25–29 ms in the focused fixtures.
The combined affected run passes all 166 checks without skips, including the
broader reproducer and both final-graph cache orders. Independent implementation
review found no blocking correctness, ownership or concurrency issue. The full
Release solution build has zero warnings and errors. Production deployment and
native removal completion remain separate acceptance gates.

Release `2bd0898c` deployed the combined correction without migrations or helper
changes. All sampled passage bindings and calculation times survived restart.
Its warm held-readiness upper bound is 11.25 seconds from worker birth; this is
not a controlled operating-system cold-start benchmark. Audit31 sealed with no
pins. A fresh native confirmation completed the existing source removal on
18 November at 20:46:35 UTC. The source root, owned database graph and app-owned
cleanup ledger are gone. No source backfill or original-file writes occurred.
The original directory retains its NTFS identity and all 26,626 baseline paths.
Metadata comparison observes eleven changed baseline entries and six additions
over the investigation period; it does not establish who changed those entries.

The historical Extract recovery passed eleven isolated SQL/native scenarios,
including exact-plan receipt conflicts, terminal successors and lost
acknowledgement. One independently reviewed operation requeued only the original
job and delivery, retaining its successful OCR result and native cleanup proof.
Normal processing completed all five stages through publication. No new OCR task
or native execution was created for that recovery.

Final Overview review identified seven older OCR records with no stored
result, settled native cleanup and an uncertain task outcome after a
source-withdrawal release. Their current logical source and retained lineage are
valid. Their independently reviewed recovery is now complete: all seven new
successors have completed every indexing stage and published. The 129
blocked text inputs have explicit binary-control or invalid-UTF8 refusals, while
four failed inputs report source removal or unavailability.

The follow-up reproduces a code defect: process-local admission and unconfirmed
removal were included in the predicate treated as definitive source withdrawal.
An already-owned execution may now persist its bounded result under unchanged
durable source identity, lease, dispatch and runtime checks. Launch and pipeline
continuation still require full admission. Both no-result release paths refuse
temporary admission loss. Pausing during inference preserves and settles the
result, then waits for permission to continue.

The existing explicit document-reprocess contract also accepts a distinct,
proved settled-no-result case. It requires current physical admission and
lineage, owned native cleanup, a released batch, terminal dispatch, no result or
result receipt, no capacity/native owner and no competing work. One transaction
records that the old Extract ended without a usable result, fences its job and
delivery generations and creates one successor. Original task/request/execution
and uncertain outcomes remain unchanged; new work receives new identities.
Old stored-failure proof contracts are retained. Older binaries fail closed on
the new proof, so compatible code must remain installed after creating a
successor; rollback is not transparent beyond that point.

All 341 affected SQL checks pass without skips, including images and PDFs,
normal successor registration, live/expired leases, changed result/binding,
commit rollback and lost-acknowledgement replay. The Release solution build has
zero warnings and errors. Release `ad7c0fde` deployed the correction; its warm
held-readiness upper bound was 9.04 seconds, with unchanged passage calculation
timestamps. This is not an operating-system cold-start benchmark. Each of the
seven exact recovery commands succeeded once; normal downstream processing
published all seven successors with new OCR identities.

The existing enabled companion processed one of five Visio waits. Its current
enabled state is preserved. An earlier disabled receipt does not prove its state
immediately before this deployment; preservation is inferred from the updater's
capture-and-restore path. No independent pre-deployment enablement observation
was retained.

The first command for the four remaining Visio inputs refused before extraction.
The CLI unconditionally invoked previous reprocessing even though ordinary
discovery had already prepared an exact current branch. The correction delegates
that branch to the existing companion runner after checking Visio availability;
previous preparation remains available. All 23 scoped SQL checks pass, including
the reproduced failure, exact-binding refusals, availability before recovery and
no second extraction on replay. The Release build has zero warnings and errors,
and independent implementation review approves the correction. Fresh operator
publication, the four-document operation and final interface acceptance remain
open. The integrated Overview path presentation change is included in the next
application payload.

Release `5c99e918` installed that payload, preserving schema, helper policies,
freshly observed companion enablement and all 32 sampled passage calculation
bindings. Recovery audit33 sealed with zero pins. MCP hybrid retrieval was ready
without warnings and its cited span matched readback. REST and installed-helper
CLI literal search/readback also matched; their bounded literal candidate search
reported the normal candidate-budget warning.

The first corrected Visio command exposed a second exact-claim predicate defect
before extraction. A physical owner with a predecessor revision was refused
because the selector required a null parent. Revision history and nested input
ownership share that field. Physical origin, rather than parent nullness,
distinguishes the owner. The independently reviewed narrow correction keeps the
remaining identity, root, suppression, admission and lease guards unchanged.
Two tests reproduce the edited-physical-owner refusal before correction while
fourteen controls pass. The first item still has zero attempts and no extraction
artifact; neither spent batch is replayed.

After the predicate correction, all 53 affected SQL checks pass without skips.
They include successful CLI extraction with a suppressed physical predecessor,
idempotent completion and exact refusal of archive/derived owners with or without
parents, suppressed current owners and wrong hash/root/branch requests. The
Release build has zero warnings and errors. This correction applies to the exact
current Visio delivery selector. The generic reprocessing contract remains in force.

## Final runtime acceptance, 19 November

Release `5e9cdb55` deployed the final physical-owner correction through the
ordinary incremental updater. Its reviewed Web and Outlook payloads match;
schema 80, the installed helper and policies are unchanged. Companion enablement
matches an independent capture immediately before deployment. All 32 sampled
passage bindings and calculation timestamps survived the restart unchanged.
Held readiness completed within 9.75 seconds of worker birth. This is a warm
restart observation, not an operating-system cold-start disk benchmark.

Recovery audit34 sealed 34 recognised packets with zero pins. Each of the four
normal Visio commands extracted once. All four records subsequently completed
all five indexing stages and have one current publication each. All seven OCR
recovery successors also have one publication each and every stage completed.
The seven original request, task, batch and dispatch rowversions are unchanged;
their uncertain historical outcomes were preserved. Both earlier refused Visio
batches remain spent.

Normal Overview refresh provides a current committed sample and reports a healthy
derived index. The integrated path presentation passes the focused browser
checks at desktop, narrow width and zoom, including copying the full path. MCP
hybrid search is semantically ready without warnings and its cited span matches
readback. REST and installed-helper CLI literal search/readback also match; their
bounded literal search reports `candidate-budget-reached`. Both application
health endpoints and the separately restored preview return HTTP 200. No owned
Visio process remains after the scoped batch.

The attention counts are not cleared to make the dashboard green. Remaining
known inputs include 127 binary-control refusals, two invalid-UTF8 refusals and
four removed/unavailable-source failures. Historical native uncertainty remains
recorded. Three recent foreground GPU uncertainties have no parent indexing job;
they are not stranded file-indexing work. The separately managed paused Outlook
source and journal allowlist/coverage warnings remain visible for their owning
chats. No MoHESR backfill or original-file write was performed.

The runtime recovery and focused acceptance are complete. At the user's explicit
request, full solution tests, the large history fixture, native contracts and
shared promotion through `scripts/dev/complete-feature.ps1` are deferred until
all participating chats have finished and their reviewed changes are integrated.
No full closeout was started in this recovery run. Preserve the tested branch,
worktrees, compatible production payloads and recovery evidence for that gate.

Independent Astra review approves this focused recovery handoff and finds no
remaining blocker within the reviewed recovery scope. That assessment does not
complete or approve the deferred combined qualification and promotion gate.
