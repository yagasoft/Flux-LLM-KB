using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Provisioning;

public sealed class SqlServerProvisioner
{
    private const string CreateDatabaseSql =
        """
        CREATE DATABASE [FluxKnowledge]
        ON PRIMARY
        (
            NAME = N'FluxKnowledge',
            FILENAME = N'I:\FluxKnowledge\Sql\Data\FluxKnowledge.mdf'
        )
        LOG ON
        (
            NAME = N'FluxKnowledge_log',
            FILENAME = N'I:\FluxKnowledge\Sql\Log\FluxKnowledge_log.ldf'
        );
        """;

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
        var backupTarget = validation.CanonicalBackupTarget
            ?? throw new InvalidOperationException(
                "The validated backup target has no canonical file-system path.");

        Directory.CreateDirectory(Path.GetDirectoryName(request.DataFilePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(request.LogFilePath)!);
        Directory.CreateDirectory(backupTarget);

        await using var administratorConnection = new SqlConnection(request.AdministratorConnectionString);
        await administratorConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureServerCanProvisionAsync(administratorConnection, cancellationToken).ConfigureAwait(false);

        await using (var createCommand = new SqlCommand(CreateDatabaseSql, administratorConnection))
        {
            await createCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var applicationConnection = new SqlConnectionStringBuilder(request.AdministratorConnectionString)
        {
            InitialCatalog = SqlServerOptions.CatalogName,
            AttachDBFilename = string.Empty,
            UserInstance = false
        }.ConnectionString;
        var contextOptions = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(applicationConnection)
            .Options;
        await using (var context = new FluxKnowledgeDbContext(contextOptions))
        {
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SqlServerProvisioningResult(
            SqlServerOptions.CatalogName,
            request.DataFilePath,
            request.LogFilePath,
            backupTarget,
            [
                $"Grant the SQL Server service identity full control of {Path.GetDirectoryName(request.DataFilePath)}.",
                $"Grant the SQL Server service identity full control of {Path.GetDirectoryName(request.LogFilePath)}.",
                $"Grant the SQL Server service identity write access to {backupTarget}."
            ]);
    }

    public static IReadOnlyList<string> Validate(SqlServerProvisioningRequest request) =>
        ValidateAndCanonicalise(request).Failures;

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
        var canonicalBackupTarget = ValidateBackupTarget(request.BackupTarget, failures);
        return new ProvisioningValidation(failures, canonicalBackupTarget);
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

    private static string? ValidateBackupTarget(
        string backupTarget,
        List<string> failures)
    {
        if (!TryCanonicaliseBackupTarget(backupTarget, out var canonicalTarget))
        {
            failures.Add("An absolute --backup-target outside I: is required.");
            return null;
        }

        var root = Path.GetPathRoot(canonicalTarget);
        if (string.Equals(root, "I:\\", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("The backup target must be outside I:.");
            return null;
        }

        return canonicalTarget;
    }

    private static bool TryCanonicaliseBackupTarget(
        string backupTarget,
        out string canonicalTarget)
    {
        canonicalTarget = string.Empty;
        if (string.IsNullOrWhiteSpace(backupTarget))
        {
            return false;
        }

        var normalised = backupTarget.Replace('/', '\\');
        if (normalised.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            normalised.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            normalised = @"\\" + normalised[8..];
        }
        else if (normalised.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                 normalised.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            normalised = normalised[4..];
            if (normalised.Length < 3 ||
                !char.IsAsciiLetter(normalised[0]) ||
                normalised[1] != ':' ||
                normalised[2] != '\\')
            {
                return false;
            }
        }

        if (!Path.IsPathFullyQualified(normalised))
        {
            return false;
        }

        try
        {
            canonicalTarget = Path.GetFullPath(normalised).Replace('/', '\\');
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
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

    private sealed record ProvisioningValidation(
        IReadOnlyList<string> Failures,
        string? CanonicalBackupTarget);
}

public sealed record SqlServerProvisioningRequest(
    string AdministratorConnectionString,
    string DataFilePath,
    string LogFilePath,
    string BackupTarget,
    bool ConfirmProvision);

public sealed record SqlServerProvisioningResult(
    string CatalogName,
    string DataFilePath,
    string LogFilePath,
    string BackupTarget,
    IReadOnlyList<string> AclInstructions);
