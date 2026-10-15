# Historical source-deletion cleanup

The change replaces a whole-source historical membership delete inside the graph
purge with scalar, bounded, committed units. It preserves source fencing, exact
ownership, survivor publication and native-cleanup requirements. The supplied
production incident is prior evidence of expensive, underestimated work; no
historical blocking chain was captured. The specifically approved 15 October
production outcome is recorded below. Earlier pending statements are a
chronological record, superseded by that outcome.

## Acceptance and current evidence

- Cutover commits exact targets and a survivor generation or safe null pointer
  while the tombstoned canonical graph remains available for resumption.
- Each claim mutates one durable unit. A membership unit deletes at most 1,000
  rows for one owned vector. Count, fully drained cursor and lease receipt commit
  together; an ambiguous acknowledgement cannot drain twice.
- Expired/stale authority cannot record failure or native receipts over a new
  owner. Late membership behind the cursor blocks the graph purge.
- Native cleanup uses the existing query/publication gates, revalidates both
  target kinds under the gate and records explicit outcomes. Two deleting sources
  sharing a blob cannot both omit its cleanup target.
- Original files, unrelated canonical records and memberships, shared retained
  bytes and the current survivor index remain protected.
- Running, waiting and attention are distinct; retry restores the durable phase.

Focused SQL coverage includes 2,501-generation fan-out, claim and membership-unit
commit-acknowledgement loss, transactional cancellation/rollback, count/cursor
replay, stale writes, late membership, unrelated placed/unplaced generations,
public retry/pre-migration inference and native IO already in flight during takeover.
The migration test preserves a pre-migration attention row and its cleanup target.
Domain coordinator/reconciliation checks pass 14/14; rendered deletion status
checks pass 4/4. Related GPU/checkpoint and query-release phase adaptations pass.
Independent Astra final review approved the source change and staging evidence
with no blocking code correction. Canonical closeout passed all 21 steps as
`31c04404ad82ef10006d01588b331e7981ef5f7a`; production-dependent gates remain open.

The separate model-free functional deployment is
`E:\Codex Workspaces\Staging\FluxKnowledge\SourceDeletion-20261014`, catalogue
`FluxKnowledge_Staging_SourceDeletion_20261014`, loopback endpoint
`http://127.0.0.1:5142`. Its restricted login has no production database access.
Configuration, runtime, retained content, SQL files and index storage are separate.
Only the SQL engine/machine resources are shared under the user's staging approval.

The small real REST preview/commit/replay -> background coordinator -> real
Windows cleanup run completed in 2.7 seconds. Originals, control vectors/retained
bytes and survivor index survived; concurrent Overview, Sources and corpus roots
reads returned successfully. An actual stage-process replacement paused after a
committed membership boundary (284 rows, cursor 86, no active lease) and resumed
the same operation to 400 rows and completion. Its 40.9 seconds includes the
deliberate restart barrier; all sampled reads succeeded. These are correctness
results, not representative scale measurements.

Repeatable synthetic fixtures and REST measurements live in
`tools/FluxKnowledge.SourceDeletionAcceptance`. Use only its guarded task binding.
Seed while that staging host is stopped. `seed-small`, `seed-restart` and
`seed-scale` preserve existing data; `exercise` drives the most recent manifest.
`exercise-restart` holds the real query gate, writes `restart-ready.json`, and waits
for an explicitly verified stage-process replacement before its signal is written.
The scale seed contains 3,434 target vectors over 11 records, 1,443 historical
generations, 4,847,721 target historical memberships and 17,952,279 control
historical memberships, plus a real initial USearch generation. All content and
vectors are synthetic. No model provider or acquisition path is loaded.
Historical directories contain synthetic cleanup marker files; the initial active
index and the survivor are real USearch artifacts. Physical absence, retained-byte
hashes and survivor validation provide the native evidence for this staging scope.
Private raw evidence remains under the deployment's `Recovery\acceptance`;
credentials and raw evidence are not committed. A failed fixture setup and an
earlier synthetic attention operation are retained separately, not repaired or
counted as the acceptance target.

