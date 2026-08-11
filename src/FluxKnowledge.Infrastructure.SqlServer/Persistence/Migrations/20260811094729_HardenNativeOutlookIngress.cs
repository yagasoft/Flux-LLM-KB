using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenNativeOutlookIngress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutlookCatchUps_ProfileId_CoalescingKey_State",
                table: "OutlookCatchUps");

            migrationBuilder.AddColumn<Guid>(
                name: "ProfileId",
                table: "OutlookBrowseRequests",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "DeferredCapabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    RequiredCapability = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Provenance = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ClaimedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    ClaimedProcessorVersion = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeferredCapabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeferredCapabilities_SourceRevisions_SourceRevisionId",
                        column: x => x.SourceRevisionId,
                        principalTable: "SourceRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCatchUps_ProfileId_CoalescingKey_State",
                table: "OutlookCatchUps",
                columns: new[] { "ProfileId", "CoalescingKey", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureExports_ProfileId",
                table: "OutlookCaptureExports",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookBrowseResults_FolderId",
                table: "OutlookBrowseResults",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookBrowseRequests_ProfileId",
                table: "OutlookBrowseRequests",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_DeferredCapabilities_SourceRevisionId_ArtifactFingerprint_RequiredCapability",
                table: "DeferredCapabilities",
                columns: new[] { "SourceRevisionId", "ArtifactFingerprint", "RequiredCapability" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_OutlookBrowseRequests_OutlookCaptureProfiles_ProfileId",
                table: "OutlookBrowseRequests",
                column: "ProfileId",
                principalTable: "OutlookCaptureProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OutlookBrowseResults_OutlookBrowseRequests_BrowseRequestId",
                table: "OutlookBrowseResults",
                column: "BrowseRequestId",
                principalTable: "OutlookBrowseRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OutlookBrowseResults_OutlookCaptureFolders_FolderId",
                table: "OutlookBrowseResults",
                column: "FolderId",
                principalTable: "OutlookCaptureFolders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OutlookCaptureExports_OutlookCaptureFolders_FolderId",
                table: "OutlookCaptureExports",
                column: "FolderId",
                principalTable: "OutlookCaptureFolders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OutlookCaptureExports_OutlookCaptureProfiles_ProfileId",
                table: "OutlookCaptureExports",
                column: "ProfileId",
                principalTable: "OutlookCaptureProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OutlookCaptureFolders_OutlookCaptureProfiles_ProfileId",
                table: "OutlookCaptureFolders",
                column: "ProfileId",
                principalTable: "OutlookCaptureProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OutlookCatchUps_OutlookCaptureProfiles_ProfileId",
                table: "OutlookCatchUps",
                column: "ProfileId",
                principalTable: "OutlookCaptureProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OutlookBrowseRequests_OutlookCaptureProfiles_ProfileId",
                table: "OutlookBrowseRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_OutlookBrowseResults_OutlookBrowseRequests_BrowseRequestId",
                table: "OutlookBrowseResults");

            migrationBuilder.DropForeignKey(
                name: "FK_OutlookBrowseResults_OutlookCaptureFolders_FolderId",
                table: "OutlookBrowseResults");

            migrationBuilder.DropForeignKey(
                name: "FK_OutlookCaptureExports_OutlookCaptureFolders_FolderId",
                table: "OutlookCaptureExports");

            migrationBuilder.DropForeignKey(
                name: "FK_OutlookCaptureExports_OutlookCaptureProfiles_ProfileId",
                table: "OutlookCaptureExports");

            migrationBuilder.DropForeignKey(
                name: "FK_OutlookCaptureFolders_OutlookCaptureProfiles_ProfileId",
                table: "OutlookCaptureFolders");

            migrationBuilder.DropForeignKey(
                name: "FK_OutlookCatchUps_OutlookCaptureProfiles_ProfileId",
                table: "OutlookCatchUps");

            migrationBuilder.DropTable(
                name: "DeferredCapabilities");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCatchUps_ProfileId_CoalescingKey_State",
                table: "OutlookCatchUps");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureExports_ProfileId",
                table: "OutlookCaptureExports");

            migrationBuilder.DropIndex(
                name: "IX_OutlookBrowseResults_FolderId",
                table: "OutlookBrowseResults");

            migrationBuilder.DropIndex(
                name: "IX_OutlookBrowseRequests_ProfileId",
                table: "OutlookBrowseRequests");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "OutlookBrowseRequests");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCatchUps_ProfileId_CoalescingKey_State",
                table: "OutlookCatchUps",
                columns: new[] { "ProfileId", "CoalescingKey", "State" },
                unique: true);
        }
    }
}
