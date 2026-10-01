using System.Reflection;
using FluxKnowledge.Application.Indexing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Indexing;

public sealed class PublicationDiagnosticsTests
{
    [Fact]
    public void Failure_logs_only_observed_SQL_number_identity_phase_and_type_without_provider_text()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), flags, null, null, null)!;
        var error = (SqlError)Activator.CreateInstance(typeof(SqlError), flags, null,
            [-2, (byte)0, (byte)14, "private-server", "secret-content-sentinel", "private-query", 1, 0, null], null)!;
        typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, [error]);
        var sql = (SqlException)typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2).Invoke(null, [errors, "synthetic"])!;
        var logger = new RecordingLogger();
        var id = Guid.Parse("00000000-2000-4000-8000-000000000000");
        PublicationDiagnostics.Failure(logger, id, "snapshot-vector-loading", sql);
        PublicationDiagnostics.Failure(logger, id, "activation", new DbUpdateException("private query body", sql));
        PublicationDiagnostics.Failure(logger, id, "placement", new IOException("private-file-path"));
        Assert.All(logger.Messages, message =>
        {
            Assert.Contains(id.ToString(), message);
            Assert.DoesNotContain("private", message);
            Assert.DoesNotContain("secret-content-sentinel", message);
        });
        Assert.Contains("SQL error -2", logger.Messages[0]);
        Assert.Contains("snapshot-vector-loading", logger.Messages[0]);
        Assert.Contains("SQL error -2", logger.Messages[1]);
        Assert.Contains("DbUpdateException", logger.Messages[1]);
        Assert.Contains("placement", logger.Messages[2]);
        Assert.DoesNotContain("SQL error -2", logger.Messages[2]);
        Assert.Equal<int?>([-2, -2, null], logger.SqlErrorNumbers);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public List<int?> SqlErrorNumbers { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
            var number = Assert.Single(fields, field => field.Key == "SqlErrorNumber").Value;
            SqlErrorNumbers.Add(number is null ? null : Assert.IsType<int>(number));
            Messages.Add(formatter(state, exception));
        }
    }
}
