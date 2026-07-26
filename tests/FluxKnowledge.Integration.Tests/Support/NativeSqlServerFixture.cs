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
    private bool _databaseCreateAttempted;

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

        await using var serverConnection = new SqlConnection(_serverConnectionString);
        await serverConnection.OpenAsync().ConfigureAwait(false);
        await EnsureSafeServerDefaultsAsync(serverConnection).ConfigureAwait(false);

        await RunCreateSequenceAsync(
            async () =>
            {
                _databaseCreateAttempted = true;
                await using var create = new SqlCommand(
                    $"CREATE DATABASE [{DatabaseName}];",
                    serverConnection);
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
            },
            () => VerifyCreatedDatabaseFilesAsync(serverConnection),
            async () =>
            {
                var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                    .UseSqlServer(ConnectionString)
                    .Options;
                await using var context = new FluxKnowledgeDbContext(options);
                await context.Database.MigrateAsync().ConfigureAwait(false);
            },
            DropDatabaseAsync).ConfigureAwait(false);
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

    internal static async Task RunCreateSequenceAsync(
        Func<Task> createDatabase,
        Func<Task> verifyDatabaseFiles,
        Func<Task> applyMigration,
        Func<Task> cleanupDatabase)
    {
        try
        {
            await createDatabase().ConfigureAwait(false);
            await verifyDatabaseFiles().ConfigureAwait(false);
            await applyMigration().ConfigureAwait(false);
        }
        catch
        {
            await cleanupDatabase().ConfigureAwait(false);
            throw;
        }
    }

    internal static void ValidateCreatedDatabaseFiles(IReadOnlyList<string?> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count < 2)
        {
            throw new InvalidOperationException(
                "Every native SQL test database file must be verified outside I:.");
        }

        foreach (var filePath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(filePath) ||
                !Path.IsPathFullyQualified(filePath) ||
                !TryGetPathRoot(filePath, out var root) ||
                IsIFileSystemRoot(root))
            {
                throw new InvalidOperationException(
                    "Every native SQL test database file must be verified outside I:.");
            }
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
        if (!await reader.ReadAsync().ConfigureAwait(false) ||
            reader.IsDBNull(0) ||
            reader.IsDBNull(1) ||
            reader.IsDBNull(2))
        {
            throw new InvalidOperationException(
                "Native SQL tests could not verify server file defaults and Full-Text state.");
        }

        ValidateCreatedDatabaseFiles([reader.GetString(0), reader.GetString(1)]);

        if (reader.GetInt32(2) != 1)
        {
            throw new InvalidOperationException(
                "Native SQL migration tests require SQL Server Full-Text.");
        }
    }

    private async Task VerifyCreatedDatabaseFilesAsync(SqlConnection connection)
    {
        ValidateGeneratedCatalog(DatabaseName);
        const string sql =
            """
            SELECT [physical_name]
            FROM sys.master_files
            WHERE [database_id] = DB_ID(@databaseName)
            ORDER BY [file_id];
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@databaseName", DatabaseName);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var filePaths = new List<string?>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            filePaths.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        ValidateCreatedDatabaseFiles(filePaths);
    }

    private async Task DropDatabaseAsync()
    {
        if (!_databaseCreateAttempted || string.IsNullOrWhiteSpace(_serverConnectionString))
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
        _databaseCreateAttempted = false;
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

    private static bool TryGetPathRoot(string path, out string root)
    {
        root = string.Empty;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
            return root.Length > 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsIFileSystemRoot(string root)
    {
        var normalisedRoot = root.Replace('/', '\\');
        if (normalisedRoot.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            normalisedRoot.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            normalisedRoot = normalisedRoot[4..];
        }

        return string.Equals(normalisedRoot, "I:\\", StringComparison.OrdinalIgnoreCase);
    }
}
