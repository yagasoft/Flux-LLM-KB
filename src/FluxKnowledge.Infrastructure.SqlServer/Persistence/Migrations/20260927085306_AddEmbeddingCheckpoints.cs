using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchInputHash",
                table: "Vectors",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EmbeddingJobId",
                table: "IndexGenerations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Vectors_SearchInputHash",
                table: "Vectors",
                sql: "[SearchInputHash] IS NULL OR (LEN([SearchInputHash]) = 64 AND [SearchInputHash] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%')");

            migrationBuilder.CreateIndex(
                name: "IX_IndexGenerations_EmbeddingJobId",
                table: "IndexGenerations",
                column: "EmbeddingJobId",
                unique: true,
                filter: "[EmbeddingJobId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_IndexGenerations_Jobs_EmbeddingJobId",
                table: "IndexGenerations",
                column: "EmbeddingJobId",
                principalTable: "Jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [IndexGenerations] WHERE [EmbeddingJobId] IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM [Vectors] WHERE [SearchInputHash] IS NOT NULL)
                    THROW 51000, 'embedding-checkpoint-downgrade-requires-empty-reset', 1;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_IndexGenerations_Jobs_EmbeddingJobId",
                table: "IndexGenerations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Vectors_SearchInputHash",
                table: "Vectors");

            migrationBuilder.DropIndex(
                name: "IX_IndexGenerations_EmbeddingJobId",
                table: "IndexGenerations");

            migrationBuilder.DropColumn(
                name: "SearchInputHash",
                table: "Vectors");

            migrationBuilder.DropColumn(
                name: "EmbeddingJobId",
                table: "IndexGenerations");
        }
    }
}
