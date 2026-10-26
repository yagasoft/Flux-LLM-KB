using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Web.Components.Sources;

public sealed class SourceVerificationPanelState(ISourceVerificationStore store, NativeCorpusCommandService commands)
{
    public SourceVerificationStatus? Status { get; private set; }
    public string? ReadError { get; private set; }
    public string? Message { get; private set; }
    public bool IsSubmitting { get; private set; }
    public bool HasPendingCommit => _pending is not null;
    private PendingCommit? _pending;
    private Guid _rootId;
    private long _readSequence;
    private bool _commitUncertain;

    public async Task LoadAsync(Guid rootId, CancellationToken ct)
    {
        if (_rootId != rootId)
        {
            _rootId = rootId;
            Status = null;
            ReadError = null;
            Message = null;
            _pending = null;
            _commitUncertain = false;
        }
        var sequence = Interlocked.Increment(ref _readSequence);
        try
        {
            var status = await store.ReadAsync(new(rootId), ct);
            if (_rootId != rootId || sequence != Volatile.Read(ref _readSequence)) return;
            Status = status;
            ReadError = status is null ? "This source is no longer available." : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            if (_rootId == rootId && sequence == Volatile.Read(ref _readSequence))
                ReadError = "Verification status could not be refreshed. Displayed values may be out of date.";
        }
    }

    public Task SetPausedAsync(Guid rootId, bool paused) => ExecuteAsync(rootId, "source_verification_set",
        new { rootId, paused }, paused ? "Verification was paused." : "Verification was resumed.");

    public Task VerifyAsync(Guid rootId) => ExecuteAsync(rootId, "source_verify", new { rootId }, "Verification was requested.");

    public Task SavePolicyAsync(Guid rootId, SourceVerificationPolicy policy) => ExecuteAsync(rootId,
        "source_verification_set", new { rootId, policy }, "Verification schedule was saved.");

    private async Task ExecuteAsync(Guid rootId, string action, object payload, string successMessage)
    {
        if (IsSubmitting || HasPendingCommit) return;
        _rootId = rootId;
        IsSubmitting = true;
        Message = null;
        try
        {
            var mutation = new NativeCorpusMutation(action, JsonSerializer.SerializeToElement(payload));
            var preview = await commands.PreviewAsync(mutation, "web", CancellationToken.None);
            _pending = new(rootId, mutation, preview.ConfirmationId, $"web-source-verification:{Guid.NewGuid():N}", successMessage);
            _commitUncertain = false;
            await CommitPendingAsync();
        }
        catch (NativeOperationException exception) { Message = $"Verification change was refused: {exception.ReasonCode}."; }
        catch { Message = "Verification change could not be prepared. Refresh the status and try again."; }
        finally { IsSubmitting = false; }
    }

    public async Task RetryAsync()
    {
        if (IsSubmitting || !HasPendingCommit) return;
        IsSubmitting = true;
        try { await CommitPendingAsync(); }
        finally { IsSubmitting = false; }
    }

    private async Task CommitPendingAsync()
    {
        if (_pending is not { } pending) return;
        try
        {
            await commands.CommitAsync(pending.Mutation, pending.ConfirmationId, pending.IdempotencyKey, "web", CancellationToken.None);
            if (_pending != pending) return;
            Interlocked.Increment(ref _readSequence);
            _pending = null;
            _commitUncertain = false;
            Message = pending.SuccessMessage;
            await LoadAsync(pending.RootId, CancellationToken.None);
        }
        catch (NativeOperationException exception)
        {
            if (_pending != pending) return;
            // A refusal before receipt lookup (for example a deployment hold) says
            // nothing about an earlier uncertain commit. Keep its exact replay tuple.
            // Expiry is raised by SQL only after the serialised receipt check proves
            // this key did not commit. A fresh action is then safe and avoids lockout.
            if (_commitUncertain && exception.ReasonCode != "confirmation-expired")
                Message = $"The result is still unconfirmed ({exception.ReasonCode}). Retry the same request when available.";
            else
            {
                _pending = null;
                _commitUncertain = false;
                Message = $"Verification change was refused: {exception.ReasonCode}.";
            }
        }
        catch
        {
            if (_pending != pending) return;
            _commitUncertain = true;
            Message = "The result is not confirmed. Retry this request to recover its recorded result before making another change.";
        }
    }

    private sealed record PendingCommit(Guid RootId, NativeCorpusMutation Mutation, string ConfirmationId,
        string IdempotencyKey, string SuccessMessage);
}
