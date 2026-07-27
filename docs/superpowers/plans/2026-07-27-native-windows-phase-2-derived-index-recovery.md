# Native Windows Phase 2 derived-index recovery implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recover a missing or invalid active USearch generation after startup from immutable SQL membership, without an IIS restart, while keeping readiness unready until the derived index is validated.

**Architecture:** Add provider-neutral recovery state, fault classification, and bounded retry policy in Application. SQL Server supplies the active-generation snapshot, durable audit evidence, referenced-generation set, and an exclusive application lock; USearch owns safe staging, placement, quarantine, validation, cleanup, and the hosted recovery loop. Web composes SQL and derived-index health into readiness and exposes only a sanitised local status projection.

**Tech Stack:** .NET 10, ASP.NET Core hosted services and Minimal APIs, EF Core SQL Server, `sp_getapplock`, SQL Server `AuditEvents`, Cloud.Unum.USearch, Blazor Interactive Server, xUnit, Playwright.

**Status:** Approved; implementation is paused before code changes.

## Global constraints

- Keep the application loopback/local-only. Do not alter bindings, expose an external endpoint, perform a deployment action, or cut over legacy operation.
- SQL is authoritative. Never rebuild SQL from a derived directory, create a replacement active pointer, or change `IndexState.ActiveIndexGenerationId` during recovery.
- A failed recovery must not change the active pointer or its SQL-referenced derived path. A successful recovery may update only the existing generation's derived path after placement and validation succeed.
- Never delete an active or SQL-referenced generation. Delete only aged, unreferenced direct children of the app-owned `staging` or `quarantine` roots; do not follow links outside the configured USearch root.
- Invalid SQL membership/checksum, schema/configuration, and permission failures are operator-actionable. They do not enter an automatic retry loop.
- Do not add model/GPU work, scheduler lanes, full Jobs/timeline UI, external access, legacy actions, user-manual assets, or database migrations in this slice.
- Preserve public/private boundaries: status and audit evidence contain only generation IDs, safe categories, attempt counts, timing, and candidate counts—never source content, paths, credentials, or raw exception text.
- Use a dedicated `codex/` worktree before changing source or tests. Keep unrelated worktree changes untouched.

## File structure

| Path | Responsibility |
| --- | --- |
| `src/FluxKnowledge.Application/Indexing/DerivedIndexRecoveryContracts.cs` | State machine, safe failure categories, immutable snapshots, retry policy, and recovery signal/status contracts. |
| `src/FluxKnowledge.Application/Ports/IDerivedIndexRecoveryStore.cs` | Provider-neutral SQL recovery snapshot, application-lock lease, and sanitised audit port. |
| `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlDerivedIndexRecoveryStore.cs` | EF/SqlClient implementation of active membership reads, `sp_getapplock`, referenced IDs, and `AuditEvents` writes. |
| `src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexRecoveryOptions.cs` | Validated local-only probe, retry, staging-retention, and quarantine-retention options. |
| `src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexFileSystem.cs` | Root-confined validation, unique placement, post-success quarantine, and safe cleanup. |
| `src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexRecoveryCoordinator.cs` | One recovery episode: classify, lock, re-read SQL, rebuild, audit, publish status, and transition state. |
| `src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexRecoveryService.cs` | Hosted startup, periodic probing, and fault-channel processing without blocking web-host startup. |
| `src/FluxKnowledge.Web/Endpoints/IndexHealthEndpoints.cs` | Read-only `/api/index-health` projection. |
| `src/FluxKnowledge.Web/Components/Status/SqlProjectionReader.cs` | Compose recovery status into the overview projection. |
| `src/FluxKnowledge.Web/Components/Pages/Overview.razor` | Render local recovery state without raw paths or details. |
| `tests/FluxKnowledge.Domain.Tests/Indexing/DerivedIndexRecoveryPolicyTests.cs` | Deterministic state and backoff rules. |
| `tests/FluxKnowledge.Integration.Tests/Indexing/DerivedIndexRecoveryIntegrationTests.cs` | SQL lock, membership, safe replacement, retry, terminal classification, and cleanup proof. |
| `tests/FluxKnowledge.Web.Tests/Endpoints/HealthEndpointTests.cs` | Composite SQL-plus-derived-index readiness behaviour. |
| `tests/FluxKnowledge.Web.Tests/Endpoints/IndexHealthEndpointTests.cs` | Sanitised local recovery status contract. |
| `tests/FluxKnowledge.Web.Tests/Components/OverviewProjectionTests.cs` | Overview projection reload and recovery visibility. |

