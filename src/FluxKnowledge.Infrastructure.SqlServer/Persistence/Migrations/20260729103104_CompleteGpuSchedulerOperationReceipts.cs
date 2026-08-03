using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteGpuSchedulerOperationReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AdmissionDisposition",
                table: "GpuSchedulerOperationReceipts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeferredUntilUtc",
                table: "GpuSchedulerOperationReceipts",
                type: "datetimeoffset(7)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextDeferredAtUtc",
                table: "GpuSchedulerOperationReceipts",
                type: "datetimeoffset(7)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WakeGeneration",
                table: "GpuSchedulerOperationReceipts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WakeConsumptionOperationId",
                table: "GpuSchedulerOperationReceipts",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdmissionDisposition",
                table: "GpuSchedulerOperationReceipts");

            migrationBuilder.DropColumn(
                name: "DeferredUntilUtc",
                table: "GpuSchedulerOperationReceipts");

            migrationBuilder.DropColumn(
                name: "NextDeferredAtUtc",
                table: "GpuSchedulerOperationReceipts");

            migrationBuilder.DropColumn(
                name: "WakeGeneration",
                table: "GpuSchedulerOperationReceipts");

            migrationBuilder.DropColumn(
                name: "WakeConsumptionOperationId",
                table: "GpuSchedulerOperationReceipts");
        }
    }
}
