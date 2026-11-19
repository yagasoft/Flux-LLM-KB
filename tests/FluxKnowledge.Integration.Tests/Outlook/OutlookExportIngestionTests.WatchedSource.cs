using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Integrations.Documents;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Search;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Outlook;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Outlook;

public sealed partial class OutlookExportIngestionTests
{
    [NativeSqlServerFact]
    public async Task Watched_cutover_reuses_capture_receipts_and_publishes_through_the_normal_source_pipeline()
    {
        var ct = CancellationToken.None;
        var spoolRoot = CreateTemporaryDirectory();
        var watchRoot = CreateTemporaryDirectory();
        var stageRoot = CreateTemporaryDirectory();
        try
        {
            var licencePath = Path.Combine(spoolRoot, "test.lic");
            await File.WriteAllTextAsync(licencePath, "test-only licence", ct);
            var scannedPdf = WatchedPdf("0 0 0 rg\n72 680 100 20 re f\n");
            var nativePdf = WatchedPdf("BT /F1 12 Tf 72 720 Td (Native sargassum attachment) Tj ET\n");
            var ocr = new WatchedScanOcrResult(Sha256(scannedPdf));
            await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(_fixture, "Unrelated baseline",
                configureServices: services =>
                {
                    services.AddSingleton(new SyncfusionLicenceRegistration(() => licencePath, _ => { }));
                    services.AddSingleton<IDocumentOcrResultReader>(ocr);
                });
            var seed = await SeedCaptureAsync(spoolRoot);
            var legacy = await PrepareExportAsync(spoolRoot, seed, "watched-backfill-entry", Now.AddMinutes(1),
                "Watched quartz eulachon", [
                    ("old.bin", "Backfilled abalone record", "text/csv"),
                    ("native.bin", nativePdf, "application/pdf"),
                    ("scan.bin", scannedPdf, "application/pdf")
                ]);
            var ingestion = CreateService(spoolRoot);
            Assert.True((await ingestion.IngestReadyAsync(spoolRoot, legacy.ExportId, ct)).Accepted);

            var policy = new SourceRootPathPolicy(new LocalIngressOptions([watchRoot]));
            var commands = new NativeCorpusCommandService(
                new NativeOperationService(new SqlNativeOperationStore(environment.Factory, TimeProvider.System), []),
                new SqlNativeCorpusActionStore(environment.Factory, policy, new LocalPrivateContentDisclosure()));
            var request = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new
            {
                path = watchRoot, displayName = "Private watched mail", recursive = true, maximumFileBytes = 1048576
            }));
            var preview = await commands.PreviewAsync(request, "outlook-test", ct);
            await commands.CommitAsync(request, preview.ConfirmationId, Guid.NewGuid().ToString("N"), "outlook-test", ct);
            Guid rootId;
            await using (var setup = CreateContext())
                rootId = await setup.SourceRootConfigurations.Where(root => root.CanonicalPath == watchRoot).Select(root => root.Id).SingleAsync(ct);

            var store = new SqlOutlookWatchedPublicationStore(environment.Factory);
            await store.BindAsync(seed.ProfileId, rootId, watchRoot, stageRoot, ct);
            var fresh = await PrepareExportAsync(spoolRoot, seed, "watched-new-entry", Now.AddMinutes(2),
                "Watched fresh trilobite", [], recordDisplayMetadata: true);
            Assert.True((await ingestion.IngestReadyAsync(spoolRoot, fresh.ExportId, ct)).Accepted);

