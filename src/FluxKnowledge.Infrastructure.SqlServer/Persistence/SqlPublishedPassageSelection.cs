using System.Runtime.CompilerServices;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>The SQL publication rule shared by lexical reads, vector previews and commits.</summary>
internal static class SqlPublishedPassageSelection
{
    // Caller holds the singleton publication fence and commits this with the eligibility mutation.
    internal static async Task AdvanceVersionAsync(FluxKnowledgeDbContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Corpus version changes require a publication transaction.");
        var state = await context.IndexState.SingleAsync(value => value.Id == 1, cancellationToken).ConfigureAwait(false);
        state.CorpusVersion = checked(state.CorpusVersion + 1);
        state.UpdatedAtUtc = now;
    }

    private const string Bindings = """
        LEFT JOIN [SourceRevisions] AS [retained] ON [record].[SourceRevisionId] = [retained].[Id]
        LEFT JOIN [DocumentPublications] AS [publication]
          ON [publication].[PipelineRecordId] = [record].[Id]
         AND [publication].[PipelineRecordRevision] = [record].[Revision]
         AND [publication].[DocumentInputSourceRevisionId] = [retained].[Id]
        LEFT JOIN [SourceRevisions] AS [owner] ON [publication].[OwnerSourceRevisionId] = [owner].[Id]
        LEFT JOIN [SourceRootConfigurations] AS [root]
          ON COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) = [root].[Id]
        """;

    private static readonly string Eligibility = $"""
        [record].[IsDeleted] = 0 AND [record].[CompletionCriteriaMet] = 1
        AND [record].[CurrentStage] = {(int)PipelineStage.Publish}
        AND [artifact].[Stage] = {(int)PipelineStage.CanonicalIndex}
        AND [chunk].[SourceRevision] = [artifact].[SourceRevision]
        AND [artifact].[SourceRevision] = [record].[Revision]
        AND [retained].[SuppressedAtUtc] IS NULL AND [owner].[SuppressedAtUtc] IS NULL
        AND ([record].[SourceRevisionId] IS NULL OR
             ([retained].[Id] IS NOT NULL AND [root].[State] <> {(int)SourceRootState.Deleting}))
        AND ([retained].[Id] IS NULL OR [retained].[OriginKind] NOT IN (2, 3) OR
             [publication].[OwnerSourceRevisionId] IS NOT NULL)
        AND ([record].[SourceRevisionId] IS NOT NULL OR [record].[Revision] = (
             SELECT MAX([current].[Revision]) FROM [PipelineRecords] AS [current]
             WHERE [current].[SourceIdentityId] = [record].[SourceIdentityId]
               AND [current].[CompletionCriteriaMet] = 1 AND [current].[CurrentStage] = {(int)PipelineStage.Publish}))
        """;

    // Only these application-owned SQL constants are inserted. All caller values remain parameters.
    internal static FormattableString Bind(FormattableString query) => FormattableStringFactory.Create(
        query.Format.Replace("/*publication-bindings*/", Bindings, StringComparison.Ordinal)
            .Replace("/*publication-eligibility*/", Eligibility, StringComparison.Ordinal), query.GetArguments());

    internal static FormattableString BindSources(FormattableString query) => FormattableStringFactory.Create(
        query.Format.Replace("/*publication-bindings*/", Bindings, StringComparison.Ordinal), query.GetArguments());

    internal static async Task AcquireFenceAsync(FluxKnowledgeDbContext context, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The publication fence requires a transaction.");
        _ = await context.Database.SqlQuery<int>($"""
            SELECT [Id] AS [Value] FROM [IndexState] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = 1
            """).SingleAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CorpusPublicationStamp> ReadStampAsync(FluxKnowledgeDbContext context, CancellationToken cancellationToken) =>
        await context.IndexState.AsNoTracking().Where(state => state.Id == 1)
            .Select(state => new CorpusPublicationStamp(state.CorpusEpoch, state.CorpusVersion)).SingleAsync(cancellationToken);

    internal static async Task<IReadOnlyList<long>> ReadVectorIdsAsync(
        FluxKnowledgeDbContext context, CancellationToken cancellationToken) =>
        await QueryVectors(context).OrderBy(vector => vector.VectorId).Select(vector => vector.VectorId).ToArrayAsync(cancellationToken);

    internal static async Task<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(
        FluxKnowledgeDbContext context, CancellationToken cancellationToken)
    {
        var rows = await QueryVectors(context).OrderBy(vector => vector.VectorId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => new CanonicalVector(row.VectorId, row.TextChunkId, row.ModelFingerprint,
            row.Dimensions, row.Values, row.TextChunkContentHash, row.PayloadChecksum, row.SourceRevision)).ToArray();
    }

    private static IQueryable<VectorRow> QueryVectors(FluxKnowledgeDbContext context) =>
        context.Database.SqlQuery<VectorRow>(Bind($"""
            SELECT [vector].[VectorId], [vector].[TextChunkId], [vector].[ModelFingerprint],
                   [vector].[Dimensions], [vector].[Values], [vector].[TextChunkContentHash],
                   [vector].[PayloadChecksum], [vector].[SourceRevision]
            FROM [Vectors] AS [vector]
            INNER JOIN [TextChunks] AS [chunk] ON [vector].[TextChunkId] = [chunk].[Id]
            INNER JOIN [Artifacts] AS [artifact] ON [chunk].[ArtifactId] = [artifact].[Id]
            INNER JOIN [PipelineRecords] AS [record] ON [artifact].[PipelineRecordId] = [record].[Id]
            /*publication-bindings*/
            WHERE /*publication-eligibility*/
              AND [vector].[IsDeleted] = 0 AND [vector].[SourceRevision] = [chunk].[SourceRevision]
              AND [vector].[TextChunkContentHash] COLLATE Latin1_General_100_BIN2 =
                  [chunk].[ContentHash] COLLATE Latin1_General_100_BIN2
              AND ([vector].[SearchInputHash] IS NULL OR [vector].[SearchInputHash] COLLATE Latin1_General_100_BIN2 =
                  [chunk].[SearchInputHash] COLLATE Latin1_General_100_BIN2)
            """));

    private sealed class VectorRow
    {
        public long VectorId { get; init; }
        public long TextChunkId { get; init; }
        public string ModelFingerprint { get; init; } = "";
        public int Dimensions { get; init; }
        public byte[] Values { get; init; } = [];
        public string TextChunkContentHash { get; init; } = "";
        public string PayloadChecksum { get; init; } = "";
        public long SourceRevision { get; init; }
    }
}