The injected retained-byte binding failure entered attention with its exact file resume phase; a fresh REST preview/commit and duplicate retry completed the same operation without resetting its count. Canonical signatures, control historical memberships and the real native survivor validation passed. A deliberately wrong HTTP responder received 64 read requests and zero mutation requests: the harness refused before preview/commit. The optional staging-only SHOWPLAN grant was independently reviewed and applied through guarded staging provisioning; profiling retains the restricted login. Fifty small scalar rollback samples used Index Seek → Top → Clustered Index Delete (p95 0.334 ms, unchanged memberships). This measures the scalar SQL command, not whole-unit or representative-scale latency.

The first full-scale run completed the deletion in 417.33 seconds, committing
4,851,155 removals (historical memberships plus the initial active generation).
Fifty rollback scalar samples had p95 16.24 ms; the actual sample used Index Seek
→ Top → Clustered Index Delete with 12,006 logical reads, 15 ms CPU and 13 ms elapsed.
The profile left the membership count unchanged. These timings measure the scalar
command, not the complete leased unit.

That run **failed acceptance**. Baseline overview reads were usable 60/60; during
cleanup they were usable 274/399 (68.67%), with 125 explicit consistency refusals
and an 8.28-second maximum gap. All 1,197 source-list, source-detail and REST roots
reads were usable, with no HTTP or transport failures. After deletion completed,
the harness's large control-membership count timed out; final canonical control
and survivor verification was incomplete. Independent post-stop file inspection
confirmed all twelve originals intact, owned retained bytes absent, control
retained bytes intact and all 1,443 historical directories absent. This does not
substitute for the remaining canonical and survivor checks. Failed-run evidence
is retained separately; it is not counted as successful acceptance.

The focused correction coordinates overview reads with the existing publication
and deletion-operation locks while retaining the DBTS consistency guard. Its
independently reviewed order is IndexState update fence, then pending/running
deletion-operation update locks, within a short READ COMMITTED transaction.
Unrelated changes must still cause rejection. One two-second budget covers the
precheck, lock acquisition, coordinated reads and SQL retry delays; cancellation
releases the transaction before the refresh error is rendered. The focused SQL
checks initially passed 13/14, with a reader-first claim refusal.
A separate read-only reproduction advanced the database counter during a cold
blocked claim SELECT without changing the stored operation rowversion; its warmed
repeat did not. This establishes a conservative refusal, not an accepted mixed
snapshot or an identified internal allocator. A later group retained 13/14 while
a writer-first read saw a changed counter after the writer committed. Query
preparation cannot establish a universal quiet-counter guarantee. The reviewed
test contract now requires writer exclusion through the closing fence, expected
old/new source data and acceptance exactly when the two actual fences are stable
and equal. Fixed multiple-reader checks require positive accepted reads. All query
warm-up was removed; that complete group passed 14/14 with no skips. The real
scale run also has no discarded warm-up reads.
The harness uses scalar generation ranges and typed control IDs for final
verification and retains partial checks/failure evidence. Its new scale fixture
also requires an untouched survivor-only generation to retain the exact metadata,
rowversion, membership and physical marker. Final branch checks remain pending.

The second full-scale deletion completed in 462.50 seconds with 4,851,155
committed removals. The physical original/control/owned-absence checks, 12,442
control vectors and expanded unrelated canonical signature passed. Acceptance
still failed: one Overview read exhausted its own two-second budget while
acquiring the publication fence, and the historical-count verifier lost its
temporary table across a parameterised RPC scope. This is staging evidence;
it does not establish a historical production blocking chain. Raw failures remain
retained. Final historical/control-generation/survivor checks were initially
incomplete. The subsequent read-only recheck passed all 17,952,279 historical
control memberships with zero foreign rows, unchanged untouched-generation
metadata/rowversion/membership/marker, owned graph absence, initial/historical
native absence and a real USearch survivor containing all 12,442 control vectors.
Its separate report explicitly does not re-evaluate the failed acceptance.

