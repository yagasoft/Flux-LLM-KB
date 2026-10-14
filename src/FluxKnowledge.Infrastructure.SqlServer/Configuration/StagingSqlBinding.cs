using System.Text.RegularExpressions;
using FluxKnowledge.Application.Operations;
using Microsoft.Data.SqlClient;

namespace FluxKnowledge.Infrastructure.SqlServer.Configuration;

// Only the separate staging composition can construct this binding. Production validation
// continues to require its canonical catalogue and files.
internal sealed class StagingSqlBinding
{
    internal StagingSqlBinding(LiveRootLayout layout, string connectionString)
    {
        var connection = new SqlConnectionStringBuilder(connectionString);
        if (layout.IsProduction ||
            !Regex.IsMatch(connection.InitialCatalog, @"\AFluxKnowledge_Staging_[A-Za-z0-9_]{1,64}\z") ||
            !string.Equals(connection.DataSource, "localhost", StringComparison.OrdinalIgnoreCase) ||
            connection.IntegratedSecurity || string.IsNullOrWhiteSpace(connection.UserID) ||
            string.IsNullOrWhiteSpace(connection.Password) ||
            !string.IsNullOrEmpty(connection.AttachDBFilename) || connection.UserInstance ||
            !string.IsNullOrEmpty(connection.FailoverPartner) || connection.MultiSubnetFailover ||
            connection.ApplicationIntent != ApplicationIntent.ReadWrite)
            throw new ArgumentException("Staging requires an explicit local staging catalogue and restricted SQL login without alternate routing.");
        connection.ConnectRetryCount = 0;
        connection.ConnectTimeout = 5;
        connection.ApplicationName = "FluxKnowledge.StagingHost";
        CatalogName = connection.InitialCatalog;
        Options = new SqlServerOptions
        {
            ConnectionString = connection.ConnectionString,
            DataFilePath = layout.SqlDataFilePath,
            LogFilePath = layout.SqlLogFilePath
        };
    }

    internal string CatalogName { get; }
    internal SqlServerOptions Options { get; }

    internal SqlServerOptionsValidationResult Validate(SqlServerOptions options) => new(
        options.ConnectionString == Options.ConnectionString &&
        options.DataFilePath == Options.DataFilePath && options.LogFilePath == Options.LogFilePath
            ? [] : ["Staging SQL options do not match the validated binding."]);
}
