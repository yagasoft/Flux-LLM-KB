using System.Security.Cryptography;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Infrastructure.Usearch;

/// <summary>Bounded verified native reuse. Idle entries retain durable owners, never a SQL session lock.</summary>
public sealed partial class UsearchSegmentCache(UsearchIndexOptions options, IIndexSegmentReadLeaseFactory paths,
    IIndexSegmentOwnerStore owners, TimeProvider clock, long maximumBytes = 8L * 1024 * 1024 * 1024,
    TimeSpan? idleLifetime = null, int searchExpansion = UsearchReadIndex.DefaultSearchExpansion) : IAsyncDisposable
{
    // Preserve the existing six-argument binary entry point for bundled callers.
    public UsearchSegmentCache(UsearchIndexOptions options, IIndexSegmentReadLeaseFactory paths,
        IIndexSegmentOwnerStore owners, TimeProvider clock, long maximumBytes, TimeSpan? idleLifetime)
        : this(options, paths, owners, clock, maximumBytes, idleLifetime, UsearchReadIndex.DefaultSearchExpansion) { }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IndexSegmentDescriptor> _prepared = new(StringComparer.Ordinal);
    private readonly Guid _instance = Guid.NewGuid();
    private readonly TimeSpan _idleLifetime = idleLifetime ?? Timeout.InfiniteTimeSpan;
    private volatile bool _stopping;
    internal Action<string>? BeforeNativeDisposal { get; set; }

    public async ValueTask<BorrowedSegment> BorrowAsync(IndexSegmentDescriptor segment, CancellationToken cancellationToken)
        => await BorrowAsync(segment, preparedOnly: false, cancellationToken);

    public bool IsPrepared(IndexSegmentDescriptor segment) => !_stopping &&
        _prepared.TryGetValue(segment.Id, out var descriptor) && descriptor == segment;

    internal ValueTask<BorrowedSegment> BorrowPreparedAsync(IndexSegmentDescriptor segment, CancellationToken cancellationToken) =>
        BorrowAsync(segment, preparedOnly: true, cancellationToken);

    /// <summary>Pressure eviction is limited to idle entries outside this captured working set.</summary>
    internal async ValueTask PrepareAsync(IReadOnlyList<IndexSegmentDescriptor> segments, CancellationToken cancellationToken)
    {
        if (segments.Sum(segment => segment.FileLength) > maximumBytes)
            throw new PassageRetrievalRefusalException("scope-capacity-exceeded");
        var required = segments.Select(segment => segment.Id).ToHashSet(StringComparer.Ordinal);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var missingBytes = segments.Where(segment => !IsPrepared(segment)).Sum(segment => segment.FileLength);
            foreach (var entry in _entries.Values.Where(value => value.Borrowers == 0 && !required.Contains(value.Descriptor.Id))
                .OrderBy(value => value.LastUsed).ToArray())
            {
                if (missingBytes <= maximumBytes - _entries.Values.Where(value => value.Retained).Sum(value => value.Descriptor.FileLength)) break;
                await DisposeEntryAsync(entry);
            }
        }
        finally { _gate.Release(); }
        foreach (var segment in segments)
        {
            await using var borrowed = await BorrowAsync(segment, cancellationToken);
        }
        if (!segments.All(IsPrepared)) throw new PassageRetrievalRefusalException("warming");
    }

    private async ValueTask<BorrowedSegment> BorrowAsync(IndexSegmentDescriptor segment, bool preparedOnly, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0 || _idleLifetime < TimeSpan.Zero && _idleLifetime != Timeout.InfiniteTimeSpan ||
            searchExpansion is < 1 or > UsearchReadIndex.MaximumSearchExpansion)
            throw new InvalidOperationException("segment-cache-budget-invalid");
        if (preparedOnly && !IsPrepared(segment)) throw new PassageRetrievalRefusalException("warming");
        var started = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_entries.TryGetValue(segment.Id, out var cached))
            {
                if (cached.Descriptor != segment || cached.Disposing) throw new InvalidOperationException("segment-cache-proof-changed");
                if ((await owners.ReadRetiringAsync([segment.Id], cancellationToken)).Contains(segment.Id))
                {
                    _prepared.TryRemove(segment.Id, out _);
                    cached.Disposing = true;
                    if (cached.Borrowers == 0) await DisposeEntryAsync(cached);
                    throw new InvalidOperationException("segment-retiring-or-proof-changed");
                }
                cached.Borrowers++;
                ReportView(segment, true, started);
                return new(this, cached);
            }
            if (preparedOnly) throw new PassageRetrievalRefusalException("warming");
            var id = await owners.RegisterAsync(segment, _instance, cancellationToken);
            // Global queries scan every segment. Evicting residents for each miss would
            // reopen the entire working set on every query when it exceeds the budget.
            var retain = segment.FileLength <= maximumBytes - _entries.Values.Where(value => value.Retained).Sum(value => value.Descriptor.FileLength);
            var entry = new Entry(segment, id, clock.GetUtcNow()) { Borrowers = 1, Retained = retain };
            _entries.Add(segment.Id, entry); // Preserve uncertain opening/disposal work on failure.
            try
            {
                var expected = Path.Combine(options.RootPath, "segments", segment.Id);
                if (!string.Equals(expected, segment.DirectoryPath, StringComparison.OrdinalIgnoreCase) ||
                    !UsearchSegmentBuilder.SupportsNativeFormat(segment.NativeFormat))
                    throw new IndexGenerationValidationException("segment-path-or-format-invalid");
                entry.Path = paths.Acquire(segment.DirectoryPath);
                var filePath = Path.Combine(segment.DirectoryPath, "index.usearch");
                using (var file = File.OpenRead(filePath))
                    if (file.Length != segment.FileLength || Convert.ToHexStringLower(SHA256.HashData(file)) != segment.FileChecksum)
                        throw new IndexGenerationValidationException("segment-file-proof-mismatch");
                // Attach ownership before view opening, so a failed native close cannot lose its guard/receipt.
                entry.Index = new UsearchReadIndex(segment.Dimensions, searchExpansion);
                entry.Index.OpenView(filePath, segment.VectorCount);
                await owners.OpenedAsync(id, cancellationToken);
                if (entry.Retained) _prepared[segment.Id] = segment;
                ReportView(segment, false, started);
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

    private static void ReportView(IndexSegmentDescriptor segment, bool reused, long started)
    {
        if (!HybridSearchDiagnostics.Log.IsEnabled()) return;
        var correlation = HybridSearchDiagnostics.CurrentCorrelation;
        HybridSearchDiagnostics.Log.SegmentView(correlation.TraceId, correlation.SpanId, segment.Id,
            segment.FileLength, reused, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public async ValueTask SweepAsync(bool forceIdle, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var retiring = await owners.ReadRetiringAsync(_entries.Keys.ToArray(), cancellationToken);
            foreach (var entry in _entries.Values.Where(value => value.Borrowers == 0 &&
                (forceIdle || value.Disposing || retiring.Contains(value.Descriptor.Id) ||
                 _idleLifetime != Timeout.InfiniteTimeSpan && clock.GetUtcNow() - value.LastUsed >= _idleLifetime))
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
        _prepared.TryRemove(entry.Descriptor.Id, out _);
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
        public UsearchReadIndex? Index { get; set; }
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
        public ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit, CancellationToken cancellationToken) =>
            SearchAsync(query, limit, null, cancellationToken);

        public ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit,
            IReadOnlySet<long>? eligible, CancellationToken cancellationToken) =>
            SearchCoreAsync(query, limit, eligible, false, cancellationToken);

        internal ValueTask<IReadOnlyList<AnnMatch>> SearchCandidatePoolAsync(IReadOnlyList<float> query, int limit,
            IReadOnlySet<long> eligible, CancellationToken cancellationToken) =>
            SearchCoreAsync(query, limit, eligible, true, cancellationToken);

        private async ValueTask<IReadOnlyList<AnnMatch>> SearchCoreAsync(IReadOnlyList<float> query, int limit,
            IReadOnlySet<long>? eligible, bool candidatePool, CancellationToken cancellationToken)
        {
            if (query.Count != _entry.Descriptor.Dimensions || query.Any(value => !float.IsFinite(value)) || !query.Any(value => value != 0))
                throw new ArgumentException("corpus-query-vector-invalid");
            if (limit < 1 || limit > (candidatePool ? IAnnCandidatePool.MaximumResults : 100)) throw new ArgumentOutOfRangeException(nameof(limit));
            var waitStarted = Stopwatch.GetTimestamp();
            try { await _entry.SearchGate.WaitAsync(cancellationToken); }
            finally { ReportPreparedPhase("ann-native-gate", waitStarted); }
            var searchStarted = Stopwatch.GetTimestamp();
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var index = _entry.Index ?? throw new ObjectDisposedException(nameof(BorrowedSegment));
                return candidatePool ? index.SearchCandidatePool(query, limit, eligible, cancellationToken)
                    : index.Search(query, limit, eligible, cancellationToken);
            }
            finally { _entry.SearchGate.Release(); ReportPreparedPhase("ann-native-search", searchStarted); }
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // Durable disposal acknowledgements need the still-live SQL/DI services.
        // Container disposal is too late to create another EF context.
        await cache.DisposeAsync();
    }

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