            var spoolPolicy = PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spoolRoot);
            using var retained = new SqlRetainedSourceReader(environment.Factory, environment.ArtifactRoot, spoolPolicy);
            var reader = new SqlOutlookWatchedBundleReader(environment.Factory, retained, spoolPolicy);
            var writer = new OutlookWatchedBundleWriter(watchRoot, stageRoot);
            var candidates = await store.ReadPendingAsync(10, ct);
            Assert.Contains(candidates, candidate => candidate.ExportId == legacy.ExportId);
            Assert.Contains(candidates, candidate => candidate.ExportId == fresh.ExportId);
            foreach (var candidate in candidates.Where(candidate => candidate.ProfileId == seed.ProfileId))
            {
                var bundle = await reader.ReadAsync(candidate.ProfileId, candidate.ExportId, ct);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkPublishedAsync(candidate, ct));
                var interrupted = candidate.ExportId == legacy.ExportId;
                var publication = writer.PublishAsync(bundle, ct, async (proof, token) =>
                {
                    Assert.False(Directory.Exists(Path.Combine(watchRoot, candidate.ExportId.ToString("N"))));
                    var prepared = await store.PrepareWithdrawalAsync(candidate, proof, token);
                    if (interrupted) throw new IOException("Interrupted before the directory move");
                    return prepared;
                });
                if (interrupted)
                    await Assert.ThrowsAsync<IOException>(() => publication);
                else
                    Assert.True(await publication);

                var pending = Assert.Single(await store.ReadPendingAsync(10, ct), item => item.ExportId == candidate.ExportId);
                Assert.NotNull(pending.PreparedBundle);
                Assert.Equal(interrupted, await writer.ResumeAsync(pending.PreparedBundle, ct));
                await store.MarkPublishedAsync(candidate, ct);
            }

            Assert.DoesNotContain(await store.ReadPendingAsync(10, ct), candidate => candidate.ProfileId == seed.ProfileId);
            Assert.Equal(2, Directory.GetDirectories(watchRoot).Length);
            Assert.Empty(Directory.EnumerateFileSystemEntries(stageRoot));
            var scans = new SqlSourceScanStore(environment.Factory, TimeProvider.System);
            var claim = Assert.IsType<ClaimedSourceScan>(await scans.ClaimNextReleasedAsync(
                "outlook-test", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), ct));
            Assert.Equal(rootId, claim.SourceRoot.Id.Value);
            using var artifacts = new ContentAddressedSourceArtifactStore(environment.ArtifactRoot);
            var worker = new SourceScanWorker(new LocalSourceEnumerator(), scans, artifacts,
                new SqlSourceActivityStore(environment.Factory, TimeProvider.System),
                new RetainedTextActivityPlanner(new SqlRetainedTextRegistrationStore(environment.Factory, TimeProvider.System,
                    sourceAdmission: new FixtureSourceAdmission(environment.Factory))));
            var scan = await worker.ScanAsync(claim.SourceRoot, claim.ScanRequest, ct);
            await scans.CompleteAsync(claim, scan, null, ct);
            await environment.PumpAsync();

            using var fsRetained = new SqlRetainedSourceReader(environment.Factory, environment.ArtifactRoot);
            var artifactWriter = new SqlRetainedArtifactWriter(environment.Factory, environment.ArtifactRoot);
            var admission = new FixtureSourceAdmission(environment.Factory);
            var pdfActivation = new RetainedProcessorActivationService(
                new SourceCapabilityService(new SqlSourceActivityStore(environment.Factory, TimeProvider.System),
                    new LocalSourceCapabilityHandlerRegistry([new PdfDocumentCapabilityHandler()])),
                new SqlRetainedProcessorBranchStore(environment.Factory, TimeProvider.System, sourceAdmission: admission),
                fsRetained, new ZipArchiveRetainedProcessor(artifactWriter), new(), TimeProvider.System,
                pdfProcessor: new PdfDocumentProcessor(artifactWriter));
            await pdfActivation.RunOnceAsync(ct);
            await new SqlRetainedTextRegistrationStore(environment.Factory, TimeProvider.System, environment.ArtifactRoot,
                sourceAdmission: admission).OfferUnlinkedInProcessActivitiesAsync(ct);
            await environment.PumpAsync();
            Assert.Equal(1, ocr.ReadCount);
            await SqlTestData.WaitForFullTextAsync(environment.Factory);

            var retrieval = new CorpusRetrievalService(new SqlCorpusRetrievalReader(environment.Factory),
                new ScopedCorpusRetrievalTests.TestEvidenceCodec(), new LocalPrivateContentDisclosure());
            foreach (var keyword in new[] { "eulachon", "abalone", "trilobite", "sargassum", "nautilus" })
            {
                var hit = Assert.Single((await retrieval.SearchAsync(new CorpusSearchRequest(keyword, 5, "root", rootId, null), ct)).Results);
                var read = await retrieval.ReadAsync(new CorpusReadRequest(hit.EvidenceRef, 1024), ct);
                Assert.Contains(keyword, read.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Empty((await retrieval.SearchAsync(new CorpusSearchRequest(keyword, 5, "root", seed.SourceRootId, null), ct)).Results);
            }

            await using var verification = CreateContext();
            var newReceipt = await verification.OutlookCaptureExports.SingleAsync(row => row.Id == fresh.ExportId, ct);
            var newBody = await verification.SourceRevisions.SingleAsync(row => row.ParentSourceRevisionId == newReceipt.SourceRevisionId, ct);
            Assert.NotNull(newReceipt.WatchedPublishedAtUtc);
            Assert.NotNull(newBody.SuppressedAtUtc);
            Assert.False(await verification.SourceActivities.AnyAsync(activity => activity.SourceRevisionId == newBody.Id, ct));
            Assert.Equal(Now.AddMinutes(2), await verification.OutlookCaptureFolders.Where(folder => folder.Id == seed.FolderId)
                .Select(folder => folder.CursorUtc).SingleAsync(ct));
            Assert.True(Directory.Exists(legacy.ReadyDirectory));

            var appliedMigrations = (await verification.Database.GetAppliedMigrationsAsync(ct)).ToArray();
            Assert.EndsWith("_BindOutlookWatchedSources", appliedMigrations[^1], StringComparison.Ordinal);
            var down = await Assert.ThrowsAsync<SqlException>(() =>
                verification.Database.GetService<IMigrator>().MigrateAsync(appliedMigrations[^2], ct));
            Assert.Equal(51000, down.Number);
            Assert.NotNull(await verification.OutlookCaptureExports.Where(row => row.Id == fresh.ExportId)
                .Select(row => row.WatchedPreparedBundleJson).SingleAsync(ct));
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
            Directory.Delete(watchRoot, recursive: true);
            Directory.Delete(stageRoot, recursive: true);
        }
    }
    private static string WatchedPdf(string contents)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {contents.Length} >>\nstream\n{contents}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        var document = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var item in objects)
        {
            offsets.Add(document.Length);
            document.Append(offsets.Count).Append(" 0 obj\n").Append(item).Append("\nendobj\n");
        }

        var xref = document.Length;
        document.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            document.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        return document.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n").ToString();
    }

    private sealed class WatchedScanOcrResult(string expectedContentHash) : IDocumentOcrResultReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<DocumentOcrExecutionResult?> ReadCompletedAsync(
            ClaimedJob parentJob, string contentSha256, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedContentHash, contentSha256);
            ReadCount++;
            return ValueTask.FromResult<DocumentOcrExecutionResult?>(new(true, "completed",
                [new(0, 0, [new("text", 1, 1, 100, 12, "Scanned nautilus attachment")])]));
        }
    }
}
