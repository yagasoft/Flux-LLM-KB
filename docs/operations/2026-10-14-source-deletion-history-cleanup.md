# Historical source-deletion cleanup

The change replaces a whole-source historical membership delete inside the graph
purge with scalar, bounded, committed units. It preserves source fencing, exact
ownership, survivor publication and native-cleanup requirements. The supplied
production incident is prior evidence of expensive, underestimated work; no
historical blocking chain was captured and no fresh production status was read.

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

## Production proposal awaiting specific approval

No production connection, retry, repair, deployment, restart, migration, retention
or cleanup was performed. Do not execute the queued historical retention action.
After integration, Main contains the additive migration and code that reads its
new columns. A later Main payload is schema-dependent and must not be treated as
an app-only release. The ordinary updater plan does not establish eligibility for
these new columns. Preserve the prior production/candidate pins across parallel
tasks until the specific migration and activation gates below pass.

1. Approve read-only preflight for the named production deployment and database,
   including incremental updater `-PlanOnly`. Verify current schema history,
   source/operation ownership, leases, native ownership, target storage and the
   predecessor payload/recovery packet. Identify every runnable deletion before
   activation: startup can resume pending/running operations without a new API
   request. Keep activation held if that would exceed the approved scope. Prior
   incident findings are not current preflight evidence.
2. Prepare and independently review a new opt-in migration contract in
   `update-native-iis-incremental.ps1`. Its existing `-ApplyMigrations` flag is
   pinned to an older source-deletion migration and must not be reused. The code
   baseline is `20261004175522_AddRepositoryRecoveryAuthority`; the additive target
   is `20261014134909_AddResumableSourceDeletionCleanup`. Generated up-SQL SHA-256:
   `3693A03A9EBD2A195E88B5ECB36CC175B029C673F140C504B54B0C535EF70861`.
   Current production eligibility is unknown until the approved preflight. No
   alternative production deployment path is authorised.
3. With the exact payload, migration contract, hold/stop sequence, probes and
   rollback packet reviewed, obtain approval for that incremental release and
   additive migration. Then seek separate approval to resume only the source and
   deletion operation identified by the user through existing preview/commit.
4. Observe durable phase/count progression, useful reason codes, bounded batches,
   normal reads and independent physical cleanup. Stop admission and retain the
   fence on attention, ownership uncertainty, non-progress or read degradation.
   Never reset a lease or infer native cleanup from SQL alone.

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
Production-dependent migration/updater and old-binary operational gates remain
pending approval; local/staging verification does not waive them.
