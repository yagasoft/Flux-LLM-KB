# Phase 2 executor/result boundary design

Status: approved design. An implementation plan is being prepared; this approval
does not authorise implementation, a migration, process launch, runtime
activation, deployment or configuration change.

## Decision

Extend the delivered Phase 2 scheduler foundation with a durable native
executor-adapter boundary and result-receipt contract. The contract is designed
now, but its first implementation slice may use only a deterministic in-process
fake executor in tests. It must not spawn, supervise or activate a real executor
process.

This is the middle-ground option between a test-only callback fixture with no
durable adapter contract and a real native process manager. It gives the
scheduler a precise, testable hand-off and recovery boundary without treating
elapsed time, an IIS restart or an absent notification as physical execution
evidence.

## Goal

Define how an admitted batch is durably offered to one opaque executor identity,
how that executor reports fenced lifecycle and result evidence, and how the
system recovers delivery visibility without changing an active or uncertain
outcome implicitly.

## Scope and non-goals

The future implementation checkpoint described here includes only:

- a durable executor-dispatch record created with an admitted batch;
- an opaque executor identity and a fenced batch handle;
- internal contracts for safe boundary, result receipt, completion, capacity
  release and trusted evidence;
- separate capacity and task/result reconciliation paths;
- deterministic fake-executor support in test-only dependency injection; and
- focused domain, SQL integration, hosted-recovery and read-only Web evidence.

It does not include real native process creation, discovery, supervision,
termination or restart; process-termination evidence; runtime/driver
interrogation; model/GPU execution, downloads, conversion or cache use;
external access; RabbitMQ or legacy action; public callback/mutation routes;
deployment; IIS configuration; or SQL migration application outside disposable
catalogues.

Final pipeline artefact materialisation and parent-Job completion also remain a
separate, model-specific approval gate. A result receipt is evidence, not a
model output.

## Existing invariants retained

- SQL Server is authoritative for admission, dispatch, lifecycle, receipt,
  reconciliation and status state. In-memory notifications are prompts only.
- The six public Job states remain unchanged. Scheduler, dispatch and receipt
  states remain private implementation detail.
- Only the existing serializable, fenced scheduler transaction may admit a
  batch. The adapter never selects, reprioritises or admits work.
- Heartbeat and owner age remain diagnostic evidence only. They can mark a slot
  Uncertain but never make it Available.
- Only an explicit matching completion/capacity-release event or trusted
  capacity reconciliation can make a slot available.
- An uncertain mini-task is never automatically requeued, duplicated,
  completed or replaced. A receipt never completes a parent Job by itself.
- Executor-facing mutations are internal application contracts. No REST,
  Blazor, SignalR, CLI or MCP mutation route is introduced.

## Options considered

| Option | Benefit | Limitation or risk |
| --- | --- | --- |
| Test-only fake callbacks | Minimal code. | No durable adapter, dispatch replay or receipt contract. |
| Durable adapter plus test-only fake executor | Stable, fenced hand-off and recovery semantics with no physical execution. | Chosen. It adds a carefully bounded persistence contract. |
| Real native process manager | Could prove process lifecycle and runtime evidence. | Deferred: requires separate approval for supervision, termination and driver/runtime reconciliation. |

## Opaque identity and fenced handle

ExecutorKey is an opaque canonical identity for the executor that owns an
admitted batch. It is not a display name, machine name, user name, model name,
process identifier or public value. It uses the scheduler's existing exact
opaque-key validation and SQL binary identity rules; blank, padded or
collation-ambiguous values are rejected.

GpuExecutorBatchHandle is the executor's sole authority token. It contains only:

| Field | Purpose |
| --- | --- |
| BatchId | Identifies the one admitted batch. |
| CapacitySlotKey | Fences the capacity reservation. |
| ExecutorKey | Must equal the batch's stored opaque owner identity. |
| AdmissionGeneration | Fences late delivery and callback attempts. |
| DispatchId | Identifies the durable one-batch offer and its idempotent delivery record. |

The handle carries no source text, path, model key, settings, credentials,
runtime output, process command or mutable capacity claim. The adapter cannot
forge a later generation by resubmitting an older handle.

## Durable contract

### Executor dispatch

The future EF migration creates one internal GpuExecutorDispatches row in the
same transaction as successful batch admission. It stores the full immutable
handle fence, an idempotency key and one private delivery state:

| State | Meaning | Permitted next state |
| --- | --- | --- |
| PendingDelivery | Durable offer exists but no matching acknowledgement is recorded. | Acknowledged or DeliveryUncertain |
| Acknowledged | Matching adapter accepted the handle for attempted execution. | ReceiptRecorded or DeliveryUncertain |
| ReceiptRecorded | At least one matching result receipt is linked to the batch. | Terminal or DeliveryUncertain |
| DeliveryUncertain | Delivery/execution cannot be proven. The batch/slot remains governed by existing uncertainty rules. | Trusted reconciliation only |
| Terminal | All adapter lifecycle obligations are durably resolved. | None |

