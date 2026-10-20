using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

internal sealed partial record SqlPublicationRetry
{
    private static async Task<IReadOnlyList<NativeTargetVersion>> ReadDerivedTargetsAsync(FluxKnowledgeDbContext context,
        PipelineRecordEntity record, SourceRevisionEntity source, SourceActivityEntity activity, CancellationToken ct)
    {
        SqlRetainedPublicationBinding? binding;
        try { binding = await SqlRetainedPublicationBinding.ReadAsync(context, record, ct); }
        catch (InvalidOperationException) { Refuse(); throw; }
        if (binding is null) Refuse();
        var owner = binding.Owner;
        var branch = binding.Branch;
        var binary = DocumentProcessingInput.TryGetContract(source.Classification, source.Extension, out var contract);
        if (owner.OriginKind != 0 || owner.SourceRootId != source.SourceRootId || owner.SuppressedAtUtc is not null ||
            binding.ChildActivity.Id != activity.Id || activity.InputFingerprint != source.ContentSha256 ||
            activity.ExecutionClass != (int)ExecutionClass.InProcess ||
            (binary ? activity.ActivityKind != (int)SourceActivityKind.DocumentParsing ||
                      activity.ProcessorVersion != contract.ProcessorVersion || activity.DescriptorFingerprint != contract.ProcessorFingerprint :
                      activity.ActivityKind != (int)SourceActivityKind.TextExtraction && activity.ActivityKind != (int)SourceActivityKind.MetadataExtraction) ||
            branch.LeaseOwner is not null || branch.LeaseExpiresAtUtc is not null || branch.CompletionReceiptFingerprint is not { Length: 64 } ||
            binding.ChildArtifact.ReferenceCount < 1 || binding.ChildArtifact.ChecksumVerifiedAtUtc == default) Refuse();
        var ownerArtifacts = await context.SourceArtifacts.Where(value => value.SourceRevisionId == owner.Id).ToArrayAsync(ct);
        if (ownerArtifacts.Length != 1 || ownerArtifacts[0].ContentSha256 != owner.ContentSha256 ||
            ownerArtifacts[0].ByteLength != owner.ByteLength || ownerArtifacts[0].ReferenceCount < 1 ||
            ownerArtifacts[0].ChecksumVerifiedAtUtc == default ||
            binary && (source.ContentSha256 != owner.ContentSha256 || source.ByteLength != owner.ByteLength)) Refuse();
        // An explicit child binding cannot establish which competing physical revision is current.
        if (await context.SourceRevisions.AnyAsync(value => value.Id != owner.Id && value.SourceRootId == owner.SourceRootId &&
                value.OriginKind == 0 && value.SuppressedAtUtc == null &&
                (value.StableSourceIdentity == owner.StableSourceIdentity || value.CanonicalPathFingerprint == owner.CanonicalPathFingerprint), ct) ||
            await context.DocumentPublications.AnyAsync(value => value.OwnerSourceRevisionId == owner.Id, ct) ||
            await context.SourceProcessorBranches.AnyAsync(value => value.SourceRevisionId == owner.Id && value.Id != branch.Id &&
                value.State != (int)RetainedProcessorBranchState.Blocked, ct)) Refuse();
        var preceding = await context.Jobs.Where(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision &&
            value.Stage >= (int)PipelineStage.Extract && value.Stage < (int)PipelineStage.Publish).ToArrayAsync(ct);
        if (preceding.Length != 4 || preceding.Select(value => value.Stage).Distinct().Count() != 4 ||
            preceding.Any(value => value.PublicState != (int)PublicJobState.Completed || value.LeaseOwner is not null || value.LeaseExpiresAtUtc is not null)) Refuse();
        var targets = new List<NativeTargetVersion>
        {
            Version("publication-owner", owner.Id, owner.RowVersion),
            Version("publication-branch", branch.Id, branch.RowVersion),
            Version("publication-owner-activity", binding.OwnerActivity.Id, binding.OwnerActivity.RowVersion),
            Version("publication-owner-artifact", ownerArtifacts[0].Id, ownerArtifacts[0].RowVersion),
            Version("publication-child-artifact", binding.ChildArtifact.Id, binding.ChildArtifact.RowVersion),
            new($"publication-member:{binding.Member.Id:D}", Hash(binding.Member))
        };
        foreach (var stage in preceding) targets.Add(Version("publication-prior-job", stage.Id, stage.RowVersion));
        if (!binary)
        {
            // The shared publication rule recognises historical producer versions using
            // the current runnable registry. Bind that same registry to this confirmation.
            var capability = await context.SourceCapabilities.SingleAsync(value =>
                value.Id == (OoxmlStructuralTextProcessor.IsKnownProcessor(branch.ProcessorVersion, branch.ProcessorFingerprint)
                    ? OoxmlStructuralTextProcessor.Capability.Id : MediaMetadataRetainedProcessor.Capability.Id), ct);
            targets.Add(Version("publication-producer", capability.Id, capability.RowVersion));
        }
        targets.AddRange(await ReadDerivedNativeTargetsAsync(context, record, ct));
        return targets;
    }

