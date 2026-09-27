using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCorpusRebuildSupersession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesOperationId",
                table: "CorpusRebuildOperations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CorpusRebuildSupersededJobs",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReplacementOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorpusRebuildSupersededJobs", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_CorpusRebuildSupersededJobs_CorpusRebuildOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "CorpusRebuildOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CorpusRebuildSupersededJobs_CorpusRebuildOperations_ReplacementOperationId",
                        column: x => x.ReplacementOperationId,
                        principalTable: "CorpusRebuildOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CorpusRebuildOperations_SupersedesOperationId",
                table: "CorpusRebuildOperations",
                column: "SupersedesOperationId",
                unique: true,
                filter: "[SupersedesOperationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CorpusRebuildSupersededJobs_OperationId",
                table: "CorpusRebuildSupersededJobs",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_CorpusRebuildSupersededJobs_ReplacementOperationId",
                table: "CorpusRebuildSupersededJobs",
                column: "ReplacementOperationId");

            migrationBuilder.AddForeignKey(
                name: "FK_CorpusRebuildOperations_CorpusRebuildOperations_SupersedesOperationId",
                table: "CorpusRebuildOperations",
                column: "SupersedesOperationId",
                principalTable: "CorpusRebuildOperations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [CorpusRebuildSupersededJobs]) OR
                    EXISTS (SELECT 1 FROM [CorpusRebuildOperations] WHERE [SupersedesOperationId] IS NOT NULL)
                    THROW 51000, 'corpus-rebuild-supersession-downgrade-refused', 1;
                IF EXISTS (SELECT 1 FROM [CorpusRebuildOperations])
                    THROW 51000, 'corpus-rebuild-downgrade-requires-empty-receipts', 1;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_CorpusRebuildOperations_CorpusRebuildOperations_SupersedesOperationId",
                table: "CorpusRebuildOperations");

            migrationBuilder.DropTable(
                name: "CorpusRebuildSupersededJobs");

            migrationBuilder.DropIndex(
                name: "IX_CorpusRebuildOperations_SupersedesOperationId",
                table: "CorpusRebuildOperations");

            migrationBuilder.DropColumn(
                name: "SupersedesOperationId",
                table: "CorpusRebuildOperations");
        }
    }
}
