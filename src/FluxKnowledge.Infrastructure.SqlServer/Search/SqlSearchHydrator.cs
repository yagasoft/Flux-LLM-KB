using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed class SqlSearchHydrator(IDbContextFactory<FluxKnowledgeDbContext> contextFactory) : ISearchHydrator
{
    public async ValueTask<IReadOnlyList<SearchHit>> HydrateAsync(
        IReadOnlyList<FusedCandidate> candidates,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0 || limit <= 0)
        {
            return [];
        }

        var candidateById = candidates.ToDictionary(static candidate => candidate.VectorId);
        var vectorIds = candidateById.Keys.ToArray();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await (
                from vector in context.Vectors.AsNoTracking()
                join chunk in context.TextChunks.AsNoTracking()
                    on new { vector.TextChunkId, vector.SourceRevision }
                    equals new { TextChunkId = chunk.Id, chunk.SourceRevision }
                join artifact in context.Artifacts.AsNoTracking()
                    on new { chunk.ArtifactId, chunk.SourceRevision }
                    equals new { ArtifactId = artifact.Id, artifact.SourceRevision }
                join record in context.PipelineRecords.AsNoTracking()
                    on new { PipelineRecordId = artifact.PipelineRecordId, artifact.SourceRevision }
                    equals new { PipelineRecordId = record.Id, SourceRevision = record.Revision }
                join source in context.SourceIdentities.AsNoTracking()
                    on record.SourceIdentityId equals source.Id
                join revisionValue in context.SourceRevisions.AsNoTracking()
                    on record.SourceRevisionId equals revisionValue.Id into revisions
                from revision in revisions.DefaultIfEmpty()
                join publicationValue in context.DocumentPublications.AsNoTracking() on new
                {
                    PipelineRecordId = record.Id,
                    PipelineRecordRevision = record.Revision,
                    DocumentInputSourceRevisionId = revision.Id
                } equals new
                {
                    publicationValue.PipelineRecordId,
                    publicationValue.PipelineRecordRevision,
                    publicationValue.DocumentInputSourceRevisionId
                } into publications
                from publication in publications.DefaultIfEmpty()
                join ownerValue in context.SourceRevisions.AsNoTracking()
                    on publication.OwnerSourceRevisionId equals ownerValue.Id into owners
                from owner in owners.DefaultIfEmpty()
                where vectorIds.Contains(vector.VectorId) &&
                      !vector.IsDeleted &&
                      !record.IsDeleted &&
                      (!record.SourceRevisionId.HasValue || context.SourceRevisions.Any(sourceRevision =>
                          sourceRevision.Id == record.SourceRevisionId && sourceRevision.SuppressedAtUtc == null &&
                          (sourceRevision.OriginKind != 2 || context.DocumentPublications.Any(document =>
                              document.PipelineRecordId == record.Id && document.PipelineRecordRevision == record.Revision &&
                              document.DocumentInputSourceRevisionId == sourceRevision.Id)) &&
                          context.SourceRootConfigurations.Any(root => root.Id == sourceRevision.SourceRootId && root.State != (int)SourceRootState.Deleting))) &&
                      vector.TextChunkContentHash == chunk.ContentHash &&
                      vector.SourceRevision == record.Revision &&
                      (record.SourceRevisionId.HasValue || record.Revision == context.PipelineRecords
                          .Where(current => current.SourceIdentityId == record.SourceIdentityId)
                          .Max(current => current.Revision))
                select new HydratedRow(
                    vector.VectorId,
                    record.Id,
                    record.Revision,
                    publication == null
                        ? EF.Functions.Collate(source.StableKey, SchemaConfiguration.SchedulerFenceCollation)
                        : EF.Functions.Collate(owner.CanonicalPath, SchemaConfiguration.SchedulerFenceCollation),
                    chunk.Content))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new { Row = row, Candidate = candidateById[row.VectorId] })
            .OrderByDescending(static item => item.Candidate.Score)
            .ThenBy(static item => item.Candidate.VectorId)
            .Take(limit)
            .Select(static item => CreateHit(item.Row, item.Candidate))
            .ToArray();
    }

    private static SearchHit CreateHit(HydratedRow row, FusedCandidate candidate)
    {
        var explanation = new List<string>(2);
        if (candidate.LexicalRank is { } lexicalRank)
        {
            explanation.Add($"lexical:rank={lexicalRank}");
        }
        if (candidate.SemanticRank is { } semanticRank)
        {
            explanation.Add($"semantic:rank={semanticRank}");
        }

        return new SearchHit(
            new PipelineRecordId(row.PipelineRecordId),
            row.SourceIdentity,
            row.Revision,
            Path.GetFileName(row.SourceIdentity),
            CreateSnippet(row.Content),
            candidate.Score,
            explanation);
    }

    private static string CreateSnippet(string content)
    {
        const int maxLength = 280;
        var compact = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= maxLength ? compact : $"{compact[..maxLength]}…";
    }

    private sealed record HydratedRow(
        long VectorId,
        Guid PipelineRecordId,
        long Revision,
        string SourceIdentity,
        string Content);
}
