using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

public sealed class NativeSqlServerFixture : IAsyncLifetime
{
    public const string ConnectionEnvironmentVariable = "FLUXKNOWLEDGE_TEST_SQL_CONNECTION";
    private const string TestCatalogPrefix = "FluxKnowledge_Phase1Tests_";

    private string? _serverConnectionString;
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
        _serverConnectionString = await ResolveDisposableServerConnectionStringAsync().ConfigureAwait(false);

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

    internal async Task<PreviousMigrationDatabase> CreatePreviousMigrationDatabaseAsync()
        => await CreateMigrationDatabaseAsync("20260726221653_EnforceCanonicalSqlSafety").ConfigureAwait(false);

    internal async Task<PreviousMigrationDatabase> CreateSchedulerPreviousMigrationDatabaseAsync()
        => await CreateMigrationDatabaseAsync("20260727055755_DistinguishVectorIdentityAndPayloadChecksum").ConfigureAwait(false);

    internal async Task<PreviousMigrationDatabase> CreateGpuSchedulerFencePreviousMigrationDatabaseAsync()
        => await CreateMigrationDatabaseAsync("20260729120305_AddGpuSchedulerOperationReceiptRequestFingerprint").ConfigureAwait(false);

    internal async Task<PreviousMigrationDatabase> CreateGpuSchedulerReceiptPreviousMigrationDatabaseAsync()
        => await CreateMigrationDatabaseAsync("20260729094809_AddGpuSchedulerOperationReceipts").ConfigureAwait(false);

    internal async Task<PreviousMigrationDatabase> CreateGpuSchedulerOpaqueKeyPreviousMigrationDatabaseAsync()
        => await CreateMigrationDatabaseAsync("20260802182703_AddGpuSchedulerBinaryFenceCollation").ConfigureAwait(false);

    internal async Task<PreviousMigrationDatabase> CreateIdentitylessOutlookExportPreviousMigrationDatabaseAsync()
        => await CreateMigrationDatabaseAsync("20260811143122_RecordOutlookExportBlockedReason").ConfigureAwait(false);

    internal async Task<PreviousMigrationDatabase> CreateRetainedProcessorForceRequestPreviousMigrationDatabaseAsync()
        => await CreateMigrationDatabaseAsync("20260813125157_AddRetainedProcessorBranchMemberChildForeignKeys").ConfigureAwait(false);

    internal async Task<PreviousMigrationDatabase> CreateOperatorActionCapabilityPreviousMigrationDatabaseAsync()
        => await CreateMigrationDatabaseAsync("20260814144818_AddSourceProcessorForceRequests").ConfigureAwait(false);

    internal async Task<PreviousMigrationDatabase> CreateRetainedCsharpPreviousMigrationDatabaseAsync()
        => await CreateMigrationDatabaseAsync("20260814170852_EnforceOperatorActionRequestPolicies").ConfigureAwait(false);

    internal async Task<PreviousMigrationDatabase> CreateRetainedCsharpLifecyclePreviousMigrationDatabaseAsync()
        => await CreateMigrationDatabaseAsync("20260820070404_HardenRetainedCsharpLifecycle").ConfigureAwait(false);

