using System.Diagnostics;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using Xunit;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxKnowledge.Domain.Tests.Sources;

[Collection("Git environment")]
public sealed class GitTrackedSourceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"FluxGitSources_{Guid.NewGuid():N}");

    public GitTrackedSourceTests()
    {
        Directory.CreateDirectory(directory);
        Git("init", "--quiet");
    }

    [Fact]
    public async Task Discovers_more_than_512_tracked_files_without_retaining_untracked_or_excluded_files()
    {
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        for (var i = 0; i < 600; i++) File.WriteAllText(Path.Combine(directory, "src", $"item{i}.md"), $"Repository fact {i}");
        Git("add", "src");
        File.WriteAllText(Path.Combine(directory, "untracked.md"), "Private untracked sentinel");
        Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
        File.WriteAllText(Path.Combine(directory, "artifacts", "output.md"), "Excluded tracked output");
        Git("add", "artifacts");
        foreach (var excluded in new[] { "data", "runtime", "transcripts", "attachments", "FluxKnowledgeData", "FluxKnowledgeIndexes", "build", "dist", "models" })
        {
            Directory.CreateDirectory(Path.Combine(directory, excluded));
            File.WriteAllText(Path.Combine(directory, excluded, "sentinel.md"), "Private tracked sentinel");
            Git("add", excluded);
        }
        File.WriteAllText(Path.Combine(directory, "appsettings.Local.json"), "{}");
        Git("add", "appsettings.Local.json");
        Directory.CreateDirectory(Path.Combine(directory, "src", "Models"));
        File.WriteAllText(Path.Combine(directory, "src", "Models", "ILocalModelStore.cs"), "public interface ILocalModelStore { }");
        Git("add", "src/Models");
        var files = await DiscoverAsync();
        Assert.Equal(601, files.Count);
        Assert.Contains(files, file => file.RelativePath.EndsWith("ILocalModelStore.cs", StringComparison.Ordinal));
        Assert.All(files, file => Assert.StartsWith("src", file.RelativePath));
    }

    [Fact]
    public async Task Addition_edit_rename_deletion_and_untracking_change_discovery_without_root_reconfiguration()
    {
        File.WriteAllText(Path.Combine(directory, "first.md"), "Initial fact");
        Git("add", "first.md");
        Assert.Equal("Initial fact", Assert.Single(await DiscoverAsync()).Classification.Text);
        File.WriteAllText(Path.Combine(directory, "first.md"), "Changed fact");
        Assert.Equal("Changed fact", Assert.Single(await DiscoverAsync()).Classification.Text);
        Git("mv", "first.md", "second.md");
        Assert.Equal("second.md", Assert.Single(await DiscoverAsync()).RelativePath);
        File.Delete(Path.Combine(directory, "second.md"));
        Assert.Empty(await DiscoverAsync());
        File.WriteAllText(Path.Combine(directory, "second.md"), "Restored fact");
        Git("add", "second.md");
        Git("rm", "--cached", "second.md");
        Assert.Empty(await DiscoverAsync());
    }

    [Fact]
    public async Task Excluded_diagnostic_file_activity_does_not_queue_repository_rescans()
    {
        var tracked = Path.Combine(directory, "safe.md");
        var excluded = Path.Combine(directory, ".agents");
        File.WriteAllText(tracked, "Tracked fact");
        Directory.CreateDirectory(excluded);
        Git("add", "safe.md");
        var root = SourceRootConfiguration.Create(directory, "Disposable Git watcher", true, false, 1024,
            discoveryMode: SourceDiscoveryMode.GitTracked);
        var store = new WatchStore(root);
        using var service = new LocalSourceRootWatchHostedService(store, new SourceWatchCoordinator(store),
            TimeProvider.System, NullLogger<LocalSourceRootWatchHostedService>.Instance);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await PrimeWatcherAsync(store, tracked);

            for (var index = 0; index < 50; index++)
            {
                var path = Path.Combine(excluded, $"diagnostic{index}.json");
                File.WriteAllText(path, "Synthetic diagnostic receipt");
                File.AppendAllText(path, " updated");
                var renamed = path + ".renamed";
                File.Move(path, renamed);
                File.Delete(renamed);
            }
            await Task.Delay(3000);
            Assert.False(store.Signals.Reader.TryRead(out _), "Excluded diagnostic writes queued a full repository rescan.");

            File.AppendAllText(tracked, " current");
            using var trackedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(root.Id, (await store.Signals.Reader.ReadAsync(trackedDeadline.Token)).RootId);
        }
        finally
        {
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stopDeadline.Token);
        }
    }

    [Theory]
    [InlineData("docs", "private/docs")]
    [InlineData("private/docs", "docs")]
    [InlineData("docs", "renamed")]
    public async Task Directory_moves_with_eligible_descendants_queue_authoritative_rescans(string oldPath, string newPath)
    {
        var tracked = Path.Combine(directory, "ready.md");
        File.WriteAllText(tracked, "Tracked readiness fact");
        var original = Path.Combine(directory, oldPath);
        var target = Path.Combine(directory, newPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "fact.md"), "Tracked directory fact");
        Git("add", ".");
        var root = SourceRootConfiguration.Create(directory, "Disposable Git directory watcher", true, false, 1024,
            discoveryMode: SourceDiscoveryMode.GitTracked);
        var store = new WatchStore(root);
        using var service = new LocalSourceRootWatchHostedService(store, new SourceWatchCoordinator(store),
            TimeProvider.System, NullLogger<LocalSourceRootWatchHostedService>.Instance);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await PrimeWatcherAsync(store, tracked);
            Directory.Move(original, target);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(root.Id, (await store.Signals.Reader.ReadAsync(deadline.Token)).RootId);
        }
        finally
        {
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stopDeadline.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Ordinary_and_linked_Git_index_changes_queue_authoritative_rescans(bool linked) =>
        VerifyGitIndexWakeAsync(linked, controlInsideExcludedTree: false);

    [Fact]
    public Task Git_control_directory_under_excluded_tree_still_queues_index_rescans() =>
        VerifyGitIndexWakeAsync(linked: true, controlInsideExcludedTree: true);

    private async Task VerifyGitIndexWakeAsync(bool linked, bool controlInsideExcludedTree)
    {
        File.WriteAllText(Path.Combine(directory, "safe.md"), "Tracked fact");
        Git("add", ".");
        Git("-c", "user.name=Disposable test", "-c", "user.email=test@example.invalid", "commit", "--quiet", "-m", "Disposable watcher fixture");
        var watched = directory;
        if (linked)
        {
            watched = Path.Combine(Path.GetTempPath(), $"FluxGitWatchLinked_{Guid.NewGuid():N}");
            Git("worktree", "add", "--detach", watched, "HEAD");
        }
        var originalControl = Path.Combine(directory, ".git");
        var relocatedControl = Path.Combine(watched, "private", "git-control");
        string? originalPointer = null;
        var controlRelocated = false;
        LocalSourceRootWatchHostedService? service = null;
        try
        {
            if (controlInsideExcludedTree)
            {
                var originalRepository = GitTrackedSourceDiscovery.ResolveRepository(watched);
                var relativeGitDirectory = Path.GetRelativePath(originalControl, originalRepository.GitDirectory);
                originalPointer = File.ReadAllText(Path.Combine(watched, ".git"));
                Directory.CreateDirectory(Path.GetDirectoryName(relocatedControl)!);
                Directory.Move(originalControl, relocatedControl);
                controlRelocated = true;
                WriteGitPointer(Path.Combine(watched, ".git"), $"gitdir: {Path.Combine(relocatedControl, relativeGitDirectory)}\n");
                Assert.Equal(relocatedControl, GitTrackedSourceDiscovery.ResolveRepository(watched).CommonDirectory);
                Assert.Equal(new[] { watched }, LocalSourceRootWatchHostedService.GetWatchPaths(
                    SourceRootConfiguration.Create(watched, "In-root control", true, false, 1024, discoveryMode: SourceDiscoveryMode.GitTracked)));
                Assert.Equal("safe.md", Assert.Single((await new GitTrackedSourceDiscovery().ReadAsync(watched, CancellationToken.None)).Paths));
            }
            var root = SourceRootConfiguration.Create(watched, "Disposable Git index watcher", true, false, 1024,
                discoveryMode: SourceDiscoveryMode.GitTracked);
            var store = new WatchStore(root);
            service = new LocalSourceRootWatchHostedService(store, new SourceWatchCoordinator(store),
                TimeProvider.System, NullLogger<LocalSourceRootWatchHostedService>.Instance);
            await service.StartAsync(CancellationToken.None);
            var tracked = Path.Combine(watched, "safe.md");
            await PrimeWatcherAsync(store, tracked);
            GitAt(watched, "rm", "--cached", "safe.md");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(root.Id, (await store.Signals.Reader.ReadAsync(deadline.Token)).RootId);
            Assert.True(File.Exists(tracked));
            Assert.Empty((await new GitTrackedSourceDiscovery().ReadAsync(watched, deadline.Token)).Paths);
        }
        finally
        {
            if (service is not null)
            {
                using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await service.StopAsync(stopDeadline.Token);
                service.Dispose();
            }
            if (controlRelocated)
            {
                Directory.Move(relocatedControl, originalControl);
                WriteGitPointer(Path.Combine(watched, ".git"), originalPointer!);
            }
            if (linked && Directory.Exists(watched))
            {
                File.WriteAllText(Path.Combine(watched, "safe.md"), "Tracked fact");
                GitAt(watched, "add", "safe.md");
                Git("worktree", "remove", watched);
            }
        }
    }

    private async Task<List<FluxKnowledge.Application.Ports.SourceDiscoveredFile>> DiscoverAsync()
    {
        var enumerator = new LocalSourceEnumerator();
        var root = SourceRootConfiguration.Create(directory, "Disposable Git", true, false, 16 * 1024 * 1024,
            discoveryMode: SourceDiscoveryMode.GitTracked);
        var files = new List<FluxKnowledge.Application.Ports.SourceDiscoveredFile>();
        await foreach (var file in enumerator.EnumerateAsync(root, CancellationToken.None)) files.Add(file);
        Assert.Empty(enumerator.LastEvidence);
        return files;
    }

    [Fact]
    public async Task Repository_helper_is_never_run_and_inherited_index_override_is_ignored()
    {
        File.WriteAllText(Path.Combine(directory, "safe.md"), "Tracked fact");
        Git("add", "safe.md");
        File.WriteAllText(Path.Combine(directory, "helper.sh"), "#!/bin/sh\ntouch sentinel\n");
        Git("config", "core.fsmonitor", "./helper.sh");
        var previous = Environment.GetEnvironmentVariable("GIT_INDEX_FILE");
        try
        {
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", Path.Combine(directory, "absent-index"));
            Assert.Single(await DiscoverAsync());
            Assert.False(File.Exists(Path.Combine(directory, "sentinel")));
        }
        finally { Environment.SetEnvironmentVariable("GIT_INDEX_FILE", previous); }
    }

    [Fact]
    public async Task Resource_pressure_refuses_inventory_and_mutation_invalidates_completed_inventory()
    {
        File.WriteAllText(Path.Combine(directory, "safe.md"), "Tracked fact"); Git("add", "safe.md");
        await Assert.ThrowsAsync<IOException>(() => new GitTrackedSourceDiscovery(() => 0).ReadAsync(directory, default).AsTask());
        var enumerator = new LocalSourceEnumerator();
        var root = SourceRootConfiguration.Create(directory, "git", true, false, 16 * 1024 * 1024, discoveryMode: SourceDiscoveryMode.GitTracked);
        await foreach (var _ in enumerator.EnumerateAsync(root, default)) { }
        Assert.NotNull(enumerator.LastInventory);
        File.WriteAllText(Path.Combine(directory, "added.md"), "Another fact"); Git("add", "added.md");
        Assert.False(await enumerator.ValidateInventoryAsync(root, default));
    }

    [Fact]
    public async Task Unreadable_tracked_file_leaves_inventory_incomplete()
    {
        var path = Path.Combine(directory, "safe.md"); File.WriteAllText(path, "Tracked fact"); Git("add", "safe.md");
        // An exclusive open is a repeatable access failure under the actual Windows file API.
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var enumerator = new LocalSourceEnumerator();
        var root = SourceRootConfiguration.Create(directory, "git", true, false, 16 * 1024 * 1024, discoveryMode: SourceDiscoveryMode.GitTracked);
        await foreach (var _ in enumerator.EnumerateAsync(root, default)) { }
        Assert.NotEmpty(enumerator.LastEvidence);
        Assert.Null(enumerator.LastInventory);
    }

    [Fact]
    public async Task Failed_Git_inventory_never_becomes_an_authoritative_empty_scan()
    {
        File.WriteAllText(Path.Combine(directory, "safe.md"), "Tracked fact"); Git("add", "safe.md");
        File.WriteAllText(Path.Combine(directory, ".git", "index"), "Corrupted disposable index");
        var enumerator = new LocalSourceEnumerator();
        var root = SourceRootConfiguration.Create(directory, "git", true, false, 1024, discoveryMode: SourceDiscoveryMode.GitTracked);
        await foreach (var _ in enumerator.EnumerateAsync(root, default)) Assert.Fail("Incomplete membership must produce no files.");
        Assert.NotEmpty(enumerator.LastEvidence); Assert.Null(enumerator.LastInventory);
    }

    [Fact]
    public async Task Permission_denied_tracked_directory_is_never_confirmed_absence()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows ACL verification requires Windows.");
        var parent = Path.Combine(directory, "locked"); Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "safe.md"), "Tracked fact"); Git("add", ".");
        var info = new DirectoryInfo(parent); var original = info.GetAccessControl();
        var denied = info.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
        try
        {
            info.SetAccessControl(denied);
            var enumerator = new LocalSourceEnumerator();
            var root = SourceRootConfiguration.Create(directory, "git", true, false, 16 * 1024 * 1024, discoveryMode: SourceDiscoveryMode.GitTracked);
            await foreach (var _ in enumerator.EnumerateAsync(root, default)) { }
            Assert.NotEmpty(enumerator.LastEvidence); Assert.Null(enumerator.LastInventory);
        }
        finally
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            info.SetAccessControl(restored);
        }
    }

    [Fact]
    public async Task Linked_worktree_inventory_validates_identity_and_watches_common_control_directory()
    {
        File.WriteAllText(Path.Combine(directory, "safe.md"), "Tracked fact"); Git("add", ".");
        Git("-c", "user.name=Disposable test", "-c", "user.email=test@example.invalid", "commit", "--quiet", "-m", "Disposable fixture");
        var linked = Path.Combine(Path.GetTempPath(), $"FluxGitLinked_{Guid.NewGuid():N}");
        try
        {
            Git("worktree", "add", "--detach", linked, "HEAD");
            var inventory = await new GitTrackedSourceDiscovery().ReadAsync(linked, default);
            Assert.Equal("safe.md", Assert.Single(inventory.Paths));
            var root = SourceRootConfiguration.Create(linked, "linked", true, false, 16 * 1024 * 1024, discoveryMode: SourceDiscoveryMode.GitTracked);
            Assert.Equal(new[] { linked, Path.Combine(directory, ".git") }, LocalSourceRootWatchHostedService.GetWatchPaths(root));
        }
        finally { if (Directory.Exists(linked)) Git("worktree", "remove", linked); }
    }

    private void Git(params string[] arguments) => GitAt(directory, arguments);

    private static void WriteGitPointer(string path, string text)
    {
        // Open the existing hidden Git pointer without replacing its file attributes.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(0);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }

    private static void GitAt(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(@"C:\Program Files\Git\cmd\git.exe") { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private sealed class WatchStore(SourceRootConfiguration root) : ISourceRootWatchStore
    {
        public Channel<SourceWatchSignal> Signals { get; } = Channel.CreateUnbounded<SourceWatchSignal>();
        public ValueTask<IReadOnlyList<SourceRootConfiguration>> ReadEnabledRootsAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<SourceRootConfiguration>>([root]);
        public ValueTask RecordSignalAsync(SourceWatchSignal signal, CancellationToken ct) =>
            Signals.Writer.WriteAsync(signal, ct);
        public ValueTask<ClaimedSourceWatchBatch?> ClaimDueBatchAsync(DateTimeOffset now, string owner, TimeSpan duration, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask ReleaseScanAsync(ClaimedSourceWatchBatch batch, CancellationToken ct) => throw new NotSupportedException();
    }

    private static async Task PrimeWatcherAsync(WatchStore store, string tracked)
    {
        // A real tracked event proves that the asynchronous watcher has opened.
        for (var attempt = 0; attempt < 50 && !store.Signals.Reader.TryPeek(out _); attempt++)
        {
            File.AppendAllText(tracked, " change");
            await Task.Delay(100);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.NotEqual(SourceWatchSignalKind.Overflow, (await store.Signals.Reader.ReadAsync(deadline.Token)).Kind);
        await Task.Delay(2500);
        while (store.Signals.Reader.TryRead(out _)) { }
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, true);
    }
}

[CollectionDefinition("Git environment", DisableParallelization = true)]
public sealed class GitEnvironmentCollection;
