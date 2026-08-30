# Native clean-slate go-live execution implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver one explicitly authorised, native-only clean-slate go-live operation that provisions and validates the empty application at `I:\\FluxKnowledge` through `scripts/dev/complete-feature.ps1`.

**Architecture:** A pure C# control plane defines authority, journal, marker grammar and no-follow contracts. Private Windows adapters and one in-process PowerShell module perform IIS, VSS, SQL and Codex lifecycle only after merged-main verification. Normal Web, REST, MCP, CLI and diagnostics remain incapable of creating a production root.

**Tech Stack:** .NET 10/C#, EF Core SQL Server, Windows handle-relative filesystem APIs, VSS COM, IIS administration, PowerShell, xUnit and disposable SQL Server integration tests.

**Spec:** `docs/superpowers/specs/2026-08-26-native-go-live-execution-design.md`

## Global constraints

- Work only in `codex/native-mcp-live-contract`; preserve unrelated changes and never run `dotnet format`.
- This is native-only. Do not add legacy compatibility, backfill, staged-squash recovery, credential bridges, custom Git executors or manual closeout.
- The only production mutation route is `scripts/dev/complete-feature.ps1 -GoLive` after reviewed local merge and merged-main verification. Direct scripts and normal product surfaces cannot initialise state.
- The only runtime tree is `I:\\FluxKnowledge` with `App`, `Config`, `Data\\Sql\\Data`, `Data\\Sql\\Log`, `Data\\Index`, `Data\\Retained`, `Runtime\\Spool`, `Runtime\\Temp`, `Runtime\\Logs`, `CodexPlugin` and `Recovery`. SQL paths are `Data\\Sql\\Data\\FluxKnowledge.mdf` and `Data\\Sql\\Log\\FluxKnowledge_log.ldf`.
- The only markerless adoption layout is preliminary `Sql` plus `OutlookSpool` with the exact descendants in the spec. It is a one-time clean-slate guard, never a runtime compatibility layer.
- Accept bootstrap SQL only from unlogged `FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP`, with loopback integrated authentication. Reject passwords/SQL authentication; do not log, serialise or pass the connection string as an argument.
- Configure unencrypted VSS diff-area storage on `I:` at exactly 10% via VSS COM. Do not create/restore snapshots, encrypt anything, control via `vssadmin` output or touch another volume.
- Data-protection keys are intentionally unencrypted and live only in `Config\\data-protection` behind narrow ACLs. Do not emit keys, secrets or connection strings.
- Do not introduce source-original paths, cloud/network parsing, model download/activation, GPU work, OCR/vision/ASR, embeddings, FFmpeg, Outlook activation or UI work.
- Register only `fluxknowledge` using `codex plugin marketplace add I:\\FluxKnowledge\\CodexPlugin` then bounded `marketplace list --json` verification. Never use `codex plugin add`, plugin activation, Git marketplaces, Python installer or Flux.
- For every production behaviour write a focused failing test, witness RED, implement minimally, run named GREEN checks and commit. Use one serial implementation subagent per task, an independent task review and a scoped fix/re-review before moving on.
- Do not run a live operation, VSS mutation, IIS change, non-disposable SQL migration, marketplace registration, deploy, push, merge or `complete-feature.ps1` until fresh explicit user authority after all implementation and review.

## File structure

- `src/FluxKnowledge.Application/Operations/NativeGoLive/*` — plan, authority, journal/marker grammar and pure root/VSS rules.
- `src/FluxKnowledge.Integrations/Windows/NativeGoLive/*` — executor, typed host ports, journal/lock and handle-relative filesystem/VSS adapters.
- `src/FluxKnowledge.Infrastructure.SqlServer/*` — empty-catalogue marker, migration, bootstrap/readiness and first-publish transition.
- `src/FluxKnowledge.Web/Configuration/*` — no-follow production configuration and intentionally unencrypted shared key ring.
- `src/FluxKnowledge.Integrations/Codex/*` — go-live-only local marketplace writer/registrar.
- `scripts/deploy/native-go-live.psm1` and `scripts/deploy/update-native-windows.ps1` — guarded Windows host operations.
- `scripts/dev/complete-feature.ps1` — only closeout/orchestration route.
- `tests/FluxKnowledge.*.Tests/*NativeGoLive*` and `tests/native/*` — focused domain, disposable-SQL, Web and PowerShell evidence.

---

### Task 1: Define the native go-live control plane and marker grammar

**Files:**
- Create: `src/FluxKnowledge.Application/Operations/NativeGoLive/NativeGoLivePlan.cs`
- Create: `src/FluxKnowledge.Application/Operations/NativeGoLive/NativeGoLiveAuthority.cs`
- Create: `src/FluxKnowledge.Application/Operations/NativeGoLive/NativeGoLiveJournal.cs`
- Create: `src/FluxKnowledge.Application/Operations/NativeGoLive/NativeGoLiveRootMarker.cs`
- Modify: `src/FluxKnowledge.Application/Operations/LiveRootLayout.cs`, `src/FluxKnowledge.Application/Operations/FreshStartPlan.cs` and `src/FluxKnowledge.Integrations/Windows/VssRecoveryPolicy.cs`
- Test: `tests/FluxKnowledge.Domain.Tests/Operations/NativeGoLivePlanTests.cs`, `NativeGoLiveAuthorityTests.cs` and `NativeGoLiveRootMarkerTests.cs`

