using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleRecursiveOutlookCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ConfigurationRevision",
                table: "OutlookCatchUps",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IncludeSubfolders",
                table: "OutlookCaptureProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextScheduledScanAtUtc",
                table: "OutlookCaptureProfiles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDiscoveredSubfolder",
                table: "OutlookCaptureFolders",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE dbo.OutlookCaptureFolders
                SET State = 0, CursorUtc = NULL, CursorFingerprint = NULL
                WHERE IsDiscoveredSubfolder = 1;
                """);

            migrationBuilder.DropColumn(
                name: "ConfigurationRevision",
                table: "OutlookCatchUps");

            migrationBuilder.DropColumn(
                name: "IncludeSubfolders",
                table: "OutlookCaptureProfiles");

            migrationBuilder.DropColumn(
                name: "NextScheduledScanAtUtc",
                table: "OutlookCaptureProfiles");

            migrationBuilder.DropColumn(
                name: "IsDiscoveredSubfolder",
                table: "OutlookCaptureFolders");
        }
    }
}
