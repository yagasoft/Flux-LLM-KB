using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Provisioning;

public interface ISqlServerReadinessValidator
{
    Task<SqlServerReadinessResult> ValidateAsync(
        SqlServerOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class SqlServerReadinessValidator : ISqlServerReadinessValidator
{
    public string BuildValidationSql() =>
        """
        SELECT
            CAST(DB_NAME() AS nvarchar(128)) AS [CatalogName],
            CONVERT(int, SERVERPROPERTY('IsFullTextInstalled')) AS [IsFullTextInstalled],
            CONVERT(bit, CASE WHEN
                (
                    SELECT COUNT_BIG(*)
                    FROM sys.fulltext_index_columns
                    WHERE [object_id] = OBJECT_ID(N'[dbo].[Artifacts]')
                ) = 1
                AND EXISTS (
                    SELECT 1
                    FROM sys.fulltext_indexes AS [index]
                    INNER JOIN sys.fulltext_catalogs AS [catalog]
                        ON [catalog].[fulltext_catalog_id] = [index].[fulltext_catalog_id]
                    INNER JOIN sys.fulltext_index_columns AS [indexColumn]
                        ON [indexColumn].[object_id] = [index].[object_id]
                    INNER JOIN sys.columns AS [column]
                        ON [column].[object_id] = [indexColumn].[object_id]
                        AND [column].[column_id] = [indexColumn].[column_id]
                    WHERE [index].[object_id] = OBJECT_ID(N'[dbo].[Artifacts]')
                        AND [catalog].[name] = N'FluxKnowledge'
                        AND [column].[name] = N'SearchText'
                )
            THEN 1 ELSE 0 END) AS [HasExpectedArtifactSearchTextFullTextIndex],
            CONVERT(bit, CASE WHEN
                EXISTS (
                    SELECT 1
                    FROM [dbo].[IndexState] AS [state]
                    INNER JOIN [dbo].[IndexGenerations] AS [generation]
                        ON [generation].[Id] = [state].[ActiveIndexGenerationId]
                    WHERE [state].[Id] = 1
                        AND [state].[EmptyCatalogueValidatedAtUtc] IS NULL
                        AND [generation].[ValidatedAtUtc] IS NOT NULL
                        AND LEN([generation].[IndexPath]) > 0
                )
                OR EXISTS (
                    SELECT 1
                    FROM [dbo].[IndexState] AS [state]
                    WHERE [state].[Id] = 1
                        AND [state].[ActiveIndexGenerationId] IS NULL
                        AND [state].[EmptyCatalogueValidatedAtUtc] IS NOT NULL
                        AND NOT EXISTS (SELECT 1 FROM [dbo].[Vectors])
                        AND NOT EXISTS (SELECT 1 FROM [dbo].[IndexGenerations])
                        AND NOT EXISTS (SELECT 1 FROM [dbo].[IndexGenerationVectors])
                )
            THEN 1 ELSE 0 END) AS [HasValidatedActiveIndex];

        SELECT [type_desc], [physical_name]
        FROM sys.database_files
        ORDER BY [file_id];
        """;

    public string BuildIndexStateValidationSql() =>
        """
        SELECT CONVERT(bit, CASE WHEN
            EXISTS (
                SELECT 1
                FROM [dbo].[IndexState] AS [state]
                INNER JOIN [dbo].[IndexGenerations] AS [generation]
                    ON [generation].[Id] = [state].[ActiveIndexGenerationId]
                WHERE [state].[Id] = 1
                    AND [state].[EmptyCatalogueValidatedAtUtc] IS NULL
                    AND [generation].[ValidatedAtUtc] IS NOT NULL
                    AND LEN([generation].[IndexPath]) > 0
            )
            OR EXISTS (
                SELECT 1
                FROM [dbo].[IndexState] AS [state]
                WHERE [state].[Id] = 1
                    AND [state].[ActiveIndexGenerationId] IS NULL
                    AND [state].[EmptyCatalogueValidatedAtUtc] IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM [dbo].[Vectors])
                    AND NOT EXISTS (SELECT 1 FROM [dbo].[IndexGenerations])
                    AND NOT EXISTS (SELECT 1 FROM [dbo].[IndexGenerationVectors])
            )
        THEN 1 ELSE 0 END) AS [HasValidatedIndexState];
        """;

    public async Task<SqlServerReadinessResult> ValidateAsync(
        SqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new SqlCommand(BuildIndexStateValidationSql(), connection);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not bool isReady || !isReady)
        {
            return new SqlServerReadinessResult(
                false,
                ["IndexState must point to a validated active index generation or a validated empty catalogue."]);
        }

        return new SqlServerReadinessResult(true, []);
    }

    public async Task<SqlServerReadinessResult> ValidateAsync(
        SqlServerOptions options,
        CancellationToken cancellationToken = default)
    {
        var optionValidation = SqlServerOptionsValidator.Validate(options);
        if (!optionValidation.IsValid)
        {
            return new SqlServerReadinessResult(false, optionValidation.Failures);
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
        var hasExpectedFullTextIndex = reader.GetBoolean(2);
        var hasValidatedActiveIndex = reader.GetBoolean(3);

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SqlServerReadinessResult(false, ["SQL Server returned no database file state."]);
        }

        var databaseFiles = new List<SqlServerDatabaseFileSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                return new SqlServerReadinessResult(
                    false,
                    ["SQL Server returned an unverifiable database file row."]);
            }

            databaseFiles.Add(new SqlServerDatabaseFileSnapshot(reader.GetString(0), reader.GetString(1)));
        }

