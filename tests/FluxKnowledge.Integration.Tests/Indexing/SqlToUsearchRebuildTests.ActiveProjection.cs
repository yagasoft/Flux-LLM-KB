using System.Data.Common;
using System.Security.Cryptography;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerFact]
    public async Task Active_projection_probe_skips_inactive_audit_and_releases_the_fence_for_source_convergence()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Serving projection.");
        await CheckpointRecoveryPerformanceTests.SeedEmptyCheckpointsAsync(environment.Factory, 2);
        var root = SourceRootConfiguration.Restore(SourceRootId.New(),
            Path.Combine(Path.GetTempPath(), "synthetic-projection-source"), "projection source", true, false,
            1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = await environment.Factory.CreateDbContextAsync())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = root.Id.Value, CanonicalPath = root.CanonicalPath, DisplayName = root.DisplayName,
                State = (int)root.State, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[]", MaximumFileBytes = 1024, ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }
        var bytes = "Synthetic source captured after a health probe."u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var file = new SourceDiscoveredFile(Path.Combine(root.CanonicalPath, "entry.txt"), "entry.txt", "projection:entry",
            bytes, true, hash, bytes.Length, DateTimeOffset.UtcNow,
            new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, "text", null));
        var receipt = new SourceArtifactReceipt(SourceArtifactId.New(), hash, $"sha256\\{hash[..2]}\\{hash}.bin", bytes.Length, false);
        var observer = new ProjectionReadObserver(blockActiveRead: true);
        var writerObserver = new ProjectionReadObserver(observeFence: true);
        IDerivedIndexRecoveryStore store = new SqlDerivedIndexRecoveryStore(
            new ProjectionObservedFactory(_fixture.ConnectionString, observer), TimeProvider.System);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var probe = store.ReadActiveProjectionAsync(budget.Token).AsTask();
        Task<SourceRevisionId>? writer = null;
        try
        {
            await observer.ActiveReadEntered.Task.WaitAsync(budget.Token);
            writer = new SqlSourceScanStore(new ProjectionObservedFactory(_fixture.ConnectionString, writerObserver), TimeProvider.System)
                .ConvergeRevisionAndArtifactAsync(root, file, receipt, budget.Token).AsTask();
            await writerObserver.FenceEntered.Task.WaitAsync(budget.Token);
            Assert.False(writer.IsCompleted);
            observer.Release.TrySetResult();
            var snapshot = await probe.WaitAsync(budget.Token);
            Assert.NotEmpty(snapshot.Membership);
            Assert.False(snapshot.IsValidatedEmptyCatalogue);
            Assert.False(observer.InactiveAuditEntered);
            Assert.NotEqual(Guid.Empty, (await writer.WaitAsync(budget.Token)).Value);
        }
        finally
        {
            observer.Release.TrySetResult();
            budget.Cancel();
            await ((Task)probe).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (writer is not null) await ((Task)writer).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [NativeSqlServerFact]
    public async Task Active_projection_remains_healthy_with_unrelated_corrupt_draft_but_full_recovery_refuses_it()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Current serving document.");
        var drafts = await CheckpointRecoveryPerformanceTests.SeedEmptyCheckpointsAsync(environment.Factory, 1);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            (await context.IndexGenerations.SingleAsync(value => value.Id == drafts[0])).MetadataChecksum = new string('a', 64);
            await context.SaveChangesAsync();
        }
        IDerivedIndexRecoveryStore store = new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System);
        var projection = await store.ReadActiveProjectionAsync(CancellationToken.None);
        Assert.Equal((await environment.ActiveGenerationAsync()).Id, projection.ActiveGenerationId);
        Assert.NotEmpty(projection.Membership);
        Assert.False(projection.IsProjectionUnavailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadActiveAsync(CancellationToken.None).AsTask());
    }

    [NativeSqlServerTheory]
    [InlineData("active-file")]
    [InlineData("publication-stamp")]
    public async Task Active_projection_fault_or_stamp_drift_uses_fresh_full_recovery(string change)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Active projection recovery routing.");
        var active = await environment.ActiveGenerationAsync();
        var store = new ProjectionRoutingStore(new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System));
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot, recoveryStore: store);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.Equal(1, store.FullReads);
        if (change == "active-file")
            File.WriteAllText(Path.Combine(active.IndexPath, UsearchGenerationValidator.MetadataFileName), "{}");
        else
        {
            await using var context = await environment.Factory.CreateDbContextAsync();
            (await context.IndexState.SingleAsync()).CorpusVersion++;
            await context.SaveChangesAsync();
        }
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, store.ProjectionReads);
        Assert.True(store.FullReads >= 2);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
    }

    [NativeSqlServerFact]
    public async Task Active_projection_still_refuses_corrupt_serving_vector_payload()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Serving payload integrity.");
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var vector = await context.Vectors.SingleAsync();
            vector.Values = vector.Values.ToArray();
            vector.Values[0] ^= 1;
            context.Entry(vector).Property(value => value.Values).IsModified = true;
            await context.SaveChangesAsync();
        }
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
        Assert.Equal(DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid, coordinator.Snapshot.FailureCategory);
    }

    [NativeSqlServerFact]
    public async Task Active_projection_refuses_a_retired_active_generation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Retired serving generation.");
        var active = await environment.ActiveGenerationAsync();
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            (await context.IndexGenerations.SingleAsync(value => value.Id == active.Id)).RetiredAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }
        IDerivedIndexRecoveryStore store = new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadActiveProjectionAsync(CancellationToken.None).AsTask());
        Assert.Equal("active-index-generation-retired-or-missing", error.Message);
    }

    [NativeSqlServerFact]
    public async Task Active_projection_without_an_active_generation_retains_full_checkpoint_validation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Nonempty catalogue without a serving generation.");
        var drafts = await CheckpointRecoveryPerformanceTests.SeedEmptyCheckpointsAsync(environment.Factory, 1);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var state = await context.IndexState.SingleAsync();
            state.ActiveIndexGenerationId = null;
            state.EmptyCatalogueValidatedAtUtc = null;
            (await context.IndexGenerations.SingleAsync(value => value.Id == drafts[0])).MetadataChecksum = new string('a', 64);
            await context.SaveChangesAsync();
        }
        IDerivedIndexRecoveryStore store = new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadActiveProjectionAsync(CancellationToken.None).AsTask());
        Assert.Equal("embedding-checkpoint-draft-conflict", error.Message);
    }

    [NativeSqlServerFact]
    public async Task Active_projection_accepts_only_a_genuinely_validated_empty_catalogue()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        await using (var context = await factory.CreateDbContextAsync())
        {
            (await context.IndexState.SingleAsync()).EmptyCatalogueValidatedAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }
        IDerivedIndexRecoveryStore store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);
        var snapshot = await store.ReadActiveProjectionAsync(CancellationToken.None);
        Assert.True(snapshot.IsValidatedEmptyCatalogue);
        Assert.Null(snapshot.ActiveGenerationId);
        Assert.Empty(snapshot.Membership);
        Assert.Empty(snapshot.ReferencedGenerationIds);
        Assert.Empty(snapshot.ReferencedIndexPaths);
    }

    private sealed class ProjectionObservedFactory(string connectionString, ProjectionReadObserver observer)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString).AddInterceptors(observer).Options);
    }

    private sealed class ProjectionReadObserver(bool blockActiveRead = false, bool observeFence = false) : DbCommandInterceptor
    {
        public TaskCompletionSource ActiveReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FenceEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool InactiveAuditEntered { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (observeFence && command.CommandText.Contains("[IndexState]", StringComparison.Ordinal) &&
                command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal)) FenceEntered.TrySetResult();
            if (!observeFence && command.CommandText.Contains("FROM [Jobs]", StringComparison.Ordinal))
            {
                InactiveAuditEntered = true;
                throw new InvalidOperationException("The active-projection probe entered inactive checkpoint validation.");
            }
            if (blockActiveRead && command.CommandText.Contains("[IndexGenerationVectors]", StringComparison.Ordinal) &&
                command.CommandText.Contains("[Values]", StringComparison.Ordinal) && ActiveReadEntered.TrySetResult())
                await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class ProjectionRoutingStore(IDerivedIndexRecoveryStore inner) : IDerivedIndexRecoveryStore
    {
        public int FullReads { get; private set; }
        public int ProjectionReads { get; private set; }
        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken ct)
        {
            FullReads++;
            return inner.ReadActiveAsync(ct);
        }
        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveProjectionAsync(CancellationToken ct)
        {
            ProjectionReads++;
            return inner.ReadActiveProjectionAsync(ct);
        }
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(TimeSpan timeout, CancellationToken ct) =>
            inner.TryAcquireExclusiveLeaseAsync(timeout, ct);
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireSharedLeaseAsync(TimeSpan timeout, CancellationToken ct) =>
            inner.TryAcquireSharedLeaseAsync(timeout, ct);
        public ValueTask<bool> TryUpdateRecoveryPathAsync(Guid id, string expected, string replacement, DateTimeOffset validated, CancellationToken ct) =>
            inner.TryUpdateRecoveryPathAsync(id, expected, replacement, validated, ct);
        public ValueTask<bool> TryActivatePublicationCandidateAsync(IndexGenerationCandidateSnapshot candidate, CancellationToken ct) =>
            inner.TryActivatePublicationCandidateAsync(candidate, ct);
        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent audit, CancellationToken ct) => inner.AppendAuditAsync(audit, ct);
    }
}
