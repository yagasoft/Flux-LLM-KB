using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Web.Components.Status;

namespace FluxKnowledge.Web.Components.Events;

/// <summary>Durable event projection with optional live refresh hints.</summary>
public sealed class EventsPageState(IOperatorEventProjectionReader reader) : IAsyncDisposable
{
    private StatusEventSubscription? _subscription;
    private long _loadGeneration;
    public OperatorEventQuery Query { get; private set; } = new();
    public OperatorEventPage Page { get; private set; } = new([], null);
    public bool LiveTailEnabled { get; private set; } = true;
    public string? Error { get; private set; }
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        try
        {
            var page = await reader.ReadPageAsync(Query, cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _loadGeneration)) return;
            Page = page;
            Error = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) when (generation == Volatile.Read(ref _loadGeneration)) { Error = "The Events projection could not be loaded."; }
    }
    public void ToggleLiveTail() => LiveTailEnabled = !LiveTailEnabled;
    public async ValueTask ChangeCorrelationAsync(string? correlationId, CancellationToken cancellationToken)
    {
        await ChangeFiltersAsync(Query.Filters with { CorrelationId = correlationId }, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask ChangeFiltersAsync(OperatorEventFilters filters, CancellationToken cancellationToken)
    {
        Query = new OperatorEventQuery(filters, Query.PageSize);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask NextPageAsync(CancellationToken cancellationToken)
    {
        if (Page.NextCursor is null) return;
        Query = new OperatorEventQuery(Query.Filters, Query.PageSize, Page.NextCursor);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask HandleStatusChangedAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusChanged);
        if (string.Equals(statusChanged.Projection, "reconnect", StringComparison.OrdinalIgnoreCase) ||
            (LiveTailEnabled && (string.Equals(statusChanged.Projection, "events", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(statusChanged.Projection, "sources", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(statusChanged.Projection, "pipeline", StringComparison.OrdinalIgnoreCase))))
            await LoadAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask SubscribeAndLoadAsync(StatusEventFeed feed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (_subscription is not null) await _subscription.DisposeAsync().ConfigureAwait(false);
        _subscription = feed.Subscribe();
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null) await _subscription.DisposeAsync().ConfigureAwait(false);
    }
}
