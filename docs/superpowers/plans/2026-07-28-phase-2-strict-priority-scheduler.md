# Phase 2 strict-priority scheduler implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Deliver the approved Phase 2 durable, in-process strict-priority GPU mini-task scheduler foundation: a SQL-authoritative mini-task hand-off, bounded compatible batch admission, explicit capacity/boundary lifecycle, event-driven wake-up, and a sanitised local read-only status projection.

**Architecture:** Add a small domain and application scheduling boundary; persist mini-task execution, batch, capacity-slot and wake state in SQL Server; use a transaction-scoped SQL application lock for one pure admission decision at a time; and host a local wake loop whose signal is only a prompt to reread durable state. Explicit executor callbacks and trusted reconciliation are fenced by batch/slot/owner/admission generation. The default admission gate deliberately does not invoke a model, GPU runtime, cache or external service.

**Tech stack:** .NET 10, C#, Entity Framework Core SQL Server migrations, SQL Server application locks, ASP.NET Core hosted services and minimal endpoints, Blazor/SignalR status projection, xUnit and disposable native SQL Server catalogues.

## Global constraints

- Work only in E:\LLM KB\.worktrees\phase-2-strict-priority-scheduler on codex/phase-2-strict-priority-scheduler. Preserve main and the two protected legacy worktrees and branches.
- Do not use Flux, its plugin, its skills, its MCP tools, or its memory APIs in this work.
- SQL Server is the sole scheduler authority. A channel signal, timer and hosted-service memory are prompts only and may never be treated as queue or capacity truth.
- Preserve exactly the existing public Job states: WorkerQueued, WorkerProcessing, GpuQueued, GpuProcessing, Completed and Failed. The new mini-task, batch and capacity states are private implementation detail.
- Do not add model execution, a model/runtime/cache/download, GPU invocation, a real device probe, an external endpoint, RabbitMQ or other legacy action, or a mutation HTTP endpoint.
- Do not deploy, restart IIS, apply a production migration, alter production configuration, or run scripts/dev/complete-feature.ps1 without a fresh, explicit approval after implementation and review.
- Make the migration additive. It may run only in the disposable native SQL Server test catalogues. It must not delete, rewrite or infer the outcome of existing jobs, tasks, batches, artefacts, vectors or index generations.
- Use test-first changes. Keep every focused test deterministic with an injected TimeProvider, admission gate, signal or callback; no test may depend on elapsed wall-clock time to prove release or requeue behaviour.
- Never use READPAST to bypass a waiting higher-priority ready task. Admission is serialised by a short transaction-scoped application lock, not by a long-running process lock.
- Keep the existing State database column on GpuMiniTasks, but map it to the new private execution-state concept so no compatibility-breaking column rename is required.
- Native SQL tests use only the disposable catalogue convention already in NativeSqlServerFixture:

    $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'

  They must clean up only their generated FluxKnowledge_Phase1Tests_<guid> catalogues.
- Do not run dotnet format. Build with warnings as errors and fix every warning in changed code.
- Commit each coherent milestone. Do not merge, purge a worktree, deploy or run live validation as part of this plan without a new user decision.

## File and responsibility map

