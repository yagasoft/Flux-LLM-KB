using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

[Collection("sql-full-text")]
public sealed class WindowEligibilityBackfillTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_page_commit_recovers_durably_without_recomputing_an_acknowledged_page(bool lostAcknowledgement)
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,new string('x',40_000),publish:false,embed:false);
        await using var db=environment.Factory.CreateDbContext();
        await db.TextChunkWindowEligibilities.ExecuteDeleteAsync();
        var interrupted=new InterruptCommit(lostAcknowledgement);
        var store=new SqlTextChunkWindowEligibilityStore(new InterruptedFactory(fixture.ConnectionString,interrupted));
        if(lostAcknowledgement) await store.RunOneAsync(default,2);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RunOneAsync(default,2).AsTask());
        Assert.True(interrupted.Interrupted);
        var before=await db.TextChunkWindowEligibilities.AsNoTracking().OrderBy(value => value.TextChunkId).ToArrayAsync();
        Assert.Equal(lostAcknowledgement?2:0,before.Length);
        for(var pass=0;pass<100;pass++)
            if((await new SqlTextChunkWindowEligibilityStore(environment.Factory).RunOneAsync(default,2)).Completed) break;
        Assert.Equal(await db.TextChunks.CountAsync(),await db.TextChunkWindowEligibilities.CountAsync());
        foreach(var proof in before)
            Assert.Equal(proof.CalculatedAtUtc,await db.TextChunkWindowEligibilities.Where(value => value.TextChunkId==proof.TextChunkId).Select(value => value.CalculatedAtUtc).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Repeated_revisit_hints_during_capture_do_not_discard_committed_progress()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,new string('x',40_000),publish:false,embed:false);
        await using var db=environment.Factory.CreateDbContext();
        await db.TextChunkWindowEligibilities.ExecuteDeleteAsync();
        var count=await db.TextChunks.CountAsync();
        var last=await db.TextChunks.MaxAsync(value => value.Id);
        var hintStore=new SqlTextChunkWindowEligibilityStore(environment.Factory);
        var barrier=new BeforePageCommit(() => hintStore.RequestRevisitAsync(last,default).AsTask());
        var factory=new InterceptedFactory(fixture.ConnectionString,barrier);
        for(var page=0;page<count;page++)
        {
            var result=await new SqlTextChunkWindowEligibilityStore(factory).RunOneAsync(default,2);
            Assert.InRange(result.Recomputed,1,2);
            if(await db.TextChunkWindowEligibilities.CountAsync()==count) break;
        }
        Assert.Equal(count,await db.TextChunkWindowEligibilities.CountAsync());
        Assert.True((await new SqlTextChunkWindowEligibilityStore(environment.Factory).RunOneAsync(default)).Completed);
        Assert.True((await db.TextChunkWindowBackfillStates.AsNoTracking().SingleAsync()).Completed);
        Assert.True(barrier.Calls>1);
    }

    [NativeSqlServerTheory]
    [InlineData("canonical")]
    [InlineData("boundary")]
    [InlineData("delete-chunk")]
    [InlineData("deleting-root")]
    public async Task A_changed_binding_during_capture_never_publishes_an_old_decision(string change)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"The original canonical passage.",publish:false,embed:false);
        await using var db=environment.Factory.CreateDbContext();
        var chunk=await db.TextChunks.SingleAsync();
        var artifact=await db.Artifacts.SingleAsync(value => value.Id==chunk.ArtifactId);
        Guid? deleting=null;
        if(change=="deleting-root")
        {
            deleting=await environment.AddRetainedAndPumpAsync("A rooted passage is queued for removal.");
        }
        await db.TextChunkWindowEligibilities.ExecuteDeleteAsync();
        var barrier=new BeforePageCommit(async () =>
        {
            await using var mutation=environment.Factory.CreateDbContext();
            if(change=="canonical")
            {
                await using var tx=await mutation.Database.BeginTransactionAsync();
                await SqlTextChunkWindowEligibilityStore.InvalidateAsync(mutation,mutation.Artifacts.Where(value => value.Id==artifact.Id).Select(value => value.Id),default);
                await mutation.Artifacts.Where(value => value.Id==artifact.Id).ExecuteUpdateAsync(set => set
                    .SetProperty(value => value.SearchText,"The replacement canonical text.")
                    .SetProperty(value => value.ContentHash,new string('f',64)));
                await tx.CommitAsync();
            }
            else if(change=="boundary")
                await mutation.TextChunks.Where(value => value.Id==chunk.Id).ExecuteUpdateAsync(set => set.SetProperty(value => value.StartOffset,1));
            else if(change=="delete-chunk")
                await mutation.TextChunks.Where(value => value.Id==chunk.Id).ExecuteDeleteAsync();
            else
            {
                var root=await mutation.PipelineRecords.Where(value => value.Id==deleting).Select(value => value.SourceRevision!.SourceRootId).SingleAsync();
                await mutation.SourceRootConfigurations.Where(value => value.Id==root).ExecuteUpdateAsync(set => set.SetProperty(value => value.State,2));
            }
        },once:true);
        await new SqlTextChunkWindowEligibilityStore(new InterceptedFactory(fixture.ConnectionString,barrier)).RunOneAsync(default);
        if(change=="deleting-root")
        {
            var excluded=db.Artifacts.Where(value => value.PipelineRecordId==deleting).Select(value => value.Id);
            Assert.False(await db.TextChunkWindowEligibilities.AnyAsync(value => excluded.Contains(value.ArtifactId)));
        }
        else Assert.False(await db.TextChunkWindowEligibilities.AnyAsync(value => value.TextChunkId==chunk.Id));
        if(change is "canonical" or "boundary")
        {
            await new SqlTextChunkWindowEligibilityStore(environment.Factory).RunOneAsync(default);
            Assert.False((await db.TextChunkWindowEligibilities.AsNoTracking().SingleAsync()).Available);
        }
    }

    [NativeSqlServerFact]
    public async Task Rule_change_recomputes_flags_once_and_an_active_deployment_hold_does_no_work()
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"A policy-bound retained decision.",publish:false,embed:false);
        await using var db=environment.Factory.CreateDbContext();
        environment.PermitRebuild(Guid.NewGuid());
        Assert.Equal(0,(await new SqlTextChunkWindowEligibilityStore(environment.Factory,hold:environment.DeploymentHold).RunOneAsync(default)).Inspected);
        Assert.Empty(await db.TextChunkWindowBackfillStates.ToArrayAsync());
        environment.ReleaseRebuild();
        var policy=CanonicalWindowEligibility.RuleVersion+"-next";
        var changed=await new SqlTextChunkWindowEligibilityStore(environment.Factory,ruleVersion:policy).RunOneAsync(default);
        Assert.Equal(1,changed.Recomputed);
        Assert.Equal(policy,(await db.TextChunkWindowEligibilities.AsNoTracking().SingleAsync()).RuleVersion);
        Assert.Equal(0,(await new SqlTextChunkWindowEligibilityStore(environment.Factory,ruleVersion:policy).RunOneAsync(default)).Recomputed);
    }

    [NativeSqlServerFact]
    public async Task Backfill_excludes_a_source_queued_for_deletion_and_leaves_its_flags_and_artifact_stamp_untouched()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"Keeper canonical passage.");
        var deleting = await environment.AddRetainedAndPumpAsync("This source is being removed and must never be backfilled.");
        await using var db = environment.Factory.CreateDbContext();
        var root = await db.PipelineRecords.Where(value => value.Id==deleting).Select(value => value.SourceRevision!.SourceRootId).SingleAsync();
        await db.SourceRootConfigurations.Where(value => value.Id==root).ExecuteUpdateAsync(set => set.SetProperty(value => value.State,2));
        var targets = await db.Artifacts.Where(value => value.PipelineRecordId==deleting).Select(value => value.Id).ToArrayAsync();
        var stamps = await db.Artifacts.Where(value => targets.Contains(value.Id)).OrderBy(value => value.Id).Select(value => value.WindowEligibilityVersion).ToArrayAsync();
        var targetChunks = await db.TextChunks.Where(value => targets.Contains(value.ArtifactId)).Select(value => value.Id).ToArrayAsync();
        Assert.NotEmpty(targetChunks);
        await db.TextChunkWindowEligibilities.ExecuteDeleteAsync();
        await db.TextChunkWindowBackfillStates.ExecuteDeleteAsync();
        var store = new SqlTextChunkWindowEligibilityStore(environment.Factory);
        var result = await store.RunOneAsync(default);
        Assert.True(result.Completed);
        Assert.True(result.Recomputed>0);
        Assert.False(await db.TextChunkWindowEligibilities.AnyAsync(value => targetChunks.Contains(value.TextChunkId)));
        Assert.Equal(stamps,await db.Artifacts.Where(value => targets.Contains(value.Id)).OrderBy(value => value.Id).Select(value => value.WindowEligibilityVersion).ToArrayAsync());
        await store.RequestRevisitAsync(targetChunks.Min(),default);
        Assert.Equal(0,(await new SqlTextChunkWindowEligibilityStore(environment.Factory).RunOneAsync(default)).Recomputed);
        Assert.False(await db.TextChunkWindowEligibilities.AnyAsync(value => targetChunks.Contains(value.TextChunkId)));
    }

    [NativeSqlServerFact]
    public async Task Legacy_chunks_backfill_in_bounded_committed_pages_and_restart_reuses_true_and_false_results()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,new string('x',40_000),publish:false,embed:false);
        await using var setup = environment.Factory.CreateDbContext();
        await setup.TextChunkWindowEligibilities.ExecuteDeleteAsync();
        var count = await setup.TextChunks.CountAsync();
        Assert.True(count > 2);
        var store = new SqlTextChunkWindowEligibilityStore(environment.Factory);
        var first = await store.RunOneAsync(default,batchSize:2);
        Assert.Equal(2,first.Inspected);
        Assert.Equal(2,first.Recomputed);
        Assert.False(first.Completed);
        var proofs = await setup.TextChunkWindowEligibilities.AsNoTracking().OrderBy(value => value.TextChunkId).ToArrayAsync();
        Assert.Equal(2,proofs.Length);
        var checkpoint = Assert.IsType<long>((await setup.TextChunkWindowBackfillStates.AsNoTracking().SingleAsync()).LastChunkId);
        for (var batch = 0; batch < count; batch++)
        {
            // Recreate the store for every batch: progress must live in SQL.
            var next = await new SqlTextChunkWindowEligibilityStore(environment.Factory).RunOneAsync(default,batchSize:2);
            Assert.InRange(next.Inspected,0,2);
            Assert.InRange(next.Recomputed,0,2);
            if (next.Completed) break;
        }
        Assert.Equal(count,await setup.TextChunkWindowEligibilities.CountAsync());
        Assert.Contains(await setup.TextChunkWindowEligibilities.ToArrayAsync(),value => !value.Available);
        var before = await setup.TextChunkWindowEligibilities.AsNoTracking().OrderBy(value => value.TextChunkId).ToArrayAsync();
        var stamps = await setup.Artifacts.OrderBy(value => value.Id).Select(value => value.WindowEligibilityVersion).ToArrayAsync();
        var repeated = await new SqlTextChunkWindowEligibilityStore(environment.Factory).RunOneAsync(default,batchSize:2);
        Assert.Equal(0,repeated.Recomputed);
        Assert.Equal(before.Select(value => value.CalculatedAtUtc),await setup.TextChunkWindowEligibilities.OrderBy(value => value.TextChunkId).Select(value => value.CalculatedAtUtc).ToArrayAsync());
        Assert.Equal(stamps,await setup.Artifacts.OrderBy(value => value.Id).Select(value => value.WindowEligibilityVersion).ToArrayAsync());
        Assert.True((await setup.TextChunkWindowBackfillStates.AsNoTracking().SingleAsync()).LastChunkId >= checkpoint);
    }
    private sealed class InterceptedFactory(string connection,DbTransactionInterceptor interceptor) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).AddInterceptors(interceptor).Options);
    }

    private sealed class BeforePageCommit(Func<Task> action,bool once=false) : DbTransactionInterceptor
    {
        private int _transactions;
        public int Calls { get; private set; }
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData,InterceptionResult<DbTransaction> result,CancellationToken cancellationToken=default)
        {
            if(++_transactions%2==0 && (!once || Calls==0)) { Calls++;await action(); }
            return result;
        }
    }

    private sealed class InterruptedFactory(string connection,InterruptCommit interceptor) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection,sql => sql.ExecutionStrategy(dependencies => new RetryLostCommit(dependencies)))
            .AddInterceptors(interceptor).Options);
    }
    private sealed class LostCommit : Exception;
    private sealed class RetryLostCommit(ExecutionStrategyDependencies dependencies) : ExecutionStrategy(dependencies,1,TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception)=>exception is LostCommit;
    }
    private sealed class InterruptCommit(bool lostAcknowledgement) : DbTransactionInterceptor
    {
        private int _commits;
        public bool Interrupted { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,TransactionEventData eventData,
            InterceptionResult result,CancellationToken cancellationToken=default)
        {
            if(++_commits==2 && !lostAcknowledgement){Interrupted=true;throw new OperationCanceledException("synthetic page cancellation");}
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,CancellationToken cancellationToken=default)
        {
            if(_commits==2 && lostAcknowledgement){Interrupted=true;throw new LostCommit();}
            return Task.CompletedTask;
        }
    }
}
