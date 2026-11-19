using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Web.Components.OperatorActions;

namespace FluxKnowledge.Web.Components.Status;

/// <summary>Keeps an exact submission available when its inspector closes within the circuit.</summary>
public sealed class GuidedRecoveryActions(
    NativeCorpusCommandService commands, ILocalOperatorPolicy operatorPolicy, ILogger<GuidedRecoveryActions> logger)
{
    private const string ActorSurface = "web";
    private readonly NativeCorpusCommandService _commands = commands;
    private readonly ILocalOperatorPolicy _operatorPolicy = operatorPolicy;
    private readonly ILogger<GuidedRecoveryActions> _logger = logger;
    private readonly Dictionary<Selection, Attempt> _attempts = [];
    private long _submissionSequence;
    public event Action? SubmissionsChanged;

    public Attempt? Select(string action, Guid targetId, bool reconcileMetadata,
        Guid? sourceRootId = null, Guid? pipelineRecordId = null, string? label = null)
    {
        if (targetId == Guid.Empty || (reconcileMetadata && action != "source_sync") ||
            action is not ("publication_retry" or "embedding_retry" or "job_retry" or "root_resume" or "source_sync"))
            return null;

        var selection = new Selection(action, targetId, reconcileMetadata);
        if (!_attempts.TryGetValue(selection, out var attempt))
        {
            var payload = action switch
            {
                "publication_retry" or "embedding_retry" or "job_retry" => JsonSerializer.SerializeToElement(new { jobId = targetId }),
                "source_sync" when reconcileMetadata => JsonSerializer.SerializeToElement(new { rootId = targetId, reconcileMetadata = true }),
                _ => JsonSerializer.SerializeToElement(new { rootId = targetId })
            };
            attempt = new Attempt(this, selection, new NativeCorpusMutation(action, payload));
            _attempts.Add(selection, attempt);
        }
        attempt.SourceRootId ??= sourceRootId;
        attempt.PipelineRecordId ??= pipelineRecordId;
        if (label is not null) attempt.Label = label;
        attempt.OpenInspectors++;
        return attempt;
    }

    internal IReadOnlyList<Attempt> Recent(Guid? sourceRootId = null, string? action = null)
    {
        var matching = _attempts.Values.Where(attempt => attempt.HasSubmission &&
            (sourceRootId is null || attempt.SourceRootId == sourceRootId) &&
            (action is null || attempt.Target.Action == action)).OrderByDescending(attempt => attempt.SubmissionSequence).ToArray();
        return matching.Where(attempt => attempt.Receipt is null).Concat(matching.Where(attempt => attempt.Receipt is not null).Take(12)).ToArray();
    }

    public void Release(Attempt attempt)
    {
        attempt.OpenInspectors--;
        DiscardIfSettled(attempt);
    }

    private void DiscardIfSettled(Attempt attempt)
    {
        if (attempt.OpenInspectors == 0 && !attempt.IsBusy && !attempt.HasSubmission)
            _attempts.Remove(attempt.Target);
    }

    private void NotifySubmissions()
    {
        var old = _attempts.Values.Where(attempt => attempt.Receipt is not null && attempt.OpenInspectors == 0)
            .OrderByDescending(attempt => attempt.SubmissionSequence).Skip(12).Select(attempt => attempt.Target).ToArray();
        foreach (var target in old) _attempts.Remove(target);
        SubmissionsChanged?.Invoke();
    }

    internal readonly record struct Selection(string Action, Guid TargetId, bool ReconcileMetadata);

    public sealed class Attempt
    {
        private readonly GuidedRecoveryActions _owner;
        private readonly NativeCorpusMutation _mutation;
        private string? _idempotencyKey;

        internal Attempt(GuidedRecoveryActions owner, Selection target, NativeCorpusMutation mutation)
        {
            _owner = owner;
            Target = target;
            _mutation = mutation;
        }

        internal Selection Target { get; }
        internal int OpenInspectors { get; set; }
        internal Guid? SourceRootId { get; set; }
        internal Guid? PipelineRecordId { get; set; }
        internal string Label { get; set; } = "Recovery request";
        internal long SubmissionSequence { get; private set; }
        internal bool HasSubmission => _idempotencyKey is not null || Receipt is not null;
        public bool IsBusy { get; private set; }
        public bool IsCommitUnconfirmed { get; private set; }
        public NativeActionPreview? Preview { get; private set; }
        public NativeActionReceipt? Receipt { get; private set; }
        public string? Message { get; private set; }
        public string? ReasonCode { get; private set; }
        public event Action? Changed;

        public string TargetLabel => Target.Action is "publication_retry" or "embedding_retry" or "job_retry" ? "Selected job" : "Selected source";

        public string? Effect => Target.Action switch
        {
            "source_sync" when Target.ReconcileMetadata => "Compare stored metadata with current files on the selected source and reconcile differences.",
            "source_sync" => "Request processing for changes already pending on the selected source. Source holds still apply.",
            "job_retry" => "Queue only the selected source scan again. Earlier attempts stay in history.",
            "root_resume" => "Resume event processing for the selected source. Metadata reconciliation follows its weekly schedule.",
            _ => Preview?.EffectSummary
        };

        public async Task PreviewAsync()
        {
            if (IsBusy || Preview is not null || Receipt is not null) return;
            Start("Preparing preview…");
            try
            {
                await _owner._operatorPolicy.EnsureMutationAllowedAsync(CancellationToken.None);
                Preview = await _owner._commands.PreviewAsync(_mutation, ActorSurface, CancellationToken.None);
                Message = "Preview ready. Review the effect before confirming.";
            }
            catch (UnauthorizedAccessException) { Refuse("operator-authority-denied"); }
            catch (NativeOperationException exception) { Refuse(exception.ReasonCode); }
            catch (Exception exception)
            {
                _owner._logger.LogWarning(exception, "Recovery preview failed for {Action} on {TargetId}", Target.Action, Target.TargetId);
                Message = "Could not prepare a preview. Refresh the status and try again.";
            }
            finally { Finish(); }
        }

        public void CancelPreview()
        {
            if (IsBusy || IsCommitUnconfirmed || Preview is null) return;
            Preview = null;
            Message = "Preview cancelled.";
            ReasonCode = null;
            Changed?.Invoke();
        }

        public async Task<bool> ConfirmAsync()
        {
            if (IsBusy || Preview is not { } preview) return false;
            _idempotencyKey ??= $"web-guided-recovery:{Guid.NewGuid():N}";
            SubmissionSequence = ++_owner._submissionSequence;
            Start(IsCommitUnconfirmed ? "Checking the recorded result…" : "Submitting the confirmed request…");
            _owner.NotifySubmissions();
            try
            {
                await _owner._operatorPolicy.EnsureMutationAllowedAsync(CancellationToken.None);
                // The mutation and receipt can outlive this inspector. Keep the exact tuple until SQL settles it.
                var receipt = await _owner._commands.CommitAsync(_mutation, preview.ConfirmationId,
                    _idempotencyKey, ActorSurface, CancellationToken.None);
                ResetSubmission();
                Receipt = receipt;
                if (receipt.Outcome != "completed" || receipt.ReasonCode is not null)
                {
                    ReasonCode = receipt.ReasonCode;
                    Message = "The service returned a receipt without confirming acceptance. Check Activity for the current state.";
                    return false;
                }
                Message = receipt.WasReplay
                    ? "Recorded request accepted. Follow its progress in Activity."
                    : "Request accepted. Follow its progress in Activity.";
                return true;
            }
            catch (UnauthorizedAccessException) { Refuse("operator-authority-denied"); }
            catch (NativeOperationException exception) { Refuse(exception.ReasonCode); }
            catch (Exception exception)
            {
                _owner._logger.LogWarning(exception, "Recovery request result is unconfirmed for {Action} on {TargetId}", Target.Action, Target.TargetId);
                IsCommitUnconfirmed = true;
                Message = "The result is unconfirmed. Retry the same request to check for its receipt.";
            }
            finally { Finish(); _owner.NotifySubmissions(); }
            return false;
        }

        public void DismissReceipt()
        {
            if (IsBusy || IsCommitUnconfirmed || Receipt is null) return;
            Receipt = null;
            Message = null;
            _owner.NotifySubmissions();
            Changed?.Invoke();
        }

        private void Start(string message)
        {
            IsBusy = true;
            Message = message;
            ReasonCode = null;
            Changed?.Invoke();
        }

        private void Finish()
        {
            IsBusy = false;
            Changed?.Invoke();
            _owner.DiscardIfSettled(this);
        }

        private void ResetSubmission()
        {
            Preview = null;
            _idempotencyKey = null;
            IsCommitUnconfirmed = false;
        }

        private void Refuse(string reason)
        {
            ReasonCode = reason;
            var explanation = reason switch
            {
                "operator-authority-denied" => "Open this dashboard through the local operator connection to use recovery actions.",
                "deployment-validation-held" => "Deployment validation is holding changes. Try again when the hold is released.",
                "confirmation-expired" => "The preview expired. Prepare a fresh preview to continue.",
                "operation-fenced" => "The target changed or its current state prevents this action. Refresh status and prepare a fresh preview.",
                "target-not-found" => "The selected target is no longer available. Refresh status to view current work.",
                "source-not-enabled" => "The source must be enabled before this action can run. Check the source status.",
                "source-paused" => "Resume this source before retrying its scan.",
                "source-deleting" => "The source is being removed. Check source status for cleanup progress.",
                "source-scan-not-admitted" => "The app is still checking the source's identity or access. Review Source updates before retrying.",
                "source-scan-configuration-changed" => "The source settings have changed. Review the current rules and reconcile metadata with those settings.",
                "source-scan-configuration-unavailable" => "The scan's configuration cannot be verified. Review the current rules and reconcile metadata with those settings.",
                "operation-conflict" => "Other source operations are pending. Check Activity and try again when their state settles.",
                "embedding-retry-not-eligible" => "This embedding job is ineligible for an exact retry. Check its checkpoint and GPU evidence in Activity.",
                "publication-retry-not-eligible" => "This publication job is ineligible for an exact retry. Check its current job and delivery in Activity.",
                _ => "The service refused this request. Refresh status and check the diagnostic reason."
            };

            // Only expiry is raised after SQL's serialised receipt check has ruled out this exact commit.
            if (IsCommitUnconfirmed && reason != "confirmation-expired")
            {
                Message = $"The earlier request remains unconfirmed. {explanation}";
                return;
            }
            ResetSubmission();
            Message = explanation;
        }
    }
}
