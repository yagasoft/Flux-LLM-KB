using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceOutlookCaptureIdentityFences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutlookCatchUps_ProfileId_CoalescingKey_State",
                table: "OutlookCatchUps");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureFolders_ProfileId",
                table: "OutlookCaptureFolders");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureExports_FolderId",
                table: "OutlookCaptureExports");

            migrationBuilder.AddColumn<string>(
                name: "CanonicalIdentityFingerprint",
                table: "OutlookCaptureFolders",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                computedColumnSql: "CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT([StoreId], N'|', [FolderEntryId])), 2)",
                stored: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "EntryIdFingerprint",
                table: "OutlookCaptureExports",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                computedColumnSql: "CONVERT(char(64), HASHBYTES('SHA2_256', [EntryId]), 2)",
                stored: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCatchUps_ProfileId_CoalescingKey",
                table: "OutlookCatchUps",
                columns: new[] { "ProfileId", "CoalescingKey" },
                unique: true,
                filter: "[State] IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureFolders_ProfileId_CanonicalIdentityFingerprint",
                table: "OutlookCaptureFolders",
                columns: new[] { "ProfileId", "CanonicalIdentityFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureExports_FolderId_EntryIdFingerprint",
                table: "OutlookCaptureExports",
                columns: new[] { "FolderId", "EntryIdFingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutlookCatchUps_ProfileId_CoalescingKey",
                table: "OutlookCatchUps");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureFolders_ProfileId_CanonicalIdentityFingerprint",
                table: "OutlookCaptureFolders");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureExports_FolderId_EntryIdFingerprint",
                table: "OutlookCaptureExports");

            migrationBuilder.DropColumn(
                name: "CanonicalIdentityFingerprint",
                table: "OutlookCaptureFolders");

            migrationBuilder.DropColumn(
                name: "EntryIdFingerprint",
                table: "OutlookCaptureExports");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCatchUps_ProfileId_CoalescingKey_State",
                table: "OutlookCatchUps",
                columns: new[] { "ProfileId", "CoalescingKey", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureFolders_ProfileId",
                table: "OutlookCaptureFolders",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureExports_FolderId",
                table: "OutlookCaptureExports",
                column: "FolderId");
        }
    }
}
