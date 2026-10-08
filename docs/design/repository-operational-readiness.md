# Repository operational readiness

Status: user-approved local implementation prepared and independently reviewed on
8 October 2026. The operational approval packet must record the required feature
closeout evidence and its current outcome. Production release,
task activation, canaries, exact recovery and retention execution require the
concrete A/B/C packet and applicable execution gates.
The [retrieval design](repository-retrieval-reliability.md) remains authoritative
for retrieval, checkpoint and activation invariants; this slice closes the remaining
operational gates without redesigning those mechanisms.

Disposable checks preserve checkpoint integrity and ownership while reducing the
representative healthy probe to 6,087.56 ms and the waiting query lease to
4,004.28 ms without increasing deadlines. The corrected frozen harness passed
synthetic endpoint checks; retention passed focused integrity, replay, interface
and representative-scale checks. Outlook restoration passed fake-port checks in
PowerShell 7 and Windows PowerShell 5.1. These results establish local capability,
not production acceptance.

Operational Plan bindings and Apply must use the same PowerShell 7 runtime as the
canonical updater. Windows PowerShell 5.1 can produce different identity and payload
fingerprints despite unchanged inputs; its observations are diagnostic and must not
authorise Apply. Existing receipt and fingerprint algorithms remain unchanged.

## Outcome and starting evidence

Deliver unattended repository publication, a correctly restored Outlook host,
recovery of only freshly eligible remaining terminal work, the unchanged frozen
semantic acceptance, and explicitly bounded historical retention. Report each
outcome separately. Keep SQL Server canonical and USearch a rebuildable projection.

The previous release completed at receipt revision 11 with candidate `e1d18352`
and operator `dc58eb91`; subsequent Main `24e73b64` changed status documentation
only. Six bounded MCP/REST/operator-CLI cases passed, including full method reads.
The initial index-updating fallback and CLI timeout, single reviewed retry and
different successful generations remain evidence; they do not satisfy the frozen
40-search gate. Reuse unchanged implementation, tests and independent reviews.

Read-only observations on 8 October are a starting inventory, not future authority:

- Configuration revision 11 has 1,084 current repository paths, 1,079 published.
  Five of 39 terminal jobs concern current sources: four Embed source-unavailable
  outcomes and one Publish placement collision. An earlier sixth source is now
  superseded. None is declared eligible until fresh native preview succeeds.
- IIS is Started but still OnDemand, with a terminating 20-minute idle timeout
  and preload disabled. The existing `-EnableUnattendedDiscovery` path already
  implements AlwaysRunning, zero idle timeout and preload, including the corrected
  Windows PowerShell startup-verifier compatibility.
- `FluxKnowledge.OutlookHost` is Disabled. Its existing interactive, limited-token
  task uses a hidden launcher, IgnoreNew, logon and 15-minute repeat triggers.
  Preserve those identities and settings; activation is a separately named action.
  All four Outlook profile/folder/catch-up/export tables are empty. The executable
  is a desktop companion that can process Visio before Outlook; successful Visio
  work is not proof of Outlook mail capture.
- Retained inventory is 51,096 vectors, 2,634 generations and 16,745,071 membership
  links: one active generation, 1,297 inactive placed candidates and 1,336 unplaced
  checkpoint-bound drafts. No observed query lease and no examined SQL reference
  on a placed candidate establishes deletion eligibility. All observed retirement
  timestamps are null. The earlier 5 October counts are historical, not a baseline.

The deployed candidate already contains Shared healthy projection probes and fresh
Exclusive reread before recovery mutation. Verify installed/source provenance;
do not schedule a replacement fix without a new demonstrated defect. Existing
coordinator cleanup covers staging/quarantine, not a proven historical-generation
retention policy. Native `embedding_retry` and `publication_retry` already exist.

## Approach and boundaries

Use one implementation owner and the existing incremental updater, hold, receipt,
GPU drain, payload swap and rollback. Prefer evidence reuse and narrowly scoped
corrections over another scheduler, new persistence, blanket reindexing or a new
retention framework. A read-only readiness report alone would leave runtime gates
open; a combined release/recovery/delete action would obscure distinct authority
and recovery risks. Use one packet with three separately authorised sections.