**Interfaces:**
- Produce `NativeGoLivePlan.CreateProduction(string committedSha)` with literals `I:\\FluxKnowledge`, `FluxKnowledge`, `fluxknowledge`, port `5137`, VSS `0.10m` and 30-minute authority.
- Produce `NativeGoLiveAuthorityIssuer.Issue` / `TryClaim`: an authority binds execution ID, SHA and plan hash, transitions once `Issued -> Claimed -> Completed|Failed`, expires in 30 minutes.
- Produce `NativeGoLiveJournal` and `NativeGoLiveAdoptionState` values `None`, `AdoptionRecorded`, `RecoveryCreated`, `AdoptedMarkerPending`, `AdoptedMarkerDurable`, `CatalogueDropPending`, `CatalogueDropped`, `OutlookSpoolDeletePending`, `OutlookSpoolDeleted`, `SqlDeletePending`, `SqlDeleted`, `CanonicalMarkerPending` and `CanonicalMarkerDurable`.
- Produce `NativeGoLiveRootMarker.Validate` accepting only canonical `Complete`/`Incomplete` trees or exact spec-defined journal-bound preliminary pairs.

- [ ] **Step 1: Write RED tests for immutable plan and authority.**

```csharp
[Fact]
public void Production_plan_has_only_approved_native_literals()
{
    var plan = NativeGoLivePlan.CreateProduction(new string('a', 40));
    Assert.Equal(@"I:\FluxKnowledge", plan.Layout.Root);
    Assert.Equal(0.10m, plan.Vss.MaximumStorageFraction);
    Assert.Equal(TimeSpan.FromMinutes(30), plan.AuthorityLifetime);
}

[Fact]
public void Authority_cannot_be_claimed_twice_or_after_expiry()
{
    var authority = issuer.Issue(plan, now.AddMinutes(30));
    Assert.True(issuer.TryClaim(authority, out _));
    Assert.False(issuer.TryClaim(authority, out var reason));
    Assert.Equal("go-live-authority-consumed", reason);
}
```

- [ ] **Step 2: Witness RED.**

Run: `dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLivePlanTests|FullyQualifiedName~NativeGoLiveAuthorityTests`

Expected: failure because native go-live plan/authority types do not exist.

- [ ] **Step 3: Write RED tests for every owner-marker grammar boundary.**

```csharp
[Theory]
[MemberData(nameof(AdoptedCrashPrefixes))]
public void Adoption_accepts_only_the_journal_bound_prefix(NativeGoLiveAdoptionState state, RootShape shape)
    => Assert.True(NativeGoLiveRootMarker.Validate(Adopted(state, shape)).IsValid);

[Fact]
public void Sql_missing_before_sql_delete_pending_is_rejected()
    => Assert.False(NativeGoLiveRootMarker.Validate(
        Adopted(NativeGoLiveAdoptionState.OutlookSpoolDeleted, RootShape.RecoveryOnly)).IsValid);
```

Include all pre/post pairs for journal-temp flush/replacement, catalogue drop, Outlook deletion, SQL deletion and normal-Incomplete marker transition. Assert no foreign child, reparse, mismatched SHA/plan hash or partial catalogue/file pair is accepted.

- [ ] **Step 4: Implement the smallest pure model.**

```csharp
public enum NativeGoLiveMarkerState { Incomplete, AdoptedPreliminary, Complete }

public sealed record NativeGoLivePlan(
    LiveRootLayout Layout, NativeGoLiveSqlIdentity Sql, NativeGoLiveVssPolicy Vss,
    NativeGoLiveCodexIdentity Codex, string CommittedSha, string PlanHash,
    TimeSpan AuthorityLifetime);

public sealed record NativeGoLiveSqlIdentity(string CatalogName, string DataFilePath, string LogFilePath);
public sealed record NativeGoLiveVssPolicy(string Volume, decimal MaximumStorageFraction);
public sealed record NativeGoLiveCodexIdentity(string MarketplaceRoot, string MarketplaceName, string PluginName);

public sealed record NativeGoLiveRootMarker(
    string Product, NativeGoLiveMarkerState State, Guid ExecutionId,
    string CommittedSha, string PlanHash);
```

Make the old fresh-start plan diagnostic-only and unable to issue a production executor. Replace command-shaped VSS planning with a pure VSS policy; do not expose an operational CLI route.

- [ ] **Step 5: Run GREEN and commit.**

Run: `dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLive`

```powershell
git add src/FluxKnowledge.Application/Operations src/FluxKnowledge.Integrations/Windows/VssRecoveryPolicy.cs tests/FluxKnowledge.Domain.Tests/Operations
git commit -m "feat: define native go-live control plane"
```

