using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGpuModelResidencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaTranscriptionGpuRequests_Cleanup",
                table: "MediaTranscriptionGpuRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EmbeddingGpuRequests_Completion",
                table: "EmbeddingGpuRequests");

            migrationBuilder.AddColumn<Guid>(
                name: "ResidentDetachmentDispatchId",
                table: "EmbeddingGpuRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GpuModelResidencies",
                columns: table => new
                {
                    ResidencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SlotKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RuntimeKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SettingsFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ModelKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OwnerProcessId = table.Column<int>(type: "int", nullable: false),
                    OwnerStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    MachineFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ChildJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChildProcessId = table.Column<int>(type: "int", nullable: true),
                    ChildStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    BorrowedDispatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BorrowedExecutionInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdleUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleaseKind = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuModelResidencies", x => x.ResidencyId);
                    table.CheckConstraint("CK_GpuModelResidencies_Child", "([ChildJobId] IS NULL AND [ChildProcessId] IS NULL AND [ChildStartedAtUtc] IS NULL) OR\n([ChildJobId] IS NOT NULL AND [ChildJobId]=[ResidencyId] AND [ChildProcessId] IS NOT NULL AND\n [ChildProcessId]>0 AND [ChildStartedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[ChildStartedAtUtc])=0 AND\n [ChildStartedAtUtc]>=[OwnerStartedAtUtc])");
                    table.CheckConstraint("CK_GpuModelResidencies_Deadline", "[IdleUntilUtc] IS NULL OR (DATEPART(TZOFFSET,[IdleUntilUtc])=0 AND [IdleUntilUtc]>=[OwnerStartedAtUtc])");
                    table.CheckConstraint("CK_GpuModelResidencies_Identity", "[ResidencyId] <> '00000000-0000-0000-0000-000000000000' AND [Version] > 0 AND [OwnerProcessId] > 0 AND\nDATEPART(TZOFFSET,[OwnerStartedAtUtc])=0 AND [OwnerStartedAtUtc] > '0001-01-01T00:00:00+00:00'");
                    table.CheckConstraint("CK_GpuModelResidencies_MachineFingerprint", "LEN([MachineFingerprint]) = 64 AND [MachineFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_GpuModelResidencies_ModelKey", "DATALENGTH([ModelKey]) > 0 AND UNICODE(RIGHT([ModelKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_GpuModelResidencies_Phase", "([State]=0 AND [BorrowedDispatchId] IS NOT NULL AND [BorrowedDispatchId]<>'00000000-0000-0000-0000-000000000000'\n AND [BorrowedExecutionInstanceId] IS NOT NULL AND [BorrowedExecutionInstanceId]<>'00000000-0000-0000-0000-000000000000'\n AND [IdleUntilUtc] IS NULL) OR\n([BorrowedDispatchId] IS NULL AND [BorrowedExecutionInstanceId] IS NULL AND\n (([State]=1 AND [IdleUntilUtc] IS NOT NULL) OR [State]=2 OR ([State] IN (3,4) AND [IdleUntilUtc] IS NULL)))");
                    table.CheckConstraint("CK_GpuModelResidencies_Release", "([State]<>4 AND [ReleasedAtUtc] IS NULL AND [ReleaseKind] IS NULL) OR\n([State]=4 AND [ReleasedAtUtc] IS NOT NULL AND DATEPART(TZOFFSET,[ReleasedAtUtc])=0 AND\n [ReleasedAtUtc]>=[OwnerStartedAtUtc] AND ([ChildStartedAtUtc] IS NULL OR [ReleasedAtUtc]>=[ChildStartedAtUtc]) AND\n [ReleaseKind] IS NOT NULL AND\n (([ChildJobId] IS NULL AND [ReleaseKind] IN (0,2)) OR ([ChildJobId] IS NOT NULL AND [ReleaseKind]=1)))");
                    table.CheckConstraint("CK_GpuModelResidencies_RuntimeKey", "DATALENGTH([RuntimeKey]) > 0 AND UNICODE(RIGHT([RuntimeKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_GpuModelResidencies_SettingsFingerprint", "DATALENGTH([SettingsFingerprint]) > 0 AND UNICODE(RIGHT([SettingsFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_GpuModelResidencies_SlotKey", "DATALENGTH([SlotKey]) > 0 AND UNICODE(RIGHT([SlotKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.ForeignKey(
                        name: "FK_GpuModelResidencies_GpuCapacitySlots_SlotKey",
                        column: x => x.SlotKey,
                        principalTable: "GpuCapacitySlots",
                        principalColumn: "SlotKey",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GpuModelDetachmentReceipts",
                columns: table => new
                {
                    DispatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResidencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdmissionGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ExecutorKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExecutionInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DetachedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpuModelDetachmentReceipts", x => x.DispatchId);
                    table.CheckConstraint("CK_GpuModelDetachmentReceipts_ExecutorKey", "DATALENGTH([ExecutorKey]) > 0 AND UNICODE(RIGHT([ExecutorKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_GpuModelDetachmentReceipts_Identity", "[DispatchId]<>'00000000-0000-0000-0000-000000000000' AND [BatchId]<>'00000000-0000-0000-0000-000000000000' AND\n[ExecutionInstanceId]<>'00000000-0000-0000-0000-000000000000' AND [Version]>0 AND [AdmissionGeneration]>0 AND\nDATEPART(TZOFFSET,[DetachedAtUtc])=0 AND [DetachedAtUtc]>'0001-01-01T00:00:00+00:00'");
                    table.ForeignKey(
                        name: "FK_GpuModelDetachmentReceipts_GpuModelResidencies_ResidencyId",
                        column: x => x.ResidencyId,
                        principalTable: "GpuModelResidencies",
                        principalColumn: "ResidencyId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaTranscriptionGpuRequests_Cleanup",
                table: "MediaTranscriptionGpuRequests",
                sql: "([ExecutionPhase]<3 AND [CleanupConfirmedAtUtc] IS NULL AND [CleanupKind] IS NULL) OR\n([ExecutionPhase]=4 AND [NativeProcessId] IS NOT NULL AND [CleanupConfirmedAtUtc] IS NULL AND [CleanupKind] IS NULL) OR\n([ExecutionPhase]>=3 AND [CleanupConfirmedAtUtc] IS NOT NULL AND [CleanupKind] IS NOT NULL AND\n ([CleanupKind]=0 AND [NativeProcessId] IS NULL OR [CleanupKind]=1 AND [NativeProcessId] IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingGpuRequests_ResidentDetachmentDispatchId",
                table: "EmbeddingGpuRequests",
                column: "ResidentDetachmentDispatchId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EmbeddingGpuRequests_Completion",
                table: "EmbeddingGpuRequests",
                sql: "[State] = 0 OR ([State] = 1 AND [ResultDigest] IS NOT NULL AND [ExecutorInstanceId] IS NOT NULL) OR ([State] = 2 AND ([NativeCleanupConfirmed] = 1 OR [ResidentDetachmentDispatchId] IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EmbeddingGpuRequests_ResidentDetachment",
                table: "EmbeddingGpuRequests",
                sql: "[ResidentDetachmentDispatchId] IS NULL OR ([State] IN (1, 2) AND [DispatchId] IS NOT NULL AND [ResidentDetachmentDispatchId] = [DispatchId] AND [ResultDigest] IS NOT NULL AND [NativeCleanupConfirmed] = 0)");

            migrationBuilder.CreateIndex(
                name: "IX_GpuModelDetachmentReceipts_ResidencyId_Version",
                table: "GpuModelDetachmentReceipts",
                columns: new[] { "ResidencyId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GpuModelResidencies_SlotKey",
                table: "GpuModelResidencies",
                column: "SlotKey",
                unique: true,
                filter: "[State] < 4");

            migrationBuilder.AddForeignKey(
                name: "FK_EmbeddingGpuRequests_GpuModelDetachmentReceipts_ResidentDetachmentDispatchId",
                table: "EmbeddingGpuRequests",
                column: "ResidentDetachmentDispatchId",
                principalTable: "GpuModelDetachmentReceipts",
                principalColumn: "DispatchId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The predecessor cannot interpret resident capacity or detached completion evidence.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [GpuModelResidencies] WITH (TABLOCKX,HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [GpuModelDetachmentReceipts] WITH (TABLOCKX,HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [GpuCapacitySlots] WITH (TABLOCKX,HOLDLOCK) WHERE [State]<>0 OR [ActiveBatchId] IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM [EmbeddingGpuRequests] WITH (TABLOCKX,HOLDLOCK) WHERE [ResidentDetachmentDispatchId] IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM [MediaTranscriptionGpuRequests] WITH (TABLOCKX,HOLDLOCK) WHERE [ExecutionPhase]=4 AND [CleanupConfirmedAtUtc] IS NULL)
                    THROW 51000, 'gpu-residency-downgrade-would-discard-evidence', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_EmbeddingGpuRequests_GpuModelDetachmentReceipts_ResidentDetachmentDispatchId",
                table: "EmbeddingGpuRequests");

            migrationBuilder.DropTable(
                name: "GpuModelDetachmentReceipts");

            migrationBuilder.DropTable(
                name: "GpuModelResidencies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaTranscriptionGpuRequests_Cleanup",
                table: "MediaTranscriptionGpuRequests");

            migrationBuilder.DropIndex(
                name: "IX_EmbeddingGpuRequests_ResidentDetachmentDispatchId",
                table: "EmbeddingGpuRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EmbeddingGpuRequests_Completion",
                table: "EmbeddingGpuRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EmbeddingGpuRequests_ResidentDetachment",
                table: "EmbeddingGpuRequests");

            migrationBuilder.DropColumn(
                name: "ResidentDetachmentDispatchId",
                table: "EmbeddingGpuRequests");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaTranscriptionGpuRequests_Cleanup",
                table: "MediaTranscriptionGpuRequests",
                sql: "([ExecutionPhase]<3 AND [CleanupConfirmedAtUtc] IS NULL AND [CleanupKind] IS NULL) OR\n([ExecutionPhase]>=3 AND [CleanupConfirmedAtUtc] IS NOT NULL AND [CleanupKind] IS NOT NULL AND\n ([CleanupKind]=0 AND [NativeProcessId] IS NULL OR [CleanupKind]=1 AND [NativeProcessId] IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EmbeddingGpuRequests_Completion",
                table: "EmbeddingGpuRequests",
                sql: "[State] = 0 OR ([State] = 1 AND [ResultDigest] IS NOT NULL AND [ExecutorInstanceId] IS NOT NULL) OR ([State] = 2 AND [NativeCleanupConfirmed] = 1)");
        }
    }
}
