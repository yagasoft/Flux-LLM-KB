using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerFact]
    public async Task Segmented_deletion_reports_committed_reclamation_and_resumes_between_segment_and_manifest()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The independent keeper remains published.", segmented: true);
        var recordId = await environment.AddRetainedAndPumpAsync("The temporary rooted source must be removed through every native retirement phase.");
        var operation = Guid.NewGuid(); Guid root;
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            root = await (from record in db.PipelineRecords join revision in db.SourceRevisions on record.SourceRevisionId equals revision.Id
                where record.Id == recordId select revision.SourceRootId).SingleAsync();
            (await db.SourceRootConfigurations.SingleAsync(r => r.Id == root)).State = (int)SourceRootState.Deleting;
            var state = await db.IndexState.SingleAsync(); state.CorpusVersion++;
            db.IndexProjectionChanges.Add(new() { CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion,
                ScopeKind = 3, ScopeId = root, CreatedAtUtc = DateTimeOffset.UtcNow });
            db.SourceDeletionOperations.Add(new() { Id = operation, SourceRootId = root, Phase = "accepted",
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var recovery = Path.Combine(environment.IndexRoot, "test-recovery"); Directory.CreateDirectory(recovery);
        await AuditRecoveryAsync(environment, recovery);
        var store = new SqlSourceDeletionStore(environment.Factory, TimeProvider.System, queryOwnerProbe: new WindowsInteractiveGpuOwnerProbe());
        using var scope = environment.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>();
        Assert.Equal("rebuild-index", (await store.PurgeAsync(await LeaseDeletionFixtureAsync(environment, operation), null, default)).Phase);
        var candidate = await publisher.BuildAndPlaceAsync(Guid.Empty, default);
        Assert.Equal("cleanup-memberships", (await store.PurgeAsync(await LeaseDeletionFixtureAsync(environment, operation), candidate, default)).Phase);
        SourceDeletionCoordinator Coordinator() => new(store, publisher,
            new WindowsSourceDeletionFileStore(environment.ArtifactRoot, environment.IndexRoot),
            new SqlSourceArtifactPublicationGate(environment.Factory), new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System),
            indexMaintenance: new SqlSegmentRetirementOperations(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
                environment.IndexRoot, recovery, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe()));
        var first = Coordinator(); var checkpoint = false;
        for (var attempt = 0; attempt < 24 && !checkpoint; attempt++)
        {
            var progressed = await first.RunOneAsync(default);
            await using var db = await environment.Factory.CreateDbContextAsync();
            var current = await db.SourceDeletionOperations.SingleAsync(d => d.Id == operation);
            if (current.ReasonCode != "source-delete-segment-retirement-progressing") continue;
            Assert.True(progressed);
            Assert.Equal(0, current.State); Assert.Null(current.LeaseId);
            Assert.True(await db.IndexSegments.AnyAsync(s => s.State == 3 && db.SourceDeletionCleanupItems.Any(i =>
                i.SourceDeletionOperationId == operation && i.StorageKind == 4 && i.RelativePath == s.Id)));
            var manifestPaths = await db.SourceDeletionCleanupItems.Where(i => i.SourceDeletionOperationId == operation && i.StorageKind == 3)
                .Select(i => i.RelativePath).ToArrayAsync();
            var manifestIds = manifestPaths.Select(p => Guid.ParseExact(p, "N")).ToArray();
            Assert.True(await db.IndexGenerations.AnyAsync(g => manifestIds.Contains(g.Id) && g.RetiredAtUtc != null && g.ReclaimedAtUtc == null));
            checkpoint = true;
        }
        Assert.True(checkpoint);
        var resumed = Coordinator();
        for (var attempt = 0; attempt < 24; attempt++)
        {
            await using var db = await environment.Factory.CreateDbContextAsync();
            if (await db.SourceDeletionOperations.AnyAsync(d => d.Id == operation && d.State == 3)) break;
            Assert.True(await resumed.RunOneAsync(default));
        }
        await using var verify = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(3, (await verify.SourceDeletionOperations.SingleAsync(d => d.Id == operation)).State);
        Assert.False(await verify.SourceRootConfigurations.AnyAsync(r => r.Id == root));
        Assert.False(await verify.PipelineRecords.AnyAsync(r => r.Id == recordId));
        await AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerFact]
    public async Task Segmented_overlapping_deletions_replay_shared_manifest_and_segment_targets_without_a_pin_cycle()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The independent control remains published.", segmented: true);
        var first = await environment.AddRetainedAndPumpAsync("The first root contributes a passage to a shared compacted segment.");
        var second = await environment.AddRetainedAndPumpAsync("The second root contributes a passage to the same compacted segment.");
        await PackSegmentedFixtureAsync(environment);
        await AssertSegmentedOracleAsync(environment);
        Guid[] operations;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var roots = await (from record in context.PipelineRecords join revision in context.SourceRevisions on record.SourceRevisionId equals revision.Id
                where record.Id == first || record.Id == second select revision.SourceRootId).ToArrayAsync();
            var state = await context.IndexState.SingleAsync();
            operations = roots.Select(_ => Guid.NewGuid()).ToArray();
            for (var index = 0; index < roots.Length; index++)
            {
                (await context.SourceRootConfigurations.SingleAsync(value => value.Id == roots[index])).State = (int)SourceRootState.Deleting;
                state.CorpusVersion++;
                context.IndexProjectionChanges.Add(new() { CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion,
                    ScopeKind = 3, ScopeId = roots[index], CreatedAtUtc = DateTimeOffset.UtcNow });
                context.SourceDeletionOperations.Add(new() { Id = operations[index], SourceRootId = roots[index], Phase = "accepted",
                    CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(index), UpdatedAtUtc = DateTimeOffset.UtcNow });
            }
            await context.SaveChangesAsync();
        }
        var store = new SqlSourceDeletionStore(environment.Factory, TimeProvider.System, queryOwnerProbe: new WindowsInteractiveGpuOwnerProbe());
        using var scope = environment.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>();
        // Interleave the two durable operations so both capture their overlapping targets before either reclaims files.
        foreach (var operation in operations)
        {
            Assert.Equal("rebuild-index", (await store.PurgeAsync(await LeaseDeletionFixtureAsync(environment, operation), null, CancellationToken.None)).Phase);
            var candidate = await publisher.BuildAndPlaceAsync(Guid.Empty, CancellationToken.None);
            Assert.Equal("cleanup-memberships", (await store.PurgeAsync(await LeaseDeletionFixtureAsync(environment, operation), candidate, CancellationToken.None)).Phase);
        }
        await using (var context = await environment.Factory.CreateDbContextAsync())
            foreach (var kind in new[] { 3, 4 })
                Assert.True(await context.SourceDeletionCleanupItems.Where(value => value.StorageKind == kind)
                    .GroupBy(value => value.RelativePath).AnyAsync(group => group.Count() == 2));
        var recovery = Path.Combine(environment.IndexRoot, "test-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var maintenance = new SqlSegmentRetirementOperations(environment.Factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recovery, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        var coordinator = new SourceDeletionCoordinator(store, publisher,
            new WindowsSourceDeletionFileStore(environment.ArtifactRoot, environment.IndexRoot),
            new SqlSourceArtifactPublicationGate(environment.Factory), new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System),
            indexMaintenance: maintenance);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        for (var attempt = 0; attempt < 60 && await verification.SourceDeletionOperations.AnyAsync(value => value.State != 3); attempt++)
            await coordinator.RunOneAsync(CancellationToken.None);
        Assert.All(await verification.SourceDeletionOperations.AsNoTracking().ToArrayAsync(), value =>
        { Assert.Equal("completed", value.Phase); Assert.Null(value.ReasonCode); });
        Assert.False(await verification.SourceRootConfigurations.AnyAsync());
        await AssertSegmentedOracleAsync(environment);
    }

    private static async Task<SourceDeletionWorkItem> LeaseDeletionFixtureAsync(PipelineEnvironment environment, Guid id)
    {
        await using var context = await environment.Factory.CreateDbContextAsync();
        var operation = await context.SourceDeletionOperations.SingleAsync(value => value.Id == id);
        Assert.Equal(0, operation.State);
        operation.State = 1; operation.LeaseId = Guid.NewGuid(); operation.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        await context.SaveChangesAsync();
        return new(operation.Id, operation.SourceRootId, operation.LeaseId.Value, operation.Phase);
    }

    private static async Task PackSegmentedFixtureAsync(PipelineEnvironment environment)
    {
        // Independent full-catalogue fixture simulates a valid bounded merge. Ordinary publication never uses this path.
        var vectors = await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None);
        var paths = new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot);
        var segments = await new UsearchSegmentBuilder(new(environment.IndexRoot), paths, new(8192))
            .BuildAsync(vectors[0].ModelFingerprint, vectors[0].Dimensions, Stream(), CancellationToken.None);
        var segment = Assert.Single(segments);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var state = await context.IndexState.SingleAsync();
        var manifest = new UsearchManifestValidator(new(environment.IndexRoot), paths).Place(new(state.CorpusEpoch, state.CorpusVersion),
            segment.ModelFingerprint, segment.Dimensions, segments);
        context.IndexSegments.Add(new() { Id = segment.Id, ModelFingerprint = segment.ModelFingerprint, Dimensions = segment.Dimensions,
            DirectoryPath = segment.DirectoryPath, MembershipChecksum = segment.MembershipChecksum, FileChecksum = segment.FileChecksum,
            FileLength = segment.FileLength, VectorCount = segment.VectorCount, NativeFormat = segment.NativeFormat, State = 1,
            ProducingCandidateId = await context.IndexPublicationCandidates.Where(value => value.State == 2).Select(value => value.Id).FirstAsync(),
            CreatedAtUtc = DateTimeOffset.UtcNow, SealedAtUtc = DateTimeOffset.UtcNow });
        context.IndexSegmentVectors.AddRange(vectors.Select(value => new IndexSegmentVectorEntity { SegmentId = segment.Id, VectorId = value.VectorId }));
        var generation = manifest.Generation;
        context.IndexGenerations.Add(new() { Id = generation.Id, ModelFingerprint = generation.ModelFingerprint, Dimensions = generation.Dimensions,
            IndexPath = generation.IndexPath, MetadataChecksum = generation.MetadataChecksum, VectorCount = generation.VectorCount,
            FormatVersion = 2, SegmentCount = 1, CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion,
            CreatedAtUtc = DateTimeOffset.UtcNow, ValidatedAtUtc = DateTimeOffset.UtcNow });
        context.IndexGenerationSegments.Add(new() { GenerationId = generation.Id, SegmentId = segment.Id });
        state.ActiveIndexGenerationId = generation.Id;
        await context.SaveChangesAsync();
        await context.CurrentIndexMembers.ExecuteUpdateAsync(update => update.SetProperty(value => value.SegmentId, segment.Id));
        async IAsyncEnumerable<CanonicalVector> Stream()
        { foreach (var vector in vectors.OrderBy(value => value.VectorId)) { yield return vector; await Task.Yield(); } }
    }
}
