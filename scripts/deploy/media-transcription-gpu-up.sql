CREATE TABLE [MediaTranscriptionGpuRequests] (
    [MiniTaskId] uniqueidentifier NOT NULL,
    [ParentJobId] uniqueidentifier NOT NULL,
    [PipelineRecordId] uniqueidentifier NOT NULL,
    [SourceRevision] bigint NOT NULL,
    [RetainedSourceRevisionId] uniqueidentifier NOT NULL,
    [ContentSha256] char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [ModelRuntimeKey] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [SettingsFingerprint] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [ExecutionPhase] int NOT NULL,
    [ExecutionInstanceId] uniqueidentifier NULL,
    [ExecutionBatchId] uniqueidentifier NULL,
    [ExecutionDispatchId] uniqueidentifier NULL,
    [ExecutionAdmissionGeneration] bigint NULL,
    [SupervisorInstanceId] uniqueidentifier NULL,
    [SupervisorProcessId] int NULL,
    [SupervisorProcessStartedAtUtc] datetimeoffset NULL,
    [MachineFingerprint] char(64) NULL,
    [ClaimedAtUtc] datetimeoffset NULL,
    [DeadlineAtUtc] datetimeoffset NULL,
    [NativeProcessId] int NULL,
    [NativeProcessStartedAtUtc] datetimeoffset NULL,
    [CleanupConfirmedAtUtc] datetimeoffset NULL,
    [CleanupKind] int NULL,
    [ResultJson] nvarchar(max) NULL,
    [ResultDigest] varbinary(32) NULL,
    [RequeuedAtUtc] datetimeoffset NULL,
    [CreatedAtUtc] datetimeoffset NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_MediaTranscriptionGpuRequests] PRIMARY KEY ([MiniTaskId]),
    CONSTRAINT [CK_MediaTranscriptionGpuRequests_Cleanup] CHECK (([ExecutionPhase]<3 AND [CleanupConfirmedAtUtc] IS NULL AND [CleanupKind] IS NULL) OR
([ExecutionPhase]>=3 AND [CleanupConfirmedAtUtc] IS NOT NULL AND [CleanupKind] IS NOT NULL AND
 ([CleanupKind]=0 AND [NativeProcessId] IS NULL OR [CleanupKind]=1 AND [NativeProcessId] IS NOT NULL))),
    CONSTRAINT [CK_MediaTranscriptionGpuRequests_ContentSha256] CHECK (LEN([ContentSha256]) = 64 AND [ContentSha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'),
    CONSTRAINT [CK_MediaTranscriptionGpuRequests_Execution] CHECK (([ExecutionPhase]=0 AND [ExecutionInstanceId] IS NULL) OR
([ExecutionPhase]>0 AND [ExecutionInstanceId] IS NOT NULL AND [ExecutionBatchId] IS NOT NULL AND
 [ExecutionDispatchId] IS NOT NULL AND [ExecutionAdmissionGeneration] IS NOT NULL AND [ExecutionAdmissionGeneration]>0 AND
 [SupervisorInstanceId] IS NOT NULL AND [SupervisorProcessId] IS NOT NULL AND [SupervisorProcessId]>0 AND
 [SupervisorProcessStartedAtUtc] IS NOT NULL AND [MachineFingerprint] IS NOT NULL AND
 [ClaimedAtUtc] IS NOT NULL AND [DeadlineAtUtc] IS NOT NULL AND [DeadlineAtUtc]>[ClaimedAtUtc])),
    CONSTRAINT [CK_MediaTranscriptionGpuRequests_ModelRuntimeKey] CHECK (DATALENGTH([ModelRuntimeKey]) > 0 AND UNICODE(RIGHT([ModelRuntimeKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)),
    CONSTRAINT [CK_MediaTranscriptionGpuRequests_Native] CHECK (([NativeProcessId] IS NULL AND [NativeProcessStartedAtUtc] IS NULL AND [ExecutionPhase]<>2) OR
([NativeProcessId] IS NOT NULL AND [NativeProcessId]>0 AND [NativeProcessStartedAtUtc] IS NOT NULL AND [ExecutionPhase]>=2)),
    CONSTRAINT [CK_MediaTranscriptionGpuRequests_Phase] CHECK ([ExecutionPhase] BETWEEN 0 AND 4),
    CONSTRAINT [CK_MediaTranscriptionGpuRequests_Requeue] CHECK ([RequeuedAtUtc] IS NULL OR [ExecutionPhase]=4),
    CONSTRAINT [CK_MediaTranscriptionGpuRequests_Result] CHECK (([ResultJson] IS NULL AND [ResultDigest] IS NULL AND [ExecutionPhase]<4) OR
([ExecutionPhase]>=3 AND [ResultJson] IS NOT NULL AND DATALENGTH([ResultJson]) BETWEEN 1 AND 4194304 AND
 [ResultDigest] IS NOT NULL AND DATALENGTH([ResultDigest])=32)),
    CONSTRAINT [CK_MediaTranscriptionGpuRequests_SettingsFingerprint] CHECK (DATALENGTH([SettingsFingerprint]) > 0 AND UNICODE(RIGHT([SettingsFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288))
);
GO

CREATE INDEX [IX_MediaTranscriptionGpuRequests_ExecutionPhase_RequeuedAtUtc_CreatedAtUtc] ON [MediaTranscriptionGpuRequests] ([ExecutionPhase], [RequeuedAtUtc], [CreatedAtUtc]);
GO

CREATE UNIQUE INDEX [IX_MediaTranscriptionGpuRequests_ParentJobId_RetainedSourceRevisionId] ON [MediaTranscriptionGpuRequests] ([ParentJobId], [RetainedSourceRevisionId]);
GO

CREATE INDEX [IX_MediaTranscriptionGpuRequests_PipelineRecordId_SourceRevision] ON [MediaTranscriptionGpuRequests] ([PipelineRecordId], [SourceRevision]);
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261119125051_AddMediaTranscriptionGpuRequests', N'10.0.10');
GO

