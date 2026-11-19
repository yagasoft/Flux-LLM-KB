using System.Text.Json.Nodes;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Integrations.Outlook;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Xunit;

namespace FluxKnowledge.OutlookHost.Tests;

public sealed class OutlookExportIngestionBridgeTests
{
    private static readonly DateTimeOffset CursorUtc = new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Promoted_ready_export_rebinds_to_new_fencing_token_after_restart()
    {
        using var spool = new TemporaryDirectory();
        var ingestion = new FakeReadyIngestionService { RejectedFencingToken = 1 };
        var bridge = new OutlookExportIngestionBridge(
            ingestion,
            PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spool.Path));
        var item = Item();
        var first = Work(spool.Path, Guid.Parse("11111111-1111-1111-1111-111111111111"), 1);
        var second = Work(spool.Path, Guid.Parse("22222222-2222-2222-2222-222222222222"), 2);

        await Assert.ThrowsAsync<OutlookReadyExportLeaseException>(async () =>
            await bridge.ExportAndIngestAsync(first.Work, first.Folder, item, Payload(), CancellationToken.None));
        var accepted = await bridge.ExportAndIngestAsync(
            second.Work,
            second.Folder,
            item,
            Payload(),
            CancellationToken.None);

        Assert.True(accepted);
        Assert.Equal(3, ingestion.Requests.Count);
        Assert.Single(ingestion.ExportIds.Distinct());
        Assert.Equal(1, ingestion.Requests[0].FencingToken);
        Assert.Equal(1, ingestion.Requests[1].FencingToken);
        Assert.Equal(2, ingestion.Requests[2].FencingToken);
        Assert.Equal(second.Work.Claim.CatchUpId, ingestion.Requests[2].CatchUpId);
    }

    [Fact]
    public async Task Promoted_ready_export_retains_private_metadata_through_rebind()
    {
        using var spool = new TemporaryDirectory();
        var ingestion = new FakeReadyIngestionService { RejectedFencingToken = 1 };
        var bridge = new OutlookExportIngestionBridge(
            ingestion,
            PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spool.Path));
        var item = Item();
        var first = Work(spool.Path, Guid.NewGuid(), 1);
        var second = Work(spool.Path, Guid.NewGuid(), 2);
        byte[] attachmentContent = [0x00, 0x01, 0xff];
        var payload = Payload() with
        {
            Attachments = [new OutlookAttachmentPayload("../private name.pdf", "application/pdf", attachmentContent)],
            Subject = "Captured subject",
            Sender = "Example sender"
        };

        await Assert.ThrowsAsync<OutlookReadyExportLeaseException>(async () =>
            await bridge.ExportAndIngestAsync(first.Work, first.Folder, item, payload, CancellationToken.None));
        Assert.True(await bridge.ExportAndIngestAsync(
            second.Work, second.Folder, item, Payload(), CancellationToken.None));

        var layout = new OutlookSpoolLayout(spool.Path);
        var exportId = Assert.Single(ingestion.ExportIds.Distinct());
        var ready = layout.GetReadyExportDirectory(exportId);
        var manifest = await layout.ReadReadyManifestAsync(exportId, CancellationToken.None);
        Assert.Equal("Captured subject", manifest.Subject);
        Assert.Equal("Example sender", manifest.Sender);
        Assert.Equal(CursorUtc, manifest.ReceivedAtUtc);
        Assert.Equal(TimeSpan.Zero, manifest.ReceivedAtUtc?.Offset);
        Assert.Equal(CursorUtc.AddMinutes(1), manifest.Recovery.CursorUtc);
        Assert.Equal(2, manifest.Recovery.FencingToken);

        var attachment = Assert.Single(manifest.Attachments);
        Assert.Equal("../private name.pdf", attachment.OriginalFileName);
        Assert.Equal(Path.Combine("attachments", "0000.bin"), attachment.RelativePath);
        Assert.Equal(attachmentContent, await File.ReadAllBytesAsync(Path.Combine(ready, attachment.RelativePath)));
        Assert.False(File.Exists(Path.Combine(ready, "private name.pdf")));
    }

    [Fact]
    public async Task Ready_manifest_reads_legacy_exports_with_no_metadata_fields()
    {
        using var spool = new TemporaryDirectory();
        var ingestion = new FakeReadyIngestionService();
        var bridge = new OutlookExportIngestionBridge(
            ingestion,
            PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spool.Path));
        var item = Item();
        var work = Work(spool.Path, Guid.NewGuid(), 1);
        var payload = Payload() with
        {
            Attachments = [new OutlookAttachmentPayload("example.txt", "text/plain", "example"u8.ToArray())]
        };
        Assert.True(await bridge.ExportAndIngestAsync(work.Work, work.Folder, item, payload, CancellationToken.None));

        var exportId = Assert.Single(ingestion.ExportIds);
        var layout = new OutlookSpoolLayout(spool.Path);
        var manifestPath = Path.Combine(layout.GetReadyExportDirectory(exportId), "manifest.json");
        var legacy = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        Assert.False(legacy.ContainsKey("subject"));
        Assert.False(legacy.ContainsKey("sender"));
        Assert.True(legacy.Remove("receivedAtUtc"));
        Assert.True(legacy["attachments"]![0]!.AsObject().Remove("originalFileName"));
        await File.WriteAllTextAsync(manifestPath, legacy.ToJsonString());

        var manifest = await layout.ReadReadyManifestAsync(exportId, CancellationToken.None);
        Assert.Null(manifest.Subject);
        Assert.Null(manifest.Sender);
        Assert.Null(manifest.ReceivedAtUtc);
        Assert.Null(Assert.Single(manifest.Attachments).OriginalFileName);
    }

    [Fact]
    public async Task Partial_inflight_export_is_rewritten_and_promoted_on_retry()
    {
        using var spool = new TemporaryDirectory();
        var ingestion = new FakeReadyIngestionService();
        var bridge = new OutlookExportIngestionBridge(
            ingestion,
            PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spool.Path));
        var item = Item();
        var work = Work(spool.Path, Guid.Parse("33333333-3333-3333-3333-333333333333"), 3);
        var exportId = OutlookExportIngestionBridge.ExportIdFor(work.Folder, item);
        var layout = new OutlookSpoolLayout(spool.Path);
        var inflight = layout.CreateInflightExportDirectory(exportId);
        await File.WriteAllTextAsync(Path.Combine(inflight, "body.bin"), "partial");

        var accepted = await bridge.ExportAndIngestAsync(
            work.Work,
            work.Folder,
            item,
            Payload(),
            CancellationToken.None);

        Assert.True(accepted);
        Assert.Single(ingestion.Requests);
        Assert.True(Directory.Exists(layout.GetReadyExportDirectory(exportId)));
    }

    private static OutlookItemEnvelope Item() =>
        new("store", "entry", CursorUtc.AddMinutes(1), CursorUtc, new string('a', 64));

    private static OutlookMessagePayload Payload() =>
        new("complete body"u8.ToArray(), "text/plain", []);

    private static (OutlookHostCatchUpWork Work, OutlookHostFolderConfiguration Folder) Work(
        string spoolRoot,
        Guid catchUpId,
        long fencingToken)
    {
        var profileId = new OutlookCaptureProfileId(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var host = new OutlookHostIdentity("S-1-5-21-test", 7, "host-test");
        var claim = new OutlookCatchUpClaim(
            catchUpId,
            profileId,
            "scheduled",
            OutlookCatchUpProvenance.Schedule,
            0,
            null,
            host,
            CursorUtc.AddMinutes(10),
            CursorUtc,
            fencingToken);
        var folder = new OutlookHostFolderConfiguration(
            new OutlookCaptureFolderId(Guid.Parse("55555555-5555-5555-5555-555555555555")),
            new OutlookFolderIdentity("store", "folder", "Inbox"),
            OutlookIncrementalBasis.LastModificationTime,
            CursorUtc,
            new string('b', 64),
            TimeSpan.FromMinutes(5),
            spoolRoot);
        return (new OutlookHostCatchUpWork(claim, true, [folder]), folder);
    }

    private sealed class FakeReadyIngestionService : IOutlookReadyExportIngestionService
    {
        public long? RejectedFencingToken { get; init; }
        public List<OutlookExportCommitRequest> Requests { get; } = [];
        public List<Guid> ExportIds { get; } = [];

        public async ValueTask<OutlookExportCommitReceipt> IngestReadyAsync(
            string spoolRoot,
            Guid exportId,
            CancellationToken cancellationToken)
        {
            var request = await new OutlookSpoolLayout(spoolRoot)
                .ReadReadyIngestionRequestAsync(exportId, cancellationToken);
            Requests.Add(request);
            ExportIds.Add(exportId);
            if (request.FencingToken == RejectedFencingToken)
            {
                throw new OutlookReadyExportLeaseException();
            }

            return new OutlookExportCommitReceipt(new OutlookCaptureExportId(exportId), true, true, false);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("flux-outlook-host-").FullName;
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
