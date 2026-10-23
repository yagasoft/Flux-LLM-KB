using System.Data;
using System.Data.Common;
using System.Diagnostics;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class OverviewReadBudgetIntegrationTests(NativeSqlServerFixture fixture, ITestOutputHelper output)
    : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData("counts")]
    [InlineData("aggregate")]
    public async Task An_ordinary_SQL_wait_exhausts_the_internal_budget_and_the_next_read_recovers(string phase)
    {
        var delay = new OrdinaryReadDelay(phase);
        var fences = new FenceTrace();
        var factory = new Factory(fixture.ConnectionString, delay, fences);
        var reader = new SqlProjectionReader(factory, new Recovery(), new SqlGpuSchedulerStore(factory));
        await WaitForFullTextIdleAsync(factory);
        await ObserveFixtureAsync(factory, "before timed experiment");
        using var caller = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        var read = reader.ReadOverviewAsync(caller.Token).AsTask();
        try
        {
            await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))));
            await Assert.ThrowsAsync<OverviewReadBudgetExceededException>(() => read);
            Assert.False(caller.IsCancellationRequested);
            Assert.InRange(elapsed.Elapsed.TotalSeconds, 1.5, 5);
        }
        finally
        {
            await caller.CancelAsync();
            try { await read; } catch (Exception) when (caller.IsCancellationRequested) { }
        }
        // A real command ran WAITFOR; recovery uses the same SQL pool, not a fake result.
        Assert.True(delay.WasDelayed);
        await ObserveFixtureAsync(factory, "after cancellation");
        // Cancellation does not establish a quiet database-wide version fence.
        // Observe the owned fixture before the single asserted recovery read;
        // never retry that read or change the production deadline/fence.
        await WaitForQuietFixtureAsync(factory);
        var recovered = await reader.ReadOverviewAsync(CancellationToken.None);
        await ObserveFixtureAsync(factory, "after recovery read");
        Assert.True(recovered.Activity.IsAvailable);
        Assert.True(recovered.Activity.IsConsistent, string.Join("; ", fences.Samples));
    }

    private async Task ObserveFixtureAsync(Factory factory, string phase)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var context = factory.CreateDbContext();
        var metadata = await context.Database.SqlQueryRaw<string>("""
            SELECT (SELECT DB_NAME() AS [database], CONVERT(bigint,@@DBTS) AS [version],
              JSON_QUERY((SELECT OBJECT_NAME(object_id) AS [table], is_enabled, change_tracking_state_desc, has_crawl_completed,
                CONVERT(int,OBJECTPROPERTYEX(object_id,'TableFulltextPopulateStatus')) AS populateStatus,
                CONVERT(int,OBJECTPROPERTYEX(object_id,'TableFulltextPendingChanges')) AS pendingChanges
                FROM sys.fulltext_indexes ORDER BY object_id FOR JSON PATH)) AS fulltext,
              JSON_QUERY((SELECT OBJECT_NAME(object_id) AS [table], name FROM sys.stats WHERE auto_created=1
                ORDER BY object_id,name FOR JSON PATH)) AS autoStatistics
              FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) AS [Value]
            """).SingleAsync(deadline.Token);
        output.WriteLine($"{phase}: {metadata}");
    }

    private static async Task WaitForFullTextIdleAsync(Factory factory)
    {
        // The migrated template is restored before asynchronous initial FT work
        // necessarily finishes. Keep both real indexes enabled and AUTO.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var context = factory.CreateDbContext();
        const string sql = """
            SELECT CASE WHEN COUNT(*)=2 AND SUM(CASE WHEN is_enabled=1 AND change_tracking_state_desc=N'AUTO'
              AND has_crawl_completed=1
              AND CONVERT(int,OBJECTPROPERTYEX(object_id,'TableFulltextPopulateStatus'))=0
              AND CONVERT(int,OBJECTPROPERTYEX(object_id,'TableFulltextPendingChanges'))=0
              THEN 1 ELSE 0 END)=2 THEN 1 ELSE 0 END AS [Value]
            FROM sys.fulltext_indexes WHERE object_id IN (OBJECT_ID(N'Artifacts'),OBJECT_ID(N'TextChunks'))
            """;
        while (await context.Database.SqlQueryRaw<int>(sql).SingleAsync(deadline.Token) != 1)
            await Task.Delay(100, deadline.Token);
    }

    private static async Task WaitForQuietFixtureAsync(Factory factory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var context = factory.CreateDbContext();
        var previous = await SqlPipelineActivityQuery.ReadFenceAsync(context, deadline.Token);
        var quiet = Stopwatch.StartNew();
        while (quiet.Elapsed < TimeSpan.FromMilliseconds(500))
        {
            await Task.Delay(50, deadline.Token);
            var next = await SqlPipelineActivityQuery.ReadFenceAsync(context, deadline.Token);
            if (!previous.Accepts(next)) quiet.Restart();
            previous = next;
        }
    }

    private sealed class FenceTrace : DbCommandInterceptor
    {
        public List<string> Samples { get; } = [];
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("MIN_ACTIVE_ROWVERSION", StringComparison.Ordinal)) return result;
            var copy = new DataTable();
            copy.Load(result);
            await result.DisposeAsync();
            Samples.Add($"database={command.Connection!.Database}, version={copy.Rows[0]["Version"]}, stable={copy.Rows[0]["IsStable"]}");
            return copy.CreateDataReader();
        }
    }

    private sealed class OrdinaryReadDelay(string phase) : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDelayed { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!WasDelayed && command.CommandText.Contains(phase == "counts" ? "FROM [Jobs]" : "FROM Jobs j", StringComparison.Ordinal))
            {
                WasDelayed = true;
                command.CommandText = "WAITFOR DELAY '00:00:10';\n" + command.CommandText;
                Started.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(string connection, params IInterceptor[] interceptors) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection, sql => sql.EnableRetryOnFailure()).AddInterceptors(interceptors).Options);
    }
    private sealed class Recovery : IDerivedIndexRecoveryStatus
    {
        public DerivedIndexRecoverySnapshot Snapshot => new(DerivedIndexRecoveryState.Healthy, null, null, null, null, 0);
    }
}
