# Phase 2 deterministic native-worker supervision design

Status: approved conceptual design, pending review of this written
specification. It authorises a later implementation plan only; it does not
authorise model or GPU activation, deployment, IIS changes, migration
application outside disposable SQL catalogues, external access or legacy work.

## Decision

Complete the remaining Phase 2 process-lifecycle proof with one app-owned,
deterministic native worker process. The worker is a compiled local console
program, supervised by the application through a private Windows named pipe.
It accepts only the existing opaque `GpuExecutorBatchHandle` and scripted test
instructions. It does not load a model, use a GPU API, read source bytes, open
a network connection, inspect a runtime or expose a public endpoint.

The existing SQL-authoritative scheduler, dispatch, receipt and lifecycle
fences remain the sole authority for work state. A worker process, PID,
heartbeat, exit, timeout or parent-host restart is evidence about an instance;
none alone proves task completion, capacity release or a parent Job outcome.

## Scope

This slice adds:

- a deterministic worker executable owned and versioned by the application;
- a default-disabled application supervisor and private executor adapter;
- private named-pipe handshake, heartbeats and protocol validation;
- durable private instance state and sanitised lifecycle/audit evidence;
- PID and process-start-time identity attestation, restart-safe liveness
  reconciliation, graceful idle shutdown and tightly fenced forced-stop proof;
- real child-process integration tests alongside the existing fake-adapter and
  SQL scheduler test matrices.

It preserves the six public Job states, the existing scheduler admission and
dispatch boundary, the private lifecycle sink and read-only public status
surfaces.

It excludes:

- model/cache download, loading, conversion, execution or GPU/driver/runtime
  probing;
- source, artefact, settings, credential or result-payload transfer to the
  child process;
- public REST, MCP, CLI, SignalR or Blazor mutation/callback routes;
- Windows Service installation, external process discovery or cross-machine
  control;
- automatic retry, requeue, capacity release, output materialisation or parent
  Job completion inferred from process state; and
- deployment, IIS restart, live migration application, external access,
  RabbitMQ, Docker, Vespa and legacy action.

## Options considered

| Option | Decision |
| --- | --- |
| App-owned named-pipe child process | Chosen. It proves real launch, identity, supervision, termination and restart behaviour without a network surface or inference dependency. |
| Windows Service worker | Deferred. Service installation, account management and independent uptime are a separate operational design. |
| Loopback HTTP child process | Rejected. It adds an unnecessary listener and authentication boundary. |

## Components and data flow

1. `FluxKnowledge.DeterministicWorker` is a small console executable. It
   supports a fixed protocol version and deterministic modes used only by test
   configuration: acknowledge-and-hold, receipt-and-complete, exit-before-
   acknowledgement and unresponsive. It has no references to inference,
   source, filesystem-payload or network packages.
2. `NativeWorkerSupervisorService` owns launch and reconciliation. It is
   registered with an option defaulting to disabled; normal production
   composition therefore still registers no active executor adapter or child
   process.
3. When explicitly enabled for a controlled test host, the supervisor creates a
   private named pipe with an ACL restricted to its app identity, writes a
   `Launching` instance record, then uses `ProcessStartInfo` with shell execute
   disabled and an explicit executable path. The command line carries only a
   pipe name, protocol version and opaque instance ID.
4. The first pipe connection must have the expected client PID. The supervisor
   compares the PID and process start time with its launch attestation, then
   sends a fresh in-memory session nonce over the authenticated pipe. The nonce
   is never persisted, logged or exposed. Subsequent protocol frames must carry
   that nonce and the agreed protocol version.
5. The adapter delivers only an existing durable fenced handle. The worker may
   acknowledge it and send scripted, bounded lifecycle frames. The parent maps
   those frames through the existing `IGpuExecutorLifecycleSink`; the worker
   never writes SQL or calls scheduler stores directly.
6. Heartbeat and process-exit observations update private instance evidence.
   A worker exit, missing heartbeat, pipe failure, PID/start-time mismatch or
   parent restart marks the associated dispatch and capacity uncertain through
   the existing explicit lifecycle contract. It cannot release capacity,
   complete tasks or trigger a replacement.

## Durable state and evidence

The implementation adds private SQL records, with exact names and migration
shape chosen by the approved implementation plan:

