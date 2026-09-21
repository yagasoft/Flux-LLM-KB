# Setup and verification

## Development prerequisites

Use Windows, PowerShell 7, Git and the .NET SDK selected by
[global.json](../global.json). Native tests use a local SQL Server or the
supported `(localdb)\MSSQLLocalDB` instance through
`scripts/dev/ensure-disposable-sql.ps1`. SQL Full-Text is required for the
corresponding retrieval checks. The test fixture accepts only a validated
server-level loopback connection and creates uniquely named disposable databases.
Never provide the application's production database connection to tests.

```powershell
dotnet tool restore
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build --filter "Category!=Browser"
pwsh -NoProfile -File tests/native/repository-contract.ps1
pwsh -NoProfile -File tests/native/complete-feature-dryrun.ps1
pwsh -NoProfile -File tests/native/native-deployment-plan.ps1
```

Browser checks use `scripts/dev/test-browser.ps1` and the guarded disposable
browser helper. Use an existing browser installation where available. The
normal suite reports browser tests as skipped unless that route is enabled.
No ordinary test is allowed to acquire a real model.

Production startup loads canonical configuration with no-follow path checks;
an unrestricted development `dotnet run` is not an installation procedure.
Tests provide their own isolated host composition and synthetic fixtures.

## Production prerequisites and layout

The native installation requires IIS with the matching ASP.NET Core hosting
support, a supported local SQL Server with Full-Text, and the required Windows
filesystem and permissions. Desktop Outlook/Visio processing additionally
requires the installed desktop application and a logged-in user session.

The fixed application hierarchy is:

```text
I:\FluxKnowledge\
  App\
  Config\
  Data\Sql\Data\FluxKnowledge.mdf
  Data\Sql\Log\FluxKnowledge_log.ldf
  Data\Index\
  Data\Retained\
  Runtime\Spool\
  Runtime\Temp\
  Runtime\Logs\
  CodexPlugin\
  Recovery\
```

The sole model store is `J:\Models`. Model manifests and inventory must identify
immutable revisions, exact dependencies, hashes and byte lengths. Offline
loading refuses missing or invalid content. A missing artifact requires a
separate exact acquisition proposal and approval after all relevant caches are
checked; setup, restore and startup never grant download permission.

## Existing installation updates

Review the incremental updater's plan before applying any change:

```powershell
pwsh -NoProfile -File scripts/deploy/update-native-iis-incremental.ps1 `
  -SourceRoot . -PlanOnly
```

Use `-Apply` only with current approval for the concrete target and change.
The updater validates the candidate under a hold, checks fixed-loopback health
and retained state, and keeps an application payload rollback path. Schema
changes require its separate reviewed migration opt-in and compatible rollback
conditions. Updating code in Git does not authorise deployment, restart or
migration. Model caches and source originals are outside the update payload.

## Clean-slate installation

The separate one-shot entry point is `scripts/dev/complete-feature.ps1 -GoLive`.
It requires all four acknowledgements: `-ConfirmCleanSlate`,
`-ConfirmConfigureVss`, `-ConfirmDestroySql` and `-ConfirmRegisterCodex`.
It proceeds only from an absent application root and target catalogue or after
wiping them in that same authorised invocation. It binds execution to a merged
payload, validates VSS prerequisites, provisions SQL, starts approved native
tasks, registers the native plugin and validates the result.

This is destructive installation, not a routine updater. It has no journalled
resume or automatic restore. A failure ends the invocation and a further attempt
needs new operational approval. The intended OS-managed shadow-storage cap is
10% of `I:`; a recovery plan is not evidence that recovery has been exercised.
The commands in this document do not authorise a GoLive invocation.

## Native clients

Clients use `http://127.0.0.1:5137`, `/api/v1` and `/mcp`. The CLI accepts JSON
on standard input and returns the canonical envelope:

```powershell
'{"view":"overview"}' | FluxKnowledge.Cli operations status
'{"query":"example","limit":10}' | FluxKnowledge.Cli knowledge search
'{"action":"note_create","title":"Example","body":"Safe retained note"}' |
  FluxKnowledge.Cli knowledge write --preview
FluxKnowledge.Cli codex plugin status
```

Commit a mutation only with the exact previewed command, its confirmation and
a caller-provided idempotency key. See [integrations](integrations.md).
The native marketplace belongs under `I:\FluxKnowledge\CodexPlugin`. Normal
startup and CLI diagnostics do not install, repair or alter plugin registrations.
Hook trust remains a user action in Codex.

## Feature closeout

Use a dedicated `codex/` worktree and run `scripts/dev/complete-feature.ps1` for
feature closeout. Its default path verifies, commits, integrates and pushes the
change without deploying. It emits step evidence including `failed_step` and
`log_path`; resolve failures before rerunning. `-KeepWorktree` preserves the
checkout. `-GoLive` is a separate operational action with the gates above.