The bounded reassessment keeps the same locks, DBTS guard, two-second read
budget and availability thresholds. A narrowly typed budget expiry now marks
status busy and unavailable, retaining only the labelled last coherent snapshot
and its original timestamp. Caller cancellation and genuine SQL/other timeouts
remain distinct. Initial and retained-snapshot rendering, later recovery and
genuine failures pass 24 focused checks; eight pure classifier cases ensure busy
content cannot count as usable even with an old timestamp. The verifier creates
its typed temporary table in a separate unparameterised batch and shares physical,
canonical, untouched-generation and real USearch checks with a read-only
`verify-preservation` mode. That mode writes a separate report without retrying a
source or re-evaluating failed acceptance. Updated SQL proof passes 14/14 with no
skips.

The final full-scale run passed every fixed acceptance gate. It removed 4,851,155
memberships in 575.11 seconds; final verification took a further 7.21 seconds.
The 1,443-generation fixture preserved earlier controls rather than cleaning them
up. Fifty scalar rollback samples again used Index Seek → Top → Clustered Index
Delete, with p95 19.22 ms and unchanged counts; the actual sample recorded 12,006
logical reads and 24 ms elapsed. This remains command-level measurement.

Baseline Overview was usable 60/60. During cleanup it was usable 533/534 (99.81%),
with one explicit busy/unavailable response, zero failures and a 7.30-second
maximum usable-read gap. Usable Overview p95 was 73.73 ms versus 38.31 ms baseline.
All 1,602 other sampled Sources, control-detail and REST roots reads were usable,
with zero failures. No samples were discarded or reclassified after the run.

All twelve original hashes, control retained bytes, unrelated canonical data,
17,952,279 control historical memberships and all 12,442 control vectors survived.
The untouched generation retained its exact metadata, rowversion, membership and
physical marker. Owned graph rows, retained bytes, initial index and historical
native directories were absent; the real USearch survivor validated and contained
all 12,442 control vectors. The exercise exited successfully and its separate
host/process/listeners were then observed stopped. Raw result and verification
files are under `Recovery\acceptance\7416960401da4365a7199601dc9ac5ad`.
Independent Astra final review approved the complete branch against `ff9f14c4`,
including the concurrency proof and all six runtime/harness hashes bound to this
successful run. At that review gate canonical closeout remained pending; the
review granted no production authority. The later canonical result is below.

The first prescribed closeout passed 3,402 feature tests (26 opt-in skips), all
native contracts and zero-warning builds in both checkouts. Main then passed
3,401 tests but failed one source Overview interleaving test while waiting for
its closing-fence barrier; no final Main commit or push occurred. The test had
not observed the read task's actual outcome. Its corrected lifecycle races the
barrier with the read, preserves early failures and cancels/observes the task
before fixture cleanup. Success additionally requires one committed unrelated
row change, exactly two unequal actual fences and rejection. No deadline,
runtime, assertion, scheduling or warm-up relaxation was introduced. Concurrent
reproduction passed 441 Web tests, 978 domain tests and all 232 relevant SQL
integration tests; the final concurrency group passes 14/14 with no skips.
Astra approved this test-only correction. The original read outcome remains
unclassified; fresh canonical verification is still required. The failed Main
log and exact staged tree are retained in the private recovery packet and a
scoped stash. Main was restored non-destructively to its clean released pin.

The second prescribed closeout stopped before Main integration: 3,399 tests
passed, three failed and 26 were skipped. The improved diagnostics exposed two
real Overview read-budget expirations and an existing corpus retrieval test's
HTTP 503 `resource-pressure` response while solution test projects overlapped.
The smaller concurrent diagnostic set had passed. These failures remain failed
evidence; neither a longer runtime deadline nor a warmed fixture was introduced.
The focused workflow correction adds `-m:1` to both solution-wide test commands
to run project processes sequentially, retaining every test, internal concurrency,
deadline and failure gate. Astra approved the exact change and the native dry-run
contract passed. The bounded solution log proves that each project ended before
the next started: 978 domain, 232 relevant integration and 441 Web tests passed
(21 browser opt-in skips). Its overall outcome failed because one existing
Outlook privacy test captured a concurrent host test's process-wide error output.
The test now uses the repository's non-parallel collection pattern while it
redirects `Console.Error`; its strict empty-output assertion and all runtime code
remain unchanged. The fresh Outlook build has zero warnings/errors and all 85
tests pass. Fresh canonical success remains required before closeout.

