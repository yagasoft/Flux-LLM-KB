using System.Data.Common;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

public sealed class NativeSqlServerFixture : IAsyncLifetime
{
    public const string ConnectionEnvironmentVariable = "FLUXKNOWLEDGE_TEST_SQL_CONNECTION";
    private const string TestCatalogPrefix = "FluxKnowledge_Phase1Tests_";

    private readonly string? _serverConnectionString =
        Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
    private bool _databaseCreated;

    public string DatabaseName { get; } = $"{TestCatalogPrefix}{Guid.NewGuid():N}";

    public string ConnectionString
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_serverConnectionString))
            {
                throw new InvalidOperationException(
                    $"{ConnectionEnvironmentVariable} is not configured.");
            }

            ValidateGeneratedCatalog(DatabaseName);
            return new SqlConnectionStringBuilder(_serverConnectionString)
            {
                InitialCatalog = DatabaseName,
                AttachDBFilename = string.Empty,
                UserInstance = false
            }.ConnectionString;
        }
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_serverConnectionString))
        {
            return;
        }

        ValidateServerConnectionString(_serverConnectionString);
        ValidateGeneratedCatalog(DatabaseName);

        try
        {
            await using var serverConnection = new SqlConnection(_serverConnectionString);
            await serverConnection.OpenAsync().ConfigureAwait(false);
            await EnsureSafeServerDefaultsAsync(serverConnection).ConfigureAwait(false);

            await using (var create = new SqlCommand(
                $"CREATE DATABASE [{DatabaseName}];",
                serverConnection))
            {
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
                _databaseCreated = true;
            }

            var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(ConnectionString)
                .Options;
            await using var context = new FluxKnowledgeDbContext(options);
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }
        catch
        {
            await DropDatabaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        await DropDatabaseAsync().ConfigureAwait(false);
    }

    public static void ValidateServerConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "A non-empty server-level disposable SQL Server connection string is required.");
        }

        try
        {
            var raw = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (string key in raw.Keys)
            {
                var normalised = key.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                if (normalised is "initialcatalog" or "database" or "attachdbfilename" or
                    "extendedproperties" or "initialfilename" or "userinstance" ||
                    normalised.Contains("attach", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Native SQL tests require a server-level connection without catalog, " +
                        "user-instance or file-attachment keys.");
                }
            }

            var parsed = new SqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(parsed.DataSource) ||
                !string.IsNullOrEmpty(parsed.InitialCatalog) ||
                !string.IsNullOrEmpty(parsed.AttachDBFilename) ||
                parsed.UserInstance)
            {
                throw new InvalidOperationException(
                    "Native SQL tests require a server-level connection without catalog, " +
                    "user-instance or file-attachment keys.");
            }
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "The native SQL test connection is not a valid server-level connection string.",
                exception);
        }
    }

    private static async Task EnsureSafeServerDefaultsAsync(SqlConnection connection)
    {
        const string sql =
            """
            SELECT
                CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath')),
                CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultLogPath')),
                CONVERT(int, SERVERPROPERTY('IsFullTextInstalled'));
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        await reader.ReadAsync().ConfigureAwait(false);

        var dataPath = reader.IsDBNull(0) ? null : reader.GetString(0);
        var logPath = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (IsIPath(dataPath) || IsIPath(logPath))
        {
            throw new InvalidOperationException(
                "Native SQL tests refuse a server whose default data or log path is on I:.");
        }

        if (reader.GetInt32(2) != 1)
        {
            throw new InvalidOperationException(
                "Native SQL migration tests require SQL Server Full-Text.");
        }
    }

    private async Task DropDatabaseAsync()
    {
        if (!_databaseCreated || string.IsNullOrWhiteSpace(_serverConnectionString))
        {
            return;
        }

        ValidateGeneratedCatalog(DatabaseName);
        SqlConnection.ClearAllPools();
        await using var serverConnection = new SqlConnection(_serverConnectionString);
        await serverConnection.OpenAsync().ConfigureAwait(false);
        await using var drop = new SqlCommand(
            $"""
             IF DB_ID(N'{DatabaseName}') IS NOT NULL
             BEGIN
                 ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                 DROP DATABASE [{DatabaseName}];
             END;
             """,
            serverConnection);
        await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
        _databaseCreated = false;
    }

    private static void ValidateGeneratedCatalog(string databaseName)
    {
        if (!databaseName.StartsWith(TestCatalogPrefix, StringComparison.Ordinal) ||
            databaseName.Length != TestCatalogPrefix.Length + 32 ||
            !Guid.TryParseExact(databaseName[TestCatalogPrefix.Length..], "N", out _))
        {
            throw new InvalidOperationException(
                "Native SQL tests may create or drop only a generated FluxKnowledge_Phase1Tests_<guid> catalog.");
        }
    }

    private static bool IsIPath(string? path) =>
        path is not null &&
        (path.StartsWith("I:\\", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("I:/", StringComparison.OrdinalIgnoreCase));
}
