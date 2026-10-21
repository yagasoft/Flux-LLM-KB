using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class SegmentedIndexSchemaTests
{
    [Fact]
    public void Segmented_schema_is_additive_default_legacy_and_has_the_required_integrity_keys()
    {
        using var context = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer("Server=localhost;Database=FluxKnowledge_DesignTimeOnly;Integrated Security=true;TrustServerCertificate=true").Options);
        Assert.Equal(1, new IndexStateEntity().RequiredIndexFormat);
        Assert.Equal(1, new IndexGenerationEntity().FormatVersion);
        Assert.Equal(1, context.Model.FindEntityType(typeof(IndexGenerationEntity))!.FindProperty("FormatVersion")!.GetDefaultValue());
        Assert.Equal(new[] { "SegmentId", "VectorId" }, context.Model.FindEntityType(typeof(IndexSegmentVectorEntity))!
            .FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(new[] { "CorpusEpoch", "VectorId" }, context.Model.FindEntityType(typeof(CurrentIndexMemberEntity))!
            .FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(new[] { "GenerationId", "SegmentId" }, context.Model.FindEntityType(typeof(IndexGenerationSegmentEntity))!
            .FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.All(context.Model.FindEntityType(typeof(IndexSegmentVectorEntity))!.GetForeignKeys(),
            foreign => Assert.Equal(DeleteBehavior.Restrict, foreign.DeleteBehavior));
        Assert.NotNull(context.Model.FindEntityType(typeof(IndexPublicationCandidateEntity))!.FindProperty("ExpectedCorpusVersion"));
        Assert.NotNull(context.Model.FindEntityType(typeof(IndexSegmentOwnerEntity))!.FindProperty("OwnerStartedAtUtc"));
        Assert.NotNull(context.Model.FindEntityType(typeof(IndexProjectionChangeEntity))!.FindProperty("ScopeId"));
    }
}
