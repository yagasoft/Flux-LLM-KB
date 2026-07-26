using System.Reflection;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Pipeline;

public sealed class PipelineRecordTests
{
    [Fact]
    public void New_revision_keeps_source_identity_and_links_to_prior_revision()
    {
        var source = SourceIdentity.ForLocalFile("C:/ingress/readme.txt");
        var first = PipelineRecord.Register(source, 1, "hash-a", null);
        var second = first.CreateRevision(2, "hash-b");

        Assert.Equal(first.SourceIdentityId, second.SourceIdentityId);
        Assert.Equal(first.Id, second.ParentRevisionRecordId);
        Assert.Equal(2, second.Revision);
        Assert.NotEqual(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void Invariant_bearing_contracts_do_not_expose_public_constructors_or_writable_state()
    {
        Type[] contractTypes =
        [
            typeof(Job),
            typeof(PipelineRecord),
            typeof(SourceIdentity),
            typeof(DispatchMessage),
            typeof(Artifact),
            typeof(GpuMiniTask)
        ];

        Assert.All(
            contractTypes,
            contractType => Assert.Empty(contractType.GetConstructors(BindingFlags.Instance | BindingFlags.Public)));
        Assert.All(
            contractTypes.SelectMany(contractType => contractType.GetProperties().Where(property => property.CanWrite)),
            property => Assert.Null(property.GetSetMethod(nonPublic: false)));
    }
}
