using System.Security.Cryptography;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Infrastructure.Usearch;

/// <summary>Bounded verified native reuse. Idle entries retain durable owners, never a SQL session lock.</summary>
public sealed class UsearchSegmentCache(UsearchIndexOptions options, IIndexSegmentReadLeaseFactory paths,
    IIndexSegmentOwnerStore owners, TimeProvider clock, long maximumBytes = 256L * 1024 * 1024,
    TimeSpan? idleLifetime = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Guid _instance = Guid.NewGuid();
    private readonly TimeSpan _idleLifetime = idleLifetime ?? TimeSpan.FromSeconds(30);
    private bool _stopping;
    internal Action<string>? BeforeNativeDisposal { get; set; }

    public async ValueTask<BorrowedSegment> BorrowAsync(IndexSegmentDescriptor segment, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0 || _idleLifetime < TimeSpan.Zero) throw new InvalidOperationException("segment-cache-budget-invalid");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_entries.TryGetValue(segment.Id, out var cached))
            {
                if (cached.Descriptor != segment || cached.Disposing) throw new InvalidOperationException("segment-cache-proof-changed");
                if ((await owners.ReadRetiringAsync([segment.Id], cancellationToken)).Contains(segment.Id))
                {
                    cached.Disposing = true;
                    if (cached.Borrowers == 0) await DisposeEntryAsync(cached);
                    throw new InvalidOperationException("segment-retiring-or-proof-changed");
                }
                cached.Borrowers++;
                return new(this, cached);
            }
            var id = await owners.RegisterAsync(segment, _instance, cancellationToken);
            // Global queries scan every segment. Evicting residents for each miss would
            // reopen the entire working set on every query when it exceeds the budget.
            var retain = segment.FileLength <= maximumBytes - _entries.Values.Where(value => value.Retained).Sum(value => value.Descriptor.FileLength);
            var entry = new Entry(segment, id, clock.GetUtcNow()) { Borrowers = 1, Retained = retain };
            _entries.Add(segment.Id, entry); // Preserve uncertain opening/disposal work on failure.
            try
            {
                var expected = Path.Combine(options.RootPath, "segments", segment.Id);
                if (!string.Equals(expected, segment.DirectoryPath, StringComparison.OrdinalIgnoreCase) || segment.NativeFormat != UsearchSegmentBuilder.NativeFormat)
                    throw new IndexGenerationValidationException("segment-path-or-format-invalid");
                entry.Path = paths.Acquire(segment.DirectoryPath);
                var filePath = Path.Combine(segment.DirectoryPath, "index.usearch");
                using (var file = File.OpenRead(filePath))
                    if (file.Length != segment.FileLength || Convert.ToHexStringLower(SHA256.HashData(file)) != segment.FileChecksum)
                        throw new IndexGenerationValidationException("segment-file-proof-mismatch");
                entry.Index = new USearchIndex(filePath, false);
                if (entry.Index.Size() != (ulong)segment.VectorCount || entry.Index.Dimensions() != (ulong)segment.Dimensions)
                    throw new IndexGenerationValidationException("segment-native-profile-invalid");
                await owners.OpenedAsync(id, cancellationToken);
                return new(this, entry);
            }
            catch
            {
                entry.Borrowers = 0;
                await DisposeEntryAsync(entry); // If disposal/ack fails, the durable owner and cache entry remain.
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask SweepAsync(bool forceIdle, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var retiring = await owners.ReadRetiringAsync(_entries.Keys.ToArray(), cancellationToken);
            foreach (var entry in _entries.Values.Where(value => value.Borrowers == 0 &&
                (forceIdle || value.Disposing || retiring.Contains(value.Descriptor.Id) || clock.GetUtcNow() - value.LastUsed >= _idleLifetime))
                .OrderBy(value => value.LastUsed).ToArray()) await DisposeEntryAsync(entry);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask ReleaseAsync(Entry entry)
    {
        await _gate.WaitAsync();
        try
        {
            if (--entry.Borrowers < 0) throw new InvalidOperationException("segment-cache-borrower-underflow");
            entry.LastUsed = clock.GetUtcNow();
            if (entry.Borrowers == 0 && (_stopping || entry.Disposing || !entry.Retained))
                await DisposeEntryAsync(entry);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask DisposeEntryAsync(Entry entry)
    {
        if (entry.Borrowers != 0) throw new InvalidOperationException("segment-native-still-borrowed");
        entry.Disposing = true;
        if (entry.Index is not null)
        {
            BeforeNativeDisposal?.Invoke(entry.Descriptor.Id);
            entry.Index.Dispose();
            entry.Index = null;
        }
        entry.Path?.Dispose();
        entry.Path = null;
        // Do not use caller cancellation between native disposal and durable acknowledgement.
        await owners.ReleasedAfterDisposalAsync(entry.OwnerId, CancellationToken.None);
        _entries.Remove(entry.Descriptor.Id);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _stopping = true;
            foreach (var entry in _entries.Values.Where(value => value.Borrowers == 0).ToArray()) await DisposeEntryAsync(entry);
            // Active borrowers retain this cache and dispose their entry on release; no false native acknowledgement.
        }
        finally { _gate.Release(); }
    }

    internal sealed class Entry(IndexSegmentDescriptor descriptor, Guid ownerId, DateTimeOffset now)
    {
        public IndexSegmentDescriptor Descriptor { get; } = descriptor;
        public Guid OwnerId { get; } = ownerId;
        public Guid NativeInstanceId { get; } = Guid.NewGuid();
        public int Borrowers { get; set; }
        public DateTimeOffset LastUsed { get; set; } = now;
        public USearchIndex? Index { get; set; }
        public IDisposable? Path { get; set; }
        public bool Disposing { get; set; }
        public bool Retained { get; init; }
        public SemaphoreSlim SearchGate { get; } = new(1, 1);
    }

    public sealed class BorrowedSegment : IAsyncDisposable
    {
        private readonly UsearchSegmentCache _cache;
        private readonly Entry _entry;
        private int _disposed;
        internal BorrowedSegment(UsearchSegmentCache cache, Entry entry) { _cache = cache; _entry = entry; }
        public Guid NativeInstanceId => _entry.NativeInstanceId;
        public async ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit, CancellationToken cancellationToken)
        {
            if (query.Count != _entry.Descriptor.Dimensions || query.Any(value => !float.IsFinite(value)) || !query.Any(value => value != 0))
                throw new ArgumentException("corpus-query-vector-invalid");
            if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
            await _entry.SearchGate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var index = _entry.Index ?? throw new ObjectDisposedException(nameof(BorrowedSegment));
                var count = index.Search(query.ToArray(), limit, out var keys, out var distances);
                cancellationToken.ThrowIfCancellationRequested();
                if (distances.Take(count).Any(value => !float.IsFinite(value))) throw new IndexGenerationValidationException("segment-ann-output-invalid");
                return Enumerable.Range(0, count).Select(index => new AnnMatch(checked((long)keys[index]), distances[index])).ToArray();
            }
            finally { _entry.SearchGate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            await _entry.SearchGate.WaitAsync();
            try
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) await _cache.ReleaseAsync(_entry);
            }
            finally { _entry.SearchGate.Release(); }
        }
    }
}

