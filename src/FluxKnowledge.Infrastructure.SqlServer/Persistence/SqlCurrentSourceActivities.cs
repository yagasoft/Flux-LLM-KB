using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Current status excludes a policy predecessor only when its exact retained binding has a recognised durable successor.</summary>
public static class SqlCurrentSourceActivities
{
    public static IQueryable<SourceActivityEntity> Query(FluxKnowledgeDbContext context)
    {
        var resolvedDocumentFailures = ResolvedDocumentRecoveryPredecessors(context);
        return context.SourceActivities.AsNoTracking()
        .Where(activity => activity.State != (int)FluxKnowledge.Domain.Sources.SourceActivityState.CancelledSuperseded || activity.Reason != RetainedDefaultPolicy.RelationshipKind)
        .Where(activity => !context.SourceActivityRelations.Any(relation =>
            relation.PredecessorActivityId == activity.Id && relation.RelationshipKind == RetainedDefaultPolicy.RelationshipKind &&
            relation.ReasonCode == RetainedDefaultPolicy.Version && context.SourceActivities.Any(successor =>
                successor.Id == relation.SuccessorActivityId && successor.SourceRevisionId == activity.SourceRevisionId &&
                successor.InputFingerprint == activity.InputFingerprint &&
                (successor.ProcessorVersion == RetainedDefaultPolicy.Version && successor.DescriptorFingerprint == RetainedDefaultPolicy.TextFingerprint ||
                 successor.ProcessorVersion == RetainedDefaultPolicy.UnsupportedBinary.ProcessorVersion && successor.DescriptorFingerprint == RetainedDefaultPolicy.UnsupportedBinary.ProcessorFingerprint ||
                 successor.ProcessorVersion == OoxmlStructuralTextProcessor.Capability.ProcessorVersion && successor.DescriptorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint ||
                 successor.ProcessorVersion == OoxmlStructuralTextProcessor.PreviousProcessorVersion && successor.DescriptorFingerprint == OoxmlStructuralTextProcessor.PreviousProcessorFingerprint || successor.ProcessorVersion == OoxmlStructuralTextProcessor.Version3ProcessorVersion && successor.DescriptorFingerprint == OoxmlStructuralTextProcessor.Version3ProcessorFingerprint ||
                 successor.ProcessorVersion == MediaMetadataRetainedProcessor.Capability.ProcessorVersion && successor.DescriptorFingerprint == MediaMetadataRetainedProcessor.Capability.ProcessorFingerprint ||
                 successor.ProcessorVersion == ZipArchiveRetainedProcessor.Capability.ProcessorVersion && successor.DescriptorFingerprint == ZipArchiveRetainedProcessor.Capability.ProcessorFingerprint ||
                 successor.ProcessorVersion == TarArchiveRetainedProcessor.Capability.ProcessorVersion && successor.DescriptorFingerprint == TarArchiveRetainedProcessor.Capability.ProcessorFingerprint ||
                 successor.ProcessorVersion == RetainedCsharpCodeProcessor.ProcessorVersion && successor.DescriptorFingerprint == RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint))))
        .Where(activity => !resolvedDocumentFailures.Contains(activity.Id));
    }

    internal static IQueryable<Guid> ResolvedDocumentRecoveryPredecessors(FluxKnowledgeDbContext context) =>
        from relation in context.SourceActivityRelations.AsNoTracking()
        join prior in context.SourceActivities on relation.PredecessorActivityId equals prior.Id
        join successor in context.SourceActivities on relation.SuccessorActivityId equals successor.Id
        join input in context.SourceRevisions on successor.SourceRevisionId equals input.Id
        join priorInput in context.SourceRevisions on prior.SourceRevisionId equals priorInput.Id
        join owner in context.SourceRevisions on input.ParentSourceRevisionId equals (Guid?)owner.Id
        join publication in context.DocumentPublications on owner.Id equals publication.OwnerSourceRevisionId
        join record in context.PipelineRecords on successor.ResultingPipelineRecordId equals (Guid?)record.Id
        join member in context.SourceProcessorBranchMembers on (Guid?)input.Id equals member.ChildSourceRevisionId
        join branch in context.SourceProcessorBranches on member.BranchId equals branch.Id
        join ownerActivity in context.SourceActivities on branch.SourceActivityId equals ownerActivity.Id
        where relation.RelationshipKind == DocumentProcessingInput.DocumentRecoveryChildRelationship && relation.ReasonCode == DocumentProcessingInput.DocumentRecoveryVersion &&
            successor.State == (int)SourceActivityState.Completed && successor.InputFingerprint == prior.InputFingerprint &&
            input.SuppressedAtUtc == null && input.OriginKind == 2 &&
            EF.Functions.Collate(input.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) == EF.Functions.Collate(successor.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) &&
            priorInput.ParentSourceRevisionId == owner.Id && priorInput.SourceRootId == input.SourceRootId && priorInput.ContentSha256 == input.ContentSha256 &&
            owner.SuppressedAtUtc == null && owner.ContentSha256 == input.ContentSha256 && owner.SourceRootId == input.SourceRootId &&
            record.Revision == successor.ResultingPipelineRecordRevision && record.SourceRevisionId == input.Id && record.CompletionCriteriaMet && !record.IsDeleted &&
            record.ContentHash == input.ContentSha256 && publication.DocumentInputSourceRevisionId == input.Id &&
            publication.PipelineRecordId == record.Id && publication.PipelineRecordRevision == record.Revision && publication.SourceProcessorBranchId == branch.Id &&
            member.ChildSourceActivityId == successor.Id && member.Disposition == "completed" && member.ByteLength == input.ByteLength &&
            branch.State == (int)RetainedProcessorBranchState.Completed && branch.SourceRevisionId == owner.Id &&
            EF.Functions.Collate(branch.InputSha256, SchemaConfiguration.SchedulerFenceCollation) == EF.Functions.Collate(input.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
            EF.Functions.Collate(branch.ProcessorFingerprint, SchemaConfiguration.SchedulerFenceCollation) == EF.Functions.Collate(publication.ProcessorFingerprint, SchemaConfiguration.SchedulerFenceCollation) && branch.CompletedMemberCount == 1 &&
            (branch.ProcessorFingerprint == ImageDocumentInputProcessor.Capability.ProcessorFingerprint || branch.ProcessorFingerprint == PdfDocumentProcessor.Capability.ProcessorFingerprint) &&
            ownerActivity.SourceRevisionId == owner.Id &&
            EF.Functions.Collate(ownerActivity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) == EF.Functions.Collate(owner.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
            EF.Functions.Collate(ownerActivity.ProcessorVersion, SchemaConfiguration.SchedulerFenceCollation) == EF.Functions.Collate(branch.ProcessorVersion, SchemaConfiguration.SchedulerFenceCollation) &&
            EF.Functions.Collate(ownerActivity.DescriptorFingerprint, SchemaConfiguration.SchedulerFenceCollation) ==
                EF.Functions.Collate(DocumentProcessingInput.DocumentRecoveryActivityPrefix + ownerActivity.Id.ToString().Replace("-", "").ToLower(), SchemaConfiguration.SchedulerFenceCollation) &&
            context.Jobs.Where(job => job.PipelineRecordId == record.Id && job.SourceRevision == record.Revision &&
                job.PublicState == (int)PublicJobState.Completed && job.Stage >= (int)PipelineStage.Extract && job.Stage <= (int)PipelineStage.Publish)
                .Select(job => job.Stage).Distinct().Count() == 5
        select prior.Id;
}
