using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Ports;

/// <summary>Persistence boundary for immutable source revisions and scan retention evidence.</summary>
public interface ISourceScanStore
{
    ValueTask<SourceRevisionId> ConvergeRevisionAndArtifactAsync(
        SourceRootConfiguration sourceRoot,
        SourceDiscoveredFile file,
        SourceArtifactReceipt receipt,
        CancellationToken cancellationToken);

    ValueTask<SourceRetentionConvergence> ConvergeBlockedRevisionAsync(
        SourceRootConfiguration sourceRoot,
        SourceDiscoveredFile file,
        string reason,
        CancellationToken cancellationToken);

    ValueTask SuppressUnseenAsync(
        SourceRootId sourceRootId,
        IReadOnlySet<SourceRevisionId> convergedRevisionIds,
        CancellationToken cancellationToken);

    ValueTask RecordEnumerationEvidenceAsync(
        SourceScanRequestId sourceScanRequestId,
        IReadOnlyList<SourceEnumerationEvidence> evidence,
        CancellationToken cancellationToken);
}
