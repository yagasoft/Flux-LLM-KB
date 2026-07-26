using FluxKnowledge.Domain.Common;

namespace FluxKnowledge.Application.Contracts;

public sealed record SearchRequest(
    string Query,
    int Limit,
    string ScopeMode,
    string? Cwd,
    string? RootName,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Filters);

public sealed record SearchHit(
    PipelineRecordId PipelineRecordId,
    string SourceIdentity,
    long Revision,
    string Title,
    string Snippet,
    double Score,
    IReadOnlyList<string> Explanation);

public sealed record SearchResponse(
    IReadOnlyList<SearchHit> Results,
    int CandidateCount,
    string ActiveIndexGeneration,
    string ScopeNote);
