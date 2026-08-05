# Phase 2 strict-priority scheduler design

Status: scheduler foundation implemented, merged and validated on the fixed
local loopback IIS site. The next executor/result boundary is a separate
approved design: [Phase 2 executor/result boundary design](2026-08-03-phase-2-executor-result-boundary-design.md).

## Goal

Add a durable, in-process strict-priority GPU mini-task scheduler to the native
IIS application. It must choose bounded compatible batches by lane order and
FIFO order, preserve the existing six public Job states, and make normal
in-process contention event-driven. The scheduler must never infer that a GPU
slot is free merely because time passed.

## Scope

This checkpoint delivers the scheduler foundation and its local read-only
status projection.

- Durable mini-task hand-off from a claimed worker Job to `GPU queued`.
- Durable queue, batch, capacity-slot, admission-generation and scheduler-wake
  state in SQL Server.
- Strict lane selection, FIFO ordering, compatible bounded batching and
  non-pre-emptive safe-boundary barriers.
- Event-driven in-process wake-up after committed work-ready, safe-boundary,
  completion and capacity-release events.
- Read-only local REST and Blazor/SignalR status projections with sanitised
  queue and capacity information.
- SQL integration, concurrency, hosted wake-loop and Web-contract evidence.

It does not add a model, a GPU runtime, a model cache, model execution,
external access, a legacy/RabbitMQ action, a mutation endpoint, deployment or
a production migration. Deterministic Phase 1 embedding remains CPU-only and
does not enter this scheduler.

## Existing contracts retained

- SQL Server remains canonical for all scheduler state. The scheduler's
  in-memory signal is only a prompt to inspect durable state.
- `Job` exposes exactly the existing public states: `WorkerQueued`,
  `WorkerProcessing`, `GpuQueued`, `GpuProcessing`, `Completed` and `Failed`.
  Mini-task and capacity states are private implementation detail.
- A worker-to-scheduler hand-off verifies the parent Job, source revision,
  owner and lease generation in the same transaction that creates durable
  mini-task rows and changes the parent Job from `WorkerProcessing` to
  `GpuQueued`.
- CPU-only classification, routing, archive expansion, metadata work,
  chunking and ordinary code parsing never create a GPU mini-task.
- A future GPU executor owns model output and final pipeline-stage completion;
  this scheduler checkpoint does not invent output or emulate an approved
  model. Its durable adapter and receipt boundary is defined separately in the
  [Phase 2 executor/result boundary design](2026-08-03-phase-2-executor-result-boundary-design.md).

## Priority and batch rules

The globally fixed lane order is:

1. `InteractiveRetrieval`
2. `DocumentIndexing`
3. `ImageOcr`
4. `ImageEnrichment`
5. `VideoOrUnknown`

Within a lane, order is FIFO by durable creation sequence and mini-task ID.
The head task determines a candidate batch's lane, model/runtime key and
settings/dimensions fingerprint. The scheduler adds only later tasks with that
same compatibility key, in FIFO order, until either configured positive bound
(`MaxBatchItems` or `MaxBatchEstimatedBytes`) is reached. It never mixes a
lane, model/runtime, dimensions or incompatible settings in one batch, never
uses ageing or automatic promotion, and never lets model identity override
the global lane order.

One scheduler admission round creates at most one batch. It does not start the
next individual mini-task after admitting an item. The next round occurs only
after a new durable scheduler wake, so a completed/released batch is a real
safe scheduling boundary at which the full queue is reconsidered.

## Durable state model

### Mini-task state

`GpuMiniTasks` retain their immutable parent/revision, lane, compatibility,
memory estimate, admission generation and idempotency key. They gain private
execution fields:

| Field / concept | Meaning |
| --- | --- |
| `ExecutionState` | `Ready`, `Active`, `Completed`, or `OutcomeUncertain`; not a public Job state. |
| `DeferredUntilUtc` | Nullable. It is set only for uncertain/unavailable capacity or a transient reservation failure before execution. Normal in-process contention leaves it null. |
| `BatchId` | Nullable durable association with the one admitted batch that owns the mini-task. |
| `AdmissionGeneration` | Increments only on a new explicit admission; it fences late boundary, completion and release callbacks. |
| `ReservationAttemptCount` | Diagnostic evidence for pre-execution reservation failures. It drives a capped delay calculation, never a capacity-pressure failure or execution timeout. |

`Ready` with a null `DeferredUntilUtc` is immediately eligible when a capacity
slot is available. A `Ready` task with a future deferral remains eligible for
early reconsideration after a real capacity-release wake; the admission gate
may write a new capped delay if capacity is still genuinely unavailable.

