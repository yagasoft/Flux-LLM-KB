using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace FluxKnowledge.Infrastructure.SqlServer.Configuration;

public static class SqlServerOptionsValidator
{
    public static SqlServerOptionsValidationResult Validate(SqlServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (!TryParseStandardApplicationConnection(options.ConnectionString, out var connection, out var parseFailure))
        {
            failures.Add(parseFailure);
        }
        else
        {
            if (!string.Equals(connection.InitialCatalog, SqlServerOptions.CatalogName, StringComparison.Ordinal))
            {
                failures.Add(
                    $"The application connection must use Initial Catalog={SqlServerOptions.CatalogName}.");
            }

            if (!string.IsNullOrEmpty(connection.AttachDBFilename) || connection.UserInstance)
            {
                failures.Add("User-owned database attachment and user instances are not permitted.");
            }
        }

        if (!string.Equals(
                options.DataFilePath,
                SqlServerOptions.ProductionDataFilePath,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"The SQL-owned data file path must be {SqlServerOptions.ProductionDataFilePath}.");
        }

        if (!string.Equals(
                options.LogFilePath,
                SqlServerOptions.ProductionLogFilePath,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"The SQL-owned log file path must be {SqlServerOptions.ProductionLogFilePath}.");
        }

        return new SqlServerOptionsValidationResult(failures);
    }

    public static void ThrowIfInvalid(SqlServerOptions options)
    {
        var result = Validate(options);
        if (!result.IsValid)
        {
            throw new OptionsValidationException(
                SqlServerOptions.SectionName,
                typeof(SqlServerOptions),
                result.Failures);
        }
    }

    private static bool TryParseStandardApplicationConnection(
        string connectionString,
        out SqlConnectionStringBuilder parsed,
        out string failure)
    {
        parsed = new SqlConnectionStringBuilder();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            failure = "ConnectionStrings:FluxKnowledge is required.";
            return false;
        }

        try
        {
            parsed.ConnectionString = connectionString;
        }
        catch (ArgumentException exception)
        {
            failure = $"The SQL Server connection string is invalid: {exception.Message}";
            return false;
        }

        failure = string.Empty;
        return true;
    }
}

public sealed class SqlServerOptionsValidationResult
{
    public SqlServerOptionsValidationResult(IReadOnlyList<string> failures)
    {
        Failures = failures;
    }

    public IReadOnlyList<string> Failures { get; }

    public bool IsValid => Failures.Count == 0;
}
