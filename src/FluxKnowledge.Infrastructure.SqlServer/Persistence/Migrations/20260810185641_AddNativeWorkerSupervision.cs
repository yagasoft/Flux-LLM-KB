using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNativeWorkerSupervision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "NativeWorkerBindOperationId",
                table: "GpuExecutorDispatches",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NativeWorkerBindRequestFingerprint",
                table: "GpuExecutorDispatches",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<Guid>(
                name: "NativeWorkerClearOperationId",
                table: "GpuExecutorDispatches",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NativeWorkerClearRequestFingerprint",
                table: "GpuExecutorDispatches",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateTable(
                name: "NativeWorkerInstances",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutorKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProcessId = table.Column<int>(type: "int", nullable: true),
                    ProcessStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    ExecutableFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProtocolVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<int>(type: "int", nullable: false),
                    LaunchedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ConnectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    ExitedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    ActiveDispatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NativeWorkerInstances", x => x.InstanceId);
                    table.CheckConstraint("CK_NativeWorkerInstances_ExecutableFingerprint_NoTrailingWhitespace", "DATALENGTH([ExecutableFingerprint]) > 0 AND UNICODE(RIGHT([ExecutableFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_NativeWorkerInstances_ExecutableFingerprint_Sha256", "LEN([ExecutableFingerprint]) = 64 AND [ExecutableFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_NativeWorkerInstances_ExecutorKey_NoTrailingWhitespace", "DATALENGTH([ExecutorKey]) > 0 AND UNICODE(RIGHT([ExecutorKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_NativeWorkerInstances_ProcessAttestation_Complete", "([ProcessId] IS NULL AND [ProcessStartedAtUtc] IS NULL) OR ([ProcessId] IS NOT NULL AND [ProcessStartedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_NativeWorkerInstances_ProcessId_Positive", "[ProcessId] IS NULL OR [ProcessId] > 0");
                    table.CheckConstraint("CK_NativeWorkerInstances_ProtocolVersion_NoTrailingWhitespace", "DATALENGTH([ProtocolVersion]) > 0 AND UNICODE(RIGHT([ProtocolVersion], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_NativeWorkerInstances_State_Closed", "[State] >= 0 AND [State] <= 13");
                    table.ForeignKey(
                        name: "FK_NativeWorkerInstances_GpuExecutorDispatches_ActiveDispatchId",
                        column: x => x.ActiveDispatchId,
                        principalTable: "GpuExecutorDispatches",
                        principalColumn: "DispatchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NativeWorkerLifecycleEvidence",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LifecycleClass = table.Column<int>(type: "int", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    OutcomeCode = table.Column<int>(type: "int", nullable: true),
                    RequestFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NativeWorkerLifecycleEvidence", x => x.OperationId);
                    table.CheckConstraint("CK_NativeWorkerLifecycleEvidence_LifecycleClass_Closed", "[LifecycleClass] >= 0 AND [LifecycleClass] <= 13");
                    table.CheckConstraint("CK_NativeWorkerLifecycleEvidence_OutcomeCode_Bounded", "[OutcomeCode] IS NULL OR ([OutcomeCode] >= -32768 AND [OutcomeCode] <= 65535)");
                    table.CheckConstraint("CK_NativeWorkerLifecycleEvidence_RequestFingerprint_NoTrailingWhitespace", "DATALENGTH([RequestFingerprint]) > 0 AND UNICODE(RIGHT([RequestFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_NativeWorkerLifecycleEvidence_RequestFingerprint_Sha256", "LEN([RequestFingerprint]) = 64 AND [RequestFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.ForeignKey(
                        name: "FK_NativeWorkerLifecycleEvidence_NativeWorkerInstances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "NativeWorkerInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NativeWorkerInstances_ActiveDispatchId",
                table: "NativeWorkerInstances",
                column: "ActiveDispatchId",
                unique: true,
                filter: "[ActiveDispatchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GpuExecutorDispatches_NativeWorkerBindOperationId",
                table: "GpuExecutorDispatches",
                column: "NativeWorkerBindOperationId",
                unique: true,
                filter: "[NativeWorkerBindOperationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GpuExecutorDispatches_NativeWorkerClearOperationId",
                table: "GpuExecutorDispatches",
                column: "NativeWorkerClearOperationId",
                unique: true,
                filter: "[NativeWorkerClearOperationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NativeWorkerLifecycleEvidence_InstanceId_ObservedAtUtc_OperationId",
                table: "NativeWorkerLifecycleEvidence",
                columns: new[] { "InstanceId", "ObservedAtUtc", "OperationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NativeWorkerLifecycleEvidence");

            migrationBuilder.DropTable(
                name: "NativeWorkerInstances");

            migrationBuilder.DropIndex(
                name: "IX_GpuExecutorDispatches_NativeWorkerBindOperationId",
                table: "GpuExecutorDispatches");

            migrationBuilder.DropIndex(
                name: "IX_GpuExecutorDispatches_NativeWorkerClearOperationId",
                table: "GpuExecutorDispatches");

            migrationBuilder.DropColumn(
                name: "NativeWorkerBindOperationId",
                table: "GpuExecutorDispatches");

            migrationBuilder.DropColumn(
                name: "NativeWorkerBindRequestFingerprint",
                table: "GpuExecutorDispatches");

            migrationBuilder.DropColumn(
                name: "NativeWorkerClearOperationId",
                table: "GpuExecutorDispatches");

            migrationBuilder.DropColumn(
                name: "NativeWorkerClearRequestFingerprint",
                table: "GpuExecutorDispatches");
        }
    }
}
