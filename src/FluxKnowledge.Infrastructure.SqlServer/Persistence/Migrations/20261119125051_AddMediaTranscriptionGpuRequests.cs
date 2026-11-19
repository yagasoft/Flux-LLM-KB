using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaTranscriptionGpuRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaTranscriptionGpuRequests",
                columns: table => new
                {
                    MiniTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    RetainedSourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ModelRuntimeKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SettingsFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExecutionPhase = table.Column<int>(type: "int", nullable: false),
                    ExecutionInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExecutionBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExecutionDispatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExecutionAdmissionGeneration = table.Column<long>(type: "bigint", nullable: true),
                    SupervisorInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupervisorProcessId = table.Column<int>(type: "int", nullable: true),
                    SupervisorProcessStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MachineFingerprint = table.Column<string>(type: "char(64)", unicode: false, maxLength: 64, nullable: true),
                    ClaimedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeadlineAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NativeProcessId = table.Column<int>(type: "int", nullable: true),
                    NativeProcessStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CleanupConfirmedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CleanupKind = table.Column<int>(type: "int", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResultDigest = table.Column<byte[]>(type: "varbinary(32)", nullable: true),
                    RequeuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaTranscriptionGpuRequests", x => x.MiniTaskId);
                    table.CheckConstraint("CK_MediaTranscriptionGpuRequests_Cleanup", "([ExecutionPhase]<3 AND [CleanupConfirmedAtUtc] IS NULL AND [CleanupKind] IS NULL) OR\n([ExecutionPhase]>=3 AND [CleanupConfirmedAtUtc] IS NOT NULL AND [CleanupKind] IS NOT NULL AND\n ([CleanupKind]=0 AND [NativeProcessId] IS NULL OR [CleanupKind]=1 AND [NativeProcessId] IS NOT NULL))");
                    table.CheckConstraint("CK_MediaTranscriptionGpuRequests_ContentSha256", "LEN([ContentSha256]) = 64 AND [ContentSha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_MediaTranscriptionGpuRequests_Execution", "([ExecutionPhase]=0 AND [ExecutionInstanceId] IS NULL) OR\n([ExecutionPhase]>0 AND [ExecutionInstanceId] IS NOT NULL AND [ExecutionBatchId] IS NOT NULL AND\n [ExecutionDispatchId] IS NOT NULL AND [ExecutionAdmissionGeneration] IS NOT NULL AND [ExecutionAdmissionGeneration]>0 AND\n [SupervisorInstanceId] IS NOT NULL AND [SupervisorProcessId] IS NOT NULL AND [SupervisorProcessId]>0 AND\n [SupervisorProcessStartedAtUtc] IS NOT NULL AND [MachineFingerprint] IS NOT NULL AND\n [ClaimedAtUtc] IS NOT NULL AND [DeadlineAtUtc] IS NOT NULL AND [DeadlineAtUtc]>[ClaimedAtUtc])");
                    table.CheckConstraint("CK_MediaTranscriptionGpuRequests_ModelRuntimeKey", "DATALENGTH([ModelRuntimeKey]) > 0 AND UNICODE(RIGHT([ModelRuntimeKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_MediaTranscriptionGpuRequests_Native", "([NativeProcessId] IS NULL AND [NativeProcessStartedAtUtc] IS NULL AND [ExecutionPhase]<>2) OR\n([NativeProcessId] IS NOT NULL AND [NativeProcessId]>0 AND [NativeProcessStartedAtUtc] IS NOT NULL AND [ExecutionPhase]>=2)");
                    table.CheckConstraint("CK_MediaTranscriptionGpuRequests_Phase", "[ExecutionPhase] BETWEEN 0 AND 4");
                    table.CheckConstraint("CK_MediaTranscriptionGpuRequests_Requeue", "[RequeuedAtUtc] IS NULL OR [ExecutionPhase]=4");
                    table.CheckConstraint("CK_MediaTranscriptionGpuRequests_Result", "([ResultJson] IS NULL AND [ResultDigest] IS NULL AND [ExecutionPhase]<4) OR\n([ExecutionPhase]>=3 AND [ResultJson] IS NOT NULL AND DATALENGTH([ResultJson]) BETWEEN 1 AND 4194304 AND\n [ResultDigest] IS NOT NULL AND DATALENGTH([ResultDigest])=32)");
                    table.CheckConstraint("CK_MediaTranscriptionGpuRequests_SettingsFingerprint", "DATALENGTH([SettingsFingerprint]) > 0 AND UNICODE(RIGHT([SettingsFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaTranscriptionGpuRequests_ExecutionPhase_RequeuedAtUtc_CreatedAtUtc",
                table: "MediaTranscriptionGpuRequests",
                columns: new[] { "ExecutionPhase", "RequeuedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaTranscriptionGpuRequests_ParentJobId_RetainedSourceRevisionId",
                table: "MediaTranscriptionGpuRequests",
                columns: new[] { "ParentJobId", "RetainedSourceRevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaTranscriptionGpuRequests_PipelineRecordId_SourceRevision",
                table: "MediaTranscriptionGpuRequests",
                columns: new[] { "PipelineRecordId", "SourceRevision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaTranscriptionGpuRequests");
        }
    }
}
