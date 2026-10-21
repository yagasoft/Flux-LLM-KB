using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class SegmentOwnerLifetimeTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Existing_cache_sweep_runs_cooperative_storage_maintenance_without_repeating_resolved_or_unchanged_warnings()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Maintenance uses the existing host loop rather than another scheduler.", segmented: true);
        var work = new ScriptedMaintenance();
        var services = new ServiceCollection().AddSingleton<IIndexProjectionMaintenance>(work);
        await using var provider = services.BuildServiceProvider();
        var time = new MaintenanceClock();
        var log = new MaintenanceLogger();
        using var host = new UsearchSegmentCacheMaintenance(environment.Services.GetRequiredService<UsearchSegmentCache>(),
            time, log, provider.GetRequiredService<IServiceScopeFactory>());
        await host.RunOnceAsync(CancellationToken.None);
        await host.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, work.Calls);
        time.Advance(); await host.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, work.Calls);
        Assert.Equal(1, log.Levels.Count(value => value == LogLevel.Warning));
        time.Advance(); await host.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, log.Levels.Count(value => value == LogLevel.Warning));
        time.Advance(); await host.RunOnceAsync(CancellationToken.None);
        Assert.Equal(4, work.Calls);
        Assert.Equal(0, log.Levels.Count(value => value == LogLevel.Information));
        time.Advance(); await host.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, log.Levels.Count(value => value == LogLevel.Information));
        time.Advance(); await host.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, log.Levels.Count(value => value == LogLevel.Information));
    }

    private sealed class MaintenanceClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(30);
    }
    private sealed class ScriptedMaintenance : IIndexProjectionMaintenance
    {
        public int Calls { get; private set; }
        public ValueTask<IndexProjectionMaintenanceResult> RunOnceAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= 2) throw new IOException("synthetic current maintenance failure");
            return ValueTask.FromResult(new IndexProjectionMaintenanceResult(0, 0, 0, Calls == 3 ? "segment-retirement-owner-active" : null,
                WorkRemaining: Calls == 4));
        }
    }
    private sealed class MaintenanceLogger : ILogger<UsearchSegmentCacheMaintenance>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    [NativeSqlServerFact]
    public async Task Warm_segments_reuse_one_native_handle_and_release_durable_ownership_only_after_disposal()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Native ownership survives a query and protects an idle reusable index.", segmented: true);
        using var scope = environment.Services.CreateScope();
        var state = await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None);
        var manifest = await scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>().ReadManifestAsync(state!.Value, CancellationToken.None);
        var segment = Assert.Single(manifest.Segments);
        var store = new SqlIndexSegmentOwnerStore(environment.Factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        await using var cache = new UsearchSegmentCache(new(environment.IndexRoot), new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot),
            store, TimeProvider.System);
        var first = await cache.BorrowAsync(segment, CancellationToken.None);
        await using var second = await cache.BorrowAsync(segment, CancellationToken.None);
        Assert.Equal(first.NativeInstanceId, second.NativeInstanceId);
        await first.DisposeAsync();
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(1, await context.IndexSegmentOwners.CountAsync());
        await cache.SweepAsync(forceIdle: true, CancellationToken.None);
        Assert.Equal(1, await context.IndexSegmentOwners.CountAsync());
        await second.DisposeAsync();
        // Idle cache ownership is durable but holds no SQL recovery session lock.
        await using (var exclusive = await new SqlDerivedIndexRecoveryStore(environment.Factory, TimeProvider.System)
            .TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None)) Assert.NotNull(exclusive);
        await cache.SweepAsync(forceIdle: true, CancellationToken.None);
        Assert.Equal(0, await context.IndexSegmentOwners.CountAsync());
        await using var reopened = await cache.BorrowAsync(segment, CancellationToken.None);
        Assert.NotEqual(first.NativeInstanceId, reopened.NativeInstanceId);
    }

    [NativeSqlServerFact]
    public async Task A_partial_cache_keeps_fitting_residents_through_repeated_scans_and_overlapping_borrowers()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "One resident survives scanning a larger working set.", segmented: true);
        await environment.AddAndPumpAsync("Another source creates a separate immutable segment.");
        using var scope = environment.Services.CreateScope();
        var active = await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None);
        var manifest = await scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>().ReadManifestAsync(active!.Value, CancellationToken.None);
        Assert.Equal(2, manifest.Segments.Count);
        var resident = manifest.Segments[0]; var transient = manifest.Segments[1];
        await using var cache = new UsearchSegmentCache(new(environment.IndexRoot), new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot),
            new SqlIndexSegmentOwnerStore(environment.Factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe()),
            TimeProvider.System, maximumBytes: resident.FileLength);
        var first = await cache.BorrowAsync(resident, CancellationToken.None);
        var residentInstance = first.NativeInstanceId;
        await using (var overflow = await cache.BorrowAsync(transient, CancellationToken.None))
        {
            await first.DisposeAsync(); // The resident releases while the overflow borrower is still active.
            await using var stillResident = await cache.BorrowAsync(resident, CancellationToken.None);
            Assert.Equal(residentInstance, stillResident.NativeInstanceId);
        }
        for (var iteration = 0; iteration < 3; iteration++)
        {
            await using (var reused = await cache.BorrowAsync(resident, CancellationToken.None))
                Assert.Equal(residentInstance, reused.NativeInstanceId);
            await using (var overflow = await cache.BorrowAsync(transient, CancellationToken.None)) { }
            await using var db = await environment.Factory.CreateDbContextAsync();
            Assert.Equal(resident.Id, await db.IndexSegmentOwners.Select(value => value.SegmentId).SingleAsync());
        }
        await cache.SweepAsync(forceIdle: true, CancellationToken.None);
        await using var final = await environment.Factory.CreateDbContextAsync();
        Assert.Empty(await final.IndexSegmentOwners.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Retirement_disposes_idle_native_owners_and_refuses_new_borrowers()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "A retired immutable segment cannot be reopened.", segmented: true);
        using var scope = environment.Services.CreateScope();
        var id = await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None);
        var manifest = await scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>().ReadManifestAsync(id!.Value, CancellationToken.None);
        var segment = Assert.Single(manifest.Segments);
        var store = new SqlIndexSegmentOwnerStore(environment.Factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        await using var cache = new UsearchSegmentCache(new(environment.IndexRoot), new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot), store, TimeProvider.System);
        await using (var borrowed = await cache.BorrowAsync(segment, CancellationToken.None)) { }
        await using var context = await environment.Factory.CreateDbContextAsync();
        // Simulates the committed first retirement phase. It must release its fence before asking owners to dispose.
        await context.IndexSegments.Where(value => value.Id == segment.Id).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, 2));
        await cache.SweepAsync(forceIdle: false, CancellationToken.None);
        Assert.False(await context.IndexSegmentOwners.AnyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.BorrowAsync(segment, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(segment.DirectoryPath, "index.usearch")));
    }

    [NativeSqlServerFact]
    public async Task Failed_native_disposal_keeps_its_owner_and_a_repeated_sweep_can_complete_it()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "A disposal failure must not be acknowledged as released.", segmented: true);
        using var scope = environment.Services.CreateScope();
        var id = await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None);
        var manifest = await scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>().ReadManifestAsync(id!.Value, CancellationToken.None);
        var segment = Assert.Single(manifest.Segments);
        var store = new SqlIndexSegmentOwnerStore(environment.Factory, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        await using var cache = new UsearchSegmentCache(new(environment.IndexRoot), new WindowsIndexSegmentReadLeaseFactory(environment.IndexRoot), store, TimeProvider.System);
        await using (var borrowed = await cache.BorrowAsync(segment, CancellationToken.None)) { }
        cache.BeforeNativeDisposal = _ => throw new IOException("injected native disposal uncertainty");
        await Assert.ThrowsAsync<IOException>(async () => await cache.SweepAsync(true, CancellationToken.None));
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.True(await context.IndexSegmentOwners.AnyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.BorrowAsync(segment, CancellationToken.None));
        cache.BeforeNativeDisposal = null;
        await cache.SweepAsync(true, CancellationToken.None);
        Assert.False(await context.IndexSegmentOwners.AnyAsync());
        await cache.SweepAsync(true, CancellationToken.None);
    }

}
