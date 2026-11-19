BEGIN TRANSACTION;
ALTER TABLE [SourceDeletionOperations] ADD [WithdrawalVectorCursor] bigint NULL;

ALTER TABLE [Artifacts] ADD [WindowEligibilityVersion] uniqueidentifier NOT NULL DEFAULT (NEWID());

CREATE TABLE [TextChunkWindowBackfillStates] (
    [Id] int NOT NULL,
    [RuleVersion] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [LastChunkId] bigint NOT NULL,
    [EndChunkId] bigint NOT NULL,
    [RevisitFromChunkId] bigint NULL,
    [Completed] bit NOT NULL,
    [UpdatedAtUtc] datetimeoffset(7) NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_TextChunkWindowBackfillStates] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_TextChunkWindowBackfillStates_Cursor] CHECK ([LastChunkId] >= 0 AND [EndChunkId] >= 0 AND ([RevisitFromChunkId] IS NULL OR [RevisitFromChunkId] > 0)),
    CONSTRAINT [CK_TextChunkWindowBackfillStates_Id] CHECK ([Id] = 1)
);

CREATE TABLE [TextChunkWindowEligibilities] (
    [TextChunkId] bigint NOT NULL,
    [ArtifactId] uniqueidentifier NOT NULL,
    [CanonicalSourceRevision] bigint NOT NULL,
    [CanonicalContentHash] char(64) NOT NULL,
    [CanonicalLength] bigint NOT NULL,
    [ChunkSourceRevision] bigint NOT NULL,
    [ChunkContentHash] char(64) NOT NULL,
    [StartOffset] int NOT NULL,
    [Length] int NOT NULL,
    [RuleVersion] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [Available] bit NOT NULL,
    [CalculatedAtUtc] datetimeoffset(7) NOT NULL,
    CONSTRAINT [PK_TextChunkWindowEligibilities] PRIMARY KEY ([TextChunkId]),
    CONSTRAINT [FK_TextChunkWindowEligibilities_TextChunks_TextChunkId] FOREIGN KEY ([TextChunkId]) REFERENCES [TextChunks] ([Id]) ON DELETE CASCADE
);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261117210606_PersistPassageWindowEligibility', N'10.0.10');

COMMIT;
GO

