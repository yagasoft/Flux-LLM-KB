# Phase 4 Outlook scheduled host implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deploy a hidden logged-in-user Windows Scheduled Task that invokes the
native Outlook COM host once at logon and every 15 minutes, while preserving
the current disabled-by-default Phase 4 safety model.

**Architecture:** `update-native-windows.ps1` publishes the existing Windows
host into an `outlook-host` sibling payload and registers a fixed PowerShell
launcher as a local scheduled-task action. The launcher passes only the local
SQL connection through the child process environment and starts the host with
the literal `--run-once` argument. Task Scheduler provides timing and
headlessness; the existing user-session mutex, durable SQL leases, cursor-last
ingestion and disabled-profile gate continue to own correctness.

**Tech stack:** PowerShell 5.1-compatible deployment scripts, Windows Task
Scheduler, .NET 10 Windows publish, existing native PowerShell contract tests,
classic Outlook COM host.

## Global constraints

- Classic desktop Outlook COM may run only in the logged-in Windows user
  session; no Windows Service, IIS, Docker or persistent daemon is allowed.
- The task starts the published host with exactly `--run-once`, with no
  `--verbose-com-errors` flag or private diagnostic output argument.
- The task is hidden, uses the interactive user token with no stored password,
  starts at user logon and repeats every 15 minutes with `IgnoreNew` overlap
  behaviour and a bounded run time.
- The launcher may read target-only `appsettings.Production.json` locally and
  set `ConnectionStrings__FluxKnowledge` only for its child process. It must
  not expose connection data, mailbox paths, spool paths or diagnostics in task
  XML, arguments, normal logs or source control.
- A disabled profile and no pending browse must exit successfully without COM
  activation. Scheduling does not enable profiles or create durable work.
- Existing Gmail code/configuration/tests/APIs/documentation remain untouched.
- Existing native REST, MCP and CLI Outlook surfaces remain read-only.
- Preserve existing read-only mailbox, SQL-authoritative lease/idempotency and
  cursor-last behaviour; do not add SQL schema, UI, API, SignalR or audit
  fields.
- Do not deploy, register the live task, enable a profile or run Outlook COM
  until all implementation verification and independent review pass and the
  user separately authorises deployment in this thread.

---

## File structure

- `scripts/deploy/run-outlook-host.ps1` — source-controlled fixed launcher
  template, installed only inside the native deployment's dedicated host payload.
- `scripts/deploy/update-native-windows.ps1` — host-payload publishing,
  scheduled-task lifecycle, pre-swap drain/rollback and post-deploy validation.
- `tests/native/outlook-scheduled-host-contract.ps1` — offline launcher and
  scheduler contract tests; no Task Scheduler registration or Outlook launch.
- `tests/native/native-deployment-plan.ps1` — authoritative deployment-plan
  projection checks for the host payload and scheduler policy.
- `tests/native/complete-feature-dryrun.ps1` — closeout contract update so the
  feature workflow requires the new scheduler tests but never registers a task
  in dry-run mode.
- `docs/architecture.md` and `docs/roadmap.md` — durable operational intent
  and state after the implementation has passed its gates.
- `docs/operations/native-windows-phase-4-outlook-ingress-validation.md` —
  aggregate-only installed-task validation after separately approved deployment.

## Task 1: Fixed headless launcher and offline contract

**Files:**

- Create: `scripts/deploy/run-outlook-host.ps1`
- Create: `tests/native/outlook-scheduled-host-contract.ps1`
- Modify: `tests/native/outlook-host-composition.ps1`

**Consumes:** Published `FluxKnowledge.OutlookHost.exe` and the production
settings file that resides one directory above the `outlook-host` payload.

**Produces:** A parameterless launcher that invokes only `--run-once`, and a
native test script that can enforce the launcher and host composition contract
without Outlook, SQL, a scheduled task or a mailbox.

