using FluxKnowledge.Integrations.Models;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class CpuSearchOwnerLeaseTests
{
    [Fact]
    public async Task Only_one_process_owner_enters_and_cancelling_a_waiter_does_not_release_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FluxCpuSearchOwner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, CpuSearchOwnerLease.FileName), []);
        try
        {
            using var first = await CpuSearchOwnerLease.AcquireAsync(directory, CancellationToken.None);
            Assert.ThrowsAny<IOException>(() => Directory.Move(directory, directory + "-moved"));
            using var cancel = new CancellationTokenSource();
            var cancelledWaiter = CpuSearchOwnerLease.AcquireAsync(directory, cancel.Token);
            await Task.Delay(150);
            Assert.False(cancelledWaiter.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);
            var next = CpuSearchOwnerLease.AcquireAsync(directory, CancellationToken.None);
            await Task.Delay(150);
            Assert.False(next.IsCompleted);
            first.Dispose();
            using var second = await next.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(Path.Combine(directory, CpuSearchOwnerLease.FileName)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Missing_owner_file_fails_closed_without_creating_one()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FluxCpuSearchMissing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                CpuSearchOwnerLease.AcquireAsync(directory, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory, CpuSearchOwnerLease.FileName)));
        }
        finally { Directory.Delete(directory); }
    }
}
