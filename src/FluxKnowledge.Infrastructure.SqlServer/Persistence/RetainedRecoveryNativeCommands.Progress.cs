using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed record RetainedRecoveryReview(Guid SourceRootId, IReadOnlyList<RetainedRecoveryTarget> Targets,
    RetainedRecoveryProgress? VisioProgress);

public sealed record RetainedRecoveryProgress(Guid? BranchId, string State, string? ReasonCode = null, int? PublishedMembers = null);

public sealed partial class RetainedRecoveryNativeCommands
{
    /// <summary>Finds narrow selectors. The native preview remains the eligibility decision.</summary>
    public async Task<RetainedRecoveryReview?> ReadReviewAsync(Guid revisionId, Guid? jobId, Guid? branchId, CancellationToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var revision = await db.SourceRevisions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == revisionId &&
            value.OriginKind == 0 && value.SuppressedAtUtc == null, token).ConfigureAwait(false);
        if (revision is null) return null;
        var targets = new List<RetainedRecoveryTarget>(2);
        var zip = await (from branch in db.SourceProcessorBranches.AsNoTracking()
                         join attempt in db.SourceProcessorAttempts.AsNoTracking() on new { BranchId = branch.Id, branch.LeaseGeneration }
                             equals new { attempt.BranchId, attempt.LeaseGeneration }
                         where branch.SourceRevisionId == revision.Id && (branchId == null || branch.Id == branchId) &&
                               branch.ProcessorFingerprint == "phase-5-zip-retained-archive-v1" &&
                               branch.State == (int)RetainedProcessorBranchState.Blocked && attempt.FinishedAtUtc != null &&
                               attempt.OutcomeCode == "archive-member-not-utf8" &&
                               !db.SourceActivityRelations.Any(relation => relation.PredecessorActivityId == branch.SourceActivityId)
                         select branch.Id).Take(2).ToArrayAsync(token).ConfigureAwait(false);
        if (zip.Length == 1) targets.Add(new("zip", revision.Id, zip[0]));

