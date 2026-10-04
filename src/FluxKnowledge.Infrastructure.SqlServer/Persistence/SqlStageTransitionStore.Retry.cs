using System.Data;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlStageTransitionStore
{
    public async ValueTask RetryAsync(StageRetryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var publicationRetry = request.CurrentJob.Stage == PipelineStage.Publish && request.CurrentJob.Operation == PipelineOperations.Publish &&
            request.Reason == "publication-snapshot-conflict";
        var embeddingContinuation = request.CurrentJob.Stage == PipelineStage.Embed && request.CurrentJob.Operation == PipelineOperations.Embed &&
            request.Reason == "embedding-batch-persisted";
        var sourceDeferral = request.SourceDeferral is not null && request.Reason == SqlRepositoryWorkRecovery.Waiting;
        if ((!publicationRetry && !embeddingContinuation && !sourceDeferral) ||
            request.DispatchMessage.Stage != request.CurrentJob.Stage || request.DispatchMessage.Operation != request.CurrentJob.Operation ||
            request.DispatchMessage.PipelineRecordId != request.CurrentJob.PipelineRecordId ||
            request.DispatchMessage.SourceRevision != request.CurrentJob.SourceRevision ||
            string.IsNullOrWhiteSpace(request.Actor))
            throw new ArgumentException("Only a fenced publication conflict or embedding continuation may be deferred here.", nameof(request));
        await using var executionContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await executionContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken);
            await SqlCorpusRebuildStore.ValidateMaintenanceJobAsync(context, request.CurrentJob.JobId.Value, cancellationToken);
            // Match the ordinary transition's dispatch-before-job order. A retry never completes delivery.
            var dispatch = await context.OutboxMessages.FromSqlInterpolated($"""
                SELECT * FROM [OutboxMessages] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {request.DispatchMessage.DispatchMessageId.Value}
                """).SingleOrDefaultAsync(cancellationToken);
            var job = await context.Jobs.FromSqlInterpolated($"""
                SELECT * FROM [Jobs] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {request.CurrentJob.JobId.Value}
                """).SingleOrDefaultAsync(cancellationToken);
            if (sourceDeferral && job is not null && dispatch is not null && dispatch.DispatchedAtUtc is null &&
                dispatch.JobId == job.Id && job.PipelineRecordId == request.CurrentJob.PipelineRecordId.Value &&
                job.SourceRevision == request.CurrentJob.SourceRevision && job.Stage == (int)request.CurrentJob.Stage && job.Operation == request.CurrentJob.Operation &&
                dispatch.PipelineRecordId == job.PipelineRecordId && dispatch.SourceRevision == job.SourceRevision && dispatch.Stage == job.Stage && dispatch.Operation == job.Operation &&
                dispatch.IdempotencyKey == request.DispatchMessage.IdempotencyKey && dispatch.DispatchGeneration == request.DispatchMessage.DispatchGeneration &&
                job.LeaseGeneration == request.CurrentJob.LeaseGeneration + 1 && dispatch.LeaseGeneration == request.DispatchMessage.LeaseGeneration + 1 &&
                job.PublicState == (int)PublicJobState.WorkerQueued && job.LeaseOwner is null && dispatch.LeaseOwner is null &&
                job.Reason is SqlRepositoryWorkRecovery.Waiting or SqlRepositoryWorkRecovery.Blocked or "repository-source-resumed")
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            if (job is null || dispatch is null || dispatch.DispatchedAtUtc is not null ||
                dispatch.JobId != job.Id ||
                job.PipelineRecordId != request.CurrentJob.PipelineRecordId.Value ||
                job.SourceRevision != request.CurrentJob.SourceRevision || job.Stage != (int)request.CurrentJob.Stage ||
                job.Operation != request.CurrentJob.Operation || dispatch.PipelineRecordId != job.PipelineRecordId ||
                dispatch.SourceRevision != job.SourceRevision || dispatch.Stage != job.Stage || dispatch.Operation != job.Operation ||
                dispatch.IdempotencyKey != request.DispatchMessage.IdempotencyKey ||
                dispatch.DispatchGeneration != request.DispatchMessage.DispatchGeneration ||
                job.LeaseGeneration != request.CurrentJob.LeaseGeneration ||
                dispatch.LeaseGeneration != request.DispatchMessage.LeaseGeneration)
                throw new InvalidOperationException("The retry no longer owns the exact durable stage delivery.");

            if (job.PublicState == (int)PublicJobState.WorkerQueued && job.LeaseOwner is null &&
                dispatch.LeaseOwner is null && job.Reason == request.Reason &&
                job.DueAtUtc == request.DueAtUtc && dispatch.DueAtUtc == request.DueAtUtc)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            if (job.PublicState != (int)PublicJobState.WorkerProcessing ||
                job.LeaseOwner != request.CurrentJob.LeaseOwner || dispatch.LeaseOwner != request.DispatchMessage.LeaseOwner)
                throw new InvalidOperationException("The retry Job or DispatchMessage lease was lost.");

            if (sourceDeferral)
            {
                if (!await (from record in context.PipelineRecords join revision in context.SourceRevisions on record.SourceRevisionId equals revision.Id
                    join root in context.SourceRootConfigurations on revision.SourceRootId equals root.Id
                    where record.Id == job.PipelineRecordId && (root.CrawlMode == (int)SourceDiscoveryMode.GitTracked || record.RepositoryRecoveryBindingJson != null)
                    select record.Id).AnyAsync(cancellationToken))
                    throw new InvalidOperationException("Only repository source work may use this deferral.");
                var deferralAt = _timeProvider.GetUtcNow();
                if (job.LeaseExpiresAtUtc <= deferralAt || dispatch.LeaseExpiresAtUtc <= deferralAt || job.LeaseExpiresAtUtc is null || dispatch.LeaseExpiresAtUtc is null)
                    throw new InvalidOperationException("The source deferral no longer owns live leases.");
                await SqlRepositoryWorkRecovery.MarkRecoveryRequiredAsync(context, job, cancellationToken);
                var refusal = await SqlRepositoryWorkRecovery.ReadEligibilityAsync(context, job, deferralAt, cancellationToken);
                refusal ??= await SqlRepositoryWorkRecovery.CheckCheckpointAsync(context, job, _embeddingRuntime, cancellationToken);
                if (request.SourceDeferral!.Blocked) refusal ??= request.SourceDeferral;
                SqlRepositoryWorkRecovery.SetWaiting(context, job, dispatch, refusal ?? new("repository-source-ready-after-concurrent-rediscovery"), deferralAt);
                if (refusal is null)
                {
                    job.Reason = "repository-source-resumed";
                    job.ErrorDetails = null;
                }
                await SqlRepositoryWorkRecovery.UpdateActivityAsync(context, job, refusal, deferralAt, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            job.PublicState = (int)PublicJobState.WorkerQueued;
            job.LeaseOwner = null;
            job.LeaseExpiresAtUtc = null;
            job.DueAtUtc = request.DueAtUtc;
            job.Reason = request.Reason;
            job.ErrorDetails = null;
            dispatch.LeaseOwner = null;
            dispatch.LeaseExpiresAtUtc = null;
            dispatch.DueAtUtc = request.DueAtUtc;
            var now = _timeProvider.GetUtcNow();
            if (publicationRetry)
            {
                await context.SourceActivities.Where(activity =>
                    activity.ResultingPipelineRecordId == job.PipelineRecordId &&
                    activity.ResultingPipelineRecordRevision == job.SourceRevision &&
                    (activity.State == (int)SourceActivityState.Pending || activity.State == (int)SourceActivityState.Running ||
                     activity.State == (int)SourceActivityState.FailedRetryable))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(activity => activity.State, (int)SourceActivityState.FailedRetryable)
                        .SetProperty(activity => activity.Reason, request.Reason).SetProperty(activity => activity.UpdatedAtUtc, now), cancellationToken);
            }
            OperatorEventAppender.Add(context, new OperatorEventDraft("pipeline.stage_deferred", "pipeline", "information",
                request.Actor, now, PipelineRecordId: job.PipelineRecordId,
                CorrelationId: $"pipeline:{job.PipelineRecordId:N}:{job.SourceRevision}",
                Details: new { stage = request.CurrentJob.Stage.ToString(), reason = request.Reason, dueAtUtc = request.DueAtUtc }));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
