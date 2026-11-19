using FluxKnowledge.Infrastructure.Inference.Search;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class BgeCpuMemoryPreflightTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(0L, false)]
    [InlineData(0L, true)]
    public async Task Preallocation_memory_refusal_is_retryable_only_after_confirmed_owner_release(long? freeBytes, bool uncertainRelease)
    {
        var owner = new PreflightOwner(uncertainRelease);
        var probes = 0;
        // No model store is supplied: refusal must happen before any tokenizer,
        // weight or native session is opened.
        var failure = await Record.ExceptionAsync(() => BgeCpuResidentModelFactory.LoadAsync(null!,
            _ => Task.FromResult<IDisposable>(owner), CancellationToken.None, 4, 4,
            () => { probes++; return freeBytes; }));
        Assert.Equal(1, probes);
        Assert.Equal(1, owner.ReleaseAttempts);
        if (uncertainRelease) Assert.IsType<CpuNativeReleaseUnconfirmedException>(failure);
        else Assert.IsType<CpuWarmupMemoryUnavailableException>(failure);
    }

    private sealed class PreflightOwner(bool uncertain) : IDisposable
    {
        public int ReleaseAttempts { get; private set; }
        public void Dispose()
        {
            ReleaseAttempts++;
            if (uncertain) throw new IOException("synthetic-owner-release-unconfirmed");
        }
    }
}
