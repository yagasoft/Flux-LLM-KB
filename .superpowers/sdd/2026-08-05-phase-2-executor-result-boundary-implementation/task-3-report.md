# Task 3 report: durable GPU executor dispatch recovery

## Status

Implemented and committed as Task 3 only. The final commit SHA is reported in the task handoff; a committed file cannot safely self-reference the hash that changes when its own SHA field is edited.

## Changed files

- `src/FluxKnowledge.Infrastructure.SqlServer/Workers/ChannelGpuExecutorDispatchSignal.cs`
- `src/FluxKnowledge.Infrastructure.SqlServer/Workers/GpuExecutorDispatchRecoveryService.cs`
- `src/FluxKnowledge.Infrastructure.SqlServer/Workers/GpuSchedulerServiceCollectionExtensions.cs`
- `tests/FluxKnowledge.Integration.Tests/Gpu/DeterministicFakeGpuExecutor.cs`
- `tests/FluxKnowledge.Integration.Tests/Gpu/DeterministicFakeGpuExecutorLifecycleTests.cs`
- `tests/FluxKnowledge.Integration.Tests/Workers/GpuExecutorDispatchRecoveryServiceTests.cs`
- `tests/FluxKnowledge.Domain.Tests/Pipeline/OutboxWorkerRegistrationTests.cs`
- `tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs`

## Delivery and safety design

- `ChannelGpuExecutorDispatchSignal` is a bounded capacity-one, payload-free coalescing prompt. It carries no handle and is not a queue.
- `GpuExecutorDispatchRecoveryService` reads only `PendingDelivery` handles on startup, a local prompt, and the bounded missed-prompt fallback. It uses the injected `TimeProvider` for both fallback and an adapter-delivery bound.
- A pass can deliver only the original stored complete handle to an ordinal-exact adapter key. No matching adapter leaves the row pending.
- Adapter error, cancellation, local delivery timeout, shutdown, and prompt loss leave the durable store untouched. The service has no scheduler, admission gate, callback, lifecycle sink, capacity, task, result, or scheduler-wake dependency.
- The only concrete adapter is `DeterministicFakeGpuExecutor` under the integration-test project. It has no direct persistence, process, runtime, GPU, file, network, or model API; it can acknowledge through the private lifecycle sink, drop delivery, or remain responsive only to cancellation.
- Normal composition registers the signal, SQL dispatch-store boundary, lifecycle coordinator, and hosted recovery service. Production resolves zero `IGpuExecutorAdapter` registrations and still uses `NoGpuAdmissionGate` as its default Busy gate.

## TDD evidence

1. Red: the first recovery test command failed as expected because `GpuExecutorDispatchRecoveryService` and `ChannelGpuExecutorDispatchSignal` did not exist (`CS0246`).
2. Green: after the minimal signal, recovery service, and registration implementation, the recovery suite passed 7/7.
3. Red: the deterministic fake acknowledgement test initially failed as expected because the fake and its mode did not exist (`CS0246`/`CS0103`).
4. Green: the recovery suite passed 9/9 after adding the test-only fake.
5. Red: the bounded-unresponsive-delivery test failed as expected before cancellation observation existed (`CS1061` for the missing test-only cancellation signal).
6. Green: after adding the local bounded delivery cancellation path, the recovery suite passed 10/10.

## Verification

- `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~GpuExecutorDispatchRecoveryServiceTests" --no-restore`
  - Passed: 10; failed: 0; skipped: 0.
- `dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~OutboxWorkerRegistrationTests" --no-restore`
  - Passed: 3; failed: 0; skipped: 0.
- `dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~WebHostCompositionTests" --no-restore`
  - Passed: 6; failed: 0; skipped: 0. It resolves the signal, dispatch store, lifecycle sink and recovery hosted service while asserting zero production adapters.
- `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~GpuExecutorDispatchRecoveryServiceTests|FullyQualifiedName~GpuSchedulerServiceTests|FullyQualifiedName~SqlGpuExecutorDispatchTests" --no-restore`
  - Passed: 32; failed: 0; skipped: 13. Skips are existing native-SQL fixture tests because no guarded disposable SQL fixture was configured in this run; no live/application database was used.
