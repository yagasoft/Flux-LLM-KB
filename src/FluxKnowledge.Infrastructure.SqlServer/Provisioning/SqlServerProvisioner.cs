using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Provisioning;

public sealed class SqlServerProvisioner
{
    private readonly LiveRootLayout _layout;
    private readonly ILiveRootPathInspector _pathInspector;
    private readonly ISqlProvisioningFileSystem _fileSystem;
    private readonly ISqlProvisioningDatabase _database;

    private static readonly string CreateDatabaseSql =
        $$"""
        CREATE DATABASE [FluxKnowledge]
        ON PRIMARY
        (
            NAME = N'FluxKnowledge',
            FILENAME = N'{{SqlServerOptions.ProductionDataFilePath}}'
        )
        LOG ON
        (
            NAME = N'FluxKnowledge_log',
            FILENAME = N'{{SqlServerOptions.ProductionLogFilePath}}'
        );
        """;

    public static SqlServerProvisioner CreateForClaimedGoLive(
        NativeGoLiveProvisioningCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        capability.EnsureClaimed();

        return new SqlServerProvisioner(
            LiveRootLayout.Production,
            FileSystemLiveRootPathInspector.Instance,
            new SqlProvisioningFileSystem(),
            new SqlProvisioningDatabase());
    }

    private SqlServerProvisioner(
        LiveRootLayout layout,
        ILiveRootPathInspector pathInspector,
        ISqlProvisioningFileSystem fileSystem,
        ISqlProvisioningDatabase database)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(pathInspector);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(database);
        if (!ReferenceEquals(layout, LiveRootLayout.Production))
        {
            throw new ArgumentException("SQL provisioning requires the production live-root layout.", nameof(layout));
        }

        _layout = layout;
        _pathInspector = pathInspector;
        _fileSystem = fileSystem;
        _database = database;
    }

    public async Task<SqlServerProvisioningResult> ProvisionAsync(
        SqlServerProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateAndCanonicalise(request);
        if (validation.Failures.Count > 0)
        {
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, validation.Failures));
        }
        ValidatePathStateBeforeIo(request);
        _fileSystem.CreateDirectory(Path.GetDirectoryName(request.DataFilePath)!);
        ValidatePathStateBeforeIo(request);
        _fileSystem.CreateDirectory(Path.GetDirectoryName(request.LogFilePath)!);
        ValidatePathStateBeforeIo(request);
        await _database.ProvisionAsync(request.AdministratorConnectionString, cancellationToken).ConfigureAwait(false);

        return new SqlServerProvisioningResult(
            SqlServerOptions.CatalogName,
            request.DataFilePath,
            request.LogFilePath,
            [
                $"Grant the SQL Server service identity full control of {Path.GetDirectoryName(request.DataFilePath)}.",
                $"Grant the SQL Server service identity full control of {Path.GetDirectoryName(request.LogFilePath)}."
            ]);
    }

    public static IReadOnlyList<string> Validate(SqlServerProvisioningRequest request) =>
        ValidateAndCanonicalise(request).Failures;

    private void ValidatePathStateBeforeIo(SqlServerProvisioningRequest request)
    {
        LiveRootPathInspection root;
        try
        {
            root = _pathInspector.Inspect(_layout.Root);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException("The canonical SQL root could not be inspected safely.", exception);
        }

        if (!root.Exists || root.IsReparsePoint || !SamePath(root.ResolvedPath, _layout.Root))
        {
            throw new InvalidOperationException("The canonical SQL root is missing, ambiguous or a reparse point.");
        }

        foreach (var path in new[] { request.DataFilePath, request.LogFilePath })
        {
            var validation = _layout.ValidateOwnedPath(path, _pathInspector);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(
                    $"The canonical SQL path failed safe inspection: {validation.Reason}.");
            }
        }
    }

    private static bool SamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static ProvisioningValidation ValidateAndCanonicalise(
        SqlServerProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failures = new List<string>();

        if (!request.ConfirmProvision)
        {
            failures.Add("Explicit --confirm-provision approval is required before any mutation.");
        }

        if (!string.Equals(
                request.DataFilePath,
                SqlServerOptions.ProductionDataFilePath,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.LogFilePath,
                SqlServerOptions.ProductionLogFilePath,
                StringComparison.Ordinal))
        {
            failures.Add("Provisioning requires the approved canonical SQL-owned data and log paths.");
        }

        ValidateAdministratorConnection(request.AdministratorConnectionString, failures);
        return new ProvisioningValidation(failures);
    }

    private static void ValidateAdministratorConnection(string connectionString, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            failures.Add("An explicit administrator connection string is required.");
            return;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(builder.AttachDBFilename) || builder.UserInstance)
            {
                failures.Add("The administrator connection cannot attach a database or use a user instance.");
            }

            if (!string.IsNullOrEmpty(builder.InitialCatalog) &&
                !string.Equals(builder.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("The administrator connection must be server-level or target the master catalog.");
            }
        }
        catch (ArgumentException exception)
        {
            failures.Add($"The administrator connection string is invalid: {exception.Message}");
        }
    }

    private static async Task EnsureServerCanProvisionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string validationSql =
            """
            SELECT
                CONVERT(int, SERVERPROPERTY('IsFullTextInstalled')),
                CONVERT(int, CASE WHEN DB_ID(N'FluxKnowledge') IS NULL THEN 0 ELSE 1 END);
            """;
        await using var command = new SqlCommand(validationSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (reader.GetInt32(0) != 1)
        {
            throw new InvalidOperationException(
                "SQL Server Full-Text must be installed before FluxKnowledge can be provisioned.");
        }

        if (reader.GetInt32(1) != 0)
        {
            throw new InvalidOperationException(
                "The FluxKnowledge database already exists; provisioning will not replace it.");
        }
    }

    private sealed record ProvisioningValidation(IReadOnlyList<string> Failures);

    private sealed class SqlProvisioningFileSystem : ISqlProvisioningFileSystem
    {
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    }

    private sealed class SqlProvisioningDatabase : ISqlProvisioningDatabase
    {
        public async Task ProvisionAsync(
            string administratorConnectionString,
            CancellationToken cancellationToken)
        {
            await using var administratorConnection = new SqlConnection(administratorConnectionString);
            await administratorConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureServerCanProvisionAsync(administratorConnection, cancellationToken).ConfigureAwait(false);

            await using (var createCommand = new SqlCommand(CreateDatabaseSql, administratorConnection))
            {
                await createCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var applicationConnection = new SqlConnectionStringBuilder(administratorConnectionString)
            {
                InitialCatalog = SqlServerOptions.CatalogName,
                AttachDBFilename = string.Empty,
                UserInstance = false
            }.ConnectionString;
            var contextOptions = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(applicationConnection)
                .Options;
            await using var context = new FluxKnowledgeDbContext(contextOptions);
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

internal interface ISqlProvisioningFileSystem
{
    void CreateDirectory(string path);
}

internal interface ISqlProvisioningDatabase
{
    Task ProvisionAsync(string administratorConnectionString, CancellationToken cancellationToken);
}

public sealed record SqlServerProvisioningRequest(
    string AdministratorConnectionString,
    string DataFilePath,
    string LogFilePath,
    bool ConfirmProvision);

public sealed record SqlServerProvisioningResult(
    string CatalogName,
    string DataFilePath,
    string LogFilePath,
    IReadOnlyList<string> AclInstructions);
