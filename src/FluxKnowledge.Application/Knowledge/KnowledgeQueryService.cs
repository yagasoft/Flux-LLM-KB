using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Application.IntegrationV1;

namespace FluxKnowledge.Application.Knowledge;

public sealed record KnowledgeSearchResult(Guid Id, string Kind, string Title, string Content, string Provenance, decimal? Confidence = null, string? SourceIdentity = null, long? SourceRevision = null);
public sealed record KnowledgeGraphResult(Guid ClaimId, string Subject, string Predicate, string ObjectText, int Depth);

public interface IKnowledgeQueryService
{
    ValueTask<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<KnowledgeGraphResult>> GraphAsync(string node, int maxDepth, int maxResults, CancellationToken cancellationToken);
}

/// <summary>Bounded, retained-only safe projections for native knowledge.</summary>
public sealed class KnowledgeQueryService(IKnowledgeStore store, ISearchService retainedSearch, ILocalPrivateContentDisclosure disclosure) : IKnowledgeQueryService
{
    public async ValueTask<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var canonicalQuery = NativeV1ContractLimits.CanonicalizeKnowledgeQuery(query);
        var sourceTask = retainedSearch.SearchAsync(new Application.Contracts.SearchRequest(canonicalQuery, limit, "local_first", null, null, null), cancellationToken).AsTask();
        var knowledgeTask = store.SearchAsync(canonicalQuery, limit, cancellationToken).AsTask();
        await Task.WhenAll(sourceTask, knowledgeTask).ConfigureAwait(false);

        var sourceRows = sourceTask.Result.Results.Select(static hit => new KnowledgeSearchResult(
            hit.PipelineRecordId.Value, "source", hit.Title, hit.Snippet, "retained-source", null, hit.SourceIdentity, hit.Revision))
            .Select(Safe).Where(static row => row is not null).Cast<KnowledgeSearchResult>().ToArray();
        var knowledgeRows = knowledgeTask.Result.Select(Safe).Where(static row => row is not null).Cast<KnowledgeSearchResult>().ToArray();
        var result = new List<KnowledgeSearchResult>(limit);
        for (var index = 0; result.Count < limit && (index < sourceRows.Length || index < knowledgeRows.Length); index++)
        {
            if (index < sourceRows.Length) result.Add(sourceRows[index]);
            if (result.Count < limit && index < knowledgeRows.Length) result.Add(knowledgeRows[index]);
        }
        return result;
    }

    public async ValueTask<IReadOnlyList<KnowledgeGraphResult>> GraphAsync(string node, int maxDepth, int maxResults, CancellationToken cancellationToken)
    {
        if (maxDepth is < 1 or > 8 || maxResults is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxResults));
        var canonicalNode = NativeV1ContractLimits.CanonicalizeGraphNode(node);
        var rows = await store.TraverseAsync(canonicalNode, maxDepth, maxResults, cancellationToken);
        return rows.Select(Safe).Where(static row => row is not null).Cast<KnowledgeGraphResult>().Take(maxResults).ToArray();
    }

    private KnowledgeSearchResult? Safe(KnowledgeSearchResult row)
    {
        var content = disclosure.Evaluate(row.Content, LocalDisclosureKind.KnowledgeRead);
        var title = disclosure.Evaluate(row.Title, LocalDisclosureKind.KnowledgeRead);
        var sourceIdentity = row.SourceIdentity is null ? null : disclosure.Evaluate(row.SourceIdentity, LocalDisclosureKind.KnowledgeRead);
        return content.Withheld || title.Withheld || sourceIdentity is { Withheld: true }
            ? null
            : row with { Content = content.Value!, Title = title.Value!, SourceIdentity = sourceIdentity?.Value };
    }

    private KnowledgeGraphResult? Safe(KnowledgeGraphResult row)
    {
        var content = disclosure.Evaluate($"{row.Subject} {row.Predicate} {row.ObjectText}", LocalDisclosureKind.KnowledgeRead);
        return content.Withheld ? null : row;
    }
}