The third canonical run proved sequential projects and passed 3,401 tests, with
one failure and 26 skips, before any Main integration. Only the unrelated-write
Overview test still exhausted its two-second budget while paused at the closing
fence. A bounded passing Web trace measured about 235 ms opening the injected
writer connection and 3.9 ms executing its update inside that forced pause; it
does not classify the earlier failure's wait. The fixture now opens its separate
writer connection and constructs a typed parameterised command before the cold
Overview read. It executes the update exactly once, in autocommit mode, between
the actual fences. No preparation query, read warm-up, timeout extension or
timeout-as-pass was added. Affected-row count one, two unequal actual fences and
rejection remain required. The revised cold read completed in 1.47 seconds;
the fresh Web build has zero warnings/errors and all 441 tests pass, with 21
browser opt-in skips. Earlier failed runs remain retained and canonical closeout
is still a gate. Astra approved the exact final fixture correction and its trace.

The fourth prescribed closeout passed all 21 steps and pushed Main
`31c04404ad82ef10006d01588b331e7981ef5f7a`, from feature commit
`bc82fcebb10a1061ba0ccd9a173e34b23cd9bcb2`. Both full solution runs passed 3,402
tests, with zero failures and 26 opt-in skips each (21 browser and five
model/performance checks). Required source SQL checks were not skipped. Both
Release builds had zero warnings/errors; all native contracts passed, including
the 28 one-shot admission checks. The tested feature and committed Main trees
matched exactly, both checkouts were clean, and fresh remote readback matched
Main. `-KeepWorktree` retained the dedicated checkout; `-GoLive` was omitted.
No production action or queued retention cleanup ran. Final receipt updates
change documentation only; earlier failed runs remain failed evidence.

Coordinate SQL/Main windows with the parallel release tasks. Fixed acceptance:
completion under ten minutes, scalar batch p95 under one second; overview usable
at least 90% with no more than a five-percentage-point drop from its measured
baseline and no gap over ten seconds; all other sampled reads usable and zero
HTTP/transport failures. Usable-read p95 must be no worse than max(two seconds,
twice baseline). Both baseline and cleanup use a one-second cadence; explicit
consistency refusals remain unavailable samples. A deliberate restart barrier is
recorded separately and excluded only from the availability-gap clock. One
measured correction/rerun is allowed before reassessing.

## Coordinated rollout on 15 October

The initial source-fix closeout performed no production action. The subsequent
user request authorises coordinated squash integration, incremental deployment,
live validation, completed-task cleanup and retry of the named production source
through its existing public interface. The queued 17-generation retention action
remains excluded. A bounded read-only production preflight found the exact prior
migration, none of the five new columns, the named operation still in attention
without a lease or cleanup targets, and zero queued/leased deletion operations.
Those observations are time-sensitive; they do not establish later eligibility.
Deployment, migration and the named retry have not yet run. The initial combined
proposal included Release 1's full frozen search acceptance. The later scope split
below supersedes that combined proposal and its Release 1 deployment gate.

The incremental updater now provides
`-ApplyResumableSourceDeletionCleanupMigration`; its older `-ApplyMigrations`
switch must not be reused. Both plan and apply reject schema-ineligible app-only
candidates. The opt-in mode requires the exact captured prior history and wholly
absent additive schema. Its baseline is
`20261004175522_AddRepositoryRecoveryAuthority`, and its target is
`20261014134909_AddResumableSourceDeletionCleanup`.

