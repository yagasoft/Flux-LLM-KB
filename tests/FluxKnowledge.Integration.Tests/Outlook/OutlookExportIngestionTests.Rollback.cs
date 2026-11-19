using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Outlook;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Outlook;

public sealed partial class OutlookExportIngestionTests
{
    [NativeSqlServerFact]
    public async Task Watched_rollback_pauses_the_bound_root_and_restores_only_unchanged_cutover_suppressions()
    {
        var ct = CancellationToken.None;
        var spoolRoot = CreateTemporaryDirectory();
        var watchRoot = CreateTemporaryDirectory();
        var stageRoot = CreateTemporaryDirectory();
        var withdrawalAt = Now.AddMinutes(3);
        var independentSuppressionAt = Now.AddMinutes(4);
        var rollbackAt = Now.AddMinutes(5);
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var export = await PrepareExportAsync(spoolRoot, seed, "rollback-entry", Now.AddMinutes(1), "Legacy body",
                [("independently-hidden.txt", "Independent suppression", "text/plain"),
                 ("later-hidden.txt", "Later suppression", "text/plain")]);
            Assert.True((await CreateService(spoolRoot).IngestReadyAsync(export.Request, ct)).Accepted);

            var destinationId = Guid.NewGuid();
            var destinationRevisionId = Guid.NewGuid();
            Guid parentId;
            Guid bodyId;
            Guid preexistingSuppressionId;
            Guid laterSuppressionId;
            await using (var setup = CreateContext())
            {
                setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
                {
                    Id = destinationId,
                    CanonicalPath = watchRoot,
                    DisplayName = "Private watched rollback test",
                    State = (int)SourceRootState.Enabled,
                    Recursive = true,
                    MaximumFileBytes = 1024 * 1024,
                    ReconciliationCadenceSeconds = 86400,
                    ConfigurationRevision = 1,
                    CreatedAtUtc = Now,
                    UpdatedAtUtc = Now
                });
                var legacy = await setup.SourceRevisions.Where(row => row.SourceRootId == seed.SourceRootId).ToArrayAsync(ct);
                parentId = legacy.Single(row => row.ParentSourceRevisionId == null).Id;
                bodyId = legacy.Single(row => row.StableSourceIdentity.EndsWith(":body", StringComparison.Ordinal)).Id;
                var earlier = legacy.Single(row => row.StableSourceIdentity.EndsWith(":attachment-1", StringComparison.Ordinal));
                earlier.SuppressedAtUtc = withdrawalAt;
                preexistingSuppressionId = earlier.Id;
                laterSuppressionId = legacy.Single(row => row.StableSourceIdentity.EndsWith(":attachment-2", StringComparison.Ordinal)).Id;
                await setup.SaveChangesAsync(ct);
            }

            var factory = new ContextFactory(_fixture.ConnectionString);
            var store = new SqlOutlookWatchedPublicationStore(factory, new ManualTimeProvider(withdrawalAt));
            await store.BindAsync(seed.ProfileId, destinationId, watchRoot, stageRoot, ct);
            var candidate = Assert.Single(await store.ReadPendingAsync(100, ct), row => row.ExportId == export.ExportId);
            var writer = new OutlookWatchedBundleWriter(watchRoot, stageRoot);
            Assert.True(await writer.PublishAsync(new OutlookWatchedBundle(export.ExportId, null, null, null, "Legacy body", []),
                ct, (receipt, token) => store.PrepareWithdrawalAsync(candidate, receipt, token)));
            await store.MarkPublishedAsync(candidate, ct);

            string? withdrawalReceipt;
            long previousCorpusVersion;
            await using (var change = CreateContext())
            {
                var later = await change.SourceRevisions.SingleAsync(row => row.Id == laterSuppressionId, ct);
                later.SuppressedAtUtc = independentSuppressionAt;
                change.SourceRevisions.Add(new SourceRevisionEntity
                {
                    Id = destinationRevisionId,
                    SourceRootId = destinationId,
                    StableSourceIdentity = "watched-test-publication",
                    Revision = 1,
                    ContentSha256 = Sha256("watched bytes"),
                    CanonicalPath = Path.Combine(watchRoot, export.ExportId.ToString("N"), "message.md"),
                    Classification = "AcceptedUtf8Text",
                    Extension = ".md",
                    ByteLength = 13,
                    DiscoveredAtUtc = Now
                });
                await change.SaveChangesAsync(ct);
                previousCorpusVersion = await change.IndexState.Select(row => row.CorpusVersion).SingleAsync(ct);
                withdrawalReceipt = await change.OutlookCaptureExports.Where(row => row.Id == export.ExportId)
                    .Select(row => row.WatchedWithdrawalRevisionIdsJson).SingleAsync(ct);
            }

            var rollback = new SqlOutlookWatchedPublicationStore(factory, new ManualTimeProvider(rollbackAt));
            await Task.WhenAll(rollback.SuspendAndRestoreLegacyAsync(seed.ProfileId, ct),
                rollback.SuspendAndRestoreLegacyAsync(seed.ProfileId, ct));

            await using var verification = CreateContext();
            var profile = await verification.OutlookCaptureProfiles.SingleAsync(row => row.Id == seed.ProfileId, ct);
            Assert.Equal(rollbackAt, profile.WatchedPublicationSuspendedAtUtc);
            Assert.Equal((int)SourceRootState.Paused, await verification.SourceRootConfigurations.Where(row => row.Id == destinationId)
                .Select(row => row.State).SingleAsync(ct));
            var revisions = await verification.SourceRevisions.Where(row => row.SourceRootId == seed.SourceRootId ||
                row.SourceRootId == destinationId).ToDictionaryAsync(row => row.Id, row => row.SuppressedAtUtc, ct);
            Assert.Null(revisions[parentId]);
            Assert.Null(revisions[bodyId]);
            Assert.Equal(withdrawalAt, revisions[preexistingSuppressionId]);
            Assert.Equal(independentSuppressionAt, revisions[laterSuppressionId]);
            Assert.Equal(rollbackAt, revisions[destinationRevisionId]);
            Assert.Equal(previousCorpusVersion + 1, await verification.IndexState.Select(row => row.CorpusVersion).SingleAsync(ct));
            var receipt = await verification.OutlookCaptureExports.SingleAsync(row => row.Id == export.ExportId, ct);
            Assert.Equal(withdrawalAt, receipt.WatchedPublishedAtUtc);
            Assert.Equal(withdrawalAt, receipt.WatchedWithdrawalAtUtc);
            Assert.Equal(withdrawalReceipt, receipt.WatchedWithdrawalRevisionIdsJson);
            Assert.True(File.Exists(Path.Combine(export.ReadyDirectory, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(watchRoot, export.ExportId.ToString("N"), "message.md")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkPublishedAsync(candidate, ct));
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
            Directory.Delete(watchRoot, recursive: true);
            Directory.Delete(stageRoot, recursive: true);
        }
    }
}
