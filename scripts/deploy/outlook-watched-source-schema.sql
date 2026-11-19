SELECT
    JSON_QUERY((
        SELECT OBJECT_NAME(c.object_id) AS TableName, c.name AS Name, TYPE_NAME(c.user_type_id) AS TypeName,
               c.max_length AS MaxLength, c.precision AS [Precision], c.scale AS Scale,
               c.is_nullable AS IsNullable, c.collation_name AS Collation
        FROM sys.columns c
        WHERE c.object_id IN (OBJECT_ID(N'dbo.OutlookCaptureProfiles'), OBJECT_ID(N'dbo.OutlookCaptureExports'))
          AND c.name LIKE N'Watched%'
        ORDER BY TableName, Name
        FOR JSON PATH, INCLUDE_NULL_VALUES
    )) AS WatchedColumns,
    JSON_QUERY((
        SELECT OBJECT_NAME(i.object_id) AS TableName, i.name AS Name, i.type AS [Type], i.is_unique AS IsUnique,
               i.is_disabled AS IsDisabled, i.filter_definition AS FilterDefinition,
               JSON_QUERY((
                   SELECT COL_NAME(k.object_id, k.column_id) AS Name, k.key_ordinal AS KeyOrdinal,
                          k.is_descending_key AS IsDescending, k.is_included_column AS IsIncluded
                   FROM sys.index_columns k
                   WHERE k.object_id = i.object_id AND k.index_id = i.index_id
                   ORDER BY k.index_column_id
                   FOR JSON PATH
               )) AS Columns
        FROM sys.indexes i
        WHERE i.object_id IN (OBJECT_ID(N'dbo.OutlookCaptureProfiles'), OBJECT_ID(N'dbo.OutlookCaptureExports'))
          AND (i.name LIKE N'%Watched%' OR i.name = N'IX_OutlookCaptureExports_ProfileId')
        ORDER BY TableName, Name
        FOR JSON PATH, INCLUDE_NULL_VALUES
    )) AS Indexes,
    JSON_QUERY((
        SELECT f.name AS Name, OBJECT_SCHEMA_NAME(f.referenced_object_id) AS TargetSchema,
               OBJECT_NAME(f.referenced_object_id) AS TargetTable, f.delete_referential_action AS DeleteAction,
               f.update_referential_action AS UpdateAction, f.is_disabled AS IsDisabled, f.is_not_trusted AS IsNotTrusted,
               COL_NAME(k.parent_object_id, k.parent_column_id) AS SourceColumn,
               COL_NAME(k.referenced_object_id, k.referenced_column_id) AS TargetColumn
        FROM sys.foreign_keys f
        JOIN sys.foreign_key_columns k ON k.constraint_object_id = f.object_id
        WHERE f.parent_object_id = OBJECT_ID(N'dbo.OutlookCaptureProfiles') AND f.name LIKE N'%Watched%'
        ORDER BY f.name, k.constraint_column_id
        FOR JSON PATH
    )) AS ForeignKeys
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