Use EF's idempotent generated script, SHA-256
`644D3847B61C49EA57081E44E3C781DFFB226FA52619EB0CA2A75D7D3B3E273F`.
The earlier non-idempotent preparation artifact remains failed evidence: its
single batch could not compile checks against newly added columns. The corrected
script generates those checks through `EXEC`, retaining one transaction. The
verified schema fingerprint is
`00FA188A700D3F97ABD746CEEF4FB7A9641917608AB4FC1AB8DAFDDFF97C439F`;
it covers types, lengths, precision, nullability, default, binary collation and
enabled/trusted checks. Disposable SQL tests execute the actual updater runner,
prove rollback on an interrupted transaction, preserve existing attention data and
reject schema/operation drift. Script idempotence does not permit automatic retry
of an attempted migration.

1. Finish standalone canonical closeout and independent review. Review
   fresh `-PlanOnly -ApplyResumableSourceDeletionCleanupMigration` output for the
   immutable source-deletion commit and fixed deployment target. Obtain specific
   authority for this revised production action before Apply.
2. Apply that same mode through the incremental updater. Before SQL and startup,
   verify the owned hold, disabled/exited companion task, configuration bindings,
   script hash, captured database/history and zero queued/leased deletions.
   Preserve the task XML, hidden launcher, model store and both prior payloads.
3. The updater flushes a database/release/payload/SQL-bound attempt receipt before
   sending SQL. It never replays SQL from the rollback callback or runs Down. It
   validates unchanged existing operations/targets and initial new progress fields.
   Candidate checks and durable hold-release intent run inside payload recovery's
   scope; ambiguous release enters forward recovery and preserves the matching
   candidate pair. On a pre-intent failure, recovery attempts to restore and verify
   predecessor payloads stopped, with the task disabled and the hold retained.
   Unproved web restoration preserves companion bytes and recovery evidence;
   persistent ownership drift or restoration failure requires separate recovery.
4. After successful deployment and live checks, use public preview/commit to retry
   only the authorised source and operation. Observe durable phase/count progress,
   useful reasons, normal reads and independent physical cleanup. On attention,
   ownership uncertainty, non-progress or read degradation, retain the source
   fence and prepare the specific recovery action for review and approval. Never
   reset a lease or infer native cleanup from SQL alone.

Rollback retains the additive schema, cursor/count, receipts and exact targets.
Keep verified predecessor payload/configuration and recovery evidence. Before an
older binary starts, keep the application stopped, workers/tasks disabled and the
deployment hold retained: the old binary does not understand new intermediate
phases. If that fence cannot be proved, remain held and roll forward. Code rollback
cannot restore deleted memberships or files. Data restoration requires a separate,
consistent database/storage recovery approval; never re-enable the source or
replay the old monolithic delete as rollback.

An uncommitted abandoned claim may wait the existing 15-minute lease duration.
Cancellation is restartable, not an automatic native-cleanup acknowledgement.
Independent review approves the updater code for canonical closeout. Final
production execution review and standalone/live acceptance remain pending.
Local/staging success does not establish production completion or approve a
subsequent repair.

The first coordinated prescribed closeout stopped at `dotnet-test-native`,
before feature commit or Main integration: 3,249 tests passed, 168 failed and 26
were skipped. The Release build had zero warnings/errors. Failures include
`resource-pressure`, `git-inventory-resource-pressure` and an Overview read-budget
expiration. The resource owner and the timeout's cause are not established; no
blocking claim follows from these results. Main remained clean at `62bfd6be`.
The new migration's focused disposable SQL/callback checks and all 32 focused
staging Web checks passed separately. Preserve the failed full-suite log and
unchanged safeguards; do not retry the same full run without addressing its
environment or a demonstrated defect. The subsequently approved one-shot memory
snapshot identified the IIS worker as the dominant consumer. It does not establish
which allocations it retains or prove a model leak. SQL was within its configured
cap; no shared SQL memory change is justified by this evidence. The rollout, named retry and completed-task archival
remain unexecuted. All 11 R1 ignored evidence logs and the 158-file stopped I:
staging predecessor now have verified external/recovery copies.

