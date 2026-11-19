ALTER TABLE [MediaTranscriptionGpuRequests] DROP CONSTRAINT [CK_MediaTranscriptionGpuRequests_Cleanup];
GO

ALTER TABLE [EmbeddingGpuRequests] DROP CONSTRAINT [CK_EmbeddingGpuRequests_Completion];
GO

ALTER TABLE [EmbeddingGpuRequests] ADD [ResidentDetachmentDispatchId] uniqueidentifier NULL;
GO

CREATE TABLE [GpuModelResidencies] (
    [ResidencyId] uniqueidentifier NOT NULL,
    [SlotKey] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [RuntimeKey] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [SettingsFingerprint] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [ModelKey] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [OwnerProcessId] int NOT NULL,
    [OwnerStartedAtUtc] datetimeoffset NOT NULL,
    [MachineFingerprint] char(64) NOT NULL,
    [ChildJobId] uniqueidentifier NULL,
    [ChildProcessId] int NULL,
    [ChildStartedAtUtc] datetimeoffset NULL,
    [State] int NOT NULL,
    [Version] bigint NOT NULL,
    [BorrowedDispatchId] uniqueidentifier NULL,
    [BorrowedExecutionInstanceId] uniqueidentifier NULL,
    [IdleUntilUtc] datetimeoffset NULL,
    [ReleasedAtUtc] datetimeoffset NULL,
    [ReleaseKind] int NULL,
    CONSTRAINT [PK_GpuModelResidencies] PRIMARY KEY ([ResidencyId]),
    CONSTRAINT [CK_GpuModelResidencies_Child] CHECK (([ChildJobId] IS NULL AND [ChildProcessId] IS NULL AND [ChildStartedAtUtc] IS NULL) OR
([ChildJobId] IS NOT NULL AND [ChildJobId]=[ResidencyId] AND [ChildProcessId] IS NOT NULL AND
 [ChildProcessId]>0 AND [ChildStartedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[ChildStartedAtUtc])=0 AND
 [ChildStartedAtUtc]>=[OwnerStartedAtUtc])),
    CONSTRAINT [CK_GpuModelResidencies_Deadline] CHECK ([IdleUntilUtc] IS NULL OR (DATEPART(TZOFFSET,[IdleUntilUtc])=0 AND [IdleUntilUtc]>=[OwnerStartedAtUtc])),
    CONSTRAINT [CK_GpuModelResidencies_Identity] CHECK ([ResidencyId] <> '00000000-0000-0000-0000-000000000000' AND [Version] > 0 AND [OwnerProcessId] > 0 AND
DATEPART(TZOFFSET,[OwnerStartedAtUtc])=0 AND [OwnerStartedAtUtc] > '0001-01-01T00:00:00+00:00'),
    CONSTRAINT [CK_GpuModelResidencies_MachineFingerprint] CHECK (LEN([MachineFingerprint]) = 64 AND [MachineFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'),
    CONSTRAINT [CK_GpuModelResidencies_ModelKey] CHECK (DATALENGTH([ModelKey]) > 0 AND UNICODE(RIGHT([ModelKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)),
    CONSTRAINT [CK_GpuModelResidencies_Phase] CHECK (([State]=0 AND [BorrowedDispatchId] IS NOT NULL AND [BorrowedDispatchId]<>'00000000-0000-0000-0000-000000000000'
 AND [BorrowedExecutionInstanceId] IS NOT NULL AND [BorrowedExecutionInstanceId]<>'00000000-0000-0000-0000-000000000000'
 AND [IdleUntilUtc] IS NULL) OR
([BorrowedDispatchId] IS NULL AND [BorrowedExecutionInstanceId] IS NULL AND
 (([State]=1 AND [IdleUntilUtc] IS NOT NULL) OR [State]=2 OR ([State] IN (3,4) AND [IdleUntilUtc] IS NULL)))),
    CONSTRAINT [CK_GpuModelResidencies_Release] CHECK (([State]<>4 AND [ReleasedAtUtc] IS NULL AND [ReleaseKind] IS NULL) OR
([State]=4 AND [ReleasedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[ReleasedAtUtc])=0 AND
 [ReleasedAtUtc]>=[OwnerStartedAtUtc] AND ([ChildStartedAtUtc] IS NULL OR [ReleasedAtUtc]>=[ChildStartedAtUtc]) AND
 [ReleaseKind] IS NOT NULL AND
 (([ChildJobId] IS NULL AND [ReleaseKind] IN (0,2)) OR ([ChildJobId] IS NOT NULL AND [ReleaseKind]=1)))),
    CONSTRAINT [CK_GpuModelResidencies_RuntimeKey] CHECK (DATALENGTH([RuntimeKey]) > 0 AND UNICODE(RIGHT([RuntimeKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)),
    CONSTRAINT [CK_GpuModelResidencies_SettingsFingerprint] CHECK (DATALENGTH([SettingsFingerprint]) > 0 AND UNICODE(RIGHT([SettingsFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)),
    CONSTRAINT [CK_GpuModelResidencies_SlotKey] CHECK (DATALENGTH([SlotKey]) > 0 AND UNICODE(RIGHT([SlotKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)),
    CONSTRAINT [FK_GpuModelResidencies_GpuCapacitySlots_SlotKey] FOREIGN KEY ([SlotKey]) REFERENCES [GpuCapacitySlots] ([SlotKey]) ON DELETE NO ACTION
);
GO

CREATE TABLE [GpuModelDetachmentReceipts] (
    [DispatchId] uniqueidentifier NOT NULL,
    [ResidencyId] uniqueidentifier NOT NULL,
    [Version] bigint NOT NULL,
    [BatchId] uniqueidentifier NOT NULL,
    [AdmissionGeneration] bigint NOT NULL,
    [ExecutorKey] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [ExecutionInstanceId] uniqueidentifier NOT NULL,
    [DetachedAtUtc] datetimeoffset NOT NULL,
    CONSTRAINT [PK_GpuModelDetachmentReceipts] PRIMARY KEY ([DispatchId]),
    CONSTRAINT [CK_GpuModelDetachmentReceipts_ExecutorKey] CHECK (DATALENGTH([ExecutorKey]) > 0 AND UNICODE(RIGHT([ExecutorKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)),
    CONSTRAINT [CK_GpuModelDetachmentReceipts_Identity] CHECK ([DispatchId]<>'00000000-0000-0000-0000-000000000000' AND [BatchId]<>'00000000-0000-0000-0000-000000000000' AND
[ExecutionInstanceId]<>'00000000-0000-0000-0000-000000000000' AND [Version]>0 AND [AdmissionGeneration]>0 AND
DATEPART(TZOFFSET,[DetachedAtUtc])=0 AND [DetachedAtUtc]>'0001-01-01T00:00:00+00:00'),
    CONSTRAINT [FK_GpuModelDetachmentReceipts_GpuModelResidencies_ResidencyId] FOREIGN KEY ([ResidencyId]) REFERENCES [GpuModelResidencies] ([ResidencyId]) ON DELETE NO ACTION
);
GO

ALTER TABLE [MediaTranscriptionGpuRequests] ADD CONSTRAINT [CK_MediaTranscriptionGpuRequests_Cleanup] CHECK (([ExecutionPhase]<3 AND [CleanupConfirmedAtUtc] IS NULL AND [CleanupKind] IS NULL) OR
([ExecutionPhase]=4 AND [NativeProcessId] IS NOT NULL AND [CleanupConfirmedAtUtc] IS NULL AND [CleanupKind] IS NULL) OR
([ExecutionPhase]>=3 AND [CleanupConfirmedAtUtc] IS NOT NULL AND [CleanupKind] IS NOT NULL AND
 ([CleanupKind]=0 AND [NativeProcessId] IS NULL OR [CleanupKind]=1 AND [NativeProcessId] IS NOT NULL)));
GO

CREATE INDEX [IX_EmbeddingGpuRequests_ResidentDetachmentDispatchId] ON [EmbeddingGpuRequests] ([ResidentDetachmentDispatchId]);
GO

ALTER TABLE [EmbeddingGpuRequests] ADD CONSTRAINT [CK_EmbeddingGpuRequests_Completion] CHECK ([State] = 0 OR ([State] = 1 AND [ResultDigest] IS NOT NULL AND [ExecutorInstanceId] IS NOT NULL) OR ([State] = 2 AND ([NativeCleanupConfirmed] = 1 OR [ResidentDetachmentDispatchId] IS NOT NULL)));
GO

ALTER TABLE [EmbeddingGpuRequests] ADD CONSTRAINT [CK_EmbeddingGpuRequests_ResidentDetachment] CHECK ([ResidentDetachmentDispatchId] IS NULL OR ([State] IN (1, 2) AND [DispatchId] IS NOT NULL AND [ResidentDetachmentDispatchId] = [DispatchId] AND [ResultDigest] IS NOT NULL AND [NativeCleanupConfirmed] = 0));
GO

CREATE UNIQUE INDEX [IX_GpuModelDetachmentReceipts_ResidencyId_Version] ON [GpuModelDetachmentReceipts] ([ResidencyId], [Version]);
GO

CREATE UNIQUE INDEX [IX_GpuModelResidencies_SlotKey] ON [GpuModelResidencies] ([SlotKey]) WHERE [State] < 4;
GO

ALTER TABLE [EmbeddingGpuRequests] ADD CONSTRAINT [FK_EmbeddingGpuRequests_GpuModelDetachmentReceipts_ResidentDetachmentDispatchId] FOREIGN KEY ([ResidentDetachmentDispatchId]) REFERENCES [GpuModelDetachmentReceipts] ([DispatchId]) ON DELETE NO ACTION;
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261119201434_AddGpuModelResidencies', N'10.0.10');
GO

