using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Web.Components.Sources;
using FluxKnowledge.Web.Components.Status;

namespace FluxKnowledge.Web.Components.Corpus;

/// <summary>Owns the SQL-backed catalogue query; status events are refresh hints only.</summary>
public sealed class CorpusPageState(ICorpusProjectionReader reader, ISourceRootProjectionReader? sourceReader = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private StatusEventSubscription? _subscription;
    private long _loadGeneration;
    private int _disposed;

    public CorpusQuery Query { get; private set; } = new();
    public CorpusPage Page { get; private set; } = new([], null);
    public IReadOnlyList<CorpusFolder> Folders { get; private set; } = [];
    public IReadOnlyList<SourceRootListProjection> Roots { get; private set; } = [];
    public string? Error { get; private set; }

    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        try
        {
            var page = await reader.ReadPageAsync(Query, cancellationToken).ConfigureAwait(false);
            var folders = Query.Filters.SourceRootId is { } rootId
                ? await reader.ReadFoldersAsync(rootId, Query.Filters.Folder, cancellationToken).ConfigureAwait(false)
                : [];
            var roots = sourceReader is null ? Roots : await sourceReader.ReadRootsAsync(cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _loadGeneration)) return;
            Page = page;
            Folders = folders;
            Roots = roots;
            Error = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _loadGeneration)) Error = "The Corpus projection could not be loaded.";
            _ = exception;
        }
    }

    public ValueTask ChangeRootAsync(Guid? rootId, CancellationToken cancellationToken) => ChangeFiltersAsync(Query.Filters with { SourceRootId = rootId, Folder = null }, Query.IncludeHistorical, cancellationToken);
    public ValueTask ChangeFolderAsync(string? folder, CancellationToken cancellationToken) => ChangeFiltersAsync(Query.Filters with { Folder = folder }, Query.IncludeHistorical, cancellationToken);
    public ValueTask ChangeSearchAsync(string? search, CancellationToken cancellationToken) => ChangeFiltersAsync(Query.Filters with { Search = search }, Query.IncludeHistorical, cancellationToken);
    public ValueTask ChangeHistoryAsync(bool includeHistorical, CancellationToken cancellationToken)
    {
        Query = new CorpusQuery(Query.Filters, includeHistorical, Query.PageSize);
        return LoadAsync(cancellationToken);
    }
    public ValueTask NextPageAsync(CancellationToken cancellationToken)
    {
        if (Page.NextCursor is null) return ValueTask.CompletedTask;
        Query = new CorpusQuery(Query.Filters, Query.IncludeHistorical, Query.PageSize, Page.NextCursor);
        return LoadAsync(cancellationToken);
    }
    public async ValueTask HandleStatusChangedAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusChanged);
        if (statusChanged.Projection is "corpus" or "sources" or "pipeline" or "reconnect")
            await LoadAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask SubscribeAndLoadAsync(StatusEventFeed feed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (_subscription is not null) await _subscription.DisposeAsync().ConfigureAwait(false);
        _subscription = feed.Subscribe();
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask ChangeFiltersAsync(CorpusFilters filters, bool includeHistorical, CancellationToken cancellationToken)
    {
        Query = new CorpusQuery(filters, includeHistorical, Query.PageSize);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_subscription is not null) await _subscription.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
