using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Integrations.Files;

/// <summary>One pending hint per configured root; a single consumer opens persistence connections.</summary>
internal sealed class LocalSourceWatchSignalBuffer(SourceWatchCoordinator coordinator, ILogger logger)
{
    private readonly object _sync = new();
    private HashSet<SourceRootId> _roots = [];
    private readonly Dictionary<SourceRootId, SourceWatchSignal> _pending = [];

    public void ConfigureRoots(IEnumerable<SourceRootId> roots)
    {
        lock (_sync)
        {
            _roots = roots.ToHashSet();
            foreach (var id in _pending.Keys.Where(id => !_roots.Contains(id)).ToArray()) _pending.Remove(id);
        }
    }

    public void Signal(SourceWatchSignal signal)
    {
        lock (_sync)
        {
            if (!_roots.Contains(signal.RootId)) return;
            if (_pending.TryGetValue(signal.RootId, out var previous))
                signal = signal with
                {
                    Kind = previous.Kind == SourceWatchSignalKind.Overflow ? previous.Kind : signal.Kind,
                    ObservedAtUtc = previous.ObservedAtUtc > signal.ObservedAtUtc ? previous.ObservedAtUtc : signal.ObservedAtUtc
                };
            _pending[signal.RootId] = signal;
        }
    }

    // Only the hosted consumer calls FlushAsync. Signals arriving during I/O form the next batch.
    public async Task<bool> FlushAsync(CancellationToken cancellationToken)
    {
        SourceWatchSignal[] signals;
        lock (_sync) { signals = _pending.Values.ToArray(); _pending.Clear(); }
        for (var index = 0; index < signals.Length; index++)
        {
            try { await coordinator.RecordAsync(signals[index], cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Source-root watcher hints could not be persisted; coalesced rescan hints remain pending for retry.");
                // A failed/ambiguous write may already have committed. Reconciliation is idempotent;
                // retry a rescan hint, never a per-file operation, and retain unattempted roots too.
                foreach (var signal in signals.Skip(index)) Signal(signal with { Kind = SourceWatchSignalKind.Overflow });
                return false;
            }
        }
        return true;
    }
}
