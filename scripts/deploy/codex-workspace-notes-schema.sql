DECLARE @table int = OBJECT_ID(N'dbo.KnowledgeItems', N'U');
DECLARE @column int = COLUMNPROPERTY(@table, 'CodexWorkspaceRootId', 'ColumnId');
DECLARE @index int = (SELECT index_id FROM sys.indexes WHERE object_id = @table
    AND name = N'IX_KnowledgeItems_CodexWorkspaceRootId_CreatedAtUtc_Id');

SELECT CASE
    WHEN @table IS NULL THEN N'Invalid'
    WHEN @column IS NULL AND @index IS NULL THEN N'Old'
    WHEN EXISTS (
        SELECT 1 FROM sys.columns WHERE object_id = @table AND column_id = @column
        AND system_type_id = TYPE_ID(N'uniqueidentifier') AND user_type_id = system_type_id
        AND max_length = 16 AND is_nullable = 1 AND is_computed = 0 AND is_sparse = 0
        AND is_hidden = 0 AND default_object_id = 0 AND rule_object_id = 0
    )
    AND EXISTS (
        SELECT 1 FROM sys.indexes WHERE object_id = @table AND index_id = @index
        AND type = 2 AND is_unique = 0 AND is_disabled = 0 AND is_hypothetical = 0 AND has_filter = 1
        AND REPLACE(REPLACE(REPLACE(filter_definition, N'(', N''), N')', N''), N' ', N'')
            = N'[CodexWorkspaceRootId]ISNOTNULLAND[ForgottenAtUtc]ISNULL'
    )
    AND (SELECT COUNT(*) FROM sys.index_columns WHERE object_id = @table AND index_id = @index) = 3
    AND EXISTS (SELECT 1 FROM sys.index_columns WHERE object_id = @table AND index_id = @index
        AND column_id = @column AND key_ordinal = 1 AND is_descending_key = 0 AND is_included_column = 0)
    AND EXISTS (SELECT 1 FROM sys.index_columns WHERE object_id = @table AND index_id = @index
        AND column_id = COLUMNPROPERTY(@table, 'CreatedAtUtc', 'ColumnId')
        AND key_ordinal = 2 AND is_descending_key = 1 AND is_included_column = 0)
    AND EXISTS (SELECT 1 FROM sys.index_columns WHERE object_id = @table AND index_id = @index
        AND column_id = COLUMNPROPERTY(@table, 'Id', 'ColumnId')
        AND key_ordinal = 3 AND is_descending_key = 0 AND is_included_column = 0)
    THEN N'New'
    ELSE N'Invalid'
END;
