using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class SchemaMappingTests
{
    private const string ModelOnlyConnection =
        "Server=localhost;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true";

    [Fact]
    public void Canonical_identity_and_dispatch_keys_are_unique()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertUniqueIndex(model, "SourceIdentities", "SourceKind", "StableKey");
        var sourceIdentity = FindTable(model, "SourceIdentities");
        var sourceKeyBytes =
            (Assert.NotNull(sourceIdentity.FindProperty("SourceKind")!.GetMaxLength()) +
             Assert.NotNull(sourceIdentity.FindProperty("StableKey")!.GetMaxLength())) * 2;
        Assert.True(sourceKeyBytes <= 1700);
        AssertUniqueIndex(model, "PipelineRecords", "SourceIdentityId", "Revision");
        AssertUniqueIndex(model, "OutboxMessages", "IdempotencyKey");
    }

    [Fact]
    public void Vector_mapping_preserves_stable_identity_and_rebuild_metadata()
    {
        using var context = CreateContext();
        var entityType = FindTable(context.Model, "Vectors");

        var vectorId = AssertProperty<long>(entityType, "VectorId");
        Assert.Equal(ValueGenerated.OnAdd, vectorId.ValueGenerated);
        Assert.True(vectorId.IsPrimaryKey());

        AssertProperty<string>(entityType, "ModelFingerprint");
        AssertProperty<int>(entityType, "Dimensions");
        AssertProperty<byte[]>(entityType, "Values");
        AssertProperty<string>(entityType, "ContentHash");
        AssertProperty<long>(entityType, "SourceRevision");
        AssertProperty<bool>(entityType, "IsDeleted");
        AssertProperty<Guid>(entityType, "IndexGenerationId");
        var rowVersion = AssertProperty<byte[]>(entityType, "RowVersion");
        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
    }

    [Fact]
    public void Gpu_mini_task_mapping_preserves_future_lane_fields()
    {
        using var context = CreateContext();
        var entityType = FindTable(context.Model, "GpuMiniTasks");

        AssertProperty<Guid>(entityType, "Id");
        AssertProperty<Guid>(entityType, "ParentJobId");
        AssertProperty<long>(entityType, "SourceRevision");
        AssertProperty<int>(entityType, "PriorityLane");
        AssertProperty<string>(entityType, "ModelRuntimeKey");
        AssertProperty<string>(entityType, "SettingsFingerprint");
        AssertProperty<long>(entityType, "EstimatedBytes");
        AssertProperty<long>(entityType, "AdmissionGeneration");
        AssertProperty<string>(entityType, "IdempotencyKey");
    }

    [Fact]
    public void Model_uses_only_the_sql_server_provider_and_standard_server_connection()
    {
        using var context = CreateContext();

        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
        Assert.DoesNotContain(
            context.Model.GetAnnotations(),
            annotation => annotation.Value?.ToString()?.Contains("SQLite", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(
            context.Database.GetConnectionString() ?? string.Empty,
            "AttachDbFilename",
            StringComparison.OrdinalIgnoreCase);
    }

    private static FluxKnowledgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(ModelOnlyConnection)
            .Options;
        return new FluxKnowledgeDbContext(options);
    }

    private static void AssertUniqueIndex(
        IModel model,
        string table,
        params string[] propertyNames)
    {
        var entityType = FindTable(model, table);
        var index = Assert.Single(
            entityType.GetIndexes(),
            candidate => candidate.Properties.Select(property => property.Name).SequenceEqual(propertyNames));

        Assert.True(index.IsUnique);
    }

    private static IEntityType FindTable(IModel model, string table) =>
        Assert.Single(model.GetEntityTypes(), entity => entity.GetTableName() == table);

    private static IProperty AssertProperty<T>(IEntityType entityType, string name)
    {
        var property = entityType.FindProperty(name);
        Assert.NotNull(property);
        Assert.Equal(typeof(T), property.ClrType);
        return property;
    }
}

public sealed class NativeSqlServerFixtureValidationTests
{
    [Theory]
    [InlineData("Server=localhost;Initial Catalog=master;Integrated Security=true")]
    [InlineData("Server=localhost;Database=FluxKnowledge;Integrated Security=true")]
    [InlineData("Server=localhost;AttachDbFilename=C:\\temp\\test.mdf;Integrated Security=true")]
    [InlineData("Server=localhost;User Instance=true;Integrated Security=true")]
    public void Native_fixture_rejects_catalog_and_file_attachment_keys(string connectionString)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => NativeSqlServerFixture.ValidateServerConnectionString(connectionString));

        Assert.Contains("server-level", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class NativeSchemaMigrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Native_migration_creates_only_the_generated_phase_one_catalog()
    {
        Assert.StartsWith(
            "FluxKnowledge_Phase1Tests_",
            fixture.DatabaseName,
            StringComparison.Ordinal);
        Assert.DoesNotContain("I:/", fixture.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I:\\", fixture.ConnectionString, StringComparison.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        const string sql =
            """
            SELECT COUNT(*)
            FROM sys.tables
            WHERE [name] IN (
                N'SourceIdentities', N'PipelineRecords', N'Jobs', N'JobAttempts',
                N'OutboxMessages', N'Artifacts', N'TextChunks', N'Vectors',
                N'IndexGenerations', N'IndexState', N'AuditEvents', N'GpuMiniTasks');
            """;
        await using var command = new SqlCommand(sql, connection);
        var tableCount = Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(12, tableCount);
    }
}
