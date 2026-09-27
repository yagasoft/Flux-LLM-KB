using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Only jobs bound to the active, previously published worklist may bypass paused intake.</summary>
internal static class SqlCorpusRebuildEligibility
{
    internal static string ActiveJob(string jobId) => $$"""
        EXISTS (
            SELECT 1 FROM [IndexState] AS [rebuildState]
            INNER JOIN [CorpusRebuildWorkItems] AS [rebuildItem] ON [rebuildItem].[OperationId] = [rebuildState].[CorpusRebuildOperationId]
            INNER JOIN [Jobs] AS [rebuildJob] ON [rebuildJob].[Id] = {{jobId}}
            INNER JOIN [PipelineRecords] AS [rebuildRecord] ON [rebuildRecord].[Id] = [rebuildJob].[PipelineRecordId]
            LEFT JOIN [SourceRevisions] AS [rebuildInput] ON [rebuildInput].[Id] = [rebuildRecord].[SourceRevisionId]
            LEFT JOIN [SourceRootConfigurations] AS [rebuildRoot] ON [rebuildRoot].[Id] = [rebuildInput].[SourceRootId]
            LEFT JOIN [SourceRevisions] AS [rebuildOwner] ON [rebuildOwner].[Id] = [rebuildInput].[ParentSourceRevisionId]
            WHERE [rebuildState].[Id] = 1 AND [rebuildItem].[State] = 1
              AND [rebuildItem].[PipelineRecordId] = [rebuildJob].[PipelineRecordId]
              AND [rebuildItem].[SourceRevision] = [rebuildJob].[SourceRevision]
              AND [rebuildRecord].[Revision] = [rebuildItem].[SourceRevision] AND [rebuildRecord].[IsDeleted] = 0
              AND [rebuildInput].[SuppressedAtUtc] IS NULL AND [rebuildOwner].[SuppressedAtUtc] IS NULL
              AND ([rebuildRecord].[SourceRevisionId] IS NULL OR [rebuildRoot].[State] IN ({{(int)SourceRootState.Enabled}}, {{(int)SourceRootState.Paused}}))
              AND (([rebuildJob].[Id] = [rebuildItem].[EmbeddingJobId] AND [rebuildJob].[Stage] = {{(int)PipelineStage.Embed}}
                     AND [rebuildJob].[Operation] = '{{PipelineOperations.Embed}}')
                OR ([rebuildJob].[Stage] = {{(int)PipelineStage.Publish}} AND [rebuildJob].[Operation] = '{{PipelineOperations.Publish}}'
                    AND EXISTS (SELECT 1 FROM [OutboxMessages] AS [rebuildParent]
                        INNER JOIN [OutboxMessages] AS [rebuildNext] ON [rebuildNext].[JobId] = [rebuildJob].[Id]
                        WHERE [rebuildParent].[Id] = [rebuildItem].[DispatchMessageId]
                          AND [rebuildParent].[JobId] = [rebuildItem].[EmbeddingJobId]
                          AND [rebuildParent].[DispatchedAtUtc] IS NOT NULL AND [rebuildParent].[CompletedArtifactId] IS NOT NULL
                          AND [rebuildNext].[PipelineRecordId] = [rebuildItem].[PipelineRecordId]
                          AND [rebuildNext].[SourceRevision] = [rebuildItem].[SourceRevision]
                          AND [rebuildNext].[DispatchGeneration] = [rebuildParent].[DispatchGeneration] + 1)))
        )
        """;

    internal static string Admission(string jobId) => $"""
        (NOT EXISTS (SELECT 1 FROM [IndexState] WHERE [Id] = 1 AND [CorpusRebuildOperationId] IS NOT NULL)
         OR {ActiveJob(jobId)})
        """;

    internal static string DeploymentAdmission(string jobId) => $"""
        ((@deploymentHeld = 0 AND NOT EXISTS (SELECT 1 FROM [IndexState] WHERE [Id] = 1 AND [CorpusRebuildOperationId] IS NOT NULL)) OR
         (EXISTS (SELECT 1 FROM [IndexState] WHERE [Id] = 1 AND [CorpusRebuildOperationId] = @permittedRebuildOperationId)
          AND {ActiveJob(jobId)}))
        """;
}