- [ ] **Step 1: Write the failing launcher contract test**

  Create `tests/native/outlook-scheduled-host-contract.ps1` with source-level
  assertions that require a parameterless launcher, a local relative settings
  resolution, child-process-only `ConnectionStrings__FluxKnowledge` handling,
  `try/finally` cleanup, exact `--run-once`, and explicit rejection of verbose
  switches, credentials, mailbox/spool values and network clients:

  ```powershell
  $launcher = Join-Path $SourceRoot 'scripts\deploy\run-outlook-host.ps1'
  $text = Get-Content -LiteralPath $launcher -Raw
  if ($text -match '(?m)^\s*param\s*\(') { throw 'The Outlook launcher must not accept task arguments.' }
  if ($text -notmatch 'ConnectionStrings__FluxKnowledge' -or
      $text -notmatch '(?s)try\s*\{.*finally\s*\{') {
      throw 'The launcher must scope and clear the SQL connection value.'
  }
  if ($text -notmatch "--run-once" -or $text -match '--verbose-com-errors|spool|mailbox|credential|https?://') {
      throw 'The launcher action is not the fixed non-diagnostic local host invocation.'
  }
  ```

  Extend `tests/native/outlook-host-composition.ps1` to require the published
  executable, `Microsoft.Office.Interop.Outlook.dll` and `office.dll` in the
  host publish directory, not merely the DLL entry point.

- [ ] **Step 2: Run the focused launcher test to prove RED**

  Run:

  ```powershell
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/outlook-scheduled-host-contract.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  ```

  Expected: FAIL because `scripts/deploy/run-outlook-host.ps1` does not yet
  exist.

- [ ] **Step 3: Implement the fixed launcher**

  Add `scripts/deploy/run-outlook-host.ps1`. It must derive every path from its
  own installed directory, refuse missing host/settings files, read only
  `ConnectionStrings.FluxKnowledge` from the adjacent production settings JSON,
  and run the executable synchronously with an argument array containing only
  `--run-once`:

  ```powershell
  $ErrorActionPreference = 'Stop'
  $payloadRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
  $deployRoot = Split-Path -Parent $payloadRoot
  $hostPath = Join-Path $payloadRoot 'FluxKnowledge.OutlookHost.exe'
  $settingsPath = Join-Path $deployRoot 'appsettings.Production.json'
  if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf) -or
      -not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
      throw 'The deployed Outlook host payload is incomplete.'
  }
  $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
  $connection = [string]$settings.ConnectionStrings.FluxKnowledge
  if ([string]::IsNullOrWhiteSpace($connection)) { throw 'The local SQL connection is unavailable.' }
  $previous = $env:ConnectionStrings__FluxKnowledge
  try {
      $env:ConnectionStrings__FluxKnowledge = $connection
      & $hostPath '--run-once'
      exit $LASTEXITCODE
  } finally {
      if ($null -eq $previous) { Remove-Item Env:\ConnectionStrings__FluxKnowledge -ErrorAction SilentlyContinue }
      else { $env:ConnectionStrings__FluxKnowledge = $previous }
  }
  ```

  Do not add output redirection, diagnostics, options or parameters. The task
  itself supplies headlessness.

- [ ] **Step 4: Run GREEN checks**

  Run:

  ```powershell
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/outlook-scheduled-host-contract.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/outlook-host-composition.ps1
  dotnet build src/FluxKnowledge.OutlookHost/FluxKnowledge.OutlookHost.csproj -c Release -warnaserror --no-restore
  ```

  Expected: all pass; the composition publish contains both required Office
  interop assemblies and the launcher has no public/private argument leakage.

- [ ] **Step 5: Review and commit the coherent launcher slice**

  Ask an independent reviewer to check command-line privacy, the fixed
  `--run-once` invocation, no service/daemon addition and Windows PowerShell
  compatibility. Resolve only reported defects, then run `git diff --check`
  and commit:

  ```powershell
  git add scripts/deploy/run-outlook-host.ps1 tests/native/outlook-scheduled-host-contract.ps1 tests/native/outlook-host-composition.ps1
  git commit -m "feat: add headless Outlook host launcher"
  ```

## Task 2: Publish and register the per-user scheduled task

**Files:**

- Modify: `scripts/deploy/update-native-windows.ps1`
- Modify: `tests/native/outlook-scheduled-host-contract.ps1`
- Modify: `tests/native/native-deployment-plan.ps1`

**Consumes:** Task 1 launcher at `scripts/deploy/run-outlook-host.ps1`; native
host project `src/FluxKnowledge.OutlookHost/FluxKnowledge.OutlookHost.csproj`.

**Produces:** `outlook-host` payload publishing and named Task Scheduler
lifecycle helpers, with plan-only output that proves the fixed scheduler policy
without creating a task.

