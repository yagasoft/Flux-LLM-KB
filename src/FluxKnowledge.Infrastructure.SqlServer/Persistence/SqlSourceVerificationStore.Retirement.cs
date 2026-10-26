using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed record SourceVerificationRetirementResult(int Runs, int Requests, int Jobs);

public sealed partial class SqlSourceVerificationStore
{
    public const string FeatureOffReason = "source-verification-feature-disabled";

    /// <summary>Explicit stopped-deployment operation, never called by a hosted service.</summary>
    public async ValueTask<SourceVerificationRetirementResult> RetireForFeatureOffAsync(ISourceVerificationReaderSlot slot,
        Func<CancellationToken, ValueTask> requireStoppedWriters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(requireStoppedWriters);
        if (options.Enabled) throw new InvalidOperationException("SourceVerificationRetirementRequiresFeatureOff");
        using var readerSlot = slot.TryAcquire() ?? throw new InvalidOperationException("SourceVerificationReaderStillOwned");
        // The physical slot proves native verification readers have unwound. The caller
        // separately binds the retained hold and stopped-writer evidence to this database/runtime.
        await requireStoppedWriters(ct).ConfigureAwait(false);
        var result = new SourceVerificationRetirementResult(0, 0, 0);
        await _transactions.ExecuteAsync(async db =>
        {
            await SqlSourceScanStore.LockControlRangesAsync(db, ct, includeAllRoots: true).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var runs = await db.SourceVerificationRuns.Where(x => x.Active).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var run in runs)
            {
                await InvalidateRunAsync(db, run, now, ct, reason: FeatureOffReason).ConfigureAwait(false);
                run.NextBatchAtUtc = null;
            }
            var requests = await db.SourceScanRequests.Where(x => x.RequestKind == RequestKind &&
                x.State != (int)SourceScanRequestState.Completed).ToArrayAsync(ct).ConfigureAwait(false);
            var jobs = await db.SourceScanJobs.Where(x => x.SourceScanRequest.RequestKind == RequestKind &&
                x.SourceScanRequest.State != (int)SourceScanRequestState.Completed && x.State != (int)SourceScanJobState.Completed)
                .ToArrayAsync(ct).ConfigureAwait(false);
            var changedRequests = 0;
            foreach (var request in requests)
            {
                if (request.State == (int)SourceScanRequestState.Failed && !request.IsReleased) continue;
                request.State = (int)SourceScanRequestState.Failed;
                request.IsReleased = false;
                changedRequests++;
            }
            var changedJobs = 0;
            foreach (var job in jobs)
            {
                if (job.State == (int)SourceScanJobState.Failed && job.Reason == FeatureOffReason &&
                    job.LeaseOwner is null && job.LeaseExpiresAtUtc is null) continue;
                job.State = (int)SourceScanJobState.Failed;
                job.Reason = FeatureOffReason;
                job.LeaseGeneration = checked(job.LeaseGeneration + 1);
                job.LeaseOwner = null; job.LeaseExpiresAtUtc = null; job.UpdatedAtUtc = now;
                changedJobs++;
            }
            result = new(runs.Length, changedRequests, changedJobs);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            // A failed final guard rolls this transaction back, including audit/state changes.
            await requireStoppedWriters(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return result;
    }
}
