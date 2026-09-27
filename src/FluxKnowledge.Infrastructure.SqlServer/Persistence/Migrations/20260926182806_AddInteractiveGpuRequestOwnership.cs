using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInteractiveGpuRequestOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ParentJobId",
                table: "GpuMiniTasks",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExecutionDeadlineUtc",
                table: "GpuMiniTasks",
                type: "datetimeoffset(7)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InteractiveCancellationRequested",
                table: "GpuMiniTasks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "InteractiveExecutorInstanceId",
                table: "GpuMiniTasks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QueueDeadlineUtc",
                table: "GpuMiniTasks",
                type: "datetimeoffset(7)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredExecutorKey",
                table: "GpuMiniTasks",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuMiniTasks_ExclusiveOwner",
                table: "GpuMiniTasks",
                sql: "([ParentJobId] IS NOT NULL AND [SourceRevision] > 0 AND [InteractiveExecutorInstanceId] IS NULL AND [RequiredExecutorKey] IS NULL AND [QueueDeadlineUtc] IS NULL AND [ExecutionDeadlineUtc] IS NULL AND [InteractiveCancellationRequested] = 0) OR ([ParentJobId] IS NULL AND [SourceRevision] = 0 AND [InteractiveExecutorInstanceId] IS NOT NULL AND [InteractiveExecutorInstanceId] <> '00000000-0000-0000-0000-000000000000' AND [RequiredExecutorKey] IS NOT NULL AND LEN([RequiredExecutorKey]) > 0 AND [QueueDeadlineUtc] IS NOT NULL AND [ExecutionDeadlineUtc] IS NOT NULL AND [ExecutionDeadlineUtc] > [QueueDeadlineUtc] AND [PriorityLane] = 0 AND [HandoffLeaseOwner] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuMiniTasks_RequiredExecutorKey_NoTrailingWhitespace",
                table: "GpuMiniTasks",
                sql: "[RequiredExecutorKey] IS NULL OR (DATALENGTH([RequiredExecutorKey]) > 0 AND UNICODE(RIGHT([RequiredExecutorKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [dbo].[GpuMiniTasks] WHERE [ParentJobId] IS NULL)
                    THROW 51000, 'interactive-gpu-ownership-downgrade-requires-drained-projection', 1;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuMiniTasks_ExclusiveOwner",
                table: "GpuMiniTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuMiniTasks_RequiredExecutorKey_NoTrailingWhitespace",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "ExecutionDeadlineUtc",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "InteractiveCancellationRequested",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "InteractiveExecutorInstanceId",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "QueueDeadlineUtc",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "RequiredExecutorKey",
                table: "GpuMiniTasks");

            migrationBuilder.AlterColumn<Guid>(
                name: "ParentJobId",
                table: "GpuMiniTasks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
