using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlCorpusRebuildStore
{
    public async ValueTask<CorpusRebuildPlan> ReadReplacementPlanAsync(Guid operationId, Guid supersededOperationId,
        Guid expectedEpoch, string expectedManifestHash, EmbeddingProfile profile, string policy, CancellationToken ct,
        EmbeddingGpuRuntime? runtime = null)
    {
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(profile.ModelFingerprint) ||
            profile.ModelFingerprint.Length > 256 || profile.Dimensions is < 1 or > 4096 ||
            policy.Length != 64 || policy.Any(value => !char.IsAsciiHexDigitLower(value)) ||
            runtime is not null && runtime.Profile != profile)
            throw new ArgumentException("corpus-rebuild-profile-invalid");
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        await SqlPublishedPassageSelection.AcquireFenceAsync(context, ct).ConfigureAwait(false);
        var old = await ReadReplacementParentAsync(context, operationId, supersededOperationId, expectedEpoch, expectedManifestHash, ct).ConfigureAwait(false);
        var supersession = await CaptureSupersessionAsync(context, old, runtime?.RuntimeKey, runtime?.SettingsFingerprint, ct).ConfigureAwait(false);
        var plan = await CapturePlanAsync(context, operationId, Guid.NewGuid(), profile, policy, ct,
            superseded: old, supersession: supersession).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return plan;
    }

    private static async Task<CorpusRebuildPlan> ReadReplacementParentAsync(FluxKnowledgeDbContext context, Guid operationId,
        Guid oldOperationId, Guid expectedEpoch, string expectedManifestHash, CancellationToken ct)
    {
        if (operationId == oldOperationId || oldOperationId == Guid.Empty || expectedEpoch == Guid.Empty ||
            await context.CorpusRebuildOperations.AnyAsync(value => value.SupersedesOperationId == oldOperationId, ct).ConfigureAwait(false))
            throw new CorpusRebuildRefusalException("corpus-rebuild-replacement-identity-mismatch");
        var old = await ReadCommittedPlanAsync(context, oldOperationId, ct).ConfigureAwait(false);
        if (old.TargetEpoch != expectedEpoch || old.ManifestHash != expectedManifestHash)
            throw new CorpusRebuildRefusalException("corpus-rebuild-replacement-identity-mismatch");
        return old;
    }

    private static async Task<CorpusRebuildSupersession> CaptureSupersessionAsync(FluxKnowledgeDbContext context,
        CorpusRebuildPlan old, string? runtimeKey, string? settingsFingerprint, CancellationToken ct)
    {
        var items = await context.CorpusRebuildWorkItems.AsNoTracking().Where(value => value.OperationId == old.OperationId)
            .OrderBy(value => value.PipelineRecordId).ToArrayAsync(ct).ConfigureAwait(false);
        if (items.Length != old.Inputs.Count || items.Any(item => !old.Inputs.Any(input =>
            input.PipelineRecordId == item.PipelineRecordId && input.SourceRevision == item.SourceRevision &&
            input.CanonicalArtifactId == item.CanonicalArtifactId && input.EmbeddingJobId == item.EmbeddingJobId &&
            input.DispatchMessageId == item.DispatchMessageId)))
            throw new CorpusRebuildRefusalException("corpus-rebuild-receipt-invalid");
        var embedIds = old.Inputs.Select(value => value.EmbeddingJobId).ToArray();
        var embedJobs = await context.Jobs.AsNoTracking().Where(value => embedIds.Contains(value.Id)).ToArrayAsync(ct).ConfigureAwait(false);
        var parents = await context.OutboxMessages.AsNoTracking().Where(value => embedIds.Contains(value.JobId ?? Guid.Empty)).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var job in embedJobs)
        {
            var input = old.Inputs.Single(value => value.EmbeddingJobId == job.Id);
            var delivery = parents.SingleOrDefault(value => value.JobId == job.Id);
            if (job.Stage != (int)PipelineStage.Embed || job.Operation != PipelineOperations.Embed ||
                job.PipelineRecordId != input.PipelineRecordId || job.SourceRevision != input.SourceRevision ||
                delivery is null || delivery.Id != input.DispatchMessageId || delivery.Stage != job.Stage || delivery.Operation != job.Operation ||
                delivery.PipelineRecordId != job.PipelineRecordId || delivery.SourceRevision != job.SourceRevision)
                throw new CorpusRebuildRefusalException("corpus-rebuild-replacement-history-invalid");
        }
        if (items.Any(item => item.State == 1 && !embedJobs.Any(job => job.Id == item.EmbeddingJobId)))
            throw new CorpusRebuildRefusalException("corpus-rebuild-replacement-history-invalid");
        var publishIds = new List<Guid>();
        foreach (var parent in parents.Where(value => value.DispatchedAtUtc is not null && value.CompletedArtifactId is not null))
        {
            var artifact = await context.Artifacts.AsNoTracking().SingleOrDefaultAsync(value => value.Id == parent.CompletedArtifactId, ct).ConfigureAwait(false);
            if (artifact is null || artifact.PipelineRecordId != parent.PipelineRecordId || artifact.SourceRevision != parent.SourceRevision ||
                artifact.Stage != (int)PipelineStage.Embed || artifact.ContentType != EmbedDraftDefaults.ArtifactContentType ||
                !Guid.TryParseExact(artifact.SearchText, "D", out var generationId) ||
                !await context.IndexGenerations.AnyAsync(value => value.Id == generationId && value.EmbeddingJobId == parent.JobId &&
                    value.CorpusEpoch == old.TargetEpoch, ct).ConfigureAwait(false))
                throw new CorpusRebuildRefusalException("corpus-rebuild-replacement-publish-provenance-invalid");
            var next = await context.OutboxMessages.AsNoTracking().Where(value => value.PipelineRecordId == parent.PipelineRecordId &&
                value.SourceRevision == parent.SourceRevision && value.DispatchGeneration == parent.DispatchGeneration + 1 &&
                value.Stage == (int)PipelineStage.Publish && value.Operation == PipelineOperations.Publish).ToArrayAsync(ct).ConfigureAwait(false);
            if (next.Length != 1 || next[0].JobId is not { } nextId || !await context.Jobs.AnyAsync(value => value.Id == nextId &&
                    value.PipelineRecordId == parent.PipelineRecordId && value.SourceRevision == parent.SourceRevision &&
                    value.Stage == (int)PipelineStage.Publish && value.Operation == PipelineOperations.Publish, ct).ConfigureAwait(false))
                throw new CorpusRebuildRefusalException("corpus-rebuild-replacement-publish-provenance-invalid");
            publishIds.Add(nextId);
        }
        var jobIds = embedJobs.Select(value => value.Id).Concat(publishIds).Order().ToArray();
        var jobs = await context.Jobs.AsNoTracking().Where(value => jobIds.Contains(value.Id)).OrderBy(value => value.Id).ToArrayAsync(ct).ConfigureAwait(false);
        if (jobs.Any(value => value.PublicState is (int)PublicJobState.WorkerProcessing or (int)PublicJobState.GpuProcessing))
            throw new CorpusRebuildRefusalException("corpus-rebuild-replacement-worker-unsettled");
        var deliveries = await context.OutboxMessages.AsNoTracking().Where(value => jobIds.Contains(value.JobId ?? Guid.Empty)).OrderBy(value => value.Id).ToArrayAsync(ct).ConfigureAwait(false);
        var requests = await context.EmbeddingGpuRequests.AsNoTracking().Where(value => embedIds.Contains(value.ParentJobId)).OrderBy(value => value.MiniTaskId).ToArrayAsync(ct).ConfigureAwait(false);
        var tasks = await context.GpuMiniTasks.AsNoTracking().Where(value => jobIds.Contains(value.ParentJobId ?? Guid.Empty)).OrderBy(value => value.Id).ToArrayAsync(ct).ConfigureAwait(false);
        var taskIds = tasks.Select(value => value.Id).ToArray();
        var results = await context.GpuExecutorResultReceipts.AsNoTracking().Where(value => taskIds.Contains(value.MiniTaskId)).OrderBy(value => value.OperationId).ToArrayAsync(ct).ConfigureAwait(false);
        var unstarted = new List<Guid>();
        foreach (var request in requests)
        {
            var task = tasks.SingleOrDefault(value => value.Id == request.MiniTaskId);
            var input = old.Inputs.Single(value => value.EmbeddingJobId == request.ParentJobId);
            if (task is null || runtimeKey is null || settingsFingerprint is null || task.ModelRuntimeKey != runtimeKey ||
                task.SettingsFingerprint != settingsFingerprint || task.ParentJobId != request.ParentJobId ||
                task.SourceRevision != request.SourceRevision || task.PriorityLane != (int)GpuPriorityLane.DocumentIndexing ||
                request.PipelineRecordId != input.PipelineRecordId || request.SourceRevision != input.SourceRevision ||
                request.CorpusEpoch != old.TargetEpoch || request.ModelFingerprint != old.Profile.ModelFingerprint || request.Dimensions != old.Profile.Dimensions ||
                Hash(request.InputsJson) != request.InputDigest ||
                !await context.IndexGenerations.AnyAsync(value => value.Id == request.GenerationId && value.EmbeddingJobId == request.ParentJobId &&
                    value.CorpusEpoch == old.TargetEpoch && value.ModelFingerprint == old.Profile.ModelFingerprint && value.Dimensions == old.Profile.Dimensions, ct).ConfigureAwait(false))
                throw new CorpusRebuildRefusalException("corpus-rebuild-replacement-request-provenance-invalid");
            if (request.State == 2 && request.NativeCleanupConfirmed &&
                task.ExecutionState is (int)GpuMiniTaskExecutionState.Completed or (int)GpuMiniTaskExecutionState.OutcomeUncertain) continue;
            if (request.State != 0 || request.NativeCleanupConfirmed || request.ExecutorInstanceId is not null || request.ClaimOperationId is not null ||
                request.OwnerProcessId is not null || request.OwnerStartedAtUtc is not null || request.OwnerMachineFingerprint is not null || request.DispatchId is not null ||
                request.ResultDigest is not null || task.ExecutionState != (int)GpuMiniTaskExecutionState.Ready || task.BatchId is not null ||
                task.AdmissionGeneration != 0 || results.Any(value => value.MiniTaskId == task.Id))
                throw new CorpusRebuildRefusalException("corpus-rebuild-replacement-request-unsettled");
            unstarted.Add(request.MiniTaskId);
        }
        if (tasks.Any(task => !requests.Any(request => request.MiniTaskId == task.Id)))
            throw new CorpusRebuildRefusalException("corpus-rebuild-replacement-request-provenance-invalid");
        var batches = tasks.Where(value => value.BatchId != null).Select(value => value.BatchId!.Value).ToArray();
        var dispatches = await context.GpuExecutorDispatches.AsNoTracking().Where(value => batches.Contains(value.BatchId)).OrderBy(value => value.DispatchId).ToArrayAsync(ct).ConfigureAwait(false);
        var historyHash = Hash(JsonSerializer.Serialize(new { items, jobs, deliveries, requests, tasks, results, dispatches }));
        return new(old.OperationId, old.TargetEpoch, old.ManifestHash, historyHash, jobIds, unstarted.Order().ToArray(), runtimeKey, settingsFingerprint);
    }

    private static async Task CommitSupersessionAsync(FluxKnowledgeDbContext context, Guid replacementOperationId,
        CorpusRebuildSupersession supersession, DateTimeOffset now, CancellationToken ct)
    {
        context.CorpusRebuildSupersededJobs.AddRange(supersession.JobIds.Select(jobId => new CorpusRebuildSupersededJobEntity
        {
            JobId = jobId, OperationId = supersession.OperationId, ReplacementOperationId = replacementOperationId, SupersededAtUtc = now
        }));
        var ids = supersession.UnstartedRequestIds.ToArray();
        foreach (var request in await context.EmbeddingGpuRequests.Where(value => ids.Contains(value.MiniTaskId)).ToArrayAsync(ct).ConfigureAwait(false))
        {
            request.State = 2;
            // The admission fence and immutable snapshot prove this request never acquired native ownership.
            request.NativeCleanupConfirmed = true;
            request.CleanupConfirmedAtUtc = now;
            request.UpdatedAtUtc = now;
        }
        foreach (var task in await context.GpuMiniTasks.Where(value => ids.Contains(value.Id)).ToArrayAsync(ct).ConfigureAwait(false))
            task.ExecutionState = (int)GpuMiniTaskExecutionState.Cancelled;
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
