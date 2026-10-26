using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationOplockNativeTests
{
    [Fact]
    public void Reader_slot_is_nonblocking_across_instances_and_preserves_the_same_lock_file()
    {
        using var fixture = new Fixture();
        var slot = new WindowsSourceVerificationReaderSlot(fixture.Root);
        using (var first = slot.TryAcquire())
        {
            Assert.NotNull(first);
            using var other = new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire();
            Assert.Null(other);
            Assert.Throws<IOException>(() => File.Delete(Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName)));
        }
        var before = PhysicalFileIdentity.Get(Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName));
        using (var next = new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire())
            Assert.NotNull(next);
        Assert.Equal(before, PhysicalFileIdentity.Get(Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName)));
    }

    [Fact]
    public void Slot_does_not_depend_on_the_acquiring_threads_lifetime()
    {
        using var fixture = new Fixture();
        IDisposable? acquired = null;
        Exception? failure = null;
        var issuer = new Thread(() =>
        {
            try { acquired = new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire(); }
            catch (Exception error) { failure = error; }
        });
        issuer.Start();
        Assert.True(issuer.Join(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Null(failure);
            Assert.NotNull(acquired);
            using var other = new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire();
            Assert.Null(other);
        }
        finally { acquired?.Dispose(); }
        using var next = new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire();
        Assert.NotNull(next);
    }

    [Fact]
    public void Unsafe_slot_location_is_reported_instead_of_treated_as_another_reader()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, WindowsSourceVerificationReaderSlot.FileName));
        var error = Record.Exception(() => new WindowsSourceVerificationReaderSlot(fixture.Root).TryAcquire()?.Dispose());
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Unsafe slot was silently ignored");
    }
}
