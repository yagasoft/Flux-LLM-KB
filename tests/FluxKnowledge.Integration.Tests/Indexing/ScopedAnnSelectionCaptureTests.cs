using System.Collections;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Usearch;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class ScopedAnnSelectionCaptureTests
{
    [Fact]
    public async Task Capture_preserves_order_excludes_empty_segments_and_never_aliases_caller_sets()
    {
        var segments = new[] { Segment("a", 3), Segment("b", 3), Segment("c", 3) };
        var first = new HashSet<long> { 1, 2 };
        var selector = new Selector([first, new HashSet<long>(), new HashSet<long> { 7 }]);
        var lease = new Lease(); var scope = new ResolvedCorpusScope("root", [Guid.NewGuid()], null);
        var result = await ScopedAnnSelectionCapture.CaptureAsync(lease, segments, scope, selector, default);
        Assert.Equal(segments, selector.Segments);
        Assert.All(selector.Leases, value => Assert.Same(lease, value));
        Assert.All(selector.Scopes, value => Assert.Same(scope, value));
        Assert.Equal(new[] { "a", "c" }, result.Select(p => p.Segment.Id));
        first.Clear();
        Assert.Equal(new long[] { 1, 2 }, result[0].CreateMembership().Order());
        var mutable = result[0].CreateMembership(); mutable.Clear();
        Assert.Equal(2, result[0].KeyCount);
        Assert.Equal(2, result[0].CreateMembership().Count);
    }

    [Fact]
    public async Task Payload_and_record_charge_are_reserved_before_enumeration_or_copy()
    {
        var selector = new Selector([new UnenumerableKeys(8 * 1024 * 1024)]);
        var error = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => ScopedAnnSelectionCapture.CaptureAsync(
            new Lease(), [Segment("large", 8 * 1024 * 1024)], Scope, selector, default).AsTask());
        Assert.Equal("scope-capacity-exceeded", error.Status);
        var tiny = new Selector([new HashSet<long> { 1 }, new UnenumerableKeys(1)]);
        await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => ScopedAnnSelectionCapture.CaptureAsync(
            new Lease(), [Segment("a", 1), Segment("b", 1)], Scope, tiny, default, maximumChargedBytes: 335).AsTask());
    }

    [Fact]
    public async Task Cancellation_after_selection_returns_no_partial_capture_or_retained_caller_alias()
    {
        using var caller = new CancellationTokenSource();
        var selector = new Selector([new HashSet<long> { 1 }, new UnenumerableKeys(1)]) { AfterRead = count => { if (count == 2) caller.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScopedAnnSelectionCapture.CaptureAsync(new Lease(),
            [Segment("a", 1), Segment("b", 1), Segment("c", 1)], Scope, selector, caller.Token).AsTask());
        Assert.Equal(2, selector.Segments.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_native_keys_refuse_the_complete_capture(long key)
    {
        var selector = new Selector([new HashSet<long> { 1 }, new HashSet<long> { key }]);
        await Assert.ThrowsAsync<IndexGenerationValidationException>(() => ScopedAnnSelectionCapture.CaptureAsync(new Lease(),
            [Segment("a", 1), Segment("b", 1)], Scope, selector, default).AsTask());
    }

    [Fact]
    public async Task Segment_membership_count_cannot_exceed_the_captured_descriptor()
    {
        await Assert.ThrowsAsync<IndexGenerationValidationException>(() => ScopedAnnSelectionCapture.CaptureAsync(new Lease(),
            [Segment("a", 1)], Scope, new Selector([new HashSet<long> { 1, 2 }]), default).AsTask());
    }

    private static readonly ResolvedCorpusScope Scope = new("root", [Guid.NewGuid()], null);
    private static IndexSegmentDescriptor Segment(string id, long count) => new(id, "synthetic", 4, "unused", "", "", 1, count, "synthetic");
    private sealed class Lease : ICorpusGenerationLease
    {
        public Guid LeaseId { get; } = Guid.NewGuid();
        public IndexGenerationDescriptor Generation => throw new NotSupportedException();
        public ValueTask<bool> IsCurrentAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Selector(IReadOnlyList<IReadOnlySet<long>> keys) : ICorpusAnnScopeSelection
    {
        public List<IndexSegmentDescriptor> Segments { get; } = [];
        public List<ICorpusGenerationLease> Leases { get; } = [];
        public List<ResolvedCorpusScope> Scopes { get; } = [];
        public Action<int>? AfterRead { get; init; }
        public ValueTask<IReadOnlySet<long>> ReadSegmentKeysAsync(ICorpusGenerationLease lease, ResolvedCorpusScope scope, IndexSegmentDescriptor segment, CancellationToken ct)
        {
            var result = keys[Segments.Count]; Segments.Add(segment); Leases.Add(lease); Scopes.Add(scope);
            AfterRead?.Invoke(Segments.Count); return ValueTask.FromResult(result);
        }
    }
    private sealed class UnenumerableKeys(int count) : IReadOnlySet<long>
    {
        public int Count => count;
        public bool Contains(long item) => throw new InvalidOperationException("Keys were inspected before budget/cancellation checks.");
        public IEnumerator<long> GetEnumerator() => throw new InvalidOperationException("Keys were copied before budget/cancellation checks.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<long> other) => throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<long> other) => throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<long> other) => throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<long> other) => throw new NotSupportedException();
        public bool Overlaps(IEnumerable<long> other) => throw new NotSupportedException();
        public bool SetEquals(IEnumerable<long> other) => throw new NotSupportedException();
    }
}