    private static async Task<IReadOnlyList<NativeTargetVersion>> ReadDerivedNativeTargetsAsync(
        FluxKnowledgeDbContext context, PipelineRecordEntity record, CancellationToken ct)
    {
        // Publication reuses completed inputs; it never dispatches new GPU work or
        // resolves uncertainty. Refuse any current native ownership on these stages.
        var tasks = await (from task in context.GpuMiniTasks
            join job in context.Jobs on task.ParentJobId equals job.Id
            where job.PipelineRecordId == record.Id && job.SourceRevision == record.Revision
            select task).ToArrayAsync(ct);
        if (tasks.Any(value => value.ExecutionState != (int)GpuMiniTaskExecutionState.Completed ||
                value.SourceRevision != record.Revision || value.BatchId is null)) Refuse();
        var batchIds = tasks.Select(value => value.BatchId!.Value).Distinct().ToArray();
        var batches = await context.GpuBatches.Where(value => batchIds.Contains(value.Id)).ToArrayAsync(ct);
        var dispatches = await context.GpuExecutorDispatches.Where(value => batchIds.Contains(value.BatchId)).ToArrayAsync(ct);
        if (batches.Length != batchIds.Length || batches.Any(value => value.State is not ((int)GpuBatchState.Completed) and not ((int)GpuBatchState.Released)) ||
            dispatches.Length != batchIds.Length || dispatches.Select(value => value.BatchId).Distinct().Count() != batchIds.Length ||
            dispatches.Any(value => value.State != (int)GpuExecutorDispatchState.Terminal) ||
            await context.GpuCapacitySlots.AnyAsync(value => value.ActiveBatchId != null && batchIds.Contains(value.ActiveBatchId.Value), ct)) Refuse();
        var dispatchIds = dispatches.Select(value => value.DispatchId).ToArray();
        if (await context.NativeWorkerInstances.AnyAsync(value => value.ActiveDispatchId != null &&
                dispatchIds.Contains(value.ActiveDispatchId.Value) && value.ExitedAtUtc == null, ct) ||
            await context.GpuExecutorEvidence.AnyAsync(value => dispatchIds.Contains(value.DispatchId) &&
                value.EvidenceClass == (int)GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed, ct)) Refuse();
        var targets = new List<NativeTargetVersion>();
        foreach (var task in tasks) targets.Add(Version("publication-native-task", task.Id, task.RowVersion));
        foreach (var batch in batches) targets.Add(Version("publication-native-batch", batch.Id, batch.RowVersion));
        foreach (var dispatch in dispatches) targets.Add(Version("publication-native-dispatch", dispatch.DispatchId, dispatch.RowVersion));
        // Keep the public confirmation target budget independent of document size.
        // Every native identity/version still participates, in deterministic order.
        return [new($"publication-native:{record.Id:D}", Hash(targets.OrderBy(value => value.TargetId, StringComparer.Ordinal).ToArray()))];
    }
}
