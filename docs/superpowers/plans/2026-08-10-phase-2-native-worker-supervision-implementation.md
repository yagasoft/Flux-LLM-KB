# Phase 2 deterministic native-worker supervision implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a default-disabled, app-owned deterministic child worker whose
private named-pipe lifecycle is SQL-fenced, restart-safe and testable without
activating inference, GPU, source-data or network capability.

**Architecture:** `FluxKnowledge.DeterministicWorker` is a small console process that
shares only the existing executor handle contracts. A `NativeWorkerSupervisor`
launches and attests it over a current-user named pipe; a private adapter maps
validated protocol frames to the existing lifecycle sink. SQL stores private
instance/evidence state and audit events, while all public projections remain
sanitised and read-only. The production default remains `NoGpuAdmissionGate`
and does not launch a worker.

**Tech stack:** .NET 10, C#, `System.Diagnostics.Process`,
`System.IO.Pipes`, `System.Text.Json`, Entity Framework Core SQL Server,
serialisable scheduler transactions, xUnit and disposable native SQL catalogues.

## Global constraints

- SQL remains authoritative for dispatch, receipt, lifecycle and worker evidence.
- Preserve exactly six public Job states; process state is private evidence only.
- The worker accepts only `GpuExecutorBatchHandle` and deterministic protocol
  frames: no model/GPU/runtime API, source bytes, artefact path, result payload,
  filesystem work, network connection, external access, RabbitMQ, Docker,
  Vespa or legacy action.
- Default production composition starts no worker and registers no active
  `IGpuExecutorAdapter`; enabling exists only for controlled local/test hosts.
- Never infer completion, capacity release, retry or requeue from PID, process
  exit, heartbeat age, timeout, pipe disconnect or host restart.
- Store no pipe name, nonce, command line, raw diagnostic, executable path,
  source content, credential or model detail in SQL/audit/public output.
- Start every behavioural change with a focused failing native C# test, then
  retain fresh GREEN command output. Build with `-warnaserror`; do not run
  `dotnet format`.
- Deployment happens only after all implementation, disposable-SQL evidence,
  review and the user-authorised `scripts/dev/complete-feature.ps1` closeout.
- This conversation explicitly authorises the controlled local migration and
  non-production loopback deployment invoked by that closeout command; it does
  not authorise worker enablement, model/GPU/runtime activation, external
  access or legacy action.

---

## File and responsibility map

| Area | Files | Responsibility |
| --- | --- | --- |
| Shared private contracts | `src/FluxKnowledge.Application/Gpu/NativeWorkerContracts.cs`, `INativeWorkerInstanceStore.cs` | Validate closed protocol, opaque instance fence, bounded safe evidence and store operations before infrastructure is called. |
| Deterministic child | `src/FluxKnowledge.DeterministicWorker/FluxKnowledge.DeterministicWorker.csproj`, `Program.cs`, `DeterministicWorkerProtocolLoop.cs` | Pipe-only console process with scripted modes and no inference/source/network dependency. |
| SQL authority | Worker entity files, `FluxKnowledgeDbContext.cs`, `CanonicalSchemaConfigurations.cs`, `SqlNativeWorkerInstanceStore.cs`, migration and snapshot | Restrictive, append-only instance/evidence records; atomic fenced state and audit writes. |
| Supervision | `NativeWorkerSupervisorService.cs`, `NativeWorkerExecutorAdapter.cs`, `NativeWorkerOptions.cs`, `NativeWorkerPipeServer.cs`, service registration | Launch, PID/start-time attestation, pipe/session validation, heartbeats, explicit uncertainty and idle shutdown. |
| Tests and operations | Domain, integration, worker, mapping, composition and status tests; deployment script contracts and docs | TDD proof, public-surface safety, migration targeting, validation record and roadmap evidence. |

### Task 1: Define private worker contracts and build the deterministic executable

