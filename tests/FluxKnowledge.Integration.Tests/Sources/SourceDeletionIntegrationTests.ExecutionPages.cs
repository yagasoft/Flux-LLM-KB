using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execution_graph_pages_preserve_anchors_and_resume_without_repeating_a_committed_page(bool lostAcknowledgement)
    {
        var (source,operation,_)=await SeedCompletedBatchGraphAsync(2001);
        var ordinary=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await ReachExecutionGraphAsync(ordinary,source,operation);
        byte[] recordVersion,jobVersion;
        await using(var before=CreateContext())
        {
            recordVersion=await before.PipelineRecords.Where(record=>record.Id==source.RecordId).Select(record=>record.RowVersion).SingleAsync();
            jobVersion=await before.Jobs.Where(job=>job.Id==source.JobId).Select(job=>job.RowVersion).SingleAsync();
        }
        var work=Assert.IsType<SourceDeletionWorkItem>(await ordinary.ClaimNextAsync(default));
        var writer=lostAcknowledgement?new SqlSourceDeletionStore(new CommitLossContextFactory(_fixture.ConnectionString),TimeProvider.System):ordinary;
        Assert.True((await writer.PurgeAsync(work,null,default)).ContinueImmediately);
        await using(var check=CreateContext())
        {
            Assert.Equal(1001,await check.GpuMiniTasks.CountAsync());
            Assert.Equal(1001,await check.DocumentOcrRequests.CountAsync());
            Assert.Equal(1001,await check.GpuBatches.CountAsync());
            Assert.Equal(recordVersion,await check.PipelineRecords.Where(record=>record.Id==source.RecordId).Select(record=>record.RowVersion).SingleAsync());
            Assert.Equal(jobVersion,await check.Jobs.Where(job=>job.Id==source.JobId).Select(job=>job.RowVersion).SingleAsync());
        }
        Assert.True((await ordinary.PurgeAsync(work,null,default)).ContinueImmediately);
        await using(var replay=CreateContext())Assert.Equal(1001,await replay.GpuMiniTasks.CountAsync());
        await RunFreshExecutionPageAsync(operation);
        await using(var fresh=CreateContext())Assert.Equal(1,await fresh.GpuMiniTasks.CountAsync());
        await AdvanceSqlToFilesAsync(ordinary,operation);
        await using var final=CreateContext();
        Assert.Empty(await final.GpuMiniTasks.ToArrayAsync());
        Assert.False(await final.PipelineRecords.AnyAsync(record=>record.Id==source.RecordId));
    }

    private async Task ReachExecutionGraphAsync(SqlSourceDeletionStore store,SeededRoot source,Guid operation)
    {
        for(var pass=0;pass<8;pass++)
        {
            await using(var check=CreateContext())
                if(!await check.TextChunks.AnyAsync(chunk=>chunk.Artifact.PipelineRecordId==source.RecordId))return;
            var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
            Assert.Equal(operation,work.OperationId);
            Assert.True((await store.PurgeAsync(work,null,default)).ContinueImmediately);
        }
        throw new InvalidOperationException("Fixture payload pages did not clear.");
    }

    private async Task RunFreshExecutionPageAsync(Guid operation)
    {
        await using var db=CreateContext();
        var databaseGuid=await db.Database.SqlQueryRaw<Guid>("SELECT database_guid AS [Value] FROM sys.database_recovery_status WHERE database_id=DB_ID()").SingleAsync();
        var assembly=typeof(SourceDeletionIntegrationTests).Assembly.Location;
        var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(assembly);start.ArgumentList.Add("source-delete-unit");
        start.Environment["FLUXKNOWLEDGE_SCENARIO_SQL"]=_fixture.ConnectionString;
        start.Environment["FLUXKNOWLEDGE_SCENARIO_DATABASE_GUID"]=databaseGuid.ToString();
        start.Environment["FLUXKNOWLEDGE_SCENARIO_OPERATION"]=operation.ToString();
        using var process=Process.Start(start)!;
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(55));}
        catch{if(!process.HasExited)process.Kill(true);throw;}
        var output=await stdout;Assert.True(process.ExitCode==0,output+await stderr);
        using var result=JsonDocument.Parse(output);
        Assert.NotEqual(Environment.ProcessId,result.RootElement.GetProperty("ProcessId").GetInt32());
        Assert.True(result.RootElement.GetProperty("ContinueImmediately").GetBoolean());
    }
}
