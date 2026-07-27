using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed class SqlFullTextSearch(IDbContextFactory<FluxKnowledgeDbContext> contextFactory) : ILexicalSearch
{
    public async ValueTask<IReadOnlyList<RankedCandidate>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var matches = await context.Database.SqlQuery<FullTextCandidate>(
                $"""
                 SELECT TOP ({limit}) [vector].[VectorId], [fulltext].[RANK] AS [Rank]
                 FROM [Vectors] AS [vector]
                 INNER JOIN [TextChunks] AS [chunk]
                    ON [vector].[TextChunkId] = [chunk].[Id]
                   AND [vector].[SourceRevision] = [chunk].[SourceRevision]
                 INNER JOIN [Artifacts] AS [artifact]
                    ON [chunk].[ArtifactId] = [artifact].[Id]
                   AND [chunk].[SourceRevision] = [artifact].[SourceRevision]
                 INNER JOIN FREETEXTTABLE([Artifacts], [SearchText], {query}) AS [fulltext]
                    ON [artifact].[Id] = [fulltext].[KEY]
                 INNER JOIN [PipelineRecords] AS [record]
                    ON [artifact].[PipelineRecordId] = [record].[Id]
                   AND [artifact].[SourceRevision] = [record].[Revision]
                 WHERE [vector].[IsDeleted] = 0
                   AND [record].[IsDeleted] = 0
                   AND [vector].[TextChunkContentHash] = [chunk].[ContentHash]
                   AND [record].[Revision] = (
                        SELECT MAX([current].[Revision])
                        FROM [PipelineRecords] AS [current]
                        WHERE [current].[SourceIdentityId] = [record].[SourceIdentityId])
                 ORDER BY [fulltext].[RANK] DESC, [vector].[VectorId] ASC
                 """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return matches
            .Select(static (match, index) => new RankedCandidate(match.VectorId, index + 1))
            .ToArray();
    }

    private sealed class FullTextCandidate
    {
        public long VectorId { get; init; }
        public int Rank { get; init; }
    }
}
