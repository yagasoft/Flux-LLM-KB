using System.Diagnostics.Tracing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class HybridPassageRetrievalTests
{
    [Fact]
    public async Task Normal_expanded_diagnostics_distinguish_candidate_admission_ranking_and_final_withdrawal()
    {
        var reader = new QualityReader
        {
            Lexical = [Passage(1, "query lexical")], Dense = [Passage(2, "related baseline")],
            Pool = [Passage(3, "related extra")], StaleAfterFirstReadId = 3
        };
        var ranker = new ExpandedRanker(); ranker.Release.TrySetResult();
        var trace = Guid.NewGuid().ToString("N"); using var events = new ExpandedCandidates(trace);
        var adapter = new ExpandedPassageRetrieval(Engine(reader, new Models()), ranker, new Models(),
            () => DateTimeOffset.UtcNow, () => throw new InvalidOperationException("unexpected-stop"));
        var result = await HybridSearchDiagnostics.WithRequestCorrelationAsync(trace, "test", () => adapter.SearchAsync(QualityRequest(), default));

        Assert.Equal("ready", result.SemanticStatus);
        Assert.Equal([1L], events.Ids("lexical"));
        Assert.Equal([2L], events.Ids("dense-baseline"));
        Assert.Equal([3L], events.Ids("dense-pool"));
        Assert.Equal([1L, 2, 3], events.Ids("disclosure-planned"));
        Assert.Equal([1L, 2, 3], events.Ids("disclosure-approved"));
        Assert.Equal([1L, 2, 3], events.Ids("reranker-input"));
        Assert.Equal([1L, 3, 2], events.Ids("ranked"));
        Assert.Equal([1L, 2], result.Results.Select(p => p.ChunkId));
        var completed = Assert.Single(events.Completed);
        Assert.Equal("ready", completed.Status);
        Assert.Equal(result.Results.Select(p => p.ChunkId), completed.Ids);
        Assert.All(events.Stages, e => Assert.Equal(completed.SearchId, e.SearchId));
    }

    [Fact]
    public async Task Normal_expanded_diagnostics_keep_the_complete_bounded_pool_and_only_emit_metadata()
    {
        var reader = new QualityReader
        {
            Lexical = Enumerable.Range(1, 100).Select(i => Passage(i, "query lexical " + i)).ToArray(),
            Dense = Enumerable.Range(1001, 100).Select(i => Passage(i, "related baseline " + i)).ToArray(),
            Pool = Enumerable.Range(2001, 400).Select(i => Passage(i, "related pool " + i)).ToArray()
        };
        var ranker = new ExpandedRanker(); ranker.Release.TrySetResult();
        var trace = Guid.NewGuid().ToString("N"); using var events = new ExpandedCandidates(trace);
        await HybridSearchDiagnostics.WithRequestCorrelationAsync(trace, "test", () =>
            Engine(reader, new Models()).SearchExpandedAsync(QualityRequest(), ranker, DateTimeOffset.UtcNow, default));
        Assert.Equal(Enumerable.Range(2001, 400).Select(i => (long)i), events.Ids("dense-pool"));
        Assert.Equal(250, events.Ids("disclosure-planned").Length);
        Assert.Equal(100, events.Ids("reranker-input").Length);
        Assert.All(events.Stages, e =>
        {
            Assert.InRange(e.Ids.Length, 0, 400);
            Assert.All(e.Ids, id => Assert.True(id > 0));
            Assert.Equal(new[] { "traceId", "spanId", "searchId", "stage", "passageIds" }, e.PayloadNames);
        });
    }

    [Fact]
    public async Task Normal_expanded_fallback_diagnostics_report_lexical_results_without_a_model_input()
    {
        var reader = new QualityReader { Lexical = [Passage(1, "query answer")] };
        var trace = Guid.NewGuid().ToString("N"); using var events = new ExpandedCandidates(trace);
        var result = await HybridSearchDiagnostics.WithRequestCorrelationAsync(trace, "test", () =>
            Engine(reader, new Models()).SearchExpandedAsync(QualityRequest(), new ExpandedRanker { Busy = true }, DateTimeOffset.UtcNow, default));
        Assert.Equal("lexical", result.Result.RetrievalMode);
        Assert.Equal([1L], events.Ids("lexical"));
        Assert.DoesNotContain(events.Stages, e => e.Stage is "reranker-input" or "ranked");
        var completed = Assert.Single(events.Completed);
        Assert.Equal("busy", completed.Status); Assert.Equal([1L], completed.Ids);
    }

    [Fact]
    public async Task Normal_expanded_diagnostics_never_report_returned_hits_after_uncertain_native_cleanup()
    {
        var reader = new QualityReader { Lexical = [Passage(1, "query answer")] };
        var ranker = new ExpandedRanker { DisposeFailure = new PassageInferenceOwnershipException() }; ranker.Release.TrySetResult();
        var trace = Guid.NewGuid().ToString("N"); using var events = new ExpandedCandidates(trace);
        await Assert.ThrowsAsync<PassageInferenceOwnershipException>(() => HybridSearchDiagnostics.WithRequestCorrelationAsync(trace, "test", () =>
            Engine(reader, new Models()).SearchExpandedAsync(QualityRequest(), ranker, DateTimeOffset.UtcNow, default)).AsTask());
        var completed = Assert.Single(events.Completed);
        Assert.Equal("ownership-unconfirmed", completed.Status); Assert.Empty(completed.Ids);
    }

    private sealed class ExpandedCandidates(string trace) : EventListener
    {
        private readonly List<CandidateStage> _stages = [];
        private readonly List<SearchCompleted> _completed = [];
        internal CandidateStage[] Stages { get { lock (_stages) return _stages.ToArray(); } }
        internal SearchCompleted[] Completed { get { lock (_stages) return _completed.ToArray(); } }
        internal long[] Ids(string stage) => Assert.Single(Stages, e => e.Stage == stage).Ids;
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name == "FluxKnowledge-HybridSearch") EnableEvents(source, EventLevel.Informational); }
        protected override void OnEventWritten(EventWrittenEventArgs item)
        {
            if (_stages is null || item.Payload is not { Count: >= 5 } p || p[0] as string != trace) return;
            lock (_stages)
            {
                if (item.EventId == 14)
                    _stages.Add(new((string)p[2]!, (string)p[3]!, Parse((string)p[4]!), item.PayloadNames!.ToArray()));
                if (item.EventId == 2)
                    _completed.Add(new((string)p[2]!, (string)p[3]!, Parse((string)p[4]!)));
            }
        }
        private static long[] Parse(string ids) => ids.Length == 0 ? [] : ids.Split(',').Select(long.Parse).ToArray();
    }
    private sealed record CandidateStage(string SearchId, string Stage, long[] Ids, string[] PayloadNames);
    private sealed record SearchCompleted(string SearchId, string Status, long[] Ids);
}
