using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGpuSchedulerDurability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GpuMiniTasks_State_PriorityLane_CreatedAtUtc",
                table: "GpuMiniTasks");

            migrationBuilder.CreateSequence(
                name: "GpuMiniTaskCreatedSequence");

            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "GpuMiniTasks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CreatedSequence",
                table: "GpuMiniTasks",
                type: "bigint",
                nullable: true);

            // Existing rows must join the durable queue in the same stable order the
            // scheduler promises: creation time followed by the immutable task ID.
            // Do not use the physical insert/plan order supplied by SQL Server.
            migrationBuilder.Sql(
                """
                ;WITH [OrderedTasks] AS
                (
                    SELECT [Id], ROW_NUMBER() OVER (ORDER BY [CreatedAtUtc], [Id]) AS [Sequence]
                    FROM [GpuMiniTasks]
                )
                UPDATE [Task]
                SET [CreatedSequence] = [OrderedTasks].[Sequence]
                FROM [GpuMiniTasks] AS [Task]
                INNER JOIN [OrderedTasks] ON [OrderedTasks].[Id] = [Task].[Id];

                DECLARE @nextSequence bigint =
                    (SELECT ISNULL(MAX([CreatedSequence]), 0) + 1 FROM [GpuMiniTasks]);
                DECLARE @restart nvarchar(200) =
                    N'ALTER SEQUENCE [GpuMiniTaskCreatedSequence] RESTART WITH ' +
                    CONVERT(nvarchar(30), @nextSequence) + N';';
                EXECUTE sp_executesql @restart;
                """);

            migrationBuilder.AlterColumn<long>(
                name: "CreatedSequence",
                table: "GpuMiniTasks",
                type: "bigint",
                nullable: false,
                defaultValueSql: "NEXT VALUE FOR [GpuMiniTaskCreatedSequence]",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeferredUntilUtc",
                table: "GpuMiniTasks",
                type: "datetimeoffset(7)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandoffLeaseOwner",
                table: "GpuMiniTasks",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReservationAttemptCount",
                table: "GpuMiniTasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "GpuSchedulerState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    WakeGeneration = table.Column<long>(type: "bigint", nullable: false),
                    PendingWakeReasons = table.Column<int>(type: "int", nullable: false),
                    NextDeferredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    InFlightWakeOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InFlightWakeGeneration = table.Column<long>(type: "bigint", nullable: true),
                    InFlightWakeReasons = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    InFlightNextDeferredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    InFlightEffectiveAdmissionReasons = table.Column<int>(type: "int", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuSchedulerState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GpuBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapacitySlotKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PriorityLane = table.Column<int>(type: "int", nullable: false),
                    ModelRuntimeKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SettingsFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ItemCount = table.Column<int>(type: "int", nullable: false),
                    EstimatedBytes = table.Column<long>(type: "bigint", nullable: false),
                    AdmissionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    OwnerKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    LastHeartbeatAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuBatches", x => x.Id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuSchedulerState_Singleton",
                table: "GpuSchedulerState",
                sql: "[Id] = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GpuSchedulerState_InFlightWake",
                table: "GpuSchedulerState",
                sql: "([InFlightWakeOperationId] IS NULL AND [InFlightWakeGeneration] IS NULL AND [InFlightWakeReasons] = 0 AND [InFlightNextDeferredAtUtc] IS NULL AND [InFlightEffectiveAdmissionReasons] IS NULL) OR ([InFlightWakeOperationId] IS NOT NULL AND [InFlightWakeGeneration] IS NOT NULL AND [InFlightEffectiveAdmissionReasons] IS NOT NULL)");

            migrationBuilder.InsertData(
                table: "GpuSchedulerState",
                columns: new[] { "Id", "WakeGeneration", "PendingWakeReasons", "NextDeferredAtUtc", "UpdatedAtUtc" },
                values: new object[]
                {
                    1,
                    0L,
                    0,
                    null,
                    new DateTimeOffset(2026, 7, 29, 8, 6, 41, TimeSpan.Zero)
                });

            migrationBuilder.CreateTable(
                name: "GpuCapacitySlots",
                columns: table => new
                {
                    SlotKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    ActiveBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuCapacitySlots", x => x.SlotKey);
                    table.ForeignKey(
                        name: "FK_GpuCapacitySlots_GpuBatches_ActiveBatchId",
                        column: x => x.ActiveBatchId,
                        principalTable: "GpuBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GpuMiniTasks_BatchId",
                table: "GpuMiniTasks",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_GpuMiniTasks_State_DeferredUntilUtc",
                table: "GpuMiniTasks",
                columns: new[] { "State", "DeferredUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GpuMiniTasks_State_PriorityLane_CreatedSequence_Id",
                table: "GpuMiniTasks",
                columns: new[] { "State", "PriorityLane", "CreatedSequence", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_GpuBatches_CapacitySlotKey",
                table: "GpuBatches",
                column: "CapacitySlotKey");

            migrationBuilder.CreateIndex(
                name: "IX_GpuCapacitySlots_ActiveBatchId",
                table: "GpuCapacitySlots",
                column: "ActiveBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_GpuCapacitySlots_State_SlotKey",
                table: "GpuCapacitySlots",
                columns: new[] { "State", "SlotKey" });

            migrationBuilder.AddForeignKey(
                name: "FK_GpuMiniTasks_GpuBatches_BatchId",
                table: "GpuMiniTasks",
                column: "BatchId",
                principalTable: "GpuBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GpuBatches_GpuCapacitySlots_CapacitySlotKey",
                table: "GpuBatches",
                column: "CapacitySlotKey",
                principalTable: "GpuCapacitySlots",
                principalColumn: "SlotKey",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GpuMiniTasks_GpuBatches_BatchId",
                table: "GpuMiniTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_GpuBatches_GpuCapacitySlots_CapacitySlotKey",
                table: "GpuBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GpuSchedulerState_Singleton",
                table: "GpuSchedulerState");

            migrationBuilder.DropTable(
                name: "GpuSchedulerState");

            migrationBuilder.DropTable(
                name: "GpuCapacitySlots");

            migrationBuilder.DropTable(
                name: "GpuBatches");

            migrationBuilder.DropIndex(
                name: "IX_GpuMiniTasks_BatchId",
                table: "GpuMiniTasks");

            migrationBuilder.DropIndex(
                name: "IX_GpuMiniTasks_State_DeferredUntilUtc",
                table: "GpuMiniTasks");

            migrationBuilder.DropIndex(
                name: "IX_GpuMiniTasks_State_PriorityLane_CreatedSequence_Id",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "CreatedSequence",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "DeferredUntilUtc",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "HandoffLeaseOwner",
                table: "GpuMiniTasks");

            migrationBuilder.DropColumn(
                name: "ReservationAttemptCount",
                table: "GpuMiniTasks");

            migrationBuilder.DropSequence(
                name: "GpuMiniTaskCreatedSequence");

            migrationBuilder.CreateIndex(
                name: "IX_GpuMiniTasks_State_PriorityLane_CreatedAtUtc",
                table: "GpuMiniTasks",
                columns: new[] { "State", "PriorityLane", "CreatedAtUtc" });
        }
    }
}
