using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class HybridPassageRetrievalTests
{
    [Fact]
    public async Task Quality_phase_timings_include_both_model_batches_and_the_awaited_native_wait()
    {
        var clock = new ManualClock();
        var reader = new QualityReader
        {
            Lexical = Enumerable.Range(1, 50).Select(i => Passage(i, "query baseline " + i)).ToArray(),
            Pool = Enumerable.Range(1001, 50).Select(i => Passage(i, "query pool " + i)).ToArray(),
            OnWindow = () => clock.Advance(TimeSpan.FromMilliseconds(7))
        };
        var models = new Models { PauseRanking = true, Score = _ => { clock.Advance(TimeSpan.FromMilliseconds(1)); return 1; } };
        var pending = Engine(reader, models, clock: clock).SearchCandidateQualityAsync(QualityRequest(), default).AsTask();
        await models.RankingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMilliseconds(300)); models.ReleaseRanking.TrySetResult();
        var result = await pending;
        Assert.Equal(new[] { 50, 50 }, models.Batches.Select(b => b.Count));
        Assert.Equal(7, result.Timings.DenseSelectionMilliseconds);
        Assert.Equal(400, result.Timings.RankingMilliseconds);
        Assert.Equal(407, result.Timings.BeforeLeaseDisposalMilliseconds);
    }
    [Fact]
    public async Task Quality_experiment_scores_up_to_a_hundred_inputs_in_fifty_pair_calls_after_one_fresh_gate()
    {
        var reader = new QualityReader
        {
            Lexical = Enumerable.Range(1, 50).Select(i => Passage(i, "query baseline " + i)).ToArray(),
            Pool = Enumerable.Range(1001, 100).Select(i => Passage(i, "query pool " + i)).ToArray()
        };
        var models = new Models();
        var result = await Engine(reader, models).SearchCandidateQualityAsync(QualityRequest(), default);
        Assert.Equal(1, reader.WindowCalls);
        Assert.Equal(new[] { 50, 50 }, models.Batches.Select(b => b.Count));
        Assert.Equal(100, result.ModelInputIds.Count);
        Assert.Equal(100, result.ModelInputIds.Distinct().Count());
        var disclosed = Assert.Single(reader.DisclosureBatches);
        Assert.InRange(disclosed.Length, 100, 250);
        Assert.Equal(1, reader.ExpandedDisclosureCalls);
        Assert.All(result.BaselineBindingIds, id => Assert.Contains(id, disclosed));
        Assert.Equal(result.BaselineModelInputIds, result.ModelInputIds.Take(result.BaselineModelInputIds.Count));
        Assert.All(result.ModelInputIds, id => Assert.Contains(id, disclosed));
        Assert.All(result.Result.Results, hit => Assert.Contains("rerank:trained", hit.Explanation));
        Assert.Equal("ready", result.Result.SemanticStatus); Assert.False(result.ServingQualified);
    }

    [Fact]
    public async Task Quality_expansion_gates_all_two_hundred_baseline_bindings_and_fifty_new_representatives_once()
    {
        var reader = new QualityReader
        {
            Lexical = Enumerable.Range(1, 100).Select(i => Passage(i, "query lexical " + i)).ToArray(),
            Dense = Enumerable.Range(1001, 100).Select(i => Passage(i, "query dense " + i)).ToArray(),
            Pool = Enumerable.Range(2001, 100).Select(i => Passage(i, "query pool " + i)).ToArray()
        };
        var models = new Models();
        var result = await Engine(reader, models).SearchCandidateQualityAsync(QualityRequest(), default);
        Assert.Equal(200, result.BaselineBindingIds.Count);
        Assert.Equal(250, Assert.Single(reader.DisclosureBatches).Length);
        Assert.Equal(1, reader.ExpandedDisclosureCalls);
        Assert.Equal(100, result.ModelInputIds.Count);
        Assert.Equal(new[] { 50, 50 }, models.Batches.Select(b => b.Count));
        Assert.Equal(result.BaselineModelInputIds, result.ModelInputIds.Take(50));
        Assert.Equal(Enumerable.Range(2001, 50).Select(i => (long)i), result.ModelInputIds.Skip(50));
    }

    [Fact]
    public async Task Quality_raw_identity_conflicts_refuse_before_unsafe_candidate_filtering()
    {
        var baseline = Passage(1, "query baseline");
        var reader = new QualityReader { Lexical = [baseline], Pool = [baseline with { Content = "Conflicting invalid hash" }] };
        var models = new Models();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(reader, models).SearchCandidateQualityAsync(QualityRequest(), default).AsTask());
        Assert.Empty(models.Batches); Assert.Empty(reader.DisclosureBatches);
    }

    [Fact]
    public async Task Quality_partial_scoring_refuses_without_starting_the_next_model_batch()
    {
        var reader = new QualityReader
        {
            Lexical = Enumerable.Range(1, 50).Select(i => Passage(i, "query baseline " + i)).ToArray(),
            Pool = Enumerable.Range(1001, 50).Select(i => Passage(i, "query pool " + i)).ToArray()
        };
        var models = new Models { PartialRanking = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(reader, models).SearchCandidateQualityAsync(QualityRequest(), default).AsTask());
        Assert.Single(models.Batches);
    }

    [Fact]
    public async Task Quality_caller_cancellation_retains_the_lease_until_actual_model_settlement()
    {
        var reader = new QualityReader { Lexical = [Passage(1, "query evidence")], Pool = [Passage(2, "query addition")] };
        var models = new Models { PauseRanking = true, IgnoreRankingCancellation = true };
        var leases = new Leases(); using var caller = new CancellationTokenSource();
        var pending = Engine(reader, models, leases).SearchCandidateQualityAsync(QualityRequest(), caller.Token).AsTask();
        await models.RankingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); caller.Cancel();
        Assert.False(pending.IsCompleted); Assert.False(leases.Disposed);
        models.ReleaseRanking.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(leases.Disposed); Assert.Single(models.Batches);
    }

    [Fact]
    public async Task Quality_final_fresh_reads_remove_a_withdrawn_trained_top_hit()
    {
        var reader = new QualityReader
        {
            Lexical = [Passage(1, "query baseline")], Pool = [Passage(1001, "query withdrawn"), Passage(1002, "query surviving")],
            StaleAfterFirstReadId = 1001
        };
        var result = await Engine(reader, new Models { Score = p => p.PassageId == 1001 ? 100 : 1 })
            .SearchCandidateQualityAsync(QualityRequest(), default);
        Assert.DoesNotContain(result.Result.Results, p => p.ChunkId == 1001);
        Assert.Contains(result.Result.Results, p => p.ChunkId == 1002);
    }

    [Fact]
    public async Task Normal_search_keeps_the_existing_fifty_input_path_even_when_window_support_exists()
    {
        var reader = new QualityReader
        {
            Lexical = Enumerable.Range(1, 50).Select(i => Passage(i, "query baseline " + i)).ToArray(),
            Pool = Enumerable.Range(1001, 100).Select(i => Passage(i, "query pool " + i)).ToArray()
        };
        var models = new Models();
        await Engine(reader, models).SearchAsync(QualityRequest(), default);
        Assert.Equal(0, reader.WindowCalls); Assert.Single(models.Batches); Assert.Equal(50, models.Inputs.Count);
        Assert.Equal(0, reader.ExpandedDisclosureCalls);
    }

    private static CorpusSearchRequest QualityRequest() => new("query", 10, "root", Guid.NewGuid(), null);

    private sealed class QualityReader : Reader, ICorpusDenseCandidateWindowReader
    {
        public IReadOnlyList<EligiblePassageCandidate> Pool { get; init; } = [];
        public int WindowCalls { get; private set; }
        public Action? OnWindow { get; init; }
        protected override IEnumerable<EligiblePassageCandidate> AllPassages => base.AllPassages.Concat(Pool);
        public ValueTask<DenseCandidateWindow> ReadDenseCandidateWindowAsync(ICorpusAnnLease lease, ResolvedCorpusScope scope,
            IReadOnlyList<float> query, CancellationToken ct)
        { WindowCalls++; OnWindow?.Invoke(); ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(new DenseCandidateWindow(DenseStatus, Dense, Pool) { BaselineRawMatches = Dense.Count, PoolRawMatches = Pool.Count }); }
    }
}
