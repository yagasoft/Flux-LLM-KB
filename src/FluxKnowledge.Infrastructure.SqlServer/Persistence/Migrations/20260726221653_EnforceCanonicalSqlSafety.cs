using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceCanonicalSqlSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Artifacts_PipelineRecords_PipelineRecordId",
                table: "Artifacts");

            migrationBuilder.DropForeignKey(
                name: "FK_GpuMiniTasks_Jobs_ParentJobId",
                table: "GpuMiniTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_PipelineRecords_PipelineRecordId",
                table: "Jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_OutboxMessages_PipelineRecords_PipelineRecordId",
                table: "OutboxMessages");

            migrationBuilder.DropForeignKey(
                name: "FK_TextChunks_Artifacts_ArtifactId",
                table: "TextChunks");

            migrationBuilder.DropForeignKey(
                name: "FK_Vectors_TextChunks_TextChunkId",
                table: "Vectors");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_PipelineRecordId",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_PipelineRecordId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_GpuMiniTasks_ParentJobId",
                table: "GpuMiniTasks");

            migrationBuilder.AddColumn<long>(
                name: "SourceRevision",
                table: "TextChunks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SourceRevision",
                table: "Jobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "JobAttempts",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_TextChunks_Id_SourceRevision",
                table: "TextChunks",
                columns: new[] { "Id", "SourceRevision" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_PipelineRecords_Id_Revision",
                table: "PipelineRecords",
                columns: new[] { "Id", "Revision" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Jobs_Id_SourceRevision",
                table: "Jobs",
                columns: new[] { "Id", "SourceRevision" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Artifacts_Id_SourceRevision",
                table: "Artifacts",
                columns: new[] { "Id", "SourceRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_Vectors_TextChunkId_SourceRevision",
                table: "Vectors",
                columns: new[] { "TextChunkId", "SourceRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_TextChunks_ArtifactId_SourceRevision",
                table: "TextChunks",
                columns: new[] { "ArtifactId", "SourceRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_PipelineRecordId_SourceRevision",
                table: "OutboxMessages",
                columns: new[] { "PipelineRecordId", "SourceRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_PipelineRecordId_SourceRevision",
                table: "Jobs",
                columns: new[] { "PipelineRecordId", "SourceRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_GpuMiniTasks_ParentJobId_SourceRevision",
                table: "GpuMiniTasks",
                columns: new[] { "ParentJobId", "SourceRevision" });

            migrationBuilder.AddForeignKey(
                name: "FK_Artifacts_PipelineRecords_PipelineRecordId_SourceRevision",
                table: "Artifacts",
                columns: new[] { "PipelineRecordId", "SourceRevision" },
                principalTable: "PipelineRecords",
                principalColumns: new[] { "Id", "Revision" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GpuMiniTasks_Jobs_ParentJobId_SourceRevision",
                table: "GpuMiniTasks",
                columns: new[] { "ParentJobId", "SourceRevision" },
                principalTable: "Jobs",
                principalColumns: new[] { "Id", "SourceRevision" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_PipelineRecords_PipelineRecordId_SourceRevision",
                table: "Jobs",
                columns: new[] { "PipelineRecordId", "SourceRevision" },
                principalTable: "PipelineRecords",
                principalColumns: new[] { "Id", "Revision" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OutboxMessages_PipelineRecords_PipelineRecordId_SourceRevision",
                table: "OutboxMessages",
                columns: new[] { "PipelineRecordId", "SourceRevision" },
                principalTable: "PipelineRecords",
                principalColumns: new[] { "Id", "Revision" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TextChunks_Artifacts_ArtifactId_SourceRevision",
                table: "TextChunks",
                columns: new[] { "ArtifactId", "SourceRevision" },
                principalTable: "Artifacts",
                principalColumns: new[] { "Id", "SourceRevision" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Vectors_TextChunks_TextChunkId_SourceRevision",
                table: "Vectors",
                columns: new[] { "TextChunkId", "SourceRevision" },
                principalTable: "TextChunks",
                principalColumns: new[] { "Id", "SourceRevision" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Artifacts_PipelineRecords_PipelineRecordId_SourceRevision",
                table: "Artifacts");

            migrationBuilder.DropForeignKey(
                name: "FK_GpuMiniTasks_Jobs_ParentJobId_SourceRevision",
                table: "GpuMiniTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_PipelineRecords_PipelineRecordId_SourceRevision",
                table: "Jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_OutboxMessages_PipelineRecords_PipelineRecordId_SourceRevision",
                table: "OutboxMessages");

            migrationBuilder.DropForeignKey(
                name: "FK_TextChunks_Artifacts_ArtifactId_SourceRevision",
                table: "TextChunks");

            migrationBuilder.DropForeignKey(
                name: "FK_Vectors_TextChunks_TextChunkId_SourceRevision",
                table: "Vectors");

            migrationBuilder.DropIndex(
                name: "IX_Vectors_TextChunkId_SourceRevision",
                table: "Vectors");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TextChunks_Id_SourceRevision",
                table: "TextChunks");

            migrationBuilder.DropIndex(
                name: "IX_TextChunks_ArtifactId_SourceRevision",
                table: "TextChunks");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PipelineRecords_Id_Revision",
                table: "PipelineRecords");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_PipelineRecordId_SourceRevision",
                table: "OutboxMessages");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Jobs_Id_SourceRevision",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_PipelineRecordId_SourceRevision",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_GpuMiniTasks_ParentJobId_SourceRevision",
                table: "GpuMiniTasks");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Artifacts_Id_SourceRevision",
                table: "Artifacts");

            migrationBuilder.DropColumn(
                name: "SourceRevision",
                table: "TextChunks");

            migrationBuilder.DropColumn(
                name: "SourceRevision",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "JobAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_PipelineRecordId",
                table: "OutboxMessages",
                column: "PipelineRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_PipelineRecordId",
                table: "Jobs",
                column: "PipelineRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_GpuMiniTasks_ParentJobId",
                table: "GpuMiniTasks",
                column: "ParentJobId");

            migrationBuilder.AddForeignKey(
                name: "FK_Artifacts_PipelineRecords_PipelineRecordId",
                table: "Artifacts",
                column: "PipelineRecordId",
                principalTable: "PipelineRecords",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GpuMiniTasks_Jobs_ParentJobId",
                table: "GpuMiniTasks",
                column: "ParentJobId",
                principalTable: "Jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_PipelineRecords_PipelineRecordId",
                table: "Jobs",
                column: "PipelineRecordId",
                principalTable: "PipelineRecords",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OutboxMessages_PipelineRecords_PipelineRecordId",
                table: "OutboxMessages",
                column: "PipelineRecordId",
                principalTable: "PipelineRecords",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TextChunks_Artifacts_ArtifactId",
                table: "TextChunks",
                column: "ArtifactId",
                principalTable: "Artifacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Vectors_TextChunks_TextChunkId",
                table: "Vectors",
                column: "TextChunkId",
                principalTable: "TextChunks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
