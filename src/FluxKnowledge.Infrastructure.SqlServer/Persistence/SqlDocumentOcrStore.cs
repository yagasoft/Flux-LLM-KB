using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>
/// Keeps the local OCR hand-off bound to one claimed document Extract job.  It stores neither a
/// corpus item nor a provider choice: the normal Extract worker consumes a completed result and
/// performs the ordinary stage transition.
/// </summary>
public sealed class SqlDocumentOcrStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    GpuSchedulerCoordinator scheduler,
    TimeProvider? timeProvider = null) : IDocumentOcrHandoff, IDocumentOcrResultReader
{
    private const int MaximumRequestedPagesJsonBytes = 8 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<DocumentOcrHandoffResult> HandoffAsync(
        DocumentOcrHandoffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PaddleOcrVlmRuntimeContract.AssertFrozenSettings();
        if (!IsCanonicalSha256(request.ContentSha256))
        {
            return DocumentOcrHandoffResult.Refused("document-ocr-content-hash-invalid");
        }

        var miniTaskId = CreateMiniTaskId(request);
        var requestedPagesJson = JsonSerializer.Serialize(request.PageIndexes, JsonOptions);
        if (StrictUtf8.GetByteCount(requestedPagesJson) > MaximumRequestedPagesJsonBytes)
        {
            return DocumentOcrHandoffResult.Refused("document-ocr-page-request-too-large");
        }

        var ensured = await EnsurePendingAsync(request, miniTaskId, requestedPagesJson, cancellationToken)
            .ConfigureAwait(false);
        if (!ensured.RequestAccepted)
        {
            return DocumentOcrHandoffResult.Refused(ensured.ReasonCode);
        }

        if (ensured.MiniTaskAlreadyExists)
        {
            return DocumentOcrHandoffResult.Queued(miniTaskId);
        }

        try
        {
            var handoff = await scheduler.HandoffAsync(
                    new GpuMiniTaskHandoffRequest(
                        request.ParentJob,
                        GpuPriorityLane.DocumentIndexing,
                        PaddleOcrVlmRuntimeContract.ModelRuntimeKey,
                        PaddleOcrVlmRuntimeContract.SettingsFingerprint,
                        PaddleOcrVlmRuntimeContract.EstimatedDocumentBytes,
                        CreateIdempotencyKey(request),
                        miniTaskId),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!handoff.Committed || handoff.MiniTaskId != miniTaskId)
            {
                throw new InvalidOperationException("document-ocr-handoff-incomplete");
            }

            return DocumentOcrHandoffResult.Queued(miniTaskId);
        }
        catch (InvalidOperationException)
        {
            if (!await MiniTaskExistsAsync(miniTaskId, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            // A concurrent/retried hand-off committed the durable task first.  The exact request
            // identity was checked before this call, so it is the same source-bound work.
            return DocumentOcrHandoffResult.Queued(miniTaskId);
        }
    }

    public async ValueTask<DocumentOcrExecutionResult?> ReadCompletedAsync(
        ClaimedJob parentJob,
        string contentSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parentJob);
        if (!IsCanonicalSha256(contentSha256))
        {
            return new DocumentOcrExecutionResult(false, "document-ocr-content-hash-invalid", []);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var resultJson = await context.DocumentOcrRequests.AsNoTracking()
            .Where(request =>
                request.ParentJobId == parentJob.JobId.Value &&
                request.PipelineRecordId == parentJob.PipelineRecordId.Value &&
                request.SourceRevision == parentJob.SourceRevision &&
                request.ContentSha256 == contentSha256 &&
                request.State >= (int)DocumentOcrRequestState.ResultStored)
            .Select(request => request.ResultJson)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (resultJson is null)
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<DocumentOcrExecutionResult>(resultJson, JsonOptions);
            return result is null || !IsResultShapeValid(result)
                ? new DocumentOcrExecutionResult(false, "document-ocr-result-invalid", [])
                : result;
        }
        catch (JsonException)
        {
            return new DocumentOcrExecutionResult(false, "document-ocr-result-invalid", []);
        }
    }

    /// <summary>Writes one bounded provider result before its GPU completion receipt is recorded.</summary>
    public async ValueTask<DocumentOcrStoredResult> StoreResultAsync(
        GpuExecutorBatchHandle handle,
        Guid miniTaskId,
        DocumentOcrExecutionResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        if (miniTaskId == Guid.Empty)
        {
            throw new ArgumentException("An OCR result requires a mini-task ID.", nameof(miniTaskId));
        }

        ValidateResultShape(result);
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        if (StrictUtf8.GetByteCount(resultJson) > DocumentOcrProvenance.MaximumMetadataUtf8Bytes)
        {
            throw new InvalidOperationException("document-ocr-result-too-large");
        }

        var digest = SHA256.HashData(StrictUtf8.GetBytes(resultJson));
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            var request = await context.DocumentOcrRequests.SingleOrDefaultAsync(
                    value => value.MiniTaskId == miniTaskId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (request is null ||
                !string.Equals(request.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.ModelRuntimeKey, StringComparison.Ordinal) ||
                !string.Equals(request.SettingsFingerprint, PaddleOcrVlmRuntimeContract.SettingsFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("document-ocr-request-unavailable");
            }

            var task = await context.GpuMiniTasks.SingleOrDefaultAsync(value =>
                    value.Id == miniTaskId &&
                    value.BatchId == handle.BatchId &&
                    value.AdmissionGeneration == handle.AdmissionGeneration &&
                    value.ExecutionState == (int)GpuMiniTaskExecutionState.Active,
                    cancellationToken)
                .ConfigureAwait(false);
            var dispatch = await context.GpuExecutorDispatches.SingleOrDefaultAsync(value =>
                    value.DispatchId == handle.DispatchId &&
                    value.BatchId == handle.BatchId &&
                    value.CapacitySlotKey == handle.CapacitySlotKey &&
                    value.ExecutorKey == handle.ExecutorKey &&
                    value.AdmissionGeneration == handle.AdmissionGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (task is null || dispatch is null ||
                !await SourceIsEnabledAsync(context, request, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("document-ocr-execution-unavailable");
            }

            if (request.State >= (int)DocumentOcrRequestState.ResultStored)
            {
                if (request.ResultDigest is null || !CryptographicOperations.FixedTimeEquals(request.ResultDigest, digest))
                {
                    throw new InvalidOperationException("document-ocr-result-conflict");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new DocumentOcrStoredResult(true, request.ResultDigest.ToArray());
            }

            if (request.State != (int)DocumentOcrRequestState.Pending)
            {
                throw new InvalidOperationException("document-ocr-request-state-invalid");
            }

            request.ResultJson = resultJson;
            request.ResultDigest = digest;
            request.State = (int)DocumentOcrRequestState.ResultStored;
            request.UpdatedAtUtc = _timeProvider.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DocumentOcrStoredResult(true, digest.ToArray());
        }).ConfigureAwait(false);
    }

    /// <summary>Reads one source-enabled local OCR task in the caller's exact durable dispatch state; it never opens a provider.</summary>
    public async ValueTask<DocumentOcrExecutionWork?> ReadExecutionWorkAsync(
        GpuExecutorBatchHandle handle,
        GpuExecutorDispatchState requiredDispatchState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        if (requiredDispatchState is not GpuExecutorDispatchState.PendingDelivery and not GpuExecutorDispatchState.Acknowledged)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredDispatchState));
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidate = await (
                from request in context.DocumentOcrRequests.AsNoTracking()
                join task in context.GpuMiniTasks.AsNoTracking() on request.MiniTaskId equals task.Id
                join dispatch in context.GpuExecutorDispatches.AsNoTracking() on task.BatchId equals dispatch.BatchId
                where task.BatchId == handle.BatchId &&
                      dispatch.DispatchId == handle.DispatchId &&
                      dispatch.CapacitySlotKey == handle.CapacitySlotKey &&
                      dispatch.ExecutorKey == handle.ExecutorKey &&
                      dispatch.AdmissionGeneration == handle.AdmissionGeneration &&
                      dispatch.State == (int)requiredDispatchState &&
                      task.AdmissionGeneration == handle.AdmissionGeneration &&
                      task.ExecutionState == (int)GpuMiniTaskExecutionState.Active &&
                      request.State == (int)DocumentOcrRequestState.Pending &&
                      request.ModelRuntimeKey == PaddleOcrVlmRuntimeContract.ModelRuntimeKey &&
                      request.SettingsFingerprint == PaddleOcrVlmRuntimeContract.SettingsFingerprint
                select new { Request = request, Task = task })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null ||
            !await SourceIsEnabledAsync(context, candidate.Request, cancellationToken).ConfigureAwait(false) ||
            await context.GpuMiniTasks.CountAsync(task =>
                    task.BatchId == handle.BatchId &&
                    task.AdmissionGeneration == handle.AdmissionGeneration &&
                    task.ExecutionState == (int)GpuMiniTaskExecutionState.Active,
                cancellationToken).ConfigureAwait(false) != 1)
        {
            return null;
        }

        try
        {
            var pages = JsonSerializer.Deserialize<int[]>(candidate.Request.RequestedPageIndexesJson, JsonOptions);
            if (pages is null || pages.Length == 0 || pages.Any(static page => page < 0) ||
                pages.Distinct().Count() != pages.Length || !pages.SequenceEqual(pages.Order()))
            {
                return null;
            }

            return new DocumentOcrExecutionWork(
                candidate.Request.MiniTaskId,
                new SourceRevisionId(candidate.Request.RetainedSourceRevisionId),
                candidate.Request.ContentSha256,
                pages);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Returns a previously persisted local result for lifecycle completion or recovery.</summary>
    public async ValueTask<DocumentOcrPendingCompletion?> ReadPendingCompletionAsync(
        GpuExecutorBatchHandle handle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidate = await (
                from request in context.DocumentOcrRequests.AsNoTracking()
                join task in context.GpuMiniTasks.AsNoTracking() on request.MiniTaskId equals task.Id
                join dispatch in context.GpuExecutorDispatches.AsNoTracking() on task.BatchId equals dispatch.BatchId
                where dispatch.DispatchId == handle.DispatchId &&
                      dispatch.BatchId == handle.BatchId &&
                      dispatch.CapacitySlotKey == handle.CapacitySlotKey &&
                      dispatch.ExecutorKey == handle.ExecutorKey &&
                      dispatch.AdmissionGeneration == handle.AdmissionGeneration &&
                      task.AdmissionGeneration == handle.AdmissionGeneration &&
                      request.State >= (int)DocumentOcrRequestState.ResultStored &&
                      request.ResultDigest != null &&
                      request.ModelRuntimeKey == PaddleOcrVlmRuntimeContract.ModelRuntimeKey &&
                      request.SettingsFingerprint == PaddleOcrVlmRuntimeContract.SettingsFingerprint
                select new { Request = request, Task = task, Dispatch = dispatch })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        return new DocumentOcrPendingCompletion(
            candidate.Request.MiniTaskId,
            candidate.Request.ResultDigest!.ToArray());
    }

    public async ValueTask<IReadOnlyList<DocumentOcrPendingCompletionHandle>> ReadPendingCompletionsAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await (
                from request in context.DocumentOcrRequests.AsNoTracking()
                join task in context.GpuMiniTasks.AsNoTracking() on request.MiniTaskId equals task.Id
                join dispatch in context.GpuExecutorDispatches.AsNoTracking() on task.BatchId equals dispatch.BatchId
                where request.State >= (int)DocumentOcrRequestState.ResultStored &&
                      request.State < (int)DocumentOcrRequestState.Requeued &&
                      request.ResultDigest != null &&
                      request.ModelRuntimeKey == PaddleOcrVlmRuntimeContract.ModelRuntimeKey &&
                      request.SettingsFingerprint == PaddleOcrVlmRuntimeContract.SettingsFingerprint &&
                      dispatch.AdmissionGeneration == task.AdmissionGeneration
                orderby request.UpdatedAtUtc, request.MiniTaskId
                select new { Request = request, Task = task, Dispatch = dispatch })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var pending = new List<DocumentOcrPendingCompletionHandle>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // Settling an already persisted result releases capacity even while its source is
            // paused. RequeueCompletedAsync still fences document continuation on source state.
            pending.Add(new DocumentOcrPendingCompletionHandle(
                new GpuExecutorBatchHandle(
                    candidate.Dispatch.BatchId,
                    candidate.Dispatch.CapacitySlotKey,
                    candidate.Dispatch.ExecutorKey,
                    candidate.Dispatch.AdmissionGeneration,
                    candidate.Dispatch.DispatchId),
                candidate.Request.MiniTaskId,
                candidate.Request.ResultDigest!.ToArray()));
        }

        return pending;
    }

    /// <summary>
    /// Releases the original Extract dispatch only after the generic GPU lifecycle reached its
    /// terminal completed state. The stored OCR payload remains private for idempotent retries.
    /// </summary>
    public async ValueTask<bool> RequeueCompletedAsync(
        GpuExecutorBatchHandle handle,
        Guid miniTaskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        if (miniTaskId == Guid.Empty)
        {
            throw new ArgumentException("An OCR requeue requires a mini-task ID.", nameof(miniTaskId));
        }

        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            var request = await context.DocumentOcrRequests.SingleOrDefaultAsync(
                    value => value.MiniTaskId == miniTaskId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (request is null || request.ResultJson is null || request.ResultDigest is null ||
                request.State < (int)DocumentOcrRequestState.ResultStored ||
                !await SourceIsEnabledAsync(context, request, cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var task = await context.GpuMiniTasks.SingleOrDefaultAsync(value =>
                    value.Id == miniTaskId &&
                    value.BatchId == handle.BatchId &&
                    value.AdmissionGeneration == handle.AdmissionGeneration &&
                    value.ExecutionState == (int)GpuMiniTaskExecutionState.Completed,
                    cancellationToken)
                .ConfigureAwait(false);
            var dispatch = await context.GpuExecutorDispatches.SingleOrDefaultAsync(value =>
                    value.DispatchId == handle.DispatchId &&
                    value.BatchId == handle.BatchId &&
                    value.CapacitySlotKey == handle.CapacitySlotKey &&
                    value.ExecutorKey == handle.ExecutorKey &&
                    value.AdmissionGeneration == handle.AdmissionGeneration &&
                    value.State == (int)GpuExecutorDispatchState.Terminal,
                    cancellationToken)
                .ConfigureAwait(false);
            if (task is null || dispatch is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var parent = await context.Jobs.SingleOrDefaultAsync(value =>
                    value.Id == request.ParentJobId &&
                    value.PipelineRecordId == request.PipelineRecordId &&
                    value.SourceRevision == request.SourceRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (parent is null || parent.PublicState is not ((int)PublicJobState.GpuProcessing or (int)PublicJobState.WorkerQueued))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (request.State == (int)DocumentOcrRequestState.Requeued &&
                parent.PublicState == (int)PublicJobState.WorkerQueued)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            var outbox = await context.OutboxMessages.SingleOrDefaultAsync(value =>
                    value.PipelineRecordId == parent.PipelineRecordId &&
                    value.SourceRevision == parent.SourceRevision &&
                    value.Stage == parent.Stage &&
                    value.Operation == parent.Operation &&
                    value.DispatchedAtUtc == null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (outbox is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var now = _timeProvider.GetUtcNow();
            parent.PublicState = (int)PublicJobState.WorkerQueued;
            parent.LeaseOwner = null;
            parent.LeaseExpiresAtUtc = null;
            parent.Reason = null;
            parent.ErrorDetails = null;
            outbox.LeaseOwner = null;
            outbox.LeaseExpiresAtUtc = null;
            outbox.DueAtUtc = now;
            request.State = (int)DocumentOcrRequestState.Requeued;
            request.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private async ValueTask<DocumentOcrEnsureResult> EnsurePendingAsync(
        DocumentOcrHandoffRequest request,
        Guid miniTaskId,
        string requestedPagesJson,
        CancellationToken cancellationToken)
    {
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var strategy = executionContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            var existing = await context.DocumentOcrRequests.SingleOrDefaultAsync(
                    value => value.MiniTaskId == miniTaskId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!MatchesRequest(existing, request, requestedPagesJson))
                {
                    throw new InvalidOperationException("document-ocr-request-identity-conflict");
                }

                var existingTask = await context.GpuMiniTasks.AnyAsync(
                        value => value.Id == miniTaskId,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new DocumentOcrEnsureResult(true, existingTask, "document-ocr-queued");
            }

            var parentClaimIsCurrent = await (
                    from job in context.Jobs.AsNoTracking()
                    join record in context.PipelineRecords.AsNoTracking()
                        on job.PipelineRecordId equals record.Id
                    join revision in context.SourceRevisions.AsNoTracking()
                        on record.SourceRevisionId equals revision.Id
                    join root in context.SourceRootConfigurations.AsNoTracking()
                        on revision.SourceRootId equals root.Id
                    where job.Id == request.ParentJob.JobId.Value &&
                          job.PipelineRecordId == request.ParentJob.PipelineRecordId.Value &&
                          job.SourceRevision == request.ParentJob.SourceRevision &&
                          job.Stage == (int)request.ParentJob.Stage &&
                          job.Operation == request.ParentJob.Operation &&
                          job.PublicState == (int)PublicJobState.WorkerProcessing &&
                          job.LeaseOwner == request.ParentJob.LeaseOwner &&
                          job.LeaseGeneration == request.ParentJob.LeaseGeneration &&
                          record.Revision == request.ParentJob.SourceRevision &&
                          !record.IsDeleted &&
                          record.SourceRevisionId == request.RetainedSourceRevisionId.Value &&
                          revision.ContentSha256 == request.ContentSha256 &&
                          root.State == (int)SourceRootState.Enabled
                    select job.Id)
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!parentClaimIsCurrent)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new DocumentOcrEnsureResult(false, false, "document-ocr-source-unavailable");
            }

            var now = _timeProvider.GetUtcNow();
            context.DocumentOcrRequests.Add(new DocumentOcrRequestEntity
            {
                MiniTaskId = miniTaskId,
                ParentJobId = request.ParentJob.JobId.Value,
                PipelineRecordId = request.ParentJob.PipelineRecordId.Value,
                SourceRevision = request.ParentJob.SourceRevision,
                RetainedSourceRevisionId = request.RetainedSourceRevisionId.Value,
                ContentSha256 = request.ContentSha256,
                RequestedPageIndexesJson = requestedPagesJson,
                ModelRuntimeKey = PaddleOcrVlmRuntimeContract.ModelRuntimeKey,
                SettingsFingerprint = PaddleOcrVlmRuntimeContract.SettingsFingerprint,
                State = (int)DocumentOcrRequestState.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DocumentOcrEnsureResult(true, false, "document-ocr-queued");
        }).ConfigureAwait(false);
    }

    private async ValueTask<bool> MiniTaskExistsAsync(Guid miniTaskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.GpuMiniTasks.AsNoTracking().AnyAsync(value => value.Id == miniTaskId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool MatchesRequest(
        DocumentOcrRequestEntity existing,
        DocumentOcrHandoffRequest request,
        string requestedPagesJson) =>
        existing.ParentJobId == request.ParentJob.JobId.Value &&
        existing.PipelineRecordId == request.ParentJob.PipelineRecordId.Value &&
        existing.SourceRevision == request.ParentJob.SourceRevision &&
        existing.RetainedSourceRevisionId == request.RetainedSourceRevisionId.Value &&
        string.Equals(existing.ContentSha256, request.ContentSha256, StringComparison.Ordinal) &&
        string.Equals(existing.RequestedPageIndexesJson, requestedPagesJson, StringComparison.Ordinal) &&
        string.Equals(existing.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.ModelRuntimeKey, StringComparison.Ordinal) &&
        string.Equals(existing.SettingsFingerprint, PaddleOcrVlmRuntimeContract.SettingsFingerprint, StringComparison.Ordinal);

    private static Guid CreateMiniTaskId(DocumentOcrHandoffRequest request) =>
        CreateDeterministicGuid($"FluxKnowledge.DocumentOcrMiniTask.v1|{request.ParentJob.JobId.Value:N}|" +
                                $"{request.ParentJob.PipelineRecordId.Value:N}|{request.ParentJob.SourceRevision}|" +
                                $"{request.RetainedSourceRevisionId.Value:N}|{request.ContentSha256}|" +
                                $"{string.Join(',', request.PageIndexes)}|{PaddleOcrVlmRuntimeContract.ModelRuntimeKey}|" +
                                PaddleOcrVlmRuntimeContract.SettingsFingerprint);

    private static string CreateIdempotencyKey(DocumentOcrHandoffRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(
            $"FluxKnowledge.DocumentOcrHandoff.v1|{request.ParentJob.JobId.Value:N}|" +
            $"{request.ParentJob.PipelineRecordId.Value:N}|{request.ParentJob.SourceRevision}|" +
            $"{request.RetainedSourceRevisionId.Value:N}|{request.ContentSha256}|{string.Join(',', request.PageIndexes)}|" +
            $"{PaddleOcrVlmRuntimeContract.ModelRuntimeKey}|{PaddleOcrVlmRuntimeContract.SettingsFingerprint}")));

    private static Guid CreateDeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(StrictUtf8.GetBytes(value));
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsResultShapeValid(DocumentOcrExecutionResult result)
    {
        try
        {
            ValidateResultShape(result);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void ValidateResultShape(DocumentOcrExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(result.ReasonCode) || result.ReasonCode.Length > 128 || result.Pages is null ||
            result.Pages.Select(static page => page.PageIndex).Distinct().Count() != result.Pages.Count ||
            result.Pages.Any(static page => page.PageIndex < 0 || page.OrientationDegrees is not (0 or 90 or 180 or 270) ||
                page.Blocks is null || page.Blocks.Count > DocumentOcrProvenance.MaximumBlocksPerPage ||
                page.SourceWidth.HasValue != page.SourceHeight.HasValue ||
                page.SourceWidth is <= 0 || page.SourceHeight is <= 0 ||
                (page.SourceTransform is not null &&
                 (page.SourceWidth is null || page.SourceTransform is not ("identity" or "rotate-90" or "rotate-180" or "rotate-270")))))
        {
            throw new InvalidOperationException("document-ocr-result-invalid");
        }

        foreach (var block in result.Pages.SelectMany(static page => page.Blocks))
        {
            if (block is null || block.Kind is not ("text" or "table" or "title" or "header" or "footer" or "figure") ||
                block.Text is null || block.Left < 0 || block.Top < 0 || block.Width <= 0 || block.Height <= 0)
            {
                throw new InvalidOperationException("document-ocr-result-invalid");
            }

            _ = StrictUtf8.GetByteCount(block.Text);
        }
    }

    private static Task<bool> SourceIsEnabledAsync(
        FluxKnowledgeDbContext context,
        DocumentOcrRequestEntity request,
        CancellationToken cancellationToken) =>
        (from record in context.PipelineRecords.AsNoTracking()
         join revision in context.SourceRevisions.AsNoTracking() on record.SourceRevisionId equals revision.Id
         join root in context.SourceRootConfigurations.AsNoTracking() on revision.SourceRootId equals root.Id
         where record.Id == request.PipelineRecordId &&
               record.Revision == request.SourceRevision &&
               !record.IsDeleted &&
               record.SourceRevisionId == request.RetainedSourceRevisionId &&
               revision.ContentSha256 == request.ContentSha256 &&
               root.State == (int)SourceRootState.Enabled
         select root.Id)
        .AnyAsync(cancellationToken);

    private sealed record DocumentOcrEnsureResult(bool RequestAccepted, bool MiniTaskAlreadyExists, string ReasonCode);
}

public sealed record DocumentOcrStoredResult(bool Stored, byte[] ResultDigest);

public sealed record DocumentOcrExecutionWork(
    Guid MiniTaskId,
    SourceRevisionId RetainedSourceRevisionId,
    string ContentSha256,
    IReadOnlyList<int> PageIndexes);

public sealed record DocumentOcrPendingCompletion(Guid MiniTaskId, byte[] ResultDigest);

public sealed record DocumentOcrPendingCompletionHandle(
    GpuExecutorBatchHandle Handle,
    Guid MiniTaskId,
    byte[] ResultDigest);
