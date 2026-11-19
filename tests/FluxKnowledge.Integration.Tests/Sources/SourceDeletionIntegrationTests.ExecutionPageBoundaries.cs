using System.Data.Common;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Execution_pages_delete_mixed_ocr_and_embedding_history_through_the_complete_graph()
    {
        var (source,operation,_)=await SeedCompletedBatchGraphAsync(2001);
        var runtime=new EmbeddingGpuRuntime("page-mixed-embedding",new string('c',64),new("page-mixed:1",1),4);
        await using(var seed=CreateContext())
        {
            var ids=(await seed.GpuMiniTasks.Select(task=>task.Id).ToArrayAsync()).Order().Where((_,index)=>index%2==0).ToArray();
            var generation=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
            var stamp=await seed.IndexState.SingleAsync();
            seed.IndexGenerations.Add(new(){Id=generation,EmbeddingJobId=source.JobId,CorpusEpoch=stamp.CorpusEpoch,CorpusVersion=stamp.CorpusVersion,
                ModelFingerprint=runtime.Profile.ModelFingerprint,Dimensions=1,IndexPath="",MetadataChecksum=new string('a',64),VectorCount=0,CreatedAtUtc=now,RetiredAtUtc=now});
            await seed.SaveChangesAsync();
            var selected=System.Text.Json.JsonSerializer.Serialize(ids);
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT EmbeddingGpuRequests(MiniTaskId,ParentJobId,PipelineRecordId,SourceRevision,GenerationId,CorpusEpoch,
                    ModelFingerprint,Dimensions,InputsJson,InputDigest,State,NativeCleanupConfirmed,CleanupConfirmedAtUtc,CreatedAtUtc,UpdatedAtUtc)
                SELECT request.MiniTaskId,request.ParentJobId,request.PipelineRecordId,request.SourceRevision,{generation},state.CorpusEpoch,
                    {runtime.Profile.ModelFingerprint},1,'[]',{new string('a',64)},2,1,{now},{now},{now}
                FROM DocumentOcrRequests request CROSS JOIN IndexState state
                WHERE state.Id=1 AND request.MiniTaskId IN(SELECT Id FROM OPENJSON({selected})WITH(Id uniqueidentifier '$'));
                DELETE FROM DocumentOcrRequests WHERE MiniTaskId IN(SELECT Id FROM OPENJSON({selected})WITH(Id uniqueidentifier '$'));
                UPDATE GpuMiniTasks SET PriorityLane=1,ModelRuntimeKey={runtime.RuntimeKey},SettingsFingerprint={runtime.SettingsFingerprint}
                WHERE Id IN(SELECT Id FROM OPENJSON({selected})WITH(Id uniqueidentifier '$'));
                UPDATE GpuBatches SET PriorityLane=1,ModelRuntimeKey={runtime.RuntimeKey},SettingsFingerprint={runtime.SettingsFingerprint}
                WHERE Id IN(SELECT Id FROM OPENJSON({selected})WITH(Id uniqueidentifier '$'));
                UPDATE Jobs SET Stage=4,Operation='embed' WHERE Id={source.JobId};
                """);
        }
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System,embeddingRuntime:runtime);
        await ReachExecutionGraphAsync(store,source,operation);
        var work=Assert.IsType<FluxKnowledge.Application.Ports.SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.True((await store.PurgeAsync(work,null,default)).ContinueImmediately);
        await using(var page=CreateContext())
        {
            Assert.Equal(1001,await page.GpuMiniTasks.CountAsync());
            Assert.NotEmpty(await page.DocumentOcrRequests.ToArrayAsync());
            Assert.NotEmpty(await page.EmbeddingGpuRequests.ToArrayAsync());
        }
        await AdvanceSqlToFilesAsync(store,operation);
        await using var final=CreateContext();
        Assert.Empty(await final.DocumentOcrRequests.ToArrayAsync());Assert.Empty(await final.EmbeddingGpuRequests.ToArrayAsync());
        Assert.Empty(await final.GpuMiniTasks.ToArrayAsync());Assert.Empty(await final.GpuBatches.ToArrayAsync());
        Assert.False(await final.PipelineRecords.AnyAsync(record=>record.Id==source.RecordId));
    }

    [NativeSqlServerFact]
    public async Task Execution_page_rechecks_a_foreign_task_inserted_after_the_previous_page_committed()
    {
        var (source,operation,_)=await SeedCompletedBatchGraphAsync(2001);
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await ReachExecutionGraphAsync(store,source,operation);
        var first=Assert.IsType<FluxKnowledge.Application.Ports.SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.True((await store.PurgeAsync(first,null,default)).ContinueImmediately);
        var control=await SeedRootAsync("late-foreign-task",SourceRootState.Enabled,DateTimeOffset.UtcNow);
        await using(var insert=CreateContext())
        {
            var batch=await insert.GpuMiniTasks.Select(task=>task.BatchId).FirstAsync();
            AddLocalOcrTask(insert,control,Guid.NewGuid(),GpuMiniTaskExecutionState.Completed,DateTimeOffset.UtcNow);
            insert.GpuMiniTasks.Local.Single().BatchId=batch;await insert.SaveChangesAsync();
        }
        var next=Assert.IsType<FluxKnowledge.Application.Ports.SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var failure=await Assert.ThrowsAsync<InvalidOperationException>(async()=>await store.PurgeAsync(next,null,default));
        Assert.Equal("source-delete-local-ocr-shared-batch",failure.Message);
        await using var check=CreateContext();Assert.Equal(1002,await check.GpuMiniTasks.CountAsync());
        Assert.Equal(1001,await check.GpuBatches.CountAsync());
        Assert.True(await check.PipelineRecords.AnyAsync(record=>record.Id==source.RecordId));
        Assert.True(await check.PipelineRecords.AnyAsync(record=>record.Id==control.RecordId));
    }

    [NativeSqlServerFact]
    public async Task Execution_pages_keep_shared_batch_metadata_until_its_last_owned_task_is_deleted()
    {
        var (source,operation,shared)=await SeedCompletedBatchGraphAsync(2001);
        await using(var seed=CreateContext())
        {
            await seed.GpuMiniTasks.ExecuteUpdateAsync(set=>set.SetProperty(task=>task.BatchId,(Guid?)shared));
            await seed.GpuBatches.Where(batch=>batch.Id!=shared).ExecuteDeleteAsync();
            await seed.GpuBatches.Where(batch=>batch.Id==shared).ExecuteUpdateAsync(set=>set.SetProperty(batch=>batch.ItemCount,2001));
        }
        await SeedExecutionPageReceiptsAsync();
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await ReachExecutionGraphAsync(store,source,operation);
        byte[] batchVersion;
        await using(var before=CreateContext())batchVersion=await before.GpuBatches.Select(batch=>batch.RowVersion).SingleAsync();
        foreach(var remaining in new[]{1001,1})
        {
            var work=Assert.IsType<FluxKnowledge.Application.Ports.SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            Assert.True((await store.PurgeAsync(work,null,default)).ContinueImmediately);
            await using var check=CreateContext();
            Assert.Equal(remaining,await check.GpuMiniTasks.CountAsync());
            Assert.Equal(remaining,await check.GpuExecutorResultReceipts.CountAsync());
            Assert.Equal(batchVersion,await check.GpuBatches.Select(batch=>batch.RowVersion).SingleAsync());
            Assert.Equal(2001,await check.GpuBatches.Select(batch=>batch.ItemCount).SingleAsync());
            Assert.Single(await check.GpuExecutorDispatches.ToArrayAsync());
            Assert.Single(await check.GpuExecutorEvidence.ToArrayAsync());
            Assert.Single(await check.GpuSchedulerOperationReceipts.ToArrayAsync());
        }
        var last=Assert.IsType<FluxKnowledge.Application.Ports.SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.True((await store.PurgeAsync(last,null,default)).ContinueImmediately);
        await using var final=CreateContext();
        Assert.Empty(await final.GpuBatches.ToArrayAsync());
        Assert.Empty(await final.GpuExecutorEvidence.ToArrayAsync());
        Assert.Empty(await final.GpuExecutorDispatches.ToArrayAsync());
        Assert.Empty(await final.GpuExecutorResultReceipts.ToArrayAsync());
        Assert.True(await final.PipelineRecords.AnyAsync(record=>record.Id==source.RecordId));
    }

    [NativeSqlServerFact]
    public async Task Execution_page_cancellation_rolls_back_deleted_requests_and_preserves_the_retry()
    {
        var (source,operation,_)=await SeedCompletedBatchGraphAsync(2001);
        var ordinary=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await ReachExecutionGraphAsync(ordinary,source,operation);
        using var cancel=new CancellationTokenSource();
        var factory=new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString)
            .AddInterceptors(new CancelExecutionPage(cancel)).Options);
        var work=Assert.IsType<FluxKnowledge.Application.Ports.SourceDeletionWorkItem>(await ordinary.ClaimNextAsync(default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await new SqlSourceDeletionStore(factory,TimeProvider.System).PurgeAsync(work,null,cancel.Token));
        await using(var check=CreateContext())
        {
            Assert.Equal(2001,await check.GpuMiniTasks.CountAsync());
            Assert.Equal(2001,await check.DocumentOcrRequests.CountAsync());
            Assert.Equal(2001,await check.GpuBatches.CountAsync());
        }
        Assert.True((await ordinary.PurgeAsync(work,null,default)).ContinueImmediately);
        await using var retried=CreateContext();Assert.Equal(1001,await retried.GpuMiniTasks.CountAsync());
    }

    [NativeSqlServerFact]
    public async Task Execution_page_refuses_a_selected_task_result_bound_to_a_foreign_batch()
    {
        var (source,operation,_)=await SeedCompletedBatchGraphAsync(2001);
        var ordinary=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await ReachExecutionGraphAsync(ordinary,source,operation);
        var control=await SeedRootAsync("foreign-result-batch",SourceRootState.Enabled,DateTimeOffset.UtcNow);
        var foreign=Guid.NewGuid();var now=DateTimeOffset.UtcNow;Guid selected;
        await using(var seed=CreateContext())
        {
            selected=(await seed.GpuMiniTasks.Select(task=>task.Id).ToArrayAsync()).Order().First();
            AddLocalOcrTask(seed,control,foreign,GpuMiniTaskExecutionState.Completed,now);
            seed.GpuBatches.Add(new(){Id=foreign,CapacitySlotKey="paddleocr-vl-local-gpu-0",PriorityLane=0,ModelRuntimeKey=PaddleOcrVlmRuntimeContract.ModelRuntimeKey,
                SettingsFingerprint=PaddleOcrVlmRuntimeContract.SettingsFingerprint,ItemCount=1,EstimatedBytes=1,AdmissionGeneration=1,OwnerKey="foreign-result-owner",State=2,CreatedAtUtc=now,UpdatedAtUtc=now});
            seed.GpuMiniTasks.Local.Single().BatchId=foreign;
            seed.GpuExecutorDispatches.Add(new(){DispatchId=foreign,BatchId=foreign,CapacitySlotKey="paddleocr-vl-local-gpu-0",OwnerKey="foreign-result-owner",
                ExecutorKey="paddleocr-vl-local-gpu-0",AdmissionGeneration=1,State=(int)GpuExecutorDispatchState.Terminal,UpdatedAtUtc=now});
            seed.GpuExecutorResultReceipts.Add(new(){OperationId=Guid.NewGuid(),DispatchId=foreign,BatchId=foreign,MiniTaskId=selected,
                ExecutorKey="paddleocr-vl-local-gpu-0",AdmissionGeneration=1,Disposition=1,EvidenceClass=1,RequestFingerprint=new string('a',64),CreatedAtUtc=now});
            await seed.SaveChangesAsync();
        }
        var work=Assert.IsType<FluxKnowledge.Application.Ports.SourceDeletionWorkItem>(await ordinary.ClaimNextAsync(default));
        var error=await Assert.ThrowsAsync<SourceDeletionFailureException>(async()=>await ordinary.PurgeAsync(work,null,default));
        Assert.Equal("source-delete-local-ocr-foreign-result",error.ReasonCode);
        await using var check=CreateContext();
        Assert.Equal(2002,await check.GpuMiniTasks.CountAsync());
        Assert.Equal(2002,await check.DocumentOcrRequests.CountAsync());
        Assert.Single(await check.GpuExecutorResultReceipts.ToArrayAsync());
        Assert.True(await check.GpuBatches.AnyAsync(batch=>batch.Id==foreign));
        Assert.True(await check.PipelineRecords.AnyAsync(record=>record.Id==source.RecordId));
        Assert.True(await check.PipelineRecords.AnyAsync(record=>record.Id==control.RecordId));
    }

    private async Task SeedExecutionPageReceiptsAsync()
    {
        await using var seed=CreateContext();
        await seed.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT GpuExecutorDispatches(DispatchId,BatchId,CapacitySlotKey,OwnerKey,ExecutorKey,AdmissionGeneration,State,UpdatedAtUtc)
            SELECT Id,Id,CapacitySlotKey,OwnerKey,CapacitySlotKey,1,{(int)GpuExecutorDispatchState.Terminal},SYSUTCDATETIME() FROM GpuBatches;
            INSERT GpuExecutorResultReceipts(OperationId,DispatchId,BatchId,MiniTaskId,ExecutorKey,AdmissionGeneration,Disposition,EvidenceClass,RequestFingerprint,CreatedAtUtc)
            SELECT Id,BatchId,BatchId,Id,'paddleocr-vl-local-gpu-0',1,1,1,{new string('a',64)},SYSUTCDATETIME() FROM GpuMiniTasks;
            INSERT GpuExecutorEvidence(OperationId,DispatchId,BatchId,CapacitySlotKey,ExecutorKey,AdmissionGeneration,EvidenceClass,VerifierKey,ObservedAtUtc,RequestFingerprint,CreatedAtUtc)
            SELECT Id,Id,Id,CapacitySlotKey,CapacitySlotKey,1,1,'synthetic-cleanup',SYSUTCDATETIME(),{new string('a',64)},SYSUTCDATETIME() FROM GpuBatches;
            INSERT GpuSchedulerOperationReceipts(OperationId,OperationKind,RequestFingerprint,BatchId,CapacitySlotKey,OwnerKey,AdmissionGeneration,Accepted,Committed,WakeReasons,CreatedAtUtc)
            SELECT Id,'synthetic-completed-batch',{new string('a',64)},Id,CapacitySlotKey,OwnerKey,1,1,1,0,SYSUTCDATETIME() FROM GpuBatches;
            """);
    }

    private sealed class CancelExecutionPage(CancellationTokenSource cancel):DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,CommandExecutedEventData data,int result,CancellationToken ct=default)
        {if(command.CommandText.StartsWith("DELETE",StringComparison.Ordinal)&&command.CommandText.Contains("[DocumentOcrRequests]",StringComparison.Ordinal))cancel.Cancel();return ValueTask.FromResult(result);}
    }
}
