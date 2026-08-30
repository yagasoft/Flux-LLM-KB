using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;

namespace FluxKnowledge.Application.IntegrationV1.Code;

public sealed record NativeCodeFeedbackMutation(JsonElement Payload);
public sealed record NativeCodeFeedbackCommitOperation(string CanonicalPayload) : NativeActionCommitOperation;

public sealed class NativeCodeFeedbackService
{
    private readonly NativeOperationService _operations;
    private readonly ILocalPrivateContentDisclosure _disclosure;
    public NativeCodeFeedbackService(INativeOperationStore operationStore, ILocalPrivateContentDisclosure disclosure)
    {
        ArgumentNullException.ThrowIfNull(operationStore);
        _disclosure = disclosure ?? throw new ArgumentNullException(nameof(disclosure));
        _operations = new NativeOperationService(operationStore, [new NativeActionDefinition("feedback", "Record privacy-safe code retrieval feedback.",
            (_, token) => ResolveTargetsAsync(token),
            (payload, _, token) => ResolveOperationAsync(payload, disclosure, token))]);
    }
    public ValueTask<NativeActionPreview> PreviewAsync(NativeCodeFeedbackMutation command, string surface, CancellationToken cancellationToken) => _operations.PreviewAsync(new("feedback", Payload(command, _disclosure), surface), cancellationToken);
    public ValueTask<NativeActionReceipt> CommitAsync(NativeCodeFeedbackMutation command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) => _operations.CommitAsync(new("feedback", Payload(command, _disclosure), confirmationId, idempotencyKey, surface), cancellationToken);
    private static string Payload(NativeCodeFeedbackMutation command, ILocalPrivateContentDisclosure disclosure)
    {
        ArgumentNullException.ThrowIfNull(command);
        var canonical = NativeOperationCanonicalization.CanonicalizeJson(command.Payload.GetRawText());
        var result = disclosure.Evaluate(canonical, LocalDisclosureKind.CodeFeedbackWrite);
        if (result.Withheld) throw new NativeOperationException(result.ReasonCode ?? "secret-content-withheld");
        return result.Value ?? "{}";
    }

    private static ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveTargetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<NativeTargetVersion>>([]);
    }

    private static ValueTask<NativeActionCommitOperation> ResolveOperationAsync(string payload, ILocalPrivateContentDisclosure disclosure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = disclosure.Evaluate(payload, LocalDisclosureKind.CodeFeedbackWrite);
        if (result.Withheld) throw new NativeOperationException(result.ReasonCode ?? "secret-content-withheld");
        return ValueTask.FromResult<NativeActionCommitOperation>(new NativeCodeFeedbackCommitOperation(result.Value ?? "{}"));
    }
}