Preserve original jobs, deliveries, checkpoints, vector bytes, source/configuration
bindings, GPU ownership, known outcomes and cleanup, native confirmation and
idempotency fences. The previously recovered 32-job cohort and its 329 saved
vectors must not be replayed. Do not weaken timeouts, acceptance checks or lease
rules. Models remain verified local artifacts under `J:\Models`; this slice grants
no acquisition, cache repair or embedding regeneration to pass integrity checks.

## Runtime, unattended discovery and Outlook

Before proposing a runtime fix or another frozen run, analyse retained timeout,
index-updating and cancellation evidence against queue/admission, Shared/Exclusive
lease wait, scoped SQL/dense work, projection/publication and native callback/cleanup
timings. Add only missing phase measurements, reproduce a causal defect with
representative disposable data and synthetic workers, and test the smallest fix.
Do not assume the already-deployed maintenance correction is missing. If no cause
can yet be established, state the unresolved question and bounded diagnostic that
will resolve it; do not substitute an unchanged workload rerun.

The frozen manifest already specifies a 25-second outer deadline, and the Web
composition passes 25 seconds to the engine. The existing private workload runner
instead uses a 40-second client timeout; its verifier lacks an explicit per-case
25-second elapsed bound. Correct this instrumentation before acceptance: read the
existing manifest deadline, preserve all query/caller/round definitions, enforce
the elapsed bound, and record cancellation plus known cleanup before further work.
Do not raise the engine deadline or count a late response as success.

First reconcile installed payload/configuration with the intended ordinary
incremental action. Use `scripts/deploy/update-native-iis-incremental.ps1`
PlanOnly and the existing `-EnableUnattendedDiscovery` path; do not combine its
hosting change with migration/rebuild/recovery modes. Capture the original hosting
tuple and retain its verified rollback. No clean-slate deployment is permitted.

The first safe observable result is a disposable supported input through durable
discovery/revision → dispatch → synthetic worker → publication → real-transport
search and exact readback, including the reproduced failure boundary and its
correction. No production access or real-model acquisition is needed for this gate.
The first production result after authorised packet A is native managed-host
startup before any HTTP request, followed by a specifically approved public canary
change after an idle interval: filesystem input → durable discovery/revision →
dispatch and worker result → publication → exact current citation. Exercise
create, modify, rename and withdrawal as distinct fixtures within one continuous
traffic-free window. Establish fixture baselines/citations before the window; allow
30 minutes without HTTP, including health probes, MCP, CLI or browser traffic,
then perform all four separately identified transitions. Observe their individual
durable publication/suppression through SQL/process evidence, allowing the existing
15-minute reconciliation bound and recorded normal processing budget without HTTP
until all outcomes settle. Use IIS access logs and events to prove absence of
traffic, not merely absence of this operator's requests; unrelated hosts remain
untouched. Unexpected traffic invalidates the idle proof and must be reported.
This tests every change after the old idle threshold without four redundant waits.
Record native startup, worker identities and transition timestamps; afterwards
read the surviving add/edit/rename citations and refuse old rename/deletion references.
Canary paths/content and their final disposition belong in A; do not mutate ordinary
repository files or invoke hidden sync/test endpoints as an acceptance shortcut.

Restore Outlook only through an explicitly reviewed operation on the existing task.
An ordinary update preserves its currently Disabled policy; it does not authorise
enabling or launching it. Packet A must name both the desired enabled policy and
any bounded initial run. Preserve task XML except the authorised enabled-state
change, principal, triggers, IgnoreNew, action and hidden VBS/PowerShell launcher
hashes. Verify the real interactive user/session, dependency availability and spool
safety. Inventory existing eligible companion work; otherwise name a harmless
local supported-format fixture through the existing control plane in A. No fixture
or task launch is authorised by this design. Actual Outlook mail capture additionally
requires the user to identify/approve a profile, folder and bounded item scope in A;
the empty configuration cannot be assumed away or filled with invented selections.

Host-processing success requires its named work to traverse durable request/claim →
worker/export → ingestion/publication → visible status/readback, with duplicate-
trigger and stale-lease safety. Witness a normal configured schedule/logon trigger
as well as any explicitly approved initial run; a manual launch alone does not
prove scheduled operation. Report task activation, companion processing and
Outlook mail capture separately. A Visio completion passes only companion processing;
mail capture remains unconfigured unless explicitly selected. Task exit zero is
insufficient: the host also returns zero for Disabled and NoDurableWork. Retain
private mailbox identifiers and message evidence outside Git. A missing supported task-restoration
operation requires a small reviewed addition to the existing operator flow before
A can be issued; do not resurrect the old full deployment/Outlook validator.