**Files:**
- Create: `src/FluxKnowledge.Application/Gpu/NativeWorkerContracts.cs`
- Create: `src/FluxKnowledge.Application/Gpu/INativeWorkerInstanceStore.cs`
- Create: `src/FluxKnowledge.DeterministicWorker/FluxKnowledge.DeterministicWorker.csproj`
- Create: `src/FluxKnowledge.DeterministicWorker/Program.cs`
- Create: `src/FluxKnowledge.DeterministicWorker/DeterministicWorkerProtocolLoop.cs`
- Modify: `FluxKnowledge.slnx`
- Create: `tests/FluxKnowledge.Domain.Tests/Gpu/NativeWorkerContractTests.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Workers/DeterministicWorkerProcessTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj`

**Interfaces:**
- Produces `NativeWorkerInstanceHandle(Guid InstanceId, string ExecutorKey,
  int ProcessId, DateTimeOffset ProcessStartedAtUtc, string ProtocolVersion)`;
  it rejects empty IDs, non-canonical keys, non-positive PID and UTC defaults.
- Produces `NativeWorkerFrame` records with closed `NativeWorkerFrameKind`:
  `Hello`, `Welcome`, `Ready`, `Heartbeat`, `Acknowledgement`, `Receipt`,
  `Dispatch`, `TestInstruction`, `Acknowledgement`, `Receipt`, `Callback`,
  `StopRequested`, `Stopped` and `ProtocolRejected`. `TestInstruction` is a
  closed, bounded test-only mode (`AcknowledgeAndHold`, `ReceiptAndComplete`,
  `ExitBeforeAcknowledgement`, `Unresponsive`) selected by the controlled host;
  it carries no source, model or runtime detail.
- Produces `NativeWorkerLifecycleClass` with the exact safe evidence names from
  the approved design, and `INativeWorkerInstanceStore` operations to create an
  instance, append immutable evidence, record an attested connection, record a
  heartbeat, record exit and mark one exact active handle uncertain.
- Consumes existing `GpuExecutorBatchHandle`, `GpuExecutorAcknowledgement`,
  `GpuExecutorResultReceipt` and `IGpuExecutorLifecycleSink` contracts.

- [ ] **Step 1: Write the failing contract and child-process tests**

  Add tests that reject empty/non-canonical instance data, unknown frame kinds,
  a frame with raw detail, protocol mismatch, a replayed nonce and a worker
  process that rejects a source/model/GPU argument. Add a process test that
  starts the new executable with only `--pipe`, `--instance` and
  `--protocol-version`, then expects a `Hello` frame and no file/network side
  effect.

  ```csharp
  Assert.Throws<ArgumentException>(() =>
      NativeWorkerInstanceHandle.Create(Guid.Empty, "executor ", 0, default, "v1"));
  Assert.Equal(NativeWorkerFrameKind.Hello, await harness.ReadFrameAsync());
  ```

- [ ] **Step 2: Run the RED tests**

  Run:

  ```powershell
  dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~NativeWorkerContractTests"
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~DeterministicWorkerProcessTests"
  ```

  Expected: compilation fails because the native-worker contracts and executable
  do not exist.

- [ ] **Step 3: Implement the minimal private protocol and worker**

  Put serialisable protocol records in `NativeWorkerContracts.cs`; frames contain
  only protocol version, instance ID, session nonce after `Welcome`, opaque
  handle and closed disposition. Validate before deserialised data reaches a
  store or lifecycle sink. Reference the Application project from the worker
  project, use `NamedPipeClientStream`, newline-delimited UTF-8 JSON with a
  16 KiB frame cap, and reject every unsupported argument. The worker must not
  reference inference projects or call `Process`, `HttpClient`, file APIs or
  GPU APIs. Add the project to `FluxKnowledge.slnx`, and make the integration-
  test project build and copy worker output through a non-reference-output
  project dependency so focused process tests work from a clean checkout.

- [ ] **Step 4: Run GREEN tests and dependency boundary search**

  Run:

  ```powershell
  dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~NativeWorkerContractTests"
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~DeterministicWorkerProcessTests"
  rg -n "HttpClient|System\.Diagnostics\.Process|File\.|Directory\.|Inference|Cuda|GPU" src/FluxKnowledge.DeterministicWorker
  ```

  Expected: both focused suites pass; the search exits 1 because the worker has
  no forbidden dependency references.