public sealed class UsearchSegmentCacheMaintenance(UsearchSegmentCache cache, TimeProvider clock,
    ILogger<UsearchSegmentCacheMaintenance> logger, IServiceScopeFactory? scopes = null) : BackgroundService
{
    private string? _failure;
    private string? _storageFailure;
    private DateTimeOffset _nextStorageMaintenance;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            await RunOnceAsync(stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await cache.SweepAsync(forceIdle: false, stoppingToken);
            if (_failure is not null)
            {
                logger.LogInformation("Segment cache disposal and ownership acknowledgement recovered.");
                _failure = null;
            }
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            var failure = exception.GetType().FullName + ":" + exception.Message;
            if (_failure != failure)
            {
                logger.LogError(exception, "Segment cache disposal or ownership acknowledgement remains unresolved.");
                _failure = failure;
            }
            return;
        }
        if (scopes is null || clock.GetUtcNow() < _nextStorageMaintenance) return;
        _nextStorageMaintenance = clock.GetUtcNow().AddSeconds(30);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var maintenance = scope.ServiceProvider.GetService<IIndexProjectionMaintenance>();
            if (maintenance is null) return;
            var result = await maintenance.RunOnceAsync(stoppingToken);
            if (result.ReasonCode is not null || !result.WorkRemaining) ReportStorageIssue(result.ReasonCode);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            ReportStorageIssue(exception.GetType().Name + ":" + exception.Message);
        }
    }

    private void ReportStorageIssue(string? reason)
    {
        if (_storageFailure == reason) return;
        if (reason is null) logger.LogInformation("Segment storage maintenance observed a drained queue.");
        else logger.LogWarning("Segment storage maintenance is waiting: {ReasonCode}.", reason);
        _storageFailure = reason;
    }
}
