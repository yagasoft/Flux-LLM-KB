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
  native build/test/merge/push/deploy/cleanup flow.
- Keep its per-step timeout, logging, JSON summary and safe feature-worktree
  cleanup behaviour.

## 4. Implement the native deployment script

- Preflight only the approved local `FluxKnowledge` IIS site, its dedicated
  app pool, loopback bindings, target path and target-only configuration.
- Publish into same-volume staging, preserve production settings, take and
  verify the database backup, apply explicitly confirmed migrations, swap only
  the target payload, and retain rollback evidence.
- Validate assembly identity, four loopback endpoints and `validate-sql`.

## 5. Verify the tooling before it operates the live target

- Run the native dry-run test red against the old closeout, then green after
  the replacement.
- Run the CI-equivalent native build/test commands locally, including the
  disposable SQL integration suite and browser Web test where available.
- Run deployment-script parameter/preflight tests that do not change IIS or
  SQL.
- Request the required independent migration/deployment safety review.

## 6. Close out and validate live

- Run the mandatory native `complete-feature.ps1` with the two migration
  confirmation switches.
- Inspect its JSON result and per-step logs.
- Confirm the merged main SHA, removed Phase 2 worktree/branch, SQL migration
  history, retained backup/rollback evidence and local endpoint status.
- Update roadmap progress/remnant work with the evidence, then preserve a
  concise redacted Flux handoff.
