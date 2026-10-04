SELECT COALESCE((SELECT t.name AS [table],
 JSON_QUERY((SELECT c.name, ty.name AS [type], SCHEMA_NAME(ty.schema_id) AS type_schema,
   c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, c.is_sparse,
   CASE WHEN c.name <> N'Fingerprint' AND c.collation_name = CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))
     THEN N'<database-default>' ELSE c.collation_name END AS collation_name,
   dc.definition AS default_definition, cc.definition AS computed_definition
   FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
   LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
   LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
   WHERE c.object_id = t.object_id ORDER BY c.column_id FOR JSON PATH, INCLUDE_NULL_VALUES)) AS columns,
 JSON_QUERY((SELECT i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_disabled, i.filter_definition,
   JSON_QUERY((SELECT c.name, ic.key_ordinal, ic.is_descending_key, ic.is_included_column
     FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
     WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
     ORDER BY ic.index_column_id FOR JSON PATH, INCLUDE_NULL_VALUES)) AS columns
   FROM sys.indexes i WHERE i.object_id = t.object_id AND i.index_id > 0
   ORDER BY i.name FOR JSON PATH, INCLUDE_NULL_VALUES)) AS indexes,
 JSON_QUERY((SELECT fk.name, SCHEMA_NAME(rt.schema_id) AS referenced_schema, rt.name AS referenced_table,
   fk.delete_referential_action_desc, fk.update_referential_action_desc, fk.is_disabled, fk.is_not_trusted, fk.is_not_for_replication,
   JSON_QUERY((SELECT pc.name AS parent_column, rc.name AS referenced_column
     FROM sys.foreign_key_columns fc
     JOIN sys.columns pc ON pc.object_id = fc.parent_object_id AND pc.column_id = fc.parent_column_id
     JOIN sys.columns rc ON rc.object_id = fc.referenced_object_id AND rc.column_id = fc.referenced_column_id
     WHERE fc.constraint_object_id = fk.object_id ORDER BY fc.constraint_column_id
     FOR JSON PATH, INCLUDE_NULL_VALUES)) AS columns
   FROM sys.foreign_keys fk JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
   WHERE fk.parent_object_id = t.object_id ORDER BY fk.name FOR JSON PATH, INCLUDE_NULL_VALUES)) AS foreign_keys,
 JSON_QUERY((SELECT ck.name, ck.definition, ck.is_disabled, ck.is_not_trusted, ck.is_not_for_replication
   FROM sys.check_constraints ck WHERE ck.parent_object_id = t.object_id
   ORDER BY ck.name FOR JSON PATH, INCLUDE_NULL_VALUES)) AS checks,
 JSON_QUERY((SELECT tr.name, tr.is_disabled FROM sys.triggers tr WHERE tr.parent_id = t.object_id
   ORDER BY tr.name FOR JSON PATH, INCLUDE_NULL_VALUES)) AS triggers
FROM sys.tables t
WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name IN (N'CanonicalCodeDisclosureProofs', N'CanonicalCodeDisclosureSpans')
ORDER BY t.name FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]') AS SchemaContract;
