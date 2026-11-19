using System.Data;
using System.Data.Common;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Windows;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SourceManifestRetirementPageTests
{
    [NativeSqlServerTheory]
    [InlineData("timeout")]
    [InlineData("cancellation")]
    [InlineData("unclassified")]
    public async Task Admission_contention_defers_without_file_effects_and_does_not_hide_other_failures(string outcome)
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(Fixture,"Publication admission is separate from file cleanup.",segmented:true);
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var (work,ids,recovery)=await SeedAsync(environment,2);var files=new PageFiles();
        var factory=outcome=="unclassified"?(IDbContextFactory<FluxKnowledgeDbContext>)new Factory(Fixture.ConnectionString,new AdmissionFailure()):new AdmissionTimeoutFactory(Fixture.ConnectionString);
        var store=new SqlSegmentRetirementOperations(factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        await using(var holder=environment.Factory.CreateDbContext())
        await using(var held=await holder.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            await SqlPublishedPassageSelection.AcquireFenceAsync(holder,default);
            using var cancellation=new CancellationTokenSource();if(outcome=="cancellation")cancellation.CancelAfter(250);
            if(outcome=="timeout")Assert.Equal("source-delete-segment-retirement-query-owned",await store.AdvanceAsync(work,default));
            else if(outcome=="cancellation")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.AdvanceAsync(work,cancellation.Token).AsTask());
            else{var error=await Assert.ThrowsAsync<SqlException>(()=>store.AdvanceAsync(work,default).AsTask());Assert.Equal(50000,error.Number);}
        }
        Assert.Empty(files.Deleted);
        await using(var check=environment.Factory.CreateDbContext())
        {Assert.Equal(0,await check.IndexGenerations.CountAsync(row=>ids.Contains(row.Id) && row.RetirementOperationId!=null));Assert.Equal(1,await check.SourceDeletionOperations.Where(row=>row.Id==work.OperationId).Select(row=>row.State).SingleAsync());}
        var source=new SqlSourceDeletionStore(environment.Factory,TimeProvider.System);
        await source.DeferAsync(work,"source-delete-segment-retirement-query-owned",default);
        var retryWork=Assert.IsType<FluxKnowledge.Application.Ports.SourceDeletionWorkItem>(await source.ClaimNextAsync(default));
        var retry=new SqlSegmentRetirementOperations(environment.Factory,files,environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        Assert.Equal("source-delete-segment-retirement-progressing",await retry.AdvanceAsync(retryWork,default));Assert.Equal(2,files.Deleted.Count);
    }

    private sealed class AdmissionTimeoutFactory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {public FluxKnowledgeDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection,options=>options.CommandTimeout(1)).Options);}
    private sealed class AdmissionFailure : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken token=default)
        {if(command.CommandText.Contains("UPDLOCK",StringComparison.Ordinal) && command.CommandText.Contains("[IndexState]",StringComparison.Ordinal))command.CommandText="THROW 50000, 'Unclassified admission fault', 1;";return ValueTask.FromResult(result);}
    }
}
