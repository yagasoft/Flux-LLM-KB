# Task 7 report: guarded go-live feature closeout

## Outcome

Implemented the sole eventual live route in `scripts/dev/complete-feature.ps1` without invoking it.
The route now requires `-GoLive` plus all four clean-slate, VSS, SQL-destruction and Codex-registration
acknowledgements. It stages an immutable merged-main publish payload and enters the private Task 6
CLR bridge directly in the closeout PowerShell process after squash merge, merged-main
restore/build/test and the main commit, but before the one final `git push origin main`.

No clean-slate execution, merge, push, cleanup, deployment, IIS or VSS mutation, SQL operation,
Outlook activation, Codex registration or Phase 6 activation was performed. Live execution remains
explicitly unauthorised.

## Closeout and recovery boundary

- Incomplete acknowledgement sets fail before closeout work. Confirmation switches without
  `-GoLive` also fail.
- Task 7 constructs the Task 6 authority, capability, exact immutable-payload manifest, production
  ports, guarded host and typed request in-process. The module-private bridge remains the only
  executor entry point.
- The SQL bootstrap value is read only from the closeout process environment by the typed Task 6
  parser. Closeout captures and clears it before any `git`, restore, build, test, publish or other
  child can inherit the environment, and restores it only around the direct Task 6 call. The value
  is not placed in a child-command environment, command line, log or journal, and is cleared again
  after the bridge returns.
- The ignored closeout journal has one exact schema. An exact `Completed` record for the current
  commit and plan retries only `push-main` and worktree cleanup; it never constructs or calls a host.
  Pending or failed execution retains the full acknowledgement and Task 6 durable-recovery gates.
- The normal code-only closeout path ignores stale go-live journal state.
- Obsolete backup/deploy parameters, Outlook and worker deployment validation, legacy runtime
  checks, post-live evidence commits and the second push were removed. Normal CLI dispatch still
  contains no `provision-sql` command.

## RED/GREEN evidence

The first required closeout contract run failed before implementation with:

```text
Closeout must require GoLive.
```

After the initial implementation, the completed-journal test exposed PowerShell's automatic ISO
date conversion. Parsing with `ConvertFrom-Json -DateKind String` preserved the exact round-trip
timestamp contract and made the retry slice green. A later regression first failed with the added
stale-journal assertion, then passed after journal inspection was scoped to `-GoLive`; this proves a
code-only closeout is independent of ignored live state.

Final review found that clearing only explicit step-runner transport was insufficient because child
processes inherit the parent environment. The added bootstrap-lifecycle assertion first failed with
`The SQL bootstrap must be absent for every child and restored only for the direct in-process
bridge.` Closeout now captures and clears the value at entry, strips it defensively from every step
runner process, and restores it only for the in-process bridge; the closeout contract then passed.

## Fresh verification

Run from `E:\LLM KB\.worktrees\native-mcp-live-contract` on 2026-08-27:

```text
pwsh -NoProfile -File tests/native/complete-feature-dryrun.ps1 -SourceRoot .
Native closeout dry-run contract passed.

pwsh -NoProfile -File tests/native/native-deployment-plan.ps1 -SourceRoot .
Native deployment plan contract passed.

pwsh -NoProfile -File tests/native/outlook-scheduled-host-contract.ps1 -SourceRoot .
Outlook scheduled host disabled-only contract passed.

pwsh -NoProfile -File tests/native/native-go-live-contract.ps1 -SourceRoot .
Native go-live contract passed.

dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
Build succeeded. 0 Warning(s), 0 Error(s).
```

The whole-slice Domain, Integration and Web test matrix, EF pending-model check and fresh independent
review remain the parent workflow's pre-live gate. They were not relabelled as Task 7 evidence and no
live authority is requested by this report.

## Documentation state

`docs/architecture.md` records the native-only guarded boundary and zero-host completed retry.
`docs/roadmap.md` records verified implementation with Phase 6 still blocked at 0%, and preserves
fresh whole-slice verification, independent review and separate explicit live authority as remaining
work. Dashboard manuals, screenshots and rendered manual assets were not changed.

## Fix round 1: bind completion to the originating feature

Review found that the completion shortcut originally matched only the current main SHA and plan
hash. A later feature branch created from that same main could therefore skip its own
verification/merge/build/test and select cleanup for an unmerged branch.

The exact closeout journal now also records `feature_branch` and `feature_head_sha`. The head is read
after `feature-commit`, so it is the exact feature commit supplied to the squash merge. At process
start, the shortcut compares the journal against both the current feature branch and current feature
head as well as the existing main SHA and plan hash. A completed record for another feature is
ignored and normal closeout proceeds; a pending or failed mismatched record remains fail-closed. The
exact originating branch/head retry remains limited to `push-main|cleanup-worktree` with no host
composition.

Two behavioural RED/GREEN slices exercise the real closeout script:

1. A second feature worktree was created from the unchanged main. Before branch binding, the test
   failed with `A completed journal from another feature skipped merge/build/test and selected
   destructive cleanup.` After branch binding, the second feature took the normal closeout path.
2. The completed record was then assigned to that branch and the feature head advanced without
   moving main. Before head binding, the test failed with `A completed journal from an older feature
   head selected destructive cleanup.` After binding the post-commit feature SHA, it took the normal
   closeout path.

Fresh fix-round verification on 2026-08-27 passed:

```text
pwsh -NoProfile -File tests/native/complete-feature-dryrun.ps1 -SourceRoot .
Native closeout dry-run contract passed.

pwsh -NoProfile -File tests/native/native-deployment-plan.ps1 -SourceRoot .
Native deployment plan contract passed.

pwsh -NoProfile -File tests/native/outlook-scheduled-host-contract.ps1 -SourceRoot .
Outlook scheduled host disabled-only contract passed.

pwsh -NoProfile -File tests/native/native-go-live-contract.ps1 -SourceRoot .
Native go-live contract passed.

dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
Build succeeded. 0 Warning(s), 0 Error(s).
```

No closeout, merge, push, cleanup, live host call, IIS/VSS/SQL mutation, Codex registration or Phase
6 activation was performed in this fix round.
