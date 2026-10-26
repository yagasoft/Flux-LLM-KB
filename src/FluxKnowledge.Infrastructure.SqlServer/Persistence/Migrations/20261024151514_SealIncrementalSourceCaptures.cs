using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SealIncrementalSourceCaptures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SealProfileId",
                table: "SourceRootScanStates",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastContentUsn",
                table: "SourcePendingChanges",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastUnjournaledGeneration",
                table: "SourcePendingChanges",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSealed",
                table: "SourceInventoryEntries",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "LatestJournalReason",
                table: "SourceInventoryEntries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LatestJournalUsn",
                table: "SourceInventoryEntries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextForcedCheckAtUtc",
                table: "SourceInventoryEntries",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SealFailureCode",
                table: "SourceInventoryEntries",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SealProfileId",
                table: "SourceInventoryEntries",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceInventoryEntries_IsSealed_NextForcedCheckAtUtc_SourceRootId",
                table: "SourceInventoryEntries",
                columns: new[] { "IsSealed", "NextForcedCheckAtUtc", "SourceRootId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceInventoryEntries_IsSealed_NextForcedCheckAtUtc_SourceRootId",
                table: "SourceInventoryEntries");

            migrationBuilder.DropColumn(
                name: "SealProfileId",
                table: "SourceRootScanStates");

            migrationBuilder.DropColumn(
                name: "LastContentUsn",
                table: "SourcePendingChanges");

            migrationBuilder.DropColumn(
                name: "LastUnjournaledGeneration",
                table: "SourcePendingChanges");

            migrationBuilder.DropColumn(
                name: "IsSealed",
                table: "SourceInventoryEntries");

            migrationBuilder.DropColumn(
                name: "LatestJournalReason",
                table: "SourceInventoryEntries");

            migrationBuilder.DropColumn(
                name: "LatestJournalUsn",
                table: "SourceInventoryEntries");

            migrationBuilder.DropColumn(
                name: "NextForcedCheckAtUtc",
                table: "SourceInventoryEntries");

            migrationBuilder.DropColumn(
                name: "SealFailureCode",
                table: "SourceInventoryEntries");

            migrationBuilder.DropColumn(
                name: "SealProfileId",
                table: "SourceInventoryEntries");
        }
    }
}
