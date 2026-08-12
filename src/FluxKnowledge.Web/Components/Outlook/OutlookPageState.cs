using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Outlook;
using Microsoft.AspNetCore.Http;

namespace FluxKnowledge.Web.Components.Outlook;

public sealed record OutlookProfileDraft(
    OutlookCaptureProfileId? ProfileId,
    string DisplayName,
    string SpoolConfigurationKey,
    OutlookIncrementalBasis IncrementalBasis,
    OutlookCaptureSchedule Schedule,
    bool Enable,
    long ConfigurationRevision)
{
    public static OutlookProfileDraft Empty { get; } = new(
        null,
        string.Empty,
        string.Empty,
        OutlookIncrementalBasis.LastModificationTime,
        new OutlookCaptureSchedule(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2)),
        false,
        1);
}

public interface IOutlookOperatorPolicy
{
    ValueTask EnsureMutationAllowedAsync(CancellationToken cancellationToken);
}

/// <summary>Captures the connection boundary once while the interactive circuit has an HTTP context.</summary>
public sealed class LocalOutlookConnectionContext(IHttpContextAccessor httpContextAccessor)
{
    public bool IsLoopback { get; } = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress is { } remoteAddress &&
        IPAddress.IsLoopback(remoteAddress);

}

/// <summary>Allows mutations only for an anonymous direct-loopback circuit.</summary>
public sealed class LocalOutlookOperatorPolicy(LocalOutlookConnectionContext connection) : IOutlookOperatorPolicy
{
    public ValueTask EnsureMutationAllowedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connection.IsLoopback)
        {
            throw new UnauthorizedAccessException("A direct loopback connection is required for Outlook configuration changes.");
        }

        return ValueTask.CompletedTask;
    }
}

public interface IOutlookSpoolValidator
{
    IReadOnlyList<OutlookSpoolChoice> Choices { get; }

    ValueTask<OutlookSpoolValidation> ValidateAsync(string spoolConfigurationKey, CancellationToken cancellationToken);
}

public sealed record OutlookSpoolChoice(string Key, string DisplayName);

