using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeSourceVerificationResolutionFair : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceVerificationItems_RunId_State_Id",
                table: "SourceVerificationItems");

            migrationBuilder.CreateIndex(
                name: "IX_SourceVerificationItems_RunId_State_AttemptedAtUtc_Id",
                table: "SourceVerificationItems",
                columns: new[] { "RunId", "State", "AttemptedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceVerificationItems_RunId_State_AttemptedAtUtc_Id",
                table: "SourceVerificationItems");

            migrationBuilder.CreateIndex(
                name: "IX_SourceVerificationItems_RunId_State_Id",
                table: "SourceVerificationItems",
                columns: new[] { "RunId", "State", "Id" });
        }
    }
}
