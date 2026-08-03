using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class SchemaMappingTests
{
    private const string ModelOnlyConnection =
        "Server=localhost;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true";

    [Fact]
    public void Initial_phase_full_text_operations_are_transaction_suppressed()
    {
        var migration = new InspectableInitialPhase1Migration();

        var upOperation = Assert.Single(
            migration.BuildUpOperations().OfType<SqlOperation>(),
            operation => operation.Sql.Contains("CREATE FULLTEXT CATALOG", StringComparison.Ordinal));
        var downOperation = Assert.Single(
            migration.BuildDownOperations().OfType<SqlOperation>(),
            operation => operation.Sql.Contains("DROP FULLTEXT CATALOG", StringComparison.Ordinal));

        Assert.True(upOperation.SuppressTransaction);
        Assert.True(downOperation.SuppressTransaction);
    }

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
        AssertProperty<string>(entityType, "TextChunkContentHash");
        AssertProperty<string>(entityType, "PayloadChecksum");
        Assert.Null(entityType.FindProperty("ContentHash"));
        AssertProperty<long>(entityType, "SourceRevision");
        AssertProperty<bool>(entityType, "IsDeleted");
        AssertProperty<Guid>(entityType, "IndexGenerationId");
        var rowVersion = AssertProperty<byte[]>(entityType, "RowVersion");
        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
    }

    [Fact]
    public void Vector_hash_migration_preserves_payload_integrity_and_backfills_chunk_identity()
    {
        using var context = CreateContext();
        var script = context.GetService<IMigrator>().GenerateScript(
            "20260726235718_AddIndexGenerationMembership",
            "20260727055755_DistinguishVectorIdentityAndPayloadChecksum");

        Assert.Contains(
            "EXEC sp_rename N'[Vectors].[ContentHash]', N'PayloadChecksum', 'COLUMN';",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "SET [TextChunkContentHash] = [chunk].[ContentHash]",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE [Vectors] ALTER COLUMN [TextChunkContentHash] char(64) NOT NULL;",
            script,
            StringComparison.Ordinal);
        Assert.Contains("THROW 51000", script, StringComparison.Ordinal);
        Assert.Contains("CK_Vectors_PayloadChecksum", script, StringComparison.Ordinal);
        Assert.Contains("CK_Vectors_TextChunkContentHash", script, StringComparison.Ordinal);
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
    public void Gpu_scheduler_durability_uses_restrictive_relationships_and_a_sequence_backed_ready_queue()
    {
        using var context = CreateContext();
        var model = context.Model;
        var miniTask = FindTable(model, "GpuMiniTasks");

        var executionState = AssertProperty<int>(miniTask, "ExecutionState");
        Assert.Equal("State", executionState.GetColumnName(StoreObjectIdentifier.Table("GpuMiniTasks", null)));
        var createdSequence = AssertProperty<long>(miniTask, "CreatedSequence");
        Assert.Equal(ValueGenerated.OnAdd, createdSequence.ValueGenerated);
        Assert.Contains("GpuMiniTaskCreatedSequence", createdSequence.GetDefaultValueSql(), StringComparison.Ordinal);
        AssertProperty<DateTimeOffset?>(miniTask, "DeferredUntilUtc");
        AssertProperty<Guid?>(miniTask, "BatchId");
        AssertProperty<int>(miniTask, "ReservationAttemptCount");
        var handoffLeaseOwner = AssertProperty<string>(miniTask, "HandoffLeaseOwner");
        Assert.Equal(256, handoffLeaseOwner.GetMaxLength());
        AssertUniqueIndex(model, "GpuMiniTasks", "IdempotencyKey");
        AssertRestrictiveForeignKey(model, "GpuMiniTasks", ["BatchId"], "GpuBatches", ["Id"]);

        var batch = FindTable(model, "GpuBatches");
        AssertProperty<string>(batch, "CapacitySlotKey");
        AssertProperty<int>(batch, "PriorityLane");
        AssertProperty<string>(batch, "ModelRuntimeKey");
        AssertProperty<string>(batch, "SettingsFingerprint");
        AssertProperty<int>(batch, "ItemCount");
        AssertProperty<long>(batch, "EstimatedBytes");
        AssertProperty<long>(batch, "AdmissionGeneration");
        AssertProperty<string>(batch, "OwnerKey");
        AssertProperty<int>(batch, "State");
        AssertProperty<DateTimeOffset?>(batch, "LastHeartbeatAtUtc");
        AssertRestrictiveForeignKey(model, "GpuBatches", ["CapacitySlotKey"], "GpuCapacitySlots", ["SlotKey"]);

        var slot = FindTable(model, "GpuCapacitySlots");
        AssertProperty<string>(slot, "SlotKey");
        AssertProperty<int>(slot, "State");
        AssertProperty<Guid?>(slot, "ActiveBatchId");
        AssertProperty<string>(slot, "OwnerKey");
        AssertProperty<DateTimeOffset?>(slot, "LastHeartbeatAtUtc");
        AssertRestrictiveForeignKey(model, "GpuCapacitySlots", ["ActiveBatchId"], "GpuBatches", ["Id"]);

        var scheduler = FindTable(context.GetService<IDesignTimeModel>().Model, "GpuSchedulerState");
        AssertProperty<int>(scheduler, "Id");
        AssertProperty<long>(scheduler, "WakeGeneration");
        AssertProperty<int>(scheduler, "PendingWakeReasons");
        AssertProperty<DateTimeOffset?>(scheduler, "NextDeferredAtUtc");
        Assert.Contains(
            scheduler.GetCheckConstraints(),
            constraint =>
                constraint.Name == "CK_GpuSchedulerState_Singleton" &&
                constraint.Sql == "[Id] = 1");
        AssertIndex(model, "GpuMiniTasks", "ExecutionState", "PriorityLane", "CreatedSequence", "Id");
        AssertIndex(model, "GpuMiniTasks", "ExecutionState", "DeferredUntilUtc");
    }

    [Fact]
    public void Scheduler_fence_and_compatibility_strings_use_binary_sql_collation()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        AssertBinaryCollation(model, "Jobs", "Operation", "LeaseOwner");
        AssertBinaryCollation(
            model,
            "GpuMiniTasks",
            "ModelRuntimeKey",
            "SettingsFingerprint",
            "IdempotencyKey",
            "HandoffLeaseOwner");
        AssertBinaryCollation(
            model,
            "GpuBatches",
            "CapacitySlotKey",
            "ModelRuntimeKey",
            "SettingsFingerprint",
            "OwnerKey");
        AssertBinaryCollation(model, "GpuCapacitySlots", "SlotKey", "OwnerKey");
        AssertBinaryCollation(
            model,
            "GpuSchedulerOperationReceipts",
            "OperationKind",
            "RequestFingerprint",
            "CapacitySlotKey",
            "OwnerKey");
    }

    [Fact]
    public void Scheduler_fence_and_compatibility_strings_require_non_empty_canonical_keys_in_the_schema()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        AssertNoTrailingWhitespaceConstraint(model, "Jobs", "Operation");
        AssertNoTrailingWhitespaceConstraint(model, "Jobs", "LeaseOwner");
        AssertNoTrailingWhitespaceConstraint(model, "GpuMiniTasks", "ModelRuntimeKey");
        AssertNoTrailingWhitespaceConstraint(model, "GpuMiniTasks", "SettingsFingerprint");
        AssertNoTrailingWhitespaceConstraint(model, "GpuMiniTasks", "IdempotencyKey");
        AssertNoTrailingWhitespaceConstraint(model, "GpuMiniTasks", "HandoffLeaseOwner");
        AssertNoTrailingWhitespaceConstraint(model, "GpuBatches", "CapacitySlotKey");
        AssertNoTrailingWhitespaceConstraint(model, "GpuBatches", "ModelRuntimeKey");
        AssertNoTrailingWhitespaceConstraint(model, "GpuBatches", "SettingsFingerprint");
        AssertNoTrailingWhitespaceConstraint(model, "GpuBatches", "OwnerKey");
        AssertNoTrailingWhitespaceConstraint(model, "GpuCapacitySlots", "SlotKey");
        AssertNoTrailingWhitespaceConstraint(model, "GpuCapacitySlots", "OwnerKey");
        AssertNoTrailingWhitespaceConstraint(model, "GpuSchedulerOperationReceipts", "OperationKind");
        AssertNoTrailingWhitespaceConstraint(model, "GpuSchedulerOperationReceipts", "RequestFingerprint");
        AssertNoTrailingWhitespaceConstraint(model, "GpuSchedulerOperationReceipts", "CapacitySlotKey");
        AssertNoTrailingWhitespaceConstraint(model, "GpuSchedulerOperationReceipts", "OwnerKey");
    }

    [Fact]
    public void Opaque_key_canonicality_migration_target_and_current_snapshot_require_non_empty_keys()
    {
        using var currentContext = CreateContext();
        var models = new (string Name, IModel Model)[]
        {
            (nameof(AddGpuSchedulerOpaqueKeyCanonicality), new InspectableAddGpuSchedulerOpaqueKeyCanonicalityMigration().BuildTargetModel()),
            ("CurrentSnapshot", currentContext.GetService<IDesignTimeModel>().Model)
        };

        foreach (var (_, model) in models)
        {
            AssertNoTrailingWhitespaceConstraint(model, "GpuCapacitySlots", "SlotKey");
            AssertNoTrailingWhitespaceConstraint(model, "GpuCapacitySlots", "OwnerKey");
            AssertNoTrailingWhitespaceConstraint(model, "GpuMiniTasks", "IdempotencyKey");
            AssertNoTrailingWhitespaceConstraint(model, "GpuSchedulerOperationReceipts", "OperationKind");
        }
    }

    [Fact]
    public void Gpu_scheduler_migration_is_additive_and_seeds_its_singleton_state()
    {
        var migration = new InspectableAddGpuSchedulerDurabilityMigration();
        var operations = migration.BuildUpOperations();

        Assert.Contains(
            operations.OfType<CreateSequenceOperation>(),
            operation => operation.Name == "GpuMiniTaskCreatedSequence");
        var createdSequence = Assert.Single(
            operations.OfType<AddColumnOperation>(),
            operation => operation.Table == "GpuMiniTasks" && operation.Name == "CreatedSequence");
        Assert.True(createdSequence.IsNullable);
        Assert.Null(createdSequence.DefaultValueSql);
        Assert.Contains(
            operations.OfType<SqlOperation>(),
            operation => operation.Sql.Contains("ROW_NUMBER() OVER (ORDER BY [CreatedAtUtc], [Id])", StringComparison.Ordinal) &&
                         operation.Sql.Contains("ALTER SEQUENCE [GpuMiniTaskCreatedSequence] RESTART WITH", StringComparison.Ordinal));
        Assert.Contains(
            operations.OfType<AlterColumnOperation>(),
            operation => operation.Table == "GpuMiniTasks" && operation.Name == "CreatedSequence" &&
                         !operation.IsNullable &&
                         operation.DefaultValueSql == "NEXT VALUE FOR [GpuMiniTaskCreatedSequence]");
        var handoffLeaseOwner = Assert.Single(
            operations.OfType<AddColumnOperation>(),
            operation => operation.Table == "GpuMiniTasks" && operation.Name == "HandoffLeaseOwner");
        Assert.True(handoffLeaseOwner.IsNullable);
        Assert.Contains(
            operations.OfType<InsertDataOperation>(),
            operation => operation.Table == "GpuSchedulerState");
        Assert.Contains(
            operations.OfType<AddCheckConstraintOperation>(),
            operation =>
                operation.Table == "GpuSchedulerState" &&
                operation.Name == "CK_GpuSchedulerState_Singleton" &&
                operation.Sql == "[Id] = 1");
        Assert.DoesNotContain(
            operations.OfType<DropTableOperation>(),
            operation => operation.Name is "GpuMiniTasks" or "Jobs" or "PipelineRecords");
        Assert.DoesNotContain(
            operations.OfType<RenameColumnOperation>(),
            operation => operation.Table == "GpuMiniTasks" && operation.Name == "State");
    }

    [Fact]
    public void Gpu_scheduler_durability_migration_target_model_contains_the_in_flight_wake_fence()
    {
        var model = new InspectableAddGpuSchedulerDurabilityMigration().BuildTargetModel();
        var scheduler = FindTable(model, "GpuSchedulerState");

        AssertProperty<Guid?>(scheduler, "InFlightWakeOperationId");
        AssertProperty<long?>(scheduler, "InFlightWakeGeneration");
        AssertProperty<int>(scheduler, "InFlightWakeReasons");
        AssertProperty<DateTimeOffset?>(scheduler, "InFlightNextDeferredAtUtc");
        Assert.Contains(
            scheduler.GetCheckConstraints(),
            constraint =>
                constraint.Name == "CK_GpuSchedulerState_InFlightWake" &&
                constraint.Sql!.Contains("[InFlightWakeOperationId] IS NULL", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_scheduler_migration_target_after_durability_retains_the_complete_in_flight_wake_fence()
    {
        using var currentContext = CreateContext();
        var targets = new (string Name, IModel Model)[]
        {
            (nameof(AddGpuSchedulerDurability), new InspectableAddGpuSchedulerDurabilityMigration().BuildTargetModel()),
            (nameof(AddGpuSchedulerOperationReceipts), new InspectableAddGpuSchedulerOperationReceiptsMigration().BuildTargetModel()),
            (nameof(CompleteGpuSchedulerOperationReceipts), new InspectableCompleteGpuSchedulerOperationReceiptsMigration().BuildTargetModel()),
            (nameof(AddGpuSchedulerOperationReceiptRequestFingerprint), new InspectableAddGpuSchedulerOperationReceiptRequestFingerprintMigration().BuildTargetModel()),
            (nameof(AddGpuSchedulerBinaryFenceCollation), new InspectableAddGpuSchedulerBinaryFenceCollationMigration().BuildTargetModel()),
            (nameof(AddGpuSchedulerOpaqueKeyCanonicality), new InspectableAddGpuSchedulerOpaqueKeyCanonicalityMigration().BuildTargetModel()),
            ("CurrentSnapshot", currentContext.Model)
        };

        foreach (var (name, model) in targets)
        {
            var scheduler = FindTable(model, "GpuSchedulerState");
            AssertProperty<Guid?>(scheduler, "InFlightWakeOperationId");
            AssertProperty<long?>(scheduler, "InFlightWakeGeneration");
            AssertProperty<int>(scheduler, "InFlightWakeReasons");
            AssertProperty<DateTimeOffset?>(scheduler, "InFlightNextDeferredAtUtc");
            AssertProperty<int?>(scheduler, "InFlightEffectiveAdmissionReasons");
            if (name != "CurrentSnapshot")
            {
                Assert.Contains(
                    scheduler.GetCheckConstraints(),
                    constraint =>
                        constraint.Name == "CK_GpuSchedulerState_InFlightWake" &&
                        constraint.Sql!.Contains("[InFlightEffectiveAdmissionReasons] IS NULL", StringComparison.Ordinal));
            }

            if (name == nameof(AddGpuSchedulerDurability))
            {
                continue;
            }

            var receipts = FindTable(model, "GpuSchedulerOperationReceipts");
            AssertProperty<int?>(receipts, "EffectiveAdmissionReasons");
        }
    }

    [Fact]
    public void Gpu_scheduler_opaque_key_migration_adds_only_canonicality_constraints()
    {
        var migration = new InspectableAddGpuSchedulerOpaqueKeyCanonicalityMigration();
        var operations = migration.BuildUpOperations();
        var constraints = operations.OfType<AddCheckConstraintOperation>().ToList();

        Assert.Equal(16, constraints.Count);
        Assert.Contains(
            constraints,
            constraint =>
                constraint.Table == "GpuCapacitySlots" &&
                constraint.Name == "CK_GpuCapacitySlots_SlotKey_NoTrailingWhitespace" &&
                constraint.Sql.Contains("UNICODE(RIGHT", StringComparison.Ordinal));
        Assert.DoesNotContain(operations, operation => operation is DropTableOperation or AlterColumnOperation);
        Assert.DoesNotContain(operations, operation => operation is UpdateDataOperation or DeleteDataOperation);
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

    private static void AssertIndex(IModel model, string table, params string[] propertyNames)
    {
        var entityType = FindTable(model, table);
        Assert.Contains(
            entityType.GetIndexes(),
            candidate => candidate.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
    }

    private static void AssertBinaryCollation(
        IModel model,
        string table,
        params string[] propertyNames)
    {
        var entityType = FindTable(model, table);
        foreach (var propertyName in propertyNames)
        {
            var property = AssertProperty<string>(entityType, propertyName);
            Assert.Equal("Latin1_General_100_BIN2", property.GetCollation());
        }
    }

    private static void AssertNoTrailingWhitespaceConstraint(
        IModel model,
        string table,
        string propertyName)
    {
        var entityType = FindTable(model, table);
        var constraint = Assert.Single(
            entityType.GetCheckConstraints(),
            candidate => candidate.Name == $"CK_{table}_{propertyName}_NoTrailingWhitespace");
        Assert.Contains("UNICODE(RIGHT", constraint.Sql, StringComparison.Ordinal);
        Assert.Contains($"DATALENGTH([{propertyName}]) > 0", constraint.Sql, StringComparison.Ordinal);
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

    private static void AssertRestrictiveForeignKey(
        IModel model,
        string dependentTable,
        string[] dependentProperties,
        string principalTable,
        string[] principalProperties)
    {
        AssertForeignKey(model, dependentTable, dependentProperties, principalTable, principalProperties);
        var entityType = FindTable(model, dependentTable);
        var foreignKey = Assert.Single(
            entityType.GetForeignKeys(),
            candidate =>
                candidate.Properties.Select(property => property.Name).SequenceEqual(dependentProperties) &&
                candidate.PrincipalEntityType.GetTableName() == principalTable);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
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

    private sealed class InspectableInitialPhase1Migration : InitialPhase1
    {
        public IReadOnlyList<MigrationOperation> BuildUpOperations()
        {
            var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            Up(builder);
            return builder.Operations;
        }

        public IReadOnlyList<MigrationOperation> BuildDownOperations()
        {
            var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            Down(builder);
            return builder.Operations;
        }
    }

    private sealed class InspectableAddGpuSchedulerDurabilityMigration : AddGpuSchedulerDurability
    {
        public IReadOnlyList<MigrationOperation> BuildUpOperations()
        {
            var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            Up(builder);
            return builder.Operations;
        }

        public IModel BuildTargetModel() => TargetModel;
    }

    private sealed class InspectableAddGpuSchedulerOpaqueKeyCanonicalityMigration
        : AddGpuSchedulerOpaqueKeyCanonicality
    {
        public IReadOnlyList<MigrationOperation> BuildUpOperations()
        {
            var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            Up(builder);
            return builder.Operations;
        }

        public IModel BuildTargetModel() => TargetModel;
    }

    private sealed class InspectableAddGpuSchedulerOperationReceiptsMigration
        : AddGpuSchedulerOperationReceipts
    {
        public IModel BuildTargetModel() => TargetModel;
    }

    private sealed class InspectableCompleteGpuSchedulerOperationReceiptsMigration
        : CompleteGpuSchedulerOperationReceipts
    {
        public IModel BuildTargetModel() => TargetModel;
    }

    private sealed class InspectableAddGpuSchedulerOperationReceiptRequestFingerprintMigration
        : AddGpuSchedulerOperationReceiptRequestFingerprint
    {
        public IModel BuildTargetModel() => TargetModel;
    }

    private sealed class InspectableAddGpuSchedulerBinaryFenceCollationMigration
        : AddGpuSchedulerBinaryFenceCollation
    {
        public IModel BuildTargetModel() => TargetModel;
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
    public async Task Native_migration_creates_only_the_generated_scheduler_catalog()
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
                N'IndexGenerations', N'IndexGenerationVectors', N'IndexState', N'AuditEvents', N'GpuMiniTasks',
                N'GpuBatches', N'GpuCapacitySlots', N'GpuSchedulerState');
            """;
        await using var command = new SqlCommand(sql, connection);
        var tableCount = Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(16, tableCount);

        const string schedulerSql =
            """
            SELECT
                (SELECT COUNT(*) FROM [GpuSchedulerState] WHERE [Id] = 1),
                (SELECT COUNT(*) FROM sys.sequences WHERE [name] = N'GpuMiniTaskCreatedSequence');
            """;
        await using var schedulerCommand = new SqlCommand(schedulerSql, connection);
        await using var reader = await schedulerCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [NativeSqlServerFact]
    public async Task Native_scheduler_state_constraint_rejects_any_non_singleton_row()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var constraintCommand = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE [parent_object_id] = OBJECT_ID(N'[GpuSchedulerState]')
              AND [name] = N'CK_GpuSchedulerState_Singleton'
              AND [definition] = N'([Id]=(1))';
            """,
            connection);
        Assert.Equal(1, Convert.ToInt32(await constraintCommand.ExecuteScalarAsync()));

        await using var insert = new SqlCommand(
            """
            INSERT INTO [GpuSchedulerState]
                ([Id], [WakeGeneration], [PendingWakeReasons], [NextDeferredAtUtc], [UpdatedAtUtc])
            VALUES (2, 0, 0, NULL, SYSUTCDATETIME());
            """,
            connection);
        var error = await Assert.ThrowsAsync<SqlException>(async () => await insert.ExecuteNonQueryAsync());
        Assert.Equal(547, error.Number);
    }

    [NativeSqlServerFact]
    public async Task Native_scheduler_fence_constraints_reject_trailing_whitespace()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var constraintCommand = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE [name] LIKE N'CK_Gpu%_NoTrailingWhitespace'
               OR [name] LIKE N'CK_Jobs_%_NoTrailingWhitespace';
            """,
            connection);
        Assert.Equal(16, Convert.ToInt32(await constraintCommand.ExecuteScalarAsync()));

        await using var insert = new SqlCommand(
            """
            INSERT INTO [GpuCapacitySlots] ([SlotKey], [State], [UpdatedAtUtc])
            VALUES (N'slot-a ', 0, SYSDATETIMEOFFSET());
            """,
            connection);
        var error = await Assert.ThrowsAsync<SqlException>(async () => await insert.ExecuteNonQueryAsync());
        Assert.Equal(547, error.Number);
    }

    [NativeSqlServerFact]
    public async Task Membership_and_vector_hash_migrations_backfill_safely_and_block_snapshot_only_downgrade()
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

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO [Vectors]
                     ([TextChunkId], [ModelFingerprint], [Dimensions], [Values],
                      [ContentHash], [SourceRevision], [IsDeleted],
                      [IndexGenerationId], [CreatedAtUtc])
                 VALUES
                     ({chunk.Id}, {"migration-test:1"}, {1}, {new byte[] { 0, 0, 128, 63 }},
                      {new string('d', 64)}, {1L}, {false}, {generationId}, {now});
                 """);
            vectorId = await context.Vectors
                .Select(candidate => candidate.VectorId)
                .SingleAsync();
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
        await verification.GetService<IMigrator>().MigrateAsync();
        var migratedVector = await verification.Vectors.SingleAsync(
            candidate => candidate.VectorId == vectorId);
        Assert.Equal(new string('c', 64), migratedVector.TextChunkContentHash);
        Assert.Equal(new string('d', 64), migratedVector.PayloadChecksum);
    }

    [NativeSqlServerFact]
    public async Task Scheduler_migration_backfills_existing_task_sequence_in_creation_order_and_new_selection_stays_fifo()
    {
        await using var database = await fixture.CreateSchedulerPreviousMigrationDatabaseAsync();
        var now = DateTimeOffset.Parse("2026-07-29T09:00:00+00:00");
        var sourceId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var olderTaskId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var newerTaskId = Guid.Parse("00000000-0000-0000-0000-000000000000");
        var olderCreatedAtUtc = now.AddMinutes(-1);

        await using (var context = database.CreateContext())
        {
            context.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = sourceId,
                SourceKind = "scheduler-migration",
                StableKey = $"scheduler-migration:{sourceId:N}",
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
            context.Jobs.Add(new JobEntity
            {
                Id = jobId,
                PipelineRecordId = recordId,
                SourceRevision = 1,
                Stage = 1,
                Operation = "scheduler-migration",
                PublicState = 2,
                DueAtUtc = now
            });
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO [GpuMiniTasks]
                     ([Id], [ParentJobId], [SourceRevision], [PriorityLane], [ModelRuntimeKey],
                      [SettingsFingerprint], [EstimatedBytes], [AdmissionGeneration], [IdempotencyKey],
                      [State], [CreatedAtUtc])
                 VALUES
                     ({newerTaskId}, {jobId}, {1L}, {0}, {"scheduler-runtime"},
                      {"scheduler-settings"}, {1L}, {0L}, {"scheduler-migration-newer"},
                      {0}, {now}),
                     ({olderTaskId}, {jobId}, {1L}, {0}, {"scheduler-runtime"},
                      {"scheduler-settings"}, {1L}, {0L}, {"scheduler-migration-older"},
                      {0}, {olderCreatedAtUtc});
                 """);

            await context.GetService<IMigrator>().MigrateAsync();
        }

        await using var verification = database.CreateContext();
        var migrated = await verification.GpuMiniTasks
            .Where(candidate => candidate.Id == olderTaskId || candidate.Id == newerTaskId)
            .OrderBy(candidate => candidate.CreatedSequence)
            .ToListAsync();
        Assert.Equal([olderTaskId, newerTaskId], migrated.Select(candidate => candidate.Id));
        Assert.All(migrated, candidate => Assert.Equal(0, candidate.ExecutionState));
        Assert.True(migrated[0].CreatedSequence > 0);
        Assert.True(migrated[1].CreatedSequence > migrated[0].CreatedSequence);
        verification.GpuMiniTasks.Add(new GpuMiniTaskEntity
        {
            Id = Guid.NewGuid(), ParentJobId = jobId, SourceRevision = 1, PriorityLane = 0,
            ModelRuntimeKey = "scheduler-runtime", SettingsFingerprint = "scheduler-settings",
            EstimatedBytes = 1, IdempotencyKey = "scheduler-migration-new-row", ExecutionState = 0,
            CreatedAtUtc = now.AddMinutes(1)
        });
        verification.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
        {
            SlotKey = "slot-a", State = 0, UpdatedAtUtc = now
        });
        await verification.SaveChangesAsync();
        Assert.True(await verification.GpuMiniTasks
            .Where(candidate => candidate.IdempotencyKey == "scheduler-migration-new-row")
            .Select(candidate => candidate.CreatedSequence)
            .SingleAsync() > migrated[1].CreatedSequence);
        Assert.Equal(1, await verification.GpuSchedulerStates.CountAsync(state => state.Id == 1));

        var store = new SqlGpuSchedulerStore(new DirectContextFactory(database.ConnectionString));
        var admission = await store.RunAdmissionRoundAsync(
            Guid.NewGuid(),
            GpuSchedulerWakeReason.WorkReady,
            new GpuSchedulerOptions(1, 1, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)),
            (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "slot-a", "test-owner", null)),
            CancellationToken.None);
        Assert.True(admission.Committed);
        await using var selected = database.CreateContext();
        Assert.Equal(olderTaskId, await selected.GpuMiniTasks
            .Where(candidate => candidate.ExecutionState == (int)GpuMiniTaskExecutionState.Active)
            .Select(candidate => candidate.Id)
            .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Gpu_scheduler_binary_fence_migration_upgrades_existing_slot_and_batch_constraints()
    {
        await using var database = await fixture.CreateGpuSchedulerFencePreviousMigrationDatabaseAsync();
        var now = DateTimeOffset.Parse("2026-08-02T18:00:00+00:00");
        var batchId = Guid.NewGuid();

        await using (var context = database.CreateContext())
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
            {
                SlotKey = "slot-a",
                State = 0,
                UpdatedAtUtc = now
            });
            context.GpuBatches.Add(new GpuBatchEntity
            {
                Id = batchId,
                CapacitySlotKey = "slot-a",
                PriorityLane = 0,
                ModelRuntimeKey = "runtime-a",
                SettingsFingerprint = "settings-a",
                ItemCount = 1,
                EstimatedBytes = 1,
                AdmissionGeneration = 1,
                OwnerKey = "owner-a",
                State = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await context.SaveChangesAsync();

            await context.GetService<IMigrator>().MigrateAsync();
        }

        await using var verification = database.CreateContext();
        Assert.Equal(batchId, await verification.GpuBatches
            .Where(candidate => candidate.CapacitySlotKey == "slot-a")
            .Select(candidate => candidate.Id)
            .SingleAsync());

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var collationCommand = new SqlCommand(
            """
            SELECT [collation_name]
            FROM sys.columns
            WHERE [object_id] = OBJECT_ID(N'[GpuCapacitySlots]')
              AND [name] = N'SlotKey';
            """,
            connection);
        Assert.Equal(
            "Latin1_General_100_BIN2",
            Convert.ToString(await collationCommand.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));

        await using var foreignKeyCommand = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE [name] = N'FK_GpuBatches_GpuCapacitySlots_CapacitySlotKey';
            """,
            connection);
        Assert.Equal(1, Convert.ToInt32(await foreignKeyCommand.ExecuteScalarAsync()));
    }

    [NativeSqlServerFact]
    public async Task Gpu_scheduler_receipt_upgrade_adds_the_durable_consumption_token_column()
    {
        await using var database = await fixture.CreateGpuSchedulerReceiptPreviousMigrationDatabaseAsync();
        await using (var context = database.CreateContext())
        {
            await context.GetService<IMigrator>().MigrateAsync();
        }

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var columnCommand = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE [object_id] = OBJECT_ID(N'[GpuSchedulerOperationReceipts]')
              AND [name] = N'WakeConsumptionOperationId'
              AND [system_type_id] = TYPE_ID(N'uniqueidentifier');
            """,
            connection);

        Assert.Equal(1, Convert.ToInt32(await columnCommand.ExecuteScalarAsync()));
    }

    [NativeSqlServerFact]
    public async Task Gpu_scheduler_opaque_key_migration_fails_safely_on_existing_trailing_whitespace()
    {
        await using var database = await fixture.CreateGpuSchedulerOpaqueKeyPreviousMigrationDatabaseAsync();
        var now = DateTimeOffset.Parse("2026-08-02T19:00:00+00:00");

        await using (var context = database.CreateContext())
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
            {
                SlotKey = "slot-a ",
                State = 0,
                UpdatedAtUtc = now
            });
            await context.SaveChangesAsync();

            var failure = await Assert.ThrowsAsync<SqlException>(
                async () => await context.GetService<IMigrator>().MigrateAsync());
            Assert.Equal(547, failure.Number);
        }

        await using var verification = database.CreateContext();
        Assert.Equal(
            "slot-a ",
            await verification.GpuCapacitySlots
                .Select(candidate => candidate.SlotKey)
                .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Gpu_scheduler_opaque_key_migration_fails_safely_on_existing_empty_required_key()
    {
        await using var database = await fixture.CreateGpuSchedulerOpaqueKeyPreviousMigrationDatabaseAsync();
        var now = DateTimeOffset.Parse("2026-08-02T19:00:00+00:00");

        await using (var context = database.CreateContext())
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
            {
                SlotKey = string.Empty,
                State = 0,
                UpdatedAtUtc = now
            });
            await context.SaveChangesAsync();

            var failure = await Assert.ThrowsAsync<SqlException>(
                async () => await context.GetService<IMigrator>().MigrateAsync());
            Assert.Equal(547, failure.Number);
        }

        await using var verification = database.CreateContext();
        Assert.Equal(
            string.Empty,
            await verification.GpuCapacitySlots
                .Select(candidate => candidate.SlotKey)
                .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Gpu_scheduler_opaque_key_migration_fails_safely_on_existing_empty_nullable_key()
    {
        await using var database = await fixture.CreateGpuSchedulerOpaqueKeyPreviousMigrationDatabaseAsync();
        var now = DateTimeOffset.Parse("2026-08-02T19:00:00+00:00");

        await using (var context = database.CreateContext())
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
            {
                SlotKey = "slot-a",
                OwnerKey = string.Empty,
                State = 0,
                UpdatedAtUtc = now
            });
            await context.SaveChangesAsync();

            var failure = await Assert.ThrowsAsync<SqlException>(
                async () => await context.GetService<IMigrator>().MigrateAsync());
            Assert.Equal(547, failure.Number);
        }

        await using var verification = database.CreateContext();
        Assert.Equal(
            string.Empty,
            await verification.GpuCapacitySlots
                .Select(candidate => candidate.OwnerKey)
                .SingleAsync());
    }

    private sealed class DirectContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options);

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
