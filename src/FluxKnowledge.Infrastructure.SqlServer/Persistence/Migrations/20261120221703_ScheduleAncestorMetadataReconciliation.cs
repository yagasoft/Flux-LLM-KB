using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleAncestorMetadataReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AncestorReconciliationDueAtUtc",
                table: "SourceRootScanStates",
                type: "datetimeoffset",
                nullable: true);

            // Previously accepted ancestor events cannot be replayed from the durable cursor.
            migrationBuilder.Sql("""
                DECLARE @now datetimeoffset = SYSUTCDATETIME();
                UPDATE scan
                SET [AncestorReconciliationDueAtUtc] = CASE
                    WHEN DATEADD(minute, 15, COALESCE(scan.[LastReconciliationAtUtc], root.[LastScanCompletedAtUtc])) > DATEADD(second, 30, @now)
                    THEN DATEADD(minute, 15, COALESCE(scan.[LastReconciliationAtUtc], root.[LastScanCompletedAtUtc]))
                    ELSE DATEADD(second, 30, @now) END
                FROM [SourceRootScanStates] AS scan
                JOIN [SourceRootConfigurations] AS root ON root.[Id] = scan.[SourceRootId]
                WHERE scan.[RecoveryRequired] = 1 AND scan.[Reason] = N'ancestor-authority-changed';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AncestorReconciliationDueAtUtc",
                table: "SourceRootScanStates");
        }
    }
}
