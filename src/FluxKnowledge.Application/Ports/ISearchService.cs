using FluxKnowledge.Application.Contracts;

namespace FluxKnowledge.Application.Ports;

public interface ISearchService
{
    ValueTask<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}
