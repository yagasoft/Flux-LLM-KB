using System.Net;
using System.Security.Claims;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web.Components.Outlook;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class OutlookPageStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Page_rejects_save_when_the_folder_browse_result_is_stale()
    {
        var store = new RecordingStore
        {
            BrowseFolders = [new OutlookBrowseFolderProjection(new OutlookCaptureFolderId(Guid.NewGuid()), "Inbox")]
        };
        var state = CreateState(store);
        var draft = Draft(enable: true);

        await state.RequestBrowseAsync(draft, CancellationToken.None);
        await state.RefreshBrowseResultAsync(draft, CancellationToken.None);
        var changedDraft = draft with { DisplayName = "Changed mailbox" };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => state.SaveAsync(changedDraft, CancellationToken.None).AsTask());

        Assert.Contains("current folder browse result", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Page_creates_only_a_durable_browse_request_and_never_invokes_a_host()
    {
        var store = new RecordingStore();
        var state = CreateState(store);
        var draft = Draft(enable: false);

        var request = await state.RequestBrowseAsync(draft, CancellationToken.None);

        Assert.Equal(draft.ProfileId, request.ProfileId);
        Assert.Equal(draft.ConfigurationRevision, request.ConfigurationRevision);
        Assert.Equal(1, store.BrowseRequestCount);
        Assert.True(store.LastBrowseReceipt?.Committed);
    }

    [Fact]
    public async Task Enabled_edit_binds_save_to_the_expected_revision_and_completed_browse()
    {
        var store = new RecordingStore
        {
            BrowseFolders = [new OutlookBrowseFolderProjection(new OutlookCaptureFolderId(Guid.NewGuid()), "Inbox")]
        };
        var state = CreateState(store);
        var draft = Draft(enable: true);

        var browse = await state.RequestBrowseAsync(draft, CancellationToken.None);
        await state.RefreshBrowseResultAsync(draft, CancellationToken.None);
        await state.SaveAsync(draft, CancellationToken.None);

        Assert.Equal(draft.ConfigurationRevision, store.LastSaveRequest?.ExpectedConfigurationRevision);
        Assert.Equal(browse.CorrelationId, store.LastSaveRequest?.BrowseCorrelationId);
    }

    [Fact]
    public async Task New_profile_cannot_be_enabled_before_a_durable_folder_selection_exists()
    {
        var store = new RecordingStore();
        var state = CreateState(store);
        var draft = Draft(enable: true) with { ProfileId = null };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => state.SaveAsync(draft, CancellationToken.None).AsTask());

        Assert.Contains("save the profile disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Local_operator_policy_is_checked_before_every_mutation()
    {
        var store = new RecordingStore();
        var state = CreateState(store, new DenyingPolicy());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => state.RequestCatchUpAsync(Draft(enable: true).ProfileId!, manualReconciliation: false, CancellationToken.None).AsTask());

        Assert.Equal(0, store.CatchUpRequestCount);
    }

    [Fact]
    public async Task Save_checks_bounded_schedule_and_validates_the_private_spool_at_save_time()
    {
        var store = new RecordingStore();
        var validator = new RecordingSpoolValidator();
        var state = CreateState(store, spoolValidator: validator);
        var draft = Draft(enable: false) with
        {
            Schedule = new OutlookCaptureSchedule(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5))
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => state.SaveAsync(draft, CancellationToken.None).AsTask());

        Assert.Equal(0, validator.ValidationCount);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Received_time_profile_exposes_a_manual_reconciliation_path()
    {
        var store = new RecordingStore();
        var state = CreateState(store);
        var profileId = new OutlookCaptureProfileId(Guid.NewGuid());

        await state.RequestCatchUpAsync(profileId, manualReconciliation: true, CancellationToken.None);

        Assert.Equal(1, store.CatchUpRequestCount);
        Assert.Equal(OutlookCatchUpProvenance.Manual, store.LastCatchUpRequest?.Provenance);
        Assert.Equal("received-time-reconciliation", store.LastCatchUpRequest?.Reason);
    }

    [Fact]
    public void Outlook_mutation_audit_shape_is_sanitised_and_contains_no_private_input()
    {
        var audit = OperatorEventDraft.OutlookMutation(
            "save-profile",
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            accepted: true,
            Now);
        var entity = OperatorEventAppender.Create(audit);

        Assert.Equal("outlook.save_profile", entity.EventType);
        Assert.Equal("outlook", entity.EventFamily);
        Assert.Equal("outlook-control-plane", entity.Actor);
        Assert.Equal("{\"kind\":\"save_profile\",\"reasonCode\":\"accepted\"}", entity.DetailsJson);
        Assert.DoesNotContain("spool", entity.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entry", entity.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Local_policy_requires_both_loopback_and_an_authenticated_identity()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "Negotiate"));
        var accessor = new HttpContextAccessor { HttpContext = context };
        var connection = new LocalOutlookConnectionContext(accessor);
        var policy = new LocalOutlookOperatorPolicy(connection);

        await policy.EnsureMutationAllowedAsync(CancellationToken.None);

        accessor.HttpContext = null;
        await policy.EnsureMutationAllowedAsync(CancellationToken.None);

        var remoteContext = new DefaultHttpContext();
        remoteContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        remoteContext.User = context.User;
        var remotePolicy = new LocalOutlookOperatorPolicy(
            new LocalOutlookConnectionContext(new HttpContextAccessor { HttpContext = remoteContext }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => remotePolicy.EnsureMutationAllowedAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public void Operator_errors_are_bounded_and_never_echo_private_exception_text()
    {
        const string privatePath = "C:\\private\\never-over-signalr";

        var message = OutlookPageState.ToSafeOperatorMessage(new IOException($"Access denied: {privatePath}"));

        Assert.Equal("The Outlook spool could not be validated. Check its configured access and capacity.", message);
        Assert.DoesNotContain(privatePath, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spool_validation_accepts_only_an_allowlisted_local_writable_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxOutlookSpoolPolicy_{Guid.NewGuid():N}");
        var spool = Path.Combine(root, "spool");
        var sibling = Path.Combine(Path.GetDirectoryName(root)!, $"FluxOutlookSpoolSibling_{Guid.NewGuid():N}");
        Directory.CreateDirectory(spool);
        Directory.CreateDirectory(sibling);
        try
        {
            var validator = new LocalOutlookSpoolValidator(new OutlookSpoolPolicyOptions([spool], 1));

            var result = await validator.ValidateAsync("spool-1", CancellationToken.None);

            Assert.True(result.IsLocalPath);
            Assert.True(result.HasRequiredAccess);
            Assert.True(result.HasSufficientCapacity);
            Assert.True(result.IsWritable);
            Assert.Equal(64, result.PathFingerprint.Length);
            Assert.Equal(Path.GetFullPath(spool), result.PrivateSpoolRoot);
            Assert.DoesNotContain(spool, System.Text.Json.JsonSerializer.Serialize(validator.Choices), StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<ArgumentException>(
                () => validator.ValidateAsync(sibling, CancellationToken.None).AsTask());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(sibling, recursive: true);
        }
    }

    private static OutlookPageState CreateState(
        RecordingStore store,
        IOutlookOperatorPolicy? policy = null,
        IOutlookSpoolValidator? spoolValidator = null) =>
        new(
            store,
            new EmptyProjectionReader(),
            spoolValidator ?? new RecordingSpoolValidator(),
            policy ?? new AllowingPolicy(),
            new FixedTimeProvider(Now));

    private static OutlookProfileDraft Draft(bool enable) => new(
        new OutlookCaptureProfileId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        "Local mailbox",
        "spool-1",
        OutlookIncrementalBasis.LastModificationTime,
        new OutlookCaptureSchedule(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2)),
        enable,
        ConfigurationRevision: 7);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AllowingPolicy : IOutlookOperatorPolicy
    {
        public ValueTask EnsureMutationAllowedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class DenyingPolicy : IOutlookOperatorPolicy
    {
        public ValueTask EnsureMutationAllowedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException(new UnauthorizedAccessException("A local authenticated operator is required."));
    }

    private sealed class RecordingSpoolValidator : IOutlookSpoolValidator
    {
        public int ValidationCount { get; private set; }
        public IReadOnlyList<OutlookSpoolChoice> Choices { get; } = [new("spool-1", "Configured spool 1")];

        public ValueTask<OutlookSpoolValidation> ValidateAsync(string privateSpoolRoot, CancellationToken cancellationToken)
        {
            ValidationCount++;
            return ValueTask.FromResult(new OutlookSpoolValidation(new string('a', 64), true, true, true, true, privateSpoolRoot));
        }
    }

    private sealed class EmptyProjectionReader : IOutlookProjectionReader
    {
        public ValueTask<OutlookPageProjection> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(OutlookPageProjection.Empty);
    }

    private sealed class RecordingStore : IOutlookCaptureStore
    {
        public int SaveCount { get; private set; }
        public int BrowseRequestCount { get; private set; }
        public int CatchUpRequestCount { get; private set; }
        public OutlookBrowseRequest? LastBrowseRequest { get; private set; }
        public OutlookOperationReceipt? LastBrowseReceipt { get; private set; }
        public OutlookCatchUpRequest? LastCatchUpRequest { get; private set; }
        public OutlookProfileSaveRequest? LastSaveRequest { get; private set; }
        public IReadOnlyList<OutlookBrowseFolderProjection> BrowseFolders { get; init; } = [];

        public ValueTask<OutlookOperationReceipt> SaveProfileAsync(OutlookProfileSaveRequest request, CancellationToken cancellationToken)
        {
            SaveCount++;
            LastSaveRequest = request;
            return Accepted(request.OperationId);
        }

        public ValueTask<OutlookOperationReceipt> RequestBrowseAsync(OutlookBrowseRequest request, CancellationToken cancellationToken)
        {
            BrowseRequestCount++;
            LastBrowseRequest = request;
            LastBrowseReceipt = new OutlookOperationReceipt(request.OperationId, true, true, false);
            return ValueTask.FromResult(LastBrowseReceipt);
        }

        public ValueTask<IReadOnlyList<OutlookBrowseFolderProjection>> ReadBrowseResultAsync(Guid correlationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(BrowseFolders);

        public ValueTask<OutlookOperationReceipt> RequestCatchUpAsync(OutlookCatchUpRequest request, CancellationToken cancellationToken)
        {
            CatchUpRequestCount++;
            LastCatchUpRequest = request;
            return Accepted(request.OperationId);
        }

        public ValueTask<OutlookOperationReceipt> PauseProfileAsync(OutlookProfilePauseRequest request, CancellationToken cancellationToken) => Accepted(request.OperationId);
        public ValueTask<OutlookOperationReceipt> RemoveProfileAsync(OutlookProfileRemoveRequest request, CancellationToken cancellationToken) => Accepted(request.OperationId);
        public ValueTask<OutlookOperationReceipt> RecordHintAsync(OutlookHintRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OutlookCatchUpClaimReceipt> ClaimCatchUpAsync(OutlookCatchUpClaimRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OutlookOperationReceipt> RenewCatchUpLeaseAsync(OutlookCatchUpLeaseRenewalRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OutlookOperationReceipt> CompleteCatchUpAsync(OutlookCatchUpCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OutlookOperationReceipt> FailCatchUpAsync(OutlookCatchUpFailureRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OutlookOperationReceipt> RequeueCatchUpAsync(OutlookCatchUpRequeueRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OutlookOperationReceipt> ReleaseStaleCatchUpLeaseAsync(OutlookStaleCatchUpLeaseReleaseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OutlookBrowseClaimReceipt> ClaimBrowseAsync(OutlookBrowseClaimRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OutlookOperationReceipt> CompleteBrowseAsync(OutlookBrowseCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OutlookOperationReceipt> FailBrowseAsync(OutlookBrowseFailureRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OutlookOperationReceipt> ReleaseStaleBrowseClaimsAsync(Guid operationId, string requestFingerprint, DateTimeOffset observedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<OutlookProfileProjection>> ReadLocalProjectionAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<OutlookProfileProjection>>([]);

        private static ValueTask<OutlookOperationReceipt> Accepted(Guid operationId) =>
            ValueTask.FromResult(new OutlookOperationReceipt(operationId, true, true, false));
    }
}