## Task 1: Define recovery contracts and bounded policy

**Files:**
- Create: `src/FluxKnowledge.Application/Indexing/DerivedIndexRecoveryContracts.cs`
- Create: `src/FluxKnowledge.Application/Ports/IDerivedIndexRecoveryStore.cs`
- Test: `tests/FluxKnowledge.Domain.Tests/Indexing/DerivedIndexRecoveryPolicyTests.cs`

**Interfaces:**
- Consumes: `IndexGenerationDescriptor` and `CanonicalVector` from `IIndexGenerationStore.cs`.
- Produces: `DerivedIndexRecoverySnapshot`, `DerivedIndexRecoveryFault`, `DerivedIndexRecoveryDecision`, `DerivedIndexRecoveryAuditEvent`, `IDerivedIndexRecoveryStatus`, `IDerivedIndexRecoverySignal`, `IDerivedIndexRecoveryStore`, and `DerivedIndexRecoveryPolicy` for SQL, USearch, and Web.

- [ ] **Step 1: Write the failing policy tests**

```csharp
[Theory]
[InlineData(1, 2)]
[InlineData(2, 5)]
[InlineData(3, 15)]
[InlineData(4, 30)]
public void Recoverable_failure_schedules_the_configured_bounded_delay(
    int failedAttemptCount, int seconds)
{
    var decision = DerivedIndexRecoveryPolicy.Decide(
        DerivedIndexRecoveryFailureCategory.TransientIo,
        failedAttemptCount);

    Assert.True(decision.ShouldRetry);
    Assert.Equal(TimeSpan.FromSeconds(seconds), decision.Delay);
}

[Fact]
public void Invalid_sql_membership_requires_operator_action_without_retry()
{
    var decision = DerivedIndexRecoveryPolicy.Decide(
        DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid,
        failedAttemptCount: 1);

    Assert.False(decision.ShouldRetry);
    Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, decision.NextState);
}
```

- [ ] **Step 2: Run the policy test to verify it fails**

Run:

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~DerivedIndexRecoveryPolicyTests
```

Expected: fail because the recovery contracts and policy do not exist.

- [ ] **Step 3: Add provider-neutral contracts and policy**

```csharp
public enum DerivedIndexRecoveryState
{
    Starting, Healthy, Recovering, RetryScheduled, OperatorActionRequired
}

public enum DerivedIndexRecoveryFailureCategory
{
    None, MissingDerivedIndex, InvalidDerivedIndex, TransientIo,
    SqlMembershipInvalid, SqlSchemaInvalid, ConfigurationInvalid,
    PermissionsDenied, RetryExhausted
}

public static class DerivedIndexRecoveryPolicy
{
    public static DerivedIndexRecoveryDecision Decide(
        DerivedIndexRecoveryFailureCategory category, int failedAttemptCount)
    {
        if (category is DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid or
            DerivedIndexRecoveryFailureCategory.SqlSchemaInvalid or
            DerivedIndexRecoveryFailureCategory.ConfigurationInvalid or
            DerivedIndexRecoveryFailureCategory.PermissionsDenied)
        {
            return new(false, null, DerivedIndexRecoveryState.OperatorActionRequired, category);
        }

        var delay = failedAttemptCount switch
        {
            1 => TimeSpan.FromSeconds(2),
            2 => TimeSpan.FromSeconds(5),
            3 => TimeSpan.FromSeconds(15),
            4 => TimeSpan.FromSeconds(30),
            _ => null
        };
        return delay is { } retryDelay
            ? new(true, retryDelay, DerivedIndexRecoveryState.RetryScheduled, category)
            : new(false, null, DerivedIndexRecoveryState.OperatorActionRequired,
                DerivedIndexRecoveryFailureCategory.RetryExhausted);
    }
}
```

Define `DerivedIndexRecoverySqlSnapshot` as the active pointer ID, descriptor,
immutable membership, and referenced generation IDs. Define an
`IDerivedIndexRecoveryLease : IAsyncDisposable` and this exact store operation:

```csharp
ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
    TimeSpan lockTimeout,
    CancellationToken cancellationToken);
