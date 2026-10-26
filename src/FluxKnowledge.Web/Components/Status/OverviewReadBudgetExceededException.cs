using Microsoft.Data.SqlClient;

namespace FluxKnowledge.Web.Components.Status;

public sealed class OverviewReadBudgetExceededException(Exception innerException)
    : TimeoutException("Overview exceeded its read budget.", innerException)
{
    internal static bool HasCommandCancellationErrors(SqlException exception) =>
        exception.Errors.Cast<SqlError>().Any(error => error.Number == 0) &&
        exception.Errors.Cast<SqlError>().All(error => error.Number is 0 or 3621);
}
