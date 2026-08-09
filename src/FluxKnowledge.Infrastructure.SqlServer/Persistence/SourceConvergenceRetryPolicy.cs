using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Retries only source-convergence races, rerunning the caller's complete fresh-context attempt.</summary>
public static class SourceConvergenceRetryPolicy
{
    private const int MaximumAttempts = 4;

    public static async Task<T> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> executeFreshAttemptAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executeFreshAttemptAsync);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await executeFreshAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < MaximumAttempts && IsRetryableConvergenceRace(exception))
            {
                // The supplied delegate owns context construction, so the next pass re-locks and re-reads SQL state.
            }
        }
    }

    private static bool IsRetryableConvergenceRace(Exception exception) =>
        FindSqlException(exception) is { Number: 1205 or 2601 or 2627 };

    private static SqlException? FindSqlException(Exception exception) => exception switch
    {
        SqlException sqlException => sqlException,
        DbUpdateException { InnerException: not null } updateException => FindSqlException(updateException.InnerException!),
        _ when exception.InnerException is not null => FindSqlException(exception.InnerException!),
        _ => null
    };
}