- `dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror`
  - Passed with 0 warnings and 0 errors.
- `git diff --check`
  - Passed.

## Native SQL validation addendum

- `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` was set only for the command process to the approved disposable-test server connection and removed in that process's `finally` block.
- `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~GpuExecutorDispatchRecoveryServiceTests|FullyQualifiedName~GpuSchedulerServiceTests|FullyQualifiedName~SqlGpuExecutorDispatchTests" --no-restore`
  - Passed: 45; failed: 0; skipped: 0. The guarded fixture created only disposable test catalogues; no application database, deployment, restart, or production action was used.

## Self-review and concerns

- Reviewed the recovery source and its dependency graph: it has no production executor adapter and no code path to mutate scheduler capacity, task/result state, admission, scheduler wake state, or dispatch handle.
- The elapsed-time test proves the fallback and delivery timeout only cause a reread or local cancellation; neither invokes a lifecycle mutation or store mutation.
- The original focused-filter skip was due solely to the absent process-scoped test connection. The native Release addendum above executed the full focused filter with zero skips, including the existing durable callback, duplicate, late and mismatched-fence SQL coverage.
- No migration, deployment, restart, external access, process management, push, merge, or closeout script was run.

## Fix round 1: scripted fake lifecycle and durable elapsed-time proof

- Extended the test-only fake with typed immutable script steps for an exact supplied acknowledgement, delivery uncertainty, result receipt, trusted evidence, and batch callback. Each step uses only `IGpuExecutorLifecycleSink`; the fake still has no persistence/store, process, file, network, runtime, model, or GPU dependency.
- Added native fake-to-sink tests covering acknowledgement replay, duplicate immutable receipt rejection, mismatched receipt and callback rejection, safe-boundary, completion, late callback and receipt rejection, delivery-uncertainty replay, and trusted-evidence replay. Every assertion reads real disposable-SQL snapshots rather than invoking lifecycle storage directly after setup.
- Added a native deterministic-clock scheduler test that advances the bounded fallback, a pre-threshold heartbeat age, and the diagnostic/heartbeat threshold. It retains a durable receipt and verifies that elapsed time alone keeps one admitted batch, active task, ready concurrent task, reserved capacity, parent GPU-processing state, and immutable receipt unchanged; only the explicit stale diagnostic changes the dispatch/batch/slot to their uncertainty states.

### Fix round 1 red/green evidence

1. Red: the new typed fake lifecycle tests failed to compile because the script-step types, scripted constructor, and script-result surface did not exist (`CS0246`, `CS0103`, `CS7036`, and `CS1061`).
2. Green candidate: the first native run executed 3/4 typed-fake tests. One expectation was red: the test expected parent `Completed`, while SQL returned state value 3.
3. Root-cause check: the existing native `Completed_fenced_callback_completes_only_its_task_releases_slot_and_preserves_parent_gpu_processing` test and `PublicJobState` enum establish that value 3 is intentionally `GpuProcessing`; executor completion must not complete the parent Job. The test expectation, not production code, was corrected.
4. Green: the typed fake plus durable-clock filter passed 5/5 under the guarded disposable SQL fixture.
5. Final focused native Release command:
   `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~GpuExecutorDispatchRecoveryServiceTests|FullyQualifiedName~GpuSchedulerServiceTests|FullyQualifiedName~SqlGpuExecutorDispatchTests|FullyQualifiedName~DeterministicFakeGpuExecutorLifecycleTests" --no-restore`
   - Passed: 50; failed: 0; skipped: 0.
6. Relevant Release composition checks passed: `OutboxWorkerRegistrationTests` 3/3 and `WebHostCompositionTests` 6/6. `dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror` passed with 0 warnings and 0 errors; `git diff --check` passed.

## Fix round 2: immutable result receipt clock evidence

- The deterministic-clock durable snapshot now includes the accepted receipt's operation ID, dispatch/batch/mini-task fence, executor key, admission generation, disposition, evidence class, exact hexadecimal opaque-digest representation, canonical request fingerprint, and creation timestamp.
- Snapshot equality is asserted after the fallback advance, pre-threshold heartbeat advance, and explicit diagnostic threshold. The receipt row count remains asserted, but cannot now mask replacement of an accepted receipt with different immutable content.

