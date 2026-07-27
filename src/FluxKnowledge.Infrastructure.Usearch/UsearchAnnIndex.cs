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
    private int _disposed;

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

        ThrowIfDisposed();
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIndexGenerationStore>();
        while (true)
        {
            var activeId = await store.GetActiveGenerationIdAsync(cancellationToken);
            if (activeId is null)
            {
                return [];
            }

            if (!await EnsureOpenAsync(activeId.Value, store, cancellationToken))
            {
                continue;
            }
            if (await store.GetActiveGenerationIdAsync(cancellationToken) != activeId)
            {
                continue;
            }
            _gate.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                if (_index is null || _generationId != activeId)
                {
                    continue;
                }

                var count = _index.Search(query.ToArray(), limit, out var keys, out var distances);
                return Enumerable.Range(0, count).Select(index => new AnnMatch((long)keys[index], distances[index])).ToArray();
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _gate.EnterWriteLock();
        try
        {
            _index?.Dispose();
            _index = null;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private async ValueTask<bool> EnsureOpenAsync(
        Guid activeId,
        IIndexGenerationStore store,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _gate.EnterReadLock();
        try
        {
            if (_generationId == activeId && _index is not null)
            {
                return true;
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
        if (await store.GetActiveGenerationIdAsync(cancellationToken) != activeId)
        {
            opened.Dispose();
            return false;
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            opened.Dispose();
            ThrowIfDisposed();
        }
        _gate.EnterWriteLock();
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                opened.Dispose();
                ThrowIfDisposed();
            }
            if (_generationId == activeId && _index is not null)
            {
                opened.Dispose();
                return true;
            }

                _index?.Dispose();
                _index = opened;
                _generationId = activeId;
                return true;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(UsearchAnnIndex));
        }
    }
}