- **Worker instance:** opaque instance and executor identifiers; private PID and
  process-start attestation; executable fingerprint; protocol version; state;
  launch, connect, last-heartbeat and exit times; and rowversion.
- **Worker lifecycle evidence:** append-only operation ID, instance fence,
  closed evidence class, observation time, bounded numeric exit/outcome code
  where relevant, canonical request fingerprint and creation time.

The allowed lifecycle classes are closed and sanitised: launch requested,
launch failed, connected, ready, heartbeat observed, graceful stop requested,
graceful stop confirmed, identity mismatch, unresponsive, exited, lost,
termination requested, termination confirmed and termination failed. No table
stores pipe names, nonce values, command lines, raw exception text, source text,
model identity, settings or environment variables.

The application appends correlated `native_worker.*` audit events using only
the allowed class, instance correlation and bounded reason code. Public status
remains aggregate-only and must not expose instance IDs, PIDs, process start
time, executable paths or fingerprints.

## Supervision and recovery rules

- A launch receives a new opaque instance ID and records `Launching` before
  process creation. A launch failure records a bounded reason and leaves the
  scheduler unchanged.
- A worker is `Ready` only after pipe identity attestation and a valid protocol
  handshake. A same-instance reconnect is an idempotent replay; a different PID
  or start time is rejected and recorded as an identity mismatch.
- Reconciliation observes the tracked PID and start time plus the pipe
  heartbeat. It never adopts an arbitrary process found by executable name or
  PID alone.
- On host restart, a prior child cannot be silently adopted. The new host marks
  the old instance lost unless it obtains a new fully attested connection; any
  active handle becomes uncertain under the existing scheduler contract.
- Graceful shutdown is allowed only after the supervisor proves that the worker
  has no active dispatched handle. It sends a fenced stop operation and records
  the observed exit.
- Forced termination is permitted only in controlled test execution after any
  active handle has first been made uncertain. The termination request and
  observed result are durable evidence. It is not a default host-shutdown or
  recovery action.
- Timeout, cancellation, worker exception, pipe disconnect, lost prompt and
  host restart all preserve durable dispatch and task records except for the
  explicitly recorded uncertainty transition. They do not retry, requeue,
  create a second worker, infer a result or free a slot.

## Verification plan

Native C# tests begin with focused failures and then fresh GREEN evidence:

| Invariant | Evidence |
| --- | --- |
| Disabled-by-default composition | Web composition proves no worker, adapter or process starts without the explicit test option. |
| Launch and attested handshake | Real child-process test proves expected PID/start time and protocol/nonce validation; a wrong PID, replayed nonce or version mismatch has no mutation. |
| Fenced dispatch delivery | A child receives only the original durable handle; duplicate delivery and reconnect cannot create a batch, reservation, receipt or second worker. |
| Exit and unresponsiveness | Real exit and withheld heartbeat mark only the exact instance/dispatch uncertain; no completion, release or retry occurs. |
| Restart and PID reuse | Recreated host rejects stale PID/start-time evidence and cannot adopt or replace an uncertain active worker. |
| Shutdown and termination | Idle graceful stop persists its evidence; forced-stop test first persists uncertainty and never affects unrelated work. |
| Privacy and public safety | SQL and endpoint tests prove no PID, pipe, nonce, executable path, fingerprint, raw diagnostic or protocol frame appears in public status, audit details, MCP, CLI or UI. |
| SQL integrity | Disposable SQL tests prove restrictive foreign keys, idempotent operation records, concurrent reconciliation fencing and transaction rollback. |

Focused verification begins with the existing executor-recovery and composition
tests, then adds worker-process integration tests. Milestone verification uses
a zero-warning Release build, disposable SQL suites, public-surface tests and a
repository-wide search for unapproved model, GPU, network and public-mutation
paths. No live deployment or migration application is part of this design.

## Rollback and approval gates

The implementation remains on an isolated feature branch. Safe rollback before
deployment is source-only: revert the coherent commits and discard generated
disposable test catalogues. Do not use a migration `Down` operation against a
live database.

Separate approval is still required before enabling the supervisor outside
controlled tests, applying a migration to a non-disposable database, deploying
or restarting IIS, starting a real inference runner, probing drivers/runtimes,
activating a model or GPU, adding a Windows Service, or making any external or
legacy integration change.
