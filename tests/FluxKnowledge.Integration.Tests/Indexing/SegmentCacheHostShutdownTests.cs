using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class SegmentCacheHostShutdownTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Hosted_stop_drains_warm_native_segments_before_the_real_EF_provider_is_disposed()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "A warm native segment must release its owner before its SQL services stop.", segmented: true);
        var segment = await ReadSegmentAsync(environment);
        await using var host = await HostRig.CreateAsync(fixture.ConnectionString, environment.IndexRoot);
        await using (var borrowed = await host.Cache.BorrowAsync(segment, CancellationToken.None)) { }
        Assert.Equal(1, await OwnerCountAsync(environment));
        Assert.Equal(1, host.Paths.ActiveReadLeases);

        await host.Host.StopAsync();

        Assert.Equal(0, host.Paths.ActiveReadLeases);
        Assert.Equal(1, host.Owners.Acknowledgements);
        Assert.Equal(0, await OwnerCountAsync(environment));
        await host.Host.StopAsync();
        await host.DisposeProviderAsync();
        await host.DisposeProviderAsync();
        Assert.Equal(1, host.Owners.Acknowledgements);
    }

    [NativeSqlServerFact]
    public async Task Failed_native_disposal_during_hosted_stop_remains_observable_and_keeps_the_SQL_owner()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "An unconfirmed native close must retain durable ownership.", segmented: true);
        var segment = await ReadSegmentAsync(environment);
        await using var host = await HostRig.CreateAsync(fixture.ConnectionString, environment.IndexRoot);
        await using (var borrowed = await host.Cache.BorrowAsync(segment, CancellationToken.None)) { }
        host.BeforeNativeDisposal = () => throw new IOException("synthetic unresolved native disposal");

        var failure = await Assert.ThrowsAsync<IOException>(() => host.Host.StopAsync());

        Assert.Equal("synthetic unresolved native disposal", failure.Message);
        Assert.Equal(1, host.Paths.ActiveReadLeases);
        Assert.Equal(0, host.Owners.Acknowledgements);
        Assert.Equal(1, await OwnerCountAsync(environment));
        host.BeforeNativeDisposal = null;
        await host.Cache.DisposeAsync();
        Assert.Equal(0, await OwnerCountAsync(environment));
    }

    [NativeSqlServerFact]
    public async Task Stopping_refuses_new_borrows_and_active_release_acknowledges_only_while_DI_is_alive()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "An exceptional active borrower keeps its native and durable owners until release.", segmented: true);
        var segment = await ReadSegmentAsync(environment);
        await using var host = await HostRig.CreateAsync(fixture.ConnectionString, environment.IndexRoot);
        await using var active = await host.Cache.BorrowAsync(segment, CancellationToken.None);

        await host.Host.StopAsync();

        Assert.False(host.Paths.NativeDisposalStarted);
        Assert.Equal(1, host.Paths.ActiveReadLeases);
        Assert.Equal(1, await OwnerCountAsync(environment));
        var rejected = await Record.ExceptionAsync(async () =>
        {
            await using var unexpected = await host.Cache.BorrowAsync(segment, CancellationToken.None);
        });
        Assert.IsType<ObjectDisposedException>(rejected);
        await active.DisposeAsync();
        Assert.Equal(0, host.Paths.ActiveReadLeases);
        Assert.Equal(1, host.Owners.Acknowledgements);
        Assert.Equal(0, await OwnerCountAsync(environment));
        await host.DisposeProviderAsync();
    }

    [NativeSqlServerFact]
    public async Task Release_after_provider_disposal_does_not_claim_a_durable_acknowledgement()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "A late release must remain unacknowledged if its SQL provider has already stopped.", segmented: true);
        var segment = await ReadSegmentAsync(environment);
        await using var host = await HostRig.CreateAsync(fixture.ConnectionString, environment.IndexRoot);
        await using var active = await host.Cache.BorrowAsync(segment, CancellationToken.None);
        await host.Host.StopAsync();
        await host.DisposeProviderAsync();
        Assert.Equal(1, host.Paths.ActiveReadLeases);
        Assert.Equal(1, await OwnerCountAsync(environment));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => active.DisposeAsync().AsTask());

        Assert.Equal(0, host.Paths.ActiveReadLeases);
        Assert.Equal(0, host.Owners.Acknowledgements);
        Assert.Equal(1, await OwnerCountAsync(environment));
    }

    private static async Task<IndexSegmentDescriptor> ReadSegmentAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment)
    {
        using var scope = environment.Services.CreateScope();
        var active = await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None);
        var manifest = await scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>()
            .ReadManifestAsync(active!.Value, CancellationToken.None);
        return Assert.Single(manifest.Segments);
    }

    private static async Task<int> OwnerCountAsync(SqlToUsearchRebuildTests.PipelineEnvironment environment)
    {
        await using var context = await environment.Factory.CreateDbContextAsync();
        return await context.IndexSegmentOwners.CountAsync();
    }

    private sealed class HostRig(IHost host, TrackingPaths paths, TrackingOwners owners) : IAsyncDisposable
    {
        private bool _providerDisposed;
        public IHost Host { get; } = host;
        public TrackingPaths Paths { get; } = paths;
        public TrackingOwners Owners { get; } = owners;
        public UsearchSegmentCache Cache => Host.Services.GetRequiredService<UsearchSegmentCache>();
        public Action? BeforeNativeDisposal { get; set; }

        public static async Task<HostRig> CreateAsync(string connection, string indexRoot)
        {
            var paths = new TrackingPaths(new WindowsIndexSegmentReadLeaseFactory(indexRoot));
            var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().UseEnvironment("Testing")
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices(services =>
                {
                    // The runtime EF factory retains its DI provider. A standalone
                    // fixture factory would conceal the observed disposal-order bug.
                    services.AddDbContextFactory<FluxKnowledgeDbContext>(options => options.UseSqlServer(connection));
                    services.AddSingleton<IGpuInteractiveOwnerProbe, WindowsInteractiveGpuOwnerProbe>();
                    services.AddSingleton<TimeProvider>(TimeProvider.System);
                    services.AddSingleton<SqlIndexSegmentOwnerStore>();
                    services.AddSingleton(paths);
                    services.AddSingleton(provider => new TrackingOwners(provider.GetRequiredService<SqlIndexSegmentOwnerStore>(), paths));
                    services.AddSingleton<IIndexSegmentOwnerStore>(provider => provider.GetRequiredService<TrackingOwners>());
                    services.AddSingleton(provider => new UsearchSegmentCache(new(indexRoot), paths,
                        provider.GetRequiredService<IIndexSegmentOwnerStore>(), TimeProvider.System));
                    services.AddHostedService<UsearchSegmentCacheMaintenance>();
                }).Build();
            var result = new HostRig(host, paths, host.Services.GetRequiredService<TrackingOwners>());
            result.Cache.BeforeNativeDisposal = _ =>
            {
                paths.NativeDisposalStarted = true;
                result.BeforeNativeDisposal?.Invoke();
            };
            await host.StartAsync();
            return result;
        }

        public async ValueTask DisposeProviderAsync()
        {
            if (_providerDisposed) return;
            await ((IAsyncDisposable)Host).DisposeAsync();
            _providerDisposed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_providerDisposed) return;
            BeforeNativeDisposal = null;
            await Cache.DisposeAsync();
            await DisposeProviderAsync();
        }
    }

    private sealed class TrackingOwners(SqlIndexSegmentOwnerStore store, TrackingPaths paths) : IIndexSegmentOwnerStore
    {
        public int Acknowledgements { get; private set; }
        public ValueTask<Guid> RegisterAsync(IndexSegmentDescriptor segment, Guid instanceId, CancellationToken cancellationToken) =>
            store.RegisterAsync(segment, instanceId, cancellationToken);
        public ValueTask OpenedAsync(Guid ownerId, CancellationToken cancellationToken) => store.OpenedAsync(ownerId, cancellationToken);
        public ValueTask<IReadOnlySet<string>> ReadRetiringAsync(IReadOnlyList<string> segmentIds, CancellationToken cancellationToken) =>
            store.ReadRetiringAsync(segmentIds, cancellationToken);
        public async ValueTask ReleasedAfterDisposalAsync(Guid ownerId, CancellationToken cancellationToken)
        {
            Assert.True(paths.NativeDisposalStarted);
            Assert.Equal(0, paths.ActiveReadLeases);
            await store.ReleasedAfterDisposalAsync(ownerId, cancellationToken);
            Acknowledgements++;
        }
    }

    private sealed class TrackingPaths(IIndexSegmentReadLeaseFactory paths) : IIndexSegmentReadLeaseFactory
    {
        public int ActiveReadLeases { get; private set; }
        public bool NativeDisposalStarted { get; set; }
        public IDisposable Acquire(string directory)
        {
            var inner = paths.Acquire(directory);
            ActiveReadLeases++;
            return new TrackedLease(inner, () => ActiveReadLeases--);
        }
        public IDisposable AcquireManifest(string directory) => paths.AcquireManifest(directory);
        public IDisposable AcquireDirectory(string directory, bool create = false) => paths.AcquireDirectory(directory, create);
        private sealed class TrackedLease(IDisposable inner, Action released) : IDisposable
        {
            public void Dispose() { inner.Dispose(); released(); }
        }
    }
}
