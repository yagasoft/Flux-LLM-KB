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

    private sealed class SecretSourceSearchService : ISearchService
    {
        public ValueTask<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new SearchResponse(
            [new SearchHit(new PipelineRecordId(Guid.NewGuid()), "token=secret-content-sentinel", 1, "Unsafe", "password=secret-content-sentinel", 1, [])], 1, string.Empty, "local_first"));
    }

    private sealed class PassThroughDisclosure : ILocalPrivateContentDisclosure
    {
        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind) => new(value, false, null);
    }
}
