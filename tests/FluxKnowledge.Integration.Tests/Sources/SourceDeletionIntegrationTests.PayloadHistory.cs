using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerFact]
    [Trait("Category","Scale")]
    public async Task Payload_pages_amortise_217685_task_history_with_replay_rollback_and_fresh_processes()
    {
        const int payloadCount=32017;
        var (source,operation,lastBatch)=await SeedCompletedBatchGraphAsync(1);
        await using(var setup=CreateContext())
        {
            await SeedProductionSizedHistoryAsync(setup,source);
            await SeedLargePurgePayloadAsync(setup,source,payloadCount);
        }
        var control=await SeedRootAsync("payload-history-control",SourceRootState.Enabled,DateTimeOffset.UtcNow);
        var (controlVector,controlGenerations)=await SeedMembershipsAsync(control,1,DateTimeOffset.UtcNow);
        byte[] sourceVersion,jobVersion,controlVersion;
        await using(var before=CreateContext())
        {
            sourceVersion=await before.PipelineRecords.Where(record=>record.Id==source.RecordId).Select(record=>record.RowVersion).SingleAsync();
            jobVersion=await before.Jobs.Where(job=>job.Id==source.JobId).Select(job=>job.RowVersion).SingleAsync();
            controlVersion=await before.PipelineRecords.Where(record=>record.Id==control.RecordId).Select(record=>record.RowVersion).SingleAsync();
        }
        var timing=new GraphCommandTimings();
        var publication=new PayloadPublicationWait(_fixture.ConnectionString);
        var options=new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString,sql=>sql.CommandTimeout(30)
                .ExecutionStrategy(dependencies=>new CommitLossRetryStrategy(dependencies)))
            .AddInterceptors(timing,publication,new LoseOneCommitAcknowledgement()).Options;
        var writer=new SqlSourceDeletionStore(new RootDeletionScaleFactory(options),TimeProvider.System);
        var ordinary=new SqlSourceDeletionStore(TimedFactory(),TimeProvider.System);
        var work=Assert.IsType<SourceDeletionWorkItem>(await ordinary.ClaimNextAsync(default));
        Assert.Equal(operation,work.OperationId);Assert.Equal("purge-records",work.Phase);
        await TimedPayloadCallAsync(()=>writer.PurgeAsync(work,null,default).AsTask(),"first-vector-lost-ack");
        await publication.FinishAsync();
        Assert.True(publication.WaitedBehindPayload);
        output.WriteLine($"payload_publication_wait_ms={publication.WaitMilliseconds:F1}");
        await AssertPayloadAsync(16017,payloadCount);
        await TimedPayloadCallAsync(()=>ordinary.PurgeAsync(work,null,default).AsTask(),"same-claim-replay");
        await AssertPayloadAsync(16017,payloadCount);

        var second=Assert.IsType<SourceDeletionWorkItem>(await ordinary.ClaimNextAsync(default));
        await AssertCancelledPayloadAsync(second,"[Vectors]",16017,payloadCount);
        await TimedPayloadCallAsync(()=>ordinary.PurgeAsync(second,null,default).AsTask(),"second-vector");
        await AssertPayloadAsync(17,payloadCount);
        await TimedPayloadCallAsync(async()=>{await RunFreshExecutionPageAsync(operation);return true;},"partial-vector-fresh-clr");
        await AssertPayloadAsync(0,payloadCount);

        var firstChunk=Assert.IsType<SourceDeletionWorkItem>(await ordinary.ClaimNextAsync(default));
        await TimedPayloadCallAsync(()=>new SqlSourceDeletionStore(TimedFactory(),TimeProvider.System).PurgeAsync(firstChunk,null,default).AsTask(),"first-chunk");
        await AssertPayloadAsync(0,16017);
        var secondChunk=Assert.IsType<SourceDeletionWorkItem>(await ordinary.ClaimNextAsync(default));
        await AssertCancelledPayloadAsync(secondChunk,"[TextChunks]",0,16017);
        await TimedPayloadCallAsync(()=>ordinary.PurgeAsync(secondChunk,null,default).AsTask(),"second-chunk");
        await AssertPayloadAsync(0,17);
        await TimedPayloadCallAsync(async()=>{await RunFreshExecutionPageAsync(operation);return true;},"partial-chunk-fresh-clr");
        await AssertPayloadAsync(0,0);
        Assert.InRange(timing.MaxMs,0,15000);
        output.WriteLine($"payload_history tasks=217685 parents=20000 vector_bytes=4096 chunk_utf16=2048 max_statement_ms={timing.MaxMs:F1}");

        RootDeletionScaleFactory TimedFactory()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString,sql=>sql.CommandTimeout(30)).AddInterceptors(timing).Options);

        async Task TimedPayloadCallAsync<T>(Func<Task<T>> action,string label)
        {
            var watch=Stopwatch.StartNew();await action();watch.Stop();
            output.WriteLine($"payload_unit label={label} elapsed_ms={watch.Elapsed.TotalMilliseconds:F1}");
            Assert.InRange(watch.Elapsed.TotalMilliseconds,0,30000);
        }

        async Task AssertCancelledPayloadAsync(SourceDeletionWorkItem claim,string table,int vectors,int chunks)
        {
            using var cancel=new CancellationTokenSource();
            var factory=new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(_fixture.ConnectionString,sql=>sql.CommandTimeout(30))
                .AddInterceptors(timing,new CancelPayloadDelete(cancel,table)).Options);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await new SqlSourceDeletionStore(factory,TimeProvider.System)
                .PurgeAsync(claim,null,cancel.Token));
            await AssertPayloadAsync(vectors,chunks);
        }

        async Task AssertPayloadAsync(int vectors,int chunks)
        {
            await using var check=CreateContext();
            Assert.Equal(vectors,await check.Vectors.CountAsync(vector=>vector.TextChunk.Artifact.PipelineRecordId==source.RecordId));
            Assert.Equal(chunks,await check.TextChunks.CountAsync(chunk=>chunk.Artifact.PipelineRecordId==source.RecordId));
            Assert.Equal(chunks,await check.TextChunkWindowEligibilities.CountAsync(flag=>flag.TextChunk.Artifact.PipelineRecordId==source.RecordId));
            Assert.Equal(217685,await check.GpuMiniTasks.CountAsync());
            Assert.Equal(217685,await check.DocumentOcrRequests.CountAsync());
            Assert.Equal(217685,await check.GpuBatches.CountAsync());
            Assert.Equal(217685,await check.GpuExecutorResultReceipts.CountAsync());
            Assert.True(await check.GpuBatches.AnyAsync(batch=>batch.Id==lastBatch));
            Assert.Equal(sourceVersion,await check.PipelineRecords.Where(record=>record.Id==source.RecordId).Select(record=>record.RowVersion).SingleAsync());
            Assert.Equal(jobVersion,await check.Jobs.Where(job=>job.Id==source.JobId).Select(job=>job.RowVersion).SingleAsync());
            Assert.Equal(controlVersion,await check.PipelineRecords.Where(record=>record.Id==control.RecordId).Select(record=>record.RowVersion).SingleAsync());
            Assert.True(await check.Vectors.AnyAsync(vector=>vector.VectorId==controlVector.VectorId&&!vector.IsDeleted));
            Assert.True(await check.IndexGenerations.AnyAsync(generation=>generation.Id==controlGenerations[0]&&generation.RetiredAtUtc==null));
            Assert.True(await check.IndexGenerationVectors.AnyAsync(member=>member.GenerationId==controlGenerations[0]&&member.VectorId==controlVector.VectorId));
        }
    }

    private static async Task SeedLargePurgePayloadAsync(FluxKnowledgeDbContext setup,SeededRoot source,int count)
    {
        var content=string.Concat(Enumerable.Repeat("Synthetic payload text. ",90))[..2048];
        var contentHash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var values=new byte[4096];BitConverter.GetBytes(1f).CopyTo(values,0);
        var payloadHash=Convert.ToHexStringLower(SHA256.HashData(values));
        var artifact=await setup.Artifacts.SingleAsync(value=>value.PipelineRecordId==source.RecordId);
        artifact.SearchText=content;artifact.ContentHash=contentHash;
        var prototype=await setup.TextChunks.SingleAsync(chunk=>chunk.ArtifactId==artifact.Id);
        prototype.Content=content;prototype.ContentHash=contentHash;prototype.Length=2048;
        await setup.SaveChangesAsync();
        setup.TextChunkWindowEligibilities.Add(CanonicalWindowEligibility.Bind(prototype,artifact,DateTimeOffset.UtcNow));
        await setup.SaveChangesAsync();
        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE Vectors SET Dimensions=1024,[Values]={values},PayloadChecksum={payloadHash},TextChunkContentHash={contentHash}
            WHERE TextChunkId={prototype.Id};
            INSERT TextChunks(ArtifactId,SourceRevision,Ordinal,StartOffset,Length,Content,ContentHash,PassagePolicyFingerprint,ContextHeader,SearchInputHash)
            SELECT original.ArtifactId,original.SourceRevision,n.Ordinal,original.StartOffset,original.Length,original.Content,
                original.ContentHash,original.PassagePolicyFingerprint,original.ContextHeader,original.SearchInputHash
            FROM TextChunks original CROSS JOIN(SELECT TOP({count-1}) ROW_NUMBER()OVER(ORDER BY(SELECT NULL)) AS Ordinal
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)n WHERE original.Id={prototype.Id};
            INSERT Vectors(TextChunkId,ModelFingerprint,Dimensions,[Values],TextChunkContentHash,PayloadChecksum,SourceRevision,
                IsDeleted,IndexGenerationId,CreatedAtUtc,SearchInputHash)
            SELECT chunk.Id,original.ModelFingerprint,original.Dimensions,original.[Values],original.TextChunkContentHash,original.PayloadChecksum,
                original.SourceRevision,1,original.IndexGenerationId,original.CreatedAtUtc,original.SearchInputHash
            FROM TextChunks chunk CROSS JOIN Vectors original
            WHERE original.TextChunkId={prototype.Id} AND chunk.ArtifactId={artifact.Id} AND chunk.Id<>{prototype.Id};
            INSERT TextChunkWindowEligibilities(TextChunkId,ArtifactId,CanonicalSourceRevision,CanonicalContentHash,CanonicalLength,
                ChunkSourceRevision,ChunkContentHash,StartOffset,Length,RuleVersion,Available,CalculatedAtUtc)
            SELECT chunk.Id,flag.ArtifactId,flag.CanonicalSourceRevision,flag.CanonicalContentHash,flag.CanonicalLength,
                flag.ChunkSourceRevision,flag.ChunkContentHash,flag.StartOffset,flag.Length,flag.RuleVersion,flag.Available,flag.CalculatedAtUtc
            FROM TextChunks chunk CROSS JOIN TextChunkWindowEligibilities flag
            WHERE flag.TextChunkId={prototype.Id} AND chunk.ArtifactId={artifact.Id} AND chunk.Id<>{prototype.Id};
            """);
    }

    private sealed class CancelPayloadDelete(CancellationTokenSource cancel,string table):DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,CommandExecutedEventData data,int result,CancellationToken ct=default)
        {if(command.CommandText.StartsWith("DELETE",StringComparison.Ordinal)&&command.CommandText.Contains(table,StringComparison.Ordinal))cancel.Cancel();return ValueTask.FromResult(result);}
    }

    private sealed class PayloadPublicationWait(string connectionString):DbCommandInterceptor
    {
        private Task? _waiter;
        internal bool WaitedBehindPayload {get;private set;}
        internal double WaitMilliseconds {get;private set;}
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData data,DbDataReader result,CancellationToken ct=default)
        {
            if(_waiter is not null||!command.CommandText.Contains("FROM [IndexState] WITH (UPDLOCK, HOLDLOCK)",StringComparison.Ordinal))return result;
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiter=Task.Run(async()=>
            {
                await using var db=new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options);
                await using var transaction=await db.Database.BeginTransactionAsync();
                var watch=Stopwatch.StartNew();entered.SetResult();
                using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await SqlPublishedPassageSelection.AcquireFenceAsync(db,budget.Token);
                WaitMilliseconds=watch.Elapsed.TotalMilliseconds;
                Assert.InRange(WaitMilliseconds,0,30000);
                await transaction.RollbackAsync();
            },CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100,ct);WaitedBehindPayload=!_waiter.IsCompleted;
            return result;
        }
        internal async Task FinishAsync(){Assert.NotNull(_waiter);await _waiter.WaitAsync(TimeSpan.FromSeconds(30));}
    }
}
