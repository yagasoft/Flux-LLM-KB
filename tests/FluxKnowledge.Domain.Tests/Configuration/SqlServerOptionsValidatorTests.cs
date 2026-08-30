using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using Microsoft.Extensions.Options;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Configuration;

public sealed class SqlServerOptionsValidatorTests
{
    [Fact]
    public void Production_options_use_the_canonical_live_root_layout()
    {
        Assert.Equal(@"I:\FluxKnowledge\Data\Sql\Data\FluxKnowledge.mdf", SqlServerOptions.ProductionDataFilePath);
        Assert.Equal(@"I:\FluxKnowledge\Data\Sql\Log\FluxKnowledge_log.ldf", SqlServerOptions.ProductionLogFilePath);
    }

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

    [Fact]
    public void Readiness_requires_exactly_the_canonical_data_and_log_files()
    {
        var options = ValidOptions();
        var snapshot = ReadySnapshot() with
        {
            DatabaseFiles =
            [
                new("ROWS", SqlServerOptions.ProductionDataFilePath),
                new("ROWS", "I:/FluxKnowledge/Data/Sql/Data/Unexpected.ndf"),
                new("LOG", SqlServerOptions.ProductionLogFilePath)
            ]
        };

        var result = SqlServerReadinessValidator.Evaluate(options, snapshot);

        Assert.False(result.IsReady);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("exactly one data file and one log file", StringComparison.Ordinal));
    }

    [Fact]
    public void Readiness_requires_artifacts_search_text_in_the_fluxknowledge_fulltext_catalog()
    {
        var result = SqlServerReadinessValidator.Evaluate(
            ValidOptions(),
            ReadySnapshot() with { HasExpectedArtifactSearchTextFullTextIndex = false });

        Assert.False(result.IsReady);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("Artifacts.SearchText", StringComparison.Ordinal));

        var sql = new SqlServerReadinessValidator().BuildValidationSql();
        Assert.Contains("sys.fulltext_index_columns", sql, StringComparison.Ordinal);
        Assert.Contains("SearchText", sql, StringComparison.Ordinal);
        Assert.Contains("FluxKnowledge", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Readiness_requires_the_exact_current_migration_set()
    {
        var result = SqlServerReadinessValidator.Evaluate(
            ValidOptions(),
            ReadySnapshot() with
            {
                ExpectedMigrations = ["20260726215521_InitialPhase1", "20260727000000_EnforceCanonicalSqlLineage"],
                AppliedMigrations = ["20260726215521_InitialPhase1"]
            });

        Assert.False(result.IsReady);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("current migration set", StringComparison.Ordinal));
    }

    [Fact]
    public void Readiness_query_includes_the_active_validated_index_state()
    {
        var sql = new SqlServerReadinessValidator().BuildValidationSql();

        Assert.Contains("[IndexState]", sql, StringComparison.Ordinal);
        Assert.Contains("[IndexGenerations]", sql, StringComparison.Ordinal);
        Assert.Contains("[ValidatedAtUtc]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Readiness_rejects_a_missing_or_unvalidated_active_index()
    {
        var result = SqlServerReadinessValidator.Evaluate(
            ValidOptions(),
            ReadySnapshot() with { HasValidatedActiveIndex = false });

        Assert.False(result.IsReady);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("validated active index generation", StringComparison.Ordinal));
    }

    private static SqlServerOptions ValidOptions() =>
        SqlServerOptions.ForProduction(
            "Server=localhost;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
            SqlServerOptions.ProductionDataFilePath,
            SqlServerOptions.ProductionLogFilePath);

    private static SqlServerReadinessSnapshot ReadySnapshot() =>
        new(
            SqlServerOptions.CatalogName,
            IsFullTextInstalled: true,
            HasExpectedArtifactSearchTextFullTextIndex: true,
            HasValidatedActiveIndex: true,
            [
                new("ROWS", SqlServerOptions.ProductionDataFilePath),
                new("LOG", SqlServerOptions.ProductionLogFilePath)
            ],
            ["20260726215521_InitialPhase1"],
            ["20260726215521_InitialPhase1"]);
}
