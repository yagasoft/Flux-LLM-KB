using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Knowledge;

public sealed class KnowledgeQueryServiceTests
{
    [Fact]
    public async Task Degraded_source_retrieval_cannot_appear_as_a_healthy_knowledge_union()
    {
        var service = new KnowledgeQueryService(new StubKnowledgeStore(), new DegradedSearch(), new PassThroughDisclosure());
        var refusal = await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => service.SearchAsync("atlas", 2, CancellationToken.None).AsTask());
        Assert.Equal("busy", refusal.Status);
    }

    [Theory]
    [InlineData(51)]
    [InlineData(100)]
    public async Task Public_knowledge_limit_above_fifty_keeps_a_bounded_source_request_and_fills_from_knowledge(int limit)
    {
        var sources = new FiftySources();
        var service = new KnowledgeQueryService(new ManyNotes(), sources, new PassThroughDisclosure());
        var results = await service.SearchAsync("atlas", limit, CancellationToken.None);
        Assert.Equal(50, sources.RequestedLimit);
        Assert.Equal(limit, results.Count);
        Assert.InRange(results.Count(row => row.Kind == "source"), 1, 50);
        Assert.Contains(results, row => row.Kind == "note");
    }

    [Fact]
    public async Task SearchAsync_returns_a_bounded_provenance_aware_union_of_retained_sources_and_native_knowledge()
    {
        var service = new KnowledgeQueryService(new StubKnowledgeStore(), new StubSearchService(), new PassThroughDisclosure());

        var results = await service.SearchAsync("atlas", 2, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, value => value.Provenance == "retained-source" && value.Kind == "source");
        Assert.Contains(results, value => value.Provenance == "knowledge" && value.Kind == "note");
    }

    [Fact]
    public async Task SearchAsync_withholds_secret_bearing_retained_source_fields_at_the_union_boundary()
    {
        var service = new KnowledgeQueryService(new StubKnowledgeStore(), new SecretSourceSearchService(), new LocalPrivateContentDisclosure());

        var results = await service.SearchAsync("atlas", 10, CancellationToken.None);

        Assert.DoesNotContain(results, value => value.Provenance == "retained-source");
        Assert.Contains(results, value => value.Provenance == "knowledge");
    }

    private sealed class StubKnowledgeStore : IKnowledgeStore
    {
        public ValueTask<KnowledgeTarget?> FindTargetAsync(KnowledgeMutation mutation, CancellationToken cancellationToken) => ValueTask.FromResult<KnowledgeTarget?>(null);
        public ValueTask<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<KnowledgeSearchResult>>([new(Guid.NewGuid(), "note", "Atlas", "native", "knowledge")]);
        public ValueTask<IReadOnlyList<KnowledgeGraphResult>> TraverseAsync(string node, int maxDepth, int maxResults, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<KnowledgeGraphResult>>([]);
    }

    private sealed class StubSearchService : ISearchService
    {
        public ValueTask<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new SearchResponse(
            [new SearchHit(new PipelineRecordId(Guid.NewGuid()), "retained-source-id", 1, "Retained Atlas", "retained", 1, [])], 1, string.Empty, "local_first"));
    }

    private sealed class DegradedSearch : ISearchService
    {
        public ValueTask<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct) => ValueTask.FromResult(new SearchResponse(
            [new(new(Guid.NewGuid()), "retained-source-id", 1, "Atlas", "Complete lexical fallback", 1, ["semantic:busy"])],
            1, string.Empty, "local_first") { DegradedStatus = "busy" });
    }

    private sealed class SecretSourceSearchService : ISearchService
    {
        public ValueTask<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new SearchResponse(
            [new SearchHit(new PipelineRecordId(Guid.NewGuid()), "token=secret-content-sentinel", 1, "Unsafe", "password=secret-content-sentinel", 1, [])], 1, string.Empty, "local_first"));
    }

    private sealed class PassThroughDisclosure : ILocalPrivateContentDisclosure
    {
        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind) => new(value, false, null);
    }

    private sealed class FiftySources : ISearchService
    {
        public int RequestedLimit { get; private set; }
        public ValueTask<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct)
        {
            _ = FluxKnowledge.Application.Search.SearchQueryValidator.Validate(request);
            RequestedLimit = request.Limit;
            return ValueTask.FromResult(new SearchResponse(Enumerable.Range(0, request.Limit).Select(i =>
                new SearchHit(new(Guid.NewGuid()), "public-source-" + i, 1, "Atlas", "Complete public passage", 1, [])).ToArray(), request.Limit, "", "local_first"));
        }
    }
    private sealed class ManyNotes : IKnowledgeStore
    {
        public ValueTask<KnowledgeTarget?> FindTargetAsync(KnowledgeMutation mutation, CancellationToken ct) => ValueTask.FromResult<KnowledgeTarget?>(null);
        public ValueTask<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(string query, int limit, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<KnowledgeSearchResult>>(Enumerable.Range(0, limit).Select(i =>
                new KnowledgeSearchResult(Guid.NewGuid(), "note", "Atlas", "Public note " + i, "knowledge")).ToArray());
        public ValueTask<IReadOnlyList<KnowledgeGraphResult>> TraverseAsync(string node, int depth, int max, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<KnowledgeGraphResult>>([]);
    }
}
