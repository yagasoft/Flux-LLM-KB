using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Knowledge;

namespace FluxKnowledge.Application.Knowledge;

public sealed record KnowledgeMutation(
    string Action, string? ItemId, string? Title, string? Body,
    string? Subject, string? Predicate, string? ObjectText,
    string? Transition, string? RelatedClaimId, string? Reason, decimal? Confidence = null);

public sealed record KnowledgeTarget(string TargetId, string RowVersion);

/// <summary>Closed mutation understood only by the native operation store's SQL transaction.</summary>
public sealed record KnowledgeMutationCommitOperation(KnowledgeMutation Mutation) : NativeActionCommitOperation;

public interface IKnowledgeCommandService
{
    ValueTask<NativeActionPreview> PreviewAsync(KnowledgeMutation command, string surface, CancellationToken cancellationToken);
    ValueTask<NativeActionReceipt> CommitAsync(KnowledgeMutation command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken);
}

/// <summary>Confirmation-bound native note and claim commands.</summary>
public sealed class KnowledgeCommandService : IKnowledgeCommandService
{
    private readonly NativeOperationService _operations;
    private readonly ILocalPrivateContentDisclosure _disclosure;

    public KnowledgeCommandService(
        INativeOperationStore operationStore,
        IKnowledgeStore knowledgeStore,
        ILocalPrivateContentDisclosure disclosure)
    {
        ArgumentNullException.ThrowIfNull(operationStore);
        ArgumentNullException.ThrowIfNull(knowledgeStore);
        _disclosure = disclosure ?? throw new ArgumentNullException(nameof(disclosure));
        _operations = new NativeOperationService(operationStore,
        [
            new NativeActionDefinition("note_create", "Create native knowledge note", (payload, cancellationToken) => ResolveTargetsAsync(knowledgeStore, payload, cancellationToken), ResolveOperationAsync),
            new NativeActionDefinition("claim_upsert", "Create or revise native knowledge claim", (payload, cancellationToken) => ResolveTargetsAsync(knowledgeStore, payload, cancellationToken), ResolveOperationAsync),
            new NativeActionDefinition("claim_transition", "Transition native knowledge claim lifecycle", (payload, cancellationToken) => ResolveTargetsAsync(knowledgeStore, payload, cancellationToken), ResolveOperationAsync),
            new NativeActionDefinition("forget", "Forget active native knowledge content", (payload, cancellationToken) => ResolveTargetsAsync(knowledgeStore, payload, cancellationToken), ResolveOperationAsync)
        ]);
    }

    public ValueTask<NativeActionPreview> PreviewAsync(KnowledgeMutation command, string surface, CancellationToken cancellationToken)
    {
        var canonical = Canonicalise(command, _disclosure);
        return _operations.PreviewAsync(new NativeActionPreviewRequest(canonical.Action, Serialize(canonical), surface), cancellationToken);
    }

    public ValueTask<NativeActionReceipt> CommitAsync(KnowledgeMutation command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken)
    {
        var canonical = Canonicalise(command, _disclosure);
        return _operations.CommitAsync(new NativeActionCommitRequest(canonical.Action, Serialize(canonical), confirmationId, idempotencyKey, surface), cancellationToken);
    }

    internal static KnowledgeMutation Parse(string payload) => JsonSerializer.Deserialize<KnowledgeMutation>(payload, JsonOptions)
        ?? throw new NativeOperationException("invalid-knowledge-mutation");

    internal static string Serialize(KnowledgeMutation mutation) => JsonSerializer.Serialize(mutation, JsonOptions);

    private static async ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveTargetsAsync(IKnowledgeStore store, string payload, CancellationToken cancellationToken)
    {
        var target = await store.FindTargetAsync(Parse(payload), cancellationToken);
        return target is null ? [] : [new NativeTargetVersion(target.TargetId, target.RowVersion)];
    }

    private static ValueTask<NativeActionCommitOperation> ResolveOperationAsync(string payload, IReadOnlyList<NativeTargetVersion> _, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<NativeActionCommitOperation>(new KnowledgeMutationCommitOperation(Parse(payload)));
    }

    internal static KnowledgeMutation Canonicalise(KnowledgeMutation command, ILocalPrivateContentDisclosure disclosure)
    {
        ArgumentNullException.ThrowIfNull(command);
        var action = NativeOperationCanonicalization.CanonicalizeAction(command.Action);
        if (action is not ("note_create" or "claim_upsert" or "claim_transition" or "forget"))
        {
            throw new NativeOperationException("action-not-allowed");
        }

        static string? Scan(string? value, ILocalPrivateContentDisclosure policy)
        {
            if (value is null) return null;
            var result = policy.Evaluate(value, LocalDisclosureKind.KnowledgeWrite);
            if (result.Withheld) throw new NativeOperationException(result.ReasonCode ?? "secret-content-withheld");
            return result.Value;
        }

        var safe = command with
        {
            Action = action,
            ItemId = Scan(command.ItemId, disclosure), Title = Scan(command.Title, disclosure), Body = Scan(command.Body, disclosure),
            Subject = Scan(command.Subject, disclosure), Predicate = Scan(command.Predicate, disclosure), ObjectText = Scan(command.ObjectText, disclosure),
            Transition = Scan(command.Transition, disclosure), RelatedClaimId = Scan(command.RelatedClaimId, disclosure), Reason = Scan(command.Reason, disclosure)
        };

        return action switch
        {
            "note_create" when safe.Title is not null && safe.Body is not null => safe with { ItemId = safe.ItemId ?? StableId(action, safe.Title, safe.Body) },
            "claim_upsert" when safe.Subject is not null && safe.Predicate is not null && safe.ObjectText is not null && safe.Confidence is >= 0m and <= 1m => CanonicaliseClaim(safe),
            "claim_transition" when Guid.TryParse(safe.ItemId, out _) && safe.Transition is not null => safe,
            "forget" when Guid.TryParse(safe.ItemId, out _) => safe,
            _ => throw new NativeOperationException("invalid-knowledge-mutation")
        };
    }

    private static string StableId(string action, params string[] values)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', [action, .. values])));
        return new Guid(bytes[..16]).ToString("D");
    }

    private static KnowledgeMutation CanonicaliseClaim(KnowledgeMutation mutation)
    {
        var claim = KnowledgeClaim.Create(mutation.Subject!, mutation.Predicate!, mutation.ObjectText!, mutation.Confidence!.Value);
        return mutation with { Subject = claim.Subject, Predicate = claim.Predicate, ObjectText = claim.ObjectText };
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
}
