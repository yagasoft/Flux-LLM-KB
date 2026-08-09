using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceRevisionTests
{
    private const string ContentSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("documents/readme.md")]
    [InlineData("C:\\Corpus\\documents\\..\\readme.md")]
    public void Revision_rejects_paths_that_are_not_canonical_absolute_paths(string canonicalPath)
    {
        Assert.Throws<DomainInvariantException>(
            () => SourceRevision.Create(
                SourceRootId.New(),
                "stable-file-identity",
                revision: 1,
                contentSha256: ContentSha256,
                canonicalPath: canonicalPath,
                parentRevisionId: null,
                classification: "utf8-text",
                byteLength: 10));
    }
}
