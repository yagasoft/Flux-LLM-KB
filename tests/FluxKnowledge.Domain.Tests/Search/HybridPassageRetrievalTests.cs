using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class HybridPassageRetrievalTests
{
    [Fact]
    public async Task Unreturnable_inputs_cannot_consume_the_fifty_group_rerank_budget()
    {
        var reader = new Reader
        {
            Dense = Enumerable.Range(1, 100).Select(id => Passage(id, "query distinct evidence " + id)).ToArray(),
            UncitableIds = Enumerable.Range(1, 60).Select(id => (long)id).ToHashSet()
        };
        var models = new Models();
        var result = await Engine(reader, models).SearchAsync("query", Scope, 2, default);
        Assert.Equal(40, models.Inputs.Count);
        Assert.All(models.Inputs, input => Assert.True(input.PassageId > 60));
        Assert.Equal(100, result.CandidateCount);
        Assert.Equal(new long[] { 61, 62 }, result.Results.Select(hit => hit.ChunkId));
    }

    [Fact]
    public async Task An_unreturnable_copy_cannot_suppress_a_citable_equivalent_before_reranking()
    {
        var reader = new Reader { Dense = [Passage(2, "query evidence"), Passage(8, "query evidence")], UncitableIds = [2] };
        var models = new Models();
        var result = await Engine(reader, models).SearchAsync("query", Scope, 1, default);
        Assert.Equal(8, Assert.Single(models.Inputs).PassageId);
        Assert.Equal(8, Assert.Single(result.Results).ChunkId);
        Assert.Equal(2, result.CandidateCount);
    }

    [Theory]
    [InlineData(null, "warming")]
    [InlineData("scope-capacity-exceeded", "scope-capacity-exceeded")]
    public async Task Absent_preparation_or_a_terminal_failure_returns_immediately_without_using_the_grace(string? failure, string expected)
    {
        var clock = new ManualClock(); var leases = new Leases { Prepared = false };
        var reader = new PreparingReader { Lexical = [Passage(8, "query evidence")], Leases = leases };
        var models = new Models();
        var signal = failure is null ? null : new PreparationSignal { Failure = failure };
        var engine = new HybridPassageRetrievalEngine(reader, reader, leases, leases, models, new Probe(), new Codec(),
            new LocalPrivateContentDisclosure(), clock, preparationSignal: signal);
        var result = await engine.SearchAsync("query", Scope, 1, default).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(expected, result.SemanticStatus);
        Assert.Equal(8, Assert.Single(result.Results).ChunkId);
        Assert.Equal(0, models.EmbeddingCalls);
        Assert.Equal(1, leases.DisposeCalls);
        Assert.Equal(DateTimeOffset.UnixEpoch, clock.GetUtcNow());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_small_preparation_finishes_on_the_original_readable_generation_within_the_grace(bool publicationAdvanced)
    {
        var clock = new ManualClock();
        var leases = new Leases { Prepared = false };
        var reader = new PreparingReader { Lexical = [Passage(8, "query evidence")], Leases = leases };
        var signal = new PreparationSignal(); var models = new Models();
        using var ready = clock.CreateTimer(_ => { leases.Prepared = true; leases.Current = !publicationAdvanced; leases.ReadableOverride = true; },
            null, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);
        var engine = new HybridPassageRetrievalEngine(reader, reader, leases, leases, models, new Probe(), new Codec(),
            new LocalPrivateContentDisclosure(), clock, preparationSignal: signal);
        var pending = engine.SearchAsync("query", Scope, 1, default).AsTask();
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ready", result.SemanticStatus);
        Assert.Equal(leases.Generation.Id, result.IndexGeneration);
        Assert.Equal(1, leases.AcquireCalls);
        Assert.Equal(1, models.EmbeddingCalls);
    }

    [Fact]
    public async Task Cold_preparation_grace_is_bounded_and_does_not_load_metadata_inline()
    {
        var clock = new ManualClock(); var leases = new Leases { Prepared = false };
        var reader = new PreparingReader { Lexical = [Passage(8, "query evidence")], Leases = leases };
        var models = new Models { FailEmbedding = true };
        var engine = new HybridPassageRetrievalEngine(reader, reader, leases, leases, models, new Probe(), new Codec(),
            new LocalPrivateContentDisclosure(), clock, preparationSignal: new PreparationSignal());
        var pending = engine.SearchAsync("query", Scope, 1, default).AsTask();
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("warming", result.SemanticStatus);
        Assert.Equal(8, Assert.Single(result.Results).ChunkId);
        Assert.Equal(0, models.EmbeddingCalls);
        Assert.Equal(1, leases.DisposeCalls);
    }

    [Fact]
    public async Task Preparation_grace_rejects_an_epoch_change_before_embedding()
    {
        var clock = new ManualClock(); var leases = new Leases { Prepared = false };
        var reader = new PreparingReader { Lexical = [Passage(8, "query evidence")], Leases = leases };
        var models = new Models();
        using var ready = clock.CreateTimer(_ => { leases.Prepared = true; leases.ReadableOverride = false; },
            null, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);
        var engine = new HybridPassageRetrievalEngine(reader, reader, leases, leases, models, new Probe(), new Codec(),
            new LocalPrivateContentDisclosure(), clock, preparationSignal: new PreparationSignal());
        var pending = engine.SearchAsync("query", Scope, 1, default).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("index-updating", result.SemanticStatus);
        Assert.Equal(0, models.EmbeddingCalls);
        Assert.Equal(1, leases.AcquireCalls);
    }

    [Fact]
    public async Task Caller_cancellation_during_preparation_grace_releases_the_owned_lease_without_embedding()
    {
        var clock = new ManualClock(); var leases = new Leases { Prepared = false };
        var reader = new PreparingReader { Lexical = [Passage(8, "query evidence")], Leases = leases };
        var models = new Models(); using var caller = new CancellationTokenSource();
        var engine = new HybridPassageRetrievalEngine(reader, reader, leases, leases, models, new Probe(), new Codec(),
            new LocalPrivateContentDisclosure(), clock, preparationSignal: new PreparationSignal());
        var pending = engine.SearchAsync("query", Scope, 1, caller.Token).AsTask();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await leases.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, models.EmbeddingCalls);
        Assert.Equal(1, leases.DisposeCalls);
    }

    [Fact]
    public async Task Withdrawal_of_a_group_representative_uses_an_equivalent_passages_own_current_citation()
    {
        var representative = Passage(2, "query equivalent evidence");
        var alternate = Passage(8, "query equivalent evidence");
        var reader = new Reader { Dense = [representative, alternate], StaleId = 2 };
        var response = await Engine(reader, new Models(), new Leases()).SearchAsync("query", Scope, 2, default);
        var hit = Assert.Single(response.Results);
        Assert.Equal(alternate.ChunkId, hit.ChunkId);
        Assert.Equal(alternate.PipelineRecordId, hit.PipelineRecordId);
        Assert.Equal(alternate.ChunkHash, hit.ChunkHash);
        Assert.Equal(alternate.SourceIdentity, hit.SourceIdentity);
        Assert.Equal(2, response.CandidateCount);
    }

    [Fact]
    public async Task Exact_copies_return_one_current_citation_without_repeated_answers()
    {
        var reader = new Reader { Dense = [Passage(2, "query evidence"), Passage(8, "query evidence")] };
        var result = await Engine(reader, new Models(), new Leases()).SearchAsync("query", Scope, 10, default);
        Assert.Equal(2, Assert.Single(result.Results).ChunkId);
    }

    [Fact]
    public async Task An_out_of_scope_copy_cannot_suppress_an_eligible_equivalent()
    {
        var root = Guid.NewGuid();
        var outside = Passage(2, "query evidence") with { RootId = root, SourceIdentity = @"C:\outside\copy.txt" };
        var inside = Passage(8, "query evidence") with { RootId = root, SourceIdentity = @"C:\scope\copy.txt" };
        var reader = new Reader { Dense = [outside, inside] };
        var result = await Engine(reader, new Models(), new Leases()).SearchAsync("query", new("workspace", [root], @"C:\scope"), 10, default);
        Assert.Equal(8, Assert.Single(result.Results).ChunkId);
    }

    [Fact]
    public async Task Withdrawing_every_equivalent_copy_returns_no_stale_citation()
    {
        var reader = new Reader { Dense = [Passage(2, "query evidence"), Passage(8, "query evidence")], StaleIds = new HashSet<long> { 2, 8 } };
        var result = await Engine(reader, new Models(), new Leases()).SearchAsync("query", Scope, 10, default);
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task A_document_limit_uses_an_equivalent_copy_from_its_own_available_document()
    {
        var first = Passage(1, "query first evidence");
        var second = Passage(2, "query second evidence") with { PipelineRecordId = first.PipelineRecordId, ArtifactId = first.ArtifactId, StartOffset = 100 };
        var primary = Passage(3, "query third evidence") with { PipelineRecordId = first.PipelineRecordId, ArtifactId = first.ArtifactId, StartOffset = 200 };
        var alternate = Passage(8, "query third evidence") with { SourceIdentity = @"C:\public\other.txt" };
        var reader = new Reader { Dense = [first, second, primary, alternate] };
        var result = await Engine(reader, new Models(), new Leases()).SearchAsync("query", Scope, 10, default);
        Assert.Equal(new long[] { 1, 2, 8 }, result.Results.Select(hit => hit.ChunkId));
        Assert.Equal(alternate.PipelineRecordId, result.Results[2].PipelineRecordId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rerank_only_timeout_preserves_both_channel_fusion_and_waits_for_actual_settlement(bool lateSuccess)
    {
        var reader = new Reader { Lexical = [Passage(8, "query lexical evidence")], Dense = [Passage(9, "query semantic evidence")] };
        var clock = new ManualClock(); var leases = new Leases();
        var models = new Models { PauseRanking = true, IgnoreRankingCancellation = lateSuccess,
            ReturnLateRankingSuccess = lateSuccess, ReverseRanking = true };
        var pending = Engine(reader, models, leases, clock).SearchAsync("query", Scope, 2, default).AsTask();
        await models.RankingStarted.Task;
        clock.Advance(TimeSpan.FromSeconds(3));
        if (lateSuccess)
        {
            Assert.False(pending.IsCompleted); Assert.False(leases.Disposed);
            models.ReleaseRanking.SetResult();
        }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ready", result.SemanticStatus); Assert.Equal("hybrid", result.RetrievalMode);
        Assert.Equal(new long[] { 8, 9 }, result.Results.Select(hit => hit.ChunkId));
        Assert.Contains("rerank:timeout", result.Warnings);
        Assert.All(result.Results, hit => Assert.DoesNotContain("rerank:trained", hit.Explanation));
        Assert.Equal(1, leases.DisposeCalls);
        // A later request uses a fresh rank budget, rather than retaining a terminated run.
        models.ReleaseRanking.TrySetResult();
        var next = await Engine(reader, models, new Leases(), clock).SearchAsync("query", Scope, 2, default);
        Assert.Equal("ready", next.SemanticStatus);
        Assert.Contains(next.Results, hit => hit.Explanation.Contains("rerank:trained"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parent_cancellation_wins_while_waiting_for_rerank_timeout_acknowledgement(bool callerCancellation)
    {
        var reader = new Reader { Lexical = [Passage(8, "query lexical evidence")], Dense = [Passage(9, "query semantic evidence")] };
        var clock = new ManualClock(); var leases = new Leases();
        var models = new Models { PauseRanking = true, IgnoreRankingCancellation = true, ReturnLateRankingSuccess = true };
        using var caller = new CancellationTokenSource();
        var pending = Engine(reader, models, leases, clock).SearchAsync("query", Scope, 2, caller.Token).AsTask();
        await models.RankingStarted.Task;
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(leases.Disposed);
        if (callerCancellation)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            var result = await pending;
            Assert.Equal("timeout", result.SemanticStatus);
            Assert.Equal("lexical", result.RetrievalMode);
            Assert.DoesNotContain("rerank:timeout", result.Warnings);
        }
        Assert.False(leases.Disposed);
        models.ReleaseRanking.SetResult();
        await leases.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, leases.DisposeCalls);
    }

    [Fact]
    public async Task A_cold_projection_returns_cited_lexical_fallback_before_embedding_or_preparation()
    {
        var reader = new PreparingReader { Lexical = [Passage(8, "query evidence")] };
        var leases = new Leases { Prepared = false }; reader.Leases = leases;
        var models = new Models { FailEmbedding = true };
        var signal = new PreparationSignal();
        var engine = new HybridPassageRetrievalEngine(reader, reader, leases, leases, models, new Probe(), new Codec(),
            new LocalPrivateContentDisclosure(), preparationSignal: signal);
        var response = await engine.SearchAsync("query", Scope, 1, CancellationToken.None);
        Assert.Equal("warming", response.SemanticStatus);
        Assert.Equal("lexical", response.RetrievalMode);
        Assert.Equal(8, Assert.Single(response.Results).ChunkId);
        Assert.Contains("semantic:warming", response.Warnings);
        Assert.Equal(0, models.EmbeddingCalls);
        Assert.Equal(0, reader.DenseCalls);
        Assert.True(signal.Requests > 0);
        Assert.Equal(1, leases.DisposeCalls);
    }

    [Theory]
    [InlineData("root", false)]
    [InlineData("workspace", false)]
    [InlineData("all", false)]
    [InlineData("root", true)]
    [InlineData("workspace", true)]
    [InlineData("all", true)]
    public async Task Every_scope_uses_ANN_and_releases_once_on_success_or_embedding_failure(
        string kind, bool failEmbedding)
    {
        var reader = new Reader();
        var leases = new Leases();
        var models = new Models { FailEmbedding = failEmbedding };
        var scope = new ResolvedCorpusScope(kind, kind == "all" ? [] : [Guid.NewGuid()], kind == "workspace" ? @"C:\scope" : null);
        var response = await Engine(reader, models, leases).SearchAsync("query", scope, 1, CancellationToken.None);
        Assert.Equal(failEmbedding ? "unavailable" : "ready", response.SemanticStatus);
        Assert.Equal(1, leases.OpenCalls);
        Assert.Equal(1, leases.DisposeCalls);
        Assert.Equal(failEmbedding ? 0 : 1, reader.DenseCalls);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("root")]
    public async Task ANN_open_failure_consumes_ownership_once_for_each_scope(string kind)
    {
        var reader = new Reader();
        var leases = new Leases { FailOpen = true };
        var scope = new ResolvedCorpusScope(kind, kind == "all" ? [] : [Guid.NewGuid()], null);
        var response = await Engine(reader, new Models(), leases).SearchAsync("query", scope, 1, CancellationToken.None);
        Assert.Equal("unavailable", response.SemanticStatus);
        Assert.Equal(1, leases.OpenCalls);
        Assert.Equal(1, leases.DisposeCalls);
    }

    [Fact]
    public async Task Cancelled_caller_acquires_no_lease_and_failed_scheduling_releases_its_bounded_lease()
    {
        var leases = new Leases();
        var reader = new Reader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Engine(reader, new Models(), leases).SearchAsync("query", Scope, 1, cancellation.Token));
        Assert.Equal(0, leases.AcquireCalls);
        var response = await Engine(reader, new Models { FailScheduling = true }, leases)
            .SearchAsync("query", Scope, 1, CancellationToken.None);
        Assert.Equal("timeout", response.SemanticStatus);
        Assert.Equal(1, leases.AcquireCalls);
        Assert.Equal(1, leases.OpenCalls);
        Assert.Equal(1, leases.DisposeCalls);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("timeout")]
    [InlineData("unavailable")]
    [InlineData("index-updating")]
    public async Task Empty_degraded_search_refuses_instead_of_reporting_a_healthy_empty_match(string status)
    {
        var reader = new Reader { DenseStatus = status };
        var service = new PassageSearchService(Engine(reader, new Models()));
        var refusal = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => service.SearchAsync(
            new("paraphrase", 2, "local_first", null, null, null), CancellationToken.None).AsTask());
        Assert.Equal(status, refusal.Status);
    }

    [Fact]
    public async Task Search_adapter_preserves_shared_passage_order_complete_bodies_counts_and_degradation()
    {
        var reader = new Reader { Dense = [Passage(8, "First complete meaning"), Passage(2, "Second complete meaning")] };
        var models = new Models { FailRanking = true };
        var engine = Engine(reader, models);
        var corpus = await engine.SearchAsync(new CorpusSearchRequest("paraphrase", 2, "all", null, null), CancellationToken.None);
        var search = await new PassageSearchService(engine).SearchAsync(new("paraphrase", 2, "local_first", null, null, null), CancellationToken.None);
        Assert.Equal(corpus.Results.Select(hit => hit.Passage), search.Results.Select(hit => hit.Snippet));
        Assert.Equal(corpus.Results.Select(hit => hit.PipelineRecordId), search.Results.Select(hit => hit.PipelineRecordId.Value));
        Assert.Equal(corpus.CandidateCount, search.CandidateCount);
        Assert.Equal(corpus.IndexGeneration!.Value.ToString("N"), search.ActiveIndexGeneration);
        Assert.Equal(new double[] { 1, 0.5 }, search.Results.Select(hit => hit.Score));
        Assert.All(search.Results, hit => Assert.Contains("rerank:unavailable", hit.Explanation));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("readiness")]
    [InlineData("lexical")]
    [InlineData("fallback-read")]
    public async Task Overall_deadline_stops_waiting_for_blocked_sql_including_scope_and_fallback(string stage)
    {
        var clock = new ManualClock();
        var reader = new Reader { Lexical = [Passage(8, "query answer")], BlockStage = stage };
        var models = new Models { FailScheduling = true };
        var engine = Engine(reader, models, clock: clock);
        var responseTask = engine.SearchAsync(new CorpusSearchRequest("query", 2, "all", null, null), CancellationToken.None).AsTask();
        await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(10));
        var refusal = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => responseTask.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("timeout", refusal.Status);
        Assert.True(refusal.Retryable);
        reader.ReleaseBlock.TrySetResult();
    }

    [Fact]
    public async Task Semantic_deadline_returns_fresh_lexical_evidence_while_blocked_native_work_retains_its_lease()
    {
        var clock = new ManualClock();
        var reader = new Reader { Lexical = [Passage(8, "query answer")], BlockStage = "dense" };
        var leases = new Leases();
        var models = new Models();
        var responseTask = Engine(reader, models, leases, clock).SearchAsync("query", Scope, 2, CancellationToken.None).AsTask();
        await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal("query answer", Assert.Single(response.Results).Passage);
            Assert.Equal("lexical", response.RetrievalMode);
            Assert.Equal("timeout", response.SemanticStatus);
            Assert.Contains("semantic:timeout", response.Warnings);
            Assert.False(leases.Disposed);
            Assert.Empty(models.Inputs);
        }
        finally { reader.ReleaseBlock.TrySetResult(); }
        await leases.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, leases.DisposeCalls);
    }

    [Fact]
    public async Task Semantic_cutoff_leaves_the_overall_budget_for_delayed_lexical_candidates()
    {
        var clock = new ManualClock();
        var reader = new Reader { Lexical = [Passage(8, "query answer")], BlockStage = "lexical" };
        var responseTask = Engine(reader, new Models(), clock: clock).SearchAsync("query", Scope, 2, CancellationToken.None).AsTask();
        await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(responseTask.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        reader.ReleaseBlock.TrySetResult();
        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("query answer", Assert.Single(response.Results).Passage);
        Assert.Equal("timeout", response.SemanticStatus);
    }

    [Fact]
    public async Task An_exhausted_overall_budget_is_a_retryable_refusal_instead_of_a_successful_empty_search()
    {
        var clock = new ManualClock();
        var reader = new Reader { BlockStage = "scope" };
        var responseTask = Engine(reader, new Models(), clock: clock)
            .SearchAsync(new CorpusSearchRequest("query", 2, "all", null, null), CancellationToken.None).AsTask();
        await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            var refusal = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(
                () => responseTask.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("timeout", refusal.Status);
            Assert.True(refusal.Retryable);
        }
        finally { reader.ReleaseBlock.TrySetResult(); }
    }

    [Fact]
    public async Task Explicit_twenty_five_second_deadline_extends_the_whole_search_without_changing_the_default()
    {
        var clock = new ManualClock();
        var reader = new Reader { BlockStage = "scope" };
        var leases = new Leases();
        var engine = new HybridPassageRetrievalEngine(reader, reader, leases, leases,
            new Models(), new Probe(), new Codec(), new LocalPrivateContentDisclosure(), clock,
            TimeSpan.FromSeconds(25));
        var responseTask = engine.SearchAsync(new CorpusSearchRequest("query", 2, "all", null, null), CancellationToken.None).AsTask();
        await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(responseTask.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(15));
        var refusal = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => responseTask.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("timeout", refusal.Status);
        reader.ReleaseBlock.TrySetResult();
    }

    [Fact]
    public async Task Dense_only_match_keeps_complete_body_identity_and_citation_with_no_lexical_anchor()
    {
        var reader = new Reader { Dense = [Passage(8, "Employees receive twenty days of annual leave.")] };
        var models = new Models(); var leases = new Leases();
        var response = await Engine(reader, models, leases).SearchAsync("holiday entitlement", Scope, 2, CancellationToken.None);
        var hit = Assert.Single(response.Results);
        Assert.Equal(reader.Dense[0].Content, hit.Passage);
        Assert.Equal(8, hit.ChunkId);
        Assert.Contains("semantic:dense", hit.Explanation);
        Assert.Contains("rerank:trained", hit.Explanation);
        Assert.Equal("hybrid", response.RetrievalMode);
        Assert.Equal("ready", response.SemanticStatus);
        Assert.Equal(leases.Generation.Id, response.IndexGeneration);
        Assert.Equal(new long[] { 8 }, models.Inputs.Select(value => value.PassageId));
        Assert.Equal(reader.Dense[0].Content, models.Inputs[0].SearchText);
        Assert.True(reader.ReadUnderLease);
        Assert.True(leases.Disposed);
    }

    [Fact]
    public async Task Failed_reranking_preserves_entire_fused_order_and_reports_degradation()
    {
        var reader = new Reader { Dense = [Passage(8, "First meaning"), Passage(2, "Second meaning")] };
        var models = new Models { FailRanking = true };
        var response = await Engine(reader, models).SearchAsync("paraphrase", Scope, 2, CancellationToken.None);
        Assert.Equal(new long[] { 8, 2 }, response.Results.Select(value => value.ChunkId));
        Assert.Contains("rerank:unavailable", response.Warnings);
        Assert.All(response.Results, value => Assert.DoesNotContain("rerank:trained", value.Explanation));
    }

    [Fact]
    public async Task Incomplete_ranking_output_discards_all_partial_scores()
    {
        var reader = new Reader { Dense = [Passage(8, "First"), Passage(2, "Second")] };
        var response = await Engine(reader, new Models { PartialRanking = true }).SearchAsync("query", Scope, 2, CancellationToken.None);
        Assert.Equal(new long[] { 8, 2 }, response.Results.Select(value => value.ChunkId));
        Assert.Contains("rerank:invalid-output", response.Warnings);
    }

    [Fact]
    public async Task Changed_generation_discards_semantic_results_before_return_and_releases_lease()
    {
        var reader = new Reader { Dense = [Passage(8, "Meaning")] }; var leases = new Leases { Current = false };
        var response = await Engine(reader, new Models(), leases).SearchAsync("query", Scope, 2, CancellationToken.None);
        Assert.Empty(response.Results);
        Assert.Equal("index-updating", response.SemanticStatus);
        Assert.Equal("lexical", response.RetrievalMode);
        Assert.True(leases.Disposed);
    }

    [Fact]
    public async Task Scoped_capacity_refusal_uses_lexical_results_with_explicit_status_and_no_reranking()
    {
        var reader = new Reader { Lexical = [Passage(8, "query value")], DenseStatus = "scope-capacity-exceeded" };
        var models = new Models();
        var response = await Engine(reader, models).SearchAsync("query", Scope, 2, CancellationToken.None);
        Assert.Single(response.Results);
        Assert.Equal("scope-capacity-exceeded", response.SemanticStatus);
        Assert.Empty(models.Inputs);
    }

    [Fact]
    public async Task Withheld_inputs_are_never_sent_to_reranking_or_returned()
    {
        var reader = new Reader { Dense = [Passage(8, "password=private-secret-sentinel"), Passage(2, "Public passage")] };
        var models = new Models();
        var response = await Engine(reader, models).SearchAsync("query", Scope, 2, CancellationToken.None);
        Assert.Equal(2, Assert.Single(response.Results).ChunkId);
        Assert.Equal(2, Assert.Single(models.Inputs).PassageId);
    }

    [Fact]
    public async Task A_passage_withdrawn_after_preflight_tops_up_only_from_already_scored_shortlist()
    {
        var reader = new Reader { Dense = [Passage(8, "First"), Passage(2, "Second")], StaleAfterFirstReadId = 8 };
        var models = new Models();
        var response = await Engine(reader, models).SearchAsync("query", Scope, 1, CancellationToken.None);
        Assert.Equal(2, Assert.Single(response.Results).ChunkId);
        Assert.Equal(2, models.Inputs.Count);
        Assert.Equal(1, reader.DenseCalls);
    }

    [Fact]
    public async Task Final_binding_validation_removes_a_passage_withdrawn_after_its_first_readback()
    {
        var reader = new Reader
        {
            Dense = [Passage(8, "First meaning"), Passage(2, "Second meaning")], StaleAfterFirstReadId = 8,
            StaleAfterReadCount = 2 // Preflight and final body read succeed; the publication fence observes withdrawal.
        };
        var response = await Engine(reader, new Models()).SearchAsync("paraphrase", Scope, 2, CancellationToken.None);
        Assert.Equal(2, Assert.Single(response.Results).ChunkId);
        Assert.Contains("publication-changed", response.Warnings);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("root")]
    [InlineData("workspace")]
    public async Task Timeout_keeps_native_or_SQL_lease_owned_until_background_callback_finishes(string kind)
    {
        var reader = new Reader { Dense = [Passage(8, "Meaning")] }; var leases = new Leases();
        var models = new Models { SimulateCallerTimeout = true };
        var clock = new ManualClock();
        var scope = new ResolvedCorpusScope(kind, kind == "all" ? [] : [Guid.NewGuid()], kind == "workspace" ? @"C:\scope" : null);
        var pending = Engine(reader, models, leases, clock).SearchAsync("query", scope, 1, CancellationToken.None).AsTask();
        await models.EmbeddingStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(5));
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("timeout", response.SemanticStatus);
        Assert.False(leases.Disposed);
        models.AllowNativeCompletion.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => models.DetachedWork!);
        await leases.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(leases.Disposed);
        Assert.Equal(1, leases.DisposeCalls);
        Assert.Equal(1, leases.OpenCalls);
    }

    private static readonly ResolvedCorpusScope Scope = new("all", [], null);
    private static HybridPassageRetrievalEngine Engine(Reader reader, Models models, Leases? leases = null, TimeProvider? clock = null)
    {
        leases ??= new(); reader.Leases = leases;
        return new(reader, reader, leases, leases, models, new Probe(), new Codec(), new LocalPrivateContentDisclosure(), clock);
    }

    private static EligiblePassageCandidate Passage(long id, string body) => new(null, null, Guid.NewGuid(), 1,
        Guid.NewGuid(), new string('a', 64), id, Hash(body), 0, body.Length, body, "C:\\public\\policy.txt",
        CorpusEpoch: Leases.Epoch, PassagePolicyFingerprint: new string('b', 64), SearchInputHash: Hash(body));
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private class Reader : ICorpusRetrievalReader, IHybridPassageCandidateReader
    {
        protected virtual IEnumerable<EligiblePassageCandidate> AllPassages => Lexical.Concat(Dense).Concat(Directed);
        public Leases Leases { get; set; } = null!;
        public IReadOnlyList<EligiblePassageCandidate> Lexical { get; init; } = [];
        public IReadOnlyList<EligiblePassageCandidate> Dense { get; init; } = [];
        public IReadOnlyList<EligiblePassageCandidate> Directed { get; init; } = [];
        public SourceDirectedRetrievalStatus DirectedStatus { get; init; }
        public string? LastDenseQuery { get; private set; }
        public List<long[]> DisclosureBatches { get; } = [];
        public string DenseStatus { get; init; } = "ready";
        public long? StaleId { get; init; }
        public IReadOnlySet<long> StaleIds { get; init; } = new HashSet<long>();
        public long? StaleAfterFirstReadId { get; init; }
        public int StaleAfterReadCount { get; init; } = 1;
        private readonly Dictionary<long, int> _readCounts = [];
        public HashSet<long> UncitableIds { get; init; } = [];
        public int DenseCalls { get; private set; }
        public int KeywordCalls { get; private set; }
        public int LiteralCalls { get; private set; }
        public int ReadinessCalls { get; private set; }
        public string? LastLiteralQuery { get; private set; }
        public bool ReadUnderLease { get; private set; }
        public string? BlockStage { get; init; }
        public Action? OnContextRead { get; init; }
        public TaskCompletionSource BlockStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DenseCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _contextReads;
        private async Task BlockAsync(string stage)
        { if (BlockStage == stage) { BlockStarted.TrySetResult(); await ReleaseBlock.Task; } }
        public async ValueTask<CorpusLexicalReadiness> GetLexicalReadinessAsync(CancellationToken ct)
        { ReadinessCalls++; await BlockAsync("readiness"); return new(true, true); }
        public ValueTask<IReadOnlyList<string>> GetLexicalTermsAsync(string query, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<string>>([query]);
        public async ValueTask<ResolvedCorpusScope?> ResolveScopeAsync(string kind, Guid? root, string? cwd, CancellationToken ct)
        { await BlockAsync("scope"); return Scope; }
        public ValueTask<IReadOnlyList<EligiblePassageCandidate>> SearchAsync(string query, ResolvedCorpusScope scope, int limit, CancellationToken ct) => throw new NotSupportedException();
        public async ValueTask<IReadOnlyList<EligiblePassageCandidate>> ReadLexicalCandidatesAsync(string query, ResolvedCorpusScope scope, CancellationToken ct)
        { KeywordCalls++; await BlockAsync("lexical"); return Lexical; }
        public async ValueTask<IReadOnlyList<EligiblePassageCandidate>> ReadLiteralCandidatesAsync(string query, ResolvedCorpusScope scope, CancellationToken ct)
        { LiteralCalls++; LastLiteralQuery = query; await BlockAsync("literal"); return Lexical; }
        public async ValueTask<DensePassageCandidates> ReadDenseCandidatesAsync(ICorpusGenerationLease lease, ResolvedCorpusScope scope, IReadOnlyList<float> query, CancellationToken ct)
        { DenseCalls++; await BlockAsync("dense"); ct.ThrowIfCancellationRequested(); DenseCompleted.TrySetResult(); return new(DenseStatus, Dense) { SourceDirectedCandidates = Directed, SourceDirectedStatus = DirectedStatus }; }
        public ValueTask<DensePassageCandidates> ReadDenseCandidatesAsync(ICorpusGenerationLease lease, ResolvedCorpusScope scope,
            string queryText, IReadOnlyList<float> query, CancellationToken ct)
        { LastDenseQuery = queryText; return ReadDenseCandidatesAsync(lease, scope, query, ct); }
        public int ExpandedDisclosureCalls { get; private set; }
        public ValueTask<IReadOnlyDictionary<long, EligibleContext>> ReadForDisclosureAsync(IReadOnlyList<CorpusEvidenceBinding> bindings, CancellationToken ct) =>
            ReadDisclosureBatchAsync(bindings, 200, ct);
        public ValueTask<IReadOnlyDictionary<long, EligibleContext>> ReadExpandedForDisclosureAsync(IReadOnlyList<CorpusEvidenceBinding> bindings, CancellationToken ct)
        { ExpandedDisclosureCalls++; return ReadDisclosureBatchAsync(bindings, 250, ct); }
        private async ValueTask<IReadOnlyDictionary<long, EligibleContext>> ReadDisclosureBatchAsync(IReadOnlyList<CorpusEvidenceBinding> bindings, int maximum, CancellationToken ct)
        {
            DisclosureBatches.Add(bindings.Select(b => b.ChunkId).ToArray());
            if (bindings.Count > maximum) throw new ArgumentOutOfRangeException(nameof(bindings));
            var result = new Dictionary<long, EligibleContext>();
            foreach (var binding in bindings)
                if (await ((ICorpusRetrievalReader)this).ReadAsync(binding, 0, ct) is { } current) result.Add(binding.ChunkId, current);
            return result;
        }
        public async ValueTask<EligibleContext?> ReadAsync(CorpusEvidenceBinding binding, int context, CancellationToken ct)
        {
            OnContextRead?.Invoke();
            var readNumber = Interlocked.Increment(ref _contextReads);
            if (readNumber == 1) await BlockAsync("citable");
            else await BlockAsync("cited");
            await BlockAsync("fallback-read");
            ReadUnderLease = !Leases.Disposed;
            _readCounts[binding.ChunkId] = _readCounts.GetValueOrDefault(binding.ChunkId) + 1;
            if (_readCounts[binding.ChunkId] > StaleAfterReadCount && StaleAfterFirstReadId == binding.ChunkId) return null;
            var passage = AllPassages.FirstOrDefault(value => value.ChunkId == binding.ChunkId && value.ChunkId != StaleId && !StaleIds.Contains(value.ChunkId));
            return passage is null ? null : new EligibleContext(passage, passage.StartOffset, passage.Content, false, null,
                UncitableIds.Contains(passage.ChunkId) ? null : passage.Content);
        }
    }

    private sealed class PreparingReader : Reader, ICorpusAnnBindingPreparation
    {
        public bool IsPrepared(Guid epoch, FluxKnowledge.Application.Indexing.IndexSegmentDescriptor segment) => false;
        public ValueTask<long> PrepareAsync(ICorpusGenerationLease lease, FluxKnowledge.Application.Indexing.IndexSegmentDescriptor segment,
            CancellationToken ct) => throw new InvalidOperationException("Serving must not load cold metadata.");
    }
    private sealed class PreparationSignal : ICorpusAnnPreparationSignal
    {
        public int Requests { get; private set; }
        public string? Failure { get; init; }
        public string? FailureFor(Guid generationId) => Failure;
        public void RequestPreparation() => Requests++;
    }

    private sealed class Models : IScheduledPassageInference, IEmbeddingProvider, IPassageReranker
    {
        public EmbeddingProfile EmbeddingProfile { get; } = new("embedding", 2);
        public string RerankerFingerprint => "ranking";
        public bool FailRanking { get; init; }
        public Exception? RankingFailure { get; init; }
        public bool PartialRanking { get; init; }
        public bool SimulateCallerTimeout { get; init; }
        public bool FailScheduling { get; init; }
        public bool FailEmbedding { get; init; }
        public bool PauseRanking { get; init; }
        public bool IgnoreRankingCancellation { get; init; }
        public bool ReturnLateRankingSuccess { get; init; }
        public bool ReverseRanking { get; init; }
        public TaskCompletionSource RankingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRanking { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int EmbeddingCalls { get; private set; }
        public Action? OnEmbedding { get; init; }
        public TaskCompletionSource EmbeddingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<RerankPassage> Inputs { get; private set; } = [];
        public List<IReadOnlyList<RerankPassage>> Batches { get; } = [];
        public Func<RerankPassage, float>? Score { get; init; }
        public TaskCompletionSource AllowNativeCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? DetachedWork { get; private set; }
        public async ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work, CancellationToken ct)
        {
            if (FailScheduling) throw new TimeoutException();
            if (!SimulateCallerTimeout) return await work(this, this, ct);
            DetachedWork = work(this, this, ct).AsTask();
            throw new TimeoutException();
        }
        public async ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct)
        {
            EmbeddingCalls++;
            OnEmbedding?.Invoke();
            EmbeddingStarted.TrySetResult();
            if (SimulateCallerTimeout) await AllowNativeCompletion.Task;
            if (FailEmbedding) throw new InvalidOperationException("synthetic-embedding-failure");
            return new([1, 0], "embedding");
        }
        public async ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken ct)
        {
            Inputs = passages;
            Batches.Add(passages.ToArray());
            RankingStarted.TrySetResult();
            if (PauseRanking)
            {
                if (IgnoreRankingCancellation) await ReleaseRanking.Task;
                else await ReleaseRanking.Task.WaitAsync(ct);
            }
            if (!ReturnLateRankingSuccess) ct.ThrowIfCancellationRequested();
            if (RankingFailure is not null) throw RankingFailure;
            if (FailRanking) throw new InvalidOperationException("synthetic-ranking-failure");
            return new RerankResult(passages.Take(PartialRanking ? 1 : passages.Count)
                .Select((value, i) => new RerankScore(value.PassageId, Score?.Invoke(value) ?? (ReverseRanking ? i : -i))).ToArray(), RerankerFingerprint);
        }
    }

    private sealed class Leases : ICorpusGenerationLeaseStore, ICorpusAnnLeaseFactory, ICorpusAnnLease
    {
        public static readonly Guid Epoch = Guid.NewGuid();
        public bool Current { get; set; } = true;
        public bool Prepared { get; set; } = true;
        public bool? ReadableOverride { get; set; }
        public bool IsPrepared(ICorpusAnnBindingPreparation metadata) => Prepared;
        public bool Disposed { get; private set; }
        public TaskCompletionSource DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OpenCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public bool FailOpen { get; init; }
        public Exception? DisposeFailure { get; init; }
        public bool BlockAcquire { get; init; }
        public TaskCompletionSource AcquireStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAcquire { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid LeaseId { get; } = Guid.NewGuid();
        public IndexGenerationDescriptor Generation { get; } = new(Guid.NewGuid(), "embedding", 2, "synthetic", "checksum", 1, new(Epoch, 1), 2, 1);
        public async ValueTask<ICorpusGenerationLease?> TryAcquireAsync(Guid instance, GpuInteractiveOwnerIdentity owner, string fingerprint, int dimensions, CancellationToken ct)
        { AcquireCalls++; AcquireStarted.TrySetResult(); if (BlockAcquire) await ReleaseAcquire.Task; return this; }
        public async ValueTask<ICorpusAnnLease> OpenAsync(ICorpusGenerationLease lease, CancellationToken ct)
        {
            OpenCalls++;
            if (FailOpen) { await lease.DisposeAsync(); throw new InvalidOperationException("synthetic-open-failure"); }
            return this;
        }
        public ValueTask<bool> IsCurrentAsync(CancellationToken ct) => ValueTask.FromResult(Current);
        public ValueTask<bool> IsReadableAsync(CancellationToken ct) => ValueTask.FromResult(ReadableOverride ?? Current);
        public ValueTask<IReadOnlyList<AnnMatch>> SearchAsync(IReadOnlyList<float> query, int limit, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { DisposeCalls++; Disposed = true; DisposedSignal.TrySetResult(); return DisposeFailure is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask; }
    }
    private sealed class Probe : IGpuInteractiveOwnerProbe
    {
        public GpuInteractiveOwnerIdentity Current => new(1, DateTimeOffset.UnixEpoch, new string('a', 64));
        public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner) => GpuInteractiveOwnerObservation.Alive;
    }
    private sealed class Codec : ICorpusEvidenceCodec
    {
        public string Encode(CorpusEvidenceBinding binding) => "synthetic-" + binding.ChunkId;
        public CorpusEvidenceBinding Decode(string reference) => throw new NotSupportedException();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<Timer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _now.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in _timers.ToArray()) timer.Fire(_now);
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime; return true; }
            public void Fire(DateTimeOffset now) { if (_due <= now) { _due = null; callback(state); } }
            public void Dispose() => _due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
