BEGIN TRANSACTION;
ALTER TABLE [IndexState] ADD [ProjectionChangeFloorVersion] bigint NOT NULL DEFAULT CAST(0 AS bigint);

ALTER TABLE [IndexState] ADD [RequiredIndexFormat] int NOT NULL DEFAULT 1;

ALTER TABLE [IndexGenerations] ADD [FormatVersion] int NOT NULL DEFAULT 1;

ALTER TABLE [IndexGenerations] ADD [SegmentCount] int NOT NULL DEFAULT 0;

CREATE TABLE [IndexProjectionChanges] (
    [CorpusEpoch] uniqueidentifier NOT NULL,
    [CorpusVersion] bigint NOT NULL,
    [ScopeKind] int NOT NULL,
    [ScopeId] uniqueidentifier NOT NULL,
    [CreatedAtUtc] datetimeoffset(7) NOT NULL,
    CONSTRAINT [PK_IndexProjectionChanges] PRIMARY KEY ([CorpusEpoch], [CorpusVersion], [ScopeKind], [ScopeId]),
    CONSTRAINT [CK_IndexProjectionChanges_Scope] CHECK ([CorpusVersion] >= 0 AND [ScopeKind] BETWEEN 1 AND 4)
);

CREATE TABLE [IndexPublicationCandidates] (
    [Id] uniqueidentifier NOT NULL,
    [CorpusEpoch] uniqueidentifier NOT NULL,
    [ExpectedCorpusVersion] bigint NOT NULL,
    [PublicationCorpusVersion] bigint NOT NULL,
    [BaseGenerationId] uniqueidentifier NULL,
    [DraftGenerationId] uniqueidentifier NOT NULL,
    [OwnerJobId] uniqueidentifier NULL,
    [OwnerLeaseGeneration] bigint NOT NULL,
    [OwnerLeaseOwner] nvarchar(256) NULL,
    [ResultGenerationId] uniqueidentifier NULL,
    [AffectedScopesJson] nvarchar(max) NOT NULL,
    [BeforeChecksum] char(64) NOT NULL,
    [AfterChecksum] char(64) NOT NULL,
    [State] int NOT NULL,
    [CreatedAtUtc] datetimeoffset(7) NOT NULL,
    [CompletedAtUtc] datetimeoffset(7) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_IndexPublicationCandidates] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_IndexPublicationCandidates_State] CHECK ([State] BETWEEN 0 AND 3 AND [ExpectedCorpusVersion] >= 0 AND [PublicationCorpusVersion] >= [ExpectedCorpusVersion]),
    CONSTRAINT [FK_IndexPublicationCandidates_IndexGenerations_BaseGenerationId] FOREIGN KEY ([BaseGenerationId]) REFERENCES [IndexGenerations] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_IndexPublicationCandidates_IndexGenerations_DraftGenerationId] FOREIGN KEY ([DraftGenerationId]) REFERENCES [IndexGenerations] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_IndexPublicationCandidates_IndexGenerations_ResultGenerationId] FOREIGN KEY ([ResultGenerationId]) REFERENCES [IndexGenerations] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_IndexPublicationCandidates_Jobs_OwnerJobId] FOREIGN KEY ([OwnerJobId]) REFERENCES [Jobs] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [IndexSegments] (
    [Id] char(64) NOT NULL,
    [ModelFingerprint] nvarchar(256) NOT NULL,
    [Dimensions] int NOT NULL,
    [NativeFormat] nvarchar(128) NOT NULL,
    [DirectoryPath] nvarchar(2048) NOT NULL,
    [MembershipChecksum] char(64) NOT NULL,
    [FileChecksum] char(64) NOT NULL,
    [FileLength] bigint NOT NULL,
    [VectorCount] bigint NOT NULL,
    [State] int NOT NULL,
    [ProducingCandidateId] uniqueidentifier NULL,
    [RetirementOperationId] uniqueidentifier NULL,
    [CreatedAtUtc] datetimeoffset(7) NOT NULL,
    [SealedAtUtc] datetimeoffset(7) NULL,
    [ReclaimedAtUtc] datetimeoffset(7) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_IndexSegments] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_IndexSegments_Profile] CHECK ([Dimensions] > 0 AND [VectorCount] > 0 AND [FileLength] > 0),
    CONSTRAINT [CK_IndexSegments_State] CHECK ([State] BETWEEN 0 AND 3),
    CONSTRAINT [FK_IndexSegments_IndexPublicationCandidates_ProducingCandidateId] FOREIGN KEY ([ProducingCandidateId]) REFERENCES [IndexPublicationCandidates] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [IndexGenerationSegments] (
    [GenerationId] uniqueidentifier NOT NULL,
    [SegmentId] char(64) NOT NULL,
    [Ordinal] int NOT NULL,
    CONSTRAINT [PK_IndexGenerationSegments] PRIMARY KEY ([GenerationId], [SegmentId]),
    CONSTRAINT [CK_IndexGenerationSegments_Ordinal] CHECK ([Ordinal] >= 0),
    CONSTRAINT [FK_IndexGenerationSegments_IndexGenerations_GenerationId] FOREIGN KEY ([GenerationId]) REFERENCES [IndexGenerations] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_IndexGenerationSegments_IndexSegments_SegmentId] FOREIGN KEY ([SegmentId]) REFERENCES [IndexSegments] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [IndexSegmentOwners] (
    [Id] uniqueidentifier NOT NULL,
    [SegmentId] char(64) NOT NULL,
    [OwnerInstanceId] uniqueidentifier NOT NULL,
    [OwnerProcessId] int NOT NULL,
    [OwnerStartedAtUtc] datetimeoffset(7) NOT NULL,
    [OwnerMachineFingerprint] char(64) NOT NULL,
    [State] int NOT NULL,
    [CreatedAtUtc] datetimeoffset(7) NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_IndexSegmentOwners] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_IndexSegmentOwners_IndexSegments_SegmentId] FOREIGN KEY ([SegmentId]) REFERENCES [IndexSegments] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [IndexSegmentVectors] (
    [SegmentId] char(64) NOT NULL,
    [VectorId] bigint NOT NULL,
    CONSTRAINT [PK_IndexSegmentVectors] PRIMARY KEY ([SegmentId], [VectorId]),
    CONSTRAINT [FK_IndexSegmentVectors_IndexSegments_SegmentId] FOREIGN KEY ([SegmentId]) REFERENCES [IndexSegments] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_IndexSegmentVectors_Vectors_VectorId] FOREIGN KEY ([VectorId]) REFERENCES [Vectors] ([VectorId]) ON DELETE NO ACTION
);

