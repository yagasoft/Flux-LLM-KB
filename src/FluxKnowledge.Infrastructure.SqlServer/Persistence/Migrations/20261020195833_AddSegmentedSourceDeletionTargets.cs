using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentedSourceDeletionTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceDeletionCleanupItems_StorageKind",
                table: "SourceDeletionCleanupItems");

            migrationBuilder.AddColumn<Guid>(
                name: "RetiredDraftGenerationId",
                table: "IndexPublicationCandidates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RetiredOwnerJobId",
                table: "IndexPublicationCandidates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceDeletionCleanupItems_StorageKind",
                table: "SourceDeletionCleanupItems",
                sql: "[StorageKind] IN (1, 2, 3, 4)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceDeletionCleanupItems_StorageKind",
                table: "SourceDeletionCleanupItems");

            migrationBuilder.DropColumn(
                name: "RetiredDraftGenerationId",
                table: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "RetiredOwnerJobId",
                table: "IndexPublicationCandidates");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceDeletionCleanupItems_StorageKind",
                table: "SourceDeletionCleanupItems",
                sql: "[StorageKind] IN (1, 2)");
        }
    }
}
