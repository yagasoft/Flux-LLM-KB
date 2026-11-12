using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed partial class HybridPassageRetrievalTests
{
    [Fact]
    public async Task Literal_preserves_raw_code_units_and_does_not_enter_models_or_generation_ownership()
    {
        const string query = " e\u0301 ";
        var reader = new Reader { Lexical = [Passage(1, "first e\u0301 last"), Passage(2, "first é last"), Passage(3, "first E\u0301 last")] };
        var models = new Models();
        var response = await Engine(reader, models).SearchAsync(ModeRequest(query, "literal"), default);
        Assert.Equal("not-requested", response.SemanticStatus);
        Assert.Equal("lexical", response.RetrievalMode);
        Assert.Null(response.IndexGeneration);
        Assert.Equal(1, Assert.Single(response.Results).ChunkId);
        Assert.Contains("exact:ordinal", response.Results[0].Explanation);
        Assert.DoesNotContain("fusion:rrf", response.Results[0].Explanation);
        Assert.DoesNotContain(response.Results[0].Explanation, value => value.StartsWith("semantic:", StringComparison.Ordinal));
        Assert.Equal(query, reader.LastLiteralQuery);
        Assert.Equal(1, reader.LiteralCalls);
        Assert.Equal(0, reader.KeywordCalls);
        Assert.Equal(0, reader.ReadinessCalls);
        Assert.Equal(0, reader.Leases.AcquireCalls);
        Assert.Equal(0, reader.DenseCalls);
        Assert.Equal(0, models.EmbeddingCalls);
        Assert.Empty(models.Inputs);
        Assert.DoesNotContain(response.Warnings, value => value.StartsWith("semantic:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Literal_keeps_the_hundred_candidate_budget_and_rejects_uncitable_and_stale_bindings()
    {
        var reader = new Reader
        {
            Lexical = Enumerable.Range(1, 100).Select(id => Passage(id, "needle exact body " + id)).Reverse().ToArray(),
            UncitableIds = Enumerable.Range(1, 98).Select(id => (long)id).ToHashSet(), StaleId = 99
        };
        var response = await Engine(reader, new Models()).SearchAsync(ModeRequest("needle", "literal"), default);
        Assert.Equal("not-requested", response.SemanticStatus);
        Assert.Equal(100, Assert.Single(response.Results).ChunkId);
        Assert.Equal(0, reader.Leases.AcquireCalls);
    }

    [Fact]
    public async Task A_completed_literal_search_can_truthfully_return_no_matches()
    {
        var reader = new Reader { Lexical = [Passage(1, "unrelated body")] };
        var response = await Engine(reader, new Models()).SearchAsync(ModeRequest("needle", "literal"), default);
        Assert.Equal("not-requested", response.SemanticStatus);
        Assert.Empty(response.Results);
        Assert.DoesNotContain(response.Warnings, value => value.StartsWith("semantic:", StringComparison.Ordinal));
        Assert.Equal(0, reader.Leases.AcquireCalls);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("KEYWORD")]
    public async Task An_unknown_match_mode_is_refused_before_scope_or_models(string mode)
    {
        var reader = new Reader();
        var error = await Assert.ThrowsAsync<NativeOperationException>(() => Engine(reader, new Models())
            .SearchAsync(ModeRequest("query", mode), default).AsTask());
        Assert.Equal("invalid-request", error.ReasonCode);
        Assert.Equal(0, reader.ReadinessCalls);
        Assert.Equal(0, reader.Leases.AcquireCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_free_service_paths_honour_literal_without_normalising_or_using_keyword(bool explicitLexical)
    {
        const string query = " e\u0301 ";
        var reader = new Reader { Lexical = [Passage(1, "first e\u0301 last")] };
        reader.Leases = new Leases();
        var service = new CorpusRetrievalService(reader, new Codec(), new LocalPrivateContentDisclosure());
        var request = ModeRequest(query, "literal");
        var result = explicitLexical ? await service.SearchLexicalAsync(request, default) : await service.SearchAsync(request, default);
        Assert.Single(result.Results);
        Assert.Equal("not-requested", result.SemanticStatus);
        Assert.Equal(query, reader.LastLiteralQuery);
        Assert.Equal(1, reader.LiteralCalls);
        Assert.Equal(0, reader.KeywordCalls);
        Assert.Equal(0, reader.ReadinessCalls);
    }

    private static CorpusSearchRequest ModeRequest(string query, string mode) =>
        JsonSerializer.Deserialize<CorpusSearchRequest>(JsonSerializer.Serialize(new
        { Query = query, Limit = 10, Scope = "all", RootId = (Guid?)null, Cwd = (string?)null, MatchMode = mode }))!;

    [Fact]
    public async Task Model_free_literal_observes_caller_cancellation_before_a_late_reader_acknowledges()
    {
        var reader = new Reader { BlockStage = "literal", Lexical = [Passage(1, "needle evidence")], Leases = new Leases() };
        var service = new CorpusRetrievalService(reader, new Codec(), new LocalPrivateContentDisclosure());
        using var caller = new CancellationTokenSource();
        var search = service.SearchLexicalAsync(ModeRequest("needle", "literal"), caller.Token).AsTask();
        try
        {
            await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(reader.ReadUnderLease);
        }
        finally { reader.ReleaseBlock.TrySetResult(); }
    }

    [Fact]
    public async Task Literal_uses_the_outer_budget_without_the_earlier_semantic_cutoff()
    {
        var clock = new ManualClock();
        var reader = new Reader { BlockStage = "literal", Lexical = [Passage(1, "needle evidence")] };
        var models = new Models();
        var engine = Engine(reader, models, clock: clock);
        var search = engine.SearchAsync(ModeRequest("needle", "literal"), default).AsTask();
        await reader.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.False(search.IsCompleted);
        reader.ReleaseBlock.TrySetResult();
        Assert.Equal("not-requested", (await search.WaitAsync(TimeSpan.FromSeconds(1))).SemanticStatus);
        Assert.Equal(0, reader.Leases.AcquireCalls);
        Assert.Equal(0, models.EmbeddingCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_free_literal_rechecks_previously_read_bindings_at_response_publication(bool explicitLexical)
    {
        var reader = new Reader
        {
            Lexical = [Passage(1, "needle first evidence"), Passage(2, "needle second evidence")],
            Leases = new Leases(), StaleAfterFirstReadId = 1, StaleAfterReadCount = 1
        };
        var service = new CorpusRetrievalService(reader, new Codec(), new LocalPrivateContentDisclosure());
        var request = ModeRequest("needle", "literal");
        var result = explicitLexical ? await service.SearchLexicalAsync(request, default) : await service.SearchAsync(request, default);
        Assert.Equal(2, Assert.Single(result.Results).ChunkId);
        Assert.Contains("publication-changed", result.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_free_literal_refuses_when_all_previously_read_bindings_have_been_withdrawn(bool explicitLexical)
    {
        var reader = new Reader
        {
            Lexical = [Passage(1, "needle first evidence")], Leases = new Leases(), StaleAfterFirstReadId = 1, StaleAfterReadCount = 1
        };
        var service = new CorpusRetrievalService(reader, new Codec(), new LocalPrivateContentDisclosure());
        var request = ModeRequest("needle", "literal");
        var error = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() =>
            (explicitLexical ? service.SearchLexicalAsync(request, default) : service.SearchAsync(request, default)).AsTask());
        Assert.Equal("index-updating", error.Status);
    }
}
