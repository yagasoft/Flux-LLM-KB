using System.Collections;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class UsearchPreparedBatchTests
{
    [Fact]
    public async Task Prepared_batch_checks_retirement_once_and_reuses_every_native_owner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var batch = await fixture.Cache.BorrowPreparedBatchAsync(fixture.Segments, default);
        Assert.Equal(fixture.Segments.Select(s => s.Id), Assert.Single(fixture.Owners.Reads));
        Assert.Equal(3, batch.Members.Count); Assert.Equal(3, fixture.Owners.Registered);
        foreach (var member in batch.Members)
            Assert.Equal(2, (await member.SearchAsync([1, 2, 3, 4], 2, default)).Count);
        var instances = batch.Members.Select(m => m.NativeInstanceId).ToArray();
        await batch.DisposeAsync(); await batch.DisposeAsync();
        await using (var again = await fixture.Cache.BorrowPreparedBatchAsync(fixture.Segments, default))
            Assert.Equal(instances, again.Members.Select(m => m.NativeInstanceId));
        Assert.Equal(3, fixture.Owners.Registered);
        await fixture.Cache.SweepAsync(true, default);
        Assert.Empty(fixture.Owners.Active);
    }

    [Fact]
    public async Task Missing_conflicting_or_duplicate_members_refuse_without_partial_borrows_or_retirement_IO()
    {
        await using var fixture = await Fixture.CreateAsync(prepare: false);
        await fixture.Cache.PrepareAsync([fixture.Segments[0]], default);
        await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => fixture.Cache.BorrowPreparedBatchAsync(fixture.Segments, default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Cache.BorrowPreparedBatchAsync(
            [fixture.Segments[0] with { FileChecksum = new string('0', 64) }], default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Cache.BorrowPreparedBatchAsync(
            [fixture.Segments[0], fixture.Segments[0]], default).AsTask());
        Assert.Empty(fixture.Owners.Reads);
        await fixture.Cache.SweepAsync(true, default);
        Assert.Empty(fixture.Owners.Active);
    }

    [Fact]
    public async Task Cancellation_after_retirement_lookup_commits_no_borrowers()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var caller = new CancellationTokenSource();
        fixture.Owners.OnRead = (_, _) => { caller.Cancel(); return ValueTask.FromResult<IReadOnlySet<string>>(new HashSet<string>()); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Cache.BorrowPreparedBatchAsync(fixture.Segments, caller.Token).AsTask());
        fixture.Owners.OnRead = null;
        await fixture.Cache.SweepAsync(true, default);
        Assert.Empty(fixture.Owners.Active);
    }

    [Fact]
    public async Task Retiring_member_invalidates_its_prepared_view_without_borrowing_other_members()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Owners.Retiring.Add(fixture.Segments[1].Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Cache.BorrowPreparedBatchAsync(fixture.Segments, default).AsTask());
        Assert.False(fixture.Cache.IsPrepared(fixture.Segments[1]));
        Assert.Equal(2, fixture.Owners.Active.Count);
        await fixture.Cache.SweepAsync(true, default);
        Assert.Empty(fixture.Owners.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_stop_or_sweep_cannot_dispose_members_between_lookup_and_atomic_commit(bool stop)
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Owners.OnRead = async (_, _) => { entered.TrySetResult(); await release.Task; return new HashSet<string>(); };
        var borrowing = fixture.Cache.BorrowPreparedBatchAsync(fixture.Segments, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var maintenance = stop ? fixture.Cache.DisposeAsync().AsTask() : fixture.Cache.SweepAsync(true, default).AsTask();
        var completedBeforeCommit = maintenance.IsCompleted;
        fixture.Owners.OnRead = null; release.TrySetResult();
        await using var batch = await borrowing; await maintenance;
        Assert.False(completedBeforeCommit);
        Assert.Equal(3, fixture.Owners.Active.Count);
        Assert.Equal(2, (await batch.Members[0].SearchAsync([1, 2, 3, 4], 2, default)).Count);
        await Task.WhenAll(batch.DisposeAsync().AsTask(), batch.DisposeAsync().AsTask());
        if (!stop) await fixture.Cache.SweepAsync(true, default);
        Assert.Empty(fixture.Owners.Active);
    }

    [Fact]
    public async Task Release_attempts_every_member_and_preserves_search_and_native_cleanup_failures()
    {
        await using var fixture = await Fixture.CreateAsync();
        var batch = await fixture.Cache.BorrowPreparedBatchAsync(fixture.Segments, default);
        var uncertain = fixture.Segments[^1].Id;
        var nativeFailure = new IOException("synthetic native-close uncertainty");
        fixture.Cache.BeforeNativeDisposal = id => { if (id == uncertain) throw nativeFailure; };
        await fixture.Cache.DisposeAsync();
        var searchFailure = new ArgumentException("synthetic search failure");
        var error = await Assert.ThrowsAsync<AggregateException>(() => batch.DisposeAfterAsync(searchFailure).AsTask());
        Assert.Contains(searchFailure, error.Flatten().InnerExceptions);
        Assert.Contains(nativeFailure, error.Flatten().InnerExceptions);
        Assert.Single(fixture.Owners.Active);
        Assert.Equal(2, fixture.Owners.Released);
        await Assert.ThrowsAsync<AggregateException>(() => batch.DisposeAsync().AsTask());
        fixture.Cache.BeforeNativeDisposal = null;
        await fixture.Cache.SweepAsync(true, default);
        Assert.Empty(fixture.Owners.Active);
    }

    [Fact]
    public async Task Cancelled_native_search_keeps_the_entire_batch_until_the_actual_callback_settles()
    {
        await using var fixture = await Fixture.CreateAsync(segments: 1, vectorsPerSegment: 1536);
        var batch = await fixture.Cache.BorrowPreparedBatchAsync(fixture.Segments, default);
        using var keys = new BlockingKeys(1536); using var caller = new CancellationTokenSource();
        var search = Task.Run(async () => await batch.Members[0].SearchAsync([1, 2, 3, 4], 10, keys, caller.Token));
        try
        {
            await keys.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); caller.Cancel();
            var disposing = batch.DisposeAsync().AsTask();
            await fixture.Cache.DisposeAsync();
            Assert.False(disposing.IsCompleted); Assert.Single(fixture.Owners.Active);
            keys.Release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
            await disposing;
            Assert.Empty(fixture.Owners.Active);
        }
        finally { keys.Release.Set(); try { await search; } catch (OperationCanceledException) { } await batch.DisposeAsync(); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "FluxKnowledgePreparedBatch_" + Guid.NewGuid().ToString("N"));
        public Owners Owners { get; } = new();
        public UsearchSegmentCache Cache { get; private set; } = null!;
        public IReadOnlyList<IndexSegmentDescriptor> Segments { get; private set; } = [];
        public static async Task<Fixture> CreateAsync(int segments = 3, int vectorsPerSegment = 2, bool prepare = true)
        {
            var fixture = new Fixture();
            try
            {
                var options = UsearchIndexOptions.FromConfiguredRoot(fixture._root);
                var paths = new WindowsIndexSegmentReadLeaseFactory(fixture._root);
                fixture.Segments = await new UsearchSegmentBuilder(options, paths, new(vectorsPerSegment))
                    .BuildAsync("synthetic-batch-profile", 4, Stream(segments * vectorsPerSegment), default);
                fixture.Cache = new(options, paths, fixture.Owners, TimeProvider.System);
                if (prepare) await fixture.Cache.PrepareAsync(fixture.Segments, default);
                fixture.Owners.Reads.Clear();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (Cache is not null) { Cache.BeforeNativeDisposal = null; Owners.OnRead = null; await Cache.DisposeAsync(); }
            var resolved = Path.GetFullPath(_root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("FluxKnowledgePreparedBatch_", StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup target escaped its owned temp root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        private static async IAsyncEnumerable<CanonicalVector> Stream(int count, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var values = new float[] { 1, 2, 3, 4 }; var bytes = new byte[16]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            for (var id = 1; id <= count; id++)
            { ct.ThrowIfCancellationRequested(); yield return new(id, id, "synthetic-batch-profile", 4, bytes, new string('a', 64), Convert.ToHexStringLower(SHA256.HashData(bytes)), 1); await Task.Yield(); }
        }
    }

    private sealed class Owners : IIndexSegmentOwnerStore
    {
        public int Registered { get; private set; }
        public int Released { get; private set; }
        public HashSet<Guid> Active { get; } = [];
        public HashSet<string> Retiring { get; } = new(StringComparer.Ordinal);
        public List<string[]> Reads { get; } = [];
        public Func<IReadOnlyList<string>, CancellationToken, ValueTask<IReadOnlySet<string>>>? OnRead { get; set; }
        public ValueTask<Guid> RegisterAsync(IndexSegmentDescriptor segment, Guid instanceId, CancellationToken ct)
        { Registered++; var id = Guid.NewGuid(); Active.Add(id); return ValueTask.FromResult(id); }
        public ValueTask OpenedAsync(Guid id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReleasedAfterDisposalAsync(Guid id, CancellationToken ct)
        { Assert.False(ct.CanBeCanceled); Assert.True(Active.Remove(id)); Released++; return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlySet<string>> ReadRetiringAsync(IReadOnlyList<string> ids, CancellationToken ct)
        { Reads.Add(ids.ToArray()); return OnRead is { } work ? work(ids, ct) : ValueTask.FromResult<IReadOnlySet<string>>(Retiring.Intersect(ids).ToHashSet(StringComparer.Ordinal)); }
    }

    private sealed class BlockingKeys(int count) : IReadOnlySet<long>, IDisposable
    {
        private readonly HashSet<long> _keys = Enumerable.Range(1, count).Select(i => (long)i).ToHashSet();
        private int _entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int Count => _keys.Count;
        public bool Contains(long item)
        { if (Interlocked.Exchange(ref _entered, 1) == 0) { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Native callback test did not settle"); } return _keys.Contains(item); }
        public bool IsProperSubsetOf(IEnumerable<long> other) => _keys.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<long> other) => _keys.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<long> other) => _keys.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<long> other) => _keys.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<long> other) => _keys.Overlaps(other);
        public bool SetEquals(IEnumerable<long> other) => _keys.SetEquals(other);
        public IEnumerator<long> GetEnumerator() => _keys.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => Release.Dispose();
    }
}
