using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingGpuRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmbeddingGpuRequests",
                columns: table => new
                {
                    MiniTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    GenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorpusEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    InputsJson = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    InputDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<int>(type: "int", nullable: false),
                    ExecutorInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClaimOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerProcessId = table.Column<int>(type: "int", nullable: true),
                    OwnerStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    OwnerMachineFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    DispatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NativeCleanupConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    CleanupConfirmedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    ResultDigest = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmbeddingGpuRequests", x => x.MiniTaskId);
                    table.CheckConstraint("CK_EmbeddingGpuRequests_Cleanup", "([NativeCleanupConfirmed] = 0 AND [CleanupConfirmedAtUtc] IS NULL) OR ([NativeCleanupConfirmed] = 1 AND [CleanupConfirmedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_EmbeddingGpuRequests_Completion", "[State] = 0 OR ([State] = 1 AND [ResultDigest] IS NOT NULL AND [ExecutorInstanceId] IS NOT NULL) OR ([State] = 2 AND [NativeCleanupConfirmed] = 1)");
                    table.CheckConstraint("CK_EmbeddingGpuRequests_InputDigest", "LEN([InputDigest]) = 64 AND [InputDigest] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_EmbeddingGpuRequests_Inputs", "ISJSON([InputsJson]) = 1 AND DATALENGTH([InputsJson]) BETWEEN 2 AND 4096");
                    table.CheckConstraint("CK_EmbeddingGpuRequests_Owner", "([ExecutorInstanceId] IS NULL AND [ClaimOperationId] IS NULL AND [OwnerProcessId] IS NULL AND [OwnerStartedAtUtc] IS NULL AND [OwnerMachineFingerprint] IS NULL AND [DispatchId] IS NULL) OR ([ExecutorInstanceId] IS NOT NULL AND [ExecutorInstanceId] <> '00000000-0000-0000-0000-000000000000' AND [ClaimOperationId] IS NOT NULL AND [ClaimOperationId] <> '00000000-0000-0000-0000-000000000000' AND [OwnerProcessId] IS NOT NULL AND [OwnerProcessId] > 0 AND [OwnerStartedAtUtc] IS NOT NULL AND [DispatchId] IS NOT NULL AND [OwnerMachineFingerprint] IS NOT NULL AND LEN([OwnerMachineFingerprint]) = 64 AND [OwnerMachineFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%')");
                    table.CheckConstraint("CK_EmbeddingGpuRequests_Profile", "[Dimensions] BETWEEN 1 AND 4096 AND DATALENGTH([ModelFingerprint]) > 0");
                    table.CheckConstraint("CK_EmbeddingGpuRequests_Result", "[ResultDigest] IS NULL OR DATALENGTH([ResultDigest]) = 32");
                    table.CheckConstraint("CK_EmbeddingGpuRequests_State", "[State] BETWEEN 0 AND 2");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingGpuRequests_ParentJobId_GenerationId_InputDigest",
                table: "EmbeddingGpuRequests",
                columns: new[] { "ParentJobId", "GenerationId", "InputDigest" },
                unique: true,
                filter: "[State] < 2");

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingGpuRequests_ParentJobId_State",
                table: "EmbeddingGpuRequests",
                columns: new[] { "ParentJobId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingGpuRequests_PipelineRecordId_SourceRevision_State",
                table: "EmbeddingGpuRequests",
                columns: new[] { "PipelineRecordId", "SourceRevision", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [EmbeddingGpuRequests])
                    THROW 51000, 'embedding-gpu-downgrade-requires-empty-reset', 1;
                """);
            migrationBuilder.DropTable(
                name: "EmbeddingGpuRequests");
        }
    }
}
