using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Outlook;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Outlook;

public sealed class OutlookExportIngestionTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-11T12:00:00+00:00");
    private const string RequestFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SourceFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CursorFingerprint = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Ready_manifest_reconstructs_ingestion_after_process_restart()
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var exportId = Guid.NewGuid();
            var operationId = Guid.NewGuid();
            var cursorUtc = Now.AddMinutes(1);
            var layout = new OutlookSpoolLayout(spoolRoot);
            var draft = layout.CreateInflightExportDirectory(exportId);
            await File.WriteAllTextAsync(Path.Combine(draft, "body.txt"), "restart body", new UTF8Encoding(false));
            await layout.WriteManifestAsync(
                draft,
                OutlookExportManifest.Create(
                    exportId,
                    "body.txt",
                    [],
                    new OutlookReadyExportRecovery(
                        operationId,
                        RequestFingerprint,
                        seed.CatchUpId,
                        seed.FencingToken,
                        seed.ProfileId,
                        seed.FolderId,
                        "entry-restart",
                        SourceFingerprint,
                        cursorUtc,
                        CursorFingerprint)),
                CancellationToken.None);
            _ = await layout.PromoteAsync(exportId, CancellationToken.None);

            var receipt = await CreateService(spoolRoot).IngestReadyAsync(spoolRoot, exportId, CancellationToken.None);

            Assert.True(receipt.Accepted);
            Assert.Equal(exportId, receipt.ExportId.Value);
            await using var context = CreateContext();
            var operation = await context.OutlookCaptureOperations.SingleAsync(row => row.OperationId == operationId);
            Assert.True(operation.Accepted);
            Assert.Equal(exportId, operation.ResourceId);
            var export = await context.OutlookCaptureExports.SingleAsync(row => row.Id == exportId);
            Assert.Equal(seed.ProfileId, export.ProfileId);
            Assert.Equal(seed.FolderId, export.FolderId);
            Assert.Equal("entry-restart", export.EntryId);
            Assert.Equal(SourceFingerprint, export.SourceFingerprint);
            Assert.Equal(cursorUtc, await context.OutlookCaptureFolders
                .Where(row => row.Id == seed.FolderId)
                .Select(row => row.CursorUtc)
                .SingleAsync());
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Ready_export_ingestion_commits_with_the_production_retrying_execution_strategy()
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot,
                seed,
                "entry-retrying-strategy",
                Now.AddMinutes(1),
                "message body",
                []);

            var receipt = await CreateService(spoolRoot, useRetryingExecutionStrategy: true)
                .IngestReadyAsync(prepared.Request, CancellationToken.None);

            Assert.True(receipt.Accepted);
            Assert.False(receipt.IsReplay);
            await using var context = CreateContext();
            Assert.Single(await context.OutlookCaptureExports
                .Where(row => row.Id == prepared.ExportId)
                .ToListAsync());
            Assert.Equal(Now.AddMinutes(1), await context.OutlookCaptureFolders
                .Where(row => row.Id == seed.FolderId)
                .Select(row => row.CursorUtc)
                .SingleAsync());
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Malformed_ready_export_ingestion_commits_with_the_production_retrying_execution_strategy()
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot,
                seed,
                "entry-retrying-malformed-recovery",
                Now.AddMinutes(1),
                "message body",
                []);
            var manifestPath = Path.Combine(prepared.ReadyDirectory, "manifest.json");
            var manifest = JsonSerializer.Deserialize<OutlookExportManifest>(
                await File.ReadAllTextAsync(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(
                    manifest with { Recovery = manifest.Recovery with { CursorFingerprint = "not-a-canonical-sha256" } },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                new UTF8Encoding(false));

            var receipt = await CreateService(spoolRoot, useRetryingExecutionStrategy: true)
                .IngestReadyAsync(spoolRoot, prepared.ExportId, CancellationToken.None);

            Assert.False(receipt.Accepted);
            Assert.True(receipt.Committed);
            await using var context = CreateContext();
            Assert.Equal((int)OutlookExportState.Blocked, await context.OutlookCaptureExports
                .Where(row => row.Id == prepared.ExportId)
                .Select(row => row.State)
                .SingleAsync());
            Assert.Null(await context.OutlookCaptureFolders
                .Where(row => row.Id == seed.FolderId)
                .Select(row => row.CursorUtc)
                .SingleAsync());
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Malformed_json_valid_recovery_is_durably_blocked_and_replays_without_advancing_cursor()
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot,
                seed,
                "entry-malformed-recovery",
                Now.AddMinutes(1),
                "message body",
                []);
            var manifestPath = Path.Combine(prepared.ReadyDirectory, "manifest.json");
            var manifest = JsonSerializer.Deserialize<OutlookExportManifest>(
                await File.ReadAllTextAsync(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var malformed = manifest with
            {
                Recovery = manifest.Recovery with { CursorFingerprint = "not-a-canonical-sha256" }
            };
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(malformed, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                new UTF8Encoding(false));

            var receipt = await CreateService(spoolRoot).IngestReadyAsync(spoolRoot, prepared.ExportId, CancellationToken.None);

            Assert.False(receipt.Accepted);
            Assert.True(receipt.Committed);
            Assert.False(receipt.IsReplay);
            Assert.True(Directory.Exists(prepared.ReadyDirectory));
            await using (var context = CreateContext())
            {
                var blocked = await context.OutlookCaptureExports.SingleAsync(row => row.Id == prepared.ExportId);
                Assert.Equal((int)OutlookExportState.Blocked, blocked.State);
                Assert.Equal("ready-manifest-recovery-invalid", blocked.BlockedReasonCode);
                Assert.Null(blocked.SourceRevisionId);
                Assert.Null(await context.OutlookCaptureFolders
                    .Where(row => row.Id == seed.FolderId)
                    .Select(row => row.CursorUtc)
                    .SingleAsync());
                Assert.Empty(await context.SourceRevisions
                    .Where(row => row.SourceRootId == seed.SourceRootId)
                    .ToListAsync());
                var audit = await context.AuditEvents.SingleAsync(row =>
                    row.EventType == "outlook.export_blocked" && row.CorrelationId == $"outlook-export:{prepared.ExportId:N}");
                Assert.Equal("{\"reasonCode\":\"ready-manifest-recovery-invalid\"}", audit.DetailsJson);
                Assert.DoesNotContain("not-a-canonical", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
            }

            var replay = await CreateService(spoolRoot).IngestReadyAsync(spoolRoot, prepared.ExportId, CancellationToken.None);
            Assert.True(replay.IsReplay);
            Assert.False(replay.Accepted);
            Assert.Equal(receipt.ExportId, replay.ExportId);
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerTheory]
    [InlineData("empty-profile", "ready-manifest-recovery-invalid")]
    [InlineData("empty-folder", "ready-manifest-recovery-invalid")]
    [InlineData("unknown-profile", "ready-manifest-identity-mismatch")]
    [InlineData("unknown-folder", "ready-manifest-identity-mismatch")]
    [InlineData("mismatched", "ready-manifest-identity-mismatch")]
    public async Task Unresolvable_recovery_identity_is_durably_blocked_without_exposing_raw_identifiers(
        string identityClass,
        string expectedReasonCode)
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot,
                seed,
                $"entry-{identityClass}-identity",
                Now.AddMinutes(1),
                "message body",
                []);
            var manifestPath = Path.Combine(prepared.ReadyDirectory, "manifest.json");
            var manifest = JsonSerializer.Deserialize<OutlookExportManifest>(
                await File.ReadAllTextAsync(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var malformedRecovery = identityClass switch
            {
                "empty-profile" => manifest.Recovery with { ProfileId = Guid.Empty },
                "empty-folder" => manifest.Recovery with { FolderId = Guid.Empty },
                "unknown-profile" => manifest.Recovery with { ProfileId = Guid.NewGuid() },
                "unknown-folder" => manifest.Recovery with { FolderId = Guid.NewGuid() },
                "mismatched" => manifest.Recovery with
                {
                    FolderId = (await SeedCaptureAsync(spoolRoot)).FolderId
                },
                _ => throw new InvalidOperationException("Unknown test identity class.")
            };
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(
                    manifest with { Recovery = malformedRecovery },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                new UTF8Encoding(false));

            var receipt = await CreateService(spoolRoot).IngestReadyAsync(spoolRoot, prepared.ExportId, CancellationToken.None);

            Assert.False(receipt.Accepted);
            Assert.True(receipt.Committed);
            Assert.False(receipt.IsReplay);
            await using (var context = CreateContext())
            {
                var blocked = await context.OutlookCaptureExports.SingleAsync(row => row.Id == prepared.ExportId);
                Assert.Equal((int)OutlookExportState.Blocked, blocked.State);
                Assert.Equal(expectedReasonCode, blocked.BlockedReasonCode);
                Assert.Null((Guid?)blocked.ProfileId);
                Assert.Null((Guid?)blocked.FolderId);
                Assert.Null(blocked.SourceRevisionId);
                Assert.Null(await context.OutlookCaptureFolders
                    .Where(row => row.Id == seed.FolderId)
                    .Select(row => row.CursorUtc)
                    .SingleAsync());
                Assert.Empty(await context.SourceRevisions
                    .Where(row => row.SourceRootId == seed.SourceRootId)
                    .ToListAsync());
                var operation = await context.OutlookCaptureOperations.SingleAsync(row => row.ResourceId == blocked.Id);
                Assert.Null(operation.ProfileId);
                var audit = await context.AuditEvents.SingleAsync(row =>
                    row.EventType == "outlook.export_blocked" && row.CorrelationId == $"outlook-blocked:{blocked.ManifestHash}");
                Assert.Null(audit.SourceRootId);
                Assert.Equal($"{{\"reasonCode\":\"{expectedReasonCode}\"}}", audit.DetailsJson);
                if (malformedRecovery.ProfileId != Guid.Empty)
                {
                    Assert.DoesNotContain(malformedRecovery.ProfileId.ToString("N"), audit.CorrelationId, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(malformedRecovery.ProfileId.ToString("N"), audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
                }
                if (malformedRecovery.FolderId != Guid.Empty)
                {
                    Assert.DoesNotContain(malformedRecovery.FolderId.ToString("N"), audit.CorrelationId, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(malformedRecovery.FolderId.ToString("N"), audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
                }
            }

            var replay = await CreateService(spoolRoot).IngestReadyAsync(spoolRoot, prepared.ExportId, CancellationToken.None);
            Assert.True(replay.IsReplay);
            Assert.False(replay.Accepted);
            Assert.Equal(receipt.ExportId, replay.ExportId);
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Complete_message_and_two_attachments_create_parent_child_revisions_and_private_artifacts()
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var exportId = Guid.NewGuid();
            var recovery = Recovery(seed, "entry-complete", Now.AddMinutes(1));
            var layout = new OutlookSpoolLayout(spoolRoot);
            var draft = layout.CreateInflightExportDirectory(exportId);
            await File.WriteAllTextAsync(Path.Combine(draft, "body.txt"), "message body", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(draft, "attachment-one.txt"), "one", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(draft, "attachment-two.txt"), "two", new UTF8Encoding(false));
            await layout.WriteManifestAsync(
                draft,
                OutlookExportManifest.Create(
                    exportId,
                    "body.txt",
                    [
                        OutlookExportSidecar.Create("attachment-one.txt", "text/plain"),
                        OutlookExportSidecar.Create("attachment-two.txt", "text/plain")
                    ],
                    recovery),
                CancellationToken.None);
            var readyDirectory = await layout.PromoteAsync(exportId, CancellationToken.None);
            var manifestHash = await Sha256FileAsync(Path.Combine(readyDirectory, "manifest.json"));
            var request = recovery.ToCommitRequest(exportId, manifestHash);
            var observation = request.Observation!;

            var receipt = await CreateService(spoolRoot).IngestReadyAsync(
                request,
                CancellationToken.None);

            Assert.True(receipt.Accepted);
            Assert.True(receipt.Committed);
            Assert.False(receipt.IsReplay);
            Assert.Equal(exportId, receipt.ExportId.Value);
            Assert.True(Directory.Exists(readyDirectory));

            await using var context = CreateContext();
            var export = await context.OutlookCaptureExports.SingleAsync(row => row.Id == exportId);
            Assert.Equal((int)OutlookExportState.Ingested, export.State);
            Assert.NotNull(export.SourceRevisionId);

            var revisions = await context.SourceRevisions
                .Where(row => row.SourceRootId == seed.SourceRootId)
                .OrderBy(row => row.CanonicalPath)
                .ToListAsync();
            Assert.Equal(4, revisions.Count);
            var parent = Assert.Single(revisions, row => row.Id == export.SourceRevisionId);
            Assert.Null(parent.ParentSourceRevisionId);
            var children = revisions.Where(row => row.ParentSourceRevisionId == parent.Id).ToArray();
            Assert.Equal(3, children.Length);
            Assert.All(revisions, row => Assert.StartsWith(seed.CanonicalRootPath, row.CanonicalPath, StringComparison.Ordinal));

            var artifacts = await context.SourceArtifacts
                .Where(row => children.Select(child => child.Id).Contains(row.SourceRevisionId))
                .OrderBy(row => row.ContentSha256)
                .ToListAsync();
            Assert.Equal(3, artifacts.Count);
            Assert.Equal(
                new[] { Sha256("one"), Sha256("message body"), Sha256("two") }.Order().ToArray(),
                artifacts.Select(row => row.ContentSha256).Order().ToArray());
            Assert.All(artifacts, artifact =>
            {
                Assert.Equal(Path.Combine("sha256", artifact.ContentSha256[..2], $"{artifact.ContentSha256}.bin"), artifact.StoreRelativePath);
                Assert.True(File.Exists(Path.Combine(spoolRoot, artifact.StoreRelativePath)));
            });

            var activities = await context.SourceActivities
                .Where(row => children.Select(child => child.Id).Contains(row.SourceRevisionId))
                .ToListAsync();
            Assert.Equal(3, activities.Count);
            Assert.All(activities, activity =>
            {
                Assert.Equal((int)SourceActivityKind.TextExtraction, activity.ActivityKind);
                Assert.Equal((int)ExecutionClass.InProcess, activity.ExecutionClass);
                Assert.Equal((int)SourceActivityState.Pending, activity.State);
            });
            var folder = await context.OutlookCaptureFolders.SingleAsync(row => row.Id == seed.FolderId);
            Assert.Equal(observation.CursorUtc, folder.CursorUtc);
            Assert.Equal(observation.CursorFingerprint, folder.CursorFingerprint);
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Accepted_Outlook_text_is_read_from_its_private_profile_spool_by_the_registered_reader()
    {
        var spoolRoot = CreateTemporaryDirectory();
        var sharedArtifactRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot,
                seed,
                "entry-readable-private-text",
                Now.AddMinutes(1),
                "private Outlook text",
                []);

            var receipt = await CreateService(spoolRoot).IngestReadyAsync(
                prepared.Request,
                CancellationToken.None);

            Assert.True(receipt.Accepted);
            Guid bodyRevisionId;
            string relativePath;
            await using (var context = CreateContext())
            {
                var body = await context.SourceRevisions
                    .SingleAsync(row => row.SourceRootId == seed.SourceRootId && row.Classification == "AcceptedUtf8Text");
                bodyRevisionId = body.Id;
                relativePath = await context.SourceArtifacts
                    .Where(row => row.SourceRevisionId == body.Id)
                    .Select(row => row.StoreRelativePath)
                    .SingleAsync();
            }

            Assert.True(File.Exists(Path.Combine(spoolRoot, relativePath)));
            Assert.False(File.Exists(Path.Combine(sharedArtifactRoot, relativePath)));
            using var reader = new SqlRetainedSourceReader(
                new ContextFactory(_fixture.ConnectionString),
                sharedArtifactRoot,
                PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spoolRoot));

            var source = await reader.ReadUtf8Async(
                new SourceRevisionId(bodyRevisionId),
                CancellationToken.None);

            Assert.Equal("private Outlook text", source.Text);
            Assert.Equal(Sha256("private Outlook text"), source.ContentHash);
        }
        finally
        {
            Directory.Delete(sharedArtifactRoot, recursive: true);
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Hash_mismatch_records_blocked_evidence_without_advancing_cursor()
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot, seed, "entry-hash-mismatch", Now.AddMinutes(1), "original body", []);
            await File.WriteAllTextAsync(
                Path.Combine(prepared.ReadyDirectory, "body.txt"),
                "tampered body",
                new UTF8Encoding(false));
            var request = prepared.Request;
            var receipt = await CreateService(spoolRoot).IngestReadyAsync(spoolRoot, prepared.ExportId, CancellationToken.None);

            Assert.False(receipt.Accepted);
            Assert.True(receipt.Committed);
            Assert.True(Directory.Exists(prepared.ReadyDirectory));
            await using var context = CreateContext();
            var blocked = await context.OutlookCaptureExports.SingleAsync(row => row.Id == prepared.ExportId);
            Assert.Equal((int)OutlookExportState.Blocked, blocked.State);
            Assert.Equal("ready-sidecar-checksum-invalid", blocked.BlockedReasonCode);
            Assert.Null(blocked.SourceRevisionId);
            Assert.Empty(await context.SourceRevisions.Where(row => row.SourceRootId == seed.SourceRootId).ToListAsync());
            Assert.Empty(await context.SourceActivities
                .Where(activity => context.SourceRevisions.Any(revision =>
                    revision.Id == activity.SourceRevisionId && revision.SourceRootId == seed.SourceRootId))
                .ToListAsync());
            Assert.Null(await context.OutlookCaptureFolders
                .Where(row => row.Id == seed.FolderId)
                .Select(row => row.CursorUtc)
                .SingleAsync());
            var operation = await context.OutlookCaptureOperations.SingleAsync(row => row.OperationId == request.OperationId);
            Assert.False(operation.Accepted);
            var audit = await context.AuditEvents.SingleAsync(row =>
                row.EventType == "outlook.export_blocked" && row.CorrelationId == $"outlook-export:{prepared.ExportId:N}");
            Assert.Equal("outlook", audit.EventFamily);
            Assert.Equal("warning", audit.Severity);
            Assert.Equal("outlook-ready-ingestion", audit.Actor);
            Assert.Equal(seed.SourceRootId, audit.SourceRootId);
            Assert.Equal("{\"reasonCode\":\"ready-sidecar-checksum-invalid\"}", audit.DetailsJson);
            Assert.DoesNotContain("tampered", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("body.txt", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Conflicting_retained_sidecar_records_blocked_evidence_without_advancing_cursor()
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot, seed, "entry-retained-conflict", Now.AddMinutes(1), "retained body", []);
            var bodyHash = Sha256("retained body");
            var retainedDirectory = Path.Combine(spoolRoot, "sha256", bodyHash[..2]);
            Directory.CreateDirectory(retainedDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(retainedDirectory, $"{bodyHash}.bin"),
                "conflicting bytes",
                new UTF8Encoding(false));
            var request = prepared.Request;
            var receipt = await CreateService(spoolRoot).IngestReadyAsync(request, CancellationToken.None);

            Assert.False(receipt.Accepted);
            Assert.True(receipt.Committed);
            Assert.True(Directory.Exists(prepared.ReadyDirectory));
            await using var context = CreateContext();
            var blocked = await context.OutlookCaptureExports.SingleAsync(row => row.Id == prepared.ExportId);
            Assert.Equal((int)OutlookExportState.Blocked, blocked.State);
            Assert.Equal("retained-sidecar-checksum-invalid", blocked.BlockedReasonCode);
            Assert.Null(blocked.SourceRevisionId);
            Assert.Empty(await context.SourceRevisions.Where(row => row.SourceRootId == seed.SourceRootId).ToListAsync());
            Assert.Null(await context.OutlookCaptureFolders
                .Where(row => row.Id == seed.FolderId)
                .Select(row => row.CursorUtc)
                .SingleAsync());
            Assert.False((await context.OutlookCaptureOperations
                .SingleAsync(row => row.OperationId == request.OperationId)).Accepted);
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Retained_sidecar_link_to_matching_outside_bytes_is_blocked_before_catalogue()
    {
        var spoolRoot = CreateTemporaryDirectory();
        var outside = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot, seed, "entry-retained-link", Now.AddMinutes(1), "linked body", []);
            var bodyHash = Sha256("linked body");
            var retainedDirectory = Path.Combine(spoolRoot, "sha256", bodyHash[..2]);
            Directory.CreateDirectory(retainedDirectory);
            var outsidePath = Path.Combine(outside, "outside.bin");
            await File.WriteAllTextAsync(outsidePath, "linked body", new UTF8Encoding(false));
            File.CreateSymbolicLink(Path.Combine(retainedDirectory, $"{bodyHash}.bin"), outsidePath);

            var receipt = await CreateService(spoolRoot).IngestReadyAsync(prepared.Request, CancellationToken.None);

            Assert.False(receipt.Accepted);
            Assert.Equal("linked body", await File.ReadAllTextAsync(outsidePath));
            await using var context = CreateContext();
            Assert.Equal((int)OutlookExportState.Blocked, await context.OutlookCaptureExports
                .Where(row => row.Id == prepared.ExportId)
                .Select(row => row.State)
                .SingleAsync());
            Assert.Empty(await context.SourceRevisions.Where(row => row.SourceRootId == seed.SourceRootId).ToListAsync());
            Assert.Null(await context.OutlookCaptureFolders
                .Where(row => row.Id == seed.FolderId)
                .Select(row => row.CursorUtc)
                .SingleAsync());
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Sql_failure_rolls_back_catalogue_and_cursor_and_ready_export_retries()
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot, seed, "entry-sql-retry", Now.AddMinutes(2), "retryable body", []);
            var request = prepared.Request;
            var observation = request.Observation!;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateService(spoolRoot, new ThrowOnSecondSaveChangesInterceptor())
                    .IngestReadyAsync(request, CancellationToken.None)
                    .AsTask());

            Assert.True(Directory.Exists(prepared.ReadyDirectory));
            await using (var failed = CreateContext())
            {
                Assert.False(await failed.OutlookCaptureExports.AnyAsync(row => row.Id == prepared.ExportId));
                Assert.False(await failed.OutlookCaptureOperations.AnyAsync(row => row.OperationId == request.OperationId));
                Assert.Empty(await failed.SourceRevisions.Where(row => row.SourceRootId == seed.SourceRootId).ToListAsync());
                Assert.Empty(await failed.SourceActivities
                    .Where(activity => failed.SourceRevisions.Any(revision =>
                        revision.Id == activity.SourceRevisionId && revision.SourceRootId == seed.SourceRootId))
                    .ToListAsync());
                Assert.Null(await failed.OutlookCaptureFolders
                    .Where(row => row.Id == seed.FolderId)
                    .Select(row => row.CursorUtc)
                    .SingleAsync());
            }

            var retry = await CreateService(spoolRoot).IngestReadyAsync(request, CancellationToken.None);

            Assert.True(retry.Accepted);
            await using var succeeded = CreateContext();
            Assert.Equal((int)OutlookExportState.Ingested, await succeeded.OutlookCaptureExports
                .Where(row => row.Id == prepared.ExportId)
                .Select(row => row.State)
                .SingleAsync());
            Assert.Equal(observation.CursorUtc, await succeeded.OutlookCaptureFolders
                .Where(row => row.Id == seed.FolderId)
                .Select(row => row.CursorUtc)
                .SingleAsync());
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Unsupported_attachment_is_deferred_and_replays_after_matching_processor_is_enabled()
    {
        var contentType = $"text/x-flux-deferred-{Guid.NewGuid():N}";
        var requiredCapability = $"outlook-content:{contentType}";
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot,
                seed,
                "entry-deferred",
                Now.AddMinutes(3),
                "supported body",
                [("future-format.txt", "future text", contentType)]);

            var receipt = await CreateService(spoolRoot).IngestReadyAsync(
                prepared.Request,
                CancellationToken.None);

            Assert.True(receipt.Accepted);
            await using (var ingested = CreateContext())
            {
                var export = await ingested.OutlookCaptureExports.SingleAsync(row => row.Id == prepared.ExportId);
                var childIds = await ingested.SourceRevisions
                    .Where(row => row.ParentSourceRevisionId == export.SourceRevisionId)
                    .Select(row => row.Id)
                    .ToListAsync();
                var activities = await ingested.SourceActivities
                    .Where(row => childIds.Contains(row.SourceRevisionId))
                    .ToListAsync();
                Assert.Single(activities, row =>
                    row.State == (int)SourceActivityState.Pending &&
                    row.ExecutionClass == (int)ExecutionClass.InProcess);
                var deferred = Assert.Single(activities, row =>
                    row.State == (int)SourceActivityState.DeferredUnsupported &&
                    row.ExecutionClass == (int)ExecutionClass.DeferredCapability);
                Assert.Equal(requiredCapability, deferred.RequiredCapability);
                var evidence = await ingested.DeferredCapabilities.SingleAsync(row => row.SourceRevisionId == deferred.SourceRevisionId);
                Assert.Equal(deferred.InputFingerprint, evidence.ArtifactFingerprint);
                Assert.Equal(requiredCapability, evidence.RequiredCapability);
            }

            var capability = new RegisteredSourceCapability(
                Guid.NewGuid(),
                requiredCapability,
                "phase-3a-v1",
                ExecutionClass.InProcess,
                "phase-4-outlook-deferred-text-v1",
                true);
            var factory = new ContextFactory(_fixture.ConnectionString);
            await new SqlSourceActivityStore(factory, new ManualTimeProvider(Now))
                .RegisterAsync(capability, CancellationToken.None);

            Assert.Equal(1, await new SqlRetainedTextRegistrationStore(
                    factory,
                    new ManualTimeProvider(Now),
                    outlookSpoolPolicy: PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spoolRoot))
                .ReplayAsync(capability, seed.SourceRootId, CancellationToken.None));
            await using var replayed = CreateContext();
            var replayedActivity = await replayed.SourceActivities
                .SingleAsync(row => row.RequiredCapability == requiredCapability);
            Assert.NotNull(replayedActivity.ResultingPipelineRecordId);
            var claimed = await replayed.DeferredCapabilities
                .SingleAsync(row => row.SourceRevisionId == replayedActivity.SourceRevisionId);
            Assert.Equal(capability.ProcessorVersion, claimed.ClaimedProcessorVersion);
            Assert.NotNull(claimed.ClaimedAtUtc);
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Binary_pdf_attachment_is_deferred_for_explicit_document_processor_not_utf8_replay()
    {
        const string requiredCapability = "outlook-content:application/pdf";
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var exportId = Guid.NewGuid();
            var recovery = Recovery(seed, "entry-binary-pdf", Now.AddMinutes(3));
            var layout = new OutlookSpoolLayout(spoolRoot);
            var draft = layout.CreateInflightExportDirectory(exportId);
            await File.WriteAllTextAsync(Path.Combine(draft, "body.txt"), "supported body", new UTF8Encoding(false));
            var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2d, 0x31, 0x2e, 0x37, 0x0a, 0x80, 0x00, 0xff };
            await File.WriteAllBytesAsync(Path.Combine(draft, "report.pdf"), pdfBytes);
            await layout.WriteManifestAsync(
                draft,
                OutlookExportManifest.Create(
                    exportId,
                    "body.txt",
                    [OutlookExportSidecar.Create("report.pdf", "application/pdf")],
                    recovery),
                CancellationToken.None);
            _ = await layout.PromoteAsync(exportId, CancellationToken.None);

            Assert.True((await CreateService(spoolRoot).IngestReadyAsync(spoolRoot, exportId, CancellationToken.None)).Accepted);

            Guid activityId;
            Guid revisionId;
            await using (var ingested = CreateContext())
            {
                var revision = await ingested.SourceRevisions.SingleAsync(row =>
                    row.SourceRootId == seed.SourceRootId && row.Extension == ".pdf");
                revisionId = revision.Id;
                Assert.Equal("DeferredCapability", revision.Classification);
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(pdfBytes)), revision.ContentSha256);
                var activity = await ingested.SourceActivities.SingleAsync(row => row.SourceRevisionId == revision.Id);
                activityId = activity.Id;
                Assert.Equal((int)SourceActivityKind.DocumentParsing, activity.ActivityKind);
                Assert.Equal((int)ExecutionClass.DeferredCapability, activity.ExecutionClass);
                Assert.Equal((int)SourceActivityState.DeferredUnsupported, activity.State);
                Assert.Equal("phase-4-outlook-content-v1", activity.ProcessorVersion);
                Assert.Equal(requiredCapability, activity.RequiredCapability);
            }

            var capability = new RegisteredSourceCapability(
                Guid.NewGuid(),
                requiredCapability,
                "phase-4-outlook-content-v1",
                ExecutionClass.InProcess,
                "future-pdf-processor-v1",
                true,
                SourceActivityKind.DocumentParsing,
                "DeferredCapability",
                "pipeline:extract-pdf");
            var factory = new ContextFactory(_fixture.ConnectionString);
            await new SqlSourceActivityStore(factory, new ManualTimeProvider(Now))
                .RegisterAsync(capability, CancellationToken.None);

            Assert.Equal(0, await new SqlRetainedTextRegistrationStore(
                    factory,
                    new ManualTimeProvider(Now),
                    outlookSpoolPolicy: PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spoolRoot))
                .ReplayAsync(capability, seed.SourceRootId, CancellationToken.None));
            await using var verification = CreateContext();
            Assert.Null((await verification.SourceActivities.SingleAsync(row => row.Id == activityId)).ResultingPipelineRecordId);
            var evidence = await verification.DeferredCapabilities.SingleAsync(row => row.SourceRevisionId == revisionId);
            Assert.Null(evidence.ClaimedAtUtc);
            Assert.Null(evidence.ClaimedProcessorVersion);
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Deferred_replay_blocks_before_claim_when_retained_artifact_checksum_is_invalid()
    {
        var contentType = $"text/x-flux-deferred-{Guid.NewGuid():N}";
        var requiredCapability = $"outlook-content:{contentType}";
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot,
                seed,
                "entry-deferred-corrupt",
                Now.AddMinutes(3),
                "supported body",
                [("future-format.txt", "future text", contentType)]);
            Assert.True((await CreateService(spoolRoot).IngestReadyAsync(prepared.Request, CancellationToken.None)).Accepted);

            Guid deferredActivityId;
            string retainedPath;
            await using (var ingested = CreateContext())
            {
                var deferred = await ingested.SourceActivities
                    .SingleAsync(row => row.RequiredCapability == requiredCapability);
                deferredActivityId = deferred.Id;
                retainedPath = await ingested.SourceArtifacts
                    .Where(row => row.SourceRevisionId == deferred.SourceRevisionId)
                    .Select(row => row.StoreRelativePath)
                    .SingleAsync();
            }
            await File.WriteAllTextAsync(Path.Combine(spoolRoot, retainedPath), "tamper data", new UTF8Encoding(false));

            var capability = new RegisteredSourceCapability(
                Guid.NewGuid(),
                requiredCapability,
                "phase-3a-v1",
                ExecutionClass.InProcess,
                "phase-4-outlook-deferred-text-v1",
                true);
            var factory = new ContextFactory(_fixture.ConnectionString);
            await new SqlSourceActivityStore(factory, new ManualTimeProvider(Now))
                .RegisterAsync(capability, CancellationToken.None);

            Assert.Equal(0, await new SqlRetainedTextRegistrationStore(
                    factory,
                    new ManualTimeProvider(Now),
                    outlookSpoolPolicy: PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spoolRoot))
                .ReplayAsync(capability, seed.SourceRootId, CancellationToken.None));
            await using var verification = CreateContext();
            var activity = await verification.SourceActivities.SingleAsync(row => row.Id == deferredActivityId);
            Assert.Equal((int)SourceActivityState.DeferredPolicy, activity.State);
            Assert.Equal("retained-artifact-checksum-invalid", activity.Reason);
            Assert.Null(activity.ResultingPipelineRecordId);
            var evidence = await verification.DeferredCapabilities.SingleAsync(row => row.SourceRevisionId == activity.SourceRevisionId);
            Assert.Null(evidence.ClaimedAtUtc);
            Assert.Null(evidence.ClaimedProcessorVersion);
            var audit = await verification.AuditEvents.SingleAsync(row =>
                row.EventType == "activity.retained_artifact_blocked" && row.SourceActivityId == deferredActivityId);
            Assert.Equal("{\"reasonCode\":\"retained-artifact-checksum-invalid\"}", audit.DetailsJson);
            Assert.DoesNotContain("tamper", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Deferred_replay_records_missing_retained_artifact_before_claim()
    {
        var contentType = $"text/x-flux-deferred-missing-{Guid.NewGuid():N}";
        var requiredCapability = $"outlook-content:{contentType}";
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot,
                seed,
                "entry-deferred-missing",
                Now.AddMinutes(3),
                "supported body",
                [("future-format.txt", "future text", contentType)]);
            Assert.True((await CreateService(spoolRoot).IngestReadyAsync(prepared.Request, CancellationToken.None)).Accepted);

            Guid deferredActivityId;
            string retainedPath;
            await using (var ingested = CreateContext())
            {
                var deferred = await ingested.SourceActivities
                    .SingleAsync(row => row.RequiredCapability == requiredCapability);
                deferredActivityId = deferred.Id;
                retainedPath = await ingested.SourceArtifacts
                    .Where(row => row.SourceRevisionId == deferred.SourceRevisionId)
                    .Select(row => row.StoreRelativePath)
                    .SingleAsync();
            }
            File.Delete(Path.Combine(spoolRoot, retainedPath));

            var capability = new RegisteredSourceCapability(
                Guid.NewGuid(),
                requiredCapability,
                "phase-3a-v1",
                ExecutionClass.InProcess,
                "phase-4-outlook-deferred-missing-v1",
                true);
            var factory = new ContextFactory(_fixture.ConnectionString);
            await new SqlSourceActivityStore(factory, new ManualTimeProvider(Now))
                .RegisterAsync(capability, CancellationToken.None);

            Assert.Equal(0, await new SqlRetainedTextRegistrationStore(
                    factory,
                    new ManualTimeProvider(Now),
                    outlookSpoolPolicy: PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spoolRoot))
                .ReplayAsync(capability, seed.SourceRootId, CancellationToken.None));
            await using var verification = CreateContext();
            var activity = await verification.SourceActivities.SingleAsync(row => row.Id == deferredActivityId);
            Assert.Equal((int)SourceActivityState.DeferredPolicy, activity.State);
            Assert.Equal("retained-artifact-missing", activity.Reason);
            Assert.Null(activity.ResultingPipelineRecordId);
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Deferred_replay_classifies_a_retained_link_as_path_invalid_without_refollowing_it()
    {
        var contentType = $"text/x-flux-deferred-link-{Guid.NewGuid():N}";
        var requiredCapability = $"outlook-content:{contentType}";
        var spoolRoot = CreateTemporaryDirectory();
        var outside = CreateTemporaryDirectory();
        string? retainedFullPath = null;
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot,
                seed,
                "entry-deferred-link",
                Now.AddMinutes(3),
                "supported body",
                [("future-format.txt", "future text", contentType)]);
            Assert.True((await CreateService(spoolRoot).IngestReadyAsync(prepared.Request, CancellationToken.None)).Accepted);

            Guid deferredActivityId;
            string retainedPath;
            await using (var ingested = CreateContext())
            {
                var deferred = await ingested.SourceActivities
                    .SingleAsync(row => row.RequiredCapability == requiredCapability);
                deferredActivityId = deferred.Id;
                retainedPath = await ingested.SourceArtifacts
                    .Where(row => row.SourceRevisionId == deferred.SourceRevisionId)
                    .Select(row => row.StoreRelativePath)
                    .SingleAsync();
            }
            retainedFullPath = Path.Combine(spoolRoot, retainedPath);
            File.Delete(retainedFullPath);
            var outsidePath = Path.Combine(outside, "outside.txt");
            await File.WriteAllTextAsync(outsidePath, "future text", new UTF8Encoding(false));
            File.CreateSymbolicLink(retainedFullPath, outsidePath);

            var capability = new RegisteredSourceCapability(
                Guid.NewGuid(),
                requiredCapability,
                "phase-3a-v1",
                ExecutionClass.InProcess,
                $"phase-4-outlook-deferred-link-{Guid.NewGuid():N}",
                true);
            var factory = new ContextFactory(_fixture.ConnectionString);
            await new SqlSourceActivityStore(factory, new ManualTimeProvider(Now))
                .RegisterAsync(capability, CancellationToken.None);

            Assert.Equal(0, await new SqlRetainedTextRegistrationStore(
                    factory,
                    new ManualTimeProvider(Now),
                    outlookSpoolPolicy: PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spoolRoot))
                .ReplayAsync(capability, seed.SourceRootId, CancellationToken.None));
            Assert.Equal("future text", await File.ReadAllTextAsync(outsidePath));
            await using var verification = CreateContext();
            var activity = await verification.SourceActivities.SingleAsync(row => row.Id == deferredActivityId);
            Assert.Equal((int)SourceActivityState.DeferredPolicy, activity.State);
            Assert.Equal("retained-artifact-path-invalid", activity.Reason);
            Assert.Null(activity.ResultingPipelineRecordId);
            var evidence = await verification.DeferredCapabilities.SingleAsync(row => row.SourceRevisionId == activity.SourceRevisionId);
            Assert.Null(evidence.ClaimedAtUtc);
            Assert.Null(evidence.ClaimedProcessorVersion);
            var audit = await verification.AuditEvents.SingleAsync(row =>
                row.EventType == "activity.retained_artifact_blocked" && row.SourceActivityId == deferredActivityId);
            Assert.Equal("{\"reasonCode\":\"retained-artifact-path-invalid\"}", audit.DetailsJson);
            Assert.DoesNotContain("outside", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (retainedFullPath is not null && File.Exists(retainedFullPath))
            {
                File.Delete(retainedFullPath);
            }
            Directory.Delete(spoolRoot, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Same_ready_export_replays_without_duplicate_catalogue_or_cursor_mutation()
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var prepared = await PrepareExportAsync(
                spoolRoot, seed, "entry-replay", Now.AddMinutes(4), "replay body", []);
            var request = prepared.Request;
            var service = CreateService(spoolRoot);

            var first = await service.IngestReadyAsync(request, CancellationToken.None);
            var replay = await service.IngestReadyAsync(request, CancellationToken.None);

            Assert.True(first.Accepted);
            Assert.False(first.IsReplay);
            Assert.True(replay.Accepted);
            Assert.True(replay.IsReplay);
            Assert.Equal(first.ExportId, replay.ExportId);
            await using var context = CreateContext();
            Assert.Equal(1, await context.OutlookCaptureExports.CountAsync(row => row.Id == prepared.ExportId));
            Assert.Equal(1, await context.OutlookCaptureOperations.CountAsync(row => row.OperationId == request.OperationId));
            var revisionIds = await context.SourceRevisions
                .Where(row => row.SourceRootId == seed.SourceRootId)
                .Select(row => row.Id)
                .ToListAsync();
            Assert.Equal(2, revisionIds.Count);
            Assert.Equal(1, await context.SourceArtifacts.CountAsync(row => revisionIds.Contains(row.SourceRevisionId)));
            Assert.Equal(1, await context.SourceActivities.CountAsync(row => revisionIds.Contains(row.SourceRevisionId)));
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Conflicting_entry_records_blocked_evidence_without_mutating_accepted_export_or_cursor()
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var seed = await SeedCaptureAsync(spoolRoot);
            var firstReady = await PrepareExportAsync(
                spoolRoot, seed, "entry-conflict", Now.AddMinutes(5), "accepted body", []);
            var firstObservation = firstReady.Request.Observation!;
            var service = CreateService(spoolRoot);
            var accepted = await service.IngestReadyAsync(
                firstReady.Request,
                CancellationToken.None);

            var conflictingReady = await PrepareExportAsync(
                spoolRoot,
                seed,
                "entry-conflict",
                Now.AddMinutes(6),
                "different body",
                [],
                Sha256("different source"));
            var conflictObservation = conflictingReady.Request.Observation!;
            var conflict = await service.IngestReadyAsync(
                conflictingReady.Request,
                CancellationToken.None);

            Assert.True(accepted.Accepted);
            Assert.False(conflict.Accepted);
            await using var context = CreateContext();
            var acceptedRow = await context.OutlookCaptureExports.SingleAsync(row => row.Id == accepted.ExportId.Value);
            var blockedRow = await context.OutlookCaptureExports.SingleAsync(row => row.Id == conflict.ExportId.Value);
            Assert.Equal((int)OutlookExportState.Ingested, acceptedRow.State);
            Assert.Equal(SourceFingerprint, acceptedRow.SourceFingerprint);
            Assert.Equal((int)OutlookExportState.Blocked, blockedRow.State);
            Assert.Null(blockedRow.SourceRevisionId);
            Assert.Equal(firstObservation.CursorUtc, await context.OutlookCaptureFolders
                .Where(row => row.Id == seed.FolderId)
                .Select(row => row.CursorUtc)
                .SingleAsync());
            Assert.Equal(2, await context.SourceRevisions.CountAsync(row => row.SourceRootId == seed.SourceRootId));
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task New_profile_gets_one_immutable_synthetic_private_source_root_binding()
    {
        var spoolRoot = CreateTemporaryDirectory();
        try
        {
            var store = new SqlOutlookCaptureStore(
                new ContextFactory(_fixture.ConnectionString),
                new ManualTimeProvider(Now));
            var createOperationId = Guid.NewGuid();
            var created = await store.SaveProfileAsync(
                SaveProfile(createOperationId, null, "Private mailbox name", spoolRoot),
                CancellationToken.None);
            Assert.True(created.Accepted);
            Guid profileId;
            Guid sourceRootId;
            await using (var context = CreateContext())
            {
                profileId = (await context.OutlookCaptureOperations
                    .Where(row => row.OperationId == createOperationId)
                    .Select(row => row.ResourceId)
                    .SingleAsync())!.Value;
                var profile = await context.OutlookCaptureProfiles.SingleAsync(row => row.Id == profileId);
                sourceRootId = profile.SourceRootId;
                var root = await context.SourceRootConfigurations.SingleAsync(row => row.Id == sourceRootId);
                Assert.Equal((int)SourceRootState.Paused, root.State);
                Assert.Equal("Private Outlook capture", root.DisplayName);
                Assert.DoesNotContain("Private mailbox name", root.CanonicalPath, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(spoolRoot, root.CanonicalPath, StringComparison.OrdinalIgnoreCase);
            }

            var updateOperationId = Guid.NewGuid();
            var updated = await store.SaveProfileAsync(
                SaveProfile(updateOperationId, new OutlookCaptureProfileId(profileId), "Renamed mailbox", spoolRoot),
                CancellationToken.None);

            Assert.True(updated.Accepted);
            await using var verification = CreateContext();
            Assert.Equal(sourceRootId, await verification.OutlookCaptureProfiles
                .Where(row => row.Id == profileId)
                .Select(row => row.SourceRootId)
                .SingleAsync());
            Assert.Equal(1, await verification.SourceRootConfigurations.CountAsync(row => row.Id == sourceRootId));
        }
        finally
        {
            Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [Fact]
    public void Capture_store_exposes_no_independent_export_commit_member()
    {
        Assert.DoesNotContain(typeof(IOutlookCaptureStore).GetMethods(), method => method.Name == "CommitExportAsync");
        Assert.Null(typeof(SqlOutlookCaptureStore).GetMethod("CommitExportAsync"));
    }

    [Fact]
    public async Task Missing_manifest_file_or_hash_mismatch_blocks_without_promoting_ready_export()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var layout = new OutlookSpoolLayout(root);
            var exportId = Guid.NewGuid();
            var draft = layout.CreateInflightExportDirectory(exportId);
            await File.WriteAllTextAsync(Path.Combine(draft, "body.txt"), "message body", new UTF8Encoding(false));
            var manifest = OutlookExportManifest.Create(exportId, "body.txt", [], TestRecovery());
            await layout.WriteManifestAsync(draft, manifest, CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Combine(draft, "manifest.json"),
                JsonSerializer.Serialize(manifest with { Body = manifest.Body with { ContentSha256 = new string('0', 64) } }));

            var blocked = await Assert.ThrowsAsync<OutlookReadyExportValidationException>(
                () => layout.PromoteAsync(exportId, CancellationToken.None));
            Assert.Equal("ready-sidecar-checksum-invalid", blocked.ReasonCode);
            Assert.True(Directory.Exists(draft));
            Assert.False(Directory.Exists(layout.GetReadyExportDirectory(exportId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Root_directory_lease_rejects_a_junction_instead_of_following_it()
    {
        var container = CreateTemporaryDirectory();
        var outside = CreateTemporaryDirectory();
        var configuredRoot = Path.Combine(container, "configured-root");
        try
        {
            Directory.CreateSymbolicLink(configuredRoot, outside);

            Assert.Throws<UnauthorizedAccessException>(() =>
                PhysicalFileIdentity.OpenDirectoryLease(configuredRoot));
        }
        finally
        {
            if (Directory.Exists(configuredRoot))
            {
                Directory.Delete(configuredRoot);
            }
            Directory.Delete(container, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Root_directory_lease_rejects_an_ancestor_junction_that_resolves_outside_the_configured_path()
    {
        var container = CreateTemporaryDirectory();
        var outside = CreateTemporaryDirectory();
        var ancestorLink = Path.Combine(container, "ancestor-link");
        var configuredRoot = Path.Combine(ancestorLink, "configured-root");
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "configured-root"));
            Directory.CreateSymbolicLink(ancestorLink, outside);

            Assert.Throws<UnauthorizedAccessException>(() =>
                PhysicalFileIdentity.OpenDirectoryLease(configuredRoot));
        }
        finally
        {
            if (Directory.Exists(ancestorLink))
            {
                Directory.Delete(ancestorLink);
            }
            Directory.Delete(container, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Nested_sidecar_reparse_is_rejected_before_outside_bytes_are_hashed()
    {
        var root = CreateTemporaryDirectory();
        var outside = CreateTemporaryDirectory();
        try
        {
            var layout = new OutlookSpoolLayout(root);
            var exportId = Guid.NewGuid();
            var draft = layout.CreateInflightExportDirectory(exportId);
            await File.WriteAllTextAsync(Path.Combine(draft, "body.txt"), "message body", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(outside, "outside.txt"), "outside private bytes", new UTF8Encoding(false));
            Directory.CreateSymbolicLink(Path.Combine(draft, "nested"), outside);
            var manifest = OutlookExportManifest.Create(
                exportId,
                "body.txt",
                [OutlookExportSidecar.Create(Path.Combine("nested", "outside.txt"), "text/plain")],
                TestRecovery());

            var exception = await Record.ExceptionAsync(() =>
                layout.WriteManifestAsync(draft, manifest, CancellationToken.None));

            var blocked = Assert.IsType<OutlookReadyExportValidationException>(exception);
            Assert.Equal("ready-sidecar-path-invalid", blocked.ReasonCode);
            Assert.False(File.Exists(Path.Combine(draft, "manifest.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private SqlOutlookExportIngestionService CreateService(
        string spoolRoot,
        IInterceptor? interceptor = null,
        bool useRetryingExecutionStrategy = false) =>
        new(
            new ContextFactory(_fixture.ConnectionString, interceptor, useRetryingExecutionStrategy),
            new ManualTimeProvider(Now),
            PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(spoolRoot));

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString)
            .Options);

    private async Task<CaptureSeed> SeedCaptureAsync(string spoolRoot)
    {
        var profileId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var catchUpId = Guid.NewGuid();
        var sourceRootId = Guid.NewGuid();
        var canonicalRootPath = $"C:\\.fluxknowledge-private\\outlook\\{sourceRootId:N}";
        const long fencingToken = 17;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = sourceRootId,
            CanonicalPath = canonicalRootPath,
            DisplayName = "Private Outlook capture",
            State = (int)SourceRootState.Paused,
            Recursive = false,
            IncludePatternsJson = "[]",
            ExcludePatternsJson = "[]",
            FollowLinks = false,
            MaximumFileBytes = 64L * 1024 * 1024,
            AllowedClassificationsJson = "[]",
            ReconciliationCadenceSeconds = 86400,
            ConfigurationRevision = 1,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now
        });
        context.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity
        {
            Id = profileId,
            SourceRootId = sourceRootId,
            DisplayName = "Do not derive identity from this name",
            SpoolRoot = spoolRoot,
            IncrementalBasis = (int)OutlookIncrementalBasis.LastModificationTime,
            State = (int)OutlookCaptureState.CatchingUp,
            IsEnabled = true,
            ConfigurationRevision = 1,
            CadenceTicks = TimeSpan.FromMinutes(15).Ticks,
            MaximumOverlapTicks = TimeSpan.FromMinutes(5).Ticks,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now
        });
        context.OutlookCaptureFolders.Add(new OutlookCaptureFolderEntity
        {
            Id = folderId,
            ProfileId = profileId,
            StoreId = $"store-{profileId:N}",
            FolderEntryId = $"folder-{folderId:N}",
            DisplayName = "Inbox",
            Basis = (int)OutlookIncrementalBasis.LastModificationTime,
            State = (int)OutlookCaptureState.CatchingUp
        });
        context.OutlookCatchUps.Add(new OutlookCatchUpEntity
        {
            Id = catchUpId,
            ProfileId = profileId,
            CoalescingKey = $"catch-up-{profileId:N}",
            Provenance = (int)OutlookCatchUpProvenance.Manual,
            State = 1,
            LeaseOwner = "S-1-5-21-test|1|host",
            LeaseExpiresAtUtc = Now.AddMinutes(10),
            LastHeartbeatAtUtc = Now,
            FencingToken = fencingToken
        });
        await context.SaveChangesAsync();
        return new CaptureSeed(profileId, folderId, catchUpId, sourceRootId, canonicalRootPath, fencingToken);
    }

    private static OutlookReadyExportRecovery Recovery(
        CaptureSeed seed,
        string entryId,
        DateTimeOffset cursorUtc,
        string sourceFingerprint = SourceFingerprint) => new(
        Guid.NewGuid(),
        RequestFingerprint,
        seed.CatchUpId,
        seed.FencingToken,
        seed.ProfileId,
        seed.FolderId,
        entryId,
        sourceFingerprint,
        cursorUtc,
        CursorFingerprint);

    private static OutlookReadyExportRecovery TestRecovery() => new(
        Guid.NewGuid(),
        RequestFingerprint,
        Guid.NewGuid(),
        1,
        Guid.NewGuid(),
        Guid.NewGuid(),
        "entry-test",
        SourceFingerprint,
        Now,
        CursorFingerprint);

    private static OutlookProfileSaveRequest SaveProfile(
        Guid operationId,
        OutlookCaptureProfileId? profileId,
        string displayName,
        string spoolRoot) => new(
        operationId,
        RequestFingerprint,
        profileId,
        displayName,
        OutlookIncrementalBasis.LastModificationTime,
        new OutlookCaptureSchedule(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5)),
        new OutlookSpoolValidation(RequestFingerprint, true, true, true, true, spoolRoot),
        ExpectedConfigurationRevision: profileId is null ? null : 1);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flux-outlook-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<string> Sha256FileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static async Task<PreparedExport> PrepareExportAsync(
        string spoolRoot,
        CaptureSeed seed,
        string entryId,
        DateTimeOffset cursorUtc,
        string body,
        IReadOnlyList<(string FileName, string Contents, string ContentType)> attachments,
        string sourceFingerprint = SourceFingerprint)
    {
        var exportId = Guid.NewGuid();
        var recovery = Recovery(seed, entryId, cursorUtc, sourceFingerprint);
        var layout = new OutlookSpoolLayout(spoolRoot);
        var draft = layout.CreateInflightExportDirectory(exportId);
        await File.WriteAllTextAsync(Path.Combine(draft, "body.txt"), body, new UTF8Encoding(false));
        var sidecars = new List<OutlookExportSidecar>();
        foreach (var attachment in attachments)
        {
            await File.WriteAllTextAsync(Path.Combine(draft, attachment.FileName), attachment.Contents, new UTF8Encoding(false));
            sidecars.Add(OutlookExportSidecar.Create(attachment.FileName, attachment.ContentType));
        }
        await layout.WriteManifestAsync(
            draft,
            OutlookExportManifest.Create(exportId, "body.txt", sidecars, recovery),
            CancellationToken.None);
        var readyDirectory = await layout.PromoteAsync(exportId, CancellationToken.None);
        var manifestHash = await Sha256FileAsync(Path.Combine(readyDirectory, "manifest.json"));
        return new PreparedExport(
            exportId,
            readyDirectory,
            manifestHash,
            recovery.ToCommitRequest(exportId, manifestHash));
    }

    private sealed record CaptureSeed(
        Guid ProfileId,
        Guid FolderId,
        Guid CatchUpId,
        Guid SourceRootId,
        string CanonicalRootPath,
        long FencingToken);

    private sealed record PreparedExport(
        Guid ExportId,
        string ReadyDirectory,
        string ManifestHash,
        OutlookExportCommitRequest Request);

    private sealed class ContextFactory(
        string connectionString,
        IInterceptor? interceptor = null,
        bool useRetryingExecutionStrategy = false) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<FluxKnowledgeDbContext>();
            builder.UseSqlServer(
                connectionString,
                sqlServer =>
                {
                    if (useRetryingExecutionStrategy)
                    {
                        sqlServer.EnableRetryOnFailure();
                    }
                });
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }
            return new FluxKnowledgeDbContext(builder.Options);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ThrowOnSecondSaveChangesInterceptor : SaveChangesInterceptor
    {
        private int _attempts;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempts) == 2)
            {
                throw new InvalidOperationException("Injected SQL persistence failure before cursor commit.");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
