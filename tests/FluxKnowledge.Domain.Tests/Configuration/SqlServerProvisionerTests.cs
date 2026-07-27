using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Configuration;

public sealed class SqlServerProvisionerTests
{
    [Theory]
    [InlineData(@"\\?\I:\FluxKnowledgeBackups")]
    [InlineData(@"\\.\I:\FluxKnowledgeBackups")]
    public void Backup_target_rejects_device_prefixed_i_drive_paths(string backupTarget)
    {
        var failures = SqlServerProvisioner.Validate(ValidRequest(backupTarget));

        Assert.Contains(
            failures,
            failure => failure.Contains("outside I:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"C:\FluxKnowledgeBackups")]
    [InlineData(@"\\?\C:\FluxKnowledgeBackups")]
    [InlineData(@"\\server\share\FluxKnowledgeBackups")]
    [InlineData(@"\\?\UNC\server\share\FluxKnowledgeBackups")]
    public void Backup_target_preserves_valid_non_i_file_system_paths(string backupTarget)
    {
        var failures = SqlServerProvisioner.Validate(ValidRequest(backupTarget));

        Assert.DoesNotContain(
            failures,
            failure => failure.Contains("backup-target", StringComparison.OrdinalIgnoreCase) ||
                       failure.Contains("outside I:", StringComparison.Ordinal));
    }

    private static SqlServerProvisioningRequest ValidRequest(string backupTarget) =>
        new(
            "Server=localhost;Initial Catalog=master;Integrated Security=true;" +
            "Encrypt=true;TrustServerCertificate=true",
            SqlServerOptions.ProductionDataFilePath,
            SqlServerOptions.ProductionLogFilePath,
            backupTarget,
            ConfirmProvision: true);
}
