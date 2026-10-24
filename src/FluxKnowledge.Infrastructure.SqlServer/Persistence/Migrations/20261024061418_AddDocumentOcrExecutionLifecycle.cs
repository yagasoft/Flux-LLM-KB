using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentOcrExecutionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CleanupProofKind",
                table: "DocumentOcrRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExecutionAdmissionGeneration",
                table: "DocumentOcrRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionBatchId",
                table: "DocumentOcrRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExecutionClaimedAtUtc",
                table: "DocumentOcrRequests",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExecutionDeadlineAtUtc",
                table: "DocumentOcrRequests",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionDispatchId",
                table: "DocumentOcrRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionInstanceId",
                table: "DocumentOcrRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionMachineFingerprint",
                table: "DocumentOcrRequests",
                type: "char(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionPhase",
                table: "DocumentOcrRequests",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NativeCleanupConfirmedAtUtc",
                table: "DocumentOcrRequests",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NativeProcessId",
                table: "DocumentOcrRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NativeProcessStartedAtUtc",
                table: "DocumentOcrRequests",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhysicalCleanupEvidenceSha256",
                table: "DocumentOcrRequests",
                type: "char(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryReason",
                table: "DocumentOcrRequests",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecoveryStartedAtUtc",
                table: "DocumentOcrRequests",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecoveryUpdatedAtUtc",
                table: "DocumentOcrRequests",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupervisorInstanceId",
                table: "DocumentOcrRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SupervisorProcessId",
                table: "DocumentOcrRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupervisorProcessStartedAtUtc",
                table: "DocumentOcrRequests",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentOcrRequests_CleanupKind",
                table: "DocumentOcrRequests",
                sql: "[CleanupProofKind] IS NULL OR [CleanupProofKind] BETWEEN 0 AND 2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentOcrRequests_CleanupProof",
                table: "DocumentOcrRequests",
                sql: "([NativeCleanupConfirmedAtUtc] IS NULL AND [CleanupProofKind] IS NULL) OR ([NativeCleanupConfirmedAtUtc] IS NOT NULL AND [CleanupProofKind] IS NOT NULL AND [ExecutionInstanceId] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentOcrRequests_ExecutionIdentity",
                table: "DocumentOcrRequests",
                sql: "[ExecutionInstanceId] IS NULL OR ([ExecutionDispatchId] IS NOT NULL AND [ExecutionBatchId] IS NOT NULL AND [ExecutionAdmissionGeneration] IS NOT NULL AND [ExecutionAdmissionGeneration] > 0 AND [SupervisorInstanceId] IS NOT NULL AND [SupervisorProcessId] IS NOT NULL AND [SupervisorProcessId] > 0 AND [SupervisorProcessStartedAtUtc] IS NOT NULL AND [ExecutionMachineFingerprint] IS NOT NULL AND [ExecutionClaimedAtUtc] IS NOT NULL AND [ExecutionDeadlineAtUtc] IS NOT NULL AND [ExecutionDeadlineAtUtc] > [ExecutionClaimedAtUtc])");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentOcrRequests_ExecutionPhase",
                table: "DocumentOcrRequests",
                sql: "[ExecutionPhase] BETWEEN 0 AND 5");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentOcrRequests_NativeIdentity",
                table: "DocumentOcrRequests",
                sql: "([NativeProcessId] IS NULL AND [NativeProcessStartedAtUtc] IS NULL) OR ([NativeProcessId] IS NOT NULL AND [NativeProcessId] > 0 AND [NativeProcessStartedAtUtc] IS NOT NULL AND [ExecutionInstanceId] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentOcrRequests_CleanupKind",
                table: "DocumentOcrRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentOcrRequests_CleanupProof",
                table: "DocumentOcrRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentOcrRequests_ExecutionIdentity",
                table: "DocumentOcrRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentOcrRequests_ExecutionPhase",
                table: "DocumentOcrRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentOcrRequests_NativeIdentity",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "CleanupProofKind",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "ExecutionAdmissionGeneration",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "ExecutionBatchId",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "ExecutionClaimedAtUtc",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "ExecutionDeadlineAtUtc",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "ExecutionDispatchId",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "ExecutionInstanceId",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "ExecutionMachineFingerprint",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "ExecutionPhase",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "NativeCleanupConfirmedAtUtc",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "NativeProcessId",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "NativeProcessStartedAtUtc",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "PhysicalCleanupEvidenceSha256",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "RecoveryReason",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "RecoveryStartedAtUtc",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "RecoveryUpdatedAtUtc",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "SupervisorInstanceId",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "SupervisorProcessId",
                table: "DocumentOcrRequests");

            migrationBuilder.DropColumn(
                name: "SupervisorProcessStartedAtUtc",
                table: "DocumentOcrRequests");
        }
    }
}
