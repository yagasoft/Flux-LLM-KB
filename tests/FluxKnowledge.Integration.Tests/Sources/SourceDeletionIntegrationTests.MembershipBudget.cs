using System.Data;
using System.Data.Common;
using System.Diagnostics;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Larger_membership_delete_keeps_the_vector_page_bound_and_replays_only_its_committed_receipt(bool lostAcknowledgement)
    {
        var (operation,ids,foreign)=await SeedMembershipBudgetAsync();
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var writer=lostAcknowledgement?new SqlSourceDeletionStore(new CommitLossContextFactory(_fixture.ConnectionString),TimeProvider.System):store;
        var watch=Stopwatch.StartNew();
        Assert.True((await writer.PurgeAsync(work,null,default)).ContinueImmediately);
        output.WriteLine($"membership_delete rows=16000 vectors=1000 lost_ack={lostAcknowledgement} total_unit_ms={watch.Elapsed.TotalMilliseconds:F1}");
        Assert.True((await store.PurgeAsync(work,null,default)).ContinueImmediately);
        await using var check=CreateContext();
        var receipt=await check.SourceDeletionOperations.AsNoTracking().SingleAsync(o=>o.Id==operation);
        Assert.Equal(16000,receipt.MembershipRowsDeleted);
        Assert.Equal(ids[999],receipt.MembershipVectorCursor);
        Assert.Equal(2,await check.IndexGenerationVectors.CountAsync());
        Assert.True(await check.IndexGenerationVectors.AnyAsync(m=>m.VectorId==foreign));
        Assert.Equal(1002,await check.Vectors.CountAsync());
        // This withdrawn-state refusal sits just outside the unchanged 1,000-vector page.
        work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        Assert.Equal("source-delete-vector-not-withdrawn",(await store.PurgeAsync(work,null,default)).ReasonCode);
        Assert.Equal(2,await check.IndexGenerationVectors.CountAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Larger_membership_delete_cancellation_rolls_back_and_publication_wait_is_bounded(bool cancelAfterDelete)
    {
        var (operation,_,foreign)=await SeedMembershipBudgetAsync();
        using var cancel=new CancellationTokenSource();
        var boundary=new MembershipDeleteBoundary(cancelAfterDelete?cancel:null);
        var factory=new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString,sql=>sql.CommandTimeout(10)).AddInterceptors(boundary).Options);
        var store=new SqlSourceDeletionStore(factory,TimeProvider.System);
        var work=Assert.IsType<SourceDeletionWorkItem>(await store.ClaimNextAsync(default));
        var watch=Stopwatch.StartNew();
        var purge=store.PurgeAsync(work,null,cancel.Token).AsTask();
        await boundary.Deleting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var publisherWait=Stopwatch.StartNew();
        var publication=Task.Run(async()=>
        {
            await using var db=CreateContext();db.Database.SetCommandTimeout(10);
            await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await SqlPublishedPassageSelection.AcquireFenceAsync(db,default);
            publisherWait.Stop();
            await transaction.CommitAsync();
        });
        if(cancelAfterDelete)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>purge);
        else Assert.True((await purge).ContinueImmediately);
        await publication;
        output.WriteLine($"membership_delete cancellation={cancelAfterDelete} total_unit_ms={watch.Elapsed.TotalMilliseconds:F1} publication_wait_ms={publisherWait.Elapsed.TotalMilliseconds:F1} deleted_rows={boundary.DeletedRows}");
        Assert.Equal(16000,boundary.DeletedRows);
        Assert.InRange(watch.Elapsed.TotalSeconds,0,10);
        Assert.InRange(publisherWait.Elapsed.TotalSeconds,0,5);
        await using var check=CreateContext();
        var receipt=await check.SourceDeletionOperations.AsNoTracking().SingleAsync(o=>o.Id==operation);
        Assert.Equal(cancelAfterDelete?0:16000,receipt.MembershipRowsDeleted);
        Assert.Equal(cancelAfterDelete?16002:2,await check.IndexGenerationVectors.CountAsync());
        Assert.True(await check.IndexGenerationVectors.AnyAsync(m=>m.VectorId==foreign));
        Assert.Equal(1002,await check.Vectors.CountAsync());
    }

    private async Task<(Guid Operation,long[] Ids,long Foreign)> SeedMembershipBudgetAsync()
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("membership-delete-budget",SourceRootState.Deleting,now);
        var control=await SeedRootAsync("membership-budget-control",SourceRootState.Enabled,now);
        var operation=await AddDeletionAsync(source,now);
        var (ids,generation)=await SeedDistinctMembershipVectorsAsync(source,1001,now);
        var store=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        await AdvanceSqlToFilesAsync(store,operation,stopAtCutover:true);
        var foreign=await AddVectorAsync(control,generation,"Foreign membership stays owned by its source.",now);
        await using var seed=CreateContext();
        seed.IndexGenerationVectors.Add(new IndexGenerationVectorEntity{GenerationId=generation,VectorId=foreign.VectorId});
        await seed.SaveChangesAsync();
        await seed.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @history TABLE(Id uniqueidentifier PRIMARY KEY);
            INSERT @history SELECT TOP(15) NEWID() FROM sys.all_objects;
            INSERT IndexGenerations(Id,FormatVersion,SegmentCount,CorpusEpoch,CorpusVersion,ModelFingerprint,Dimensions,
                IndexPath,MetadataChecksum,VectorCount,CreatedAtUtc,ValidatedAtUtc,RetiredAtUtc)
            SELECT history.Id,original.FormatVersion,original.SegmentCount,original.CorpusEpoch,original.CorpusVersion,
                original.ModelFingerprint,original.Dimensions,'membership-budget',original.MetadataChecksum,1000,{now},{now},{now}
            FROM @history history CROSS JOIN IndexGenerations original WHERE original.Id={generation};
            INSERT IndexGenerationVectors(GenerationId,VectorId)
            SELECT history.Id,member.VectorId FROM @history history CROSS JOIN IndexGenerationVectors member
            WHERE member.GenerationId={generation} AND member.VectorId<{ids[1000]};
            UPDATE Vectors SET IsDeleted=0 WHERE VectorId={ids[1000]};
            """);
        Assert.Equal(16002,await seed.IndexGenerationVectors.CountAsync());
        return(operation,ids,foreign.VectorId);
    }

    private sealed class MembershipDeleteBoundary(CancellationTokenSource? cancellation):DbCommandInterceptor
    {
        public TaskCompletionSource Deleting{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DeletedRows{get;private set;}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<int> result,CancellationToken cancellationToken=default)
        {
            if(command.CommandText.Contains("DELETE TOP (",StringComparison.Ordinal)&&command.CommandText.Contains("FROM [IndexGenerationVectors]",StringComparison.Ordinal))
                Deleting.TrySetResult();
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,CommandExecutedEventData eventData,int result,
            CancellationToken cancellationToken=default)
        {
            if(command.CommandText.Contains("DELETE TOP (",StringComparison.Ordinal)&&command.CommandText.Contains("FROM [IndexGenerationVectors]",StringComparison.Ordinal))
            {
                DeletedRows=result;cancellation?.Cancel();
            }
            return ValueTask.FromResult(result);
        }
    }
}
