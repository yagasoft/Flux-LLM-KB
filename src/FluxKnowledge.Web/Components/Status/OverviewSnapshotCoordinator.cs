using System.Collections.Immutable;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;

namespace FluxKnowledge.Web.Components.Status;

public sealed record OverviewSnapshot(OverviewProjection? Projection, string? RefreshIssue, DateTimeOffset? PublishedAtUtc = null);
public sealed record OverviewRefreshMetrics(long Attempts, long Completed, long Failed, long CacheHits, long Coalesced);

/// <summary>One bounded refresh per app process; caller cancellation only stops that caller waiting.</summary>
public sealed class OverviewSnapshotCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly StatusEventFeed _events;
    private readonly StatusEventSubscription _subscription;
    private readonly TimeProvider _clock;
    private readonly ILogger<OverviewSnapshotCoordinator>? _logger;
    private readonly IDeploymentValidationHold? _hold;
    private readonly CancellationTokenSource _stopping;
    private readonly Task _observe;
    private readonly Task _periodic;
    private OverviewSnapshot _current = new(null, null);
    private Task? _inFlight;
    private DateTimeOffset _lastStart = DateTimeOffset.MinValue;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private long _invalidationVersion, _publishedVersion;
    private DeploymentHoldAdmission? _lastObservedAdmission;
    private int _subscribers;
    private long _attempts, _completed, _failed, _cacheHits, _coalesced;

    public OverviewSnapshotCoordinator(IServiceScopeFactory scopes, StatusEventFeed events,
        TimeProvider? timeProvider = null, ILogger<OverviewSnapshotCoordinator>? logger = null,
        IHostApplicationLifetime? lifetime = null, IDeploymentValidationHold? hold = null)
    {
        _scopes = scopes;
        _events = events;
        _clock = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _hold = hold;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime?.ApplicationStopping ?? default);
        _subscription = events.Subscribe();
        _observe = ObserveAsync();
        _periodic = PeriodicAsync();
    }

    public OverviewSnapshot Current
    {
        get
        {
            OverviewSnapshot value;
            bool pending;
            lock (_sync) { value = _current; pending = _invalidationVersion != _publishedVersion; }
            if (value.Projection is { } projection && _hold is not null)
            {
                var admission = _hold.ReadAdmissionState();
                var mismatch = admission.IsHeld != projection.DeploymentHeld ||
                    admission.PermittedCorpusRebuildOperationId != projection.PermittedRebuildOperationId;
                bool changed;
                lock (_sync)
                {
                    changed = _lastObservedAdmission is null ? mismatch : _lastObservedAdmission != admission;
                    _lastObservedAdmission = admission;
                    if (changed) _invalidationVersion++;
                    pending = _invalidationVersion != _publishedVersion;
                }
                if (changed || mismatch) _ = RequestRefresh(force: true, automatic: true);
                if (mismatch)
                    return value with { RefreshIssue = "Deployment admission changed after this sample. Refreshing status." };
            }
            return pending && value.RefreshIssue is null
                ? value with { RefreshIssue = "Status changed after this sample. Refreshing status." } : value;
        }
    }

    public OverviewRefreshMetrics Metrics
    {
        get { lock (_sync) return new(_attempts, _completed, _failed, _cacheHits, _coalesced); }
    }

    public IDisposable Subscribe()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
            _subscribers++;
        }
        return new Subscription(this);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken, bool force = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RequestRefresh(force, automatic: false).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task RequestRefresh(bool force, bool automatic)
    {
        lock (_sync)
        {
            if (_stopping.IsCancellationRequested) return Task.CompletedTask;
            if (_inFlight is not null) { _coalesced++; return _inFlight; }
            if (automatic && (_subscribers == 0 || _clock.GetUtcNow() < _retryAfter)) return Task.CompletedTask;
            if (!force && _invalidationVersion == _publishedVersion && IsFresh(_current)) { _cacheHits++; return Task.CompletedTask; }
            // Queue outside the caller's scope and cancellation lifetime. Assignment happens before execution.
            _inFlight = Task.Run(() => RefreshCoreAsync(automatic));
            return _inFlight;
        }
    }

    private bool IsFresh(OverviewSnapshot value) => value.Projection is not null && value.RefreshIssue is null &&
        _clock.GetUtcNow() - SampleTime(value) < TimeSpan.FromSeconds(10);

    internal static DateTimeOffset SampleTime(OverviewSnapshot value) =>
        value.Projection?.Activity.ReadStartedAtUtc is { } started && started != default
            ? started : value.PublishedAtUtc ?? DateTimeOffset.MinValue;

    private async Task RefreshCoreAsync(bool automatic)
    {
        var succeeded = false;
        long attemptNumber = 0;
        DateTimeOffset attemptStarted = default;
        System.Diagnostics.Stopwatch? elapsed = null;
        long sampledVersion = 0;
        try
        {
            TimeSpan wait;
            lock (_sync)
            {
                wait = _lastStart + TimeSpan.FromSeconds(2) - _clock.GetUtcNow();
                if (automatic && wait < TimeSpan.FromMilliseconds(250)) wait = TimeSpan.FromMilliseconds(250);
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _clock, _stopping.Token).ConfigureAwait(false);
            lock (_sync) { _lastStart = attemptStarted = _clock.GetUtcNow(); sampledVersion = _invalidationVersion; attemptNumber = ++_attempts; }
            elapsed = System.Diagnostics.Stopwatch.StartNew();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            budget.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var reader = scope.ServiceProvider.GetRequiredService<IProjectionReader>();
                var next = await reader.ReadOverviewAsync(budget.Token).ConfigureAwait(false);
                budget.Token.ThrowIfCancellationRequested();
                if (!next.Activity.IsConsistent)
                    RecordFailure("Status changed during the read. Waiting for a consistent sample.");
                else
                {
                    // Records are immutable; detach mutable backing collections too.
                    next = next with { Activity = next.Activity with
                    {
                        Sources = next.Activity.Sources.ToImmutableArray(),
                        Jobs = next.Activity.Jobs.ToImmutableArray(),
                        GpuTasks = next.Activity.GpuTasks.ToImmutableArray()
                    } };
                    lock (_sync)
                    {
                        _current = new(next, next.RuntimeAdmissionChanged ? "Deployment admission changed during this sample. Refreshing status." : null, _clock.GetUtcNow());
                        _completed++;
                        _retryAfter = DateTimeOffset.MinValue;
                        _publishedVersion = sampledVersion;
                        if (next.RuntimeAdmissionChanged) _invalidationVersion++;
                    }
                    succeeded = true;
                }
            }
            catch (OperationCanceledException) when (!_stopping.IsCancellationRequested && budget.IsCancellationRequested)
            { RecordFailure("Status is busy. Waiting for a consistent sample."); }
            catch (OverviewReadBudgetExceededException)
            { RecordFailure("Status is busy. Waiting for a consistent sample."); }
            catch (Exception exception) when (!_stopping.IsCancellationRequested)
            {
                _logger?.LogWarning(exception, "Shared overview refresh failed.");
                RecordFailure("Status refresh failed.");
            }
            if (!_stopping.IsCancellationRequested)
                await _events.PublishAsync(new(null, "overview-snapshot", _clock.GetUtcNow()), _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        finally
        {
            bool again;
            lock (_sync)
            {
                _inFlight = null;
                again = succeeded && _invalidationVersion != _publishedVersion && _subscribers > 0 && !_stopping.IsCancellationRequested;
            }
            if (elapsed is not null)
                _logger?.LogInformation("Overview refresh attempt {Attempt}: startedUtc={StartedUtc:O}; published={Published}; durationMs={DurationMs}; automatic={Automatic}.",
                    attemptNumber, attemptStarted, succeeded, elapsed.Elapsed.TotalMilliseconds, automatic);
            if (again) _ = RequestRefresh(force: true, automatic: true);
        }
    }

    private void RecordFailure(string issue)
    {
        lock (_sync)
        {
            _current = _current with { RefreshIssue = issue };
            _failed++;
            _retryAfter = _clock.GetUtcNow() + TimeSpan.FromSeconds(10);
        }
    }

    private async Task ObserveAsync()
    {
        try
        {
            await foreach (var changed in _subscription.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                var connection = changed.Projection.Equals("reconnect", StringComparison.OrdinalIgnoreCase);
                var relevant = !connection && OverviewProjectionState.AffectsOverview(changed.Projection);
                while (_subscription.Reader.TryRead(out var pending))
                {
                    var hint = pending.Projection.Equals("reconnect", StringComparison.OrdinalIgnoreCase);
                    connection |= hint;
                    relevant |= !hint && OverviewProjectionState.AffectsOverview(pending.Projection);
                }
                if (!relevant && !connection) continue;
                if (relevant) { lock (_sync) _invalidationVersion++; }
                // Connection-up also occurs during hydration. It joins a read
                // or reuses a fresh sample; actual data changes still invalidate.
                _ = RequestRefresh(force: relevant, automatic: true);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task PeriodicAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
                _ = RequestRefresh(force: true, automatic: true);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _subscription.DisposeAsync().ConfigureAwait(false);
        Task? pending;
        lock (_sync) pending = _inFlight;
        await Task.WhenAll(_observe, _periodic, pending ?? Task.CompletedTask).ConfigureAwait(false);
        _stopping.Dispose();
    }

    private sealed class Subscription(OverviewSnapshotCoordinator owner) : IDisposable
    {
        private OverviewSnapshotCoordinator? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } current)
                lock (current._sync) current._subscribers--;
        }
    }
}