The updater provides a stop-only maintenance action requiring separate approval,
`-EnterSourceDeletionAcceptanceMaintenance`, to make acceptance possible without
changing model/runtime policies or weakening memory admission. Its Apply path pins
the reviewed updater and all loaded dependencies before importing them. It flushes
the original task XML/policy and payload/configuration/database/IIS bindings before
disabling the companion, then uses the existing GPU admission/drain and exact IIS
worker/descendant-exit proof. A completed receipt requires a stopped exclusive
pool, no workers or companion process, a free CPU owner lock, unchanged existing
deletion state and no deployment hold. It changes no schema, payload or
configuration and never forces process termination or restarts automatically.

Only the source cleanup migration may consume the completed receipt through
`-SourceDeletionAcceptanceMaintenanceReceipt` and
`-ExpectedSourceDeletionAcceptanceMaintenanceReceiptSha256`. Fresh checks reject
an incomplete/changed receipt, restarted worker, ownership or binding drift and
foreign hold. The updater creates its own release hold before SQL and carries the
receipt's original enabled policy through successful activation. It does not
silently preserve the temporary disabled policy. Native tests cover exact
descendant proof, lost drain proof, original policy restoration, interruption,
repeat refusal and drift. All 19 actual updater callback cases and the native
incremental/repository contracts passed. At that point, the full canonical and R1
frozen gates remained open; the later scope split separates those gates.

Maintenance requires fresh explicit approval of the outage before execution.
Successful entry leaves the verified pool stopped and task disabled during the
combined checks; failed entry may leave partial quiescence. Failed acceptance
keeps the verified stopped state with original schema/payloads intact, pending a
specific reviewed recovery action. No persistent maintenance hold or hold-transfer
framework is needed at this unchanged pre-schema boundary. Normal migration and
release rollback retain the stronger held recovery rules above.

The user approved that maintenance window on 15 October. A fresh plan and Astra
execution review passed, and stop-only Apply completed with both the exact IIS
worker and its child proved absent. Native ownership cleared; available physical
memory was 53.79 GiB afterwards. The original enabled task policy, configuration,
payloads and production schema remain preserved. All 143 focused checks covering
the prior resource refusals and Overview concurrency passed without safeguard
changes. This does not prove the earlier Overview timeout's cause.

The isolated CPU stage reverified all nine cached model files, migrated only its
own catalogue and registered the public repository through preview/commit. Its
1,145 tracked files yield 1,142 eligible records; `.gitattributes`, `.gitignore`
and `LICENSE` are outside the unchanged eligible extension set. Canonical passage
preparation completed; embedding/publication and frozen acceptance remain open.
The initial combined proposal allowed canonical checks during that isolated
preparation, with production deployment gated on R1 acceptance. The scope split
below supersedes that coupling. No production payload/schema change or named
deletion retry has run.

The post-maintenance full test run passed 3,417 tests with zero failures and the
26 unchanged opt-in skips; its Release build had zero warnings/errors. Prescribed
closeout then stopped at `native-deployment-contract`, before commit or merge.
The stopped-pool test still used the current candidate against the unmigrated
production schema and matched superseded expression text. Its corrected plan
fixture uses a candidate without the new migration, while disposable SQL retains
the current-candidate eligibility checks. Actual restart-expression cases and the
containing recovery handler replace brittle text matching; existing held-rollback
and six disposable swap outcomes remain required. The focused stopped-pool and
complete native deployment contracts passed. Prescribed closeout must be rerun.

## Production and staging scope split

The user subsequently separated Release 1 staging and performance work from the
production release. Production coordination now covers source deletion, pipeline
visibility and incident follow-up. Release 1's unvalidated additions are excluded
from the production candidate, so its frozen acceptance remains required for its
own delivery without delaying this release. No R1 behaviour is shipped by waiving
that acceptance.

