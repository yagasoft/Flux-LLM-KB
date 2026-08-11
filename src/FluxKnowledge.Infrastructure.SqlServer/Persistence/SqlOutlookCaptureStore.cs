using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>
/// SQL-only control plane for Outlook capture. Private COM identifiers never leave this class or its entities.
/// It deliberately does not construct Outlook, access the spool, or activate a processor.
/// </summary>
public sealed class SqlOutlookCaptureStore(IDbContextFactory<FluxKnowledgeDbContext> contextFactory, TimeProvider? timeProvider = null)
    : IOutlookCaptureStore, IOutlookCaptureRecoveryStore
{
    private const int MaximumRecoveryBatchSize = 100;
    private readonly IDbContextFactory<FluxKnowledgeDbContext> _contextFactory = contextFactory;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public ValueTask<OutlookOperationReceipt> SaveProfileAsync(OutlookProfileSaveRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        return MutateAsync("save-profile", request.OperationId, request.RequestFingerprint, request.ProfileId?.Value, async context =>
        {
            var now = _clock.GetUtcNow();
            OutlookCaptureProfileEntity row;
            if (request.ProfileId is null)
            {
                var sourceRootId = Guid.NewGuid();
                context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
                {
                    Id = sourceRootId,
                    CanonicalPath = $"C:\\.fluxknowledge-private\\outlook\\{sourceRootId:N}",
                    DisplayName = "Private Outlook capture",
                    State = (int)SourceRootState.Paused,
                    Recursive = false,
                    IncludePatternsJson = "[]",
                    ExcludePatternsJson = "[]",
                    FollowLinks = false,
                    MaximumFileBytes = 64L * 1024 * 1024,
                    AllowedClassificationsJson = "[]",
                    CrawlMode = 0,
                    ReconciliationCadenceSeconds = 86400,
                    ConfigurationRevision = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                row = new OutlookCaptureProfileEntity
                {
                    Id = Guid.NewGuid(),
                    SourceRootId = sourceRootId,
                    CreatedAtUtc = now,
                    ConfigurationRevision = 1
                };
                context.OutlookCaptureProfiles.Add(row);
            }
            else
            {
                var existing = await context.OutlookCaptureProfiles.SingleOrDefaultAsync(x => x.Id == request.ProfileId.Value, cancellationToken).ConfigureAwait(false);
                if (existing is null || existing.ConfigurationRevision != request.ExpectedConfigurationRevision)
                {
                    return (false, (Guid?)null);
                }
                var existingSpoolRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(existing.SpoolRoot));
                var requestedSpoolRoot = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(request.SpoolValidation.PrivateSpoolRoot!));
                if (!string.Equals(existingSpoolRoot, requestedSpoolRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, (Guid?)null);
                }
                row = existing;
                if (request.Enable)
                {
                    var browseCorrelationId = request.BrowseCorrelationId!.Value;
                    var hasCurrentBrowse = await context.OutlookBrowseRequests.AnyAsync(
                        browse => browse.ProfileId == row.Id &&
                            browse.CorrelationId == browseCorrelationId &&
                            browse.ConfigurationRevision == row.ConfigurationRevision &&
                            browse.State == 2 &&
                            context.OutlookBrowseResults.Any(result => result.BrowseRequestId == browse.Id),
                        cancellationToken).ConfigureAwait(false);
                    if (!hasCurrentBrowse)
                    {
                        return (false, (Guid?)null);
                    }
                }
                row.ConfigurationRevision++;
            }
            row.DisplayName = request.DisplayName; row.IncrementalBasis = (int)request.IncrementalBasis; row.CadenceTicks = request.Schedule.Cadence.Ticks;
            row.MaximumOverlapTicks = request.Schedule.MaximumOverlap.Ticks; row.SpoolRoot = request.SpoolValidation.PrivateSpoolRoot!;
            row.IsEnabled = request.Enable; row.State = (int)(request.Enable ? OutlookCaptureState.AwaitingHost : OutlookCaptureState.Disabled); row.UpdatedAtUtc = now;
            if (request.ProfileId is not null)
            {
                var folders = await context.OutlookCaptureFolders
                    .Where(folder => folder.ProfileId == row.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var folder in folders)
                {
                    folder.Basis = (int)request.IncrementalBasis;
                }
            }
            return (true, row.Id);
        }, cancellationToken);
    }

    public ValueTask<OutlookOperationReceipt> PauseProfileAsync(OutlookProfilePauseRequest request, CancellationToken cancellationToken) { request.Validate(); return SetProfileStateAsync("pause-profile", request.OperationId, request.RequestFingerprint, request.ProfileId.Value, OutlookCaptureState.Disabled, cancellationToken); }
    public ValueTask<OutlookOperationReceipt> RemoveProfileAsync(OutlookProfileRemoveRequest request, CancellationToken cancellationToken) { request.Validate(); return SetProfileStateAsync("remove-profile", request.OperationId, request.RequestFingerprint, request.ProfileId.Value, OutlookCaptureState.Stale, cancellationToken); }

    public ValueTask<OutlookOperationReceipt> RecordHintAsync(OutlookHintRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        return RequestCatchUpAsync(new OutlookCatchUpRequest(request.OperationId, request.RequestFingerprint, request.ProfileId, request.CoalescingKey, OutlookCatchUpProvenance.Hint, request.Reason), cancellationToken);
    }

    public ValueTask<OutlookOperationReceipt> RequestCatchUpAsync(OutlookCatchUpRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        return MutateAsync("request-catchup", request.OperationId, request.RequestFingerprint, request.ProfileId.Value, async context =>
        {
            var profile = await context.OutlookCaptureProfiles.SingleOrDefaultAsync(x => x.Id == request.ProfileId.Value, cancellationToken).ConfigureAwait(false);
            if (profile is null || !profile.IsEnabled || profile.State == (int)OutlookCaptureState.Disabled) return (false, (Guid?)null);
            var existing = await context.OutlookCatchUps.SingleOrDefaultAsync(x => x.ProfileId == request.ProfileId.Value && x.CoalescingKey == request.CoalescingKey && (x.State == 0 || x.State == 1), cancellationToken).ConfigureAwait(false);
            if (existing is not null) return (true, (Guid?)existing.Id);
            var catchUp = new OutlookCatchUpEntity { Id = Guid.NewGuid(), ProfileId = request.ProfileId.Value, CoalescingKey = request.CoalescingKey, Provenance = (int)request.Provenance, State = 0, Reason = request.Reason };
            context.OutlookCatchUps.Add(catchUp);
            return (true, (Guid?)catchUp.Id);
        }, cancellationToken);
    }

    public async ValueTask<OutlookCatchUpClaimReceipt> ClaimCatchUpAsync(OutlookCatchUpClaimRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        var operation = await MutateAsync("claim-catchup", request.OperationId, request.RequestFingerprint, null, async context =>
        {
            var now = _clock.GetUtcNow(); var row = await context.OutlookCatchUps.OrderBy(x => x.Id).FirstOrDefaultAsync(x => x.State == 0 && (x.NotBeforeUtc == null || x.NotBeforeUtc <= now), cancellationToken).ConfigureAwait(false);
            if (row is null) return (false, (Guid?)null);
            row.State = 1; row.FencingToken++; row.LeaseOwner = Owner(request.Host); row.LastHeartbeatAtUtc = now; row.LeaseExpiresAtUtc = now.Add(request.LeaseDuration); return (true, row.Id);
        }, cancellationToken);
        if (!operation.Accepted) return new OutlookCatchUpClaimReceipt(null, false, operation.Committed, operation.IsReplay);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false); var resourceId = await context.OutlookCaptureOperations.Where(x => x.OperationId == request.OperationId).Select(x => x.ResourceId).SingleAsync(cancellationToken).ConfigureAwait(false); var row = await context.OutlookCatchUps.SingleAsync(x => x.Id == resourceId, cancellationToken).ConfigureAwait(false);
        return new OutlookCatchUpClaimReceipt(ToClaim(row), true, true, operation.IsReplay);
    }

    public ValueTask<OutlookOperationReceipt> RenewCatchUpLeaseAsync(OutlookCatchUpLeaseRenewalRequest request, CancellationToken cancellationToken) { request.Validate(); return CatchUpMutationAsync("renew-catchup", request.OperationId, request.RequestFingerprint, request.Claim, row => { row.LeaseExpiresAtUtc = request.RenewedLeaseExpiresAtUtc; row.LastHeartbeatAtUtc = _clock.GetUtcNow(); }, cancellationToken); }
    public ValueTask<OutlookOperationReceipt> CompleteCatchUpAsync(OutlookCatchUpCompletionRequest request, CancellationToken cancellationToken) { request.Validate(); return CatchUpMutationAsync("complete-catchup", request.OperationId, request.RequestFingerprint, request.Claim, row => row.State = 2, cancellationToken); }
    public ValueTask<OutlookOperationReceipt> FailCatchUpAsync(OutlookCatchUpFailureRequest request, CancellationToken cancellationToken) { request.Validate(); return CatchUpMutationAsync("fail-catchup", request.OperationId, request.RequestFingerprint, request.Claim, row => { row.State = 3; row.Reason = request.FailureReason.ToString(); }, cancellationToken); }
    public ValueTask<OutlookOperationReceipt> RequeueCatchUpAsync(OutlookCatchUpRequeueRequest request, CancellationToken cancellationToken) { request.Validate(); return CatchUpMutationAsync("requeue-catchup", request.OperationId, request.RequestFingerprint, request.Claim, row => { row.State = 0; row.RetryCount++; row.Reason = request.RetryReason.ToString(); row.NotBeforeUtc = request.NotBeforeUtc; }, cancellationToken); }

    public ValueTask<OutlookOperationReceipt> ReleaseStaleCatchUpLeaseAsync(OutlookStaleCatchUpLeaseReleaseRequest request, CancellationToken cancellationToken)
    {
        request.Validate(_clock.GetUtcNow());
        return MutateAsync("release-stale-catchup", request.OperationId, request.RequestFingerprint, request.ProfileId.Value, async context =>
        {
            var row = await context.OutlookCatchUps.SingleOrDefaultAsync(x =>
                x.Id == request.CatchUpId &&
                x.ProfileId == request.ProfileId.Value &&
                x.FencingToken == request.FencingToken &&
                x.LeaseExpiresAtUtc == request.LeaseExpiresAtUtc &&
                x.State == 1, cancellationToken).ConfigureAwait(false);
            if (row is null || row.LeaseExpiresAtUtc > _clock.GetUtcNow()) return (false, (Guid?)null);
            row.State = 0; row.LeaseOwner = null; row.LeaseExpiresAtUtc = null; return (true, row.Id);
        }, cancellationToken);
    }

    public async ValueTask<OutlookCaptureRecoverySnapshot> ReadRecoverySnapshotAsync(
        DateTimeOffset staleBeforeUtc,
        DateTimeOffset pendingHintBeforeUtc,
        CancellationToken cancellationToken)
    {
        if (staleBeforeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("UTC time required.", nameof(staleBeforeUtc));
        }
        if (pendingHintBeforeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("UTC time required.", nameof(pendingHintBeforeUtc));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var staleRows = await context.OutlookCatchUps
            .AsNoTracking()
            .Where(row => row.State == 1 && row.LeaseExpiresAtUtc != null && row.LeaseExpiresAtUtc <= staleBeforeUtc)
            .OrderBy(row => row.LeaseExpiresAtUtc)
            .ThenBy(row => row.Id)
            .Take(MaximumRecoveryBatchSize)
            .Select(row => new
            {
                row.Id,
                row.ProfileId,
                row.FencingToken,
                LeaseExpiresAtUtc = row.LeaseExpiresAtUtc!.Value
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hintRows = await context.OutlookCatchUps
            .AsNoTracking()
            .Where(row =>
                row.State == 0 &&
                row.Provenance == (int)OutlookCatchUpProvenance.Hint)
            .Select(row => new
            {
                row.Id,
                row.ProfileId,
                row.CoalescingKey,
                row.Reason,
                OperationId = context.OutlookCaptureOperations
                    .Where(operation =>
                        operation.Kind == "request-catchup" &&
                        operation.Accepted &&
                        operation.ResourceId == row.Id &&
                        operation.CompletedAtUtc <= pendingHintBeforeUtc)
                    .OrderBy(operation => operation.CompletedAtUtc)
                    .ThenBy(operation => operation.Id)
                    .Select(operation => (Guid?)operation.OperationId)
                    .FirstOrDefault(),
                RecordedAtUtc = context.OutlookCaptureOperations
                    .Where(operation =>
                        operation.Kind == "request-catchup" &&
                        operation.Accepted &&
                        operation.ResourceId == row.Id &&
                        operation.CompletedAtUtc <= pendingHintBeforeUtc)
                    .OrderBy(operation => operation.CompletedAtUtc)
                    .ThenBy(operation => operation.Id)
                    .Select(operation => (DateTimeOffset?)operation.CompletedAtUtc)
                    .FirstOrDefault()
            })
            .Where(row => row.OperationId != null && row.RecordedAtUtc != null)
            .OrderBy(row => row.RecordedAtUtc)
            .ThenBy(row => row.OperationId)
            .Take(MaximumRecoveryBatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hintOperationIds = hintRows.Select(row => row.OperationId!.Value).ToArray();
        var operationById = hintOperationIds.Length == 0
            ? []
            : await context.OutlookCaptureOperations
                .AsNoTracking()
                .Where(operation => hintOperationIds.Contains(operation.OperationId))
                .ToDictionaryAsync(operation => operation.OperationId, cancellationToken)
                .ConfigureAwait(false);

        var leases = staleRows
            .Select(row => new OutlookCatchUpLeaseRecoveryCandidate(
                row.Id,
                new OutlookCaptureProfileId(row.ProfileId),
                row.FencingToken,
                row.LeaseExpiresAtUtc))
            .ToArray();
        var hints = hintRows
            .Where(row => operationById.ContainsKey(row.OperationId!.Value))
            .Select(row =>
            {
                var operation = operationById[row.OperationId!.Value];
                return new OutlookHintRecoveryCandidate(
                    new OutlookHintRequest(
                        operation.OperationId,
                        operation.RequestFingerprint,
                        new OutlookCaptureProfileId(row.ProfileId),
                        row.CoalescingKey,
                        row.Reason),
                    operation.CompletedAtUtc);
            })
            .ToArray();
        return new OutlookCaptureRecoverySnapshot(leases, hints);
    }

    public ValueTask<OutlookOperationReceipt> ReplayHintAsync(
        OutlookHintRequest request,
        CancellationToken cancellationToken) =>
        RecordHintAsync(request, cancellationToken);

    public ValueTask<OutlookOperationReceipt> RequestBrowseAsync(OutlookBrowseRequest request, CancellationToken cancellationToken)
    {
        request.Validate(); return MutateAsync("request-browse", request.OperationId, request.RequestFingerprint, request.ProfileId!.Value, context => { context.OutlookBrowseRequests.Add(new OutlookBrowseRequestEntity { Id = request.BrowseRequestId, ProfileId = request.ProfileId!.Value, CorrelationId = request.CorrelationId, ConfigurationRevision = request.ConfigurationRevision, ExpiresAtUtc = request.ExpiresAtUtc, State = 0 }); return Task.FromResult((true, (Guid?)request.BrowseRequestId)); }, cancellationToken);
    }

    public async ValueTask<OutlookBrowseClaimReceipt> ClaimBrowseAsync(OutlookBrowseClaimRequest request, CancellationToken cancellationToken)
    {
        request.Validate(); var receipt = await MutateAsync("claim-browse", request.OperationId, request.RequestFingerprint, null, async context =>
        { var row = await context.OutlookBrowseRequests.SingleOrDefaultAsync(x => x.Id == request.BrowseRequestId, cancellationToken).ConfigureAwait(false); if (row is null || row.State != 0 || row.ExpiresAtUtc < _clock.GetUtcNow()) return (false, (Guid?)null); row.State = 1; row.FencingToken++; row.LeaseOwner = Owner(request.Host); row.LeaseExpiresAtUtc = request.LeaseExpiresAtUtc; return (true, row.Id); }, cancellationToken);
        if (!receipt.Accepted) return new OutlookBrowseClaimReceipt(null, false, receipt.Committed, receipt.IsReplay);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false); var row = await context.OutlookBrowseRequests.SingleAsync(x => x.Id == request.BrowseRequestId, cancellationToken).ConfigureAwait(false);
        return new OutlookBrowseClaimReceipt(new OutlookBrowseClaim(row.Id, row.CorrelationId, row.ConfigurationRevision, request.Host, row.FencingToken, row.LeaseExpiresAtUtc!.Value), true, true, receipt.IsReplay);
    }

    public ValueTask<OutlookOperationReceipt> CompleteBrowseAsync(OutlookBrowseCompletionRequest request, CancellationToken cancellationToken)
    {
        request.Validate(); return MutateAsync("complete-browse", request.OperationId, request.RequestFingerprint, null, async context =>
        {
            var row = await context.OutlookBrowseRequests.SingleOrDefaultAsync(x => x.Id == request.BrowseRequestId, cancellationToken).ConfigureAwait(false);
            var profile = row is null ? null : await context.OutlookCaptureProfiles.SingleOrDefaultAsync(x => x.Id == row.ProfileId, cancellationToken).ConfigureAwait(false);
            if (request.PrivateFolders is null || !Matches(row, request.Host, request.FencingToken) || row!.ConfigurationRevision != request.ConfigurationRevision || profile!.ConfigurationRevision != row.ConfigurationRevision)
            {
                return (false, (Guid?)null);
            }

            row.State = 2;
            var configuredFolders = await context.OutlookCaptureFolders
                .Where(folder => folder.ProfileId == row.ProfileId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var selectedFolderIds = new HashSet<Guid>();
            foreach (var folder in request.PrivateFolders)
            {
                var canonical = configuredFolders.SingleOrDefault(candidate =>
                    candidate.StoreId == folder.StoreId && candidate.FolderEntryId == folder.FolderEntryId);
                if (canonical is null)
                {
                    canonical = new OutlookCaptureFolderEntity
                    {
                        Id = folder.FolderId.Value,
                        ProfileId = row.ProfileId,
                        StoreId = folder.StoreId,
                        FolderEntryId = folder.FolderEntryId
                    };
                    configuredFolders.Add(canonical);
                    context.OutlookCaptureFolders.Add(canonical);
                }
                canonical.DisplayName = folder.DisplayName;
                canonical.Basis = profile.IncrementalBasis;
                canonical.State = (int)OutlookCaptureState.Ready;
                selectedFolderIds.Add(canonical.Id);
                context.OutlookBrowseResults.Add(new OutlookBrowseResultEntity
                {
                    Id = Guid.NewGuid(),
                    BrowseRequestId = row.Id,
                    FolderId = canonical.Id,
                    DisplayName = canonical.DisplayName
                });
            }
            foreach (var folder in configuredFolders.Where(folder => !selectedFolderIds.Contains(folder.Id)))
            {
                folder.State = (int)OutlookCaptureState.Disabled;
            }
            return (true, row.Id);
        }, cancellationToken);
    }
    public ValueTask<OutlookOperationReceipt> FailBrowseAsync(OutlookBrowseFailureRequest request, CancellationToken cancellationToken)
    { request.Validate(); return MutateAsync("fail-browse", request.OperationId, request.RequestFingerprint, null, async context => { var row = await context.OutlookBrowseRequests.SingleOrDefaultAsync(x => x.Id == request.BrowseRequestId, cancellationToken).ConfigureAwait(false); if (!Matches(row, request.Host, request.FencingToken)) return (false, (Guid?)null); row!.State = 3; row.FailureCode = (int)request.FailureCode; return (true, row.Id); }, cancellationToken); }
    public ValueTask<OutlookOperationReceipt> ReleaseStaleBrowseClaimsAsync(Guid operationId, string requestFingerprint, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    { if (observedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("UTC time required.", nameof(observedAtUtc)); return MutateAsync("release-stale-browse", operationId, requestFingerprint, null, async context => { var rows = await context.OutlookBrowseRequests.Where(x => x.State == 1 && x.LeaseExpiresAtUtc <= observedAtUtc).ToListAsync(cancellationToken).ConfigureAwait(false); foreach (var row in rows) { row.State = 0; row.LeaseOwner = null; row.LeaseExpiresAtUtc = null; } return (rows.Count > 0, (Guid?)null); }, cancellationToken); }
    public async ValueTask<IReadOnlyList<OutlookProfileProjection>> ReadLocalProjectionAsync(CancellationToken cancellationToken) { await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false); return await context.OutlookCaptureProfiles.Where(x => x.State != (int)OutlookCaptureState.Stale).OrderBy(x => x.DisplayName).Select(x => new OutlookProfileProjection(new OutlookCaptureProfileId(x.Id), x.DisplayName, (OutlookCaptureState)x.State, (OutlookIncrementalBasis)x.IncrementalBasis, x.ConfigurationRevision, new OutlookCaptureSchedule(TimeSpan.FromTicks(x.CadenceTicks), TimeSpan.FromTicks(x.MaximumOverlapTicks)), x.IncrementalBasis == (int)OutlookIncrementalBasis.ReceivedTime ? new[] { OutlookConfigurationWarning.ReceivedTimeRequiresManualReconciliation } : Array.Empty<OutlookConfigurationWarning>())).ToListAsync(cancellationToken).ConfigureAwait(false); }
    public async ValueTask<IReadOnlyList<OutlookBrowseFolderProjection>> ReadBrowseResultAsync(Guid correlationId, CancellationToken cancellationToken) { await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false); return await (from result in context.OutlookBrowseResults join request in context.OutlookBrowseRequests on result.BrowseRequestId equals request.Id where request.CorrelationId == correlationId && request.State == 2 select new OutlookBrowseFolderProjection(new OutlookCaptureFolderId(result.FolderId), result.DisplayName)).ToListAsync(cancellationToken).ConfigureAwait(false); }

    private ValueTask<OutlookOperationReceipt> SetProfileStateAsync(string kind, Guid id, string fingerprint, Guid profileId, OutlookCaptureState state, CancellationToken token) => MutateAsync(kind, id, fingerprint, profileId, async context => { var row = await context.OutlookCaptureProfiles.SingleOrDefaultAsync(x => x.Id == profileId, token).ConfigureAwait(false); if (row is null) return (false, (Guid?)null); row.State = (int)state; row.IsEnabled = false; row.ConfigurationRevision++; row.UpdatedAtUtc = _clock.GetUtcNow(); return (true, profileId); }, token);
    private ValueTask<OutlookOperationReceipt> CatchUpMutationAsync(string kind, Guid id, string fingerprint, OutlookCatchUpClaim claim, Action<OutlookCatchUpEntity> mutation, CancellationToken token) => MutateAsync(kind, id, fingerprint, claim.ProfileId.Value, async context => { var row = await context.OutlookCatchUps.SingleOrDefaultAsync(x => x.Id == claim.CatchUpId && x.ProfileId == claim.ProfileId.Value && x.CoalescingKey == claim.CoalescingKey && x.FencingToken == claim.FencingToken && x.State == 1 && x.LeaseOwner == Owner(claim.LeaseOwner), token).ConfigureAwait(false); if (row is null || row.LeaseExpiresAtUtc is null || row.LeaseExpiresAtUtc < _clock.GetUtcNow()) return (false, (Guid?)null); mutation(row); return (true, row.Id); }, token);
    private async ValueTask<OutlookOperationReceipt> MutateAsync(
        string kind,
        Guid operationId,
        string fingerprint,
        Guid? profileId,
        Func<FluxKnowledgeDbContext, Task<(bool Accepted, Guid? ResourceId)>> mutate,
        CancellationToken token)
    {
        var result = await MutateDetailedAsync(
            kind,
            operationId,
            fingerprint,
            profileId,
            async context =>
            {
                var outcome = await mutate(context).ConfigureAwait(false);
                return new MutationOutcome(outcome.Accepted, outcome.ResourceId);
            },
            token).ConfigureAwait(false);
        return new OutlookOperationReceipt(operationId, result.Accepted, true, result.IsOperationReplay);
    }

    private async ValueTask<MutationReceipt> MutateDetailedAsync(
        string kind,
        Guid operationId,
        string fingerprint,
        Guid? profileId,
        Func<FluxKnowledgeDbContext, Task<MutationOutcome>> mutate,
        CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await MutateOnceAsync(
                    kind,
                    operationId,
                    fingerprint,
                    profileId,
                    mutate,
                    token).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                // A fresh context observes the durable winner or recomputes the mutation
                // against the current rowversion.
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new InvalidOperationException(
                    "The Outlook mutation could not be reconciled after concurrent durable updates.",
                    exception);
            }
            catch (DbUpdateException exception) when (attempt < 2 && IsUniqueConstraintViolation(exception))
            {
                // The durable unique index decided the race. A new context reloads that winner
                // and either replays it or records a closed conflict outcome.
            }
            catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
            {
                throw new InvalidOperationException(
                    "The Outlook mutation conflicted with an immutable durable identity.",
                    exception);
            }
        }
    }

    private async ValueTask<MutationReceipt> MutateOnceAsync(
        string kind,
        Guid operationId,
        string fingerprint,
        Guid? profileId,
        Func<FluxKnowledgeDbContext, Task<MutationOutcome>> mutate,
        CancellationToken token)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(token).ConfigureAwait(false);
        var prior = await context.OutlookCaptureOperations
            .SingleOrDefaultAsync(candidate => candidate.OperationId == operationId, token)
            .ConfigureAwait(false);
        if (prior is not null)
        {
            if (prior.Kind != kind || prior.RequestFingerprint != fingerprint || prior.ProfileId != profileId)
            {
                throw new InvalidOperationException("The Outlook operation does not match its immutable request.");
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new MutationReceipt(prior.Accepted, prior.ResourceId, IsOperationReplay: true, ResourceReused: false);
        }

        var outcome = await mutate(context).ConfigureAwait(false);
        OperatorEventAppender.Add(
            context,
            OperatorEventDraft.OutlookMutation(
                kind,
                operationId,
                outcome.Accepted,
                _clock.GetUtcNow()));
        context.OutlookCaptureOperations.Add(new OutlookCaptureOperationEntity
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            Kind = kind,
            OperationId = operationId,
            RequestFingerprint = fingerprint,
            ResourceId = outcome.ResourceId,
            Accepted = outcome.Accepted,
            CompletedAtUtc = _clock.GetUtcNow()
        });
        await context.SaveChangesAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new MutationReceipt(outcome.Accepted, outcome.ResourceId, IsOperationReplay: false, outcome.ResourceReused);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private sealed record MutationOutcome(bool Accepted, Guid? ResourceId, bool ResourceReused = false);
    private sealed record MutationReceipt(bool Accepted, Guid? ResourceId, bool IsOperationReplay, bool ResourceReused);
    private static string Owner(OutlookHostIdentity host) => $"{host.WindowsUserSid}|{host.SessionId}|{host.HostInstanceId}";
    private bool Matches(OutlookBrowseRequestEntity? row, OutlookHostIdentity host, long token) => row is not null && row.State == 1 && row.FencingToken == token && row.LeaseOwner == Owner(host) && row.LeaseExpiresAtUtc >= _clock.GetUtcNow();
    private static OutlookCatchUpClaim ToClaim(OutlookCatchUpEntity row) => new(row.Id, new OutlookCaptureProfileId(row.ProfileId), row.CoalescingKey, (OutlookCatchUpProvenance)row.Provenance, row.RetryCount, row.Reason, ParseOwner(row.LeaseOwner!), row.LeaseExpiresAtUtc!.Value, row.LastHeartbeatAtUtc!.Value, row.FencingToken);
    private static OutlookHostIdentity ParseOwner(string value) { var p = value.Split('|'); return new OutlookHostIdentity(p[0], int.Parse(p[1]), p[2]); }
}
