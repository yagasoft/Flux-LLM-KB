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

    // Implementations without transactional lease/configuration fencing must refuse Git deletion reconciliation.
    ValueTask<bool> SuppressUnseenAuthoritativelyAsync(
        SourceRootConfiguration root, SourceScanRequest request, GitInventoryEvidence inventory,
        IReadOnlySet<SourceRevisionId> convergedRevisionIds, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    ValueTask RecordEnumerationEvidenceAsync(
        SourceScanRequestId sourceScanRequestId,
        IReadOnlyList<SourceEnumerationEvidence> evidence,
        CancellationToken cancellationToken);
}
