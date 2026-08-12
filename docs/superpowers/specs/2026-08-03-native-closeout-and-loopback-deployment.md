# Native closeout and loopback deployment design

Status: approved for implementation and amended on 2026-08-21 for the
Phase 5 retained-processors deployment.

## Goal

Make the active validation and feature-closeout path match the approved native
Windows architecture. The active CI, deployment and live-validation suites
exercise only the .NET solution, native SQL Server integration contracts and
the IIS-hosted local application. Feature closeout retains one narrowly scoped
preservation exception: its focused legacy Gmail pytest regression. It does
not start, deploy or activate legacy Python, Docker, RabbitMQ, Vespa,
dashboard, model or GPU components.

## Scope

- Replace the active GitHub Actions test entry point with a Windows .NET build
  and native closeout dry-run contract check.
- Replace `scripts/dev/complete-feature.ps1` with a native-only closeout
  sequence.
- Add a native IIS deployment script for the existing `FluxKnowledge` site.
- Preserve the target-only production settings file while publishing a staged
  release, and retain a rollback payload and a verified SQL backup.
- Apply only the explicitly enumerated reviewed migration families, now ending
  with the nine generated Phase 5 migrations and
  `20260820101021_CloseRetainedCsharpMixedOutcomes`, and only when both
  migration switches are supplied.
- Validate live loopback endpoints and the SQL contract after deployment.

## Non-goals

- No legacy branch/source/test deletion, merge or deployment.
- No Docker, RabbitMQ, Vespa, Python dashboard, model, GPU or external-access
  action.
- No IIS server restart, public binding, database restore, or automatic model
  work.
- No automatic clean-up of retained deployment rollback payloads or SQL
  backups.

## Native closeout contract

`complete-feature.ps1` remains the mandatory closeout entry point for a
`codex/*` feature branch.  It records structured per-step logs and performs:

1. checks that `main` is clean;
2. restores locked .NET tools and packages, builds the solution in Release,
    and runs the native .NET tests against an explicitly supplied disposable
    SQL Server connection; it refuses a non-dry-run closeout without that
    connection;
3. runs the native closeout dry-run contract test and exactly the focused
   legacy Gmail pytest regression before each feature and squashed-main commit.
   This preservation-only exception covers the enumerated `test_mail_*` files,
   `test_background_jobs.py`, and `test_worker.py -k imap`; no other Python or
   pytest command is permitted in closeout;
4. commits the feature, fast-forwards `main`, squash-merges, commits and
   pushes `main`;
5. when deployment is not explicitly skipped, invokes only
   `update-native-windows.ps1` with `-KeepOutlookHostDisabled`, runs the
   read-only Phase 5 deployment validator, then records its sanitised result;
   and
6. purges only the merged feature worktree and its `codex/*` branch.

The command fails at the first failed step and emits JSON containing the failed
step and its log path. Its dry-run output is an executable contract: it must
contain the native build/test/deploy steps and the one explicitly enumerated
focused legacy Gmail pytest regression, with no other Python or pytest command
or legacy command family.

The active CI workflow runs on Windows.  It uses locked .NET restore, Release
build, the non-browser native test suite, and the same closeout dry-run
contract test.  Native SQL integration tests are locally validated with the
disposable SQL Server fixture; hosted CI does not manufacture a Docker or
remote database dependency.

## Deployment safety contract

`update-native-windows.ps1` has a deliberately narrow target:

- The site must be named `FluxKnowledge`, use its dedicated `FluxKnowledge`
  app pool, resolve to the requested deployment directory, and have only
  loopback bindings.  The current approved target is
  `http://127.0.0.1:5137`. The executable rejects any other IIS site name
  before it reads target configuration or SQL.
- The script refuses wildcard, external, missing, mismatched or shared-site
  targets.  It stops and starts only the named app pool; it never restarts IIS.
- It publishes the already-built native web project to a same-volume staging
  directory, copies target-only `appsettings*.json` files without logging
  their contents, and verifies the staged application assembly.
