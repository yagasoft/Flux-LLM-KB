using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCorpusRebuildWorklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CorpusRebuildOperationId",
                table: "IndexState",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CorpusRebuildOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ManifestHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ManifestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorpusRebuildOperations", x => x.Id);
                    table.CheckConstraint("CK_CorpusRebuildOperations_ManifestJson", "ISJSON([ManifestJson]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "CorpusRebuildWorkItems",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    CanonicalArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmbeddingJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispatchMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    PreparedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorpusRebuildWorkItems", x => new { x.OperationId, x.PipelineRecordId });
                    table.CheckConstraint("CK_CorpusRebuildWorkItems_State", "[State] IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_CorpusRebuildWorkItems_CorpusRebuildOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "CorpusRebuildOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "IndexState",
                keyColumn: "Id",
                keyValue: 1,
                column: "CorpusRebuildOperationId",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_IndexState_CorpusRebuildOperationId",
                table: "IndexState",
                column: "CorpusRebuildOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_CorpusRebuildOperations_TargetEpoch",
                table: "CorpusRebuildOperations",
                column: "TargetEpoch",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CorpusRebuildWorkItems_DispatchMessageId",
                table: "CorpusRebuildWorkItems",
                column: "DispatchMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CorpusRebuildWorkItems_EmbeddingJobId",
                table: "CorpusRebuildWorkItems",
                column: "EmbeddingJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CorpusRebuildWorkItems_OperationId_State",
                table: "CorpusRebuildWorkItems",
                columns: new[] { "OperationId", "State" });

            migrationBuilder.AddForeignKey(
                name: "FK_IndexState_CorpusRebuildOperations_CorpusRebuildOperationId",
                table: "IndexState",
                column: "CorpusRebuildOperationId",
                principalTable: "CorpusRebuildOperations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [CorpusRebuildOperations])
                   OR EXISTS (SELECT 1 FROM [CorpusRebuildWorkItems])
                   OR EXISTS (SELECT 1 FROM [IndexState] WHERE [CorpusRebuildOperationId] IS NOT NULL)
                    THROW 51000, 'corpus-rebuild-downgrade-requires-empty-receipts', 1;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_IndexState_CorpusRebuildOperations_CorpusRebuildOperationId",
                table: "IndexState");

            migrationBuilder.DropTable(
                name: "CorpusRebuildWorkItems");

            migrationBuilder.DropTable(
                name: "CorpusRebuildOperations");

            migrationBuilder.DropIndex(
                name: "IX_IndexState_CorpusRebuildOperationId",
                table: "IndexState");

            migrationBuilder.DropColumn(
                name: "CorpusRebuildOperationId",
                table: "IndexState");
        }
    }
}
