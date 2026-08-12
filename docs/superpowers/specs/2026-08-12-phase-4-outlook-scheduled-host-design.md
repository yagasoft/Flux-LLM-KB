# Phase 4 Outlook scheduled host design

## Goal

Run the existing native classic Outlook host automatically and headlessly every
15 minutes on this Windows PC, without changing its COM/session boundary or
making Outlook capture active by default.

The scheduled task is an execution mechanism for already-authorised durable
SQL work. It neither enables a profile nor creates a browse or catch-up
request. The currently paused rollout profile remains disabled during
installation. An operator must explicitly enable it through the loopback
Outlook page before a future scheduled invocation can claim capture work.

## Scope and non-goals

In scope:

- Publish `FluxKnowledge.OutlookHost` with the native Windows deployment.
- Install a hidden per-user Windows Scheduled Task that invokes exactly one
  `--run-once` host process at logon and every 15 minutes thereafter.
- Use a deployed local launcher to read the local production configuration and
  provide its SQL connection only through the child process environment.
- Make update/redeployment disable, drain and re-register the task safely.
- Prove the task contract and the disabled-profile no-COM path with focused
  tests, followed by a bounded local validation.

Out of scope:

- A Windows Service, IIS/Docker COM host, persistent host daemon or new
  network listener.
- SQL tables, migrations, profile/UI/API/MCP/CLI changes, automatic profile
  enabling, mailbox mutation, Gmail changes, or a background diagnostics
  subsystem.
- Automatically enabling verbose COM diagnostics. The approved explicit
  `--verbose-com-errors` mode remains manual-only.

## Architecture

The native deployment script publishes the Windows-only host into a dedicated
`outlook-host` payload directory below the deployed application root, together
with a narrow PowerShell launcher. The launcher is the only scheduled-task
action. It:

1. Resolves only files below the deployed payload and its adjacent production
   settings file.
2. Reads the local SQL connection value in memory, sets
   `ConnectionStrings__FluxKnowledge` only for the child process, and invokes
   the published host with the literal argument `--run-once`.
3. Uses no mailbox path, credential, diagnostic option or private spool value
   in its command line, task XML, normal logs or source-controlled files.
4. Propagates the host exit code to Task Scheduler and clears the process
   environment value in `finally`.

The task runs with the current user's interactive token, limited privileges and
no stored password. It is hidden, starts at user logon, and has a second
repeating 15-minute trigger. `IgnoreNew` rejects overlap. The host's existing
per-user session mutex and SQL fencing remain independent second lines of
defence. A task run has a bounded execution limit shorter than the next trigger;
if a process is terminated or exits unexpectedly, the existing lease expiry,
retry and cursor-last rules recover work on a later run.

The host remains headless: Task Scheduler hides the task and the launcher does
not create a visible shell. The host still runs in the logged-in interactive
Windows session, which is mandatory for classic Outlook COM. It may process a
pending operator-requested folder browse as well as an eligible catch-up; it
never invents either kind of work. When no durable work is eligible, it exits
without COM activation.

## Deployment and recovery

`update-native-windows.ps1` owns the task lifecycle:

1. Before replacing the deployment root, locate only the named Outlook task,
   disable it, and wait for any running invocation to finish within a bounded
   timeout. A timeout fails deployment rather than replacing an active host
   payload.
2. Publish the host payload, deploy the web and host files together, and then
   register or update the hidden task with the fixed action and current
   interactive-user principal.
3. Validate that the registered task is interactive-only, hidden, non-
   overlapping, uses the expected 15-minute trigger and contains no verbose
   diagnostic flag or connection value.
4. If registration or validation fails, deployment fails closed; the host is
   not started as a compensating action.

The scheduler itself has no retry loop beyond its regular trigger. A transient
COM or SQL failure therefore follows the host's existing bounded durable retry
semantics, and the next scheduled run is safe. The task does not run when the
user is signed out. This is deliberate: a non-interactive session must not
attempt desktop Outlook automation.

## Security and privacy

- Task Scheduler runs a single published binary through a fixed local launcher
  under the interactive user. It stores no password.
- Connection data remains in the target-only local settings file and child
  process environment. It is not emitted to task arguments, task history,
  repository files or public application surfaces.
- The task starts without `--verbose-com-errors`; raw COM diagnostics remain
  opt-in, local and private according to the existing Phase 4 contract.
- Existing read-only Outlook COM behaviour, private spool ownership, exact
  folder selection, EntryID dedupe, SQL-authoritative cursor-last commit and
  deferred capability handling are unchanged.

## Acceptance criteria

1. Native deployment publishes the host and contains the Office/Outlook
   interop dependencies already required by the host composition test.
2. A registered `FluxKnowledge.OutlookHost` task is hidden, interactive-user
   only, logon-plus-15-minute recurring, non-overlapping and bounded. Its
   action is the fixed launcher; it has no verbose diagnostics or secret-like
   arguments.
3. Task execution invokes `--run-once` exactly once. With every profile
   disabled and no pending browse, it exits successfully without activating
   COM. With an explicitly enabled profile and durable due work, it continues
   to use existing catch-up/lease/cursor safeguards.
4. Deployment disables and drains the task before payload replacement, then
   re-registers and validates it. A drain, publish, registration or validation
   failure stops deployment safely.
5. Native deployment contract tests, scheduler/launcher tests, existing host
   tests and a Release warning-free build pass. A bounded local validation
   proves the installed task can launch headlessly while the profile remains
   disabled; a separately authorised enabled-profile validation may follow.

## Risks and decisions

- A scheduled task cannot automate Outlook while the user is signed out. This
  is an intentional COM safety boundary, not an availability defect.
- `IgnoreNew` can skip one nominal trigger if a previous run is still active.
  SQL claim fencing and the next trigger preserve correctness; no concurrent
  host is allowed merely to improve throughput.
- The first live deployment installs the task but leaves the current profile
  paused. Enabling recurring mailbox capture is a separate explicit local
  operator action after the task validation succeeds.

## Verified implementation evidence (2026-08-12)

The fixed launcher and offline host-composition contract were committed in
`05c7d85`, and the Outlook payload publishing and scheduler policy contract in
`f000c66`. Task 3 added the three scheduler checks to the repository closeout
sequence and verified the isolated closeout dry-run, launcher contract and
native deployment-plan contract. This is offline implementation evidence only:
no task was registered, Outlook was started, profile enabled, mailbox accessed
or SQL connection used. The deployment and both disabled-profile and separately
authorised enabled-profile scheduled validation gates remain open.