- Migration is opt-in through both `-ApplyMigrations` and
  `-ConfirmApplyMigrations`.  Before an update it creates a COPY_ONLY SQL
  backup with `CHECKSUM`, verifies it with `RESTORE VERIFYONLY`, and records
  only the backup path and migration identifiers. The IIS application pool and
  scheduled application writer are stopped or drained before that backup.
  It refuses a catalog that lacks the four known Phase 1 baseline migrations,
  so it cannot initialise an unknown database during release.
- The seven scheduler migrations are additive scheduler tables/columns/indexes,
  binary-collation and canonical-key constraints, and the executor-dispatch,
  immutable-receipt and trusted-evidence tables. Existing `Jobs` values are
  validated by SQL during the collation/constraint step; failure stops the
  deployment before payload placement. The script never silently bypasses a
  failed migration.
- The migration command retains distinct scheduler, Phase 3, native worker,
  Outlook and Phase 5 families. The Outlook family ends at
  `20260812102333_AddOutlookBrowseTargetPath`; the nine-entry Phase 5 family
  ends at `20260820101021_CloseRetainedCsharpMixedOutcomes`, which is the
  deployment target. A later unreviewed migration cannot enter this release
  through the same confirmation.
- The staged payload replaces the deployment directory only after preflight
  and any authorised migration have succeeded.  The previous payload is kept
  under a unique rollback directory.  A swap failure restores that payload
  immediately.  A post-migration validation failure preserves both the rollback
  payload and verified SQL backup for an operator-directed recovery decision;
  it never restores a database automatically.
- Success requires deployed-assembly verification, `/health/live` 200,
  `/health/ready` 200, `/api/index-health` 200, `/api/gpu-status` 200, and
  `validate-sql` with the preserved local production connection configuration.
- `-KeepOutlookHostDisabled` disables and drains any pre-existing Outlook host
  task, never registers or enables a replacement, and never restores its
  enabled state after failure. The closeout path supplies this mode by default,
  and plan output reports `outlook_host_activation=false`.
- `phase-5-deployment-plan.ps1` defines the exact read-only live-validation
  contract. `validate-phase-5-deployment.ps1` checks migration history and
  required tables/fencing triggers with SQL metadata reads; requires 200 from
  `/operator-actions`, `/api/operator-actions`, `/search/csharp-code`, and an
  empty `/api/local/retained-csharp-code` search; and requires 403 for every
  forwarded/proxy header family on each route. It performs no HTTP mutation,
  source-original read, Outlook operation, model operation or runtime
  activation. Its only write is the sanitised record under `docs/operations`.
- The legacy Gmail guard remains closed by default. The explicit
  `-ConfirmApprovedLegacyLocalSurfaceChanges` switch permits only the thirteen
  reviewed Phase 5 local-surface path identities (including the approved
  generated-asset deletion); any other Gmail-owned path or any content change
  to an approved identity still fails closeout.

## Acceptance criteria

- Active CI and every native deployment or live-validation command contain no
  Python, pytest, npm, Docker, RabbitMQ, Vespa, dashboard, model or GPU
  deployment command. Closeout permits only the focused legacy Gmail pytest
  regression immediately before its feature and squashed-main commits; no
  other Python or pytest command is permitted.
- The closeout dry-run creates a native release plan and fails if a legacy
  command family reappears.
- The closeout script retains structured logs, commits, merges, pushes and
  purges only after all required native checks succeed.
- The deployment script rejects an external/wildcard or mismatched IIS target
  before it stops the app pool or changes SQL.
- A deployment preserves target-only configuration, creates and verifies a
  SQL backup before the approved migration, and leaves a rollback payload.
- All seven pending Phase 2 migrations appear in SQL migration history after the
  authorised update.
- All nine generated Phase 5 migrations appear in SQL migration history and
  the target is `20260820101021_CloseRetainedCsharpMixedOutcomes`.
- The loopback site returns 200 from every required liveness, readiness,
  index-health and scheduler-status endpoint, and native SQL validation
  succeeds.
- Legacy files may remain in the repository, but are absent from the active
  test, closeout and deployment paths.
- Deployment and every read-only Phase 5 validation probe complete before the
  validation-record commit, record push and worktree cleanup.
