using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Web.Components.Events;
using FluxKnowledge.Web.Components.Status;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class EventsPageStateTests
{
    [Fact]
    public async Task Events_reloads_from_SQL_on_reconnect_even_when_live_tail_is_paused()
    {
        var reader = new SequencedEventReader();
        await using var state = new EventsPageState(reader);
        await state.LoadAsync(CancellationToken.None);
        state.ToggleLiveTail();
        await state.HandleStatusChangedAsync(new StatusChanged(null, "reconnect", DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task Events_pause_ignores_live_hints_and_dispose_releases_subscription()
    {
        var reader = new SequencedEventReader();
        var feed = new StatusEventFeed();
        var state = new EventsPageState(reader);
        await state.SubscribeAndLoadAsync(feed, CancellationToken.None);
        state.ToggleLiveTail();
        await state.HandleStatusChangedAsync(new StatusChanged(null, "events", DateTimeOffset.UtcNow), CancellationToken.None);
        await state.DisposeAsync();
        await feed.PublishAsync(new StatusChanged(null, "events", DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task Events_filter_change_clears_cursor_and_retains_all_filter_dimensions()
    {
        var reader = new SequencedEventReader();
        await using var state = new EventsPageState(reader);
        await state.ChangeFiltersAsync(new OperatorEventFilters("source", "warning", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1)), CancellationToken.None);

        Assert.Null(state.Query.Cursor);
        Assert.Equal("source", state.Query.Filters.Family);
        Assert.Equal("warning", state.Query.Filters.Severity);
        Assert.Equal("correlation", state.Query.Filters.CorrelationId);
        Assert.NotNull(state.Query.Filters.SourceRootId);
        Assert.NotNull(state.Query.Filters.PipelineRecordId);
        Assert.NotNull(state.Query.Filters.SourceRevisionId);
    }

    [Fact]
    public async Task Events_discards_a_stale_load()
    {
        var reader = new BlockingEventReader();
        await using var state = new EventsPageState(reader);
        var first = state.LoadAsync(CancellationToken.None).AsTask(); await reader.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = state.ChangeCorrelationAsync("current", CancellationToken.None).AsTask(); await reader.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.CompleteSecond(); await second; reader.CompleteFirst(); await first;

        Assert.Equal("current", Assert.Single(state.Page.Items).CorrelationId);
    }

    private sealed class SequencedEventReader : IOperatorEventProjectionReader
    {
        public int ReadCount { get; private set; }
        public ValueTask<OperatorEventPage> ReadPageAsync(OperatorEventQuery query, CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(new OperatorEventPage([], null));
        }
    }

    private sealed class BlockingEventReader : IOperatorEventProjectionReader
    {
        private readonly TaskCompletionSource _firstMayComplete = new(TaskCreationOptions.RunContinuationsAsynchronously); private readonly TaskCompletionSource _secondMayComplete = new(TaskCreationOptions.RunContinuationsAsynchronously); private int _calls;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void CompleteFirst() => _firstMayComplete.SetResult(); public void CompleteSecond() => _secondMayComplete.SetResult();
        public async ValueTask<OperatorEventPage> ReadPageAsync(OperatorEventQuery query, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1) { FirstStarted.SetResult(); await _firstMayComplete.Task.WaitAsync(cancellationToken); return Page("stale"); }
            SecondStarted.SetResult(); await _secondMayComplete.Task.WaitAsync(cancellationToken); return Page("current");
        }
        private static OperatorEventPage Page(string correlation) => new([new OperatorEventEntry(1, DateTimeOffset.UnixEpoch, "source.added", "source", "information", "source.added", null, null, null, null, null, correlation, "{}")], null);
    }
}
