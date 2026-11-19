using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistPassageWindowEligibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "WithdrawalVectorCursor",
                table: "SourceDeletionOperations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WindowEligibilityVersion",
                table: "Artifacts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.CreateTable(
                name: "TextChunkWindowBackfillStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    RuleVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    LastChunkId = table.Column<long>(type: "bigint", nullable: false),
                    EndChunkId = table.Column<long>(type: "bigint", nullable: false),
                    RevisitFromChunkId = table.Column<long>(type: "bigint", nullable: true),
                    Completed = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextChunkWindowBackfillStates", x => x.Id);
                    table.CheckConstraint("CK_TextChunkWindowBackfillStates_Cursor", "[LastChunkId] >= 0 AND [EndChunkId] >= 0 AND ([RevisitFromChunkId] IS NULL OR [RevisitFromChunkId] > 0)");
                    table.CheckConstraint("CK_TextChunkWindowBackfillStates_Id", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "TextChunkWindowEligibilities",
                columns: table => new
                {
                    TextChunkId = table.Column<long>(type: "bigint", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalSourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    CanonicalContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    CanonicalLength = table.Column<long>(type: "bigint", nullable: false),
                    ChunkSourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    ChunkContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    StartOffset = table.Column<int>(type: "int", nullable: false),
                    Length = table.Column<int>(type: "int", nullable: false),
                    RuleVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Available = table.Column<bool>(type: "bit", nullable: false),
                    CalculatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextChunkWindowEligibilities", x => x.TextChunkId);
                    table.ForeignKey(
                        name: "FK_TextChunkWindowEligibilities_TextChunks_TextChunkId",
                        column: x => x.TextChunkId,
                        principalTable: "TextChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TextChunkWindowBackfillStates");

            migrationBuilder.DropTable(
                name: "TextChunkWindowEligibilities");

            migrationBuilder.DropColumn(
                name: "WithdrawalVectorCursor",
                table: "SourceDeletionOperations");

            migrationBuilder.DropColumn(
                name: "WindowEligibilityVersion",
                table: "Artifacts");
        }
    }
}
