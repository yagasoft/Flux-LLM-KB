using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExplicitProjectionRebuild : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BuildPurpose",
                table: "IndexPublicationCandidates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ExpectedIndexFormat",
                table: "IndexPublicationCandidates",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddCheckConstraint(
                name: "CK_IndexPublicationCandidates_Purpose",
                table: "IndexPublicationCandidates",
                sql: "[BuildPurpose] BETWEEN 0 AND 2 AND [ExpectedIndexFormat] IN (1, 2) AND ([BuildPurpose] <> 0 OR [ExpectedIndexFormat] = 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_IndexPublicationCandidates_Purpose",
                table: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "BuildPurpose",
                table: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "ExpectedIndexFormat",
                table: "IndexPublicationCandidates");
        }
    }
}
