using FluxKnowledge.Application.Indexing;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

internal readonly record struct CanonicalBindingKey(Guid ArtifactId, Guid PipelineRecordId, long SourceRevision);
internal sealed record SegmentBindingGroups(IReadOnlyDictionary<CanonicalBindingKey, long[]> Groups,
    IReadOnlySet<CanonicalBindingKey> DeletedBindings, long EstimatedBytes);

/// <summary>
/// Bounded immutable SQL projection metadata. It never grants publication/scope eligibility;
/// each query supplies a fresh live binding set. Native files retain their separate owners.
/// </summary>
public sealed class CorpusAnnBindingCache(TimeProvider? timeProvider = null, long maximumBytes = 64L * 1024 * 1024,
    TimeSpan? idleLifetime = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _idle = idleLifetime ?? Timeout.InfiniteTimeSpan;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<(Guid Epoch, string Segment), Entry> _entries = [];
    internal long RetainedBytes { get; private set; }
    internal long MaximumRetainedBytes => maximumBytes;

    internal async ValueTask<SegmentBindingGroups> GetAsync(Guid epoch, IndexSegmentDescriptor descriptor,
        Func<CancellationToken, ValueTask<SegmentBindingGroups>> load, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (epoch == Guid.Empty || maximumBytes <= 0 || _idle < TimeSpan.Zero && _idle != Timeout.InfiniteTimeSpan)
            throw new InvalidOperationException("segment-binding-cache-budget-invalid");
        if (TryGet(epoch, descriptor) is { } ready) return ready;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGet(epoch, descriptor) is { } found) return found;
            // Serial loading prevents duplicate complete segment captures. A cancelled
            // or failed load publishes nothing; a later caller can load independently.
            var value = await load(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (value.EstimatedBytes <= 0) throw new InvalidOperationException("segment-binding-cache-size-invalid");
            lock (_sync)
            {
                var now = _clock.GetUtcNow();
                foreach (var expired in _entries.Where(pair => Expired(pair.Value, now)).Select(pair => pair.Key).ToArray()) Remove(expired);
                if (value.EstimatedBytes <= maximumBytes)
                {
                    foreach (var evict in _entries.OrderBy(pair => pair.Value.LastUsed).Select(pair => pair.Key).ToArray())
                    {
                        if (RetainedBytes <= maximumBytes - value.EstimatedBytes) break;
                        Remove(evict);
                    }
                    _entries.Add((epoch, descriptor.Id), new(descriptor, value, now));
                    RetainedBytes += value.EstimatedBytes;
                }
            }
            // An oversized entry remains bounded by one captured segment and is not retained.
            return value;
        }
        finally { _gate.Release(); }
    }

    // Readiness never waits for a loader. Only complete immutable entries are visible.
    internal SegmentBindingGroups? TryGet(Guid epoch, IndexSegmentDescriptor descriptor)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue((epoch, descriptor.Id), out var entry)) return null;
            if (entry.Descriptor != descriptor) throw new InvalidOperationException("segment-binding-cache-proof-changed");
            var now = _clock.GetUtcNow();
            if (Expired(entry, now)) { Remove((epoch, descriptor.Id)); return null; }
            entry.LastUsed = now;
            return entry.Value;
        }
    }

    private bool Expired(Entry entry, DateTimeOffset now) => _idle != Timeout.InfiniteTimeSpan && now - entry.LastUsed >= _idle;

    private void Remove((Guid Epoch, string Segment) key)
    { if (_entries.Remove(key, out var entry)) RetainedBytes -= entry.Value.EstimatedBytes; }
    private sealed class Entry(IndexSegmentDescriptor descriptor, SegmentBindingGroups value, DateTimeOffset now)
    {
        internal IndexSegmentDescriptor Descriptor { get; } = descriptor;
        internal SegmentBindingGroups Value { get; } = value;
        internal DateTimeOffset LastUsed { get; set; } = now;
    }
}
