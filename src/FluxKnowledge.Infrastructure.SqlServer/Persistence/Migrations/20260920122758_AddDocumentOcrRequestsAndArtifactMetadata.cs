using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentOcrRequestsAndArtifactMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentMetadataJson",
                table: "Artifacts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentOcrRequests",
                columns: table => new
                {
                    MiniTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    RetainedSourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RequestedPageIndexesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: false),
                    ModelRuntimeKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SettingsFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<int>(type: "int", nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResultDigest = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentOcrRequests", x => x.MiniTaskId);
                    table.CheckConstraint("CK_DocumentOcrRequests_ContentSha256", "LEN([ContentSha256]) = 64 AND [ContentSha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_DocumentOcrRequests_ModelRuntimeKey_NoTrailingWhitespace", "DATALENGTH([ModelRuntimeKey]) > 0 AND UNICODE(RIGHT([ModelRuntimeKey], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_DocumentOcrRequests_RequestedPages_Bounded", "DATALENGTH([RequestedPageIndexesJson]) > 0 AND DATALENGTH([RequestedPageIndexesJson]) <= 8192");
                    table.CheckConstraint("CK_DocumentOcrRequests_ResultDigest_Length", "[ResultDigest] IS NULL OR DATALENGTH([ResultDigest]) = 32");
                    table.CheckConstraint("CK_DocumentOcrRequests_ResultJson_Bounded", "[ResultJson] IS NULL OR DATALENGTH([ResultJson]) <= 4194304");
                    table.CheckConstraint("CK_DocumentOcrRequests_SettingsFingerprint_NoTrailingWhitespace", "DATALENGTH([SettingsFingerprint]) > 0 AND UNICODE(RIGHT([SettingsFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Artifacts_DocumentMetadataJson_Bounded",
                table: "Artifacts",
                sql: "[DocumentMetadataJson] IS NULL OR DATALENGTH([DocumentMetadataJson]) <= 4194304");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentOcrRequests_ParentJobId_State",
                table: "DocumentOcrRequests",
                columns: new[] { "ParentJobId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentOcrRequests_PipelineRecordId_SourceRevision_State",
                table: "DocumentOcrRequests",
                columns: new[] { "PipelineRecordId", "SourceRevision", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentOcrRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Artifacts_DocumentMetadataJson_Bounded",
                table: "Artifacts");

            migrationBuilder.DropColumn(
                name: "DocumentMetadataJson",
                table: "Artifacts");
        }
    }
}
