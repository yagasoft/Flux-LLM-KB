using System.Reflection;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceConvergenceRetryPolicyTests
{
    [Theory]
    [InlineData(1205)]
    [InlineData(2601)]
    [InlineData(2627)]
    public async Task Recognised_sql_failures_retry_the_entire_attempt_then_return_the_reread_winner(int number)
    {
        var attempts = 0;
        var winner = Guid.NewGuid();

        var result = await SourceConvergenceRetryPolicy.ExecuteAsync(
            (_, _) => ++attempts == 1
                ? Task.FromException<Guid>(CreateSqlException(number))
                : Task.FromResult(winner),
            CancellationToken.None);

        Assert.Equal(winner, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Fourth_recognised_sql_failure_is_propagated()
    {
        var attempts = 0;

        var failure = await Assert.ThrowsAsync<SqlException>(() => SourceConvergenceRetryPolicy.ExecuteAsync<Guid>(
            (_, _) =>
            {
                attempts++;
                return Task.FromException<Guid>(CreateSqlException(1205));
            },
            CancellationToken.None));

        Assert.Equal(1205, failure.Number);
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task Non_retryable_failure_is_propagated_without_a_second_attempt()
    {
        var attempts = 0;
        var expected = new InvalidOperationException("invariant");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => SourceConvergenceRetryPolicy.ExecuteAsync<Guid>(
            (_, _) =>
            {
                attempts++;
                return Task.FromException<Guid>(expected);
            },
            CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
    }

    private static SqlException CreateSqlException(int number)
    {
        var error = (SqlError)Activator.CreateInstance(typeof(SqlError), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [number, (byte)0, (byte)14, "server", "test", string.Empty, 1, 0, null], culture: null)!;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: null, culture: null)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(errors, [error]);
        return (SqlException)typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2)
            .Invoke(null, [errors, "server"])!;
    }
}
