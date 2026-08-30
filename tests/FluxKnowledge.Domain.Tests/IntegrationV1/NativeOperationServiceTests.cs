using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using Xunit;

namespace FluxKnowledge.Domain.Tests.IntegrationV1;

public sealed class NativeOperationServiceTests
{
    [Fact]
    public async Task PreviewAsync_canonicalises_action_json_and_target_identifiers_before_persisting()
    {
        var store = new RecordingStore();
        var service = new NativeOperationService(
            store,
            [new NativeActionDefinition("knowledge.create", "Create knowledge", static (_, _) => ValueTask.FromResult<IReadOnlyList<NativeTargetVersion>>(
                [new NativeTargetVersion(" NOTE-42 ", "AAAAAAAAAAA=")]), static (_, _, _) => ValueTask.FromResult<NativeActionCommitOperation>(new NativeFenceTargetMutation("note-42", "created")))]);

        await service.PreviewAsync(
            new NativeActionPreviewRequest(" KNOWLEDGE.CREATE ", "{\"z\":1,\"a\":{\"b\":2,\"a\":1}}", "mcp"),
            CancellationToken.None);

        Assert.NotNull(store.PreviewRequest);
        Assert.Equal("knowledge.create", store.PreviewRequest!.Action);
        Assert.Equal("{\"a\":{\"a\":1,\"b\":2},\"z\":1}", store.PreviewRequest.CanonicalPayload);
        Assert.Equal("note-42", Assert.Single(store.PreviewRequest.Targets).TargetId);
        Assert.Equal(64, store.PreviewRequest.RequestFingerprint.Length);
    }

    [Fact]
    public async Task CommitAsync_rejects_non_ascii_or_oversized_idempotency_keys_before_the_store()
    {
        var store = new RecordingStore();
        var service = CreateService(store);

        var exception = await Assert.ThrowsAsync<NativeOperationException>(async () =>
            await service.CommitAsync(
                new NativeActionCommitRequest("knowledge.create", "{}", "confirmation", "é", "mcp"),
                CancellationToken.None));

        Assert.Equal("invalid-idempotency-key", exception.ReasonCode);
        Assert.Null(store.CommitRequest);
    }

    [Fact]
    public async Task PreviewAsync_rejects_actions_outside_the_caller_supplied_allowlist()
    {
        var store = new RecordingStore();
        var service = CreateService(store);

        var exception = await Assert.ThrowsAsync<NativeOperationException>(async () =>
            await service.PreviewAsync(new NativeActionPreviewRequest("knowledge.delete", "{}", "mcp"), CancellationToken.None));

        Assert.Equal("action-not-allowed", exception.ReasonCode);
        Assert.Null(store.PreviewRequest);
    }

    private static NativeOperationService CreateService(INativeOperationStore store) =>
        new(store,
            [new NativeActionDefinition("knowledge.create", "Create knowledge", static (_, _) => ValueTask.FromResult<IReadOnlyList<NativeTargetVersion>>([]), static (_, _, _) => ValueTask.FromResult<NativeActionCommitOperation>(new NativeFenceTargetMutation("none", "none")))]);

    private sealed class RecordingStore : INativeOperationStore
    {
        public NativeActionPreviewRequest? PreviewRequest { get; private set; }
        public NativeActionCommitRequest? CommitRequest { get; private set; }

        public ValueTask<NativeActionReceipt?> TryReplayAsync(string action, string canonicalPayload, string confirmationId, string idempotencyKey, string actorSurface, CancellationToken cancellationToken) => ValueTask.FromResult<NativeActionReceipt?>(null);

        public ValueTask<NativeActionPreview> CreatePreviewAsync(NativeActionPreviewRequest request, CancellationToken cancellationToken)
        {
            PreviewRequest = request;
            return ValueTask.FromResult(new NativeActionPreview(
                Guid.NewGuid(),
                "confirmation",
                request.RequestFingerprint,
                DateTimeOffset.UtcNow.AddMinutes(5),
                request.Targets,
                request.EffectSummary));
        }

        public ValueTask<NativeActionReceipt> CommitAsync(NativeActionCommitRequest request, CancellationToken cancellationToken)
        {
            CommitRequest = request;
            return ValueTask.FromResult(new NativeActionReceipt(Guid.NewGuid(), false, "completed", null));
        }
    }
}
