using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGpuSchedulerOpaqueKeyCanonicality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_Jobs_LeaseOwner_NoTrailingWhitespace",
                table: "Jobs",
                sql: "[LeaseOwner] IS NULL OR (DATALENGTH([LeaseOwner]) > 0 AND UNICODE(RIGHT([LeaseOwner], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Jobs_Operation_NoTrailingWhitespace",
                table: "Jobs",
                sql: "DATALENGTH([Operation]) > 0 AND UNICODE(RIGHT([Operation], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuSchedulerOperationReceipts_CapacitySlotKey_NoTrailingWhitespace",
                table: "GpuSchedulerOperationReceipts",
                sql: "[CapacitySlotKey] IS NULL OR (DATALENGTH([CapacitySlotKey]) > 0 AND UNICODE(RIGHT([CapacitySlotKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuSchedulerOperationReceipts_OperationKind_NoTrailingWhitespace",
                table: "GpuSchedulerOperationReceipts",
                sql: "DATALENGTH([OperationKind]) > 0 AND UNICODE(RIGHT([OperationKind], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuSchedulerOperationReceipts_OwnerKey_NoTrailingWhitespace",
                table: "GpuSchedulerOperationReceipts",
                sql: "[OwnerKey] IS NULL OR (DATALENGTH([OwnerKey]) > 0 AND UNICODE(RIGHT([OwnerKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuSchedulerOperationReceipts_RequestFingerprint_NoTrailingWhitespace",
                table: "GpuSchedulerOperationReceipts",
                sql: "[RequestFingerprint] IS NULL OR (DATALENGTH([RequestFingerprint]) > 0 AND UNICODE(RIGHT([RequestFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuMiniTasks_HandoffLeaseOwner_NoTrailingWhitespace",
                table: "GpuMiniTasks",
                sql: "[HandoffLeaseOwner] IS NULL OR (DATALENGTH([HandoffLeaseOwner]) > 0 AND UNICODE(RIGHT([HandoffLeaseOwner], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuMiniTasks_IdempotencyKey_NoTrailingWhitespace",
                table: "GpuMiniTasks",
                sql: "DATALENGTH([IdempotencyKey]) > 0 AND UNICODE(RIGHT([IdempotencyKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuMiniTasks_ModelRuntimeKey_NoTrailingWhitespace",
                table: "GpuMiniTasks",
                sql: "DATALENGTH([ModelRuntimeKey]) > 0 AND UNICODE(RIGHT([ModelRuntimeKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuMiniTasks_SettingsFingerprint_NoTrailingWhitespace",
                table: "GpuMiniTasks",
                sql: "DATALENGTH([SettingsFingerprint]) > 0 AND UNICODE(RIGHT([SettingsFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuCapacitySlots_OwnerKey_NoTrailingWhitespace",
                table: "GpuCapacitySlots",
                sql: "[OwnerKey] IS NULL OR (DATALENGTH([OwnerKey]) > 0 AND UNICODE(RIGHT([OwnerKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuCapacitySlots_SlotKey_NoTrailingWhitespace",
                table: "GpuCapacitySlots",
                sql: "DATALENGTH([SlotKey]) > 0 AND UNICODE(RIGHT([SlotKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuBatches_CapacitySlotKey_NoTrailingWhitespace",
                table: "GpuBatches",
                sql: "DATALENGTH([CapacitySlotKey]) > 0 AND UNICODE(RIGHT([CapacitySlotKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuBatches_ModelRuntimeKey_NoTrailingWhitespace",
                table: "GpuBatches",
                sql: "DATALENGTH([ModelRuntimeKey]) > 0 AND UNICODE(RIGHT([ModelRuntimeKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuBatches_OwnerKey_NoTrailingWhitespace",
                table: "GpuBatches",
                sql: "DATALENGTH([OwnerKey]) > 0 AND UNICODE(RIGHT([OwnerKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuBatches_SettingsFingerprint_NoTrailingWhitespace",
                table: "GpuBatches",
                sql: "DATALENGTH([SettingsFingerprint]) > 0 AND UNICODE(RIGHT([SettingsFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Jobs_LeaseOwner_NoTrailingWhitespace",
                table: "Jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Jobs_Operation_NoTrailingWhitespace",
                table: "Jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuSchedulerOperationReceipts_CapacitySlotKey_NoTrailingWhitespace",
                table: "GpuSchedulerOperationReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuSchedulerOperationReceipts_OperationKind_NoTrailingWhitespace",
                table: "GpuSchedulerOperationReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuSchedulerOperationReceipts_OwnerKey_NoTrailingWhitespace",
                table: "GpuSchedulerOperationReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuSchedulerOperationReceipts_RequestFingerprint_NoTrailingWhitespace",
                table: "GpuSchedulerOperationReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuMiniTasks_HandoffLeaseOwner_NoTrailingWhitespace",
                table: "GpuMiniTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuMiniTasks_IdempotencyKey_NoTrailingWhitespace",
                table: "GpuMiniTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuMiniTasks_ModelRuntimeKey_NoTrailingWhitespace",
                table: "GpuMiniTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuMiniTasks_SettingsFingerprint_NoTrailingWhitespace",
                table: "GpuMiniTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuCapacitySlots_OwnerKey_NoTrailingWhitespace",
                table: "GpuCapacitySlots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuCapacitySlots_SlotKey_NoTrailingWhitespace",
                table: "GpuCapacitySlots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuBatches_CapacitySlotKey_NoTrailingWhitespace",
                table: "GpuBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuBatches_ModelRuntimeKey_NoTrailingWhitespace",
                table: "GpuBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuBatches_OwnerKey_NoTrailingWhitespace",
                table: "GpuBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuBatches_SettingsFingerprint_NoTrailingWhitespace",
                table: "GpuBatches");
        }
    }
}
