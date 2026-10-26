using FluxKnowledge.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceVerificationStore
{
    public async ValueTask FinishBatchAsync(SourceVerificationBatch batch, long sourceBytesRead, long retainedBytesRead, string? reason, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceBytesRead);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedBytesRead);
        await _transactions.ExecuteAsync(async db =>
        {
            var root = await RequireOwnedAsync(db, batch.Root, batch.Request, ct).ConfigureAwait(false);
            var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id, ct).ConfigureAwait(false);
            var run = await db.SourceVerificationRuns.SingleAsync(x => x.Id == batch.RunId, ct).ConfigureAwait(false);
            var request = await db.SourceScanRequests.SingleAsync(x => x.Id == batch.Request.Id.Value, ct).ConfigureAwait(false);
            var old = RequireBatchProgress(request, batch, sourceBytesRead, retainedBytesRead);
            if (old.Completed) return;
            SaveBatchProgress(run, request, old, sourceBytesRead, retainedBytesRead, completed: true);
            var now = time.GetUtcNow();
            if (!Matches(root, state, batch.Authority))
            { await InvalidateRunAsync(db, run, now, ct, request.Id).ConfigureAwait(false); return; }
            var counts = await db.SourceVerificationItems.Where(x => x.RunId == run.Id).GroupBy(x => x.State)
                .Select(group => new { State = group.Key, Count = group.Count() }).ToDictionaryAsync(x => x.State, x => x.Count, ct).ConfigureAwait(false);
            if (counts.GetValueOrDefault("Pending") + counts.GetValueOrDefault("AwaitingCapture") == 0)
            {
                var clean = counts.GetValueOrDefault("Deferred") == 0 && batch.Authority.CompleteNamespace && state.NamespaceComplete &&
                    state.DeferredEvidenceJson is null or "[]" && !await db.SourcePendingChanges.AnyAsync(x => x.SourceRootId == root.Id, ct).ConfigureAwait(false);
                run.State = clean ? "Verified" : "CompletedWithIssues"; run.Active = false; run.CompletedAtUtc = now;
                run.NextBatchAtUtc = null; run.Reason = clean ? null : "verification-debt";
                if (clean) state.LastVerifiedAtUtc = now;
            }
            else
            {
                run.State = now >= run.CreatedAtUtc.AddDays(7) ? "Overdue" : "Pending";
                run.Reason = reason ?? "batch-limit"; run.NextBatchAtUtc = now.AddSeconds(5);
                if (!run.Manual)
                {
                    var policy = ReadPolicy(state);
                    var window = FluxKnowledge.Domain.Sources.SourceVerificationSchedule.CurrentOrNextWindow(policy,
                        run.NextBatchAtUtc.Value, TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId));
                    if (!window.Contains(run.NextBatchAtUtc.Value)) run.NextBatchAtUtc = window.Start;
                }
            }
        }, ct).ConfigureAwait(false);
    }
}
