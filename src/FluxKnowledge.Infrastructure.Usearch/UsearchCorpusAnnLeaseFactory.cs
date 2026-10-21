using Cloud.Unum.USearch;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;

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
            if (!await lease.IsCurrentAsync(cancellationToken)) throw new PublicationSnapshotConflictException("The captured manifest changed.");
            var manifest = await segmented.ReadManifestAsync(lease.Generation.Id, cancellationToken);
            if (manifest.Generation != lease.Generation) throw new IndexGenerationValidationException("manifest-sql-proof-mismatch");
            new UsearchManifestValidator(options, paths).Validate(manifest);
            if (!await lease.IsCurrentAsync(cancellationToken)) throw new PublicationSnapshotConflictException("The corpus changed while opening its manifest.");
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
        IReadOnlyList<IndexSegmentDescriptor> segments, UsearchSegmentCache cache, IDerivedIndexRecoverySignal? signal) : ICorpusAnnLease
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;
        public Guid LeaseId => sqlLease.LeaseId;
        public IndexGenerationDescriptor Generation => sqlLease.Generation;
        public ValueTask<bool> IsCurrentAsync(CancellationToken cancellationToken) => sqlLease.IsCurrentAsync(cancellationToken);
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
        var category = exception switch
        {
            FileNotFoundException or DirectoryNotFoundException => DerivedIndexRecoveryFailureCategory.MissingDerivedIndex,
            UnauthorizedAccessException => DerivedIndexRecoveryFailureCategory.PermissionsDenied,
            IndexGenerationValidationException => DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex,
            IOException => DerivedIndexRecoveryFailureCategory.TransientIo,
            _ => (DerivedIndexRecoveryFailureCategory?)null
        };
        if (category is { } value) signal?.Notify(new(value, generationId));
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
