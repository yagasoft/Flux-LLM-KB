using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class LocalSourceRootWatchHostedServiceTests
{
    [Theory]
    [InlineData(".agents/receipt.json", false)]
    [InlineData("docs/.AgEnTs/receipt.json", false)]
    [InlineData("artifacts/output.md", false)]
    [InlineData("src/obj/generated.cs", false)]
    [InlineData("private", false)]
    [InlineData("models/config.json", false)]
    [InlineData("src/Models/Record.cs", true)]
    [InlineData("docs", true)]
    [InlineData("docs/manual.md", true)]
    [InlineData("appsettings.Local.json", true)]
    [InlineData("unsupported.exe", true)]
    [InlineData(".git/index", true)]
    [InlineData(".git/worktrees/linked/index", true)]
    [InlineData("../common/.git/index", true)]
    [InlineData(".", true)]
    public void Git_file_hints_suppress_only_provably_excluded_trees(string relative, bool expected)
    {
        var root = SourceRootConfiguration.Create(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FluxWatchPolicy")),
            "Synthetic Git watcher policy", true, false, 1024, discoveryMode: SourceDiscoveryMode.GitTracked);
        foreach (var kind in new[] { WatcherChangeTypes.Created, WatcherChangeTypes.Changed, WatcherChangeTypes.Deleted })
            Assert.Equal(expected, LocalSourceRootWatchHostedService.ShouldSignal(root, new FileSystemEventArgs(kind, root.CanonicalPath, relative)));
    }

    [Theory]
    [InlineData(".agents/old.md", ".agents/new.md", false)]
    [InlineData("private/docs", "artifacts/docs", false)]
    [InlineData("docs/fact.md", ".agents/fact.md", true)]
    [InlineData(".agents/fact.md", "docs/fact.md", true)]
    [InlineData("docs", "private/docs", true)]
    [InlineData("private/docs", "docs", true)]
    [InlineData("docs", "renamed", true)]
    [InlineData(".git/index.lock", ".git/index", true)]
    public void Git_rename_hints_consider_both_endpoints_and_eligible_descendants(string oldPath, string newPath, bool expected)
    {
        var root = SourceRootConfiguration.Create(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FluxWatchPolicy")),
            "Synthetic Git watcher policy", true, false, 1024, discoveryMode: SourceDiscoveryMode.GitTracked);
        Assert.Equal(expected, LocalSourceRootWatchHostedService.ShouldSignal(root,
            new RenamedEventArgs(WatcherChangeTypes.Renamed, root.CanonicalPath, newPath, oldPath)));
    }

    [Fact]
    public void Uncertain_paths_and_ordinary_filesystem_roots_keep_their_hints()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FluxWatchPolicy"));
        var git = SourceRootConfiguration.Create(path, "Git watcher", true, false, 1024, discoveryMode: SourceDiscoveryMode.GitTracked);
        Assert.True(LocalSourceRootWatchHostedService.ShouldSignal(git, new FileSystemEventArgs(WatcherChangeTypes.Deleted, "relative", ".agents/file.md")));
        Assert.True(LocalSourceRootWatchHostedService.ShouldSignal(git, new FileSystemEventArgs(WatcherChangeTypes.Deleted, path, "invalid\0/.agents/file.md")));
        var filesystem = SourceRootConfiguration.Create(path, "Ordinary watcher", true, false, 1024);
        Assert.True(LocalSourceRootWatchHostedService.ShouldSignal(filesystem, new FileSystemEventArgs(WatcherChangeTypes.Changed, path, ".agents/file.md")));
        Assert.True(LocalSourceRootWatchHostedService.ShouldSignal(filesystem, new RenamedEventArgs(WatcherChangeTypes.Renamed, path, "private/new.md", "private/old.md")));
    }

    [Theory]
    [InlineData("private/git-control/worktrees/linked/index", true)]
    [InlineData("private/git-control/objects/object", true)]
    [InlineData("private/git-control", true)]
    [InlineData("private", true)]
    [InlineData("private/unrelated.md", false)]
    [InlineData("private/git-control-other/index", false)]
    public void Validated_Git_control_trees_and_their_ancestors_remain_hints(string relative, bool expected)
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FluxWatchPolicy"));
        var root = SourceRootConfiguration.Create(path, "Git control watcher", true, false, 1024, discoveryMode: SourceDiscoveryMode.GitTracked);
        var control = Path.Combine(path, "private", "git-control");
        var repository = new GitTrackedSourceDiscovery.Repository(Path.Combine(control, "worktrees", "linked"), control, "synthetic");
        Assert.Equal(expected, LocalSourceRootWatchHostedService.ShouldSignal(root,
            new FileSystemEventArgs(WatcherChangeTypes.Deleted, path, relative), repository));
    }

    [Fact]
    public async Task Overflow_and_changes_during_persistence_survive_as_one_follow_up_rescan_hint()
    {
        var root = SourceRootId.New();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new List<SourceWatchSignal>();
        var store = new SignalStore(async (signal, ct) =>
        {
            recorded.Add(signal);
            if (recorded.Count == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
        });
        var buffer = new LocalSourceWatchSignalBuffer(new SourceWatchCoordinator(store), NullLogger.Instance);
        buffer.ConfigureRoots([root]);
        for (var i = 0; i < 1000; i++) buffer.Signal(new(root, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch));
        var flushing = buffer.FlushAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        buffer.Signal(new(root, SourceWatchSignalKind.Overflow, DateTimeOffset.UnixEpoch.AddSeconds(1)));
        buffer.Signal(new(root, SourceWatchSignalKind.Renamed, DateTimeOffset.UnixEpoch.AddSeconds(2)));
        release.TrySetResult();
        Assert.True(await flushing);
        Assert.Single(recorded);
        Assert.True(await buffer.FlushAsync(CancellationToken.None));
        Assert.Equal(2, recorded.Count);
        Assert.Equal(SourceWatchSignalKind.Overflow, recorded[1].Kind);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2), recorded[1].ObservedAtUtc);
        Assert.True(await buffer.FlushAsync(CancellationToken.None));
        Assert.Equal(2, recorded.Count);
    }

    [Fact]
    public async Task Failed_or_ambiguous_persistence_retries_a_rescan_and_drops_removed_roots()
    {
        var root = SourceRootId.New();
        var removed = SourceRootId.New();
        var attempts = 0;
        var recorded = new List<SourceWatchSignal>();
        var store = new SignalStore((signal, _) =>
        {
            if (++attempts == 1) throw new IOException("synthetic SQL persistence unavailable");
            recorded.Add(signal);
            return ValueTask.CompletedTask;
        });
        var buffer = new LocalSourceWatchSignalBuffer(new SourceWatchCoordinator(store), NullLogger.Instance);
        buffer.ConfigureRoots([root, removed]);
        buffer.Signal(new(root, SourceWatchSignalKind.Created, DateTimeOffset.UnixEpoch));
        buffer.Signal(new(removed, SourceWatchSignalKind.Deleted, DateTimeOffset.UnixEpoch));
        buffer.Signal(new(SourceRootId.New(), SourceWatchSignalKind.Overflow, DateTimeOffset.UnixEpoch));
        Assert.False(await buffer.FlushAsync(CancellationToken.None));
        buffer.ConfigureRoots([root]);
        buffer.Signal(new(root, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch.AddSeconds(1)));
        Assert.True(await buffer.FlushAsync(CancellationToken.None));
        var rescan = Assert.Single(recorded);
        Assert.Equal(root, rescan.RootId);
        Assert.Equal(SourceWatchSignalKind.Overflow, rescan.Kind);
    }

    [Fact]
    public async Task File_event_burst_has_one_outstanding_persistence_and_shutdown_cancels_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FluxWatchBurst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var root = SourceRootConfiguration.Create(directory, "Disposable watcher", true, false, 1024);
        var store = new BlockedStore(root);
        using var service = new LocalSourceRootWatchHostedService(store, new SourceWatchCoordinator(store),
            TimeProvider.System, NullLogger<LocalSourceRootWatchHostedService>.Instance);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await store.ReadRoots.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var attempt = 0; attempt < 50 && !store.Entered.Task.IsCompleted; attempt++)
            {
                File.WriteAllText(Path.Combine(directory, "first.md"), attempt.ToString());
                await Task.Delay(100);
            }
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 200; i++) File.WriteAllText(Path.Combine(directory, $"file{i}.md"), "change");
            await Task.Delay(200);
            Assert.Equal(1, store.Attempts);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(deadline.Token);
            Assert.True(store.Cancelled.Task.IsCompleted);
        }
        finally
        {
            store.Release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
            Directory.Delete(directory, true);
        }
    }

    private sealed class BlockedStore(SourceRootConfiguration root) : ISourceRootWatchStore
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public TaskCompletionSource ReadRoots { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyList<SourceRootConfiguration>> ReadEnabledRootsAsync(CancellationToken ct)
        {
            ReadRoots.TrySetResult();
            return ValueTask.FromResult<IReadOnlyList<SourceRootConfiguration>>([root]);
        }
        public async ValueTask RecordSignalAsync(SourceWatchSignal signal, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { Cancelled.TrySetResult(); throw; }
        }
        public ValueTask<ClaimedSourceWatchBatch?> ClaimDueBatchAsync(DateTimeOffset now, string owner, TimeSpan duration, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask ReleaseScanAsync(ClaimedSourceWatchBatch batch, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class SignalStore(Func<SourceWatchSignal, CancellationToken, ValueTask> record) : ISourceRootWatchStore
    {
        public ValueTask RecordSignalAsync(SourceWatchSignal signal, CancellationToken ct) => record(signal, ct);
        public ValueTask<IReadOnlyList<SourceRootConfiguration>> ReadEnabledRootsAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<ClaimedSourceWatchBatch?> ClaimDueBatchAsync(DateTimeOffset now, string owner, TimeSpan duration, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask ReleaseScanAsync(ClaimedSourceWatchBatch batch, CancellationToken ct) => throw new NotSupportedException();
    }
}