### Task 2: Execute the clean-slate state machine against deterministic host ports

**Files:**
- Create: `src/FluxKnowledge.Integrations/Windows/NativeGoLive/NativeGoLiveExecutor.cs`, `NativeGoLivePorts.cs` and `NativeGoLiveResult.cs`
- Modify: `src/FluxKnowledge.Integrations/Windows/FreshStartExecutor.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Operations/NativeGoLiveExecutorTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Operations/FreshStartExecutorTests.cs`

**Interfaces:**
- Consume Task 1 models.
- Produce `NativeGoLiveExecutor.ExecuteAsync(NativeGoLiveRequest, INativeGoLiveHost, CancellationToken)`.
- `INativeGoLiveHost` contains only typed `AcquireLeaseAsync`, `ReadJournalAsync`, `CompareAndSwapJournalAsync`, `PreflightAsync`, `StopPoolAsync`, `RestorePoolAsync`, `ConfigureVssAsync`, `DestroyOwnedStateAsync`, `ProvisionEmptyCatalogueAsync`, `PublishAndStartAsync`, `ValidateAsync` and `RegisterMarketplaceAsync`.

```csharp
public sealed record NativeGoLiveRequest(
    NativeGoLivePlan Plan, bool PlanOnly, bool ConfirmCleanSlate,
    bool ConfirmConfigureVss, bool ConfirmDestroySql, bool ConfirmRegisterCodex);

public interface INativeGoLiveHost
{
    ValueTask<INativeGoLiveLease> AcquireLeaseAsync(NativeGoLiveRequest request, CancellationToken cancellationToken);
    ValueTask<NativeGoLiveJournal?> ReadJournalAsync(CancellationToken cancellationToken);
    ValueTask<NativeGoLiveJournal> CompareAndSwapJournalAsync(NativeGoLiveJournal next, CancellationToken cancellationToken);
    ValueTask PreflightAsync(NativeGoLivePlan plan, CancellationToken cancellationToken);
    ValueTask<bool> StopPoolAsync(CancellationToken cancellationToken);
    ValueTask RestorePoolAsync(CancellationToken cancellationToken);
    ValueTask ConfigureVssAsync(NativeGoLiveVssPolicy policy, CancellationToken cancellationToken);
    ValueTask DestroyOwnedStateAsync(NativeGoLiveJournal journal, CancellationToken cancellationToken);
    ValueTask ProvisionEmptyCatalogueAsync(NativeGoLiveSqlIdentity sql, CancellationToken cancellationToken);
    ValueTask PublishAndStartAsync(NativeGoLivePlan plan, CancellationToken cancellationToken);
    ValueTask ValidateAsync(NativeGoLivePlan plan, CancellationToken cancellationToken);
    ValueTask RegisterMarketplaceAsync(NativeGoLiveCodexIdentity codex, CancellationToken cancellationToken);
}

public interface INativeGoLiveLease : IAsyncDisposable { }
```

- [ ] **Step 1: Write RED tests for no-I/O plan-only and acknowledgements.**

```csharp
[Fact]
public async Task Plan_only_performs_no_host_operation()
{
    var host = new RecordingNativeGoLiveHost();
    var result = await executor.ExecuteAsync(NativeGoLiveRequest.PlanOnly(plan), host);
    Assert.True(result.Succeeded);
    Assert.Empty(host.Calls);
}

[Fact]
public async Task Missing_sql_acknowledgement_fails_before_authority_issue()
{
    var result = await executor.ExecuteAsync(ConfirmedRequest() with { ConfirmDestroySql = false }, host);
    Assert.Equal("go-live-acknowledgement-required", result.ReasonCode);
    Assert.Empty(host.Mutations);
}
```

- [ ] **Step 2: Witness RED.**

Run: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLiveExecutorTests`

- [ ] **Step 3: Add RED tests for lease, cancellation and phase order.**

```csharp
[Fact]
public async Task Cancellation_before_vss_restores_only_a_previously_running_pool()
{
    host.CancelAfter(NativeGoLiveHostStep.PoolStopped);
    var result = await executor.ExecuteAsync(ConfirmedRequest(), host, cancellationToken);
    Assert.Equal("go-live-cancelled-before-vss", result.ReasonCode);
    Assert.Equal(["stop-pool", "restore-pool"], host.Mutations);
}

