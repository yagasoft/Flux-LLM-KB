using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class PipelineOperatorEventIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Registration_retains_legacy_event_and_appends_correlated_pipeline_event()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var store = new SqlPipelineStore(new ContextFactory(_fixture.ConnectionString));
        var receipt = await store.RegisterAsync(new Utf8FileRegistration($"C:\\events\\{Guid.NewGuid():N}.txt", new string('a', 64), "test", null), CancellationToken.None);
        await using var context = CreateContext();
        Assert.Single(await context.AuditEvents.Where(value => value.PipelineRecordId == receipt.PipelineRecordId.Value && value.EventType == "pipeline record registered").ToListAsync());
        Assert.Single(await context.AuditEvents.Where(value => value.PipelineRecordId == receipt.PipelineRecordId.Value && value.EventType == "pipeline.registered").ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Operator_event_appender_persists_pipeline_transition_metadata()
    {
        var store = new SqlPipelineStore(new ContextFactory(_fixture.ConnectionString));
        var receipt = await store.RegisterAsync(new Utf8FileRegistration($"C:\\events\\{Guid.NewGuid():N}.txt", new string('a', 64), "test", null), CancellationToken.None);
        await using var context = CreateContext();
        OperatorEventAppender.Add(context, OperatorEventDraft.PipelineCompleted(receipt.PipelineRecordId.Value, "pipeline-test", new { stage = "Publish" }));
        await context.SaveChangesAsync();

        var persisted = await context.AuditEvents.SingleAsync(value => value.PipelineRecordId == receipt.PipelineRecordId.Value && value.EventType == "pipeline.completed");
        Assert.Equal("pipeline.completed", persisted.EventType);
        Assert.Equal("pipeline", persisted.EventFamily);
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);
    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
