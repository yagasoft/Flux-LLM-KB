using System.Diagnostics;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed partial class UsearchSegmentCache
{
    /// <summary>One authoritative retirement lookup for this call; never opens cold views.</summary>
    internal async ValueTask<BorrowedBatch> BorrowPreparedBatchAsync(IReadOnlyList<IndexSegmentDescriptor> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != segments.Count)
            throw new ArgumentException("segment-batch-ids-invalid", nameof(segments));
        var started = Stopwatch.GetTimestamp();
        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { ReportPreparedPhase("ann-cache-gate", started); }
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var entries = new Entry[segments.Count];
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (!_entries.TryGetValue(segment.Id, out var entry)) throw new PassageRetrievalRefusalException("warming");
                if (entry.Descriptor != segment || entry.Disposing) throw new InvalidOperationException("segment-cache-proof-changed");
                if (!IsPrepared(segment) || entry.Index is null) throw new PassageRetrievalRefusalException("warming");
                if (entry.Borrowers == int.MaxValue) throw new InvalidOperationException("segment-cache-borrower-overflow");
                entries[i] = entry;
            }
            if (entries.Length > 0)
            {
                // The SQL implementation pages this method internally; this is not a
                // promise of one SQL command for arbitrarily large manifests.
                var retirementStarted = Stopwatch.GetTimestamp();
                IReadOnlySet<string> retiring;
                try { retiring = await owners.ReadRetiringAsync(segments.Select(s => s.Id).ToArray(), cancellationToken).ConfigureAwait(false); }
                finally { ReportPreparedPhase("ann-retirement-read", retirementStarted); }
                cancellationToken.ThrowIfCancellationRequested();
                var affected = entries.Where(e => retiring.Contains(e.Descriptor.Id)).ToArray();
                if (affected.Length > 0)
                {
                    foreach (var entry in affected) { entry.Disposing = true; _prepared.TryRemove(entry.Descriptor.Id, out _); }
                    foreach (var entry in affected.Where(e => e.Borrowers == 0)) await DisposeEntryAsync(entry).ConfigureAwait(false);
                    throw new InvalidOperationException("segment-retiring-or-proof-changed");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Allocate the complete result before committing any borrower count.
            var members = entries.Select(entry => new BorrowedSegment(this, entry)).ToArray();
            var batch = new BorrowedBatch(members);
            foreach (var entry in entries) entry.Borrowers++;
            foreach (var segment in segments) ReportView(segment, true, Stopwatch.GetTimestamp());
            ReportBatch(segments.Count, started);
            return batch;
        }
        finally { _gate.Release(); }
    }

    private static void ReportPreparedPhase(string phase, long started)
    {
        if (!HybridSearchDiagnostics.Log.IsEnabled()) return;
        var correlation = HybridSearchDiagnostics.CurrentCorrelation;
        HybridSearchDiagnostics.Log.Phase(correlation.TraceId, correlation.SpanId, string.Empty, phase,
            "settled", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static void ReportBatch(int segments, long started)
    {
        if (!HybridSearchDiagnostics.Log.IsEnabled()) return;
        var correlation = HybridSearchDiagnostics.CurrentCorrelation;
        HybridSearchDiagnostics.Log.PreparedBorrowBatch(correlation.TraceId, correlation.SpanId, segments,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    internal sealed class BorrowedBatch(BorrowedSegment[] members) : IAsyncDisposable
    {
        private readonly object _disposalGate = new();
        private Task? _disposal;
        public IReadOnlyList<BorrowedSegment> Members { get; } = Array.AsReadOnly(members);

        public ValueTask DisposeAsync()
        {
            // Every concurrent/repeated disposer observes the same actual settlement
            // and any uncertainty, rather than returning early or decrementing twice.
            lock (_disposalGate) return new(_disposal ??= ReleaseAllAsync());
        }

        internal async ValueTask DisposeAfterAsync(Exception? operationFailure)
        {
            try { await DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure) when (operationFailure is not null)
            { throw new AggregateException("ANN search and prepared-view release failed.", operationFailure, cleanupFailure); }
        }

        private async Task ReleaseAllAsync()
        {
            var failures = new List<Exception>();
            for (var i = members.Length - 1; i >= 0; i--)
            {
                try { await members[i].DisposeAsync().ConfigureAwait(false); }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (failures.Count > 0) throw new AggregateException("Prepared-view release remains uncertain.", failures);
        }
    }
}
