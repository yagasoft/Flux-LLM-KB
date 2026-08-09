using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceOperatorEventIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Rescans_emit_one_added_one_updated_and_one_removed_without_duplicate_added()
    {
        var rootId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-08-09T10:00:00+00:00");
        var root = SourceRootConfiguration.Restore(new SourceRootId(rootId), $"C:\\operator-events\\{rootId:N}", "events", true, false, 1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1);
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = root.CanonicalPath, DisplayName = root.DisplayName, State = (int)root.State,
                Recursive = true, FollowLinks = false, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]",
                MaximumFileBytes = root.MaximumFileBytes, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }
        var store = new SqlSourceScanStore(new ContextFactory(_fixture.ConnectionString), new FixedTimeProvider(now));
        var firstFile = File(root.CanonicalPath, new string('a', 64));
        var changedFile = File(root.CanonicalPath, new string('b', 64));
        var first = await store.ConvergeRevisionAndArtifactAsync(root, firstFile, Receipt(firstFile), CancellationToken.None);
        var repeated = await store.ConvergeRevisionAndArtifactAsync(root, firstFile, Receipt(firstFile), CancellationToken.None);
        var changed = await store.ConvergeRevisionAndArtifactAsync(root, changedFile, Receipt(changedFile), CancellationToken.None);
        await store.SuppressUnseenAsync(root.Id, new HashSet<SourceRevisionId>(), CancellationToken.None);
        await store.SuppressUnseenAsync(root.Id, new HashSet<SourceRevisionId>(), CancellationToken.None);

        Assert.Equal(first, repeated);
        await using var verification = CreateContext();
        Assert.Single(await verification.AuditEvents.Where(x => x.SourceRevisionId == first.Value && x.EventType == "source.added").ToListAsync());
        Assert.Single(await verification.AuditEvents.Where(x => x.SourceRevisionId == changed.Value && x.EventType == "source.updated").ToListAsync());
        Assert.Single(await verification.AuditEvents.Where(x => x.SourceRevisionId == first.Value && x.EventType == "source.removed").ToListAsync());
        Assert.Single(await verification.AuditEvents.Where(x => x.SourceRevisionId == changed.Value && x.EventType == "source.removed").ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Operator_event_appender_uses_allowlisted_bounded_source_metadata()
    {
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = "C:\\operator-event-metadata", DisplayName = "metadata", State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]", MaximumFileBytes = 1024, ReconciliationCadenceSeconds = 900, CreatedAtUtc = now, UpdatedAtUtc = now });
        await context.SaveChangesAsync();
        context.SourceRevisions.Add(new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = "metadata-source", Revision = 1, ContentSha256 = new string('a', 64), CanonicalPath = "C:\\operator-event-metadata\\entry.txt", Classification = "AcceptedUtf8Text", Extension = ".txt", DiscoveredAtUtc = now });
        await context.SaveChangesAsync();
        OperatorEventAppender.Add(context, OperatorEventDraft.SourceAdded(rootId, null, revisionId, "source-test", new
        {
            revision = 1L,
            classification = "AcceptedUtf8Text",
            stage = "credential value",
            rawText = new string('x', 1000),
            leaseGeneration = 99,
            credential = "not-allowed"
        }));
        await context.SaveChangesAsync();

        var persisted = await context.AuditEvents.SingleAsync(value => value.SourceRevisionId == revisionId);
        Assert.Equal("source.added", persisted.EventType);
        Assert.Equal("source", persisted.EventFamily);
        Assert.Equal("source-test", persisted.CorrelationId);
        Assert.Equal("{\"revision\":1,\"classification\":\"AcceptedUtf8Text\"}", persisted.DetailsJson);
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private static SourceDiscoveredFile File(string rootPath, string hash) => new($"{rootPath}\\sentinel.txt", "sentinel.txt", "stable:sentinel", "text"u8.ToArray(), true, hash, 4, DateTimeOffset.UtcNow, new SourceClassificationResult(SourceClassification.AcceptedUtf8Text, "text", null));
    private static SourceArtifactReceipt Receipt(SourceDiscoveredFile file) => new(SourceArtifactId.New(), file.ContentSha256, $"sha256\\{file.ContentSha256[..2]}\\{file.ContentSha256}.bin", file.ByteLength, false);
    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
