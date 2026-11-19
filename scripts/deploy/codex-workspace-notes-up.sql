ALTER TABLE [KnowledgeItems] ADD [CodexWorkspaceRootId] uniqueidentifier NULL;
GO

CREATE INDEX [IX_KnowledgeItems_CodexWorkspaceRootId_CreatedAtUtc_Id] ON [KnowledgeItems] ([CodexWorkspaceRootId], [CreatedAtUtc] DESC, [Id]) WHERE [CodexWorkspaceRootId] IS NOT NULL AND [ForgottenAtUtc] IS NULL;
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261118225053_BindCodexNotesToWorkspace', N'10.0.10');
GO