[Fact]
public async Task Second_contender_loses_stable_lease_without_mutation()
{
    var first = executor.ExecuteAsync(ConfirmedRequest(), sharedHost);
    await sharedHost.WaitForLeaseAsync();
    var second = await executor.ExecuteAsync(ConfirmedRequest(), sharedHost);
    Assert.Equal("go-live-lease-unavailable", second.ReasonCode);
    await first;
}
```

- [ ] **Step 4: Implement strict fake-host orchestration.**

```csharp
await using var lease = await host.AcquireLeaseAsync(request, cancellationToken);
var journal = await host.CompareAndSwapJournalAsync(NativeGoLiveJournal.Initial(request), cancellationToken);
await host.PreflightAsync(plan, cancellationToken);
await host.StopPoolAsync(cancellationToken);
await host.ConfigureVssAsync(plan.Vss, cancellationToken);
await host.DestroyOwnedStateAsync(journal, cancellationToken);
await host.ProvisionEmptyCatalogueAsync(plan.Sql, cancellationToken);
await host.PublishAndStartAsync(plan, cancellationToken);
await host.ValidateAsync(plan, cancellationToken);
await host.RegisterMarketplaceAsync(plan.Codex, cancellationToken);
```

The executor alone sequences host calls. Require stable lease plus journal CAS for every transition. Before VSS restore the originally running pool on cancellation/failure; after VSS record incomplete and keep the pool stopped until new host validation succeeds.

- [ ] **Step 5: Add RED/GREEN crash-resume tests.**

```csharp
[Theory]
[InlineData(NativeGoLiveFaultPoint.JournalTempFlushed)]
[InlineData(NativeGoLiveFaultPoint.OutlookSpoolDeleted)]
[InlineData(NativeGoLiveFaultPoint.SqlDeleted)]
[InlineData(NativeGoLiveFaultPoint.CanonicalMarkerReplaced)]
public async Task Fresh_authority_resumes_only_matching_prefix(NativeGoLiveFaultPoint point)
{
    await RunWithInjectedCrashAsync(point);
    var retry = await executor.ExecuteAsync(ConfirmedRetry(), host);
    Assert.True(retry.Succeeded, retry.ReasonCode);
    Assert.True(host.AllMutationsMatchExpectedPrefix);
}
```

Run: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~FreshStartExecutorTests|FullyQualifiedName~NativeGoLiveExecutorTests`

- [ ] **Step 6: Commit.**

```powershell
git add src/FluxKnowledge.Integrations/Windows tests/FluxKnowledge.Integration.Tests/Operations
git commit -m "feat: add deterministic native go-live executor"
```

### Task 3: Persist and prove explicit empty-catalogue SQL readiness

**Files:**
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/IndexStateEntity.cs`, `Configurations/CanonicalSchemaConfigurations.cs`, `Provisioning/SqlServerReadinessValidator.cs`, `Persistence/SqlDerivedIndexRecoveryStore.cs`, `Persistence/SqlStageTransitionStore.cs` and `Provisioning/SqlServerProvisioner.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/EmptyCatalogueBootstrapper.cs`
- Create: EF migration named `AddEmptyCatalogueReadiness` plus generated designer; update `FluxKnowledgeDbContextModelSnapshot.cs`.
- Modify: `src/FluxKnowledge.Cli/Program.cs`
- Delete: `src/FluxKnowledge.Cli/Commands/ProvisionSqlCommand.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Indexing/EmptyCatalogueReadinessIntegrationTests.cs`, `Persistence/SchemaMappingTests.cs` and `tests/FluxKnowledge.Domain.Tests/Configuration/SqlServerProvisionerTests.cs`

**Interfaces:**
- Add nullable `DateTimeOffset? EmptyCatalogueValidatedAtUtc` to `IndexStateEntity`.
- Produce `EmptyCatalogueBootstrapper.ProveAndMarkAsync(FluxKnowledgeDbContext, CancellationToken)`.
- Strict readiness accepts either a valid active generation or exact empty marker with zero vectors, generations and memberships; ordinary first index publication clears the marker atomically.
- Normal CLI has no `provision-sql` dispatch; private go-live composition is the only real provisioner constructor path.

- [ ] **Step 1: Write RED disposable-SQL tests.**

```csharp
[Fact]
public async Task Empty_catalogue_is_ready_without_a_usearch_file()
{
    await bootstrapper.ProveAndMarkAsync(context, CancellationToken.None);
    var state = await context.IndexState.SingleAsync();
    Assert.NotNull(state.EmptyCatalogueValidatedAtUtc);
    Assert.Null(state.ActiveIndexGenerationId);
    Assert.True((await validator.ValidateAsync(connection)).IsReady);
}

[Theory]
[InlineData(1, 0, 0)]
[InlineData(0, 1, 0)]
[InlineData(0, 0, 1)]
public async Task Empty_marker_with_nonempty_state_is_unavailable(int vectors, int generations, int memberships) { }
```

- [ ] **Step 2: Witness RED.**

Run: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~EmptyCatalogueReadinessIntegrationTests`

- [ ] **Step 3: Implement migration, transaction and readiness.**

```csharp
await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
if (await context.Vectors.AnyAsync(cancellationToken) ||
    await context.IndexGenerations.AnyAsync(cancellationToken) ||
    await context.IndexGenerationVectors.AnyAsync(cancellationToken))
    throw new InvalidOperationException("empty-catalogue-state-not-empty");
state.ActiveIndexGenerationId = null;
state.EmptyCatalogueValidatedAtUtc = timeProvider.GetUtcNow();
await context.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

Add a database constraint preventing marker plus active generation. In the same transaction that validates and activates the first normal generation clear the marker. Do not create a vector, USearch artefact or source record.

- [ ] **Step 4: Add and run RED/GREEN transition/surface tests.**

```csharp
[Fact]
public async Task First_validated_generation_clears_empty_marker_atomically() { }

