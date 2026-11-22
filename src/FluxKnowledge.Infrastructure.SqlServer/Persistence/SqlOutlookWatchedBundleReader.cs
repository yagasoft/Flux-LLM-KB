using System.Globalization;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Outlook;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Reconstructs an accepted Outlook capture from its immutable retained source inputs.</summary>
public sealed class SqlOutlookWatchedBundleReader(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    IRetainedSourceReader retainedSourceReader,
    PersistedOutlookSpoolRootPolicy spoolPolicy)
{
    private const string IngestionOperationKind = "ingest-ready-export";
    private const int MaximumManifestBytes = 1024 * 1024;
    // Each original manifest sidecar needs at least its 64-byte SHA-256 field.
    private const int MaximumSidecars = MaximumManifestBytes / 64;
    private const long MaximumSidecarBytes = 64L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OutlookWatchedBundle> ReadAsync(Guid profileId, Guid exportId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(profileId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(exportId, Guid.Empty);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var owned = await (
            from export in context.OutlookCaptureExports.AsNoTracking()
            join profile in context.OutlookCaptureProfiles.AsNoTracking() on export.ProfileId equals (Guid?)profile.Id
            join folder in context.OutlookCaptureFolders.AsNoTracking() on export.FolderId equals (Guid?)folder.Id
            join root in context.SourceRootConfigurations.AsNoTracking() on profile.SourceRootId equals root.Id
            join revision in context.SourceRevisions.AsNoTracking() on export.SourceRevisionId equals (Guid?)revision.Id
            where export.Id == exportId && export.State == (int)OutlookExportState.Ingested &&
                profile.Id == profileId && folder.ProfileId == profileId && revision.SourceRootId == root.Id &&
                context.OutlookCaptureOperations.Any(operation => operation.ProfileId == profileId &&
                    operation.ResourceId == exportId && operation.Kind == IngestionOperationKind && operation.Accepted)
            select new { Export = export, profile.SpoolRoot, root.CanonicalPath, Parent = revision }
        ).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var messageIdentity = $"outlook:{exportId:N}";
        var readyPath = Path.Combine("ready", exportId.ToString("N"));
        if (owned is null || owned.Export.RelativeSpoolPath != readyPath || owned.Export.ManifestHash is null ||
            owned.Parent.ContentSha256 != owned.Export.ManifestHash || owned.Parent.ParentSourceRevisionId is not null ||
            owned.Parent.StableSourceIdentity != messageIdentity || owned.Parent.Classification != "OutlookMessage" ||
            owned.Parent.CanonicalPath != Path.Combine(owned.CanonicalPath, readyPath, "message.manifest") ||
            owned.Parent.OriginKind != 0 || owned.Parent.Revision != 1 ||
            owned.Parent.ByteLength is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("The accepted Outlook export is unavailable for this capture profile.");
        }

        var spoolRoot = spoolPolicy.RequireCanonicalBeforeIo(owned.SpoolRoot);
        var symbolicDirectory = Path.Combine(owned.CanonicalPath, readyPath);
        var sidecars = await ReadSidecarBindingsAsync(context, owned.Parent, messageIdentity, symbolicDirectory,
            cancellationToken).ConfigureAwait(false);
        var manifest = await ReadManifestIfPresentAsync(context, owned.Export, owned.Parent.ByteLength, spoolRoot, readyPath,
            cancellationToken).ConfigureAwait(false);
        if (manifest is not null &&
            (manifest.Attachments.Count + 1 != sidecars.Length ||
             !MatchesSidecar(manifest.Body, sidecars[0]) ||
             manifest.Attachments.Where((sidecar, index) => !MatchesSidecar(sidecar, sidecars[index + 1])).Any()))
        {
            throw new InvalidDataException("The Outlook export's retained sidecars disagree with its accepted manifest.");
        }

        var bodyBytes = await ReadSidecarAsync(sidecars[0], cancellationToken).ConfigureAwait(false);
        string body;
        try
        {
            body = StrictUtf8.GetString(bodyBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The retained Outlook message body is not valid UTF-8.", exception);
        }

        var attachments = new OutlookWatchedAttachment[sidecars.Length - 1];
        for (var index = 0; index < attachments.Length; index++)
        {
            var binding = sidecars[index + 1];
            var metadata = manifest?.Attachments[index];
            attachments[index] = new OutlookWatchedAttachment(metadata?.OriginalFileName,
                async token => await ReadSidecarAsync(binding, token).ConfigureAwait(false), metadata?.ContentType)
            {
                IsSignatureDecoration = binding.Classification == OutlookAttachmentDisposition.SignatureDecorationClassification
            };
        }

        return new OutlookWatchedBundle(exportId, manifest?.Subject, manifest?.Sender, manifest?.ReceivedAtUtc, body, attachments);
    }

    private static async Task<SidecarBinding[]> ReadSidecarBindingsAsync(
        FluxKnowledgeDbContext context, SourceRevisionEntity parent, string messageIdentity, string symbolicDirectory,
        CancellationToken cancellationToken)
    {
        var bodyIdentity = $"{messageIdentity}:body";
        var attachmentPrefix = $"{messageIdentity}:attachment-";
        var revisions = await context.SourceRevisions.AsNoTracking()
            .Where(revision => (revision.ParentSourceRevisionId == parent.Id || revision.SourceRootId == parent.SourceRootId) &&
                (revision.StableSourceIdentity == bodyIdentity || revision.StableSourceIdentity.StartsWith(attachmentPrefix)))
            .Select(revision => new SidecarBinding(revision.Id, revision.SourceRootId, revision.ParentSourceRevisionId,
                revision.StableSourceIdentity, revision.CanonicalPath, revision.ContentSha256, revision.Classification,
                revision.Revision, revision.OriginKind, revision.ByteLength))
            .Take(MaximumSidecars + 1)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        if (revisions.Length is 0 or > MaximumSidecars)
        {
            throw new InvalidDataException("The Outlook export does not have a bounded retained sidecar inventory.");
        }

        var ordered = new SidecarBinding[revisions.Length];
        foreach (var revision in revisions)
        {
            var identity = revision.StableSourceIdentity;
            var ordinal = 0;
            if (identity != bodyIdentity &&
                (!identity.StartsWith(attachmentPrefix, StringComparison.Ordinal) ||
                 !int.TryParse(identity.AsSpan(attachmentPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out ordinal) ||
                 ordinal <= 0 || identity != attachmentPrefix + ordinal.ToString(CultureInfo.InvariantCulture)))
            {
                throw new InvalidDataException("The Outlook export has an invalid attachment ordinal.");
            }

            var kind = ordinal == 0 ? "body" : $"attachment-{ordinal.ToString(CultureInfo.InvariantCulture)}";
            if (ordinal >= ordered.Length || ordered[ordinal] is not null || revision.ParentSourceRevisionId != parent.Id ||
                revision.SourceRootId != parent.SourceRootId || revision.Revision != 1 || revision.OriginKind != 0 ||
                revision.CanonicalPath != Path.Combine(symbolicDirectory, kind) ||
                (ordinal == 0 ? revision.Classification != "AcceptedUtf8Text" :
                    revision.Classification is not ("AcceptedUtf8Text" or "DeferredCapability" or OutlookAttachmentDisposition.SignatureDecorationClassification)))
            {
                throw new InvalidDataException("The Outlook export has a missing or ambiguous retained sidecar binding.");
            }

            if (revision.ByteLength is < 0 or > MaximumSidecarBytes)
            {
                throw new InvalidDataException("The Outlook export has a sidecar outside the 64 MiB read bound.");
            }

            ordered[ordinal] = revision;
        }
        return ordered;
    }

    private static async Task<OutlookExportManifest?> ReadManifestIfPresentAsync(
        FluxKnowledgeDbContext context, OutlookCaptureExportEntity export, long manifestByteLength,
        string spoolRoot, string readyPath, CancellationToken cancellationToken)
    {
        OutlookExportManifest? manifest;
        try
        {
            var file = await ContainedFileReader.ReadAsync(spoolRoot, Path.Combine(readyPath, "manifest.json"),
                MaximumManifestBytes, cancellationToken, export.ManifestHash, manifestByteLength).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<OutlookExportManifest>(file.Bytes, ManifestJsonOptions);
            if (manifest?.Recovery is null || manifest.Body is null || manifest.Attachments is null || manifest.ExportId != export.Id)
                throw new InvalidDataException("The Outlook ready manifest does not identify the accepted export.");
            _ = manifest.Recovery.ToCommitRequest(export.Id, file.ContentSha256);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new InvalidDataException("The present Outlook ready manifest could not be verified.", exception);
        }

        var recovery = manifest.Recovery;
        if (recovery.ProfileId != export.ProfileId || recovery.FolderId != export.FolderId ||
            recovery.CatchUpId != export.CatchUpId || recovery.FencingToken != export.FencingToken ||
            recovery.EntryId != export.EntryId || recovery.SourceFingerprint != export.SourceFingerprint)
        {
            throw new InvalidDataException("The Outlook ready manifest does not match the accepted capture identity.");
        }

        var fingerprint = await context.OutlookCaptureOperations.AsNoTracking()
            .Where(operation => operation.OperationId == recovery.OperationId &&
                operation.ProfileId == export.ProfileId && operation.ResourceId == export.Id &&
                operation.Accepted && operation.Kind == IngestionOperationKind)
            .Select(operation => operation.RequestFingerprint)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (fingerprint != recovery.RequestFingerprint)
            throw new InvalidDataException("The Outlook ready manifest does not match the accepted operation receipt.");
        return manifest;
    }

    private async Task<byte[]> ReadSidecarAsync(SidecarBinding revision, CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await retainedSourceReader.OpenReadAsync(new SourceRevisionId(revision.Id),
                MaximumSidecarBytes, cancellationToken).ConfigureAwait(false);
            if (lease.SourceRevisionId.Value != revision.Id || lease.ContentSha256 != revision.ContentSha256 ||
                lease.ByteLength != revision.ByteLength)
            {
                throw new InvalidDataException("The retained Outlook sidecar changed after its binding was selected.");
            }

            var bytes = new byte[checked((int)lease.ByteLength)];
            await lease.Stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("A required retained Outlook sidecar could not be read.", exception);
        }
    }

    private static bool MatchesSidecar(OutlookExportSidecar? sidecar, SidecarBinding revision) =>
        sidecar is not null && sidecar.ByteLength == revision.ByteLength && sidecar.ContentSha256 == revision.ContentSha256 &&
        sidecar.IsSignatureDecoration == (revision.Classification == OutlookAttachmentDisposition.SignatureDecorationClassification);

    private sealed record SidecarBinding(
        Guid Id,
        Guid SourceRootId,
        Guid? ParentSourceRevisionId,
        string StableSourceIdentity,
        string CanonicalPath,
        string ContentSha256,
        string Classification,
        long Revision,
        int OriginKind,
        long ByteLength);
}
