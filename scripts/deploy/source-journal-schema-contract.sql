DECLARE @tables TABLE (Name sysname PRIMARY KEY);
INSERT @tables VALUES (N'SourceInventoryEntries'),(N'SourcePendingChanges'),
 (N'SourceRootScanStates'),(N'SourceVerificationRuns'),
 (N'SourceVerificationItems'),(N'SourceVolumeJournalCheckpoints');
DECLARE @extra TABLE (TableName sysname, ColumnName sysname);
INSERT @extra VALUES (N'SourceScanRequests',N'IncrementalManifestJson'),
 (N'SourceScanRequests',N'VerificationRunId'),(N'SourceRootWatchStates',N'HintsJson');
-- Return a single nvarchar(max) cell, rather than FOR JSON's chunked row stream.
DECLARE @json nvarchar(max) = (
 SELECT
  (SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
   WHERE s.name=N'dbo' AND t.name IN (SELECT Name FROM @tables)
   ORDER BY t.name FOR JSON PATH) AS tables,
  (SELECT t.name AS [table],c.name,ty.name AS [type],c.max_length,
    c.precision,c.scale,c.is_nullable,c.is_identity,c.is_computed,c.collation_name,
    dc.definition AS [default]
   FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
   JOIN sys.columns c ON c.object_id=t.object_id
   JOIN sys.types ty ON ty.user_type_id=c.user_type_id
   LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
   WHERE s.name=N'dbo' AND (t.name IN (SELECT Name FROM @tables) OR
    EXISTS (SELECT 1 FROM @extra e WHERE e.TableName=t.name AND e.ColumnName=c.name))
   ORDER BY t.name,c.name FOR JSON PATH,INCLUDE_NULL_VALUES) AS columns,
  (SELECT t.name AS [table],i.name,i.type,i.is_unique,i.is_primary_key,i.is_disabled,
    i.has_filter,i.filter_definition,
    (SELECT c.name,ic.key_ordinal,ic.is_descending_key,ic.is_included_column
     FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
     WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id
     ORDER BY ic.index_column_id FOR JSON PATH) AS columns
   FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
   JOIN sys.indexes i ON i.object_id=t.object_id AND i.index_id>0
   WHERE s.name=N'dbo' AND t.name IN (SELECT Name FROM @tables)
   ORDER BY t.name,i.name FOR JSON PATH,INCLUDE_NULL_VALUES) AS indexes,
  (SELECT t.name AS [table],f.name,rt.name AS referenced_table,rs.name AS referenced_schema,
    f.delete_referential_action,f.update_referential_action,f.is_disabled,f.is_not_trusted,
    (SELECT pc.name AS parent_column,rc.name AS referenced_column
     FROM sys.foreign_key_columns fc
     JOIN sys.columns pc ON pc.object_id=fc.parent_object_id AND pc.column_id=fc.parent_column_id
     JOIN sys.columns rc ON rc.object_id=fc.referenced_object_id AND rc.column_id=fc.referenced_column_id
     WHERE fc.constraint_object_id=f.object_id ORDER BY fc.constraint_column_id FOR JSON PATH) AS columns
   FROM sys.foreign_keys f JOIN sys.tables t ON t.object_id=f.parent_object_id
   JOIN sys.schemas s ON s.schema_id=t.schema_id
   JOIN sys.tables rt ON rt.object_id=f.referenced_object_id
   JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
   WHERE s.name=N'dbo' AND t.name IN (SELECT Name FROM @tables)
   ORDER BY t.name,f.name FOR JSON PATH) AS foreign_keys,
  (SELECT t.name AS [table],cc.name,cc.definition,cc.is_disabled,cc.is_not_trusted
   FROM sys.check_constraints cc JOIN sys.tables t ON t.object_id=cc.parent_object_id
   JOIN sys.schemas s ON s.schema_id=t.schema_id
   WHERE s.name=N'dbo' AND t.name IN (SELECT Name FROM @tables)
   ORDER BY t.name,cc.name FOR JSON PATH) AS checks
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER
);
SELECT @json;
