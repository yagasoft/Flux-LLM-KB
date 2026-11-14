# Scoped search release, 12 November 2026

The scoped ANN search correction is deployed as `ec928805`. Production is
serving trained hybrid results through REST, MCP and CLI, with current citations
and lexical fallback. Full production performance and previous-answer
non-regression acceptance remain open.

## Deployment and verification

The required feature closeout squash-merged and pushed the qualified candidate.
It passed 5,448 executed .NET tests, with 46 existing opt-in skips, nine native
contract groups and zero build warnings. Independent Astra review covered the
complete code, staging qualification and the exact production deployment packet.

The incremental updater used its reviewed stopped-pool mode and a three-role
payload binding. Installed Web, Outlook and unchanged helper hashes matched
their reviewed values. Readiness returned HTTP 200 and the deployment hold was
released after retained-state validation. No schema migration, configuration
change, helper upgrade, model acquisition or projection rebuild ran. Existing
workspace pauses were preserved.

An outer invocation copied a native command's final exit value after the
PowerShell updater completed normally. The updater emitted `ok:true`; fresh
installed hashes, running pool, absent hold and readiness independently
confirmed application. The deployment was not repeated.

## Measured scope and remaining acceptance

| Check | Isolated staging | Production |
| --- | --- | --- |
| Published vectors / segments | 1,113,836 / 136 | 1,257,687 / 284 |
| Frozen workload | 40 requests, two callers | 40 requests, two callers |
| Trained hybrid responses | 40/40 | 40/40 |
| Exact cited reads | 400/400 | 400/400 |
| Answer presence per round | 19/20 | 18/20 |
| API p95 | 9.335 seconds | 10.804 seconds |
| Dense-selection p95 | 1.434 seconds | Not measured |
| Concurrent native indexing | Qualified | No production fixture injected |

Production passed the unchanged 90% answer-presence threshold. Another valid
identifier-lookup answer replaced an earlier citation. The CRM question lost
its previously returned source-specific prohibition; generic prohibitions from
other systems do not establish applicability. The registration-prerequisite
question remains unanswered. These misses remain in the denominator.

Production did not pass the ten-second API p95 target or previous-answer
non-regression. Its slowest measured request was 11.861 seconds; every request
remained within twenty-five seconds. The changed corpus and graph/segment
population are relevant differences, not a proved cause. The next latency
observation needed is a bounded phase breakdown on the exact serving worker.
Automatic approval review rejected the prepared metadata-only capture before
execution, stating `blocked by policy`; no collector or search from that command
ran. No capture bypass or unchanged benchmark repeat followed.

Initial preparation preserved usable lexical results. A subsequent cold queue
timeout also returned ten lexical hits with explicit semantic status. Durable
records show acknowledgement after the queue deadline, with ownership settled;
they do not measure native model load or scoring. Later standalone CLI and MCP
semantic checks completed in 7.13 and 7.72 seconds. MCP readback matched exactly.

The test covers published retained data. Complete original-file coverage,
whole-host overload and autonomous production indexing throughput are not
established by these results.

## Cleanup and recovery boundary

User-authorised cleanup uses exact resource manifests and fresh ownership,
registration, path and database checks. Obsolete worktrees and isolated staging
catalogues are purged without merging unrelated changes. Unique conflicted and
staged contents are preserved through named Git refs and private archives, with
working-file and index reconstruction verified before removal. The reusable
restricted SQL test configuration remains available.

Current production data, model cache, deployed and predecessor payloads,
configuration and native recovery references remain outside the obsolete set.
The ten existing recovery writers and 105 pins are preserved; an invalid audit
does not authorise deleting their evidence. Private deployment, qualification,
preservation and cleanup receipts are retained together outside purge targets.
These cleanup operations do not imply that open search acceptance has passed.

## Bounded follow-up, 14 November 2026

After renewed user authority, the reviewed phase collector completed normally
on the exact serving worker. Both original queries returned ten trained hybrid
hits in 9.525 and 9.607 seconds. Candidate payloads were ready in 2.589 and
2.527 seconds; ranking waits were 6.655 and 6.717 seconds. Dense selection took
2.113 and 1.925 seconds, while query embedding took 63 and 219 milliseconds.
Lexical retrieval overlapped dense work and completed before its awaited join.
The bounded trace lost no events. Shared GPU native events lacked per-request
attribution, so their durations cannot be assigned to either query. This pair
does not replace the frozen workload or establish p95 below ten seconds.

The missing CRM answer remains current, published and exactly citable. A
read-only call of the actual lexical reader returned 100 candidates in 986
milliseconds, without that passage. A private heap load of its one existing
8.39 MiB segment found the target second among all 2,075 segment vectors, and
in both independent unfiltered ANN-100 and ANN-400 calls. This does not establish
global rank, the serving scope filter or admission to the model shortlist.
The same natural question scoped to its G3 folder returned the answer first;
explicit literal mode also found it and passed exact cited readback. These are
demonstrated temporary retrieval workarounds, not full-workspace acceptance.

The user declined a new snapshot, restore and large copy. No such operation
started. The older saved staging corpus differs from the serving corpus and
cannot stand in for current-corpus acceptance. Its prior qualification remains
historical evidence only. Existing files and scoring packets are preserved for
reuse; no new model or graph trial is justified by the current observations.

The normal expanded adapter discarded internal candidate diagnostics. A small
opt-in correction now emits metadata-only Event 14 at lexical, hydrated dense,
disclosure, shortlist and ranked boundaries, plus existing Event 2 after owned
cleanup. Each stage is bounded to 400 ordered chunk IDs; no query, document,
source path or vector is recorded. Four test-first diagnostic regressions and
all 111 related retrieval tests passed, and independent Astra code review
approved the change. It has not yet been deployed. A complete normal-interface
CRM trace must show both ranked and successful completion events before its
shortlist can be treated as consumed model inputs. Absence from the hydrated
dense pool would still leave raw ANN selection versus hydration unresolved.
