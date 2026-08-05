using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGpuExecutorDispatchAndReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GpuExecutorDispatches",
                columns: table => new
                {
                    DispatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapacitySlotKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OwnerKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExecutorKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AdmissionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuExecutorDispatches", x => x.DispatchId);
                    table.CheckConstraint("CK_GpuExecutorDispatches_CapacitySlotKey_NoTrailingWhitespace", "DATALENGTH([CapacitySlotKey]) > 0 AND UNICODE(RIGHT([CapacitySlotKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_GpuExecutorDispatches_ExecutorKey_NoTrailingWhitespace", "DATALENGTH([ExecutorKey]) > 0 AND UNICODE(RIGHT([ExecutorKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_GpuExecutorDispatches_OwnerKey_NoTrailingWhitespace", "DATALENGTH([OwnerKey]) > 0 AND UNICODE(RIGHT([OwnerKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.ForeignKey(
                        name: "FK_GpuExecutorDispatches_GpuBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "GpuBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GpuExecutorDispatches_GpuCapacitySlots_CapacitySlotKey",
                        column: x => x.CapacitySlotKey,
                        principalTable: "GpuCapacitySlots",
                        principalColumn: "SlotKey",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GpuExecutorEvidence",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapacitySlotKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExecutorKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AdmissionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    EvidenceClass = table.Column<int>(type: "int", nullable: false),
                    VerifierKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuExecutorEvidence", x => x.OperationId);
                    table.CheckConstraint("CK_GpuExecutorEvidence_CapacitySlotKey_NoTrailingWhitespace", "DATALENGTH([CapacitySlotKey]) > 0 AND UNICODE(RIGHT([CapacitySlotKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_GpuExecutorEvidence_ExecutorKey_NoTrailingWhitespace", "DATALENGTH([ExecutorKey]) > 0 AND UNICODE(RIGHT([ExecutorKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_GpuExecutorEvidence_RequestFingerprint_NoTrailingWhitespace", "DATALENGTH([RequestFingerprint]) > 0 AND UNICODE(RIGHT([RequestFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_GpuExecutorEvidence_VerifierKey_NoTrailingWhitespace", "DATALENGTH([VerifierKey]) > 0 AND UNICODE(RIGHT([VerifierKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.ForeignKey(
                        name: "FK_GpuExecutorEvidence_GpuBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "GpuBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GpuExecutorEvidence_GpuCapacitySlots_CapacitySlotKey",
                        column: x => x.CapacitySlotKey,
                        principalTable: "GpuCapacitySlots",
                        principalColumn: "SlotKey",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GpuExecutorEvidence_GpuExecutorDispatches_DispatchId",
                        column: x => x.DispatchId,
                        principalTable: "GpuExecutorDispatches",
                        principalColumn: "DispatchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GpuExecutorResultReceipts",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MiniTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutorKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    AdmissionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Disposition = table.Column<int>(type: "int", nullable: false),
                    EvidenceClass = table.Column<int>(type: "int", nullable: false),
                    OpaqueResultDigest = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    RequestFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuExecutorResultReceipts", x => x.OperationId);
                    table.CheckConstraint("CK_GpuExecutorResultReceipts_ExecutorKey_NoTrailingWhitespace", "DATALENGTH([ExecutorKey]) > 0 AND UNICODE(RIGHT([ExecutorKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_GpuExecutorResultReceipts_RequestFingerprint_NoTrailingWhitespace", "DATALENGTH([RequestFingerprint]) > 0 AND UNICODE(RIGHT([RequestFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.ForeignKey(
                        name: "FK_GpuExecutorResultReceipts_GpuBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "GpuBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GpuExecutorResultReceipts_GpuExecutorDispatches_DispatchId",
                        column: x => x.DispatchId,
                        principalTable: "GpuExecutorDispatches",
                        principalColumn: "DispatchId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GpuExecutorResultReceipts_GpuMiniTasks_MiniTaskId",
                        column: x => x.MiniTaskId,
                        principalTable: "GpuMiniTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GpuExecutorDispatches_BatchId",
                table: "GpuExecutorDispatches",
                column: "BatchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GpuExecutorDispatches_CapacitySlotKey",
                table: "GpuExecutorDispatches",
                column: "CapacitySlotKey");

            migrationBuilder.CreateIndex(
                name: "IX_GpuExecutorDispatches_State_UpdatedAtUtc",
                table: "GpuExecutorDispatches",
                columns: new[] { "State", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GpuExecutorEvidence_BatchId",
                table: "GpuExecutorEvidence",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_GpuExecutorEvidence_CapacitySlotKey",
                table: "GpuExecutorEvidence",
                column: "CapacitySlotKey");

            migrationBuilder.CreateIndex(
                name: "IX_GpuExecutorEvidence_DispatchId_EvidenceClass_OperationId",
                table: "GpuExecutorEvidence",
                columns: new[] { "DispatchId", "EvidenceClass", "OperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_GpuExecutorResultReceipts_BatchId_MiniTaskId_AdmissionGeneration",
                table: "GpuExecutorResultReceipts",
                columns: new[] { "BatchId", "MiniTaskId", "AdmissionGeneration" });

            migrationBuilder.CreateIndex(
                name: "IX_GpuExecutorResultReceipts_DispatchId_MiniTaskId",
                table: "GpuExecutorResultReceipts",
                columns: new[] { "DispatchId", "MiniTaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GpuExecutorResultReceipts_MiniTaskId",
                table: "GpuExecutorResultReceipts",
                column: "MiniTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GpuExecutorEvidence");

            migrationBuilder.DropTable(
                name: "GpuExecutorResultReceipts");

            migrationBuilder.DropTable(
                name: "GpuExecutorDispatches");
        }
    }
}
