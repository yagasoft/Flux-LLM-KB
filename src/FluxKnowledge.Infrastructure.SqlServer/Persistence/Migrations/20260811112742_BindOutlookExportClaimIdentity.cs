using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindOutlookExportClaimIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CatchUpId",
                table: "OutlookCaptureExports",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureExports_CatchUpId",
                table: "OutlookCaptureExports",
                column: "CatchUpId");

            migrationBuilder.AddForeignKey(
                name: "FK_OutlookCaptureExports_OutlookCatchUps_CatchUpId",
                table: "OutlookCaptureExports",
                column: "CatchUpId",
                principalTable: "OutlookCatchUps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OutlookCaptureExports_OutlookCatchUps_CatchUpId",
                table: "OutlookCaptureExports");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureExports_CatchUpId",
                table: "OutlookCaptureExports");

            migrationBuilder.DropColumn(
                name: "CatchUpId",
                table: "OutlookCaptureExports");
        }
    }
}
