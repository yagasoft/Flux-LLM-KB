using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FenceIncrementalRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NamespaceComplete",
                table: "SourceRootScanStates",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "NamespaceGeneration",
                table: "SourceRootScanStates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RecoveryGeneration",
                table: "SourceRootScanStates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "RecoveryInProgress",
                table: "SourceRootScanStates",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RecoveryRequired",
                table: "SourceRootScanStates",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NamespaceComplete",
                table: "SourceRootScanStates");

            migrationBuilder.DropColumn(
                name: "NamespaceGeneration",
                table: "SourceRootScanStates");

            migrationBuilder.DropColumn(
                name: "RecoveryGeneration",
                table: "SourceRootScanStates");

            migrationBuilder.DropColumn(
                name: "RecoveryInProgress",
                table: "SourceRootScanStates");

            migrationBuilder.DropColumn(
                name: "RecoveryRequired",
                table: "SourceRootScanStates");
        }
    }
}