### Fix round 2 red/green evidence

1. Red: the focused deterministic-clock test failed to compile as expected because `ElapsedTimeSnapshot` had no `AcceptedReceipt` property (`CS1061`).
2. Green: after adding the immutable accepted-receipt snapshot, the guarded native deterministic-clock test passed 1/1.
3. The guarded Release recovery/service/SQL/fake focus passed 50/50, 0 failed, 0 skipped. `git diff --check` passed.

## Whole-branch remediation: persisted SQL restart proof

- Added a guarded disposable native-SQL test class that creates the recovery host through `AddFluxKnowledgeGpuScheduler`, resolves the actual hosted `GpuExecutorDispatchRecoveryService`, and asserts the scoped `IGpuExecutorDispatchStore` is `SqlGpuSchedulerStore`.
- A test-only recording adapter is used only at the executor boundary. A SQL command observer confirms that each recreated hosted service actually reads `GpuExecutorDispatches` through the real store; no in-memory dispatch-store substitute is used.
- The pending-dispatch case starts an ordinal-matching host, receives the exact persisted handle, completes its recovery pass, then stops and disposes without acknowledgement. A fresh DI provider and SQL store configuration against the same generated catalogue receives the same unchanged handle after recreation. Full row snapshots prove no batch, slot, task, dispatch, operation receipt, result receipt, evidence, heartbeat, or lifecycle mutation.
- A two-dispatch case records an immutable receipt with a non-empty opaque digest and trusted evidence, moves a second dispatch to `DeliveryUncertain` with evidence, and asserts the exact seeded durable states and identities before recreating two matching-adapter hosts. Each pass completes before asserting neither non-pending dispatch is delivered; snapshots preserve result/evidence identity and content as well as all scheduler relations.

### Whole-branch remediation red/green and verification

1. Red: the new test skeleton failed to compile before scenario/provider/snapshot/adapter helpers existed (`CS0103` and `CS0246`).
2. The first guarded native green candidate exposed a test-fixture admission setup error: a one-byte batch cap rejected a ten-byte fixture task. The test-only cap was corrected to 100 bytes; production code was unchanged.
3. Guarded focused Release test:
   `dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~SqlGpuExecutorDispatchRecoveryServiceTests" --no-restore`
   - Passed: 2; failed: 0; skipped: 0.
4. Guarded combined Integration Release suite:
   `dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --no-restore`
   - Passed: 252; failed: 0; skipped: 0.
5. `dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror`
   - Passed with 0 warnings and 0 errors.
6. Every guarded native command used only the process-scoped generated-test connection and removed `FLUXKNOWLEDGE_TEST_SQL_CONNECTION` in a `finally` block. No application database, process/IIS restart, deployment, migration, GPU/model operation, route change, or fake production adapter was used.

## Whole-branch remediation fix round 1: completed-pass restart proof

- The pending-dispatch restart test now uses an ordinal-matching adapter for its first provider, receives the exact persisted handle once, waits for the recovery pass to complete, then stops/disposes without acknowledgement. A fresh provider and `SqlGpuSchedulerStore` configuration against the same generated catalogue receives that exact unchanged handle once after recreation.
- The same-process fallback redelivery was removed from the restart proof. Each provider's injected `ManualTimeProvider` instead observes the recovery service scheduling its fallback timer, which occurs only after the durable read and adapter loop complete.
- The non-pending test now uses matching adapters and completed-pass timer observation for both recreated providers before asserting no delivery. It also asserts before provider startup that exactly two dispatches exist; the receipt handle is `ReceiptRecorded`, the uncertain handle is `DeliveryUncertain`, there is one immutable result receipt with the fixed 32-byte digest, two exact evidence rows, and seven lifecycle operation receipts.

### Fix round 1 red/green and verification

1. Red: the revised test failed to compile before the completed-pass and pre-provider durable-state helpers existed (`CS0103`).
2. Green: the guarded focused native test passed 2/2, zero failed and zero skipped after adding those test-only helpers.