- [ ] **Step 1: Write failing scheduler/deployment-plan tests**

  Extend `tests/native/outlook-scheduled-host-contract.ps1` to require helpers
  named `New-OutlookHostTaskTriggers`, `Register-OutlookHostTask`,
  `DisableAndDrain-OutlookHostTask`, and `Assert-OutlookHostTask`; require
  `New-ScheduledTaskPrincipal` with `-LogonType Interactive` and `-RunLevel
  Limited`; and require `New-ScheduledTaskSettingsSet` with `-Hidden`,
  `-MultipleInstances IgnoreNew` and an execution limit under 15 minutes.

  Extend `tests/native/native-deployment-plan.ps1` to require this exact JSON
  projection from `-PlanOnly`:

  ```powershell
  if ($plan.outlook_host_scheduler.task_name -ne 'FluxKnowledge.OutlookHost' -or
      -not $plan.outlook_host_scheduler.interactive_only -or
      -not $plan.outlook_host_scheduler.hidden -or
      $plan.outlook_host_scheduler.interval_minutes -ne 15 -or
      $plan.outlook_host_scheduler.multiple_instances -ne 'IgnoreNew' -or
      $plan.outlook_host_scheduler.verbose_diagnostics -ne $false) {
      throw 'The native deployment plan has lost the approved Outlook scheduler boundary.'
  }
  if (-not $plan.outlook_host_payload.published -or
      $plan.outlook_host_payload.relative_directory -ne 'outlook-host') {
      throw 'The native deployment plan does not publish the Outlook host payload.'
  }
  ```

- [ ] **Step 2: Run the focused tests to prove RED**

  Run:

  ```powershell
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/outlook-scheduled-host-contract.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/native-deployment-plan.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  ```

  Expected: FAIL because the deployment script has neither scheduler helpers
  nor the `outlook_host_scheduler` / `outlook_host_payload` plan projection.

