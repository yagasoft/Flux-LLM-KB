using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed class CorpusAnnBindingCacheTests
{
    [Fact]
    public async Task Readiness_is_nonblocking_during_loading_and_concurrent_preparations_share_one_complete_load()
    {
        var cache = new CorpusAnnBindingCache(); var epoch = Guid.NewGuid(); var loads = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<SegmentBindingGroups> Load(CancellationToken ct)
        { loads++; started.SetResult(); await release.Task.WaitAsync(ct); return Groups(); }
        var first = cache.GetAsync(epoch, Segment('a'), Load, CancellationToken.None).AsTask();
        await started.Task;
        var second = cache.GetAsync(epoch, Segment('a'), Load, CancellationToken.None).AsTask();
        Assert.Null(cache.TryGet(epoch, Segment('a')));
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        release.SetResult();
        Assert.Same(await first, await second);
        Assert.Equal(1, loads);
        Assert.NotNull(cache.TryGet(epoch, Segment('a')));
    }

    [Fact]
    public async Task Default_retention_does_not_reload_a_fitting_projection_after_ten_idle_minutes()
    {
        var clock = new Clock(); var cache = new CorpusAnnBindingCache(clock);
        var epoch = Guid.NewGuid(); var loads = 0;
        ValueTask<SegmentBindingGroups> Load(CancellationToken _) { loads++; return ValueTask.FromResult(Groups()); }
        var first = await cache.GetAsync(epoch, Segment('a'), Load, CancellationToken.None);
        clock.Now += TimeSpan.FromHours(1);
        Assert.Same(first, await cache.GetAsync(epoch, Segment('a'), Load, CancellationToken.None));
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Immutable_metadata_is_reused_but_another_epoch_cannot_reuse_it()
    {
        var cache = new CorpusAnnBindingCache(); var epoch = Guid.NewGuid(); var loads = 0;
        ValueTask<SegmentBindingGroups> Load(CancellationToken _) { loads++; return ValueTask.FromResult(Groups()); }
        var first = await cache.GetAsync(epoch, Segment('a'), Load, CancellationToken.None);
        Assert.Same(first, await cache.GetAsync(epoch, Segment('a'), Load, CancellationToken.None));
        Assert.Equal(1, loads);
        Assert.NotSame(first, await cache.GetAsync(Guid.NewGuid(), Segment('a'), Load, CancellationToken.None));
        Assert.Equal(2, loads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(epoch,
            Segment('a') with { FileChecksum = new string('c', 64) }, Load, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Failed_or_cancelled_loads_never_publish_partial_metadata()
    {
        var cache = new CorpusAnnBindingCache(); var epoch = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync(epoch, Segment('a'),
            _ => ValueTask.FromException<SegmentBindingGroups>(new IOException("incomplete")), CancellationToken.None).AsTask());
        Assert.Equal(0, cache.RetainedBytes);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(epoch, Segment('a'),
            _ => ValueTask.FromResult(Groups()), cancelled.Token).AsTask());
        Assert.Equal(0, cache.RetainedBytes);
        Assert.NotNull(await cache.GetAsync(epoch, Segment('a'), _ => ValueTask.FromResult(Groups()), CancellationToken.None));
    }

    [Fact]
    public async Task Managed_metadata_obeys_byte_budget_and_idle_expiry()
    {
        var clock = new Clock(); var cache = new CorpusAnnBindingCache(clock, 600, TimeSpan.FromMinutes(10));
        var epoch = Guid.NewGuid(); var loads = 0;
        ValueTask<SegmentBindingGroups> Load(CancellationToken _) { loads++; return ValueTask.FromResult(Groups()); }
        await cache.GetAsync(epoch, Segment('a'), Load, CancellationToken.None);
        await cache.GetAsync(epoch, Segment('b'), Load, CancellationToken.None);
        Assert.InRange(cache.RetainedBytes, 1, 600);
        await cache.GetAsync(epoch, Segment('a'), Load, CancellationToken.None);
        Assert.Equal(3, loads);
        clock.Now += TimeSpan.FromMinutes(11);
        await cache.GetAsync(epoch, Segment('a'), Load, CancellationToken.None);
        Assert.Equal(4, loads);
        Assert.InRange(cache.RetainedBytes, 1, 600);
    }

    private static SegmentBindingGroups Groups() => new(
        new Dictionary<CanonicalBindingKey, long[]> { [new(Guid.NewGuid(), Guid.NewGuid(), 1)] = [1, 2, 3] }, new HashSet<CanonicalBindingKey>(), 512);
    private static IndexSegmentDescriptor Segment(char id) => new(new string(id, 64), "profile", 2,
        @"I:\synthetic\" + id, new string('a', 64), new string('b', 64), 1024, 3, "synthetic");
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
