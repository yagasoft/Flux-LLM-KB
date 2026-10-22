using System.Data.Common;
using System.Diagnostics;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class OverviewReadBudgetIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData("counts")]
    [InlineData("aggregate")]
    public async Task An_ordinary_SQL_wait_exhausts_the_internal_budget_and_the_next_read_recovers(string phase)
    {
        var delay = new OrdinaryReadDelay(phase);
        var factory = new Factory(fixture.ConnectionString, delay);
        var reader = new SqlProjectionReader(factory, new Recovery(), new SqlGpuSchedulerStore(factory));
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
        var recovered = await reader.ReadOverviewAsync(CancellationToken.None);
        Assert.True(recovered.Activity.IsAvailable);
        Assert.True(recovered.Activity.IsConsistent);
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