[Fact]
public async Task Normal_cli_usage_does_not_advertise_or_dispatch_provision_sql() { }
```

Run: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~EmptyCatalogueReadinessIntegrationTests|FullyQualifiedName~SchemaMappingTests`

- [ ] **Step 5: Verify the EF model and commit.**

Run: `dotnet ef migrations has-pending-model-changes --project src/FluxKnowledge.Infrastructure.SqlServer --startup-project src/FluxKnowledge.Web`

```powershell
git add src/FluxKnowledge.Infrastructure.SqlServer src/FluxKnowledge.Cli tests/FluxKnowledge.Domain.Tests tests/FluxKnowledge.Integration.Tests
git commit -m "feat: prove empty native catalogue readiness"
```

### Task 4: Compose only canonical, unencrypted private-PC runtime configuration

**Files:**
- Create: `src/FluxKnowledge.Web/Configuration/NoFollowJsonConfigurationProvider.cs` and `NativeGoLiveRuntimeOptions.cs`
- Modify: `src/FluxKnowledge.Web/WebHostComposition.cs`, `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/PrivatePcDataProtectionProviderFactory.cs`, `src/FluxKnowledge.Integrations/Files/LocalIngressOptionsValidator.cs` and `src/FluxKnowledge.Web/appsettings.json`
- Test: `tests/FluxKnowledge.Web.Tests/Configuration/NoFollowJsonConfigurationProviderTests.cs`, `Composition/WebHostCompositionTests.cs` and `tests/FluxKnowledge.Integration.Tests/Sources/LocalRetainedCsharpCodeReaderIntegrationTests.cs`

**Interfaces:**
- Produce `NoFollowJsonConfigurationProvider.LoadCanonicalProduction(string, INoFollowPathOpener)` for only `I:\\FluxKnowledge\\Config\\appsettings.Production.json` in live composition.
- Produce `NativeGoLiveRuntimeOptions.ValidateEffective(NativeGoLiveRuntimeConfiguration)` safe reason codes for source roots, Outlook, worker/model/GPU, OCR/vision/ASR, FFmpeg and network parsing.
- Data protection writes unencrypted keys only in validated `Config\\data-protection`; Web is writer/rotator and CLI opens an existing ring read-only.

```csharp
public interface INoFollowPathOpener
{
    Stream OpenRead(string canonicalPath);
    string ValidateDirectory(string canonicalPath);
}

public sealed record NativeGoLiveRuntimeConfiguration(
    IReadOnlyList<string> SourceRoots, LocalIngressOptions LocalIngress,
    bool OutlookEnabled, bool WorkerEnabled, bool ModelRuntimeEnabled,
    bool GpuEnabled, bool OcrEnabled, bool VisionEnabled, bool AsrEnabled,
    bool FfmpegEnabled, bool NetworkParsingEnabled);
```

- [ ] **Step 1: Write RED Web tests.**

```csharp
[Fact]
public void Production_provider_rejects_reparse_config_before_opening_json()
{
    var opener = new RecordingNoFollowPathOpener(reparseAt: @"I:\FluxKnowledge\Config");
    Assert.Throws<InvalidOperationException>(() =>
        NoFollowJsonConfigurationProvider.LoadCanonicalProduction(path, opener));
    Assert.Equal(0, opener.FileOpenCount);
}

[Fact]
public void Production_options_have_empty_catalogue_and_inert_retained_ingress()
{
    Assert.Empty(options.SourceRoots);
    Assert.Equal([@"I:\FluxKnowledge\Data\Retained"], options.LocalIngress.AllowedRoots);
}
```

- [ ] **Step 2: Witness RED.**

Run: `dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter FullyQualifiedName~NoFollowJsonConfigurationProviderTests|FullyQualifiedName~WebHostCompositionTests`

- [ ] **Step 3: Implement canonical composition without an encryption provider.**

```csharp
var provider = DataProtectionProvider.Create(new DirectoryInfo(validatedKeyRing), options =>
{
    options.SetApplicationName("FluxKnowledge.Native");
    // No ProtectKeysWithDpapi and no replacement encryption provider.
});
```

Load no old deployment configuration. Enforce narrow ACLs: app-pool Modify only on key subtree, current operator Read, owner/Administrators/SYSTEM control. Validate effective services, not configuration text, and ensure no source-original root is accepted.

- [ ] **Step 4: Add/run RED/GREEN cross-token and Phase 6 exclusion tests.**

```csharp
[Fact]
public void Shared_key_ring_round_trips_between_app_pool_writer_and_cli_reader() { }

[Theory]
[InlineData("Outlook:Enabled", "outlook-active")]
[InlineData("Runtime:GpuEnabled", "phase-6-runtime-active")]
public void Go_live_options_reject_disabled_capability_activation(string key, string reason) { }
```