## Exact recovery, settlement and semantic acceptance

Before the single approval packet, account for every one of the 39 observed terminal
jobs with current eligible/refused/excluded disposition, including the 34 historical
or suppressed entries. Obtain fresh current-source membership and native eligibility
evidence for each, and a fresh preview for every proposed candidate; do not infer
recovery from inclusion in this inventory. Packet B binds exact
job/action, source bytes/revision, checkpoint/draft/profile/epoch, vector positions
and bytes, maximum permitted missing positions, delivery/lease versions, GPU input/result/dispatch/
cleanup/slot evidence, confirmation and idempotency identity. Use `embedding_retry`
or `publication_retry`, never blanket `job_retry` to bypass their invariants.
Refuse superseded, deleted, changed, already completed or otherwise ineligible
work. After A's live validation and fresh independent review, refresh native
confirmations/ownership inside that approved exact scope; remove no-longer-eligible
entries, but never substitute or add IDs or missing positions. Routine refresh does
not require repeated permission when the same action/target/scope remains approved.
No inferred recovery of the historical 32 jobs or historical uncertain GPU
outcomes. Missing positions are measured, not estimated from a job count.

Recover only B's approved exact cohort, preserving completed vectors and performing
only proven missing embedding work. Require original identity/checkpoint continuity,
normal publication, current exact citations and known GPU outcome/cleanup. Refresh
the denominator from current Git membership, supported policy and source bytes;
settle automatic publication before measurement. New failures are not silently
added to B. A blocked current source remains visible and prevents a false coverage
completion claim.

Reuse the frozen 20-query manifest, two rounds and two concurrent callers: 40/40
hybrid/semantic-ready results on the expected current, settled generation, exact
root/nested scope and current passage/readback offsets, stable source hashes and
p95 strictly below 20 seconds with the existing 25-second outer search deadline.
Include current code method and document reads through MCP, REST and operator CLI.
Keep first-pass failures and phase/lease/publication evidence. No unchanged rerun,
timeout increase, omitted query or lexical-fallback pass; a failed gate requires a
specific evidenced correction and review of its affected checks before retry.

## Historical retention

Audit the complete reference closure before selecting anything: SQL foreign keys
and logical/JSON references, active/publication/rebuild state, vectors and membership,
jobs/deliveries/checkpoints/artifacts, current query leases and process ownership,
deletion work, native/SQL recovery receipts, release rollback requirements and actual
filesystem contents/shared paths. Classify checkpoint-bound drafts separately from
placed historical generations. Preserve source-root retention semantics; do not
reinterpret root deletion or staging/quarantine expiry as history deletion policy.

The current evidence supplies neither an approved historical-retention rule nor
a historical-generation deletion path. `SqlDerivedIndexRecoveryStore` treats every
nonempty generation path as referenced; calling staging/quarantine cleanup, expanding
its allowed areas or forcing recovery cannot safely reclaim these records. This
specific gap justifies one bounded manifest-driven operator action, reusing existing
native confirmation/operation receipts, Exclusive ownership and path guards; it
does not justify an automatic pruning service or general policy framework.

Before the packet, propose the conservative protected rollback/recovery set and
exact candidate IDs with exclusion reasons. C states its one-off rule, protected
set and deletion together for user approval; no invented age/count threshold or
separate general planning loop is needed. If the protected set is ambiguous or
no eligible IDs can be proven, C remains blocked and that material limitation is
reported. Do not force an early partial packet approval; separately authorised
A/B work may proceed only within the authority actually given. Inactive state, null
retirement or a zero-lease snapshot is never a concurrency or eligibility guarantee.

