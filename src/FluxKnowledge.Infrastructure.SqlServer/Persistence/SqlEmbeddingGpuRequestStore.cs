using System.Data;
using FluxKnowledge.Application.Indexing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Background batches use the existing parent job and GPU lifecycle, never a worker lease fabricated from GPU ownership.</summary>
public sealed class SqlEmbeddingGpuRequestStore(IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    IGpuSchedulerStore scheduler, IGpuSchedulerWakeSignal wake, EmbeddingGpuRuntime runtime, TimeProvider clock) : IEmbeddingGpuRequestStore
{
    private sealed record Input(long Id, string Hash);
    public EmbeddingProfile Profile => runtime.Profile;

    internal static IQueryable<GpuMiniTaskEntity> OwnedLocalTasks(FluxKnowledgeDbContext context, EmbeddingGpuRuntime runtime)
        => context.GpuMiniTasks.Where(task => task.ModelRuntimeKey == runtime.RuntimeKey && task.SettingsFingerprint == runtime.SettingsFingerprint &&
            task.PriorityLane == (int)GpuPriorityLane.DocumentIndexing && context.EmbeddingGpuRequests.Any(request =>
                request.MiniTaskId == task.Id && request.ParentJobId == task.ParentJobId && request.SourceRevision == task.SourceRevision &&
                request.ModelFingerprint == runtime.Profile.ModelFingerprint && request.Dimensions == runtime.Profile.Dimensions &&
                context.Jobs.Any(job => job.Id == request.ParentJobId && job.PipelineRecordId == request.PipelineRecordId &&
                    job.SourceRevision == request.SourceRevision && job.Stage == (int)PipelineStage.Embed && job.Operation == PipelineOperations.Embed) &&
                context.IndexGenerations.Any(generation => generation.Id == request.GenerationId && generation.EmbeddingJobId == request.ParentJobId &&
                    generation.CorpusEpoch == request.CorpusEpoch && EF.Functions.Collate(generation.ModelFingerprint, SchemaConfiguration.SchedulerFenceCollation) == request.ModelFingerprint &&
                    generation.Dimensions == request.Dimensions)));

    internal static Task<bool> HasContradictoryRequestsAsync(FluxKnowledgeDbContext context, IReadOnlyCollection<Guid> recordIds,
        IReadOnlyCollection<Guid> ownedTaskIds, CancellationToken ct)
        => context.EmbeddingGpuRequests.AnyAsync(request => recordIds.Contains(request.PipelineRecordId) &&
            context.GpuMiniTasks.Any(task => task.Id == request.MiniTaskId) && !ownedTaskIds.Contains(request.MiniTaskId), ct);