Run: `dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --filter FullyQualifiedName~NoFollowJsonConfigurationProviderTests|FullyQualifiedName~WebHostCompositionTests`

- [ ] **Step 5: Commit.**

```powershell
git add src/FluxKnowledge.Web src/FluxKnowledge.Infrastructure.SqlServer/Persistence src/FluxKnowledge.Integrations/Files tests/FluxKnowledge.Web.Tests tests/FluxKnowledge.Integration.Tests/Sources
git commit -m "feat: compose canonical native go-live runtime"
```

### Task 5: Implement safe filesystem/journal and narrow Codex marketplace adapters

**Files:**
- Create: `src/FluxKnowledge.Integrations/Windows/NativeGoLive/HandleRelativeNativeFileSystem.cs`, `NativeGoLiveJournalStore.cs` and `NativeGoLiveLease.cs`
- Modify: `src/FluxKnowledge.Integrations/Codex/NativeCodexPluginManifestWriter.cs`, `NativeCodexMarketplaceLifecycle.cs` and `CodexRegistrationPaths.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Operations/NativeGoLiveJournalStoreTests.cs` and `HandleRelativeNativeFileSystemTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Codex/NativeCodexPluginRegistrarTests.cs`

**Interfaces:**
- Produce internal handle-relative `OpenDirectory`, `CreateDirectory`, `ReplaceFile`, `DeleteLiteralChild` and `MoveLiteralChild` operations requiring a verified parent handle and expected identity.
- Produce `NativeGoLiveJournalStore.CompareAndSwapAsync(NativeGoLiveJournal expected, NativeGoLiveJournal next, CancellationToken cancellationToken)` under `Global\\FluxKnowledge.NativeGoLive.v1` plus a stable ignored `native-go-live.lock`. Never hold the replaceable journal open during replacement.
- Produce a go-live-only marketplace registrar that uses the shared no-follow writer and only `marketplace add` followed by bounded `list --json`.

- [ ] **Step 1: Write RED swap/CAS tests.**

```csharp
[Fact]
public async Task Swap_between_validation_and_delete_is_rejected_without_delete()
{
    await fileSystem.InjectSwapBefore(NativeFileOperation.DeleteLiteralChild);
    var result = await fileSystem.DeleteLiteralChildAsync(parent, "OutlookSpool", expectedIdentity);
    Assert.Equal("file-identity-changed", result.Reason);
}

[Fact]
public async Task Journal_replace_flushes_then_reopens_and_verifies_under_stable_lock()
{
    Assert.True((await store.CompareAndSwapAsync(expected, next)).Changed);
    Assert.Equal(next, await store.ReadAsync());
}
```

- [ ] **Step 2: Witness RED.**

Run: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLiveJournalStoreTests|FullyQualifiedName~HandleRelativeNativeFileSystemTests`

- [ ] **Step 3: Implement exact safe primitives.**

```csharp
await using var lease = await NativeGoLiveLease.AcquireAsync(globalMutexName, lockFile, cancellationToken);
var actual = await ReadAndValidateJournalAsync(cancellationToken);
if (actual.ExecutionId != expected.ExecutionId)
    return JournalMutation.Conflict("journal-execution-mismatch");
await WriteFlushReplaceAndVerifyAsync(next, cancellationToken);
```

Use Windows reparse-point/handle-relative semantics on all segments. Reject unknown temporary files, foreign child names and identity changes. Do not use wildcard/recursive deletion.

- [ ] **Step 4: Write RED/GREEN marketplace tests and implement.**

```csharp
[Fact]
public async Task Marketplace_uses_only_add_then_bounded_list_verification()
{
    var result = await adapter.RegisterAsync(identity, CancellationToken.None);
    Assert.Equal(["codex plugin marketplace add", "codex plugin marketplace list --json"], runner.Commands);
    Assert.True(result.IsHealthy);
}

[Fact]
public async Task Foreign_marketplace_fails_before_writer_or_process_mutation() { }
```

Treat an exact existing source as no-op, prove unrelated configuration structural hashes unchanged without keeping its content, and remove the separately consumable marketplace authority in favour of Task 2’s authority.

- [ ] **Step 5: Run GREEN and commit.**

Run: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLiveJournalStoreTests|FullyQualifiedName~HandleRelativeNativeFileSystemTests|FullyQualifiedName~NativeCodexPluginRegistrarTests`

```powershell
git add src/FluxKnowledge.Integrations/Windows/NativeGoLive src/FluxKnowledge.Integrations/Codex tests/FluxKnowledge.Integration.Tests
git commit -m "feat: harden native go-live storage and marketplace"
```

### Task 6: Implement the guarded Windows host lifecycle

**Files:**
- Create: `scripts/deploy/native-go-live.psm1` and `src/FluxKnowledge.Integrations/Windows/NativeGoLive/VssDiffAreaAdministration.cs`
- Modify: `scripts/deploy/update-native-windows.ps1` and `src/FluxKnowledge.Integrations/Windows/NativeGoLive/NativeGoLivePorts.cs`
- Create: `tests/native/native-go-live-contract.ps1` and `tests/FluxKnowledge.Integration.Tests/Operations/NativeGoLiveHostLifecycleTests.cs`
- Modify: `tests/native/native-deployment-plan.ps1` and `tests/native/phase-5-deployment-safety.ps1`

