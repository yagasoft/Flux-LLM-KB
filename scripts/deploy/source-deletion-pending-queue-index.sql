DECLARE @queueObjectId int=OBJECT_ID(N'dbo.SourceDeletionCleanupItems',N'U');
IF @queueObjectId IS NULL THROW 51000,'source-deletion-pending-queue-table-missing',1;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=@queueObjectId AND name=N'IX_SourceDeletionCleanupItems_PendingQueue')
    CREATE INDEX [IX_SourceDeletionCleanupItems_PendingQueue]
        ON [dbo].[SourceDeletionCleanupItems] ([SourceDeletionOperationId],[State],[StorageKind],[Id]);
IF NOT EXISTS(
    SELECT 1 FROM sys.indexes index_info
    WHERE index_info.object_id=@queueObjectId AND index_info.name=N'IX_SourceDeletionCleanupItems_PendingQueue'
      AND index_info.type=2 AND index_info.is_unique=0 AND index_info.has_filter=0
      AND index_info.is_disabled=0 AND index_info.is_hypothetical=0
      AND (SELECT COUNT(*) FROM sys.index_columns c WHERE c.object_id=index_info.object_id AND c.index_id=index_info.index_id)=4
      AND NOT EXISTS(
          SELECT expected.Name,expected.Ordinal FROM (VALUES(N'SourceDeletionOperationId',1),(N'State',2),(N'StorageKind',3),(N'Id',4)) expected(Name,Ordinal)
          EXCEPT
          SELECT column_info.name,membership.key_ordinal FROM sys.index_columns membership
          INNER JOIN sys.columns column_info ON column_info.object_id=membership.object_id AND column_info.column_id=membership.column_id
          WHERE membership.object_id=index_info.object_id AND membership.index_id=index_info.index_id
            AND membership.is_included_column=0 AND membership.is_descending_key=0))
    THROW 51000,'source-deletion-pending-queue-index-definition-invalid',1;