    public async ValueTask QueueAsync(StageWorkItem work, EmbeddingWorkBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Profile != runtime.Profile || batch.Chunks.Count is < 1 or > 4 || batch.CompletedChecksum is not null ||
            batch.Chunks.Select(chunk => chunk.Id).Distinct().Count() != batch.Chunks.Count)
            throw new InvalidOperationException("embedding-gpu-batch-invalid");
        var inputs = JsonSerializer.Serialize(batch.Chunks.Select(chunk => new Input(chunk.Id, chunk.SearchInputHash)).ToArray());
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inputs)));
        var id = await TransactionAsync(async context =>
        {
            var previous = await context.EmbeddingGpuRequests.SingleOrDefaultAsync(request => request.ParentJobId == work.Job.JobId.Value &&
                request.GenerationId == batch.GenerationId && request.InputDigest == digest && request.State < 2, cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                if (previous.PipelineRecordId != work.Job.PipelineRecordId.Value || previous.SourceRevision != work.Job.SourceRevision ||
                    previous.CorpusEpoch != batch.CorpusEpoch || previous.InputsJson != inputs ||
                    previous.ModelFingerprint != batch.Profile.ModelFingerprint || previous.Dimensions != batch.Profile.Dimensions)
                    throw new InvalidOperationException("embedding-gpu-request-conflict");
                return previous.MiniTaskId;
            }
            await SqlEmbeddingCheckpointStore.ValidateClaimAsync(context, work, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (await context.EmbeddingGpuRequests.CountAsync(request => request.ParentJobId == work.Job.JobId.Value &&
                request.GenerationId == batch.GenerationId && request.InputDigest == digest && request.State == 2 && request.ResultDigest == null,
                cancellationToken).ConfigureAwait(false) >= 3)
                throw new InvalidOperationException("embedding-gpu-batch-retry-limit");
            var epoch = (await SqlPublishedPassageSelection.ReadStampAsync(context, cancellationToken).ConfigureAwait(false)).CorpusEpoch;
            if (epoch != batch.CorpusEpoch) throw new InvalidOperationException("embedding-checkpoint-epoch-changed");
            var draft = await context.IndexGenerations.SingleAsync(value => value.Id == batch.GenerationId, cancellationToken).ConfigureAwait(false);
            SqlEmbeddingCheckpointStore.ValidateDraft(draft, work, batch.Profile, epoch);
            var current = await ReadBatchAsync(context, work.Job.PipelineRecordId.Value, work.Job.SourceRevision,
                batch.GenerationId, epoch, inputs, cancellationToken).ConfigureAwait(false);
            if (!current.Chunks.SequenceEqual(batch.Chunks)) throw new InvalidOperationException("embedding-checkpoint-input-changed");
            var request = new EmbeddingGpuRequestEntity
            {
                MiniTaskId = Guid.NewGuid(), ParentJobId = work.Job.JobId.Value, PipelineRecordId = work.Job.PipelineRecordId.Value,
                SourceRevision = work.Job.SourceRevision, GenerationId = batch.GenerationId, CorpusEpoch = epoch,
                ModelFingerprint = runtime.Profile.ModelFingerprint, Dimensions = runtime.Profile.Dimensions,
                InputsJson = inputs, InputDigest = digest, CreatedAtUtc = clock.GetUtcNow(), UpdatedAtUtc = clock.GetUtcNow()
            };
            context.EmbeddingGpuRequests.Add(request);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return request.MiniTaskId;
        }, cancellationToken).ConfigureAwait(false);
        var result = await scheduler.GpuTaskHandoffAsync(new(work.Job, GpuPriorityLane.DocumentIndexing,
            runtime.RuntimeKey, runtime.SettingsFingerprint, runtime.EstimatedBytes, $"embedding-gpu:{id:N}", id), cancellationToken).ConfigureAwait(false);
        if (!result.Committed || result.MiniTaskId != id) throw new InvalidOperationException("embedding-gpu-handoff-incomplete");
        wake.Notify(GpuSchedulerWakeReason.WorkReady);
    }

    public ValueTask<EmbeddingGpuExecutionWork?> ClaimExecutionAsync(GpuExecutorBatchHandle handle,
        Guid executorInstance, Guid claimOperation, GpuInteractiveOwnerIdentity owner, CancellationToken cancellationToken)
    {
        owner.Validate();
        if (executorInstance == Guid.Empty || claimOperation == Guid.Empty) throw new ArgumentException("embedding-gpu-owner-invalid", nameof(executorInstance));
        return TransactionAsync<EmbeddingGpuExecutionWork?>(async context =>
        {
            var request = await ReadOwnedRequestAsync(context, handle, cancellationToken).ConfigureAwait(false);
            if (request is null || request.State != 0 || request.NativeCleanupConfirmed) return null;
            if (request.ExecutorInstanceId is not null && (request.ExecutorInstanceId != executorInstance || request.ClaimOperationId != claimOperation ||
                request.DispatchId != handle.DispatchId || request.OwnerProcessId != owner.ProcessId || request.OwnerStartedAtUtc != owner.StartedAtUtc ||
                request.OwnerMachineFingerprint != owner.MachineFingerprint)) return null;
            request.ExecutorInstanceId = executorInstance;
            request.ClaimOperationId = claimOperation;
            request.OwnerProcessId = owner.ProcessId;
            request.OwnerStartedAtUtc = owner.StartedAtUtc;
            request.OwnerMachineFingerprint = owner.MachineFingerprint;
            request.DispatchId = handle.DispatchId;
            request.UpdatedAtUtc = clock.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            // Bind the process before preflight can refuse work. The adapter can then
            // durably prove that no native allocation started, including after withdrawal.
            if (!await SourceAvailableAsync(context, request, cancellationToken).ConfigureAwait(false)) return null;
            var epoch = (await SqlPublishedPassageSelection.ReadStampAsync(context, cancellationToken).ConfigureAwait(false)).CorpusEpoch;
            if (epoch != request.CorpusEpoch) return null;
            EmbeddingWorkBatch batch;
            try
            {
                var draft = await context.IndexGenerations.SingleAsync(value => value.Id == request.GenerationId, cancellationToken).ConfigureAwait(false);
                SqlEmbeddingCheckpointStore.ValidateDraft(draft, request.ParentJobId, runtime.Profile, epoch);
                batch = await ReadBatchAsync(context, request.PipelineRecordId, request.SourceRevision,
                    request.GenerationId, epoch, request.InputsJson, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { return null; }
            return new(request.MiniTaskId, claimOperation, request.ParentJobId, request.PipelineRecordId, request.SourceRevision, batch);
        }, cancellationToken);
    }

    public async ValueTask RecordNativeCleanupAsync(GpuExecutorBatchHandle handle, Guid executorInstance, Guid claimOperation, CancellationToken cancellationToken)
    {
        await TransactionAsync(async context =>
        {
            var request = await ReadBoundRequestAsync(context, handle, cancellationToken).ConfigureAwait(false);
            if (request is null || request.ExecutorInstanceId != executorInstance || request.ClaimOperationId != claimOperation ||
                request.DispatchId != handle.DispatchId)
                throw new InvalidOperationException("embedding-gpu-cleanup-owner-mismatch");
            request.NativeCleanupConfirmed = true;
            request.CleanupConfirmedAtUtc ??= clock.GetUtcNow();
            request.UpdatedAtUtc = clock.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<EmbeddingGpuCompletion?> ReadPendingCompletionAsync(GpuExecutorBatchHandle handle, CancellationToken cancellationToken)
        => TransactionAsync<EmbeddingGpuCompletion?>(async context =>
        {
            var request = await ReadBoundRequestAsync(context, handle, cancellationToken).ConfigureAwait(false);
            return request is { NativeCleanupConfirmed: true, CleanupConfirmedAtUtc: not null }
                ? new(request.MiniTaskId, request.ResultDigest?.ToArray(), request.CleanupConfirmedAtUtc.Value) : null;
        }, cancellationToken);

    public ValueTask<IReadOnlyList<EmbeddingGpuRecoveryWork>> ReadRecoveryAsync(CancellationToken cancellationToken)
        => TransactionAsync<IReadOnlyList<EmbeddingGpuRecoveryWork>>(async context =>
        {
            var candidates = await (from request in context.EmbeddingGpuRequests.AsNoTracking()
                join task in context.GpuMiniTasks.AsNoTracking() on request.MiniTaskId equals task.Id
                join batch in context.GpuBatches.AsNoTracking() on task.BatchId equals batch.Id
                join dispatch in context.GpuExecutorDispatches.AsNoTracking() on batch.Id equals dispatch.BatchId
                where request.State < 2 && batch.ItemCount == 1 && request.ParentJobId == task.ParentJobId &&
                    request.SourceRevision == task.SourceRevision && request.ModelFingerprint == runtime.Profile.ModelFingerprint &&
                    request.Dimensions == runtime.Profile.Dimensions &&
                    task.AdmissionGeneration == batch.AdmissionGeneration && dispatch.AdmissionGeneration == batch.AdmissionGeneration &&
                    task.ModelRuntimeKey == runtime.RuntimeKey && task.SettingsFingerprint == runtime.SettingsFingerprint &&
                    batch.ModelRuntimeKey == runtime.RuntimeKey && batch.SettingsFingerprint == runtime.SettingsFingerprint &&
                    batch.CapacitySlotKey == dispatch.CapacitySlotKey && batch.OwnerKey == dispatch.OwnerKey &&
                    dispatch.ExecutorKey == EmbeddingGpuExecutor.Name &&
                    (request.DispatchId == null || request.DispatchId == dispatch.DispatchId) &&
                    (dispatch.State == (int)GpuExecutorDispatchState.Acknowledged ||
                        dispatch.State == (int)GpuExecutorDispatchState.DeliveryUncertain ||
                        request.NativeCleanupConfirmed && dispatch.State == (int)GpuExecutorDispatchState.Terminal)
                orderby request.UpdatedAtUtc, request.MiniTaskId
                select new { Request = request, Dispatch = dispatch }).Take(128).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return candidates.Select(candidate => new EmbeddingGpuRecoveryWork(candidate.Request.MiniTaskId,
                new(candidate.Dispatch.BatchId, candidate.Dispatch.CapacitySlotKey, candidate.Dispatch.ExecutorKey,
                    candidate.Dispatch.AdmissionGeneration, candidate.Dispatch.DispatchId),
                candidate.Request.ExecutorInstanceId, candidate.Request.ClaimOperationId,
                candidate.Request.OwnerProcessId is null ? null : new(candidate.Request.OwnerProcessId.Value,
                    candidate.Request.OwnerStartedAtUtc!.Value, candidate.Request.OwnerMachineFingerprint!),
                candidate.Request.NativeCleanupConfirmed)).ToArray();
        }, cancellationToken);

    public ValueTask<bool> ConfirmUnstartedCleanupAsync(EmbeddingGpuRecoveryWork work, Guid executorInstance,
        Guid claimOperation, GpuInteractiveOwnerIdentity owner, CancellationToken cancellationToken)
    {
        owner.Validate();
        work.Handle.Validate();
        if (work.Owner is not null || work.ExecutorInstanceId is not null || work.ClaimOperationId is not null ||
            executorInstance == Guid.Empty || claimOperation == Guid.Empty) throw new ArgumentException("embedding-gpu-unstarted-recovery-invalid");
        return TransactionAsync(async context =>
        {
            var handle = work.Handle;
            var request = await (from value in context.EmbeddingGpuRequests
                join task in context.GpuMiniTasks on value.MiniTaskId equals task.Id
                join batch in context.GpuBatches on task.BatchId equals batch.Id
                join dispatch in context.GpuExecutorDispatches on batch.Id equals dispatch.BatchId
                where value.MiniTaskId == work.MiniTaskId && value.State == 0 && !value.NativeCleanupConfirmed && value.ExecutorInstanceId == null &&
                    value.DispatchId == null && value.ResultDigest == null && value.ParentJobId == task.ParentJobId && value.SourceRevision == task.SourceRevision &&
                    value.ModelFingerprint == runtime.Profile.ModelFingerprint && value.Dimensions == runtime.Profile.Dimensions &&
                    task.ExecutionState == (int)GpuMiniTaskExecutionState.Active && task.AdmissionGeneration == handle.AdmissionGeneration &&
                    task.ModelRuntimeKey == runtime.RuntimeKey && task.SettingsFingerprint == runtime.SettingsFingerprint &&
                    batch.Id == handle.BatchId && batch.ItemCount == 1 && batch.AdmissionGeneration == handle.AdmissionGeneration &&
                    batch.ModelRuntimeKey == runtime.RuntimeKey && batch.SettingsFingerprint == runtime.SettingsFingerprint &&
                    batch.CapacitySlotKey == handle.CapacitySlotKey && batch.OwnerKey == dispatch.OwnerKey &&
                    dispatch.DispatchId == handle.DispatchId && dispatch.CapacitySlotKey == handle.CapacitySlotKey &&
                    dispatch.ExecutorKey == handle.ExecutorKey && handle.ExecutorKey == EmbeddingGpuExecutor.Name &&
                    dispatch.AdmissionGeneration == handle.AdmissionGeneration &&
                    (dispatch.State == (int)GpuExecutorDispatchState.Acknowledged || dispatch.State == (int)GpuExecutorDispatchState.DeliveryUncertain)
                select value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (request is null) return false;
            request.ExecutorInstanceId = executorInstance;
            request.ClaimOperationId = claimOperation;
            request.OwnerProcessId = owner.ProcessId;
            request.OwnerStartedAtUtc = owner.StartedAtUtc;
            request.OwnerMachineFingerprint = owner.MachineFingerprint;
            request.DispatchId = handle.DispatchId;
            request.NativeCleanupConfirmed = true;
            request.CleanupConfirmedAtUtc = clock.GetUtcNow();
            request.UpdatedAtUtc = clock.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public ValueTask<bool> ConfirmExitedCleanupAsync(EmbeddingGpuRecoveryWork work, CancellationToken cancellationToken)
        => TransactionAsync(async context =>
        {
            var request = await ReadBoundRequestAsync(context, work.Handle, cancellationToken).ConfigureAwait(false);
            if (request is null || request.MiniTaskId != work.MiniTaskId || request.ExecutorInstanceId != work.ExecutorInstanceId ||
                request.ClaimOperationId != work.ClaimOperationId || work.Owner is null || request.OwnerProcessId != work.Owner.ProcessId ||
                request.OwnerStartedAtUtc != work.Owner.StartedAtUtc || request.OwnerMachineFingerprint != work.Owner.MachineFingerprint) return false;
            request.NativeCleanupConfirmed = true;
            request.CleanupConfirmedAtUtc ??= clock.GetUtcNow();
            request.UpdatedAtUtc = clock.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public ValueTask<bool> RequeueSettledAsync(GpuExecutorBatchHandle handle, Guid miniTaskId, CancellationToken cancellationToken)
        => TransactionAsync(async context =>
        {
            var request = await ReadBoundRequestAsync(context, handle, cancellationToken).ConfigureAwait(false);
            if (request is null || request.MiniTaskId != miniTaskId || !request.NativeCleanupConfirmed) return false;
            if (request.State == 2) return true;
            var terminal = await (from task in context.GpuMiniTasks
                join batch in context.GpuBatches on task.BatchId equals batch.Id
                join dispatch in context.GpuExecutorDispatches on batch.Id equals dispatch.BatchId
                where task.Id == miniTaskId && task.BatchId == handle.BatchId && task.AdmissionGeneration == handle.AdmissionGeneration &&
                    dispatch.DispatchId == handle.DispatchId &&
                    (dispatch.State == (int)GpuExecutorDispatchState.Terminal ||
                        dispatch.State == (int)GpuExecutorDispatchState.DeliveryUncertain &&
                        (batch.State == (int)GpuBatchState.Released ||
                            batch.State == (int)GpuBatchState.CapacityUncertain && context.GpuSchedulerOperationReceipts.Any(receipt =>
                                receipt.OperationKind == "capacity-reconciliation" && receipt.Accepted && receipt.Committed &&
                                receipt.BatchId == batch.Id && receipt.AdmissionGeneration == batch.AdmissionGeneration &&
                                receipt.CapacitySlotKey == batch.CapacitySlotKey && receipt.OwnerKey == batch.OwnerKey))) &&
                    (task.ExecutionState == (int)GpuMiniTaskExecutionState.Completed || task.ExecutionState == (int)GpuMiniTaskExecutionState.OutcomeUncertain)
                select task.Id).AnyAsync(cancellationToken).ConfigureAwait(false);
            if (!terminal) return false;
            if (!await SourceAvailableAsync(context, request, cancellationToken).ConfigureAwait(false))
            {
                // Finish the cleanup record without resurrecting a withdrawn source.
                request.State = 2;
                request.UpdatedAtUtc = clock.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
            var parent = await context.Jobs.SingleAsync(value => value.Id == request.ParentJobId, cancellationToken).ConfigureAwait(false);
            if (parent.PublicState != (int)PublicJobState.GpuProcessing || parent.Stage != (int)PipelineStage.Embed || parent.Operation != PipelineOperations.Embed ||
                parent.PipelineRecordId != request.PipelineRecordId || parent.SourceRevision != request.SourceRevision) return false;
            var outbox = await context.OutboxMessages.SingleOrDefaultAsync(value => value.JobId == parent.Id && value.PipelineRecordId == request.PipelineRecordId &&
                value.SourceRevision == request.SourceRevision && value.Stage == (int)PipelineStage.Embed &&
                value.Operation == PipelineOperations.Embed && value.DispatchedAtUtc == null, cancellationToken).ConfigureAwait(false);
            if (outbox is null) return false;
            parent.PublicState = (int)PublicJobState.WorkerQueued;
            parent.LeaseOwner = null;
            parent.LeaseExpiresAtUtc = null;
            parent.Reason = request.ResultDigest is null ? "embedding-gpu-retry" : null;
            parent.ErrorDetails = null;
            outbox.LeaseOwner = null;
            outbox.LeaseExpiresAtUtc = null;
            outbox.DueAtUtc = request.ResultDigest is null ? clock.GetUtcNow().AddSeconds(5) : clock.GetUtcNow();
            parent.DueAtUtc = outbox.DueAtUtc;
            request.State = 2;
            request.UpdatedAtUtc = clock.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private async Task<EmbeddingGpuRequestEntity?> ReadBoundRequestAsync(FluxKnowledgeDbContext context, GpuExecutorBatchHandle handle, CancellationToken ct)
    {
        handle.Validate();
        return await (from request in context.EmbeddingGpuRequests
            join task in context.GpuMiniTasks on request.MiniTaskId equals task.Id
            join batch in context.GpuBatches on task.BatchId equals batch.Id
            join dispatch in context.GpuExecutorDispatches on batch.Id equals dispatch.BatchId
            where batch.Id == handle.BatchId && batch.ItemCount == 1 && batch.AdmissionGeneration == handle.AdmissionGeneration &&
                batch.CapacitySlotKey == handle.CapacitySlotKey && batch.OwnerKey == dispatch.OwnerKey &&
                task.AdmissionGeneration == handle.AdmissionGeneration && task.ParentJobId == request.ParentJobId &&
                task.SourceRevision == request.SourceRevision && task.ModelRuntimeKey == runtime.RuntimeKey && task.SettingsFingerprint == runtime.SettingsFingerprint &&
                batch.ModelRuntimeKey == runtime.RuntimeKey && batch.SettingsFingerprint == runtime.SettingsFingerprint &&
                dispatch.DispatchId == handle.DispatchId && dispatch.CapacitySlotKey == handle.CapacitySlotKey &&
                dispatch.ExecutorKey == handle.ExecutorKey && dispatch.AdmissionGeneration == handle.AdmissionGeneration &&
                request.DispatchId == handle.DispatchId && request.ExecutorInstanceId != null && request.ClaimOperationId != null
            select request).SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask CommitAsync(GpuExecutorBatchHandle handle, Guid executorInstance,
        EmbeddingGpuExecutionWork work, IReadOnlyList<EmbeddingResult> results, CancellationToken cancellationToken)
    {
        if (work.Batch.Profile != runtime.Profile || work.Batch.Chunks.Count is < 1 or > 4 ||
            results.Count != work.Batch.Chunks.Count || work.Batch.Chunks.Select(chunk => chunk.Id).Distinct().Count() != work.Batch.Chunks.Count)
            throw new InvalidOperationException("embedding-gpu-batch-invalid");
        var payloads = results.Select(result => SqlEmbeddingCheckpointStore.ValidateResult(result, runtime.Profile)).ToArray();
        var digest = SHA256.HashData(payloads.SelectMany(value => value).ToArray());
        await TransactionAsync(async context =>
        {
            var request = await ReadOwnedRequestAsync(context, handle, cancellationToken).ConfigureAwait(false);
            if (request is null || request.MiniTaskId != work.MiniTaskId || request.ParentJobId != work.ParentJobId ||
                request.PipelineRecordId != work.PipelineRecordId || request.SourceRevision != work.SourceRevision ||
                request.GenerationId != work.Batch.GenerationId || request.CorpusEpoch != work.Batch.CorpusEpoch ||
                request.ExecutorInstanceId != executorInstance || request.ClaimOperationId != work.ClaimOperationId || request.DispatchId != handle.DispatchId ||
                request.State > 1 || request.NativeCleanupConfirmed ||
                !await SourceAvailableAsync(context, request, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("embedding-gpu-ownership-lost");
            var epoch = (await SqlPublishedPassageSelection.ReadStampAsync(context, cancellationToken).ConfigureAwait(false)).CorpusEpoch;
            if (epoch != request.CorpusEpoch) throw new InvalidOperationException("embedding-checkpoint-epoch-changed");
            var draft = await context.IndexGenerations.SingleAsync(value => value.Id == request.GenerationId, cancellationToken).ConfigureAwait(false);
            SqlEmbeddingCheckpointStore.ValidateDraft(draft, request.ParentJobId, runtime.Profile, epoch);
            var storedBatch = await ReadBatchAsync(context, request.PipelineRecordId, request.SourceRevision,
                request.GenerationId, epoch, request.InputsJson, cancellationToken).ConfigureAwait(false);
            if (!storedBatch.Chunks.SequenceEqual(work.Batch.Chunks)) throw new InvalidOperationException("embedding-checkpoint-input-changed");
            if (request.ResultDigest is not null && !request.ResultDigest.AsSpan().SequenceEqual(digest))
                throw new InvalidOperationException("embedding-checkpoint-replay-conflict");
            await SqlEmbeddingCheckpointStore.CommitVectorsAsync(context, request.PipelineRecordId, request.SourceRevision,
                draft, work.Batch, payloads, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            request.ResultDigest = digest;
            request.State = 1;
            request.UpdatedAtUtc = clock.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EmbeddingGpuRequestEntity?> ReadOwnedRequestAsync(FluxKnowledgeDbContext context,
        GpuExecutorBatchHandle handle, CancellationToken ct)
    {
        handle.Validate();
        // A single bounded request is one scheduler mini-task, even when it contains four passages.
        var requests = await (from request in context.EmbeddingGpuRequests
            join task in context.GpuMiniTasks on request.MiniTaskId equals task.Id
            join dispatch in context.GpuExecutorDispatches on task.BatchId equals dispatch.BatchId
            join batch in context.GpuBatches on dispatch.BatchId equals batch.Id
            join slot in context.GpuCapacitySlots on dispatch.CapacitySlotKey equals slot.SlotKey
            join parent in context.Jobs on request.ParentJobId equals parent.Id
            where task.BatchId == handle.BatchId && task.AdmissionGeneration == handle.AdmissionGeneration &&
                task.ParentJobId == request.ParentJobId && task.SourceRevision == request.SourceRevision &&
                task.ExecutionState == (int)GpuMiniTaskExecutionState.Active && task.PriorityLane == (int)GpuPriorityLane.DocumentIndexing &&
                task.ModelRuntimeKey == runtime.RuntimeKey && task.SettingsFingerprint == runtime.SettingsFingerprint &&
                task.EstimatedBytes == runtime.EstimatedBytes && dispatch.DispatchId == handle.DispatchId &&
                dispatch.CapacitySlotKey == handle.CapacitySlotKey && dispatch.ExecutorKey == handle.ExecutorKey &&
                dispatch.AdmissionGeneration == handle.AdmissionGeneration && dispatch.State == (int)GpuExecutorDispatchState.Acknowledged &&
                slot.ActiveBatchId == handle.BatchId && slot.OwnerKey == batch.OwnerKey &&
                dispatch.OwnerKey == batch.OwnerKey && batch.CapacitySlotKey == handle.CapacitySlotKey &&
                batch.State == (int)GpuBatchState.Active && batch.AdmissionGeneration == handle.AdmissionGeneration && batch.ItemCount == 1 &&
                batch.ModelRuntimeKey == runtime.RuntimeKey && batch.SettingsFingerprint == runtime.SettingsFingerprint &&
                slot.State == (int)GpuCapacitySlotState.Reserved &&
                parent.PipelineRecordId == request.PipelineRecordId && parent.SourceRevision == request.SourceRevision &&
                parent.Stage == (int)PipelineStage.Embed && parent.Operation == PipelineOperations.Embed &&
                parent.PublicState == (int)PublicJobState.GpuProcessing && request.ModelFingerprint == runtime.Profile.ModelFingerprint &&
                request.Dimensions == runtime.Profile.Dimensions
            select request).ToArrayAsync(ct).ConfigureAwait(false);
        if (requests.Length != 1 || await context.GpuMiniTasks.CountAsync(task => task.BatchId == handle.BatchId &&
            task.AdmissionGeneration == handle.AdmissionGeneration, ct).ConfigureAwait(false) != 1) return null;
        return requests[0];
    }

    private static async Task<bool> SourceAvailableAsync(FluxKnowledgeDbContext context, EmbeddingGpuRequestEntity request, CancellationToken ct)
    {
        bool maintenance;
        try { maintenance = await SqlCorpusRebuildStore.ValidateMaintenanceJobAsync(context, request.ParentJobId, ct).ConfigureAwait(false); }
        catch (CorpusRebuildRefusalException) { return false; }
        return await context.PipelineRecords.AnyAsync(record => record.Id == request.PipelineRecordId && record.Revision == request.SourceRevision &&
            !record.IsDeleted && record.CurrentStage == (int)PipelineStage.Embed &&
            (record.SourceRevisionId == null || record.SourceRevision!.SuppressedAtUtc == null &&
                (record.SourceRevision.SourceRoot.State == (int)SourceRootState.Enabled || maintenance)), ct).ConfigureAwait(false);
    }

    private async Task<EmbeddingWorkBatch> ReadBatchAsync(FluxKnowledgeDbContext context, Guid recordId, long revision,
        Guid generation, Guid epoch, string inputsJson, CancellationToken ct)
    {
        var inputs = JsonSerializer.Deserialize<Input[]>(inputsJson) ?? throw new InvalidOperationException("embedding-gpu-inputs-invalid");
        if (inputs.Length is < 1 or > 4 || inputs.Select(input => input.Id).Distinct().Count() != inputs.Length)
            throw new InvalidOperationException("embedding-gpu-inputs-invalid");
        var ids = inputs.Select(input => input.Id).ToArray();
        var rows = await SqlEmbeddingCheckpointStore.Chunks(context, recordId, revision).Where(chunk => ids.Contains(chunk.Id)).ToArrayAsync(ct).ConfigureAwait(false);
        var chunks = new List<CanonicalTextChunk>(inputs.Length);
        foreach (var input in inputs)
        {
            var row = rows.SingleOrDefault(chunk => chunk.Id == input.Id) ?? throw new InvalidOperationException("embedding-checkpoint-input-changed");
            var chunk = new CanonicalTextChunk(row.Id, row.Ordinal, row.StartOffset, row.Length, row.Content,
                row.ContentHash, row.PassagePolicyFingerprint, row.ContextHeader);
            if (chunk.SearchInputHash != input.Hash || row.SearchInputHash != input.Hash)
                throw new InvalidOperationException("embedding-checkpoint-input-changed");
            chunks.Add(chunk);
        }
        return new(generation, epoch, runtime.Profile, chunks);
    }

    private async ValueTask<T> TransactionAsync<T>(Func<FluxKnowledgeDbContext, Task<T>> action, CancellationToken ct)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, ct).ConfigureAwait(false);
            var value = await action(context).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return value;
        }).ConfigureAwait(false);
    }
}