Prepare and locally verify C's operator before the single packet, even though its
production execution is last. C must be a bounded exact generation/path/membership set with a full
exclusion rationale. Reuse existing Exclusive cleanup ownership, publication fences
and owner-proven query drainage; revalidate closure inside the mutation boundary.
Never drain a running/unknown query owner. Preserve all canonical vectors and all
checkpoint/rollback/recovery data; this action removes only specifically authorised
historical generation records, their exact membership links and derived files.
Bind member identities through ordered streamed child artifacts with per-generation
digest/count and file hashes/lengths, not a flat millions-entry JSON list or directory
globbing. Reuse the existing sequential retained-row fingerprint reader and snapshot
mechanisms; no full membership materialisation, OFFSET paging or repeated rescans.
Before destructive work, retain a verified, exact SQL-row/link and file recovery
snapshot under the existing recovery root, protected from this cleanup. Under the
existing ownership/fence order, revalidate closure and process one exact generation
per bounded transaction/child receipt under the approved parent manifest. Bind and
test its maximum row/link count and cancellation bound before approval; refuse
larger units rather than loading an unbounded set. Commit that unit's exact SQL
removals with its existing durable operation receipt in one transaction, then remove only the
receipt-bound files while maintaining the required ownership and reference checks.
An interruption before commit leaves SQL unchanged; after commit, the receipt and
verified snapshot support only the remaining exact file step or reviewed selective
restoration. Duplicate operation/manifest identity must converge; uncertain outcome
or changed references stops instead of widening or repeating the mutation.

Prove this SQL/filesystem ordering with disposable interruption tests before the
packet. Restoration must compare current references/rows and restore only this
operation's missing, non-conflicting records/files; never overwrite later writes
or restore the full database over them. Deleting the recovery snapshot would make
loss irreversible and is outside C. If selective restore is unsafe because state
has advanced, retain evidence and report the limit for reviewed forward recovery;
do not promise unconditional rollback.

Run C only after frozen acceptance and approval of its section. Verify exact
authorised effects, unchanged active generation/protected vectors, current citations,
health and targeted search parity. Reuse the frozen result when active code,
configuration, corpus and query behaviour remain verified unchanged; repeat the
full workload only when a relevant change/failure invalidates that evidence.
Do not delete history to improve latency or make acceptance pass.

## Approval packet and stop conditions

Keep one private `operational-readiness-packet.json` referencing hash-bound A/B/C
manifests and evidence; filenames describe proposed artifacts, not files already
created. Common fields are schema version, UTC observation, repository/operator and
candidate hashes, exact target/configuration identity, prior release receipt hash,
section dependencies, allowed actions, exclusions, evidence paths/SHA-256, expected
effects, failure/rollback procedure, independent review and explicit human authority.

| Section | Required concrete manifest |
| --- | --- |
| A — release/IIS/task/canary | Exact updater PlanOnly/Apply arguments and payload/configuration hashes; current/desired hosting tuple; task XML/principal/action/launcher identity, enabled/launch operations and normal-trigger proof; native-start and one continuous no-HTTP window with four post-idle fixture transitions, logs/content/paths; approved companion/mail scope; probes, bounds, rollback and stop criteria. |
| B — terminal recovery | Preview references, exact job IDs/actions and maximum missing-position sets; saved-vector/checkpoint/source/GPU bindings; original job/delivery identities; execution-time confirmation refresh and idempotency keys; expected per-job publication/readback and removal of no-longer-eligible entries. |
| C — retention | Proposed one-off rule and protected rollback/recovery set; exact generation IDs and hash-bound ordered member child artifacts/digests/counts, paths and hashes/bytes; bounded per-generation transaction/cancellation limits; complete reference exclusions; execution-time ownership/closure revalidation; exact recovery snapshot and selective-restore limits; bounded deletion commands and final regression. |

Prepare concrete A, B and C after all necessary local implementation/verification,
before the first production action. The user may approve the named sections together;
approval of an explicitly partial A alone does not imply B/C. If B/C cannot yet be
concrete, report the missing material part rather than forcing an early A request.
Refresh confirmations, dependencies and ownership at execution without reasking
for unchanged approved scope; drop ineligible entries, never substitute or add IDs,
positions or effects. Material scope changes require new authority.
Fresh independent Astra/equivalent review is required
immediately before production actions, and after live validation before terminal
processing. Reviews assess technical safety and do not confer human authority.
Reuse valid unaffected tests/reviews, but refresh time-sensitive ownership and drift
evidence. Stop and report any new integrity, unknown ownership/GPU cleanup, crash,
binding drift, migration anomaly or failed rollback; retain evidence and the
appropriate hold. An incompatible predecessor stays stopped/held. No automatic
replay, rebaseline or widening of an approved cohort/deletion set.

Reassess after two coherent implementation batches, or earlier if progress stalls:
show an executable safe result, identify the remaining blocker and narrow the path.
Plans, inventories and reviewers alone do not complete readiness. Acceptance remains
partial until the respective runtime, Outlook, recovery, frozen and retention gates
are met; a safely blocked retention decision is reported as blocked, not completed.
