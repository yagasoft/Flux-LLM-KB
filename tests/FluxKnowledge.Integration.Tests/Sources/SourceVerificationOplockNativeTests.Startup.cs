using System.IO.MemoryMappedFiles;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationOplockNativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_startup_waits_for_the_sole_owner_to_finish(bool afterNativeIssue)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Action pause = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var acquiring = Task.Run(() => fixture.Acquire(cancellation.Token, owner =>
        {
            if (afterNativeIssue) owner.AfterNativeIssue = pause;
            else owner.BeforeNativeIssue = pause;
        }));
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2))));
            cancellation.Cancel();
            Assert.False(acquiring.IsCompleted);
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring.WaitAsync(TimeSpan.FromSeconds(2)));
        using var exclusive = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Owner_start_failure_completes_cleanup_without_issuing_native_io()
    {
        using var fixture = new Fixture();
        var issued = false;
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Acquire(configureProbe: owner =>
        {
            owner.BeforeOwnerStart = () => throw new InvalidOperationException("Controlled owner start failure.");
            owner.BeforeNativeIssue = () => issued = true;
        }));
        Assert.Equal("Controlled owner start failure.", error.Message);
        Assert.False(issued);
        using var exclusive = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Refused_grant_completes_the_handshake_and_releases_its_native_owner()
    {
        using var fixture = new Fixture();
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            var error = Assert.Throws<IOException>(() => fixture.Acquire(configureProbe: owner =>
                owner.BeforeNativeIssue = () =>
                {
                    using var input = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    mapping = MemoryMappedFile.CreateFromFile(input, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
                    view = mapping.CreateViewAccessor(0, 64, MemoryMappedFileAccess.ReadWrite);
                }));
            Assert.Equal("SourceVerificationOplockUnavailable", error.Message);
        }
        finally { view?.Dispose(); mapping?.Dispose(); }
        using var exclusive = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Post_grant_validation_rejects_a_replacement_after_admission_guards_were_released()
    {
        using var fixture = new Fixture();
        var moved = fixture.Path + ".original";
        Assert.Throws<SourceSnapshotChangedException>(() => fixture.Acquire(configureProbe: owner =>
            owner.BeforeNativeIssue = () =>
            {
                File.Move(fixture.Path, moved);
                File.WriteAllText(fixture.Path, new string('r', 64));
            }));
        using var current = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var original = File.Open(moved, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
