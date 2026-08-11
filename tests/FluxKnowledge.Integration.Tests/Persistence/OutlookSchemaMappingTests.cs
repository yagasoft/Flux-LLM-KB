using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class OutlookSchemaMappingTests
{
    [Fact]
    public void Schema_excludes_raw_mail_attachment_and_credential_columns()
    {
        using var context = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=FluxKnowledge_mapping_only;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
        var names = context.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()).Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Attachment", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("RawContent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Private_identifiers_are_not_present_in_local_projection_contract()
    {
        var privateProperties = typeof(OutlookCaptureFolderEntity).GetProperties().Select(property => property.Name).ToArray();
        var projectionProperties = typeof(FluxKnowledge.Application.Contracts.OutlookProfileProjection).GetProperties().Select(property => property.Name).ToArray();

        Assert.Contains("StoreId", privateProperties);
        Assert.Contains("FolderEntryId", privateProperties);
        Assert.DoesNotContain("StoreId", projectionProperties);
        Assert.DoesNotContain("FolderEntryId", projectionProperties);
        Assert.DoesNotContain("SpoolRoot", projectionProperties);
    }

    [Fact]
    public void Canonical_private_identities_use_indexable_sha256_digests()
    {
        using var context = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=FluxKnowledge_mapping_only;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
        var folder = context.Model.FindEntityType(typeof(FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.OutlookCaptureFolderEntity))!;
        var export = context.Model.FindEntityType(typeof(FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.OutlookCaptureExportEntity))!;

        Assert.NotNull(folder.FindProperty("CanonicalIdentityFingerprint"));
        Assert.NotNull(export.FindProperty("EntryIdFingerprint"));
        Assert.Contains(folder.GetIndexes(), index => index.IsUnique && index.Properties.Any(property => property.Name == "CanonicalIdentityFingerprint"));
        Assert.Contains(export.GetIndexes(), index => index.IsUnique && index.Properties.Any(property => property.Name == "EntryIdFingerprint"));
    }

    [Fact]
    public void Only_blocked_exports_may_omit_unresolvable_private_identity_keys()
    {
        using var context = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=FluxKnowledge_mapping_only;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
        var export = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(OutlookCaptureExportEntity))!;

        Assert.True(export.FindProperty(nameof(OutlookCaptureExportEntity.ProfileId))!.IsNullable);
        Assert.True(export.FindProperty(nameof(OutlookCaptureExportEntity.FolderId))!.IsNullable);
        Assert.Contains(
            export.GetCheckConstraints(),
            constraint => constraint.Name == "CK_OutlookCaptureExports_IdentityRequiredUnlessBlocked" &&
                constraint.Sql == "([State] = 4 AND [ProfileId] IS NULL AND [FolderId] IS NULL) OR " +
                    "([ProfileId] IS NOT NULL AND [FolderId] IS NOT NULL)");
    }
}
