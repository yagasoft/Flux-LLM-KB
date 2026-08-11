using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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

    [Fact]
    public void Native_worker_lifecycle_audit_contains_only_the_allowed_class_instance_correlation_and_bounded_reason()
    {
        var instanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var draft = OperatorEventDraft.NativeWorkerLifecycle(
            NativeWorkerLifecycleClass.Unresponsive,
            instanceId,
            7,
            DateTimeOffset.Parse("2026-08-11T09:00:00+00:00"));

        var persisted = OperatorEventAppender.Create(draft);

        Assert.Equal("native_worker.unresponsive", persisted.EventType);
        Assert.Equal("native_worker", persisted.EventFamily);
        Assert.Equal("native-worker:11111111222233334444555555555555", persisted.CorrelationId);
        Assert.Equal("{\"kind\":\"unresponsive\",\"reasonCode\":\"7\"}", persisted.DetailsJson);
        Assert.DoesNotContain("pipe", persisted.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nonce", persisted.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", persisted.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", persisted.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostic", persisted.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source", persisted.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", persisted.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Native_worker_lifecycle_audit_rejects_unknown_classes_and_unbounded_reason_codes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OperatorEventDraft.NativeWorkerLifecycle(
            (NativeWorkerLifecycleClass)99,
            Guid.NewGuid(),
            null,
            DateTimeOffset.Parse("2026-08-11T09:00:00+00:00")));
        Assert.Throws<ArgumentOutOfRangeException>(() => OperatorEventDraft.NativeWorkerLifecycle(
            NativeWorkerLifecycleClass.Unresponsive,
            Guid.NewGuid(),
            65536,
            DateTimeOffset.Parse("2026-08-11T09:00:00+00:00")));
    }

    [NativeSqlServerFact]
    public async Task Native_worker_persistence_audits_only_a_sanitised_lifecycle_summary()
    {
        var now = DateTimeOffset.Parse("2026-08-11T09:00:00+00:00");
        var instanceId = Guid.NewGuid();
        var store = new SqlNativeWorkerInstanceStore(
            new ContextFactory(_fixture.ConnectionString),
            new FixedTimeProvider(now));
        await store.CreateAsync(
            Guid.NewGuid(),
            new NativeWorkerLaunchRequest(
                instanceId,
                "executor-a",
                new string('a', 64),
                NativeWorkerProtocol.SupportedVersion),
            CancellationToken.None);
        await store.RecordConnectionAsync(
            Guid.NewGuid(),
            new NativeWorkerConnectionAttestation(
                NativeWorkerInstanceHandle.Create(instanceId, "executor-a", 4321, now, NativeWorkerProtocol.SupportedVersion),
                new string('a', 64)),
            CancellationToken.None);
        await store.AppendEvidenceAsync(
            new NativeWorkerLifecycleEvidence(
                Guid.NewGuid(), instanceId, NativeWorkerLifecycleClass.Unresponsive, now.AddMinutes(1), 7, new string('b', 64)),
            CancellationToken.None);

        await using var context = CreateContext();
        var events = await context.AuditEvents
            .Where(value => value.CorrelationId == $"native-worker:{instanceId:N}")
            .OrderBy(value => value.OccurredAtUtc)
            .ToListAsync();

        Assert.Equal(3, events.Count);
        Assert.All(events, value =>
        {
            Assert.StartsWith("native_worker.", value.EventType, StringComparison.Ordinal);
            Assert.Equal("native_worker", value.EventFamily);
            Assert.DoesNotContain("pipe", value.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nonce", value.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("command", value.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("path", value.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("diagnostic", value.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("source", value.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model", value.DetailsJson, StringComparison.OrdinalIgnoreCase);
            using var details = JsonDocument.Parse(value.DetailsJson);
            Assert.All(details.RootElement.EnumerateObject(), property =>
                Assert.Contains(property.Name, new[] { "kind", "reasonCode" }));
        });
        Assert.Equal(now.AddMinutes(1), events[^1].OccurredAtUtc);
        Assert.Equal("{\"kind\":\"unresponsive\",\"reasonCode\":\"7\"}", events[^1].DetailsJson);
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