The dedicated source-deletion checkout now uses a standalone branch from Main
`62bfd6be`. The original combined branch, all twelve pending source-release files,
their hash-verified snapshot and a retained stash preserve the earlier work.
Release 1's own branch and checkout remain intact. The in-progress combined
closeout rerun was cancelled before commit or merge; it is not successful closeout
evidence. Run the prescribed closeout for the standalone branch.

Release 1's isolated runtime and observation sessions are stopped, and ownership
of its configuration, data and tuning returned to its original task. Exact
process/child absence, no listener, a free CPU owner lock and recovered memory
prove physical CPU resource release. No graceful application-cleanup
acknowledgement was observed, so interrupted staging work and graceful shutdown
remain unconfirmed. No database acknowledgement or repair was used to manufacture
that proof. Independent Astra review accepted the physical isolation for
production preparation. Keep real-model staging, bulk indexing and search
measurement stopped during production recovery; a separately reviewed resource
plan is required before restarting that workload.

Production remains at the approved stop-only boundary with its original payloads
and schema preserved. The revised standalone release requires its own concrete
target, canonical results, fresh plan and independent execution review before the
specific production action is approved. The named source retry follows successful
deployment and bounded live checks; the 17-generation cleanup remains excluded.

Standalone canonical closeout subsequently completed all 21 prescribed steps and
pushed Main `20dd2fe1`. Both feature and Main suites passed 3,403 tests with zero
failures and the 26 unchanged opt-in skips; both Release builds had zero warnings
and errors. All required native contracts passed. The checked feature and Main
trees matched exactly, with the dedicated worktree retained and GoLive omitted.
An earlier standalone attempt lost its process without a Main test result or
final receipt. Its identical staged squash was preserved before the successful
rerun; that interruption is not successful verification evidence.

The remaining production gate is the revised standalone action: prepare the
immutable web/companion payloads, review a fresh eligible PlanOnly and obtain
independent execution review and specific approval before Apply. Production
payload/schema activation and the named retry have not run. Original source files,
unrelated canonical data, actual native target absence and survivor validity remain
live acceptance checks. Completed task archival follows those checks; unfinished
R1 staging, its branch/data and the preserved combined candidate remain retained.

Fresh production planning subsequently rejected the stopped-receipt path because
the pool was Started. A bounded fixed-target read found an active worker, unchanged
original web/companion payloads, configurations and task/launcher identity, with
the companion still disabled. The normal Started-pool PlanOnly passed against the
exact prior schema and zero runnable deletions. The restart cause is unproved and
was not investigated; the earlier stopped receipt is now ineligible.

Independent Astra review approved the revised approach: use the ordinary
Started-pool migration path, including its existing drain and exact worker-exit
proof, without the old stopped receipt. After confirmed release and a healthy
matching candidate, revalidate task, launcher, companion and configuration bindings,
then explicitly restore the original enabled task policy without starting it.
An enable error may have an uncertain outcome: observe and record the actual task
state, without blind repetition or release rollback. Specific user approval and
final execution review remain required. No migration, payload activation, named
retry or task-policy restoration has run in this preparation.

## Approved production outcome on 15 October

The user approved the revised exact-target rollout after immutable payload
preparation, fresh normal running-pool planning and independent Astra execution
review. The incremental updater deployed Main `1577aca8` to the existing
production installation and completed release `20261015T120851Z-1577aca8ccf8`.
It applied only the additive
`20261014134909_AddResumableSourceDeletionCleanup` migration, retained the
predecessor web/companion pair and recovery receipts, and released its hold after
verification. The old stopped-pool receipt was not used. After a healthy matching
release, the original companion task's enabled policy was restored without
starting it; subsequent ordinary scheduled activation returned result zero.

One public REST preview/commit retried only source Test. The same durable deletion
operation completed without an active lease, with 4,847,721 membership removals,
all 3,434 captured vectors and all 11 pipeline records removed. Its root and 24
source revisions are absent. Membership absence is supported by the captured
vector identities and the enabled, trusted membership foreign key, without a
whole historical-table scan. No direct database repair, lease reset or repeated
retry was used.

