using System.Reflection;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.Status;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SqlProjectionReadBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Internal_budget_sql_cancellation_reports_unavailable_and_retains_the_original_error(bool terminationNotice)
    {
        var original = terminationNotice ? SqlFailure(0, 0, 3621) : SqlFailure(0);
        var factory = new FailingFactory(original, waitForCancellation: true);
        var failure = await Assert.ThrowsAsync<OverviewReadBudgetExceededException>(
            () => Reader(factory).ReadOverviewAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(original, failure.InnerException);
        Assert.True(factory.ObservedToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancelled_sql_commands_are_not_reclassified_as_an_internal_timeout(bool terminationNotice)
    {
        using var caller = new CancellationTokenSource();
        var original = terminationNotice ? SqlFailure(0, 0, 3621) : SqlFailure(0);
        var factory = new FailingFactory(original, waitForCancellation: true);
        var read = Reader(factory).ReadOverviewAsync(caller.Token).AsTask();
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await caller.CancelAsync();
        Assert.Same(original, await Assert.ThrowsAsync<SqlException>(() => read));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(1205)]
    [InlineData(50000)]
    [InlineData(3621)]
    public async Task Sql_errors_without_internal_cancellation_are_preserved(int number)
    {
        var original = SqlFailure(number);
        var factory = new FailingFactory(original, waitForCancellation: false);
        Assert.Same(original, await Assert.ThrowsAsync<SqlException>(
            () => Reader(factory).ReadOverviewAsync(default).AsTask()));
        Assert.False(factory.ObservedToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(1205)]
    [InlineData(50000)]
    [InlineData(3621)]
    public async Task Genuine_sql_errors_are_preserved_even_after_the_internal_budget_expires(int number)
    {
        var original = SqlFailure(number);
        var factory = new FailingFactory(original, waitForCancellation: true);
        Assert.Same(original, await Assert.ThrowsAsync<SqlException>(
            () => Reader(factory).ReadOverviewAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.True(factory.ObservedToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_genuine_error_following_a_zero_number_error_is_not_hidden_by_budget_expiry(bool terminationNotice)
    {
        var original = terminationNotice ? SqlFailure(0, 3621, 1205) : SqlFailure(0, 1205);
        var factory = new FailingFactory(original, waitForCancellation: true);
        Assert.Same(original, await Assert.ThrowsAsync<SqlException>(
            () => Reader(factory).ReadOverviewAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_or_uncancelled_termination_evidence_remains_a_failure(bool empty)
    {
        var original = empty ? SqlFailure() : SqlFailure(0, 0, 3621);
        Assert.Same(original, await Assert.ThrowsAsync<SqlException>(
            () => Reader(new(original, false)).ReadOverviewAsync(default).AsTask()));
    }

    private static SqlProjectionReader Reader(FailingFactory factory) =>
        new(factory, new RecoveryStatus(), new SqlGpuSchedulerStore(factory));

    private sealed class RecoveryStatus : IDerivedIndexRecoveryStatus
    {
        public DerivedIndexRecoverySnapshot Snapshot => new(DerivedIndexRecoveryState.Healthy, null, null, null, null, 0);
    }

    // Exercise the reader's public exception boundary deterministically, without
    // connecting to SQL. Existing deletion integration tests prove lock release.
    private sealed class FailingFactory(SqlException failure, bool waitForCancellation) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ObservedToken { get; private set; }
        public FluxKnowledgeDbContext CreateDbContext() => throw new NotSupportedException();
        public async Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            Entered.TrySetResult();
            if (waitForCancellation)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            }
            throw failure;
        }
    }

    internal static SqlException SqlFailure(params int[] numbers)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), flags, null, null, null)!;
        foreach (var number in numbers)
        {
            var error = (SqlError)Activator.CreateInstance(typeof(SqlError), flags, null,
                [number, (byte)0, (byte)14, "synthetic-server", "synthetic", string.Empty, 1, 0, null], null)!;
            typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, [error]);
        }
        if (numbers.Length == 0)
            return (SqlException)Activator.CreateInstance(typeof(SqlException), flags, null,
                ["synthetic", errors, null, Guid.Empty], null)!;
        return (SqlException)typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2)
            .Invoke(null, [errors, "synthetic-server"])!;
    }
}
