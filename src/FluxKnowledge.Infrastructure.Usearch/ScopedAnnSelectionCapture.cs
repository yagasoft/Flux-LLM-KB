using System.Diagnostics;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;

namespace FluxKnowledge.Infrastructure.Usearch;

/// <summary>A private key snapshot; caller-owned sets never escape into a later native call.</summary>
internal sealed class ScopedAnnSelection(IndexSegmentDescriptor segment, long[] keys, double selectionMilliseconds)
{
    internal IndexSegmentDescriptor Segment { get; } = segment;
    internal int KeyCount => keys.Length;
    internal double SelectionMilliseconds { get; } = selectionMilliseconds;
    internal HashSet<long> CreateMembership() => new(keys);
}

internal static class ScopedAnnSelectionCapture
{
    // Additional per-query retained-selection accounting, independent of the
    // existing immutable binding cache. The record charge is not a CLR heap measurement.
    internal const long MaximumChargedBytes = 64L * 1024 * 1024;
    private const long RecordCharge = 160;

    internal static async ValueTask<IReadOnlyList<ScopedAnnSelection>> CaptureAsync(ICorpusGenerationLease lease,
        IReadOnlyList<IndexSegmentDescriptor> segments, ResolvedCorpusScope scope, ICorpusAnnScopeSelection selection,
        CancellationToken cancellationToken, long maximumChargedBytes = MaximumChargedBytes)
    {
        if (maximumChargedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumChargedBytes));
        var result = new List<ScopedAnnSelection>(); // Never preallocate by the complete manifest count.
        long charged = 0;
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            var eligible = await selection.ReadSegmentKeysAsync(lease, scope, segment, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var count = eligible.Count;
            if (count < 0 || count > segment.VectorCount) throw new IndexGenerationValidationException("segment-scope-membership-invalid");
            if (count == 0)
            {
                if (HybridSearchDiagnostics.Log.IsEnabled())
                {
                    var correlation = HybridSearchDiagnostics.CurrentCorrelation;
                    HybridSearchDiagnostics.Log.ScopedAnnSegment(correlation.TraceId, correlation.SpanId, segment.Id, 0, 0,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0);
                }
                continue;
            }
            var next = checked(charged + checked(sizeof(long) * (long)count) + RecordCharge);
            if (next > maximumChargedBytes) throw new PassageRetrievalRefusalException("scope-capacity-exceeded");
            charged = next; // Reserve before allocating either keys or retained bookkeeping.
            var keys = new long[count];
            var index = 0;
            foreach (var key in eligible)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (key <= 0 || index == count) throw new IndexGenerationValidationException("segment-scope-membership-invalid");
                keys[index++] = key;
            }
            if (index != count) throw new IndexGenerationValidationException("segment-scope-membership-invalid");
            result.Add(new(segment, keys, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
