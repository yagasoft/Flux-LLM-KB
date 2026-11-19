using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

public sealed class SqlHostedDatabaseFailureClassifier : IHostedDatabaseFailureClassifier
{
    public bool TryClassify(Exception exception, out int errorNumber)
    {
        while (exception is DbUpdateException or RetryLimitExceededException && exception.InnerException is { } inner)
            exception = inner;
        var sql = exception as SqlException;
        errorNumber = sql?.Number ?? 0;
        return sql is not null && sql.Errors.Count > 0 && sql.Errors.Cast<SqlError>().All(error =>
            SqlDatabaseStartupReadiness.IsAvailabilityErrorNumber(error.Number) || error.Number is 1205 or -2146893055);
    }
}
