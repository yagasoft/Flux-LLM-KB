using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindInteractiveGpuOwnerProcess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [dbo].[GpuMiniTasks] WHERE [InteractiveExecutorInstanceId] IS NOT NULL)
                    THROW 51000, 'interactive-process-binding-requires-drained-projection', 1;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuMiniTasks_ExclusiveOwner",
                table: "GpuMiniTasks");

            migrationBuilder.AddColumn<string>(
                name: "InteractiveOwnerMachineFingerprint",
                table: "GpuMiniTasks",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<int>(
                name: "InteractiveOwnerProcessId",
                table: "GpuMiniTasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InteractiveOwnerStartedAtUtc",
                table: "GpuMiniTasks",
                type: "datetimeoffset(7)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuMiniTasks_ExclusiveOwner",
                table: "GpuMiniTasks",
                sql: "([ParentJobId] IS NOT NULL AND [SourceRevision] > 0 AND [InteractiveExecutorInstanceId] IS NULL AND [RequiredExecutorKey] IS NULL AND [QueueDeadlineUtc] IS NULL AND [ExecutionDeadlineUtc] IS NULL AND [InteractiveCancellationRequested] = 0 AND [InteractiveOwnerProcessId] IS NULL AND [InteractiveOwnerStartedAtUtc] IS NULL AND [InteractiveOwnerMachineFingerprint] IS NULL) OR ([ParentJobId] IS NULL AND [SourceRevision] = 0 AND [InteractiveExecutorInstanceId] IS NOT NULL AND [InteractiveExecutorInstanceId] <> '00000000-0000-0000-0000-000000000000' AND [RequiredExecutorKey] IS NOT NULL AND LEN([RequiredExecutorKey]) > 0 AND [QueueDeadlineUtc] IS NOT NULL AND [ExecutionDeadlineUtc] IS NOT NULL AND [ExecutionDeadlineUtc] > [QueueDeadlineUtc] AND [PriorityLane] = 0 AND [HandoffLeaseOwner] IS NULL AND [InteractiveOwnerProcessId] IS NOT NULL AND [InteractiveOwnerProcessId] > 0 AND [InteractiveOwnerStartedAtUtc] IS NOT NULL AND [InteractiveOwnerStartedAtUtc] > '0001-01-01T00:00:00+00:00' AND DATEPART(TZOFFSET, [InteractiveOwnerStartedAtUtc]) = 0 AND [InteractiveOwnerMachineFingerprint] IS NOT NULL AND DATALENGTH([InteractiveOwnerMachineFingerprint]) = 128 AND [InteractiveOwnerMachineFingerprint] NOT LIKE '%[^0-9a-f]%' COLLATE Latin1_General_100_BIN2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [dbo].[GpuMiniTasks] WHERE [InteractiveExecutorInstanceId] IS NOT NULL)
                    THROW 51000, 'interactive-process-binding-downgrade-requires-drained-projection', 1;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuMiniTasks_ExclusiveOwner",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "InteractiveOwnerMachineFingerprint",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "InteractiveOwnerProcessId",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "InteractiveOwnerStartedAtUtc",
                table: "GpuMiniTasks");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuMiniTasks_ExclusiveOwner",
                table: "GpuMiniTasks",
                sql: "([ParentJobId] IS NOT NULL AND [SourceRevision] > 0 AND [InteractiveExecutorInstanceId] IS NULL AND [RequiredExecutorKey] IS NULL AND [QueueDeadlineUtc] IS NULL AND [ExecutionDeadlineUtc] IS NULL AND [InteractiveCancellationRequested] = 0) OR ([ParentJobId] IS NULL AND [SourceRevision] = 0 AND [InteractiveExecutorInstanceId] IS NOT NULL AND [InteractiveExecutorInstanceId] <> '00000000-0000-0000-0000-000000000000' AND [RequiredExecutorKey] IS NOT NULL AND LEN([RequiredExecutorKey]) > 0 AND [QueueDeadlineUtc] IS NOT NULL AND [ExecutionDeadlineUtc] IS NOT NULL AND [ExecutionDeadlineUtc] > [QueueDeadlineUtc] AND [PriorityLane] = 0 AND [HandoffLeaseOwner] IS NULL)");
        }
    }
}