- [ ] **Step 5: Commit the coherent protocol boundary**

  ```powershell
  git add FluxKnowledge.slnx src/FluxKnowledge.Application/Gpu src/FluxKnowledge.DeterministicWorker tests/FluxKnowledge.Domain.Tests/Gpu tests/FluxKnowledge.Integration.Tests
  git commit -m "feat: add deterministic native worker protocol"
  ```

### Task 2: Persist fenced worker instance and lifecycle evidence

**Files:**
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/NativeWorkerInstanceEntity.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/NativeWorkerLifecycleEvidenceEntity.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/CanonicalSchemaConfigurations.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlNativeWorkerInstanceStore.cs`
- Generate: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/*_AddNativeWorkerSupervision.cs` and designer
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/FluxKnowledgeDbContextModelSnapshot.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Workers/SqlNativeWorkerInstanceStoreTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Support/SqlTestData.cs`

**Interfaces:**
- `NativeWorkerInstances` uses `InstanceId` as the key, references the exact
  `GpuExecutorDispatches.DispatchId` only when a handle is active, has opaque
  executor key, PID/start-time attestation, executable SHA-256 fingerprint,
  protocol version, closed state, timestamps and rowversion.
- `NativeWorkerLifecycleEvidence` uses `OperationId` as an immutable primary
  idempotency key, has a restrictive FK to its instance and retains only the
  closed lifecycle class, bounded numeric outcome code, canonical request
  fingerprint and timestamp.
- Store mutations use the same serialisable transaction and request-fingerprint
  replay rules as the existing GPU dispatch store. Each mutation appends a
  sanitised correlated `native_worker.*` audit event through `OperatorEventAppender`.

- [ ] **Step 1: Write failing native SQL mapping and transaction tests**

  Assert the two new tables, binary opaque-key collation, no-trailing-whitespace
  checks, SHA-256 executable fingerprint, rowversions, restrictive FKs,
  `(DispatchId)` uniqueness for an active association, operation replay,
  atomic rollback and concurrent same-instance reconciliation fencing. Seed a
  dispatch, create an instance, record the same heartbeat operation twice,
  then attempt a divergent same-operation replay. Race two distinct
  reconciliation operations against one instance and prove that serialisable
  fencing accepts exactly one state transition/evidence/audit append while the
  loser is a deterministic rejection or replay.

  ```csharp
  Assert.True(first.Accepted);
  Assert.True(replay.IsIdempotentReplay);
  await Assert.ThrowsAsync<InvalidOperationException>(() => divergent);
  Assert.Single(await db.NativeWorkerLifecycleEvidence.ToListAsync());
  ```

- [ ] **Step 2: Run RED mapping and store tests**

  Run:

  ```powershell
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SchemaMappingTests|FullyQualifiedName~SqlNativeWorkerInstanceStoreTests"
  ```

  Expected: tests fail because worker entities, tables and store are absent.

- [ ] **Step 3: Implement schema, store and inspected additive migration**

  Implement private entities/configurations and store methods. Generate the
  migration only after model compilation:

  ```powershell
  dotnet ef migrations add AddNativeWorkerSupervision --project src/FluxKnowledge.Infrastructure.SqlServer/FluxKnowledge.Infrastructure.SqlServer.csproj --startup-project src/FluxKnowledge.Web/FluxKnowledge.Web.csproj
  ```

  Inspect that it adds only the two worker tables, indexes, restrictive keys and
  constraints. In `SqlTestData`, delete worker evidence before instances and
  instances before dispatches. Do not alter existing scheduler state from worker
  observations except through the existing explicit uncertainty sink.

- [ ] **Step 4: Run GREEN SQL evidence**

  Run the Step 2 command with the documented disposable SQL connection, then:

  ```powershell
  dotnet build FluxKnowledge.slnx --configuration Release -warnaserror
  git diff --check
  ```

  Expected: focused SQL tests, including concurrent reconciliation, and the
  zero-warning build pass; migration diff is additive only.

- [ ] **Step 5: Commit durable worker evidence**

  ```powershell
  git add src/FluxKnowledge.Infrastructure.SqlServer/Persistence tests/FluxKnowledge.Integration.Tests/Persistence tests/FluxKnowledge.Integration.Tests/Workers
  git commit -m "feat: persist native worker lifecycle evidence"
  ```

### Task 3: Supervise the child process and map frames through the existing lifecycle sink

**Files:**
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/NativeWorkerOptions.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/NativeWorkerPipeServer.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/NativeWorkerSupervisorService.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/NativeWorkerExecutorAdapter.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/GpuSchedulerServiceCollectionExtensions.cs`
- Modify: `src/FluxKnowledge.Web/WebHostComposition.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Workers/NativeWorkerSupervisorServiceTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Workers/GpuExecutorDispatchRecoveryServiceTests.cs`
- Modify: `tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs`

