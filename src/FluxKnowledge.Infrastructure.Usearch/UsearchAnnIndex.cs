using System.Threading;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Indexing;
using Microsoft.Extensions.DependencyInjection;

namespace FluxKnowledge.Infrastructure.Usearch;

public sealed class UsearchAnnIndex(
    IServiceScopeFactory scopeFactory,
    Func<string, USearchIndex>? indexOpener = null) : IAnnIndex, IDisposable
{
    private readonly ReaderWriterLockSlim _gate = new();
    private readonly Func<string, USearchIndex> _indexOpener = indexOpener ??
        (static path => new USearchIndex(path, false));
    private Guid? _generationId;
    private string? _indexPath;
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
        var validator = scope.ServiceProvider.GetService<UsearchGenerationValidator>() ?? new UsearchGenerationValidator();
        while (true)
        {
            var activeId = await store.GetActiveGenerationIdAsync(cancellationToken);
            if (activeId is null)
            {
                return [];
            }

            if (!await EnsureOpenAsync(activeId.Value, store, validator, cancellationToken))
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

                try
                {
                    var count = _index.Search(query.ToArray(), limit, out var keys, out var distances);
                    return Enumerable.Range(0, count).Select(index => new AnnMatch((long)keys[index], distances[index])).ToArray();
                }
                catch (Exception) when (_generationId is { } generationId)
                {
                    NotifyRecovery(DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex, generationId);
                    throw;
                }
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
        UsearchGenerationValidator validator,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var generation = await store.GetGenerationAsync(activeId, cancellationToken);
        if (generation is null)
        {
            NotifyRecovery(DerivedIndexRecoveryFailureCategory.MissingDerivedIndex, activeId);
            throw new IndexGenerationValidationException("The active SQL index generation is missing.");
        }
        _gate.EnterReadLock();
        try
        {
            if (_generationId == activeId && _index is not null &&
                string.Equals(_indexPath, generation.IndexPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }
        var vectors = await store.ReadVectorsAsync(activeId, cancellationToken);
        try
        {
            validator.Validate(generation.IndexPath, generation, vectors);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException or
            IndexGenerationValidationException or IOException or UnauthorizedAccessException)
        {
            NotifyRecovery(exception switch
            {
                DirectoryNotFoundException or FileNotFoundException => DerivedIndexRecoveryFailureCategory.MissingDerivedIndex,
                IOException => DerivedIndexRecoveryFailureCategory.TransientIo,
                UnauthorizedAccessException => DerivedIndexRecoveryFailureCategory.PermissionsDenied,
                _ => DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex
            }, activeId);
            throw;
        }
        USearchIndex opened;
        try
        {
            opened = _indexOpener(Path.Combine(generation.IndexPath, UsearchGenerationValidator.IndexFileName));
        }
        catch (Exception exception)
        {
            NotifyRecovery(exception switch
            {
                UnauthorizedAccessException => DerivedIndexRecoveryFailureCategory.PermissionsDenied,
                IOException => DerivedIndexRecoveryFailureCategory.TransientIo,
                _ => DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex
            }, activeId);
            throw;
        }
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
            if (_generationId == activeId && _index is not null &&
                string.Equals(_indexPath, generation.IndexPath, StringComparison.OrdinalIgnoreCase))
            {
                opened.Dispose();
                return true;
            }

                _index?.Dispose();
                _index = opened;
                _generationId = activeId;
                _indexPath = generation.IndexPath;
                return true;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private void NotifyRecovery(DerivedIndexRecoveryFailureCategory category, Guid activeId)
    {
        using var signalScope = scopeFactory.CreateScope();
        signalScope.ServiceProvider.GetService<IDerivedIndexRecoverySignal>()?.Notify(
            new DerivedIndexRecoveryFault(category, activeId));
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(UsearchAnnIndex));
        }
    }
}