**Interfaces:**
- `Invoke-NativeGoLive -Request <internal typed request>` is module-private and callable only by Task 7’s closeout process.
- `update-native-windows.ps1 -PlanOnly` is read-only; direct `-GoLive` without claimed in-process authority refuses.
- Typed VSS states are `ExactExisting`, `SupportedAbsent`, `ForeignAssociation`, `Unsupported`, `Failed` and `Interrupted`.

```csharp
public enum VssAssociationState { ExactExisting, SupportedAbsent, ForeignAssociation, Unsupported, Failed, Interrupted }
public sealed record VssDiffAreaState(VssAssociationState State, string SourceVolumeId, string StorageVolumeId, ulong? MaximumBytes);
```

- [ ] **Step 1: Write RED PowerShell boundary tests.**

```powershell
$plan = & $deploymentScript -PlanOnly
Assert-True ($plan.executionAvailable -eq $false) 'PlanOnly must not expose execution.'
Assert-True ($plan.root -eq 'I:\FluxKnowledge') 'Plan must use canonical root.'
Assert-Throws { & $deploymentScript -GoLive } 'Direct execution must be refused.'
Assert-NotContains (Get-Content -Raw $deploymentScript) 'vssadmin resize shadowstorage'
```

- [ ] **Step 2: Witness RED.**

Run: `pwsh -NoProfile -File tests/native/native-go-live-contract.ps1 -SourceRoot .`

- [ ] **Step 3: Implement no-mutation preflight and typed VSS choice.**

```powershell
function Test-NativeGoLivePreflight {
    param([NativeGoLiveRequest]$Request)
    Assert-CanonicalIisBinding -SiteName 'FluxKnowledge' -Port 5137
    Assert-LocalIntegratedSqlBootstrap -Connection $env:FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP
    Assert-CanonicalRootAndAncestors -Root 'I:\FluxKnowledge'
    Assert-DisabledPhaseSixAndOutlook
    Get-NativeVssDiffAreaState -Volume 'I:'
}
```

Query VSS COM by the I: volume GUID. Before VSS reject foreign/non-loopback IIS and SQL, non-native payload, invalid SQL service/login privileges, foreign marketplace, malformed bootstrap, noncanonical root, unsupported/foreign VSS, or enabled Outlook/Phase 6.
Preflight must prove Full-Text; the bootstrap principal can create/drop only the
canonical catalogue and manage only the fixed app-pool login; an existing login
has the current SID, no server role/sysadmin/DDL grant; and the SQL service can
write only canonical Data/Log while the app pool has no MDF/LDF access.

- [ ] **Step 4: Write RED lifecycle tests and implement irreversible phases.**

```csharp
[Theory]
[InlineData(VssAssociationState.ExactExisting, "ChangeDiffAreaMaximumSize")]
[InlineData(VssAssociationState.SupportedAbsent, "AddDiffArea")]
public async Task Vss_uses_exact_i_volume_action(VssAssociationState state, string action) { }

[Fact]
public async Task Marketplace_is_reached_only_after_healthy_loopback_validation() { }
```

```powershell
Stop-NativeGoLivePool -Name 'FluxKnowledge'
Set-NativeVssDiffArea -Volume 'I:' -MaximumBytes $exactTenPercent
Invoke-NativeGoLiveOwnedStateDestruction -Journal $journal
Invoke-NativeGoLiveSqlBootstrap -IntegratedConnection $bootstrap
Publish-NativeGoLiveApp -SourceRoot $mergedMain -Destination 'I:\FluxKnowledge\App'
Start-NativeGoLivePool -Name 'FluxKnowledge'
Test-NativeGoLiveLoopback -BaseUri 'http://127.0.0.1:5137'
Register-NativeGoLiveMarketplace
```

Clear bootstrap environment before publish/probes/Codex. Enforce narrow ACLs, migrate/prove empty readiness, require health/readiness/index/MCP/REST empty-query success and Forwarded/non-loopback denial, then register marketplace after validation. Emit bounded safe facts only.
The exact checks are HTTP 200 from §/health/live§, §/health/ready§ and
§/api/index-health§; a zero-work §/api/gpu-status§; an empty successful
§POST /api/v1/knowledge/search§ with synthetic bounded input; and HTTP MCP
initialise/tools-list advertising exactly §knowledge.search§, §knowledge.write§,
§knowledge.graph§, §code.query§, §code.write§, §corpus.query§,
§corpus.write§, §operations.status§ and §operations.audit§. The published
payload hash must equal the journal-bound merged-main SHA.

- [ ] **Step 5: Run GREEN and commit.**

Run: `pwsh -NoProfile -File tests/native/native-go-live-contract.ps1 -SourceRoot .`

Run: `dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter FullyQualifiedName~NativeGoLiveHostLifecycleTests`

