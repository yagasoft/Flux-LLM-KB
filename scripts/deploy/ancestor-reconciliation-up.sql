ALTER TABLE dbo.SourceRootScanStates ADD AncestorReconciliationDueAtUtc datetimeoffset NULL;
GO

DECLARE @now datetimeoffset = SYSUTCDATETIME();
UPDATE scan
SET AncestorReconciliationDueAtUtc = CASE
    WHEN DATEADD(minute, 15, COALESCE(scan.LastReconciliationAtUtc, root.LastScanCompletedAtUtc)) > DATEADD(second, 30, @now)
    THEN DATEADD(minute, 15, COALESCE(scan.LastReconciliationAtUtc, root.LastScanCompletedAtUtc))
    ELSE DATEADD(second, 30, @now) END
FROM dbo.SourceRootScanStates AS scan
JOIN dbo.SourceRootConfigurations AS root ON root.Id = scan.SourceRootId
WHERE scan.RecoveryRequired = 1 AND scan.Reason = N'ancestor-authority-changed';

INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
VALUES (N'20261120221703_ScheduleAncestorMetadataReconciliation', N'10.0.10');
