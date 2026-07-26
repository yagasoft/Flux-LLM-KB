using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using Microsoft.Extensions.Options;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Configuration;

public sealed class SqlServerOptionsValidatorTests
{
    [Theory]
    [InlineData("Server=.;AttachDbFilename=C:\\temp\\FluxKnowledge.mdf;Integrated Security=true")]
    [InlineData("Data Source=.;Initial Catalog=FluxKnowledge;User Instance=true;Integrated Security=true")]
    public void Production_connection_string_cannot_attach_a_user_database(string connectionString)
    {
        var result = SqlServerOptionsValidator.Validate(new SqlServerOptions
        {
            ConnectionString = connectionString,
            DataFilePath = SqlServerOptions.ProductionDataFilePath,
            LogFilePath = SqlServerOptions.ProductionLogFilePath
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Production_options_require_the_approved_sql_owned_file_paths()
    {
        var options = SqlServerOptions.ForProduction(
            "Server=localhost;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
            "I:/other/FluxKnowledge.mdf",
            SqlServerOptions.ProductionLogFilePath);

        var error = Assert.Throws<OptionsValidationException>(
            () => SqlServerOptionsValidator.ThrowIfInvalid(options));

        Assert.Contains(SqlServerOptions.ProductionDataFilePath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_options_require_the_canonical_catalog()
    {
        var options = SqlServerOptions.ForProduction(
            "Server=localhost;Initial Catalog=NotFluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
            SqlServerOptions.ProductionDataFilePath,
            SqlServerOptions.ProductionLogFilePath);

        var result = SqlServerOptionsValidator.Validate(options);

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, failure => failure.Contains("FluxKnowledge", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_readiness_does_not_contain_database_creation_or_file_movement()
    {
        var script = new SqlServerReadinessValidator().BuildValidationSql();

        Assert.DoesNotContain("CREATE DATABASE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER DATABASE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ATTACH", script, StringComparison.OrdinalIgnoreCase);
    }
}
