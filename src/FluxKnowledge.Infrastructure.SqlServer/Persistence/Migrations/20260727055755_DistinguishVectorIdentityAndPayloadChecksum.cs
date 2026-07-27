using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DistinguishVectorIdentityAndPayloadChecksum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Vectors_ContentHash",
                table: "Vectors");

            migrationBuilder.RenameColumn(
                name: "ContentHash",
                table: "Vectors",
                newName: "PayloadChecksum");

            migrationBuilder.AddColumn<string>(
                name: "TextChunkContentHash",
                table: "Vectors",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE [vector]
                SET [TextChunkContentHash] = [chunk].[ContentHash]
                FROM [Vectors] AS [vector]
                INNER JOIN [TextChunks] AS [chunk]
                    ON [chunk].[Id] = [vector].[TextChunkId]
                   AND [chunk].[SourceRevision] = [vector].[SourceRevision];

                IF EXISTS (
                    SELECT 1
                    FROM [Vectors]
                    WHERE [TextChunkContentHash] IS NULL)
                BEGIN
                    THROW 51000, 'Every vector must resolve to its canonical text chunk before hash semantics can be separated.', 1;
                END;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "TextChunkContentHash",
                table: "Vectors",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "char(64)",
                oldUnicode: false,
                oldFixedLength: true,
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Vectors_PayloadChecksum",
                table: "Vectors",
                sql: "LEN([PayloadChecksum]) = 64 AND [PayloadChecksum] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Vectors_TextChunkContentHash",
                table: "Vectors",
                sql: "LEN([TextChunkContentHash]) = 64 AND [TextChunkContentHash] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Vectors_PayloadChecksum",
                table: "Vectors");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Vectors_TextChunkContentHash",
                table: "Vectors");

            migrationBuilder.DropColumn(
                name: "TextChunkContentHash",
                table: "Vectors");

            migrationBuilder.RenameColumn(
                name: "PayloadChecksum",
                table: "Vectors",
                newName: "ContentHash");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Vectors_ContentHash",
                table: "Vectors",
                sql: "LEN([ContentHash]) = 64 AND [ContentHash] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
        }
    }
}
