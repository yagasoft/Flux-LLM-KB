using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData("valid")]
    [InlineData("lost-ack")]
    [InlineData("cli")]
    [InlineData("missing-proof")]
    [InlineData("stale-proof")]
    [InlineData("owner")]
    [InlineData("admission")]
    [InlineData("root-enabled")]
    [InlineData("foreign-record")]
    [InlineData("partial-claim")]
    [InlineData("active-capacity")]
    [InlineData("other-active-capacity")]
    [InlineData("task-version")]
    [InlineData("request-version")]
    public async Task Cleanup_only_maintenance_requires_exact_fresh_physical_proof_and_preserves_the_failed_work(string change)
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("ocr-cleanup-only",SourceRootState.Deleting,now);
        var control=await SeedRootAsync("ocr-cleanup-control",SourceRootState.Enabled,now);
        var operation=await AddDeletionAsync(source,now);
        await SeedDistinctMembershipVectorsAsync(source,3,now);
        var factory=SqlTestData.CreateFactory(_fixture);
        var deletion=new SqlSourceDeletionStore(factory,TimeProvider.System);
        await AdvanceSqlToFilesAsync(deletion,operation,stopAtCutover:true);
        var task=Guid.NewGuid();var batch=Guid.NewGuid();var otherBatch=Guid.NewGuid();var otherTask=Guid.NewGuid();
        var handle=new GpuExecutorBatchHandle(batch,PaddleOcrVlmRuntimeContract.CapacitySlotKey,PaddleOcrVlmRuntimeContract.ExecutorKey,1,batch);
        const string owner="legacy-ocr-cleanup-owner";
        await using(var seed=CreateContext())
        {
            seed.GpuCapacitySlots.Add(new(){SlotKey=handle.CapacitySlotKey,State=(int)GpuCapacitySlotState.Available,UpdatedAtUtc=now});
            seed.GpuBatches.Add(new(){Id=batch,CapacitySlotKey=handle.CapacitySlotKey,PriorityLane=0,
                ModelRuntimeKey=PaddleOcrVlmRuntimeContract.ModelRuntimeKey,SettingsFingerprint=PaddleOcrVlmRuntimeContract.SettingsFingerprint,
                ItemCount=1,EstimatedBytes=1,AdmissionGeneration=1,OwnerKey=owner,State=(int)GpuBatchState.Released,CreatedAtUtc=now,UpdatedAtUtc=now});
            AddLocalOcrTask(seed,source,task,GpuMiniTaskExecutionState.OutcomeUncertain,now);
            var miniTask=seed.GpuMiniTasks.Local.Single();miniTask.BatchId=batch;miniTask.AdmissionGeneration=1;
            var request=seed.DocumentOcrRequests.Local.Single();request.ResultJson="{}";request.ResultDigest=new byte[32];
            seed.GpuExecutorDispatches.Add(new(){DispatchId=batch,BatchId=batch,CapacitySlotKey=handle.CapacitySlotKey,OwnerKey=owner,
                ExecutorKey=handle.ExecutorKey,AdmissionGeneration=1,State=(int)GpuExecutorDispatchState.DeliveryUncertain,UpdatedAtUtc=now});
            await seed.SaveChangesAsync();
        }
        DocumentOcrTerminalCleanupBinding binding;
        byte[] originalJob,originalTask,originalSlot,originalBatch;
        await using(var read=CreateContext())
        {
            originalJob=await read.Jobs.Where(value=>value.Id==source.JobId).Select(value=>value.RowVersion).SingleAsync();
            originalTask=await read.GpuMiniTasks.Where(value=>value.Id==task).Select(value=>value.RowVersion).SingleAsync();
            originalSlot=await read.GpuCapacitySlots.Select(value=>value.RowVersion).SingleAsync();
            originalBatch=await read.GpuBatches.Where(value=>value.Id==batch).Select(value=>value.RowVersion).SingleAsync();
            binding=new(handle,task,source.JobId,source.RecordId,source.RootId,1,owner,
                await read.DocumentOcrRequests.Select(value=>value.RowVersion).SingleAsync(),originalTask,originalJob,Guid.NewGuid());
            if(change=="root-enabled") await read.SourceRootConfigurations.Where(value=>value.Id==source.RootId)
                .ExecuteUpdateAsync(set=>set.SetProperty(value=>value.State,(int)SourceRootState.Enabled));
            if(change=="partial-claim") await read.DocumentOcrRequests.ExecuteUpdateAsync(set=>set.SetProperty(value=>value.ExecutionDispatchId,batch));
            if(change=="active-capacity") await read.GpuCapacitySlots.ExecuteUpdateAsync(set=>set
                .SetProperty(value=>value.State,(int)GpuCapacitySlotState.Uncertain).SetProperty(value=>value.ActiveBatchId,batch).SetProperty(value=>value.OwnerKey,owner));
            if(change=="other-active-capacity")
            {
                read.GpuBatches.Add(new(){Id=otherBatch,CapacitySlotKey=handle.CapacitySlotKey,PriorityLane=0,
                    ModelRuntimeKey=PaddleOcrVlmRuntimeContract.ModelRuntimeKey,SettingsFingerprint=PaddleOcrVlmRuntimeContract.SettingsFingerprint,
                    ItemCount=1,EstimatedBytes=1,AdmissionGeneration=2,OwnerKey=owner,State=(int)GpuBatchState.Active,CreatedAtUtc=now,UpdatedAtUtc=now});
                AddLocalOcrTask(read,control,otherTask,GpuMiniTaskExecutionState.Active,now);
                var other=read.GpuMiniTasks.Local.Single();other.BatchId=otherBatch;other.AdmissionGeneration=2;
                read.GpuExecutorDispatches.Add(new(){DispatchId=otherBatch,BatchId=otherBatch,CapacitySlotKey=handle.CapacitySlotKey,
                    OwnerKey=owner,ExecutorKey=handle.ExecutorKey,AdmissionGeneration=2,State=(int)GpuExecutorDispatchState.Acknowledged,UpdatedAtUtc=now});
                await read.SaveChangesAsync();
                await read.GpuCapacitySlots.ExecuteUpdateAsync(set=>set.SetProperty(value=>value.State,(int)GpuCapacitySlotState.Reserved)
                    .SetProperty(value=>value.ActiveBatchId,otherBatch).SetProperty(value=>value.OwnerKey,owner));
            }
        }
        if(change=="owner") binding=binding with {CapacityOwnerKey="foreign-owner"};
        if(change=="admission") binding=binding with {Handle=handle with {AdmissionGeneration=2}};
        if(change=="foreign-record") binding=binding with {PipelineRecordId=control.RecordId};
        if(change=="task-version") binding=binding with {TaskRowVersion=new byte[8]};
        if(change=="request-version") binding=binding with {RequestRowVersion=new byte[8]};
        if(change is "valid" or "other-active-capacity") await Documents.OcrLifecycleDeploymentTests.RunScriptAsync("ocr-terminal-cleanup-contract.ps1",_fixture.ConnectionString);
        var verifier=new TerminalCleanupVerifier {Observation=change=="missing-proof"?null:
            new(new(Guid.NewGuid(),new(4242,now.AddHours(-1),new string('a',64))),
                change=="stale-proof"?now.AddMinutes(-1):DateTimeOffset.UtcNow,new string('b',64))};
        var writer=change=="lost-ack"?new CommitLossContextFactory(_fixture.ConnectionString):factory;
        var store=new SqlDocumentOcrStore(writer,scheduler:null);
        if(change=="cli") await InvokeTerminalCleanupCliAsync(binding);
        var result=await store.AcceptTerminalCleanupAsync(binding,verifier,default);
        var accepted=change is "valid" or "lost-ack" or "cli";
        Assert.Equal(accepted,result.Accepted);
        await using(var check=CreateContext())
        {
            Assert.Equal(originalJob,await check.Jobs.Where(value=>value.Id==source.JobId).Select(value=>value.RowVersion).SingleAsync());
            Assert.Equal(originalTask,await check.GpuMiniTasks.Where(value=>value.Id==task).Select(value=>value.RowVersion).SingleAsync());
            Assert.Equal(originalBatch,await check.GpuBatches.Where(value=>value.Id==batch).Select(value=>value.RowVersion).SingleAsync());
            if(change is not ("active-capacity" or "other-active-capacity")) Assert.Equal(originalSlot,await check.GpuCapacitySlots.Select(value=>value.RowVersion).SingleAsync());
            var request=await check.DocumentOcrRequests.SingleAsync(value=>value.MiniTaskId==task);
            Assert.Equal("{}",request.ResultJson);Assert.Equal(new byte[32],request.ResultDigest);Assert.Equal(0,request.State);
            Assert.Equal(accepted?1:0,await check.GpuExecutorEvidence.CountAsync());
            Assert.Equal(accepted?binding.RecoveryInstanceId:null,request.ExecutionInstanceId);
            if(!accepted) Assert.Null(request.NativeCleanupConfirmedAtUtc);
        }
        if(change=="other-active-capacity")
        {
            await using(var quiesce=CreateContext())
            {
                await quiesce.GpuMiniTasks.Where(value=>value.Id==otherTask).ExecuteUpdateAsync(set=>set.SetProperty(value=>value.ExecutionState,(int)GpuMiniTaskExecutionState.Completed));
                await quiesce.GpuBatches.Where(value=>value.Id==otherBatch).ExecuteUpdateAsync(set=>set.SetProperty(value=>value.State,(int)GpuBatchState.Completed));
                await quiesce.GpuExecutorDispatches.Where(value=>value.BatchId==otherBatch).ExecuteUpdateAsync(set=>set.SetProperty(value=>value.State,(int)GpuExecutorDispatchState.Terminal));
                await quiesce.GpuCapacitySlots.ExecuteUpdateAsync(set=>set.SetProperty(value=>value.State,(int)GpuCapacitySlotState.Available)
                    .SetProperty(value=>value.ActiveBatchId,(Guid?)null).SetProperty(value=>value.OwnerKey,(string?)null));
            }
            verifier.Observation=verifier.Observation! with {ObservedAtUtc=DateTimeOffset.UtcNow};
            Assert.True((await store.AcceptTerminalCleanupAsync(binding,verifier,default)).Accepted);
            await using var check=CreateContext();
            Assert.Equal((int)GpuMiniTaskExecutionState.Completed,await check.GpuMiniTasks.Where(value=>value.Id==otherTask).Select(value=>value.ExecutionState).SingleAsync());
            Assert.Equal(originalTask,await check.GpuMiniTasks.Where(value=>value.Id==task).Select(value=>value.RowVersion).SingleAsync());
            return;
        }
        if(!accepted) return;
        var calls=verifier.Calls;
        verifier.Observation=null;
        Assert.Equal(DocumentOcrLifecycleDisposition.AlreadyApplied,(await store.AcceptTerminalCleanupAsync(binding,verifier,default)).Disposition);
        Assert.Equal(calls,verifier.Calls);
        Assert.False((await store.AcceptTerminalCleanupAsync(binding with {RequestRowVersion=new byte[8]},verifier,default)).Accepted);
        Assert.False((await store.AcceptTerminalCleanupAsync(binding with {RecoveryInstanceId=Guid.NewGuid()},verifier,default)).Accepted);
        await AdvanceSqlToFilesAsync(deletion,operation);
        await using var final=CreateContext();
        Assert.False(await final.PipelineRecords.AnyAsync(value=>value.Id==source.RecordId));
        Assert.True(await final.PipelineRecords.AnyAsync(value=>value.Id==control.RecordId));
        Assert.Empty(await final.GpuMiniTasks.ToArrayAsync());
        Assert.Empty(await final.DocumentOcrRequests.ToArrayAsync());
        Assert.Empty(await final.Vectors.ToArrayAsync());
    }

    private async Task InvokeTerminalCleanupCliAsync(DocumentOcrTerminalCleanupBinding binding)
    {
        var area=Path.Combine(Path.GetTempPath(),"FluxOcrCli_"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        try
        {
            await using var db=CreateContext();
            var identity=await db.Database.SqlQueryRaw<string>("SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName'))+'|'+DB_NAME()+'|'+CONVERT(nvarchar(36),database_guid) AS [Value] FROM sys.database_recovery_status WHERE database_id=DB_ID()").SingleAsync();
            var config=Path.Combine(area,"config.json");await File.WriteAllTextAsync(config,"{}");
            var hold=Path.Combine(area,"hold.json");await File.WriteAllTextAsync(hold,JsonSerializer.Serialize("synthetic-cleanup-only"));
            var verifier=Path.Combine(area,"synthetic-verifier.ps1");
            await File.WriteAllTextAsync(verifier,"[ordered]@{CleanupVerified=$true;ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')}|ConvertTo-Json -Compress");
            static async Task<string> HashAsync(string path)=>Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var plan=Path.Combine(area,"plan.json");
            var history=(await db.Database.GetAppliedMigrationsAsync()).ToArray();
            var declined=await RunCleanupAsync(history.SkipLast(1).ToArray());
            Assert.Equal(1,declined.ExitCode);
            Assert.Contains("document-ocr-maintenance-database-binding-changed",declined.Error);
            Assert.Empty(await db.GpuExecutorEvidence.ToArrayAsync());
            var applied=await RunCleanupAsync(history);
            Assert.True(applied.ExitCode==0,applied.Output+applied.Error);
            using var result=JsonDocument.Parse(applied.Output);
            Assert.True(result.RootElement.GetProperty("CleanupConfirmed").GetBoolean());
            Assert.True(result.RootElement.GetProperty("CleanupOnly").GetBoolean());
            Assert.False(result.RootElement.TryGetProperty("Settled",out _));
            Assert.False(result.RootElement.TryGetProperty("Requeued",out _));

            async Task<(int ExitCode,string Output,string Error)> RunCleanupAsync(string[] plannedHistory)
            {
                await File.WriteAllTextAsync(plan,JsonSerializer.Serialize(new {Version=2,Mode="CleanupOnly",Binding=(object?)null,TerminalBinding=binding,
                    ReleaseId="synthetic-cleanup-only",DatabaseIdentity=identity,OriginalHistory=plannedHistory,
                    OperationRoot=area,HoldPath=hold,ConfigurationPath=config,ConfigurationSha256=await HashAsync(config),
                    VerifierScriptPath=verifier,VerifierScriptSha256=await HashAsync(verifier)}));
                var testAssembly=typeof(SourceDeletionIntegrationTests).Assembly.Location;
                var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var argument in new[]{"exec","--runtimeconfig",Path.ChangeExtension(testAssembly,"runtimeconfig.json"),"--depsfile",
                    Path.ChangeExtension(testAssembly,"deps.json"),typeof(FluxKnowledge.Cli.Commands.OcrRecoveryCommand).Assembly.Location,
                    "ocr-recovery","apply","--plan",plan,"--sha256",await HashAsync(plan)}) start.ArgumentList.Add(argument);
                start.Environment["ConnectionStrings__FluxKnowledge"]=_fixture.ConnectionString;
                using var process=Process.Start(start)!;
                var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
                using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try{await process.WaitForExitAsync(budget.Token);}catch{if(!process.HasExited)process.Kill(true);throw;}
                return (process.ExitCode,await stdout,await stderr);
            }
        }
        finally{Directory.Delete(area,true);}
    }

    private sealed class TerminalCleanupVerifier : IDocumentOcrMaintenanceVerifier
    {
        internal DocumentOcrMaintenanceVerification? Observation {get;set;}
        internal int Calls {get;private set;}
        public ValueTask<DocumentOcrMaintenanceVerification?> VerifyStoppedAndCleanAsync(CancellationToken ct)
        {Calls++;return ValueTask.FromResult(Observation);}
    }
}
