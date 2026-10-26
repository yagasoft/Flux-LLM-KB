using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using Xunit;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceMetadataPacingNativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_child_directory_handle_can_block_ancestor_rename_despite_delete_sharing(bool enumerate)
    {
        using var area = new Area(2);
        using (var cursor = WindowsSourcePathAccess.OpenDirectoryPath(area.Root, enumerate: enumerate, allowNamespaceChanges: true))
            Assert.Throws<IOException>(() => Directory.Move(area.Parent, area.Parent + "-moved"));
        Directory.Move(area.Parent, area.Parent + "-moved");
    }

    [Theory]
    [InlineData(2)] // The terminal page must be guarded too.
    [InlineData(80)] // Multiple native pages retain one cursor.
    public async Task A_metadata_pause_allows_child_writes_and_compatible_ancestor_moves_then_rejects_the_old_path(int files)
    {
        using var area = new Area(files);
        var clock = new Clock();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        long sourceBytes = 0;
        var enumerator = new LocalSourceEnumerator(metadataTimeProvider: clock)
        {
            SourceBytesRead = bytes => sourceBytes += bytes,
            BeforeMetadataDelay = async ct =>
            {
                if (first) { first = false; paused.SetResult(); await resume.Task.WaitAsync(ct); }
                await clock.AdvanceAsync();
            }
        };
        using var scope = enumerator.UseScope(new(["."], (_, _) => ValueTask.FromResult(false)) { MetadataEntriesPerSecond = 1 });
        var scan = DrainAsync(enumerator, area.Root);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var latency = Stopwatch.StartNew();
        File.AppendAllText(Path.Combine(area.Root, "file-000.txt"), "changed while paused");
        Directory.Move(area.Parent, area.Parent + "-moved");
        Assert.True(latency.Elapsed < TimeSpan.FromSeconds(3), "A restrictive root or ancestor handle survived the metadata pause.");
        resume.SetResult();
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, sourceBytes);
        Assert.Contains(enumerator.LastEvidence, issue => issue.NamespaceIncomplete);
    }

    [Fact]
    public async Task Cancellation_during_a_metadata_pause_disposes_cursor_and_namespace_handles()
    {
        using var area = new Area(3);
        using (var control = WindowsSourcePathAccess.OpenDirectoryPath(area.Root, enumerate: true, allowNamespaceChanges: true))
        using (var blocked = OpenExclusiveDirectory(area.Root, 0x1, 0, IntPtr.Zero, 3, 0x02300000, IntPtr.Zero))
        {
            var error = Marshal.GetLastPInvokeError();
            Assert.True(blocked.IsInvalid, "The control must conflict with a live list cursor.");
            Assert.Equal(32, error);
        }
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enumerator = new LocalSourceEnumerator(metadataTimeProvider: clock)
        {
            BeforeMetadataDelay = async ct => { paused.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        };
        using var scope = enumerator.UseScope(new(["."], (_, _) => ValueTask.FromResult(false)) { MetadataEntriesPerSecond = 1 });
        var scan = DrainAsync(enumerator, area.Root, cancellation.Token);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var pausedHandle = OpenExclusiveDirectory(area.Root, 0x1, 0, IntPtr.Zero, 3, 0x02300000, IntPtr.Zero))
            Assert.False(pausedHandle.IsInvalid, "A metadata pause retained a list cursor.");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        using (var released = OpenExclusiveDirectory(area.Root, 0x1, 0, IntPtr.Zero, 3, 0x02300000, IntPtr.Zero))
            Assert.False(released.IsInvalid, "Cancellation leaked an observational list cursor.");
        Directory.Move(area.Parent, area.Parent + "-moved");
    }

    [Fact]
    public async Task A_paced_multi_page_pass_visits_each_materialised_file_once_without_content_reads()
    {
        using var area = new Area(100);
        var clock = new Clock();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long sourceBytes = 0;
        var enumerator = new LocalSourceEnumerator(metadataTimeProvider: clock)
        {
            SourceBytesRead = bytes => sourceBytes += bytes,
            BeforeMetadataDelay = _ => clock.AdvanceAsync()
        };
        using var scope = enumerator.UseScope(new(["."], (relative, _) =>
        {
            Assert.True(visited.Add(relative), "A directory cursor restarted or emitted duplicate work.");
            return ValueTask.FromResult(false);
        }) { MetadataEntriesPerSecond = 64 });
        await DrainAsync(enumerator, area.Root).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(100, visited.Count);
        Assert.Equal(100, enumerator.LastEnumeratedEntries);
        Assert.Empty(enumerator.LastEvidence);
        Assert.Equal(0, sourceBytes);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Revoked_list_access_is_rechecked_on_the_next_page_instead_of_using_a_stale_cursor_grant()
    {
        using var area = new Area(80);
        var directory = new DirectoryInfo(area.Root);
        var original = directory.GetAccessControl(AccessControlSections.Access);
        var changed = false;
        var clock = new Clock();
        var enumerator = new LocalSourceEnumerator(metadataTimeProvider: clock)
        {
            BeforeMetadataDelay = async _ =>
            {
                if (!changed)
                {
                    changed = true;
                    var deny = new DirectorySecurity();
                    deny.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    deny.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
                    directory.SetAccessControl(deny);
                }
                await clock.AdvanceAsync();
            }
        };
        using var scope = enumerator.UseScope(new(["."], (_, _) => ValueTask.FromResult(false)) { MetadataEntriesPerSecond = 1 });
        try
        {
            await DrainAsync(enumerator, area.Root).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(enumerator.LastEvidence, issue => issue.NamespaceIncomplete && issue.Kind == "permission");
            Assert.True(enumerator.LastEnumeratedEntries < 80);
        }
        finally
        {
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            directory.SetAccessControl(restore);
        }
    }

    [Fact]
    public async Task Cloud_membership_becoming_incomplete_during_a_pause_remains_explicit_debt()
    {
        using var provider = new FluxKnowledge.TestSupport.CloudSourceFixture(@"I:\Codex Workspaces\Staging\FluxKnowledge");
        var source = provider.Directory("source");
        File.WriteAllText(Path.Combine(source, "one.txt"), "one");
        File.WriteAllText(Path.Combine(source, "two.txt"), "two");
        var clock = new Clock();
        var changed = false;
        var enumerator = new LocalSourceEnumerator(metadataTimeProvider: clock)
        {
            BeforeMetadataDelay = async _ =>
            {
                if (!changed) { changed = true; provider.MakeCloudDirectory(source); }
                await clock.AdvanceAsync();
            }
        };
        using var scope = enumerator.UseScope(new(["."], (_, _) => ValueTask.FromResult(false)) { MetadataEntriesPerSecond = 1 });
        await DrainAsync(enumerator, source).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(enumerator.LastEvidence, issue => issue.NamespaceIncomplete && issue.Detail == "CloudDirectoryNamespaceIncomplete");
    }

    private static async Task DrainAsync(LocalSourceEnumerator enumerator, string path, CancellationToken ct = default)
    {
        var root = SourceRootConfiguration.Create(path, "Metadata pacing fixture", true, false, 1024);
        await foreach (var capture in enumerator.EnumerateCapturedAsync(root, new(SourceScanRequestId.New(), new(Guid.NewGuid(), "metadata-test", 1)), ct))
            await capture.DisposeAsync();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenExclusiveDirectory(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    private sealed class Area : IDisposable
    {
        private const string Staging = @"I:\Codex Workspaces\Staging\FluxKnowledge";
        private readonly string _area = Path.Combine(Staging, "MetadataPacing-" + Guid.NewGuid().ToString("N"));
        internal string Parent => Path.Combine(_area, "parent");
        internal string Root => Path.Combine(Parent, "source");
        internal Area(int files)
        {
            Directory.CreateDirectory(Root);
            for (var index = 0; index < files; index++) File.WriteAllText(Path.Combine(Root, $"file-{index:000}.txt"), "entry");
        }
        public void Dispose()
        {
            Assert.StartsWith(Path.GetFullPath(Staging) + Path.DirectorySeparatorChar, Path.GetFullPath(_area), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(_area)) Directory.Delete(_area, true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private TimeSpan _now;
        private readonly Channel<Delay> _timers = Channel.CreateUnbounded<Delay>();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _now.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Delay(callback, state, dueTime); _timers.Writer.TryWrite(timer); return timer; }
        internal async ValueTask AdvanceAsync()
        { var delay = await _timers.Reader.ReadAsync(); _now += delay.Due; delay.Fire(); }
    }
    private sealed class Delay(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        internal TimeSpan Due => due;
        private bool _disposed;
        internal void Fire() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
