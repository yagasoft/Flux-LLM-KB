using FluxKnowledge.Application.Knowledge;

namespace FluxKnowledge.Application.Ports;

/// <summary>Retained-only persistence and projection port for native knowledge data.</summary>
public interface IKnowledgeStore
{
    ValueTask<KnowledgeTarget?> FindTargetAsync(KnowledgeMutation mutation, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<KnowledgeGraphResult>> TraverseAsync(string node, int maxDepth, int maxResults, CancellationToken cancellationToken);
}