CREATE TABLE [CurrentIndexMembers] (
    [CorpusEpoch] uniqueidentifier NOT NULL,
    [VectorId] bigint NOT NULL,
    [SegmentId] char(64) NOT NULL,
    [SourceIdentityId] uniqueidentifier NOT NULL,
    [OwnerSourceRevisionId] uniqueidentifier NULL,
    [SourceRootId] uniqueidentifier NULL,
    CONSTRAINT [PK_CurrentIndexMembers] PRIMARY KEY ([CorpusEpoch], [VectorId]),
    CONSTRAINT [FK_CurrentIndexMembers_IndexSegmentVectors_SegmentId_VectorId] FOREIGN KEY ([SegmentId], [VectorId]) REFERENCES [IndexSegmentVectors] ([SegmentId], [VectorId]) ON DELETE NO ACTION
);

UPDATE [IndexState] SET [ProjectionChangeFloorVersion] = CAST(0 AS bigint), [RequiredIndexFormat] = 1
WHERE [Id] = 1;
SELECT @@ROWCOUNT;


CREATE INDEX [IX_CurrentIndexMembers_CorpusEpoch_OwnerSourceRevisionId_VectorId] ON [CurrentIndexMembers] ([CorpusEpoch], [OwnerSourceRevisionId], [VectorId]);

CREATE INDEX [IX_CurrentIndexMembers_CorpusEpoch_SourceIdentityId_VectorId] ON [CurrentIndexMembers] ([CorpusEpoch], [SourceIdentityId], [VectorId]);

CREATE INDEX [IX_CurrentIndexMembers_CorpusEpoch_SourceRootId_VectorId] ON [CurrentIndexMembers] ([CorpusEpoch], [SourceRootId], [VectorId]);

CREATE INDEX [IX_CurrentIndexMembers_SegmentId_VectorId] ON [CurrentIndexMembers] ([SegmentId], [VectorId]);

CREATE UNIQUE INDEX [IX_IndexGenerationSegments_GenerationId_Ordinal] ON [IndexGenerationSegments] ([GenerationId], [Ordinal]);

CREATE INDEX [IX_IndexGenerationSegments_SegmentId] ON [IndexGenerationSegments] ([SegmentId]);

CREATE INDEX [IX_IndexPublicationCandidates_BaseGenerationId] ON [IndexPublicationCandidates] ([BaseGenerationId]);

CREATE INDEX [IX_IndexPublicationCandidates_DraftGenerationId] ON [IndexPublicationCandidates] ([DraftGenerationId]);

CREATE INDEX [IX_IndexPublicationCandidates_OwnerJobId] ON [IndexPublicationCandidates] ([OwnerJobId]);