Dispatch state never proves capacity is free, execution finished or a parent Job
is complete. Re-delivery of the same DispatchId is idempotent: it returns the
original handle and cannot create another batch, task, reservation or executor
identity.

### Result receipts

GpuExecutorResultReceipts are keyed by mini-task, batch fence and executor
operation ID. A receipt contains a validated outcome category, an optional
fixed-size opaque result digest and a sanitised evidence classification. It
contains no raw payload, source content or model/runtime output.

An accepted receipt is immutable. A same-operation replay with the same
canonical request fingerprint returns the original receipt; a divergent replay
is rejected without mutation. A late, mismatched or generation-stale receipt
cannot change mini-task, batch, slot or parent-Job state.

Completed receipt evidence may mark the scheduler mini-task complete and take
part in a matching batch completion/release. It cannot synthesize a final
artefact or complete the parent Job. A failed or unknown receipt does not imply
retry; it remains durable evidence for later explicit outcome handling.

### Trusted evidence

GpuExecutorEvidence is separate from result receipts. It records only an
allowed closed evidence class, verifier identity, observation time and the
fenced handle. Raw process/runtime/driver data remains outside this record and
outside public projections. A future real process-management slice, not this
one, defines how evidence is produced or verified.

## Internal adapter and lifecycle contracts

Production has no active executor adapter by default. The existing Busy-only
admission gate remains the safe default until a separately approved capacity
provider and executor implementation exist.

The future internal adapter receives a GpuExecutorBatchHandle and can only:

1. acknowledge the exact durable dispatch;
2. record an explicit safe boundary, declaring whether capacity is retained or
   released;
3. record a fenced result receipt for an exact mini-task;
4. complete a batch only when it references an accepted receipt for every
   mini-task; and
5. release capacity only with the exact handle and an explicit disposition for
   every unresolved mini-task.

Each request carries a non-empty operation ID and the complete handle fence.
The existing GpuBatchCallback remains the scheduler lifecycle primitive; the
adapter contracts compose it rather than bypassing it.

An adapter return, exception, timeout or cancellation is not proof of process
exit, capacity release or task outcome. It only contributes explicit durable
delivery evidence. No retry loop may start another executor, reclaim an active
batch or replace a result.

## Lifecycle flow

1. The existing scheduler admits a batch. The same SQL transaction reserves the
   slot, activates selected mini-tasks, changes parent Jobs to GpuProcessing and
   inserts one PendingDelivery dispatch.
2. After commit, an internal notifier may prompt a dispatcher. A lost notifier
   is harmless because recovery rereads durable PendingDelivery rows; no
   in-memory queue is authoritative.
3. The adapter receives the original handle and records acknowledgement.
   Repeated delivery or acknowledgement is an idempotent replay.
4. At an explicit safe boundary, the adapter declares retained or released
   capacity. A retained boundary wakes whole-queue priority reconsideration but
   cannot admit another batch to that slot.
5. Before successful completion, the adapter records one fenced receipt for
   every completed mini-task. Matching completion links those receipts, marks
   the batch/tasks complete and releases the matching slot.
6. An explicit capacity release without verified results records all affected
   mini-tasks OutcomeUncertain, releases the reservation and wakes the
   scheduler. It never fabricates a result or requeues work.
7. A future model-specific materialiser may validate an immutable output and
   perform the existing parent-Job stage transition. This design adds neither
   that materialiser nor a model output.

## Capacity and task/result reconciliation

Capacity reconciliation and task/result reconciliation are independently fenced
operations with separate evidence records.

| Operation | Required trusted evidence | May change | Must not change |
| --- | --- | --- | --- |
| Capacity reconciliation | Verified evidence that the exact reservation no longer consumes capacity. | Matching Uncertain slot may become Available; scheduler wake may advance. | Mini-task outcome, result receipt, parent Job, retry state or result replacement. |
| Task/result reconciliation | Verified evidence that establishes an exact task outcome or absence of execution. | Named receipt/outcome record according to its explicit contract. | Capacity availability without separate capacity evidence; unrelated work; automatic retry. |

Heartbeat age, owner age, elapsed time, adapter timeout, missed wake and IIS
restart are diagnostic or recovery triggers only. None is trusted evidence.

## Test-only fake executor

DeterministicFakeGpuExecutor exists only in test projects and test dependency
injection. It accepts an already-issued handle, records a deterministic
acknowledgement and emits explicitly scripted lifecycle/receipt calls. It has
no model dependency, GPU API, process creation, network/file payload or
registration in deployed application services.

The fake never selects work or changes scheduler state directly. It uses the
same internal lifecycle sink as a future adapter so integration tests prove SQL
fences, idempotency records and recovery rules rather than a test shortcut.

