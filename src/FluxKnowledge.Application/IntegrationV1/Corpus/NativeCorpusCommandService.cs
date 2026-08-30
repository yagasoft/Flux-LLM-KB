using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Application.IntegrationV1.Corpus;

public sealed record NativeCorpusMutation(string Action, JsonElement Payload);
public sealed record NativeCorpusMutationCommitOperation(string Action, string CanonicalPayload, SourceRootPathValidation? RootAdmission = null) : NativeActionCommitOperation;

public sealed class NativeCorpusCommandService
{
    private static readonly IReadOnlyDictionary<string, string> Effects = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["root_create"] = "Queue source-root creation.",
        ["root_update"] = "Update the selected source root and queue reconciliation.",
        ["root_disable"] = "Disable the selected source root.",
        ["source_sync"] = "Queue source synchronisation.",
        ["watcher_set"] = "Set persisted watcher state.",
        ["job_retry"] = "Queue a supported job retry."
    };
    private readonly NativeOperationService _operations;

    public NativeCorpusCommandService(NativeOperationService operations, INativeCorpusActionStore actionStore)
        : this((operations ?? throw new ArgumentNullException(nameof(operations))).Store, actionStore)
    {
    }

    public NativeCorpusCommandService(INativeOperationStore operationStore, INativeCorpusActionStore actionStore)
    {
        ArgumentNullException.ThrowIfNull(operationStore);
        ArgumentNullException.ThrowIfNull(actionStore);
        _operations = new NativeOperationService(operationStore, Effects.Select(pair => new NativeActionDefinition(
            pair.Key, pair.Value,
            (payload, token) => actionStore.ResolveTargetsAsync(pair.Key, payload, token),
            (payload, targets, token) => actionStore.CreateCommitOperationAsync(pair.Key, payload, targets, token))));
    }

    public ValueTask<NativeActionPreview> PreviewAsync(NativeCorpusMutation command, string surface, CancellationToken cancellationToken) =>
        _operations.PreviewAsync(ToPreview(command, surface), cancellationToken);

    public ValueTask<NativeActionReceipt> CommitAsync(NativeCorpusMutation command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) =>
        _operations.CommitAsync(new NativeActionCommitRequest(Action(command), Payload(command), confirmationId, idempotencyKey, surface), cancellationToken);

    private static NativeActionPreviewRequest ToPreview(NativeCorpusMutation command, string surface) => new(Action(command), Payload(command), surface);
    private static string Action(NativeCorpusMutation command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var action = NativeOperationCanonicalization.CanonicalizeAction(command.Action);
        if (!Effects.ContainsKey(action)) throw new NativeOperationException("action-not-allowed");
        return action;
    }
    private static string Payload(NativeCorpusMutation command) => NativeOperationCanonicalization.CanonicalizeJson(command.Payload.GetRawText());
}
