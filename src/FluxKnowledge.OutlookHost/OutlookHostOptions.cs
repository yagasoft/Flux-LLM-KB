using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Outlook;

namespace FluxKnowledge.OutlookHost;

internal sealed class OutlookHostOptions
{
    public bool Enabled { get; init; }
    public TimeSpan CatchUpLeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan HeartbeatCadence { get; init; } = TimeSpan.FromMinutes(1);
}

public enum OutlookHostExitReason
{
    Disabled,
    NotWindows,
    NonInteractiveSession,
    SingletonUnavailable,
    NoDurableWork,
    DurableClaimDisabled,
    Completed,
    ComDependencyMissing,
    OutlookUnavailable,
    FolderAccessDenied,
    LeaseStale,
    IngestionFailed
}

internal sealed record OutlookHostRunResult(OutlookHostExitReason Reason, int ExportedCount = 0);

internal sealed record OutlookHostFolderConfiguration(
    OutlookCaptureFolderId FolderId,
    OutlookFolderIdentity Identity,
    OutlookIncrementalBasis Basis,
    DateTimeOffset? CursorUtc,
    string? CursorFingerprint,
    TimeSpan Overlap,
    string SpoolRoot);

internal sealed record OutlookHostCatchUpWork(
    OutlookCatchUpClaim Claim,
    bool IsDurablyEnabled,
    IReadOnlyList<OutlookHostFolderConfiguration> Folders);

internal sealed record OutlookFolderDescriptor(
    OutlookCaptureFolderId FolderId,
    OutlookFolderIdentity Identity);

internal sealed record OutlookHint(string CoalescingKey);

internal sealed record OutlookCursor(
    OutlookIncrementalBasis Basis,
    DateTimeOffset FromUtc,
    string? Fingerprint);

internal sealed record OutlookItemEnvelope(
    string StoreId,
    string EntryId,
    DateTimeOffset LastModificationTimeUtc,
    DateTimeOffset ReceivedTimeUtc,
    string SourceFingerprint)
{
    public DateTimeOffset Timestamp(OutlookIncrementalBasis basis) =>
        basis == OutlookIncrementalBasis.LastModificationTime
            ? LastModificationTimeUtc
            : ReceivedTimeUtc;
}

internal sealed record OutlookAttachmentPayload(
    string FileName,
    string ContentType,
    ReadOnlyMemory<byte> Content);

internal sealed record OutlookMessagePayload(
    ReadOnlyMemory<byte> Body,
    string BodyContentType,
    IReadOnlyList<OutlookAttachmentPayload> Attachments);

internal enum OutlookComFailureReason
{
    DependencyMissing,
    OutlookUnavailable,
    FolderAccessDenied,
    LeaseStale
}

internal sealed class OutlookComHostException(
    OutlookComFailureReason reason,
    Exception? innerException = null)
    : Exception("Classic Outlook access failed.", innerException)
{
    public OutlookComFailureReason Reason { get; } = reason;
}

/// <summary>Maps binding failures and other raw COM-boundary exceptions to public-safe host categories.</summary>
internal static class OutlookComFailureClassifier
{
    public static OutlookComHostException Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new OutlookComHostException(
            HasMissingInteropDependency(exception)
                ? OutlookComFailureReason.DependencyMissing
                : OutlookComFailureReason.OutlookUnavailable,
            exception);
    }

    private static bool HasMissingInteropDependency(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
            {
                return true;
            }
        }

        return false;
    }
}
