using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenOutlookCaptureReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureExports_FolderId_EntryIdFingerprint",
                table: "OutlookCaptureExports");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureFolders_ProfileId_CanonicalIdentityFingerprint",
                table: "OutlookCaptureFolders");

            migrationBuilder.AlterColumn<string>(
                name: "CanonicalIdentityFingerprint",
                table: "OutlookCaptureFolders",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                computedColumnSql: "CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT(CONVERT(nvarchar(20), DATALENGTH([StoreId])), N':', [StoreId], CONVERT(nvarchar(20), DATALENGTH([FolderEntryId])), N':', [FolderEntryId])), 2)",
                stored: true,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "char(64)",
                oldUnicode: false,
                oldFixedLength: true,
                oldMaxLength: 64,
                oldComputedColumnSql: "CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT([StoreId], N'|', [FolderEntryId])), 2)",
                oldStored: true,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureFolders_ProfileId_CanonicalIdentityFingerprint",
                table: "OutlookCaptureFolders",
                columns: new[] { "ProfileId", "CanonicalIdentityFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureExports_FolderId_EntryIdFingerprint",
                table: "OutlookCaptureExports",
                columns: new[] { "FolderId", "EntryIdFingerprint" },
                unique: true,
                filter: "[State] <> 4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureExports_FolderId_EntryIdFingerprint",
                table: "OutlookCaptureExports");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureFolders_ProfileId_CanonicalIdentityFingerprint",
                table: "OutlookCaptureFolders");

            migrationBuilder.AlterColumn<string>(
                name: "CanonicalIdentityFingerprint",
                table: "OutlookCaptureFolders",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                computedColumnSql: "CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT([StoreId], N'|', [FolderEntryId])), 2)",
                stored: true,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "char(64)",
                oldUnicode: false,
                oldFixedLength: true,
                oldMaxLength: 64,
                oldComputedColumnSql: "CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT(CONVERT(nvarchar(20), DATALENGTH([StoreId])), N':', [StoreId], CONVERT(nvarchar(20), DATALENGTH([FolderEntryId])), N':', [FolderEntryId])), 2)",
                oldStored: true,
                oldCollation: "Latin1_General_100_BIN2");

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
    }
}
