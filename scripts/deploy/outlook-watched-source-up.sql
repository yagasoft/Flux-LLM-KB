DROP INDEX [IX_OutlookCaptureExports_ProfileId] ON [OutlookCaptureExports];

ALTER TABLE [OutlookCaptureProfiles] ADD [WatchedPublicationSuspendedAtUtc] datetimeoffset NULL;

ALTER TABLE [OutlookCaptureProfiles] ADD [WatchedRootIdentity] varchar(128) NULL;

ALTER TABLE [OutlookCaptureProfiles] ADD [WatchedRootPath] nvarchar(2048) COLLATE Latin1_General_100_BIN2 NULL;

ALTER TABLE [OutlookCaptureProfiles] ADD [WatchedSourceRootId] uniqueidentifier NULL;

ALTER TABLE [OutlookCaptureProfiles] ADD [WatchedStagingIdentity] varchar(128) NULL;

ALTER TABLE [OutlookCaptureProfiles] ADD [WatchedStagingPath] nvarchar(2048) COLLATE Latin1_General_100_BIN2 NULL;

ALTER TABLE [OutlookCaptureExports] ADD [WatchedPreparedBundleJson] nvarchar(max) NULL;

ALTER TABLE [OutlookCaptureExports] ADD [WatchedPublishedAtUtc] datetimeoffset NULL;

ALTER TABLE [OutlookCaptureExports] ADD [WatchedRetryAfterUtc] datetimeoffset NULL;

ALTER TABLE [OutlookCaptureExports] ADD [WatchedWithdrawalAtUtc] datetimeoffset NULL;

ALTER TABLE [OutlookCaptureExports] ADD [WatchedWithdrawalRevisionIdsJson] nvarchar(max) NULL;

GO

CREATE UNIQUE INDEX [IX_OutlookCaptureProfiles_WatchedSourceRootId] ON [OutlookCaptureProfiles] ([WatchedSourceRootId]) WHERE [WatchedSourceRootId] IS NOT NULL;

CREATE INDEX [IX_OutlookCaptureExports_ProfileId_WatchedPublishedAtUtc] ON [OutlookCaptureExports] ([ProfileId], [WatchedPublishedAtUtc]);

CREATE INDEX [IX_OutlookCaptureExports_ProfileId_WatchedRetryAfterUtc_Id] ON [OutlookCaptureExports] ([ProfileId], [WatchedRetryAfterUtc], [Id]) WHERE [WatchedPublishedAtUtc] IS NULL AND [State] = 2;

ALTER TABLE [OutlookCaptureProfiles] ADD CONSTRAINT [FK_OutlookCaptureProfiles_SourceRootConfigurations_WatchedSourceRootId] FOREIGN KEY ([WatchedSourceRootId]) REFERENCES [SourceRootConfigurations] ([Id]) ON DELETE NO ACTION;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261118224032_BindOutlookWatchedSources', N'10.0.10');
