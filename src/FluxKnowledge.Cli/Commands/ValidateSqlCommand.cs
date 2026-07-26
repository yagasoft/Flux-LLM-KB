using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using Microsoft.Data.SqlClient;

namespace FluxKnowledge.Cli.Commands;

public static class ValidateSqlCommand
{
    public static async Task<int> ExecuteAsync(
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var options = SqlServerOptions.ForProduction(
            Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge") ?? string.Empty,
            SqlServerOptions.ProductionDataFilePath,
            SqlServerOptions.ProductionLogFilePath);
        try
        {
            var result = await new SqlServerReadinessValidator()
                .ValidateAsync(options, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsReady)
            {
                await output.WriteLineAsync("FluxKnowledge SQL Server is ready.").ConfigureAwait(false);
                return 0;
            }

            foreach (var failure in result.Failures)
            {
                await error.WriteLineAsync(failure).ConfigureAwait(false);
            }

            return 1;
        }
        catch (SqlException exception)
        {
            await error.WriteLineAsync($"SQL Server readiness validation failed: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }
}
