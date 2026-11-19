using System.Data.Common;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceDeletionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Withdrawal_page_freezes_ownership_and_preserves_cancellation_cursor_and_replay()
    {
        var now=DateTimeOffset.UtcNow;
        var source=await SeedRootAsync("frozen-withdrawal",SourceRootState.Deleting,now);
        var control=await SeedRootAsync("frozen-withdrawal-control",SourceRootState.Enabled,now);
        var operation=await AddDeletionAsync(source,now);
        var (ids,_)=await SeedDistinctMembershipVectorsAsync(source,4097,now);
        var finalVector=ids[^1];
        var (foreign,_)=await SeedMembershipsAsync(control,1,now);
        var ordinary=new SqlSourceDeletionStore(SqlTestData.CreateFactory(_fixture),TimeProvider.System);
        var work=Assert.IsType<SourceDeletionWorkItem>(await ordinary.ClaimNextAsync(default));
        Assert.Equal(operation,work.OperationId);
        using var cancel=new CancellationTokenSource();
        var cancelled=new SqlSourceDeletionStore(new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString).AddInterceptors(new CancelWithdrawalUpdate(cancel)).Options),TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await cancelled.PurgeAsync(work,null,cancel.Token));
        await using(var rollback=CreateContext())
        {
            Assert.False(await rollback.Vectors.AnyAsync(vector=>vector.IsDeleted));
            Assert.False(await rollback.PipelineRecords.Where(record=>record.Id==source.RecordId).Select(record=>record.IsDeleted).SingleAsync());
            Assert.Null(await rollback.SourceDeletionOperations.Where(value=>value.Id==operation).Select(value=>value.WithdrawalVectorCursor).SingleAsync());
        }
        var ownership=new WithdrawalOwnershipProbe(_fixture.ConnectionString,source.RevisionId,control.RootId);
        var writer=new SqlSourceDeletionStore(new RootDeletionScaleFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString).AddInterceptors(ownership).Options),TimeProvider.System);
        Assert.True((await writer.PurgeAsync(work,null,default)).ContinueImmediately);
        Assert.True(ownership.VerifiedBlocked);
        await ordinary.PurgeAsync(work,null,default);
        await using var check=CreateContext();
        Assert.Equal(ids.Take(4096),await check.Vectors.Where(vector=>vector.IsDeleted).OrderBy(vector=>vector.VectorId).Select(vector=>vector.VectorId).ToArrayAsync());
        Assert.False(await check.Vectors.Where(vector=>vector.VectorId==finalVector).Select(vector=>vector.IsDeleted).SingleAsync());
        Assert.False(await check.Vectors.Where(vector=>vector.VectorId==foreign.VectorId).Select(vector=>vector.IsDeleted).SingleAsync());
        Assert.Equal(ids[4095],await check.SourceDeletionOperations.Where(value=>value.Id==operation).Select(value=>value.WithdrawalVectorCursor).SingleAsync());
        Assert.Equal(source.RootId,await check.SourceRevisions.Where(value=>value.Id==source.RevisionId).Select(value=>value.SourceRootId).SingleAsync());
    }

    private sealed class CancelWithdrawalUpdate(CancellationTokenSource cancel):DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,CommandExecutedEventData data,int result,CancellationToken ct=default)
        {if(command.CommandText.StartsWith("UPDATE",StringComparison.Ordinal)&&command.CommandText.Contains("[Vectors]",StringComparison.Ordinal))cancel.Cancel();return ValueTask.FromResult(result);}
    }

    private sealed class WithdrawalOwnershipProbe(string connectionString,Guid revision,Guid otherRoot):DbCommandInterceptor
    {
        internal bool VerifiedBlocked {get;private set;}
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {
            if(VerifiedBlocked||!command.CommandText.StartsWith("UPDATE",StringComparison.Ordinal)||!command.CommandText.Contains("[Vectors]",StringComparison.Ordinal))return result;
            await using var connection=new SqlConnection(new SqlConnectionStringBuilder(connectionString){Pooling=false,MultipleActiveResultSets=false}.ConnectionString);
            await connection.OpenAsync(ct);
            await using(var limit=connection.CreateCommand()){limit.CommandText="SET LOCK_TIMEOUT 100;";await limit.ExecuteNonQueryAsync(ct);}
            await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(ct);
            try
            {
                await using var mutation=new SqlCommand("UPDATE SourceRevisions SET SourceRootId=@root WHERE Id=@revision",connection,transaction){CommandTimeout=5};
                mutation.Parameters.AddWithValue("@root",otherRoot);mutation.Parameters.AddWithValue("@revision",revision);
                var refused=await Assert.ThrowsAsync<SqlException>(()=>mutation.ExecuteNonQueryAsync(ct));
                Assert.Equal(1222,refused.Number);VerifiedBlocked=true;
            }
            finally{await transaction.RollbackAsync(CancellationToken.None);}
            return result;
        }
    }

}
