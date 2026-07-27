using System.Net.Sockets;
using System.Reflection;

namespace FluxKnowledge.Application.Mcp;

public static class McpTransientFailureClassifier
{
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            TimeoutException or SocketException or IOException => true,
            _ when IsTransientSqlException(exception) => true,
            _ when exception.InnerException is not null => IsTransient(exception.InnerException),
            _ => false
        };
    }

    private static bool IsTransientSqlException(Exception exception)
    {
        if (!string.Equals(
                exception.GetType().FullName,
                "Microsoft.Data.SqlClient.SqlException",
                StringComparison.Ordinal))
        {
            return false;
        }

        return exception.GetType()
            .GetProperty("IsTransient", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(exception) is true;
    }
}
