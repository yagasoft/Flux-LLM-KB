using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>One finite retained ownership rule for new execution and result acceptance.</summary>
internal static class SqlSourceWorkEligibility
{
    // Expressions and aliases are application-owned constants, never caller input.
    internal static string Source(string revisionExpression, bool includePending = true, bool includeAdmission = true) => $$"""
        ({{revisionExpression}} IS NULL OR EXISTS
        (
            SELECT 1 FROM [SourceRevisions] AS [swInput]
            INNER JOIN [SourceRootConfigurations] AS [swRoot] ON [swRoot].[Id]=[swInput].[SourceRootId]
            LEFT JOIN [SourceRevisions] AS [swParent] ON [swParent].[Id]=[swInput].[ParentSourceRevisionId] AND [swInput].[OriginKind]<>0
            LEFT JOIN [SourceRevisions] AS [swArchive] ON [swArchive].[Id]=[swParent].[ParentSourceRevisionId] AND [swParent].[OriginKind]=1
            CROSS APPLY (SELECT CASE WHEN [swInput].[OriginKind]=0 THEN [swInput].[CanonicalPath]
                WHEN [swParent].[OriginKind]=0 THEN [swParent].[CanonicalPath] ELSE [swArchive].[CanonicalPath] END AS [Path]) AS [swPhysical]
            WHERE [swInput].[Id]={{revisionExpression}} AND [swInput].[SuppressedAtUtc] IS NULL
              AND [swRoot].[State]<>{{(int)SourceRootState.Deleting}}
              AND ([swInput].[OriginKind]=0 OR
                ([swParent].[SourceRootId]=[swInput].[SourceRootId] AND [swParent].[SuppressedAtUtc] IS NULL AND
                  ([swInput].[OriginKind]=1 AND [swParent].[OriginKind]=0 OR
                   [swInput].[OriginKind] IN (2,3) AND ([swParent].[OriginKind]=0 OR
                     [swParent].[OriginKind]=1 AND [swArchive].[OriginKind]=0 AND [swArchive].[SuppressedAtUtc] IS NULL
                       AND [swArchive].[SourceRootId]=[swInput].[SourceRootId]))))
              {{(includePending ? PendingScope : "")}}
              {{(includeAdmission ? AdmissionScope : "")}}
        ))
        """;

    private const string AdmissionScope = """
        AND EXISTS (SELECT 1 FROM OPENJSON(@sourceAdmittedRoots)
            WITH ([RootId] uniqueidentifier '$.RootId', [ConfigurationRevision] bigint '$.ConfigurationRevision') AS [swAdmission]
            WHERE [swAdmission].[RootId]=[swRoot].[Id] AND [swAdmission].[ConfigurationRevision]=[swRoot].[ConfigurationRevision])
        """;

    internal static SqlParameter AdmissionParameter(ISourceWorkAdmission? admission) =>
        new("@sourceAdmittedRoots", SqlDbType.NVarChar, -1)
        { Value = JsonSerializer.Serialize((admission ?? SourceWorkAdmission.Closed).ReadAdmittedRoots()) };

    private static readonly string PendingScope = $$"""
        AND NOT EXISTS
        (
            SELECT 1 FROM [SourcePendingChanges] AS [swPending]
            CROSS APPLY (SELECT CONCAT([swRoot].[CanonicalPath] COLLATE Latin1_General_100_CI_AS,
                CASE WHEN RIGHT([swRoot].[CanonicalPath],1)=N'\' THEN N'' ELSE N'\' END,
                REPLACE([swPending].[RelativePath] COLLATE Latin1_General_100_CI_AS,N'/',N'\')) AS [Path]) AS [swScope]
            WHERE [swPending].[SourceRootId]=[swInput].[SourceRootId]
              AND ([swPending].[ReasonFlags] & CAST({{SourcePendingReasons.RemovalPending}} AS bigint))<>0
              AND ([swPending].[RelativePath]=N'.' OR
                [swPhysical].[Path] COLLATE Latin1_General_100_CI_AS=[swScope].[Path] COLLATE Latin1_General_100_CI_AS OR
                [swPending].[IsSubtree]=1 AND LEFT([swPhysical].[Path],LEN([swScope].[Path])+1) COLLATE Latin1_General_100_CI_AS=
                    CONCAT([swScope].[Path],N'\') COLLATE Latin1_General_100_CI_AS)
        )
        """;

    internal static IQueryable<SourceRevisionEntity> Inputs(FluxKnowledgeDbContext db, ISourceWorkAdmission? admission = null,
        bool includePending = true, bool includeAdmission = true)
    {
        var sql = "SELECT [swRevision].* FROM [SourceRevisions] AS [swRevision] WHERE " + Source("[swRevision].[Id]", includePending, includeAdmission);
        return includeAdmission ? db.SourceRevisions.FromSqlRaw(sql, AdmissionParameter(admission)) : db.SourceRevisions.FromSqlRaw(sql);
    }

    internal static IQueryable<PipelineRecordEntity> Records(FluxKnowledgeDbContext db, ISourceWorkAdmission? admission = null,
        bool includePending = true, bool includeAdmission = true)
    {
        var sql = "SELECT [swRecord].* FROM [PipelineRecords] AS [swRecord] WHERE [swRecord].[IsDeleted]=0 AND " +
            Source("[swRecord].[SourceRevisionId]", includePending, includeAdmission);
        return includeAdmission ? db.PipelineRecords.FromSqlRaw(sql, AdmissionParameter(admission)) : db.PipelineRecords.FromSqlRaw(sql);
    }

    internal static Task<bool> IsAvailableAsync(FluxKnowledgeDbContext db, Guid recordId, CancellationToken ct, ISourceWorkAdmission? admission = null) =>
        Records(db, admission).AnyAsync(x => x.Id == recordId, ct);

    internal static async Task RequireAsync(FluxKnowledgeDbContext db, Guid recordId, CancellationToken ct, ISourceWorkAdmission? admission = null)
    {
        if (!await IsAvailableAsync(db, recordId, ct, admission).ConfigureAwait(false)) throw new SourceWorkDeferredException();
    }
}