- [ ] **Step 3: Add fail-closed task helpers and publish the host payload**

  In `scripts/deploy/update-native-windows.ps1`, add fixed constants:

  ```powershell
  $OutlookHostTaskName = 'FluxKnowledge.OutlookHost'
  $OutlookHostPayloadDirectory = 'outlook-host'
  $OutlookHostIntervalMinutes = 15
  $OutlookHostExecutionLimit = New-TimeSpan -Minutes 14
  ```

  Implement the following helpers using only Windows PowerShell-compatible
  cmdlets:

  ```powershell
  function New-OutlookHostTaskTriggers {
      $logon = New-ScheduledTaskTrigger -AtLogOn
      $repeat = New-ScheduledTaskTrigger -Once -At ([DateTime]::Now.AddMinutes(1)) `
          -RepetitionInterval (New-TimeSpan -Minutes $OutlookHostIntervalMinutes) `
          -RepetitionDuration (New-TimeSpan -Days 3650)
      @($logon, $repeat)
  }

  function Register-OutlookHostTask([string]$DeployRoot) {
      $launcher = Join-Path (Join-Path $DeployRoot $OutlookHostPayloadDirectory) 'run-outlook-host.ps1'
      $powershell = Join-Path $PSHOME 'powershell.exe'
      $action = New-ScheduledTaskAction -Execute $powershell -Argument (
          '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $launcher)
      $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) `
          -LogonType Interactive -RunLevel Limited
      $settings = New-ScheduledTaskSettingsSet -Disable -Hidden -MultipleInstances IgnoreNew `
          -ExecutionTimeLimit $OutlookHostExecutionLimit -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
      Register-ScheduledTask -TaskName $OutlookHostTaskName -Action $action -Trigger (New-OutlookHostTaskTriggers) `
          -Principal $principal -Settings $settings -Description 'FluxKnowledge read-only Outlook COM host' -Force | Out-Null
  }
  ```

  `DisableAndDrain-OutlookHostTask` must disable only this named task, wait a
  bounded 30 seconds for its `Running` state to end, and return whether it was
  previously enabled. `Assert-OutlookHostTask` must read the registered task's
  action/principal/settings/triggers and reject a non-interactive user, visible
  task, wrong launcher, verbose switch, non-15-minute trigger, overlap policy
  other than `IgnoreNew`, or missing execution limit. It must not print task
  arguments if validation fails.

  During staging, publish the host project to
  `$stagingRoot\outlook-host`, copy the launcher template there, and require
  `FluxKnowledge.OutlookHost.exe`, `FluxKnowledge.OutlookHost.dll`,
  `Microsoft.Office.Interop.Outlook.dll`, `office.dll`, and the launcher before
  payload swap. Add their expected relative directory and scheduler policy to
  the existing `-PlanOnly` projection.

- [ ] **Step 4: Integrate safe lifecycle ordering**

  Store the prior task-enabled state before the existing `Move-Item` payload
  swap. The required ordering is:

  ```text
  stage Web + Outlook-host payload
  → disable and drain named task
  → swap deployment root
  → start/probe IIS and validate SQL
  → register named task disabled
  → assert registered task policy while disabled
  → enable named task
  → assert registered task policy while enabled
  ```

  In the existing catch/finally path, if the new payload was not installed,
  restore the old task's enabled state. If task registration, assertion or
  enablement fails after payload swap, keep the task disabled, fail explicitly
  if it cannot be disabled, restore IIS if needed and throw; never start a
  fallback COM process. Do not alter migration behaviour.

- [ ] **Step 5: Run GREEN checks**

  Run:

  ```powershell
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/outlook-scheduled-host-contract.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/native-deployment-plan.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/outlook-host-composition.ps1
  dotnet build src/FluxKnowledge.OutlookHost/FluxKnowledge.OutlookHost.csproj -c Release -warnaserror --no-restore
  ```

  Expected: all pass without registering a scheduled task or launching
  Outlook. The plan-only check proves scheduling metadata only.

- [ ] **Step 6: Independent review and commit**

  Ask an independent reviewer to inspect task principal/session semantics,
  fail-closed deployment ordering, task-argument privacy, `IgnoreNew` policy,
  rollback handling and accidental task activation during dry-run. Resolve only
  findings, rerun the focused matrix and `git diff --check`, then commit:

  ```powershell
  git add scripts/deploy/update-native-windows.ps1 tests/native/outlook-scheduled-host-contract.ps1 tests/native/native-deployment-plan.ps1
  git commit -m "feat: schedule native Outlook host"
  ```

## Task 3: Closeout contracts and operational documentation

**Files:**

- Modify: `scripts/dev/complete-feature.ps1`
- Modify: `tests/native/complete-feature-dryrun.ps1`
- Modify: `docs/architecture.md`
- Modify: `docs/roadmap.md`
- Modify: `docs/superpowers/specs/2026-08-12-phase-4-outlook-scheduled-host-design.md`
- Modify: `docs/superpowers/plans/2026-08-12-phase-4-outlook-scheduled-host-implementation.md`

**Consumes:** Task 2 native deployment contract and offline scheduler test.

**Produces:** A closeout path that exercises the scheduler contract before a
deploy step, plus durable documentation that distinguishes installed automation
from profile activation.

- [ ] **Step 1: Write failing closeout and documentation contract tests**

  Extend `tests/native/complete-feature-dryrun.ps1` so its expected native
  verification commands include, in order before a deploy action:

  ```powershell
  tests/native/outlook-scheduled-host-contract.ps1
  tests/native/outlook-host-composition.ps1
  tests/native/native-deployment-plan.ps1
  ```

  Add source checks that reject `Register-ScheduledTask` from the dry-run
  path, reject `--verbose-com-errors` in the task-registration helper, and
  require the feature closeout to preserve the explicit deployment gate.

- [ ] **Step 2: Run the closeout contract to prove RED**

  Run:

  ```powershell
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/complete-feature-dryrun.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  ```

  Expected: FAIL because the scheduler contract test is absent from the
  complete-feature verification sequence.

- [ ] **Step 3: Implement closeout and documentation updates**

  Add the three native checks to the existing closeout verification sequence;
  do not invoke deployment script execution from `-DryRun` / contract mode.

  Update `docs/architecture.md` to state that the native Outlook host is a
  hidden per-user interactive task triggered at logon and every 15 minutes, and
  that scheduling does not override the durable disabled-profile gate. Update
  the Phase 4 roadmap entry to describe the delivered scheduler only after the
  relevant code/tests pass, with remaining work explicitly including a
  separately authorised enabled-profile scheduled validation. Update the
  specification and this plan only to record verified implementation evidence;
  do not change their approved requirements.

- [ ] **Step 4: Run GREEN documentation and closeout checks**

  Run:

  ```powershell
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/complete-feature-dryrun.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/outlook-scheduled-host-contract.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/native-deployment-plan.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  git diff --check
  ```

  Expected: all pass; no dry-run registers a task, starts Outlook or exposes
  private values.

- [ ] **Step 5: Independent review and commit**

  Ask an independent reviewer to compare the final diff against the approved
  scheduler spec, especially the no-service/no-auto-enable boundaries and
  closeout ordering. Resolve only findings, then commit:

  ```powershell
  git add scripts/dev/complete-feature.ps1 tests/native/complete-feature-dryrun.ps1 docs/architecture.md docs/roadmap.md docs/superpowers/specs/2026-08-12-phase-4-outlook-scheduled-host-design.md docs/superpowers/plans/2026-08-12-phase-4-outlook-scheduled-host-implementation.md
  git commit -m "docs: close out scheduled Outlook host"
  ```

### Verified implementation evidence (2026-08-12)

Task 1 was committed as `05c7d85` and Task 2 as `f000c66`. Task 3 verified its
required RED state, then passed the isolated closeout dry-run together with the
offline launcher and native deployment-plan contracts. The closeout sequence now
checks the scheduler contract, Outlook host composition and deployment-plan
contract before any deployment step. No live deployment, scheduled-task
registration, Outlook launch, profile enablement, mailbox access or SQL access
occurred while gathering this evidence.

## Task 4: Full verification, deployment gate and aggregate validation

**Files:**

- Modify after approved live validation only: `docs/operations/native-windows-phase-4-outlook-ingress-validation.md`

**Consumes:** Completed Tasks 1–3 and an independent whole-branch review.

**Produces:** A separately authorised installed scheduled task validated while
the profile remains disabled, followed by an explicit choice whether to enable
recurring capture.

- [ ] **Step 1: Run the complete non-live verification matrix**

  Run:

  ```powershell
  dotnet restore FluxKnowledge.slnx --locked-mode
  dotnet test tests/FluxKnowledge.OutlookHost.Tests/FluxKnowledge.OutlookHost.Tests.csproj -c Release --no-restore
  dotnet build FluxKnowledge.slnx -c Release -warnaserror --no-restore
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/outlook-scheduled-host-contract.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/outlook-host-composition.ps1
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/native-deployment-plan.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/native/complete-feature-dryrun.ps1 -SourceRoot E:\LLM KB\.worktrees\outlook-scheduled-host-design
  git diff --check
  ```

  Expected: all pass with zero build warnings. No host, task, COM or mailbox is
  started by this matrix.

- [ ] **Step 2: Whole-branch independent review**

  Give an independent reviewer the approved spec, plan, final diff and current
  command output. Require review of privacy, task action/principal/trigger
  semantics, update drain/rollback, no-service boundary, disabled-profile COM
  gate, Gmail non-regression and test adequacy. Resolve only Critical or
  Important findings, then repeat affected RED/GREEN tests and re-review.

- [ ] **Step 3: Stop for explicit deployment authority**

  Report the verification/review result and ask for explicit authority to run
  `scripts/dev/complete-feature.ps1` with the deployment/apply-migrations
  parameters. Do not deploy, register a live task or change the paused profile
  before that response.

- [ ] **Step 4: After explicit deployment authority, run feature closeout**

  Run the repository-owned closeout rather than assembling an alternative
  deployment sequence:

  ```powershell
  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/dev/complete-feature.ps1 `
    -FeatureWorktree 'E:\LLM KB\.worktrees\outlook-scheduled-host-design' `
    -MainRoot 'E:\LLM KB' `
    -CommitMessage 'Schedule native Outlook host' `
    -ApplyMigrations -ConfirmApplyMigrations
  ```

  Expected: repository verification, merge/push and deployment complete; the
  task is registered hidden and interactive-only but the profile remains
  disabled. If it fails, report its JSON `failed_step` and `log_path`, fix only
  that failure, and rerun the same closeout command.

- [ ] **Step 5: After deployment, perform only the disabled-profile task probe**

  With deployment authority, inspect the named task's sanitised configuration,
  trigger it once through Task Scheduler, and verify a zero exit result with no
  Outlook COM activation while the profile is disabled. Append only aggregate
  task state, exit result and disabled-profile evidence to the validation
  record. Do not write private task arguments, identities, paths, connection
  values or diagnostic content.

  Do not enable the profile in this task. Request a separate explicit operator
  decision before enabling recurring mailbox capture and observing a later
  scheduled catch-up.
