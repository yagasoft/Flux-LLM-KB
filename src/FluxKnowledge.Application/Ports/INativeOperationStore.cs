using FluxKnowledge.Application.IntegrationV1;

namespace FluxKnowledge.Application.Ports;

public interface INativeOperationStore
{
    ValueTask<NativeActionReceipt?> FindReceiptAsync(string idempotencyKey, string actorSurface, CancellationToken cancellationToken);
    ValueTask<NativeActionReceipt?> TryReplayAsync(string action, string canonicalPayload, string confirmationId, string idempotencyKey, string actorSurface, CancellationToken cancellationToken);
    ValueTask<NativeActionPreview> CreatePreviewAsync(NativeActionPreviewRequest request, CancellationToken cancellationToken);
    ValueTask<NativeActionReceipt> CommitAsync(NativeActionCommitRequest request, CancellationToken cancellationToken);
}
