DECLARE @objects TABLE (name sysname PRIMARY KEY);
INSERT @objects VALUES (N'IndexProjectionChanges'),(N'IndexPublicationCandidates'),(N'IndexSegments'),
 (N'IndexGenerationSegments'),(N'IndexSegmentOwners'),(N'IndexSegmentVectors'),(N'CurrentIndexMembers'),
 (N'IndexCandidateMembers'),(N'IndexRecoveryAudit'),(N'IndexRecoveryPins'),(N'IndexRecoveryWriters');
SELECT (SELECT
 JSON_QUERY(COALESCE((SELECT t.name AS [table],c.name,ty.name AS [type],c.max_length,c.precision,c.scale,
   c.is_nullable,c.is_identity,c.is_computed,c.collation_name,dc.definition AS default_definition
   FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
   LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
   WHERE t.schema_id=SCHEMA_ID(N'dbo') AND (t.name IN (SELECT name FROM @objects) OR
    t.name=N'IndexState' AND c.name IN (N'RequiredIndexFormat',N'ProjectionChangeFloorVersion') OR
    t.name=N'IndexGenerations' AND c.name IN (N'FormatVersion',N'SegmentCount',N'ReclaimedAtUtc',N'RetirementOperationId'))
   ORDER BY t.name,c.name FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]')) AS [columns],
 JSON_QUERY(COALESCE((SELECT t.name AS [table],c.name,c.definition,c.is_disabled,c.is_not_trusted,c.is_not_for_replication
   FROM sys.check_constraints c JOIN sys.tables t ON t.object_id=c.parent_object_id
   WHERE t.schema_id=SCHEMA_ID(N'dbo') AND (t.name IN (SELECT name FROM @objects) OR
    t.name=N'SourceDeletionCleanupItems' AND c.name=N'CK_SourceDeletionCleanupItems_StorageKind')
   ORDER BY t.name,c.name FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]')) AS checks,
 JSON_QUERY(COALESCE((SELECT t.name AS [table],i.name,i.type,i.is_unique,i.is_primary_key,i.is_disabled,i.has_filter,i.filter_definition,
   JSON_QUERY((SELECT c.name,ic.key_ordinal,ic.is_descending_key,ic.is_included_column
    FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
    WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id ORDER BY ic.index_column_id FOR JSON PATH)) AS [columns]
   FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id
   WHERE t.schema_id=SCHEMA_ID(N'dbo') AND t.name IN (SELECT name FROM @objects) AND i.index_id>0
   ORDER BY t.name,i.name FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]')) AS indexes,
 JSON_QUERY(COALESCE((SELECT t.name AS [table],f.name,f.is_disabled,f.is_not_trusted,f.delete_referential_action,f.update_referential_action,
   JSON_QUERY((SELECT pc.name AS [column],rt.name AS referenced_table,rc.name AS referenced_column
    FROM sys.foreign_key_columns fc JOIN sys.columns pc ON pc.object_id=fc.parent_object_id AND pc.column_id=fc.parent_column_id
    JOIN sys.tables rt ON rt.object_id=fc.referenced_object_id
    JOIN sys.columns rc ON rc.object_id=fc.referenced_object_id AND rc.column_id=fc.referenced_column_id
    WHERE fc.constraint_object_id=f.object_id ORDER BY fc.constraint_column_id FOR JSON PATH)) AS [columns]
   FROM sys.foreign_keys f JOIN sys.tables t ON t.object_id=f.parent_object_id
   WHERE t.schema_id=SCHEMA_ID(N'dbo') AND t.name IN (SELECT name FROM @objects)
   ORDER BY t.name,f.name FOR JSON PATH,INCLUDE_NULL_VALUES),N'[]')) AS foreign_keys
 FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER) AS SchemaContract;
