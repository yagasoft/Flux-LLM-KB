using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Outlook;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class OutlookProjectionReaderIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Profile_save_rejects_a_stale_revision_or_unrelated_browse_result()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options;
        IDbContextFactory<FluxKnowledgeDbContext> factory = new TestDbContextFactory(options);
        var store = new SqlOutlookCaptureStore(factory, new FixedTimeProvider(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));
        var profileId = Guid.NewGuid();
        var sourceRootId = Guid.NewGuid();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = sourceRootId,
                CanonicalPath = $"C:\\.fluxknowledge-private\\outlook\\{sourceRootId:N}",
                DisplayName = "Private Outlook capture",
                State = 1,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[]",
                MaximumFileBytes = 64L * 1024 * 1024,
                ReconciliationCadenceSeconds = 86400,
                ConfigurationRevision = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            seed.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity
            {
                Id = profileId,
                SourceRootId = sourceRootId,
                DisplayName = "Current mailbox",
                SpoolRoot = "C:\\private\\current-spool",
                IncrementalBasis = (int)OutlookIncrementalBasis.LastModificationTime,
                State = (int)OutlookCaptureState.Disabled,
                IsEnabled = false,
                ConfigurationRevision = 8,
                CadenceTicks = TimeSpan.FromMinutes(15).Ticks,
                MaximumOverlapTicks = TimeSpan.FromMinutes(2).Ticks,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        FluxKnowledge.Application.Contracts.OutlookProfileSaveRequest Request(long revision, Guid correlation) => new(
                Guid.NewGuid(),
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())),
                new OutlookCaptureProfileId(profileId),
                "Stale mailbox",
                OutlookIncrementalBasis.LastModificationTime,
                new FluxKnowledge.Application.Contracts.OutlookCaptureSchedule(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2)),
                new FluxKnowledge.Application.Contracts.OutlookSpoolValidation(new string('e', 64), true, true, true, true, "C:\\private\\spool"),
                Enable: true,
                ExpectedConfigurationRevision: revision,
                BrowseCorrelationId: correlation);

        var staleRevision = await store.SaveProfileAsync(Request(7, Guid.NewGuid()), CancellationToken.None);
        var unrelatedBrowse = await store.SaveProfileAsync(Request(8, Guid.NewGuid()), CancellationToken.None);

        Assert.False(staleRevision.Accepted);
        Assert.False(unrelatedBrowse.Accepted);
        await using var context = await factory.CreateDbContextAsync();
        Assert.Equal(8, (await context.OutlookCaptureProfiles.SingleAsync(profile => profile.Id == profileId)).ConfigurationRevision);
    }

    [NativeSqlServerFact]
    public async Task Durable_Outlook_mutation_appends_only_sanitised_audit_evidence()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options;
        IDbContextFactory<FluxKnowledgeDbContext> factory = new TestDbContextFactory(options);
        var store = new SqlOutlookCaptureStore(
            factory,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));
        var operationId = Guid.NewGuid();
        const string privateSpool = "C:\\private\\audit-outlook-spool";

        await store.SaveProfileAsync(
            new FluxKnowledge.Application.Contracts.OutlookProfileSaveRequest(
                operationId,
                new string('a', 64),
                null,
                "Audited mailbox",
                OutlookIncrementalBasis.LastModificationTime,
                new FluxKnowledge.Application.Contracts.OutlookCaptureSchedule(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2)),
                new FluxKnowledge.Application.Contracts.OutlookSpoolValidation(new string('b', 64), true, true, true, true, privateSpool)),
            CancellationToken.None);

        await using var context = await factory.CreateDbContextAsync();
        var audit = await context.AuditEvents.SingleAsync(row => row.CorrelationId == $"outlook-operation:{operationId:N}");
        Assert.Equal("outlook.save_profile", audit.EventType);
        Assert.DoesNotContain(privateSpool, audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreId", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EntryID", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [NativeSqlServerFact]
    public async Task Projection_shows_folder_and_spool_status_but_not_entry_ids_or_raw_diagnostics()
    {
        await using var sql = new NativeSqlServerFixture();
        await sql.InitializeAsync();
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(sql.ConnectionString).Options;
        IDbContextFactory<FluxKnowledgeDbContext> factory = new TestDbContextFactory(options);
        var profileId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var sourceRootId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        const string privateSpool = "C:\\private\\secret-outlook-spool";
        const string storeId = "private-store-id";
        const string folderEntryId = "private-folder-entry-id";
        const string entryId = "private-message-entry-id";
        const string rawDiagnostic = "COM exception included a private mailbox path";

        await using (var context = await factory.CreateDbContextAsync())
        {
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = sourceRootId,
                CanonicalPath = $"C:\\.fluxknowledge-private\\outlook\\{sourceRootId:N}",
                DisplayName = "Private Outlook capture",
                State = 1,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[]",
                MaximumFileBytes = 64L * 1024 * 1024,
                ReconciliationCadenceSeconds = 86400,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            context.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity
            {
                Id = profileId,
                SourceRootId = sourceRootId,
                DisplayName = "Operations mailbox",
                SpoolRoot = privateSpool,
                IncrementalBasis = (int)OutlookIncrementalBasis.LastModificationTime,
                State = (int)OutlookCaptureState.Ready,
                IsEnabled = true,
                ConfigurationRevision = 3,
                CadenceTicks = TimeSpan.FromMinutes(15).Ticks,
                MaximumOverlapTicks = TimeSpan.FromMinutes(2).Ticks,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            context.OutlookCaptureFolders.Add(new OutlookCaptureFolderEntity
            {
                Id = folderId,
                ProfileId = profileId,
                StoreId = storeId,
                FolderEntryId = folderEntryId,
                DisplayName = "Selected inbox",
                Basis = (int)OutlookIncrementalBasis.LastModificationTime,
                CursorUtc = now.AddMinutes(-5),
                CursorFingerprint = new string('b', 64),
                State = (int)OutlookCaptureState.Ready
            });
            context.OutlookCaptureExports.AddRange(
                Export(OutlookExportState.Ingested),
                Export(OutlookExportState.Deferred),
                Export(OutlookExportState.Blocked));
            context.AuditEvents.Add(new AuditEventEntity
            {
                EventFamily = "outlook",
                EventType = "outlook.host_failed",
                Actor = "test",
                DetailsJson = JsonSerializer.Serialize(new { rawDiagnostic, storeId, entryId }),
                Severity = "error",
                OccurredAtUtc = now
            });
            await context.SaveChangesAsync();
        }

        var projection = await new SqlOutlookProjectionReader(
                factory,
                new SafeSpoolHealthReader(),
                PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(privateSpool))
            .ReadAsync(CancellationToken.None);
        var json = JsonSerializer.Serialize(projection);

        var profile = Assert.Single(projection.Profiles);
        Assert.Equal("Operations mailbox", profile.DisplayName);
        Assert.Equal("Configured", profile.Spool.Status);
        Assert.Equal("Healthy", profile.Spool.Health);
        var folder = Assert.Single(profile.Folders);
        Assert.Equal("Selected inbox", folder.DisplayName);
        Assert.Equal(1, folder.IngestedCount);
        Assert.Equal(1, folder.DeferredCount);
        Assert.Equal(1, folder.BlockedCount);
        Assert.DoesNotContain(privateSpool, json, StringComparison.Ordinal);
        Assert.DoesNotContain(storeId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(folderEntryId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(entryId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(rawDiagnostic, json, StringComparison.Ordinal);

        var overview = await new SqlProjectionReader(
            factory,
            new HealthyRecoveryStatus(),
            new SqlGpuSchedulerStore(factory)).ReadOverviewAsync(CancellationToken.None);
        Assert.Equal(1, overview.OutlookCapture.ProfileCount);
        Assert.Equal(1, overview.OutlookCapture.EnabledProfileCount);
        Assert.Equal(1, overview.OutlookCapture.FolderCount);
        Assert.Equal(1, overview.OutlookCapture.IngestedCount);
        Assert.Equal(1, overview.OutlookCapture.DeferredCount);
        Assert.Equal(1, overview.OutlookCapture.BlockedCount);

        OutlookCaptureExportEntity Export(OutlookExportState state) => new()
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            FolderId = folderId,
            EntryId = entryId + state,
            SourceFingerprint = new string('c', 64),
            State = (int)state,
            FencingToken = 1
        };
    }

    private sealed class SafeSpoolHealthReader : IOutlookSpoolHealthReader
    {
        public ValueTask<OutlookSpoolStatus> ReadAsync(string privateSpoolRoot, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OutlookSpoolStatus("Configured", "Healthy", "Sufficient"));
    }

    private sealed class HealthyRecoveryStatus : IDerivedIndexRecoveryStatus
    {
        public DerivedIndexRecoverySnapshot Snapshot { get; } = new(
            DerivedIndexRecoveryState.Healthy,
            null,
            null,
            null,
            null,
            0);
    }

    private sealed class TestDbContextFactory(DbContextOptions<FluxKnowledgeDbContext> options)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
