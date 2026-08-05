# Task 4 report: public safety proof and milestone evidence

## Status

**Complete and committed.** Task 4 was committed as
`0f23fb24ef502db60e8a55a6627731ac1d2555ea`
(`docs: record GPU executor boundary verification`). The required guarded native
matrix passed after minimal stale full-matrix test maintenance from earlier
approved boundary work and a focused correction to one order-sensitive
concurrency assertion. The final `git diff --check` passed before commit.

## Current changed files

- `tests/FluxKnowledge.Web.Tests/Endpoints/GpuStatusEndpointTests.cs`
- `tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs`
- `tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs`
  (stale full-matrix schema-count and executor-key test maintenance)
- `tests/FluxKnowledge.Integration.Tests/Gpu/SqlGpuAdmissionConcurrencyTests.cs`
  (legal serial-outcome assertion correction)
- `docs/architecture.md`
- `docs/roadmap.md`
- `docs/superpowers/specs/2026-08-03-phase-2-executor-result-boundary-design.md`
- this report

The endpoint's real disposable-SQL projection fixture now seeds private batch,
slot, task, dispatch, executor, receipt, trusted-verifier and 32-byte digest
values. It asserts that their identifiers and both Base64 and hexadecimal digest
representations are absent from the `GET /api/gpu-status` response. Existing
`405` mutation-verb and bodyless expected-failure `503` coverage remains.

The normal composition test invokes the scheduler registration extension twice
and asserts the resulting normal provider still resolves `NoGpuAdmissionGate`,
has exactly one `GpuExecutorDispatchRecoveryService`, and has zero
`IGpuExecutorAdapter` registrations. It does not create or test a deployed
process or adapter.

## Test evidence

1. Initial focused non-native command:

   ```powershell
   dotnet test tests\FluxKnowledge.Web.Tests\FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~GpuStatusEndpointTests|FullyQualifiedName~WebHostCompositionTests" --no-restore
   ```

   Passed **12**, failed **0**, skipped **2** guarded native-SQL tests.

2. The first guarded focused Release run reached the disposable catalogue and
   failed only because this new test's private digest marker was 33 bytes for
   the existing `varbinary(32)` column. The marker was reduced to exactly 32
   bytes; no production schema, endpoint or contract changed.

3. Guarded focused Release rerun:

   ```powershell
   $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
   try {
       dotnet test tests\FluxKnowledge.Web.Tests\FluxKnowledge.Web.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~GpuStatusEndpointTests|FullyQualifiedName~WebHostCompositionTests"
   }
   finally {
       Remove-Item Env:\FLUXKNOWLEDGE_TEST_SQL_CONNECTION -ErrorAction SilentlyContinue
   }
   ```

   Passed **14**, failed **0**, skipped **0**. The environment variable was
   removed in the same process. The fixture used generated disposable catalogues
   only.

4. Required matrix commands completed before the blocker:

   ```powershell
   dotnet restore FluxKnowledge.slnx --locked-mode
   dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
   dotnet test tests\FluxKnowledge.Domain.Tests\FluxKnowledge.Domain.Tests.csproj -c Release --no-restore
   ```

   Restore passed; Release build passed with **0 warnings, 0 errors**; Domain
   tests passed **188**, failed **0**, skipped **0**.

5. The first required guarded Integration run found two stale test-maintenance
   cases from earlier approved boundary work:

   - `NativeSchemaMigrationTests.Native_scheduler_fence_constraints_reject_trailing_whitespace`
     at `SchemaMappingTests.cs:856` expected **16**, actual **25**. Task 2 added
     exactly nine approved fences: dispatch 3, receipt 2 and evidence 4. The
     static mapping tests already cover each added fence, so the native expected
     count was updated to 25.
   - `NativeSchemaMigrationTests.Scheduler_migration_backfills_existing_task_sequence_in_creation_order_and_new_selection_stays_fifo`
     at `SchemaMappingTests.cs:1088` constructed an admitted
     `GpuAdmissionDecision` without the approved required executor key. The test
     now supplies `test-executor`; production validation was not relaxed.

   Each focused guarded native test then passed **1/1**, with zero skips.

6. Required guarded Integration command:

   ```powershell
   $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
   try {
       dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --no-restore
   }
   finally {
       Remove-Item Env:\FLUXKNOWLEDGE_TEST_SQL_CONNECTION -ErrorAction SilentlyContinue
   }
   ```

   Passed **248**, failed **0**, skipped **0**. The process-local environment
   variable was removed in `finally`; the fixture used generated disposable
   catalogues only.

7. Required guarded native Web command:

   ```powershell
   $env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
   try {
       dotnet test tests\FluxKnowledge.Web.Tests\FluxKnowledge.Web.Tests.csproj -c Release --no-restore
   }
   finally {
       Remove-Item Env:\FLUXKNOWLEDGE_TEST_SQL_CONNECTION -ErrorAction SilentlyContinue
   }
   ```

   Passed **49**, failed **0**, skipped **1** browser-only test outside the
   native-only matrix. The process-local environment variable was removed in
   `finally`.

## Static safety evidence

```powershell
git diff --check
rg -n "Process(Start|Info)?|System\.Diagnostics\.Process|HttpClient|RabbitMQ|Vespa|Docker" src\FluxKnowledge.Application\Gpu src\FluxKnowledge.Infrastructure.SqlServer\Workers
```

`git diff --check` passed. The forbidden-reference search returned no matches:
`No forbidden executor implementation references found.`

## Concurrency serial-order correction

After the first green full Integration result, a fresh guarded rerun found the
following order-sensitive failure:

```powershell
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
try {
    dotnet test tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --no-restore
}
finally {
    Remove-Item Env:\FLUXKNOWLEDGE_TEST_SQL_CONNECTION -ErrorAction SilentlyContinue
}
```

`SqlGpuAdmissionConcurrencyTests.Concurrent_callback_and_diagnostic_uncertainty_leave_only_the_uncertain_lifecycle_without_requeue_or_result_replacement`
failed an `Assert.True` at `SqlGpuAdmissionConcurrencyTests.cs:367`. Root-cause
analysis established two legal serialised outcomes under the lifecycle lock:

- diagnostics first: uncertainty commits and leaves slot/batch/dispatch
  Uncertain/CapacityUncertain/DeliveryUncertain; the stale callback is rejected;
- retained SafeBoundary first: the callback commits, refreshes the tracked
  heartbeat/rowversion, and correctly makes the stale diagnostic request return
  false while retaining Reserved/AtSafeBoundary/PendingDelivery state.

The existing sequential retained-boundary test already proves the second branch.
The concurrent test now explicitly accepts exactly those two branches and rejects
all other result combinations; it retains the common no-requeue, no-receipt,
no-evidence and active-task assertions. No production code changed.

The corrected focused native test passed **1/1**, followed by three additional
isolated **1/1** guarded runs. The final full guarded Integration rerun passed
**248/248**, and the final guarded Web run passed **49/49 native** with **one
browser-only skip** outside the native matrix.

## Self-review and closeout limitation

Reviewed the Task 4 diff against the approved public-surface and composition
acceptance criteria. The endpoint contract remains unchanged and read-only;
private executor-boundary values are seeded only in the disposable SQL fixture
and asserted absent. The normal provider retains `NoGpuAdmissionGate`, one
idempotently registered recovery service and zero production adapters. The
forbidden-reference search found no executor process, network, Docker, RabbitMQ
or Vespa path in the approved production folders.

This is a self-review, not an independent whole-branch review. No deploy, IIS
restart, migration application, external access, process/model/GPU action,
legacy action, push, merge, purge or closeout script was run.