CREATE INDEX [IX_IndexPublicationCandidates_ResultGenerationId] ON [IndexPublicationCandidates] ([ResultGenerationId]);

CREATE INDEX [IX_IndexPublicationCandidates_State_CreatedAtUtc] ON [IndexPublicationCandidates] ([State], [CreatedAtUtc]);

CREATE UNIQUE INDEX [IX_IndexSegmentOwners_SegmentId_OwnerInstanceId] ON [IndexSegmentOwners] ([SegmentId], [OwnerInstanceId]);

CREATE INDEX [IX_IndexSegments_ProducingCandidateId] ON [IndexSegments] ([ProducingCandidateId]);

CREATE INDEX [IX_IndexSegments_State_CreatedAtUtc] ON [IndexSegments] ([State], [CreatedAtUtc]);

CREATE INDEX [IX_IndexSegmentVectors_VectorId] ON [IndexSegmentVectors] ([VectorId]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261020183242_AddSegmentedIndexPublication', N'10.0.10');

COMMIT;
GO

BEGIN TRANSACTION;
DECLARE @var nvarchar(max);
SELECT @var = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[IndexPublicationCandidates]') AND [c].[name] = N'DraftGenerationId');
IF @var IS NOT NULL EXEC(N'ALTER TABLE [IndexPublicationCandidates] DROP CONSTRAINT ' + @var + ';');
ALTER TABLE [IndexPublicationCandidates] ALTER COLUMN [DraftGenerationId] uniqueidentifier NULL;

ALTER TABLE [IndexPublicationCandidates] ADD [AfterCount] bigint NOT NULL DEFAULT CAST(0 AS bigint);

ALTER TABLE [IndexPublicationCandidates] ADD [BeforeCount] bigint NOT NULL DEFAULT CAST(0 AS bigint);

ALTER TABLE [IndexPublicationCandidates] ADD [LastAppendOrdinal] bigint NOT NULL DEFAULT CAST(0 AS bigint);

ALTER TABLE [IndexPublicationCandidates] ADD [LastPageChecksum] char(64) NOT NULL DEFAULT '';

ALTER TABLE [IndexPublicationCandidates] ADD [OwnerInstanceId] uniqueidentifier NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';

ALTER TABLE [IndexPublicationCandidates] ADD [OwnerMachineFingerprint] char(64) NOT NULL DEFAULT '';

ALTER TABLE [IndexPublicationCandidates] ADD [OwnerProcessId] int NOT NULL DEFAULT 0;

ALTER TABLE [IndexPublicationCandidates] ADD [OwnerStartedAtUtc] datetimeoffset(7) NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00';

ALTER TABLE [IndexPublicationCandidates] ADD [WorklistComplete] bit NOT NULL DEFAULT CAST(0 AS bit);

CREATE TABLE [IndexCandidateMembers] (
    [CandidateId] uniqueidentifier NOT NULL,
    [Phase] int NOT NULL,
    [VectorId] bigint NOT NULL,
    [PayloadChecksum] char(64) NOT NULL,
    [SourceIdentityId] uniqueidentifier NOT NULL,
    [OwnerSourceRevisionId] uniqueidentifier NULL,
    [SourceRootId] uniqueidentifier NULL,
    CONSTRAINT [PK_IndexCandidateMembers] PRIMARY KEY ([CandidateId], [Phase], [VectorId]),
    CONSTRAINT [CK_IndexCandidateMembers_Phase] CHECK ([Phase] IN (0, 1) AND [VectorId] > 0),
    CONSTRAINT [FK_IndexCandidateMembers_IndexPublicationCandidates_CandidateId] FOREIGN KEY ([CandidateId]) REFERENCES [IndexPublicationCandidates] ([Id]) ON DELETE NO ACTION
);

CREATE INDEX [IX_IndexCandidateMembers_VectorId] ON [IndexCandidateMembers] ([VectorId]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261020185203_AddSegmentedPreviewWorklists', N'10.0.10');

COMMIT;
GO

BEGIN TRANSACTION;
ALTER TABLE [IndexGenerations] ADD [ReclaimedAtUtc] datetimeoffset NULL;

ALTER TABLE [IndexGenerations] ADD [RetirementOperationId] uniqueidentifier NULL;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261020192553_AddIndexRetirementIntents', N'10.0.10');

COMMIT;
GO

BEGIN TRANSACTION;
ALTER TABLE [SourceDeletionCleanupItems] DROP CONSTRAINT [CK_SourceDeletionCleanupItems_StorageKind];

ALTER TABLE [IndexPublicationCandidates] ADD [RetiredDraftGenerationId] uniqueidentifier NULL;

ALTER TABLE [IndexPublicationCandidates] ADD [RetiredOwnerJobId] uniqueidentifier NULL;

ALTER TABLE [SourceDeletionCleanupItems] ADD CONSTRAINT [CK_SourceDeletionCleanupItems_StorageKind] CHECK ([StorageKind] IN (1, 2, 3, 4));

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261020195833_AddSegmentedSourceDeletionTargets', N'10.0.10');

COMMIT;
GO

BEGIN TRANSACTION;
ALTER TABLE [IndexPublicationCandidates] ADD [RepackedSegmentsJson] nvarchar(1024) NOT NULL DEFAULT N'[]';

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261020212959_AddBoundedSegmentMerge', N'10.0.10');

COMMIT;
GO

BEGIN TRANSACTION;
ALTER TABLE [IndexPublicationCandidates] ADD [BuildPurpose] int NOT NULL DEFAULT 0;

ALTER TABLE [IndexPublicationCandidates] ADD [ExpectedIndexFormat] int NOT NULL DEFAULT 2;

ALTER TABLE [IndexPublicationCandidates] ADD CONSTRAINT [CK_IndexPublicationCandidates_Purpose] CHECK ([BuildPurpose] BETWEEN 0 AND 2 AND [ExpectedIndexFormat] IN (1, 2) AND ([BuildPurpose] <> 0 OR [ExpectedIndexFormat] = 2));

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261020222343_AddExplicitProjectionRebuild', N'10.0.10');

COMMIT;
GO

BEGIN TRANSACTION;
CREATE TABLE [IndexRecoveryAudit] (
    [Id] int NOT NULL,
    [IsValid] bit NOT NULL,
    [ClassifierVersion] int NOT NULL,
    [DatabaseIncarnation] uniqueidentifier NOT NULL,
    [RecoveryRoot] nvarchar(2048) NOT NULL,
    [IndexRoot] nvarchar(2048) NOT NULL,
    [RootFingerprint] nvarchar(256) NOT NULL,
    [InventoryChecksum] nvarchar(64) NOT NULL,
    [SealedAtUtc] datetimeoffset(7) NULL,
    [FailurePath] nvarchar(2048) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_IndexRecoveryAudit] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_IndexRecoveryAudit_Singleton] CHECK ([Id]=1)
);

