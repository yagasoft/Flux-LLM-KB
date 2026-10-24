BEGIN TRANSACTION;
ALTER TABLE [DocumentOcrRequests] ADD [CleanupProofKind] int NULL;

ALTER TABLE [DocumentOcrRequests] ADD [ExecutionAdmissionGeneration] bigint NULL;

ALTER TABLE [DocumentOcrRequests] ADD [ExecutionBatchId] uniqueidentifier NULL;

ALTER TABLE [DocumentOcrRequests] ADD [ExecutionClaimedAtUtc] datetimeoffset NULL;

ALTER TABLE [DocumentOcrRequests] ADD [ExecutionDeadlineAtUtc] datetimeoffset NULL;

ALTER TABLE [DocumentOcrRequests] ADD [ExecutionDispatchId] uniqueidentifier NULL;

ALTER TABLE [DocumentOcrRequests] ADD [ExecutionInstanceId] uniqueidentifier NULL;

ALTER TABLE [DocumentOcrRequests] ADD [ExecutionMachineFingerprint] char(64) NULL;

ALTER TABLE [DocumentOcrRequests] ADD [ExecutionPhase] int NOT NULL DEFAULT 0;

ALTER TABLE [DocumentOcrRequests] ADD [NativeCleanupConfirmedAtUtc] datetimeoffset NULL;

ALTER TABLE [DocumentOcrRequests] ADD [NativeProcessId] int NULL;

ALTER TABLE [DocumentOcrRequests] ADD [NativeProcessStartedAtUtc] datetimeoffset NULL;

ALTER TABLE [DocumentOcrRequests] ADD [PhysicalCleanupEvidenceSha256] char(64) NULL;

ALTER TABLE [DocumentOcrRequests] ADD [RecoveryReason] nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL;

ALTER TABLE [DocumentOcrRequests] ADD [RecoveryStartedAtUtc] datetimeoffset NULL;

ALTER TABLE [DocumentOcrRequests] ADD [RecoveryUpdatedAtUtc] datetimeoffset NULL;

ALTER TABLE [DocumentOcrRequests] ADD [SupervisorInstanceId] uniqueidentifier NULL;

ALTER TABLE [DocumentOcrRequests] ADD [SupervisorProcessId] int NULL;

ALTER TABLE [DocumentOcrRequests] ADD [SupervisorProcessStartedAtUtc] datetimeoffset NULL;

ALTER TABLE [DocumentOcrRequests] ADD CONSTRAINT [CK_DocumentOcrRequests_CleanupKind] CHECK ([CleanupProofKind] IS NULL OR [CleanupProofKind] BETWEEN 0 AND 2);

ALTER TABLE [DocumentOcrRequests] ADD CONSTRAINT [CK_DocumentOcrRequests_CleanupProof] CHECK (([NativeCleanupConfirmedAtUtc] IS NULL AND [CleanupProofKind] IS NULL) OR ([NativeCleanupConfirmedAtUtc] IS NOT NULL AND [CleanupProofKind] IS NOT NULL AND [ExecutionInstanceId] IS NOT NULL));

ALTER TABLE [DocumentOcrRequests] ADD CONSTRAINT [CK_DocumentOcrRequests_ExecutionIdentity] CHECK ([ExecutionInstanceId] IS NULL OR ([ExecutionDispatchId] IS NOT NULL AND [ExecutionBatchId] IS NOT NULL AND [ExecutionAdmissionGeneration] IS NOT NULL AND [ExecutionAdmissionGeneration] > 0 AND [SupervisorInstanceId] IS NOT NULL AND [SupervisorProcessId] IS NOT NULL AND [SupervisorProcessId] > 0 AND [SupervisorProcessStartedAtUtc] IS NOT NULL AND [ExecutionMachineFingerprint] IS NOT NULL AND [ExecutionClaimedAtUtc] IS NOT NULL AND [ExecutionDeadlineAtUtc] IS NOT NULL AND [ExecutionDeadlineAtUtc] > [ExecutionClaimedAtUtc]));

ALTER TABLE [DocumentOcrRequests] ADD CONSTRAINT [CK_DocumentOcrRequests_ExecutionPhase] CHECK ([ExecutionPhase] BETWEEN 0 AND 5);

ALTER TABLE [DocumentOcrRequests] ADD CONSTRAINT [CK_DocumentOcrRequests_NativeIdentity] CHECK (([NativeProcessId] IS NULL AND [NativeProcessStartedAtUtc] IS NULL) OR ([NativeProcessId] IS NOT NULL AND [NativeProcessId] > 0 AND [NativeProcessStartedAtUtc] IS NOT NULL AND [ExecutionInstanceId] IS NOT NULL));

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261024061418_AddDocumentOcrExecutionLifecycle', N'10.0.10');

COMMIT;
GO

