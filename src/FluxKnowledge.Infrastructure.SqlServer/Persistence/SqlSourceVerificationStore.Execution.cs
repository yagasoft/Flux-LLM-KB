using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSourceVerificationStore
{
    public async ValueTask<SourceVerificationBatch?> PrepareAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken ct)
    {
        if (!options.Enabled) throw new InvalidOperationException("SourceVerificationFeatureDisabled");
        SourceVerificationAuthority? admission = null;
        await _transactions.ExecuteAsync(async db =>
        {
            admission = null;
            var current = await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
            var run = await db.SourceVerificationRuns.SingleAsync(x => x.Id == request.VerificationRunId, ct).ConfigureAwait(false);
            if (!run.Active) return;
            var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == current.Id, ct).ConfigureAwait(false);
            var frozen = JsonSerializer.Deserialize<SourceVerificationAuthority>(run.AuthorityJson) ?? throw new InvalidDataException("SourceVerificationAuthorityMissing");
            if (!Matches(current, state, frozen) || !await db.SourceVolumeJournalCheckpoints.AnyAsync(x =>
                x.VolumeIdentity == frozen.VolumeIdentity && x.JournalId == frozen.JournalId, ct).ConfigureAwait(false))
            { await InvalidateRunAsync(db, run, time.GetUtcNow(), ct).ConfigureAwait(false); return; }
            admission = frozen;
        }, ct).ConfigureAwait(false);
        if (admission is null) return null;
        var barrier = await (incrementalCoordinator ?? throw new InvalidOperationException("SourceVerificationJournalNotConfigured"))
            .ReadVerificationBarrierAsync(admission.VolumeIdentity, admission.JournalId, ct).ConfigureAwait(false);
        SourceVerificationBatch? batch = null;
        await _transactions.ExecuteAsync(async db =>
        {
            batch = null;
            var current = await RequireOwnedAsync(db, root, request, ct).ConfigureAwait(false);
            var run = await db.SourceVerificationRuns.SingleAsync(x => x.Id == request.VerificationRunId, ct).ConfigureAwait(false);
            if (!run.Active) return;
            var state = await db.SourceRootScanStates.SingleAsync(x => x.SourceRootId == root.Id.Value, ct).ConfigureAwait(false);
            var authority = JsonSerializer.Deserialize<SourceVerificationAuthority>(run.AuthorityJson) ?? throw new InvalidDataException("SourceVerificationAuthorityMissing");
            if (authority != admission || !Matches(current, state, authority))
            { await InvalidateRunAsync(db, run, time.GetUtcNow(), ct).ConfigureAwait(false); return; }
            var policy = ReadPolicy(state);
            var now = time.GetUtcNow();
            var window = SourceVerificationSchedule.CurrentOrNextWindow(policy, now, TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId));
            var end = run.Manual ? DateTimeOffset.MaxValue : policy.Enabled && window.Contains(now) ? window.End : now;
            var rows = await db.SourceVerificationItems.Where(x => x.RunId == run.Id && x.State == "Pending")
                .OrderBy(x => x.AttemptedAtUtc).ThenBy(x => x.Id).Take(100).ToArrayAsync(ct).ConfigureAwait(false);
            if (rows.Length < 100)
            {
                var awaiting = await db.SourceVerificationItems.Where(x => x.RunId == run.Id && x.State == "AwaitingCapture")
                    .OrderBy(x => x.AttemptedAtUtc).ThenBy(x => x.Id).Take(100 - rows.Length).ToArrayAsync(ct).ConfigureAwait(false);
                rows = [.. rows, .. awaiting];
            }
            await ResolveCandidatesAsync(db, current, state, run, authority, rows, barrier, time.GetUtcNow(), ct).ConfigureAwait(false);
            var items = rows.Where(x => x.State == "Pending").Select(row => new SourceVerificationItem(row.Id,
                JsonSerializer.Deserialize<SourceVerificationBinding>(row.CandidateBindingJson ?? row.BindingJson)
                    ?? throw new InvalidDataException("SourceVerificationBindingMissing"))).ToArray();
            var hashes = items.Select(x => x.Binding.Sha256).Where(x => x is not null).Distinct().ToArray();
            // Only one historical receipt per relevant blob; do not reload the growing cycle on every batch.
            var prior = await db.SourceVerificationItems.Where(x => x.RunId == run.Id &&
                    (x.State == "Verified" || x.State == "CapturedSuccessor") && hashes.Contains(x.ReceiptSha256) && x.ReceiptJson != null)
                .GroupBy(x => new { x.ReceiptSha256, x.ReceiptByteLength })
                .Select(group => group.OrderByDescending(x => x.CompletedAtUtc).ThenByDescending(x => x.Id).Select(x => x.ReceiptJson!).First())
                .ToArrayAsync(ct).ConfigureAwait(false);
            var attemptId = Guid.NewGuid();
            var requestRow = await db.SourceScanRequests.SingleAsync(x => x.Id == request.Id.Value, ct).ConfigureAwait(false);
            if (HasUnfinishedAttempt(requestRow.AuditEvidenceJson)) run.ReadCountsIncomplete = true;
            requestRow.AuditEvidenceJson = JsonSerializer.Serialize(new BatchStatistics(attemptId, 0, 0));
            run.State = "Running";
            batch = new(run.Id, root, request, authority, policy, state.VerificationPolicyRevision, end,
                items,
                prior.Select(json => JsonSerializer.Deserialize<SourceVerificationReceipt>(json)
                    ?? throw new InvalidDataException("SourceVerificationReceiptInvalid")).ToArray(), run.CreatedAtUtc, attemptId);
        }, ct).ConfigureAwait(false);
        return batch;
    }

    public async ValueTask<bool> MayReadAsync(SourceVerificationBatch batch, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!options.Enabled || now >= batch.WindowEndUtc || batch.Request.Lease is not { } lease) return false;
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.SourceScanJobs.AsNoTracking().AnyAsync(job => job.Id == lease.JobId &&
            job.SourceScanRequestId == batch.Request.Id.Value && job.LeaseOwner == lease.Owner && job.LeaseGeneration == lease.Generation &&
            job.State == (int)SourceScanJobState.Running && job.LeaseExpiresAtUtc > now &&
            job.SourceScanRequest.State == (int)SourceScanRequestState.Running && job.SourceScanRequest.VerificationRunId == batch.RunId &&
            job.SourceScanRequest.SourceRoot.State == (int)SourceRootState.Enabled &&
            job.SourceScanRequest.SourceRoot.ConfigurationRevision == batch.Authority.ConfigurationRevision &&
            job.SourceScanRequest.SourceRoot.LastScanCompletedAtUtc != null &&
            job.SourceScanRequest.SourceRoot.LastScanCompletedAtUtc.Value.AddSeconds(job.SourceScanRequest.SourceRoot.ReconciliationCadenceSeconds) > now &&
            db.SourceRootScanStates.Any(state => state.SourceRootId == batch.Root.Id.Value && !state.VerificationPaused && !state.RecoveryRequired &&
                state.BaselineEpoch == batch.Authority.BaselineEpoch && state.RecoveryGeneration == batch.Authority.RecoveryGeneration &&
                state.VerificationPolicyRevision == batch.PolicyRevision) &&
            db.SourceVerificationRuns.Any(run => run.Id == batch.RunId && run.Active && (run.NextBatchAtUtc == null || run.NextBatchAtUtc <= now)) &&
            !db.SourcePendingChanges.Any(pending =>
                (pending.NotBeforeUtc == null || pending.NotBeforeUtc <= now ||
                 pending.ReasonFlags != SourcePendingReasons.NamespaceMetadata && (pending.RetryGeneration == null || pending.RetryGeneration != pending.Generation)) &&
                db.SourceRootConfigurations.Any(root => root.Id == pending.SourceRootId && root.State == (int)SourceRootState.Enabled)) &&
            !db.SourceScanRequests.Any(other => other.RequestKind != RequestKind && other.IsReleased && other.SourceRoot.State == (int)SourceRootState.Enabled &&
                (other.State == (int)SourceScanRequestState.Released || other.State == (int)SourceScanRequestState.Running)), ct).ConfigureAwait(false);
    }

    private async Task<SourceRootConfigurationEntity> RequireOwnedAsync(FluxKnowledgeDbContext db, SourceRootConfiguration root,
        SourceScanRequest request, CancellationToken ct)
    {
        var current = await LockRootAsync(db, root.Id.Value, ct).ConfigureAwait(false);
        if (!request.IsVerification || request.VerificationRunId is null || request.Lease is null ||
            current.State != (int)SourceRootState.Enabled || current.ConfigurationRevision != root.ConfigurationRevision ||
            current.CrawlMode != (int)root.DiscoveryMode ||
            !await SqlSourceScanStore.OwnsRootScanAsync(db, root.Id.Value, new(request.Id, request.Lease), time.GetUtcNow(), ct).ConfigureAwait(false) ||
            !await db.SourceScanRequests.AnyAsync(x => x.Id == request.Id.Value && x.RequestKind == RequestKind && x.VerificationRunId == request.VerificationRunId, ct).ConfigureAwait(false))
            throw new SourceSnapshotChangedException("SourceVerificationOwnershipLost");
        return current;
    }

    private static bool Matches(SourceRootConfigurationEntity root, SourceRootScanStateEntity state, SourceVerificationAuthority authority) =>
        authority.Version == 1 && root.ConfigurationRevision == authority.ConfigurationRevision &&
        SqlSourceScanStore.ParseAdmissionIdentityFingerprint(root.HealthEvidenceJson) == authority.RootIdentity &&
        state.BaselineEpoch == authority.BaselineEpoch && state.RecoveryGeneration == authority.RecoveryGeneration &&
        state.PolicyFingerprint == authority.PolicyFingerprint && state.VolumeIdentity == authority.VolumeIdentity &&
        !SqlSourceIncrementalState.RequiresBaseline(state) && state.Mode != "Legacy";
}
