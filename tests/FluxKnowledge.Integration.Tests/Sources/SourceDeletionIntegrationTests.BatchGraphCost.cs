using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Graph_purge_with_completed_batch_history_keeps_both_cached_plan_orders_bounded(bool largeFirst) =>
        AssertCompletedBatchGraphPurgeAsync(largeFirst, pagedBatchCount: 1001, maximumPagedReads: 100_000);

    [SqlServerScaleTheory]
    [Trait("Category", "Scale")]
    [InlineData(false)]
    [InlineData(true)]
    public Task Large_graph_purge_with_completed_batch_history_keeps_both_cached_plan_orders_bounded(bool largeFirst) =>
        AssertCompletedBatchGraphPurgeAsync(largeFirst, pagedBatchCount: 32768, maximumPagedReads: 8_000_000);

    private async Task AssertCompletedBatchGraphPurgeAsync(bool largeFirst, int pagedBatchCount, int maximumPagedReads)
    {
        var control=await SeedRootAsync("batch-graph-control",SourceRootState.Enabled,DateTimeOffset.UtcNow);
        await using var connection=new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var messages=new List<string>();
        connection.InfoMessage+=(_,args)=>messages.Add(args.Message);
        await using(var stats=connection.CreateCommand())
        {stats.CommandText="SET STATISTICS IO ON;";await stats.ExecuteNonQueryAsync();}
        var factory=new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection,sql=>sql.CommandTimeout(8)).Options);
        var store=new SqlSourceDeletionStore(factory,TimeProvider.System);
        foreach(var count in largeFirst?new[]{pagedBatchCount,1}:new[]{1,pagedBatchCount})
        {
            var (source,operation,_)=await SeedCompletedBatchGraphAsync(count);
            messages.Clear();
            var watch=Stopwatch.StartNew();
            await AdvanceSqlToFilesAsync(store,operation,maxPasses:64);
            var reads=messages.SelectMany(message=>Regex.Matches(message,@"(?<!lob )logical reads (\d+),"))
                .Sum(match=>long.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture));
            output.WriteLine($"batch_graph count={count} large_first={largeFirst} sql_purge_ms={watch.Elapsed.TotalMilliseconds:F1} logical_reads={reads}");
            Assert.InRange(reads, 1, count == 1 ? 1_000 : maximumPagedReads);
            await using(var check=CreateContext())
            {
                Assert.False(await check.PipelineRecords.AnyAsync(record=>record.Id==source.RecordId));
                Assert.False(await check.GpuMiniTasks.AnyAsync(task=>task.ParentJobId==source.JobId));
                Assert.False(await check.DocumentOcrRequests.AnyAsync(request=>request.PipelineRecordId==source.RecordId));
                Assert.Empty(await check.GpuBatches.ToArrayAsync());
                Assert.True(await check.PipelineRecords.AnyAsync(record=>record.Id==control.RecordId));
            }
            // Finish the SQL-store workflow with synthetic missing-file receipts.
            // No real model, source or native index files exist in this fixture.
            for(var pass=0;pass<16;pass++)
            {
                var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
                Assert.Equal(operation,work.OperationId);
                var pending=await store.ReadPendingCleanupAsync(work,default);
                if(pending.Count>0)
                    await store.RecordCleanupResultAsync(work,pending[0].CleanupItemId,new(true,"source-delete-file-missing"),default);
                else if((await store.PurgeAsync(work,null,default)).Completed) break;
                Assert.True(pass<15,"Synthetic cleanup did not finish.");
            }
            await using var completed=CreateContext();
            Assert.Equal(3,await completed.SourceDeletionOperations.Where(value=>value.Id==operation).Select(value=>value.State).SingleAsync());
        }
    }

    [NativeSqlServerFact]
    public Task Graph_purge_refuses_a_foreign_task_in_the_last_of_many_owned_batches_without_mutation() =>
        AssertGraphPurgeRefusesForeignTaskAsync(1001);

    [SqlServerScaleFact]
    [Trait("Category", "Scale")]
    public Task Large_graph_purge_refuses_a_foreign_task_in_the_last_of_many_owned_batches_without_mutation() =>
        AssertGraphPurgeRefusesForeignTaskAsync(32768);

    private async Task AssertGraphPurgeRefusesForeignTaskAsync(int batchCount)
    {
        var (source,operation,lastBatch)=await SeedCompletedBatchGraphAsync(batchCount);
        var control=await SeedRootAsync("batch-graph-foreign",SourceRootState.Enabled,DateTimeOffset.UtcNow);
        var foreign=Guid.NewGuid();
        await using(var seed=CreateContext())
        {
            AddLocalOcrTask(seed,control,foreign,GpuMiniTaskExecutionState.Completed,DateTimeOffset.UtcNow);
            seed.GpuMiniTasks.Local.Single().BatchId=lastBatch;
            await seed.SaveChangesAsync();
        }
        byte[][] versions;
        await using(var before=CreateContext())
            versions=await before.GpuMiniTasks.OrderBy(task=>task.Id).Select(task=>task.RowVersion).ToArrayAsync();
        var factory=new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString,sql=>sql.CommandTimeout(8)).Options);
        var store=new SqlSourceDeletionStore(factory,TimeProvider.System);
        var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal(operation,work.OperationId);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(async()=>await store.PurgeAsync(work,null,default));
        Assert.Equal("source-delete-local-ocr-shared-batch",error.Message);
        await using var check=CreateContext();
        Assert.Equal(versions,await check.GpuMiniTasks.OrderBy(task=>task.Id).Select(task=>task.RowVersion).ToArrayAsync());
        Assert.Equal(batchCount,await check.GpuBatches.CountAsync());
        Assert.Equal(batchCount+1,await check.DocumentOcrRequests.CountAsync());
        Assert.True(await check.PipelineRecords.AnyAsync(record=>record.Id==source.RecordId));
        Assert.True(await check.PipelineRecords.AnyAsync(record=>record.Id==control.RecordId));
        Assert.NotEmpty(await check.Vectors.ToArrayAsync());
    }

    private async Task<(SeededRoot Source,Guid Operation,Guid LastBatch)> SeedCompletedBatchGraphAsync(int count)
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("completed-batch-graph",SourceRootState.Deleting,now);
        var operation=await AddDeletionAsync(source,now);
        await SeedDistinctMembershipVectorsAsync(source,1,now);
        var ordinary=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await AdvanceSqlToFilesAsync(ordinary,operation,stopAtCutover:true);
        for(var pass=0;pass<4;pass++)
        {
            var work=Assert.IsType<SourceDeletionWorkItem>(await ordinary.ClaimNextAsync(default));
            var result=await ordinary.PurgeAsync(work,null,default);
            if(result.Phase=="purge-records") break;
            Assert.True(result.ContinueImmediately,result.ReasonCode);
            Assert.True(pass<3);
        }
        var prototype=Guid.NewGuid();
        await using var seed=CreateContext();
        if(!await seed.GpuCapacitySlots.AnyAsync(slot=>slot.SlotKey=="paddleocr-vl-local-gpu-0"))
            seed.GpuCapacitySlots.Add(new(){SlotKey="paddleocr-vl-local-gpu-0",State=(int)GpuCapacitySlotState.Available,UpdatedAtUtc=now});
        AddLocalOcrTask(seed,source,prototype,GpuMiniTaskExecutionState.Completed,now);
        await seed.SaveChangesAsync();
        await seed.Database.ExecuteSqlInterpolatedAsync($"""
            CREATE TABLE #History(Id uniqueidentifier NOT NULL PRIMARY KEY);
            INSERT #History VALUES({prototype});
            INSERT #History SELECT TOP({count-1}) NEWID() FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            INSERT GpuMiniTasks(Id,ParentJobId,SourceRevision,PriorityLane,ModelRuntimeKey,SettingsFingerprint,
                EstimatedBytes,AdmissionGeneration,IdempotencyKey,State,ReservationAttemptCount,CreatedAtUtc,InteractiveCancellationRequested)
            SELECT h.Id,p.ParentJobId,p.SourceRevision,p.PriorityLane,p.ModelRuntimeKey,p.SettingsFingerprint,
                p.EstimatedBytes,1,CONVERT(nvarchar(36),h.Id),p.State,0,p.CreatedAtUtc,0
            FROM #History h CROSS JOIN GpuMiniTasks p WHERE p.Id={prototype} AND h.Id<>{prototype};
            INSERT DocumentOcrRequests(MiniTaskId,ParentJobId,PipelineRecordId,SourceRevision,RetainedSourceRevisionId,
                ContentSha256,RequestedPageIndexesJson,ModelRuntimeKey,SettingsFingerprint,State,CreatedAtUtc,UpdatedAtUtc,ExecutionPhase)
            SELECT h.Id,p.ParentJobId,p.PipelineRecordId,p.SourceRevision,p.RetainedSourceRevisionId,
                p.ContentSha256,p.RequestedPageIndexesJson,p.ModelRuntimeKey,p.SettingsFingerprint,p.State,p.CreatedAtUtc,p.UpdatedAtUtc,0
            FROM #History h CROSS JOIN DocumentOcrRequests p WHERE p.MiniTaskId={prototype} AND h.Id<>{prototype};
            INSERT GpuBatches(Id,CapacitySlotKey,PriorityLane,ModelRuntimeKey,SettingsFingerprint,ItemCount,
                EstimatedBytes,AdmissionGeneration,OwnerKey,State,CreatedAtUtc,UpdatedAtUtc)
            SELECT h.Id,'paddleocr-vl-local-gpu-0',p.PriorityLane,p.ModelRuntimeKey,p.SettingsFingerprint,1,
                p.EstimatedBytes,1,'batch-graph-fixture',2,{now},{now}
            FROM #History h CROSS JOIN GpuMiniTasks p WHERE p.Id={prototype};
            UPDATE task SET BatchId=task.Id,AdmissionGeneration=1 FROM GpuMiniTasks task JOIN #History h ON h.Id=task.Id;
            DROP TABLE #History;
            """);
        return(source,operation,await seed.GpuMiniTasks.Where(task=>task.ParentJobId==source.JobId).OrderBy(task=>task.BatchId)
            .Select(task=>task.BatchId!.Value).LastAsync());
    }
}