`Active` and `OutcomeUncertain` are never changed by elapsed time, heartbeat
age, owner age, a missed in-memory wake, an IIS recycle or a scheduler restart.

### Batch and capacity-slot state

A durable `GpuBatches` record represents the unit admitted to a capacity slot.
It stores a batch ID, compatibility key, aggregate bounds, admission generation,
owner identity, diagnostic heartbeat timestamps and an explicit lifecycle:

| State | Entry | Exit |
| --- | --- | --- |
| `Active` | A successful fenced admission decision. | Explicit `SafeBoundary`, `Completed` or `CapacityReleased` event; or diagnostic transition to `CapacityUncertain`. |
| `AtSafeBoundary` | The executor explicitly confirms a safe boundary and declares whether it retains or releases its capacity reservation. | Explicit subsequent completion/release or an executor-owned next action. |
| `Completed` | A matching, verified completion event. | Terminal for the batch. |
| `Released` | A matching capacity-release event. | Terminal for the reservation; no output is implied. |
| `CapacityUncertain` | Diagnostics indicate the owner is unresponsive while execution might continue. | Only an explicit trusted reconciliation record; it does not automatically change the mini-task outcome. |

Each logical GPU admission slot is `Available`, `Reserved` or `Uncertain`.
Only an explicit matching release/completion event moves a `Reserved` slot to
`Available`. A stale owner or heartbeat moves the slot to `Uncertain`, not
`Available`.

A safe-boundary event by itself never implies that capacity is free. It must
durably declare whether the executor released or retained its reservation. If
it retained capacity, the scheduler re-evaluates queue priority for status and
the next decision but cannot admit another batch to that slot until an explicit
matching release or trusted reconciliation makes it `Available`.

An `OutcomeUncertain` mini-task keeps its parent Job in `GpuProcessing` and is
not requeued automatically. Trusted reconciliation may establish that a task
did not execute, that a result was verified, or that an operator action is
required. Capacity reconciliation and task/result reconciliation are separate:
reliable release evidence may make a slot available for other work without
resubmitting or replacing the uncertain batch's result.

## Scheduler wake and admission flow

`GpuSchedulerState` is a singleton durable record. Every committed hand-off,
safe-boundary, completion, capacity release, trusted reconciliation or
pre-execution deferral advances its wake generation and records a sanitised
reason flag. The transaction commits before it invokes the coalescing local
`IGpuSchedulerWakeSignal`.

The local signal is a bounded prompt, not the source of truth. It preserves a
`CapacityReleased` reason even when several events coalesce. On application
start, and on a bounded fallback interval, the hosted scheduler reads the
durable wake generation and queue state so a missed signal cannot strand work.
A missed-wake or restart-recovery recheck is scheduler-level durable timing,
not a per-task execution timeout or automatic requeue of an active batch.

For each wake the scheduler:

1. Takes a short transaction-scoped SQL application lock for admission
   selection. It never holds this lock while executing GPU work.
2. Reads every ready lane and current capacity-slot state from SQL. Ordinary
   selection includes ready tasks with no deferral or an elapsed deferral; a
   genuine capacity-release reason additionally includes future-deferred ready
   tasks for immediate reconsideration.
3. If an eligible slot is `Uncertain`, admits nothing to that slot. It records
   diagnostic/status evidence only; no task is requeued or delayed merely
   because the executor is old.
4. Otherwise selects the lowest eligible lane and FIFO head, forms exactly one
   bounded compatible batch, and requests a pure admission/reservation
   decision. The decision does not invoke a model or execute GPU work.
5. On admission, atomically writes the batch, marks its mini-tasks `Active`,
   advances their admission generation, reserves the capacity slot and changes
   affected parent Jobs to `GpuProcessing`. The future executor receives only
   this fenced batch handle.
6. On normal in-process contention, leaves tasks ready with no deferred time
   and waits for a committed safe-boundary or capacity-release wake.
7. On uncertain/unavailable capacity or transient reservation failure before
   execution, leaves tasks `Ready`, writes `DeferredUntilUtc` using a capped
   delay, and records a durable future wake. A genuine capacity-release wake
   ignores the future deferral for selection and re-evaluates the entire queue
   immediately.

A high-priority arrival while a lower-priority batch is active does not cancel
the active batch. At its next explicit safe boundary or release, the scheduler
re-reads the whole queue and records the highest lane's admission decision
before starting another lower-priority batch. A retained safe-boundary
reservation can therefore yield a truthful unavailable decision rather than
an unsafe concurrent admission.

## Events, fencing and reconciliation

