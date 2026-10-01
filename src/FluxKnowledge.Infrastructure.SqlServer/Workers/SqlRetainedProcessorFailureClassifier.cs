using FluxKnowledge.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

public sealed class SqlRetainedProcessorFailureClassifier : IRetainedProcessorFailureClassifier
{
    public bool TryClassify(Exception exception, out int errorNumber)
    {
        var sql = exception as SqlException ?? (exception is DbUpdateException ? exception.InnerException as SqlException : null);
        errorNumber = sql?.Number ?? 0;
        // These are the observed command timeout and transaction deadlock. Resuming the
        // retained reconciler grants no stale lease or terminal Publish replay authority.
        return errorNumber is -2 or 1205 && sql!.Errors.Cast<SqlError>().All(error => error.Number is -2 or 1205);
    }
}
