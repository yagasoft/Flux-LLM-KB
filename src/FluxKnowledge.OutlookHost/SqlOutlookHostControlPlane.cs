using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.OutlookHost;

/// <summary>
/// Resolves private Outlook configuration only after the SQL store has issued a fenced durable claim.
/// It performs no COM operation and projects no private identity outside this host process.
/// </summary>
internal sealed class SqlOutlookHostControlPlane(
    IOutlookCaptureStore store,
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider? timeProvider = null) : IOutlookHostControlPlane, IOutlookFolderBrowseControlPlane
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async ValueTask<OutlookHostCatchUpWork?> TryClaimCatchUpAsync(
        OutlookHostIdentity host,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var receipt = await store.ClaimCatchUpAsync(
            new OutlookCatchUpClaimRequest(
                operationId,
                Fingerprint("claim-catchup", operationId, host),
                host,
                leaseDuration),
            cancellationToken).ConfigureAwait(false);
        if (receipt.Claim is null)
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var profile = await context.OutlookCaptureProfiles.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == receipt.Claim.ProfileId.Value, cancellationToken)
            .ConfigureAwait(false);
        var isEnabled = profile is not null &&
            profile.IsEnabled &&
            profile.State != (int)OutlookCaptureState.Disabled;
        if (profile is null)
        {
            return new OutlookHostCatchUpWork(receipt.Claim, false, []);
        }

        var folderRows = await context.OutlookCaptureFolders.AsNoTracking()
            .Where(row => row.ProfileId == profile.Id && row.State != (int)OutlookCaptureState.Disabled)
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var overlap = TimeSpan.FromTicks(profile.MaximumOverlapTicks);
        var folders = folderRows.Select(row => new OutlookHostFolderConfiguration(
            new OutlookCaptureFolderId(row.Id),
            new OutlookFolderIdentity(row.StoreId, row.FolderEntryId, row.DisplayName),
            (OutlookIncrementalBasis)row.Basis,
            row.CursorUtc,
            row.CursorFingerprint,
            overlap,
            profile.SpoolRoot)).ToArray();
        return new OutlookHostCatchUpWork(receipt.Claim, isEnabled, folders);
    }

    public ValueTask RecordHintAsync(
        OutlookCaptureProfileId profileId,
        OutlookHint hint,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        return AsValueTask(store.RecordHintAsync(
            new OutlookHintRequest(
                operationId,
                Fingerprint("record-hint", operationId, profileId.Value, hint.CoalescingKey),
                profileId,
                hint.CoalescingKey),
            cancellationToken));
    }

    public async ValueTask<OutlookCatchUpClaim?> RenewCatchUpAsync(
        OutlookCatchUpClaim claim,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var renewedAt = _clock.GetUtcNow();
        var renewedExpiry = renewedAt.Add(leaseDuration);
        var receipt = await store.RenewCatchUpLeaseAsync(
            new OutlookCatchUpLeaseRenewalRequest(
                operationId,
                Fingerprint("renew-catchup", operationId, claim.CatchUpId, claim.FencingToken, renewedExpiry),
                claim,
                renewedExpiry),
            cancellationToken).ConfigureAwait(false);
        return receipt.Accepted
            ? claim with { LeaseExpiresAtUtc = renewedExpiry, LastHeartbeatAtUtc = renewedAt }
            : null;
    }

    public async ValueTask<bool> CompleteCatchUpAsync(
        OutlookCatchUpClaim claim,
        int exportedCount,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var receipt = await store.CompleteCatchUpAsync(
            new OutlookCatchUpCompletionRequest(
                operationId,
                Fingerprint("complete-catchup", operationId, claim.CatchUpId, claim.FencingToken, exportedCount),
                claim,
                exportedCount),
            cancellationToken).ConfigureAwait(false);
        return receipt.Accepted;
    }

    public ValueTask FailCatchUpAsync(
        OutlookCatchUpClaim claim,
        OutlookCatchUpFailureReason reason,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        return AsValueTask(store.FailCatchUpAsync(
            new OutlookCatchUpFailureRequest(
                operationId,
                Fingerprint("fail-catchup", operationId, claim.CatchUpId, claim.FencingToken, reason),
                claim,
                reason),
            cancellationToken));
    }

    public async ValueTask<OutlookBrowseClaim?> TryClaimBrowseAsync(
        OutlookHostIdentity host,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        var browseRequestId = await context.OutlookBrowseRequests.AsNoTracking()
            .Where(row => row.State == 0 && row.ExpiresAtUtc >= now)
            .OrderBy(row => row.Id)
            .Select(row => (Guid?)row.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (browseRequestId is null)
        {
            return null;
        }

        var operationId = Guid.NewGuid();
        var receipt = await store.ClaimBrowseAsync(
            new OutlookBrowseClaimRequest(
                operationId,
                Fingerprint("claim-browse", operationId, browseRequestId.Value, host),
                browseRequestId.Value,
                host,
                now.Add(leaseDuration)),
            cancellationToken).ConfigureAwait(false);
        return receipt.Claim;
    }

    public ValueTask CompleteBrowseAsync(
        OutlookBrowseClaim claim,
        IReadOnlyList<OutlookFolderDescriptor> folders,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var projections = folders
            .Select(folder => new OutlookBrowseFolderProjection(folder.FolderId, folder.Identity.DisplayName))
            .ToArray();
        var privateFolders = folders
            .Select(folder => new OutlookBrowseFolderResult(
                folder.FolderId,
                folder.Identity.StoreId,
                folder.Identity.FolderEntryId,
                folder.Identity.DisplayName))
            .ToArray();
        return AsValueTask(store.CompleteBrowseAsync(
            new OutlookBrowseCompletionRequest(
                operationId,
                Fingerprint("complete-browse", operationId, claim.BrowseRequestId, claim.FencingToken),
                claim.BrowseRequestId,
                claim.Host,
                claim.FencingToken,
                projections,
                claim.ConfigurationRevision,
                privateFolders),
            cancellationToken));
    }

    public ValueTask FailBrowseAsync(
        OutlookBrowseClaim claim,
        OutlookBrowseFailureCode failureCode,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        return AsValueTask(store.FailBrowseAsync(
            new OutlookBrowseFailureRequest(
                operationId,
                Fingerprint("fail-browse", operationId, claim.BrowseRequestId, claim.FencingToken, failureCode),
                claim.BrowseRequestId,
                claim.Host,
                claim.FencingToken,
                failureCode),
            cancellationToken));
    }

    private static async ValueTask AsValueTask(ValueTask<OutlookOperationReceipt> operation) =>
        _ = await operation.ConfigureAwait(false);

    private static string Fingerprint(string kind, params object[] values)
    {
        var material = string.Join('|', values.Prepend(kind));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