public sealed class OutlookPageState(
    IOutlookCaptureStore store,
    IOutlookProjectionReader projectionReader,
    IOutlookSpoolValidator spoolValidator,
    IOutlookOperatorPolicy operatorPolicy,
    TimeProvider timeProvider)
{
    private Guid? _browseCorrelationId;
    private string? _browseDraftFingerprint;

    public OutlookPageProjection Projection { get; private set; } = OutlookPageProjection.Empty;
    public IReadOnlyList<OutlookBrowseFolderProjection> BrowseFolders { get; private set; } = [];
    public IReadOnlyList<OutlookSpoolChoice> SpoolChoices => spoolValidator.Choices;

    public async ValueTask ReloadAsync(CancellationToken cancellationToken) =>
        Projection = await projectionReader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<OutlookBrowseRequest> RequestBrowseAsync(
        OutlookProfileDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await operatorPolicy.EnsureMutationAllowedAsync(cancellationToken).ConfigureAwait(false);
        if (draft.ProfileId is null || draft.ProfileId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Save the disabled Outlook profile before browsing for folders.");
        }
        if (draft.ConfigurationRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(draft.ConfigurationRevision));
        }

        InvalidateBrowse();
        var operationId = Guid.NewGuid();
        var request = new OutlookBrowseRequest(
            operationId,
            RequestFingerprint("request-browse", operationId, draft.ProfileId.Value, draft.ConfigurationRevision),
            Guid.NewGuid(),
            Guid.NewGuid(),
            draft.ConfigurationRevision,
            timeProvider.GetUtcNow().AddMinutes(5),
            draft.ProfileId);
        var receipt = await store.RequestBrowseAsync(request, cancellationToken).ConfigureAwait(false);
        if (!receipt.Accepted || !receipt.Committed)
        {
            throw new InvalidOperationException("The durable Outlook folder browse request was not accepted.");
        }

        _browseCorrelationId = request.CorrelationId;
        _browseDraftFingerprint = DraftFingerprint(draft);
        return request;
    }

    public async ValueTask<IReadOnlyList<OutlookBrowseFolderProjection>> RefreshBrowseResultAsync(
        OutlookProfileDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (_browseCorrelationId is not { } correlationId ||
            !string.Equals(_browseDraftFingerprint, DraftFingerprint(draft), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Outlook folder browse result is stale for the current configuration.");
        }

        var folders = await store.ReadBrowseResultAsync(correlationId, cancellationToken).ConfigureAwait(false);
        if (folders.Count > 500)
        {
            throw new InvalidOperationException("The Outlook folder browse result exceeded the supported bound.");
        }
        foreach (var folder in folders)
        {
            folder.Validate();
        }

        BrowseFolders = folders;
        return folders;
    }

    public async ValueTask<OutlookOperationReceipt> SaveAsync(
        OutlookProfileDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await operatorPolicy.EnsureMutationAllowedAsync(cancellationToken).ConfigureAwait(false);
        draft.Schedule.Validate();
        if (draft.Enable && draft.ProfileId is null)
        {
            throw new InvalidOperationException("Save the profile disabled, browse for folders through the Outlook host, then enable it.");
        }
        if (draft.ProfileId is not null && draft.Enable && !HasCurrentBrowseResult(draft))
        {
            throw new InvalidOperationException("Saving an enabled Outlook profile requires a current folder browse result.");
        }

        var spool = await spoolValidator.ValidateAsync(draft.SpoolConfigurationKey, cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var request = new OutlookProfileSaveRequest(
            operationId,
            RequestFingerprint(
                "save-profile",
                operationId,
                draft.ProfileId?.Value,
                draft.DisplayName,
                draft.IncrementalBasis,
                draft.Schedule.Cadence.Ticks,
                draft.Schedule.MaximumOverlap.Ticks,
                draft.SpoolConfigurationKey,
                draft.Enable,
                draft.ConfigurationRevision,
                draft.Enable ? _browseCorrelationId : null),
            draft.ProfileId,
            draft.DisplayName,
            draft.IncrementalBasis,
            draft.Schedule,
            spool,
            draft.Enable,
            draft.ProfileId is null ? null : draft.ConfigurationRevision,
            draft.Enable ? _browseCorrelationId : null);
        var receipt = await store.SaveProfileAsync(request, cancellationToken).ConfigureAwait(false);
        if (!receipt.Accepted || !receipt.Committed)
        {
            throw new InvalidOperationException("The Outlook profile change was not committed.");
        }

        InvalidateBrowse();
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public async ValueTask<OutlookOperationReceipt> PauseAsync(
        OutlookCaptureProfileId profileId,
        CancellationToken cancellationToken)
    {
        await operatorPolicy.EnsureMutationAllowedAsync(cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var receipt = await store.PauseProfileAsync(
            new OutlookProfilePauseRequest(
                operationId,
                RequestFingerprint("pause-profile", operationId, profileId.Value),
                profileId,
                "local-operator"),
            cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public async ValueTask<OutlookOperationReceipt> RemoveAsync(
        OutlookCaptureProfileId profileId,
        CancellationToken cancellationToken)
    {
        await operatorPolicy.EnsureMutationAllowedAsync(cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var receipt = await store.RemoveProfileAsync(
            new OutlookProfileRemoveRequest(
                operationId,
                RequestFingerprint("remove-profile", operationId, profileId.Value),
                profileId,
                "local-operator"),
            cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public async ValueTask<OutlookOperationReceipt> RequestCatchUpAsync(
        OutlookCaptureProfileId profileId,
        bool manualReconciliation,
        CancellationToken cancellationToken)
    {
        await operatorPolicy.EnsureMutationAllowedAsync(cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var coalescingKey = manualReconciliation
            ? $"received-reconcile:{profileId.Value:N}"
            : $"manual:{profileId.Value:N}";
        var receipt = await store.RequestCatchUpAsync(
            new OutlookCatchUpRequest(
                operationId,
                RequestFingerprint("request-catchup", operationId, profileId.Value, coalescingKey),
                profileId,
                coalescingKey,
                OutlookCatchUpProvenance.Manual,
                manualReconciliation ? "received-time-reconciliation" : "manual-catch-up"),
            cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public bool HasCurrentBrowseResult(OutlookProfileDraft draft) =>
        _browseCorrelationId is not null &&
        BrowseFolders.Count > 0 &&
        string.Equals(_browseDraftFingerprint, DraftFingerprint(draft), StringComparison.Ordinal);

    public void InvalidateBrowse()
    {
        _browseCorrelationId = null;
        _browseDraftFingerprint = null;
        BrowseFolders = [];
    }

    private static string DraftFingerprint(OutlookProfileDraft draft) => RequestFingerprint(
        "profile-draft",
        draft.ProfileId?.Value,
        draft.DisplayName,
        draft.SpoolConfigurationKey,
        draft.IncrementalBasis,
        draft.Schedule.Cadence.Ticks,
        draft.Schedule.MaximumOverlap.Ticks,
        draft.Enable,
        draft.ConfigurationRevision);

    private static string RequestFingerprint(params object?[] values)
    {
        var text = string.Join('\u001f', values.Select(static value => value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString()
        }));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string ToSafeOperatorMessage(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "A direct loopback connection is required for Outlook configuration changes.",
        IOException or DirectoryNotFoundException => "The Outlook spool could not be validated. Check its configured access and capacity.",
        ArgumentOutOfRangeException => "The Outlook schedule, overlap or selected configuration is outside the supported bounds.",
        ArgumentException => "The Outlook configuration is invalid. Check the selected values.",
        _ => "The Outlook request could not be completed. Reload its SQL status and try again."
    };
}
