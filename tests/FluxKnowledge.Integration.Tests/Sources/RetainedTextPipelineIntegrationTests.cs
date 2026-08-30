using System.Security.Cryptography;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class RetainedTextPipelineIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Retained_reader_extracts_after_the_original_path_is_renamed_without_rereading_it()
    {
        var bytes = "retained text"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var root = Path.Combine(Path.GetTempPath(), $"flux-retained-{Guid.NewGuid():N}");
        var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
        await File.WriteAllBytesAsync(Path.Combine(root, relative), bytes);
        var revisionId = await SeedRevisionAsync(hash, bytes.Length, relative);
        var originalPath = Path.Combine(root, "original.txt");
        await File.WriteAllTextAsync(originalPath, "different bytes");
        File.Move(originalPath, originalPath + ".renamed");

        var source = await new SqlRetainedSourceReader(new ContextFactory(_fixture.ConnectionString), root)
            .ReadUtf8Async(new SourceRevisionId(revisionId), CancellationToken.None);

        Assert.Equal("retained text", source.Text);
        Assert.Equal(hash, source.ContentHash);
        Directory.Delete(root, recursive: true);
    }

    [NativeSqlServerFact]
    public async Task Retained_reader_rejects_checksum_mismatch()
    {
        var bytes = "retained text"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var root = Path.Combine(Path.GetTempPath(), $"flux-retained-{Guid.NewGuid():N}");
        var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
        await File.WriteAllTextAsync(Path.Combine(root, relative), "tampered");
        var revisionId = await SeedRevisionAsync(hash, bytes.Length, relative);

        await Assert.ThrowsAsync<InvalidDataException>(() => new SqlRetainedSourceReader(new ContextFactory(_fixture.ConnectionString), root)
            .ReadUtf8Async(new SourceRevisionId(revisionId), CancellationToken.None).AsTask());

        Directory.Delete(root, recursive: true);
    }

    [NativeSqlServerFact]
    public async Task Retained_reader_rejects_a_final_artifact_reparse_point_even_when_its_target_has_the_expected_bytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bytes = "retained text"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var root = Path.Combine(Path.GetTempPath(), $"flux-retained-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"flux-retained-outside-{Guid.NewGuid():N}.bin");
        var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
        await File.WriteAllBytesAsync(outside, bytes);
        try
        {
            try
            {
                File.CreateSymbolicLink(Path.Combine(root, relative), outside);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            var revisionId = await SeedRevisionAsync(hash, bytes.Length, relative);
            await Assert.ThrowsAsync<IOException>(() => new SqlRetainedSourceReader(
                    new ContextFactory(_fixture.ConnectionString), root)
                .ReadUtf8Async(new SourceRevisionId(revisionId), CancellationToken.None).AsTask());
        }
        finally
        {
            File.Delete(outside);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [NativeSqlServerFact]
    public async Task Disabled_persisted_Outlook_profile_cannot_redirect_a_retained_read_to_an_external_root()
    {
        var bytes = "external retained text"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var fallbackRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-canonical-{Guid.NewGuid():N}");
        var externalRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-external-{Guid.NewGuid():N}");
        var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(fallbackRoot);
        Directory.CreateDirectory(Path.Combine(externalRoot, "sha256", hash[..2]));
        await File.WriteAllBytesAsync(Path.Combine(externalRoot, relative), bytes);
        try
        {
            var revisionId = await SeedRevisionAsync(hash, bytes.Length, relative);
            await using (var context = CreateContext())
            {
                var sourceRootId = await context.SourceRevisions
                    .Where(value => value.Id == revisionId)
                    .Select(value => value.SourceRootId)
                    .SingleAsync();
                var now = DateTimeOffset.UtcNow;
                context.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity
                {
                    Id = Guid.NewGuid(),
                    SourceRootId = sourceRootId,
                    DisplayName = "Disabled stale Outlook profile",
                    SpoolRoot = externalRoot,
                    IncrementalBasis = (int)OutlookIncrementalBasis.LastModificationTime,
                    State = (int)OutlookCaptureState.Disabled,
                    IsEnabled = false,
                    ConfigurationRevision = 1,
                    CadenceTicks = TimeSpan.FromMinutes(15).Ticks,
                    MaximumOverlapTicks = TimeSpan.FromMinutes(5).Ticks,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                await context.SaveChangesAsync();
            }

            using var reader = new SqlRetainedSourceReader(
                new ContextFactory(_fixture.ConnectionString),
                fallbackRoot);
            await Assert.ThrowsAsync<InvalidDataException>(() => reader
                .ReadUtf8Async(new SourceRevisionId(revisionId), CancellationToken.None)
                .AsTask());
        }
        finally
        {
            if (Directory.Exists(fallbackRoot)) Directory.Delete(fallbackRoot, recursive: true);
            if (Directory.Exists(externalRoot)) Directory.Delete(externalRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Retained_reader_rejects_a_replaced_shard_before_opening_the_final_artifact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bytes = "retained text"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var root = Path.Combine(Path.GetTempPath(), $"flux-retained-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"flux-retained-outside-{Guid.NewGuid():N}");
        var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        var shard = Path.Combine(root, "sha256", hash[..2]);
        SqlRetainedSourceReader? reader = null;
        Directory.CreateDirectory(shard);
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(shard, $"{hash}.bin"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(outside, $"{hash}.bin"), bytes);
        try
        {
            var revisionId = await SeedRevisionAsync(hash, bytes.Length, relative);
            reader = new SqlRetainedSourceReader(new ContextFactory(_fixture.ConnectionString), root);
            var movedShard = shard + ".original";
            Directory.Move(shard, movedShard);
            try
            {
                Directory.CreateSymbolicLink(shard, outside);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader
                .ReadUtf8Async(new SourceRevisionId(revisionId), CancellationToken.None).AsTask());
        }
        finally
        {
            reader?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    [NativeSqlServerFact]
    public async Task Retained_reader_rejects_artifacts_larger_than_the_16_mib_text_limit_before_reading_them()
    {
        const int maximumTextBytes = 16 * 1024 * 1024;
        var bytes = new byte[maximumTextBytes + 1];
        Array.Fill(bytes, (byte)'x');
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var root = Path.Combine(Path.GetTempPath(), $"flux-retained-{Guid.NewGuid():N}");
        var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
        await File.WriteAllBytesAsync(Path.Combine(root, relative), bytes);
        try
        {
            var revisionId = await SeedRevisionAsync(hash, bytes.Length, relative);
            await Assert.ThrowsAsync<InvalidDataException>(() => new SqlRetainedSourceReader(
                    new ContextFactory(_fixture.ConnectionString), root)
                .ReadUtf8Async(new SourceRevisionId(revisionId), CancellationToken.None).AsTask());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [NativeSqlServerFact]
    public async Task Planning_the_same_retained_activity_twice_creates_one_pipeline_job_and_outbox_link()
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var revisionId = await SeedRevisionAsync(hash, 4, Path.Combine("sha256", hash[..2], $"{hash}.bin"));
        var activity = SourceActivity.Create(new SourceRevisionId(revisionId), SourceActivityKind.TextExtraction,
            ExecutionClass.InProcess, "phase-3a-v1", hash, null, null);
        await using (var setup = CreateContext())
        {
            setup.SourceActivities.Add(new SourceActivityEntity
            {
                Id = activity.Id.Value, SourceRevisionId = revisionId, ActivityKind = (int)activity.Kind,
                ExecutionClass = (int)activity.ExecutionClass, ProcessorVersion = activity.ProcessorVersion,
                InputFingerprint = activity.InputFingerprint, State = (int)activity.State,
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        var planner = new RetainedTextActivityPlanner(new SqlRetainedTextRegistrationStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System));
        Assert.True(await planner.PlanAsync(activity, CancellationToken.None));
        Assert.False(await planner.PlanAsync(activity, CancellationToken.None));

        await using var verification = CreateContext();
        var record = await verification.PipelineRecords.SingleAsync(item => item.SourceRevisionId == revisionId);
        Assert.Equal(1, await verification.Jobs.CountAsync(item => item.PipelineRecordId == record.Id));
        Assert.Equal(1, await verification.OutboxMessages.CountAsync(item => item.PipelineRecordId == record.Id));
        var persisted = await verification.SourceActivities.SingleAsync(item => item.Id == activity.Id.Value);
        Assert.Equal(record.Id, persisted.ResultingPipelineRecordId);
    }

    [NativeSqlServerFact]
    public async Task Parallel_text_and_metadata_activities_for_one_retained_revision_create_exactly_one_record_job_and_outbox()
    {
        const string hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var revisionId = await SeedRevisionAsync(hash, 4, Path.Combine("sha256", hash[..2], $"{hash}.bin"));
        var text = SourceActivity.Create(new SourceRevisionId(revisionId), SourceActivityKind.TextExtraction,
            ExecutionClass.InProcess, "phase-3a-v1", hash, null, null);
        var metadata = SourceActivity.Create(new SourceRevisionId(revisionId), SourceActivityKind.MetadataExtraction,
            ExecutionClass.InProcess, "phase-3a-v1", hash, null, null);
        await using (var setup = CreateContext())
        {
            foreach (var activity in new[] { text, metadata })
            {
                setup.SourceActivities.Add(new SourceActivityEntity
                {
                    Id = activity.Id.Value, SourceRevisionId = revisionId, ActivityKind = (int)activity.Kind,
                    ExecutionClass = (int)activity.ExecutionClass, ProcessorVersion = activity.ProcessorVersion,
                    InputFingerprint = activity.InputFingerprint, State = (int)activity.State,
                    CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
            await setup.SaveChangesAsync();
        }

        var planner = new RetainedTextActivityPlanner(new SqlRetainedTextRegistrationStore(
            new ContextFactory(_fixture.ConnectionString), TimeProvider.System));
        var outcomes = await Task.WhenAll(
            planner.PlanAsync(text, CancellationToken.None).AsTask(),
            planner.PlanAsync(metadata, CancellationToken.None).AsTask());

        Assert.Single(outcomes, outcome => outcome);
        await using var verification = CreateContext();
        var record = Assert.Single(await verification.PipelineRecords
            .Where(item => item.SourceRevisionId == revisionId)
            .ToListAsync());
        Assert.Equal(1, await verification.Jobs.CountAsync(item => item.PipelineRecordId == record.Id));
        Assert.Equal(1, await verification.OutboxMessages.CountAsync(item => item.PipelineRecordId == record.Id));
        var linkedActivityIds = await verification.SourceActivities
            .Where(item => item.SourceRevisionId == revisionId && item.ResultingPipelineRecordId == record.Id)
            .Select(item => item.Id)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(
            new[] { text.Id.Value, metadata.Id.Value }.OrderBy(id => id.ToString("N"), StringComparer.Ordinal),
            linkedActivityIds.OrderBy(id => id.ToString("N"), StringComparer.Ordinal));
    }

    [NativeSqlServerFact]
    public async Task Pending_in_process_metadata_activity_uses_the_existing_retained_text_registration_path()
    {
        const string hash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        var revisionId = await SeedRevisionAsync(hash, 4, Path.Combine("sha256", "cc", $"{hash}.bin"));
        var activity = SourceActivity.Create(new SourceRevisionId(revisionId), SourceActivityKind.MetadataExtraction,
            ExecutionClass.InProcess, "phase-3a-v1", hash, null, null);
        await using (var setup = CreateContext())
        {
            setup.SourceActivities.Add(new SourceActivityEntity
            {
                Id = activity.Id.Value, SourceRevisionId = revisionId, ActivityKind = (int)activity.Kind,
                ExecutionClass = (int)activity.ExecutionClass, ProcessorVersion = activity.ProcessorVersion,
                InputFingerprint = activity.InputFingerprint, State = (int)activity.State,
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        Assert.True(await new RetainedTextActivityPlanner(new SqlRetainedTextRegistrationStore(
            new ContextFactory(_fixture.ConnectionString), TimeProvider.System)).PlanAsync(activity, CancellationToken.None));
        await using var verification = CreateContext();
        Assert.Single(await verification.PipelineRecords.Where(value => value.SourceRevisionId == revisionId).ToListAsync());
    }

    private async Task<Guid> SeedRevisionAsync(string hash, int byteLength, string relativePath)
    {
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId, CanonicalPath = $"C:\\retained-tests\\{rootId:N}", DisplayName = "Test", State = 0,
            Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false,
            MaximumFileBytes = 16 * 1024 * 1024, AllowedClassificationsJson = "[]", CrawlMode = 0,
            ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"test:{revisionId:N}", Revision = 1,
            ContentSha256 = hash, CanonicalPath = $"C:\\retained-tests\\{revisionId:N}.txt", Classification = "AcceptedUtf8Text",
            Extension = ".txt", ByteLength = byteLength, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}"
        });
        context.SourceArtifacts.Add(new SourceArtifactEntity
        {
            Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relativePath,
            ByteLength = byteLength, ChecksumVerifiedAtUtc = now, ReferenceCount = 1
        });
        await context.SaveChangesAsync();
        return revisionId;
    }

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString, options => options.EnableRetryOnFailure()).Options);

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
