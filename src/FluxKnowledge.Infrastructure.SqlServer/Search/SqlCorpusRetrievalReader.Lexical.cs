namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed partial class SqlCorpusRetrievalReader
{
    // The scope is applied before the candidate budget. Ranked keys let SQL stop
    // after enough eligible rows, without inspecting every current body.
    private const string KeywordKeySelection = """
        INSERT INTO #corpusChosen ([ChunkId], [FullTextRank])
        SELECT TOP (@budget) [fulltext].[KEY], [fulltext].[RANK]
        FROM FREETEXTTABLE([TextChunks], [SearchText], @query) AS [fulltext]
        INNER JOIN [TextChunks] AS [scoped] WITH (FORCESEEK) ON [scoped].[Id] = [fulltext].[KEY]
        INNER JOIN #corpusScope AS [eligible]
            ON [scoped].[ArtifactId] = [eligible].[ArtifactId] AND [scoped].[SourceRevision] = [eligible].[SourceRevision]
        WHERE [scoped].[Length] BETWEEN 1 AND 2048
        ORDER BY [fulltext].[RANK] DESC, [fulltext].[KEY]
        OPTION (MAXDOP 1);
        """;

    // Literal mode deliberately performs exhaustive ordinal substring discovery.
    // No Full-Text function occurs in its command, so a missing FT index cannot
    // make an explicit literal search unavailable.
    private const string LiteralKeySelection = """
        SELECT [scoped].[Id] AS [ChunkId] INTO #corpusChunks
        FROM #corpusScope AS [eligible]
        INNER LOOP JOIN [TextChunks] AS [scoped] WITH (INDEX([IX_TextChunks_ArtifactId_SourceRevision]), FORCESEEK)
            ON [scoped].[ArtifactId] = [eligible].[ArtifactId] AND [scoped].[SourceRevision] = [eligible].[SourceRevision];
        CREATE UNIQUE CLUSTERED INDEX [IX_chunks] ON #corpusChunks ([ChunkId]);
        INSERT INTO #corpusChosen ([ChunkId], [FullTextRank])
        SELECT TOP (@budget) [chunk].[Id], 0
        FROM #corpusChunks AS [requested]
        INNER LOOP JOIN [TextChunks] AS [chunk] WITH (FORCESEEK) ON [chunk].[Id] = [requested].[ChunkId]
        WHERE [chunk].[Length] BETWEEN 1 AND 2048
          AND CHARINDEX(@query, [chunk].[Content] COLLATE Latin1_General_100_BIN2) > 0
        ORDER BY [chunk].[Id]
        OPTION (LOOP JOIN, FORCE ORDER, MAXDOP 4);
        """;
}
