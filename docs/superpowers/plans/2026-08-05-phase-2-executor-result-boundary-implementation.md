# Phase 2 executor/result boundary implementation plan

> **For implementing agents:** REQUIRED SUB-SKILL: use
> `superpowers:subagent-driven-development` only if the user explicitly chooses
> delegated execution; otherwise use `superpowers:executing-plans` task by task.

**Goal:** Implement the approved Phase 2 executor/result boundary as a durable,
SQL-authoritative adapter contract. A successful scheduler admission must create
one opaque fenced dispatch; deterministic test-only execution can acknowledge
it, submit receipts and issue explicit lifecycle callbacks. The deployed
application must retain its Busy-only admission gate and have no active native
process, model or GPU executor.

**Architecture:** Keep the scheduler as the sole admission and capacity owner.
Add a separate executor-dispatch persistence boundary, a fenced handle and a
private lifecycle sink. `GpuExecutorDispatchRecoveryService` may reread durable
pending dispatches after startup, a local prompt or a bounded missed-prompt
recovery scan, but it may only deliver the original handle to a matching
internally registered adapter. Production registers no adapter. A fake adapter
exists only in integration-test dependency injection and uses the same private
sink as a future real adapter.

**Tech stack:** .NET 10, C#, Entity Framework Core SQL Server migrations, SQL
Server serializable transactions and application locks, ASP.NET Core hosted
services, existing Blazor/minimal-API read projection, xUnit and disposable
native SQL Server catalogues.

## Approval and execution boundary

- Before code changes, create the dedicated worktree
  `E:\LLM KB\.worktrees\phase-2-executor-result-boundary` on
  `codex/phase-2-executor-result-boundary`. Preserve `main` and the protected
  legacy worktrees and branches.
- SQL Server remains authoritative. A channel, hosted-service memory, adapter
  return, exception, cancellation, timeout, heartbeat age, owner age, elapsed
  time and IIS restart are prompts or diagnostic evidence only.
- Preserve exactly the six public Job states. Dispatch, receipt, evidence and
  executor states are private implementation details. A receipt must never
  create an artefact or complete a parent Job.
- No real process creation, process discovery/supervision/termination, driver
  or runtime probing, model/GPU work, model download, filesystem payload,
  network payload, external access, RabbitMQ action, legacy action or public
  mutation route is in scope.
- The migration is additive and may run only in generated disposable test
  catalogues. It must not alter existing task/batch/slot/Job outcomes or infer
  execution from legacy data.
- Do not deploy, restart IIS, apply a local IIS database migration, change
  configuration, merge, purge a worktree or run `scripts/dev/complete-feature.ps1`
  without a new explicit user approval after implementation and review.
- Use test-first changes. Inject a `TimeProvider`, admission gate, signals and
  fake adapter. No test may rely on elapsed wall-clock time to prove capacity
  release, requeueing or result replacement.
- Do not run `dotnet format`. Build with warnings as errors and fix every new
  warning.

## File and responsibility map

| Area | Files | Responsibility |
| --- | --- | --- |
| Executor contracts | `src/FluxKnowledge.Application/Gpu/GpuExecutorContracts.cs`, `IGpuExecutorDispatchStore.cs`, `IGpuExecutorAdapter.cs`, `GpuExecutorLifecycleCoordinator.cs` | Define opaque handles, immutable delivery/receipt/evidence requests, private adapter/sink ports and exact validation. |
| Scheduler integration | `GpuSchedulerContracts.cs`, `IGpuSchedulerStore.cs`, `GpuSchedulerCoordinator.cs` | Require an executor key for an admitted decision, create a post-commit dispatch prompt and make lifecycle callbacks carry the complete handle. |
| SQL durability | `FluxKnowledgeDbContext.cs`, GPU entity files, `CanonicalSchemaConfigurations.cs`, `SqlGpuSchedulerStore.cs`, generated migration and snapshot | Persist dispatches, receipts and evidence; create the dispatch atomically with admission; fence and idempotently process every mutation. |
| Local recovery prompt | `Workers/ChannelGpuExecutorDispatchSignal.cs`, `GpuExecutorDispatchRecoveryService.cs`, `GpuSchedulerServiceCollectionExtensions.cs` | Reread durable pending delivery on start, signal or missed-prompt recovery, without creating a real executor or treating time as release evidence. |
| Tests | Domain, Integration, worker, persistence mapping, composition and existing GPU-status tests | Prove fencing, idempotency, recovery and public-surface safety against real disposable SQL where durable behaviour matters. |
| Durable docs | `docs/architecture.md`, `docs/roadmap.md`, approved design and this plan | Record implementation evidence only after the test milestone; do not increase roadmap progress merely for planning. |

