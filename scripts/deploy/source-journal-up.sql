BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE TABLE [SourceInventoryEntries] (
        [Id] uniqueidentifier NOT NULL,
        [SourceRootId] uniqueidentifier NOT NULL,
        [RelativePath] nvarchar(max) NOT NULL,
        [PathFingerprint] varchar(64) NOT NULL,
        [FileId] varchar(32) NOT NULL,
        [ParentId] varchar(32) NOT NULL,
        [IsDirectory] bit NOT NULL,
        [IsAncestor] bit NOT NULL,
        [BaselineEpoch] bigint NOT NULL,
        [SourceRevisionId] uniqueidentifier NULL,
        [SourceArtifactId] uniqueidentifier NULL,
        [ObservationJson] nvarchar(max) NULL,
        [LastVerifiedAtUtc] datetimeoffset NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_SourceInventoryEntries] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SourceInventoryEntries_SourceRootConfigurations_SourceRootId] FOREIGN KEY ([SourceRootId]) REFERENCES [SourceRootConfigurations] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE TABLE [SourcePendingChanges] (
        [Id] uniqueidentifier NOT NULL,
        [SourceRootId] uniqueidentifier NOT NULL,
        [RelativePath] nvarchar(max) NOT NULL,
        [PathFingerprint] varchar(64) NOT NULL,
        [ReasonFlags] bigint NOT NULL,
        [IsSubtree] bit NOT NULL,
        [Generation] bigint NOT NULL,
        [FirstUsn] bigint NOT NULL,
        [LastUsn] bigint NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_SourcePendingChanges] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SourcePendingChanges_SourceRootConfigurations_SourceRootId] FOREIGN KEY ([SourceRootId]) REFERENCES [SourceRootConfigurations] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE TABLE [SourceRootScanStates] (
        [SourceRootId] uniqueidentifier NOT NULL,
        [VolumeIdentity] nvarchar(64) NOT NULL,
        [PolicyFingerprint] nvarchar(64) NOT NULL,
        [BaselineEpoch] bigint NOT NULL,
        [Mode] nvarchar(32) NOT NULL,
        [Reason] nvarchar(128) NULL,
        [BaselineStartUsn] bigint NOT NULL,
        [CoveredUsn] bigint NOT NULL,
        [Generation] bigint NOT NULL,
        [UnresolvedThroughUsn] bigint NULL,
        [LastReconciliationAtUtc] datetimeoffset NULL,
        [LastVerifiedAtUtc] datetimeoffset NULL,
        [VerificationPolicyJson] nvarchar(max) NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_SourceRootScanStates] PRIMARY KEY ([SourceRootId]),
        CONSTRAINT [FK_SourceRootScanStates_SourceRootConfigurations_SourceRootId] FOREIGN KEY ([SourceRootId]) REFERENCES [SourceRootConfigurations] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE TABLE [SourceVerificationRuns] (
        [Id] uniqueidentifier NOT NULL,
        [SourceRootId] uniqueidentifier NOT NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        [CompletedAtUtc] datetimeoffset NULL,
        [State] nvarchar(32) NOT NULL,
        [BytesRead] bigint NOT NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_SourceVerificationRuns] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SourceVerificationRuns_SourceRootConfigurations_SourceRootId] FOREIGN KEY ([SourceRootId]) REFERENCES [SourceRootConfigurations] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE TABLE [SourceVolumeJournalCheckpoints] (
        [VolumeIdentity] nvarchar(64) NOT NULL,
        [JournalId] nvarchar(16) NOT NULL,
        [NextUsn] bigint NOT NULL,
        [RegistrationGeneration] bigint NOT NULL,
        [LeaseOwner] nvarchar(128) NULL,
        [LeaseGeneration] bigint NOT NULL,
        [LeaseExpiresAtUtc] datetimeoffset NULL,
        [DueAtUtc] datetimeoffset NULL,
        [LastBatchFingerprint] nvarchar(64) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_SourceVolumeJournalCheckpoints] PRIMARY KEY ([VolumeIdentity])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE TABLE [SourceVerificationItems] (
        [Id] uniqueidentifier NOT NULL,
        [RunId] uniqueidentifier NOT NULL,
        [InventoryEntryId] uniqueidentifier NOT NULL,
        [ExpectedRevisionId] uniqueidentifier NULL,
        [State] nvarchar(32) NOT NULL,
        [Reason] nvarchar(128) NULL,
        [AttemptedAtUtc] datetimeoffset NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_SourceVerificationItems] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SourceVerificationItems_SourceVerificationRuns_RunId] FOREIGN KEY ([RunId]) REFERENCES [SourceVerificationRuns] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE INDEX [IX_SourceInventoryEntries_SourceRootId_FileId] ON [SourceInventoryEntries] ([SourceRootId], [FileId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SourceInventoryEntries_SourceRootId_PathFingerprint] ON [SourceInventoryEntries] ([SourceRootId], [PathFingerprint]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SourcePendingChanges_SourceRootId_PathFingerprint] ON [SourcePendingChanges] ([SourceRootId], [PathFingerprint]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE INDEX [IX_SourceRootScanStates_VolumeIdentity] ON [SourceRootScanStates] ([VolumeIdentity]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SourceVerificationItems_RunId_InventoryEntryId] ON [SourceVerificationItems] ([RunId], [InventoryEntryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    CREATE INDEX [IX_SourceVerificationRuns_SourceRootId_CreatedAtUtc] ON [SourceVerificationRuns] ([SourceRootId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024095915_AddIncrementalSourceScanning'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261024095915_AddIncrementalSourceScanning', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024101913_AddIncrementalScanManifests'
)
BEGIN
    ALTER TABLE [SourceScanRequests] ADD [IncrementalManifestJson] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024101913_AddIncrementalScanManifests'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [DeferredEvidenceJson] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024101913_AddIncrementalScanManifests'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [GitInventoryGeneration] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024101913_AddIncrementalScanManifests'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [LastStatisticsJson] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024101913_AddIncrementalScanManifests'
)
BEGIN
    ALTER TABLE [SourcePendingChanges] ADD [NotBeforeUtc] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024101913_AddIncrementalScanManifests'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261024101913_AddIncrementalScanManifests', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024105456_BoundIncrementalChangeDebounce'
)
BEGIN
    ALTER TABLE [SourcePendingChanges] ADD [FirstObservedAtUtc] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024105456_BoundIncrementalChangeDebounce'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261024105456_BoundIncrementalChangeDebounce', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024110355_RecordJournalAvailability'
)
BEGIN
    ALTER TABLE [SourceVolumeJournalCheckpoints] ADD [LastFailureCode] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024110355_RecordJournalAvailability'
)
BEGIN
    ALTER TABLE [SourceVolumeJournalCheckpoints] ADD [UnavailableSinceUtc] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024110355_RecordJournalAvailability'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261024110355_RecordJournalAvailability', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024122515_PersistSourceWatchPathHints'
)
BEGIN
    ALTER TABLE [SourceRootWatchStates] ADD [HintsJson] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024122515_PersistSourceWatchPathHints'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261024122515_PersistSourceWatchPathHints', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024132626_FenceIncrementalRecovery'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [NamespaceComplete] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024132626_FenceIncrementalRecovery'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [NamespaceGeneration] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024132626_FenceIncrementalRecovery'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [RecoveryGeneration] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024132626_FenceIncrementalRecovery'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [RecoveryInProgress] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024132626_FenceIncrementalRecovery'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [RecoveryRequired] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024132626_FenceIncrementalRecovery'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261024132626_FenceIncrementalRecovery', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024151514_SealIncrementalSourceCaptures'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [SealProfileId] varchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024151514_SealIncrementalSourceCaptures'
)
BEGIN
    ALTER TABLE [SourcePendingChanges] ADD [LastContentUsn] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024151514_SealIncrementalSourceCaptures'
)
BEGIN
    ALTER TABLE [SourcePendingChanges] ADD [LastUnjournaledGeneration] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024151514_SealIncrementalSourceCaptures'
)
BEGIN
    ALTER TABLE [SourceInventoryEntries] ADD [IsSealed] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024151514_SealIncrementalSourceCaptures'
)
BEGIN
    ALTER TABLE [SourceInventoryEntries] ADD [LatestJournalReason] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024151514_SealIncrementalSourceCaptures'
)
BEGIN
    ALTER TABLE [SourceInventoryEntries] ADD [LatestJournalUsn] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024151514_SealIncrementalSourceCaptures'
)
BEGIN
    ALTER TABLE [SourceInventoryEntries] ADD [NextForcedCheckAtUtc] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024151514_SealIncrementalSourceCaptures'
)
BEGIN
    ALTER TABLE [SourceInventoryEntries] ADD [SealFailureCode] varchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024151514_SealIncrementalSourceCaptures'
)
BEGIN
    ALTER TABLE [SourceInventoryEntries] ADD [SealProfileId] varchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024151514_SealIncrementalSourceCaptures'
)
BEGIN
    CREATE INDEX [IX_SourceInventoryEntries_IsSealed_NextForcedCheckAtUtc_SourceRootId] ON [SourceInventoryEntries] ([IsSealed], [NextForcedCheckAtUtc], [SourceRootId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024151514_SealIncrementalSourceCaptures'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261024151514_SealIncrementalSourceCaptures', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationRuns] ADD [Active] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationRuns] ADD [AuthorityJson] nvarchar(max) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationRuns] ADD [Manual] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationRuns] ADD [NextBatchAtUtc] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationRuns] ADD [OriginalPolicyJson] nvarchar(max) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationRuns] ADD [Reason] nvarchar(128) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationRuns] ADD [RetainedBytesRead] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationRuns] ADD [ScheduledForUtc] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationRuns] ADD [SourceBytesRead] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationItems] ADD [AttemptCount] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationItems] ADD [BindingJson] nvarchar(max) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationItems] ADD [CompletedAtUtc] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceVerificationItems] ADD [ReceiptJson] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceScanRequests] ADD [VerificationRunId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [VerificationPaused] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [VerificationPolicyRevision] bigint NOT NULL DEFAULT CAST(0 AS bigint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    CREATE INDEX [IX_SourceVerificationRuns_Active_NextBatchAtUtc] ON [SourceVerificationRuns] ([Active], [NextBatchAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_SourceVerificationRuns_SourceRootId] ON [SourceVerificationRuns] ([SourceRootId]) WHERE [Active] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101429_AddSourceVerificationExecution'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261025101429_AddSourceVerificationExecution', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025103623_DistinguishParkedSourceRetryDebt'
)
BEGIN
    ALTER TABLE [SourcePendingChanges] ADD [RetryGeneration] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025103623_DistinguishParkedSourceRetryDebt'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261025103623_DistinguishParkedSourceRetryDebt', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025111314_AddSourceVerificationResolution'
)
BEGIN
    ALTER TABLE [SourceVerificationItems] ADD [CandidateBindingJson] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025111314_AddSourceVerificationResolution'
)
BEGIN
    ALTER TABLE [SourceVerificationItems] ADD [ReceiptByteLength] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025111314_AddSourceVerificationResolution'
)
BEGIN
    ALTER TABLE [SourceVerificationItems] ADD [ReceiptSha256] varchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025111314_AddSourceVerificationResolution'
)
BEGIN
    ALTER TABLE [SourceRootScanStates] ADD [LastBaselineAtUtc] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025111314_AddSourceVerificationResolution'
)
BEGIN
    CREATE INDEX [IX_SourceVerificationItems_RunId_ReceiptSha256_ReceiptByteLength] ON [SourceVerificationItems] ([RunId], [ReceiptSha256], [ReceiptByteLength]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025111314_AddSourceVerificationResolution'
)
BEGIN
    CREATE INDEX [IX_SourceVerificationItems_RunId_State_Id] ON [SourceVerificationItems] ([RunId], [State], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025111314_AddSourceVerificationResolution'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261025111314_AddSourceVerificationResolution', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025113436_MakeSourceVerificationResolutionFair'
)
BEGIN
    DROP INDEX [IX_SourceVerificationItems_RunId_State_Id] ON [SourceVerificationItems];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025113436_MakeSourceVerificationResolutionFair'
)
BEGIN
    CREATE INDEX [IX_SourceVerificationItems_RunId_State_AttemptedAtUtc_Id] ON [SourceVerificationItems] ([RunId], [State], [AttemptedAtUtc], [Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025113436_MakeSourceVerificationResolutionFair'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261025113436_MakeSourceVerificationResolutionFair', N'10.0.10');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025124025_PreserveSourceVerificationReadCounts'
)
BEGIN
    ALTER TABLE [SourceVerificationRuns] ADD [ReadCountsIncomplete] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025124025_PreserveSourceVerificationReadCounts'
)
BEGIN
    EXEC(N'UPDATE [SourceVerificationRuns] SET [ReadCountsIncomplete] = 1;');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025124025_PreserveSourceVerificationReadCounts'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261025124025_PreserveSourceVerificationReadCounts', N'10.0.10');
END;

COMMIT;
GO

