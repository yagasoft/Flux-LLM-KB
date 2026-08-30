using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.IntegrationV1;

/// <summary>Transport-neutral entry point for a caller's closed native v1 action family.</summary>
public sealed class NativeOperationService
{
    private readonly INativeOperationStore _store;
    private readonly IReadOnlyDictionary<string, NativeActionDefinition> _actions;

    public NativeOperationService(INativeOperationStore store, IEnumerable<NativeActionDefinition> actions)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(actions);
        _actions = actions.ToDictionary(
            definition => NativeOperationCanonicalization.CanonicalizeAction(definition.Action),
            definition => definition,
            StringComparer.Ordinal);
    }

    internal INativeOperationStore Store => _store;

    public async ValueTask<NativeActionPreview> PreviewAsync(
        NativeActionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(request.Action, request.CanonicalPayload, request.ActorSurface, cancellationToken);
        return await _store.CreatePreviewAsync(
            request with
            {
                Action = prepared.Action,
                CanonicalPayload = prepared.CanonicalPayload,
                Targets = prepared.Targets,
                RequestFingerprint = prepared.RequestFingerprint,
                EffectSummary = prepared.Definition.EffectSummary
            },
            cancellationToken);
    }

    public async ValueTask<NativeActionReceipt> CommitAsync(
        NativeActionCommitRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        var canonicalAction = NativeOperationCanonicalization.CanonicalizeAction(request.Action);
        var canonicalPayload = NativeOperationCanonicalization.CanonicalizeJson(request.CanonicalPayload);
        if (string.IsNullOrWhiteSpace(request.ActorSurface) || request.ActorSurface.Length > 64) throw new NativeOperationException("invalid-actor-surface");
        var replay = await _store.TryReplayAsync(canonicalAction, canonicalPayload, request.ConfirmationId, request.IdempotencyKey, request.ActorSurface, cancellationToken);
        if (replay is not null) return replay;
        var prepared = await PrepareAsync(request.Action, request.CanonicalPayload, request.ActorSurface, cancellationToken);
        var operation = await prepared.Definition.ResolveCommitOperationAsync(
            prepared.CanonicalPayload,
            prepared.Targets,
            cancellationToken);
        return await _store.CommitAsync(
            request with
            {
                Action = prepared.Action,
                CanonicalPayload = prepared.CanonicalPayload,
                Targets = prepared.Targets,
                RequestFingerprint = prepared.RequestFingerprint,
                CommitOperation = operation
            },
            cancellationToken);
    }

    private async ValueTask<PreparedRequest> PrepareAsync(
        string action,
        string payload,
        string actorSurface,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorSurface) || actorSurface.Length > 64)
        {
            throw new NativeOperationException("invalid-actor-surface");
        }

        var canonicalAction = NativeOperationCanonicalization.CanonicalizeAction(action);
        if (!_actions.TryGetValue(canonicalAction, out var definition))
        {
            throw new NativeOperationException("action-not-allowed");
        }

        var canonicalPayload = NativeOperationCanonicalization.CanonicalizeJson(payload);
        var targets = NativeOperationCanonicalization.CanonicalizeTargets(
            await definition.ResolveTargetsAsync(canonicalPayload, cancellationToken));
        return new PreparedRequest(
            canonicalAction,
            canonicalPayload,
            targets,
            NativeOperationCanonicalization.CreateRequestFingerprint(canonicalAction, canonicalPayload, targets),
            definition);
    }

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 ||
            idempotencyKey.Any(character => character is < '!' or > '~'))
        {
            throw new NativeOperationException("invalid-idempotency-key");
        }
    }

    private sealed record PreparedRequest(
        string Action,
        string CanonicalPayload,
        IReadOnlyList<NativeTargetVersion> Targets,
        string RequestFingerprint,
        NativeActionDefinition Definition);
}
