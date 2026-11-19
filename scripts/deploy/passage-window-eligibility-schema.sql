SELECT
 JSON_QUERY((SELECT t.name AS [Table],c.name AS [Name],TYPE_NAME(c.system_type_id) AS [Type],c.max_length AS [Bytes],
  c.precision AS [Precision],c.scale AS [Scale],c.is_nullable AS [Nullable],c.is_identity AS [Identity],c.is_computed AS [Computed],
  CASE WHEN c.name=N'RuleVersion' THEN c.collation_name ELSE NULL END AS [RuleCollation],d.definition AS [Default]
  FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id
  LEFT JOIN sys.default_constraints d ON d.object_id=c.default_object_id
  WHERE SCHEMA_NAME(t.schema_id)=N'dbo' AND (t.name IN (N'TextChunkWindowEligibilities',N'TextChunkWindowBackfillStates')
   OR t.name=N'Artifacts' AND c.name=N'WindowEligibilityVersion' OR t.name=N'SourceDeletionOperations' AND c.name=N'WithdrawalVectorCursor')
  ORDER BY t.name,c.column_id FOR JSON PATH,INCLUDE_NULL_VALUES)) AS [Columns],
 JSON_QUERY((SELECT t.name AS [Table],i.name AS [Name],i.type AS [Type],i.is_unique AS [Unique],i.is_disabled AS [Disabled],
  JSON_QUERY((SELECT c.name AS [Name],ic.key_ordinal AS [Ordinal],ic.is_descending_key AS [Descending],ic.is_included_column AS [Included]
   FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
   WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id ORDER BY ic.index_column_id FOR JSON PATH)) AS [Columns]
  FROM sys.tables t JOIN sys.indexes i ON i.object_id=t.object_id WHERE SCHEMA_NAME(t.schema_id)=N'dbo'
   AND t.name IN (N'TextChunkWindowEligibilities',N'TextChunkWindowBackfillStates') AND i.index_id>0
  ORDER BY t.name,i.index_id FOR JSON PATH)) AS [Keys],
 JSON_QUERY((SELECT t.name AS [Table],f.name AS [Name],OBJECT_SCHEMA_NAME(f.referenced_object_id) AS [ReferenceSchema],
  OBJECT_NAME(f.referenced_object_id) AS [ReferenceTable],f.delete_referential_action AS [Delete],f.update_referential_action AS [Update],
  f.is_disabled AS [Disabled],f.is_not_trusted AS [Untrusted],
  JSON_QUERY((SELECT COL_NAME(fc.parent_object_id,fc.parent_column_id) AS [Column],
   COL_NAME(fc.referenced_object_id,fc.referenced_column_id) AS [ReferenceColumn]
   FROM sys.foreign_key_columns fc WHERE fc.constraint_object_id=f.object_id ORDER BY fc.constraint_column_id FOR JSON PATH)) AS [Columns]
  FROM sys.tables t JOIN sys.foreign_keys f ON f.parent_object_id=t.object_id WHERE SCHEMA_NAME(t.schema_id)=N'dbo'
   AND t.name IN (N'TextChunkWindowEligibilities',N'TextChunkWindowBackfillStates') ORDER BY t.name,f.name FOR JSON PATH)) AS [ForeignKeys],
 JSON_QUERY((SELECT t.name AS [Table],c.name AS [Name],c.definition AS [Definition],c.is_disabled AS [Disabled],c.is_not_trusted AS [Untrusted]
  FROM sys.tables t JOIN sys.check_constraints c ON c.parent_object_id=t.object_id WHERE SCHEMA_NAME(t.schema_id)=N'dbo'
   AND t.name IN (N'TextChunkWindowEligibilities',N'TextChunkWindowBackfillStates') ORDER BY t.name,c.name FOR JSON PATH)) AS [Checks]
FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;
