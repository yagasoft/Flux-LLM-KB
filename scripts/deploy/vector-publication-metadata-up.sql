CREATE INDEX [IX_Vectors_PublicationMetadata] ON [Vectors] ([VectorId]) INCLUDE ([TextChunkId], [SourceRevision], [PayloadChecksum], [TextChunkContentHash], [SearchInputHash], [IsDeleted]);
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261101195325_AddVectorPublicationMetadata', N'10.0.10');
GO

