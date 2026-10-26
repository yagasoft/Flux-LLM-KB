using System.Diagnostics;
using System.Runtime.Versioning;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

[SupportedOSPlatform("windows")]
public sealed partial class SourceVerificationOplockNativeTests
{
    [Fact]
    public async Task Granted_request_stays_pending_and_local_retirement_releases_all_resources()
    {
        using var fixture = new Fixture();
        await using var lease = fixture.Acquire();
        Assert.True(lease.IsUnbroken());
        Assert.False(lease.Completion.IsCompleted);
        using (var finalGuard = WindowsSourcePathAccess.OpenRead(fixture.Root, fixture.Path))
            Assert.True(lease.TryAttest());
        Assert.True(await lease.RetireAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(lease.Completion.IsCompletedSuccessfully);
        Assert.False(lease.IsUnbroken());
        using var exclusive = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task External_write_with_restored_timestamp_invalidates_without_verifier_progress()
    {
        using var fixture = new Fixture();
        using var persistent = File.Open(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using var lease = fixture.Acquire();
        var stamp = File.GetLastWriteTimeUtc(fixture.Path);
        var watch = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            File.WriteAllText(fixture.Path, new string('z', 64));
            File.SetLastWriteTimeUtc(fixture.Path, stamp);
        }).WaitAsync(TimeSpan.FromSeconds(2));
        await lease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(lease.IsUnbroken());
        Assert.False(await lease.RetireAsync());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(fixture.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_does_not_wait_for_the_stalled_verifier(bool ancestor)
    {
        using var fixture = new Fixture();
        await using var lease = fixture.Acquire();
        var from = ancestor ? fixture.Directory : fixture.Path;
        var to = from + "-moved";
        try
        {
            await Task.Run(() =>
            {
                if (ancestor) System.IO.Directory.Move(from, to);
                else File.Move(from, to);
            }).WaitAsync(TimeSpan.FromSeconds(2));
            await lease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(lease.IsUnbroken());
            Assert.False(await lease.RetireAsync());
        }
        finally
        {
            // Retire the owned native request before restoring the fixture namespace.
            await lease.DisposeAsync();
            if (ancestor && System.IO.Directory.Exists(to)) System.IO.Directory.Move(to, from);
            if (!ancestor && File.Exists(to)) File.Move(to, from);
        }
    }

    [Fact]
    public async Task Cancellation_after_grant_retires_the_exact_request_before_disposal()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await using var lease = fixture.Acquire(cancellation.Token);
        Assert.True(lease.IsUnbroken());
        cancellation.Cancel();
        await lease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(lease.IsUnbroken());
        Assert.False(await lease.RetireAsync());
        using var exclusive = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Cancellation_before_grant_opens_no_native_request()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Acquire(cancellation.Token));
        using var exclusive = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Base = @"I:\Codex Workspaces\Staging\FluxKnowledge";
        private readonly string _root = System.IO.Path.Combine(Base, "VerificationOplock-" + Guid.NewGuid().ToString("N"));
        internal string Root => _root;
        internal string Directory => System.IO.Path.Combine(_root, "ancestor");
        internal string Path => System.IO.Path.Combine(Directory, "file.txt");
        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path, new string('a', 64));
        }
        internal WindowsSourceVerificationOplock Acquire(CancellationToken cancellationToken = default,
            Action<WindowsSourceVerificationOplock>? configureProbe = null) =>
            WindowsSourceVerificationOplock.Acquire(_root, Path,
                PhysicalFileIdentity.GetDirectory(_root).IdentityFingerprint, PhysicalFileIdentity.Get(Path), cancellationToken, configureProbe);

        public void Dispose()
        {
            if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_root)) != Base ||
                !System.IO.Path.GetFileName(_root).StartsWith("VerificationOplock-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its owned staging directory.");
            System.IO.Directory.Delete(_root, true);
        }
    }
}
