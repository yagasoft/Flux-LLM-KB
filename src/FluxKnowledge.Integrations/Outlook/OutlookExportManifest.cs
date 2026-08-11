using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Integrations.Files;

namespace FluxKnowledge.Integrations.Outlook;

/// <summary>Private on-disk description of a complete Outlook export. Paths are always relative to one export directory.</summary>
public sealed record OutlookExportManifest(
    Guid ExportId,
    OutlookExportSidecar Body,
    IReadOnlyList<OutlookExportSidecar> Attachments,
    OutlookReadyExportRecovery Recovery)
{
    public static OutlookExportManifest Create(
        Guid exportId,
        string bodyRelativePath,
        IReadOnlyList<OutlookExportSidecar> attachments,
        OutlookReadyExportRecovery recovery)
    {
        if (exportId == Guid.Empty)
        {
            throw new ArgumentException("An Outlook export identifier is required.", nameof(exportId));
        }

        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(recovery);
        recovery.ToCommitRequest(exportId, new string('0', 64)).Validate();
        return new OutlookExportManifest(
            exportId,
            OutlookExportSidecar.Create(bodyRelativePath, "text/plain"),
            attachments,
            recovery);
    }
}

/// <summary>
/// Private restart envelope written before promotion. The manifest hash and canonical ready path
/// are reconstructed from the promoted directory, so neither relies on process memory.
/// </summary>
public sealed record OutlookReadyExportRecovery(
    Guid OperationId,
    string RequestFingerprint,
    Guid CatchUpId,
    long FencingToken,
    Guid ProfileId,
    Guid FolderId,
    string EntryId,
    string SourceFingerprint,
    DateTimeOffset CursorUtc,
    string CursorFingerprint)
{
    public OutlookExportCommitRequest ToCommitRequest(Guid exportId, string manifestHash)
    {
        var request = new OutlookExportCommitRequest(
            OperationId,
            RequestFingerprint,
            new OutlookCaptureExportId(exportId),
            CatchUpId,
            FencingToken,
            new OutlookExportObservation(
                new OutlookCaptureProfileId(ProfileId),
                new OutlookCaptureFolderId(FolderId),
                EntryId,
                SourceFingerprint,
                manifestHash,
                Path.Combine("ready", exportId.ToString("N")),
                CursorUtc,
                CursorFingerprint));
        request.Validate();
        request.Observation!.Validate();
        return request;
    }
}

public sealed record OutlookExportSidecar(
    string RelativePath,
    string ContentType,
    string ContentSha256,
    long ByteLength)
{
    public static OutlookExportSidecar Create(string relativePath, string contentType) =>
        new(relativePath, contentType, new string('0', 64), 0);

    internal static async Task<OutlookExportSidecar> ReadAndHashAsync(
        string exportDirectory,
        OutlookExportSidecar sidecar,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateRelativePath(sidecar.RelativePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(sidecar.ContentType);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            throw new OutlookReadyExportValidationException("ready-sidecar-path-invalid", exception);
        }

        VerifiedContainedFile verified;
        try
        {
            verified = await ContainedFileReader.ReadAsync(
                exportDirectory,
                sidecar.RelativePath,
                64L * 1024 * 1024,
                cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new OutlookReadyExportValidationException("ready-sidecar-path-invalid", exception);
        }
        catch (FileNotFoundException exception)
        {
            throw new OutlookReadyExportValidationException("ready-sidecar-missing", exception);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw new OutlookReadyExportValidationException("ready-sidecar-invalid", exception);
        }
        return new OutlookExportSidecar(
            sidecar.RelativePath,
            sidecar.ContentType,
            verified.ContentSha256,
            verified.ByteLength);
    }

    internal static void ValidateRelativePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Path.IsPathRooted(value) || value.Contains(':') || value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part is "." or ".."))
        {
            throw new InvalidDataException("Outlook export sidecar paths must be contained relative paths.");
        }
    }
}
