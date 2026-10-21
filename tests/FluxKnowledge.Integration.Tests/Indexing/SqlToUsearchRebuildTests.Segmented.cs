using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerFact]
    public async Task Segmented_source_deletion_activates_a_survivor_manifest_before_draining_owned_membership()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The independent source must remain searchable after root deletion.", segmented: true);
        var originalControl = await environment.ActiveGenerationAsync();
        var deleting = await environment.AddRetainedAndPumpAsync("This retained source is removed through the durable source deletion pipeline.");
        var old = await environment.ActiveGenerationAsync();
        await environment.AddAndPumpAtPathAsync("The independent source has a new current revision.", "initial.txt");
        await using var context = await environment.Factory.CreateDbContextAsync();
        var unrelatedSegment = await context.IndexSegments.AsNoTracking().SingleAsync(value => context.IndexGenerationSegments
            .Any(member => member.GenerationId == originalControl.Id && member.SegmentId == value.Id));
        var recovery = Path.Combine(environment.IndexRoot, "test-recovery");
        Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var retirement = new SqlSegmentRetirementOperations(environment.Factory,
            new FluxKnowledge.Integrations.Files.WindowsIndexProjectionFileStore(environment.IndexRoot), environment.IndexRoot, recovery,
            TimeProvider.System, new FluxKnowledge.Integrations.Windows.WindowsInteractiveGpuOwnerProbe());
        var controlRetirement = await retirement.MarkManifestAsync(originalControl.Id, Guid.NewGuid(), CancellationToken.None);
        Assert.Empty(controlRetirement.Segments); // The older mixed manifest still references this segment.
        Assert.True(await retirement.TryReclaimManifestAsync(originalControl.Id, controlRetirement.OperationId, CancellationToken.None));
        var rootId = await (from record in context.PipelineRecords join revision in context.SourceRevisions on record.SourceRevisionId equals revision.Id
            where record.Id == deleting select revision.SourceRootId).SingleAsync();
        var root = await context.SourceRootConfigurations.SingleAsync(value => value.Id == rootId);
        var ownedJob = await context.Jobs.Where(value => value.PipelineRecordId == deleting).Select(value => value.Id).FirstAsync();
        var terminalCandidate = await context.IndexPublicationCandidates.SingleAsync(value => value.ResultGenerationId == old.Id && value.State == 2);
        terminalCandidate.OwnerJobId = ownedJob;
        root.State = (int)SourceRootState.Deleting;
        var state = await context.IndexState.SingleAsync();
        state.CorpusVersion++;
        context.IndexProjectionChanges.Add(new() { CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion, ScopeKind = 3,
            ScopeId = rootId, CreatedAtUtc = DateTimeOffset.UtcNow });
        context.SourceDeletionOperations.Add(new() { Id = Guid.NewGuid(), SourceRootId = rootId, Phase = "accepted",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        var store = new SqlSourceDeletionStore(environment.Factory, TimeProvider.System,
            queryOwnerProbe: new FluxKnowledge.Integrations.Windows.WindowsInteractiveGpuOwnerProbe());
        var first = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(CancellationToken.None));
        Assert.Equal("rebuild-index", (await store.PurgeAsync(first, null, CancellationToken.None)).Phase);
        using var scope = environment.Services.CreateScope();
        var candidate = await scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>().BuildAndPlaceAsync(Guid.Empty, CancellationToken.None);
        var rebuild = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(CancellationToken.None));
        var outcome = await store.PurgeAsync(rebuild, candidate, CancellationToken.None);
        Assert.Null(outcome.ReasonCode);
        Assert.Equal("cleanup-memberships", outcome.Phase);
        await AssertSegmentedOracleAsync(environment);
        Assert.True(await context.Vectors.AnyAsync(value => value.IsDeleted));
        Assert.False(await context.CurrentIndexMembers.AnyAsync(value => value.SourceRootId == rootId));
        var membership = Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(CancellationToken.None));
        Assert.Equal("source-delete-segment-retirement-pending", (await store.PurgeAsync(membership, null, CancellationToken.None)).ReasonCode);
        Assert.True(await context.PipelineRecords.AnyAsync(value => value.Id == deleting));
        var coordinator = new SourceDeletionCoordinator(store,
            scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>(),
            new FluxKnowledge.Integrations.Files.WindowsSourceDeletionFileStore(environment.ArtifactRoot, environment.IndexRoot),
            new SqlSourceArtifactPublicationGate(environment.Factory), new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System),
            indexMaintenance: retirement);
        for (var attempt = 0; attempt < 20 && await context.SourceDeletionOperations.AnyAsync(value => value.SourceRootId == rootId && value.State != 3); attempt++)
            await coordinator.RunOneAsync(CancellationToken.None);
        var completed = await context.SourceDeletionOperations.AsNoTracking().SingleAsync(value => value.SourceRootId == rootId);
        Assert.Equal("completed", completed.Phase);
        Assert.Null(completed.ReasonCode);
        Assert.False(await context.SourceRootConfigurations.AnyAsync(value => value.Id == rootId));
        Assert.False(await context.PipelineRecords.AnyAsync(value => value.Id == deleting));
        Assert.False(Directory.Exists(old.IndexPath));
        var provenance = await context.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == terminalCandidate.Id);
        Assert.Null(provenance.OwnerJobId);
        Assert.Equal(ownedJob, provenance.RetiredOwnerJobId);
        Assert.Equal(1, (await context.IndexSegments.AsNoTracking().SingleAsync(value => value.Id == unrelatedSegment.Id)).State);
        Assert.True(File.Exists(Path.Combine(unrelatedSegment.DirectoryPath, "index.usearch")));
        await AssertSegmentedOracleAsync(environment);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Segmented_health_probe_does_not_clear_an_unavailable_or_corrupt_native_segment(bool corrupt)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The durable source remains valid while its derived native segment is unavailable.", segmented: true);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var segment = await context.IndexSegments.AsNoTracking().FirstAsync();
        var path = Path.Combine(segment.DirectoryPath, "index.usearch");
        if (corrupt) await File.WriteAllBytesAsync(path, [1, 2, 3]);
        else File.Delete(path);
        var coordinator = environment.Services.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        await coordinator.RunOnceAsync(CancellationToken.None);
        Assert.NotEqual(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.NotNull(coordinator.Snapshot.FailureCategory);
        Assert.True(await context.Vectors.AnyAsync());
    }

    [NativeSqlServerFact]
    public async Task Segmented_publication_reuses_unchanged_native_files_and_membership_through_the_real_pipeline()
    {
        var text = string.Join('\n', Enumerable.Range(1, 40).Select(id =>
            $"Synthetic architecture passage {id}. Source registration, indexing and retrieval preserve current publication identity."));
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, text, segmented: true);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var state = await context.IndexState.AsNoTracking().SingleAsync();
        Assert.True(state.ActiveIndexGenerationId is not null, string.Join("\n", await context.Jobs.AsNoTracking()
            .Where(value => value.Reason != null || value.ErrorDetails != null)
            .Select(value => value.Reason + ": " + value.ErrorDetails).ToArrayAsync()));
        var first = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == state.ActiveIndexGenerationId);
        Assert.Equal(2, first.FormatVersion);
        Assert.True(first.SegmentCount >= 2);
        Assert.True(File.Exists(Path.Combine(first.IndexPath, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(first.IndexPath, "index.usearch")));
        var before = await context.IndexSegments.AsNoTracking().ToArrayAsync();
        var bytes = before.ToDictionary(value => value.Id, value =>
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(value.DirectoryPath, "index.usearch")))));
        var memberships = await context.IndexSegmentVectors.AsNoTracking().OrderBy(value => value.SegmentId)
            .ThenBy(value => value.VectorId).Select(value => new { value.SegmentId, value.VectorId }).ToArrayAsync();
        Assert.Equal(first.VectorCount, await context.CurrentIndexMembers.LongCountAsync());
        Assert.Equal(0, await context.IndexGenerationVectors.LongCountAsync(value => value.GenerationId == first.Id));

        await environment.AddAndPumpAsync("A second source discusses bounded search segments and durable SQL publication.");
        var current = await context.IndexState.AsNoTracking().SingleAsync();
        Assert.NotEqual(first.Id, current.ActiveIndexGenerationId);
        var reused = await context.IndexSegments.AsNoTracking().Where(value => bytes.Keys.Contains(value.Id)).ToArrayAsync();
        Assert.Equal(before.Length, reused.Length);
        foreach (var segment in reused)
            Assert.Equal(bytes[segment.Id], Convert.ToHexStringLower(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(segment.DirectoryPath, "index.usearch")))));
        Assert.Equal(memberships, await context.IndexSegmentVectors.AsNoTracking().Where(value => bytes.Keys.Contains(value.SegmentId))
            .OrderBy(value => value.SegmentId).ThenBy(value => value.VectorId)
            .Select(value => new { value.SegmentId, value.VectorId }).ToArrayAsync());

        var generation = await environment.Store.GetGenerationAsync(current.ActiveIndexGenerationId!.Value, CancellationToken.None);
        Assert.NotNull(generation);
        var owner = new GpuInteractiveOwnerIdentity(Environment.ProcessId,
            new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()), new string('a', 64));
        var lease = await new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)
            .TryAcquireAsync(Guid.NewGuid(), owner, generation.ModelFingerprint, generation.Dimensions, CancellationToken.None);
        Assert.NotNull(lease);
        using var scope = environment.Services.CreateScope();
        await using var ann = await scope.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>()
            .OpenAsync(lease, CancellationToken.None);
        var vector = await context.Vectors.AsNoTracking().OrderBy(value => value.VectorId).FirstAsync();
        var query = new float[vector.Dimensions];
        Buffer.BlockCopy(vector.Values, 0, query, 0, vector.Values.Length);
        var hits = await ann.SearchAsync(query, 100, CancellationToken.None);
        Assert.NotEmpty(hits);
        Assert.True(await ann.IsCurrentAsync(CancellationToken.None));
        Assert.Contains(hits, hit => hit.VectorId == vector.VectorId);
        Assert.Contains(await environment.Reader.SearchAsync(query, 10, CancellationToken.None), hit => hit.VectorId == vector.VectorId);
        var eligible = await context.CurrentIndexMembers.Select(value => value.VectorId).ToArrayAsync();
        Assert.All(hits, hit => Assert.Contains(hit.VectorId, eligible));
        await AssertSegmentedOracleAsync(environment);
        await BindSegmentedFixtureInputsAsync(environment);
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        var dense = await reader.ReadDenseCandidatesAsync(ann, new("all", [], null), query, CancellationToken.None);
        Assert.Equal("ready", dense.Status);
        var passage = Assert.Single(dense.Candidates, value => value.ChunkId == vector.TextChunkId);
        var binding = new CorpusEvidenceBinding(2, passage.RootId, passage.OwnerSourceRevisionId,
            Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(passage.SourceIdentity))),
            passage.PipelineRecordId, passage.PipelineRecordRevision, passage.ArtifactId, passage.ArtifactHash,
            passage.ChunkId, passage.ChunkHash, passage.StartOffset, passage.Length, passage.CorpusEpoch);
        var read = await reader.ReadAsync(binding, 4096, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Contains(passage.Content, read.Text, StringComparison.Ordinal);

        var sourceIdentity = await context.PipelineRecords.Where(record => record.Id == passage.PipelineRecordId).Select(record => record.SourceIdentityId).SingleAsync();
        var originalIds = await context.CurrentIndexMembers.Where(value => value.SourceIdentityId == sourceIdentity)
            .Select(value => value.VectorId).ToArrayAsync();
        await environment.AddAndPumpAtPathAsync("The architecture has an updated current publication and must retire the old passages.", "initial.txt");
        await AssertSegmentedOracleAsync(environment);
        Assert.False(await ann.IsCurrentAsync(CancellationToken.None));
        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(async () => await ann.SearchAsync(query, 100, CancellationToken.None));
        Assert.False(await context.CurrentIndexMembers.AnyAsync(value => originalIds.Contains(value.VectorId)));
        Assert.Null(await reader.ReadAsync(binding, 4096, CancellationToken.None));
        Assert.True(await context.Vectors.AnyAsync(value => originalIds.Contains(value.VectorId)));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Segmented_scan_suppression_is_logged_and_caught_up_without_reembedding_the_survivors(bool backgroundRefresh)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The initial source survives.", segmented: true);
        var suppressed = await environment.AddRetainedAndPumpAsync("The temporary retained passage must disappear after authoritative suppression.");
        Guid rootId;
        long[] removed;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var record = await context.PipelineRecords.SingleAsync(value => value.Id == suppressed);
            rootId = (await context.SourceRevisions.SingleAsync(value => value.Id == record.SourceRevisionId)).SourceRootId;
            removed = await context.CurrentIndexMembers.Where(value => value.SourceRootId == rootId).Select(value => value.VectorId).ToArrayAsync();
        }
        Assert.NotEmpty(removed);
        await new SqlSourceScanStore(environment.Factory, TimeProvider.System).SuppressUnseenAsync(new SourceRootId(rootId),
            new HashSet<SourceRevisionId>(), CancellationToken.None);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var state = await context.IndexState.SingleAsync();
            Assert.True(await context.IndexProjectionChanges.AnyAsync(value => value.CorpusVersion == state.CorpusVersion));
        }
        if (backgroundRefresh)
        {
            await environment.Services.GetRequiredService<DerivedIndexRecoveryCoordinator>().RunOnceAsync(CancellationToken.None);
            Assert.Equal(DerivedIndexRecoveryState.Healthy, environment.Services.GetRequiredService<DerivedIndexRecoveryCoordinator>().Snapshot.State);
        }
        else await environment.AddAndPumpAsync("A new source triggers bounded projection catch-up.");
        await AssertSegmentedOracleAsync(environment);
        await using var after = await environment.Factory.CreateDbContextAsync();
        Assert.False(await after.CurrentIndexMembers.AnyAsync(value => removed.Contains(value.VectorId)));
        Assert.Equal(removed.Length, await after.Vectors.CountAsync(value => removed.Contains(value.VectorId)));
    }

    [NativeSqlServerFact]
    public async Task Segmented_empty_replacement_publishes_an_empty_projection_without_claiming_an_empty_canonical_catalogue()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The last published passage will be replaced by empty content.", segmented: true);
        await environment.AddAndPumpAtPathAsync(string.Empty, "initial.txt");
        await AssertSegmentedOracleAsync(environment);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var state = await context.IndexState.AsNoTracking().SingleAsync();
        var generation = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == state.ActiveIndexGenerationId);
        Assert.Equal(0, generation.VectorCount);
        Assert.Equal(0, generation.SegmentCount);
        Assert.Null(state.EmptyCatalogueValidatedAtUtc);
        Assert.True(await context.Vectors.AnyAsync());
        var owner = new FluxKnowledge.Integrations.Windows.WindowsInteractiveGpuOwnerProbe().Current;
        var lease = await new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System).TryAcquireAsync(Guid.NewGuid(), owner,
            generation.ModelFingerprint, generation.Dimensions, CancellationToken.None);
        Assert.NotNull(lease);
        using var scope = environment.Services.CreateScope();
        await using var ann = await scope.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(lease, CancellationToken.None);
        var query = new float[generation.Dimensions]; query[0] = 1;
        Assert.Empty(await ann.SearchAsync(query, 100, CancellationToken.None));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Segmented_sealed_candidate_replay_is_atomic_and_stale_candidates_cannot_activate(bool sqlRetries)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The first pending source.", publish: false, segmented: true);
        var request = await ClaimSegmentedPublishAsync(environment, sqlRetries);
        var transition = new SqlStageTransitionStore(environment.Factory);
        var first = await transition.TransitionAsync(request, CancellationToken.None);
        var replay = await transition.TransitionAsync(request, CancellationToken.None);
        Assert.False(first.ExistingTransition);
        Assert.True(replay.ExistingTransition);
        Assert.Equal(first.ArtifactId, replay.ArtifactId);
        await AssertSegmentedOracleAsync(environment);
        await using (var worklists = await environment.Factory.CreateDbContextAsync())
        {
            Assert.False(await worklists.IndexCandidateMembers.AnyAsync(value => value.CandidateId == request.IndexingOutput!.SegmentedCandidateId));
            var committed = await worklists.IndexPublicationCandidates.SingleAsync(value => value.Id == request.IndexingOutput!.SegmentedCandidateId);
            Assert.Equal(2, committed.State);
            Assert.True(committed.AfterCount > 0);
            Assert.True(await worklists.CurrentIndexMembers.AnyAsync());
        }

        await environment.AddAndPumpAsync("An older pending second source.");
        var stale = await ClaimSegmentedPublishAsync(environment, sqlRetries);
        await environment.AddAndPumpAsync("The third pending source publishes before the second.");
        var newer = await ClaimSegmentedPublishAsync(environment, sqlRetries);
        await transition.TransitionAsync(newer, CancellationToken.None);
        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(async () => await transition.TransitionAsync(stale, CancellationToken.None));
        await AssertSegmentedOracleAsync(environment);
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.False(await context.Artifacts.AnyAsync(value => value.Id == stale.Artifact.Id));
        Assert.False((await context.PipelineRecords.SingleAsync(value => value.Id == stale.CurrentJob.PipelineRecordId.Value)).CompletionCriteriaMet);
        Assert.Equal(newer.IndexingOutput!.ActivateGeneration!.Id, (await context.IndexState.SingleAsync()).ActiveIndexGenerationId);
        Assert.True(await context.IndexCandidateMembers.AnyAsync(value => value.CandidateId == stale.IndexingOutput!.SegmentedCandidateId));
        // A late acknowledgement remains idempotent after a later manifest has become current.
        Assert.True((await transition.TransitionAsync(request, CancellationToken.None)).ExistingTransition);
        using var scope = environment.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>().AbandonAsync(
            request.IndexingOutput!.SegmentedCandidateId!.Value, CancellationToken.None);
        Assert.Equal(2, (await context.IndexPublicationCandidates.AsNoTracking().SingleAsync(
            value => value.Id == request.IndexingOutput.SegmentedCandidateId)).State);
    }

    [NativeSqlServerFact]
    public async Task Segmented_abandonment_requires_exact_process_ownership_and_releases_only_its_uncommitted_worklist()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "A pending source has a sealed but uncommitted candidate.", publish: false, segmented: true);
        var request = await ClaimSegmentedPublishAsync(environment, false);
        var id = request.IndexingOutput!.SegmentedCandidateId!.Value;
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        await using var context = await environment.Factory.CreateDbContextAsync();
        var candidate = await context.IndexPublicationCandidates.SingleAsync(value => value.Id == id);
        var machine = candidate.OwnerMachineFingerprint;
        candidate.OwnerMachineFingerprint = machine[0] == 'f' ? new string('e', 64) : new string('f', 64);
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AbandonAsync(id, CancellationToken.None));
        Assert.Equal(1, (await context.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == id)).State);
        Assert.True(await context.IndexCandidateMembers.AnyAsync(value => value.CandidateId == id));
        candidate.OwnerMachineFingerprint = machine;
        await context.SaveChangesAsync();
        await store.AbandonAsync(id, CancellationToken.None);
        await store.AbandonAsync(id, CancellationToken.None);
        Assert.Equal(3, (await context.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == id)).State);
        Assert.False(await context.IndexCandidateMembers.AnyAsync(value => value.CandidateId == id));
        Assert.True(await context.Vectors.AnyAsync());
        Assert.True(File.Exists(Path.Combine(request.IndexingOutput.ActivateGeneration!.IndexPath, "manifest.json")));
        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(async () =>
            await new SqlStageTransitionStore(environment.Factory).TransitionAsync(request, CancellationToken.None));
        Assert.Null((await context.IndexState.AsNoTracking().SingleAsync()).ActiveIndexGenerationId);
    }

    private static async Task<StageTransitionRequest> ClaimSegmentedPublishAsync(PipelineEnvironment environment, bool sqlRetries)
    {
        var now = DateTimeOffset.UtcNow;
        var dispatch = await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("segment-test-dispatcher", now.AddMinutes(1),
            TimeSpan.FromMinutes(5), [PipelineOperations.Publish], CancellationToken.None);
        Assert.NotNull(dispatch);
        var job = await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(dispatch, "segment-test-worker", now.AddMinutes(1),
            TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(job);
        var source = await environment.Store.ReadStageSourceAsync(job.PipelineRecordId, job.SourceRevision, job.Stage, CancellationToken.None);
        Assert.True(Guid.TryParse(source.InputText, out var draft));
        using var scope = environment.Services.CreateScope();
        IIndexGenerationPublisher publisher = scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>();
        if (sqlRetries)
        {
            await using var bindingContext = await environment.Factory.CreateDbContextAsync();
            var factory = new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<FluxKnowledgeDbContext>(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(bindingContext.Database.GetConnectionString(), options => options.EnableRetryOnFailure()).Options);
            publisher = new SegmentedIndexPublisher(environment.Builder, new SqlSegmentedIndexStore(factory, TimeProvider.System,
                new FluxKnowledge.Integrations.Windows.WindowsInteractiveGpuOwnerProbe()), new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System),
                new UsearchSegmentBuilder(new(environment.IndexRoot), new FluxKnowledge.Integrations.Files.WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot), new(2)),
                new UsearchManifestValidator(new(environment.IndexRoot), new FluxKnowledge.Integrations.Files.WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot)), true);
        }
        var candidate = await publisher.BuildAndPlaceAsync(draft, CancellationToken.None);
        return new(dispatch, job, new(Guid.NewGuid(), PipelineStage.Publish, candidate.Generation.MetadataChecksum,
            "application/vnd.fluxknowledge.usearch-generation", candidate.Generation.Id.ToString("N"), now), null, null, nameof(SqlToUsearchRebuildTests),
            new(ActivateGeneration: candidate.Generation, ExpectedCorpusStamp: candidate.ExpectedCorpusStamp, SegmentedCandidateId: candidate.SegmentedCandidateId));
    }

    internal static async Task AssertSegmentedOracleAsync(PipelineEnvironment environment)
    {
        var expected = (await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None)).Select(value => value.VectorId).Order().ToArray();
        await using var context = await environment.Factory.CreateDbContextAsync();
        var state = await context.IndexState.AsNoTracking().SingleAsync();
        var generation = await context.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == state.ActiveIndexGenerationId);
        var actual = await (from manifest in context.IndexGenerationSegments join member in context.IndexSegmentVectors on manifest.SegmentId equals member.SegmentId
            where manifest.GenerationId == generation.Id orderby member.VectorId select member.VectorId).ToArrayAsync();
        Assert.Equal(expected, actual);
        Assert.Equal(actual, await context.CurrentIndexMembers.Where(value => value.CorpusEpoch == state.CorpusEpoch).OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync());
        Assert.Equal(state.CorpusEpoch, generation.CorpusEpoch);
        Assert.Equal(state.CorpusVersion, generation.CorpusVersion);
        Assert.Equal(actual.LongLength, generation.VectorCount);
        Assert.Equal(0, await context.IndexGenerationVectors.CountAsync(value => value.GenerationId == generation.Id));
    }

    private static async Task BindSegmentedFixtureInputsAsync(PipelineEnvironment environment)
    {
        // The established synthetic unbatched embedding fixture predates the checkpoint input hash.
        await using var context = await environment.Factory.CreateDbContextAsync();
        await context.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
    }

    private sealed class SegmentedFixtureWords : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-segment-words-v1";
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
