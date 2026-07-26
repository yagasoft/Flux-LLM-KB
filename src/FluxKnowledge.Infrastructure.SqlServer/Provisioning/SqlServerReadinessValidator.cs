using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using Microsoft.Data.SqlClient;

namespace FluxKnowledge.Infrastructure.SqlServer.Provisioning;

public sealed class SqlServerReadinessValidator
{
    private const string InitialMigrationSuffix = "_InitialPhase1";

    public string BuildValidationSql() =>
        """
        SELECT
            CAST(DB_NAME() AS nvarchar(128)) AS [CatalogName],
            CONVERT(int, SERVERPROPERTY('IsFullTextInstalled')) AS [IsFullTextInstalled],
            CONVERT(bit, CASE WHEN EXISTS (
                SELECT 1
                FROM sys.fulltext_catalogs
                WHERE [name] = N'FluxKnowledge'
            ) THEN 1 ELSE 0 END) AS [HasFullTextCatalog],
            CONVERT(bit, CASE WHEN EXISTS (
                SELECT 1
                FROM sys.fulltext_indexes
                WHERE [object_id] = OBJECT_ID(N'[dbo].[Artifacts]')
            ) THEN 1 ELSE 0 END) AS [HasArtifactFullTextIndex],
            CONVERT(bit, CASE WHEN OBJECT_ID(N'[dbo].[__EFMigrationsHistory]', N'U') IS NULL
                THEN 0 ELSE 1 END) AS [HasMigrationHistory],
            (SELECT TOP (1) [physical_name]
                FROM sys.database_files
                WHERE [type_desc] = N'ROWS'
                ORDER BY [file_id]) AS [DataFilePath],
            (SELECT TOP (1) [physical_name]
                FROM sys.database_files
                WHERE [type_desc] = N'LOG'
                ORDER BY [file_id]) AS [LogFilePath];
        """;

    public async Task<SqlServerReadinessResult> ValidateAsync(
        SqlServerOptions options,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        var optionValidation = SqlServerOptionsValidator.Validate(options);
        failures.AddRange(optionValidation.Failures);
        if (failures.Count > 0)
        {
            return new SqlServerReadinessResult(false, failures);
        }

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(BuildValidationSql(), connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SqlServerReadinessResult(false, ["SQL Server returned no readiness state."]);
        }

        var catalogName = reader.GetString(0);
        var fullTextInstalled = reader.GetInt32(1) == 1;
        var hasFullTextCatalog = reader.GetBoolean(2);
        var hasArtifactFullTextIndex = reader.GetBoolean(3);
        var hasMigrationHistory = reader.GetBoolean(4);
        var dataFilePath = reader.IsDBNull(5) ? null : reader.GetString(5);
        var logFilePath = reader.IsDBNull(6) ? null : reader.GetString(6);
        await reader.CloseAsync().ConfigureAwait(false);

        if (!string.Equals(catalogName, SqlServerOptions.CatalogName, StringComparison.Ordinal))
        {
            failures.Add($"Connected catalog is {catalogName}, not {SqlServerOptions.CatalogName}.");
        }

        if (!fullTextInstalled)
        {
            failures.Add("SQL Server Full-Text is not installed.");
        }
        else if (!hasFullTextCatalog || !hasArtifactFullTextIndex)
        {
            failures.Add("The FluxKnowledge SQL Full-Text catalog and Artifacts index are not ready.");
        }

        if (!PathsMatch(dataFilePath, options.DataFilePath))
        {
            failures.Add($"The SQL data file is not at {options.DataFilePath}.");
        }

        if (!PathsMatch(logFilePath, options.LogFilePath))
        {
            failures.Add($"The SQL log file is not at {options.LogFilePath}.");
        }

        if (!hasMigrationHistory ||
            !await HasInitialMigrationAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            failures.Add("The InitialPhase1 schema migration has not been applied.");
        }

        return new SqlServerReadinessResult(failures.Count == 0, failures);
    }

    private static async Task<bool> HasInitialMigrationAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT CONVERT(int, CASE WHEN EXISTS (
                SELECT 1
                FROM [dbo].[__EFMigrationsHistory]
                WHERE [MigrationId] LIKE N'%' + @suffix
            ) THEN 1 ELSE 0 END);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@suffix", InitialMigrationSuffix);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static bool PathsMatch(string? actual, string expected) =>
        actual is not null &&
        string.Equals(
            actual.Replace('\\', '/'),
            expected.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
}

public sealed record SqlServerReadinessResult(bool IsReady, IReadOnlyList<string> Failures);
