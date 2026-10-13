using System.Reflection;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace FluxKnowledge.OutlookHost.Tests;

public sealed class DesktopStartupSqlFailureTests
{
    [Fact]
    public async Task Exhausted_read_only_SQL_availability_retries_are_classified_without_replaying_work()
    {
        var failure = new RetryLimitExceededException("synthetic-retry-limit", SqlFailure(2));
        var factory = new FailingFactory(failure);
        var runner = new PendingVisioDocumentRunner(factory, "unused-retained-root");
        var unavailable = await Assert.ThrowsAsync<DatabaseStartupUnavailableException>(
            () => runner.RunOneAsync(CancellationToken.None).AsTask());
        Assert.Equal(2, unavailable.SqlErrorNumber);
        Assert.Same(failure, unavailable.InnerException);
        Assert.Equal(1, factory.Calls);
    }

    [Theory]
    [InlineData(18456)]
    [InlineData(1205)]
    [InlineData(2627)]
    public async Task Retry_wrapper_does_not_hide_authentication_transaction_or_integrity_failures(int number)
    {
        var failure = new RetryLimitExceededException("synthetic-retry-limit", SqlFailure(number));
        var factory = new FailingFactory(failure);
        var runner = new PendingVisioDocumentRunner(factory, "unused-retained-root");
        Assert.Same(failure, await Assert.ThrowsAsync<RetryLimitExceededException>(
            () => runner.RunOneAsync(CancellationToken.None).AsTask()));
        Assert.Equal(1, factory.Calls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4060)]
    public async Task Known_read_only_discovery_unavailability_is_classified_before_COM_or_durable_work(int number)
    {
        var failure = SqlFailure(number);
        var factory = new FailingFactory(failure);
        var runner = new PendingVisioDocumentRunner(factory, "unused-retained-root");

        var unavailable = await Assert.ThrowsAsync<DatabaseStartupUnavailableException>(
            () => runner.RunOneAsync(CancellationToken.None).AsTask());

        Assert.Equal(number, unavailable.SqlErrorNumber);
        Assert.Same(failure, unavailable.InnerException);
        Assert.Equal(1, factory.Calls);
    }

    [Theory]
    [InlineData(18456)]
    [InlineData(1205)]
    [InlineData(2627)]
    public async Task Authentication_transaction_and_integrity_failures_are_not_reclassified(int number)
    {
        var failure = SqlFailure(number);
        var factory = new FailingFactory(failure);
        var runner = new PendingVisioDocumentRunner(factory, "unused-retained-root");
        Assert.Same(failure, await Assert.ThrowsAsync<SqlException>(
            () => runner.RunOneAsync(CancellationToken.None).AsTask()));
        Assert.Equal(1, factory.Calls);
    }

    private sealed class FailingFactory(Exception failure) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public int Calls { get; private set; }
        public FluxKnowledgeDbContext CreateDbContext() { Calls++; throw failure; }
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static SqlException SqlFailure(int number)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var error = (SqlError)Activator.CreateInstance(typeof(SqlError), flags, null,
            [number, (byte)0, (byte)14, "synthetic-server", "synthetic", string.Empty, 1, 0, null], null)!;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), flags, null, null, null)!;
        typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, [error]);
        return (SqlException)typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2)
            .Invoke(null, [errors, "synthetic-server"])!;
    }
}
