using System.Threading;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class UsearchAnnIndex(IIndexGenerationStore store) : IAnnIndex, IDisposable
{
    private readonly ReaderWriterLockSlim _gate = new();
    private Guid? _generationId;
    private USearchIndex? _index;

    public async ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(
        IReadOnlyList<float> query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (limit <= 0)
        {
            return [];
        }

        var activeId = await store.GetActiveGenerationIdAsync(cancellationToken);
        if (activeId is null)
        {
            return [];
        }

        EnsureOpen(activeId.Value, cancellationToken);
        _gate.EnterReadLock();
        try
        {
            if (_index is null || _generationId != activeId)
            {
                return [];
            }

            var count = _index.Search(query.ToArray(), limit, out var keys, out var distances);
            return Enumerable.Range(0, count).Select(index => new AnnMatch((long)keys[index], distances[index])).ToArray();
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public void Dispose()
    {
        _gate.EnterWriteLock();
        try
        {
            _index?.Dispose();
            _index = null;
        }
        finally
        {
            _gate.ExitWriteLock();
            _gate.Dispose();
        }
    }

    private void EnsureOpen(Guid activeId, CancellationToken cancellationToken)
    {
        _gate.EnterUpgradeableReadLock();
        try
        {
            if (_generationId == activeId && _index is not null)
            {
                return;
            }

            var generation = store.GetGenerationAsync(activeId, cancellationToken).AsTask().GetAwaiter().GetResult()
                ?? throw new IndexGenerationValidationException("The active SQL index generation is missing.");
            var vectors = store.ReadVectorsAsync(activeId, cancellationToken).AsTask().GetAwaiter().GetResult();
            new UsearchGenerationValidator().Validate(generation.IndexPath, generation, vectors);
            var opened = new USearchIndex(Path.Combine(generation.IndexPath, UsearchGenerationValidator.IndexFileName), false);
            _gate.EnterWriteLock();
            try
            {
                _index?.Dispose();
                _index = opened;
                _generationId = activeId;
            }
            finally
            {
                _gate.ExitWriteLock();
            }
        }
        finally
        {
            _gate.ExitUpgradeableReadLock();
        }
    }
}
