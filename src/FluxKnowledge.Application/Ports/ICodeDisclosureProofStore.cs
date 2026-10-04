using FluxKnowledge.Application.Visibility;

namespace FluxKnowledge.Application.Ports;

public sealed record CodeDisclosureArtifact(
    Guid ArtifactId, Guid PipelineRecordId, long SourceRevision, string CanonicalHash,
    int CanonicalLength, string? Text);

public interface ICodeDisclosureProofStore
{
    ValueTask<CodeDisclosureArtifact?> ReadNextAsync(CancellationToken cancellationToken);
    ValueTask<bool> CommitAsync(CodeDisclosureArtifact artifact, CodeDisclosureProof proof, CancellationToken cancellationToken);
}
