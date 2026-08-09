using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class Phase3BWatchAndEventSchemaTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Existing_pipeline_audit_row_remains_valid_without_source_correlations()
    {
        await using var context = CreateContext();
        context.AuditEvents.Add(new AuditEventEntity
        {
            EventType = "pipeline.stage_completed",
            Actor = "test",
            DetailsJson = "{}",
            OccurredAtUtc = DateTimeOffset.UnixEpoch
        });

        await context.SaveChangesAsync();

        Assert.Null((await context.AuditEvents.SingleAsync()).SourceRootId);
    }

    [NativeSqlServerFact]
    public async Task Phase_3_source_cleanup_removes_correlated_scan_audit_events_before_scan_control()
    {
        var rootId = Guid.NewGuid();
        var scanRequestId = Guid.NewGuid();
        var now = DateTimeOffset.UnixEpoch;

        await using (var context = CreateContext())
        {
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $"C:\\phase3b-schema-tests\\{rootId:N}",
                DisplayName = "Phase 3B cleanup root",
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
            context.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = scanRequestId,
                SourceRootId = rootId,
                RequestKind = 0,
                RequestedBy = "integration-test",
                RequestedAtUtc = now,
                State = 0
            });
            context.AuditEvents.Add(new AuditEventEntity
            {
                EventType = "scan.released",
                Actor = "test",
                DetailsJson = "{}",
                OccurredAtUtc = now,
                SourceScanRequestId = scanRequestId
            });
            await context.SaveChangesAsync();
        }

        await SqlTestData.ClearPhase3SourceDataAsync(_fixture);

        await using var verification = CreateContext();
        Assert.Empty(await verification.AuditEvents.ToListAsync());
        Assert.Empty(await verification.SourceScanRequests.ToListAsync());
        Assert.Empty(await verification.SourceRootConfigurations.ToListAsync());
    }

    [Fact]
    public void Watch_state_and_event_correlations_use_restricted_keys_and_timeline_indexes()
    {
        using var context = new FluxKnowledgeDbContext(
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=Phase3BWatchAndEventSchemaModel;Trusted_Connection=True;TrustServerCertificate=True")
                .Options);
        var model = context.GetService<IDesignTimeModel>().Model;

        var watchState = model.FindEntityType(typeof(SourceRootWatchStateEntity))!;
        Assert.Contains(
            watchState.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual(
                [
                    nameof(SourceRootWatchStateEntity.DueAtUtc),
                    nameof(SourceRootWatchStateEntity.LeaseExpiresAtUtc)
                ]));
        var rootForeignKey = Assert.Single(watchState.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, rootForeignKey.DeleteBehavior);

        var audit = model.FindEntityType(typeof(AuditEventEntity))!;
        Assert.Null(audit.FindProperty(nameof(AuditEventEntity.SourceRootId))!.GetMaxLength());
        Assert.Equal(256, audit.FindProperty(nameof(AuditEventEntity.CorrelationId))!.GetMaxLength());
        Assert.Equal(128, audit.FindProperty(nameof(AuditEventEntity.EventFamily))!.GetMaxLength());
        Assert.Equal(64, audit.FindProperty(nameof(AuditEventEntity.Severity))!.GetMaxLength());
        Assert.Contains(
            audit.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AuditEventEntity.OccurredAtUtc), nameof(AuditEventEntity.Id)]));
        Assert.All(audit.GetForeignKeys(), foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
    }

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString)
            .Options);
}
