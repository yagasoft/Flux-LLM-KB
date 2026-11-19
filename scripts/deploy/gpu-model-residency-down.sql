SET XACT_ABORT ON;
BEGIN TRANSACTION;
BEGIN TRY
    EXEC sys.sp_executesql N'IF EXISTS (SELECT 1 FROM [GpuModelResidencies] WITH (TABLOCKX,HOLDLOCK))
    OR EXISTS (SELECT 1 FROM [GpuModelDetachmentReceipts] WITH (TABLOCKX,HOLDLOCK))
    OR EXISTS (SELECT 1 FROM [GpuCapacitySlots] WITH (TABLOCKX,HOLDLOCK) WHERE [State]<>0 OR [ActiveBatchId] IS NOT NULL)
    OR EXISTS (SELECT 1 FROM [EmbeddingGpuRequests] WITH (TABLOCKX,HOLDLOCK) WHERE [ResidentDetachmentDispatchId] IS NOT NULL)
    OR EXISTS (SELECT 1 FROM [MediaTranscriptionGpuRequests] WITH (TABLOCKX,HOLDLOCK) WHERE [ExecutionPhase]=4 AND [CleanupConfirmedAtUtc] IS NULL)
    THROW 51000, ''gpu-residency-downgrade-would-discard-evidence'', 1;';
    EXEC sys.sp_executesql N'ALTER TABLE [EmbeddingGpuRequests] DROP CONSTRAINT [FK_EmbeddingGpuRequests_GpuModelDetachmentReceipts_ResidentDetachmentDispatchId];';
    EXEC sys.sp_executesql N'DROP TABLE [GpuModelDetachmentReceipts];';
    EXEC sys.sp_executesql N'DROP TABLE [GpuModelResidencies];';
    EXEC sys.sp_executesql N'ALTER TABLE [MediaTranscriptionGpuRequests] DROP CONSTRAINT [CK_MediaTranscriptionGpuRequests_Cleanup];';
    EXEC sys.sp_executesql N'DROP INDEX [IX_EmbeddingGpuRequests_ResidentDetachmentDispatchId] ON [EmbeddingGpuRequests];';
    EXEC sys.sp_executesql N'ALTER TABLE [EmbeddingGpuRequests] DROP CONSTRAINT [CK_EmbeddingGpuRequests_Completion];';
    EXEC sys.sp_executesql N'ALTER TABLE [EmbeddingGpuRequests] DROP CONSTRAINT [CK_EmbeddingGpuRequests_ResidentDetachment];';
    EXEC sys.sp_executesql N'DECLARE @var nvarchar(max);
SELECT @var = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N''[EmbeddingGpuRequests]'') AND [c].[name] = N''ResidentDetachmentDispatchId'');
IF @var IS NOT NULL EXEC(N''ALTER TABLE [EmbeddingGpuRequests] DROP CONSTRAINT '' + @var + '';'');
ALTER TABLE [EmbeddingGpuRequests] DROP COLUMN [ResidentDetachmentDispatchId];';
    EXEC sys.sp_executesql N'ALTER TABLE [MediaTranscriptionGpuRequests] ADD CONSTRAINT [CK_MediaTranscriptionGpuRequests_Cleanup] CHECK (([ExecutionPhase]<3 AND [CleanupConfirmedAtUtc] IS NULL AND [CleanupKind] IS NULL) OR
([ExecutionPhase]>=3 AND [CleanupConfirmedAtUtc] IS NOT NULL AND [CleanupKind] IS NOT NULL AND
 ([CleanupKind]=0 AND [NativeProcessId] IS NULL OR [CleanupKind]=1 AND [NativeProcessId] IS NOT NULL)));';
    EXEC sys.sp_executesql N'ALTER TABLE [EmbeddingGpuRequests] ADD CONSTRAINT [CK_EmbeddingGpuRequests_Completion] CHECK ([State] = 0 OR ([State] = 1 AND [ResultDigest] IS NOT NULL AND [ExecutorInstanceId] IS NOT NULL) OR ([State] = 2 AND [NativeCleanupConfirmed] = 1));';
    EXEC sys.sp_executesql N'DELETE FROM [__EFMigrationsHistory]
WHERE [MigrationId] = N''20261119201434_AddGpuModelResidencies'';';
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
