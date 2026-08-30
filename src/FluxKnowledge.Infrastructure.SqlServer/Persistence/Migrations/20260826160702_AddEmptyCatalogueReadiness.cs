using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmptyCatalogueReadiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmptyCatalogueValidatedAtUtc",
                table: "IndexState",
                type: "datetimeoffset(7)",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "IndexState",
                keyColumn: "Id",
                keyValue: 1,
                column: "EmptyCatalogueValidatedAtUtc",
                value: null);

            migrationBuilder.AddCheckConstraint(
                name: "CK_IndexState_ActiveGenerationOrEmptyCatalogue",
                table: "IndexState",
                sql: "[ActiveIndexGenerationId] IS NULL OR [EmptyCatalogueValidatedAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_IndexState_ActiveGenerationOrEmptyCatalogue",
                table: "IndexState");

            migrationBuilder.DropColumn(
                name: "EmptyCatalogueValidatedAtUtc",
                table: "IndexState");
        }
    }
}
