using FluxKnowledge.Domain.Knowledge;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Knowledge;

public sealed class KnowledgeClaimTests
{
    [Fact]
    public void Create_canonicalises_subject_predicate_and_object_for_a_stable_identity()
    {
        var claim = KnowledgeClaim.Create("  Project Atlas  ", " OWNS ", "  Retained corpus  ", 0.85m);

        Assert.Equal("project atlas", claim.Subject);
        Assert.Equal("owns", claim.Predicate);
        Assert.Equal("retained corpus", claim.ObjectText);
        Assert.Equal("project atlas\u001fowns\u001fretained corpus", claim.CanonicalIdentity);
    }
}
