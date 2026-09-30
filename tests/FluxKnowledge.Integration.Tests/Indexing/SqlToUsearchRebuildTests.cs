using System.Security.Cryptography;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Infrastructure.Usearch.Search;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class SqlToUsearchRebuildTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Stale_stamped_projection_refreshes_from_sql_without_reembedding_or_repairing_old_files()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Projection version baseline.");
        var active = await environment.ActiveGenerationAsync();
        var metadataBefore = File.ReadAllText(Path.Combine(active.IndexPath, UsearchGenerationValidator.MetadataFileName));
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var state = await context.IndexState.SingleAsync();
            state.CorpusVersion++;
            await context.SaveChangesAsync();
        }
        var sql = await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None);
        Assert.True(sql.IsProjectionUnavailable);
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.Equal(metadataBefore, File.ReadAllText(Path.Combine(active.IndexPath, UsearchGenerationValidator.MetadataFileName)));
        Assert.False(coordinator.Snapshot.IsProjectionUnavailable);
        Assert.NotEqual(active.Id, coordinator.Snapshot.ActiveGenerationId);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        var currentState = await verification.IndexState.SingleAsync();
        var refreshed = await verification.IndexGenerations.SingleAsync(value => value.Id == currentState.ActiveIndexGenerationId);
        Assert.Equal(currentState.CorpusEpoch, refreshed.CorpusEpoch);
        Assert.Equal(currentState.CorpusVersion, refreshed.CorpusVersion);
        Assert.Equal(await verification.TextChunks.CountAsync(), await verification.Vectors.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Suppressed_source_is_removed_from_refreshed_membership_without_deleting_canonical_vectors()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Surviving public passage.");
        var recordId = await environment.AddRetainedAndPumpAsync("Temporary passage to suppress.");
        long suppressedVectorId;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var record = await context.PipelineRecords.SingleAsync(value => value.Id == recordId);
            var revision = await context.SourceRevisions.SingleAsync(value => value.Id == record.SourceRevisionId);
            suppressedVectorId = await (from vector in context.Vectors
                join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
                join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
                where artifact.PipelineRecordId == recordId
                select vector.VectorId).SingleAsync();
            revision.SuppressedAtUtc = DateTimeOffset.UtcNow;
            var state = await context.IndexState.SingleAsync();
            state.CorpusVersion++;
            await context.SaveChangesAsync();
        }
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        await provider.GetRequiredService<DerivedIndexRecoveryCoordinator>().RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, provider.GetRequiredService<DerivedIndexRecoveryCoordinator>().Snapshot.State);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        var active = (await verification.IndexState.SingleAsync()).ActiveIndexGenerationId;
        Assert.DoesNotContain(suppressedVectorId, await verification.IndexGenerationVectors
            .Where(value => value.GenerationId == active).Select(value => value.VectorId).ToArrayAsync());
        Assert.NotNull(await verification.Vectors.SingleOrDefaultAsync(value => value.VectorId == suppressedVectorId));
    }

    [NativeSqlServerFact]
    public async Task Publication_refresh_refuses_a_candidate_when_the_corpus_stamp_changes()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Stable source.");
        var original = await environment.ActiveGenerationAsync();
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var state = await context.IndexState.SingleAsync();
            state.CorpusVersion++;
            await context.SaveChangesAsync();
        }
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var state = await context.IndexState.SingleAsync();
            state.CorpusVersion++;
            await context.SaveChangesAsync();
        }
        Assert.False(await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System)
            .TryActivatePublicationCandidateAsync(candidate, CancellationToken.None));
        Assert.Equal(original.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Deployment_hold_defers_automatic_publication_refresh()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Held source.");
        var original = await environment.ActiveGenerationAsync();
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var state = await context.IndexState.SingleAsync();
            state.CorpusVersion++;
            await context.SaveChangesAsync();
        }
        environment.PermitRebuild(Guid.NewGuid());
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot, environment.DeploymentHold);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.IndexUpdating, coordinator.Snapshot.State);
        Assert.Equal(original.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        environment.ReleaseRebuild();
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
    }

    [NativeSqlServerFact]
    public async Task Placed_candidate_and_two_recovery_processes_converge_on_one_current_generation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Concurrent recovery source.");
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var state = await context.IndexState.SingleAsync();
            state.CorpusVersion++;
            await context.SaveChangesAsync();
        }
        var placed = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        using var first = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        using var second = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        await Task.WhenAll(
            first.GetRequiredService<DerivedIndexRecoveryCoordinator>().RunOnceAsync(CancellationToken.None).AsTask(),
            second.GetRequiredService<DerivedIndexRecoveryCoordinator>().RunOnceAsync(CancellationToken.None).AsTask());
        var stateAfter = await environment.ActiveGenerationAsync();
        Assert.Equal(placed.Generation.Id, stateAfter.Id);
        Assert.Equal(placed.Generation.CorpusStamp, stateAfter.CorpusStamp);
        await using var contextAfter = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(1, await contextAfter.IndexGenerations.CountAsync(value => value.Id == placed.Generation.Id));
        Assert.Equal(placed.Vectors.Count, await contextAfter.IndexGenerationVectors.CountAsync(value => value.GenerationId == placed.Generation.Id));
    }

    [NativeSqlServerFact]
    public async Task Publication_refresh_preserves_canonical_rows_when_no_vectors_remain_eligible()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Only source.");
        var original = await environment.ActiveGenerationAsync();
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            (await context.PipelineRecords.SingleAsync()).IsDeleted = true;
            (await context.IndexState.SingleAsync()).CorpusVersion++;
            await context.SaveChangesAsync();
        }
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.IndexUpdating, coordinator.Snapshot.State);
        Assert.Equal(original.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        await using var verification = await environment.Factory.CreateDbContextAsync();
        Assert.True(await verification.Vectors.AnyAsync());
        Assert.True(await verification.TextChunks.AnyAsync());
        Assert.Null((await verification.IndexState.SingleAsync()).EmptyCatalogueValidatedAtUtc);
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.IndexUpdating, coordinator.Snapshot.State);
        await using (var restoration = await environment.Factory.CreateDbContextAsync())
        {
            (await restoration.PipelineRecords.SingleAsync()).IsDeleted = false;
            (await restoration.IndexState.SingleAsync()).CorpusVersion++;
            await restoration.SaveChangesAsync();
        }
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
    }

    [NativeSqlServerFact]
    public async Task Candidate_activation_refuses_membership_change_even_at_the_same_stamp_and_a_new_hold()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Membership source.");
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            (await context.IndexState.SingleAsync()).CorpusVersion++;
            await context.SaveChangesAsync();
        }
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var original = await environment.ActiveGenerationAsync();
        environment.PermitRebuild(Guid.NewGuid());
        var heldStore = new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System,
            deploymentHold: environment.DeploymentHold);
        Assert.False(await heldStore.TryActivatePublicationCandidateAsync(candidate, CancellationToken.None));
        await using (var heldVerification = await environment.Factory.CreateDbContextAsync())
        {
            Assert.False(await heldVerification.IndexGenerations.AnyAsync(value => value.Id == candidate.Generation.Id));
            Assert.False(await heldVerification.IndexGenerationVectors.AnyAsync(value => value.GenerationId == candidate.Generation.Id));
        }
        environment.ReleaseRebuild();
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            (await context.Vectors.SingleAsync()).IsDeleted = true;
            await context.SaveChangesAsync();
        }
        Assert.False(await heldStore.TryActivatePublicationCandidateAsync(candidate, CancellationToken.None));
        Assert.Equal(original.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Active_corpus_rebuild_operation_prevents_automatic_publication_refresh()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Maintenance source.");
        var original = await environment.ActiveGenerationAsync();
        var operationId = Guid.NewGuid();
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            context.CorpusRebuildOperations.Add(new CorpusRebuildOperationEntity
            {
                Id = operationId, TargetEpoch = Guid.NewGuid(), ManifestHash = new string('a', 64),
                ManifestJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow
            });
            var state = await context.IndexState.SingleAsync();
            state.CorpusVersion++;
            state.CorpusRebuildOperationId = operationId;
            await context.SaveChangesAsync();
        }
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.IndexUpdating, coordinator.Snapshot.State);
        Assert.Equal(original.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Hosted_recovery_reprobes_after_hold_release_and_a_later_publication_change()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Repeated source change.");
        var original = await environment.ActiveGenerationAsync();
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            (await context.IndexState.SingleAsync()).CorpusVersion++;
            await context.SaveChangesAsync();
        }
        environment.PermitRebuild(Guid.NewGuid());
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot, environment.DeploymentHold);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var probes = 0;
        var service = new DerivedIndexRecoveryService(coordinator, DerivedIndexRecoveryOptions.Default, TimeProvider.System,
            async (_, _) =>
            {
                probes++;
                if (probes == 1)
                {
                    Assert.Equal(DerivedIndexRecoveryState.IndexUpdating, coordinator.Snapshot.State);
                    Assert.Equal(original.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
                    environment.ReleaseRebuild();
                }
                else if (probes == 2)
                {
                    Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
                    await using var changed = await environment.Factory.CreateDbContextAsync();
                    (await changed.IndexState.SingleAsync()).CorpusVersion++;
                    await changed.SaveChangesAsync();
                }
                else
                {
                    Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
                    stop.Cancel();
                }
            });
        await service.RunForTestingAsync(stop.Token);
        Assert.Equal(3, probes);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        var state = await verification.IndexState.SingleAsync();
        var active = await verification.IndexGenerations.SingleAsync(value => value.Id == state.ActiveIndexGenerationId);
        Assert.Equal(state.CorpusEpoch, active.CorpusEpoch);
        Assert.Equal(state.CorpusVersion, active.CorpusVersion);
    }

    [NativeSqlServerFact]
    public async Task Live_query_lease_defers_publication_refresh_until_its_native_owner_releases_it()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Leased source.");
        var original = await environment.ActiveGenerationAsync();
        var lease = await new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System).TryAcquireAsync(Guid.NewGuid(),
            new GpuInteractiveOwnerIdentity(Environment.ProcessId, DateTimeOffset.UtcNow, new string('b', 64)),
            original.ModelFingerprint, original.Dimensions, CancellationToken.None);
        Assert.NotNull(lease);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            (await context.IndexState.SingleAsync()).CorpusVersion++;
            await context.SaveChangesAsync();
        }
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(original.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        await lease.DisposeAsync();
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
    }

    [NativeSqlServerFact]
    public async Task Leased_ann_uses_captured_membership_and_refuses_after_publication_changes()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Leased native ANN baseline.");
        var active = await environment.ActiveGenerationAsync();
        var sqlLease = await new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System).TryAcquireAsync(Guid.NewGuid(),
            new GpuInteractiveOwnerIdentity(Environment.ProcessId, DateTimeOffset.UtcNow, new string('a', 64)),
            active.ModelFingerprint, active.Dimensions, CancellationToken.None);
        Assert.NotNull(sqlLease);
        var native = await new UsearchCorpusAnnLeaseFactory(environment.Store, new UsearchGenerationValidator())
            .OpenAsync(sqlLease, CancellationToken.None);
        var vector = (await environment.Store.ReadVectorsAsync(active.Id, CancellationToken.None))[0];
        var query = new float[active.Dimensions];
        Buffer.BlockCopy(vector.Values, 0, query, 0, vector.Values.Length);
        var matches = await native.SearchAsync(query, 1, CancellationToken.None);
        Assert.Equal(vector.VectorId, Assert.Single(matches).VectorId);
        Assert.True(await native.IsCurrentAsync(CancellationToken.None));
        await environment.AddAndPumpAtPathAsync("Leased native ANN successor.", "initial.txt");
        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(async () => await native.SearchAsync(query, 1, CancellationToken.None));
        await native.DisposeAsync();
        Assert.False(await sqlLease.IsCurrentAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await native.SearchAsync(query, 1, CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Failed_native_open_releases_the_captured_sql_lease()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Leased ANN corruption baseline.");
        var active = await environment.ActiveGenerationAsync();
        var sqlLease = await new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System).TryAcquireAsync(Guid.NewGuid(),
            new GpuInteractiveOwnerIdentity(Environment.ProcessId, DateTimeOffset.UtcNow, new string('a', 64)),
            active.ModelFingerprint, active.Dimensions, CancellationToken.None);
        Assert.NotNull(sqlLease);
        File.WriteAllText(Path.Combine(active.IndexPath, UsearchGenerationValidator.MetadataFileName), "{}");
        await Assert.ThrowsAsync<IndexGenerationValidationException>(async () =>
            await new UsearchCorpusAnnLeaseFactory(environment.Store, new UsearchGenerationValidator()).OpenAsync(sqlLease, CancellationToken.None));
        Assert.False(await sqlLease.IsCurrentAsync(CancellationToken.None));
        await using var exclusive = await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System)
            .TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(exclusive);
    }

    [NativeSqlServerFact]
    public async Task Lost_query_session_refuses_results_and_blocks_cleanup_until_native_disposal()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Query session loss.");
        var active = await environment.ActiveGenerationAsync();
        var leases = new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System);
        var lease = await leases.TryAcquireAsync(Guid.NewGuid(),
            new GpuInteractiveOwnerIdentity(Environment.ProcessId, DateTimeOffset.UtcNow, new string('a', 64)),
            active.ModelFingerprint, active.Dimensions, CancellationToken.None);
        Assert.NotNull(lease);
        var native = await new UsearchCorpusAnnLeaseFactory(environment.Store, new UsearchGenerationValidator()).OpenAsync(lease, CancellationToken.None);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var registration = await context.CorpusQueryLeases.SingleAsync(value => value.Id == lease.LeaseId);
            // Fixture guarantees a generated disposable catalogue on the loopback server.
            // Kill only the exact query-owned session, never the server or another process.
            await context.Database.ExecuteSqlRawAsync(FormattableString.Invariant($"KILL {registration.SqlSessionId}"));
        }
        Assert.False(await lease.IsCurrentAsync(CancellationToken.None));
        var recovery = new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System);
        Assert.Null(await recovery.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None));
        await using (var context = await environment.Factory.CreateDbContextAsync())
            Assert.True(await context.CorpusQueryLeases.AnyAsync(value => value.Id == lease.LeaseId));
        var deletionStore = new SqlSourceDeletionStore(environment.Factory, TimeProvider.System);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            context.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = Guid.NewGuid(), SourceRootId = Guid.NewGuid(), State = 0, Phase = "accepted",
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }
        var work = await deletionStore.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(work);
        Assert.False((await deletionStore.PurgeAsync(work, null, CancellationToken.None)).Completed);
        await using (var context = await environment.Factory.CreateDbContextAsync())
            Assert.Equal("source-delete-search-queries-active", (await context.SourceDeletionOperations.SingleAsync()).ReasonCode);
        await native.DisposeAsync();
        await using (var exclusive = await recovery.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None))
        {
            Assert.NotNull(exclusive);
            await using var context = await environment.Factory.CreateDbContextAsync();
            Assert.Empty(await context.CorpusQueryLeases.ToListAsync());
            Assert.True(await context.IndexGenerations.AnyAsync(value => value.Id == active.Id));
        }
        await lease.DisposeAsync();
        var resumed = await deletionStore.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(resumed);
        Assert.True((await deletionStore.PurgeAsync(resumed, null, CancellationToken.None)).Completed);
    }

    [NativeSqlServerTheory]
    [InlineData(GpuInteractiveOwnerObservation.Unknown)]
    [InlineData(GpuInteractiveOwnerObservation.Alive)]
    [InlineData(GpuInteractiveOwnerObservation.Exited)]
    public async Task Only_proven_exited_query_incarnations_can_be_recovered(GpuInteractiveOwnerObservation observation)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Query incarnation recovery.");
        var active = await environment.ActiveGenerationAsync();
        var owner = new GpuInteractiveOwnerIdentity(Environment.ProcessId, DateTimeOffset.UtcNow, new string('a', 64));
        var id = Guid.NewGuid();
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            context.CorpusQueryLeases.Add(new CorpusQueryLeaseEntity
            {
                Id = id, GenerationId = active.Id, CorpusEpoch = active.CorpusStamp!.CorpusEpoch,
                CorpusVersion = active.CorpusStamp.CorpusVersion, ModelFingerprint = active.ModelFingerprint, Dimensions = active.Dimensions,
                OwnerInstanceId = Guid.NewGuid(), OwnerProcessId = owner.ProcessId, OwnerStartedAtUtc = owner.StartedAtUtc,
                OwnerMachineFingerprint = owner.MachineFingerprint, SqlSessionId = 123, CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }
        var observer = new QueryOwnerProbe(owner, observation);
        await using var exclusive = await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System, observer)
            .TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(observation == GpuInteractiveOwnerObservation.Exited, exclusive is not null);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(observation != GpuInteractiveOwnerObservation.Exited, await verification.CorpusQueryLeases.AnyAsync(value => value.Id == id));
        // This row was a synthetic observer fixture, with no native query owner.
        await verification.CorpusQueryLeases.Where(value => value.Id == id).ExecuteDeleteAsync();
    }

    private sealed class QueryOwnerProbe(GpuInteractiveOwnerIdentity current, GpuInteractiveOwnerObservation observation) : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current => current;
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner)
        {
            Assert.Equal(current, owner);
            return observation;
        }
    }

    [NativeSqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task New_activation_refuses_a_missing_expected_or_generation_stamp(bool missingExpected)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Missing stamp baseline.");
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        candidate = missingExpected ? candidate with { ExpectedCorpusStamp = null } :
            candidate with { Generation = candidate.Generation with { CorpusStamp = null } };
        var request = await ClaimPublishAsync(environment, candidate);
        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(async () =>
            await new SqlStageTransitionStore(environment.Factory).TransitionAsync(request, CancellationToken.None));
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.False(await context.Artifacts.AnyAsync(value => value.Id == request.Artifact.Id));
    }

    [NativeSqlServerFact]
    public async Task Query_lease_blocks_cross_instance_recovery_and_captures_one_generation_until_disposal()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Query lease baseline.");
        var active = await environment.ActiveGenerationAsync();
        var owner = new GpuInteractiveOwnerIdentity(Environment.ProcessId, DateTimeOffset.UtcNow, new string('a', 64));
        var leases = new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System);
        var lease = await leases.TryAcquireAsync(Guid.NewGuid(), owner, active.ModelFingerprint, active.Dimensions, CancellationToken.None);
        Assert.NotNull(lease);
        Assert.Equal(active, lease.Generation);
        Assert.True(await lease.IsCurrentAsync(CancellationToken.None));
        var recovery = new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System);
        Assert.Null(await recovery.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None));
        await environment.AddAndPumpAtPathAsync("Query lease successor.", "initial.txt");
        Assert.False(await lease.IsCurrentAsync(CancellationToken.None));
        Assert.Equal(active, lease.Generation);
        Assert.NotEmpty(await environment.Store.ReadVectorsAsync(active.Id, CancellationToken.None));
        Assert.Null(await recovery.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None));
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.False(await lease.IsCurrentAsync(CancellationToken.None));
        await using var exclusive = await recovery.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(exclusive);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        Assert.Empty(await verification.CorpusQueryLeases.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Query_lease_refuses_wrong_profile_and_stale_or_unstamped_projection()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Query profile baseline.");
        var active = await environment.ActiveGenerationAsync();
        var owner = new GpuInteractiveOwnerIdentity(Environment.ProcessId, DateTimeOffset.UtcNow, new string('a', 64));
        var leases = new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System);
        Assert.Null(await leases.TryAcquireAsync(Guid.NewGuid(), owner, new string('b', 64), active.Dimensions, CancellationToken.None));
        Assert.Null(await leases.TryAcquireAsync(Guid.NewGuid(), owner, active.ModelFingerprint, active.Dimensions + 1, CancellationToken.None));
        await using var context = await environment.Factory.CreateDbContextAsync();
        var state = await context.IndexState.SingleAsync();
        state.CorpusVersion++;
        await context.SaveChangesAsync();
        Assert.Null(await leases.TryAcquireAsync(Guid.NewGuid(), owner, active.ModelFingerprint, active.Dimensions, CancellationToken.None));
        state.CorpusVersion--;
        var generation = await context.IndexGenerations.SingleAsync(value => value.Id == active.Id);
        generation.CorpusEpoch = null;
        generation.CorpusVersion = null;
        await context.SaveChangesAsync();
        Assert.Null(await leases.TryAcquireAsync(Guid.NewGuid(), owner, active.ModelFingerprint, active.Dimensions, CancellationToken.None));
        Assert.Empty(await context.CorpusQueryLeases.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Publications_stamp_the_exact_epoch_and_membership_version()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Published first version.");
        var first = await environment.ActiveGenerationAsync();
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var state = await context.IndexState.SingleAsync();
            Assert.Equal(new CorpusPublicationStamp(state.CorpusEpoch, state.CorpusVersion), first.CorpusStamp);
        }
        await environment.AddAndPumpAtPathAsync("Published replacement version.", "initial.txt");
        var replacement = await environment.ActiveGenerationAsync();
        Assert.NotNull(first.CorpusStamp);
        Assert.NotNull(replacement.CorpusStamp);
        Assert.Equal(first.CorpusStamp.CorpusEpoch, replacement.CorpusStamp.CorpusEpoch);
        Assert.Equal(first.CorpusStamp.CorpusVersion + 1, replacement.CorpusStamp.CorpusVersion);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(replacement.CorpusStamp.CorpusVersion, (await verification.IndexState.SingleAsync()).CorpusVersion);
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var request = await ClaimPublishAsync(environment, candidate);
        await new SqlStageTransitionStore(environment.Factory).TransitionAsync(request, CancellationToken.None);
        await using var afterNoMembershipChange = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(replacement.CorpusStamp.CorpusVersion, (await afterNoMembershipChange.IndexState.SingleAsync()).CorpusVersion);
    }

    [NativeSqlServerFact]
    public async Task Old_epoch_candidate_refuses_even_when_vector_membership_is_identical()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Published epoch baseline.");
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.NotNull(candidate.ExpectedCorpusStamp);
        var request = await ClaimPublishAsync(environment, candidate);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var state = await context.IndexState.SingleAsync();
            state.CorpusEpoch = Guid.NewGuid();
            await context.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(async () =>
            await new SqlStageTransitionStore(environment.Factory).TransitionAsync(request, CancellationToken.None));
        await using var verification = await environment.Factory.CreateDbContextAsync();
        Assert.False(await verification.Artifacts.AnyAsync(artifact => artifact.Id == request.Artifact.Id));
        Assert.False((await verification.PipelineRecords.SingleAsync(record => record.Id == request.CurrentJob.PipelineRecordId.Value)).CompletionCriteriaMet);
        var newEpochCandidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.NotEqual(candidate.Generation.Id, newEpochCandidate.Generation.Id);
    }

    [NativeSqlServerFact]
    public async Task Publication_conflict_retry_is_durable_idempotent_and_fenced_from_a_later_claim()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Published retry baseline.");
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var claim = await ClaimPublishAsync(environment, candidate);
        IStageTransitionStore store = new SqlStageTransitionStore(environment.Factory);
        var due = DateTimeOffset.UtcNow.AddSeconds(5);
        var request = new StageRetryRequest(claim.DispatchMessage, claim.CurrentJob, due,
            "publication-snapshot-conflict", nameof(SqlToUsearchRebuildTests));
        await store.RetryAsync(request, CancellationToken.None);
        await store.RetryAsync(request, CancellationToken.None);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var job = await context.Jobs.SingleAsync(job => job.Id == claim.CurrentJob.JobId.Value);
            var dispatch = await context.OutboxMessages.SingleAsync(message => message.Id == claim.DispatchMessage.DispatchMessageId.Value);
            Assert.Equal((int)PublicJobState.WorkerQueued, job.PublicState);
            Assert.Null(job.LeaseOwner);
            Assert.Null(job.LeaseExpiresAtUtc);
            Assert.Equal(due, job.DueAtUtc);
            Assert.Equal(request.Reason, job.Reason);
            Assert.Null(dispatch.LeaseOwner);
            Assert.Null(dispatch.LeaseExpiresAtUtc);
            Assert.Null(dispatch.DispatchedAtUtc);
            Assert.Equal(due, dispatch.DueAtUtc);
            Assert.False(await context.Artifacts.AnyAsync(artifact => artifact.Id == claim.Artifact.Id));
        }
        var outbox = new SqlOutboxStore(environment.Factory);
        Assert.Null(await outbox.ClaimNextDueAsync("retry-dispatcher", due.AddMilliseconds(-1),
            TimeSpan.FromMinutes(2), [PipelineOperations.Publish], CancellationToken.None));
        var nextDispatch = await outbox.ClaimNextDueAsync("retry-dispatcher", due,
            TimeSpan.FromMinutes(2), [PipelineOperations.Publish], CancellationToken.None);
        Assert.NotNull(nextDispatch);
        var nextJob = await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(nextDispatch,
            "retry-worker", due, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(nextJob);
        Assert.True(nextJob.LeaseGeneration > claim.CurrentJob.LeaseGeneration);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RetryAsync(request, CancellationToken.None));
        await using var verification = await environment.Factory.CreateDbContextAsync();
        var active = await verification.Jobs.SingleAsync(job => job.Id == nextJob.JobId.Value);
        Assert.Equal((int)PublicJobState.WorkerProcessing, active.PublicState);
        Assert.Equal("retry-worker", active.LeaseOwner);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_preview_includes_the_pending_source_and_rolls_back_all_visibility_changes(bool sqlRetries)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Pending preview source.", publish: false);
        await using var before = await environment.Factory.CreateDbContextAsync();
        var vector = await before.Vectors.SingleAsync();
        Assert.Empty(await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None));
        var previewStore = sqlRetries
            ? new SqlPipelineStore(new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<FluxKnowledgeDbContext>(
                new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString,
                    sql => sql.EnableRetryOnFailure()).Options))
            : environment.Store;
        var beforeState = await before.IndexState.SingleAsync();
        var preview = await previewStore.ReadPublicationSnapshotAsync(vector.IndexGenerationId, CancellationToken.None);
        Assert.Equal(vector.VectorId, Assert.Single(preview.Vectors).VectorId);
        Assert.Equal(new CorpusPublicationStamp(beforeState.CorpusEpoch, beforeState.CorpusVersion), preview.ExpectedCorpusStamp);
        Assert.Equal(new CorpusPublicationStamp(beforeState.CorpusEpoch, beforeState.CorpusVersion + 1), preview.PublicationStamp);
        await using var after = await environment.Factory.CreateDbContextAsync();
        var record = await after.PipelineRecords.SingleAsync();
        Assert.Equal(beforeState.CorpusVersion, (await after.IndexState.SingleAsync()).CorpusVersion);
        Assert.False(record.CompletionCriteriaMet);
        Assert.Null((await after.IndexState.SingleAsync()).ActiveIndexGenerationId);
        Assert.Empty(await after.DocumentPublications.ToArrayAsync());
        Assert.Empty(await after.IndexGenerationVectors.ToArrayAsync());
        Assert.Empty(await after.Artifacts.Where(artifact => artifact.Stage == (int)PipelineStage.Publish).ToArrayAsync());
        Assert.Equal((int)PublicJobState.WorkerQueued,
            (await after.Jobs.SingleAsync(job => job.Stage == (int)PipelineStage.Publish)).PublicState);
    }

    [NativeSqlServerFact]
    public async Task Unfinished_unrooted_revision_preserves_the_last_published_passages_in_both_channels()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Published INV-42 approval.");
        var expected = await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None);
        Assert.NotEmpty(expected);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var published = await context.PipelineRecords.SingleAsync();
            context.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = Guid.NewGuid(), SourceIdentityId = published.SourceIdentityId,
                Revision = published.Revision + 1, ContentHash = new string('a', 64),
                RootLineageRecordId = published.RootLineageRecordId, ParentRevisionRecordId = published.Id,
                CurrentStage = (int)PipelineStage.Embed, RegisteredAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }

        Assert.Equal(expected.Select(vector => vector.VectorId),
            (await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None)).Select(vector => vector.VectorId));
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        var scope = await reader.ResolveScopeAsync("all", null, null, CancellationToken.None);
        Assert.NotNull(scope);
        Assert.NotEmpty(await reader.SearchAsync("INV-42", scope, 5, CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Unpublished_retained_passages_are_absent_from_vector_and_lexical_selection()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Published baseline.");
        var recordId = await environment.AddRetainedAndPumpAsync("UNPUBLISHED-42 approval.");
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var record = await context.PipelineRecords.SingleAsync(candidate => candidate.Id == recordId);
            record.CompletionCriteriaMet = false;
            await context.SaveChangesAsync();
        }
        await using var verification = await environment.Factory.CreateDbContextAsync();
        var forbidden = await (from vector in verification.Vectors
            join chunk in verification.TextChunks on vector.TextChunkId equals chunk.Id
            join artifact in verification.Artifacts on chunk.ArtifactId equals artifact.Id
            where artifact.PipelineRecordId == recordId select vector.VectorId).ToArrayAsync();
        Assert.DoesNotContain(await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None),
            vector => forbidden.Contains(vector.VectorId));
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        var scope = await reader.ResolveScopeAsync("all", null, null, CancellationToken.None);
        Assert.NotNull(scope);
        Assert.Empty(await reader.SearchAsync("UNPUBLISHED-42", scope, 5, CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Vector_selection_requires_the_exact_canonical_content_hash()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Published exact revision.");
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var vector = await context.Vectors.SingleAsync();
            vector.TextChunkContentHash = new string('b', 64);
            await context.SaveChangesAsync();
        }
        Assert.Empty(await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Coherent_passages_flow_through_ingress_publication_search_and_citation_readback()
    {
        var passageBuilder = new PassageBuilder(new SyntheticPassageTokenizer(), new PassagePolicy(12, 16, 160, 8, 80));
        var text = string.Concat(Enumerable.Repeat("Every payment requires a manager approval. ", 8)) +
            "INV-42 confirms the final approved payment.";
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, text, passageBuilder);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var chunks = await context.TextChunks.OrderBy(chunk => chunk.Ordinal).ToArrayAsync();
        Assert.True(chunks.Length > 1);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(passageBuilder.PolicyFingerprint, chunk.PassagePolicyFingerprint);
            Assert.InRange(chunk.Length, 1, 160);
            Assert.Equal(chunk.Content, chunk.SearchText);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(chunk.SearchText))),
                chunk.SearchInputHash);
        });
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(environment.Factory),
            new CorpusEvidenceCodec(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()),
            new FluxKnowledge.Infrastructure.SqlServer.Visibility.LocalPrivateContentDisclosure(), new CorpusRetrievalOptions(true));
        var hit = Assert.Single((await service.SearchAsync(new CorpusSearchRequest("INV-42", 5, "all", null, null),
            CancellationToken.None)).Results);
        Assert.Equal(chunks.Single(chunk => chunk.Content.Contains("INV-42", StringComparison.Ordinal)).Content, hit.Passage);
        Assert.Equal(hit.Passage, (await service.ReadAsync(new CorpusReadRequest(hit.EvidenceRef, 0),
            CancellationToken.None)).Text);
    }

    [NativeSqlServerFact]
    public async Task Rebuild_from_sql_keeps_a_retained_source_pipeline_record_searchable()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "baseline source");
        var retainedRecordId = await environment.AddRetainedAndPumpAsync("retained rebuild source");
        var active = await environment.ActiveGenerationAsync();
        Directory.Delete(environment.IndexRoot, recursive: true);

        _ = await environment.Builder.RebuildFromSqlAsync(active.Id, CancellationToken.None);
        var query = await environment.Embeddings.CreateEmbeddingAsync("retained rebuild", CancellationToken.None);
        var matches = await environment.Reader.SearchAsync(query.Values, 10, CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();

        var retainedVectorId = await (
                from vector in context.Vectors
                join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
                join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
                where artifact.PipelineRecordId == retainedRecordId
                select vector.VectorId)
            .SingleAsync();
        var rebuiltMembership = await context.IndexGenerationVectors
            .Where(member => member.GenerationId == active.Id)
            .Select(member => member.VectorId)
            .ToListAsync();
        var returnedVectorIds = matches.Select(match => match.VectorId).ToArray();
        var returnedRecordByVectorId = await (
                from vector in context.Vectors
                join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
                join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
                where returnedVectorIds.Contains(vector.VectorId)
                select new { vector.VectorId, artifact.PipelineRecordId })
            .ToDictionaryAsync(value => value.VectorId, value => value.PipelineRecordId);

        Assert.Contains(retainedVectorId, rebuiltMembership);
        Assert.Contains(matches, match => match.VectorId == retainedVectorId);
        Assert.Equal(retainedRecordId, returnedRecordByVectorId[retainedVectorId]);
    }

    [NativeSqlServerFact]
    public async Task First_validated_generation_clears_empty_marker_atomically()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        await using (var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync())
        {
            await new EmptyCatalogueBootstrapper().ProveAndMarkAsync(context, CancellationToken.None);
        }

        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first catalogue entry");
        await using var verification = await environment.Factory.CreateDbContextAsync();
        var state = await verification.IndexState.SingleAsync(candidate => candidate.Id == 1);

        Assert.NotNull(state.ActiveIndexGenerationId);
        Assert.Null(state.EmptyCatalogueValidatedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Publish_snapshot_keeps_an_older_unsuppressed_retained_record_when_a_later_retained_revision_is_suppressed()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "retained publish snapshot");
        var now = DateTimeOffset.UtcNow;
        var suppressedRevisionId = Guid.NewGuid();
        var olderRecordId = await environment.AddRetainedAndPumpAsync("retained publish source");
        IReadOnlyList<CanonicalVector> expectedMembership;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var olderRecord = await context.PipelineRecords.SingleAsync(record => record.Id == olderRecordId);
            Assert.NotNull(olderRecord.SourceRevisionId);
        }
        expectedMembership = await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None);
        Assert.NotEmpty(expectedMembership);

        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var olderRecord = await context.PipelineRecords.SingleAsync(record => record.Id == olderRecordId);
            var olderRevision = await context.SourceRevisions.SingleAsync(revision => revision.Id == olderRecord.SourceRevisionId!.Value);
            context.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = suppressedRevisionId, SourceRootId = olderRevision.SourceRootId,
                StableSourceIdentity = olderRevision.StableSourceIdentity, Revision = olderRevision.Revision + 1,
                ContentSha256 = olderRecord.ContentHash, CanonicalPath = "C:\\retained-publish\\two.txt",
                Classification = "AcceptedUtf8Text", Extension = ".txt", ByteLength = 1, DiscoveredAtUtc = now,
                SuppressedAtUtc = now, DiscoveryEvidenceJson = "{}"
            });
            context.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = Guid.NewGuid(), SourceIdentityId = olderRecord.SourceIdentityId, SourceRevisionId = suppressedRevisionId,
                Revision = olderRecord.Revision + 1, ContentHash = olderRecord.ContentHash,
                RootLineageRecordId = olderRecord.RootLineageRecordId, ParentRevisionRecordId = olderRecord.Id,
                CurrentStage = (int)PipelineStage.Publish, RegisteredAtUtc = now
            });
            await context.SaveChangesAsync();
        }

        var publicationStamp = await environment.Store.ReadPublicationSnapshotAsync(Guid.NewGuid(), CancellationToken.None);
        var candidate = new IndexGenerationCandidateSnapshot(
            new IndexGenerationDescriptor(
                Guid.NewGuid(),
                expectedMembership[0].ModelFingerprint,
                expectedMembership[0].Dimensions,
                "retained-publish-snapshot",
                UsearchGenerationValidator.ComputeChecksum(
                    expectedMembership[0].ModelFingerprint,
                    expectedMembership[0].Dimensions,
                    expectedMembership),
                expectedMembership.Count, publicationStamp.PublicationStamp),
            expectedMembership, publicationStamp.ExpectedCorpusStamp);
        var request = await ClaimPublishAsync(environment, candidate);

        _ = await new SqlStageTransitionStore(environment.Factory).TransitionAsync(request, CancellationToken.None);

        Assert.Equal(candidate.Generation.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        await using var verification = await environment.Factory.CreateDbContextAsync();
        var members = await verification.IndexGenerationVectors
            .Where(member => member.GenerationId == candidate.Generation.Id)
            .Select(member => member.VectorId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(expectedMembership.Select(vector => vector.VectorId).OrderBy(id => id), members);
    }

    [NativeSqlServerFact]
    public async Task Rebuild_after_index_root_deletion_uses_sql_membership_and_keeps_the_active_generation_searchable()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first document");
        var active = await environment.ActiveGenerationAsync();
        var query = await environment.Embeddings.CreateEmbeddingAsync("first", CancellationToken.None);

        Directory.Delete(environment.IndexRoot, recursive: true);
        var rebuilt = await environment.Builder.RebuildFromSqlAsync(active.Id, CancellationToken.None);
        var pointer = await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None);
        var matches = await environment.Reader.SearchAsync(query.Values, 5, CancellationToken.None);

        Assert.Equal(active.Id, rebuilt.Id);
        Assert.Equal(active.Id, pointer);
        Assert.True(File.Exists(Path.Combine(rebuilt.IndexPath, UsearchGenerationValidator.IndexFileName)));
        Assert.NotEmpty(matches);
        Assert.Equal(active.VectorCount, rebuilt.VectorCount);
    }

    [NativeSqlServerFact]
    public async Task Candidate_validation_failure_preserves_the_prior_active_pointer_and_immutable_directory()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first document");
        await environment.AddAndPumpAsync("second document");
        var active = await environment.ActiveGenerationAsync();
        var activePath = active.IndexPath;
        var failing = new UsearchGenerationBuilder(
            environment.Store,
            new UsearchIndexOptions(environment.IndexRoot),
            new ThrowingValidator());

        await Assert.ThrowsAsync<IndexGenerationValidationException>(
            async () => await failing.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(activePath, UsearchGenerationValidator.IndexFileName)));
    }

    [NativeSqlServerFact]
    public async Task Hosted_pipeline_persists_canonical_chunks_stable_vectors_membership_and_active_generation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "cafe\u0301\r\nline\r");
        var receipt = environment.LastReceipt;
        Assert.NotNull(receipt);
        await using var context = await environment.Factory.CreateDbContextAsync();

        var canonical = await context.Artifacts.SingleAsync(artifact =>
            artifact.PipelineRecordId == receipt!.PipelineRecordId.Value &&
            artifact.Stage == (int)FluxKnowledge.Domain.Pipeline.PipelineStage.CanonicalIndex);
        var chunks = await context.TextChunks.Where(chunk => chunk.ArtifactId == canonical.Id).OrderBy(chunk => chunk.Ordinal).ToListAsync();
        var vectors = await context.Vectors.OrderBy(vector => vector.VectorId).ToListAsync();
        var active = await context.IndexState.SingleAsync(state => state.Id == 1);
        var membership = await context.IndexGenerationVectors.Where(member => member.GenerationId == active.ActiveIndexGenerationId).ToListAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == receipt.PipelineRecordId.Value);

        Assert.Equal("café\nline\n", canonical.SearchText);
        Assert.NotEmpty(chunks);
        Assert.NotEmpty(vectors);
        Assert.All(vectors, vector => Assert.True(vector.VectorId > 0));
        Assert.NotNull(active.ActiveIndexGenerationId);
        Assert.Equal((int)PipelineStage.Publish, record.CurrentStage);
        Assert.True(record.CompletionCriteriaMet);
        Assert.Equal(vectors.Select(vector => vector.VectorId).Order(), membership.Select(member => member.VectorId).Order());
        var generation = await context.IndexGenerations.SingleAsync(generation => generation.Id == active.ActiveIndexGenerationId);
        Assert.True(File.Exists(Path.Combine(generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
    }

    [NativeSqlServerFact]
    public async Task Valid_active_generation_with_a_recognised_unplaced_embed_draft_remains_healthy()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        var active = await environment.ActiveGenerationAsync();
        await using var context = await environment.Factory.CreateDbContextAsync();
        var draft = await context.IndexGenerations.AsNoTracking().SingleAsync(generation =>
            generation.Id != active.Id && generation.IndexPath == string.Empty);
        var activePath = active.IndexPath;
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        var recoveryStore = provider.GetRequiredService<IDerivedIndexRecoveryStore>();

        await coordinator.RunOnceAsync(CancellationToken.None);

        var snapshot = await recoveryStore.ReadActiveAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.DoesNotContain(string.Empty, snapshot.ReferencedIndexPaths);
        Assert.Contains(draft.Id, snapshot.ReferencedGenerationIds);
        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.Equal(activePath, (await environment.Store.GetGenerationAsync(active.Id, CancellationToken.None))!.IndexPath);
        Assert.Equal(string.Empty, (await context.IndexGenerations.SingleAsync(generation => generation.Id == draft.Id)).IndexPath);
    }

    [NativeSqlServerFact]
    public async Task Zero_vector_unplaced_embed_draft_with_valid_provenance_remains_healthy()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAsync(string.Empty);
        var active = await environment.ActiveGenerationAsync();
        await using var context = await environment.Factory.CreateDbContextAsync();
        var draft = await context.IndexGenerations.AsNoTracking().SingleAsync(generation =>
            generation.Id != active.Id && generation.IndexPath == string.Empty && generation.VectorCount == 0);
        var vectorReferenceCount = await context.Vectors.CountAsync(vector => vector.IndexGenerationId == draft.Id);
        var membershipCount = await context.IndexGenerationVectors.CountAsync(item => item.GenerationId == draft.Id);
        var activePath = active.IndexPath;
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        var recoveryStore = provider.GetRequiredService<IDerivedIndexRecoveryStore>();

        await coordinator.RunOnceAsync(CancellationToken.None);

        var snapshot = await recoveryStore.ReadActiveAsync(CancellationToken.None);
        Assert.Equal(0, vectorReferenceCount);
        Assert.Equal(0, membershipCount);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.DoesNotContain(string.Empty, snapshot.ReferencedIndexPaths);
        Assert.Contains(draft.Id, snapshot.ReferencedGenerationIds);
        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.Equal(activePath, (await environment.Store.GetGenerationAsync(active.Id, CancellationToken.None))!.IndexPath);
    }

    [NativeSqlServerFact]
    public async Task Zero_vector_unplaced_embed_draft_allows_each_publish_worker_state()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAsync(string.Empty);
        var active = await environment.ActiveGenerationAsync();
        await using var context = await environment.Factory.CreateDbContextAsync();
        var draft = await context.IndexGenerations.SingleAsync(candidate =>
            candidate.Id != active.Id && candidate.IndexPath == string.Empty && candidate.VectorCount == 0);
        var artifact = await context.Artifacts.SingleAsync(candidate =>
            candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draft.Id.ToString("D"));
        var publishJob = await context.Jobs.SingleAsync(job =>
            job.PipelineRecordId == artifact.PipelineRecordId &&
            job.SourceRevision == artifact.SourceRevision &&
            job.Stage == (int)PipelineStage.Publish);
        var publishOutbox = await context.OutboxMessages.SingleAsync(message =>
            message.PipelineRecordId == artifact.PipelineRecordId &&
            message.SourceRevision == artifact.SourceRevision &&
            message.Stage == (int)PipelineStage.Publish);

        foreach (var state in new[]
                 {
                     PublicJobState.WorkerQueued,
                     PublicJobState.WorkerProcessing,
                     PublicJobState.Completed,
                     PublicJobState.Failed
                 })
        {
            publishJob.PublicState = (int)state;
            publishOutbox.DispatchedAtUtc = state is PublicJobState.Completed or PublicJobState.Failed
                ? DateTimeOffset.UtcNow
                : null;
            await context.SaveChangesAsync();
            using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
            Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        }
    }

    [NativeSqlServerFact]
    public async Task Unrecognised_nonzero_embed_draft_variants_require_operator_action_without_mutation()
    {
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var state = await context.IndexState.SingleAsync(candidate => candidate.Id == 1);
            state.ActiveIndexGenerationId = draftId;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).IndexPath = " ";
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).ValidatedAtUtc = DateTimeOffset.UtcNow;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).MetadataChecksum = new string('1', 64);
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var vectorId = await context.Vectors.Where(vector => vector.IndexGenerationId == draftId)
                .Select(vector => vector.VectorId)
                .SingleAsync();
            context.IndexGenerationVectors.Add(new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.IndexGenerationVectorEntity
            {
                GenerationId = draftId,
                VectorId = vectorId
            });
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            artifact.SearchText = Guid.NewGuid().ToString("D");
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            artifact.ContentType = "application/vnd.fluxknowledge.not-an-embedding-set";
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            artifact.Stage = -1;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.PipelineRecords.SingleAsync(record => record.Id == artifact.PipelineRecordId)).CurrentStage =
                (int)PipelineStage.Embed;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.Jobs.SingleAsync(job =>
                    job.PipelineRecordId == artifact.PipelineRecordId &&
                    job.SourceRevision == artifact.SourceRevision &&
                    job.Stage == (int)PipelineStage.Embed)).PublicState = (int)PublicJobState.WorkerQueued;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.Jobs.SingleAsync(job =>
                    job.PipelineRecordId == artifact.PipelineRecordId &&
                    job.SourceRevision == artifact.SourceRevision &&
                    job.Stage == (int)PipelineStage.Publish)).Operation = "not-a-publish-operation";
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.Jobs.SingleAsync(job =>
                    job.PipelineRecordId == artifact.PipelineRecordId &&
                    job.SourceRevision == artifact.SourceRevision &&
                    job.Stage == (int)PipelineStage.Publish)).PublicState = (int)PublicJobState.GpuQueued;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.OutboxMessages.SingleAsync(message =>
                    message.PipelineRecordId == artifact.PipelineRecordId &&
                    message.SourceRevision == artifact.SourceRevision &&
                    message.Stage == (int)PipelineStage.Embed)).DispatchedAtUtc = null;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.Jobs.SingleAsync(job =>
                    job.PipelineRecordId == artifact.PipelineRecordId &&
                    job.SourceRevision == artifact.SourceRevision &&
                    job.Stage == (int)PipelineStage.Publish)).PublicState = (int)PublicJobState.Completed;
            (await context.OutboxMessages.SingleAsync(message =>
                    message.PipelineRecordId == artifact.PipelineRecordId &&
                    message.SourceRevision == artifact.SourceRevision &&
                    message.Stage == (int)PipelineStage.Publish)).DispatchedAtUtc = null;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.Jobs.SingleAsync(job =>
                    job.PipelineRecordId == artifact.PipelineRecordId &&
                    job.SourceRevision == artifact.SourceRevision &&
                    job.Stage == (int)PipelineStage.Publish)).PublicState = (int)PublicJobState.WorkerProcessing;
            (await context.OutboxMessages.SingleAsync(message =>
                    message.PipelineRecordId == artifact.PipelineRecordId &&
                    message.SourceRevision == artifact.SourceRevision &&
                    message.Stage == (int)PipelineStage.Publish)).DispatchedAtUtc = DateTimeOffset.UtcNow;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.OutboxMessages.SingleAsync(message =>
                    message.PipelineRecordId == artifact.PipelineRecordId &&
                    message.SourceRevision == artifact.SourceRevision &&
                    message.Stage == (int)PipelineStage.Publish)).DispatchGeneration++;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).VectorCount++;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.Vectors.SingleAsync(vector => vector.IndexGenerationId == draftId)).ModelFingerprint =
                "incompatible-draft-fingerprint";
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.Vectors.SingleAsync(vector => vector.IndexGenerationId == draftId)).Dimensions++;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.Vectors.SingleAsync(vector => vector.IndexGenerationId == draftId)).IsDeleted = true;
        });
    }

    [NativeSqlServerFact]
    public async Task Cross_record_vector_provenance_in_an_unplaced_draft_requires_operator_action_without_mutation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAsync("second source");
        Guid draftId;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var drafts = await context.IndexGenerations
                .Where(candidate => candidate.IndexPath == string.Empty && candidate.VectorCount > 0)
                .OrderBy(candidate => candidate.CreatedAtUtc)
                .ToListAsync();
            var target = drafts[0];
            var source = drafts[1];
            var sourceVector = await context.Vectors.SingleAsync(vector => vector.IndexGenerationId == source.Id);
            context.Vectors.Add(new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.VectorEntity
            {
                TextChunkId = sourceVector.TextChunkId,
                ModelFingerprint = sourceVector.ModelFingerprint,
                Dimensions = sourceVector.Dimensions,
                Values = sourceVector.Values.ToArray(),
                TextChunkContentHash = sourceVector.TextChunkContentHash,
                PayloadChecksum = sourceVector.PayloadChecksum,
                SourceRevision = sourceVector.SourceRevision,
                IsDeleted = false,
                IndexGenerationId = target.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            target.VectorCount++;
            draftId = target.Id;
            await context.SaveChangesAsync();
        }

        await AssertUnrecognisedDraftDoesNotMutateAsync(environment, draftId);
    }

    [NativeSqlServerFact]
    public async Task Source_revision_mismatch_in_an_unplaced_draft_requires_operator_action_without_mutation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAtPathAsync("second source revision", "initial.txt");
        Guid draftId;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var drafts = await context.IndexGenerations
                .Where(candidate => candidate.IndexPath == string.Empty && candidate.VectorCount > 0)
                .ToListAsync();
            var draftArtifacts = await context.Artifacts
                .Where(candidate => candidate.Stage == (int)PipelineStage.Embed)
                .ToListAsync();
            var ordered = drafts
                .Select(draft => new
                {
                    Draft = draft,
                    Artifact = draftArtifacts.Single(candidate =>
                        candidate.SearchText == draft.Id.ToString("D"))
                })
                .OrderBy(candidate => candidate.Artifact.SourceRevision)
                .ToArray();
            Assert.Equal(2, ordered.Length);
            Assert.Equal(1, ordered[0].Artifact.SourceRevision);
            Assert.Equal(2, ordered[1].Artifact.SourceRevision);

            var target = ordered[0];
            var source = ordered[1];
            var sourceVector = await context.Vectors.SingleAsync(vector => vector.IndexGenerationId == source.Draft.Id);
            context.Vectors.Add(new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.VectorEntity
            {
                TextChunkId = sourceVector.TextChunkId,
                ModelFingerprint = sourceVector.ModelFingerprint,
                Dimensions = sourceVector.Dimensions,
                Values = sourceVector.Values.ToArray(),
                TextChunkContentHash = sourceVector.TextChunkContentHash,
                PayloadChecksum = sourceVector.PayloadChecksum,
                SourceRevision = sourceVector.SourceRevision,
                IsDeleted = false,
                IndexGenerationId = target.Draft.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            target.Draft.VectorCount++;
            draftId = target.Draft.Id;
            await context.SaveChangesAsync();
        }

        await AssertUnrecognisedDraftDoesNotMutateAsync(environment, draftId);
    }

    [NativeSqlServerFact]
    public async Task Missing_zero_vector_embed_evidence_requires_operator_action_without_mutation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAsync(string.Empty);
        Guid draftId;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var draft = await context.IndexGenerations.SingleAsync(candidate =>
                candidate.IndexPath == string.Empty && candidate.VectorCount == 0);
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draft.Id.ToString("D"));
            artifact.ContentHash = Convert.ToHexStringLower(SHA256.HashData("not-empty"u8));
            draftId = draft.Id;
            await context.SaveChangesAsync();
        }

        await AssertUnrecognisedDraftDoesNotMutateAsync(environment, draftId);
    }

    [NativeSqlServerFact]
    public async Task Unrecognised_zero_vector_embed_draft_variants_require_operator_action_without_mutation()
    {
        await AssertUnrecognisedZeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).ModelFingerprint =
                "incompatible-zero-draft-fingerprint";
        });
        await AssertUnrecognisedZeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).Dimensions++;
        });
        await AssertUnrecognisedZeroDraftAsync(async (context, draftId) =>
        {
            var embedArtifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            var canonicalArtifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.PipelineRecordId == embedArtifact.PipelineRecordId &&
                candidate.SourceRevision == embedArtifact.SourceRevision &&
                candidate.Stage == (int)PipelineStage.CanonicalIndex);
            context.TextChunks.Add(new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.TextChunkEntity
            {
                ArtifactId = canonicalArtifact.Id,
                SourceRevision = embedArtifact.SourceRevision,
                Ordinal = 0,
                StartOffset = 0,
                Length = 1,
                Content = "x",
                ContentHash = Convert.ToHexStringLower(SHA256.HashData("x"u8))
            });
        });
    }

    [NativeSqlServerFact]
    public async Task Worker_produced_vector_round_trips_through_hybrid_search_and_preserves_stale_chunk_protection()
    {
        const string sourceText = "restart the native worker safely";
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, sourceText);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var vector = await context.Vectors.SingleAsync();
        var chunk = await context.TextChunks.SingleAsync(candidate => candidate.Id == vector.TextChunkId);

        Assert.Equal(chunk.ContentHash, vector.TextChunkContentHash);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(vector.Values)),
            vector.PayloadChecksum);
        Assert.NotEqual(vector.TextChunkContentHash, vector.PayloadChecksum);

        var lexical = new SqlFullTextSearch(environment.Factory);
        IReadOnlyList<RankedCandidate> lexicalCandidates = [];
        var fullTextDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (lexicalCandidates.Count == 0 && DateTimeOffset.UtcNow < fullTextDeadline)
        {
            lexicalCandidates = await lexical.SearchAsync("restart", 5, CancellationToken.None);
            if (lexicalCandidates.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }

        Assert.Contains(lexicalCandidates, candidate => candidate.VectorId == vector.VectorId);

        var search = new HybridSearchService(
            lexical,
            new UsearchNearestNeighbourQuery(environment.Embeddings, environment.Reader),
            new SqlSearchHydrator(environment.Factory),
            environment.Store);
        var response = await search.SearchAsync(
            new SearchRequest("restart", 5, "local_first", null, null, null),
            CancellationToken.None);
        var hit = Assert.Single(response.Results);
        Assert.Contains(sourceText, hit.Snippet, StringComparison.Ordinal);
        Assert.Contains(hit.Explanation, item => item.StartsWith("lexical:", StringComparison.Ordinal));
        Assert.Contains(hit.Explanation, item => item.StartsWith("semantic:", StringComparison.Ordinal));

        vector.TextChunkContentHash = new string('f', 64);
        await context.SaveChangesAsync();

        var staleResponse = await search.SearchAsync(
            new SearchRequest("restart", 5, "local_first", null, null, null),
            CancellationToken.None);

        Assert.Empty(staleResponse.Results);
    }

    [NativeSqlServerFact]
    public async Task Second_corpus_publish_retains_vectors_from_two_independent_current_sources()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "alpha source");
        await environment.AddAndPumpAsync("bravo source");
        await using var context = await environment.Factory.CreateDbContextAsync();
        var activeId = (await context.IndexState.SingleAsync(state => state.Id == 1)).ActiveIndexGenerationId;
        var membership = await context.IndexGenerationVectors
            .Where(member => member.GenerationId == activeId)
            .Select(member => member.VectorId)
            .OrderBy(id => id)
            .ToListAsync();
        var allVectors = await context.Vectors.OrderBy(vector => vector.VectorId).Select(vector => vector.VectorId).ToListAsync();

        Assert.Equal(allVectors, membership);
        Assert.True(membership.Count >= 2);
    }

    [NativeSqlServerFact]
    public async Task Stale_snapshot_refuses_and_rolls_back_publication_without_pointer_regression()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        var stale = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        await environment.AddAndPumpAsync("second source");
        var active = await environment.ActiveGenerationAsync();
        var transition = new SqlStageTransitionStore(environment.Factory);
        var request = await ClaimPublishAsync(environment, stale);

        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(async () =>
            await transition.TransitionAsync(request, CancellationToken.None));
        await using var context = await environment.Factory.CreateDbContextAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == request.CurrentJob.PipelineRecordId.Value);

        Assert.False(record.CompletionCriteriaMet);
        Assert.False(await context.Artifacts.AnyAsync(artifact => artifact.Id == request.Artifact.Id));
        Assert.Equal((int)PublicJobState.WorkerProcessing,
            (await context.Jobs.SingleAsync(job => job.Id == request.CurrentJob.JobId.Value)).PublicState);
        Assert.NotEqual(stale.Generation.Id, active.Id);
        Assert.True(File.Exists(Path.Combine(stale.Generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Completed_publish_replay_does_not_duplicate_membership_or_replace_a_valid_placement()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "replay source");
        var active = await environment.ActiveGenerationAsync();
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var transition = new SqlStageTransitionStore(environment.Factory);
        var request = await ClaimPublishAsync(environment, candidate);
        var first = await transition.TransitionAsync(request, CancellationToken.None);
        var replay = await transition.TransitionAsync(request, CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var members = await context.IndexGenerationVectors.Where(member => member.GenerationId == active.Id).ToListAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == request.CurrentJob.PipelineRecordId.Value);

        Assert.False(first.ExistingTransition);
        Assert.True(replay.ExistingTransition);
        Assert.True(record.CompletionCriteriaMet);
        Assert.Equal(first.ArtifactId, replay.ArtifactId);
        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.Equal(members.Select(member => member.VectorId).Distinct().Count(), members.Count);
        Assert.True(File.Exists(Path.Combine(candidate.Generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
        await environment.AddAndPumpAsync("Replay successor publication.");
        var successorId = await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None);
        var lateReplay = await transition.TransitionAsync(request with
        {
            IndexingOutput = request.IndexingOutput! with { ExpectedCorpusStamp = null,
                ActivateGeneration = request.IndexingOutput!.ActivateGeneration! with { CorpusStamp = null } }
        }, CancellationToken.None);
        Assert.True(lateReplay.ExistingTransition);
        Assert.Equal(first.ArtifactId, lateReplay.ArtifactId);
        Assert.Equal(successorId, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Failed_terminal_publish_rolls_back_the_completion_flag()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "failed publish source");
        var active = await environment.ActiveGenerationAsync();
        var vectors = await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None);
        var incompatible = active with { IndexPath = active.IndexPath + "-incompatible" };
        var request = await ClaimPublishAsync(
            environment,
            new IndexGenerationCandidateSnapshot(incompatible, vectors, active.CorpusStamp));

        await Assert.ThrowsAsync<IndexGenerationStaleException>(
            async () => await new SqlStageTransitionStore(environment.Factory)
                .TransitionAsync(request, CancellationToken.None));

        await using var context = await environment.Factory.CreateDbContextAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == request.CurrentJob.PipelineRecordId.Value);

        Assert.False(record.CompletionCriteriaMet);
        Assert.DoesNotContain(
            await context.Artifacts.ToListAsync(),
            artifact => artifact.Id == request.Artifact.Id);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_same_candidate_activation_creates_one_generation_and_membership_snapshot()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "concurrent source");
        await PrepareUntrackedCandidateAsync(environment);
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var barrier = new ActivationBarrier();
        var first = new SqlStageTransitionStore(environment.Factory, barrier);
        var second = new SqlStageTransitionStore(environment.Factory, barrier);
        var firstRequest = await ClaimPublishAsync(environment, candidate);
        var secondRequest = await ClaimPublishAsync(environment, candidate);

        var firstTransition = first.TransitionAsync(firstRequest, CancellationToken.None).AsTask();
        var secondTransition = second.TransitionAsync(secondRequest, CancellationToken.None).AsTask();
        var barrierReached = await Task.WhenAny(
            barrier.ArtifactWritten.Task,
            Task.Delay(TimeSpan.FromSeconds(10)));
        barrier.Release();
        var transitions = await Task.WhenAll(firstTransition, secondTransition);

        Assert.Same(barrier.ArtifactWritten.Task, barrierReached);
        Assert.All(transitions, transition => Assert.False(transition.ExistingTransition));
        Assert.Equal(candidate.Generation.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await context.IndexGenerations.Where(generation => generation.Id == candidate.Generation.Id).ToListAsync());
        Assert.Equal(candidate.Vectors.Count, await context.IndexGenerationVectors.CountAsync(
            membership => membership.GenerationId == candidate.Generation.Id));
    }

    [NativeSqlServerFact]
    public async Task Existing_generation_with_empty_membership_is_repaired_idempotently()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "empty membership source");
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            await context.IndexGenerationVectors
                .Where(membership => membership.GenerationId == candidate.Generation.Id)
                .ExecuteDeleteAsync();
        }

        await new SqlStageTransitionStore(environment.Factory).TransitionAsync(
            await ClaimPublishAsync(environment, candidate),
            CancellationToken.None);

        await using var verification = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await verification.IndexGenerations.Where(generation => generation.Id == candidate.Generation.Id).ToListAsync());
        Assert.Equal(candidate.Vectors.Count, await verification.IndexGenerationVectors.CountAsync(
            membership => membership.GenerationId == candidate.Generation.Id));
    }

    private static async Task PrepareUntrackedCandidateAsync(PipelineEnvironment environment)
    {
        var active = await environment.ActiveGenerationAsync();
        await using var context = await environment.Factory.CreateDbContextAsync();
        var origin = new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.IndexGenerationEntity
        {
            Id = Guid.NewGuid(),
            ModelFingerprint = active.ModelFingerprint,
            Dimensions = active.Dimensions,
            IndexPath = active.IndexPath,
            MetadataChecksum = active.MetadataChecksum,
            VectorCount = active.VectorCount,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ValidatedAtUtc = DateTimeOffset.UtcNow
        };
        context.IndexGenerations.Add(origin);
        await context.SaveChangesAsync();
        var vectors = await context.Vectors.ToListAsync();
        foreach (var vector in vectors)
        {
            vector.IndexGenerationId = origin.Id;
        }
        var state = await context.IndexState.SingleAsync(candidate => candidate.Id == 1);
        state.ActiveIndexGenerationId = null;
        await context.SaveChangesAsync();
        await context.IndexGenerationVectors
            .Where(membership => membership.GenerationId == active.Id)
            .ExecuteDeleteAsync();
        await context.IndexGenerations
            .Where(generation => generation.Id == active.Id)
            .ExecuteDeleteAsync();
    }

    private async Task<StageTransitionRequest> ClaimPublishAsync(
        PipelineEnvironment environment,
        IndexGenerationCandidateSnapshot candidate)
    {
        var now = DateTimeOffset.UtcNow;
        await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null,
            stage: PipelineStage.Publish,
            operation: PipelineOperations.Publish);
        var outbox = await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync(
            "task-5-publish-dispatcher",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(2),
            [PipelineOperations.Publish],
            CancellationToken.None);
        Assert.NotNull(outbox);
        var job = await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(
            outbox!,
            "task-5-publish-worker",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        Assert.NotNull(job);
        return new StageTransitionRequest(
            outbox!,
            job!,
            new StageArtifact(
                Guid.NewGuid(),
                PipelineStage.Publish,
                candidate.Generation.MetadataChecksum,
                "application/vnd.fluxknowledge.usearch-generation",
                candidate.Generation.Id.ToString("N"),
                now),
            null,
            null,
            nameof(SqlToUsearchRebuildTests),
            new IndexingStageOutput(
                ActivateGeneration: candidate.Generation,
                ActivateMembership: candidate.Vectors,
                ExpectedCorpusStamp: candidate.ExpectedCorpusStamp));
    }

    private async Task AssertUnrecognisedNonzeroDraftAsync(
        Func<FluxKnowledgeDbContext, Guid, Task> mutate)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        Guid draftId;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var draft = await context.IndexGenerations.SingleAsync(candidate =>
                candidate.IndexPath == string.Empty && candidate.VectorCount > 0);
            draftId = draft.Id;
            await mutate(context, draftId);
            await context.SaveChangesAsync();
        }

        await AssertUnrecognisedDraftDoesNotMutateAsync(environment, draftId);
    }

    private async Task AssertUnrecognisedZeroDraftAsync(
        Func<FluxKnowledgeDbContext, Guid, Task> mutate)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAsync(string.Empty);
        Guid draftId;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var draft = await context.IndexGenerations.SingleAsync(candidate =>
                candidate.IndexPath == string.Empty && candidate.VectorCount == 0);
            draftId = draft.Id;
            await mutate(context, draftId);
            await context.SaveChangesAsync();
        }

        await AssertUnrecognisedDraftDoesNotMutateAsync(environment, draftId);
    }

    private static async Task AssertUnrecognisedDraftDoesNotMutateAsync(
        PipelineEnvironment environment,
        Guid draftId)
    {
        var activeIdBefore = await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None);
        Assert.NotNull(activeIdBefore);
        var activeBefore = await environment.Store.GetGenerationAsync(activeIdBefore!.Value, CancellationToken.None);
        Assert.NotNull(activeBefore);
        DraftState draftBefore;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            draftBefore = await context.IndexGenerations.AsNoTracking()
                .Where(candidate => candidate.Id == draftId)
                .Select(candidate => new DraftState(
                    candidate.ModelFingerprint,
                    candidate.Dimensions,
                    candidate.IndexPath,
                    candidate.MetadataChecksum,
                    candidate.VectorCount,
                    candidate.ValidatedAtUtc))
                .SingleAsync();
        }
        var evidenceBefore = await ReadRecoveryEvidenceSnapshotAsync(environment.Factory);

        var staging = Path.Combine(environment.IndexRoot, "staging");
        var quarantine = Path.Combine(environment.IndexRoot, "quarantine");
        var stagingBefore = ReadDirectoryEntries(staging);
        var quarantineBefore = ReadDirectoryEntries(quarantine);
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

        await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
        Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid, coordinator.Snapshot.FailureCategory);
        Assert.Null(coordinator.Snapshot.NextRetryAtUtc);
        Assert.Equal(activeIdBefore, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.Equal(activeBefore!.IndexPath,
            (await environment.Store.GetGenerationAsync(activeIdBefore.Value, CancellationToken.None))!.IndexPath);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var draftAfter = await context.IndexGenerations.AsNoTracking()
                .Where(candidate => candidate.Id == draftId)
                .Select(candidate => new DraftState(
                    candidate.ModelFingerprint,
                    candidate.Dimensions,
                    candidate.IndexPath,
                    candidate.MetadataChecksum,
                    candidate.VectorCount,
                    candidate.ValidatedAtUtc))
                .SingleAsync();
            Assert.Equal(draftBefore, draftAfter);
        }
        var evidenceAfter = await ReadRecoveryEvidenceSnapshotAsync(environment.Factory);
        AssertRecoveryEvidenceUnchanged(evidenceBefore, evidenceAfter);

        Assert.Equal(stagingBefore, ReadDirectoryEntries(staging));
        Assert.Equal(quarantineBefore, ReadDirectoryEntries(quarantine));
    }

    private static string[] ReadDirectoryEntries(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                .Select(entry => Path.GetRelativePath(path, entry))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static ServiceProvider CreateRecoveryProvider(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        string root,
        IDeploymentValidationHold? hold = null)
    {
        var services = new ServiceCollection();
        if (hold is not null) services.AddSingleton(hold);
        services.AddSingleton(factory);
        services.AddSingleton<IDerivedIndexRecoveryStore, SqlDerivedIndexRecoveryStore>();
        services.AddScoped<SqlPipelineStore>();
        services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<SqlPipelineStore>());
        services.AddSingleton(UsearchIndexOptions.FromConfiguredRoot(root));
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddScoped<UsearchGenerationBuilder>();
        services.AddSingleton<DerivedIndexFileSystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DerivedIndexRecoveryCoordinator>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed record DraftState(
        string ModelFingerprint,
        int Dimensions,
        string IndexPath,
        string MetadataChecksum,
        long VectorCount,
        DateTimeOffset? ValidatedAtUtc);

    private static async Task<RecoveryEvidenceSnapshot> ReadRecoveryEvidenceSnapshotAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync();
        var indexStates = await context.IndexState.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var generations = await context.IndexGenerations.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var records = await context.PipelineRecords.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var artifacts = await context.Artifacts.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var chunks = await context.TextChunks.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var vectors = await context.Vectors.AsNoTracking().OrderBy(candidate => candidate.VectorId).ToListAsync();
        var memberships = await context.IndexGenerationVectors.AsNoTracking()
            .OrderBy(candidate => candidate.GenerationId)
            .ThenBy(candidate => candidate.VectorId)
            .ToListAsync();
        var jobs = await context.Jobs.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var outbox = await context.OutboxMessages.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();

        return new RecoveryEvidenceSnapshot(
            indexStates.Select(candidate => new IndexStateEvidence(
                candidate.Id,
                candidate.ActiveIndexGenerationId,
                candidate.UpdatedAtUtc,
                HashBytes(candidate.RowVersion))).ToArray(),
            generations.Select(candidate => new GenerationEvidence(
                candidate.Id,
                candidate.ModelFingerprint,
                candidate.Dimensions,
                candidate.IndexPath,
                candidate.MetadataChecksum,
                candidate.VectorCount,
                candidate.CreatedAtUtc,
                candidate.ValidatedAtUtc,
                HashBytes(candidate.RowVersion))).ToArray(),
            records.Select(candidate => new PipelineRecordEvidence(
                candidate.Id,
                candidate.SourceIdentityId,
                candidate.Revision,
                candidate.ContentHash,
                candidate.RootLineageRecordId,
                candidate.ParentRevisionRecordId,
                candidate.CurrentStage,
                candidate.CompletionCriteriaMet,
                candidate.IsDeleted,
                candidate.RegisteredAtUtc,
                HashBytes(candidate.RowVersion))).ToArray(),
            artifacts.Select(candidate => new ArtifactEvidence(
                candidate.Id,
                candidate.PipelineRecordId,
                candidate.SourceRevision,
                candidate.Stage,
                candidate.ContentHash,
                candidate.ContentType,
                candidate.SearchText,
                candidate.CreatedAtUtc)).ToArray(),
            chunks.Select(candidate => new ChunkEvidence(
                candidate.Id,
                candidate.ArtifactId,
                candidate.SourceRevision,
                candidate.Ordinal,
                candidate.StartOffset,
                candidate.Length,
                HashText(candidate.Content),
                candidate.ContentHash)).ToArray(),
            vectors.Select(candidate => new VectorEvidence(
                candidate.VectorId,
                candidate.TextChunkId,
                candidate.ModelFingerprint,
                candidate.Dimensions,
                HashBytes(candidate.Values),
                candidate.TextChunkContentHash,
                candidate.PayloadChecksum,
                candidate.SourceRevision,
                candidate.IsDeleted,
                candidate.IndexGenerationId,
                candidate.CreatedAtUtc,
                HashBytes(candidate.RowVersion))).ToArray(),
            memberships.Select(candidate => new MembershipEvidence(candidate.GenerationId, candidate.VectorId)).ToArray(),
            jobs.Select(candidate => new JobEvidence(
                candidate.Id,
                candidate.PipelineRecordId,
                candidate.SourceRevision,
                candidate.Stage,
                candidate.Operation,
                candidate.PublicState,
                candidate.DueAtUtc,
                candidate.AttemptCount,
                candidate.LeaseOwner,
                candidate.LeaseExpiresAtUtc,
                candidate.LeaseGeneration,
                HashText(candidate.Reason),
                HashText(candidate.ErrorDetails),
                HashBytes(candidate.RowVersion))).ToArray(),
            outbox.Select(candidate => new OutboxEvidence(
                candidate.Id,
                candidate.PipelineRecordId,
                candidate.SourceRevision,
                candidate.Stage,
                candidate.Operation,
                candidate.DispatchGeneration,
                candidate.IdempotencyKey,
                candidate.DueAtUtc,
                candidate.CreatedAtUtc,
                candidate.DispatchedAtUtc,
                candidate.LeaseOwner,
                candidate.LeaseExpiresAtUtc,
                candidate.LeaseGeneration,
                HashBytes(candidate.RowVersion))).ToArray());
    }

    private static void AssertRecoveryEvidenceUnchanged(
        RecoveryEvidenceSnapshot before,
        RecoveryEvidenceSnapshot after)
    {
        Assert.Equal(before.IndexStates, after.IndexStates);
        Assert.Equal(before.Generations, after.Generations);
        Assert.Equal(before.PipelineRecords, after.PipelineRecords);
        Assert.Equal(before.Artifacts, after.Artifacts);
        Assert.Equal(before.Chunks, after.Chunks);
        Assert.Equal(before.Vectors, after.Vectors);
        Assert.Equal(before.Memberships, after.Memberships);
        Assert.Equal(before.Jobs, after.Jobs);
        Assert.Equal(before.Outbox, after.Outbox);
    }

    private static string HashBytes(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private static string? HashText(string? value) =>
        value is null ? null : HashBytes(System.Text.Encoding.UTF8.GetBytes(value));

    private sealed record RecoveryEvidenceSnapshot(
        IReadOnlyList<IndexStateEvidence> IndexStates,
        IReadOnlyList<GenerationEvidence> Generations,
        IReadOnlyList<PipelineRecordEvidence> PipelineRecords,
        IReadOnlyList<ArtifactEvidence> Artifacts,
        IReadOnlyList<ChunkEvidence> Chunks,
        IReadOnlyList<VectorEvidence> Vectors,
        IReadOnlyList<MembershipEvidence> Memberships,
        IReadOnlyList<JobEvidence> Jobs,
        IReadOnlyList<OutboxEvidence> Outbox);

    private sealed record IndexStateEvidence(
        int Id,
        Guid? ActiveGenerationId,
        DateTimeOffset UpdatedAtUtc,
        string RowVersion);

    private sealed record GenerationEvidence(
        Guid Id,
        string ModelFingerprint,
        int Dimensions,
        string IndexPath,
        string MetadataChecksum,
        long VectorCount,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? ValidatedAtUtc,
        string RowVersion);

    private sealed record PipelineRecordEvidence(
        Guid Id,
        Guid SourceIdentityId,
        long Revision,
        string ContentHash,
        Guid RootLineageRecordId,
        Guid? ParentRevisionRecordId,
        int CurrentStage,
        bool CompletionCriteriaMet,
        bool IsDeleted,
        DateTimeOffset RegisteredAtUtc,
        string RowVersion);

    private sealed record ArtifactEvidence(
        Guid Id,
        Guid PipelineRecordId,
        long SourceRevision,
        int Stage,
        string ContentHash,
        string ContentType,
        string SearchText,
        DateTimeOffset CreatedAtUtc);

    private sealed record ChunkEvidence(
        long Id,
        Guid ArtifactId,
        long SourceRevision,
        int Ordinal,
        int StartOffset,
        int Length,
        string? ContentHashEvidence,
        string ContentHash);

    private sealed record VectorEvidence(
        long Id,
        long TextChunkId,
        string ModelFingerprint,
        int Dimensions,
        string ValuesHash,
        string TextChunkContentHash,
        string PayloadChecksum,
        long SourceRevision,
        bool IsDeleted,
        Guid GenerationId,
        DateTimeOffset CreatedAtUtc,
        string RowVersion);

    private sealed record MembershipEvidence(Guid GenerationId, long VectorId);

    private sealed record JobEvidence(
        Guid Id,
        Guid PipelineRecordId,
        long SourceRevision,
        int Stage,
        string Operation,
        int PublicState,
        DateTimeOffset DueAtUtc,
        int AttemptCount,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAtUtc,
        long LeaseGeneration,
        string? ReasonHash,
        string? ErrorDetailsHash,
        string RowVersion);

    private sealed record OutboxEvidence(
        Guid Id,
        Guid PipelineRecordId,
        long SourceRevision,
        int Stage,
        string Operation,
        long DispatchGeneration,
        string IdempotencyKey,
        DateTimeOffset DueAtUtc,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? DispatchedAtUtc,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAtUtc,
        long LeaseGeneration,
        string RowVersion);

    private sealed class ThrowingValidator : UsearchGenerationValidator
    {
        public override void Validate(string directory, IndexGenerationDescriptor expected, IReadOnlyList<CanonicalVector> vectors) =>
            throw new IndexGenerationValidationException("injected candidate validation failure");
    }

    private sealed class ActivationBarrier : IStageTransitionFailureInjector
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ArtifactWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask AfterArtifactWrittenAsync(CancellationToken cancellationToken)
        {
            ArtifactWritten.TrySetResult(true);

            return new ValueTask(_release.Task.WaitAsync(cancellationToken));
        }

        public void Release() => _release.TrySetResult(true);
    }

    public sealed class PipelineEnvironment : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _ingressRoot;
        private readonly string _artifactRoot;
        private PipelineEnvironment(ServiceProvider provider, string ingressRoot, string artifactRoot, string indexRoot, IDbContextFactory<FluxKnowledgeDbContext> factory)
        {
            _provider = provider; _ingressRoot = ingressRoot; _artifactRoot = artifactRoot; IndexRoot = indexRoot; Factory = factory;
            Store = new SqlPipelineStore(factory); Builder = _provider.GetRequiredService<UsearchGenerationBuilder>();
            Reader = _provider.GetRequiredService<UsearchAnnIndex>(); Embeddings = _provider.GetRequiredService<IEmbeddingProvider>();
        }
        public string IndexRoot { get; }
        public string ArtifactRoot => _artifactRoot;
        public IDbContextFactory<FluxKnowledgeDbContext> Factory { get; }
        public SqlPipelineStore Store { get; }
        public UsearchGenerationBuilder Builder { get; }
        public UsearchAnnIndex Reader { get; }
        public IEmbeddingProvider Embeddings { get; }
        public IDeploymentValidationHold DeploymentHold => _provider.GetRequiredService<IDeploymentValidationHold>();
        public void PermitRebuild(Guid operationId) => ((RebuildTestHold)DeploymentHold).OperationId = operationId;
        public void ReleaseRebuild() => ((RebuildTestHold)DeploymentHold).OperationId = null;
        public FluxKnowledge.Application.Contracts.RegisterUtf8FileResult? LastReceipt { get; private set; }

        public static async Task<PipelineEnvironment> CreateAsync(NativeSqlServerFixture fixture, string text,
            PassageBuilder? passageBuilder = null, bool publish = true, bool embed = true, TimeProvider? clock = null)
        {
            await SqlTestData.ClearPipelineAsync(fixture);
            var ingress = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIngress_{Guid.NewGuid():N}");
            var artifact = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRetained_{Guid.NewGuid():N}");
            var index = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}");
            Directory.CreateDirectory(ingress);
            Directory.CreateDirectory(artifact);
            var services = new ServiceCollection();
            services.AddSingleton<IDeploymentValidationHold>(new RebuildTestHold());
            services.AddSingleton(SqlTestData.CreateFactory(fixture));
            if (clock is not null) services.AddSingleton(clock);
            if (passageBuilder is not null) services.AddSingleton(passageBuilder);
            services.AddSingleton<IUtf8FileSourceReader>(new Utf8FileSourceReader(new LocalIngressOptions([ingress])));
            services.AddScoped<IRetainedSourceReader>(provider => new SqlRetainedSourceReader(
                provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(), artifact));
            services.AddFluxKnowledgeOutboxWorkers();
            services.AddSingleton<IEmbeddingProvider, DeterministicTokenHashEmbeddingProvider>();
            services.AddFluxKnowledgeUsearch(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Usearch:RootPath"] = index }).Build());
            services.AddScoped<IStageWorker, CanonicalIndexStageWorker>();
            if (embed) services.AddScoped<IStageWorker, EmbedStageWorker>();
            if (publish) services.AddScoped<IStageWorker, PublishStageWorker>();
            var provider = services.BuildServiceProvider();
            var environment = new PipelineEnvironment(provider, ingress, artifact, index, provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>());
            await environment.AddAndPumpAtPathAsync(text, "initial.txt");
            return environment;
        }

        public async Task AddAndPumpAsync(string text)
        {
            await AddAndPumpAtPathAsync(text, $"{Guid.NewGuid():N}.txt");
        }

        public async Task AddAndPumpAtPathAsync(string text, string fileName, bool pump = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            {
                throw new ArgumentException("The test ingress file name must not contain a directory.", nameof(fileName));
            }

            var path = Path.Combine(_ingressRoot, fileName);
            await File.WriteAllTextAsync(path, text);
            using var scope = _provider.CreateScope();
            LastReceipt = await scope.ServiceProvider.GetRequiredService<RegisterUtf8FileHandler>().HandleAsync(new(path, "native-sql-test", null), CancellationToken.None);
            if (pump) await PumpAsync();
        }

        public Task PumpAsync() => _provider.GetRequiredService<OutboxPumpService>().PumpOnceAsync(CancellationToken.None).AsTask();

        private sealed class RebuildTestHold : IDeploymentValidationHold
        {
            public Guid? OperationId { get; set; }
            public bool IsHeld => OperationId is not null;
            public DeploymentHoldAdmission ReadAdmissionState() => new(IsHeld, OperationId);
            public ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        }

        public async Task<Guid> AddRetainedAndPumpAsync(string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var rootId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(_artifactRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(_artifactRoot, relative), bytes);
            var activity = SourceActivity.Create(new SourceRevisionId(revisionId), SourceActivityKind.TextExtraction,
                ExecutionClass.InProcess, "phase-3a-v1", hash, null, null);
            var now = DateTimeOffset.UtcNow;
            await using (var context = await Factory.CreateDbContextAsync())
            {
                context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
                {
                    Id = rootId, CanonicalPath = $"C:\\retained-rebuild\\{rootId:N}", DisplayName = "Retained", State = 0,
                    Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false,
                    MaximumFileBytes = 16 * 1024 * 1024, AllowedClassificationsJson = "[]", CrawlMode = 0,
                    ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
                });
                context.SourceRevisions.Add(new SourceRevisionEntity
                {
                    Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"retained:{revisionId:N}", Revision = 1,
                    ContentSha256 = hash, CanonicalPath = $"C:\\retained-rebuild\\{revisionId:N}.txt", Classification = "AcceptedUtf8Text",
                    Extension = ".txt", ByteLength = bytes.Length, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}"
                });
                context.SourceArtifacts.Add(new SourceArtifactEntity
                {
                    Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relative,
                    ByteLength = bytes.Length, ChecksumVerifiedAtUtc = now, ReferenceCount = 1
                });
                context.SourceActivities.Add(new SourceActivityEntity
                {
                    Id = activity.Id.Value, SourceRevisionId = revisionId, ActivityKind = (int)activity.Kind,
                    ExecutionClass = (int)activity.ExecutionClass, ProcessorVersion = activity.ProcessorVersion,
                    InputFingerprint = activity.InputFingerprint, State = (int)activity.State, CreatedAtUtc = now, UpdatedAtUtc = now
                });
                await context.SaveChangesAsync();
            }
            using (var scope = _provider.CreateScope())
            {
                Assert.True(await scope.ServiceProvider.GetRequiredService<RetainedTextActivityPlanner>()
                    .PlanAsync(activity, CancellationToken.None));
            }
            await _provider.GetRequiredService<OutboxPumpService>().PumpOnceAsync(CancellationToken.None);
            await using var verification = await Factory.CreateDbContextAsync();
            return (await verification.PipelineRecords.SingleAsync(record => record.SourceRevisionId == revisionId)).Id;
        }

        public async Task<IndexGenerationDescriptor> ActiveGenerationAsync()
        {
            var id = await Store.GetActiveGenerationIdAsync(CancellationToken.None);
            Assert.NotNull(id);
            return (await Store.GetGenerationAsync(id!.Value, CancellationToken.None))!;
        }

        public ValueTask DisposeAsync()
        {
            _provider.Dispose();
            if (Directory.Exists(_ingressRoot)) Directory.Delete(_ingressRoot, true);
            if (Directory.Exists(_artifactRoot)) Directory.Delete(_artifactRoot, true);
            if (Directory.Exists(IndexRoot)) Directory.Delete(IndexRoot, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SyntheticPassageTokenizer : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-word-v1";
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
