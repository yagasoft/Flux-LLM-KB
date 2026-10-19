using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Only diagnostic rows with an exact, selected successful successor are obsolete.</summary>
internal static class SqlResolvedSourceIssueEvents
{
    internal static IQueryable<AuditEventEntity> Query(FluxKnowledgeDbContext context, Guid? rootId = null)
    {
        var officeBranches = ResolvedOfficeBranches(context, rootId);
        var resolved = ResolvedActivities(context, rootId).Concat(ResolvedOfficeBindings(context, officeBranches));
        // Emit the publication proof once. Repeating it for each event family
        // makes SQL optimisation exceed the bounded cleanup command budget.
        return context.AuditEvents.Where(value => value.SourceScanRequestId == null && resolved.Any(proof =>
            value.OccurredAtUtc < proof.PublishedAtUtc &&
            (value.EventType == "activity.failed" && value.EventFamily == "activity" && value.Severity == "error" &&
             proof.ActivityId != null && proof.SourceRevisionId != null && proof.PipelineId != null &&
             value.SourceRootId == proof.RootId && value.SourceRevisionId == proof.SourceRevisionId &&
             value.SourceActivityId == proof.ActivityId && value.PipelineRecordId == proof.PipelineId &&
             value.CorrelationId == "source:" + proof.SourceRevisionId!.Value.ToString().Replace("-", "").ToLower() ||
             value.EventType == "pipeline.failed" && value.EventFamily == "pipeline" && value.Severity == "error" &&
             proof.PipelineId != null && proof.PipelineRevision != null &&
             value.SourceRootId == null && value.SourceRevisionId == null && value.SourceActivityId == null &&
             value.PipelineRecordId == proof.PipelineId &&
             value.CorrelationId == "pipeline:" + proof.PipelineId!.Value.ToString().Replace("-", "").ToLower() + ":" + proof.PipelineRevision!.Value.ToString() ||
             value.EventType == "retained_processor.blocked" && value.EventFamily == "retained_processor" && value.Severity == "warning" &&
             proof.BranchId != null && value.SourceRootId == proof.RootId && value.SourceRevisionId == proof.OwnerId &&
             value.SourceActivityId == null && value.PipelineRecordId == null &&
             value.CorrelationId == "retained-processor:" + proof.BranchId!.Value.ToString().Replace("-", "").ToLower())));
    }

