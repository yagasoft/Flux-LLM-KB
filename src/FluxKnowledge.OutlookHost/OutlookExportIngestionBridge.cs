using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Outlook;

namespace FluxKnowledge.OutlookHost;

/// <summary>
/// Writes one complete private export, promotes it atomically, then enters only Task 3's
/// SQL-authoritative ready-export ingestion path. It never mutates a cursor directly.
/// </summary>
internal interface IOutlookReadyExportIngestionService
{
    ValueTask<OutlookExportCommitReceipt> IngestReadyAsync(
        string spoolRoot,
        Guid exportId,
        CancellationToken cancellationToken);
}

internal sealed class SqlOutlookReadyExportIngestionService(SqlOutlookExportIngestionService inner)
    : IOutlookReadyExportIngestionService
{
    public ValueTask<OutlookExportCommitReceipt> IngestReadyAsync(
        string spoolRoot,
        Guid exportId,
        CancellationToken cancellationToken) => inner.IngestReadyAsync(spoolRoot, exportId, cancellationToken);
}

internal sealed class OutlookExportIngestionBridge(
    IOutlookReadyExportIngestionService ingestionService,
    PersistedOutlookSpoolRootPolicy spoolRootPolicy)
    : IOutlookExportIngestionBridge
{
    public async ValueTask<bool> ExportAndIngestAsync(
        OutlookHostCatchUpWork work,
        OutlookHostFolderConfiguration folder,
        OutlookItemEnvelope item,
        OutlookMessagePayload payload,
        CancellationToken cancellationToken)
    {
        var canonicalSpoolRoot = spoolRootPolicy.RequireCanonicalBeforeIo(folder.SpoolRoot);
        var exportId = ExportIdFor(folder, item);
        var layout = new OutlookSpoolLayout(canonicalSpoolRoot);
        var readyDirectory = layout.GetReadyExportDirectory(exportId);
        var operationId = StableGuid($"ingest|{exportId:N}|{work.Claim.CatchUpId:N}|{work.Claim.FencingToken}");
        var cursorUtc = item.Timestamp(folder.Basis);
        var recovery = new OutlookReadyExportRecovery(
            operationId,
            Sha256($"ingest-ready|{exportId:N}|{work.Claim.CatchUpId:N}|{work.Claim.FencingToken}|{item.SourceFingerprint}"),
            work.Claim.CatchUpId,
            work.Claim.FencingToken,
            work.Claim.ProfileId.Value,
            folder.FolderId.Value,
            item.EntryId,
            item.SourceFingerprint,
            cursorUtc,
            Sha256($"{cursorUtc:O}|{item.SourceFingerprint}"));
        if (!Directory.Exists(readyDirectory))
        {
            var inflightDirectory = layout.CreateInflightExportDirectory(exportId);
            var bodyPath = "body.bin";
            await WritePrivateFileAsync(
                Path.Combine(inflightDirectory, bodyPath),
                payload.Body,
                cancellationToken).ConfigureAwait(false);
            var attachments = new List<OutlookExportSidecar>(payload.Attachments.Count);
            for (var index = 0; index < payload.Attachments.Count; index++)
            {
                var relativePath = Path.Combine("attachments", $"{index:D4}.bin");
                await WritePrivateFileAsync(
                    Path.Combine(inflightDirectory, relativePath),
                    payload.Attachments[index].Content,
                    cancellationToken).ConfigureAwait(false);
                attachments.Add(OutlookExportSidecar.Create(relativePath, payload.Attachments[index].ContentType));
            }

            var manifest = OutlookExportManifest.Create(exportId, bodyPath, attachments, recovery) with
            {
                Body = OutlookExportSidecar.Create(bodyPath, payload.BodyContentType)
            };
            await layout.WriteManifestAsync(inflightDirectory, manifest, cancellationToken).ConfigureAwait(false);
            _ = await layout.PromoteAsync(exportId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var existing = await layout.ReadReadyManifestAsync(exportId, cancellationToken).ConfigureAwait(false);
            if (existing.Recovery != recovery)
            {
                try
                {
                    var existingReceipt = await ingestionService
                        .IngestReadyAsync(folder.SpoolRoot, exportId, cancellationToken)
                        .ConfigureAwait(false);
                    return existingReceipt.Accepted && !existingReceipt.IsReplay;
                }
                catch (OutlookReadyExportLeaseException)
                {
                    // The promoted content is complete but its prior fenced owner can no longer
                    // commit. Rebind only after the old receipt path has proved unusable, so an
                    // already-committed immutable manifest is never rewritten.
                }

                await layout.RebindReadyRecoveryAsync(exportId, recovery, cancellationToken).ConfigureAwait(false);
            }
        }

        var receipt = await ingestionService
            .IngestReadyAsync(folder.SpoolRoot, exportId, cancellationToken)
            .ConfigureAwait(false);
        return receipt.Accepted && !receipt.IsReplay;
    }

    internal static Guid ExportIdFor(
        OutlookHostFolderConfiguration folder,
        OutlookItemEnvelope item) =>
        StableGuid($"{folder.FolderId.Value:N}|{item.StoreId}|{item.EntryId}|{item.SourceFingerprint}");

    private static async Task WritePrivateFileAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static Guid StableGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
