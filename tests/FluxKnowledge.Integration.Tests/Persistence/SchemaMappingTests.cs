using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
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
    public void Revision_bearing_records_are_foreign_keyed_to_their_canonical_lineage()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertForeignKey(
            model,
            "Artifacts",
            ["PipelineRecordId", "SourceRevision"],
            "PipelineRecords",
            ["Id", "Revision"]);
        AssertForeignKey(
            model,
            "OutboxMessages",
            ["PipelineRecordId", "SourceRevision"],
            "PipelineRecords",
            ["Id", "Revision"]);
        AssertForeignKey(
            model,
            "Jobs",
            ["PipelineRecordId", "SourceRevision"],
            "PipelineRecords",
            ["Id", "Revision"]);
        AssertForeignKey(
            model,
            "TextChunks",
            ["ArtifactId", "SourceRevision"],
            "Artifacts",
            ["Id", "SourceRevision"]);
        AssertForeignKey(
            model,
            "Vectors",
            ["TextChunkId", "SourceRevision"],
            "TextChunks",
            ["Id", "SourceRevision"]);
        AssertForeignKey(
            model,
            "GpuMiniTasks",
            ["ParentJobId", "SourceRevision"],
            "Jobs",
            ["Id", "SourceRevision"]);
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
    public void Immutable_generation_membership_has_composite_stable_keys()
    {
        using var context = CreateContext();
        var entity = FindTable(context.Model, "IndexGenerationVectors");

        Assert.Equal(
            ["GenerationId", "VectorId"],
            entity.FindPrimaryKey()!.Properties.Select(property => property.Name));
        AssertForeignKey(context.Model, "IndexGenerationVectors", ["GenerationId"], "IndexGenerations", ["Id"]);
        AssertForeignKey(context.Model, "IndexGenerationVectors", ["VectorId"], "Vectors", ["VectorId"]);
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
    public void Mutable_job_attempts_use_rowversion_concurrency()
    {
        using var context = CreateContext();
        var entityType = FindTable(context.Model, "JobAttempts");

        var rowVersion = AssertProperty<byte[]>(entityType, "RowVersion");

        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
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

    private static void AssertForeignKey(
        IModel model,
        string dependentTable,
        string[] dependentProperties,
        string principalTable,
        string[] principalProperties)
    {
        var entityType = FindTable(model, dependentTable);
        var foreignKey = Assert.Single(
            entityType.GetForeignKeys(),
            candidate =>
                candidate.Properties.Select(property => property.Name).SequenceEqual(dependentProperties) &&
                candidate.PrincipalEntityType.GetTableName() == principalTable);

        Assert.Equal(
            principalProperties,
            foreignKey.PrincipalKey.Properties.Select(property => property.Name));
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\test.mdf")]
    [InlineData("I:\\FluxKnowledge\\Tests\\test.mdf")]
    [InlineData("I:/FluxKnowledge/Tests/test.ldf")]
    [InlineData("\\\\?\\I:\\FluxKnowledge\\Tests\\test.mdf")]
    [InlineData("\\\\.\\I:\\FluxKnowledge\\Tests\\test.ldf")]
    public void Native_fixture_fails_closed_for_unverifiable_or_i_drive_file_paths(string? path)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => NativeSqlServerFixture.ValidateCreatedDatabaseFiles(
                [path, "C:\\SqlData\\known-valid.mdf"]));

        Assert.Contains("verified outside I:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_fixture_accepts_only_verified_non_i_drive_file_paths()
    {
        NativeSqlServerFixture.ValidateCreatedDatabaseFiles(
            [
                "C:\\SqlData\\test.mdf",
                "D:\\SqlLog\\test.ldf",
                "\\\\?\\C:\\SqlData\\device-test.mdf",
                "\\\\.\\D:\\SqlLog\\device-test.ldf"
            ]);
    }

    [Fact]
    public async Task Ambiguous_create_failure_still_runs_generated_database_cleanup()
    {
        var serverCommittedCreate = false;
        var cleanupCalled = false;

        await Assert.ThrowsAsync<IOException>(
            () => NativeSqlServerFixture.RunCreateSequenceAsync(
                () =>
                {
                    serverCommittedCreate = true;
                    throw new IOException("client lost acknowledgement");
                },
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () =>
                {
                    Assert.True(serverCommittedCreate);
                    cleanupCalled = true;
                    return Task.CompletedTask;
                }));

        Assert.True(cleanupCalled);
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
                N'IndexGenerations', N'IndexGenerationVectors', N'IndexState', N'AuditEvents', N'GpuMiniTasks');
            """;
        await using var command = new SqlCommand(sql, connection);
        var tableCount = Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(13, tableCount);
    }

    [NativeSqlServerFact]
    public async Task Membership_migration_backfills_origins_and_blocks_snapshot_only_downgrade()
    {
        await using var database = await fixture.CreatePreviousMigrationDatabaseAsync();
        var now = DateTimeOffset.Parse("2026-07-27T12:00:00+00:00");
        var sourceId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var generationId = Guid.NewGuid();
        long vectorId;

        await using (var context = database.CreateContext())
        {
            context.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = sourceId,
                SourceKind = "migration-test",
                StableKey = $"migration-test:{sourceId:N}",
                CreatedAtUtc = now
            });
            context.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = recordId,
                SourceIdentityId = sourceId,
                Revision = 1,
                ContentHash = new string('a', 64),
                RootLineageRecordId = recordId,
                CurrentStage = 1,
                RegisteredAtUtc = now
            });
            context.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = generationId,
                ModelFingerprint = "migration-test:1",
                Dimensions = 1,
                IndexPath = "C:\\migration-test",
                MetadataChecksum = new string('0', 64),
                VectorCount = 1,
                CreatedAtUtc = now
            });
            await context.SaveChangesAsync();

            var artifact = new ArtifactEntity
            {
                Id = Guid.NewGuid(),
                PipelineRecordId = recordId,
                SourceRevision = 1,
                Stage = 1,
                ContentHash = new string('b', 64),
                ContentType = "text/plain",
                SearchText = "migration test",
                CreatedAtUtc = now
            };
            context.Artifacts.Add(artifact);
            await context.SaveChangesAsync();

            var chunk = new TextChunkEntity
            {
                ArtifactId = artifact.Id,
                SourceRevision = 1,
                Ordinal = 0,
                StartOffset = 0,
                Length = 14,
                Content = "migration test",
                ContentHash = new string('c', 64)
            };
            context.TextChunks.Add(chunk);
            await context.SaveChangesAsync();

            var vector = new VectorEntity
            {
                TextChunkId = chunk.Id,
                SourceRevision = 1,
                ModelFingerprint = "migration-test:1",
                Dimensions = 1,
                Values = [0, 0, 128, 63],
                ContentHash = new string('d', 64),
                IndexGenerationId = generationId,
                CreatedAtUtc = now
            };
            context.Vectors.Add(vector);
            await context.SaveChangesAsync();
            vectorId = vector.VectorId;
        }

        await using (var context = database.CreateContext())
        {
            await context.GetService<IMigrator>().MigrateAsync("20260726235718_AddIndexGenerationMembership");
            var membership = await context.IndexGenerationVectors.SingleAsync();

            Assert.Equal(generationId, membership.GenerationId);
            Assert.Equal(vectorId, membership.VectorId);

            var snapshotOnlyGenerationId = Guid.NewGuid();
            context.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = snapshotOnlyGenerationId,
                ModelFingerprint = "migration-test:1",
                Dimensions = 1,
                IndexPath = "C:\\migration-test-history",
                MetadataChecksum = new string('1', 64),
                VectorCount = 1,
                CreatedAtUtc = now
            });
            context.IndexGenerationVectors.Add(new IndexGenerationVectorEntity
            {
                GenerationId = snapshotOnlyGenerationId,
                VectorId = vectorId
            });
            var activeState = await context.IndexState.SingleAsync(state => state.Id == 1);
            activeState.ActiveIndexGenerationId = snapshotOnlyGenerationId;
            await context.SaveChangesAsync();

            var failure = await Assert.ThrowsAsync<SqlException>(
                async () => await context.GetService<IMigrator>()
                    .MigrateAsync("20260726221653_EnforceCanonicalSqlSafety"));

            Assert.Equal(51000, failure.Number);
            Assert.Contains("snapshot-only history", failure.Message, StringComparison.Ordinal);
        }

        await using var verification = database.CreateContext();
        Assert.Equal(2, await verification.IndexGenerationVectors.CountAsync());
    }
}
