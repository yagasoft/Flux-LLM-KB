using Cloud.Unum.USearch;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using System.Diagnostics;

namespace FluxKnowledge.Infrastructure.Usearch;

/// <summary>Opens exactly the generation captured before embedding; never follows a later pointer.</summary>
public sealed class UsearchCorpusAnnLeaseFactory(IIndexGenerationStore store, UsearchGenerationValidator validator,
    ISegmentedIndexStore? segmented = null, UsearchIndexOptions? options = null, IIndexSegmentReadLeaseFactory? paths = null,
    UsearchSegmentCache? cache = null, IDerivedIndexRecoverySignal? recoverySignal = null) : ICorpusAnnLeaseFactory
{
    public async ValueTask<ICorpusAnnLease> OpenAsync(ICorpusGenerationLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Generation.FormatVersion == 2)
            return await OpenSegmentedAsync(lease, cancellationToken).ConfigureAwait(false);
        if (lease.Generation.FormatVersion != 1) throw new IndexGenerationValidationException("corpus-index-format-unsupported");
        USearchIndex? index = null;
        try
        {
            if (!await lease.IsCurrentAsync(cancellationToken).ConfigureAwait(false))
                throw new PublicationSnapshotConflictException("The captured corpus generation is no longer current.");
            var vectors = await store.ReadVectorsAsync(lease.Generation.Id, cancellationToken).ConfigureAwait(false);
            validator.Validate(lease.Generation.IndexPath, lease.Generation, vectors);
            cancellationToken.ThrowIfCancellationRequested();
            index = new USearchIndex(Path.Combine(lease.Generation.IndexPath, UsearchGenerationValidator.IndexFileName), false);
            if (!await lease.IsCurrentAsync(cancellationToken).ConfigureAwait(false))
                throw new PublicationSnapshotConflictException("The corpus changed while opening its captured generation.");
            return new NativeLease(lease, index);
        }
        catch
        {
            index?.Dispose();
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<ICorpusAnnLease> OpenSegmentedAsync(ICorpusGenerationLease lease, CancellationToken cancellationToken)
    {
        try
        {
            if (segmented is null || options is null || paths is null || cache is null) throw new InvalidOperationException("segmented-ann-not-configured");
            if (!await lease.IsReadableAsync(cancellationToken)) throw new PublicationSnapshotConflictException("The captured manifest changed.");
            var manifest = await segmented.ReadManifestAsync(lease.Generation.Id, cancellationToken);
            if (manifest.Generation != lease.Generation) throw new IndexGenerationValidationException("manifest-sql-proof-mismatch");
            new UsearchManifestValidator(options, paths).Validate(manifest);
            if (!await lease.IsReadableAsync(cancellationToken)) throw new PublicationSnapshotConflictException("The corpus changed while opening its manifest.");
            return new SegmentedNativeLease(lease, manifest.Segments, cache, recoverySignal);
        }
        catch (Exception exception)
        {
            NotifySegmentFailure(recoverySignal, lease.Generation.Id, exception);
            await lease.DisposeAsync();
            throw;
        }
    }

    private sealed class SegmentedNativeLease(ICorpusGenerationLease sqlLease,
        IReadOnlyList<IndexSegmentDescriptor> segments, UsearchSegmentCache cache, IDerivedIndexRecoverySignal? signal) : ICorpusAnnLease, IAnnCandidatePool
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile bool _disposed;
        public Guid LeaseId => sqlLease.LeaseId;
        public IndexGenerationDescriptor Generation => sqlLease.Generation;
        public ValueTask<bool> IsCurrentAsync(CancellationToken cancellationToken) => sqlLease.IsCurrentAsync(cancellationToken);
        public ValueTask<bool> IsReadableAsync(CancellationToken cancellationToken) => sqlLease.IsReadableAsync(cancellationToken);

        public bool IsPrepared(ICorpusAnnBindingPreparation metadata) => !_disposed && segments.All(segment =>
            metadata.IsPrepared(Generation.CorpusStamp!.CorpusEpoch, segment) && cache.IsPrepared(segment));

        public async ValueTask PrepareAsync(ICorpusAnnBindingPreparation metadata, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!await sqlLease.IsReadableAsync(cancellationToken)) throw new PassageRetrievalRefusalException("index-updating");
                long requiredBytes = 0;
                foreach (var segment in segments)
                {
                    requiredBytes = checked(requiredBytes + await metadata.PrepareAsync(this, segment, cancellationToken));
                    if (requiredBytes > metadata.MaximumRetainedBytes) throw new PassageRetrievalRefusalException("scope-capacity-exceeded");
                }
                if (!segments.All(segment => metadata.IsPrepared(Generation.CorpusStamp!.CorpusEpoch, segment)))
                    throw new PassageRetrievalRefusalException("warming");
                await cache.PrepareAsync(segments, cancellationToken);
                if (!await sqlLease.IsReadableAsync(cancellationToken)) throw new PassageRetrievalRefusalException("index-updating");
            }
            finally { _gate.Release(); }
        }

        public ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit,
            ResolvedCorpusScope scope, ICorpusAnnScopeSelection selection, CancellationToken cancellationToken) =>
            SearchScopedCoreAsync(query, limit, scope, selection, false, cancellationToken);

        public ValueTask<IReadOnlyList<AnnMatch>> SearchCandidatePoolAsync(IReadOnlyList<float> query, int limit,
            ResolvedCorpusScope scope, ICorpusAnnScopeSelection selection, CancellationToken cancellationToken) =>
            SearchScopedCoreAsync(query, limit, scope, selection, true, cancellationToken);

        private async ValueTask<IReadOnlyList<AnnMatch>> SearchScopedCoreAsync(IReadOnlyList<float> query, int limit,
            ResolvedCorpusScope scope, ICorpusAnnScopeSelection selection, bool candidatePool, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (query.Count != Generation.Dimensions || query.Any(value => !float.IsFinite(value)) || !query.Any(value => value != 0))
                throw new ArgumentException("corpus-query-vector-invalid");
            if (limit < 1 || limit > (candidatePool ? IAnnCandidatePool.MaximumResults : 100)) throw new ArgumentOutOfRangeException(nameof(limit));
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!await sqlLease.IsReadableAsync(cancellationToken)) throw new PublicationSnapshotConflictException("The captured manifest is unreadable.");
                var selected = await ScopedAnnSelectionCapture.CaptureAsync(this, segments, scope, selection, cancellationToken);
                var borrowed = await cache.BorrowPreparedBatchAsync(selected.Select(s => s.Segment).ToArray(), cancellationToken);
                Exception? operationFailure = null;
                try
                {
                    var top = new List<AnnMatch>();
                    for (var i = 0; i < selected.Count; i+=2)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var first=i;
                        var window=await ScopedAnnSearchWindow.RunAsync(()=>SearchOneAsync(first),
                            first+1<selected.Count?()=>SearchOneAsync(first+1):null).ConfigureAwait(false);
                        // Join both actual callbacks before merging or releasing any borrowed native view.
                        foreach(var matches in window)top.AddRange(matches);
                        top = top.OrderBy(value => value.Distance).ThenBy(value => value.VectorId).Take(limit).ToList();
                    }
                    if (!await sqlLease.IsReadableAsync(cancellationToken)) throw new PublicationSnapshotConflictException("The captured manifest is unreadable.");
                    return top;

                    async ValueTask<IReadOnlyList<AnnMatch>> SearchOneAsync(int index)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var item = selected[index];
                        // At most two transient membership sets and two result buffers exist per window.
                        var eligible = item.CreateMembership();
                        var started = Stopwatch.GetTimestamp();
                        var entry = borrowed.Members[index];
                        var matches = candidatePool ? await entry.SearchCandidatePoolAsync(query, limit, eligible, cancellationToken)
                            : await entry.SearchAsync(query, limit, eligible, cancellationToken);
                        ReportSegment(item.Segment.Id, item.KeyCount, matches.Count, item.SelectionMilliseconds,
                            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        return matches;
                    }
                }
                catch (Exception failure) { operationFailure = failure; throw; }
                finally { await borrowed.DisposeAfterAsync(operationFailure); }
            }
            catch (Exception exception)
            {
                NotifySegmentFailure(signal, Generation.Id, exception);
                throw;
            }
            finally { _gate.Release(); }
        }

        private static void ReportSegment(string id, int keys, int matches, double selectionMs, double searchMs)
        {
            if (!HybridSearchDiagnostics.Log.IsEnabled()) return;
            var correlation = HybridSearchDiagnostics.CurrentCorrelation;
            HybridSearchDiagnostics.Log.ScopedAnnSegment(correlation.TraceId, correlation.SpanId, id, keys, matches, selectionMs, searchMs);
        }
        public async ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit, CancellationToken cancellationToken)
        {
            if (query.Count != Generation.Dimensions || query.Any(value => !float.IsFinite(value)) || !query.Any(value => value != 0))
                throw new ArgumentException("corpus-query-vector-invalid");
            if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!await sqlLease.IsCurrentAsync(cancellationToken)) throw new PublicationSnapshotConflictException("The captured manifest changed.");
                var top = new List<AnnMatch>();
                foreach (var segment in segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // One borrowed native segment per query at a time; a large manifest cannot pin the whole corpus in RAM.
                    await using var entry = await cache.BorrowAsync(segment, cancellationToken);
                    top.AddRange(await entry.SearchAsync(query, limit, cancellationToken));
                    // Merge continuously: memory is bounded by top-k plus one segment's candidates.
                    top = top.OrderBy(value => value.Distance).ThenBy(value => value.VectorId).Take(limit).ToList();
                }
                if (!await sqlLease.IsCurrentAsync(cancellationToken)) throw new PublicationSnapshotConflictException("The corpus changed during ANN retrieval.");
                return top;
            }
            catch (Exception exception)
            {
                NotifySegmentFailure(signal, Generation.Id, exception);
                throw;
            }
            finally { _gate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed) return;
                // Search borrows are already released. Idle native handles retain their separate durable cache owners.
                _disposed = true;
                await sqlLease.DisposeAsync();
            }
            finally { _gate.Release(); }
        }
    }

    private static void NotifySegmentFailure(IDerivedIndexRecoverySignal? signal, Guid generationId, Exception exception)
    {
        if (signal is null) return;
        IEnumerable<Exception> causes = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [exception];
        var categories = causes.Select(cause => cause switch
            {
                FileNotFoundException or DirectoryNotFoundException => DerivedIndexRecoveryFailureCategory.MissingDerivedIndex,
                UnauthorizedAccessException => DerivedIndexRecoveryFailureCategory.PermissionsDenied,
                IndexGenerationValidationException => DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex,
                IOException => DerivedIndexRecoveryFailureCategory.TransientIo,
                _ => (DerivedIndexRecoveryFailureCategory?)null
            }).Where(category => category.HasValue).Select(category => category!.Value).Distinct()
            // The coordinator admits the first fault. An operator-action failure
            // must not be hidden by a retryable search or cleanup error.
            .OrderBy(category => category == DerivedIndexRecoveryFailureCategory.PermissionsDenied ? 0 :
                category == DerivedIndexRecoveryFailureCategory.TransientIo ? 2 : 1);
        foreach (var category in categories) signal.Notify(new(category, generationId));
    }

    private sealed class NativeLease(ICorpusGenerationLease sqlLease, USearchIndex index) : ICorpusAnnLease
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private USearchIndex? _index = index;
        public Guid LeaseId => sqlLease.LeaseId;
        public IndexGenerationDescriptor Generation => sqlLease.Generation;

        public ValueTask<bool> IsCurrentAsync(CancellationToken cancellationToken) => sqlLease.IsCurrentAsync(cancellationToken);

        public async ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (query.Count != Generation.Dimensions || query.Any(value => !float.IsFinite(value)) || !query.Any(value => value != 0))
                throw new ArgumentException("corpus-query-vector-invalid");
            if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_index is null, this);
                if (!await sqlLease.IsCurrentAsync(cancellationToken).ConfigureAwait(false))
                    throw new PublicationSnapshotConflictException("The captured generation changed before ANN retrieval.");
                cancellationToken.ThrowIfCancellationRequested();
                var count = _index.Search(query.ToArray(), limit, out var keys, out var distances);
                cancellationToken.ThrowIfCancellationRequested();
                if (distances.Take(count).Any(value => !float.IsFinite(value)))
                    throw new IndexGenerationValidationException("ANN retrieval returned non-finite distances.");
                return Enumerable.Range(0, count).Select(position => new AnnMatch(checked((long)keys[position]), distances[position])).ToArray();
            }
            finally { _gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Never permit file cleanup before native disposal has returned successfully.
                _index?.Dispose();
                _index = null;
                await sqlLease.DisposeAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
    }
}
