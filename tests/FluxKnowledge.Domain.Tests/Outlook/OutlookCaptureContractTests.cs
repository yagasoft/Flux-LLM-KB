using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Outlook;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Outlook;

public sealed class OutlookCaptureContractTests
{
    [Fact]
    public void Folder_identity_rejects_blank_store_or_entry_ids() =>
        Assert.Throws<ArgumentException>(() => new OutlookFolderIdentity("", "folder", "Capture"));

    [Fact]
    public void Profile_defaults_to_disabled_and_last_modification_time()
    {
        var profile = OutlookCaptureProfile.Create("Inbox capture");

        Assert.Equal(OutlookCaptureState.Disabled, profile.State);
        Assert.Equal(OutlookIncrementalBasis.LastModificationTime, profile.IncrementalBasis);
    }

    [Fact]
    public void Export_identity_cannot_be_rebound_to_a_different_fingerprint()
    {
        var profileId = OutlookCaptureProfileId.New();
        var folderId = OutlookCaptureFolderId.New();
        var export = OutlookCaptureExport.Create(new OutlookExportIdentity(
            profileId.Value,
            folderId.Value,
            "entry-1",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        Assert.Throws<DomainInvariantException>(() => export.EnsureMatches(new OutlookExportIdentity(
            profileId.Value,
            folderId.Value,
            "entry-1",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")));
    }

    [Fact]
    public void Received_time_profile_requires_a_manual_reconciliation_path()
    {
        var profile = OutlookCaptureProfile.Create("Inbox capture", OutlookIncrementalBasis.ReceivedTime);

        Assert.True(profile.RequiresManualReconciliation);
    }

    [Fact]
    public void Profile_save_rejects_spool_evidence_that_is_not_writable()
    {
        var request = new OutlookProfileSaveRequest(
            Guid.NewGuid(),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            null,
            "Inbox capture",
            OutlookIncrementalBasis.LastModificationTime,
            new OutlookCaptureSchedule(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5)),
            new OutlookSpoolValidation("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true, true, true, false));

        Assert.Throws<ArgumentException>(request.Validate);
    }

    [Fact]
    public void Spool_validation_rejects_a_path_instead_of_a_non_sensitive_fingerprint()
    {
        var validation = new OutlookSpoolValidation(@"C:\operator-spool", true, true, true, true);

        Assert.Throws<ArgumentException>(validation.Validate);
    }

    [Fact]
    public void Browse_completion_requires_the_claim_fencing_token()
    {
        var completion = new OutlookBrowseCompletionRequest(
            Guid.NewGuid(),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Guid.NewGuid(),
            new OutlookHostIdentity("S-1-5-21-100", 4, "host-a"),
            0,
            [new OutlookBrowseFolderProjection(OutlookCaptureFolderId.New(), "Inbox")]);

        Assert.Throws<ArgumentOutOfRangeException>(completion.Validate);
    }

    [Fact]
    public void Catch_up_lease_rejects_a_non_stale_release()
    {
        var release = new OutlookStaleCatchUpLeaseReleaseRequest(
            Guid.NewGuid(),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Guid.NewGuid(),
            OutlookCaptureProfileId.New(),
            3,
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Throws<ArgumentException>(() => release.Validate(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Catch_up_claim_returns_durable_receipt_evidence()
    {
        var method = typeof(IOutlookCaptureStore).GetMethod(
            nameof(IOutlookCaptureStore.ClaimCatchUpAsync),
            [typeof(OutlookCatchUpClaimRequest), typeof(CancellationToken)]);

        Assert.Equal(typeof(ValueTask<OutlookCatchUpClaimReceipt>), method!.ReturnType);
    }

    [Fact]
    public void Browse_claim_returns_durable_receipt_evidence()
    {
        var method = typeof(IOutlookCaptureStore).GetMethod(
            nameof(IOutlookCaptureStore.ClaimBrowseAsync),
            [typeof(OutlookBrowseClaimRequest), typeof(CancellationToken)]);

        Assert.Equal(typeof(ValueTask<OutlookBrowseClaimReceipt>), method!.ReturnType);
    }
}
