using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class OperatorEventProjectionIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(fixture); public Task DisposeAsync() => Task.CompletedTask;
    [NativeSqlServerFact]
    public async Task Event_filters_apply_root_revision_and_correlation_with_stable_keyset()
    {
        var root = Guid.NewGuid(); var revision = Guid.NewGuid(); var when = DateTimeOffset.UtcNow;
        await using (var db = Context())
        {
            db.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = root, CanonicalPath = "C:\\events", DisplayName = "Events", State = 1, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]", MaximumFileBytes = 1024, ReconciliationCadenceSeconds = 900, CreatedAtUtc = when, UpdatedAtUtc = when });
            await db.SaveChangesAsync();
            db.SourceRevisions.Add(new SourceRevisionEntity { Id = revision, SourceRootId = root, StableSourceIdentity = "event-source", Revision = 1, ContentSha256 = new string('a', 64), CanonicalPath = "C:\\events\\entry.txt", Classification = "AcceptedUtf8Text", Extension = ".txt", DiscoveredAtUtc = when });
            await db.SaveChangesAsync();
            db.AuditEvents.AddRange(new AuditEventEntity { SourceRootId=root, SourceRevisionId=revision, CorrelationId="correlation-a", EventFamily="source", Severity="information", EventType="source.added", Actor="test", DetailsJson="{\"private\":\"must-not-render\"}", OccurredAtUtc=when }, new AuditEventEntity { SourceRootId=root, SourceRevisionId=revision, CorrelationId="correlation-a", EventFamily="source", Severity="information", EventType="source.updated", Actor="test", DetailsJson="{}", OccurredAtUtc=when.AddTicks(-1) }, new AuditEventEntity { CorrelationId="correlation-b", EventFamily="watch", Severity="warning", EventType="watch.overflow_detected", Actor="test", DetailsJson="{}", OccurredAtUtc=when });
            await db.SaveChangesAsync();
        }
        var reader = new SqlOperatorEventProjectionReader(SqlTestData.CreateFactory(fixture)); var page = await reader.ReadPageAsync(new OperatorEventQuery(new OperatorEventFilters(SourceRootId:root, SourceRevisionId:revision, CorrelationId:"correlation-a"), PageSize:1), CancellationToken.None);
        Assert.Single(page.Items); Assert.Equal("correlation-a", page.Items[0].CorrelationId); Assert.Equal("{\"sanitised\":true}", page.Items[0].Details); Assert.NotNull(page.NextCursor);
        var next = await reader.ReadPageAsync(new OperatorEventQuery(new OperatorEventFilters(SourceRootId:root, SourceRevisionId:revision, CorrelationId:"correlation-a"), PageSize:1, Cursor:page.NextCursor), CancellationToken.None);
        Assert.Single(next.Items); Assert.NotEqual(page.Items[0].Id, next.Items[0].Id); Assert.Null(next.NextCursor);
    }
    private FluxKnowledgeDbContext Context() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options);
}
