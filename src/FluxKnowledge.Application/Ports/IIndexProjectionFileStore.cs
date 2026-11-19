using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Application.Ports;

/// <summary>Deletes only an exact sealed app-owned object after the SQL retirement gate grants authority.</summary>
public interface IIndexProjectionFileStore
{
    // Zero is valid for metadata-only stores that never read file bodies.
    long MaximumSegmentVerificationBytes(IndexSegmentDescriptor segment);
    long MaximumManifestVerificationBytes(IndexGenerationDescriptor generation);
    ValueTask DeleteSegmentAsync(IndexSegmentDescriptor segment, CancellationToken cancellationToken);
    ValueTask DeleteManifestAsync(IndexGenerationDescriptor generation, CancellationToken cancellationToken);
}