```powershell
git add scripts/deploy src/FluxKnowledge.Integrations/Windows/NativeGoLive tests/native tests/FluxKnowledge.Integration.Tests/Operations
git commit -m "feat: implement guarded native Windows go-live host"
```

### Task 7: Route go-live through feature closeout and record verified intent

**Files:**
- Modify: `scripts/dev/complete-feature.ps1`
- Modify: `tests/native/complete-feature-dryrun.ps1`, `tests/native/native-deployment-plan.ps1` and `tests/native/outlook-scheduled-host-contract.ps1`
- Modify after green verification only: `docs/architecture.md` and `docs/roadmap.md`

**Interfaces:**
- Add `-GoLive`, `-ConfirmCleanSlate`, `-ConfirmConfigureVss`, `-ConfirmDestroySql` and `-ConfirmRegisterCodex`. Every incomplete acknowledgement set fails.
- Call the Task 6 module in-process after squash merge, merged-main restore/build/test and main commit, before the only final `git push origin main`.
- An exact ignored `Completed` journal retries only pending push/cleanup; no post-live validation-record commit exists.

- [ ] **Step 1: Write RED closeout contract tests.**

```powershell
Assert-True ($text -match '\[switch\]\$GoLive') 'Closeout must require GoLive.'
Assert-True ($text -match 'Invoke-NativeGoLive') 'GoLive must be in-process, not Invoke-FeatureStep.'
Assert-True ($text.IndexOf('Invoke-NativeGoLive') -lt $text.IndexOf('push-main')) 'GoLive must precede push.'
Assert-False ($text -match 'BackupRoot') 'No backup-root contract may remain.'
Assert-False ($text -match 'post-deploy-validation-record-commit') 'Live evidence must not create a second commit.'
```

- [ ] **Step 2: Witness RED.**

Run: `pwsh -NoProfile -File tests/native/complete-feature-dryrun.ps1 -SourceRoot .`

- [ ] **Step 3: Implement closeout order and completed-journal retry.**

```powershell
if ($GoLive) {
    Assert-NativeGoLiveAcknowledgements
    # Direct module invocation in this process; never Invoke-FeatureStep.
    Invoke-NativeGoLive -MergedMainRoot $MainRoot -CommittedSha $headSha -Acknowledgements $acknowledgements
}
Invoke-FeatureStep -Name 'push-main' -Cwd $MainRoot -Command 'git push origin main'
```

Remove `DeployRoot`/`BackupRoot` and obsolete Outlook/worker deployment validation from this contract. Never forward SQL bootstrap through a child command environment or log it.

- [ ] **Step 4: Add/run RED/GREEN retry and normal-surface tests.**

```powershell
Assert-Throws { & $closeout -GoLive -ConfirmCleanSlate } 'Every acknowledgement is required.'
Assert-True (Test-CompletedJournalRetryOnlyPushAndCleanup) 'Completed rerun makes zero host calls.'
Assert-False (Test-CliContainsCommand -Name 'provision-sql') 'Normal CLI cannot initialise SQL.'
```

Run: `pwsh -NoProfile -File tests/native/complete-feature-dryrun.ps1 -SourceRoot .`

Run: `pwsh -NoProfile -File tests/native/native-deployment-plan.ps1 -SourceRoot .`

- [ ] **Step 5: Update docs after code/tests are green and commit.**

Update architecture with the native-only guarded go-live boundary. Update roadmap with verified implementation state, remaining explicit live-authorisation gate and no Phase 6 activation. Do not modify dashboard manuals/screenshots/rendered manuals.

```powershell
git add scripts/dev/complete-feature.ps1 tests/native docs/architecture.md docs/roadmap.md
git commit -m "feat: gate native go-live through feature closeout"
```

## Whole-slice verification and review

After Task 7, the Subagent-Driven workflow must obtain one fresh independent whole-slice review using the spec, this plan, final diff package and task ledger. The reviewer must assess retained-only/native-only input, absence of source-original paths, preflight/package identity, no-follow protections, stable lock/journal CAS, preliminary-root crash recovery, VSS 10% no-encryption, SQL identity/empty readiness, transaction/fencing/idempotency, cancellation/supersession, marketplace restrictions, privacy and Phase 6 exclusions.

Before requesting live authority, preserve fresh output from:

```powershell
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-build
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --no-build
dotnet test FluxKnowledge.slnx -c Release --no-build --logger "console;verbosity=minimal"
dotnet ef migrations has-pending-model-changes --project src/FluxKnowledge.Infrastructure.SqlServer --startup-project src/FluxKnowledge.Web
pwsh -NoProfile -File tests/native/complete-feature-dryrun.ps1 -SourceRoot .
pwsh -NoProfile -File tests/native/native-go-live-contract.ps1 -SourceRoot .
```

No browser validation is needed because there is no UI change. Do not perform the actual clean-slate run, merge, push or closeout until the user grants separate explicit authority. When that authority exists, use `scripts/dev/complete-feature.ps1` only; never substitute a manual sequence.
