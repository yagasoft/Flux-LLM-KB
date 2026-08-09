using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class SourceRootTransactionTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(_fixture);

    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Create_save_only_persists_a_held_request_with_no_due_control_work()
    {
        var store = CreateStore();

        var receipt = await store.CreateAsync(Request("C:\\source-transaction-tests\\held"), ScanStartIntent.SaveOnly, CancellationToken.None);

        await using var context = CreateContext();
        var request = await context.SourceScanRequests.SingleAsync(value => value.Id == receipt.SourceScanRequestId.Value);
        Assert.False(request.IsReleased);
        Assert.Null(request.ReleasedAtUtc);
        Assert.Equal(DateTimeOffset.MaxValue, await context.SourceScanJobs.Where(value => value.Id == receipt.ControlJobId.Value).Select(value => value.DueAtUtc).SingleAsync());
        Assert.Equal(DateTimeOffset.MaxValue, await context.SourceScanOutbox.Where(value => value.Id == receipt.OutboxId.Value).Select(value => value.DueAtUtc).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Create_save_and_scan_releases_request_and_due_values_at_one_committed_time()
    {
        var now = DateTimeOffset.Parse("2026-08-08T12:00:00+00:00");
        var store = CreateStore(new FixedTimeProvider(now));

        var receipt = await store.CreateAsync(Request("C:\\source-transaction-tests\\released"), ScanStartIntent.SaveAndScan, CancellationToken.None);

        await using var restartedContext = CreateContext();
        var request = await restartedContext.SourceScanRequests.SingleAsync(value => value.Id == receipt.SourceScanRequestId.Value);
        Assert.True(request.IsReleased);
        Assert.Equal(now, request.ReleasedAtUtc);
        Assert.Equal(now, await restartedContext.SourceScanJobs.Where(value => value.Id == receipt.ControlJobId.Value).Select(value => value.DueAtUtc).SingleAsync());
        Assert.Equal(now, await restartedContext.SourceScanOutbox.Where(value => value.Id == receipt.OutboxId.Value).Select(value => value.DueAtUtc).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Repeat_save_and_scan_releases_the_existing_held_control_request_without_creating_duplicates()
    {
        var now = DateTimeOffset.Parse("2026-08-08T12:00:00+00:00");
        var store = CreateStore(new FixedTimeProvider(now));
        var request = Request("C:\\source-transaction-tests\\idempotent");

        var first = await store.CreateAsync(request, ScanStartIntent.SaveOnly, CancellationToken.None);
        var second = await store.CreateAsync(request, ScanStartIntent.SaveAndScan, CancellationToken.None);

        Assert.Equal(first.SourceRootId, second.SourceRootId);
        Assert.Equal(first.SourceScanRequestId, second.SourceScanRequestId);
        Assert.Equal(first.ControlJobId, second.ControlJobId);
        Assert.Equal(first.OutboxId, second.OutboxId);
        Assert.True(first.IsHeld);
        Assert.False(second.IsHeld);
        await using var context = CreateContext();
        Assert.Single(await context.SourceRootConfigurations.ToListAsync());
        Assert.Single(await context.SourceScanRequests.ToListAsync());
        Assert.True(await context.SourceScanRequests.Select(value => value.IsReleased).SingleAsync());
        Assert.Equal(now, await context.SourceScanRequests.Select(value => value.ReleasedAtUtc).SingleAsync());
        Assert.Equal(now, await context.SourceScanJobs.Select(value => value.DueAtUtc).SingleAsync());
        Assert.Equal(now, await context.SourceScanOutbox.Select(value => value.DueAtUtc).SingleAsync());
    }

    [NativeSqlServerFact]
    public async Task Create_rolls_back_every_control_record_when_the_transaction_fails()
    {
        var store = CreateStore(failureInjector: static () => throw new InvalidOperationException("injected"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CreateAsync(Request("C:\\source-transaction-tests\\rollback"), ScanStartIntent.SaveOnly, CancellationToken.None));

        await using var context = CreateContext();
        Assert.Empty(await context.SourceRootConfigurations.ToListAsync());
        Assert.Empty(await context.SourceScanRequests.ToListAsync());
        Assert.Empty(await context.SourceScanJobs.ToListAsync());
        Assert.Empty(await context.SourceScanOutbox.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Create_with_the_same_path_but_a_different_display_name_rejects_the_changed_configuration()
    {
        var store = CreateStore();
        var first = Request("C:\\source-transaction-tests\\name");
        await store.CreateAsync(first, ScanStartIntent.SaveOnly, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CreateAsync(first with { DisplayName = "Different label" }, ScanStartIntent.SaveOnly, CancellationToken.None));

        Assert.Contains("configuration fingerprint", exception.Message, StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Create_rejects_a_physical_root_replacement_at_the_same_canonical_path()
    {
        var store = CreateStore();
        var first = Request("C:\\source-transaction-tests\\identity") with { PathValidation = Validation("identity-a") };
        await store.CreateAsync(first, ScanStartIntent.SaveOnly, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CreateAsync(first with { PathValidation = Validation("identity-b") }, ScanStartIntent.SaveOnly, CancellationToken.None));

        Assert.Contains("physical identity", exception.Message, StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Release_is_idempotent_and_updates_both_due_values()
    {
        var now = DateTimeOffset.Parse("2026-08-08T12:00:00+00:00");
        var store = CreateStore(new FixedTimeProvider(now));
        var receipt = await store.CreateAsync(Request("C:\\source-transaction-tests\\release"), ScanStartIntent.SaveOnly, CancellationToken.None);

        Assert.True(await store.ReleaseAsync(receipt.SourceRootId, receipt.SourceScanRequestId, "operator", CancellationToken.None));
        Assert.False(await store.ReleaseAsync(receipt.SourceRootId, receipt.SourceScanRequestId, "operator", CancellationToken.None));

        await using var context = CreateContext();
        Assert.Equal(now, await context.SourceScanJobs.Where(value => value.Id == receipt.ControlJobId.Value).Select(value => value.DueAtUtc).SingleAsync());
        Assert.Equal(now, await context.SourceScanOutbox.Where(value => value.Id == receipt.OutboxId.Value).Select(value => value.DueAtUtc).SingleAsync());
        var evidence = await context.SourceScanRequests.Select(value => value.AuditEvidenceJson).SingleAsync();
        Assert.Contains("configurationFingerprint", evidence, StringComparison.Ordinal);
        Assert.Contains("releasedByFingerprint", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", evidence, StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_creates_return_one_existing_receipt_without_unique_key_failures()
    {
        var request = Request("C:\\source-transaction-tests\\concurrent-create");
        var gate = new LockBoundaryGate(expectedCalls: 2);
        var firstStore = CreateStore(lockObserver: gate.WaitAsync);
        var secondStore = CreateStore(lockObserver: gate.WaitAsync);

        var receipts = await Task.WhenAll(
            firstStore.CreateAsync(request, ScanStartIntent.SaveOnly, CancellationToken.None).AsTask(),
            secondStore.CreateAsync(request, ScanStartIntent.SaveOnly, CancellationToken.None).AsTask());

        Assert.Equal(receipts[0].SourceRootId, receipts[1].SourceRootId);
        Assert.Equal(receipts[0].SourceScanRequestId, receipts[1].SourceScanRequestId);
        Assert.Equal(2, gate.Calls);
        await using var context = CreateContext();
        Assert.Single(await context.SourceRootConfigurations.ToListAsync());
        Assert.Single(await context.SourceScanRequests.ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_releases_change_a_held_request_once_and_return_false_to_the_loser()
    {
        var store = CreateStore();
        var receipt = await store.CreateAsync(Request("C:\\source-transaction-tests\\concurrent-release"), ScanStartIntent.SaveOnly, CancellationToken.None);
        var gate = new LockBoundaryGate(expectedCalls: 2);
        store = CreateStore(lockObserver: gate.WaitAsync);
        var otherStore = CreateStore(lockObserver: gate.WaitAsync);

        var released = await Task.WhenAll(
            store.ReleaseAsync(receipt.SourceRootId, receipt.SourceScanRequestId, "operator-a", CancellationToken.None).AsTask(),
            otherStore.ReleaseAsync(receipt.SourceRootId, receipt.SourceScanRequestId, "operator-b", CancellationToken.None).AsTask());

        Assert.Equal(1, released.Count(value => value));
        Assert.Equal(2, gate.Calls);
        await using var context = CreateContext();
        Assert.True(await context.SourceScanRequests.Select(value => value.IsReleased).SingleAsync());
    }

    private SqlSourceRootStore CreateStore(
        TimeProvider? timeProvider = null,
        Action? failureInjector = null,
        Func<SourceRootLockPoint, CancellationToken, Task>? lockObserver = null) =>
        new(new PooledDbContextFactory<FluxKnowledgeDbContext>(
                new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                    .UseSqlServer(_fixture.ConnectionString, sqlServer => sqlServer.EnableRetryOnFailure())
                    .Options),
            timeProvider ?? TimeProvider.System,
            failureInjector,
            lockObserver);

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);

    private static SourceRootCreateRequest Request(string path) => new(
        path, "Transaction corpus", true, ["*.txt"], ["bin/**"], false, 1024,
        ["text/plain"], TimeSpan.FromMinutes(15), "integration-test");

    private static SourceRootPathValidation Validation(string identityFingerprint) => new(
        "C:\\source-transaction-tests\\identity",
        new SourceRootPhysicalIdentity(
            "C:\\source-transaction-tests\\identity",
            "C:\\",
            IsFixedNtfs: true,
            identityFingerprint),
        new SourceRootPermissionEvidence(true, "path-fingerprint", "{}"));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class LockBoundaryGate(int expectedCalls)
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task WaitAsync(SourceRootLockPoint _, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) == expectedCalls)
            {
                _allArrived.TrySetResult();
            }

            return _allArrived.Task.WaitAsync(cancellationToken);
        }
    }
}