---

### Task 1: Define the fenced executor contracts and private lifecycle sink

**Files:**

- Create: `src/FluxKnowledge.Application/Gpu/GpuExecutorContracts.cs`
- Create: `src/FluxKnowledge.Application/Gpu/IGpuExecutorDispatchStore.cs`
- Create: `src/FluxKnowledge.Application/Gpu/IGpuExecutorAdapter.cs`
- Create: `src/FluxKnowledge.Application/Gpu/IGpuExecutorLifecycleSink.cs`
- Create: `src/FluxKnowledge.Application/Gpu/GpuExecutorLifecycleCoordinator.cs`
- Modify: `src/FluxKnowledge.Application/Gpu/GpuSchedulerContracts.cs`
- Modify: `src/FluxKnowledge.Application/Gpu/GpuSchedulerCoordinator.cs`
- Modify: `tests/FluxKnowledge.Domain.Tests/Gpu/GpuSchedulerContractTests.cs`
- Create: `tests/FluxKnowledge.Domain.Tests/Gpu/GpuExecutorContractTests.cs`

**Contracts and invariants:**

- Add `GpuExecutorBatchHandle(Guid BatchId, string CapacitySlotKey, string
  ExecutorKey, long AdmissionGeneration, Guid DispatchId)`. Its `Validate()`
  method rejects empty GUIDs, non-positive generations, blank keys and keys
  with trailing whitespace. It uses `GpuSchedulerOpaqueKeyValidator` and never
  carries an owner key, source text/path, model/settings key, credential,
  command, runtime output or capacity claim.
- Add private enums with exactly these members:

  - `GpuExecutorDispatchState`: `PendingDelivery`, `Acknowledged`,
    `ReceiptRecorded`, `DeliveryUncertain`, `Terminal`.
  - `GpuExecutorEvidenceClass`: `CapacityReleaseConfirmed`,
    `TaskOutcomeConfirmed`, `TaskOutcomeUncertainConfirmed`.

  Both are internal execution evidence classifications only. No production code
  in this slice produces process, driver or runtime evidence for them.
- Define immutable, validated application records for acknowledgement, delivery
  uncertainty, result receipt and trusted evidence. Every mutation takes an
  externally supplied, non-empty operation ID and the complete handle. A result
  receipt has one mini-task ID, a `GpuMiniTaskBoundaryDisposition`, an optional
  exactly 32-byte opaque digest, a closed evidence class and no raw result
  payload. A trusted-evidence request has a canonical opaque verifier key,
  observation time and one allowed `GpuExecutorEvidenceClass`.
- Change `GpuAdmissionDecision` to include nullable `ExecutorKey`. `Admit`
  requires canonical capacity-slot, owner and executor keys and no retry delay;
  `Busy` and `Defer` require all three keys to be null. This keeps production
  `NoGpuAdmissionGate` safely Busy while allowing the test gate to name the
  opaque executor that owns an admitted dispatch.
- Replace the callback's separate batch/slot/owner/generation fence parameters
  with one `GpuExecutorBatchHandle`. The SQL implementation derives the private
  capacity owner only from the exact dispatch it finds for that handle. Keep
  `GpuBatchCallback` as the lifecycle primitive; do not create a parallel
  executor-only completion path.
- Make capacity and task/result reconciliation requests reference the full
  handle plus a persisted trusted-evidence operation ID. The capacity operation
  may only affect an exact uncertain slot; task/result reconciliation may only
  affect the named task evidence. Neither request may perform the other
  operation implicitly.
- `IGpuExecutorDispatchStore` owns private reads of pending dispatches plus
  idempotent acknowledge, delivery-uncertain, receipt and evidence writes.
  `IGpuExecutorLifecycleSink` composes those calls with
  `GpuSchedulerCoordinator.HandleCallbackAsync`; it never selects work, opens
  capacity, changes a public Job state or directly manipulates SQL entities.
