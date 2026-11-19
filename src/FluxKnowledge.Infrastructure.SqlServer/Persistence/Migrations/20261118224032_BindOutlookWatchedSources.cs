using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindOutlookWatchedSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureExports_ProfileId",
                table: "OutlookCaptureExports");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WatchedPublicationSuspendedAtUtc",
                table: "OutlookCaptureProfiles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WatchedRootIdentity",
                table: "OutlookCaptureProfiles",
                type: "varchar(128)",
                unicode: false,
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WatchedRootPath",
                table: "OutlookCaptureProfiles",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<Guid>(
                name: "WatchedSourceRootId",
                table: "OutlookCaptureProfiles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WatchedStagingIdentity",
                table: "OutlookCaptureProfiles",
                type: "varchar(128)",
                unicode: false,
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WatchedStagingPath",
                table: "OutlookCaptureProfiles",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "WatchedPreparedBundleJson",
                table: "OutlookCaptureExports",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WatchedPublishedAtUtc",
                table: "OutlookCaptureExports",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WatchedRetryAfterUtc",
                table: "OutlookCaptureExports",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WatchedWithdrawalAtUtc",
                table: "OutlookCaptureExports",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WatchedWithdrawalRevisionIdsJson",
                table: "OutlookCaptureExports",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureProfiles_WatchedSourceRootId",
                table: "OutlookCaptureProfiles",
                column: "WatchedSourceRootId",
                unique: true,
                filter: "[WatchedSourceRootId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureExports_ProfileId_WatchedPublishedAtUtc",
                table: "OutlookCaptureExports",
                columns: new[] { "ProfileId", "WatchedPublishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureExports_ProfileId_WatchedRetryAfterUtc_Id",
                table: "OutlookCaptureExports",
                columns: new[] { "ProfileId", "WatchedRetryAfterUtc", "Id" },
                filter: "[WatchedPublishedAtUtc] IS NULL AND [State] = 2");

            migrationBuilder.AddForeignKey(
                name: "FK_OutlookCaptureProfiles_SourceRootConfigurations_WatchedSourceRootId",
                table: "OutlookCaptureProfiles",
                column: "WatchedSourceRootId",
                principalTable: "SourceRootConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [OutlookCaptureProfiles] WHERE [WatchedSourceRootId] IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM [OutlookCaptureExports] WHERE [WatchedWithdrawalAtUtc] IS NOT NULL)
                    THROW 51000, 'The Outlook watched-source schema contains retained cutover evidence.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_OutlookCaptureProfiles_SourceRootConfigurations_WatchedSourceRootId",
                table: "OutlookCaptureProfiles");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureProfiles_WatchedSourceRootId",
                table: "OutlookCaptureProfiles");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureExports_ProfileId_WatchedPublishedAtUtc",
                table: "OutlookCaptureExports");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureExports_ProfileId_WatchedRetryAfterUtc_Id",
                table: "OutlookCaptureExports");

            migrationBuilder.DropColumn(
                name: "WatchedPublicationSuspendedAtUtc",
                table: "OutlookCaptureProfiles");

            migrationBuilder.DropColumn(
                name: "WatchedRootIdentity",
                table: "OutlookCaptureProfiles");

            migrationBuilder.DropColumn(
                name: "WatchedRootPath",
                table: "OutlookCaptureProfiles");

            migrationBuilder.DropColumn(
                name: "WatchedSourceRootId",
                table: "OutlookCaptureProfiles");

            migrationBuilder.DropColumn(
                name: "WatchedStagingIdentity",
                table: "OutlookCaptureProfiles");

            migrationBuilder.DropColumn(
                name: "WatchedStagingPath",
                table: "OutlookCaptureProfiles");

            migrationBuilder.DropColumn(
                name: "WatchedPreparedBundleJson",
                table: "OutlookCaptureExports");

            migrationBuilder.DropColumn(
                name: "WatchedPublishedAtUtc",
                table: "OutlookCaptureExports");

            migrationBuilder.DropColumn(
                name: "WatchedRetryAfterUtc",
                table: "OutlookCaptureExports");

            migrationBuilder.DropColumn(
                name: "WatchedWithdrawalAtUtc",
                table: "OutlookCaptureExports");

            migrationBuilder.DropColumn(
                name: "WatchedWithdrawalRevisionIdsJson",
                table: "OutlookCaptureExports");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureExports_ProfileId",
                table: "OutlookCaptureExports",
                column: "ProfileId");
        }
    }
}
