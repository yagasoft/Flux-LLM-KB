using System.Threading;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class UsearchAnnIndex(IServiceScopeFactory scopeFactory) : IAnnIndex, IDisposable
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

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIndexGenerationStore>();
        var activeId = await store.GetActiveGenerationIdAsync(cancellationToken);
        if (activeId is null)
        {
            return [];
        }

        await EnsureOpenAsync(activeId.Value, store, cancellationToken);
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

    private async ValueTask EnsureOpenAsync(
        Guid activeId,
        IIndexGenerationStore store,
        CancellationToken cancellationToken)
    {
        _gate.EnterReadLock();
        try
        {
            if (_generationId == activeId && _index is not null)
            {
                return;
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        var generation = await store.GetGenerationAsync(activeId, cancellationToken)
            ?? throw new IndexGenerationValidationException("The active SQL index generation is missing.");
        var vectors = await store.ReadVectorsAsync(activeId, cancellationToken);
        new UsearchGenerationValidator().Validate(generation.IndexPath, generation, vectors);
        var opened = new USearchIndex(Path.Combine(generation.IndexPath, UsearchGenerationValidator.IndexFileName), false);
        _gate.EnterWriteLock();
        try
        {
            if (_generationId == activeId && _index is not null)
            {
                opened.Dispose();
                return;
            }

                _index?.Dispose();
                _index = opened;
                _generationId = activeId;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }
}
