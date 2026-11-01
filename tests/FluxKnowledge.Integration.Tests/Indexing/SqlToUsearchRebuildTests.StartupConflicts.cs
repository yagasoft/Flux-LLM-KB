using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_native_validation_defers_stamp_drift_and_preserves_a_newer_fault(bool newerFault)
    {
        var checkpoint = new AfterStartupLease();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The startup keeper remains available.", segmented: true,
            configureServices: services => services.AddScoped<ICorpusGenerationLeaseStore>(provider =>
                new StartupLeaseStore(new SqlCorpusGenerationLeaseStore(provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
                    TimeProvider.System), checkpoint)));
        var removed = await environment.AddRetainedAndPumpAsync("The source disappears after the startup query lease is committed.");
        await using var db = await environment.Factory.CreateDbContextAsync();
        var root = await (from record in db.PipelineRecords join source in db.SourceRevisions on record.SourceRevisionId equals source.Id
            where record.Id == removed select source.SourceRootId).SingleAsync();
        var pointer = await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync();
        var auditCount = await db.AuditEvents.CountAsync(value => value.EventType == "derived_index_recovery");
        var coordinator = environment.Services.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        checkpoint.Mutate = async token =>
        {
            await new SqlSourceScanStore(environment.Factory, TimeProvider.System).SuppressUnseenAsync(new SourceRootId(root),
                new HashSet<SourceRevisionId>(), token);
            if (newerFault) coordinator.Notify(new(DerivedIndexRecoveryFailureCategory.MissingDerivedIndex, pointer));
        };
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await coordinator.RunOnceAsync(budget.Token);
        Assert.True(checkpoint.Mutated);
        Assert.Equal(newerFault ? DerivedIndexRecoveryState.Recovering : DerivedIndexRecoveryState.IndexUpdating, coordinator.Snapshot.State);
        if (newerFault) Assert.Equal(DerivedIndexRecoveryFailureCategory.MissingDerivedIndex, coordinator.Snapshot.FailureCategory);
        else
        {
            Assert.True(coordinator.Snapshot.IsProjectionUnavailable);
            Assert.Null(coordinator.Snapshot.FailureCategory);
            Assert.Null(coordinator.Snapshot.NextRetryAtUtc);
        }
        Assert.Equal(pointer, await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        Assert.Equal(auditCount, await db.AuditEvents.CountAsync(value => value.EventType == "derived_index_recovery"));
        Assert.False(await db.CorpusQueryLeases.AnyAsync());

        await coordinator.RunOnceAsync(budget.Token);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.False(await db.CurrentIndexMembers.AnyAsync(value => value.SourceRootId == root));
        Assert.False(await db.CorpusQueryLeases.AnyAsync());
        await AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task Startup_refresh_preparation_conflict_is_abandoned_and_retried_from_fresh_state()
    {
        var append = new SegmentedSelectionScaleTests.MetadataAppendObserver();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The startup refresh keeper.", segmented: true,
            configureServices: services => services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                new SegmentedSelectionScaleTests.MetadataFactory(_fixture.ConnectionString, append)));
        var removed = await environment.AddRetainedAndPumpAsync("A source removal invalidates startup refresh capture.");
        var (root, _) = await AddRootDebtAsync(environment, removed);
        var coordinator = environment.Services.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        append.Arm();
        var probing = coordinator.RunOnceAsync(budget.Token).AsTask();
        try
        {
            if (await Task.WhenAny(append.Entered.Task, probing) == probing) await probing;
            await append.Entered.Task.WaitAsync(budget.Token);
            await new SqlSourceScanStore(environment.Factory, TimeProvider.System).SuppressUnseenAsync(new SourceRootId(root),
                new HashSet<SourceRevisionId>(), budget.Token);
        }
        finally { append.Release.TrySetResult(); }
        await probing;
        Assert.Equal(DerivedIndexRecoveryState.IndexUpdating, coordinator.Snapshot.State);
        Assert.True(coordinator.Snapshot.IsProjectionUnavailable);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var abandoned = await db.IndexPublicationCandidates.OrderByDescending(value => value.CreatedAtUtc).FirstAsync();
        Assert.Equal(3, abandoned.State);
        Assert.False(await db.IndexCandidateMembers.AnyAsync(value => value.CandidateId == abandoned.Id));
        await coordinator.RunOnceAsync(budget.Token);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        await AssertSegmentedOracleAsync(environment);
    }

    private sealed class AfterStartupLease
    {
        public Func<CancellationToken, Task>? Mutate { get; set; }
        public bool Mutated { get; set; }
    }

    private sealed class StartupLeaseStore(ICorpusGenerationLeaseStore inner, AfterStartupLease checkpoint) : ICorpusGenerationLeaseStore
    {
        public async ValueTask<ICorpusGenerationLease?> TryAcquireAsync(Guid ownerInstanceId, GpuInteractiveOwnerIdentity owner,
            string modelFingerprint, int dimensions, CancellationToken cancellationToken)
        {
            var lease = await inner.TryAcquireAsync(ownerInstanceId, owner, modelFingerprint, dimensions, cancellationToken);
            if (lease is not null && checkpoint.Mutate is { } mutate)
            {
                checkpoint.Mutate = null;
                try { await mutate(cancellationToken); checkpoint.Mutated = true; }
                catch { await lease.DisposeAsync(); throw; }
            }
            return lease;
        }
    }
}
