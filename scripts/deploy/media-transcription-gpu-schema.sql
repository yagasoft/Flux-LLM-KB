DECLARE @object int=OBJECT_ID(N'dbo.MediaTranscriptionGpuRequests');
IF @object IS NULL BEGIN SELECT N'Old'; RETURN; END;
IF NOT EXISTS(SELECT 1 FROM sys.tables WHERE object_id=@object AND is_memory_optimized=0 AND temporal_type=0)
   OR EXISTS(SELECT 1 FROM sys.triggers WHERE parent_id=@object)
   OR EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=@object OR referenced_object_id=@object)
BEGIN SELECT N'Unknown'; RETURN; END;
DECLARE @definition nvarchar(max)=(SELECT
 JSON_QUERY((SELECT c.column_id,c.name,TYPE_NAME(c.user_type_id) type_name,c.max_length,c.precision,c.scale,c.is_nullable,
    CASE WHEN c.collation_name=CONVERT(nvarchar(128),DATABASEPROPERTYEX(DB_NAME(),'Collation'))
         AND c.name NOT IN ('ModelRuntimeKey','SettingsFingerprint','ContentSha256') THEN N'DATABASE_DEFAULT' ELSE c.collation_name END collation_name,
    c.is_identity,c.is_computed,c.is_sparse,c.is_rowguidcol,c.generated_always_type,c.is_hidden,
    CONVERT(bit,CASE WHEN c.default_object_id=0 THEN 0 ELSE 1 END) has_default
    FROM sys.columns c WHERE object_id=@object ORDER BY column_id FOR JSON PATH)) cols,
 JSON_QUERY((SELECT name,definition,is_disabled,is_not_trusted,is_not_for_replication
    FROM sys.check_constraints WHERE parent_object_id=@object ORDER BY name FOR JSON PATH)) checks,
 JSON_QUERY((SELECT i.name,i.type,i.is_unique,i.is_primary_key,i.is_unique_constraint,i.is_disabled,i.has_filter,i.filter_definition,
    i.ignore_dup_key, i.is_hypothetical,
    JSON_QUERY((SELECT ic.key_ordinal,ic.is_descending_key,ic.is_included_column,c.name
                FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id ORDER BY ic.index_column_id FOR JSON PATH)) cols
    FROM sys.indexes i WHERE i.object_id=@object ORDER BY i.name FOR JSON PATH)) indexes
 FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',@definition),2);
