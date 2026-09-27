using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Domain.Sources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class EmbeddingCheckpointTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private static readonly EmbeddingProfile Profile = new(DeterministicTokenHashEmbeddingProvider.Fingerprint, 256);
    private static string LongText => string.Join("\n\n", Enumerable.Range(0, 24).Select(i => $"Section {i}. " + new string((char)('a' + i), 900) + "."));

    public async Task InitializeAsync()
    {
        await using var context = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        await context.SourceDeletionCleanupItems.ExecuteDeleteAsync();
        await context.SourceDeletionOperations.ExecuteDeleteAsync();
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Ordinary_embedding_continuation_preserves_source_activity_state_and_reason()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Unrooted control", embed: false);
        _ = await ClaimAsync(environment);
        var recordId = await environment.AddRetainedAndPumpAsync(LongText);
        var claim = await ClaimAsync(environment);
        Assert.Equal(recordId, claim.Job.PipelineRecordId.Value);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var before = await context.SourceActivities.AsNoTracking().SingleAsync(value => value.ResultingPipelineRecordId == recordId);
        var transitions = new StageTransitionService(new SqlStageTransitionStore(environment.Factory), new NullStatusEventPublisher(),
            new ChannelOutboxWakeSignal(), TimeProvider.System);
        var worker = new EmbedStageWorker(environment.Store, new RecordingProvider(), transitions, TimeProvider.System,
            new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System));
        await worker.ExecuteAsync(claim, CancellationToken.None);
        var after = await context.SourceActivities.AsNoTracking().SingleAsync(value => value.Id == before.Id);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Reason, after.Reason);
        var job = await context.Jobs.AsNoTracking().SingleAsync(value => value.Id == claim.Job.JobId.Value);
        Assert.Equal((int)FluxKnowledge.Domain.Jobs.PublicJobState.WorkerQueued, job.PublicState);
        Assert.Equal("embedding-batch-persisted", job.Reason);
        Assert.Null(job.ErrorDetails);
    }

    [NativeSqlServerFact]
    public async Task Large_draft_seal_hashes_all_keyset_pages_in_passage_order()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, string.Concat(Enumerable.Repeat(LongText, 14)), embed: false);
        var store = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var claim = await ClaimAsync(environment);
        var completed = await store.ReadNextAsync(claim, Profile, CancellationToken.None);
        while (completed.Chunks.Count > 0)
        {
            await store.CommitAsync(claim, completed, await EmbedAsync(completed), CancellationToken.None);
            completed = await store.ReadNextAsync(claim, Profile, CancellationToken.None);
        }
        await using var context = await environment.Factory.CreateDbContextAsync();
        var rows = await context.Vectors.OrderBy(value => value.TextChunk.Ordinal).ThenBy(value => value.TextChunkId).ToArrayAsync();
        Assert.True(rows.Length > 128);
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(rows.SelectMany(value => value.Values).ToArray()));
        Assert.Equal(expected, completed.CompletedChecksum);
        Assert.Equal(await context.TextChunks.CountAsync(), rows.Length);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_zero_or_partial_checkpoint_is_recognised_as_updating(bool persist)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, LongText, embed: false);
        var claim = await ClaimAsync(environment);
        var store = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var batch = await store.ReadNextAsync(claim, Profile, CancellationToken.None);
        if (persist) await store.CommitAsync(claim, batch, await EmbedAsync(batch), CancellationToken.None);
        var snapshot = await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None);
        Assert.True(snapshot.IsProjectionUnavailable);
        Assert.False(snapshot.IsValidatedEmptyCatalogue);
        Assert.Contains(batch.GenerationId, snapshot.ReferencedGenerationIds);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_deletion_captures_zero_or_partial_checkpoint_before_releasing_its_job(bool persist)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Other root control.", embed: false);
        var otherClaim = await ClaimAsync(environment);
        var checkpoints = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var other = await checkpoints.ReadNextAsync(otherClaim, Profile, CancellationToken.None);
        var recordId = await environment.AddRetainedAndPumpAsync(LongText);
        var deletingClaim = await ClaimAsync(environment);
        Assert.Equal(recordId, deletingClaim.Job.PipelineRecordId.Value);
        var deleting = await checkpoints.ReadNextAsync(deletingClaim, Profile, CancellationToken.None);
        if (persist) await checkpoints.CommitAsync(deletingClaim, deleting, await EmbedAsync(deleting), CancellationToken.None);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var rootId = await (from record in context.PipelineRecords join revision in context.SourceRevisions on record.SourceRevisionId equals revision.Id
                where record.Id == recordId select revision.SourceRootId).SingleAsync();
            (await context.SourceRootConfigurations.SingleAsync(value => value.Id == rootId)).State = (int)SourceRootState.Deleting;
            (await context.Jobs.SingleAsync(value => value.Id == deletingClaim.Job.JobId.Value)).LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            (await context.OutboxMessages.SingleAsync(value => value.Id == deletingClaim.DispatchMessage.DispatchMessageId.Value)).LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            context.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
            {
                Id = Guid.NewGuid(), SourceRootId = rootId, State = 0, Phase = "accepted", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }
        var deletion = new SqlSourceDeletionStore(environment.Factory, TimeProvider.System);
        var first = await deletion.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.True((await deletion.PurgeAsync(first, null, CancellationToken.None)).RequiresIndexBuild);
        var betweenPhases = await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None);
        Assert.True(betweenPhases.IsProjectionUnavailable);
        Assert.Contains(other.GenerationId, betweenPhases.ReferencedGenerationIds);
        var second = await deletion.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal("cleanup-files", (await deletion.PurgeAsync(second, null, CancellationToken.None)).Phase);
        await using var check = await environment.Factory.CreateDbContextAsync();
        Assert.False(await check.Jobs.AnyAsync(value => value.Id == deletingClaim.Job.JobId.Value));
        var retired = await check.IndexGenerations.SingleAsync(value => value.Id == deleting.GenerationId);
        Assert.NotNull(retired.RetiredAtUtc);
        Assert.Null(retired.EmbeddingJobId);
        Assert.True(await check.IndexGenerations.AnyAsync(value => value.Id == other.GenerationId && value.RetiredAtUtc == null));
        Assert.Single(await check.SourceDeletionCleanupItems.Where(value => value.StorageKind == 2 && value.RelativePath == deleting.GenerationId.ToString("N")).ToListAsync());
        Assert.Empty(await check.Vectors.Where(value => value.IndexGenerationId == deleting.GenerationId).ToListAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_migration_refuses_owner_or_input_hash_loss_and_empty_round_trip_preserves_epoch(bool hashOnly)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, LongText, embed: false);
        var claim = await ClaimAsync(environment);
        var checkpoints = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var batch = await checkpoints.ReadNextAsync(claim, Profile, CancellationToken.None);
        if (hashOnly) await checkpoints.CommitAsync(claim, batch, await EmbedAsync(batch), CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        if (hashOnly)
        {
            (await context.IndexGenerations.SingleAsync()).EmbeddingJobId = null;
            await context.SaveChangesAsync();
        }
        var epoch = (await context.IndexState.SingleAsync()).CorpusEpoch;
        var migrations = context.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, value => value.EndsWith("_AddEmbeddingCheckpoints", StringComparison.Ordinal));
        Assert.True(index > 0);
        var failure = await Assert.ThrowsAsync<SqlException>(() => context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]));
        Assert.Contains("embedding-checkpoint-downgrade-requires-empty-reset", failure.Message);
        Assert.Contains(migrations[index], await context.Database.GetAppliedMigrationsAsync());
        await context.Database.MigrateAsync();
        Assert.Equal(epoch, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusEpoch);
        await SqlTestData.ClearPipelineAsync(fixture);
        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        await context.Database.MigrateAsync();
        Assert.Equal(epoch, (await context.IndexState.AsNoTracking().SingleAsync()).CorpusEpoch);
    }

    [NativeSqlServerFact]
    public async Task Persisted_batch_survives_reclaim_and_is_reused_without_duplicate_vectors()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, LongText, embed: false);
        var claim = await ClaimAsync(environment);
        var store = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var batch = await store.ReadNextAsync(claim, Profile, CancellationToken.None);
        Assert.Equal(4, batch.Chunks.Count);
        var results = await EmbedAsync(batch);
        await store.CommitAsync(claim, batch, results, CancellationToken.None);
        await store.CommitAsync(claim, batch, results, CancellationToken.None);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            Assert.Equal(4, await context.Vectors.CountAsync());
            Assert.Empty(await context.Artifacts.Where(value => value.Stage == (int)PipelineStage.Embed).ToListAsync());
            await context.OutboxMessages.Where(value => value.Id == claim.DispatchMessage.DispatchMessageId.Value)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await context.Jobs.Where(value => value.Id == claim.Job.JobId.Value)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.LeaseExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        var reclaimed = await ClaimAsync(environment);
        Assert.True(reclaimed.Job.LeaseGeneration > claim.Job.LeaseGeneration);
        var next = await store.ReadNextAsync(reclaimed, Profile, CancellationToken.None);
        Assert.Equal(batch.GenerationId, next.GenerationId);
        Assert.Equal(4, next.Chunks.Count);
        Assert.Empty(next.Chunks.Select(value => value.Id).Intersect(batch.Chunks.Select(value => value.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(claim, next, results, CancellationToken.None).AsTask());
    }

    [NativeSqlServerTheory]
    [InlineData("job")]
    [InlineData("dispatch")]
    [InlineData("epoch")]
    [InlineData("input")]
    [InlineData("expired")]
    public async Task Changed_ownership_epoch_or_input_refuses_checkpoint_atomically(string change)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, LongText, embed: false);
        var claim = await ClaimAsync(environment);
        var store = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var batch = await store.ReadNextAsync(claim, Profile, CancellationToken.None);
        var results = await EmbedAsync(batch);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            if (change == "job") (await context.Jobs.SingleAsync(value => value.Id == claim.Job.JobId.Value)).LeaseGeneration++;
            if (change == "dispatch") (await context.OutboxMessages.SingleAsync(value => value.Id == claim.DispatchMessage.DispatchMessageId.Value)).LeaseGeneration++;
            if (change == "epoch") (await context.IndexState.SingleAsync()).CorpusEpoch = Guid.NewGuid();
            if (change == "input") (await context.TextChunks.SingleAsync(value => value.Id == batch.Chunks[0].Id)).ContextHeader = "changed heading";
            if (change == "expired") (await context.Jobs.SingleAsync(value => value.Id == claim.Job.JobId.Value)).LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(claim, batch, results, CancellationToken.None).AsTask());
        await using var check = await environment.Factory.CreateDbContextAsync();
        Assert.Empty(await check.Vectors.ToListAsync());
        Assert.Equal(0, (await check.IndexGenerations.SingleAsync()).VectorCount);
    }

    [NativeSqlServerFact]
    public async Task Invalid_later_vector_rolls_back_entire_batch_and_wrong_profile_cannot_resume()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, LongText, embed: false);
        var claim = await ClaimAsync(environment);
        var store = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var batch = await store.ReadNextAsync(claim, Profile, CancellationToken.None);
        var results = (await EmbedAsync(batch)).ToArray();
        results[^1] = new(new float[256], Profile.ModelFingerprint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(claim, batch, results, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadNextAsync(claim, new("another-profile", 1024), CancellationToken.None).AsTask());
        await using var check = await environment.Factory.CreateDbContextAsync();
        Assert.Empty(await check.Vectors.ToListAsync());
        Assert.Single(await check.IndexGenerations.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Worker_resumes_existing_vectors_embeds_search_text_and_seals_the_same_draft()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, LongText, embed: false);
        var claim = await ClaimAsync(environment);
        var store = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var first = await store.ReadNextAsync(claim, Profile, CancellationToken.None);
        await store.CommitAsync(claim, first, await EmbedAsync(first), CancellationToken.None);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var chunk = await context.TextChunks.Where(value => !first.Chunks.Select(c => c.Id).Contains(value.Id)).OrderBy(value => value.Ordinal).FirstAsync();
            chunk.ContextHeader = "Useful section title";
            chunk.SearchInputHash = new CanonicalTextChunk(chunk.Id, chunk.Ordinal, chunk.StartOffset, chunk.Length, chunk.Content, chunk.ContentHash, chunk.PassagePolicyFingerprint, chunk.ContextHeader).SearchInputHash;
            await context.SaveChangesAsync();
        }
        var provider = new RecordingProvider();
        var transitions = new StageTransitionService(new SqlStageTransitionStore(environment.Factory), new NullStatusEventPublisher(),
            new ChannelOutboxWakeSignal(), TimeProvider.System);
        var worker = new EmbedStageWorker(environment.Store, provider, transitions, TimeProvider.System, store);
        while (true)
        {
            await worker.ExecuteAsync(claim, CancellationToken.None);
            await using var status = await environment.Factory.CreateDbContextAsync();
            if (await status.Artifacts.AnyAsync(value => value.Stage == (int)PipelineStage.Embed)) break;
            claim = await ClaimAsync(environment);
        }
        Assert.Contains(provider.Inputs, value => value.StartsWith("Useful section title\n", StringComparison.Ordinal));
        Assert.All(provider.BatchSizes, size => Assert.InRange(size, 1, 4));
        Assert.DoesNotContain(first.Chunks[0].SearchText, provider.Inputs);
        await using var check = await environment.Factory.CreateDbContextAsync();
        var generation = Assert.Single(await check.IndexGenerations.ToListAsync());
        Assert.Equal(first.GenerationId, generation.Id);
        Assert.Equal(await check.TextChunks.CountAsync(), await check.Vectors.CountAsync());
        Assert.All(await check.Vectors.ToListAsync(), vector => Assert.NotNull(vector.SearchInputHash));
        var artifact = await check.Artifacts.SingleAsync(value => value.Stage == (int)PipelineStage.Embed);
        Assert.Equal(first.GenerationId.ToString("D"), artifact.SearchText);
        Assert.Single(await check.Jobs.Where(value => value.Operation == PipelineOperations.Publish).ToListAsync());
        var recovery = await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None);
        Assert.True(recovery.IsProjectionUnavailable);
        var publish = await ClaimAsync(environment, PipelineOperations.Publish);
        await new PublishStageWorker(environment.Store, environment.Store, environment.Builder, transitions, TimeProvider.System)
            .ExecuteAsync(publish, CancellationToken.None);
        var active = await environment.ActiveGenerationAsync();
        Assert.Equal(generation.VectorCount, active.VectorCount);
        Assert.NotEqual(first.GenerationId, active.Id);
        Assert.NotNull(active.CorpusStamp);
        var published = await environment.Store.ReadVectorsAsync(active.Id, CancellationToken.None);
        var query = new float[active.Dimensions];
        Buffer.BlockCopy(published[0].Values, 0, query, 0, published[0].Values.Length);
        var matches = await environment.Reader.SearchAsync(query, 1, CancellationToken.None);
        Assert.Equal(published[0].VectorId, Assert.Single(matches).VectorId);
        Assert.False((await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None)).IsProjectionUnavailable);
    }

    private static async Task<StageWorkItem> ClaimAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment, string operation = PipelineOperations.Embed)
    {
        var now = DateTimeOffset.UtcNow;
        var dispatch = await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("checkpoint-dispatch", now, TimeSpan.FromMinutes(2), [operation], CancellationToken.None);
        Assert.NotNull(dispatch);
        var job = await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(dispatch, "checkpoint-worker", now, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(job);
        return new(dispatch, job);
    }

    private static async Task<IReadOnlyList<EmbeddingResult>> EmbedAsync(EmbeddingWorkBatch batch)
    {
        var provider = new DeterministicTokenHashEmbeddingProvider();
        var results = new List<EmbeddingResult>();
        foreach (var chunk in batch.Chunks) results.Add(await provider.CreateEmbeddingAsync(chunk.SearchText, CancellationToken.None));
        return results;
    }

    private sealed class RecordingProvider : IBatchedEmbeddingProvider
    {
        public EmbeddingProfile Profile => EmbeddingCheckpointTests.Profile;
        public int MaximumBatchSize => 4;
        public List<string> Inputs { get; } = [];
        public List<int> BatchSizes { get; } = [];
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct) => new DeterministicTokenHashEmbeddingProvider().CreateEmbeddingAsync(text, ct);
        public async ValueTask<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Inputs.AddRange(texts); BatchSizes.Add(texts.Count);
            var results = new List<EmbeddingResult>();
            foreach (var text in texts) results.Add(await CreateEmbeddingAsync(text, ct));
            return results;
        }
    }

    private sealed class NullStatusEventPublisher : IStatusEventPublisher
    {
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
