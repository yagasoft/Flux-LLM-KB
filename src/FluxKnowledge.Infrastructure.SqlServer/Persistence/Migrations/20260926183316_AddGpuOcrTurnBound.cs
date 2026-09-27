using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGpuOcrTurnBound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SearchBatchesWhileOcrWaiting",
                table: "GpuSchedulerState",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuSchedulerState_SearchTurnBound",
                table: "GpuSchedulerState",
                sql: "[SearchBatchesWhileOcrWaiting] BETWEEN 0 AND 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuSchedulerState_SearchTurnBound",
                table: "GpuSchedulerState");

            migrationBuilder.DropColumn(
                name: "SearchBatchesWhileOcrWaiting",
                table: "GpuSchedulerState");
        }
    }
}
