using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindCodexNotesToWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CodexWorkspaceRootId",
                table: "KnowledgeItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeItems_CodexWorkspaceRootId_CreatedAtUtc_Id",
                table: "KnowledgeItems",
                columns: new[] { "CodexWorkspaceRootId", "CreatedAtUtc", "Id" },
                descending: new[] { false, true, false },
                filter: "[CodexWorkspaceRootId] IS NOT NULL AND [ForgottenAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_KnowledgeItems_CodexWorkspaceRootId_CreatedAtUtc_Id",
                table: "KnowledgeItems");

            migrationBuilder.DropColumn(
                name: "CodexWorkspaceRootId",
                table: "KnowledgeItems");
        }
    }
}