        await reader.CloseAsync().ConfigureAwait(false);

        var contextOptions = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection)
            .Options;
        await using var context = new FluxKnowledgeDbContext(contextOptions);
        var expectedMigrations = context.Database.GetMigrations().ToArray();
        var appliedMigrations = (
            await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .ToArray();

        return Evaluate(
            options,
            new SqlServerReadinessSnapshot(
                catalogName,
                fullTextInstalled,
                hasExpectedFullTextIndex,
                hasValidatedActiveIndex,
                databaseFiles,
                expectedMigrations,
                appliedMigrations));
    }

    public static SqlServerReadinessResult Evaluate(
        SqlServerOptions options,
        SqlServerReadinessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snapshot);

        var failures = new List<string>();
        failures.AddRange(SqlServerOptionsValidator.Validate(options).Failures);

        if (!string.Equals(snapshot.CatalogName, SqlServerOptions.CatalogName, StringComparison.Ordinal))
        {
            failures.Add(
                $"Connected catalog is {snapshot.CatalogName}, not {SqlServerOptions.CatalogName}.");
        }

        if (!snapshot.IsFullTextInstalled)
        {
            failures.Add("SQL Server Full-Text is not installed.");
        }
        else if (!snapshot.HasExpectedArtifactSearchTextFullTextIndex)
        {
            failures.Add(
                "The FluxKnowledge Full-Text catalogue must index only Artifacts.SearchText.");
        }

        if (!snapshot.HasValidatedActiveIndex)
        {
            failures.Add(
                "IndexState must point to a validated active index generation.");
        }

        var dataFiles = snapshot.DatabaseFiles
            .Where(file => string.Equals(file.TypeDescription, "ROWS", StringComparison.Ordinal))
            .ToArray();
        var logFiles = snapshot.DatabaseFiles
            .Where(file => string.Equals(file.TypeDescription, "LOG", StringComparison.Ordinal))
            .ToArray();
        if (snapshot.DatabaseFiles.Count != 2 || dataFiles.Length != 1 || logFiles.Length != 1)
        {
            failures.Add(
                "The FluxKnowledge catalog must have exactly one data file and one log file.");
        }

        if (dataFiles.Length != 1 || !PathsMatch(dataFiles[0].PhysicalName, options.DataFilePath))
        {
            failures.Add($"The SQL data file set is not exactly {options.DataFilePath}.");
        }

        if (logFiles.Length != 1 || !PathsMatch(logFiles[0].PhysicalName, options.LogFilePath))
        {
            failures.Add($"The SQL log file set is not exactly {options.LogFilePath}.");
        }

        if (snapshot.ExpectedMigrations.Count == 0 ||
            !snapshot.ExpectedMigrations.SequenceEqual(
                snapshot.AppliedMigrations,
                StringComparer.Ordinal))
        {
            failures.Add("The database does not have the exact current migration set.");
        }

        return new SqlServerReadinessResult(failures.Count == 0, failures);
    }

    private static bool PathsMatch(string actual, string expected) =>
        TryNormalisePath(actual, out var normalisedActual) &&
        TryNormalisePath(expected, out var normalisedExpected) &&
        string.Equals(normalisedActual, normalisedExpected, StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalisePath(string path, out string normalised)
    {
        normalised = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalised = Path.GetFullPath(path).Replace('/', '\\');
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public sealed record SqlServerDatabaseFileSnapshot(string TypeDescription, string PhysicalName);

public sealed record SqlServerReadinessSnapshot(
    string CatalogName,
    bool IsFullTextInstalled,
    bool HasExpectedArtifactSearchTextFullTextIndex,
    bool HasValidatedActiveIndex,
    IReadOnlyList<SqlServerDatabaseFileSnapshot> DatabaseFiles,
    IReadOnlyList<string> ExpectedMigrations,
    IReadOnlyList<string> AppliedMigrations);

public sealed record SqlServerReadinessResult(bool IsReady, IReadOnlyList<string> Failures);
