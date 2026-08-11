using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordOutlookExportBlockedReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlockedReasonCode",
                table: "OutlookCaptureExports",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockedReasonCode",
                table: "OutlookCaptureExports");
        }
    }
}
