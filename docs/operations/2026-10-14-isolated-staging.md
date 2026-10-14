# Isolated staging

Production operations require specific current user approval. The normal Web and
CLI executables retain their canonical production configuration and endpoint.
Use the separate `FluxKnowledge.StagingHost` for functional trials. It does not
load production configuration, register scheduled tasks or configure Codex hooks.

## Functional host

`scripts/dev/provision-isolated-staging.ps1` provides a create-only `-PlanOnly`
and `-Apply` path. It derives files under one named directory in
`E:\Codex Workspaces\Staging\FluxKnowledge`, creates a separate SQL database and
runtime login, and writes private configuration and a non-secret receipt.
Inspect the plan and obtain the applicable technical review before Apply.
An existing root, database or login is a stop condition; failed provisioning
preserves its files and receipt for inspection.

Publish `tools/FluxKnowledge.StagingHost/FluxKnowledge.StagingHost.csproj` into
the owned staging `App` directory. With that host stopped, run:

```powershell
& '<staging-root>\App\FluxKnowledge.StagingHost.exe' `
  --settings-file '<staging-root>\Config\staging.json' --migrate
```

Migration verifies the restricted runtime catalogue and exact SQL files first,
then uses the current Windows provisioner only for that staging catalogue. It
proves a fresh empty catalogue through the existing serialisable bootstrapper.
The runtime login receives reader/writer/execute access, catalogue-specific
`VIEW DEFINITION`, and database-local `CREATE FULLTEXT CATALOG`. The last
permission enables the existing `sys.dm_fts_parser` read path and also permits
full-text catalogue creation within staging. No server role is granted.

Start the same command without `--migrate`. The host checks its SQL privileges
and files, pins a separate `127.0.0.1` listener, and admits source folders only
beneath its own `Sources` directory. Configuration, index, retained content,
temporary files and both protection-key stores remain inside the staging root.
`/staging` describes the mode; `/health/ready` uses the actual staging binding.

This initial host uses deterministic embeddings for functional checks. Real
semantic models, GPU execution, OCR and mail are disabled; a visible staging
banner identifies that limit. Its native corpus search reports lexical retrieval
with semantics not enabled. These results do not establish semantic quality,
the Release 1 performance target or IIS heap-crash prevention.

The staging CLI reuses the native command contract with an independently pinned
origin and a 25-second HTTP deadline:

```powershell
'{"query":"example","scope":"workspace","cwd":"<staging-source>"}' |
  & '<staging-root>\App\FluxKnowledge.StagingHost.exe' `
    --settings-file '<staging-root>\Config\staging.json' --cli corpus search
```

The normal CLI remains pinned to its production port. Stop only the recorded
staging process when updating staging; preserve its data and evidence.

## Disposable SQL tests

`scripts/dev/provision-disposable-sql.ps1` has a separate reviewed create-only
plan/apply path. Its private test principal has `CREATE ANY DATABASE`, without
`sysadmin`, `dbcreator`, `ALTER ANY DATABASE`, `CONTROL SERVER` or production
database access. This principal is for test provisioning, never the running app.
Shared SQL provides logical data isolation and still shares machine resources.
Coordinate large scale tests and latency acceptance runs.

Load the generated private configuration into the test process without printing
it or passing credentials on a command line:

```powershell
$testSettings = Get-Content -LiteralPath '<test-root>\Config\test-sql.json' -Raw |
  ConvertFrom-Json
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = $testSettings.ServerConnectionString
$env:FLUXKNOWLEDGE_TEST_SQL_ROOT = $testSettings.FileRoot
```

`NativeSqlServerFixture` fails closed without both explicit bindings. Ordinary
and previous-migration fixtures create GUID-named catalogues with exact files in
the owned test root. They check creator ownership and exact file membership
before migration and destructive disposal. An unacknowledged creation outcome
is preserved. The running staging application's database is never used for
disposable test clears.

Session-loss tests interrupt all connections only in their exclusive fixture
catalogue, after verifying acknowledged creation, owner and exact files. They
cycle that catalogue through `SINGLE_USER` and restore `MULTI_USER` from a fresh,
bounded master connection even after a failed interruption. Unconfirmed
restoration fails the test and preserves the error. They require no global
`KILL` or server-state visibility. The shell integration receives only the exact
fixture-generated guarded operation through its process environment.

Use `scripts/dev/complete-feature.ps1` with those process environment variables,
without `-GoLive`. The hook repair's earlier closeout exception does not apply
to subsequent work. No test or closeout command grants production authority.
