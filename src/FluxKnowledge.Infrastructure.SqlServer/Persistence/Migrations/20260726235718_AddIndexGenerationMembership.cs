using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexGenerationMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IndexGenerationVectors",
                columns: table => new
                {
                    GenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VectorId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexGenerationVectors", x => new { x.GenerationId, x.VectorId });
                    table.ForeignKey(
                        name: "FK_IndexGenerationVectors_IndexGenerations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "IndexGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndexGenerationVectors_Vectors_VectorId",
                        column: x => x.VectorId,
                        principalTable: "Vectors",
                        principalColumn: "VectorId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IndexGenerationVectors_VectorId",
                table: "IndexGenerationVectors",
                column: "VectorId");

            migrationBuilder.Sql(
                """
                INSERT INTO [IndexGenerationVectors] ([GenerationId], [VectorId])
                SELECT [IndexGenerationId], [VectorId]
                FROM [Vectors];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS
                (
                    SELECT 1
                    FROM [IndexGenerationVectors] [m]
                    INNER JOIN [Vectors] [v] ON [v].[VectorId] = [m].[VectorId]
                    WHERE [m].[GenerationId] <> [v].[IndexGenerationId]
                )
                    THROW 51000, 'Cannot downgrade immutable generation membership with snapshot-only history.', 1;
                """);
            migrationBuilder.DropTable(
                name: "IndexGenerationVectors");
        }
    }
}
