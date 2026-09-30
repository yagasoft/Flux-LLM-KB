using System.Diagnostics;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using Xunit;
using System.Security.AccessControl;
using System.Security.Principal;

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

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo(@"C:\Program Files\Git\cmd\git.exe") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, true);
    }
}

[CollectionDefinition("Git environment", DisableParallelization = true)]
public sealed class GitEnvironmentCollection;
