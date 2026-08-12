using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class TarArchiveReplayIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Activation_replays_a_retained_tar_without_reading_the_missing_source_original_and_is_idempotent()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-tar-retained-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var tar = CreateTar("confidential-member-sentinel.txt", "private retained member");
            var hash = Convert.ToHexStringLower(SHA256.HashData(tar));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), tar);
            var seeded = await SeedDeferredTarAsync(hash, tar.Length, relativePath);

            var first = await CreateActivation(privateRoot).RunOnceAsync(CancellationToken.None);
            var second = await CreateActivation(privateRoot).RunOnceAsync(CancellationToken.None);

            Assert.Equal("archive-tar-expand", first.Capability);
            Assert.Equal(1, first.CompletedBranches);
            Assert.Equal(0, second.CompletedBranches);
            await using var verification = CreateContext();
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId);
            var member = await verification.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == branch.Id);
            var child = await verification.SourceRevisions.SingleAsync(value => value.Id == member.ChildSourceRevisionId);
            var legacy = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.LegacyActivityId);
            var relation = await verification.SourceActivityRelations.SingleAsync(value => value.PredecessorActivityId == seeded.LegacyActivityId);
            Assert.Equal(seeded.SourceRevisionId, child.ParentSourceRevisionId);
            Assert.Equal("superseded-by-archive-tar-expand", legacy.Reason);
            Assert.Equal("superseded-by-archive-tar-expand", relation.ReasonCode);
            Assert.Equal("C:\\retained-archive-members\\" + member.MemberFingerprint, child.CanonicalPath);
            var audit = await verification.AuditEvents.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId && value.EventType == "retained_processor.completed");
            var publicJson = JsonSerializer.Serialize(new { child, member, audit });
            Assert.DoesNotContain("confidential-member-sentinel", publicJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private retained member", publicJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(privateRoot, publicJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("archive_tar", audit.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Missing_or_corrupt_retained_tar_blocks_the_generic_deferred_activity_without_creating_a_branch()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-tar-blocked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var missingHash = new string('a', 64);
            var missing = await SeedDeferredTarAsync(missingHash, 512, Path.Combine("sha256", missingHash[..2], $"{missingHash}.bin"));
            var tar = CreateTar("valid.txt", "expected");
            var hash = Convert.ToHexStringLower(SHA256.HashData(tar));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), [1, 2, 3]);
            var corrupt = await SeedDeferredTarAsync(hash, tar.Length, relativePath);

            var result = await CreateActivation(privateRoot).RunOnceAsync(CancellationToken.None);

            Assert.Equal(0, result.PromotedBranches);
            await using var verification = CreateContext();
            var activities = await verification.SourceActivities.Where(value => value.Id == missing.LegacyActivityId || value.Id == corrupt.LegacyActivityId).ToArrayAsync();
            Assert.Contains(activities, value => value.Id == missing.LegacyActivityId && value.Reason == "retained-artifact-missing");
            Assert.Contains(activities, value => value.Id == corrupt.LegacyActivityId && value.Reason == "retained-artifact-checksum-invalid");
            Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == missing.SourceRevisionId || value.SourceRevisionId == corrupt.SourceRevisionId).ToListAsync());
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Processor_specific_claims_cannot_cross_claim_zip_and_tar_branches()
    {
        var zip = await SeedDeferredTarAsync(new string('b', 64), 512, Path.Combine("sha256", "bb", "zip.bin"));
        var tar = await SeedDeferredTarAsync(new string('c', 64), 512, Path.Combine("sha256", "cc", "tar.bin"));
        var store = new SqlRetainedProcessorBranchStore(new ContextFactory(_fixture.ConnectionString), TimeProvider.System);
        Assert.True(await store.PromoteAsync(new RetainedProcessorPromotionCandidate(zip.LegacyActivityId, new SourceRevisionId(zip.SourceRevisionId), new string('b', 64)), ZipArchiveRetainedProcessor.Capability, CancellationToken.None));
        Assert.True(await store.PromoteAsync(new RetainedProcessorPromotionCandidate(tar.LegacyActivityId, new SourceRevisionId(tar.SourceRevisionId), new string('c', 64)), TarArchiveRetainedProcessor.Capability, CancellationToken.None));

        var tarClaims = await store.ClaimAsync("tar-owner", 16, TarArchiveRetainedProcessor.Capability.ProcessorFingerprint, CancellationToken.None);
        var zipClaims = await store.ClaimAsync("zip-owner", 16, ZipArchiveRetainedProcessor.Capability.ProcessorFingerprint, CancellationToken.None);

        Assert.Equal(tar.SourceRevisionId, Assert.Single(tarClaims).SourceRevisionId.Value);
        Assert.Equal(zip.SourceRevisionId, Assert.Single(zipClaims).SourceRevisionId.Value);
    }

    [NativeSqlServerFact]
    public async Task Disabled_tar_activation_leaves_registration_promotion_claims_and_generic_deferred_work_untouched()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-tar-disabled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var tar = CreateTar("disabled-sentinel.txt", "retained");
            var hash = Convert.ToHexStringLower(SHA256.HashData(tar));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), tar);
            var seeded = await SeedDeferredTarAsync(hash, tar.Length, relativePath);
            await using var before = CreateContext();
            var registrationsBefore = await before.SourceCapabilities.CountAsync(value => value.Id == TarArchiveRetainedProcessor.Capability.Id);

            var result = await CreateActivation(privateRoot, enabled: false).RunOnceAsync(CancellationToken.None);

            Assert.False(result.Enabled);
            await using var verification = CreateContext();
            Assert.Equal(registrationsBefore, await verification.SourceCapabilities.CountAsync(value => value.Id == TarArchiveRetainedProcessor.Capability.Id));
            Assert.Empty(await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == seeded.SourceRevisionId).ToListAsync());
            var generic = await verification.SourceActivities.SingleAsync(value => value.Id == seeded.LegacyActivityId);
            Assert.Equal((int)SourceActivityState.DeferredUnsupported, generic.State);
            Assert.Equal("deferred", generic.Reason);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Unsafe_tar_records_each_sanitised_member_outcome_and_creates_no_child()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-tar-unsafe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var tar = CreateTar([("../private-path-sentinel.txt", "x"), ("safe.txt", "y")]);
            var hash = Convert.ToHexStringLower(SHA256.HashData(tar));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), tar);
            var seeded = await SeedDeferredTarAsync(hash, tar.Length, relativePath);

            var result = await CreateActivation(privateRoot).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await using var verification = CreateContext();
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId);
            var outcome = await verification.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == branch.Id);
            Assert.Equal("blocked", outcome.Disposition);
            Assert.Equal("archive-entry-path-invalid", outcome.ReasonCode);
            Assert.Empty(await verification.SourceRevisions.Where(value => value.ParentSourceRevisionId == seeded.SourceRevisionId).ToListAsync());
            var audit = await verification.AuditEvents.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId && value.EventType == "retained_processor.blocked");
            var serialised = JsonSerializer.Serialize(new { outcome, audit });
            Assert.DoesNotContain("private-path-sentinel", serialised, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(privateRoot, serialised, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Unsupported_tar_parser_failure_commits_an_opaque_blocked_member_attempt_and_event_without_a_child()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"flux-tar-sparse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var tar = CreateUnsupportedSparseTar();
            var hash = Convert.ToHexStringLower(SHA256.HashData(tar));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), tar);
            var seeded = await SeedDeferredTarAsync(hash, tar.Length, relativePath);

            var result = await CreateActivation(privateRoot).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            await using var verification = CreateContext();
            var branch = await verification.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId);
            var member = await verification.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == branch.Id);
            var attempt = await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == branch.Id);
            var audit = await verification.AuditEvents.SingleAsync(value => value.SourceRevisionId == seeded.SourceRevisionId && value.EventType == "retained_processor.blocked");
            Assert.Equal("blocked", member.Disposition);
            Assert.Equal("archive-entry-unsupported", member.ReasonCode);
            Assert.Equal("archive-entry-unsupported", attempt.OutcomeCode);
            Assert.Contains("archive_tar", audit.DetailsJson, StringComparison.Ordinal);
            Assert.Empty(await verification.SourceRevisions.Where(value => value.ParentSourceRevisionId == seeded.SourceRevisionId).ToListAsync());
            var publicJson = JsonSerializer.Serialize(new { member, attempt, audit });
            Assert.DoesNotContain("sparse-private-sentinel", publicJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(privateRoot, publicJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    private RetainedProcessorActivationService CreateActivation(string privateRoot, bool enabled = true)
    {
        var factory = new ContextFactory(_fixture.ConnectionString);
        var writer = new SqlRetainedArtifactWriter(factory, privateRoot);
        var zip = new ZipArchiveRetainedProcessor(writer);
        var tar = new TarArchiveRetainedProcessor(writer);
        return new RetainedProcessorActivationService(
            new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), new LocalSourceCapabilityHandlerRegistry([zip, tar])),
            new SqlRetainedProcessorBranchStore(factory, TimeProvider.System), new SqlRetainedSourceReader(factory, privateRoot), zip,
            new RetainedProcessorOptions { ArchiveTarExpandEnabled = enabled }, TimeProvider.System, tar);
    }

    private async Task<(Guid LegacyActivityId, Guid SourceRevisionId)> SeedDeferredTarAsync(string hash, int byteLength, string relativePath)
    {
        var rootId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var activityId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = $"C:\\private-spool-sentinel\\{rootId:N}", DisplayName = "TAR test", State = 0,
            Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 64L * 1024 * 1024,
            AllowedClassificationsJson = "[]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceRevisions.Add(new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"tar-parent:{revisionId:N}", Revision = 1,
            ContentSha256 = hash, CanonicalPath = "C:\\missing-source-original-sentinel.tar", Classification = "DeferredCapability", Extension = ".tar", ByteLength = byteLength, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}" });
        context.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relativePath, ByteLength = byteLength, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
        context.SourceActivities.Add(new SourceActivityEntity { Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.DocumentParsing,
            ExecutionClass = (int)ExecutionClass.DeferredCapability, ProcessorVersion = "phase-3a-v1", InputFingerprint = hash, RequiredCapability = "local-source-capability",
            State = (int)SourceActivityState.DeferredUnsupported, Reason = "deferred", CreatedAtUtc = now, UpdatedAtUtc = now });
        await context.SaveChangesAsync();
        return (activityId, revisionId);
    }

    private static byte[] CreateTar(string name, string content)
    {
        using var buffer = new MemoryStream();
        using var writer = new TarWriter(buffer, TarEntryFormat.Ustar, leaveOpen: true);
        var entry = new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
        writer.WriteEntry(entry);
        return buffer.ToArray();
    }

    private static byte[] CreateTar(IEnumerable<(string Name, string Content)> entries)
    {
        using var buffer = new MemoryStream();
        using var writer = new TarWriter(buffer, TarEntryFormat.Ustar, leaveOpen: true);
        foreach (var (name, content) in entries)
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
            writer.WriteEntry(entry);
        }
        return buffer.ToArray();
    }

    private static byte[] CreateUnsupportedSparseTar()
    {
        var archive = CreateTar("sparse-private-sentinel", "x");
        archive[156] = (byte)'S';
        Array.Fill(archive, (byte)' ', 148, 8);
        var checksum = archive.AsSpan(0, 512).ToArray().Sum(value => value);
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(archive, 148);
        return archive;
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
