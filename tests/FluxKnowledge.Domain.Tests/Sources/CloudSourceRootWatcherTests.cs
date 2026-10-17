using System.Collections.Concurrent;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class CloudSourceRootWatcherTests
{
    [Fact]
    public async Task Partial_cloud_root_delivers_hints_without_hydrating_its_unavailable_child()
    {
        using var fixture = new CloudSourceFixture();
        var path = fixture.Directory("documents");
        var offline = fixture.File("documents/offline.txt", "Private unavailable fixture", dehydrate: true);
        fixture.File("documents/available.txt", "Private available fixture");
        using var offlineMetadata = CloudSourceFixture.OpenMetadata(offline);
        fixture.MakeCloudDirectory(path);
        var root = Admitted(path);
        var store = new WatchStore(root);
        var logger = new WatchLogger();
        using var service = Service(store, logger);
        var before = WindowsSourcePathAccess.ReadMetadata(offlineMetadata);
        using (var lease = WindowsSourcePathAccess.OpenDirectoryPath(path)) Assert.True(lease.Metadata.NamespaceIncomplete);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await store.ReadRoots.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var attempt = 0; attempt < 50 && !store.Signal.Task.IsCompleted; attempt++)
            {
                File.WriteAllText(Path.Combine(path, "available.txt"), attempt.ToString());
                await Task.Delay(100);
            }
            var signal = await store.Signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(root.Id, signal.RootId);
            Assert.NotEqual(SourceWatchSignalKind.Overflow, signal.Kind);
            Assert.Empty(logger.Warnings);
            Assert.Equal(before, WindowsSourcePathAccess.ReadMetadata(offlineMetadata));
            using var after = WindowsSourcePathAccess.OpenDirectoryPath(path, enumerate: true);
            Assert.True(after.Metadata.NamespaceIncomplete);
            Assert.Equal(["available.txt", "offline.txt"], after.Entries(CancellationToken.None).Select(entry => entry.Name).Order().ToArray());
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(deadline.Token);
        }
    }

    [Fact]
    public async Task Watcher_pins_ordinary_root_and_ancestor_names_until_disposal()
    {
        using var fixture = new CloudSourceFixture();
        var parent = fixture.Directory("parent");
        var path = Directory.CreateDirectory(Path.Combine(parent, "documents")).FullName;
        var store = new WatchStore(Admitted(path));
        var logger = new WatchLogger();
        using var service = Service(store, logger);
        using (await service.BuildWatchersAsync(CancellationToken.None))
        {
            Assert.Empty(logger.Warnings);
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-moved"));
            Assert.Throws<IOException>(() => Directory.Move(path, path + "-moved"));
        }
        Directory.Move(parent, parent + "-moved");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("bad-identity", false)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", true)]
    public async Task Missing_malformed_or_mismatched_identity_refuses_watcher_and_releases_names(string? identity, bool required)
    {
        using var fixture = new CloudSourceFixture();
        var path = fixture.Directory("documents");
        var store = new WatchStore(Admitted(path, identity, required));
        var logger = new WatchLogger();
        using var service = Service(store, logger);
        using (await service.BuildWatchersAsync(CancellationToken.None)) Assert.Single(logger.Warnings);
        Directory.Move(path, path + "-moved");
    }

    [Fact]
    public async Task Symbolic_link_root_is_refused_and_target_is_not_pinned()
    {
        using var fixture = new CloudSourceFixture();
        var target = fixture.Directory("target");
        var link = Path.Combine(fixture.Root, "link");
        Directory.CreateSymbolicLink(link, target);
        var store = new WatchStore(SourceRootConfiguration.Create(link, "Unsafe fixture", true, false, 1024));
        var logger = new WatchLogger();
        using var service = Service(store, logger);
        using (await service.BuildWatchersAsync(CancellationToken.None)) Assert.Single(logger.Warnings);
        Directory.Move(target, target + "-moved");
        Directory.Delete(link);
    }

    [Fact]
    public async Task Git_root_keeps_its_strict_cloud_reparse_refusal()
    {
        using var fixture = new CloudSourceFixture();
        var path = fixture.Directory("documents");
        fixture.MakeCloudDirectory(path);
        var store = new WatchStore(Admitted(path, mode: SourceDiscoveryMode.GitTracked));
        var logger = new WatchLogger();
        using var service = Service(store, logger);
        using (await service.BuildWatchersAsync(CancellationToken.None)) Assert.Single(logger.Warnings);
        Directory.Move(path, path + "-moved");
    }

    [Fact]
    public async Task Failed_Git_control_validation_releases_the_already_acquired_root_lease()
    {
        using var fixture = new CloudSourceFixture();
        var path = fixture.Directory("not-a-repository");
        var store = new WatchStore(Admitted(path, mode: SourceDiscoveryMode.GitTracked));
        var logger = new WatchLogger();
        using var service = Service(store, logger);
        using (await service.BuildWatchersAsync(CancellationToken.None))
        {
            Assert.Single(logger.Warnings);
            Directory.Move(path, path + "-moved");
        }
    }

    [Fact]
    public async Task Cancellation_after_an_earlier_root_registered_releases_its_pins()
    {
        using var fixture = new CloudSourceFixture();
        var path = fixture.Directory("documents");
        using var cancellation = new CancellationTokenSource();
        var roots = new CancellingRoots(Admitted(path), () =>
        {
            // This callback runs between registrations, after ConfigureRoots' inventory pass.
            Assert.Throws<IOException>(() => Directory.Move(path, path + "-moved"));
            cancellation.Cancel();
        });
        var store = new WatchStore(roots);
        var logger = new WatchLogger();
        using var service = Service(store, logger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.BuildWatchersAsync(cancellation.Token));
        Assert.Empty(logger.Warnings);
        Directory.Move(path, path + "-moved");
    }

    private static SourceRootConfiguration Admitted(string path, SourceDiscoveryMode mode = SourceDiscoveryMode.Filesystem) =>
        Admitted(path, PhysicalFileIdentity.GetDirectory(path).IdentityFingerprint, true, mode);

    private static SourceRootConfiguration Admitted(string path, string? fingerprint, bool required,
        SourceDiscoveryMode mode = SourceDiscoveryMode.Filesystem) =>
        SourceRootConfiguration.Restore(SourceRootId.New(), path, "Private watcher fixture", true, false, 1024,
            [], [], null, TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1,
            physicalIdentityFingerprint: fingerprint, requiresPhysicalIdentityValidation: required, discoveryMode: mode);

    private static LocalSourceRootWatchHostedService Service(WatchStore store, WatchLogger logger) =>
        new(store, new SourceWatchCoordinator(store), TimeProvider.System, logger);

    private sealed class WatchStore(IReadOnlyList<SourceRootConfiguration> roots) : ISourceRootWatchStore
    {
        public WatchStore(SourceRootConfiguration root) : this(new[] { root }) { }
        public TaskCompletionSource ReadRoots { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SourceWatchSignal> Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyList<SourceRootConfiguration>> ReadEnabledRootsAsync(CancellationToken ct)
        {
            ReadRoots.TrySetResult();
            return ValueTask.FromResult<IReadOnlyList<SourceRootConfiguration>>(roots);
        }
        public ValueTask RecordSignalAsync(SourceWatchSignal signal, CancellationToken ct)
        {
            Signal.TrySetResult(signal);
            return ValueTask.CompletedTask;
        }
        public ValueTask<ClaimedSourceWatchBatch?> ClaimDueBatchAsync(DateTimeOffset now, string owner, TimeSpan duration, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask ReleaseScanAsync(ClaimedSourceWatchBatch batch, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CancellingRoots(SourceRootConfiguration root, Action afterFirstRegistration) : IReadOnlyList<SourceRootConfiguration>
    {
        private int _enumerations;
        public int Count => 2;
        public SourceRootConfiguration this[int index] => index is 0 or 1 ? root : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<SourceRootConfiguration> GetEnumerator()
        {
            var registrationPass = Interlocked.Increment(ref _enumerations) > 1;
            yield return root;
            if (registrationPass) afterFirstRegistration();
            yield return root;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class WatchLogger : ILogger<LocalSourceRootWatchHostedService>
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Warning) Warnings.Enqueue(formatter(state, exception));
        }
    }
}