    private static IQueryable<Resolution> ResolvedActivities(FluxKnowledgeDbContext context, Guid? rootId) =>
        from activity in context.SourceActivities.AsNoTracking()
        join input in context.SourceRevisions on activity.SourceRevisionId equals input.Id
        join owner in context.SourceRevisions on input.ParentSourceRevisionId equals (Guid?)owner.Id
        join publication in context.DocumentPublications on owner.Id equals publication.OwnerSourceRevisionId
        join record in context.PipelineRecords on activity.ResultingPipelineRecordId equals (Guid?)record.Id
        where (rootId == null || owner.SourceRootId == rootId) &&
            input.SourceRootId == owner.SourceRootId && record.SourceRevisionId == input.Id &&
            record.Revision == activity.ResultingPipelineRecordRevision &&
            EF.Functions.Collate(record.ContentHash, SchemaConfiguration.SchedulerFenceCollation) ==
                EF.Functions.Collate(input.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
            SqlCurrentSourceActivities.ResolvedDocumentRecoveryPredecessors(context).Contains(activity.Id)
        select new Resolution { ActivityId = activity.Id, SourceRevisionId = input.Id, RootId = owner.SourceRootId, OwnerId = owner.Id, BranchId = null,
            PipelineId = record.Id, PipelineRevision = record.Revision, PublishedAtUtc = publication.PublishedAtUtc };

    // Only the four shipped structural processors are recognised. Strictly increasing
    // ranks bound the policy history to three edges and exclude cycles and unknown versions.
    private static IQueryable<OfficeActivity> OfficeActivities(FluxKnowledgeDbContext context) =>
        context.SourceActivities.AsNoTracking().Where(activity =>
            activity.ProcessorVersion == OoxmlStructuralTextProcessor.LegacyProcessorVersion && activity.DescriptorFingerprint == OoxmlStructuralTextProcessor.LegacyProcessorFingerprint ||
            activity.ProcessorVersion == OoxmlStructuralTextProcessor.PreviousProcessorVersion && activity.DescriptorFingerprint == OoxmlStructuralTextProcessor.PreviousProcessorFingerprint ||
            activity.ProcessorVersion == OoxmlStructuralTextProcessor.Version3ProcessorVersion && activity.DescriptorFingerprint == OoxmlStructuralTextProcessor.Version3ProcessorFingerprint ||
            activity.ProcessorVersion == OoxmlStructuralTextProcessor.Capability.ProcessorVersion && activity.DescriptorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint)
        .Select(activity => new OfficeActivity
        {
            Id = activity.Id, SourceRevisionId = activity.SourceRevisionId, InputHash = activity.InputFingerprint,
            // Both descriptor columns have the binary fence collation. Filter the
            // known pairs once; rank comparisons need only the recognised version.
            Rank = activity.ProcessorVersion == OoxmlStructuralTextProcessor.LegacyProcessorVersion ? 1 :
                activity.ProcessorVersion == OoxmlStructuralTextProcessor.PreviousProcessorVersion ? 2 :
                activity.ProcessorVersion == OoxmlStructuralTextProcessor.Version3ProcessorVersion ? 3 : 4
        });

    private static IQueryable<OfficePath> OfficePolicyHistory(FluxKnowledgeDbContext context)
    {
        var activities = OfficeActivities(context);
        var relations = context.SourceActivityRelations.AsNoTracking().Where(relation =>
            EF.Functions.Collate(relation.RelationshipKind, SchemaConfiguration.SchedulerFenceCollation) == RetainedDefaultPolicy.RelationshipKind &&
            EF.Functions.Collate(relation.ReasonCode, SchemaConfiguration.SchedulerFenceCollation) == RetainedDefaultPolicy.Version);
        // Relation endpoints are unique. Walk that one path once instead of
        // expanding the same publication proof across three UNION branches.
        return from prior in activities
            join edge1 in relations on prior.Id equals edge1.PredecessorActivityId
            join first in activities on edge1.SuccessorActivityId equals first.Id
            join edge2 in relations on first.Id equals edge2.PredecessorActivityId into secondEdges
            from edge2 in secondEdges.DefaultIfEmpty()
            join second in activities on (Guid?)edge2.SuccessorActivityId equals (Guid?)second.Id into seconds
            from second in seconds.DefaultIfEmpty()
            join edge3 in relations on (Guid?)second.Id equals (Guid?)edge3.PredecessorActivityId into thirdEdges
            from edge3 in thirdEdges.DefaultIfEmpty()
            join third in activities on (Guid?)edge3.SuccessorActivityId equals (Guid?)third.Id into thirds
            from third in thirds.DefaultIfEmpty()
            where first.Rank > prior.Rank && prior.SourceRevisionId == first.SourceRevisionId &&
                EF.Functions.Collate(prior.InputHash, SchemaConfiguration.SchedulerFenceCollation) ==
                    EF.Functions.Collate(first.InputHash, SchemaConfiguration.SchedulerFenceCollation)
            let secondValid = second.Rank > first.Rank && second.SourceRevisionId == prior.SourceRevisionId &&
                EF.Functions.Collate(second.InputHash, SchemaConfiguration.SchedulerFenceCollation) ==
                    EF.Functions.Collate(prior.InputHash, SchemaConfiguration.SchedulerFenceCollation)
            let thirdValid = secondValid && third.Rank > second.Rank && third.SourceRevisionId == prior.SourceRevisionId &&
                EF.Functions.Collate(third.InputHash, SchemaConfiguration.SchedulerFenceCollation) ==
                    EF.Functions.Collate(prior.InputHash, SchemaConfiguration.SchedulerFenceCollation)
            select new OfficePath { PriorId = prior.Id, OwnerId = prior.SourceRevisionId, FirstId = first.Id,
                SecondId = secondValid ? (Guid?)second.Id : null, ThirdId = thirdValid ? (Guid?)third.Id : null };
    }

    private static IQueryable<OfficeBranchResolution> ResolvedOfficeBranches(FluxKnowledgeDbContext context, Guid? rootId)
    {
        var history = OfficePolicyHistory(context);
        return from publication in context.DocumentPublications.AsNoTracking()
            join owner in context.SourceRevisions on publication.OwnerSourceRevisionId equals owner.Id
            join root in context.SourceRootConfigurations on owner.SourceRootId equals root.Id
            join branch in context.SourceProcessorBranches on publication.SourceProcessorBranchId equals branch.Id
            join activity in context.SourceActivities on branch.SourceActivityId equals activity.Id
            join member in context.SourceProcessorBranchMembers on branch.Id equals member.BranchId
            join child in context.SourceRevisions on member.ChildSourceRevisionId equals (Guid?)child.Id
            join childActivity in context.SourceActivities on member.ChildSourceActivityId equals (Guid?)childActivity.Id
            join record in context.PipelineRecords on publication.PipelineRecordId equals record.Id
            join link in history on owner.Id equals link.OwnerId
            join priorBranch in context.SourceProcessorBranches on link.PriorId equals priorBranch.SourceActivityId
            join priorActivity in context.SourceActivities on link.PriorId equals priorActivity.Id
            where (rootId == null || owner.SourceRootId == rootId) && root.State != (int)SourceRootState.Deleting &&
                (activity.Id == link.FirstId || activity.Id == link.SecondId || activity.Id == link.ThirdId) &&
                owner.OriginKind == 0 && owner.SuppressedAtUtc == null &&
                branch.SourceRevisionId == owner.Id && branch.State == (int)RetainedProcessorBranchState.Completed &&
                branch.LeaseOwner == null && branch.LeaseExpiresAtUtc == null && branch.CompletedMemberCount == 1 &&
                branch.CompletionReceiptFingerprint != null && branch.CompletionReceiptFingerprint!.Length == 64 &&
                activity.SourceRevisionId == owner.Id && branch.InputSha256 == owner.ContentSha256 &&
                activity.InputFingerprint == EF.Functions.Collate(owner.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) && activity.ProcessorVersion == branch.ProcessorVersion &&
                activity.DescriptorFingerprint == branch.ProcessorFingerprint &&
                branch.ProcessorFingerprint == publication.ProcessorFingerprint &&
                priorBranch.SourceRevisionId == owner.Id && priorBranch.InputSha256 == owner.ContentSha256 &&
                priorBranch.ProcessorVersion == priorActivity.ProcessorVersion && priorBranch.ProcessorFingerprint == priorActivity.DescriptorFingerprint &&
                child.Id == publication.DocumentInputSourceRevisionId && child.ParentSourceRevisionId == owner.Id &&
                child.SourceRootId == owner.SourceRootId && child.SuppressedAtUtc == null && child.OriginKind == 2 &&
                child.Classification == "AcceptedUtf8Text" && child.Extension == ".txt" && child.ByteLength == member.ByteLength &&
                member.Disposition == "completed" && !context.SourceProcessorBranchMembers.Any(other => other.BranchId == branch.Id && other.Id != member.Id) &&
                childActivity.SourceRevisionId == child.Id && childActivity.InputFingerprint == EF.Functions.Collate(child.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                childActivity.State == (int)SourceActivityState.Completed && childActivity.ResultingPipelineRecordId == record.Id &&
                childActivity.ResultingPipelineRecordRevision == record.Revision &&
                record.SourceRevisionId == child.Id && record.ContentHash == child.ContentSha256 &&
                record.Revision == publication.PipelineRecordRevision && record.CurrentStage == (int)PipelineStage.Publish &&
                record.CompletionCriteriaMet && !record.IsDeleted &&
                context.SourceArtifacts.Any(artifact => artifact.SourceRevisionId == owner.Id && artifact.ContentSha256 == owner.ContentSha256 &&
                    artifact.ByteLength == owner.ByteLength && artifact.ChecksumVerifiedAtUtc > DateTimeOffset.MinValue && artifact.ReferenceCount > 0) &&
                context.SourceArtifacts.Any(artifact => artifact.SourceRevisionId == child.Id && artifact.ContentSha256 == child.ContentSha256 &&
                    artifact.ByteLength == child.ByteLength && artifact.ChecksumVerifiedAtUtc > DateTimeOffset.MinValue && artifact.ReferenceCount > 0) &&
                context.Jobs.Count(job => job.PipelineRecordId == record.Id && job.SourceRevision == record.Revision) == 5 &&
                context.Jobs.Where(job => job.PipelineRecordId == record.Id && job.SourceRevision == record.Revision &&
                    job.PublicState == (int)PublicJobState.Completed && job.LeaseOwner == null && job.LeaseExpiresAtUtc == null &&
                    job.Stage >= (int)PipelineStage.Extract && job.Stage <= (int)PipelineStage.Publish).Select(job => job.Stage).Distinct().Count() == 5
            select new OfficeBranchResolution { BranchId = priorBranch.Id, OwnerId = owner.Id,
                RootId = owner.SourceRootId, PublishedAtUtc = publication.PublishedAtUtc };
    }

    private static IQueryable<Resolution> ResolvedOfficeBindings(FluxKnowledgeDbContext context, IQueryable<OfficeBranchResolution> branches) =>
        from proof in branches
        join member in context.SourceProcessorBranchMembers on proof.BranchId equals member.BranchId into priorMembers
        from member in priorMembers.DefaultIfEmpty()
        join input in context.SourceRevisions on member.ChildSourceRevisionId equals (Guid?)input.Id into priorInputs
        from input in priorInputs.DefaultIfEmpty()
        join activity in context.SourceActivities on member.ChildSourceActivityId equals (Guid?)activity.Id into priorActivities
        from activity in priorActivities.DefaultIfEmpty()
        join record in context.PipelineRecords on activity.ResultingPipelineRecordId equals (Guid?)record.Id into priorRecords
        from record in priorRecords.DefaultIfEmpty()
        let bound = member.Disposition == "completed" && input.ParentSourceRevisionId == proof.OwnerId && input.SourceRootId == proof.RootId &&
            input.OriginKind == 2 && input.ByteLength == member.ByteLength && activity.SourceRevisionId == input.Id &&
            activity.InputFingerprint == EF.Functions.Collate(input.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) && record.SourceRevisionId == input.Id &&
            record.ContentHash == input.ContentSha256 && record.Revision == activity.ResultingPipelineRecordRevision
        select new Resolution { ActivityId = bound ? (Guid?)activity.Id : null, SourceRevisionId = bound ? (Guid?)input.Id : null,
            RootId = proof.RootId, OwnerId = proof.OwnerId, BranchId = proof.BranchId,
            PipelineId = bound ? (Guid?)record.Id : null, PipelineRevision = bound ? (long?)record.Revision : null, PublishedAtUtc = proof.PublishedAtUtc };

    private sealed class OfficeActivity
    {
        public Guid Id { get; init; }
        public Guid SourceRevisionId { get; init; }
        public string InputHash { get; init; } = "";
        public int Rank { get; init; }
    }

    private sealed class OfficePath
    {
        public Guid PriorId { get; init; }
        public Guid OwnerId { get; init; }
        public Guid FirstId { get; init; }
        public Guid? SecondId { get; init; }
        public Guid? ThirdId { get; init; }
    }

    private sealed class OfficeBranchResolution
    {
        public Guid BranchId { get; init; }
        public Guid OwnerId { get; init; }
        public Guid RootId { get; init; }
        public DateTimeOffset PublishedAtUtc { get; init; }
    }

    private sealed class Resolution
    {
        public Guid? ActivityId { get; init; }
        public Guid? SourceRevisionId { get; init; }
        public Guid RootId { get; init; }
        public Guid OwnerId { get; init; }
        public Guid? BranchId { get; init; }
        public Guid? PipelineId { get; init; }
        public long? PipelineRevision { get; init; }
        public DateTimeOffset PublishedAtUtc { get; init; }
    }
}
