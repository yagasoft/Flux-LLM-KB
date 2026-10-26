# Overview refresh and verification runtime follow-up

Status on 26 October 2026: independent GPT-6 Astra review supports retaining three
correctness fixes. The proposed job-facts specialisation and forced serial plan
have been withdrawn after qualification. This record supports source integration,
not production readiness. The user has prohibited running the feature closeout script.

## Verified behaviour

The shared overview retains its last successful committed sample. Its two-second
deadline covers combined SQL, materialisation and cleanup work. SqlClient can
report cancellation as error zero, sometimes followed by termination notice 3621.
The coordinator owns the outer deadline; the reader preserves caller cancellation.
That deadline could therefore produce either the busy notice or the failed notice.

Production's apparent recovery did not follow a deployment, configuration change
or worker restart. Historical measurements show a modest reduction in queue size
and query durations. Available history does not establish a single causal event.
Unchanged isolated queries also alternate between successful reads and exhausted
budgets. This follow-up does not claim to resolve all refresh latency.

The weekly verifier has a separate runtime permission defect. Its stable physical
reader file was opened with creation/write rights, although the IIS identity has
read/execute rights on Runtime. Provisioning a missing file belongs to stopped,
held maintenance, rather than normal worker execution.

## Selected corrections

- Translate recognised SqlClient cancellation from the coordinator's expired
  deadline to busy. Require at least one zero error and permit only zero and 3621.
  Preserve genuine SQL failures, caller cancellation and shutdown cleanup.
- Replace the correlated Publish-artefact maximum with a nullable exact-key join.
  The existing unique record/revision/stage index prevents row multiplication.
  Preserve document-publication precedence and completion/deletion guards.
- Pre-provision the stable reader file during guarded maintenance and open the
  existing file with read access and exclusive sharing. Preserve physical identity
  and ACLs; reject missing, unsafe or unexpectedly writable files. Keep ownership
  through worker cleanup and retirement.

These fixes have independent correctness reasons to retain them. They do not
change the page location, refresh interval, deadline, schema or GPU lifecycle.

## Job-facts proposal: not selected

The proposed Snapshot-only ordinary branch omitted unreachable rebuild checks and
forced `MAXDOP 1`; the active-rebuild branch retained the canonical query. Full-row
comparisons supported semantic equivalence. Saved executed plans confirmed exact
1,000/20,000-row inserts, `MaxDOPSetToOne`, thread zero throughout and no parallel
operators. SQL reported degree zero for these serial executions, so requiring a
reported degree of one was an incorrect evidence check.

End-to-end measurements did not establish a reliable benefit. Each comparison used
30,000 files, the application's EF retry/buffering policy, Server GC, concurrent
writes, one warm-up pair and 24 fixed measured reads per implementation. Every
failure was retained. Query order was reversed at the larger scale.

| Current jobs | Implementation | Usable reads | p95 elapsed, including failed attempts | Maximum elapsed |
| --- | --- | --- | --- | --- |
| 1,000 | Canonical, first | 22/24 | 2,014 ms | 2,790 ms |
| 1,000 | Proposed, second | 23/24 | 1,983 ms | 2,006 ms |
| 20,000 | Proposed, first | 22/24 | 2,022 ms | 2,079 ms |
| 20,000 | Canonical, second | 24/24 | 1,346 ms | 1,526 ms |

Order and process state confound causal comparisons. Large managed pauses occurred
in the smaller run, but a larger-run proposed-query failure had only about 43 ms
of GC pause. Neither GC nor cold start explains every timeout. Serial execution
proof establishes that the hint worked, not that it improved user-visible reads.
Both runtime SQL files have therefore been restored to the canonical main version.
No further hint or timeout increase is justified by this evidence.

Restoring the canonical query then passed 61 of 62 SQL checks; the heavy workload
exhausted its budget on the first measured overview. Its setup job-facts insertion
took about 679 ms. One final predeclared trial isolated the specialisation without
any hint. Both fixed workloads and both full-row transition cases passed (4/4).
Each fixed workload retained all 24 reads, with p95/max of 1,029/1,167 ms at 1,000
jobs and 1,326/1,588 ms at 20,000 jobs. These test hosts reported workstation GC.

Fresh application-policy Server GC hosts were the final acceptance checkpoint:

| Current jobs | Usable measured reads | p95 elapsed | Maximum elapsed | Cold warm-up failures |
| --- | --- | --- | --- | --- |
| 1,000 | 24/24 | 1,327 ms | 1,450 ms | Overview, 2,027 ms |
| 20,000 | 23/24 | 1,302 ms | 2,072 ms | Overview/page, 2,043/2,106 ms |

The larger case missed the predeclared gate. The specialisation was therefore
withdrawn as well, with no further tuning round. The canonical job-facts and
rebuild-eligibility files again match main exactly. This completes assessment of
the proposed tuning; it does not complete the remaining latency investigation.

## Qualification corrections and limits

The original performance reader omitted `EnableRetryOnFailure`, which the
application enables. EF buffers results when retries are enabled. Measured reads
also included statistics messages and explicit result draining. Those diagnostics
have been removed from the measured qualification path; setup-only diagnostics
remain cancellation-bound and are labelled accordingly. The measured reader now
uses the application retry policy and records the actual test-host GC mode.

An isolated four-mode comparison retained all results for retry on/off and
statistics/draining on/off. Its uninstrumented production-policy runs passed 24/24
at both job counts, but later matched runs above still failed. Correcting the
harness does not prove that instrumentation caused every historical timeout.

A precise allocation-counter experiment is retained separately and excluded from
the final comparison: `GC.GetTotalAllocatedBytes(true)` suspends runtime threads.
Final measurements use approximate process-wide counters, including writer work,
with timestamps and pause counters captured before formatting diagnostics.
A bounded GC trace lost no events and passed all 24 measured proposed-query reads
at 1,000 jobs (683 ms p95, 1,354 ms maximum), while its cold overview warm-up failed
at about 2,010 ms. The trace did not reproduce the long pauses. Intermittent deadline
exhaustion and cold-start capacity remain unresolved.

Canonical materialised-versus-direct full-row admission checks retain independent
owned databases for initial rebuild states, holds and concurrent transitions.
They no longer claim qualification of a specialisation or a forced plan.

## Source checks and remaining release gates

The final selected source build passed with zero warnings or errors. Fresh focused
checks passed all 60 web cases: 39 cancellation/coordinator cases, 19 publication
projection regressions and two SQL read-budget integration cases. All 45 native
ownership/permission cases passed. Provisioning and incremental-updater contracts
also passed. These results qualify the retained correctness fixes;
the canonical 61-pass/1-performance-failure result above remains a failed capacity
qualification. No deadline or workload assertion has been relaxed.

Independent Astra review found no blocking defect in the retained corrections,
corrected harness or canonical full-row tests. It supports source integration
subject to the focused results and accurate documentation. It does not certify
that intermittent latency or cold-start capacity has been fixed.

An actual IIS verification cycle with the restricted runtime identity remains
required before deployment. Automatic approval review rejected its earlier staging
resumption with only "blocked by policy". A new explicit request names the isolated staging pool,
port 5156 and dedicated helper; dependent actions remain pending that answer.
The temporary pool is stopped and its dedicated helper is disabled. Production
payloads, configuration and recovery evidence remain preserved.

Source integration and worktree cleanup must preserve main's unrelated README
update and the rejected tuning's recovery evidence. Production activation and live
acceptance remain outside this source-integration decision. Do not run the closeout
script or treat integration as runtime qualification.