The executor-facing boundary, completion and release calls carry the batch ID,
capacity-slot ID, owner identity and admission generation. SQL rejects a late,
duplicate or mismatched callback without changing queue state, capacity state
or output. Completion/output handling remains owned by a future approved
executor and existing atomic stage-transition boundary.

Heartbeat and owner age are sanitised diagnostic fields. They can cause a
capacity slot to become `Uncertain`, but never establish that a GPU workload
has stopped. Reliable reconciliation requires evidence such as verified process
termination together with runtime/driver reconciliation showing no remaining
work. It explicitly records the evidence class and has these effects:

- it can move the slot from `Uncertain` to `Available`;
- it never automatically requeues an uncertain mini-task;
- it never replaces a result or completes a parent Job;
- a separate explicit, fenced task-outcome reconciliation is required before
  resubmission or terminal result handling.

## Read-only local projection

`GET /api/gpu-status` and the Overview projection expose only:

- ready, active, deferred and outcome-uncertain mini-task counts;
- counts by priority lane;
- active-batch presence and lane;
- slot availability/uncertainty counts;
- the next durable pre-execution retry time, when one exists; and
- a bounded sanitised diagnostic age/state for uncertain capacity.

They do not expose source paths, text, idempotency keys, task IDs, batch IDs,
owner IDs, model keys or runtime output. Status changes publish through the
existing SignalR presentation feed; reconnects always reload SQL projections.

## Error handling and safety boundaries

- Invalid lane, compatibility key, dimensions/settings fingerprint, memory
  bound, source revision, parent-job lease or idempotency input fails before a
  scheduler mutation.
- Duplicate hand-off returns the already durable mini-task set or fails
  consistently; it never creates a second task or changes an active admission.
- A transient reservation failure before execution retains the parent Job in
  `GpuQueued` and uses a capped durable pre-execution delay only where capacity
  is genuinely uncertain. A SQL integrity, configuration or fencing error
  leaves current durable state unchanged and is operator-visible; it creates no
  automatic deferred retry.
- An explicit executor failure does not infer output or retry policy in this
  slice. It is recorded as a fenced boundary outcome for later executor/stage
  handling.
- No operation deletes mini-tasks, batches, parent Jobs, canonical artefacts,
  vectors or index generations as part of this checkpoint.

## Acceptance matrix

| Invariant | Required evidence |
| --- | --- |
| Atomic hand-off | Real SQL test verifies parent lease/revision, task rows and `GpuQueued` transition commit together; injected failure leaves all unchanged. |
| Duplicate delivery | Parallel same-idempotency hand-off produces one durable task set and no duplicate parent transition. |
| Lane/FIFO/compatibility | Real SQL matrix proves lane order, FIFO head choice, same-key-only batch formation and item/byte bounds. |
| Safe batch boundary | A lower batch remains active while a higher task arrives; no task is interrupted and the next explicit boundary/release admits the highest eligible lane first. |
| Event-driven contention | A busy-but-healthy slot leaves ready tasks without a deferred time; the committed release wake triggers immediate whole-queue reconsideration. |
| Early deferred reconsideration | A true capacity-release wake considers future-deferred work immediately, then records a new bounded deferral only if the pure gate still reports uncertainty. |
| Time is not capacity proof | Advancing every heartbeat, owner-age, lease and fallback clock causes no new admission, requeue, parent-Job transition, batch replacement or result mutation. |
| Uncertain executor | A stale/unresponsive owner marks the slot `Uncertain`; higher-priority work remains pending and no task is admitted to that slot. |
| Trusted reconciliation | Verified release evidence can free a slot but leaves the prior uncertain task/result unresolved until a separate explicit outcome reconciliation. |
| Callback fencing | Late, duplicate and generation-mismatched boundary/completion/release callbacks have no durable side effect. |
| Concurrency | Parallel scheduler calls cannot admit the same task or bypass a waiting higher lane. |
| Status safety | REST, Blazor projection and reconnect tests expose sanitised counts/state only and have no mutation route. |
| Model restriction | No test, setup or hosted default loads, downloads, converts, activates or invokes a model/GPU runtime. |

## Verification and operational boundary

Implementation uses test-first changes: focused domain tests, real SQL Server
integration tests, hosted wake-loop tests and Web contract/component tests,
then the repository's locked restore, warning-as-error Release build and
applicable full suites. A database migration may be created and tested only
against disposable SQL catalogues. It is not applied to the loopback IIS
database and this checkpoint does not deploy, restart IIS, or change production
configuration without a separate current-conversation approval.

Rollback before deployment is a normal branch/worktree discard after preserving
the clean main worktree. After a future approved deployment, rollback must use
the retained native payload and must not infer or alter GPU batch outcomes.
