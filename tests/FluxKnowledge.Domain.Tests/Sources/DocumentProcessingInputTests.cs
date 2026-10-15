using System.Security.Cryptography;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class DocumentProcessingInputTests
{
    [Theory]
    [InlineData("vsdx")]
    [InlineData("visio")]
    [InlineData("pdf")]
    [InlineData("jpg")]
    [InlineData("png")]
    public void Each_document_input_has_a_parent_revision_locator_and_preserves_its_container_reference(string kind)
    {
        byte[] bytes = [1, 2, 3];
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var firstClaim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "same-parent", hash,
            "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var secondClaim = firstClaim with { SourceRevisionId = SourceRevisionId.New() };
        RetainedProcessorDerivedChild Create(RetainedProcessorClaim claim)
        {
            var retained = new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length);
            return kind switch
            {
                "vsdx" => DocumentProcessingInput.CreateVsdxChild(claim, retained),
                "visio" => DocumentProcessingInput.CreateVisioChild(claim, retained),
                "pdf" => DocumentProcessingInput.CreatePdfChild(claim, retained),
                _ => DocumentProcessingInput.CreateImageChild(claim, retained, $".{kind}")
            };
        }

        var first = Create(firstClaim);
        var second = Create(secondClaim);
        Assert.NotEqual(first.SyntheticLocator, second.SyntheticLocator);
        Assert.Equal(first.StableSourceIdentity, second.StableSourceIdentity);
        Assert.Equal(first.MemberFingerprint, second.MemberFingerprint);
        Assert.Equal(second, Create(secondClaim));
        Assert.Equal(hash, second.ContentSha256);
        Assert.Equal(bytes.Length, second.ByteLength);
        Assert.Equal(string.Empty, second.StoreRelativePath);
        Assert.Equal(2, second.OriginKind);
        Assert.True(DocumentProcessingInput.TryGetContract(second.Classification, second.Extension, out _));
    }
}
