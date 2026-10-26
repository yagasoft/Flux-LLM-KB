using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIncrementalScanManifests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IncrementalManifestJson",
                table: "SourceScanRequests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeferredEvidenceJson",
                table: "SourceRootScanStates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitInventoryGeneration",
                table: "SourceRootScanStates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastStatisticsJson",
                table: "SourceRootScanStates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotBeforeUtc",
                table: "SourcePendingChanges",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncrementalManifestJson",
                table: "SourceScanRequests");

            migrationBuilder.DropColumn(
                name: "DeferredEvidenceJson",
                table: "SourceRootScanStates");

            migrationBuilder.DropColumn(
                name: "GitInventoryGeneration",
                table: "SourceRootScanStates");

            migrationBuilder.DropColumn(
                name: "LastStatisticsJson",
                table: "SourceRootScanStates");

            migrationBuilder.DropColumn(
                name: "NotBeforeUtc",
                table: "SourcePendingChanges");
        }
    }
}
