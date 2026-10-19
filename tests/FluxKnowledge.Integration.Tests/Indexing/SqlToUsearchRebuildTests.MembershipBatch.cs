using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerFact]
    public async Task Publication_inserts_large_membership_in_one_round_trip_and_repeated_activation_preserves_the_exact_set()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Membership baseline.");
        await AddMembershipTestVectorsAsync(environment, 1024);
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(candidate.Vectors.Count >= 1025);
        var observer = new MembershipWriteObserver();
        var store = new SqlStageTransitionStore(new MembershipObservedFactory(_fixture.ConnectionString, observer));
        var firstRequest = await ClaimPublishAsync(environment, candidate);
        var first = await store.TransitionAsync(firstRequest, CancellationToken.None);
        Assert.False(first.ExistingTransition);
        Assert.Equal(1, observer.InsertCommands);
        var replay = await store.TransitionAsync(firstRequest, CancellationToken.None);
        Assert.True(replay.ExistingTransition);
        Assert.Equal(first.ArtifactId, replay.ArtifactId);
        Assert.Equal(1, observer.InsertCommands);

        var nextRequest = await ClaimPublishAsync(environment, candidate);
        Assert.False((await store.TransitionAsync(nextRequest, CancellationToken.None)).ExistingTransition);
        Assert.Equal(2, observer.InsertCommands);
        await using var verify = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(candidate.Vectors.Select(value => value.VectorId), await verify.IndexGenerationVectors
            .Where(value => value.GenerationId == candidate.Generation.Id).OrderBy(value => value.VectorId)
            .Select(value => value.VectorId).ToArrayAsync());
        Assert.Equal(candidate.Generation.Id, (await verify.IndexState.SingleAsync()).ActiveIndexGenerationId);
    }

    [NativeSqlServerFact]
    public async Task Publication_refuses_incompatible_existing_membership_without_partial_activation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Incompatible membership baseline.");
        await AddMembershipTestVectorsAsync(environment, 3);
        var previous = await environment.ActiveGenerationAsync();
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        await using (var seed = await environment.Factory.CreateDbContextAsync())
        {
            seed.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = candidate.Generation.Id, ModelFingerprint = candidate.Generation.ModelFingerprint,
                Dimensions = candidate.Generation.Dimensions, IndexPath = candidate.Generation.IndexPath,
                MetadataChecksum = candidate.Generation.MetadataChecksum, VectorCount = candidate.Generation.VectorCount,
                CorpusEpoch = candidate.Generation.CorpusStamp!.CorpusEpoch, CorpusVersion = candidate.Generation.CorpusStamp.CorpusVersion,
                CreatedAtUtc = DateTimeOffset.UtcNow, ValidatedAtUtc = DateTimeOffset.UtcNow
            });
            seed.IndexGenerationVectors.Add(new() { GenerationId = candidate.Generation.Id, VectorId = candidate.Vectors[0].VectorId });
            await seed.SaveChangesAsync();
        }
        var request = await ClaimPublishAsync(environment, candidate);
        var observer = new MembershipWriteObserver();
        await Assert.ThrowsAsync<IndexGenerationStaleException>(async () => await new SqlStageTransitionStore(
            new MembershipObservedFactory(_fixture.ConnectionString, observer)).TransitionAsync(request, CancellationToken.None));
        Assert.Equal(0, observer.InsertCommands);
        await AssertMembershipActivationRolledBackAsync(environment, request, previous.Id);
        await using var verify = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(candidate.Vectors[0].VectorId, (await verify.IndexGenerationVectors.SingleAsync(value => value.GenerationId == candidate.Generation.Id)).VectorId);
    }

    [NativeSqlServerFact]
    public async Task Cancellation_after_membership_insert_rolls_back_generation_membership_and_publication()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Cancelled membership baseline.");
        await AddMembershipTestVectorsAsync(environment, 3);
        var previous = await environment.ActiveGenerationAsync();
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var request = await ClaimPublishAsync(environment, candidate);
        using var cancellation = new CancellationTokenSource();
        var observer = new MembershipWriteObserver(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new SqlStageTransitionStore(
            new MembershipObservedFactory(_fixture.ConnectionString, observer)).TransitionAsync(request, cancellation.Token));
        Assert.True(observer.InsertCommands > 0);
        await AssertMembershipActivationRolledBackAsync(environment, request, previous.Id);
        await using var verify = await environment.Factory.CreateDbContextAsync();
        Assert.False(await verify.IndexGenerations.AnyAsync(value => value.Id == candidate.Generation.Id));
        Assert.False(await verify.IndexGenerationVectors.AnyAsync(value => value.GenerationId == candidate.Generation.Id));
    }

    [NativeSqlServerFact]
    public async Task Publication_refuses_duplicate_snapshot_members_without_silently_discarding_them()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Duplicate membership baseline.");
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var request = await ClaimPublishAsync(environment, candidate with { Vectors = [.. candidate.Vectors, candidate.Vectors[0]] });
        var previous = await environment.ActiveGenerationAsync();
        var observer = new MembershipWriteObserver();
        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(async () => await new SqlStageTransitionStore(
            new MembershipObservedFactory(_fixture.ConnectionString, observer)).TransitionAsync(request, CancellationToken.None));
        Assert.Equal(0, observer.InsertCommands);
        await AssertMembershipActivationRolledBackAsync(environment, request, previous.Id);
    }

    private static async Task AddMembershipTestVectorsAsync(PipelineEnvironment environment, int count)
    {
        await using var seed = await environment.Factory.CreateDbContextAsync();
        var template = await seed.Vectors.AsNoTracking().FirstAsync();
        var original = await seed.TextChunks.AsNoTracking().SingleAsync(value => value.Id == template.TextChunkId);
        var lastOrdinal = await seed.TextChunks.Where(value => value.ArtifactId == original.ArtifactId).MaxAsync(value => value.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var text = "Synthetic membership passage " + index;
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var chunk = new TextChunkEntity { ArtifactId = original.ArtifactId, SourceRevision = original.SourceRevision,
                Ordinal = lastOrdinal + index + 1, Content = text, ContentHash = hash, Length = text.Length,
                PassagePolicyFingerprint = original.PassagePolicyFingerprint, SearchInputHash = hash };
            seed.TextChunks.Add(chunk);
            seed.Vectors.Add(new VectorEntity { TextChunk = chunk, ModelFingerprint = template.ModelFingerprint,
                Dimensions = template.Dimensions, Values = template.Values, TextChunkContentHash = hash,
                PayloadChecksum = template.PayloadChecksum, SearchInputHash = hash, SourceRevision = template.SourceRevision,
                IndexGenerationId = template.IndexGenerationId, CreatedAtUtc = DateTimeOffset.UtcNow });
        }
        await seed.SaveChangesAsync();
    }

    private static async Task AssertMembershipActivationRolledBackAsync(PipelineEnvironment environment, StageTransitionRequest request, Guid activeId)
    {
        await using var verify = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(activeId, (await verify.IndexState.SingleAsync()).ActiveIndexGenerationId);
        Assert.False((await verify.PipelineRecords.SingleAsync(value => value.Id == request.CurrentJob.PipelineRecordId.Value)).CompletionCriteriaMet);
        Assert.False(await verify.Artifacts.AnyAsync(value => value.Id == request.Artifact.Id));
        Assert.Equal((int)PublicJobState.WorkerProcessing, (await verify.Jobs.SingleAsync(value => value.Id == request.CurrentJob.JobId.Value)).PublicState);
    }

    private sealed class MembershipObservedFactory(string connectionString, MembershipWriteObserver observer) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString).AddInterceptors(observer).Options);
    }

    private sealed class MembershipWriteObserver(CancellationTokenSource? cancelAfterInsert = null) : DbCommandInterceptor
    {
        public int InsertCommands { get; private set; }
        private static bool IsMembershipInsert(DbCommand command) => command.CommandText.Contains("INSERT INTO [IndexGenerationVectors]", StringComparison.Ordinal);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsMembershipInsert(command)) InsertCommands++;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (IsMembershipInsert(command)) cancelAfterInsert?.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
