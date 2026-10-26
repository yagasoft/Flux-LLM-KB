using System.IO.MemoryMappedFiles;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationOplockNativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_survives_the_issuing_thread_lifetime(bool retainIssuerThread)
    {
        using var fixture = new Fixture();
        using var releaseIssuer = new ManualResetEventSlim();
        var acquired = new TaskCompletionSource<WindowsSourceVerificationOplock>(TaskCreationOptions.RunContinuationsAsynchronously);
        var issuer = new Thread(() =>
        {
            try
            {
                acquired.SetResult(fixture.Acquire());
                if (retainIssuerThread) releaseIssuer.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { acquired.TrySetException(error); }
        }) { IsBackground = true };
        issuer.Start();
        await using var lease = await acquired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            if (!retainIssuerThread) Assert.True(issuer.Join(TimeSpan.FromSeconds(2)));
            await Task.Delay(20);
            Assert.True(lease.IsUnbroken(), "Issuer alive=" + issuer.IsAlive + "; " + lease.CompletionEvidence);
        }
        finally { releaseIssuer.Set(); Assert.True(issuer.Join(TimeSpan.FromSeconds(2))); }
    }

    [Fact]
    public async Task Ordinary_guarded_read_does_not_break_the_verifier_request()
    {
        using var fixture = new Fixture();
        await using var lease = fixture.Acquire();
        using (var guard = WindowsSourcePathAccess.OpenRead(fixture.Root, fixture.Path))
        {
            guard.Parent.ValidateCurrent();
            _ = WindowsSourceObservationReader.Describe(guard);
            await using var input = new FileStream(guard.Handle, FileAccess.Read, 1, isAsync: true);
            input.Position = 0;
            Assert.True(lease.IsUnbroken(), "BeforeRead: " + lease.CompletionEvidence);
            Assert.Equal(64, await input.ReadAsync(new byte[64]));
            Assert.True(lease.IsUnbroken(), "AfterRead: " + lease.CompletionEvidence);
            guard.Parent.ValidateCurrent();
            _ = WindowsSourceObservationReader.Describe(guard);
            Assert.True(lease.IsUnbroken(), "AfterObservation: " + lease.CompletionEvidence);
        }
        Assert.True(lease.IsUnbroken(), "AfterGuardDisposal: " + lease.CompletionEvidence);
    }

    [Fact]
    public async Task User_cancellation_after_native_cleanup_before_publication_rejects_attestation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await using var lease = fixture.Acquire(cancellation.Token);
        using (var finalGuard = WindowsSourcePathAccess.OpenRead(fixture.Root, fixture.Path))
            Assert.True(lease.TryAttest());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        lease.BeforeTerminalPublication = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var retiring = lease.RetireAsync().AsTask();
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2))));
            Assert.False(retiring.IsCompleted);
            using var exclusive = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            cancellation.Cancel(); // The callback invalidates the already cached clean decision.
        }
        finally { release.Set(); }
        Assert.False(await retiring.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Native_check_straddling_terminal_disposal_rejects_without_accessing_freed_storage()
    {
        using var fixture = new Fixture();
        await using var lease = fixture.Acquire();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        lease.BeforeNativeCheck = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var checking = Task.Run(lease.IsUnbroken);
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2))));
            await Task.Run(() => File.WriteAllText(fixture.Path, new string('b', 64))).WaitAsync(TimeSpan.FromSeconds(2));
            await lease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); }
        Assert.False(await checking.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Native_completion_is_rejected_before_the_delayed_managed_owner_can_mark_it()
    {
        using var fixture = new Fixture();
        await using var lease = fixture.Acquire();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        lease.NativeCompletionObserved = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var writer = Task.Run(() => File.WriteAllText(fixture.Path, new string('c', 64)));
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2))));
            Assert.False(lease.IsUnbroken());
            Assert.False(lease.TryAttest());
            Assert.False(lease.Completion.IsCompleted);
        }
        finally { release.Set(); }
        await writer.WaitAsync(TimeSpan.FromSeconds(2));
        await lease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task External_completion_racing_local_retirement_never_preserves_attestation()
    {
        using var fixture = new Fixture();
        await using var lease = fixture.Acquire();
        using (var finalGuard = WindowsSourcePathAccess.OpenRead(fixture.Root, fixture.Path))
            Assert.True(lease.TryAttest());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        lease.NativeCompletionObserved = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var writer = Task.Run(() => File.WriteAllText(fixture.Path, new string('d', 64)));
        Task<bool>? retiring = null;
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2))));
            retiring = lease.RetireAsync().AsTask();
            Assert.False(retiring.IsCompleted);
        }
        finally { release.Set(); }
        await writer.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(await retiring!.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Delayed_user_cancellation_keeps_resources_until_terminal_and_never_reports_success()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await using var lease = fixture.Acquire(cancellation.Token);
        using (var finalGuard = WindowsSourcePathAccess.OpenRead(fixture.Root, fixture.Path))
            Assert.True(lease.TryAttest());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        lease.NativeCompletionObserved = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        Task<bool>? retiring = null;
        try
        {
            cancellation.Cancel();
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2))));
            retiring = lease.RetireAsync().AsTask();
            Assert.False(retiring.IsCompleted);
            Assert.False(lease.IsUnbroken());
            Assert.Throws<IOException>(() => File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        finally { release.Set(); }
        Assert.False(await retiring!.WaitAsync(TimeSpan.FromSeconds(2)));
        using var exclusive = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Retirement_without_final_attestation_is_not_a_verification_receipt()
    {
        using var fixture = new Fixture();
        await using var lease = fixture.Acquire();
        Assert.False(await lease.RetireAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writable_mapping_cannot_modify_bytes_under_an_unbroken_grant(bool mappingAlreadyExists)
    {
        using var fixture = new Fixture();
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? view = null;
        WindowsSourceVerificationOplock? lease = null;
        try
        {
            if (mappingAlreadyExists)
            {
                using var input = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                mapping = MemoryMappedFile.CreateFromFile(input, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
                view = mapping.CreateViewAccessor(0, 64, MemoryMappedFileAccess.ReadWrite);
            }
            // An existing writable mapping may prevent admission; that is valid outstanding debt.
            try { lease = fixture.Acquire(); }
            catch (IOException) when (mappingAlreadyExists) { return; }
            if (mapping is null)
            {
                await Task.Run(() =>
                {
                    using var input = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    mapping = MemoryMappedFile.CreateFromFile(input, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
                    view = mapping.CreateViewAccessor(0, 64, MemoryMappedFileAccess.ReadWrite);
                }).WaitAsync(TimeSpan.FromSeconds(2));
            }
            view!.Write(0, (byte)'m');
            view.Flush();
            Assert.False(lease.IsUnbroken());
            await lease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
            view?.Dispose(); mapping?.Dispose();
        }
    }
}
