using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using Xunit;
using System.Diagnostics.Tracing;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class HybridPassageRetrievalTests
{
    [Fact]
    public async Task Expanded_phase_evidence_distinguishes_pending_lexical_work_from_completed_dense_selection()
    {
        var reader=new QualityReader {BlockStage="lexical",Lexical=[Passage(1,"query answer")],Pool=[Passage(2,"related answer")]};
        var ranker=new ExpandedRanker();ranker.Release.TrySetResult();var trace=Guid.NewGuid().ToString("N");
        using var phases=new ExpandedPhases(trace);
        var task=HybridSearchDiagnostics.WithRequestCorrelationAsync(trace,"test",()=>Engine(reader,new Models()).SearchExpandedAsync(QualityRequest(),ranker,DateTimeOffset.UtcNow,default)).AsTask();
        try
        {
            await phases.Dense.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.DoesNotContain("lexical-retrieval",phases.Names);
            reader.ReleaseBlock.TrySetResult();var result=await task;
            Assert.Equal("ready",result.Result.SemanticStatus);
            foreach(var name in new[]{"lexical-retrieval","lexical-wait","lexical-terms","disclosure-preflight","payload-ready"})Assert.Contains(name,phases.Names);
        }
        finally{reader.ReleaseBlock.TrySetResult();await task;}
    }
    private sealed class ExpandedPhases(string trace):EventListener
    {
        private readonly List<string> _names=[];
        internal TaskCompletionSource Dense {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string[] Names {get{lock(_names)return _names.ToArray();}}
        protected override void OnEventSourceCreated(EventSource source)
        {if(source.Name=="FluxKnowledge-HybridSearch")EnableEvents(source,EventLevel.Informational);}
        protected override void OnEventWritten(EventWrittenEventArgs item)
        {
            if(item.EventId!=6 || item.Payload is not {Count:>=6} p || p[0] as string!=trace || p[4] as string!="completed" || _names is null)return;
            lock(_names)_names.Add((string)p[3]!);
            if(p[3] as string=="dense-selection")Dense.TrySetResult();
        }
    }
    [Fact]
    public async Task Normal_scoped_search_uses_cpu_query_embedding_and_the_shared_trained_cohort()
    {
        var reader = new QualityReader { Lexical = [Passage(1, "query answer")], Pool = [Passage(2, "related answer")] };
        var legacy = new Models { FailScheduling = true }; var cpu = new Models();
        var ranker = new ExpandedRanker(); ranker.Release.TrySetResult();
        var arrival = DateTimeOffset.UtcNow.AddMilliseconds(-5);
        var retrieval = new ExpandedPassageRetrieval(Engine(reader, legacy), ranker, cpu, () => arrival, () => throw new InvalidOperationException("unexpected-stop"));
        var result = await retrieval.SearchAsync(QualityRequest(), default);
        Assert.Equal("ready", result.SemanticStatus); Assert.Equal("hybrid", result.RetrievalMode);
        Assert.Equal(arrival, ranker.Arrival); Assert.Equal(1, cpu.EmbeddingCalls); Assert.Equal(0, legacy.EmbeddingCalls);
        Assert.Empty(cpu.Batches); Assert.Equal(1, ranker.Submissions);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("staging", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("literal", "workspace")]
    [InlineData("keyword", "all")]
    public async Task Normal_expansion_keeps_literal_and_all_scope_on_the_existing_path(string matchMode, string scope)
    {
        var reader = new QualityReader { Lexical = [Passage(1, "query answer")] };
        var ranker = new ExpandedRanker { Busy = true }; var legacy = new Models(); var cpu = new Models();
        var retrieval = new ExpandedPassageRetrieval(Engine(reader, legacy), ranker, cpu, () => DateTimeOffset.UtcNow, () => { });
        var request = QualityRequest() with { MatchMode = matchMode };
        if (scope == "all") request = request with { Scope = scope, Cwd = null, RootId = null };
        var result = await retrieval.SearchAsync(request, default);
        Assert.False(ranker.Reserved); Assert.Equal(0, cpu.EmbeddingCalls); Assert.Single(result.Results);
        if (matchMode == "literal") Assert.Equal("not-requested", result.SemanticStatus);
    }

    [Fact]
    public async Task Normal_expanded_busy_preserves_lexical_fallback()
    {
        var reader = new QualityReader { Lexical = [Passage(1, "query answer")] }; var cpu = new Models();
        var retrieval = new ExpandedPassageRetrieval(Engine(reader, new Models()), new ExpandedRanker { Busy = true }, cpu,
            () => DateTimeOffset.UtcNow, () => throw new InvalidOperationException("unexpected-stop"));
        var result = await retrieval.SearchAsync(QualityRequest(), default);
        Assert.Equal("lexical", result.RetrievalMode); Assert.Equal("busy", result.SemanticStatus);
        Assert.Equal(1, Assert.Single(result.Results).ChunkId); Assert.Null(result.IndexGeneration); Assert.Equal(0, cpu.EmbeddingCalls);
    }

    [Fact]
    public async Task Normal_expanded_uncertain_ownership_stops_the_host_and_never_returns_fallback()
    {
        var ranker = new ExpandedRanker { DisposeFailure = new PassageInferenceOwnershipException() }; ranker.Release.TrySetResult();
        var reader = new QualityReader { Lexical = [Passage(1, "query answer")] }; var stops = 0;
        var retrieval = new ExpandedPassageRetrieval(Engine(reader, new Models()), ranker, new Models(), () => DateTimeOffset.UtcNow, () => stops++);
        await Assert.ThrowsAsync<PassageInferenceOwnershipException>(() => retrieval.SearchAsync(QualityRequest(), default).AsTask());
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task Expanded_gpu_reserves_before_embedding_and_runs_dense_while_lexical_is_pending()
    {
        var reader = new QualityReader { BlockStage = "lexical", Lexical = [Passage(1,"query baseline")], Pool = [Passage(2,"additional meaning")] };
        var ranker = new ExpandedRanker();
        var models = new Models { OnEmbedding = () => Assert.True(ranker.Reserved) };
        var pending = Engine(reader,models).SearchExpandedAsync(QualityRequest(),ranker,DateTimeOffset.UtcNow,default).AsTask();
        try
        {
            await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1,reader.WindowCalls); Assert.Equal(1,models.EmbeddingCalls); Assert.Empty(models.Batches);
        }
        finally { reader.ReleaseBlock.TrySetResult(); ranker.Release.TrySetResult(); }
        var result = await pending;
        Assert.Equal("ready",result.Result.SemanticStatus); Assert.Equal(1,ranker.Submissions);
        Assert.Equal(1,reader.ExpandedDisclosureCalls); Assert.All(result.Result.Results,h => Assert.Contains("rerank:trained",h.Explanation));
    }

    [Fact]
    public async Task Expanded_gpu_busy_preserves_completed_lexical_results_and_never_runs_embedding()
    {
        var reader = new QualityReader { Lexical = [Passage(1,"query answer")] };
        var models = new Models();
        var result = await Engine(reader,models).SearchExpandedAsync(QualityRequest(),new ExpandedRanker { Busy = true },DateTimeOffset.UtcNow,default);
        Assert.Equal(1,Assert.Single(result.Result.Results).ChunkId);
        Assert.Equal("busy",result.Result.SemanticStatus); Assert.Equal("lexical",result.Result.RetrievalMode);
        Assert.Contains("semantic:busy",result.Result.Warnings); Assert.Null(result.Result.IndexGeneration);
        Assert.Equal(0,models.EmbeddingCalls);
    }

    [Fact]
    public async Task Expanded_gpu_cancellation_awaits_whole_request_settlement_before_releasing_corpus_lease()
    {
        var reader = new QualityReader { Lexical = [Passage(1,"query answer")] };
        var leases = new Leases(); var ranker = new ExpandedRanker(); using var cancel = new CancellationTokenSource();
        var pending = Engine(reader,new Models(),leases).SearchExpandedAsync(QualityRequest(),ranker,DateTimeOffset.UtcNow,cancel.Token).AsTask();
        await ranker.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancel.Cancel(); Assert.False(pending.IsCompleted); Assert.False(leases.Disposed);
        ranker.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(leases.Disposed); Assert.True(ranker.Closed);
    }

    [Fact]
    public async Task Expanded_gpu_final_publication_fence_removes_withdrawn_scored_passages()
    {
        var reader = new QualityReader { Lexical = [Passage(1,"query baseline")], Pool = [Passage(2,"query withdrawn")], StaleAfterFirstReadId = 2 };
        var ranker = new ExpandedRanker(); ranker.Release.TrySetResult();
        var result = await Engine(reader,new Models()).SearchExpandedAsync(QualityRequest(),ranker,DateTimeOffset.UtcNow,default);
        Assert.DoesNotContain(result.Result.Results,h => h.ChunkId == 2); Assert.Single(result.Result.Results);
    }

    private sealed class ExpandedRanker : IExpandedPassageRanker
    {
        public string RerankerFingerprint => "gpu-ranking";
        internal bool Busy, Reserved, Closed;
        internal Exception? DisposeFailure;
        internal string? RefusalStatus;
        internal Action? OnSubmission;
        internal int Submissions;
        internal DateTimeOffset? Arrival;
        internal readonly TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IExpandedPassageRankTicket Reserve(DateTimeOffset arrival,string traceId,string spanId,CancellationToken ct)
        {
            Arrival = arrival;
            if(Busy) throw new PassageRetrievalRefusalException("busy");
            Reserved=true; return new Ticket(this,ct);
        }
        private sealed class Ticket(ExpandedRanker owner,CancellationToken ct) : IExpandedPassageRankTicket
        {
            public Guid ClientId { get; } = Guid.NewGuid();
            public Guid CohortId { get; } = Guid.NewGuid();
            public async ValueTask<RerankResult> SubmitAsync(string query,IReadOnlyList<RerankPassage> pairs)
            {
                owner.Submissions++; owner.Submitted.TrySetResult(); await owner.Release.Task; ct.ThrowIfCancellationRequested();
                owner.OnSubmission?.Invoke();
                if(owner.RefusalStatus is { } refusal) throw new PassageRetrievalRefusalException(refusal);
                return new(pairs.Select(p => new RerankScore(p.PassageId,p.PassageId)).ToArray(),owner.RerankerFingerprint);
            }
            public async ValueTask DisposeAsync() { if(owner.Submissions>0) await owner.Release.Task; owner.Closed=true; if(owner.DisposeFailure is { } failure) throw failure; }
        }
    }

    [Fact]
    public async Task Expanded_gpu_dual_cleanup_failure_preserves_ownership_and_corpus_failures_without_returning_fallback()
    {
        var ownership = new PassageInferenceOwnershipException(); var corpusFailure = new IOException("synthetic-corpus-disposal");
        var ranker = new ExpandedRanker { DisposeFailure = ownership }; ranker.Release.TrySetResult();
        var reader = new QualityReader { Lexical = [Passage(1,"query answer")] };
        var error = await Assert.ThrowsAsync<PassageInferenceOwnershipException>(() => Engine(reader,new Models(),new Leases { DisposeFailure=corpusFailure })
            .SearchExpandedAsync(QualityRequest(),ranker,DateTimeOffset.UtcNow,default).AsTask());
        var failures = Assert.IsType<AggregateException>(error.InnerException).Flatten().InnerExceptions;
        Assert.Contains(ownership,failures); Assert.Contains(corpusFailure,failures);
    }

    [Fact]
    public async Task Expanded_gpu_fallback_retains_completed_phase_timings_and_payload_identity()
    {
        var clock=new ManualClock();var leases=new Leases();
        var reader=new QualityReader { Lexical=[Passage(1,"query answer")],OnWindow=() => clock.Advance(TimeSpan.FromMilliseconds(7)) };
        var models=new Models { OnEmbedding=() => clock.Advance(TimeSpan.FromMilliseconds(3)) };
        var ranker=new ExpandedRanker { RefusalStatus="timeout",OnSubmission=() => clock.Advance(TimeSpan.FromMilliseconds(11)) };ranker.Release.TrySetResult();
        var result=await Engine(reader,models,leases,clock).SearchExpandedAsync(QualityRequest(),ranker,DateTimeOffset.UtcNow,default);
        Assert.Equal("timeout",result.Result.SemanticStatus);Assert.Single(result.Result.Results);
        Assert.Equal(3,result.Timings.EmbeddingMilliseconds);Assert.Equal(7,result.Timings.DenseSelectionMilliseconds);
        Assert.Equal(11,result.Timings.RankingMilliseconds);
        Assert.Equal(10,result.PayloadReadyMilliseconds);Assert.Equal("rerank-wait",result.FailurePhase);
        Assert.Equal(leases.Generation.Id,result.CapturedGeneration);Assert.NotNull(result.ClientId);Assert.NotNull(result.CohortId);
    }

    [Fact]
    public async Task Expanded_gpu_successful_assembly_timing_includes_final_reads_and_fenced_validation()
    {
        var clock=new ManualClock();
        var reader=new QualityReader {Lexical=[Passage(1,"query answer")],OnContextRead=() => clock.Advance(TimeSpan.FromMilliseconds(5))};
        var ranker=new ExpandedRanker();ranker.Release.TrySetResult();
        var result=await Engine(reader,new Models(),clock:clock).SearchExpandedAsync(QualityRequest(),ranker,DateTimeOffset.UtcNow,default);
        Assert.Equal(10,result.Timings.AssemblyMilliseconds);
    }

    [Fact]
    public async Task Expanded_gpu_timeout_before_submission_reports_partial_phases_after_owned_cleanup()
    {
        var clock=new ManualClock();var leases=new Leases();
        var reader=new QualityReader {OnWindow=() => clock.Advance(TimeSpan.FromSeconds(30))};
        var ranker=new ExpandedRanker();ExpandedPipelineDiagnostic? observed=null;
        var failure=await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => Engine(reader,new Models(),leases,clock)
            .SearchExpandedAsync(QualityRequest(),ranker,DateTimeOffset.UtcNow,default,snapshot => observed=snapshot).AsTask());
        Assert.Equal("timeout",failure.Status);Assert.True(leases.Disposed);Assert.True(ranker.Closed);
        Assert.NotNull(observed);Assert.Equal(leases.Generation.Id,observed.CapturedGeneration);
        Assert.Equal("dense-selection",observed.Phase);Assert.Equal(30000,observed.Timings.DenseSelectionMilliseconds);
        Assert.Null(observed.PayloadReadyMilliseconds);Assert.Equal("timeout",observed.Outcome);Assert.Equal(0,ranker.Submissions);
    }
}