CREATE TABLE [IndexRecoveryPins] (
    [Id] char(64) NOT NULL,
    [Packet] nvarchar(2048) NOT NULL,
    [GenerationId] uniqueidentifier NULL,
    [IndexPath] nvarchar(2048) NULL,
    CONSTRAINT [PK_IndexRecoveryPins] PRIMARY KEY ([Id])
);

CREATE TABLE [IndexRecoveryWriters] (
    [Id] uniqueidentifier NOT NULL,
    [RecoveryRoot] nvarchar(2048) NOT NULL,
    [OwnerProcessId] int NOT NULL,
    [OwnerStartedAtUtc] datetimeoffset(7) NOT NULL,
    [OwnerMachineFingerprint] char(64) NOT NULL,
    [OwnerExitSufficient] bit NOT NULL DEFAULT CAST(0 AS bit),
    [CreatedAtUtc] datetimeoffset(7) NOT NULL,
    CONSTRAINT [PK_IndexRecoveryWriters] PRIMARY KEY ([Id])
);

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'ClassifierVersion', N'DatabaseIncarnation', N'FailurePath', N'IndexRoot', N'InventoryChecksum', N'IsValid', N'RecoveryRoot', N'RootFingerprint', N'SealedAtUtc') AND [object_id] = OBJECT_ID(N'[IndexRecoveryAudit]'))
    SET IDENTITY_INSERT [IndexRecoveryAudit] ON;
INSERT INTO [IndexRecoveryAudit] ([Id], [ClassifierVersion], [DatabaseIncarnation], [FailurePath], [IndexRoot], [InventoryChecksum], [IsValid], [RecoveryRoot], [RootFingerprint], [SealedAtUtc])
VALUES (1, 0, '00000000-0000-0000-0000-000000000000', NULL, N'', N'', CAST(0 AS bit), N'', N'', NULL);
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'ClassifierVersion', N'DatabaseIncarnation', N'FailurePath', N'IndexRoot', N'InventoryChecksum', N'IsValid', N'RecoveryRoot', N'RootFingerprint', N'SealedAtUtc') AND [object_id] = OBJECT_ID(N'[IndexRecoveryAudit]'))
    SET IDENTITY_INSERT [IndexRecoveryAudit] OFF;

CREATE INDEX [IX_IndexRecoveryPins_GenerationId] ON [IndexRecoveryPins] ([GenerationId]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261021010911_AddIndexRecoveryReferenceAudit', N'10.0.10');

COMMIT;
GO

