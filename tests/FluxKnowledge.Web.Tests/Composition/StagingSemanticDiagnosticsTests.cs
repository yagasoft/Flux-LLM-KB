using FluxKnowledge.Application.Ports;
using FluxKnowledge.Web;
using Microsoft.AspNetCore.Http;
using Xunit;
using System.Text.Json;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingSemanticDiagnosticsTests
{
    [Theory]
    [InlineData("{\"query\":\"test\",\"scope\":\"all\"}")]
    [InlineData("{\"query\":\"test\",\"scope\":\"workspace\",\"cwd\":\"I:\\\\owned\",\"match_mode\":\"literal\"}")]
    [InlineData("{\"query\":\"test\",\"scope\":\"root\"}")]
    [InlineData("{\"query\":\"test\",\"scope\":\"workspace\",\"cwd\":\"I:\\\\owned\",\"limit\":100}")]
    [InlineData("[]")]
    public async Task Candidate_quality_rejects_unscoped_literal_or_invalid_payload_before_engine_access(string json)
    {
        using var input = JsonDocument.Parse(json);
        var result = await StagingSemanticDiagnostics.CandidateQualityAsync(input.RootElement, null!, default);
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task Normal_rerank_diagnostic_retains_its_fifty_pair_limit()
    {
        var models = new Models();
        var pairs = Enumerable.Range(1, 51).Select(id => new RerankPassage(id, "canonical")).ToArray();
        var result = await StagingSemanticDiagnostics.RerankAsync(new("query", pairs), models, default);
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, models.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Cancelled_embedding_diagnostic_waits_for_native_settlement_and_discards_late_values(bool lateSuccess, bool lateRefusal)
    {
        var models=new Models { BlockEmbedding=true, LateEmbeddingSuccess=lateSuccess, LateEmbeddingRefusal=lateRefusal };
        using var caller=new CancellationTokenSource();
        var pending=StagingSemanticDiagnostics.EmbedAsync(new("query"),models,caller.Token);
        await models.EmbeddingStarted.Task;
        caller.Cancel();
        await Task.WhenAny(pending,Task.Delay(100));
        var completedEarly=pending.IsCompleted;
        models.ReleaseEmbedding.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        Assert.False(completedEarly);
    }
    [Fact]
    public async Task Cancelled_rerank_diagnostic_waits_for_the_actual_resident_callback_to_settle()
    {
        var models = new Models { BlockRanking = true };
        using var caller = new CancellationTokenSource();
        var pending = StagingSemanticDiagnostics.RerankAsync(new("query", [new(8, "canonical")]), models, caller.Token);
        await models.RankingStarted.Task;
        caller.Cancel();
        Assert.False(pending.IsCompleted);
        models.ReleaseRanking.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Rerank_diagnostic_preserves_the_complete_input_and_refuses_duplicate_ids_before_admission()
    {
        var models = new Models();
        var pairs = new RerankPassage[] { new(8, "  canonical\n"), new(2, "second") };
        var result = await StagingSemanticDiagnostics.RerankAsync(new("query", pairs), models, default);
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(pairs, models.Pairs);
        var rejected = await StagingSemanticDiagnostics.RerankAsync(new("query", [pairs[0], pairs[0]]), models, default);
        Assert.Equal(400, ((IStatusCodeHttpResult)rejected).StatusCode);
        Assert.Equal(1, models.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Invalid_queries_do_not_enter_the_resident_pool(string? query)
    {
        var models = new Models();
        var response = await StagingSemanticDiagnostics.EmbedAsync(new(query), models, default);
        Assert.Equal(400, ((IStatusCodeHttpResult)response).StatusCode);
        Assert.Equal(0, models.Calls);
    }

    [Fact]
    public async Task Oracle_queries_use_the_serving_normalisation_and_current_model_profile()
    {
        var models = new Models();
        var response = await StagingSemanticDiagnostics.EmbedAsync(new("  cafe\u0301  "), models, default);
        Assert.Equal(200, ((IStatusCodeHttpResult)response).StatusCode);
        Assert.Equal("caf\u00e9", models.Query);
        Assert.Equal(1, models.Calls);
        var rejected = await StagingSemanticDiagnostics.EmbedAsync(new(new string('x', 2049)), models, default);
        Assert.Equal(400, ((IStatusCodeHttpResult)rejected).StatusCode);
        Assert.Equal(1, models.Calls);
    }

    [Fact]
    public async Task Exact_canonical_input_diagnostics_preserve_whitespace_and_unicode()
    {
        const string input = "  cafe\u0301\n";
        var models = new Models();
        var response = await StagingSemanticDiagnostics.EmbedAsync(new(input, ExactInput: true), models, default);
        Assert.Equal(200, ((IStatusCodeHttpResult)response).StatusCode);
        Assert.Equal(input, models.Query);
        var rejected = await StagingSemanticDiagnostics.EmbedAsync(new(new string('x', 2049), ExactInput: true), models, default);
        Assert.Equal(400, ((IStatusCodeHttpResult)rejected).StatusCode);
        Assert.Equal(1, models.Calls);
    }

    private sealed class Models : IScheduledPassageInference, IEmbeddingProvider, IPassageReranker
    {
        public int Calls { get; private set; }
        public string? Query { get; private set; }
        public IReadOnlyList<RerankPassage>? Pairs { get; private set; }
        public bool BlockRanking { get; init; }
        public bool BlockEmbedding { get; init; }
        public bool LateEmbeddingSuccess { get; init; }
        public bool LateEmbeddingRefusal { get; init; }
        public TaskCompletionSource EmbeddingStarted { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEmbedding { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RankingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRanking { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EmbeddingProfile EmbeddingProfile => new("test", 2);
        public string RerankerFingerprint => "test";
        public ValueTask<T> ExecuteAsync<T>(Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work,
            CancellationToken ct) { Calls++; return work(this, this, ct); }
        public async ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken ct)
        {
            Query=text; EmbeddingStarted.TrySetResult();
            if(BlockEmbedding) await ReleaseEmbedding.Task;
            if(LateEmbeddingRefusal) throw new PassageRetrievalRefusalException("unavailable");
            if(!LateEmbeddingSuccess) ct.ThrowIfCancellationRequested();
            return new EmbeddingResult([1,0],"test");
        }
        public async ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken ct)
        {
            Query = query; Pairs = passages; RankingStarted.TrySetResult();
            if (BlockRanking) await ReleaseRanking.Task;
            ct.ThrowIfCancellationRequested();
            return new(passages.Select(p => new RerankScore(p.PassageId, 1)).ToArray(), "test");
        }
    }
}
