using System.Data.Common;
using System.Diagnostics;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
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
    [Trait("Category","Scale")]
    public async Task Complete_graph_purge_clears_217685_tasks_and_receipts_across_20000_parents()
    {
        var (source,operation,_)=await SeedCompletedBatchGraphAsync(1);
        var control=await SeedRootAsync("graph-scale-control",SourceRootState.Enabled,DateTimeOffset.UtcNow);
        byte[] controlVersion;
        await using(var setup=CreateContext())
        {
            controlVersion=await setup.PipelineRecords.Where(record=>record.Id==control.RecordId).Select(record=>record.RowVersion).SingleAsync();
            await SeedProductionSizedHistoryAsync(setup,source);
        }
        var timing=new GraphCommandTimings();
        var factory=new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString,sql=>sql.CommandTimeout(30)).AddInterceptors(timing).Options);
        var store=new SqlSourceDeletionStore(factory,TimeProvider.System);
        var maxUnitMs=0d;var total=Stopwatch.StartNew();var finished=false;
        for(var pass=0;pass<512;pass++)
        {
            var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            Assert.Equal(operation,work.OperationId);
            if(work.Phase=="cleanup-files")
            {
                var pending=await store.ReadPendingCleanupAsync(work,default);
                if(pending.Count>0)
                {await store.RecordCleanupResultAsync(work,pending[0].CleanupItemId,new(true,"source-delete-file-missing"),default);continue;}
            }
            var statementsBefore=timing.Count;var sqlBefore=timing.TotalMs;
            var watch=Stopwatch.StartNew();
            var result=await store.PurgeAsync(work,null,default);
            maxUnitMs=Math.Max(maxUnitMs,watch.Elapsed.TotalMilliseconds);
            output.WriteLine($"graph_scale_unit pass={pass} phase={work.Phase} elapsed_ms={watch.Elapsed.TotalMilliseconds:F1} statements={timing.Count-statementsBefore} command_ms={timing.TotalMs-sqlBefore:F1}");
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(),$"graph-scale-{_fixture.DatabaseName}.json"),
                System.Text.Json.JsonSerializer.Serialize(new{Pass=pass,work.Phase,ElapsedMs=total.Elapsed.TotalMilliseconds,MaxUnitMs=maxUnitMs,MaxStatementMs=timing.MaxMs}));
            if(result.Completed){finished=true;break;}
            Assert.True(result.ContinueImmediately,result.ReasonCode);
        }
        output.WriteLine($"graph_scale tasks=217685 parents=20000 total_ms={total.Elapsed.TotalMilliseconds:F1} max_purge_unit_ms={maxUnitMs:F1} max_statement_ms={timing.MaxMs:F1} slowest_statement={timing.Slowest}");
        foreach(var item in timing.Longest.OrderByDescending(item=>item.Milliseconds).Take(12))
            output.WriteLine($"graph_scale_statement ms={item.Milliseconds:F1} sql={item.Sql}");
        Assert.True(finished,"Full graph purge did not finish.");
        Assert.InRange(timing.MaxMs,0,15000);
        // A whole Purge call bounds its transaction/publication-fence lifetime.
        Assert.InRange(maxUnitMs,0,30000);
        await using var check=CreateContext();
        Assert.Empty(await check.GpuMiniTasks.ToArrayAsync());
        Assert.Empty(await check.DocumentOcrRequests.ToArrayAsync());
        Assert.Empty(await check.GpuBatches.ToArrayAsync());
        Assert.Empty(await check.GpuExecutorDispatches.ToArrayAsync());
        Assert.Empty(await check.GpuExecutorEvidence.ToArrayAsync());
        Assert.Empty(await check.GpuExecutorResultReceipts.ToArrayAsync());
        Assert.Empty(await check.GpuSchedulerOperationReceipts.ToArrayAsync());
        Assert.False(await check.SourceRootConfigurations.AnyAsync(root=>root.Id==source.RootId));
        Assert.Equal(controlVersion,await check.PipelineRecords.Where(record=>record.Id==control.RecordId).Select(record=>record.RowVersion).SingleAsync());
    }

    private static async Task SeedProductionSizedHistoryAsync(FluxKnowledgeDbContext setup,SeededRoot source)
    {
        setup.Database.SetCommandTimeout(120);
        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            CREATE TABLE #Parents(N int NOT NULL PRIMARY KEY,RecordId uniqueidentifier NOT NULL,JobId uniqueidentifier NOT NULL,IdentityId uniqueidentifier NOT NULL,RevisionId uniqueidentifier NOT NULL);
            INSERT #Parents SELECT TOP(19999) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)),NEWID(),NEWID(),NEWID(),NEWID()
                FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            INSERT SourceRevisions(Id,SourceRootId,StableSourceIdentity,Revision,ContentSha256,CanonicalPath,Classification,Extension,ByteLength,DiscoveredAtUtc,OriginKind)
                SELECT RevisionId,{source.RootId},CONVERT(nvarchar(36),RevisionId),1,{new string('a',64)},
                    CONCAT('C:\graph-scale\',RevisionId),'AcceptedUtf8Text','.txt',4,SYSUTCDATETIME(),0 FROM #Parents;
            INSERT SourceIdentities(Id,SourceKind,StableKey,CreatedAtUtc)
                SELECT IdentityId,'local file',CONVERT(nvarchar(36),IdentityId),SYSUTCDATETIME() FROM #Parents;
            INSERT PipelineRecords(Id,SourceIdentityId,SourceRevisionId,Revision,ContentHash,RootLineageRecordId,CurrentStage,RegisteredAtUtc,CompletionCriteriaMet,IsDeleted)
                SELECT RecordId,IdentityId,RevisionId,1,{new string('a',64)},RecordId,0,SYSUTCDATETIME(),1,1 FROM #Parents;
            INSERT Jobs(Id,PipelineRecordId,SourceRevision,Stage,Operation,PublicState,DueAtUtc,AttemptCount,LeaseGeneration)
                SELECT JobId,RecordId,1,0,'extract-utf8',4,SYSUTCDATETIME(),1,0 FROM #Parents;
            CREATE TABLE #History(Id uniqueidentifier NOT NULL PRIMARY KEY,ParentN int NOT NULL);
            INSERT #History SELECT TOP(217684) NEWID(),1+(ROW_NUMBER() OVER(ORDER BY (SELECT NULL))%19999)
                FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            INSERT GpuMiniTasks(Id,ParentJobId,SourceRevision,PriorityLane,ModelRuntimeKey,SettingsFingerprint,
                EstimatedBytes,AdmissionGeneration,IdempotencyKey,State,ReservationAttemptCount,CreatedAtUtc,InteractiveCancellationRequested)
            SELECT h.Id,parent.JobId,p.SourceRevision,p.PriorityLane,p.ModelRuntimeKey,p.SettingsFingerprint,
                p.EstimatedBytes,1,CONVERT(nvarchar(36),h.Id),p.State,0,p.CreatedAtUtc,0
            FROM #History h JOIN #Parents parent ON parent.N=h.ParentN CROSS JOIN GpuMiniTasks p
            WHERE p.ParentJobId={source.JobId};
            INSERT DocumentOcrRequests(MiniTaskId,ParentJobId,PipelineRecordId,SourceRevision,RetainedSourceRevisionId,
                ContentSha256,RequestedPageIndexesJson,ModelRuntimeKey,SettingsFingerprint,State,CreatedAtUtc,UpdatedAtUtc,ExecutionPhase)
            SELECT h.Id,parent.JobId,parent.RecordId,p.SourceRevision,parent.RevisionId,p.ContentSha256,
                p.RequestedPageIndexesJson,p.ModelRuntimeKey,p.SettingsFingerprint,p.State,p.CreatedAtUtc,p.UpdatedAtUtc,0
            FROM #History h JOIN #Parents parent ON parent.N=h.ParentN CROSS JOIN DocumentOcrRequests p
            WHERE p.ParentJobId={source.JobId};
            INSERT GpuBatches(Id,CapacitySlotKey,PriorityLane,ModelRuntimeKey,SettingsFingerprint,ItemCount,
                EstimatedBytes,AdmissionGeneration,OwnerKey,State,CreatedAtUtc,UpdatedAtUtc)
            SELECT h.Id,p.CapacitySlotKey,p.PriorityLane,p.ModelRuntimeKey,p.SettingsFingerprint,1,
                p.EstimatedBytes,1,p.OwnerKey,p.State,p.CreatedAtUtc,p.UpdatedAtUtc
            FROM #History h CROSS JOIN GpuBatches p;
            UPDATE task SET BatchId=task.Id FROM GpuMiniTasks task JOIN #History h ON h.Id=task.Id;
            DROP TABLE #History;
            DROP TABLE #Parents;
            INSERT GpuExecutorDispatches(DispatchId,BatchId,CapacitySlotKey,OwnerKey,ExecutorKey,AdmissionGeneration,State,UpdatedAtUtc)
            SELECT batch.Id,batch.Id,batch.CapacitySlotKey,batch.OwnerKey,batch.CapacitySlotKey,1,
                {(int)GpuExecutorDispatchState.Terminal},SYSUTCDATETIME() FROM GpuBatches batch;
            INSERT GpuExecutorResultReceipts(OperationId,DispatchId,BatchId,MiniTaskId,ExecutorKey,AdmissionGeneration,
                Disposition,EvidenceClass,RequestFingerprint,CreatedAtUtc)
            SELECT task.Id,task.BatchId,task.BatchId,task.Id,task.ModelRuntimeKey,1,1,1,{new string('a',64)},SYSUTCDATETIME()
            FROM GpuMiniTasks task WHERE task.BatchId IS NOT NULL;
            INSERT GpuExecutorEvidence(OperationId,DispatchId,BatchId,CapacitySlotKey,ExecutorKey,AdmissionGeneration,
                EvidenceClass,VerifierKey,ObservedAtUtc,RequestFingerprint,CreatedAtUtc)
            SELECT batch.Id,batch.Id,batch.Id,batch.CapacitySlotKey,batch.CapacitySlotKey,1,1,'synthetic-cleanup',
                SYSUTCDATETIME(),{new string('a',64)},SYSUTCDATETIME() FROM GpuBatches batch;
            INSERT GpuSchedulerOperationReceipts(OperationId,OperationKind,RequestFingerprint,BatchId,CapacitySlotKey,
                OwnerKey,AdmissionGeneration,Accepted,Committed,WakeReasons,CreatedAtUtc)
            SELECT batch.Id,'synthetic-completed-batch',{new string('a',64)},batch.Id,batch.CapacitySlotKey,
                batch.OwnerKey,1,1,1,0,SYSUTCDATETIME() FROM GpuBatches batch;
            """);
        Assert.Equal(217685,await setup.GpuMiniTasks.CountAsync());
        Assert.Equal(217685,await setup.GpuExecutorEvidence.CountAsync());
        Assert.Equal(217685,await setup.GpuExecutorResultReceipts.CountAsync());
        Assert.Equal(217685,await setup.GpuExecutorDispatches.CountAsync());
        Assert.Equal(20000,await setup.Jobs.CountAsync(job=>job.PipelineRecord.SourceRevision!.SourceRootId==source.RootId));
    }

    private sealed class GraphCommandTimings : DbCommandInterceptor
    {
        internal double MaxMs {get;private set;}
        internal double TotalMs {get;private set;}
        internal int Count {get;private set;}
        internal List<(double Milliseconds,string Sql)> Longest {get;}=[];
        internal string Slowest {get;private set;}="";
        internal string FailedSql {get;private set;}="";
        internal double WithdrawalUpdateMaxMs {get;private set;}
        internal string WithdrawalUpdateSql {get;private set;}="";
        internal double GenerationCaptureMaxMs {get;private set;}
        internal string GenerationCaptureSql {get;private set;}="";
        public override Task CommandFailedAsync(DbCommand command,CommandErrorEventData data,CancellationToken ct=default)
        {FailedSql=command.CommandText;return Task.CompletedTask;}
        private void Record(DbCommand command,CommandExecutedEventData data)
        {
            TotalMs+=data.Duration.TotalMilliseconds;Count++;
            if(command.CommandText.Contains("source-delete-generation-targets",StringComparison.Ordinal))
            {GenerationCaptureMaxMs=Math.Max(GenerationCaptureMaxMs,data.Duration.TotalMilliseconds);GenerationCaptureSql=command.CommandText;}
            if(command.CommandText.StartsWith("UPDATE",StringComparison.Ordinal)&&command.CommandText.Contains("[Vectors]",StringComparison.Ordinal))
            {WithdrawalUpdateMaxMs=Math.Max(WithdrawalUpdateMaxMs,data.Duration.TotalMilliseconds);WithdrawalUpdateSql=command.CommandText;}
            if(data.Duration.TotalMilliseconds>100)
                Longest.Add((data.Duration.TotalMilliseconds,command.CommandText.Length>400?command.CommandText[..400]:command.CommandText));
            if(data.Duration.TotalMilliseconds<=MaxMs)return;
            MaxMs=data.Duration.TotalMilliseconds;
            Slowest=command.CommandText.Length>400?command.CommandText[..400]:command.CommandText;
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData data,
            DbDataReader result,CancellationToken ct=default){Record(command,data);return ValueTask.FromResult(result);}
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,CommandExecutedEventData data,
            int result,CancellationToken ct=default){Record(command,data);return ValueTask.FromResult(result);}
        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command,CommandExecutedEventData data,
            object? result,CancellationToken ct=default){Record(command,data);return ValueTask.FromResult(result);}
    }
}
