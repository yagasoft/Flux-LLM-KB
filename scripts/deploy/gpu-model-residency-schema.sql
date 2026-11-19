DECLARE @resident int=OBJECT_ID(N'dbo.GpuModelResidencies'), @receipt int=OBJECT_ID(N'dbo.GpuModelDetachmentReceipts');
DECLARE @objects TABLE(name sysname PRIMARY KEY, table_id int NOT NULL);
INSERT INTO @objects SELECT name,object_id FROM sys.tables WHERE schema_id=SCHEMA_ID(N'dbo') AND
   name IN (N'EmbeddingGpuRequests',N'MediaTranscriptionGpuRequests',N'DocumentOcrRequests',N'GpuModelResidencies',N'GpuModelDetachmentReceipts');
IF (SELECT COUNT(*) FROM @objects) NOT IN (3,5)
   OR (SELECT COUNT(*) FROM @objects WHERE name IN (N'EmbeddingGpuRequests',N'MediaTranscriptionGpuRequests',N'DocumentOcrRequests'))<>3
   OR (@resident IS NOT NULL AND NOT EXISTS(SELECT 1 FROM @objects WHERE table_id=@resident))
   OR (@receipt IS NOT NULL AND NOT EXISTS(SELECT 1 FROM @objects WHERE table_id=@receipt))
   OR EXISTS(SELECT 1 FROM sys.tables t JOIN @objects o ON o.table_id=t.object_id WHERE t.is_memory_optimized<>0 OR t.temporal_type<>0)
   OR EXISTS(SELECT 1 FROM sys.triggers WHERE parent_id IN (SELECT table_id FROM @objects))
BEGIN SELECT N'Unknown'; RETURN; END;
DECLARE @definition nvarchar(max)=(SELECT t.name,
 JSON_QUERY((SELECT c.name,TYPE_NAME(c.user_type_id) type_name,c.max_length,c.precision,c.scale,c.is_nullable,
    CASE WHEN c.collation_name=CONVERT(nvarchar(128),DATABASEPROPERTYEX(DB_NAME(),'Collation'))
          AND c.name NOT IN ('SlotKey','RuntimeKey','SettingsFingerprint','ModelKey','ExecutorKey') THEN N'DATABASE_DEFAULT' ELSE c.collation_name END collation_name,
    c.is_identity,c.is_computed,c.is_sparse,c.is_rowguidcol,c.generated_always_type,c.is_hidden,OBJECT_DEFINITION(c.default_object_id) default_definition
    FROM sys.columns c WHERE c.object_id=t.object_id ORDER BY c.column_id FOR JSON PATH)) cols,
 JSON_QUERY((SELECT name,definition,is_disabled,is_not_trusted,is_not_for_replication
    FROM sys.check_constraints WHERE parent_object_id=t.object_id ORDER BY name FOR JSON PATH)) checks,
 JSON_QUERY((SELECT i.name,i.type,i.is_unique,i.is_primary_key,i.is_unique_constraint,i.is_disabled,i.has_filter,i.filter_definition,
    i.ignore_dup_key,i.is_hypothetical,
    JSON_QUERY((SELECT ic.key_ordinal,ic.is_descending_key,ic.is_included_column,c.name
                FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id ORDER BY ic.index_column_id FOR JSON PATH)) cols
    FROM sys.indexes i WHERE i.object_id=t.object_id ORDER BY i.name FOR JSON PATH)) indexes,
 JSON_QUERY((SELECT fk.name,OBJECT_SCHEMA_NAME(fk.parent_object_id) parent_schema,OBJECT_NAME(fk.parent_object_id) parent_table,
    OBJECT_SCHEMA_NAME(fk.referenced_object_id) referenced_schema,OBJECT_NAME(fk.referenced_object_id) referenced_table,
    fk.delete_referential_action,fk.update_referential_action,fk.is_disabled,fk.is_not_trusted,fk.is_not_for_replication,
    JSON_QUERY((SELECT COL_NAME(c.parent_object_id,c.parent_column_id) parent_col,COL_NAME(c.referenced_object_id,c.referenced_column_id) referenced_col
                FROM sys.foreign_key_columns c WHERE c.constraint_object_id=fk.object_id ORDER BY c.constraint_column_id FOR JSON PATH)) cols
    FROM sys.foreign_keys fk WHERE fk.parent_object_id=t.object_id OR fk.referenced_object_id=t.object_id ORDER BY fk.name FOR JSON PATH)) fks
 FROM sys.tables t WHERE t.object_id IN (SELECT table_id FROM @objects) ORDER BY t.name FOR JSON PATH);
SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',@definition),2);