        RetainedRecoveryProgress? visio = null;
        switch (revision.Extension.ToLowerInvariant())
        {
            case ".vsdx" when branchId is null:
                var prepared = await db.SourceProcessorBranches.AsNoTracking().Where(branch => branch.SourceRevisionId == revision.Id &&
                    branch.InputSha256 == revision.ContentSha256 && branch.ProcessorFingerprint == DocumentProcessingInput.Visio.ParentProcessorFingerprint &&
                    branch.ProcessorVersion == DocumentProcessingInput.Visio.ParentProcessorVersion)
                    .Take(2).ToArrayAsync(token).ConfigureAwait(false);
                if (prepared.Length == 0) targets.Add(new("visio", revision.Id));
                if (prepared.Length == 1) visio = await DescribeProgressAsync(db, prepared[0], "visio", token).ConfigureAwait(false);
                break;
            case ".pdf" or ".jpg" or ".jpeg" or ".png" when branchId is null:
                var extract = await (from input in db.SourceRevisions.AsNoTracking()
                                     join record in db.PipelineRecords.AsNoTracking() on input.Id equals record.SourceRevisionId
                                     join job in db.Jobs.AsNoTracking() on new { RecordId = record.Id, record.Revision }
                                         equals new { RecordId = job.PipelineRecordId, Revision = job.SourceRevision }
                                     where input.ParentSourceRevisionId == revision.Id && input.SuppressedAtUtc == null && input.OriginKind == 2 &&
                                           !record.IsDeleted && job.Operation == PipelineOperations.ExtractDocument && (jobId == null || job.Id == jobId) &&
                                           (job.PublicState == (int)PublicJobState.Failed || job.PublicState == (int)PublicJobState.GpuProcessing)
                                     select job.Id).Take(2).ToArrayAsync(token).ConfigureAwait(false);
                if (extract.Length == 1) targets.Add(new(revision.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "pdf" : "image", revision.Id, extract[0]));
                break;
        }
        if (await FindPolicyTargetAsync(db, revision, branchId, token).ConfigureAwait(false) is { } policy)
            targets.Add(policy);
        return new(revision.SourceRootId, targets, visio);
    }

    public async Task<RetainedRecoveryProgress?> ReadProgressAsync(RetainedRecoveryTarget target, Guid operationId, CancellationToken token)
    {
        Validate(target);
        await using var db = await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var action = Action(target);
        var receipt = await (from operation in db.NativeOperationReceipts.AsNoTracking()
                             join intent in db.NativeOperationIntents.AsNoTracking() on operation.IntentId equals intent.Id
                             where operation.OperationId == operationId && operation.Action == action && operation.ActorSurface == Actor
                             select new { operation.RequestFingerprint, intent.TargetMetadataJson }).SingleOrDefaultAsync(token).ConfigureAwait(false);
        if (receipt is null) return null;
        var targets = JsonSerializer.Deserialize<NativeTargetVersion[]>(receipt.TargetMetadataJson) ?? [];
        if (target.Kind == "policy")
            return await ReadPolicyProgressAsync(db, target, targets, token).ConfigureAwait(false);
        var payload = NativeOperationCanonicalization.CanonicalizeJson(Payload(target));
        if (receipt.RequestFingerprint != NativeOperationCanonicalization.CreateRequestFingerprint(action, payload, targets)) return null;
        var predecessorPrefix = target.Kind is "pdf" or "image" ? "settled-document-proof:" : "predecessor:";
        var predecessor = targets.SingleOrDefault(value => value.TargetId.StartsWith(predecessorPrefix, StringComparison.Ordinal));
        if (predecessor is null || !Guid.TryParse(predecessor.TargetId[predecessorPrefix.Length..], out var predecessorId)) return null;
        var relationReason = target.Kind switch
        {
            "zip" => SqlRetainedProcessorBranchStore.ZipEncodingSuccessorReason,
            "visio" => SqlRetainedProcessorBranchStore.ExactDocumentSuccessorReason,
            _ => DocumentProcessingInput.DocumentRecoveryVersion
        };
        var branch = await (from relation in db.SourceActivityRelations.AsNoTracking()
                            join successor in db.SourceProcessorBranches.AsNoTracking() on relation.SuccessorActivityId equals successor.SourceActivityId
                            where relation.PredecessorActivityId == predecessorId && relation.ReasonCode == relationReason &&
                                  relation.RelationshipKind == "superseded-by-retained-processor" && successor.SourceRevisionId == target.SourceRevisionId
                            select successor).SingleOrDefaultAsync(token).ConfigureAwait(false);
        return branch is null ? null : await DescribeProgressAsync(db, branch, target.Kind, token).ConfigureAwait(false);
    }

    private static async Task<RetainedRecoveryProgress> DescribeProgressAsync(FluxKnowledgeDbContext db,
        SourceProcessorBranchEntity branch, string kind, CancellationToken token)
    {
        var state = await (from revision in db.SourceRevisions.AsNoTracking()
                           join root in db.SourceRootConfigurations.AsNoTracking() on revision.SourceRootId equals root.Id
                           where revision.Id == branch.SourceRevisionId && revision.SuppressedAtUtc == null && revision.ContentSha256 == branch.InputSha256
                           select (SourceRootState?)root.State).SingleOrDefaultAsync(token).ConfigureAwait(false);
        if (state is null or SourceRootState.Deleting) return new(branch.Id, "Superseded");
        if (state == SourceRootState.Paused) return new(branch.Id, "Source paused");
        if (state != SourceRootState.Enabled) return new(branch.Id, "Source disabled");
        var owner = await db.SourceActivities.AsNoTracking().SingleOrDefaultAsync(value => value.Id == branch.SourceActivityId, token).ConfigureAwait(false);
        if (owner is not null && SqlGuidedRetainedBinding.IsMarked(owner) &&
            !await SqlGuidedRetainedBinding.IsCurrentAsync(db, owner, token).ConfigureAwait(false))
            return new(branch.Id, "Source configuration changed", "policy-binding-stale");
        if (branch.State == (int)RetainedProcessorBranchState.Blocked)
        {
            var reason = await db.SourceProcessorAttempts.AsNoTracking().Where(value => value.BranchId == branch.Id && value.LeaseGeneration == branch.LeaseGeneration)
                .Select(value => value.OutcomeCode).SingleOrDefaultAsync(token).ConfigureAwait(false);
            return new(branch.Id, "Failed", reason);
        }
        var pending = branch.State == (int)RetainedProcessorBranchState.Pending;
        if (branch.State != (int)RetainedProcessorBranchState.Completed)
            return new(branch.Id, pending ? "Queued" : "Preparation recorded. Check worker ownership in Activity.");

        var published = await db.Database.SqlQuery<Guid>(SqlPublishedPassageSelection.Bind($"""
            SELECT DISTINCT COALESCE([publication].[OwnerSourceRevisionId], [retained].[Id]) AS [Value]
            FROM [TextChunks] [chunk]
            INNER JOIN [Artifacts] [artifact] ON [chunk].[ArtifactId] = [artifact].[Id]
            INNER JOIN [PipelineRecords] [record] ON [artifact].[PipelineRecordId] = [record].[Id]
            /*publication-bindings*/
            WHERE /*publication-eligibility*/
              AND ([publication].[SourceProcessorBranchId] = {branch.Id} OR EXISTS (
                SELECT 1 FROM [SourceProcessorBranchMembers] [member]
                WHERE [member].[BranchId] = {branch.Id} AND [member].[Disposition] = 'completed'
                  AND [member].[ChildSourceRevisionId] = COALESCE([publication].[OwnerSourceRevisionId], [retained].[Id]) AND {kind} = 'zip'))
            """)).CountAsync(token).ConfigureAwait(false);
        if (kind == "zip")
            return new(branch.Id, "Archive expanded", PublishedMembers: published);
        if (published != 0) return new(branch.Id, "Searchable");

        var jobs = await (from member in db.SourceProcessorBranchMembers.AsNoTracking()
                          join record in db.PipelineRecords.AsNoTracking() on member.ChildSourceRevisionId equals record.SourceRevisionId
                          join job in db.Jobs.AsNoTracking() on new { RecordId = record.Id, record.Revision, Stage = record.CurrentStage }
                              equals new { RecordId = job.PipelineRecordId, Revision = job.SourceRevision, job.Stage }
                          where member.BranchId == branch.Id && !record.IsDeleted
                          select new { job.PublicState, job.Reason, job.Operation, job.LeaseExpiresAtUtc, record.CompletionCriteriaMet })
            .Take(2).ToArrayAsync(token).ConfigureAwait(false);
        if (jobs.Length > 1) return new(branch.Id, "Waiting for ownership evidence");
        var current = jobs.SingleOrDefault();
        if (current?.PublicState == (int)PublicJobState.Failed) return new(branch.Id, "Failed", current.Reason);
        if (current?.CompletionCriteriaMet == true) return new(branch.Id, "Completed without searchable text");
        if (kind == "visio" && (current is null || current.Operation == PipelineOperations.ExtractVisio &&
            current.PublicState == (int)PublicJobState.WorkerQueued)) return new(branch.Id, "Waiting for the Visio desktop companion");
        if (current?.PublicState == (int)PublicJobState.WorkerProcessing && current.LeaseExpiresAtUtc > DateTimeOffset.UtcNow)
            return new(branch.Id, "Processing recorded. Check worker ownership in Activity.");
        return new(branch.Id, "Waiting for indexing");
    }

    private static async Task<RetainedRecoveryProgress?> ReadPolicyProgressAsync(FluxKnowledgeDbContext db,
        RetainedRecoveryTarget target, IReadOnlyList<NativeTargetVersion> targets, CancellationToken token)
    {
        if (!targets.Any(value => value.TargetId == $"policy-revision:{target.SourceRevisionId:D}") ||
            !targets.Any(value => value.TargetId == $"policy-activity:{target.ExactId:D}")) return null;
        var successor = await (from relation in db.SourceActivityRelations.AsNoTracking()
                               join activity in db.SourceActivities.AsNoTracking() on relation.SuccessorActivityId equals activity.Id
                               join revision in db.SourceRevisions.AsNoTracking() on activity.SourceRevisionId equals revision.Id
                               join root in db.SourceRootConfigurations.AsNoTracking() on revision.SourceRootId equals root.Id
                               where relation.PredecessorActivityId == target.ExactId && relation.RelationshipKind == RetainedDefaultPolicy.RelationshipKind &&
                                     relation.ReasonCode == RetainedDefaultPolicy.Version && activity.SourceRevisionId == target.SourceRevisionId
                               select new { Activity = activity, revision.SuppressedAtUtc, SourceState = (SourceRootState)root.State })
            .SingleOrDefaultAsync(token).ConfigureAwait(false);
        if (successor is null) return null;
        if (successor.SuppressedAtUtc is not null || successor.SourceState == SourceRootState.Deleting) return new(null, "Superseded");
        if (successor.SourceState != SourceRootState.Enabled)
            return new(null, successor.SourceState == SourceRootState.Paused ? "Source paused" : "Source disabled");
        var chosenActivity = successor.Activity;
        var branch = await db.SourceProcessorBranches.AsNoTracking().SingleOrDefaultAsync(value => value.SourceActivityId == chosenActivity.Id, token).ConfigureAwait(false);
        if (branch is not null) return await DescribeProgressAsync(db, branch, chosenActivity.ActivityKind == (int)SourceActivityKind.ArchiveExpansion ? "zip" : "policy", token).ConfigureAwait(false);
        if (chosenActivity.State == (int)SourceActivityState.FailedTerminal) return new(null, "Failed", chosenActivity.Reason);
        if (chosenActivity.ResultingPipelineRecordId is not { } recordId || chosenActivity.ResultingPipelineRecordRevision is not { } recordRevision)
            return new(null, "Queued");
        var published = await db.Database.SqlQuery<Guid>(SqlPublishedPassageSelection.Bind($"""
            SELECT [record].[Id] AS [Value]
            FROM [TextChunks] [chunk]
            INNER JOIN [Artifacts] [artifact] ON [chunk].[ArtifactId] = [artifact].[Id]
            INNER JOIN [PipelineRecords] [record] ON [artifact].[PipelineRecordId] = [record].[Id]
            /*publication-bindings*/
            WHERE /*publication-eligibility*/ AND [record].[Id] = {recordId} AND [record].[Revision] = {recordRevision}
            """)).AnyAsync(token).ConfigureAwait(false);
        return published ? new(null, "Searchable") : new(null, "Waiting for indexing");
    }
}
