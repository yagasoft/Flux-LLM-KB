using System.Data.Common;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integration.Tests.Workers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerTheory]
    [InlineData("prepared",-2,true,false)]
    [InlineData("segment-sealed",-2,true,false)]
    [InlineData("prepared",2601,false,false)]
    [InlineData("segment-sealed",2601,false,false)]
    [InlineData("segment-sealed",0,false,false)]
    [InlineData("segment-sealed",-2,false,true)]
    public async Task SQL_failure_releases_only_known_unwound_candidate_work_and_keeps_unclassified_failures_pinned(
        string point,int number,bool released,bool refuseCleanup)
    {
        await using var environment=await PipelineEnvironment.CreateAsync(_fixture,"A publication candidate is interrupted at a durable SQL boundary.",
            publish:false,segmented:true);
        await using var db=await environment.Factory.CreateDbContextAsync();
        var draft=Guid.Parse((await db.Artifacts.SingleAsync(a=>a.Stage==(int)PipelineStage.Embed)).SearchText!);
        var fault=new CandidateSqlCommitFault(point,number);
        var cleanup=new CandidateCleanupRefusal(fault,refuseCleanup);
        var factory=new IndexingSqlFaultFactory(_fixture.ConnectionString,fault,cleanup);
        var store=new SqlSegmentedIndexStore(factory,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        var paths=new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot);
        var publisher=new SegmentedIndexPublisher(environment.Builder,store,new SqlDerivedIndexRecoveryStore(factory,TimeProvider.System),
            new UsearchSegmentBuilder(new(environment.IndexRoot),paths,new(2)),
            new UsearchManifestValidator(new(environment.IndexRoot),paths),true,new SqlRetainedProcessorFailureClassifier());
        var error=await Record.ExceptionAsync(async()=>await publisher.BuildAndPlaceAsync(draft,default));
        Assert.NotNull(error);
        if(number==0)Assert.IsType<IOException>(error);
        else Assert.Equal(number,Assert.IsType<SqlException>(error).Number);
        Assert.Same(fault.Failure,error);Assert.True(fault.Fired);
        Assert.Equal(refuseCleanup,error.Data.Contains("candidate-abandonment-failure"));
        var candidate=Assert.Single(await db.IndexPublicationCandidates.AsNoTracking().ToArrayAsync());
        Assert.Equal(released?3:0,candidate.State);
        if(released)Assert.False(await db.IndexCandidateMembers.AnyAsync(m=>m.CandidateId==candidate.Id));
        Assert.Null((await db.IndexState.AsNoTracking().SingleAsync()).ActiveIndexGenerationId);
        Assert.True(await db.Vectors.AnyAsync());
        if(Directory.Exists(environment.IndexRoot))
            foreach(var file in Directory.EnumerateFiles(environment.IndexRoot,"index.usearch",SearchOption.AllDirectories))
                using(File.Open(file,FileMode.Open,FileAccess.Read,FileShare.None)){}
    }

    [NativeSqlServerTheory]
    [InlineData(PipelineStage.Embed, "before-write")]
    [InlineData(PipelineStage.Embed, "rolled-back")]
    [InlineData(PipelineStage.Embed, "committed")]
    [InlineData(PipelineStage.Publish, "before-write")]
    [InlineData(PipelineStage.Publish, "rolled-back")]
    [InlineData(PipelineStage.Publish, "committed")]
    public async Task Real_indexing_stages_recover_SQL_failure_without_duplicate_output_or_live_candidate_pins(
        PipelineStage stage, string point)
    {
        var clock=new CapacityClock();
        var fault=new IndexingSqlFault(stage,point);
        await using var environment=await PipelineEnvironment.CreateAsync(_fixture,"An existing publication survives a SQL outage.",
            clock:clock,segmented:true,configureServices:services=>
            {
                services.AddSingleton<IStageTransitionFailureInjector>(fault);
                services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(new IndexingSqlFaultFactory(_fixture.ConnectionString,fault));
                foreach(var type in new[]{typeof(EmbedStageWorker),typeof(PublishStageWorker)})
                {
                    var registration=Assert.Single(services,d=>d.ServiceType==typeof(IStageWorker)&&d.ImplementationType==type);
                    services.Remove(registration);
                    services.AddScoped<IStageWorker>(provider=>new SqlFaultingIndexWorker(
                        (IStageWorker)ActivatorUtilities.CreateInstance(provider,type),fault));
                }
            });
        await environment.AddAndPumpAtPathAsync("A second source completes exactly once after recovery.","second.txt",pump:false);
        var recordId=environment.LastReceipt!.PipelineRecordId.Value;
        var original=(await environment.ActiveGenerationAsync()).Id;
        fault.Armed=true;
        await Assert.ThrowsAsync<SqlException>(()=>environment.PumpAsync());
        if(point=="committed")Assert.True(fault.CommitFaultFired);
        await using(var check=await environment.Factory.CreateDbContextAsync())
        {
            var job=await check.Jobs.SingleAsync(j=>j.PipelineRecordId==recordId&&j.Stage==(int)stage);
            Assert.Equal((int)(point=="committed"?PublicJobState.Completed:PublicJobState.WorkerProcessing),job.PublicState);
            Assert.False(await check.AuditEvents.AnyAsync(a=>a.EventType=="pipeline.failed"));
            Assert.False(await check.IndexPublicationCandidates.AnyAsync(c=>c.State==0||c.State==1));
            if(stage==PipelineStage.Publish&&point=="committed")
                Assert.NotEqual(original,(await environment.ActiveGenerationAsync()).Id);
            else Assert.Equal(original,(await environment.ActiveGenerationAsync()).Id);
        }
        clock.Advance();
        await environment.PumpAsync();
        await using var verified=await environment.Factory.CreateDbContextAsync();
        Assert.All(await verified.Jobs.Where(j=>j.PipelineRecordId==recordId).ToArrayAsync(),
            j=>Assert.Equal((int)PublicJobState.Completed,j.PublicState));
        var artifacts=await verified.Artifacts.Where(a=>a.PipelineRecordId==recordId).ToArrayAsync();
        Assert.Equal(5,artifacts.Length);
        Assert.Equal(5,artifacts.Select(a=>a.Stage).Distinct().Count());
        var draftId=Guid.Parse(Assert.Single(artifacts,a=>a.Stage==(int)PipelineStage.Embed).SearchText!);
        Assert.Single(await verified.IndexGenerations.Where(g=>g.Id==draftId).ToArrayAsync());
        Assert.False(await verified.IndexPublicationCandidates.AnyAsync(c=>c.State==0||c.State==1));
        Assert.Equal(point=="committed"?1:2,fault.TargetCalls);
        await AssertSegmentedOracleAsync(environment);
    }

    private sealed class IndexingSqlFault(PipelineStage stage,string point):DbTransactionInterceptor,IStageTransitionFailureInjector
    {
        public PipelineStage Stage=>stage;
        public string Point=>point;
        public bool Armed{get;set;}
        public bool InTarget{get;set;}
        public int TargetCalls{get;set;}
        public bool CommitFaultFired{get;private set;}
        public ValueTask AfterArtifactWrittenAsync(CancellationToken cancellationToken)
        {
            if(InTarget&&point=="rolled-back")throw CpuStageSqlRecoveryTests.SqlFailure(-2);
            return ValueTask.CompletedTask;
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,
            CancellationToken cancellationToken=default)
        {
            if(InTarget&&point=="committed"&&!CommitFaultFired&&eventData.Context!.ChangeTracker.Entries<ArtifactEntity>()
                .Any(e=>e.Entity.Stage==(int)stage))
            {
                CommitFaultFired=true;
                throw CpuStageSqlRecoveryTests.SqlFailure(-2);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class IndexingSqlFaultFactory(string connection,params IInterceptor[] interceptors):IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).AddInterceptors(interceptors).Options);
    }

    private sealed class CandidateSqlCommitFault(string point,int number):DbTransactionInterceptor
    {
        public bool Fired{get;private set;}
        public Exception Failure{get;}=number==0?new IOException("Synthetic native or file I/O failure."):CpuStageSqlRecoveryTests.SqlFailure(number);
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,
            CancellationToken cancellationToken=default)
        {
            var hit=point=="prepared"
                ?eventData.Context!.ChangeTracker.Entries<IndexPublicationCandidateEntity>().Any(e=>e.Entity.State==0&&!e.Entity.WorklistComplete)
                :eventData.Context!.ChangeTracker.Entries<IndexSegmentEntity>().Any(e=>e.Entity.ProducingCandidateId!=null&&e.Entity.State==1);
            if(!Fired&&hit){Fired=true;throw Failure;}
            return Task.CompletedTask;
        }
    }

    private sealed class CandidateCleanupRefusal(CandidateSqlCommitFault fault,bool enabled):DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            if(enabled&&fault.Fired&&command.CommandText.Contains("FROM [IndexPublicationCandidates] WITH (UPDLOCK, HOLDLOCK)",StringComparison.Ordinal))
                throw CpuStageSqlRecoveryTests.SqlFailure(-2);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SqlFaultingIndexWorker(IStageWorker inner,IndexingSqlFault fault):IStageWorker
    {
        public string Operation=>inner.Operation;
        public async ValueTask ExecuteAsync(StageWorkItem work,CancellationToken cancellationToken)
        {
            var target=work.Job.Stage==fault.Stage&&fault.Armed;
            if(target)
            {
                fault.TargetCalls++;fault.Armed=false;
                if(fault.Point=="before-write")throw CpuStageSqlRecoveryTests.SqlFailure(-2);
            }
            else if(work.Job.Stage==fault.Stage&&fault.TargetCalls>0)fault.TargetCalls++;
            fault.InTarget=target;
            try{await inner.ExecuteAsync(work,cancellationToken);}
            finally{fault.InTarget=false;}
        }
    }
}
