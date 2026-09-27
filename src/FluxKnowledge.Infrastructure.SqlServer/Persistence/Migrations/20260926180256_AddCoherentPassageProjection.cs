using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoherentPassageProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Full-Text DDL cannot share the schema transaction. A failure after
            // its commit but before the history receipt must permit a safe replay.
            migrationBuilder.Sql(
                """
                IF COL_LENGTH(N'dbo.TextChunks', N'ContextHeader') IS NULL
                    ALTER TABLE [dbo].[TextChunks] ADD [ContextHeader] nvarchar(256) NOT NULL DEFAULT N'';
                IF COL_LENGTH(N'dbo.TextChunks', N'PassagePolicyFingerprint') IS NULL
                    ALTER TABLE [dbo].[TextChunks] ADD [PassagePolicyFingerprint] nvarchar(64) NOT NULL DEFAULT N'';
                IF COL_LENGTH(N'dbo.TextChunks', N'SearchInputHash') IS NULL
                    ALTER TABLE [dbo].[TextChunks] ADD [SearchInputHash] nvarchar(64) NOT NULL DEFAULT N'';
                IF COL_LENGTH(N'dbo.IndexState', N'CorpusEpoch') IS NULL
                    ALTER TABLE [dbo].[IndexState] ADD [CorpusEpoch] uniqueidentifier NOT NULL DEFAULT NEWID();
                IF COL_LENGTH(N'dbo.TextChunks', N'SearchText') IS NULL
                    EXEC(N'ALTER TABLE [dbo].[TextChunks] ADD [SearchText] AS
                        (CASE WHEN [ContextHeader] = N'''' THEN [Content]
                         ELSE [ContextHeader] + NCHAR(10) + [Content] END) PERSISTED');
                """);

            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE [object_id] = OBJECT_ID(N'[dbo].[TextChunks]'))
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_index_columns
                        WHERE [object_id] = OBJECT_ID(N'[dbo].[TextChunks]')
                        AND [column_id] = COLUMNPROPERTY(OBJECT_ID(N'[dbo].[TextChunks]'), N'SearchText', 'ColumnId'))
                        ALTER FULLTEXT INDEX ON [dbo].[TextChunks] ADD ([SearchText] LANGUAGE 1033);
                    IF EXISTS (SELECT 1 FROM sys.fulltext_index_columns
                        WHERE [object_id] = OBJECT_ID(N'[dbo].[TextChunks]')
                        AND [column_id] = COLUMNPROPERTY(OBJECT_ID(N'[dbo].[TextChunks]'), N'Content', 'ColumnId'))
                        ALTER FULLTEXT INDEX ON [dbo].[TextChunks] DROP ([Content]);
                END;
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE [object_id] = OBJECT_ID(N'[dbo].[TextChunks]'))
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_index_columns
                        WHERE [object_id] = OBJECT_ID(N'[dbo].[TextChunks]')
                        AND [column_id] = COLUMNPROPERTY(OBJECT_ID(N'[dbo].[TextChunks]'), N'Content', 'ColumnId'))
                        ALTER FULLTEXT INDEX ON [dbo].[TextChunks] ADD ([Content] LANGUAGE 1033);
                    IF EXISTS (SELECT 1 FROM sys.fulltext_index_columns
                        WHERE [object_id] = OBJECT_ID(N'[dbo].[TextChunks]')
                        AND [column_id] = COLUMNPROPERTY(OBJECT_ID(N'[dbo].[TextChunks]'), N'SearchText', 'ColumnId'))
                        ALTER FULLTEXT INDEX ON [dbo].[TextChunks] DROP ([SearchText]);
                END;
                """, suppressTransaction: true);

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "TextChunks");

            migrationBuilder.DropColumn(
                name: "ContextHeader",
                table: "TextChunks");

            migrationBuilder.DropColumn(
                name: "PassagePolicyFingerprint",
                table: "TextChunks");

            migrationBuilder.DropColumn(
                name: "SearchInputHash",
                table: "TextChunks");

            migrationBuilder.DropColumn(
                name: "CorpusEpoch",
                table: "IndexState");
        }
    }
}
