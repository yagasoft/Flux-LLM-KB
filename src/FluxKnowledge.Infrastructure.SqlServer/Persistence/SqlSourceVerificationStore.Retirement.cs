using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed record SourceVerificationRetirementResult(int Runs, int Requests, int Jobs);
public sealed record SourceMetadataPolicyTransitionResult(string Policy, int Roots, SourceVerificationRetirementResult Verification);

public sealed partial class SqlSourceVerificationStore
{
    public const string FeatureOffReason = "source-verification-feature-disabled";
    public const string PolicyRetiredReason = "source-verification-policy-retired";

    public ValueTask<SourceMetadataPolicyTransitionResult> RetireForMetadataPolicyAsync(ISourceVerificationReaderSlot slot,
        Func<CancellationToken, ValueTask> requireStoppedWriters, CancellationToken ct) =>
        RetireAsync(slot, requireStoppedWriters, metadataPolicy: true, ct);

    /// <summary>Explicit stopped-deployment operation, never called by a hosted service.</summary>
    public async ValueTask<SourceVerificationRetirementResult> RetireForFeatureOffAsync(ISourceVerificationReaderSlot slot,
        Func<CancellationToken, ValueTask> requireStoppedWriters, CancellationToken ct)
        => (await RetireAsync(slot, requireStoppedWriters, metadataPolicy: false, ct).ConfigureAwait(false)).Verification;

    private async ValueTask<SourceMetadataPolicyTransitionResult> RetireAsync(ISourceVerificationReaderSlot slot,
        Func<CancellationToken, ValueTask> requireStoppedWriters, bool metadataPolicy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(requireStoppedWriters);
        if (!metadataPolicy && options.Enabled) throw new InvalidOperationException("SourceVerificationRetirementRequiresFeatureOff");
        using var readerSlot = slot.TryAcquire() ?? throw new InvalidOperationException("SourceVerificationReaderStillOwned");
        // The physical slot proves native verification readers have unwound. The caller
        // separately binds the retained hold and stopped-writer evidence to this database/runtime.
        await requireStoppedWriters(ct).ConfigureAwait(false);
        var reason = metadataPolicy ? PolicyRetiredReason : FeatureOffReason;
        var result = new SourceMetadataPolicyTransitionResult(SourcePolicyDefaults.DetectionPolicy, 0, new(0, 0, 0));
        await _transactions.ExecuteAsync(async db =>
        {
            await SqlSourceScanStore.LockControlRangesAsync(db, ct, includeAllRoots: true).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var runs = await db.SourceVerificationRuns.Where(x => x.Active).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var run in runs)
            {
                await InvalidateRunAsync(db, run, now, ct, reason: reason).ConfigureAwait(false);
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
                if (job.State == (int)SourceScanJobState.Failed && job.Reason == reason &&
                    job.LeaseOwner is null && job.LeaseExpiresAtUtc is null) continue;
                job.State = (int)SourceScanJobState.Failed;
                job.Reason = reason;
                job.LeaseGeneration = checked(job.LeaseGeneration + 1);
                job.LeaseOwner = null; job.LeaseExpiresAtUtc = null; job.UpdatedAtUtc = now;
                changedJobs++;
            }
            var transitioned = 0;
            if (metadataPolicy)
            {
                var roots = await db.SourceRootConfigurations.Where(x => x.State != (int)SourceRootState.Deleting)
                    .OrderBy(x => x.Id).ToArrayAsync(ct).ConfigureAwait(false);
                const string auditType = "source.policy.events-weekly-metadata-v1";
                foreach (var root in roots)
                {
                    if (await db.AuditEvents.AnyAsync(x => x.SourceRootId == root.Id && x.EventType == auditType, ct).ConfigureAwait(false)) continue;
                    var state = await db.SourceRootScanStates.SingleOrDefaultAsync(x => x.SourceRootId == root.Id, ct).ConfigureAwait(false);
                    var debt = await db.SourcePendingChanges.Where(x => x.SourceRootId == root.Id && x.RelativePath == ".").ToArrayAsync(ct).ConfigureAwait(false);
                    var manifests = await db.SourceScanRequests.AsNoTracking().Where(x => x.SourceRootId == root.Id &&
                        x.RequestKind != RequestKind && x.IncrementalManifestJson != null &&
                        x.State != (int)SourceScanRequestState.Completed).Select(x => new { x.Id, x.IncrementalManifestJson }).ToArrayAsync(ct).ConfigureAwait(false);
                    // Keep the exact prior obligation and frozen targets as evidence. Specific
                    // path debt is untouched; old manifests carry it into their next owned plan.
                    var evidence = JsonSerializer.Serialize(new { policy = SourcePolicyDefaults.DetectionPolicy,
                        previousCadenceSeconds = root.ReconciliationCadenceSeconds, root.ConfigurationRevision,
                        previousState = state, previousRootDebt = debt, previousManifests = manifests,
                        verificationReason = reason, newCadenceSeconds = SourcePolicyDefaults.MetadataReconciliationSeconds });
                    root.ReconciliationCadenceSeconds = SourcePolicyDefaults.MetadataReconciliationSeconds;
                    root.ConfigurationRevision++;
                    root.UpdatedAtUtc = now;
                    root.HealthEvidenceJson = SourceRootControlAuditEvidence.UpdateConfigurationFingerprint(root.HealthEvidenceJson,
                        SourceRootControlConfiguration.From(root));
                    if (state is not null)
                    {
                        state.RecoveryGeneration++; state.NamespaceGeneration++;
                        state.RecoveryInProgress = false;
                        state.Reason = "metadata-policy-transition";
                        state.PolicyFingerprint = SqlRepositoryWorkRecovery.Policy(root);
                    }
                    foreach (var item in debt)
                        item.ReasonFlags = (item.ReasonFlags & ~SourcePendingReasons.FullRecapture) | SourcePendingReasons.NamespaceMetadata;
                    db.AuditEvents.Add(new AuditEventEntity { SourceRootId = root.Id, EventType = auditType,
                        EventFamily = "source-control", Severity = "information", Actor = "stopped-deployment",
                        OccurredAtUtc = now, DetailsJson = evidence });
                    transitioned++;
                }
            }
            result = new(SourcePolicyDefaults.DetectionPolicy, transitioned, new(runs.Length, changedRequests, changedJobs));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            // A failed final guard rolls this transaction back, including audit/state changes.
            await requireStoppedWriters(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return result;
    }
}