```

The store also reads the snapshot and appends a sanitised audit event. Keep the
contracts free of SQL, filesystem, IIS, or exception-text types.

- [ ] **Step 4: Run focused tests to verify the policy passes**

Run:

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~DerivedIndexRecoveryPolicyTests
```

Expected: all recovery-policy tests pass, including retry exhaustion after the
fifth failed attempt and no retry for membership, schema, configuration, and
permission categories.

- [ ] **Step 5: Commit the contract batch**

```powershell
git add src/FluxKnowledge.Application/Indexing/DerivedIndexRecoveryContracts.cs src/FluxKnowledge.Application/Ports/IDerivedIndexRecoveryStore.cs tests/FluxKnowledge.Domain.Tests/Indexing/DerivedIndexRecoveryPolicyTests.cs
git commit -m "feat: define derived index recovery contracts"
```

## Task 2: Provide SQL recovery snapshots, evidence, and cross-process locking

**Files:**
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlDerivedIndexRecoveryStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/ServiceCollectionExtensions.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Indexing/DerivedIndexRecoveryIntegrationTests.cs`

**Interfaces:**
- Consumes: `IDbContextFactory<FluxKnowledgeDbContext>`, `TimeProvider`, and the Task 1 recovery store contract.
- Produces: `IDerivedIndexRecoveryStore` backed by immutable membership reads, `AuditEvents`, and a session-scoped SQL application lock named `FluxKnowledge.DerivedIndexRecovery`.

- [ ] **Step 1: Write failing SQL integration tests**

```csharp
[NativeSqlServerFact]
public async Task Exclusive_recovery_lease_allows_only_one_holder()
{
    var first = await _store.TryAcquireExclusiveLeaseAsync(
        TimeSpan.Zero, CancellationToken.None);
    Assert.NotNull(first);
    await using var heldLease = first!;
    var second = await _otherStore.TryAcquireExclusiveLeaseAsync(
        TimeSpan.Zero, CancellationToken.None);

    Assert.Null(second);
}