**Interfaces:**
- `NativeWorkerOptions` defaults to `Enabled = false`, requires a non-empty
  canonical executor key and verified worker executable path when enabled, and
  caps connect, heartbeat and idle-stop intervals.
- `NativeWorkerSupervisorService` implements `IHostedService`; it owns exactly
  one worker per enabled executor key and exposes no public mutation method.
- `NativeWorkerExecutorAdapter : IGpuExecutorAdapter` sends only the original
  `GpuExecutorBatchHandle` to a fully attested ready worker.
- `NativeWorkerPipeServer` validates frame bounds, protocol version, the
  expected Windows named-pipe client PID, PID start time and a fresh in-memory
  nonce before it forwards acknowledgement/receipt/callback frames to
  `IGpuExecutorLifecycleSink`.

- [ ] **Step 1: Write failing supervisor tests**

  Add tests for disabled composition (no adapter/process), `Launching` durable
  evidence before `Process.Start`, bounded launch failure with no scheduler
  mutation, attested launch and ready handshake, same-instance reconnect replay,
  stale PID/start-time rejection before nonce issuance or mutation, duplicate
  delivery returning the original durable handle, one real child-process proof
  for each deterministic mode, child exit and heartbeat timeout recording only
  uncertainty, restart refusing to adopt a prior worker, graceful idle stop,
  and forced stop first recording uncertainty. Use a real child executable and
  inject `TimeProvider`; never use wall-clock sleeps as the assertion.

  ```csharp
  await supervisor.ReconcileAsync(CancellationToken.None);
  Assert.Equal(GpuExecutorDispatchState.DeliveryUncertain, dispatch.State);
  Assert.Equal(GpuCapacitySlotState.Uncertain, slot.State);
  Assert.Equal(before.CompletedTaskCount, after.CompletedTaskCount);
  ```

- [ ] **Step 2: Run RED supervisor and composition tests**

  Run:

  ```powershell
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~NativeWorkerSupervisorServiceTests|FullyQualifiedName~GpuExecutorDispatchRecoveryServiceTests"
  dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~WebHostCompositionTests"
  ```

  Expected: tests fail because supervision, options and adapter registration are absent.

- [ ] **Step 3: Implement launch, attestation, recovery and safe stop**

  Register only the supervisor when options are enabled; otherwise retain the
  existing zero-adapter composition. Create `NamedPipeServerStream` with
  `PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly`; use a minimal,
  isolated Windows `GetNamedPipeClientProcessId` P/Invoke over the server
  pipe's safe handle behind a testable interface, compare it with the launched
  `Process.Id`, and verify `Process.StartTime` before sending the nonce. Use
  `ProcessStartInfo` with `UseShellExecute = false`, redirected
  standard streams and only the three permitted arguments. Convert accepted
  frames into existing lifecycle calls. On every lost/exit/restart observation,
  write evidence and invoke the existing delivery-uncertainty path for the exact
  handle; do not call a completion, release or retry API. Permit `Kill` only in
  the injected controlled-test stop path after uncertainty is durable.

- [ ] **Step 4: Run GREEN real-process behaviour and composition safety**

  Run the Step 2 commands, then:

  ```powershell
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SqlNativeWorkerInstanceStoreTests|FullyQualifiedName~NativeWorkerSupervisorServiceTests"
  rg -n "Map(Post|Put|Delete)|McpServerTool|HttpClient" src/FluxKnowledge.Infrastructure.SqlServer/Workers/NativeWorker* src/FluxKnowledge.DeterministicWorker
  ```

  Expected: focused suites pass; the search exits 1 because no public mutation or network surface was introduced.

