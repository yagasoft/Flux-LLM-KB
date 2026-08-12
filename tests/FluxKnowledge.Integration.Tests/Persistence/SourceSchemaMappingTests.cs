using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class SourceSchemaMappingTests
{
    [Fact]
    public void Phase_3a_source_schema_uses_canonical_unique_keys_and_restrict_foreign_keys()
    {
        using var context = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=SourceSchemaMappingModel;Trusted_Connection=True;TrustServerCertificate=True")
                .Options);

        var model = context.GetService<IDesignTimeModel>().Model;
        AssertPathFingerprintIsIndexable(model.FindEntityType(typeof(SourceRootConfigurationEntity))!);
        AssertPathFingerprintIsIndexable(model.FindEntityType(typeof(SourceRevisionEntity))!);
        Assert.Contains(
            model.FindEntityType(typeof(SourceRootConfigurationEntity))!.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(SourceRootConfigurationEntity.CanonicalPathFingerprint)]));
        Assert.Contains(
            model.FindEntityType(typeof(SourceRevisionEntity))!.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(
                [
                    nameof(SourceRevisionEntity.SourceRootId),
                    nameof(SourceRevisionEntity.CanonicalPathFingerprint),
                    nameof(SourceRevisionEntity.ContentSha256)
                ]));
        AssertSqlServerIndexKeyIsBounded(
            model.FindEntityType(typeof(SourceRootConfigurationEntity))!,
            nameof(SourceRootConfigurationEntity.CanonicalPathFingerprint));
        AssertSqlServerIndexKeyIsBounded(
            model.FindEntityType(typeof(SourceRevisionEntity))!,
            nameof(SourceRevisionEntity.SourceRootId),
            nameof(SourceRevisionEntity.CanonicalPathFingerprint),
            nameof(SourceRevisionEntity.ContentSha256));
        Assert.Contains(
            model.FindEntityType(typeof(SourceActivityEntity))!.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(
                [
                    nameof(SourceActivityEntity.SourceRevisionId),
                    nameof(SourceActivityEntity.ActivityKind),
                    nameof(SourceActivityEntity.ProcessorVersion),
                    nameof(SourceActivityEntity.DescriptorFingerprint),
                    nameof(SourceActivityEntity.InputFingerprint)
                ]));
        Assert.Contains(
            model.FindEntityType(typeof(SourceCapabilityEntity))!.GetCheckConstraints(),
            constraint => constraint.Name == "CK_SourceCapabilities_NativeExecutorLater_NotRunnable" &&
                constraint.Sql == "[ExecutionClass] <> 2 OR [IsRunnable] = CONVERT(bit, 0)");

        var immutableTypes = new[]
        {
            typeof(SourceRevisionEntity),
            typeof(SourceArtifactEntity),
            typeof(SourceActivityEntity)
        };
        Assert.All(
            immutableTypes.Select(type => model.FindEntityType(type)!),
            entity => Assert.All(entity.GetForeignKeys(), foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior)));

        AssertImmutableAfterInsert(
            model.FindEntityType(typeof(SourceRevisionEntity))!,
            nameof(SourceRevisionEntity.ContentSha256),
            nameof(SourceRevisionEntity.CanonicalPath),
            nameof(SourceRevisionEntity.StableSourceIdentity),
            nameof(SourceRevisionEntity.Revision));
        AssertImmutableAfterInsert(
            model.FindEntityType(typeof(SourceArtifactEntity))!,
            nameof(SourceArtifactEntity.ContentSha256),
            nameof(SourceArtifactEntity.StoreRelativePath),
            nameof(SourceArtifactEntity.ByteLength),
            nameof(SourceArtifactEntity.ChecksumVerifiedAtUtc));
        var pipelineRecord = model.FindEntityType(typeof(PipelineRecordEntity))!;
        AssertImmutableAfterInsert(pipelineRecord, nameof(PipelineRecordEntity.SourceRevisionId));
        Assert.Contains(
            pipelineRecord.GetForeignKeys(),
            foreignKey => foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(PipelineRecordEntity.SourceRevisionId)]) &&
                foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
    }

    private static void AssertPathFingerprintIsIndexable(IEntityType entityType)
    {
        var path = entityType.FindProperty(nameof(SourceRootConfigurationEntity.CanonicalPath))!;
        var fingerprint = entityType.FindProperty(nameof(SourceRootConfigurationEntity.CanonicalPathFingerprint))!;
        Assert.Equal(2048, path.GetMaxLength());
        Assert.Equal(64, fingerprint.GetMaxLength());
        Assert.False(fingerprint.IsUnicode());
        Assert.True(fingerprint.IsFixedLength());
        Assert.Equal("CONVERT(char(64), HASHBYTES('SHA2_256', [CanonicalPath]), 2)", fingerprint.GetComputedColumnSql());
    }

    private static void AssertImmutableAfterInsert(IEntityType entityType, params string[] propertyNames) =>
        Assert.All(
            propertyNames,
            propertyName => Assert.Equal(PropertySaveBehavior.Throw, entityType.FindProperty(propertyName)!.GetAfterSaveBehavior()));

    private static void AssertSqlServerIndexKeyIsBounded(IEntityType entityType, params string[] propertyNames)
    {
        var totalBytes = propertyNames.Sum(
            propertyName => SqlServerIndexKeyBytes(entityType.FindProperty(propertyName)!));
        Assert.InRange(totalBytes, 1, 1700);
    }

    private static int SqlServerIndexKeyBytes(IProperty property) => property.ClrType switch
    {
        var type when type == typeof(Guid) => 16,
        var type when type == typeof(long) => 8,
        var type when type == typeof(int) => 4,
        var type when type == typeof(bool) => 1,
        var type when type == typeof(string) => property.GetMaxLength()!.Value * (property.IsUnicode() == true ? 2 : 1),
        _ => throw new InvalidOperationException($"Unsupported index-key type: {property.ClrType}.")
    };
}
