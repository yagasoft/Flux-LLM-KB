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
    public const string FileRootEnvironmentVariable = "FLUXKNOWLEDGE_TEST_SQL_ROOT";
    private const string TestCatalogPrefix = "FluxKnowledge_Phase1Tests_";

    private string? _serverConnectionString;
    private string? _fileRoot;
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
        _fileRoot = ValidateTestFileRoot(Environment.GetEnvironmentVariable(FileRootEnvironmentVariable));
        if (!Directory.Exists(_fileRoot)) throw new InvalidOperationException("The owned disposable SQL file root is not provisioned.");

        ValidateServerConnectionString(_serverConnectionString);
        ValidateGeneratedCatalog(DatabaseName);

        await using var serverConnection = new SqlConnection(_serverConnectionString);
        await serverConnection.OpenAsync().ConfigureAwait(false);
        await EnsureSafeServerDefaultsAsync(serverConnection).ConfigureAwait(false);

        await RunCreateSequenceAsync(
            async () =>
            {
                await using var create = new SqlCommand(BuildCreateDatabaseSql(DatabaseName, _fileRoot), serverConnection);
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
                _databaseCreateAttempted = true;
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

    // Interrupts every connection in this test's catalogue, never another catalogue.
    // The restricted test principal deliberately has no server-wide KILL permission.
    internal async Task InterruptDatabaseSessionsAsync()
    {
        var sql = await CreateSessionInterruptionSqlAsync().ConfigureAwait(false);
        try
        {
            await ExecuteServerSqlAsync(sql).ConfigureAwait(false);
        }
        finally
        {
            // Use a fresh master connection even if the first batch lost its session.
            // A failed restoration is surfaced; never silently leave a fixture restricted.
            await ExecuteServerSqlAsync(BuildRestoreDatabaseSessionsSql(DatabaseName, _fileRoot!))
                .ConfigureAwait(false);
        }
    }

    internal async Task<string> CreateSessionInterruptionSqlAsync()
    {
        if (!_databaseCreateAttempted || _serverConnectionString is null || _fileRoot is null)
            throw new InvalidOperationException("Session interruption requires this fixture's acknowledged database.");
        await VerifyOwnedDatabaseAsync(_serverConnectionString, DatabaseName, _fileRoot).ConfigureAwait(false);
        return BuildSessionInterruptionSql(DatabaseName, _fileRoot);
    }

    internal string RestoreDatabaseSessionsSql => BuildRestoreDatabaseSessionsSql(DatabaseName, _fileRoot!);

    private async Task ExecuteServerSqlAsync(string sql)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(_serverConnectionString)
        { InitialCatalog = "master", Pooling = false, ConnectTimeout = 5 }.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    internal static string BuildSessionInterruptionSql(string name, string root) =>
        BuildSessionControlGuardSql(name, root) + $"""

        ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
        ALTER DATABASE [{name}] SET MULTI_USER WITH ROLLBACK IMMEDIATE;
        """;

    private static string BuildRestoreDatabaseSessionsSql(string name, string root) =>
        BuildSessionControlGuardSql(name, root) + $"""

        ALTER DATABASE [{name}] SET MULTI_USER WITH ROLLBACK IMMEDIATE;
        """;

    private static string BuildSessionControlGuardSql(string name, string root)
    {
        ValidateGeneratedCatalog(name);
        root = ValidateTestFileRoot(root);
        return $"""
            IF DB_NAME() <> N'master'
                OR NOT EXISTS (SELECT 1 FROM sys.databases WHERE [name]=N'{name}' AND owner_sid=SUSER_SID())
                OR (SELECT COUNT(*) FROM sys.master_files WHERE database_id=DB_ID(N'{name}')) <> 2
                OR NOT EXISTS (SELECT 1 FROM sys.master_files WHERE database_id=DB_ID(N'{name}') AND [type]=0 AND physical_name=N'{Path.Combine(root, name + ".mdf")}')
                OR NOT EXISTS (SELECT 1 FROM sys.master_files WHERE database_id=DB_ID(N'{name}') AND [type]=1 AND physical_name=N'{Path.Combine(root, name + "_log.ldf")}')
                THROW 51000, 'Disposable session-control ownership or exact file manifest is unverified.', 1;
            """;
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

    internal async Task<PreviousMigrationDatabase> CreateDocumentOcrPreviousMigrationDatabaseAsync()
        => await CreateMigrationDatabaseAsync("20260918193207_AddDocumentPublicationSelector").ConfigureAwait(false);

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
            targetMigration, _fileRoot!);
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

    private static Task<string> ResolveDisposableServerConnectionStringAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        return Task.FromResult(ValidateRestrictedTestConnection(configured));
    }

    internal static string ValidateRestrictedTestConnection(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"Explicit restricted staging test credentials are required in {ConnectionEnvironmentVariable}; no Windows/admin fallback is permitted.");
        ValidateServerConnectionString(configured);
        var parsed = new SqlConnectionStringBuilder(configured);
        if (parsed.IntegratedSecurity || string.IsNullOrWhiteSpace(parsed.UserID) || string.IsNullOrWhiteSpace(parsed.Password) ||
            !string.IsNullOrEmpty(parsed.FailoverPartner) || parsed.MultiSubnetFailover || parsed.ApplicationIntent != ApplicationIntent.ReadWrite)
            throw new InvalidOperationException("Disposable SQL requires a restricted SQL test principal without alternate routing.");
        parsed.ConnectRetryCount = 0;
        parsed.ConnectTimeout = 5;
        return parsed.ConnectionString;
    }

    internal static string ValidateTestFileRoot(string? root)
    {
        const string stageBase = @"E:\Codex Workspaces\Staging\FluxKnowledge";
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException($"{FileRootEnvironmentVariable} must name the provisioned disposable SQL file root.");
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var deployment = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(canonical)))!;
        if (!string.Equals(root, canonical, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(deployment), stageBase, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(deployment), @"\ASqlTests-[A-Za-z0-9_-]{1,48}\z") ||
            !string.Equals(canonical, Path.Combine(deployment, "Data", "Sql", "Disposable"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Disposable SQL files must stay within an explicit owned SqlTests staging deployment.");
        for (var path = canonical; path is not null; path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Disposable SQL storage cannot traverse reparse points.");
        return canonical;
    }

    internal static string BuildCreateDatabaseSql(string name, string root)
    {
        ValidateGeneratedCatalog(name);
        root = ValidateTestFileRoot(root);
        return $"CREATE DATABASE [{name}] ON PRIMARY (NAME=N'{name}', FILENAME=N'{Path.Combine(root, name + ".mdf")}') LOG ON (NAME=N'{name}_log', FILENAME=N'{Path.Combine(root, name + "_log.ldf")}');";
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
            SELECT CONVERT(int, SERVERPROPERTY('IsFullTextInstalled')),
                IS_SRVROLEMEMBER('sysadmin'), IS_SRVROLEMEMBER('dbcreator'),
                HAS_PERMS_BY_NAME(NULL,NULL,'CONTROL SERVER'),
                HAS_PERMS_BY_NAME(NULL,NULL,'ALTER ANY DATABASE'), HAS_DBACCESS('FluxKnowledge'),
                HAS_PERMS_BY_NAME(NULL,NULL,'CREATE ANY DATABASE');
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false) ||
            reader.IsDBNull(0) || reader.GetInt32(0) != 1 ||
            Enumerable.Range(1, 5).Any(index => reader.IsDBNull(index) || reader.GetInt32(index) != 0) ||
            reader.IsDBNull(6) || reader.GetInt32(6) != 1)
        {
            throw new InvalidOperationException(
                "Native SQL tests require Full-Text and an explicit create-only test principal without production access or server administration.");
        }
    }

    private async Task VerifyCreatedDatabaseFilesAsync(SqlConnection connection)
        => await VerifyOwnedDatabaseAsync(_serverConnectionString!, DatabaseName, _fileRoot!).ConfigureAwait(false);

    private static async Task VerifyOwnedDatabaseAsync(string serverConnectionString, string name, string root)
    {
        ValidateGeneratedCatalog(name);
        root = ValidateTestFileRoot(root);
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = name }.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        const string sql =
            """
            SELECT DB_NAME(), CONVERT(bit, CASE WHEN owner_sid=SUSER_SID() THEN 1 ELSE 0 END)
            FROM sys.databases WHERE database_id=DB_ID();
            SELECT [physical_name]
            FROM sys.database_files
            ORDER BY [file_id];
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false) || reader.GetString(0) != name || !reader.GetBoolean(1))
            throw new InvalidOperationException("Disposable database ownership is unverified; preserve it.");
        if (!await reader.NextResultAsync().ConfigureAwait(false)) throw new InvalidOperationException("Disposable database files are unverified.");
        var filePaths = new List<string?>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            filePaths.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        ValidateOwnedDatabaseFiles(name, root, filePaths);
    }

    internal static void ValidateOwnedDatabaseFiles(string name, string root, IReadOnlyList<string?> paths)
    {
        ValidateGeneratedCatalog(name);
        root = ValidateTestFileRoot(root);
        ValidateCreatedDatabaseFiles(paths);
        var expected = new[] { Path.Combine(root, name + ".mdf"), Path.Combine(root, name + "_log.ldf") };
        if (paths.Count != 2 || !paths.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(expected.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Disposable database files do not match this run's exact owned manifest.");
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
        await VerifyOwnedDatabaseAsync(_serverConnectionString, DatabaseName, _fileRoot!).ConfigureAwait(false);
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
        private readonly string _fileRoot;
        private bool _created;
        internal string DatabaseName => _databaseName;

        internal PreviousMigrationDatabase(
            string serverConnectionString,
            string databaseName,
            string targetMigration, string fileRoot)
        {
            ValidateServerConnectionString(serverConnectionString);
            ValidateGeneratedCatalog(databaseName);
            _serverConnectionString = serverConnectionString;
            _databaseName = databaseName;
            _targetMigration = targetMigration;
            _fileRoot = ValidateTestFileRoot(fileRoot);
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
                await using (var create = new SqlCommand(
                                 BuildCreateDatabaseSql(_databaseName, _fileRoot),
                                 serverConnection))
                {
                    await create.ExecuteNonQueryAsync().ConfigureAwait(false);
                    _created = true;
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
            await VerifyOwnedDatabaseAsync(_serverConnectionString, _databaseName, _fileRoot).ConfigureAwait(false);
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
            => await VerifyOwnedDatabaseAsync(_serverConnectionString, _databaseName, _fileRoot).ConfigureAwait(false);
    }
}
