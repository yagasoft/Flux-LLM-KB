# Native go-live PowerShell host-binding repair

## Context

The authorised one-shot closeout reached the merged payload publish step but
never entered the native executor. PowerShell treats `$Host` as a read-only
automatic variable, while the closeout hand-off used that spelling for the
constructed native host object. The existing IIS site, `I:\FluxKnowledge`
tree, and target catalogue were therefore not changed.

## Constraints

- Keep the one-shot fail-closed design; add no recovery, resume, adoption or
  persistent deployment state.
- Change no live state while fixing or testing this hand-off.
- Use no Flux plugin or Flux KB tools.
- Preserve the current protection of real secrets and bootstrap authority.
- A later live attempt must still be a fresh `complete-feature.ps1` invocation
  with all four explicit confirmations.

## Task 1: make the hand-off executable and prove it before execution

1. Completed: added a no-execution PowerShell contract test that reaches the
   complete constructed-host hand-off and fails if any reserved `$Host` binding
   remains.
2. Completed: renamed the three local/parameter bindings in
   `complete-feature.ps1` and `native-go-live.psm1` to `NativeGoLiveHost`.
3. Completed: recorded RED then GREEN for the new test and ran the focused
   native closeout contract checks.

## Review and verification

- Independent task review must check that the test reaches all three original
  boundaries and that no executable native go-live method is invoked.
- Independent whole-branch review must check the one-shot model remains
  simple and fail-closed, and no bootstrap data becomes observable.
- Before a new live invocation, run locked restore, zero-warning Release build,
  the required focused tests, full native Release suite, and EF
  no-pending-model verification. Only `scripts/dev/complete-feature.ps1` may
  make the next live attempt.

## Task 2: keep closeout scratch out of the feature commit

The first repaired invocation correctly reached its main-worktree squash step,
but the existing broad feature staging command captured untracked SDD scratch
from this repair workspace. It was stopped before main commit, push or native
execution.

1. Add a narrow contract proving the closeout feature-commit step excludes the
   SDD scratch directory while retaining normal source staging.
2. Ignore SDD scratch at the repository root, narrow the existing staging
   command accordingly, and remove this branch's accidentally committed SDD
   artifacts.
3. Run the contract RED then GREEN and the focused closeout contracts. Keep
   only source, tests and durable plan documentation in the branch.

This is a source-control hygiene repair only: it must not add a recovery
workflow, deployment state, a second closeout entry point or any live action.
