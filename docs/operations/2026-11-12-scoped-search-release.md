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
