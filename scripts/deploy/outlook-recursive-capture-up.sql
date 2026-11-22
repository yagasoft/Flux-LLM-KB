ALTER TABLE dbo.OutlookCatchUps ADD ConfigurationRevision bigint NULL;
ALTER TABLE dbo.OutlookCaptureProfiles ADD IncludeSubfolders bit NOT NULL DEFAULT (0);
ALTER TABLE dbo.OutlookCaptureProfiles ADD NextScheduledScanAtUtc datetimeoffset NULL;
ALTER TABLE dbo.OutlookCaptureFolders ADD IsDiscoveredSubfolder bit NOT NULL DEFAULT (0);
GO

INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
VALUES (N'20261122215123_ScheduleRecursiveOutlookCapture', N'10.0.10');