## Failure and recovery behaviour

- A missing local delivery/wake prompt causes durable dispatch/wake reread on the
  next allowed recovery pass. It does not alter an active batch, slot or receipt.
- An unresponsive executor can lead only to diagnostic CapacityUncertain state.
  New work is blocked from that slot pending trusted capacity reconciliation.
- An IIS restart recreates local signals and rereads durable state. It does not
  spawn a process, infer completion, reclaim capacity or requeue uncertain work.
- Invalid receipt, callback or evidence input leaves current durable state
  unchanged and creates no automatic retry.
- Duplicate, late or mismatched delivery, acknowledgement, boundary, receipt,
  completion, release and reconciliation calls are idempotent replays or
  deterministic rejections with no state replacement.

## Read-only visibility

The existing GPU status projection may add sanitised aggregate counts for
pending/uncertain dispatches and accepted/unresolved receipts. It must not
expose executor identities, batch/task IDs, receipt digests, output references,
model keys, process evidence or mutation controls.

## Acceptance matrix

| Invariant | Required evidence |
| --- | --- |
| Atomic dispatch creation | Real SQL test proves one dispatch is inserted atomically with batch admission, slot reservation, mini-task activation and parent GpuProcessing transition; injected failure leaves all unchanged. |
| Opaque fenced handle | Domain and SQL tests reject blank, padded, stale or mismatched executor/slot/batch/generation fields without mutation. |
| Idempotent delivery | Same dispatch/operation returns the original durable handle or acknowledgement; divergent replay cannot create a second batch or reservation. |
| Result receipt fencing | Only exact active batch membership/generation accepts a receipt; late, duplicate and mismatched receipts cannot change tasks, capacity, parent Jobs or accepted receipts. |
| Completion proof | Completion requires an accepted receipt for every mini-task and explicit matching release; it cannot synthesize a final artefact or parent-Job completion. |
| Safe-boundary priority | Retained safe boundary advances a durable wake and whole-queue priority reconsideration, but no batch enters the retained slot. |
| Explicit release only | Only matching completion/release or trusted capacity reconciliation frees capacity. Advancing clocks, heartbeats, owner age, adapter timeout, notification loss or IIS restart cannot. |
| Capacity/result split | Trusted release makes capacity available while uncertain tasks/results stay unchanged. Result reconciliation cannot free capacity without separate evidence. |
| Lost notification | Dropping a local prompt then running durable recovery reaches the original pending dispatch exactly once or idempotently, without re-admission. |
| Executor unresponsiveness | Diagnostic stale evidence marks the slot Uncertain; no ready task is admitted there and no active/uncertain task is requeued or replaced. |
| IIS restart/recovery | Recreating hosted services and fake adapter from persisted SQL preserves active/uncertain batches and receipts; only durable pending dispatch/wake evidence is consumed. |
| Callback closure | Late, duplicate and generation-mismatched safe-boundary, completion, capacity-release and trusted-evidence calls have no durable side effect. |
| Fake-executor boundary | Fake is test-only; production DI retains no active executor/process adapter and contains no process, model, GPU, network or file execution path. |
| Public-surface safety | Status stays sanitised and read-only; no public mutation route exists. |
| Deployment safety | Only disposable-SQL verification is in scope. IIS, deployment configuration and external exposure remain unchanged. |

## Future verification sequence

1. Add failing domain tests for handle validation, receipt idempotency and
   capacity/result separation.
2. Add the EF migration and SQL-store tests against generated disposable
   catalogues, including injected transaction failures and parallel callbacks.
3. Add the test-only fake executor and hosted restart/lost-notification tests.
4. Add sanitised read-only projection tests with no mutation route.
5. Run locked restore, a zero-warning Release build and the full native test
   suite. Do not run process, model/GPU, external, legacy or deployment work.
6. Seek separate approval before a real process manager, driver/runtime
   reconciliation, model/GPU activation, deployment or production migration.

## Roadmap wording updated on approval

The following wording in docs/roadmap.md now records the approved design without
claiming implementation progress:

- The P0 Pipeline durability, scheduler and rebuild invariants row should
  distinguish this approved durable adapter/receipt design from its future
  implementation slice, then separately approval-gated real process management,
  termination evidence, runtime/driver reconciliation and model/GPU activation.
- The Phase 2 scheduler foundation section should link this design and say that
  the delivered scheduler still has no active executor; its remaining-work text
  must retain separate external-access and legacy gates.
- No roadmap progress percentage has increased. Progress changes only after
  implementation and disposable-SQL acceptance evidence.

## Approval boundary

Approval of this design authorises only an implementation plan for the durable
adapter/receipt boundary and deterministic test fake. It does not authorise
real process management, process termination evidence, runtime/driver
reconciliation, model/GPU activation, external access, legacy actions,
deployment or applying a migration to the local IIS database.
