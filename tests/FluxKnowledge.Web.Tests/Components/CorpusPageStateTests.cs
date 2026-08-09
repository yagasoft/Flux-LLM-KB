using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Web.Components.Corpus;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class CorpusPageStateTests
{
    [Fact]
    public async Task Corpus_filter_change_clears_the_previous_cursor_before_reloading()
    {
        var reader = new SequencedCorpusReader();
        await using var state = new CorpusPageState(reader);
        await state.LoadAsync(CancellationToken.None);

        await state.ChangeRootAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(state.Query.Cursor);
        Assert.Equal(2, reader.PageReadCount);
    }

    [Fact]
    public async Task Corpus_reloads_SQL_projection_for_reconnect_and_ignores_unrelated_status()
    {
        var reader = new SequencedCorpusReader();
        await using var state = new CorpusPageState(reader);
        await state.LoadAsync(CancellationToken.None);
        await state.HandleStatusChangedAsync(new StatusChanged(null, "unrelated", DateTimeOffset.UtcNow), CancellationToken.None);
        await state.HandleStatusChangedAsync(new StatusChanged(null, "reconnect", DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(2, reader.PageReadCount);
    }

    [Fact]
    public async Task Corpus_discards_a_stale_load_when_a_newer_filter_load_completes_first()
    {
        var reader = new BlockingCorpusReader();
        await using var state = new CorpusPageState(reader);
        var first = state.LoadAsync(CancellationToken.None).AsTask();
        await reader.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var root = Guid.NewGuid();
        var second = state.ChangeRootAsync(root, CancellationToken.None).AsTask();
        await reader.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.CompleteSecond();
        await second;
        reader.CompleteFirst();
        await first;

        Assert.Equal(root, state.Query.Filters.SourceRootId);
        Assert.Equal("current", Assert.Single(state.Page.Items).Entry);
    }

    [Fact]
    public async Task Corpus_filter_change_preserves_every_approved_filter_and_clears_cursor()
    {
        var reader = new SequencedCorpusReader();
        await using var state = new CorpusPageState(reader);
        await state.ChangeFiltersAsync(new CorpusFilters("needle", "local file", Guid.NewGuid(), "folder", "acceptedutf8text", "Publish", "deferred", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1)), true, CancellationToken.None);

        Assert.Null(state.Query.Cursor);
        Assert.True(state.Query.IncludeHistorical);
        Assert.Equal("needle", state.Query.Filters.Search);
        Assert.Equal("local file", state.Query.Filters.SourceKind);
        Assert.Equal("folder", state.Query.Filters.Folder);
        Assert.Equal("acceptedutf8text", state.Query.Filters.SourceClassification);
        Assert.Equal("publish", state.Query.Filters.PipelineStatus);
        Assert.Equal("deferred", state.Query.Filters.SourceActivityStatus);
    }

    [Fact]
    public async Task Corpus_disposal_is_idempotent_when_the_component_and_scope_both_release_state()
    {
        var state = new CorpusPageState(new SequencedCorpusReader());

        await state.DisposeAsync();
        await state.DisposeAsync();
    }

    private sealed class SequencedCorpusReader : ICorpusProjectionReader
    {
        public int PageReadCount { get; private set; }
        public ValueTask<CorpusPage> ReadPageAsync(CorpusQuery query, CancellationToken cancellationToken)
        {
            PageReadCount++;
            return ValueTask.FromResult(new CorpusPage([], null));
        }
        public ValueTask<IReadOnlyList<CorpusFolder>> ReadFoldersAsync(Guid sourceRootId, string? folder, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CorpusFolder>>([]);
        public ValueTask<CorpusEntryDetail?> ReadDetailAsync(Guid pipelineRecordId, CancellationToken cancellationToken) => ValueTask.FromResult<CorpusEntryDetail?>(null);
    }

    private sealed class BlockingCorpusReader : ICorpusProjectionReader
    {
        private readonly TaskCompletionSource _firstMayComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondMayComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void CompleteFirst() => _firstMayComplete.SetResult();
        public void CompleteSecond() => _secondMayComplete.SetResult();
        public async ValueTask<CorpusPage> ReadPageAsync(CorpusQuery query, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1) { FirstStarted.SetResult(); await _firstMayComplete.Task.WaitAsync(cancellationToken); return Page("stale"); }
            SecondStarted.SetResult(); await _secondMayComplete.Task.WaitAsync(cancellationToken); return Page("current");
        }
        public ValueTask<IReadOnlyList<CorpusFolder>> ReadFoldersAsync(Guid sourceRootId, string? folder, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CorpusFolder>>([]);
        public ValueTask<CorpusEntryDetail?> ReadDetailAsync(Guid pipelineRecordId, CancellationToken cancellationToken) => ValueTask.FromResult<CorpusEntryDetail?>(null);
        private static CorpusPage Page(string entry) => new([new CorpusEntry(Guid.NewGuid(), entry, "local file", null, "Direct", "Published", "Indexed", DateTimeOffset.UnixEpoch, null, null, null)], null);
    }
}
