using FluxKnowledge.Application.IntegrationV1;

namespace FluxKnowledge.Application.Ports;

/// <summary>Prepares closed corpus mutations from authoritative source/job state without exposing an executor.</summary>
public interface INativeCorpusActionStore
{
    ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveTargetsAsync(string action, string canonicalPayload, CancellationToken cancellationToken);
    ValueTask<NativeActionCommitOperation> CreateCommitOperationAsync(string action, string canonicalPayload, IReadOnlyList<NativeTargetVersion> targets, CancellationToken cancellationToken);
}