| Area | Files | Responsibility |
| --- | --- | --- |
| Domain | src/FluxKnowledge.Domain/Gpu/GpuMiniTask.cs and new neighbouring enum/value files | Validate immutable task input and define private execution, batch, capacity, wake and callback state. |
| Application | new src/FluxKnowledge.Application/Gpu/* and Contracts/StatusContracts.cs | Define durable-store ports, pure admission gate, scheduler coordinator, hand-off/boundary/reconciliation requests and sanitised status contracts. |
| Persistence | FluxKnowledgeDbContext.cs, new entities/configuration, SqlGpuSchedulerStore.cs, migration and snapshot | Persist all state and execute fenced, atomic SQL transitions. |
| Wake loop | new Infrastructure.SqlServer/Workers/GpuScheduler*.cs | Coalesce durable reasons, run one admission round per wake, schedule only allowed durable pre-execution retries and never execute GPU work. |
| Web | WebHostComposition.cs, Program.cs, GpuStatusEndpoints.cs, status components | Register the scheduler and show read-only, content-free local state. |
| Tests | Domain, Integration and Web test projects | Prove every approved invariant against real SQL where durable/concurrency behaviour matters. |
| Durable docs | docs/architecture.md and docs/roadmap.md | Record the implemented local-only boundary and leave deployment/execution as remaining work. |

---

### Task 1: Define the private scheduler state and application boundary

**Files:**

- Modify: src/FluxKnowledge.Domain/Gpu/GpuMiniTask.cs
- Create: src/FluxKnowledge.Domain/Gpu/GpuMiniTaskExecutionState.cs
- Create: src/FluxKnowledge.Domain/Gpu/GpuBatchState.cs
- Create: src/FluxKnowledge.Domain/Gpu/GpuCapacitySlotState.cs
- Create: src/FluxKnowledge.Domain/Gpu/GpuSchedulerWakeReason.cs
- Create: src/FluxKnowledge.Domain/Gpu/GpuMiniTaskBoundaryDisposition.cs
- Create: src/FluxKnowledge.Application/Gpu/GpuSchedulerContracts.cs
- Create: src/FluxKnowledge.Application/Gpu/GpuSchedulerOptions.cs
- Create: src/FluxKnowledge.Application/Gpu/IGpuSchedulerStore.cs
- Create: src/FluxKnowledge.Application/Gpu/IGpuSchedulerWakeSignal.cs
- Create: src/FluxKnowledge.Application/Gpu/IGpuAdmissionGate.cs
- Create: src/FluxKnowledge.Application/Gpu/GpuSchedulerCoordinator.cs
- Modify: tests/FluxKnowledge.Domain.Tests/Gpu/GpuMiniTaskTests.cs
- Create: tests/FluxKnowledge.Domain.Tests/Gpu/GpuSchedulerContractTests.cs

**Interfaces:**

- GpuMiniTaskExecutionState has exactly Ready, Active, Completed and OutcomeUncertain.
- GpuBatchState has exactly Active, AtSafeBoundary, Completed, Released and CapacityUncertain.
- GpuCapacitySlotState has exactly Available, Reserved and Uncertain.
- GpuSchedulerWakeReason is a flags enum with WorkReady, SafeBoundary, CapacityReleased, DeferredRetry, StartupRecovery and Reconciliation. It contains no task or capacity identifier.
- GpuMiniTask exposes immutable validated input and starts in Ready with a null deferral, null batch and admission generation zero. It remains a domain description, not a model executor.
- The application layer owns the following public records and ports:

    public sealed record GpuMiniTaskHandoffRequest(
        ClaimedJob ParentJob,
        GpuPriorityLane PriorityLane,
        string ModelRuntimeKey,
        string SettingsFingerprint,
        long EstimatedBytes,
        string IdempotencyKey);

    public sealed record GpuBatchCandidate(
        GpuPriorityLane PriorityLane,
        string ModelRuntimeKey,
        string SettingsFingerprint,
        int ItemCount,
        long EstimatedBytes);

    public sealed record GpuAdmissionDecision(
        GpuAdmissionDisposition Disposition,
        string? CapacitySlotKey,
        string? OwnerKey,
        TimeSpan? RetryAfter);

    public sealed record GpuBatchCallback(
        Guid BatchId,
        string CapacitySlotKey,
        string OwnerKey,
        long AdmissionGeneration,
        GpuBatchCallbackKind Kind,
        IReadOnlyList<GpuMiniTaskBoundaryOutcome> Outcomes,
        bool CapacityReleased);

    public sealed record GpuMiniTaskBoundaryOutcome(
        Guid MiniTaskId,
        GpuMiniTaskBoundaryDisposition Disposition);

    public interface IGpuAdmissionGate
    {
        ValueTask<GpuAdmissionDecision> DecideAsync(
            GpuBatchCandidate candidate,
            CancellationToken cancellationToken);
    }

- The store port separates hand-off, one admission round, a fenced callback, diagnostic uncertainty marking, trusted reconciliation, wake inspection and sanitised status reads. It returns structured outcomes rather than exceptions for a duplicate idempotency request or a rejected late callback.
- GpuSchedulerOptions supplies positive MaxBatchItems, MaxBatchEstimatedBytes, CapacityDeferralCap, FallbackInterval and UnresponsiveDiagnosticAge. It is injected from a code default and is overrideable in tests; this checkpoint does not change deployment configuration files.
- GpuSchedulerCoordinator publishes a status event and signals only after the relevant store transaction has committed. Its admission method receives a durable wake reason, uses no GPU/model API and makes at most one store admission round.

- [ ] Write failing domain tests that prove the five lane enum values have the approved numeric order, GpuMiniTask rejects blank compatibility/idempotency values and invalid numeric input, every new enum has the exact approved members, options reject zero/negative bounds, and invalid callback shapes are rejected before a persistence call.

- [ ] Add focused coordinator tests with fake store, fake signal, fake status publisher and deterministic admission gate. Prove that:

  - a successful hand-off publishes and signals WorkReady only after the fake store reports a committed transition;
  - an idempotent replay returns the original task result without a second signal;
  - a normal Busy decision leaves a task ready with no due time;
  - only a Defer decision can contain a bounded retry delay;
  - a CapacityReleased wake is forwarded distinctly, even when other reasons are coalesced; and
  - the coordinator invokes no model/runtime abstraction.

- [ ] Run:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~GpuMiniTaskTests|FullyQualifiedName~GpuSchedulerContractTests"

  Expect the new tests to fail because the scheduler contracts do not exist.

- [ ] Implement the enums, records and validation. Make the lane enum ordering explicit in tests rather than relying on declaration order alone. Add an options Validate method called by its constructor or factory, including the maximum batch byte bound and capped deferral duration.

- [ ] Implement GpuSchedulerCoordinator as a narrow orchestration layer. It may call IGpuSchedulerStore, IStatusEventPublisher and IGpuSchedulerWakeSignal only. It must not reference Infrastructure.Inference, Infrastructure.Usearch, HttpClient, a file path, a model key in a status event, or device APIs.

- [ ] Re-run the focused tests. Expected: all pass, with no warnings.

- [ ] Commit:

    git add src/FluxKnowledge.Domain/Gpu src/FluxKnowledge.Application/Gpu tests/FluxKnowledge.Domain.Tests/Gpu
    git commit -m "feat: define GPU scheduler state contracts"

### Task 2: Add additive SQL durability and atomic mini-task hand-off

**Files:**

- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/GpuMiniTaskEntity.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/GpuBatchEntity.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/GpuCapacitySlotEntity.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/GpuSchedulerStateEntity.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/CanonicalSchemaConfigurations.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlGpuSchedulerStore.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/<timestamp>_AddGpuSchedulerDurability.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/<timestamp>_AddGpuSchedulerDurability.Designer.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/FluxKnowledgeDbContextModelSnapshot.cs
- Modify: tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Gpu/SqlGpuTaskHandoffTests.cs
- Modify: tests/FluxKnowledge.Integration.Tests/Support/SqlTestData.cs

**Durable schema:**

- Extend GpuMiniTasks with CreatedSequence, ExecutionState, DeferredUntilUtc, BatchId and ReservationAttemptCount. Map ExecutionState to the existing database column State. Add a non-null durable CreatedSequence supplied by a SQL sequence and order eligible tasks by PriorityLane, CreatedSequence and Id.
- Preserve immutable ParentJobId, SourceRevision, ModelRuntimeKey, SettingsFingerprint, EstimatedBytes, AdmissionGeneration and IdempotencyKey. Do not relax the existing unique IdempotencyKey constraint.
- Add GpuBatches with Id, CapacitySlotKey, PriorityLane, ModelRuntimeKey, SettingsFingerprint, ItemCount, EstimatedBytes, AdmissionGeneration, OwnerKey, State, LastHeartbeatAtUtc, CreatedAtUtc, UpdatedAtUtc and RowVersion.
- Add GpuCapacitySlots with SlotKey, State, ActiveBatchId, OwnerKey, LastHeartbeatAtUtc, UpdatedAtUtc and RowVersion. The diagnostic owner/heartbeat fields are private and are never projected to Web status.
- Add singleton GpuSchedulerState with Id fixed to 1, WakeGeneration, PendingWakeReasons, NextDeferredAtUtc, UpdatedAtUtc and RowVersion. Seed or create its lone row deterministically in the migration.
- Add foreign keys and indexes required for batch membership, ready queue ordering, capacity lookup and next durable deferral. Use restrictive delete behaviour everywhere. No cascade is permitted from a job, task, batch or slot into scheduler history.
- Generate an additive migration named AddGpuSchedulerDurability after the model compiles. Its Down method may remove only these newly added scheduler objects; it is never used in a live environment.

**Atomic hand-off behaviour:**

- GpuTaskHandoffAsync validates the claimed parent job ID, source revision, lease owner, lease generation and WorkerProcessing state in the same transaction that inserts the one idempotent mini-task row, changes the parent to GpuQueued and advances GpuSchedulerState.WakeGeneration with WorkReady.
- A unique-key replay must reread and return the original mini-task only when it represents the same parent/revision/validated request. A conflicting duplicate is rejected with no mutation.
- An injected SQL failure after insert but before job transition rolls back the task, job state and wake evidence together.
- Hand-off performs no stage execution, model loading, GPU work or outbox processing.

- [ ] Add failing mapping/migration tests that assert the new tables, foreign keys, restrictive deletes, GpuMiniTasks.State column mapping, sequence-backed CreatedSequence, and the native migration table count/schema upgrade.

- [ ] Add real SQL hand-off tests using NativeSqlServerFixture that:

  - create and claim a WorkerProcessing Job through the existing lease contract;
  - hand it off once and assert the task, GpuQueued parent state and WorkReady wake generation were committed together;
  - inject a hand-off failure and assert none of those three changes occurred;
  - invoke same-idempotency hand-off concurrently and observe exactly one mini-task and one parent transition;
  - reject wrong source revision, lease owner, lease generation, lane, compatibility/input or parent state without a scheduler mutation; and
  - confirm cleanup deletes GpuMiniTasks before Jobs and clears the new scheduler tables in safe dependency order.

- [ ] Run:

    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SchemaMappingTests|FullyQualifiedName~SqlGpuTaskHandoffTests"

  Expect failure until the additive model and store exist. If the native SQL connection variable is absent, report the specific skipped tests and retain the unit/mapping evidence; do not replace real SQL assertions with an in-memory provider.

- [ ] Add the entities and mappings. Use enum-backed integers and datetimeoffset(7), configure rowversions consistently with existing SQL mappings, and preserve the existing GpuMiniTasks table/column names.

- [ ] Create the migration with:

    dotnet ef migrations add AddGpuSchedulerDurability --project src/FluxKnowledge.Infrastructure.SqlServer/FluxKnowledge.Infrastructure.SqlServer.csproj --startup-project src/FluxKnowledge.Web/FluxKnowledge.Web.csproj

  Inspect the generated migration before retaining it. Ensure it creates a durable SQL sequence for CreatedSequence, safely backfills/sets values for any existing task rows, creates the singleton only once, and contains no drop/rebuild of an existing canonical table.

- [ ] Implement only GpuTaskHandoffAsync and read/status primitives in SqlGpuSchedulerStore in this task. Use a single SQL transaction with the existing application data-access conventions. Return an idempotent result explicitly; never catch a broad exception and reinterpret integrity/configuration/permission failures as a retry.

- [ ] Re-run the focused tests, then:

    dotnet build FluxKnowledge.slnx -c Release --no-restore

  Expected: focused tests pass, the Release build has zero warnings, and migration evidence is confined to disposable test databases.

- [ ] Commit:

    git add src/FluxKnowledge.Infrastructure.SqlServer/Persistence tests/FluxKnowledge.Integration.Tests/Persistence tests/FluxKnowledge.Integration.Tests/Gpu tests/FluxKnowledge.Integration.Tests/Support
    git commit -m "feat: persist GPU scheduler hand-off state"

### Task 3: Implement strict admission, batching and SQL fencing

**Files:**

- Modify: src/FluxKnowledge.Application/Gpu/GpuSchedulerContracts.cs
- Modify: src/FluxKnowledge.Application/Gpu/IGpuSchedulerStore.cs
- Modify: src/FluxKnowledge.Application/Gpu/GpuSchedulerCoordinator.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlGpuSchedulerStore.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Workers/NoGpuAdmissionGate.cs
- Create: tests/FluxKnowledge.Integration.Tests/Gpu/SqlGpuAdmissionTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Gpu/SqlGpuAdmissionConcurrencyTests.cs
- Create: tests/FluxKnowledge.Domain.Tests/Gpu/GpuSchedulerCoordinatorTests.cs

**Admission contract:**

- The scheduler calls the store once for each durable wake. The store takes a transaction-scoped exclusive application lock via sp_getapplock, rereads canonical queue/slot state, invokes the supplied pure admission decision, and commits at most one batch.
- The candidate selection query considers every ready lane. Its normal eligibility predicate is ExecutionState=Ready and DeferredUntilUtc is null or not later than now. For a CapacityReleased reason it additionally considers future-deferred Ready work, so a genuine release can immediately reconsider all of it.
- It sorts globally by approved lane, then CreatedSequence, then Id. The head determines ModelRuntimeKey and SettingsFingerprint. Later candidates must match that complete compatibility key and fit both positive item and byte bounds.
- It never uses READPAST for selection, ageing, promotion, model-key priority or a blind next-item loop.
- A successful Admit decision atomically creates one GpuBatch, increments its mini-tasks' AdmissionGeneration, marks them Active, assigns BatchId, reserves a previously Available capacity slot, changes each affected parent Job from GpuQueued to GpuProcessing and records scheduler wake/status evidence.
- Busy means ordinary healthy in-process contention. It leaves the selected work Ready with DeferredUntilUtc null, writes no per-item due time and makes no new batch.
- Defer is permitted only for explicit uncertain/unavailable capacity or a transient pre-execution reservation failure. It leaves work Ready, increments ReservationAttemptCount, writes a capped DeferredUntilUtc and records a future durable wake. SQL integrity/configuration/permission and fencing errors do not defer or mutate state.
- Uncertain slots are not eligible for admission. A stale heartbeat may move a slot to Uncertain, but it may not make a slot Available, requeue a task, change a Job, create a batch, or replace a result.

- [ ] Write failing real-SQL selection tests for:

  - all five lanes, FIFO by CreatedSequence then Id, and no lower-lane bypass;
  - a head task forming a bounded batch only with same lane, runtime key and settings fingerprint;
  - both item and byte limit boundaries;
  - an older incompatible task preventing a later compatible item from jumping the FIFO position;
  - Busy retaining Ready/null deferral;
  - Defer writing a capped pre-execution delay and a durable next wake;
  - CapacityReleased selecting a future-deferred high-priority task before otherwise eligible lower work; and
  - an Uncertain slot blocking admission without touching the queue.

- [ ] Add a deterministic parallel admission test that starts two callers behind a barrier against one ready set and one slot. Assert exactly one batch, each selected mini-task active once, a single slot reservation, a single parent GpuProcessing transition, and no lower-lane bypass. It must execute against native SQL rather than merely an in-memory fake.

- [ ] Run:

    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SqlGpuAdmissionTests|FullyQualifiedName~SqlGpuAdmissionConcurrencyTests"

  Expect the admission tests to fail before selection is implemented.

- [ ] Implement NoGpuAdmissionGate as the default injected gate. It returns Busy without accessing a driver, process, HTTP endpoint, model cache, inference package or device. Tests inject an explicit deterministic Admit, Busy or Defer gate.

- [ ] Implement the store admission round with a short transaction and sp_getapplock. Keep query/read, pure gate result validation and durable transition inside the bounded transaction; hold neither lock nor transaction after the decision commits. If the gate throws or returns invalid slot/owner/retry data, roll back and surface an operator-actionable failure.

- [ ] Use compare-and-set update predicates and rowversions for selected task/slot/job state. Reject a slot with any active batch other than the exact admitted one. Record a sanitised wake reason after each successful durable state transition, but do not emit IDs, keys or owner values through status events.

- [ ] Re-run the focused selection and concurrency tests, then the Task 1 coordinator tests. Expected: all pass, and normal contention has no calculated due time.

- [ ] Commit:

    git add src/FluxKnowledge.Application/Gpu src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlGpuSchedulerStore.cs src/FluxKnowledge.Infrastructure.SqlServer/Workers/NoGpuAdmissionGate.cs tests/FluxKnowledge.Integration.Tests/Gpu tests/FluxKnowledge.Domain.Tests/Gpu
    git commit -m "feat: admit strict-priority GPU batches"

### Task 4: Add event-driven wakes, safe boundaries and reconciliation fencing

**Files:**

- Create: src/FluxKnowledge.Infrastructure.SqlServer/Workers/ChannelGpuSchedulerWakeSignal.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Workers/GpuSchedulerService.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Workers/GpuSchedulerServiceCollectionExtensions.cs
- Modify: src/FluxKnowledge.Application/Gpu/GpuSchedulerCoordinator.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlGpuSchedulerStore.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs only if a shared registration location is required by existing conventions
- Create: tests/FluxKnowledge.Integration.Tests/Gpu/SqlGpuBatchLifecycleTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Workers/GpuSchedulerServiceTests.cs
- Modify: tests/FluxKnowledge.Domain.Tests/Pipeline/OutboxWorkerRegistrationTests.cs only if its registration assertions are the established location

**Wake and callback contract:**

- ChannelGpuSchedulerWakeSignal uses a bounded coalescing representation of GpuSchedulerWakeReason. If several wake reasons merge, CapacityReleased remains present until observed; a bool-only signal is not sufficient.
- Every committed hand-off, safe boundary, completion, capacity release, explicit pre-execution deferral and trusted reconciliation increments GpuSchedulerState.WakeGeneration and ORs its reason flags before sending the local signal.
- GpuSchedulerService runs one coordinator admission round per observed durable wake. At startup it requests StartupRecovery; at the configured bounded fallback interval it rereads durable wake state. A fallback recheck is never a task lease expiry or executor timeout.
- The service uses TimeProvider and a cancellable timer/delay only to reach durable DeferredUntilUtc/fallback checks. A real signal wakes it early. It does not spawn a per-mini-task timer.
- SafeBoundary callback requires matching batch ID, slot key, owner key and admission generation. It may change the batch to AtSafeBoundary and record whether capacity was retained or explicitly released. A retained boundary is not evidence that capacity is free.
- Completed callback requires the same fenced identity, marks only its batch/task records as completed and explicitly releases its slot. This checkpoint does not write model output or terminalise a parent job.
- CapacityReleased callback requires matching fence, explicitly frees the Reserved slot and may mark unresolved task outcomes OutcomeUncertain. It does not automatically requeue them or change parent jobs to a result state.
- A late, duplicate or mismatched callback returns a rejected outcome with no durable side effect.
- MarkCapacityUncertain is diagnostic-only. It moves Reserved to Uncertain and its batch to CapacityUncertain but never frees capacity or changes mini-task/job/output state.
- Trusted reconciliation requires a specific trusted evidence class. It may move only the matching Uncertain slot to Available, records the reconciliation wake, and leaves any OutcomeUncertain task/job unchanged. Separate explicit fenced task-outcome reconciliation is the only future route to resubmission or terminal result handling.

- [ ] Write failing lifecycle integration tests proving:

  - a lower-priority active batch is not cancelled when a higher-priority task arrives;
  - its explicit safe boundary causes a whole-queue re-evaluation, and the highest lane is selected first if capacity is released;
  - a retained safe boundary produces a truthful unavailable decision and no concurrent slot reservation;
  - CapacityReleased wakes deferred work immediately even if its durable due time is in the future;
  - advancing every fallback/heartbeat/owner-age clock alone creates no batch, requeue, parent Job transition, result replacement or task outcome transition;
  - stale owner diagnostics produce Uncertain rather than Available and block a waiting high-priority task;
  - trusted reconciliation can free the slot while leaving the prior uncertain task OutcomeUncertain and parent job GpuProcessing; and
  - late/duplicate/wrong-generation/wrong-owner callbacks cause no state change.

- [ ] Write hosted-service tests with a fake TimeProvider, fake store or disposable SQL fixture, deterministic gate and TaskCompletionSource wake signal. Prove a CapacityReleased signal causes immediate reconsideration before the next deferred due time, a normal Busy result waits for a real boundary/release signal, service restart rereads durable wake state, and the default registration never calls model/GPU code.

- [ ] Run:

    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SqlGpuBatchLifecycleTests|FullyQualifiedName~GpuSchedulerServiceTests"

  Expect the event and fencing tests to fail before the new signal, service and callback store transitions are present.

- [ ] Implement callback transitions as one transaction each. Validate all task outcomes belong to the exact batch and cover the required active task set; reject partial/mixed callback payloads rather than guessing. Persist state first, then let the coordinator publish a content-free gpu-scheduler status event and signal the coalescing channel.

- [ ] Implement GpuSchedulerService with a single logical worker and cancellation-safe disposal. It must not use a lease-expiry requeue loop, call ClaimGpuAsync, alter OutboxPumpService ownership, or invoke a runtime. Treat a scheduler exception as visible diagnostics/logging and retain durable state; do not silently write a new deferred delay for configuration, permission or SQL-integrity errors.

- [ ] Register the service alongside, not inside, the existing outbox pump registration. Resolve its default gate, store, signal and TimeProvider through dependency injection. Add a composition assertion that the hosted service is available but no inference/model executor is activated.

- [ ] Re-run the focused lifecycle, hosted-service and composition tests. Expected: all pass and the elapsed-time safety tests demonstrate no admission/requeue/result replacement.

- [ ] Commit:

    git add src/FluxKnowledge.Infrastructure.SqlServer/Workers src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlGpuSchedulerStore.cs src/FluxKnowledge.Application/Gpu tests/FluxKnowledge.Integration.Tests/Gpu tests/FluxKnowledge.Integration.Tests/Workers tests/FluxKnowledge.Domain.Tests/Pipeline
    git commit -m "feat: wake GPU scheduler at explicit boundaries"

### Task 5: Expose sanitised local scheduler status

**Files:**

- Modify: src/FluxKnowledge.Application/Contracts/StatusContracts.cs
- Modify: src/FluxKnowledge.Web/Components/Status/SqlProjectionReader.cs
- Modify: src/FluxKnowledge.Web/Components/Status/OverviewProjectionState.cs
- Modify: src/FluxKnowledge.Web/Components/Pages/Overview.razor
- Create: src/FluxKnowledge.Web/Endpoints/GpuStatusEndpoints.cs
- Modify: src/FluxKnowledge.Web/Program.cs
- Modify: src/FluxKnowledge.Web/WebHostComposition.cs
- Create: tests/FluxKnowledge.Web.Tests/Endpoints/GpuStatusEndpointTests.cs
- Modify: tests/FluxKnowledge.Web.Tests/Components/OverviewProjectionTests.cs
- Modify: tests/FluxKnowledge.Web.Tests/Components/SqlProjectionReaderIntegrationTests.cs
- Modify: tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs

**Status contract:**

- Add a GpuSchedulerStatusSnapshot containing only aggregate Ready, Active, Deferred and OutcomeUncertain counts; per-lane aggregate counts; active-batch presence and lane; Available/Reserved/Uncertain slot counts; next durable pre-execution retry; and a bounded age/state for uncertain capacity.
- The Web API serialises only that snapshot at GET /api/gpu-status. It has no POST, PUT, PATCH or DELETE route.
- Do not include source paths, text, idempotency keys, mini-task IDs, batch IDs, slot IDs, owner IDs, model/runtime keys, settings fingerprints, SQL exception text, raw heartbeat values, runtime output or a mutating action token.
- Add gpu-scheduler as a presentation event projection. SignalR only prompts a SQL reload; reconnect remains SQL-authoritative.
- The Overview adds concise local status counts only. It does not claim the app is executing GPU work and does not introduce a control.

- [ ] Add failing Web endpoint tests that seed scheduler state through the store/fixture and assert exact GET field names, values, content type and only sanitised data. Add negative tests that search the response body for seeded sensitive identifiers/keys/text and verify every mutation verb is MethodNotAllowed.

- [ ] Add failing projection/component tests that assert:

  - initial Overview load reads the new SQL projection;
  - a gpu-scheduler event reloads it;
  - a reconnect reloads it from SQL rather than trusting event payload;
  - null next retry and no active batch render truthfully; and
  - no scheduler control/action UI appears.

- [ ] Run:

    dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~GpuStatusEndpointTests|FullyQualifiedName~OverviewProjectionTests|FullyQualifiedName~SqlProjectionReaderIntegrationTests|FullyQualifiedName~WebHostCompositionTests"

  Expect failure because no status endpoint/projection exists.

- [ ] Implement a dedicated ReadGpuSchedulerStatusAsync query that performs aggregate SQL reads and bounds diagnostic age in the application contract. Do not materialise private scheduler entity values into the Web response and do not make the endpoint call an admission/reconciliation method.

- [ ] Map only GET /api/gpu-status beside the existing health/index endpoint registrations. Publish StatusChanged with projection gpu-scheduler only after durable scheduler commits. Update OverviewProjectionState to recognise that projection and retain existing pipeline/index-recovery behaviour unchanged.

- [ ] Re-run the focused Web suite. Expected: endpoint, component, reconnect, composition and mutation-negative tests pass; no model or device test is introduced.

- [ ] Commit:

    git add src/FluxKnowledge.Application/Contracts/StatusContracts.cs src/FluxKnowledge.Web tests/FluxKnowledge.Web.Tests
    git commit -m "feat: expose local GPU scheduler status"

### Task 6: Run the complete verification matrix, review scope and update durable intent

**Files:**

- Modify: docs/architecture.md
- Modify: docs/roadmap.md
- Modify: any changed test support file only where failure evidence demonstrates a fixture cleanup or native migration requirement

**Documentation boundary:**

- Add the Phase 2 scheduler authority and safety invariants to architecture.md: SQL authority, lane/FIFO/batching order, application-lock scope, no lease-expiry requeue, explicit release/boundary fencing, Uncertain capacity, trusted reconciliation separation, and local read-only status.
- Update the relevant Phase 2 roadmap progress only after implementation and focused evidence exist. Record that real executor/model work, production migration/deployment and external access remain unimplemented. Do not update the dashboard manual, screenshots or rendered manuals.

- [ ] Run the focused unit and native-SQL matrix:

    dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj
    dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SqlGpuTaskHandoffTests|FullyQualifiedName~SqlGpuAdmissionTests|FullyQualifiedName~SqlGpuAdmissionConcurrencyTests|FullyQualifiedName~SqlGpuBatchLifecycleTests|FullyQualifiedName~GpuSchedulerServiceTests|FullyQualifiedName~SchemaMappingTests"
    dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~GpuStatusEndpointTests|FullyQualifiedName~OverviewProjectionTests|FullyQualifiedName~SqlProjectionReaderIntegrationTests|FullyQualifiedName~WebHostCompositionTests"

  Record exact pass/fail/skip counts. If native SQL tests are skipped because the controlled connection is unavailable, do not claim the real-SQL acceptance invariants as passed.

- [ ] Run the broad repository checks after focused tests are green:

    dotnet restore FluxKnowledge.slnx --locked-mode
    dotnet build FluxKnowledge.slnx -c Release --no-restore
    dotnet test FluxKnowledge.slnx -c Release --no-build
    git diff --check
    git status --short

  Expected: locked restore succeeds, Release build has zero warnings, applicable full test projects pass, diff check is clean and only intended source/test/doc changes remain.

- [ ] Independently inspect the final diff against the approved spec. Confirm each acceptance invariant has a named focused test and that no change:

  - requeues Active or OutcomeUncertain work from elapsed time;
  - treats heartbeat/owner age as capacity release;
  - starts a next individual item without a new durable wake;
  - deletes an active or referenced task/batch/job;
  - includes model/GPU, external, legacy/RabbitMQ, mutation endpoint, deployment or production configuration work; or
  - exposes sensitive scheduler identifiers or text.

- [ ] Update architecture.md and roadmap.md with only verified facts. Leave a concise remaining-work entry for an explicitly approved future executor/result stage and separate approval-gated production migration/deployment.

- [ ] Commit:

    git add docs/architecture.md docs/roadmap.md
    git commit -m "docs: record Phase 2 scheduler foundation"

- [ ] Stop for a fresh user decision before any branch closeout, merge, worktree purge, deployment, IIS action, production database operation or live validation. If the user later authorises code-only closeout, use scripts/dev/complete-feature.ps1 exactly as required by repository guidance; do not replace it with a manual sequence.

## Acceptance traceability

| Approved acceptance invariant | Implementing task | Primary proof |
| --- | --- | --- |
| Atomic worker hand-off and duplicate idempotency | Task 2 | SqlGpuTaskHandoffTests against native SQL |
| Strict lane/FIFO/compatibility/bounds | Task 3 | SqlGpuAdmissionTests |
| One admission round and no blind next item | Tasks 3-4 | admission-round and service wake tests |
| Busy healthy capacity is event-driven | Tasks 3-4 | Busy/null-deferral and release wake tests |
| Future deferred work is reconsidered on real release | Tasks 3-4 | CapacityReleased early-selection test |
| Time alone never changes execution/capacity/result state | Task 4 | deterministic clock-advance lifecycle test |
| Unresponsive executor becomes Uncertain, not free | Task 4 | stale-owner diagnostic test |
| Trusted reconciliation only frees verified capacity | Task 4 | reconciliation/outcome-uncertain test |
| Fenced late/duplicate callbacks have no side effect | Task 4 | callback fencing matrix |
| Parallel admission is safe | Task 3 | native SQL concurrency test |
| Local status is read-only and sanitised | Task 5 | REST, component and reconnect tests |
| No model/GPU/external/legacy/deployment work | Tasks 1, 4, 5, 6 | composition/default-gate and final scope review |

## Approval-gated work deliberately excluded

The following is not implementation-plan execution and requires new current-conversation approval after the code review:

1. Applying AddGpuSchedulerDurability to the loopback IIS database.
2. Starting or restarting IIS, an app pool, a scheduler process, a model process or any external service.
3. Deploying/merging/closing the feature branch, purging its worktree or running scripts/dev/complete-feature.ps1.
4. Connecting a real GPU executor, model runtime/cache/download, output/result reconciler, worker stage or external access surface.
5. Any production or live validation beyond disposable native SQL catalogues and local automated test hosts.
