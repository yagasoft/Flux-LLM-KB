using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class RetainedRecoveryNativeCommands
{
    // The payload contains the retained hash. The web endpoint protects it before returning it to a browser.
    public async Task<(RetainedRecoveryPreview Preview, string Payload)> PreviewPolicyAsync(RetainedRecoveryTarget target, CancellationToken token)
    {
        EnsureAllowed(target);
        if (target.Kind != "policy" || corpusCommands is null) throw new NativeOperationException("invalid-payload");
        await using var db = await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var request = await (from revision in db.SourceRevisions.AsNoTracking()
                             join root in db.SourceRootConfigurations.AsNoTracking() on revision.SourceRootId equals root.Id
                             where revision.Id == target.SourceRevisionId
                             select new SqlDefaultRetainedPolicy.Request(root.Id, revision.Id, target.ExactId!.Value,
                                 revision.ContentSha256, root.ConfigurationRevision)).SingleOrDefaultAsync(token).ConfigureAwait(false)
                      ?? throw new NativeOperationException("target-not-found");
        var metadata = await SqlDefaultRetainedPolicy.ReadMetadataAsync(db, request, token).ConfigureAwait(false);
        EnsurePolicyProcessorAvailable(metadata.Descriptor);
        var payload = JsonSerializer.SerializeToElement(new { rootId = request.RootId, sourceRevisionId = request.RevisionId,
            predecessorActivityId = request.ActivityId, inputSha256 = request.Hash, configurationRevision = request.ConfigurationRevision });
        var confirmation = await corpusCommands.PreviewAsync(new("policy_reprocess", payload), Actor, token).ConfigureAwait(false);
        return (new(target, confirmation, Path.GetFileName(metadata.Revision.CanonicalPath), metadata.Revision.Revision,
            metadata.Descriptor?.ProcessorVersion ?? RetainedDefaultPolicy.Version, "retained-text-policy-reassessment-required", null),
            payload.GetRawText());
    }

    public async ValueTask<NativeActionReceipt> CommitPolicyAsync(RetainedRecoveryTarget target, string payload,
        string confirmationId, string idempotencyKey, CancellationToken token)
    {
        Validate(target);
        if (target.Kind != "policy" || corpusCommands is null) throw new NativeOperationException("invalid-payload");
        var request = SqlDefaultRetainedPolicy.Parse(payload);
        if (request.RevisionId != target.SourceRevisionId || request.ActivityId != target.ExactId)
            throw new NativeOperationException("confirmation-mismatch");
        var replay = await TryReplayAsync(target, payload, confirmationId, idempotencyKey, token).ConfigureAwait(false);
        if (replay is not null && deploymentHold?.IsHeld == true) return replay;
        EnsureAllowed(target);
        if (replay is null)
        {
            await using var db = await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
            var metadata = await SqlDefaultRetainedPolicy.ReadMetadataAsync(db, request, token).ConfigureAwait(false);
            EnsurePolicyProcessorAvailable(metadata.Descriptor);
        }
        using var document = JsonDocument.Parse(payload);
        return await corpusCommands.CommitAsync(new("policy_reprocess", document.RootElement), confirmationId, idempotencyKey, Actor, token).ConfigureAwait(false);
    }

    private async ValueTask<RetainedRecoveryTarget?> FindPolicyTargetAsync(FluxKnowledgeDbContext db, SourceRevisionEntity revision,
        Guid? branchId, CancellationToken token)
    {
        if (corpusCommands is null) return null;
        var configuration = await db.SourceRootConfigurations.AsNoTracking().Where(root => root.Id == revision.SourceRootId)
            .Select(root => root.ConfigurationRevision).SingleAsync(token).ConfigureAwait(false);
        var activities = await db.SourceActivities.AsNoTracking().Where(activity => activity.SourceRevisionId == revision.Id &&
            activity.InputFingerprint == revision.ContentSha256 && activity.ResultingPipelineRecordId == null &&
            (activity.State == (int)SourceActivityState.DeferredPolicy || activity.State == (int)SourceActivityState.Pending ||
             activity.State == (int)SourceActivityState.FailedTerminal) &&
            (branchId == null || db.SourceProcessorBranches.Any(branch => branch.Id == branchId && branch.SourceActivityId == activity.Id)) &&
            !db.SourceActivityRelations.Any(relation => relation.PredecessorActivityId == activity.Id &&
                !db.SourceActivities.Any(successor => successor.Id == relation.SuccessorActivityId && successor.Reason == "policy-binding-stale")))
            .OrderBy(activity => activity.Id).Select(activity => activity.Id).Take(9).ToArrayAsync(token).ConfigureAwait(false);
        if (activities.Length > 8) return null;
        Guid? candidate = null;
        foreach (var activity in activities)
        {
            try
            {
                var metadata = await SqlDefaultRetainedPolicy.ReadMetadataAsync(db,
                    new(revision.SourceRootId, revision.Id, activity, revision.ContentSha256, configuration), token).ConfigureAwait(false);
                EnsurePolicyProcessorAvailable(metadata.Descriptor);
                if (candidate is not null) return null;
                candidate = activity;
            }
            catch (NativeOperationException) { /* No supported recovery for this predecessor. */ }
        }
        return candidate is { } id ? new("policy", revision.Id, id) : null;
    }

    private void EnsurePolicyProcessorAvailable(SourceCapabilityDescriptor? descriptor)
    {
        if (descriptor?.Id == RetainedDefaultPolicy.UnsupportedBinary.Id)
            throw new NativeOperationException("policy-processor-unsupported");
        if (descriptor?.Id == ZipArchiveRetainedProcessor.Capability.Id && !options.ArchiveZipExpandEnabled ||
            descriptor?.Id == TarArchiveRetainedProcessor.Capability.Id && !options.ArchiveTarExpandEnabled ||
            descriptor?.Id == OoxmlStructuralTextProcessor.Capability.Id && !options.OoxmlDocumentStructuralExtractEnabled ||
            descriptor?.Id == MediaMetadataRetainedProcessor.Capability.Id && !options.MediaMetadataEnabled ||
            descriptor?.Id == RetainedCsharpCodeProcessor.Capability.Id && !options.CsharpCodeEnabled)
            throw new NativeOperationException("policy-processor-unavailable");
    }
}
