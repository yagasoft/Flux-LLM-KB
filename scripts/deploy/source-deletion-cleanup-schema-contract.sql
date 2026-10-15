SELECT (SELECT
 JSON_QUERY(COALESCE((SELECT c.name, ty.name AS [type], SCHEMA_NAME(ty.schema_id) AS type_schema,
   c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, c.is_sparse,
   c.collation_name, dc.definition AS default_definition
   FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id
   LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
   WHERE c.object_id=OBJECT_ID(N'dbo.SourceDeletionOperations') AND c.name IN
     (N'LastCommittedLeaseId',N'LastProgressAtUtc',N'MembershipRowsDeleted',N'MembershipVectorCursor',N'ResumePhase')
   ORDER BY c.name FOR JSON PATH, INCLUDE_NULL_VALUES),N'[]')) AS [columns],
 JSON_QUERY(COALESCE((SELECT name, definition, is_disabled, is_not_trusted, is_not_for_replication
   FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.SourceDeletionOperations')
     AND name LIKE N'CK_SourceDeletionOperations_Membership%'
   ORDER BY name FOR JSON PATH, INCLUDE_NULL_VALUES),N'[]')) AS checks
FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER) AS SchemaContract;
