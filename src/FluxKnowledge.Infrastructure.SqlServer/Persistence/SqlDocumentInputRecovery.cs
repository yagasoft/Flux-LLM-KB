using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>One exact retained document successor for a proved settled failure. Never resets old execution.</summary>
internal static class SqlDocumentInputRecovery
{
    private const string ParentRelationship = "superseded-by-retained-processor";
    private const string DimensionFailure = "image-document-dimensions-exceeded";
    private const string RuntimeFailure = "document-ocr-runtime-failed";
    private const string EncryptedPdfFailure = "pdf-document-encrypted";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<DocumentReprocessRequestResult> RequestAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,
        DocumentReprocessRequest request, CancellationToken token)
    {
        if (request.SourceRevisionId.Value == Guid.Empty || request.FailedExtractJobId is not { } jobId || jobId == Guid.Empty ||
            request.ExpectedRootConfigurationRevision is not > 0 || !TryGetDescriptor(request.ExpectedProcessorFingerprint, out var descriptor) ||
            request.ExpectedInputSha256.Length != 64 || request.ExpectedInputSha256.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            return DocumentReprocessRequestResult.NotEligible;
        await using var db = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
        await SqlPublishedPassageSelection.AcquireFenceAsync(db, token).ConfigureAwait(false);
        var existing = await (from relation in db.SourceActivityRelations
                              join activity in db.SourceActivities on relation.SuccessorActivityId equals activity.Id
                              join branch in db.SourceProcessorBranches on activity.Id equals branch.SourceActivityId
                              where activity.SourceRevisionId == request.SourceRevisionId.Value && relation.RelationshipKind == ParentRelationship &&
                                    relation.ReasonCode == DocumentProcessingInput.DocumentRecoveryVersion
                              select new { Activity = activity, Branch = branch }).ToArrayAsync(token).ConfigureAwait(false);
        if (existing.Length != 0)
        {
            if (existing.Length != 1 || !TryRead(existing[0].Activity, out var proof) || proof.Binding.FailedJobId != jobId ||
                proof.Binding.InputSha256 != request.ExpectedInputSha256 || proof.Binding.ConfigurationRevision != request.ExpectedRootConfigurationRevision ||
                proof.Binding.ProcessorFingerprint != descriptor.ProcessorFingerprint ||
                !await BindingIsCurrentAsync(db, existing[0].Activity.Id, token).ConfigureAwait(false))
                return DocumentReprocessRequestResult.NotEligible;
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new(false, true, existing[0].Branch.Id);
        }
        var binding = await ReadSettledFailureAsync(db, request.SourceRevisionId.Value, request.ExpectedInputSha256,
            request.ExpectedRootConfigurationRevision.Value, descriptor.ProcessorFingerprint, jobId, null, token).ConfigureAwait(false);
        if (binding is null || await db.SourceActivityRelations.AnyAsync(value => value.PredecessorActivityId == binding.PredecessorActivityId, token).ConfigureAwait(false))
            return DocumentReprocessRequestResult.NotEligible;
        var activityId = Guid.NewGuid(); var branchId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var admission = new RecoveryProof(DocumentProcessingInput.DocumentRecoveryVersion, activityId, branchId, binding);
        db.SourceActivities.Add(new SourceActivityEntity { Id = activityId, SourceRevisionId = binding.ParentRevisionId,
            ActivityKind = (int)SourceActivityKind.TextExtraction, ExecutionClass = (int)ExecutionClass.InProcess,
            ProcessorVersion = descriptor.ProcessorVersion, DescriptorFingerprint = ActivityFingerprint(activityId),
            InputFingerprint = binding.InputSha256, State = (int)SourceActivityState.Pending, AttemptEvidenceJson = JsonSerializer.Serialize(admission, JsonOptions),
            CreatedAtUtc = now, UpdatedAtUtc = now });
        db.SourceProcessorBranches.Add(new SourceProcessorBranchEntity { Id = branchId, SourceActivityId = activityId, SourceRevisionId = binding.ParentRevisionId,
            InputSha256 = binding.InputSha256, ProcessorVersion = descriptor.ProcessorVersion,
            ProcessorFingerprint = descriptor.ProcessorFingerprint, State = (int)RetainedProcessorBranchState.Pending,
            CreatedAtUtc = now, UpdatedAtUtc = now });
        db.SourceActivityRelations.Add(new SourceActivityRelationEntity { Id = Guid.NewGuid(), PredecessorActivityId = binding.PredecessorActivityId,
            SuccessorActivityId = activityId, RelationshipKind = ParentRelationship, ReasonCode = DocumentProcessingInput.DocumentRecoveryVersion, CreatedAtUtc = now });
        OperatorEventAppender.Add(db, new OperatorEventDraft("retained_processor.document_reprocess_requested", "retained_processor", "information", "trusted-local-cli", now,
            SourceRootId: binding.RootId, SourceRevisionId: binding.ParentRevisionId, SourceActivityId: activityId,
            CorrelationId: $"retained-processor:{branchId:N}", Details: new { kind = "document_input", reasonCode = DocumentProcessingInput.DocumentRecoveryVersion }));
        await db.SaveChangesAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new(true, false, branchId);
    }

    internal static string ActivityFingerprint(Guid activityId) => DocumentProcessingInput.DocumentRecoveryActivityPrefix + activityId.ToString("N");

    internal static bool IsMarked(SourceActivityEntity activity) =>
        activity.DescriptorFingerprint.StartsWith(DocumentProcessingInput.DocumentRecoveryActivityPrefix, StringComparison.Ordinal) ||
        activity.AttemptEvidenceJson?.Contains(DocumentProcessingInput.DocumentRecoveryVersion, StringComparison.Ordinal) == true;

    internal static Guid? RecoveryId(SourceActivityEntity activity) =>
        !IsMarked(activity)
            ? null : TryRead(activity, out var proof) ? proof.RecoveryActivityId : Guid.Empty;

    private static bool TryRead(SourceActivityEntity activity, out RecoveryProof proof)
    {
        proof = null!;
        if (activity.ResultingPipelineRecordId is not null || activity.ResultingPipelineRecordRevision is not null ||
            activity.DescriptorFingerprint != ActivityFingerprint(activity.Id) || activity.AttemptEvidenceJson is not { Length: > 0 and <= 16_384 } json)
            return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<RecoveryProof>(json, JsonOptions);
            if (parsed is null || parsed.Binding is null || parsed.Version != DocumentProcessingInput.DocumentRecoveryVersion ||
                parsed.RecoveryActivityId != activity.Id || parsed.RecoveryBranchId == Guid.Empty ||
                activity.SourceRevisionId != parsed.Binding.ParentRevisionId || activity.InputFingerprint != parsed.Binding.InputSha256 ||
                parsed.Binding.InputSha256 is not { Length: 64 } || parsed.Binding.ConfigurationRevision <= 0 ||
                !TryGetDescriptor(parsed.Binding.ProcessorFingerprint, out var descriptor) || activity.ProcessorVersion != descriptor.ProcessorVersion ||
                activity.ActivityKind != (int)SourceActivityKind.TextExtraction || activity.ExecutionClass != (int)ExecutionClass.InProcess) return false;
            proof = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static async Task<bool> BindingIsCurrentAsync(FluxKnowledgeDbContext db, Guid activityId, CancellationToken token)
    {
        var activity = await db.SourceActivities.SingleOrDefaultAsync(value => value.Id == activityId, token).ConfigureAwait(false);
        if (activity is null || !TryRead(activity, out var proof)) return false;
        var branch = await db.SourceProcessorBranches.SingleOrDefaultAsync(value => value.Id == proof.RecoveryBranchId && value.SourceActivityId == activityId, token).ConfigureAwait(false);
        if (branch is null || branch.SourceRevisionId != proof.Binding.ParentRevisionId || branch.InputSha256 != proof.Binding.InputSha256 ||
            branch.ProcessorFingerprint != proof.Binding.ProcessorFingerprint || branch.ProcessorVersion != activity.ProcessorVersion ||
            !await db.SourceActivityRelations.AnyAsync(value => value.PredecessorActivityId == proof.Binding.PredecessorActivityId &&
                value.SuccessorActivityId == activityId && value.RelationshipKind == ParentRelationship && value.ReasonCode == proof.Version, token).ConfigureAwait(false)) return false;
        var current = await ReadSettledFailureAsync(db, proof.Binding.ParentRevisionId, proof.Binding.InputSha256,
            proof.Binding.ConfigurationRevision, proof.Binding.ProcessorFingerprint, proof.Binding.FailedJobId, branch.Id, token).ConfigureAwait(false);
        return current == proof.Binding;
    }

    internal static async Task<Guid?> ReadRecoveryIdAsync(FluxKnowledgeDbContext db, Guid activityId, CancellationToken token) =>
        RecoveryId(await db.SourceActivities.SingleAsync(value => value.Id == activityId, token).ConfigureAwait(false));

    internal static async Task AddChildRelationAsync(FluxKnowledgeDbContext db, Guid activityId, Guid childActivityId, DateTimeOffset now, CancellationToken token)
    {
        var activity = await db.SourceActivities.SingleAsync(value => value.Id == activityId, token).ConfigureAwait(false);
        if (!TryRead(activity, out var proof)) throw new InvalidOperationException("The document recovery proof is missing or inconsistent.");
        var existing = await db.SourceActivityRelations.SingleOrDefaultAsync(value => value.PredecessorActivityId == proof.Binding.FailedChildActivityId ||
            value.SuccessorActivityId == childActivityId, token).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.PredecessorActivityId != proof.Binding.FailedChildActivityId || existing.SuccessorActivityId != childActivityId ||
                existing.RelationshipKind != DocumentProcessingInput.DocumentRecoveryChildRelationship || existing.ReasonCode != proof.Version)
                throw new InvalidOperationException("The document recovery child conflicts with existing lineage.");
            return;
        }
        db.SourceActivityRelations.Add(new SourceActivityRelationEntity { Id = Guid.NewGuid(), PredecessorActivityId = proof.Binding.FailedChildActivityId,
            SuccessorActivityId = childActivityId, RelationshipKind = DocumentProcessingInput.DocumentRecoveryChildRelationship, ReasonCode = proof.Version, CreatedAtUtc = now });
    }

    internal static async Task<bool> InputBindingIsCurrentAsync(FluxKnowledgeDbContext db, Guid childId, Guid childActivityId, CancellationToken token)
    {
        var owners = await (from member in db.SourceProcessorBranchMembers
                            join branch in db.SourceProcessorBranches on member.BranchId equals branch.Id
                            join activity in db.SourceActivities on branch.SourceActivityId equals activity.Id
                            where member.ChildSourceRevisionId == childId && member.ChildSourceActivityId == childActivityId &&
                                  (activity.DescriptorFingerprint.StartsWith(DocumentProcessingInput.DocumentRecoveryActivityPrefix) ||
                                   activity.AttemptEvidenceJson != null && activity.AttemptEvidenceJson.Contains(DocumentProcessingInput.DocumentRecoveryVersion) ||
                                   db.SourceActivityRelations.Any(relation => relation.SuccessorActivityId == activity.Id && relation.ReasonCode == DocumentProcessingInput.DocumentRecoveryVersion))
                            select activity).Take(2).ToArrayAsync(token).ConfigureAwait(false);
        if (owners.Length == 0) return true; // Ordinary and historical document contracts retain their existing guards.
        if (owners.Length != 1 || !TryRead(owners[0], out var proof) ||
            !await BindingIsCurrentAsync(db, owners[0].Id, token).ConfigureAwait(false)) return false;
        return await db.SourceActivityRelations.AnyAsync(value => value.PredecessorActivityId == proof.Binding.FailedChildActivityId &&
            value.SuccessorActivityId == childActivityId && value.RelationshipKind == DocumentProcessingInput.DocumentRecoveryChildRelationship &&
            value.ReasonCode == proof.Version, token).ConfigureAwait(false);
    }

    private static async Task<FailureBinding?> ReadSettledFailureAsync(FluxKnowledgeDbContext db, Guid parentId, string hash, long configuration,
        string processorFingerprint, Guid jobId, Guid? recoveryBranchId, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Document recovery requires a fenced transaction.");
        if (!TryGetDescriptor(processorFingerprint, out var descriptor)) return null;
        var pdf = processorFingerprint == PdfDocumentProcessor.Capability.ProcessorFingerprint;
        var contract = pdf ? DocumentProcessingInput.Pdf : DocumentProcessingInput.Jpeg;
        var parent = await db.SourceRevisions.SingleOrDefaultAsync(value => value.Id == parentId, token).ConfigureAwait(false);
        if (parent is null || parent.ParentSourceRevisionId is not null || parent.OriginKind != 0 || parent.SuppressedAtUtc is not null || parent.ContentSha256 != hash ||
            parent.ByteLength <= 0 || parent.ByteLength > (pdf ? PdfDocumentProcessor.MaximumInputBytes : ImageDocumentInputProcessor.MaximumInputBytes) ||
            (pdf ? !parent.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) : parent.Extension.ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png"))) return null;
        var root = await db.SourceRootConfigurations.SingleOrDefaultAsync(value => value.Id == parent.SourceRootId, token).ConfigureAwait(false);
        if (root is null || root.State != (int)SourceRootState.Enabled || root.ConfigurationRevision != configuration || root.FollowLinks ||
            !IsInScope(root, parent) ||
            await db.SourceRevisions.AnyAsync(value => value.Id != parentId && value.SourceRootId == parent.SourceRootId && value.SuppressedAtUtc == null &&
                (value.StableSourceIdentity == parent.StableSourceIdentity || value.OriginKind == 0 &&
                 EF.Functions.Collate(value.CanonicalPath, "Latin1_General_100_CI_AS") == parent.CanonicalPath), token).ConfigureAwait(false)) return null;
        if (!await SqlRetainedProcessorBranchStore.IsExactDocumentDescriptorRunnableAsync(db, descriptor, token).ConfigureAwait(false) ||
            await db.SourceProcessorBranches.AnyAsync(value => value.SourceRevisionId == parentId && value.Id != recoveryBranchId &&
                (value.State == (int)RetainedProcessorBranchState.Pending || value.State == (int)RetainedProcessorBranchState.Running), token).ConfigureAwait(false) ||
            await db.DocumentPublications.AnyAsync(value => value.OwnerSourceRevisionId == parentId && value.SourceProcessorBranchId != recoveryBranchId, token).ConfigureAwait(false)) return null;
        var artifact = await db.SourceArtifacts.SingleOrDefaultAsync(value => value.SourceRevisionId == parentId, token).ConfigureAwait(false);
        if (artifact is null || artifact.ContentSha256 != hash || artifact.ByteLength != parent.ByteLength ||
            !string.Equals(artifact.StoreRelativePath, Path.Combine("sha256", hash[..2], hash + ".bin"), StringComparison.OrdinalIgnoreCase)) return null;
        var job = await db.Jobs.SingleOrDefaultAsync(value => value.Id == jobId, token).ConfigureAwait(false);
        if (job is null || job.Stage != (int)PipelineStage.Extract || job.Operation != PipelineOperations.ExtractDocument ||
            job.PublicState != (int)PublicJobState.Failed ||
            !(job.Reason == RuntimeFailure || !pdf && job.Reason == DimensionFailure || pdf && job.Reason == EncryptedPdfFailure) ||
            job.LeaseOwner is not null || job.LeaseExpiresAtUtc is not null ||
            await db.JobAttempts.AnyAsync(value => value.JobId == jobId && value.CompletedAtUtc == null, token).ConfigureAwait(false) ||
            await db.OutboxMessages.AnyAsync(value => value.JobId == jobId && (value.DispatchedAtUtc == null || value.LeaseOwner != null || value.LeaseExpiresAtUtc != null), token).ConfigureAwait(false)) return null;
        var record = await db.PipelineRecords.SingleOrDefaultAsync(value => value.Id == job.PipelineRecordId, token).ConfigureAwait(false);
        if (record is null || record.IsDeleted || record.CompletionCriteriaMet || record.CurrentStage != (int)PipelineStage.Extract ||
            record.Revision != job.SourceRevision || record.ContentHash != hash || record.SourceRevisionId is null ||
            await db.Jobs.AnyAsync(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision && value.Id != jobId, token).ConfigureAwait(false) ||
            await db.Artifacts.AnyAsync(value => value.PipelineRecordId == record.Id && value.SourceRevision == record.Revision, token).ConfigureAwait(false)) return null;
        var child = await db.SourceRevisions.SingleOrDefaultAsync(value => value.Id == record.SourceRevisionId, token).ConfigureAwait(false);
        if (child is null || child.ParentSourceRevisionId != parentId || child.SourceRootId != root.Id || child.SuppressedAtUtc is not null || child.OriginKind != 2 ||
            child.Classification != contract.Classification || (pdf ? child.Extension != ".pdf" : child.Extension is not (".jpg" or ".png")) ||
            child.ContentSha256 != hash || child.ByteLength != parent.ByteLength ||
            await db.PipelineRecords.CountAsync(value => value.SourceRevisionId == child.Id, token).ConfigureAwait(false) != 1) return null;
        var members = await (from member in db.SourceProcessorBranchMembers
                             join branch in db.SourceProcessorBranches on member.BranchId equals branch.Id
                             join activity in db.SourceActivities on branch.SourceActivityId equals activity.Id
                             where member.ChildSourceRevisionId == child.Id && branch.SourceRevisionId == parentId &&
                                   branch.ProcessorVersion == descriptor.ProcessorVersion && branch.ProcessorFingerprint == descriptor.ProcessorFingerprint
                             select new { Member = member, Branch = branch, Activity = activity }).Take(2).ToArrayAsync(token).ConfigureAwait(false);
        if (members.Length != 1) return null;
        var owner = members[0];
        if (owner.Branch.InputSha256 != hash || owner.Branch.State != (int)RetainedProcessorBranchState.Completed || owner.Branch.CompletedMemberCount != 1 ||
            owner.Branch.CompletionReceiptFingerprint is null || owner.Branch.LeaseOwner is not null || owner.Branch.LeaseExpiresAtUtc is not null ||
            owner.Activity.SourceRevisionId != parentId || owner.Activity.InputFingerprint != hash || owner.Activity.ProcessorVersion != descriptor.ProcessorVersion ||
            owner.Activity.ActivityKind != (int)SourceActivityKind.TextExtraction || owner.Activity.ExecutionClass != (int)ExecutionClass.InProcess ||
            owner.Activity.DescriptorFingerprint != descriptor.ProcessorFingerprint || owner.Activity.ResultingPipelineRecordId is not null || owner.Activity.ResultingPipelineRecordRevision is not null ||
            owner.Member.Disposition != "completed" || owner.Member.ByteLength != parent.ByteLength || owner.Member.ChildSourceActivityId is null ||
            await db.SourceProcessorBranchMembers.CountAsync(value => value.BranchId == owner.Branch.Id, token).ConfigureAwait(false) != 1 ||
            await db.SourceProcessorAttempts.AnyAsync(value => value.BranchId == owner.Branch.Id && value.FinishedAtUtc == null, token).ConfigureAwait(false) ||
            !await db.SourceProcessorAttempts.AnyAsync(value => value.BranchId == owner.Branch.Id && value.LeaseGeneration == owner.Branch.LeaseGeneration &&
                value.FinishedAtUtc != null && value.OutcomeCode == "completed", token).ConfigureAwait(false) ||
            await db.SourceProcessorForceRequests.AnyAsync(value => value.SourceProcessorBranchId == owner.Branch.Id && value.State <= 1, token).ConfigureAwait(false)) return null;
        var childActivity = await db.SourceActivities.SingleOrDefaultAsync(value => value.Id == owner.Member.ChildSourceActivityId, token).ConfigureAwait(false);
        var childArtifact = await db.SourceArtifacts.SingleOrDefaultAsync(value => value.SourceRevisionId == child.Id, token).ConfigureAwait(false);
        if (childActivity is null || childActivity.SourceRevisionId != child.Id || childActivity.State != (int)SourceActivityState.FailedTerminal || childActivity.Reason != job.Reason ||
            childActivity.ActivityKind != (int)SourceActivityKind.DocumentParsing || childActivity.ExecutionClass != (int)ExecutionClass.InProcess ||
            childActivity.ProcessorVersion != contract.ProcessorVersion || childActivity.DescriptorFingerprint != contract.ProcessorFingerprint ||
            childActivity.ResultingPipelineRecordId != record.Id || childActivity.ResultingPipelineRecordRevision != record.Revision || childActivity.InputFingerprint != hash ||
            childArtifact is null || childArtifact.ContentSha256 != hash || childArtifact.ByteLength != parent.ByteLength || childArtifact.StoreRelativePath != artifact.StoreRelativePath) return null;
        var ordinaryClaim = new RetainedProcessorClaim(owner.Branch.Id, new(parentId), parent.StableSourceIdentity, hash, "binding-proof", 1, DateTimeOffset.MaxValue);
        var identity = pdf ? DocumentProcessingInput.CreatePdfIdentity(ordinaryClaim) : DocumentProcessingInput.CreateImageIdentity(ordinaryClaim, child.Extension);
        var receiptFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"completed:{identity.MemberFingerprint}:{hash}:{parent.ByteLength}")));
        if (owner.Member.MemberFingerprint != identity.MemberFingerprint || child.StableSourceIdentity != identity.StableSourceIdentity ||
            // Persisted predecessors may predate the parent-qualified locator. Its
            // independently derived member identity and all ownership proofs still apply.
            (child.CanonicalPath != identity.SyntheticLocator && child.CanonicalPath != $"retained-document-input:{identity.MemberFingerprint}") ||
            owner.Branch.CompletionReceiptFingerprint != receiptFingerprint) return null;
        if (await db.SourceActivityRelations.AnyAsync(relation => relation.PredecessorActivityId == childActivity.Id &&
            (relation.RelationshipKind != DocumentProcessingInput.DocumentRecoveryChildRelationship || relation.ReasonCode != DocumentProcessingInput.DocumentRecoveryVersion ||
             !db.SourceProcessorBranchMembers.Any(member => member.BranchId == recoveryBranchId && member.ChildSourceActivityId == relation.SuccessorActivityId && member.Disposition == "completed")), token).ConfigureAwait(false)) return null;
        var requests = await db.DocumentOcrRequests.Where(value => value.ParentJobId == jobId).Take(2).ToArrayAsync(token).ConfigureAwait(false);
        if (job.Reason == EncryptedPdfFailure)
        {
            // The old blanket flag refusal happened before any OCR handoff. Absence of
            // a request alone is insufficient: orphaned GPU history also refuses it.
            if (requests.Length != 0 || await db.GpuMiniTasks.AnyAsync(value => value.ParentJobId == jobId, token).ConfigureAwait(false)) return null;
            return Bind(null);
        }
        if (requests.Length != 1) return null;
        var request = requests[0];
        if (request.State != (int)DocumentOcrRequestState.Requeued || request.PipelineRecordId != record.Id || request.SourceRevision != record.Revision ||
            request.RetainedSourceRevisionId != child.Id || request.ContentSha256 != hash || request.ModelRuntimeKey != PaddleOcrVlmRuntimeContract.ModelRuntimeKey ||
            request.SettingsFingerprint != PaddleOcrVlmRuntimeContract.SettingsFingerprint || request.ResultDigest is not { Length: 32 } digest ||
            request.ResultJson is not { Length: > 0 and <= 32_768 } resultJson || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(resultJson)), digest) ||
            !IsSettledFailureResult(resultJson, job.Reason)) return null;
        var task = await db.GpuMiniTasks.SingleOrDefaultAsync(value => value.Id == request.MiniTaskId, token).ConfigureAwait(false);
        if (task is null || task.ParentJobId != jobId || task.SourceRevision != record.Revision || task.ExecutionState != (int)GpuMiniTaskExecutionState.Completed ||
            task.BatchId is null || task.AdmissionGeneration <= 0 || task.PriorityLane != (int)GpuPriorityLane.DocumentIndexing ||
            task.RequiredExecutorKey is not null && task.RequiredExecutorKey != PaddleOcrVlmRuntimeContract.ExecutorKey ||
            task.ModelRuntimeKey != request.ModelRuntimeKey || task.SettingsFingerprint != request.SettingsFingerprint ||
            await db.GpuMiniTasks.AnyAsync(value => value.ParentJobId == jobId && value.Id != task.Id, token).ConfigureAwait(false)) return null;
        var batch = await db.GpuBatches.SingleOrDefaultAsync(value => value.Id == task.BatchId, token).ConfigureAwait(false);
        var dispatches = await db.GpuExecutorDispatches.Where(value => value.BatchId == task.BatchId).Take(2).ToArrayAsync(token).ConfigureAwait(false);
        if (batch is null || batch.State != (int)GpuBatchState.Completed || batch.AdmissionGeneration != task.AdmissionGeneration || batch.ItemCount != 1 || dispatches.Length != 1 ||
            batch.ModelRuntimeKey != task.ModelRuntimeKey || batch.SettingsFingerprint != task.SettingsFingerprint ||
            await db.GpuMiniTasks.CountAsync(value => value.BatchId == batch.Id, token).ConfigureAwait(false) != 1 ||
            await db.GpuCapacitySlots.AnyAsync(value => value.ActiveBatchId == batch.Id, token).ConfigureAwait(false)) return null;
        var dispatch = dispatches[0];
        if (dispatch.State != (int)GpuExecutorDispatchState.Terminal || dispatch.AdmissionGeneration != task.AdmissionGeneration ||
            dispatch.ExecutorKey != PaddleOcrVlmRuntimeContract.ExecutorKey || dispatch.OwnerKey != batch.OwnerKey || dispatch.CapacitySlotKey != batch.CapacitySlotKey ||
            await db.NativeWorkerInstances.AnyAsync(value => value.ActiveDispatchId == dispatch.DispatchId && value.ExitedAtUtc == null, token).ConfigureAwait(false) ||
            await db.GpuExecutorEvidence.AnyAsync(value => value.DispatchId == dispatch.DispatchId && value.EvidenceClass == (int)GpuExecutorEvidenceClass.TaskOutcomeUncertainConfirmed, token).ConfigureAwait(false)) return null;
        var receipts = await db.GpuExecutorResultReceipts.Where(value => value.DispatchId == dispatch.DispatchId && value.BatchId == batch.Id &&
            value.MiniTaskId == task.Id && value.AdmissionGeneration == task.AdmissionGeneration && value.Disposition == 0 &&
            value.EvidenceClass == (int)GpuExecutorEvidenceClass.TaskOutcomeConfirmed && value.ExecutorKey == dispatch.ExecutorKey)
            .OrderBy(value => value.CreatedAtUtc).ThenBy(value => value.OperationId).ToArrayAsync(token).ConfigureAwait(false);
        if (receipts.Length == 0 || receipts.Any(value => value.OpaqueResultDigest is null || !CryptographicOperations.FixedTimeEquals(value.OpaqueResultDigest, digest))) return null;
        var receipt = receipts[0];
        if (!await db.GpuSchedulerOperationReceipts.AnyAsync(value => value.OperationId == receipt.OperationId && value.OperationKind == "executor-result-receipt" &&
            value.Accepted && value.Committed && value.BatchId == batch.Id && value.CapacitySlotKey == batch.CapacitySlotKey && value.OwnerKey == batch.OwnerKey &&
            value.AdmissionGeneration == task.AdmissionGeneration && value.RequestFingerprint == receipt.RequestFingerprint, token).ConfigureAwait(false)) return null;
        return Bind(new(request.MiniTaskId, batch.Id, task.AdmissionGeneration, dispatch.DispatchId, receipt.OperationId,
            Convert.ToHexStringLower(digest), request.ModelRuntimeKey, request.SettingsFingerprint));

        FailureBinding Bind(NativeBinding? native) => new(root.Id, configuration, parentId, hash, parent.ByteLength, artifact.Id,
            descriptor.ProcessorFingerprint, job.Reason!, owner.Activity.Id, owner.Branch.Id, owner.Member.Id,
            owner.Member.MemberFingerprint, child.Id, childActivity.Id, record.Id, record.Revision, jobId, native);
    }

    private static bool IsSettledFailureResult(string json, string? expectedReason)
    {
        try
        {
            using var result = JsonDocument.Parse(json);
            return result.RootElement.TryGetProperty("succeeded", out var succeeded) && succeeded.ValueKind == JsonValueKind.False &&
                result.RootElement.TryGetProperty("reasonCode", out var reason) && reason.GetString() == expectedReason &&
                result.RootElement.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array && pages.GetArrayLength() == 0;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    private static bool TryGetDescriptor(string fingerprint, out SourceCapabilityDescriptor descriptor)
    {
        descriptor = fingerprint == ImageDocumentInputProcessor.Capability.ProcessorFingerprint ? ImageDocumentInputProcessor.Capability : PdfDocumentProcessor.Capability;
        return fingerprint == descriptor.ProcessorFingerprint;
    }

    private static bool IsInScope(SourceRootConfigurationEntity root, SourceRevisionEntity revision)
    {
        try
        {
            if (!Path.IsPathFullyQualified(revision.CanonicalPath) ||
                !string.Equals(Path.GetFullPath(revision.CanonicalPath), revision.CanonicalPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(revision.CanonicalPath), revision.Extension, StringComparison.OrdinalIgnoreCase) ||
                !revision.CanonicalPath.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.CanonicalPath)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            var relative = Path.GetRelativePath(root.CanonicalPath, revision.CanonicalPath);
            return (root.Recursive || relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0) &&
                LocalSourceEnumerator.MatchesPolicy(relative, JsonSerializer.Deserialize<string[]>(root.IncludePatternsJson) ?? [], JsonSerializer.Deserialize<string[]>(root.ExcludePatternsJson) ?? []);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or JsonException) { return false; }
    }

    private sealed record RecoveryProof(string Version, Guid RecoveryActivityId, Guid RecoveryBranchId, FailureBinding Binding);
    private sealed record FailureBinding(Guid RootId, long ConfigurationRevision, Guid ParentRevisionId, string InputSha256, long ByteLength,
        Guid ParentArtifactId, string ProcessorFingerprint, string FailureReason, Guid PredecessorActivityId, Guid PredecessorBranchId, Guid PredecessorMemberId, string PredecessorMemberFingerprint,
        Guid FailedChildRevisionId, Guid FailedChildActivityId, Guid FailedPipelineRecordId, long FailedPipelineRevision, Guid FailedJobId, NativeBinding? Native);
    private sealed record NativeBinding(Guid MiniTaskId, Guid BatchId, long AdmissionGeneration, Guid DispatchId, Guid ResultReceiptOperationId, string ResultDigest,
        string ModelRuntimeKey, string SettingsFingerprint);
}
