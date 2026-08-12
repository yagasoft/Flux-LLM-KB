# Native closeout and loopback deployment plan

> **For implementation:** execute this plan using the approved native-only
> design in `2026-08-03-native-closeout-and-loopback-deployment.md`.

## 1. Prove the old closeout is unsuitable

- Run the existing closeout script in dry-run mode and capture its Python,
  Docker and dashboard steps as the expected failing contract.
- Keep the legacy test files intact; remove only their active runners.

## 2. Add a native closeout contract test

- Add a self-contained PowerShell test that creates a temporary `main` and
  `codex/*` worktree, runs `complete-feature.ps1 -DryRun`, and asserts the
  generated plan is native-only.
- It must not require a network, Docker, IIS or SQL Server.

## 3. Replace active suite and closeout

- Convert CI to a Windows locked .NET restore, Release build, non-browser
  native test run and the dry-run contract test.
- Replace legacy parameters and steps in `complete-feature.ps1` with the
  native build/test/merge/push/deploy/cleanup flow. Retain exactly one
  preservation-only exception: the focused legacy Gmail pytest regression
  before the feature and squashed-main commits; no other Python or pytest
  command belongs in closeout.
- Keep its per-step timeout, logging, JSON summary and safe feature-worktree
  cleanup behaviour.

## 4. Implement the native deployment script

- Preflight only the approved local `FluxKnowledge` IIS site, its dedicated
  app pool, loopback bindings, target path and target-only configuration.
- Publish into same-volume staging, preserve production settings, take and
  verify the database backup, apply explicitly confirmed migrations, swap only
  the target payload, and retain rollback evidence.
- Validate assembly identity, four loopback endpoints and `validate-sql`.

## 4A. Amend the native deployment for Phase 5

- Keep the eleven Outlook migrations as a distinct family ending at
  `20260812102333_AddOutlookBrowseTargetPath`; enumerate the nine generated
  Phase 5 migrations separately and pin the update target to
  `20260820101021_CloseRetainedCsharpMixedOutcomes`.
- Add `-KeepOutlookHostDisabled`; the closeout uses it by default, drains any
  pre-existing task and never registers, enables or restores that task.
- Stop or drain the Outlook task and IIS application pool before the COPY_ONLY
  backup, verify the backup, and retain the no-automatic-database-restore rule.
- Replace the broad Gmail veto with a default-closed, explicit confirmation
  that accepts only the reviewed thirteen Phase 5 local-surface blob
  identities and continues to reject actual Gmail mutations.
- Keep the focused legacy Gmail pytest regression as the sole documented
  Python exception in closeout: the enumerated `test_mail_*`,
  `test_background_jobs.py`, and `test_worker.py -k imap` preservation checks
  run before each commit boundary; no other Python or pytest command may enter
  active closeout, deployment, CI or live validation.
- Add `phase-5-deployment-plan.ps1` and a read-only live validator for the nine
  migrations, required schema, the four direct-loopback GET surfaces, an empty
  retained C# search, and the full forwarded/proxy 403 matrix. The validator
  may write only its sanitised `docs/operations` record.
- Insert the validator after deployment and before the validation-record
  commit, record push and worktree cleanup. Preserve structured logs, timeouts,
  failure JSON and operator-directed database recovery.

## 5. Verify the tooling before it operates the live target

- Run the native dry-run test red against the old closeout, then green after
  the replacement.
- Run the CI-equivalent native build/test commands locally, including the
  disposable SQL integration suite and browser Web test where available.
- Run deployment-script parameter/preflight tests that do not change IIS or
  SQL.
- Request the required independent migration/deployment safety review.
- Run `tests/native/native-deployment-plan.ps1`,
  `tests/native/phase-5-deployment-safety.ps1`,
  `tests/native/legacy-gmail-approved-local-surfaces.ps1`, and
  `tests/native/complete-feature-dryrun.ps1` with witnessed RED/GREEN evidence,
  then request a fresh independent deployment-safety review.

## 6. Close out and validate live

- Run the mandatory native `complete-feature.ps1` with the two migration
  confirmation switches, `-KeepOutlookHostDisabled`, and the explicitly
  audited `-ConfirmApprovedLegacyLocalSurfaceChanges` switch.
- Inspect its JSON result and per-step logs.
- Confirm the merged main SHA, removed Phase 2 worktree/branch, SQL migration
  history, retained backup/rollback evidence and local endpoint status.
- Update roadmap progress/remnant work with the evidence, then preserve a
  concise redacted Flux handoff.
