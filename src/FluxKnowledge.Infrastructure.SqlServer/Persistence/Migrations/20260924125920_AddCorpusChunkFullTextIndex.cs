using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCorpusChunkFullTextIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF CONVERT(int, SERVERPROPERTY('IsFullTextInstalled')) = 1
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.fulltext_indexes
                       WHERE [object_id] = OBJECT_ID(N'[dbo].[TextChunks]'))
                BEGIN
                    CREATE FULLTEXT INDEX ON [dbo].[TextChunks]
                    (
                        [Content] LANGUAGE 1033
                    )
                    KEY INDEX [PK_TextChunks]
                    ON [FluxKnowledge]
                    WITH CHANGE_TRACKING AUTO;
                END;
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1 FROM sys.fulltext_indexes
                    WHERE [object_id] = OBJECT_ID(N'[dbo].[TextChunks]'))
                BEGIN
                    DROP FULLTEXT INDEX ON [dbo].[TextChunks];
                END;
                """,
                suppressTransaction: true);
        }
    }
}