[NativeSqlServerFact]
public async Task Recovery_audit_persists_only_safe_fields()
{
    await _store.AppendAuditAsync(
        new DerivedIndexRecoveryAuditEvent(
            "rebuild_succeeded", Guid.NewGuid(), null, 1,
            TimeSpan.FromSeconds(1), null, 0),
        CancellationToken.None);
    await using var context = await SqlTestData.CreateFactory(_fixture)
        .CreateDbContextAsync();
    var audit = await context.AuditEvents
        .OrderByDescending(item => item.Id)
        .FirstAsync();

    Assert.DoesNotContain("C:\\", audit.DetailsJson, StringComparison.Ordinal);
    Assert.DoesNotContain("secret", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run the SQL integration test to verify it fails**

Run:

```powershell
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~DerivedIndexRecoveryIntegrationTests
Remove-Item Env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION
```

Expected: fail because no recovery SQL store or application lock exists.

- [ ] **Step 3: Implement the SQL store without a migration**

Use a fresh DbContext for each read/audit write. Read `IndexState`, the active
`IndexGeneration`, ordered `IndexGenerationVectors`, joined vectors, and all
currently referenced generation IDs as no-tracking queries. Retain a dedicated
open `SqlConnection` in the lease and call `sp_getapplock` with
`@LockOwner = 'Session'`, an exclusive lock mode, and the exact resource name.
Dispose the lease by calling `sp_releaseapplock` and closing the connection.

Append a bounded JSON object to `AuditEvents` with `PipelineRecordId = null`,
event type `derived_index_recovery`, actor `DerivedIndexRecoveryService`, a
safe category, generation ID, attempts, duration, retry due time, and cleanup
counts. Do not persist a raw exception message or path.

Register `TimeProvider.System` with `TryAddSingleton` and register the new
store in `AddFluxKnowledgeSqlServer`; do not move existing outbox registrations.

- [ ] **Step 4: Run focused SQL integration tests to verify they pass**

Run the Step 2 command and then:

```powershell
sqlcmd -S localhost -E -d master -Q "SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'FluxKnowledge_Phase1Tests[_]%'"
```

Expected: recovery snapshot, lock, and sanitised audit tests pass; disposable
catalogue count returns zero after test disposal.

- [ ] **Step 5: Commit the SQL recovery batch**

```powershell
git add src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlDerivedIndexRecoveryStore.cs src/FluxKnowledge.Infrastructure.SqlServer/ServiceCollectionExtensions.cs tests/FluxKnowledge.Integration.Tests/Indexing/DerivedIndexRecoveryIntegrationTests.cs
git commit -m "feat: persist derived index recovery evidence"
```

## Task 3: Implement safe derived-index repair, cleanup, and continuous recovery

**Files:**
- Create: `src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexRecoveryOptions.cs`
- Create: `src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexFileSystem.cs`
- Create: `src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexRecoveryCoordinator.cs`
- Create: `src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexRecoveryService.cs`
- Modify: `src/FluxKnowledge.Infrastructure.Usearch/AtomicGenerationPlacement.cs`
- Modify: `src/FluxKnowledge.Infrastructure.Usearch/UsearchGenerationBuilder.cs`
- Modify: `src/FluxKnowledge.Infrastructure.Usearch/UsearchAnnIndex.cs`
- Modify: `src/FluxKnowledge.Infrastructure.Usearch/ServiceCollectionExtensions.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Indexing/DerivedIndexRecoveryIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 1 contracts, Task 2 store, `UsearchGenerationValidator`, `UsearchIndexOptions`, `IStatusEventPublisher`, and `TimeProvider`.
- Produces: a singleton recovery state/signal, an `IHostedService`, root-confined filesystem operations, and a recovery-aware ANN reader.

- [ ] **Step 1: Add failing filesystem and coordinator tests**

```csharp
[NativeSqlServerFact]
public async Task Missing_active_index_after_startup_recovers_without_changing_the_active_pointer()
{
    await using var environment = await RecoveryPipelineEnvironment.CreateAsync(_fixture);
    var before = await environment.Store.ReadActiveAsync(CancellationToken.None);
    Directory.Delete(before.Generation!.IndexPath, recursive: true);

    await environment.Coordinator.RunRecoveryCycleAsync(CancellationToken.None);

    var after = await environment.Store.ReadActiveAsync(CancellationToken.None);
    Assert.Equal(before.ActiveGenerationId, after.ActiveGenerationId);
    Assert.NotEqual(before.Generation.IndexPath, after.Generation!.IndexPath);
    Assert.Equal(DerivedIndexRecoveryState.Healthy, environment.Status.Snapshot.State);
}

[Fact]
public async Task Permission_failure_becomes_operator_actionable_without_retry()
{
    var fileSystem = new ThrowingDerivedIndexFileSystem(new UnauthorizedAccessException());
    var coordinator = CreateCoordinator(fileSystem);

    await coordinator.RunRecoveryCycleAsync(CancellationToken.None);

    Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
    Assert.Null(coordinator.Snapshot.NextRetryAtUtc);
}
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run:

```powershell
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~DerivedIndexRecoveryIntegrationTests
Remove-Item Env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION
```

Expected: fail because runtime recovery, safe unique placement, and fault classification do not exist.

- [ ] **Step 3: Implement the recovery mechanics**

Validate immutable SQL membership before filesystem mutation:

```csharp
var expectedChecksum = UsearchGenerationValidator.ComputeChecksum(
    snapshot.Generation.ModelFingerprint,
    snapshot.Generation.Dimensions,
    snapshot.Membership);
if (!string.Equals(expectedChecksum, snapshot.Generation.MetadataChecksum, StringComparison.Ordinal))
{
    return DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid;
}
```

Use `DerivedIndexRecoveryOptions` defaults of a 60-second probe interval,
24-hour staging retention, and seven-day quarantine retention. Use the exact
Task 1 retry policy: initial attempt plus retries after 2, 5, 15, and 30
seconds, then `OperatorActionRequired`.

Extend `UsearchGenerationBuilder` with a recovery method that accepts an
already-read descriptor and immutable membership. It builds and validates in
staging, atomically moves that staging directory to a new unique recovery path,
then asks the SQL store to update only `IndexGenerations.IndexPath` and
`ValidatedAtUtc`. It must not update `IndexState.ActiveIndexGenerationId`.
Only after that metadata update may `DerivedIndexFileSystem` move the old
unreferenced path into quarantine. If placement or metadata update fails, leave
the old SQL-referenced path intact and quarantine only a safely unreferenced
replacement candidate.

Make cleanup enumerate direct children only, normalise every candidate against
the canonical root, reject reparse points, require the configured age, and skip
every currently referenced generation. Never call recursive deletion on a path
that has not passed those checks.

`DerivedIndexRecoveryService` starts in the background, performs an initial
cycle, consumes ANN-reader fault signals, and runs bounded periodic probes.
Each cycle obtains the SQL lease, re-reads SQL after lock acquisition, records
sanitised evidence, and publishes `StatusChanged(null, "index-recovery", now)`.
`UsearchAnnIndex` reports missing/validation faults to the signal before
returning its normal typed failure; it must not start a parallel recovery itself.

- [ ] **Step 4: Prove all recovery invariants**

Add and pass tests for:

```text
missing active directory -> 503 state -> validated rebuild -> same active pointer -> Healthy
corrupt index/metadata -> unique replacement path -> prior path quarantined only after metadata update
recoverable transient I/O -> exact 2/5/15/30 schedule -> later success
five recoverable failures -> OperatorActionRequired with no sixth automatic attempt
invalid SQL checksum -> OperatorActionRequired with no filesystem mutation
access denied/configuration invalid -> OperatorActionRequired with no retry
old unreferenced staging/quarantine -> removed; old referenced paths -> retained
two coordinators -> one SQL-locked rebuild and one converged Healthy outcome
```

Run the focused integration test command from Step 2 and the existing rebuild
suite:

```powershell
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~DerivedIndexRecoveryIntegrationTests|FullyQualifiedName~SqlToUsearchRebuildTests'
Remove-Item Env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION
```

Expected: all selected tests pass; existing missing-root rebuild and failed
candidate pointer-preservation tests remain green.

- [ ] **Step 5: Commit the recovery-engine batch**

```powershell
git add src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexRecoveryOptions.cs src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexFileSystem.cs src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexRecoveryCoordinator.cs src/FluxKnowledge.Infrastructure.Usearch/DerivedIndexRecoveryService.cs src/FluxKnowledge.Infrastructure.Usearch/AtomicGenerationPlacement.cs src/FluxKnowledge.Infrastructure.Usearch/UsearchGenerationBuilder.cs src/FluxKnowledge.Infrastructure.Usearch/UsearchAnnIndex.cs src/FluxKnowledge.Infrastructure.Usearch/ServiceCollectionExtensions.cs tests/FluxKnowledge.Integration.Tests/Indexing/DerivedIndexRecoveryIntegrationTests.cs
git commit -m "feat: recover derived indexes after startup"
```

## Task 4: Gate readiness and expose read-only local recovery status

**Files:**
- Create: `src/FluxKnowledge.Web/Endpoints/IndexHealthEndpoints.cs`
- Modify: `src/FluxKnowledge.Web/Endpoints/HealthEndpoints.cs`
- Modify: `src/FluxKnowledge.Web/Program.cs`
- Modify: `src/FluxKnowledge.Application/Contracts/StatusContracts.cs`
- Modify: `src/FluxKnowledge.Web/Components/Status/SqlProjectionReader.cs`
- Modify: `src/FluxKnowledge.Web/Components/Pages/Overview.razor`
- Modify: `tests/FluxKnowledge.Web.Tests/Endpoints/HealthEndpointTests.cs`
- Create: `tests/FluxKnowledge.Web.Tests/Endpoints/IndexHealthEndpointTests.cs`
- Modify: `tests/FluxKnowledge.Web.Tests/Components/OverviewProjectionTests.cs`
- Test: `tests/FluxKnowledge.Web.Tests/Browser/PhaseOneVerticalSliceBrowserTests.cs`

**Interfaces:**
- Consumes: `ISqlServerReadinessValidator`, `IDerivedIndexRecoveryStatus`, and `DerivedIndexRecoverySnapshot`.
- Produces: composite `/health/ready`, read-only `/api/index-health`, status-feed invalidations, and an overview-safe recovery summary.

- [ ] **Step 1: Write failing web and component tests**

```csharp
[Theory]
[InlineData(true, DerivedIndexRecoveryState.Healthy, HttpStatusCode.OK)]
[InlineData(true, DerivedIndexRecoveryState.Recovering, HttpStatusCode.ServiceUnavailable)]
[InlineData(false, DerivedIndexRecoveryState.Healthy, HttpStatusCode.ServiceUnavailable)]
public async Task Ready_requires_both_sql_and_derived_index_health(
    bool sqlReady,
    DerivedIndexRecoveryState recoveryState,
    HttpStatusCode expectedStatus)
{
    await using var application = await CreateApplicationAsync(sqlReady, recoveryState);
    var response = await application.GetTestClient().GetAsync("/health/ready");

    Assert.Equal(expectedStatus, response.StatusCode);
}

[Fact]
public async Task Index_health_returns_safe_recovery_fields_only()
{
    var response = await _client.GetFromJsonAsync<IndexRecoveryProjection>("/api/index-health");
    Assert.Equal(DerivedIndexRecoveryState.RetryScheduled.ToString(), response!.State);
    Assert.DoesNotContain("C:\\", JsonSerializer.Serialize(response), StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run web tests to verify they fail**

Run:

```powershell
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter 'FullyQualifiedName~HealthEndpointTests|FullyQualifiedName~IndexHealthEndpointTests|FullyQualifiedName~OverviewProjectionTests'
```

Expected: fail because health only consults SQL and no index-health route or
recovery projection exists.

- [ ] **Step 3: Implement composite health and status projection**

Keep `ISqlServerReadinessValidator` unchanged. Make `HealthEndpoints.ReadyAsync`
return 503 unless SQL readiness succeeds and the recovery snapshot is `Healthy`.
Add `MapFluxKnowledgeIndexHealth()` in `Program.cs` and return a response shaped
like this:

```csharp
public sealed record IndexRecoveryProjection(
    string State,
    string? ActiveGeneration,
    DateTimeOffset? LastCompletedAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    string? FailureCategory,
    int CleanedCandidateCount);
```

Inject `IDerivedIndexRecoveryStatus` into `SqlProjectionReader`, extend
`OverviewProjection` with a safe recovery summary, and render state, retry time,
and safe category in `Overview.razor`. Reuse the existing status feed; the
overview reloads on `index-recovery` and reconnect events. Do not add a repair
button or a mutating HTTP endpoint.

- [ ] **Step 4: Run focused web and browser checks to verify they pass**

Run the Step 2 command, then:

```powershell
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
$env:FLUXKNOWLEDGE_BROWSER_TESTS = '1'
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter Category=Browser
Remove-Item Env:FLUXKNOWLEDGE_BROWSER_TESTS, Env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION
```

Expected: composite readiness returns the correct status, index-health is
sanitised and read-only, the overview renders recovery status, and the existing
browser vertical slice remains green.

- [ ] **Step 5: Commit the local visibility batch**

```powershell
git add src/FluxKnowledge.Web/Endpoints/IndexHealthEndpoints.cs src/FluxKnowledge.Web/Endpoints/HealthEndpoints.cs src/FluxKnowledge.Web/Program.cs src/FluxKnowledge.Application/Contracts/StatusContracts.cs src/FluxKnowledge.Web/Components/Status/SqlProjectionReader.cs src/FluxKnowledge.Web/Components/Pages/Overview.razor tests/FluxKnowledge.Web.Tests/Endpoints/HealthEndpointTests.cs tests/FluxKnowledge.Web.Tests/Endpoints/IndexHealthEndpointTests.cs tests/FluxKnowledge.Web.Tests/Components/OverviewProjectionTests.cs tests/FluxKnowledge.Web.Tests/Browser/PhaseOneVerticalSliceBrowserTests.cs
git commit -m "feat: expose local derived index recovery status"
```

## Task 5: Whole-slice verification, review, and documentation

**Files:**
- Modify: `docs/operations/native-windows-phase-1-validation.md`
- Modify: `docs/roadmap.md`
- Test: `FluxKnowledge.slnx`

**Interfaces:**
- Consumes: completed Tasks 1–4 and the approved recovery design.
- Produces: verified local-only Phase 2 recovery evidence and updated roadmap status without a deployment change.

- [ ] **Step 1: Run the narrow recovery matrix**

Run:

```powershell
dotnet restore FluxKnowledge.slnx --locked-mode --nologo
dotnet build FluxKnowledge.slnx -c Release --no-restore --nologo
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
dotnet test FluxKnowledge.slnx -c Release --no-build --no-restore --filter 'Category!=Browser' --nologo
Remove-Item Env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION
```

Expected: locked restore, zero-warning build, all non-browser suites, including
the disposable SQL recovery matrix, pass.

- [ ] **Step 2: Run browser and static checks**

Run:

```powershell
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
$env:FLUXKNOWLEDGE_BROWSER_TESTS = '1'
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --no-build --no-restore --filter Category=Browser --nologo
Remove-Item Env:FLUXKNOWLEDGE_BROWSER_TESTS, Env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION
git diff --check
```

Expected: browser slice passes and no whitespace errors exist.

- [ ] **Step 3: Perform the mandated scope and invariant review**

Review the final diff against the approved design with these explicit checks:

```text
SQL remains the source of truth; recovery does not alter IndexState.ActiveIndexGenerationId.
Failed recovery leaves the SQL-referenced path intact.
Only post-success metadata updates change the current generation path.
Cleanup cannot escape the USearch root or touch referenced directories.
Retry is finite and excludes membership, schema/configuration, and permission faults.
No scheduler, GPU/model, external-access, legacy, or deployment change entered the diff.
```

Expected: no Critical or Important findings. Address any finding with a focused
test and correction before closeout.

- [ ] **Step 4: Update durable documentation without claiming an unrun deployment**

Record only command output actually run, test counts, local recovery evidence,
known residual risks, and unchanged loopback-only status. Update the Phase 2
roadmap row's Progress % and Remaining Work based on delivered capability; do
not claim a live IIS checkpoint unless the user separately authorises and the
checkpoint is actually run.

- [ ] **Step 5: Commit the verified slice**

```powershell
git add docs/operations/native-windows-phase-1-validation.md docs/roadmap.md
git commit -m "docs: record Phase 2 recovery verification"
```

## Self-review

- **Spec coverage:** Tasks 1–3 cover continuous recovery, bounded retry, SQL authority, safe placement, post-success path update, cleanup, lock serialisation, and operator-actionable failures. Task 4 covers 503-to-200 readiness gating, sanitised audit/status exposure, and local UI visibility. Task 5 covers the full verification and documentation boundary.
- **Placeholder scan:** The plan contains concrete files, interfaces, test names, expected outcomes, commands, retry values, cleanup values, and commits. It contains no unresolved implementation marker.
- **Type consistency:** `DerivedIndexRecoverySnapshot`, `DerivedIndexRecoveryFailureCategory`, `DerivedIndexRecoveryDecision`, `DerivedIndexRecoveryAuditEvent`, `IDerivedIndexRecoveryStatus`, `IDerivedIndexRecoverySignal`, `IDerivedIndexRecoveryStore`, and `IndexRecoveryProjection` are defined once and used consistently by later tasks.

## Execution handoff

Implementation is intentionally paused before code changes. When the user lifts
that pause, use `superpowers:using-git-worktrees`, create a dedicated
`codex/` worktree, then use `superpowers:executing-plans` to execute Tasks 1–5
in order. Do not perform a deployment action under this plan.