- `IGpuExecutorAdapter` is a process-agnostic boundary containing a canonical
  `ExecutorKey` and `DeliverAsync(GpuExecutorBatchHandle, ...)`. It has no
  process, runtime, network, file or model member. A later approval-gated
  native-process implementation can satisfy this interface without changing the
  SQL fence.

- [ ] Write failing contract tests for every invalid handle, admission decision,
  receipt digest, operation ID and evidence request; prove validation occurs
  before the fake store/sink is called.
- [ ] Write coordinator tests proving a committed admission sends a separate
  executor-dispatch prompt only after status publication/SQL commit ordering;
  an idempotent admission replay and any rejected lifecycle input produce no
  second prompt.
- [ ] Run:

  ```powershell
  dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~GpuSchedulerContractTests|FullyQualifiedName~GpuExecutorContractTests"
  ```

- [ ] Implement the contracts and update all existing private test gates and
  callbacks to supply a test-only `executor-a` key/handle. Do not add a default
  adapter or a real executor implementation in `src`.
- [ ] Re-run the focused tests and the zero-warning Release build for the
  affected projects.
- [ ] Commit the coherent boundary:

  ```powershell
  git add src/FluxKnowledge.Application/Gpu tests/FluxKnowledge.Domain.Tests/Gpu
  git commit -m "feat: define fenced GPU executor contracts"
  ```

### Task 2: Persist dispatch, receipt and evidence records atomically

**Files:**

- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/GpuExecutorDispatchEntity.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/GpuExecutorResultReceiptEntity.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/GpuExecutorEvidenceEntity.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/CanonicalSchemaConfigurations.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlGpuSchedulerStore.cs`
- Generate: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/*_AddGpuExecutorDispatchAndReceipts.cs` and designer
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/FluxKnowledgeDbContextModelSnapshot.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Gpu/SqlGpuAdmissionTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Gpu/SqlGpuAdmissionConcurrencyTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Gpu/SqlGpuBatchLifecycleTests.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Gpu/SqlGpuExecutorDispatchTests.cs`

**Durable schema:**

- `GpuExecutorDispatches` has a GUID `DispatchId` primary key, a unique
  `BatchId`, the immutable handle fence, the internally retained owner key,
  state, acknowledgement/updated timestamps and a rowversion. Its capacity,
  owner and executor keys use the existing binary collation and no-trailing-
  whitespace SQL constraints. It has restrictive foreign keys to the batch and
  slot; one batch therefore owns one immutable dispatch.
- `GpuExecutorResultReceipts` is append-only. Its operation ID is the primary
  idempotency key; it retains dispatch/batch/mini-task/executor/generation
  fences, the closed outcome/evidence classifications, nullable `varbinary(32)`
  digest, canonical request fingerprint and creation time. A unique fence on
  `(DispatchId, MiniTaskId)` prevents a second operation from replacing an
  accepted receipt for the same task.
- `GpuExecutorEvidence` is append-only. Its operation ID is the primary key and
  it retains only dispatch/handle fence, closed evidence class, opaque verifier
  key, observed-at time, canonical fingerprint and creation time. It contains
  no raw evidence payload. All foreign keys use restrictive deletion.
- Add only the indexes needed for one-batch dispatch lookup, pending-delivery
  rereads, receipt membership/completion checks and exact evidence lookup. Keep
  identifiers and digests out of all existing Web projections.

**Transactional behaviour:**

- In `CommitAdmissionAsync`, allocate a deterministic retry-safe dispatch ID
  before the execution strategy retries. In the same serializable transaction
  that inserts the batch, reserves the slot, activates selected mini-tasks and
  changes parent Jobs to `GpuProcessing`, insert one `PendingDelivery` dispatch
  using the admitted executor key. Any failure rolls back all five changes.
- Existing admission-operation replay returns the original durable admission;
  it cannot create a second batch, reservation, dispatch or executor identity.
- Acknowledge only an exact `PendingDelivery` handle. A matching same-operation
  replay returns the original result; a divergent operation fingerprint throws
  with no mutation. Acknowledgement does not release capacity or change a task.
- A receipt only succeeds for an acknowledged exact dispatch, active exact
  mini-task and matching generation. It records immutable evidence and advances
  the dispatch to `ReceiptRecorded`, but does not itself free a slot, create an
  artefact or complete a parent Job.
- `ApplyBatchCallbackAsync` must resolve the exact handle/dispatch before
  reading the private owner key. A `Completed` callback succeeds only when one
  accepted completed receipt exists for every active mini-task in the batch. It
  then uses the existing one-transaction callback to mark those scheduler tasks
  completed, release the matching slot, record wake evidence and mark the
  dispatch `Terminal`.
- A safe boundary with retained capacity changes only scheduler liveness/wake
  state; it does not change the dispatch fence or admit a second batch. An
  explicit capacity release with unresolved outcomes records those outcomes,
  changes the dispatch to `DeliveryUncertain` and releases capacity only through
  the existing explicit callback rules.
- The existing diagnostic uncertainty transition may mark the matching
  non-terminal dispatch `DeliveryUncertain`, but it never releases capacity,
  changes a task result or requeues work. Trusted capacity reconciliation must
  validate a persisted `CapacityReleaseConfirmed` evidence row and may change
  only the exact uncertain capacity state; task/result reconciliation must
  validate its separate evidence row and may not free capacity.

- [ ] Add failing mapping/migration tests for table shape, exact key collation,
  no-trailing-whitespace checks, restrictive foreign keys, one-dispatch-per-
  batch, one-receipt-per-dispatch/task and `varbinary(32)` digest mapping.
- [ ] Add real disposable-SQL tests proving atomic admission dispatch creation
  and injected rollback; duplicate admission; acknowledgement replay/divergence;
  invalid/late/mismatched/generation-stale receipts; duplicate receipt
  prevention; completed callback without all receipts; completion after every
  receipt; and explicit release/result-reconciliation separation.
- [ ] Add parallel SQL tests for two delivery/receipt calls racing the same
  dispatch. Assert one durable receipt/effective state transition, no duplicate
  slot reservation and no replacement of an earlier accepted receipt.
- [ ] Run:

  ```powershell
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SchemaMappingTests|FullyQualifiedName~SqlGpuAdmissionTests|FullyQualifiedName~SqlGpuAdmissionConcurrencyTests|FullyQualifiedName~SqlGpuBatchLifecycleTests|FullyQualifiedName~SqlGpuExecutorDispatchTests"
  ```

- [ ] Generate the additive migration only after the model compiles:

  ```powershell
  dotnet ef migrations add AddGpuExecutorDispatchAndReceipts --project src/FluxKnowledge.Infrastructure.SqlServer/FluxKnowledge.Infrastructure.SqlServer.csproj --startup-project src/FluxKnowledge.Web/FluxKnowledge.Web.csproj
  ```

  Inspect it before retaining it. It must add only the three new tables,
  indexes, foreign keys and constraints; it must not rebuild or alter existing
  scheduler tables, delete data or run outside disposable catalogues.
- [ ] Implement the SQL-store methods with serializable transactions,
  canonical request fingerprints and existing operation-receipt conventions.
  Do not translate SQL integrity, configuration or permissions failures into a
  retry, release, requeue or replacement.
- [ ] Re-run the focused SQL suite and:

  ```powershell
  dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
  ```

- [ ] Commit the durable vertical slice:

  ```powershell
  git add src/FluxKnowledge.Infrastructure.SqlServer/Persistence tests/FluxKnowledge.Integration.Tests/Persistence tests/FluxKnowledge.Integration.Tests/Gpu
  git commit -m "feat: persist GPU executor dispatch evidence"
  ```

### Task 3: Add test-only delivery and durable recovery prompts

**Files:**

- Create: `src/FluxKnowledge.Application/Gpu/IGpuExecutorDispatchSignal.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/ChannelGpuExecutorDispatchSignal.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/GpuExecutorDispatchRecoveryService.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/GpuSchedulerServiceCollectionExtensions.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Workers/GpuSchedulerServiceTests.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Workers/GpuExecutorDispatchRecoveryServiceTests.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Gpu/DeterministicFakeGpuExecutor.cs`
- Modify: `tests/FluxKnowledge.Domain.Tests/Pipeline/OutboxWorkerRegistrationTests.cs`
- Modify: `tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs`

**Recovery behaviour:**

- `ChannelGpuExecutorDispatchSignal` is a bounded coalescing prompt without
  handles or payloads. It is not a queue. After an admission commit, the
  scheduler coordinator notifies it in a `finally` block after publishing the
  normal committed status notification, just as scheduler wakes are notified
  after commit.
- `GpuExecutorDispatchRecoveryService` scans durable `PendingDelivery` rows on
  startup, on a local dispatch prompt and at the existing bounded fallback
  interval solely as missed-prompt/restart recovery. It never derives an
  individual due time, opens capacity, changes scheduler wake state, starts
  another batch or converts uncertainty into retry.
- The service delivers only the original persisted handle to an exact ordinal
  matching `IGpuExecutorAdapter.ExecutorKey`. An absent adapter leaves the row
  pending. An adapter exception, cancellation, timeout or lost local prompt
  leaves capacity/task state untouched and cannot cause a second admission or a
  new handle. Only an explicit matching lifecycle request can acknowledge,
  classify delivery uncertainty, record a receipt or release capacity.
- Register the signal, lifecycle coordinator and recovery hosted service in the
  normal scheduler composition. Register **no** `IGpuExecutorAdapter` in
  production. `NoGpuAdmissionGate` remains the sole default admission gate, so
  deployed services create no dispatch in normal operation and start no process.
- `DeterministicFakeGpuExecutor` lives only under the integration-test project.
  It accepts an issued handle, records scripted acknowledgement/receipt/boundary
  operations through `IGpuExecutorLifecycleSink`, and can deliberately drop a
  prompt or remain unresponsive. It contains no process start, runtime/GPU API,
  model, file, network or direct database access.

- [ ] Write failing hosted-service tests that prove a fake receives an original
  handle exactly once or as an idempotent replay after a dropped local prompt;
  a new service instance rereads pending dispatches after an IIS-style restart;
  an unmatched adapter never receives a handle; and production composition has
  zero registered adapters.
- [ ] Add integration tests that script late, duplicate and mismatched
  acknowledgement/receipt/callback operations; a lost prompt; fake
  unresponsiveness; and an IIS restart. In each case assert the original
  batch/slot/task/receipt snapshots remain unchanged except for the explicitly
  requested diagnostic uncertainty transition.
- [ ] Add a deterministic clock test advancing fallback, diagnostic and
  heartbeat thresholds. It must prove elapsed time alone never admits concurrent
  work, releases capacity, requeues active/uncertain work or replaces a result.
- [ ] Run:

  ```powershell
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~GpuExecutorDispatchRecoveryServiceTests|FullyQualifiedName~GpuSchedulerServiceTests|FullyQualifiedName~SqlGpuExecutorDispatchTests"
  ```

- [ ] Implement the signal and service using cancellation-safe waits and
  injected `TimeProvider`. Keep all delivery recovery behaviour read/retry of
  the same pending dispatch only. Do not add a process manager, socket, IPC
  transport or model invocation.
- [ ] Re-run focused worker and SQL tests, then the appropriate composition
  tests. Confirm the production service provider resolves zero adapters.
- [ ] Commit the recovery boundary:

  ```powershell
  git add src/FluxKnowledge.Application/Gpu src/FluxKnowledge.Infrastructure.SqlServer/Workers tests/FluxKnowledge.Integration.Tests/Workers tests/FluxKnowledge.Integration.Tests/Gpu tests/FluxKnowledge.Domain.Tests/Pipeline tests/FluxKnowledge.Web.Tests/Composition
  git commit -m "feat: recover pending GPU executor dispatches"
  ```

### Task 4: Close the public-surface safety proof and milestone evidence

**Files:**

- Modify: `tests/FluxKnowledge.Web.Tests/Endpoints/GpuStatusEndpointTests.cs`
- Modify only if required by compiled contract changes:
  `tests/FluxKnowledge.Web.Tests/Components/OverviewProjectionTests.cs` and
  `tests/FluxKnowledge.Web.Tests/Components/SqlProjectionReaderIntegrationTests.cs`
- Modify after passing implementation evidence:
  `docs/architecture.md` and `docs/roadmap.md`
- Modify: `docs/superpowers/specs/2026-08-03-phase-2-executor-result-boundary-design.md`

**Public and operational proof:**

- Keep `GET /api/gpu-status` read-only and its existing sanitised contract in
  this slice. Do not expose dispatch state, executor/slot/batch/task IDs,
  receipt digest, verifier key, model/settings data, process evidence or an
  operation handle. The approved design permits future aggregate dispatch
  counts, but this implementation does not need a public contract expansion.
- Add endpoint tests seeded with distinct private executor/dispatch/receipt
  values and assert their absence from the response. Retain the mutation-verb
  `405` tests and expected-projection-failure bodyless `503` test.
- Add composition tests asserting one recovery hosted service can be registered
  idempotently, the normal provider uses `NoGpuAdmissionGate`, and no
  `IGpuExecutorAdapter` is registered. Do not test a deployed process because
  one must not exist in this slice.
- Update architecture/roadmap only with verified implementation and disposable
  test evidence. Keep the pipeline item at its current conservative percentage
  unless the verified vertical slice genuinely changes the agreed progress
  estimate. State explicitly that real process management, termination evidence,
  runtime/driver reconciliation, model/GPU activation, external access and
  legacy work remain approval-gated.

- [ ] Run the focused Web/composition tests:

  ```powershell
  dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~GpuStatusEndpointTests|FullyQualifiedName~WebHostCompositionTests"
  ```

- [ ] Run the complete native-only verification matrix, stopping to diagnose any
  failure rather than weakening an assertion:

  ```powershell
  dotnet restore FluxKnowledge.slnx --locked-mode
  dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
  dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-restore
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-restore
  dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --no-restore
  git diff --check
  rg -n "Process(Start|Info)?|System\.Diagnostics\.Process|HttpClient|RabbitMQ|Vespa|Docker" src/FluxKnowledge.Application/Gpu src/FluxKnowledge.Infrastructure.SqlServer/Workers
  if ($LASTEXITCODE -eq 1) { Write-Output "No forbidden executor implementation references found." } elseif ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  ```

  The final search must show no newly introduced executor process/model/network
  path; inspect any existing unrelated match rather than suppressing it.
- [ ] Perform one whole-branch review against the approved design and acceptance
  matrix: atomic dispatch, opaque fencing, receipt idempotency, complete receipt
  proof, retained-boundary safety, explicit-release-only, reconciliation split,
  lost prompt, unresponsive adapter, restart recovery, elapsed-time safety,
  fake-only isolation and no public mutation route.
- [ ] Commit documentation and final tests only after that review:

  ```powershell
  git add docs tests/FluxKnowledge.Web.Tests
  git commit -m "docs: record GPU executor boundary verification"
  ```

## Acceptance checklist

- [ ] SQL admission atomically commits exactly one dispatch with the batch,
  reservation, active mini-tasks and `GpuProcessing` parent transition; an
  injected failure commits none.
- [ ] Blank, padded, stale, mismatched or divergent handle/operation/receipt
  input has no durable side effect.
- [ ] A delivery replay returns the original handle/acknowledgement and cannot
  create a second batch, reservation, dispatch, receipt or executor identity.
- [ ] Completion needs one accepted matching completed receipt per active task;
  it releases only the exact slot and does not create an artefact or complete a
  parent Job.
- [ ] Retained safe boundaries re-evaluate the whole durable ready queue but
  retain the slot. Only exact completion/release or independently verified
  capacity reconciliation can make it available.
- [ ] Capacity reconciliation and task/result reconciliation remain separately
  fenced. One cannot produce the other result.
- [ ] Lost local prompts, stale heartbeats, owner age, elapsed time, adapter
  failure and IIS restart never release capacity, requeue uncertain work or
  replace a result.
- [ ] The deterministic fake is confined to test assemblies and production
  composition contains no executor adapter, process or model/GPU path.
- [ ] `GET /api/gpu-status` remains sanitised/read-only; no public callback or
  mutation route exists.
- [ ] Only disposable SQL catalogues receive the additive migration. No IIS,
  deployment, external-access or legacy change is made.

## Rollback and next approval gate

Before deployment, the safe rollback is source-only: retain the dedicated
worktree and branch, revert the coherent commits if necessary, and discard only
generated disposable test catalogues. Do not use the migration `Down` method or
delete durable records against the local IIS database.

After implementation, tests and whole-branch review, report the evidence and
request a fresh decision for merge/closeout, IIS deployment, production
migration, live validation or a real native process-management slice. Those
operational steps are intentionally outside this approved plan.
