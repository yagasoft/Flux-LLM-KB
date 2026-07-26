using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using Microsoft.Data.SqlClient;

namespace FluxKnowledge.Cli.Commands;

public static class ProvisionSqlCommand
{
    public static async Task<int> ExecuteAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!args.Contains("--confirm-provision", StringComparer.Ordinal))
        {
            await error.WriteLineAsync(
                "Provisioning was not started: --confirm-provision is required.").ConfigureAwait(false);
            return 2;
        }

        var backupTarget = ReadArgument(args, "--backup-target");
        if (string.IsNullOrWhiteSpace(backupTarget))
        {
            await error.WriteLineAsync(
                "Provisioning was not started: --backup-target outside I: is required.").ConfigureAwait(false);
            return 2;
        }

        var administratorConnection =
            Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_SQL_ADMIN_CONNECTION") ?? string.Empty;
        var request = new SqlServerProvisioningRequest(
            administratorConnection,
            SqlServerOptions.ProductionDataFilePath,
            SqlServerOptions.ProductionLogFilePath,
            backupTarget,
            ConfirmProvision: true);

        try
        {
            var result = await new SqlServerProvisioner()
                .ProvisionAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Provisioned {result.CatalogName} at {result.DataFilePath} and {result.LogFilePath}.")
                .ConfigureAwait(false);
            foreach (var instruction in result.AclInstructions)
            {
                await output.WriteLineAsync(instruction).ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or SqlException)
        {
            await error.WriteLineAsync($"Provisioning failed: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