    private async Task<PreviousMigrationDatabase> CreateMigrationDatabaseAsync(string targetMigration)
    {
        if (string.IsNullOrWhiteSpace(_serverConnectionString))
        {
            throw new InvalidOperationException(
                $"{ConnectionEnvironmentVariable} is not configured.");
        }

        ValidateServerConnectionString(_serverConnectionString);
        var database = new PreviousMigrationDatabase(
            _serverConnectionString,
            $"{TestCatalogPrefix}{Guid.NewGuid():N}",
            targetMigration);
        await database.InitializeAsync().ConfigureAwait(false);
        return database;
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

            ValidateLoopbackServer(parsed.DataSource);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "The native SQL test connection is not a valid server-level connection string.",
                exception);
        }
    }

    private static void ValidateLoopbackServer(string dataSource)
    {
        var host = dataSource.Trim();
        if (host.StartsWith("(localdb)\\", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(host, "(localdb)\\MSSQLLocalDB", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Native SQL tests allow only the (localdb)\\MSSQLLocalDB LocalDB instance.");
            }

            return;
        }

        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = host.IndexOf(']');
            if (closingBracket <= 1)
            {
                throw new InvalidOperationException("Native SQL tests require a resolvable loopback-only SQL Server target.");
            }

            host = host[1..closingBracket];
        }
        else
        {
            var separator = host.LastIndexOf(',');
            if (separator >= 0)
            {
                host = host[..separator];
            }
        }

        if (IPAddress.TryParse(host, out var address))
        {
            if (IPAddress.IsLoopback(address))
            {
                return;
            }

            throw new InvalidOperationException("Native SQL tests require a loopback-only SQL Server target.");
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            throw new InvalidOperationException("Native SQL tests require a resolvable loopback-only SQL Server target.", exception);
        }

        if (addresses.Length == 0 || addresses.Any(address => !IPAddress.IsLoopback(address)))
        {
            throw new InvalidOperationException("Native SQL tests require a loopback-only SQL Server target.");
        }
    }

    private static async Task<string> ResolveDisposableServerConnectionStringAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        var repositoryRoot = FindRepositoryRoot();
        var script = Path.Combine(repositoryRoot, "scripts", "dev", "ensure-disposable-sql.ps1");
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            start.ArgumentList.Add("-ServerConnectionString");
            start.ArgumentList.Add(configured);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the disposable SQL prerequisite helper.");
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Disposable SQL prerequisite helper failed: {error.Trim()}");
        }

        var connectionString = output.Trim();
        ValidateServerConnectionString(connectionString);
        return connectionString;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FluxKnowledge.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root for the disposable SQL prerequisite helper.");
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

    internal sealed class PreviousMigrationDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;
        private readonly string _databaseName;
        private readonly string _targetMigration;
        private bool _created;

        internal PreviousMigrationDatabase(
            string serverConnectionString,
            string databaseName,
            string targetMigration)
        {
            ValidateServerConnectionString(serverConnectionString);
            ValidateGeneratedCatalog(databaseName);
            _serverConnectionString = serverConnectionString;
            _databaseName = databaseName;
            _targetMigration = targetMigration;
        }

        internal string ConnectionString => new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = _databaseName,
            AttachDBFilename = string.Empty,
            UserInstance = false
        }.ConnectionString;

        internal FluxKnowledgeDbContext CreateContext() => new(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(ConnectionString)
                .Options);

        internal async Task InitializeAsync()
        {
            await using var serverConnection = new SqlConnection(_serverConnectionString);
            await serverConnection.OpenAsync().ConfigureAwait(false);
            await EnsureSafeServerDefaultsAsync(serverConnection).ConfigureAwait(false);
            try
            {
                _created = true;
                await using (var create = new SqlCommand(
                                 $"CREATE DATABASE [{_databaseName}];",
                                 serverConnection))
                {
                    await create.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await VerifyCreatedDatabaseFilesAsync(serverConnection).ConfigureAwait(false);
                await using var context = CreateContext();
                await context.GetService<IMigrator>()
                    .MigrateAsync(_targetMigration)
                    .ConfigureAwait(false);
            }
            catch
            {
                await DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_created)
            {
                return;
            }

            ValidateGeneratedCatalog(_databaseName);
            SqlConnection.ClearAllPools();
            await using var serverConnection = new SqlConnection(_serverConnectionString);
            await serverConnection.OpenAsync().ConfigureAwait(false);
            await using var drop = new SqlCommand(
                $"""
                 IF DB_ID(N'{_databaseName}') IS NOT NULL
                 BEGIN
                     ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                     DROP DATABASE [{_databaseName}];
                 END;
                 """,
                serverConnection);
            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
            _created = false;
        }

        private async Task VerifyCreatedDatabaseFilesAsync(SqlConnection connection)
        {
            ValidateGeneratedCatalog(_databaseName);
            const string sql =
                """
                SELECT [physical_name]
                FROM sys.master_files
                WHERE [database_id] = DB_ID(@databaseName)
                ORDER BY [file_id];
                """;
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@databaseName", _databaseName);
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            var filePaths = new List<string?>();
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                filePaths.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            }

            ValidateCreatedDatabaseFiles(filePaths);
        }
    }
}
