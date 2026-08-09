using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class SourceRootPersistenceTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Scan_control_keeps_held_work_invisible_until_its_committed_release_time()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();

        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $"C:\\source-schema-tests\\{rootId:N}",
                DisplayName = "Test root",
                State = 0,
                Recursive = true,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                FollowLinks = false,
                MaximumFileBytes = 16 * 1024 * 1024,
                AllowedClassificationsJson = "[\"text\"]",
                CrawlMode = 0,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = requestId,
                SourceRootId = rootId,
                RequestKind = 0,
                RequestedBy = "integration-test",
                RequestedAtUtc = now,
                IsReleased = false,
                State = 0
            });
            setup.SourceScanJobs.Add(new SourceScanJobEntity
            {
                Id = jobId,
                SourceScanRequestId = requestId,
                State = 0,
                DueAtUtc = DateTimeOffset.MaxValue,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceScanOutbox.Add(new SourceScanOutboxEntity
            {
                Id = outboxId,
                SourceScanRequestId = requestId,
                Operation = "source.scan",
                IdempotencyKey = $"source-scan:{requestId:N}",
                DueAtUtc = DateTimeOffset.MaxValue,
                CreatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }

        await using (var held = CreateContext())
        {
            Assert.Empty(await held.SourceScanJobs.Where(job => job.DueAtUtc <= now).ToListAsync());
            Assert.Empty(await held.SourceScanOutbox.Where(message => message.DueAtUtc <= now).ToListAsync());

            var request = await held.SourceScanRequests.SingleAsync(candidate => candidate.Id == requestId);
            request.IsReleased = true;
            request.ReleasedAtUtc = now;
            request.State = 1;
            (await held.SourceScanJobs.SingleAsync(candidate => candidate.Id == jobId)).DueAtUtc = now;
            (await held.SourceScanOutbox.SingleAsync(candidate => candidate.Id == outboxId)).DueAtUtc = now;
            await held.SaveChangesAsync();
        }

        await using var released = CreateContext();
        Assert.Equal(jobId, await released.SourceScanJobs
            .Where(job => job.DueAtUtc <= now)
            .Select(job => job.Id)
            .SingleAsync());
        Assert.Equal(outboxId, await released.SourceScanOutbox
            .Where(message => message.DueAtUtc <= now)
            .Select(message => message.Id)
            .SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Sql_constraints_reject_duplicate_canonical_roots_and_activity_idempotency_keys()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var canonicalPath = LongCanonicalPath();

        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = canonicalPath,
                DisplayName = "Original root",
                State = 0,
                Recursive = true,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                FollowLinks = false,
                MaximumFileBytes = 1,
                AllowedClassificationsJson = "[]",
                CrawlMode = 0,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = revisionId,
                SourceRootId = rootId,
                StableSourceIdentity = "volume:file:1",
                Revision = 1,
                ContentSha256 = new string('a', 64),
                CanonicalPath = "C:\\source-schema-tests\\document.txt",
                Classification = "text/plain",
                Extension = ".txt",
                ByteLength = 1,
                DiscoveredAtUtc = now
            });
            setup.SourceActivities.Add(Activity(Guid.NewGuid(), revisionId, now));
            await setup.SaveChangesAsync();
        }

        await using (var duplicateActivity = CreateContext())
        {
            duplicateActivity.SourceActivities.Add(Activity(Guid.NewGuid(), revisionId, now));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateActivity.SaveChangesAsync());
        }

        await using var duplicateRoot = CreateContext();
        duplicateRoot.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = Guid.NewGuid(),
            CanonicalPath = canonicalPath,
            DisplayName = "Duplicate root",
            State = 0,
            Recursive = false,
            IncludePatternsJson = "[]",
            ExcludePatternsJson = "[]",
            FollowLinks = false,
            MaximumFileBytes = 1,
            AllowedClassificationsJson = "[]",
            CrawlMode = 0,
            ReconciliationCadenceSeconds = 900,
            ConfigurationRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicateRoot.SaveChangesAsync());
    }

    [NativeSqlServerFact]
    public async Task Revision_identity_and_provenance_fields_cannot_be_changed_after_persistence()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var canonicalPath = $"C:\\source-schema-tests\\{rootId:N}";

        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, canonicalPath, now));
            setup.SourceRevisions.Add(Revision(revisionId, rootId, canonicalPath, now));
            await setup.SaveChangesAsync();
        }

        await using var mutation = CreateContext();
        (await mutation.SourceRevisions.SingleAsync(candidate => candidate.Id == revisionId)).ContentSha256 = new string('b', 64);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation.SaveChangesAsync());
    }

    [NativeSqlServerFact]
    public async Task Artifact_identity_and_store_metadata_cannot_be_changed_after_persistence()
    {
        var now = DateTimeOffset.Parse("2026-08-06T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var canonicalPath = $"C:\\source-schema-tests\\{rootId:N}";

        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, canonicalPath, now));
            setup.SourceRevisions.Add(Revision(revisionId, rootId, canonicalPath, now));
            setup.SourceArtifacts.Add(Artifact(artifactId, revisionId, now));
            await setup.SaveChangesAsync();
        }

        await using var mutation = CreateContext();
        (await mutation.SourceArtifacts.SingleAsync(candidate => candidate.Id == artifactId)).StoreRelativePath = "sha256\\bb\\mutated.bin";

        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation.SaveChangesAsync());
    }

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString)
            .Options);

    private static SourceActivityEntity Activity(Guid id, Guid revisionId, DateTimeOffset now) => new()
    {
        Id = id,
        SourceRevisionId = revisionId,
        ActivityKind = 0,
        ExecutionClass = 0,
        ProcessorVersion = "text-v1",
        InputFingerprint = "input-v1",
        State = 0,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private static SourceRootConfigurationEntity Root(Guid id, string canonicalPath, DateTimeOffset now) => new()
    {
        Id = id,
        CanonicalPath = canonicalPath,
        DisplayName = "Test root",
        State = 0,
        Recursive = true,
        IncludePatternsJson = "[]",
        ExcludePatternsJson = "[]",
        FollowLinks = false,
        MaximumFileBytes = 1,
        AllowedClassificationsJson = "[]",
        CrawlMode = 0,
        ReconciliationCadenceSeconds = 900,
        ConfigurationRevision = 1,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private static SourceRevisionEntity Revision(Guid id, Guid rootId, string canonicalPath, DateTimeOffset now) => new()
    {
        Id = id,
        SourceRootId = rootId,
        StableSourceIdentity = "volume:file:immutable",
        Revision = 1,
        ContentSha256 = new string('a', 64),
        CanonicalPath = $"{canonicalPath}\\document.txt",
        Classification = "text/plain",
        Extension = ".txt",
        ByteLength = 1,
        DiscoveredAtUtc = now
    };

    private static SourceArtifactEntity Artifact(Guid id, Guid revisionId, DateTimeOffset now) => new()
    {
        Id = id,
        SourceRevisionId = revisionId,
        ContentSha256 = new string('a', 64),
        StoreRelativePath = "sha256\\aa\\artifact.bin",
        ByteLength = 1,
        ChecksumVerifiedAtUtc = now
    };

    private static string LongCanonicalPath() =>
        "C:\\" + string.Join('\\', Enumerable.Repeat(new string('p', 50), 40));
}
