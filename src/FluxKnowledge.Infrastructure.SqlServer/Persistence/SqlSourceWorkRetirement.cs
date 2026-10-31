using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlSourceWorkRetirement(IDbContextFactory<FluxKnowledgeDbContext> factory, TimeProvider clock,
    EmbeddingGpuExecutor? embeddings = null, PaddleOcrVlmExecutionRegistry? ocr = null) : ISourceWorkRetirement
{
    public async ValueTask<int> RetireAsync(CancellationToken cancellationToken)
    {
        await using (var lookup = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var eligible = SqlSourceWorkEligibility.Records(lookup, includePending: false, includeAdmission: false).Select(x => x.Id);
            var active = await lookup.GpuMiniTasks.AsNoTracking().Where(x => x.ParentJobId != null && x.ExecutionState == (int)GpuMiniTaskExecutionState.Active &&
                    !eligible.Contains(x.ParentJob!.PipelineRecordId) && x.ParentJob.PipelineRecord.SourceRevisionId != null)
                .OrderBy(x => x.CreatedSequence).Take(128).Select(x => x.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in active) { embeddings?.RequestCancellation(id); ocr?.RequestCancellation(id); }
        }
        await using var execution = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await execution.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(db, cancellationToken).ConfigureAwait(false);
            await SqlGpuSchedulerStore.AcquireMutationLockAsync(db, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
            var eligible = SqlSourceWorkEligibility.Records(db, includePending: false, includeAdmission: false).Select(x => x.Id);
            var obsolete = from record in db.PipelineRecords
                join source in db.SourceRevisions on record.SourceRevisionId equals source.Id
                join root in db.SourceRootConfigurations on source.SourceRootId equals root.Id
                where root.State != (int)SourceRootState.Deleting && !eligible.Contains(record.Id)
                select record.Id;
            // Admission generation zero and no batch prove that no native reservation
            // ever existed. Do not rewrite previously admitted/uncertain task evidence.
            await db.GpuMiniTasks.Where(x => x.ParentJobId != null && x.ExecutionState == (int)GpuMiniTaskExecutionState.Ready &&
                    x.AdmissionGeneration == 0 && x.ReservationAttemptCount == 0 && x.BatchId == null && obsolete.Contains(x.ParentJob!.PipelineRecordId))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.ExecutionState, (int)GpuMiniTaskExecutionState.Cancelled), cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var availableInputs = SqlSourceWorkEligibility.Inputs(db, includePending: false, includeAdmission: false).Select(x => x.Id);
            var obsoleteInputs = from input in db.SourceRevisions
                join root in db.SourceRootConfigurations on input.SourceRootId equals root.Id
                where root.State != (int)SourceRootState.Deleting && !availableInputs.Contains(input.Id)
                select input.Id;
            var branches = await db.SourceProcessorBranches.Where(branch => obsoleteInputs.Contains(branch.SourceRevisionId) &&
                    (branch.State == (int)RetainedProcessorBranchState.Pending || branch.State == (int)RetainedProcessorBranchState.Running) &&
                    (branch.LeaseExpiresAtUtc == null || branch.LeaseExpiresAtUtc <= now))
                .OrderBy(x => x.UpdatedAtUtc).ThenBy(x => x.Id).Take(128).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var branch in branches)
            {
                branch.State = (int)RetainedProcessorBranchState.Blocked;
                branch.LeaseOwner = null; branch.LeaseExpiresAtUtc = null; branch.UpdatedAtUtc = now;
                var activity = await db.SourceActivities.SingleAsync(x => x.Id == branch.SourceActivityId, cancellationToken).ConfigureAwait(false);
                activity.State = (int)SourceActivityState.CancelledSuperseded;
                activity.Reason = "source-removed"; activity.UpdatedAtUtc = now;
                foreach (var attempt in await db.SourceProcessorAttempts.Where(x => x.BranchId == branch.Id && x.FinishedAtUtc == null)
                             .ToArrayAsync(cancellationToken).ConfigureAwait(false))
                {
                    attempt.FinishedAtUtc = now;
                    attempt.OutcomeCode = "source-removed";
                }
            }
            var unbound = await db.SourceActivities.Where(activity => activity.ResultingPipelineRecordId == null &&
                    obsoleteInputs.Contains(activity.SourceRevisionId) &&
                    (activity.State == (int)SourceActivityState.Pending || activity.State == (int)SourceActivityState.Running ||
                     activity.State == (int)SourceActivityState.FailedRetryable) &&
                    !db.SourceProcessorBranches.Any(branch => branch.SourceActivityId == activity.Id &&
                        branch.State == (int)RetainedProcessorBranchState.Running && branch.LeaseExpiresAtUtc > now))
                .OrderBy(x => x.UpdatedAtUtc).ThenBy(x => x.Id).Take(128).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var activity in unbound)
            {
                activity.State = (int)SourceActivityState.CancelledSuperseded;
                activity.Reason = "source-removed"; activity.UpdatedAtUtc = now;
            }
            var jobs = await db.Jobs.Where(job => obsolete.Contains(job.PipelineRecordId) &&
                    (job.PublicState == (int)PublicJobState.WorkerQueued || job.PublicState == (int)PublicJobState.GpuQueued ||
                     job.PublicState == (int)PublicJobState.WorkerProcessing && (job.LeaseExpiresAtUtc == null || job.LeaseExpiresAtUtc <= now) ||
                     job.PublicState == (int)PublicJobState.GpuProcessing) &&
                    !db.GpuMiniTasks.Any(task => task.ParentJobId == job.Id && (task.ExecutionState == (int)GpuMiniTaskExecutionState.Active ||
                        task.ExecutionState == (int)GpuMiniTaskExecutionState.Ready)) &&
                    !db.EmbeddingGpuRequests.Any(request => request.ParentJobId == job.Id && request.ExecutorInstanceId != null && !request.NativeCleanupConfirmed) &&
                    !db.DocumentOcrRequests.Any(request => request.ParentJobId == job.Id && request.ExecutionInstanceId != null && request.NativeCleanupConfirmedAtUtc == null))
                .OrderBy(x => x.DueAtUtc).ThenBy(x => x.Id).Take(128).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var job in jobs)
            {
                job.PublicState = (int)PublicJobState.Failed;
                job.Reason = "source-removed";
                job.ErrorDetails = null;
                job.LeaseOwner = null;
                job.LeaseExpiresAtUtc = null;
            }
            var recordIds = jobs.Select(x => x.PipelineRecordId).ToArray();
            await db.SourceActivities.Where(x => x.ResultingPipelineRecordId != null && recordIds.Contains(x.ResultingPipelineRecordId.Value) &&
                    x.State != (int)SourceActivityState.Completed)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.State, (int)SourceActivityState.CancelledSuperseded)
                    .SetProperty(x => x.Reason, "source-removed").SetProperty(x => x.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return jobs.Length + branches.Length;
        }).ConfigureAwait(false);
    }
}