Physical checks verified all 1,454 captured generation paths absent; they were
already absent at the baseline, so this is not evidence that this retry removed
1,454 directories. All 11 eligible owned blobs are absent. Four shared blobs
retain their surviving references, byte lengths and hashes. All 11 original
workspace files and the two sampled unrelated canonical/blob controls are
unchanged. A separate read-only verifier checked the active survivor's exact
54,914 SQL vectors and native USearch contents at 1,024 dimensions, with a stable
descriptor and active pointer; it completed in 4.11 seconds. It constructed no
model provider and performed no acquisition or database mutation.

Production cleanup took 39 minutes 25 seconds from public retry intent. Across
three bounded observation windows, Overview was usable for 545 of 607 samples
(89.786%), with 62 explicit busy responses. All 1,665 other sampled source-list,
source-detail and REST roots reads were usable; none of the 2,272 sampled reads
had an HTTP failure. The maximum gaps between usable Overview samples within
those windows were approximately 8, 15 and 23 seconds, below the approved
production 30-second threshold. Gaps between observation windows prevent a claim
of continuous coverage. Production did not reproduce the staged under-ten-minute
and 90%-usable Overview benchmark; those fixed staging thresholds were not
production service-level commitments in the approved packet.

Final probes were healthy, the installed payload hashes matched, the release
hold was absent and the same worker remained running. No matching crash,
host-stop or native-cleanup-deadline event was observed. Five managed SQL
deadlocks and five source-watcher reparse warnings remain recorded unresolved
observations. This is not evidence of error-free operation, historical blocking
or resolution of the earlier native heap incident. Independent Astra final
review accepted this scoped rollout and named deletion with these limitations
explicitly retained and no blocking integrity finding.

The original payload pair, migration/release receipts, staging data and private
evidence remain preserved. An application rollback requires a separately reviewed
and approved guarded incremental action that proves the selected pair compatible
with the additive schema and committed cleanup state. The retained predecessor is
recovery evidence, not permission to restart an old binary. Do not run a down
migration or replay the completed deletion. Restoring purged indexed data would
require a separately approved consistent database and retained-content recovery,
or new ingestion from the preserved originals.

Release 1's frozen acceptance and staging recovery remain separate. Its branch,
runtime, storage, catalogue and evidence are preserved. The queued 17-generation
cleanup, dead letters, model acquisition/cache changes, shared SQL configuration
and clean-slate deployment were not performed. Private approval, execution,
canonical/file/native proof, read metrics and review receipts are retained under
`I:\Codex Workspaces\Release Preparation\FluxKnowledge\source-deletion-20261015`.

## Completed-task cleanup and recovery

The completed source, pipeline and incident checkouts were removed after final
acceptance. The completed source-production and pipeline branches were removed;
the combined source candidate, original incident recovery branch, all 27 stashes,
unfinished Release 1 checkout and external investigation/rollback evidence remain
preserved. Pipeline evidence checks found no missing file or size change, but
lacked a pre-archive hash baseline. Incident cleanup detached its artifacts
junction first and then verified all 2,992 preserved files by hash without a
mismatch.

The completed pipeline and incident chats were archived. Release 1 remains open;
the temporary production-recovery resource hold was released without starting or
authorising a new workload. Its own reviewed resource plan and acceptance gates
still apply.

Source cleanup exposed an archive-tool limitation: native archival emptied its
linked external staging Build directory, and native restore then reported a
missing snapshot. The verified full-history Git bundle and eleven separately
preserved ignored evidence files retain source-task recovery. The generated
solution outputs were rebuilt from the preserved feature commit with zero
warnings/errors, offline package restore and an explicit external output path.
The temporary recovery checkout had no junction and was removed only after
checking that it was clean. These rebuilt files are not byte-identical to the
pre-archive generated output. The separate staging App, Data and Recovery paths
remain outside the affected Build path; the accepted result receipt still matches
its original hash. No host, database or model operation ran during this recovery.
Use the retained bundle for source checkout recovery rather than assuming native
restore is available, and detach external junctions before future worktree cleanup.
