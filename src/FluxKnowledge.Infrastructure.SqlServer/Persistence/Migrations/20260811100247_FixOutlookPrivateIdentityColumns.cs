using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixOutlookPrivateIdentityColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OutlookCaptureFolders_ProfileId_StoreId_FolderEntryId' AND object_id = OBJECT_ID(N'[OutlookCaptureFolders]')) DROP INDEX [IX_OutlookCaptureFolders_ProfileId_StoreId_FolderEntryId] ON [OutlookCaptureFolders];");
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OutlookCaptureExports_FolderId_EntryId' AND object_id = OBJECT_ID(N'[OutlookCaptureExports]')) DROP INDEX [IX_OutlookCaptureExports_FolderId_EntryId] ON [OutlookCaptureExports];");

            migrationBuilder.AlterColumn<string>(
                name: "StoreId",
                table: "OutlookCaptureFolders",
                type: "nvarchar(max)",
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(4096)",
                oldMaxLength: 4096,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "FolderEntryId",
                table: "OutlookCaptureFolders",
                type: "nvarchar(max)",
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(4096)",
                oldMaxLength: 4096,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "EntryId",
                table: "OutlookCaptureExports",
                type: "nvarchar(max)",
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(4096)",
                oldMaxLength: 4096,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureFolders_ProfileId",
                table: "OutlookCaptureFolders",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureExports_FolderId",
                table: "OutlookCaptureExports",
                column: "FolderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureFolders_ProfileId",
                table: "OutlookCaptureFolders");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureExports_FolderId",
                table: "OutlookCaptureExports");

            migrationBuilder.AlterColumn<string>(
                name: "StoreId",
                table: "OutlookCaptureFolders",
                type: "nvarchar(4096)",
                maxLength: 4096,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "FolderEntryId",
                table: "OutlookCaptureFolders",
                type: "nvarchar(4096)",
                maxLength: 4096,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "EntryId",
                table: "OutlookCaptureExports",
                type: "nvarchar(4096)",
                maxLength: 4096,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureFolders_ProfileId_StoreId_FolderEntryId",
                table: "OutlookCaptureFolders",
                columns: new[] { "ProfileId", "StoreId", "FolderEntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureExports_FolderId_EntryId",
                table: "OutlookCaptureExports",
                columns: new[] { "FolderId", "EntryId" },
                unique: true);
        }
    }
}
