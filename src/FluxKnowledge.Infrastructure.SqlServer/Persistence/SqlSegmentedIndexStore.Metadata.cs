using System.Data;
using System.Runtime.CompilerServices;
using FluxKnowledge.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSegmentedIndexStore
{
    private const int MetadataPageSize = 16_384;

    internal static IQueryable<MemberRow> QueryRootCurrentWindow(FluxKnowledgeDbContext context,
        Entities.IndexPublicationCandidateEntity candidate, long after, long through, bool composable = false)
    {
        FormattableString query = $"""
            SELECT [member].[VectorId], [vector].[PayloadChecksum], [member].[SourceIdentityId],
                [member].[OwnerSourceRevisionId], [member].[SourceRootId]
            FROM [CurrentIndexMembers] AS [member] WITH (FORCESEEK)
            INNER JOIN [Vectors] AS [vector] ON [vector].[VectorId] = [member].[VectorId]
            WHERE [member].[CorpusEpoch] = {candidate.CorpusEpoch}
              AND [member].[VectorId] > {after} AND [member].[VectorId] <= {through}
              AND EXISTS (SELECT 1 FROM OPENJSON({candidate.AffectedScopesJson}) WITH ([Kind] int '$.Kind', [Id] uniqueidentifier '$.Id') AS [scope]
                WHERE ([scope].[Kind] = 1 AND [scope].[Id] = [member].[SourceIdentityId])
                   OR ([scope].[Kind] = 2 AND [scope].[Id] = [member].[OwnerSourceRevisionId])
                   OR ([scope].[Kind] = 3 AND [scope].[Id] = [member].[SourceRootId]))
            """;
        return context.Database.SqlQuery<MemberRow>(OrderedMetadata(query, "member", composable));
    }

    private static bool IsRootRefresh(Entities.IndexPublicationCandidateEntity candidate) =>
        candidate.DraftGenerationId is null && System.Text.Json.JsonSerializer.Deserialize<IndexAffectedScope[]>(candidate.AffectedScopesJson)!
            .Any(value => value.Kind == 3);

    internal static IQueryable<MemberRow> QueryRootEligibleWindow(FluxKnowledgeDbContext context, string scopes, long after, long through,
        bool composable = false)
    {
        // Root capture uses bounded raw-vector windows.
        // Start with that range instead of expanding the whole root per window.
        var joins = """
            FROM [Vectors] AS [vector] WITH (FORCESEEK)
            INNER LOOP JOIN [TextChunks] AS [chunk] ON [chunk].[Id] = [vector].[TextChunkId]
            INNER LOOP JOIN [Artifacts] AS [artifact] ON [artifact].[Id] = [chunk].[ArtifactId]
            INNER LOOP JOIN [PipelineRecords] AS [record] ON [record].[Id] = [artifact].[PipelineRecordId]
            /*publication-bindings*/
            """;
        var query = SqlPublishedPassageSelection.Bind(FormattableStringFactory.Create("""
            SELECT [vector].[VectorId], [vector].[PayloadChecksum], [record].[SourceIdentityId],
                COALESCE([owner].[Id], CASE WHEN [retained].[OriginKind] <> 0 THEN [retained].[ParentSourceRevisionId] END, [retained].[Id]) AS [OwnerSourceRevisionId],
                COALESCE([owner].[SourceRootId], [retained].[SourceRootId]) AS [SourceRootId]
            """ + "\n" + joins + "\n" + """
            WHERE /*publication-eligibility*/ AND
            """ + " " + ScopeFilter + """
              AND [vector].[VectorId] > {1} AND [vector].[VectorId] <= {2} AND [vector].[IsDeleted] = 0
              AND [vector].[SourceRevision] = [chunk].[SourceRevision]
              AND [vector].[TextChunkContentHash] COLLATE Latin1_General_100_BIN2 = [chunk].[ContentHash] COLLATE Latin1_General_100_BIN2
              AND ([vector].[SearchInputHash] IS NULL OR [vector].[SearchInputHash] COLLATE Latin1_General_100_BIN2 = [chunk].[SearchInputHash] COLLATE Latin1_General_100_BIN2)
            """, scopes, after, through));
        return context.Database.SqlQuery<MemberRow>(OrderedMetadata(query, "vector", composable));
    }

    private static FormattableString OrderedMetadata(FormattableString query, string alias, bool composable) =>
        FormattableStringFactory.Create(query.Format + (composable ? "" :
            "\nORDER BY [" + alias + "].[VectorId]\nOPTION (LOOP JOIN, FORCE ORDER, MAXDOP 1)"), query.GetArguments());

    internal static async Task CreateRootEligibilityMapAsync(FluxKnowledgeDbContext context, string scopes, CancellationToken token)
    {
        if (context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("The root eligibility map requires the serializable publication transaction.");
        // CREATE must be a parameterless session batch. Creating this table
        // inside sp_executesql would discard it before the later windows run.
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE #RootEligibleArtifacts (
                [ArtifactId] uniqueidentifier NOT NULL PRIMARY KEY,
                [SourceRevision] bigint NOT NULL,
                [SourceIdentityId] uniqueidentifier NOT NULL,
                [OwnerSourceRevisionId] uniqueidentifier NULL,
                [SourceRootId] uniqueidentifier NULL
            );
            """, token);
        var insert = SqlPublishedPassageSelection.Bind(FormattableStringFactory.Create("""
            INSERT #RootEligibleArtifacts (ArtifactId,SourceRevision,SourceIdentityId,OwnerSourceRevisionId,SourceRootId)
            SELECT [artifact].[Id], [artifact].[SourceRevision], [record].[SourceIdentityId],
                COALESCE([owner].[Id], CASE WHEN [retained].[OriginKind] <> 0 THEN [retained].[ParentSourceRevisionId] END, [retained].[Id]),
                COALESCE([owner].[SourceRootId], [retained].[SourceRootId])
            FROM [Artifacts] AS [artifact]
            INNER JOIN [PipelineRecords] AS [record] ON [record].[Id] = [artifact].[PipelineRecordId]
            /*publication-bindings*/
            WHERE /*publication-artifact-eligibility*/ AND
            """ + " " + ScopeFilter, scopes));
        await context.Database.ExecuteSqlInterpolatedAsync(insert, token);
    }

    internal static IQueryable<MemberRow> QueryMappedRootEligibleWindow(FluxKnowledgeDbContext context, long after, long through,
        bool composable = false)
    {
        FormattableString query = $"""
            SELECT [vector].[VectorId], [vector].[PayloadChecksum], [eligible].[SourceIdentityId],
                [eligible].[OwnerSourceRevisionId], [eligible].[SourceRootId]
            FROM [Vectors] AS [vector] WITH (FORCESEEK)
            INNER LOOP JOIN [TextChunks] AS [chunk] ON [chunk].[Id] = [vector].[TextChunkId]
            INNER LOOP JOIN #RootEligibleArtifacts AS [eligible]
              ON [eligible].[ArtifactId] = [chunk].[ArtifactId] AND [eligible].[SourceRevision] = [chunk].[SourceRevision]
            WHERE [vector].[VectorId] > {after} AND [vector].[VectorId] <= {through} AND [vector].[IsDeleted] = 0
              AND [vector].[SourceRevision] = [chunk].[SourceRevision]
              AND [vector].[TextChunkContentHash] COLLATE Latin1_General_100_BIN2 = [chunk].[ContentHash] COLLATE Latin1_General_100_BIN2
              AND ([vector].[SearchInputHash] IS NULL OR [vector].[SearchInputHash] COLLATE Latin1_General_100_BIN2 = [chunk].[SearchInputHash] COLLATE Latin1_General_100_BIN2)
            """;
        return context.Database.SqlQuery<MemberRow>(OrderedMetadata(query, "vector", composable));
    }

    private static async IAsyncEnumerable<MemberRow[]> ReadMemberPagesAsync(FluxKnowledgeDbContext context,
        IQueryable<MemberRow> query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ADO keeps this ordered read streaming even when EF's retry strategy buffers
        // query results. The owning operation still retries the complete transaction.
        await using var command = query.CreateDbCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandTimeout = context.Database.GetCommandTimeout() ?? 30;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        var vector = reader.GetOrdinal(nameof(MemberRow.VectorId));
        var checksum = reader.GetOrdinal(nameof(MemberRow.PayloadChecksum));
        var source = reader.GetOrdinal(nameof(MemberRow.SourceIdentityId));
        var owner = reader.GetOrdinal(nameof(MemberRow.OwnerSourceRevisionId));
        var root = reader.GetOrdinal(nameof(MemberRow.SourceRootId));
        var page = new List<MemberRow>(MetadataPageSize);
        while (await reader.ReadAsync(cancellationToken))
        {
            page.Add(new()
            {
                VectorId = reader.GetInt64(vector), PayloadChecksum = reader.GetString(checksum),
                SourceIdentityId = reader.GetGuid(source),
                OwnerSourceRevisionId = reader.IsDBNull(owner) ? null : reader.GetGuid(owner),
                SourceRootId = reader.IsDBNull(root) ? null : reader.GetGuid(root)
            });
            if (page.Count != MetadataPageSize) continue;
            yield return page.ToArray();
            page.Clear();
        }
        if (page.Count != 0) yield return page.ToArray();
    }

    private static async Task BulkAppendMembersAsync(FluxKnowledgeDbContext context, Guid candidateId, int phase,
        MemberRow[] page, CancellationToken cancellationToken)
    {
        if (page.Length == 0) return; // The caller still commits the empty window's ordinal receipt.
        // Only bounded preview metadata crosses connections. Check constraints and
        // the candidate FK stay enforced; no canonical vector/source FK is added.
        using var rows = new DataTable();
        rows.Columns.Add("CandidateId", typeof(Guid));
        rows.Columns.Add("Phase", typeof(int));
        rows.Columns.Add("VectorId", typeof(long));
        rows.Columns.Add("PayloadChecksum", typeof(string));
        rows.Columns.Add("SourceIdentityId", typeof(Guid));
        rows.Columns.Add("OwnerSourceRevisionId", typeof(Guid));
        rows.Columns.Add("SourceRootId", typeof(Guid));
        foreach (var member in page)
            rows.Rows.Add(candidateId, phase, member.VectorId, member.PayloadChecksum, member.SourceIdentityId,
                (object?)member.OwnerSourceRevisionId ?? DBNull.Value, (object?)member.SourceRootId ?? DBNull.Value);
        using var bulk = new SqlBulkCopy((SqlConnection)context.Database.GetDbConnection(), SqlBulkCopyOptions.CheckConstraints,
            (SqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction())
        {
            DestinationTableName = "[IndexCandidateMembers]", BatchSize = MetadataPageSize,
            BulkCopyTimeout = context.Database.GetCommandTimeout() ?? 30
        };
        foreach (DataColumn column in rows.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(rows, cancellationToken);
    }
}
