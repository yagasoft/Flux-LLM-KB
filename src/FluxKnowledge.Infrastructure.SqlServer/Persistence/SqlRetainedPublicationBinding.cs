using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Read-only lineage proof shared by publication and explicit recovery.</summary>
internal sealed record SqlRetainedPublicationBinding(SourceRevisionEntity Input, SourceRevisionEntity Owner,
    SourceProcessorBranchEntity Branch, SourceProcessorBranchMemberEntity Member, SourceActivityEntity OwnerActivity,
    SourceActivityEntity ChildActivity, SourceArtifactEntity ChildArtifact)
{
    internal static async ValueTask<SqlRetainedPublicationBinding?> ReadAsync(FluxKnowledgeDbContext context,
        PipelineRecordEntity record, CancellationToken cancellationToken)
    {
        if (record.SourceRevisionId is not { } inputRevisionId)
        {
            return null;
        }

        var input = await context.SourceRevisions.SingleOrDefaultAsync(
            value => value.Id == inputRevisionId,
            cancellationToken).ConfigureAwait(false);
        if (input is null || input.ParentSourceRevisionId is not { } ownerRevisionId)
        {
            return null;
        }
        var isDocumentInput = DocumentProcessingInput.TryGetContract(
            input.Classification, input.Extension, out var documentContract);
        var isLogicalText = input.OriginKind is 2 or 3 &&
            input.Classification == "AcceptedUtf8Text" && input.Extension == ".txt";
        if (!isDocumentInput && !isLogicalText)
        {
            return null;
        }

        // Publish is the final externally visible mutation for a retained document.  A source
        // pause, suppression, or deletion that committed after this worker was claimed must win
        // over its stale claim.  The enclosing Publish transaction is serializable, so this read
        // is also the publication fence against a concurrent root-state transition.
        var sourceIsPublishable = await (
                from candidate in context.SourceRevisions
                join owner in context.SourceRevisions on candidate.ParentSourceRevisionId equals owner.Id
                join root in context.SourceRootConfigurations on owner.SourceRootId equals root.Id
                where candidate.Id == inputRevisionId &&
                      candidate.SourceRootId == owner.SourceRootId &&
                      candidate.SuppressedAtUtc == null &&
                      owner.SuppressedAtUtc == null &&
                      root.State == (int)SourceRootState.Enabled
                select root.Id)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!sourceIsPublishable)
        {
            throw new InvalidOperationException("document-publication-source-unavailable");
        }

        var binding = await (
            from member in context.SourceProcessorBranchMembers
            join candidateBranch in context.SourceProcessorBranches on member.BranchId equals candidateBranch.Id
            join childActivity in context.SourceActivities on member.ChildSourceActivityId equals childActivity.Id
            join ownerActivity in context.SourceActivities on candidateBranch.SourceActivityId equals ownerActivity.Id
            join childArtifact in context.SourceArtifacts on member.ChildSourceRevisionId equals childArtifact.SourceRevisionId
            join ownerRevision in context.SourceRevisions on candidateBranch.SourceRevisionId equals ownerRevision.Id
            where member.ChildSourceRevisionId == inputRevisionId &&
                  member.Disposition == "completed" &&
                  member.ByteLength == input.ByteLength &&
                  childActivity.SourceRevisionId == inputRevisionId &&
                  EF.Functions.Collate(childActivity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) ==
                      EF.Functions.Collate(input.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                  childActivity.ResultingPipelineRecordId == record.Id &&
                  childActivity.ResultingPipelineRecordRevision == record.Revision &&
                  EF.Functions.Collate(record.ContentHash, SchemaConfiguration.SchedulerFenceCollation) ==
                      EF.Functions.Collate(input.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                  EF.Functions.Collate(childArtifact.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                      EF.Functions.Collate(input.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                  childArtifact.ByteLength == input.ByteLength &&
                  candidateBranch.SourceRevisionId == ownerRevisionId &&
                  ownerRevision.SourceRootId == input.SourceRootId &&
                  EF.Functions.Collate(candidateBranch.InputSha256, SchemaConfiguration.SchedulerFenceCollation) ==
                      EF.Functions.Collate(ownerRevision.ContentSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                  candidateBranch.State == (int)RetainedProcessorBranchState.Completed &&
                  candidateBranch.CompletedMemberCount == 1 &&
                  context.SourceProcessorBranchMembers.Count(other => other.BranchId == candidateBranch.Id) == 1 &&
                  ownerActivity.SourceRevisionId == ownerRevisionId &&
                  EF.Functions.Collate(ownerActivity.InputFingerprint, SchemaConfiguration.SchedulerFenceCollation) ==
                      EF.Functions.Collate(candidateBranch.InputSha256, SchemaConfiguration.SchedulerFenceCollation) &&
                  EF.Functions.Collate(ownerActivity.ProcessorVersion, SchemaConfiguration.SchedulerFenceCollation) ==
                      EF.Functions.Collate(candidateBranch.ProcessorVersion, SchemaConfiguration.SchedulerFenceCollation) &&
                  (EF.Functions.Collate(ownerActivity.DescriptorFingerprint, SchemaConfiguration.SchedulerFenceCollation) ==
                      EF.Functions.Collate(candidateBranch.ProcessorFingerprint, SchemaConfiguration.SchedulerFenceCollation) ||
                   (candidateBranch.ProcessorFingerprint == ImageDocumentInputProcessor.Capability.ProcessorFingerprint ||
                    candidateBranch.ProcessorFingerprint == PdfDocumentProcessor.Capability.ProcessorFingerprint) &&
                   ownerActivity.DescriptorFingerprint == DocumentProcessingInput.DocumentRecoveryActivityPrefix + ownerActivity.Id.ToString().Replace("-", "").ToLower())
            select new { Branch = candidateBranch, OwnerActivity = ownerActivity, ChildActivity = childActivity, Owner = ownerRevision, Member = member, ChildArtifact = childArtifact }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (binding is null)
        {
            throw new InvalidOperationException("The document publication has no exact completed retained-processor binding.");
        }
        var branch = binding.Branch;
        if (binding.OwnerActivity.DescriptorFingerprint != branch.ProcessorFingerprint &&
            !await SqlDocumentInputRecovery.InputBindingIsCurrentAsync(context, inputRevisionId, binding.ChildActivity.Id, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("document-recovery-binding-stale");
        var exactBinaryBinding = isDocumentInput &&
            branch.ProcessorFingerprint == documentContract.ParentProcessorFingerprint &&
            branch.ProcessorVersion == documentContract.ParentProcessorVersion;
        var exactLogicalBinding = isLogicalText &&
            await HasExpectedLogicalProducerAsync(context, branch, cancellationToken).ConfigureAwait(false);
        if (!exactBinaryBinding && !exactLogicalBinding)
        {
            throw new InvalidOperationException("The document publication has no exact completed retained-processor binding.");
        }

        return new(input, binding.Owner, branch, binding.Member, binding.OwnerActivity, binding.ChildActivity, binding.ChildArtifact);
    }

    private static ValueTask<bool> HasExpectedLogicalProducerAsync(
        FluxKnowledgeDbContext context,
        SourceProcessorBranchEntity branch,
        CancellationToken cancellationToken)
    {
        var expected = OoxmlStructuralTextProcessor.IsKnownProcessor(branch.ProcessorVersion, branch.ProcessorFingerprint)
            ? OoxmlStructuralTextProcessor.Capability
            : MediaMetadataRetainedProcessor.IsKnownProcessor(branch.ProcessorVersion, branch.ProcessorFingerprint)
                ? MediaMetadataRetainedProcessor.Capability
                : null;
        return expected is null
            ? ValueTask.FromResult(false)
            : new ValueTask<bool>(context.SourceCapabilities.AnyAsync(capability =>
                capability.Id == expected.Id &&
                capability.ProcessorKind == expected.ProcessorKind &&
                capability.IsRunnable &&
                capability.ExecutionClass == (int)ExecutionClass.InProcess &&
                capability.ProcessorVersion == expected.ProcessorVersion &&
                capability.ProcessorFingerprint == expected.ProcessorFingerprint &&
                capability.OutputContract == expected.OutputContract,
                cancellationToken));
    }

}
