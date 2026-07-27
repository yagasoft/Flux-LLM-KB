using System.Net.Sockets;
using System.Reflection;
using FluxKnowledge.Application.Pipeline;

namespace FluxKnowledge.Application.Mcp;

public static class McpTransientFailureClassifier
{
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            TimeoutException or SocketException or IndexGenerationStaleException => true,
            _ when IsConnectionReset(exception) => true,
            _ when IsTransientIndexIoFailure(exception) => true,
            _ when IsTransientSqlException(exception) => true,
            _ when exception.InnerException is not null => IsTransient(exception.InnerException),
            _ => false
        };
    }

    private static bool IsConnectionReset(Exception exception) =>
        string.Equals(
            exception.GetType().FullName,
            "Microsoft.AspNetCore.Connections.ConnectionResetException",
            StringComparison.Ordinal);

    private static bool IsTransientIndexIoFailure(Exception exception) =>
        exception is IOException && (exception.HResult & 0xFFFF) is 32 or 33;

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
