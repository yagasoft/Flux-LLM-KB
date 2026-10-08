using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class CheckpointRecoveryPerformanceTests(NativeSqlServerFixture fixture, ITestOutputHelper output)
    : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData("epoch")]
    [InlineData("draft")]
    [InlineData("deleted-record")]
    [InlineData("job-stage")]
    [InlineData("delivery")]
    [InlineData("seal")]
    [InlineData("publish-dispatch")]
    [InlineData("input")]
    [InlineData("payload")]
    [InlineData("normalisation")]
    [InlineData("count")]
    public async Task Batched_probe_still_refuses_corrupt_retained_checkpoints(string corruption)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Checkpoint integrity control.", publish: false, embed: false);
        var now = DateTimeOffset.UtcNow;
        var dispatchClaim = await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync("integrity-dispatch", now, TimeSpan.FromMinutes(2), [PipelineOperations.Embed], CancellationToken.None);
        Assert.NotNull(dispatchClaim);
        var jobClaim = await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(dispatchClaim, "integrity-worker", now, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(jobClaim);
        var work = new StageWorkItem(dispatchClaim, jobClaim);
        var checkpoints = new SqlEmbeddingCheckpointStore(environment.Factory, TimeProvider.System);
        var profile = new EmbeddingProfile(Infrastructure.Inference.DeterministicTokenHashEmbeddingProvider.Fingerprint, 256);
        var batch = await checkpoints.ReadNextAsync(work, profile, CancellationToken.None);
        var embedding = await environment.Embeddings.CreateEmbeddingAsync(batch.Chunks.Single().SearchText, CancellationToken.None);
        await checkpoints.CommitAsync(work, batch, [embedding], CancellationToken.None);
        var sealedBatch = await checkpoints.ReadNextAsync(work, profile, CancellationToken.None);
        await new SqlStageTransitionStore(environment.Factory).TransitionAsync(new(work.DispatchMessage, work.Job,
            new StageArtifact(Guid.NewGuid(), PipelineStage.Embed, sealedBatch.CompletedChecksum!, EmbedDraftDefaults.ArtifactContentType,
                batch.GenerationId.ToString("D"), now), PipelineStage.Publish, PipelineOperations.Publish, nameof(CheckpointRecoveryPerformanceTests),
            new IndexingStageOutput(IndexGenerationId: batch.GenerationId, ModelFingerprint: profile.ModelFingerprint, UsePersistedEmbeddingDraft: true)), CancellationToken.None);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var draft = await context.IndexGenerations.SingleAsync();
            var job = await context.Jobs.SingleAsync(value => value.Id == draft.EmbeddingJobId);
            var record = await context.PipelineRecords.SingleAsync(value => value.Id == job.PipelineRecordId);
            var dispatch = await context.OutboxMessages.SingleAsync(value => value.JobId == job.Id);
            var vector = await context.Vectors.Include(value => value.TextChunk).SingleAsync();
            switch (corruption)
            {
                case "epoch": draft.CorpusEpoch = Guid.NewGuid(); break;
                case "draft": draft.MetadataChecksum = new string('a', 64); break;
                case "deleted-record": record.IsDeleted = true; break;
                case "job-stage": job.Stage = (int)PipelineStage.Publish; break;
                case "delivery": dispatch.DispatchedAtUtc = null; break;
                case "seal": (await context.Artifacts.SingleAsync(value => value.Stage == (int)PipelineStage.Embed)).ContentHash = new string('0', 64); break;
                case "publish-dispatch": (await context.OutboxMessages.SingleAsync(value => value.Stage == (int)PipelineStage.Publish)).DispatchGeneration += 1; break;
                case "input": vector.TextChunk.ContextHeader = "Changed canonical input"; break;
                case "payload": vector.Values = vector.Values.ToArray(); vector.Values[0] ^= 1; context.Entry(vector).Property(value => value.Values).IsModified = true; break;
                case "normalisation":
                    vector.Values = new byte[vector.Values.Length];
                    vector.PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(vector.Values));
                    break;
                case "count": draft.VectorCount += 1; break;
            }
            await context.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System)
            .ReadActiveAsync(CancellationToken.None).AsTask());
    }

    [Visibility.RepositoryRecoveryMigrationDeploymentTests.RecoveryScaleFact]
    public async Task Healthy_probe_and_real_query_lease_meet_the_existing_budget_at_retained_production_scale()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Synthetic scale control.", embed: false);
        var expected = await SeedEmptyCheckpointsAsync(environment.Factory, 1336);
        await using (var seed = await environment.Factory.CreateDbContextAsync())
        {
            seed.Database.SetCommandTimeout(180);
            await seed.Database.ExecuteSqlRawAsync(ScaleSql);
            Assert.Equal(50_000, await seed.Vectors.LongCountAsync());
            Assert.Equal(2634, await seed.IndexGenerations.LongCountAsync());
            Assert.Equal(16_745_071, await seed.IndexGenerationVectors.LongCountAsync());
        }
        var observer = new ReadObserver(blockJobs: true);
        using var payloadReads = new PayloadReadObserver();
        using var probeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var queryBudget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var timer = Stopwatch.StartNew();
        var probe = new SqlDerivedIndexRecoveryStore(new ObservedFactory(fixture.ConnectionString, observer), TimeProvider.System)
            .ReadActiveAsync(probeBudget.Token).AsTask();
        var queryTimer = Stopwatch.StartNew();
        Task<ICorpusGenerationLease?>? query = null;
        try
        {
            await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            queryTimer.Restart();
            query = new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System).TryAcquireAsync(Guid.NewGuid(),
                new(Environment.ProcessId, DateTimeOffset.UtcNow, new string('a', 64)), new string('b', 64), 1024, queryBudget.Token).AsTask();
            observer.Release.TrySetResult();
            var snapshot = await probe;
            timer.Stop();
            var lease = await query;
            queryTimer.Stop();
            Assert.NotNull(lease);
            Assert.True(await lease.IsCurrentAsync(CancellationToken.None));
            Assert.All(expected, id => Assert.Contains(id, snapshot.ReferencedGenerationIds));
            Assert.Equal(41095, snapshot.Membership.Length);
            Assert.Equal(2, payloadReads.Reads.Count);
            Assert.Equal(50_000, payloadReads.Reads.Sum(value => value.Rows));
            Assert.All(payloadReads.Reads, value => Assert.Equal("completed", value.Outcome));
            Assert.InRange(observer.Commands + payloadReads.Reads.Count, 1, 500);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20));
            Assert.True(queryTimer.Elapsed < TimeSpan.FromSeconds(20));
        }
        finally
        {
            probeBudget.Cancel();
            queryBudget.Cancel();
            observer.Release.TrySetResult();
            await ((Task)probe).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (query is not null)
            {
                await ((Task)query).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (query.IsCompletedSuccessfully && query.Result is { } acquired) await acquired.DisposeAsync();
            }
            output.WriteLine($"drafts=1336 vectors=50000 generations=2634 memberships=16745071 ef_commands={observer.Commands} payload_commands={payloadReads.Reads.Count} payload_rows={payloadReads.Reads.Sum(value => value.Rows)} payload_ms={payloadReads.Reads.Sum(value => value.ElapsedMs):F2} probe_ms={timer.Elapsed.TotalMilliseconds:F2} query_ms={queryTimer.Elapsed.TotalMilliseconds:F2} managed_bytes={GC.GetTotalMemory(false)}");
        }
    }

    private const string ScaleSql = """
        SET NOCOUNT ON;
        SELECT TOP(50000) CONVERT(int,ROW_NUMBER() OVER(ORDER BY (SELECT NULL))) n
            INTO #Numbers FROM sys.all_objects a CROSS JOIN sys.all_objects b;
        CREATE UNIQUE CLUSTERED INDEX IX_n ON #Numbers(n);
        SELECT CONVERT(int,ROW_NUMBER() OVER(ORDER BY g.Id)) n,g.Id,g.EmbeddingJobId,j.PipelineRecordId,j.SourceRevision,
            NEWID() CanonicalArtifactId,NEWID() EmbedArtifactId INTO #Drafts
            FROM IndexGenerations g JOIN Jobs j ON j.Id=g.EmbeddingJobId;
        DECLARE @content nvarchar(100)=N'Synthetic checkpoint passage.';
        DECLARE @contentHash varchar(64)=LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varchar(max),@content)),2));
        DECLARE @payload varbinary(max)=0x0000803F+CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),CHAR(0)),4092));
        DECLARE @payloadHash varchar(64)=LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',@payload),2));
        INSERT Artifacts(Id,PipelineRecordId,SourceRevision,Stage,ContentHash,ContentType,SearchText,CreatedAtUtc)
            SELECT CanonicalArtifactId,PipelineRecordId,SourceRevision,3,@contentHash,N'text/plain',@content,SYSDATETIMEOFFSET() FROM #Drafts;
        INSERT TextChunks(ArtifactId,SourceRevision,Ordinal,StartOffset,Length,Content,ContentHash,PassagePolicyFingerprint,ContextHeader,SearchInputHash)
            SELECT d.CanonicalArtifactId,d.SourceRevision,(n.n-1)/1336,0,LEN(@content),@content,@contentHash,N'synthetic-policy',N'',@contentHash
            FROM #Numbers n JOIN #Drafts d ON d.n=((n.n-1)%1336)+1;
        INSERT Vectors(TextChunkId,ModelFingerprint,Dimensions,[Values],TextChunkContentHash,PayloadChecksum,SourceRevision,IsDeleted,IndexGenerationId,CreatedAtUtc,SearchInputHash)
            SELECT c.Id,REPLICATE('b',64),1024,@payload,@contentHash,@payloadHash,c.SourceRevision,0,d.Id,SYSDATETIMEOFFSET(),@contentHash
            FROM TextChunks c JOIN #Drafts d ON d.CanonicalArtifactId=c.ArtifactId;
        UPDATE g SET VectorCount=v.n FROM IndexGenerations g JOIN (SELECT IndexGenerationId,COUNT_BIG(*) n FROM Vectors GROUP BY IndexGenerationId) v ON v.IndexGenerationId=g.Id;
        UPDATE j SET PublicState=4 FROM Jobs j JOIN #Drafts d ON d.EmbeddingJobId=j.Id;
        UPDATE p SET CurrentStage=5 FROM PipelineRecords p JOIN #Drafts d ON d.PipelineRecordId=p.Id;
        UPDATE o SET DispatchedAtUtc=SYSDATETIMEOFFSET() FROM OutboxMessages o JOIN #Drafts d ON d.EmbeddingJobId=o.JobId;
        INSERT Artifacts(Id,PipelineRecordId,SourceRevision,Stage,ContentHash,ContentType,SearchText,CreatedAtUtc)
            SELECT d.EmbedArtifactId,d.PipelineRecordId,d.SourceRevision,4,
                LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),@payload,2),CONVERT(int,g.VectorCount)),2)),2)),
                N'application/vnd.fluxknowledge.embedding-set+binary',LOWER(CONVERT(nvarchar(36),d.Id)),SYSDATETIMEOFFSET()
            FROM #Drafts d JOIN IndexGenerations g ON g.Id=d.Id;
        SELECT d.*,NEWID() PublishJobId INTO #Next FROM #Drafts d;
        INSERT Jobs(Id,PipelineRecordId,SourceRevision,Stage,Operation,PublicState,DueAtUtc,AttemptCount,LeaseGeneration)
            SELECT PublishJobId,PipelineRecordId,SourceRevision,5,N'publish',0,SYSDATETIMEOFFSET(),0,0 FROM #Next;
        INSERT OutboxMessages(Id,JobId,PipelineRecordId,SourceRevision,Stage,Operation,DispatchGeneration,IdempotencyKey,DueAtUtc,CreatedAtUtc,LeaseGeneration)
            SELECT NEWID(),PublishJobId,PipelineRecordId,SourceRevision,5,N'publish',2,CONVERT(nvarchar(36),PublishJobId),SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET(),0 FROM #Next;
        SELECT n,NEWID() Id INTO #Placed FROM #Numbers WHERE n<=1298;
        INSERT IndexGenerations(Id,CorpusEpoch,CorpusVersion,ModelFingerprint,Dimensions,IndexPath,MetadataChecksum,VectorCount,CreatedAtUtc,ValidatedAtUtc)
            SELECT p.Id,s.CorpusEpoch,s.CorpusVersion,REPLICATE('b',64),1024,N'C:\\synthetic-index\\generations\\'+CONVERT(nvarchar(36),p.Id),
                REPLICATE('c',64),CASE WHEN p.n=1 THEN 41095 ELSE 0 END,SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET()
            FROM #Placed p CROSS JOIN IndexState s;
        SELECT CONVERT(int,ROW_NUMBER() OVER(ORDER BY VectorId)) n,VectorId INTO #Vectors FROM Vectors;
        INSERT IndexGenerationVectors(GenerationId,VectorId)
            SELECT p.Id,v.VectorId FROM #Placed p CROSS JOIN #Vectors v WHERE p.n=1 AND v.n<=41095
                OR p.n>1 AND v.n<=12880 AND CONVERT(bigint,p.n-2)*12880+v.n<=16745071-41095;
        UPDATE s SET ActiveIndexGenerationId=p.Id FROM IndexState s CROSS JOIN #Placed p WHERE p.n=1;
        """;

    [NativeSqlServerFact]
    public async Task Healthy_probe_batches_checkpoint_checks_instead_of_round_tripping_per_draft()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Published control.");
        var expected = await SeedEmptyCheckpointsAsync(environment.Factory, 100);
        var observer = new ReadObserver();
        var factory = new ObservedFactory(fixture.ConnectionString, observer);
        var timer = Stopwatch.StartNew();
        var snapshot = await new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System).ReadActiveAsync(CancellationToken.None);
        output.WriteLine($"drafts=100 commands={observer.Commands} elapsed_ms={timer.Elapsed.TotalMilliseconds:F2}");
        Assert.All(expected, id => Assert.Contains(id, snapshot.ReferencedGenerationIds));
        Assert.False(snapshot.IsProjectionUnavailable);
        // A deterministic command budget proves batching independently of machine speed.
        Assert.InRange(observer.Commands, 1, 50);
    }

    [NativeSqlServerFact]
    public async Task Checkpoint_reads_hold_the_publication_fence_and_block_real_query_lease_acquisition()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Published lock control.");
        await SeedEmptyCheckpointsAsync(environment.Factory, 2);
        var active = await environment.ActiveGenerationAsync();
        var observer = new ReadObserver(blockJobs: true);
        var probe = new SqlDerivedIndexRecoveryStore(new ObservedFactory(fixture.ConnectionString, observer), TimeProvider.System)
            .ReadActiveAsync(CancellationToken.None).AsTask();
        var owner = new GpuInteractiveOwnerIdentity(Environment.ProcessId, DateTimeOffset.UtcNow, new string('a', 64));
        var leases = new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System);
        try
        {
            await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancelledWhileFenced = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await using var blocked = await leases.TryAcquireAsync(Guid.NewGuid(), owner,
                    active.ModelFingerprint, active.Dimensions, cancelledWhileFenced.Token);
                Assert.Fail("Query acquisition crossed the held publication fence.");
            });
        }
        finally
        {
            observer.Release.TrySetResult();
            await probe.WaitAsync(TimeSpan.FromSeconds(15));
        }
        await using var query = await leases.TryAcquireAsync(Guid.NewGuid(), owner,
            active.ModelFingerprint, active.Dimensions, CancellationToken.None);
        Assert.NotNull(query);
        Assert.True(await query.IsCurrentAsync(CancellationToken.None));
        await using var verification = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await verification.CorpusQueryLeases.ToArrayAsync());
    }

    internal static async Task<Guid[]> SeedEmptyCheckpointsAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, int count)
    {
        await using var context = await factory.CreateDbContextAsync();
        var sourceId = await context.SourceIdentities.Select(value => value.Id).FirstAsync();
        var epoch = await context.IndexState.Select(value => value.CorpusEpoch).SingleAsync();
        var now = DateTimeOffset.UtcNow;
        var result = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            var recordId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var generationId = Guid.NewGuid();
            context.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = recordId, SourceIdentityId = sourceId, Revision = index + 2, ContentHash = new string('a', 64),
                RootLineageRecordId = recordId, CurrentStage = (int)PipelineStage.Embed, RegisteredAtUtc = now
            });
            context.Jobs.Add(new JobEntity
            {
                Id = jobId, PipelineRecordId = recordId, SourceRevision = index + 2,
                Stage = (int)PipelineStage.Embed, Operation = PipelineOperations.Embed,
                PublicState = (int)PublicJobState.WorkerQueued, DueAtUtc = now
            });
            context.OutboxMessages.Add(new OutboxMessageEntity
            {
                Id = Guid.NewGuid(), JobId = jobId, PipelineRecordId = recordId, SourceRevision = index + 2,
                Stage = (int)PipelineStage.Embed, Operation = PipelineOperations.Embed, DispatchGeneration = 1,
                IdempotencyKey = jobId.ToString("N"), DueAtUtc = now, CreatedAtUtc = now
            });
            context.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = generationId, EmbeddingJobId = jobId, CorpusEpoch = epoch, CorpusVersion = 0,
                ModelFingerprint = new string('b', 64), Dimensions = 1024,
                MetadataChecksum = EmbedDraftDefaults.MetadataChecksum, CreatedAtUtc = now
            });
            result.Add(generationId);
        }
        await context.SaveChangesAsync();
        return result.ToArray();
    }

    private sealed class ObservedFactory(string connectionString, ReadObserver observer) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString).AddInterceptors(observer).Options);
    }

    private sealed class ReadObserver(bool blockJobs = false) : DbCommandInterceptor
    {
        public int Commands { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            if (blockJobs && command.CommandText.Contains("FROM [Jobs]", StringComparison.Ordinal) && Entered.TrySetResult())
                await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class PayloadReadObserver : EventListener
    {
        public ConcurrentQueue<(long Rows, double ElapsedMs, string Outcome)> Reads { get; } = new();
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "FluxKnowledge-HybridSearch") EnableEvents(eventSource, EventLevel.Informational);
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId == 8 && eventData.Payload is { Count: 5 } payload)
                Reads.Enqueue(((long)payload[2]!, (double)payload[3]!, (string)payload[4]!));
        }
    }
}