- [ ] **Step 5: Commit supervision behaviour**

  ```powershell
  git add src/FluxKnowledge.Infrastructure.SqlServer/Workers src/FluxKnowledge.Web/WebHostComposition.cs tests/FluxKnowledge.Integration.Tests/Workers tests/FluxKnowledge.Web.Tests/Composition
  git commit -m "feat: supervise deterministic native worker"
  ```

### Task 4: Prove privacy, read-only status and end-to-end recovery boundaries

**Files:**
- Modify: `tests/FluxKnowledge.Web.Tests/Endpoints/GpuStatusEndpointTests.cs`
- Modify: `tests/FluxKnowledge.Web.Tests/Mcp/McpEndpointRegistrationTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Workers/SqlGpuExecutorDispatchRecoveryServiceTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Workers/NativeWorkerSupervisorServiceTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Persistence/PipelineOperatorEventIntegrationTests.cs`
- Modify: `tests/FluxKnowledge.Web.Tests/Components/SqlProjectionReaderIntegrationTests.cs`

- [ ] **Step 1: Write failing storage, audit and restart tests**

  Assert the worker tables have no columns for pipe name, nonce, command line,
  executable path or raw diagnostics; contract tests reject a frame carrying raw
  detail before it can reach persistence. Seed only permitted private evidence
  (PID, start time, opaque instance ID and executable fingerprint), then prove
  `native_worker.*` audit details retain only an allowlisted class, instance
  correlation and bounded reason code. Recreate the supervisor after a
  persisted active worker and prove it writes lost/uncertainty evidence without
  adopting, killing, completing or replacing the old process. Keep current
  REST, MCP, CLI and Blazor tests as read-only characterisation checks; they
  must not require forbidden values to be persisted merely to make them RED.

- [ ] **Step 2: Run RED public/recovery tests**

  ```powershell
  dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~GpuStatusEndpointTests|FullyQualifiedName~McpEndpointRegistrationTests"
  dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SqlGpuExecutorDispatchRecoveryServiceTests|FullyQualifiedName~NativeWorkerSupervisorServiceTests"
  ```

  Expected: storage, audit and restart assertions fail until sanitisation and
  recovery are fenced; unchanged public-surface characterisation assertions
  remain green and prove no new public disclosure or mutation surface.

- [ ] **Step 3: Make only the minimal projection and recovery corrections**

  Keep the status DTO unchanged unless an aggregate worker count is necessary;
  if one is added, expose only a bounded count. Ensure `OperatorEventAppender`
  receives only allowlisted worker classes and bounded reason codes. Make host
  restart perform durable lost/uncertain observation, not process adoption or
  termination. Do not add a public CLI, REST, MCP, SignalR or Blazor worker
  control path.

- [ ] **Step 4: Run GREEN focused safety matrix**

  Run the Step 2 commands, then:

  ```powershell
  dotnet build FluxKnowledge.slnx --configuration Release -warnaserror
  git diff --check
  ```

  Expected: public status remains read-only and sanitised; build has zero warnings and errors.

- [ ] **Step 5: Commit safety proof**

  ```powershell
  git add tests/FluxKnowledge.Web.Tests tests/FluxKnowledge.Integration.Tests
  git commit -m "test: prove native worker supervision safety"
  ```

### Task 5: Complete verification, deployment targeting and authorised closeout

**Files:**
- Modify: `scripts/deploy/update-native-windows.ps1`
- Modify: `tests/native/native-deployment-plan.ps1`
- Modify: `scripts/dev/complete-feature.ps1`
- Modify: `tests/native/complete-feature-dryrun.ps1`
- Create: `scripts/deploy/validate-native-worker-supervision.ps1`
- Modify: `docs/architecture.md` and `docs/roadmap.md` after fresh evidence
- Create: `docs/operations/native-windows-phase-2-native-worker-supervision-validation.md`

