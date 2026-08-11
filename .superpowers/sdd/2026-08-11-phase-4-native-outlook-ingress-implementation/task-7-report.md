# Task 7 report — whole-branch verification and operational handoff

## Outcome

Tasks 1–6 and the whole-branch review remediations are implemented. Their Phase
4-specific offline/disposable evidence is green, and final independent review
found no remaining code-level finding. Repository closeout is green. No live or
deployment action was taken.

## Fresh verification

All SQL commands used only the process-scoped disposable server connection
`Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;`.
The fixture generated and removed only `FluxKnowledge_Phase1Tests_<guid>`
catalogues.

- locked restore: passed;
- Release solution build with `-warnaserror`: passed, 0 warnings and 0 errors;
- unfiltered solution test: 862 passed, 0 failed, 6 browser skips;
- the formerly flaky native-worker operator-event test now injects its fixed
  `TimeProvider`, so its lifecycle evidence shares the test's fixed clock;
- Task 1 contracts: 10/10;
- Task 2 SQL store/schema: 15/15;
- Task 3 ready-export/deferred replay: 29/29;
- Task 4 Outlook host: 33/33;
- Task 5 Web: 28 passed, 3 browser skips;
- Task 6 recovery: 3/3;
- EF pending-model check: none;
- native closeout dry-run and deployment-plan contracts: passed;
- focused legacy Gmail regression: 117 passed;
- legacy Gmail preservation diff guard: passed.

An earlier exact-exclusion attempt hit a 10-second SQL Full-Text publication
timeout in the unchanged hybrid-search integration test. That exact test passed
immediately in isolation; the final unfiltered run also passed. No timeout
assertion was changed.

## Task 2 test-fence correction

RED: two existing profile-update concurrency tests failed before their intended
SQL paths because they omitted the Task 5-required current revision and completed
browse correlation. GREEN: the setup now reads the current revision from SQL and
creates a completed browse result bound to it; both tests pass 2/2. Their replay,
row-version and final revision assertions are unchanged. No production code was
modified.

## Whole-branch review remediation

The first independent whole-branch review found two Important defects.

1. Accepted Outlook text was retained in the private profile spool but the
   registered retained-source reader resolved only the shared artifact root.
   RED reproduced the missing read. The reader now resolves the profile through
   the source revision's unique source-root binding, leases that private root and
   applies the existing containment, no-follow, length, SHA-256 and strict UTF-8
   checks. Because relative artifact identity would otherwise be orphaned, an
   existing profile now accepts only same-root configuration edits and rejects a
   spool-root rebind without changing its revision. GREEN: reader E2E and binding
   tests passed; the complete Task 2 and Task 3 filters passed 15/15 and 29/29.
2. Expected private-spool/SQL failures escaped `OutlookHostLoop` without calling
   the durable sanitised failure path. RED fake-host tests reproduced spool and
   database failures. The loop now maps only the expected IO, validation and
   database exception families to `RetryableHostFailure`/`IngestionFailed`, with
   no completion or cursor movement. Re-review found the adjacent normal
   `OutlookReadyExportLeaseException` race; RED reproduced it and the loop now
   maps it to `LeaseLost`/`LeaseStale`. GREEN: the full host project passed 33/33.

Final independent re-review found no remaining Critical, Important or Minor code
finding. Its sole evidence correction was the stale pre-remediation test totals,
which this report and the SDD ledger now replace with the fresh counts above.

## Approval and live-validation gate

The following inputs are still required before operational closeout:

1. written approval for the actual feature worktree, local deployment target and
   complete Phase 4 migration sequence;
2. written approval for one non-production classic Outlook profile and selected
   folder under a named interactive Windows user/session;
3. an approved private local spool root with confirmed ACL, capacity and
   writability;
4. an explicit bounded validation window and permission to enable the otherwise
   default-disabled host/profile for that run; and
5. permission to record only the sanitised post-deploy aggregate evidence after
   the deployment and live probes actually occur.

The plan example names `phase-4-native-outlook-ingress`, while the actual worktree
is `E:\LLM KB\.worktrees\phase-4-native-outlook-design`; future authorised
closeout must use the verified actual path. Until these gates are met, do not run
`complete-feature.ps1`, deploy, migrate, start COM, connect Outlook, access a
mailbox, merge, push or create the Phase 4 validation record.
