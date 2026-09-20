using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Prepares one retained input for the exact interactive desktop command, never for IIS execution.</summary>
public sealed class VisioDocumentInputProcessor : ILocalSourceCapabilityHandler
{
    public static readonly SourceCapabilityDescriptor Capability = new(
        new Guid("93a2ce7d-a168-4d3e-a40e-5c51c991d786"),
        "document-vsdx-visio-extract", DocumentProcessingInput.Visio.ParentProcessorVersion,
        ExecutionClass.InProcess, DocumentProcessingInput.Visio.ParentProcessorFingerprint,
        SourceActivityKind.TextExtraction, "VsdxDocumentContainer", "retained:document-vsdx-visio-extract");

    public SourceCapabilityDescriptor Descriptor => Capability;

    public async ValueTask<RetainedProcessorCompletion> ProcessAsync(
        RetainedProcessorClaim claim, RetainedSourceBytes retained, CancellationToken cancellationToken)
    {
        if (retained.ContentSha256 != claim.InputSha256 ||
            Convert.ToHexStringLower(SHA256.HashData(retained.Bytes)) != claim.InputSha256)
        {
            throw new RetainedProcessorException("retained-artifact-checksum-invalid");
        }
        await VisioDocumentPreflight.ValidateAsync(retained, cancellationToken).ConfigureAwait(false);
        var child = DocumentProcessingInput.CreateVisioChild(claim, retained);
        var receipt = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"completed:{child.MemberFingerprint}:{child.ContentSha256}:{child.ByteLength}")));
        return new RetainedProcessorCompletion([child], receipt);
    }
}
