using System.Security.Cryptography;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class DocumentProcessingInputTests
{
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".pdf")]
    public void A_recovery_has_a_distinct_repeatable_child_without_changing_ordinary_document_identity(string extension)
    {
        byte[] bytes = [1, 2, 3];
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var ordinary = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "same-parent", hash,
            "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var recovery = ordinary with { DocumentRecoveryId = Guid.NewGuid() };
        var retained = new RetainedSourceBytes(ordinary.SourceRevisionId, bytes, hash, bytes.Length);
        RetainedProcessorDerivedChild Create(RetainedProcessorClaim claim) => extension == ".pdf"
            ? DocumentProcessingInput.CreatePdfChild(claim, retained)
            : DocumentProcessingInput.CreateImageChild(claim, retained, extension);
        var predecessor = Create(ordinary);
        var successor = Create(recovery);

        Assert.NotEqual(predecessor.MemberFingerprint, successor.MemberFingerprint);
        Assert.NotEqual(predecessor.StableSourceIdentity, successor.StableSourceIdentity);
        Assert.NotEqual(predecessor.SyntheticLocator, successor.SyntheticLocator);
        Assert.Equal(successor, Create(recovery));
        Assert.Equal(predecessor, Create(ordinary));
        Assert.Equal(hash, successor.ContentSha256);
        Assert.Equal(bytes.Length, successor.ByteLength);
        Assert.NotEqual(successor.MemberFingerprint,
            Create(recovery with { DocumentRecoveryId = Guid.NewGuid() }).MemberFingerprint);
    }

    [Theory]
    [InlineData("vsdx")]
    [InlineData("visio")]
    [InlineData("empty-image")]
    public void A_recovery_discriminator_cannot_change_an_ordinary_or_immutable_document_contract(string kind)
    {
        byte[] bytes = [1, 2, 3];
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "same-parent", hash,
            "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5)) { DocumentRecoveryId = kind == "empty-image" ? Guid.Empty : Guid.NewGuid() };
        var retained = new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length);

        Assert.Throws<ArgumentException>(() => kind switch
        {
            "pdf" => DocumentProcessingInput.CreatePdfChild(claim, retained),
            "vsdx" => DocumentProcessingInput.CreateVsdxChild(claim, retained),
            "visio" => DocumentProcessingInput.CreateVisioChild(claim, retained),
            _ => DocumentProcessingInput.CreateImageChild(claim, retained, ".png")
        });
    }

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
