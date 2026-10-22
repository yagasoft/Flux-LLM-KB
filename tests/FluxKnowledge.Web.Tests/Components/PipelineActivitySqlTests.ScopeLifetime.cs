using System.Data;
using System.Data.Common;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_branch_scopes_balance_connection_ownership_and_preserve_a_caller_transaction(bool transaction)
    {
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        await context.Database.OpenConnectionAsync();
        await using var callerTransaction = transaction ? await context.Database.BeginTransactionAsync() : null;
        var connection = (SqlConnection)context.Database.GetDbConnection();
        var connectionId = connection.ClientConnectionId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using (var branches = await SqlSelectedBranchReadScope.CreateAsync(context, [], CancellationToken.None))
            {
                Assert.Equal(0, await ReadBranchCountAsync(context));
                Assert.Same(callerTransaction, context.Database.CurrentTransaction);
                await branches.CompleteAsync(CancellationToken.None);
            }
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(connectionId, connection.ClientConnectionId);
            Assert.Equal(0, await context.Database.SqlQueryRaw<int>(
                "SELECT CASE WHEN OBJECT_ID('tempdb..#FluxPipelineSelectedBranches') IS NULL THEN 0 ELSE 1 END AS [Value]").SingleAsync());
        }
        if (callerTransaction is not null) await callerTransaction.RollbackAsync();
        await context.Database.CloseConnectionAsync();
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_or_cancelled_branch_preparation_preserves_the_failure_and_allows_pool_reuse(bool cancel)
    {
        var failure = new BranchPreparationFailure(cancel);
        await using var context = new TestDbContextFactory(_fixture.ConnectionString, failure).CreateDbContext();
        if (cancel) await context.Database.ExecuteSqlRawAsync("CREATE TABLE dbo.DashboardPreparationStarted (Value int NOT NULL);");
        using var stopping = new CancellationTokenSource();
        var prepare = SqlSelectedBranchReadScope.CreateAsync(context, [], stopping.Token).AsTask();
        if (cancel)
        {
            // A committed marker proves the server reached WAITFOR. InfoMessage
            // notifications from ExecuteNonQuery may be buffered until completion.
            await using var probe = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
            using var signalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await probe.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM dbo.DashboardPreparationStarted")
                       .SingleAsync(signalDeadline.Token) == 0)
                await Task.Delay(25, signalDeadline.Token);
            Assert.Equal(stopping.Token, failure.CommandToken);
            await stopping.CancelAsync();
            var exception = await Record.ExceptionAsync(() => prepare.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(exception is OperationCanceledException || exception is SqlException sql && sql.Number == 0,
                $"Unexpected cancellation result: {exception}");
            await probe.Database.ExecuteSqlRawAsync("DROP TABLE dbo.DashboardPreparationStarted;");
        }
        else
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() => prepare);
            Assert.Equal(50000, exception.Number);
        }
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        await using var recovered = await SqlSelectedBranchReadScope.CreateAsync(context, [], CancellationToken.None);
        Assert.Equal(0, await ReadBranchCountAsync(context));
        await recovered.CompleteAsync(CancellationToken.None);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_branch_scopes_are_connection_local_and_reject_a_different_context()
    {
        await using var first = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        await using var second = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        await using var left = await SqlSelectedBranchReadScope.CreateAsync(first, [], CancellationToken.None);
        await using var right = await SqlSelectedBranchReadScope.CreateAsync(second, [], CancellationToken.None);
        // This is connection-local synthetic lookup state, not a canonical application write.
        await first.Database.ExecuteSqlRawAsync("INSERT #FluxPipelineSelectedBranches VALUES (NEWID(), NEWID());");
        Assert.Equal(1, await ReadBranchCountAsync(first));
        Assert.Equal(0, await ReadBranchCountAsync(second));
        Assert.Throws<InvalidOperationException>(() => SqlPipelineActivityQuery.Jobs(second, DateTimeOffset.UtcNow,
            new(false, null), left, new("[]", 0)));
        await left.CompleteAsync(CancellationToken.None);
        await right.CompleteAsync(CancellationToken.None);
    }

    [NativeSqlServerTheory]
    [InlineData("overview")]
    [InlineData("jobs")]
    [InlineData("gpu")]
    public async Task A_transient_failure_retries_the_entire_read_with_a_fresh_prepared_scope(string surface)
    {
        var fault = new BranchReadRetry(surface);
        var factory = new BranchRetryFactory(_fixture.ConnectionString, fault);
        var reader = ActivityReader(factory);
        await ReadAsync(); // Warm compilation without the injected failure.
        fault.Enabled = true;
        await ReadAsync();
        Assert.True(fault.Failed);
        // Attention may additionally repeat one completed but inconsistent sample;
        // that is separate from rebuilding the failed execution-strategy attempt.
        Assert.InRange(fault.PreparedContexts.Count, 2, surface == "overview" ? 2 : 3);
        Assert.Equal(fault.PreparedContexts.Count, fault.PreparedContexts.Distinct().Count());
        Assert.Equal(fault.PreparedContexts[0], fault.FailedContext);
        Assert.NotEqual(fault.FailedContext, fault.PreparedContexts[1]);

        async Task ReadAsync()
        {
            if (surface == "overview") _ = await reader.ReadOverviewAsync(CancellationToken.None);
            else _ = await reader.ReadPipelineAttentionAsync(surface, null, 0, CancellationToken.None);
        }
    }

    private static Task<int> ReadBranchCountAsync(FluxKnowledgeDbContext context) => context.Database
        .SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM #FluxPipelineSelectedBranches").SingleAsync();

    private sealed class BranchPreparationFailure(bool cancel) : DbCommandInterceptor
    {
        private bool _failed;
        public CancellationToken CommandToken { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_failed && command.CommandText.Contains("INSERT INTO #FluxPipelineSelectedBranches", StringComparison.Ordinal))
            {
                _failed = true;
                CommandToken = cancellationToken;
                if (!cancel) throw SqlProjectionReadBudgetTests.SqlFailure(50000);
                command.CommandText = "INSERT dbo.DashboardPreparationStarted VALUES (1); WAITFOR DELAY '00:00:10';\n" + command.CommandText;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BranchReadRetry(string surface) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool Failed { get; private set; }
        public Guid FailedContext { get; private set; }
        public List<Guid> PreparedContexts { get; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("CREATE TABLE #FluxPipelineSelectedBranches", StringComparison.Ordinal))
                PreparedContexts.Add(eventData.Context!.ContextId.InstanceId);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !Failed && command.CommandText.Contains(surface == "gpu" ? "FROM GpuMiniTasks t" : "FROM Jobs j", StringComparison.Ordinal))
            {
                Failed = true;
                FailedContext = eventData.Context!.ContextId.InstanceId;
                throw SqlProjectionReadBudgetTests.SqlFailure(1205);
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class BranchRetryFactory(string connection, IInterceptor interceptor) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection, options => options.EnableRetryOnFailure()).AddInterceptors(interceptor).Options);
    }
}
