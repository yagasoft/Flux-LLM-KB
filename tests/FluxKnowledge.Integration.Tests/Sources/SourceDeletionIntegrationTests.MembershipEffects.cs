using System.Globalization;
using System.Data.Common;
using System.Text.RegularExpressions;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData("valid")]
    [InlineData("uncommitted")]
    [InlineData("fingerprint")]
    [InlineData("admission")]
    [InlineData("extra-task")]
    [InlineData("partial-claim")]
    [InlineData("active-task")]
    [InlineData("slot-rebound")]
    public async Task A_legacy_scheduler_release_does_not_authorise_membership_or_payload_deletion(string change)
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("membership-legacy-callback",SourceRootState.Deleting,now);
        var operation=await AddDeletionAsync(source,now);
        await SeedDistinctMembershipVectorsAsync(source,3,now);
        var factory=SqlTestData.CreateFactory(_fixture);
        var store=new SqlSourceDeletionStore(factory,TimeProvider.System);
        await AdvanceSqlToFilesAsync(store,operation,stopAtCutover:true);
        var task=Guid.NewGuid();var batch=Guid.NewGuid();var receipt=Guid.NewGuid();
        const string slot="legacy-ocr-slot";
        const string owner="legacy-ocr-owner";
        await using(var seed=CreateContext())
        {
            seed.GpuCapacitySlots.Add(new(){SlotKey=slot,State=(int)GpuCapacitySlotState.Available,UpdatedAtUtc=now});
            seed.GpuBatches.Add(new(){Id=batch,CapacitySlotKey=slot,PriorityLane=0,ModelRuntimeKey=PaddleOcrVlmRuntimeContract.ModelRuntimeKey,
                SettingsFingerprint=PaddleOcrVlmRuntimeContract.SettingsFingerprint,ItemCount=1,EstimatedBytes=1,
                AdmissionGeneration=1,OwnerKey=owner,State=(int)GpuBatchState.Active,CreatedAtUtc=now,UpdatedAtUtc=now});
            AddLocalOcrTask(seed,source,task,GpuMiniTaskExecutionState.Active,now);
            var miniTask=seed.GpuMiniTasks.Local.Single();miniTask.BatchId=batch;miniTask.AdmissionGeneration=1;
            seed.GpuExecutorDispatches.Add(new(){DispatchId=batch,BatchId=batch,CapacitySlotKey=slot,OwnerKey=owner,
                ExecutorKey=slot,AdmissionGeneration=1,State=(int)GpuExecutorDispatchState.Acknowledged,UpdatedAtUtc=now});
            await seed.SaveChangesAsync();
            await seed.GpuCapacitySlots.Where(value=>value.SlotKey==slot).ExecuteUpdateAsync(set=>set
                .SetProperty(value=>value.State,(int)GpuCapacitySlotState.Reserved).SetProperty(value=>value.ActiveBatchId,batch).SetProperty(value=>value.OwnerKey,owner));
        }
        var handle=new GpuExecutorBatchHandle(batch,slot,slot,1,batch);
        var callback=new GpuBatchCallback(handle,GpuBatchCallbackKind.CapacityReleased,
            [new(task,GpuMiniTaskBoundaryDisposition.OutcomeUncertain)],CapacityReleased:true);
        var released=await new SqlGpuSchedulerStore(factory).ApplyBatchCallbackAsync(receipt,callback,default);
        Assert.True(released.Accepted);Assert.True(released.Committed);
        await using(var mutation=CreateContext())
        {
            if(change=="uncommitted") await mutation.GpuSchedulerOperationReceipts.Where(value=>value.OperationId==receipt)
                .ExecuteUpdateAsync(set=>set.SetProperty(value=>value.Committed,false));
            if(change=="fingerprint") await mutation.GpuSchedulerOperationReceipts.Where(value=>value.OperationId==receipt)
                .ExecuteUpdateAsync(set=>set.SetProperty(value=>value.RequestFingerprint,new string('a',64)));
            if(change=="admission") await mutation.GpuMiniTasks.Where(value=>value.Id==task)
                .ExecuteUpdateAsync(set=>set.SetProperty(value=>value.AdmissionGeneration,2));
            if(change=="active-task") await mutation.GpuMiniTasks.Where(value=>value.Id==task)
                .ExecuteUpdateAsync(set=>set.SetProperty(value=>value.ExecutionState,(int)GpuMiniTaskExecutionState.Active));
            if(change=="partial-claim") await mutation.DocumentOcrRequests.Where(value=>value.MiniTaskId==task)
                .ExecuteUpdateAsync(set=>set.SetProperty(value=>value.ExecutionDispatchId,batch));
            if(change=="slot-rebound") await mutation.GpuCapacitySlots.Where(value=>value.SlotKey==slot)
                .ExecuteUpdateAsync(set=>set.SetProperty(value=>value.State,(int)GpuCapacitySlotState.Uncertain)
                    .SetProperty(value=>value.ActiveBatchId,batch).SetProperty(value=>value.OwnerKey,owner));
            if(change=="extra-task")
            {
                var extra=Guid.NewGuid();AddLocalOcrTask(mutation,source,extra,GpuMiniTaskExecutionState.OutcomeUncertain,now);
                var miniTask=mutation.GpuMiniTasks.Local.Single();miniTask.BatchId=batch;miniTask.AdmissionGeneration=1;
                await mutation.SaveChangesAsync();
            }
        }
        await using(var before=CreateContext())
        {
            var taskVersion=await before.GpuMiniTasks.Where(value=>value.Id==task).Select(value=>value.RowVersion).SingleAsync();
            var requestVersion=await before.DocumentOcrRequests.Where(value=>value.MiniTaskId==task).Select(value=>value.RowVersion).SingleAsync();
            var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            var result=await store.PurgeAsync(work,null,default);
            Assert.False(result.ContinueImmediately);
            Assert.Equal("source-delete-native-cleanup-pending",result.ReasonCode);
            await using var check=CreateContext();
            Assert.Equal(3,await check.IndexGenerationVectors.CountAsync());
            Assert.Equal(3,await check.Vectors.CountAsync());
            Assert.Equal(taskVersion,await check.GpuMiniTasks.Where(value=>value.Id==task).Select(value=>value.RowVersion).SingleAsync());
            Assert.Equal(requestVersion,await check.DocumentOcrRequests.Where(value=>value.MiniTaskId==task).Select(value=>value.RowVersion).SingleAsync());
            Assert.True(await check.GpuSchedulerOperationReceipts.AnyAsync(value=>value.OperationId==receipt));
        }
        await using(var fixture=CreateContext())
        {
            await fixture.IndexGenerationVectors.ExecuteDeleteAsync();
            await fixture.SourceDeletionOperations.Where(value=>value.Id==operation)
                .ExecuteUpdateAsync(set=>set.SetProperty(value=>value.Phase,"purge-records"));
        }
        var purge=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var refused=await store.PurgeAsync(purge,null,default);
        Assert.False(refused.ContinueImmediately);
        Assert.Equal("source-delete-native-cleanup-pending",refused.ReasonCode);
        await using var anchors=CreateContext();
        Assert.Equal(3,await anchors.Vectors.CountAsync());
        Assert.Equal(3,await anchors.TextChunks.CountAsync());
        Assert.True(await anchors.DocumentOcrRequests.AnyAsync(value=>value.MiniTaskId==task));
    }

    [NativeSqlServerTheory]
    [InlineData("unknown-task")]
    [InlineData("reversed-ocr")]
    [InlineData("foreign-lineage")]
    public async Task Membership_pruning_preserves_late_ownership_conflicts_for_the_graph_gate(string conflict)
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("membership-effects",SourceRootState.Deleting,now);
        var control=await SeedRootAsync("membership-effects-control",SourceRootState.Enabled,now);
        var operation=await AddDeletionAsync(source,now);
        await SeedDistinctMembershipVectorsAsync(source,3,now);
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await AdvanceSqlToFilesAsync(store,operation,stopAtCutover:true);
        var task=Guid.NewGuid();
        await using(var mutation=CreateContext())
        {
            if(conflict=="foreign-lineage")
                await mutation.PipelineRecords.Where(value=>value.Id==control.RecordId)
                    .ExecuteUpdateAsync(set=>set.SetProperty(value=>value.ParentRevisionRecordId,source.RecordId));
            else
            {
                AddLocalOcrTask(mutation,conflict=="reversed-ocr"?control:source,task,GpuMiniTaskExecutionState.Completed,now);
                var request=mutation.DocumentOcrRequests.Local.Single();
                if(conflict=="reversed-ocr")
                {
                    request.PipelineRecordId=source.RecordId;
                    request.RetainedSourceRevisionId=source.RevisionId;
                }
                else mutation.DocumentOcrRequests.Remove(request);
                await mutation.SaveChangesAsync();
            }
        }
        var page=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.True((await store.PurgeAsync(page,null,default)).ContinueImmediately);
        await using(var check=CreateContext())
        {
            Assert.Empty(await check.IndexGenerationVectors.ToArrayAsync());
            Assert.Equal(3,await check.Vectors.CountAsync());
            Assert.Equal(3,await check.TextChunks.CountAsync());
            Assert.Equal(2,await check.Jobs.CountAsync());
            Assert.True(await check.PipelineRecords.AnyAsync(value=>value.Id==source.RecordId));
            if(conflict!="foreign-lineage") Assert.True(await check.GpuMiniTasks.AnyAsync(value=>value.Id==task));
        }
        // This transition must not fall through to payload deletion in the same unit.
        page=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("purge-records",(await store.PurgeAsync(page,null,default)).Phase);
        page=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal(conflict=="foreign-lineage"?"source-delete-cross-root-dependency":"source-delete-external-execution-owned",
            (await store.PurgeAsync(page,null,default)).ReasonCode);
        await using var final=CreateContext();
        Assert.Equal(3,await final.Vectors.CountAsync());
        Assert.Equal(3,await final.TextChunks.CountAsync());
        Assert.Equal(2,await final.Jobs.CountAsync());
        Assert.Equal(2,await final.SourceRevisions.CountAsync());
        if(conflict=="reversed-ocr") Assert.True(await final.DocumentOcrRequests.AnyAsync(value=>value.MiniTaskId==task));
    }

    [NativeSqlServerTheory]
    [InlineData("active-parent")]
    [InlineData("active-reversed-request")]
    [InlineData("active-unknown-parent")]
    [InlineData("uncertain-unconfirmed")]
    [InlineData("uncertain-no-claim")]
    [InlineData("uncertain-no-request")]
    [InlineData("dispatch-only")]
    [InlineData("timestamp-only")]
    [InlineData("cleaned-uncertain")]
    public async Task Membership_pruning_defers_unresolved_execution_without_cancelling_it(string evidence)
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("membership-native",SourceRootState.Deleting,now);
        var control=await SeedRootAsync("membership-native-control",SourceRootState.Enabled,now);
        var operation=await AddDeletionAsync(source,now);
        await SeedDistinctMembershipVectorsAsync(source,3,now);
        var registry=new PaddleOcrVlmExecutionRegistry();
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System,registry);
        await AdvanceSqlToFilesAsync(store,operation,stopAtCutover:true);
        var task=Guid.NewGuid();
        await using(var mutation=CreateContext())
        {
            AddLocalOcrTask(mutation,evidence=="active-reversed-request"?control:source,task,
                evidence.StartsWith("active",StringComparison.Ordinal)?GpuMiniTaskExecutionState.Active:GpuMiniTaskExecutionState.OutcomeUncertain,now);
            var request=mutation.DocumentOcrRequests.Local.Single();
            if(evidence is "active-unknown-parent" or "uncertain-no-request") mutation.DocumentOcrRequests.Remove(request);
            if(evidence=="active-reversed-request")
            {
                request.PipelineRecordId=source.RecordId;
                request.RetainedSourceRevisionId=source.RevisionId;
            }
            if(evidence=="dispatch-only") request.ExecutionDispatchId=Guid.NewGuid();
            else if(!evidence.StartsWith("active",StringComparison.Ordinal) && evidence is not ("uncertain-no-claim" or "uncertain-no-request"))
            {
                request.ExecutionInstanceId=Guid.NewGuid();
                request.ExecutionDispatchId=Guid.NewGuid();
                request.ExecutionBatchId=Guid.NewGuid();
                request.ExecutionAdmissionGeneration=1;
                request.SupervisorInstanceId=Guid.NewGuid();
                request.SupervisorProcessId=123;
                request.SupervisorProcessStartedAtUtc=now.AddMinutes(-1);
                request.ExecutionMachineFingerprint=new string('a',64);
                request.ExecutionClaimedAtUtc=now;
                request.ExecutionDeadlineAtUtc=now.AddMinutes(1);
                if(evidence is "timestamp-only" or "cleaned-uncertain")
                {
                    request.NativeCleanupConfirmedAtUtc=now;
                    request.CleanupProofKind=(int)DocumentOcrCleanupKind.VerifiedUnstarted;
                    request.ExecutionPhase=evidence=="cleaned-uncertain"?(int)DocumentOcrExecutionPhase.Settled:(int)DocumentOcrExecutionPhase.Claimed;
                }
            }
            await mutation.SaveChangesAsync();
        }
        using var execution=registry.Begin(task);
        var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var result=await store.PurgeAsync(work,null,default);
        Assert.False(execution.IsCancellationRequested);
        Assert.Equal(evidence=="cleaned-uncertain",result.ContinueImmediately);
        Assert.Equal(evidence=="cleaned-uncertain"?null:"source-delete-native-cleanup-pending",result.ReasonCode);
        await using var check=CreateContext();
        Assert.Equal(evidence=="cleaned-uncertain"?0:3,await check.IndexGenerationVectors.CountAsync());
        Assert.Equal(3,await check.Vectors.CountAsync());
        Assert.True(await check.GpuMiniTasks.AnyAsync(value=>value.Id==task));
        Assert.Equal(evidence is not ("active-unknown-parent" or "uncertain-no-request"),await check.DocumentOcrRequests.AnyAsync(value=>value.MiniTaskId==task));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Membership_pruning_defers_unconfirmed_embedding_requests_in_either_ownership_direction(bool reversed)
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("membership-embedding",SourceRootState.Deleting,now);
        var control=await SeedRootAsync("membership-embedding-control",SourceRootState.Enabled,now);
        var operation=await AddDeletionAsync(source,now);
        var (_,generation)=await SeedDistinctMembershipVectorsAsync(source,3,now);
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await AdvanceSqlToFilesAsync(store,operation,stopAtCutover:true);
        var task=Guid.NewGuid();
        await using(var mutation=CreateContext())
        {
            AddLocalOcrTask(mutation,reversed?control:source,task,GpuMiniTaskExecutionState.Completed,now);
            mutation.DocumentOcrRequests.Remove(mutation.DocumentOcrRequests.Local.Single());
            mutation.EmbeddingGpuRequests.Add(new() { MiniTaskId=task,ParentJobId=(reversed?control:source).JobId,
                PipelineRecordId=(reversed?source:control).RecordId,SourceRevision=1,GenerationId=generation,
                CorpusEpoch=(await mutation.IndexState.SingleAsync()).CorpusEpoch,ModelFingerprint="source-deletion-test:1",
                Dimensions=1,InputsJson="[]",InputDigest=new string('a',64),State=0,ExecutorInstanceId=Guid.NewGuid(),
                ClaimOperationId=Guid.NewGuid(),OwnerProcessId=123,OwnerStartedAtUtc=now,OwnerMachineFingerprint=new string('a',64),
                DispatchId=Guid.NewGuid(),CreatedAtUtc=now,UpdatedAtUtc=now });
            await mutation.SaveChangesAsync();
        }
        var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var result=await store.PurgeAsync(work,null,default);
        Assert.False(result.ContinueImmediately);
        Assert.Equal("source-delete-native-cleanup-pending",result.ReasonCode);
        await using var check=CreateContext();
        Assert.Equal(3,await check.IndexGenerationVectors.CountAsync());
        Assert.Equal(3,await check.Vectors.CountAsync());
        Assert.True(await check.EmbeddingGpuRequests.AnyAsync(value=>value.MiniTaskId==task));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Membership_pruning_preserves_the_whole_page_when_a_late_foreign_segment_uses_its_last_vector(bool current)
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("membership-segment",SourceRootState.Deleting,now);
        var control=await SeedRootAsync("membership-segment-control",SourceRootState.Enabled,now);
        var operation=await AddDeletionAsync(source,now);
        var (ids,_)=await SeedDistinctMembershipVectorsAsync(source,3,now);
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await AdvanceSqlToFilesAsync(store,operation,stopAtCutover:true);
        await using(var mutation=CreateContext())
        {
            var segment=new string('c',64);
            mutation.IndexSegments.Add(new() {Id=segment,ModelFingerprint="source-deletion-test:1",Dimensions=1,
                NativeFormat="fixture",DirectoryPath="fixture",MembershipChecksum=new string('a',64),FileChecksum=new string('b',64),
                FileLength=1,VectorCount=1,State=1,CreatedAtUtc=now});
            mutation.IndexSegmentVectors.Add(new(){SegmentId=segment,VectorId=ids[^1]});
            if(current) mutation.CurrentIndexMembers.Add(new(){CorpusEpoch=(await mutation.IndexState.SingleAsync()).CorpusEpoch,
                VectorId=ids[^1],SegmentId=segment,SourceRootId=control.RootId,OwnerSourceRevisionId=control.RevisionId,
                SourceIdentityId=await mutation.PipelineRecords.Where(record=>record.Id==control.RecordId).Select(record=>record.SourceIdentityId).SingleAsync()});
            await mutation.SaveChangesAsync();
        }
        var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("source-delete-segment-retirement-pending",(await store.PurgeAsync(work,null,default)).ReasonCode);
        await using var check=CreateContext();
        Assert.Equal(3,await check.IndexGenerationVectors.CountAsync());
        Assert.Equal(3,await check.Vectors.CountAsync());
        Assert.Equal(0,await check.SourceDeletionOperations.Select(value=>value.MembershipRowsDeleted).SingleAsync());
        Assert.Single(await check.IndexSegmentVectors.ToArrayAsync());
        Assert.Equal(current?1:0,await check.CurrentIndexMembers.CountAsync());
    }

    [SqlServerScaleTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category","Scale")]
    public async Task Membership_unit_cost_stays_bounded_with_217685_historical_gpu_tasks(bool embeddingHistory)
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("membership-task-history",SourceRootState.Deleting,now);
        var operation=await AddDeletionAsync(source,now);
        var (_,generation)=await SeedDistinctMembershipVectorsAsync(source,2000,now);
        var ordinary=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await AdvanceSqlToFilesAsync(ordinary,operation,stopAtCutover:true);
        var prototype=Guid.NewGuid();
        await using(var seed=CreateContext())
        {
            AddLocalOcrTask(seed,source,prototype,GpuMiniTaskExecutionState.Completed,now);
            await seed.SaveChangesAsync();
            seed.Database.SetCommandTimeout(120);
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                CREATE TABLE #Parents(N int NOT NULL PRIMARY KEY,RecordId uniqueidentifier NOT NULL,JobId uniqueidentifier NOT NULL,IdentityId uniqueidentifier NOT NULL,RevisionId uniqueidentifier NOT NULL);
                INSERT #Parents SELECT TOP(20000) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)),NEWID(),NEWID(),NEWID(),NEWID()
                    FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                INSERT SourceRevisions(Id,SourceRootId,StableSourceIdentity,Revision,ContentSha256,CanonicalPath,Classification,Extension,ByteLength,DiscoveredAtUtc,OriginKind)
                    SELECT RevisionId,{source.RootId},CONVERT(nvarchar(36),RevisionId),1,{new string('a',64)},
                        CONCAT('C:\membership-task-history\',RevisionId),'AcceptedUtf8Text','.txt',4,{now},0 FROM #Parents;
                INSERT SourceIdentities(Id,SourceKind,StableKey,CreatedAtUtc)
                    SELECT IdentityId,'local file',CONVERT(nvarchar(36),IdentityId),{now} FROM #Parents;
                INSERT PipelineRecords(Id,SourceIdentityId,SourceRevisionId,Revision,ContentHash,RootLineageRecordId,CurrentStage,RegisteredAtUtc,CompletionCriteriaMet,IsDeleted)
                    SELECT RecordId,IdentityId,RevisionId,1,{new string('a',64)},RecordId,0,{now},1,1 FROM #Parents;
                INSERT Jobs(Id,PipelineRecordId,SourceRevision,Stage,Operation,PublicState,DueAtUtc,AttemptCount,LeaseGeneration)
                    SELECT JobId,RecordId,1,0,'extract-utf8',0,{now},0,0 FROM #Parents;
                CREATE TABLE #Tasks(Id uniqueidentifier NOT NULL PRIMARY KEY,ParentN int NOT NULL);
                INSERT #Tasks SELECT TOP(217684) NEWID(),1+(ROW_NUMBER() OVER(ORDER BY (SELECT NULL))%20000)
                    FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                INSERT GpuMiniTasks(Id,ParentJobId,SourceRevision,PriorityLane,ModelRuntimeKey,SettingsFingerprint,
                    EstimatedBytes,AdmissionGeneration,IdempotencyKey,State,ReservationAttemptCount,CreatedAtUtc,InteractiveCancellationRequested)
                SELECT t.Id,parent.JobId,p.SourceRevision,p.PriorityLane,p.ModelRuntimeKey,p.SettingsFingerprint,
                    p.EstimatedBytes,0,CONVERT(nvarchar(36),t.Id),p.State,0,p.CreatedAtUtc,0
                FROM #Tasks t JOIN #Parents parent ON parent.N=t.ParentN CROSS JOIN GpuMiniTasks p WHERE p.Id={prototype};
                INSERT DocumentOcrRequests(MiniTaskId,ParentJobId,PipelineRecordId,SourceRevision,RetainedSourceRevisionId,
                    ContentSha256,RequestedPageIndexesJson,ModelRuntimeKey,SettingsFingerprint,State,CreatedAtUtc,UpdatedAtUtc,ExecutionPhase)
                SELECT t.Id,parent.JobId,parent.RecordId,p.SourceRevision,parent.RevisionId,p.ContentSha256,
                    p.RequestedPageIndexesJson,p.ModelRuntimeKey,p.SettingsFingerprint,p.State,p.CreatedAtUtc,p.UpdatedAtUtc,0
                FROM #Tasks t JOIN #Parents parent ON parent.N=t.ParentN CROSS JOIN DocumentOcrRequests p WHERE p.MiniTaskId={prototype};
                DROP TABLE #Tasks;
                DROP TABLE #Parents;
                """);
            Assert.Equal(217685,await seed.GpuMiniTasks.CountAsync());
            if(embeddingHistory)
            {
                await seed.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT EmbeddingGpuRequests(MiniTaskId,ParentJobId,PipelineRecordId,SourceRevision,GenerationId,CorpusEpoch,
                        ModelFingerprint,Dimensions,InputsJson,InputDigest,State,ExecutorInstanceId,ClaimOperationId,
                        OwnerProcessId,OwnerStartedAtUtc,OwnerMachineFingerprint,DispatchId,NativeCleanupConfirmed,CleanupConfirmedAtUtc,CreatedAtUtc,UpdatedAtUtc)
                    SELECT request.MiniTaskId,request.ParentJobId,request.PipelineRecordId,request.SourceRevision,{generation},state.CorpusEpoch,
                        'source-deletion-test:1',1,'[]',{new string('a',64)},2,NEWID(),NEWID(),123,{now},{new string('a',64)},NEWID(),1,{now},{now},{now}
                    FROM DocumentOcrRequests request CROSS JOIN IndexState state WHERE state.Id=1;
                    DELETE FROM DocumentOcrRequests;
                    UPDATE GpuMiniTasks SET PriorityLane=1,ModelRuntimeKey='embedding-history',SettingsFingerprint={new string('a',64)};
                    UPDATE Jobs SET Stage=4,Operation='embed';
                    """);
            }
        }
        await using var connection=new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var messages=new List<string>();
        connection.InfoMessage+=(_,item)=>messages.Add(item.Message);
        await using(var command=connection.CreateCommand())
        {
            command.CommandText="SET STATISTICS IO ON;";
            await command.ExecuteNonQueryAsync();
        }
        var counter=new MembershipUnitCommands();
        var factory=new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection,sql=>sql.CommandTimeout(15)).AddInterceptors(counter).Options);
        var store=new SqlSourceDeletionStore(factory,TimeProvider.System);
        if(!embeddingHistory)
        {
            // The full ownership classification must also remain bounded with
            // the same populated history, without splitting it into query chunks.
            counter.Readers=0;
            await using var baseline=factory.CreateDbContext();
            await using var transaction=await baseline.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await SqlPublishedPassageSelection.AcquireFenceAsync(baseline,default);
            Assert.True((await SqlSourceDeletionOwnership.ReadAsync(baseline,source.RootId,null,default)).IsOwned);
            output.WriteLine($"membership_task_history set_classification_read_commands={counter.Readers}");
            Assert.InRange(counter.Readers,1,10);
            await transaction.CommitAsync();
        }
        for(var unit=0;unit<2;unit++)
        {
            messages.Clear();
            counter.Readers=0;
            var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            Assert.True((await store.PurgeAsync(work,null,default)).ContinueImmediately);
            var reads=messages.SelectMany(message=>Regex.Matches(message,@"(?<!lob )logical reads (\d+),"))
                .Sum(match=>long.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture));
            output.WriteLine($"membership_task_history embedding={embeddingHistory} tasks=217685 jobs=20001 unit={unit} total_logical_reads={reads} read_commands={counter.Readers}");
            Assert.InRange(reads,1,50000);
            Assert.InRange(counter.Readers,1,40);
        }
        await using var check=CreateContext();
        Assert.Empty(await check.IndexGenerationVectors.ToArrayAsync());
        Assert.Equal(2000,await check.Vectors.CountAsync());
        Assert.Equal(217685,await check.GpuMiniTasks.CountAsync());
        Assert.Equal(embeddingHistory?0:217685,await check.DocumentOcrRequests.CountAsync());
        Assert.Equal(embeddingHistory?217685:0,await check.EmbeddingGpuRequests.CountAsync());
    }

    private sealed class MembershipUnitCommands : DbCommandInterceptor
    {
        internal int Readers {get;set;}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            Readers++;
            return ValueTask.FromResult(result);
        }
    }
}