- [ ] **Step 1: Write failing deployment-plan and closeout-contract tests**

  Extend the native deployment-plan test so it reads the generated
  `*_AddNativeWorkerSupervision.cs` filename, derives its migration ID, and
  asserts the deployment script includes that exact ID in required migrations,
  preflight, `dotnet ef database update` target and final result. Add a
  closeout-contract assertion for a post-deployment validation hook: it runs
  only after the local deployment reports success, verifies the worker remains
  disabled, writes the sanitised validation record, commits that record on
  `main`, pushes it, and only then removes the feature worktree.

- [ ] **Step 2: Run RED deployment tests**

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File tests/native/native-deployment-plan.ps1 -SourceRoot .
  powershell -NoProfile -ExecutionPolicy Bypass -File tests/native/complete-feature-dryrun.ps1 -SourceRoot .
  ```

  Expected: the deployment-plan test fails until the new migration target is
  required by the script.

- [ ] **Step 3: Implement deployment targeting and record verified evidence**

  Add the exact generated migration ID to the deployment script’s required-ID
  list without changing confirmation, backup or rollback fences. Add a focused
  post-deployment validation script and a narrowly parameterised hook in
  `complete-feature.ps1`; the hook runs after a successful local deployment,
  rechecks loopback health, `__EFMigrationsHistory`, private worker table shape
  and disabled worker composition, then writes only its fresh sanitised evidence
  to the validation record. The closeout script commits and pushes that record
  on `main` before worktree cleanup. Record fresh pre-deployment test/build
  evidence in architecture and roadmap as part of the feature commit; record
  deployment evidence only through the post-deployment hook. Keep the worker
  disabled in deployed configuration. Do not expose or enable a model/GPU
  runner.

- [ ] **Step 4: Run the complete pre-closeout matrix**

  ```powershell
  dotnet restore FluxKnowledge.slnx --locked-mode
  dotnet build FluxKnowledge.slnx --configuration Release --no-restore -warnaserror
  dotnet test FluxKnowledge.slnx --configuration Release --no-build --logger "console;verbosity=minimal"
  powershell -NoProfile -ExecutionPolicy Bypass -File tests/native/complete-feature-dryrun.ps1 -SourceRoot .
  powershell -NoProfile -ExecutionPolicy Bypass -File tests/native/native-deployment-plan.ps1 -SourceRoot .
  git diff --check
  ```

  Expected: all enabled tests pass, the migration is targeted, and no warning or
  whitespace error remains. Diagnose any failure before closeout.

- [ ] **Step 5: Perform independent whole-branch review and authorised closeout**

  Review against the approved design: disabled default, private named pipe,
  identity attestation, SQL idempotency, uncertainty-only recovery, safe stop,
  no forbidden capability and sanitised public surfaces. Confirm the main
  checkout is clean before closeout; if unrelated user changes remain, preserve
  them and stop rather than reset, discard or overwrite them. The current user
  authorises the explicitly confirmed local migration and non-production
  loopback deployment, so after the clean-check approval run the
  repository-required closeout command:

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev/complete-feature.ps1 -ApplyMigrations -ConfirmApplyMigrations
  ```

  Capture its JSON result and deployment log. The script invokes the tested
  post-deployment validator, which verifies live loopback health, the generated
  migration in `__EFMigrationsHistory`, private worker tables and disabled
  worker composition, records only sanitised fresh evidence, commits and pushes
  it on `main`, then cleans up the feature worktree. Do not manually substitute
  any part of this closeout sequence.

## Acceptance checklist

- [ ] The worker is a compiled local deterministic executable with no model,
  GPU, source, file-payload or network capability.
- [ ] Production defaults to no worker/adapter/process; controlled tests can
  enable exactly one attested worker.
- [ ] PID plus start-time and current-user named-pipe nonce fencing reject an
  impostor, stale process, replay or incompatible protocol without mutation.
- [ ] Every worker observation is SQL-authoritative, idempotent and sanitised;
  private process data never leaks through audit, status, MCP, CLI or UI.
- [ ] Exit, timeout, pipe loss and host restart make only exact active work
  uncertain; they cannot complete, release, retry, requeue or replace it.
- [ ] Graceful idle stop and controlled forced-stop evidence are proved with a
  real child process; forced stop first preserves uncertainty.
- [ ] Migration, deployment script targeting, local loopback health and
  disabled-by-default live composition are freshly verified.
