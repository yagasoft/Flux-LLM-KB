using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGpuSchedulerOperationReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GpuSchedulerOperationReceipts",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CapacitySlotKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OwnerKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AdmissionGeneration = table.Column<long>(type: "bigint", nullable: true),
                    Accepted = table.Column<bool>(type: "bit", nullable: false),
                    Committed = table.Column<bool>(type: "bit", nullable: false),
                    WakeReasons = table.Column<int>(type: "int", nullable: false),
                    EffectiveAdmissionReasons = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuSchedulerOperationReceipts", x => x.OperationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GpuSchedulerOperationReceipts_OperationKind_BatchId_CapacitySlotKey_AdmissionGeneration",
                table: "GpuSchedulerOperationReceipts",
                columns: new[] { "OperationKind", "BatchId", "CapacitySlotKey", "AdmissionGeneration" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GpuSchedulerOperationReceipts");
        }
    }
}
