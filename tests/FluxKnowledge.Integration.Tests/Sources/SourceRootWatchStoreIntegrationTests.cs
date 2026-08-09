using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceRootWatchStoreIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(_fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Burst_signals_release_one_scan_request_and_one_watch_event()
    {
        var now = DateTimeOffset.Parse("2026-08-09T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            await setup.SaveChangesAsync();
        }

        var store = new SqlSourceRootWatchStore(new ContextFactory(_fixture.ConnectionString), new FixedTimeProvider(now));
        await store.RecordSignalAsync(new SourceWatchSignal(new SourceRootId(rootId), SourceWatchSignalKind.Created, now), CancellationToken.None);
        await store.RecordSignalAsync(new SourceWatchSignal(new SourceRootId(rootId), SourceWatchSignalKind.Changed, now.AddMilliseconds(200)), CancellationToken.None);
        var batch = await store.ClaimDueBatchAsync(now.AddSeconds(3), "test", TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(batch);
        await store.ReleaseScanAsync(batch, CancellationToken.None);
        await using var verification = CreateContext();
        Assert.Single(await verification.SourceScanRequests.Where(value => value.SourceRootId == rootId).ToListAsync());
        Assert.Single(await verification.AuditEvents.Where(value => value.SourceRootId == rootId && value.EventType == "watch.batch_detected").ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Later_watcher_generation_keeps_a_follow_up_release_when_the_previous_scan_is_running()
    {
        var now = DateTimeOffset.Parse("2026-08-09T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        var runningRequestId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(Root(rootId, now));
            setup.SourceScanRequests.Add(new SourceScanRequestEntity { Id = runningRequestId, SourceRootId = rootId, RequestKind = 1, RequestedBy = "reconciliation", RequestedAtUtc = now, IsReleased = true, ReleasedAtUtc = now, State = (int)SourceScanRequestState.Running });
            setup.SourceScanJobs.Add(new SourceScanJobEntity { Id = Guid.NewGuid(), SourceScanRequestId = runningRequestId, State = (int)SourceScanJobState.Running, DueAtUtc = now, LeaseOwner = "earlier", LeaseExpiresAtUtc = now.AddMinutes(1), LeaseGeneration = 1, AttemptCount = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            setup.SourceScanOutbox.Add(new SourceScanOutboxEntity { Id = Guid.NewGuid(), SourceScanRequestId = runningRequestId, Operation = "source.scan", IdempotencyKey = $"source-scan:{runningRequestId:N}", DueAtUtc = now, CreatedAtUtc = now });
            await setup.SaveChangesAsync();
        }

        var store = new SqlSourceRootWatchStore(new ContextFactory(_fixture.ConnectionString), new FixedTimeProvider(now));
        await store.RecordSignalAsync(new SourceWatchSignal(new SourceRootId(rootId), SourceWatchSignalKind.Overflow, now), CancellationToken.None);
        var batch = await store.ClaimDueBatchAsync(now, "watch", TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.ReleaseScanAsync(Assert.IsType<ClaimedSourceWatchBatch>(batch), CancellationToken.None);

        await using var verification = CreateContext();
        var requests = await verification.SourceScanRequests.Where(request => request.SourceRootId == rootId).ToListAsync();
        Assert.Equal(2, requests.Count);
        Assert.Contains(requests, request => request.Id == runningRequestId && request.State == (int)SourceScanRequestState.Running);
        Assert.Contains(requests, request => request.Id != runningRequestId && request.State == (int)SourceScanRequestState.Released && request.IsReleased);
    }

    [NativeSqlServerFact]
    public async Task Overflow_persists_a_full_scan_release_and_one_overflow_event()
    {
        var now = DateTimeOffset.Parse("2026-08-09T12:00:00+00:00");
        var rootId = Guid.NewGuid();
        await using (var setup = CreateContext()) { setup.SourceRootConfigurations.Add(Root(rootId, now)); await setup.SaveChangesAsync(); }
        var store = new SqlSourceRootWatchStore(new ContextFactory(_fixture.ConnectionString), new FixedTimeProvider(now));
        await store.RecordSignalAsync(new SourceWatchSignal(new SourceRootId(rootId), SourceWatchSignalKind.Overflow, now), CancellationToken.None);
        var batch = await store.ClaimDueBatchAsync(now, "watch", TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.ReleaseScanAsync(Assert.IsType<ClaimedSourceWatchBatch>(batch), CancellationToken.None);
        await using var verification = CreateContext();
        Assert.Single(await verification.SourceScanRequests.Where(request => request.SourceRootId == rootId && request.IsReleased).ToListAsync());
        Assert.Single(await verification.AuditEvents.Where(@event => @event.SourceRootId == rootId && @event.EventType == "watch.overflow_detected").ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Persisted_due_batch_is_released_after_a_coordinator_restart()
    {
        var now = DateTimeOffset.Parse("2026-08-09T12:00:00+00:00"); var rootId = Guid.NewGuid();
        await using (var setup = CreateContext()) { setup.SourceRootConfigurations.Add(Root(rootId, now)); await setup.SaveChangesAsync(); }
        await new SqlSourceRootWatchStore(new ContextFactory(_fixture.ConnectionString), new FixedTimeProvider(now)).RecordSignalAsync(new SourceWatchSignal(new SourceRootId(rootId), SourceWatchSignalKind.Overflow, now), CancellationToken.None);
        var restarted = new SqlSourceRootWatchStore(new ContextFactory(_fixture.ConnectionString), new FixedTimeProvider(now));
        var batch = await restarted.ClaimDueBatchAsync(now, "after-restart", TimeSpan.FromMinutes(1), CancellationToken.None);
        await restarted.ReleaseScanAsync(Assert.IsType<ClaimedSourceWatchBatch>(batch), CancellationToken.None);
        await using var verification = CreateContext();
        Assert.Single(await verification.SourceScanRequests.Where(request => request.SourceRootId == rootId && request.IsReleased).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Newer_signal_after_claim_survives_first_batch_release_as_a_separate_due_batch()
    {
        var now = DateTimeOffset.Parse("2026-08-09T12:00:00+00:00"); var rootId = Guid.NewGuid();
        await using (var setup = CreateContext()) { setup.SourceRootConfigurations.Add(Root(rootId, now)); await setup.SaveChangesAsync(); }
        var store = new SqlSourceRootWatchStore(new ContextFactory(_fixture.ConnectionString), new FixedTimeProvider(now));
        await store.RecordSignalAsync(new SourceWatchSignal(new SourceRootId(rootId), SourceWatchSignalKind.Overflow, now), CancellationToken.None);
        var first = Assert.IsType<ClaimedSourceWatchBatch>(await store.ClaimDueBatchAsync(now, "first", TimeSpan.FromMinutes(1), CancellationToken.None));
        await store.RecordSignalAsync(new SourceWatchSignal(new SourceRootId(rootId), SourceWatchSignalKind.Overflow, now.AddSeconds(1)), CancellationToken.None);

        await store.ReleaseScanAsync(first, CancellationToken.None);
        var second = await store.ClaimDueBatchAsync(now.AddSeconds(1), "second", TimeSpan.FromMinutes(1), CancellationToken.None);

        await store.ReleaseScanAsync(Assert.IsType<ClaimedSourceWatchBatch>(second), CancellationToken.None);
        await using var verification = CreateContext();
        Assert.Equal(2, await verification.SourceScanRequests.CountAsync(request => request.SourceRootId == rootId && request.IsReleased));
        Assert.Equal(2, await verification.SourceScanJobs.CountAsync(job => job.SourceScanRequest.SourceRootId == rootId));
        Assert.Equal(2, await verification.SourceScanOutbox.CountAsync(outbox => outbox.SourceScanRequest.SourceRootId == rootId));
        Assert.Equal(2, await verification.AuditEvents.CountAsync(@event => @event.SourceRootId == rootId && @event.EventType == "watch.batch_detected"));
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).Options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static SourceRootConfigurationEntity Root(Guid id, DateTimeOffset now) => new()
    {
        Id = id, CanonicalPath = $"C:\\source-watch-tests\\{id:N}", DisplayName = "Watch", State = (int)SourceRootState.Enabled,
        Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 1024,
        AllowedClassificationsJson = "[]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1,
        CreatedAtUtc = now, UpdatedAtUtc = now
    };
}
