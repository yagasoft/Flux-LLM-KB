using Cloud.Unum.USearch;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

/// <summary>Opens exactly the generation captured before embedding; never follows a later pointer.</summary>
public sealed class UsearchCorpusAnnLeaseFactory(IIndexGenerationStore store, UsearchGenerationValidator validator) : ICorpusAnnLeaseFactory
{
    public async ValueTask<ICorpusAnnLease> OpenAsync(ICorpusGenerationLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
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
