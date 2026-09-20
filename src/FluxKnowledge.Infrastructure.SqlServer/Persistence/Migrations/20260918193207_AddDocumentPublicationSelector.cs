using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentPublicationSelector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentPublications",
                columns: table => new
                {
                    OwnerSourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentInputSourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceProcessorBranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineRecordRevision = table.Column<long>(type: "bigint", nullable: false),
                    ProcessorFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentPublications", x => x.OwnerSourceRevisionId);
                    table.ForeignKey(
                        name: "FK_DocumentPublications_PipelineRecords_PipelineRecordId_PipelineRecordRevision",
                        columns: x => new { x.PipelineRecordId, x.PipelineRecordRevision },
                        principalTable: "PipelineRecords",
                        principalColumns: new[] { "Id", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPublications_SourceProcessorBranches_SourceProcessorBranchId",
                        column: x => x.SourceProcessorBranchId,
                        principalTable: "SourceProcessorBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPublications_SourceRevisions_DocumentInputSourceRevisionId",
                        column: x => x.DocumentInputSourceRevisionId,
                        principalTable: "SourceRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPublications_SourceRevisions_OwnerSourceRevisionId",
                        column: x => x.OwnerSourceRevisionId,
                        principalTable: "SourceRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPublications_DocumentInputSourceRevisionId",
                table: "DocumentPublications",
                column: "DocumentInputSourceRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPublications_PipelineRecordId_PipelineRecordRevision_DocumentInputSourceRevisionId",
                table: "DocumentPublications",
                columns: new[] { "PipelineRecordId", "PipelineRecordRevision", "DocumentInputSourceRevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPublications_SourceProcessorBranchId",
                table: "DocumentPublications",
                column: "SourceProcessorBranchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentPublications");
        }
    }
}
