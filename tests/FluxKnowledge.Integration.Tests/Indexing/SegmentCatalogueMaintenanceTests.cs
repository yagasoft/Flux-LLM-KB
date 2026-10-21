using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class SegmentCatalogueMaintenanceTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Exit_observation_is_rechecked_under_the_fence_and_live_prefix_does_not_starve_later_candidates()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Proved execution exit, rather than a selection hint, authorises abandonment.", publish: false, segmented: true);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var draft = await db.IndexGenerations.Where(value => value.IndexPath == string.Empty).Select(value => value.Id).SingleAsync();
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        for (var index = 0; index < 3; index++) await store.PrepareAsync(draft, CancellationToken.None);
        var ids = await db.IndexPublicationCandidates.OrderBy(value => value.Id).Select(value => value.Id).ToArrayAsync();
        var probe = new ControlledProbe();
        probe.Pending.Enqueue(GpuInteractiveOwnerObservation.Exited);
        probe.Pending.Enqueue(GpuInteractiveOwnerObservation.Unknown);
        var operations = await OperationsAsync(environment, probe);
        var budget = new IndexProjectionMaintenanceBudget(MaximumCandidateObservations: 1);
        await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.False(await db.IndexPublicationCandidates.AnyAsync(value => value.State == 3));
        probe.Observation = GpuInteractiveOwnerObservation.Exited;
        await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.Equal(0, (await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == ids[0])).State);
        Assert.Equal(3, (await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == ids[1])).State);
        Assert.Equal(0, (await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == ids[2])).State);
        await operations.RunOnceAsync(budget, CancellationToken.None);
        await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.False(await db.IndexPublicationCandidates.AnyAsync(value => value.State < 2));
    }

    [NativeSqlServerFact]
    public async Task Abandoned_work_requires_proved_process_exit_and_its_preview_cleanup_is_bounded_and_repeatable()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "A worker can stop after committing temporary candidate metadata.", publish: false, segmented: true);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var draft = await db.IndexGenerations.Where(value => value.IndexPath == string.Empty).Select(value => value.Id).SingleAsync();
        using var scope = environment.Services.CreateScope();
        var candidate = await scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>().PrepareAsync(draft, CancellationToken.None);
        db.IndexCandidateMembers.AddRange(Enumerable.Range(1000, 20).Select(value => new IndexCandidateMemberEntity
        {
            CandidateId = candidate.Id, Phase = 1, VectorId = value, PayloadChecksum = new('0', 64), SourceIdentityId = Guid.NewGuid()
        }));
        await db.SaveChangesAsync();
        var count = await db.IndexCandidateMembers.CountAsync();
        var probe = new ControlledProbe();
        var operations = await OperationsAsync(environment, probe);
        var budget = new IndexProjectionMaintenanceBudget(MaximumMemberDeletes: 3);
        await operations.RunOnceAsync(budget, CancellationToken.None);
        probe.Observation = GpuInteractiveOwnerObservation.Unknown;
        await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.Equal(0, (await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == candidate.Id)).State);
        Assert.Equal(count, await db.IndexCandidateMembers.CountAsync());

        probe.Observation = GpuInteractiveOwnerObservation.Exited;
        await operations.RunOnceAsync(budget, CancellationToken.None);
        var abandoned = await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == candidate.Id);
        Assert.Equal(3, abandoned.State);
        Assert.NotNull(abandoned.CompletedAtUtc);
        Assert.InRange(count - await db.IndexCandidateMembers.CountAsync(), 1, 3);
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var before = await db.IndexCandidateMembers.CountAsync();
            await operations.RunOnceAsync(budget, CancellationToken.None);
            Assert.InRange(before - await db.IndexCandidateMembers.CountAsync(), 0, 3);
        }
        Assert.False(await db.IndexCandidateMembers.AnyAsync());
        Assert.Equal(abandoned.CompletedAtUtc, (await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == candidate.Id)).CompletedAtUtc);
        Assert.True(await db.Vectors.AnyAsync());
        Assert.False(await db.IndexSegments.AnyAsync());
    }

    [NativeSqlServerFact]
    public async Task Consumed_changes_are_paged_past_only_the_active_and_candidate_base_watermarks()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The first source anchors an older publication.", segmented: true);
        var old = await environment.ActiveGenerationAsync();
        await environment.AddAndPumpAsync("The second source is now part of the current publication.");
        await using var db = await environment.Factory.CreateDbContextAsync();
        var state = await db.IndexState.SingleAsync();
        var active = await db.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == state.ActiveIndexGenerationId);
        var owner = new WindowsInteractiveGpuOwnerProbe().Current;
        var pin = new IndexPublicationCandidateEntity
        {
            Id = Guid.NewGuid(), CorpusEpoch = state.CorpusEpoch, ExpectedCorpusVersion = state.CorpusVersion,
            PublicationCorpusVersion = state.CorpusVersion, BaseGenerationId = old.Id,
            BeforeChecksum = new('0', 64), AfterChecksum = new('0', 64), OwnerInstanceId = Guid.NewGuid(),
            OwnerProcessId = owner.ProcessId, OwnerStartedAtUtc = owner.StartedAtUtc, OwnerMachineFingerprint = owner.MachineFingerprint,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.IndexPublicationCandidates.Add(pin);
        db.IndexProjectionChanges.AddRange(Enumerable.Range(0, 8).Select(_ => new IndexProjectionChangeEntity
        {
            CorpusEpoch = state.CorpusEpoch, CorpusVersion = old.CorpusStamp!.CorpusVersion, ScopeKind = 1, ScopeId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow
        }));
        var unconsumedVersion = ++state.CorpusVersion;
        var source = await db.PipelineRecords.Select(value => value.SourceIdentityId).FirstAsync();
        db.IndexProjectionChanges.Add(new() { CorpusEpoch = state.CorpusEpoch, CorpusVersion = unconsumedVersion,
            ScopeKind = 1, ScopeId = source, CreatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var operations = await OperationsAsync(environment, new WindowsInteractiveGpuOwnerProbe());
        var budget = new IndexProjectionMaintenanceBudget(MaximumChangeDeletes: 3);
        var beforeCount = await db.IndexProjectionChanges.CountAsync();
        await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.InRange(beforeCount - await db.IndexProjectionChanges.CountAsync(), 1, 3);
        for (var iteration = 0; iteration < 8; iteration++) await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.Equal(old.CorpusStamp!.CorpusVersion, (await db.IndexState.AsNoTracking().SingleAsync()).ProjectionChangeFloorVersion);
        Assert.True(await db.IndexProjectionChanges.AnyAsync(value => value.CorpusVersion == active.CorpusVersion));
        Assert.True(await db.IndexProjectionChanges.AnyAsync(value => value.CorpusVersion == unconsumedVersion));

        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        await store.AbandonAsync(pin.Id, CancellationToken.None);
        for (var iteration = 0; iteration < 3; iteration++) await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.Equal(active.CorpusVersion, (await db.IndexState.AsNoTracking().SingleAsync()).ProjectionChangeFloorVersion);
        Assert.True(await db.IndexProjectionChanges.AnyAsync(value => value.CorpusVersion == unconsumedVersion));
        var refresh = await scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>().BuildAndPlaceAsync(Guid.Empty, CancellationToken.None);
        Assert.True(await store.TryActivateRefreshAsync(refresh, CancellationToken.None));
        for (var iteration = 0; iteration < 3; iteration++) await operations.RunOnceAsync(budget, CancellationToken.None);
        Assert.False(await db.IndexProjectionChanges.AnyAsync());
        Assert.Equal(unconsumedVersion, (await db.IndexState.AsNoTracking().SingleAsync()).ProjectionChangeFloorVersion);
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    private static async Task<SqlSegmentRetirementOperations> OperationsAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, IGpuInteractiveOwnerProbe probe)
    {
        var recovery = Path.Combine(environment.IndexRoot, "catalogue-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        return new(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot), environment.IndexRoot, recovery, TimeProvider.System, probe);
    }

    private sealed class ControlledProbe : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current { get; } = new WindowsInteractiveGpuOwnerProbe().Current;
        public GpuInteractiveOwnerObservation Observation { get; set; } = GpuInteractiveOwnerObservation.Alive;
        public Queue<GpuInteractiveOwnerObservation> Pending { get; } = new();
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner) => Pending.TryDequeue(out var next) ? next : Observation;
    }
}
